using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Revisions;
using Raven.Client.ServerWide;
using Raven.Server.NotificationCenter.Notifications;
using Raven.Server.NotificationCenter.Notifications.Details;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Sparrow;
using Sparrow.Binary;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Sparrow.Logging;
using Sparrow.Server;
using Sparrow.Server.Utils;
using Voron;
using Voron.Data.Tables;
using Voron.Exceptions;
using Voron.Impl;
using static Raven.Server.Documents.DocumentsStorage;
using Constants = Raven.Client.Constants;

namespace Raven.Server.Documents.Revisions
{
    public class RevisionsStorage
    {
        private static readonly Slice IdAndEtagSlice;
        public static readonly Slice DeleteRevisionEtagSlice;
        public static readonly Slice AllRevisionsEtagsSlice;
        public static readonly Slice CollectionRevisionsEtagsSlice;
        private static readonly Slice RevisionsCountSlice;
        private static readonly Slice RevisionsTombstonesSlice;
        private static readonly Slice RevisionsPrefix;
        public static Slice ResolvedFlagByEtagSlice;

        public static readonly string RevisionsTombstones = "Revisions.Tombstones";

        public static readonly TableSchema RevisionsSchema = new TableSchema()
        {
            TableType = (byte)TableType.Revisions,
        };

        public static readonly TableSchema CompressedRevisionsSchema = new TableSchema()
        {
            TableType = (byte)TableType.Revisions,
        };

        public RevisionsConfiguration ConflictConfiguration;

        private readonly DocumentDatabase _database;
        private readonly DocumentsStorage _documentsStorage;
        public RevisionsConfiguration Configuration { get; private set; }
        public readonly RevisionsOperations Operations;
        private HashSet<string> _tableCreated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Logger _logger;

        private static readonly TimeSpan MaxEnforceConfigurationSingleBatchTime = TimeSpan.FromSeconds(30);

        public enum RevisionsTable
        {
            /* ChangeVector is the table's key as it's unique and will avoid conflicts (by replication) */
            ChangeVector = 0,
            LowerId = 1,
            /* We are you using the record separator in order to avoid loading another documents that has the same ID prefix,
                e.g. fitz(record-separator)01234567 and fitz0(record-separator)01234567, without the record separator we would have to load also fitz0 and filter it. */
            RecordSeparator = 2,
            Etag = 3, // etag to keep the insertion order
            Id = 4,
            Document = 5,
            Flags = 6,
            DeletedEtag = 7,
            LastModified = 8,
            TransactionMarker = 9,

            // Field for finding the resolved conflicts
            Resolved = 10,

            SwappedLastModified = 11,
        }

        public const long NotDeletedRevisionMarker = 0;

        private readonly RevisionsCollectionConfiguration _emptyConfiguration = new RevisionsCollectionConfiguration { Disabled = true };

        public RevisionsStorage(DocumentDatabase database, Transaction tx)
        {
            _database = database;
            _documentsStorage = _database.DocumentsStorage;
            _logger = LoggingSource.Instance.GetLogger<RevisionsStorage>(database.Name);
            Operations = new RevisionsOperations(_database);
            ConflictConfiguration = new RevisionsConfiguration
            {
                Default = new RevisionsCollectionConfiguration
                {
                    MinimumRevisionAgeToKeep = TimeSpan.FromDays(45),
                    Disabled = false
                }
            };
            CreateTrees(tx);
        }

        public Table EnsureRevisionTableCreated(Transaction tx, CollectionName collection)
        {
            var tableName = collection.GetTableName(CollectionTableType.Revisions);

            if (_tableCreated.Contains(collection.Name) == false)
            {
                // RavenDB-11705: It is possible that this will revert if the transaction
                // aborts, so we must record this only after the transaction has been committed
                // note that calling the Create() method multiple times is a noop
                RevisionsSchema.Create(tx, tableName, 16);
                tx.LowLevelTransaction.OnDispose += _ =>
                 {
                     if (tx.LowLevelTransaction.Committed == false)
                         return;

                     // not sure if we can _rely_ on the tx write lock here, so let's be safe and create
                     // a new instance, just in case
                     _tableCreated = new HashSet<string>(_tableCreated, StringComparer.OrdinalIgnoreCase)
                     {
                         collection.Name
                     };
                 };
            }

            var revisionsSchema = _documentsStorage.DocumentPut.DocumentsCompression.CompressRevisions ?
                CompressedRevisionsSchema :
                RevisionsSchema;

            return tx.OpenTable(revisionsSchema, tableName);
        }

        static RevisionsStorage()
        {
            using (StorageEnvironment.GetStaticContext(out var ctx))
            {
                Slice.From(ctx, "RevisionsChangeVector", ByteStringType.Immutable, out var changeVectorSlice);
                Slice.From(ctx, "RevisionsIdAndEtag", ByteStringType.Immutable, out IdAndEtagSlice);
                Slice.From(ctx, "DeleteRevisionEtag", ByteStringType.Immutable, out DeleteRevisionEtagSlice);
                Slice.From(ctx, "AllRevisionsEtags", ByteStringType.Immutable, out AllRevisionsEtagsSlice);
                Slice.From(ctx, "CollectionRevisionsEtags", ByteStringType.Immutable, out CollectionRevisionsEtagsSlice);
                Slice.From(ctx, "RevisionsCount", ByteStringType.Immutable, out RevisionsCountSlice);
                Slice.From(ctx, nameof(ResolvedFlagByEtagSlice), ByteStringType.Immutable, out ResolvedFlagByEtagSlice);
                Slice.From(ctx, RevisionsTombstones, ByteStringType.Immutable, out RevisionsTombstonesSlice);
                Slice.From(ctx, CollectionName.GetTablePrefix(CollectionTableType.Revisions), ByteStringType.Immutable, out RevisionsPrefix);

                AddRevisionIndexes(RevisionsSchema, changeVectorSlice);
                AddRevisionIndexes(CompressedRevisionsSchema, changeVectorSlice);

                RevisionsSchema.CompressValues(
                    RevisionsSchema.FixedSizeIndexes[CollectionRevisionsEtagsSlice], compress: false);
                CompressedRevisionsSchema.CompressValues(
                    RevisionsSchema.FixedSizeIndexes[CollectionRevisionsEtagsSlice], compress: true);
            }
        }

        private static void AddRevisionIndexes(TableSchema revisionsSchema, Slice changeVectorSlice)
        {
            revisionsSchema.DefineKey(new TableSchema.SchemaIndexDef
            {
                StartIndex = (int)RevisionsTable.ChangeVector,
                Count = 1,
                Name = changeVectorSlice,
                IsGlobal = true
            });
            revisionsSchema.DefineIndex(new TableSchema.SchemaIndexDef
            {
                StartIndex = (int)RevisionsTable.LowerId,
                Count = 3,
                Name = IdAndEtagSlice,
                IsGlobal = true
            });
            revisionsSchema.DefineFixedSizeIndex(new TableSchema.FixedSizeSchemaIndexDef
            {
                StartIndex = (int)RevisionsTable.Etag,
                Name = AllRevisionsEtagsSlice,
                IsGlobal = true
            });
            revisionsSchema.DefineFixedSizeIndex(new TableSchema.FixedSizeSchemaIndexDef
            {
                StartIndex = (int)RevisionsTable.Etag,
                Name = CollectionRevisionsEtagsSlice
            });
            revisionsSchema.DefineIndex(new TableSchema.SchemaIndexDef
            {
                StartIndex = (int)RevisionsTable.DeletedEtag,
                Count = 1,
                Name = DeleteRevisionEtagSlice,
                IsGlobal = true
            });
            revisionsSchema.DefineIndex(new TableSchema.SchemaIndexDef
            {
                StartIndex = (int)RevisionsTable.Resolved,
                Count = 2,
                Name = ResolvedFlagByEtagSlice,
                IsGlobal = true
            });
        }

        public void InitializeFromDatabaseRecord(DatabaseRecord dbRecord)
        {
            try
            {
                if (dbRecord.RevisionsForConflicts != null)
                    ConflictConfiguration.Default = dbRecord.RevisionsForConflicts;

                var revisions = dbRecord.Revisions;
                if (revisions == null ||
                    (revisions.Default == null && revisions.Collections.Count == 0))
                {
                    Configuration = null;
                    return;
                }

                if (revisions.Equals(Configuration))
                    return;

                Configuration = revisions;

                if (_logger.IsInfoEnabled)
                    _logger.Info("Revisions configuration changed");
            }
            catch (Exception e)
            {
                const string message = "Failed to enable revisions for documents as the revisions configuration " +
                          "in the database record is missing or not valid.";

                _database.NotificationCenter.Add(AlertRaised.Create(
                    _database.Name,
                    $"Revisions error in {_database.Name}", message,
                    AlertType.RevisionsConfigurationNotValid,
                    NotificationSeverity.Error,
                    _database.Name,
                    details: new ExceptionDetails(e)));

                if (_logger.IsOperationsEnabled)
                    _logger.Operations(message, e);
            }
        }

        private static void CreateTrees(Transaction tx)
        {
            tx.CreateTree(RevisionsCountSlice);
            TombstonesSchema.Create(tx, RevisionsTombstonesSlice, 16);
        }

        public RevisionsCollectionConfiguration GetRevisionsConfiguration(string collection, DocumentFlags flags = DocumentFlags.None)
        {
            if (Configuration == null)
            {
                if (flags.Contain(DocumentFlags.Resolved) || flags.Contain(DocumentFlags.Conflicted))
                {
                    return ConflictConfiguration.Default;
                }

                return _emptyConfiguration;
            }

            if (Configuration.Collections != null &&
                Configuration.Collections.TryGetValue(collection, out RevisionsCollectionConfiguration configuration))
                return configuration;

            if (Configuration.Default == null)
            {
                if (flags.Contain(DocumentFlags.Resolved) || flags.Contain(DocumentFlags.Conflicted))
                {
                    return ConflictConfiguration.Default;
                }
            }

            return Configuration.Default ?? _emptyConfiguration;
        }

        public bool ShouldVersionDocument(CollectionName collectionName, NonPersistentDocumentFlags nonPersistentFlags,
            BlittableJsonReaderObject existingDocument, BlittableJsonReaderObject document,
            DocumentsOperationContext context, string id,
            long? lastModifiedTicks,
            ref DocumentFlags documentFlags, out RevisionsCollectionConfiguration configuration)
        {
            configuration = GetRevisionsConfiguration(collectionName.Name, documentFlags);

            if (nonPersistentFlags.Contain(NonPersistentDocumentFlags.FromReplication))
                return false;

            if (nonPersistentFlags.Contain(NonPersistentDocumentFlags.SkipRevisionCreation))
                return false;

            if (nonPersistentFlags.Contain(NonPersistentDocumentFlags.FromSmuggler))
            {
                if (nonPersistentFlags.Contain(NonPersistentDocumentFlags.ByCountersUpdate))
                    return false;

                if (nonPersistentFlags.Contain(NonPersistentDocumentFlags.ByAttachmentUpdate))
                    return false;

                if (nonPersistentFlags.Contain(NonPersistentDocumentFlags.ByTimeSeriesUpdate))
                    return false;

                if (configuration == ConflictConfiguration.Default || configuration.Disabled)
                    return false;
            }

            if (nonPersistentFlags.Contain(NonPersistentDocumentFlags.Resolved))
                return true;

            if (Configuration == null)
                return false;

            if (configuration.Disabled)
                return false;

            if (configuration.MinimumRevisionsToKeep == 0)
            {
                DeleteRevisionsFor(context, id, configuration.MaximumRevisionsToDeleteUponDocumentUpdate);
                documentFlags = documentFlags.Strip(DocumentFlags.HasRevisions);
                return false;
            }

            if (configuration.MinimumRevisionAgeToKeep.HasValue && lastModifiedTicks.HasValue)
            {
                if (_database.Time.GetUtcNow().Ticks - lastModifiedTicks.Value > configuration.MinimumRevisionAgeToKeep.Value.Ticks)
                {
                    DeleteRevisionsFor(context, id, configuration.MaximumRevisionsToDeleteUponDocumentUpdate);
                    documentFlags = documentFlags.Strip(DocumentFlags.HasRevisions);
                    return false;
                }
            }

            if (existingDocument == null)
            {
                if (nonPersistentFlags.Contain(NonPersistentDocumentFlags.SkipRevisionCreationForSmuggler))
                {
                    // Smuggler is configured to avoid creating new revisions during import
                    return false;
                }

                // we are not going to create a revision if it's an import from v3
                // (since this import is going to import revisions as well)
                if (nonPersistentFlags.Contain(NonPersistentDocumentFlags.LegacyHasRevisions))
                {
                    documentFlags |= DocumentFlags.HasRevisions;
                    return false;
                }

                return true;
            }

            if (documentFlags.Contain(DocumentFlags.Reverted))
                return true; // we always want to create a new version for a reverted document

            // compare the contents of the existing and the new document
            if (DocumentCompare.IsEqualTo(existingDocument, document, DocumentCompare.DocumentCompareOptions.Default) != DocumentCompareResult.NotEqual)
            {
                // no need to create a new revision, both documents have identical content
                return false;
            }

            return true;
        }

        public bool ShouldVersionOldDocument(DocumentsOperationContext context, DocumentFlags flags, BlittableJsonReaderObject oldDoc, string changeVector, CollectionName collectionName)
        {
            if (oldDoc == null)
                return false; // no document to version

            if (flags.Contain(DocumentFlags.HasRevisions))
                return false; // version already exists

            if (flags.Contain(DocumentFlags.Resolved))
            {
                if (Configuration == null)
                    return false;
                var configuration = GetRevisionsConfiguration(collectionName.Name);

                if (configuration.Disabled)
                    return false;

                if (configuration.MinimumRevisionsToKeep == 0)
                    return false;

                using (Slice.From(context.Allocator, changeVector, out Slice changeVectorSlice))
                {
                    var table = EnsureRevisionTableCreated(context.Transaction.InnerTransaction, collectionName);
                    // True if we already versioned it with the a conflicted flag
                    // False if we didn't resolved the conflict locally

                    return (table.ReadByKey(changeVectorSlice, out var tvr) == false);
                }
            }

            return true;
        }

        public unsafe bool Put(DocumentsOperationContext context, string id, BlittableJsonReaderObject document,
            DocumentFlags flags, NonPersistentDocumentFlags nonPersistentFlags, string changeVector, long lastModifiedTicks,
            RevisionsCollectionConfiguration configuration = null, CollectionName collectionName = null)
        {
            Debug.Assert(changeVector != null, "Change vector must be set");
            Debug.Assert(lastModifiedTicks != DateTime.MinValue.Ticks, "last modified ticks must be set");

            BlittableJsonReaderObject.AssertNoModifications(document, id, assertChildren: true);

            if (collectionName == null)
                collectionName = _database.DocumentsStorage.ExtractCollectionName(context, document);
            if (configuration == null)
                configuration = GetRevisionsConfiguration(collectionName.Name, flags);

            if (configuration.Disabled &&
                nonPersistentFlags.Contain(NonPersistentDocumentFlags.FromReplication) == false &&
                nonPersistentFlags.Contain(NonPersistentDocumentFlags.ForceRevisionCreation) == false &&
                nonPersistentFlags.Contain(NonPersistentDocumentFlags.FromSmuggler) == false)
                return false;

            using (DocumentIdWorker.GetLowerIdSliceAndStorageKey(context, id, out Slice lowerId, out Slice idSlice))
            using (Slice.From(context.Allocator, changeVector, out Slice changeVectorSlice))
            {
                var table = EnsureRevisionTableCreated(context.Transaction.InnerTransaction, collectionName);
                var revisionExists = table.ReadByKey(changeVectorSlice, out var tvr);

                if (revisionExists)
                {
                    MarkRevisionsAsConflictedIfNeeded(context, lowerId, idSlice, flags, tvr, table, changeVectorSlice);
                    return false;
                }

                // We want the revision's attachments to have a lower etag than the revision itself
                if (flags.Contain(DocumentFlags.HasAttachments) &&
                    flags.Contain(DocumentFlags.Revision) == false)
                {
                    _documentsStorage.AttachmentsStorage.RevisionAttachments(context, lowerId, changeVectorSlice);
                }

                document = AddCounterAndTimeSeriesSnapshotsIfNeeded(context, id, document);

                document = PutFromRevisionIfChangeVectorIsGreater(context, document, id, changeVector, lastModifiedTicks, flags, nonPersistentFlags);

                if (table.VerifyKeyExists(changeVectorSlice)) // we might create
                    return true;

                flags |= DocumentFlags.Revision;
                var etag = _database.DocumentsStorage.GenerateNextEtag();
                var newEtagSwapBytes = Bits.SwapBytes(etag);

                using (table.Allocate(out TableValueBuilder tvb))
                {
                    tvb.Add(changeVectorSlice.Content.Ptr, changeVectorSlice.Size);
                    tvb.Add(lowerId);
                    tvb.Add(SpecialChars.RecordSeparator);
                    tvb.Add(newEtagSwapBytes);
                    tvb.Add(idSlice);
                    tvb.Add(document.BasePointer, document.Size);
                    tvb.Add((int)flags);
                    tvb.Add(NotDeletedRevisionMarker);
                    tvb.Add(lastModifiedTicks);
                    tvb.Add(context.GetTransactionMarker());
                    if (flags.Contain(DocumentFlags.Resolved))
                    {
                        tvb.Add((int)DocumentFlags.Resolved);
                    }
                    else
                    {
                        tvb.Add(0);
                    }
                    tvb.Add(Bits.SwapBytes(lastModifiedTicks));
                    table.Insert(tvb);
                }

                DeleteOldRevisions(context, table, lowerId, collectionName, configuration, nonPersistentFlags, changeVector, lastModifiedTicks);
            }

            return true;
        }

        private BlittableJsonReaderObject AddCounterAndTimeSeriesSnapshotsIfNeeded(DocumentsOperationContext context, string id, BlittableJsonReaderObject document)
        {
            if (document.TryGet(Constants.Documents.Metadata.Key, out BlittableJsonReaderObject metadata) == false)
                return document;

            if (metadata.TryGet(Constants.Documents.Metadata.Counters, out BlittableJsonReaderArray counterNames))
            {
                var dvj = new DynamicJsonValue();
                for (var i = 0; i < counterNames.Length; i++)
                {
                    var counter = counterNames[i].ToString();
                    var val = _documentsStorage.CountersStorage.GetCounterValue(context, id, counter, capOnOverflow: true)?.Value;
                    if (val == null)
                        continue;
                    dvj[counter] = val.Value;
                }

                metadata.Modifications = new DynamicJsonValue(metadata)
                {
                    [Constants.Documents.Metadata.RevisionCounters] = dvj
                };

                metadata.Modifications.Remove(Constants.Documents.Metadata.Counters);
            }

            if (metadata.TryGet(Constants.Documents.Metadata.TimeSeries, out BlittableJsonReaderArray timeSeriesNames))
            {
                var dvj = new DynamicJsonValue();
                for (var i = 0; i < timeSeriesNames.Length; i++)
                {
                    var name = timeSeriesNames[i].ToString();
                    var (count, start, end) = _documentsStorage.TimeSeriesStorage.Stats.GetStats(context, id, name);
                    Debug.Assert(start == default || start.Kind == DateTimeKind.Utc);

                    dvj[name] = new DynamicJsonValue
                    {
                        ["Count"] = count,
                        ["Start"] = start,
                        ["End"] = end
                    };
                }

                metadata.Modifications ??= new DynamicJsonValue(metadata);

                metadata.Modifications[Constants.Documents.Metadata.RevisionTimeSeries] = dvj;

                metadata.Modifications.Remove(Constants.Documents.Metadata.TimeSeries);
            }

            if (metadata.Modifications != null)
            {
                document.Modifications = new DynamicJsonValue(document)
                {
                    [Constants.Documents.Metadata.Key] = metadata
                };

                document = context.ReadObject(document, id, BlittableJsonDocumentBuilder.UsageMode.ToDisk);
            }

            return document;
        }

        private BlittableJsonReaderObject PutFromRevisionIfChangeVectorIsGreater(
            DocumentsOperationContext context,
            BlittableJsonReaderObject document,
            string id,
            string changeVector,
            long lastModifiedTicks,
            DocumentFlags flags,
            NonPersistentDocumentFlags nonPersistentFlags,
            CollectionName collectionName = null)
        {
            if (nonPersistentFlags.Contain(NonPersistentDocumentFlags.FromReplication) == false)
                return document;

            if ((flags.Contain(DocumentFlags.Revision) || flags.Contain(DocumentFlags.DeleteRevision)) == false)
                return document; // only revision can overwrite the document

            if (flags.Contain(DocumentFlags.Conflicted))
                return document; // but, conflicted revision can't

            using (DocumentIdWorker.GetLowerIdSliceAndStorageKey(context, id, out var lowerId, out _))
            {
                var conflictStatus = ConflictsStorage.GetConflictStatusForDocument(context, id, changeVector, out _);
                if (conflictStatus != ConflictStatus.Update)
                    return document; // Do not modify the document.

                if (flags.Contain(DocumentFlags.Resolved))
                {
                    _database.ReplicationLoader.ConflictResolver.SaveLocalAsRevision(context, id);
                }

                nonPersistentFlags |= NonPersistentDocumentFlags.SkipRevisionCreation;
                flags = flags.Strip(DocumentFlags.Revision | DocumentFlags.DeleteRevision) | DocumentFlags.HasRevisions;

                if (document == null)
                {
                    _documentsStorage.Delete(context, lowerId, id, null, lastModifiedTicks, changeVector, collectionName,
                        nonPersistentFlags, flags);
                    return null;
                }

                document = RevertSnapshotFlags(context, document, id);

                _documentsStorage.Put(context, id, null, document, lastModifiedTicks, changeVector,
                    null, flags, nonPersistentFlags);
            }

            return document;
        }

        private static bool RevertSnapshotFlag(BlittableJsonReaderObject metadata, string snapshotFlag, string flag)
        {
            if (metadata.TryGet(snapshotFlag, out BlittableJsonReaderObject bjro) == false)
                return false;

            var names = bjro.GetPropertyNames();

            metadata.Modifications ??= new DynamicJsonValue(metadata);
            metadata.Modifications.Remove(snapshotFlag);
            var arr = new DynamicJsonArray();
            foreach (var name in names)
            {
                arr.Add(name);
            }

            metadata.Modifications[flag] = arr;

            return true;
        }

        private static BlittableJsonReaderObject RevertSnapshotFlags(DocumentsOperationContext context, BlittableJsonReaderObject document, string documentId)
        {
            if (document.TryGet(Constants.Documents.Metadata.Key, out BlittableJsonReaderObject metadata) == false)
                return document;

            var metadataModified = RevertSnapshotFlag(metadata, Constants.Documents.Metadata.RevisionCounters, Constants.Documents.Metadata.Counters);
            metadataModified |= RevertSnapshotFlag(metadata, Constants.Documents.Metadata.RevisionTimeSeries, Constants.Documents.Metadata.TimeSeries);

            if (metadataModified)
            {
                document.Modifications = new DynamicJsonValue(document) { [Constants.Documents.Metadata.Key] = metadata };

                using (var old = document)
                    document = context.ReadObject(document, documentId, BlittableJsonDocumentBuilder.UsageMode.ToDisk);
            }

            return document;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DeleteOldRevisions(DocumentsOperationContext context, Table table, Slice lowerId, CollectionName collectionName,
            RevisionsCollectionConfiguration configuration, NonPersistentDocumentFlags nonPersistentFlags, string changeVector, long lastModifiedTicks)
        {
            using (GetKeyPrefix(context, lowerId, out Slice prefixSlice))
            {
                // We delete the old revisions after we put the current one,
                // because in case that MinimumRevisionsToKeep is 3 or lower we may get a revision document from replication
                // which is old. But because we put it first, we make sure to clean this document, because of the order to the revisions.
                var revisionsCount = IncrementCountOfRevisions(context, prefixSlice, 1);
                DeleteOldRevisions(context, table, prefixSlice, collectionName, configuration, revisionsCount, nonPersistentFlags, changeVector, lastModifiedTicks);
            }
        }

        private bool DeleteOldRevisions(DocumentsOperationContext context, Table table, Slice prefixSlice, CollectionName collectionName,
            RevisionsCollectionConfiguration configuration, long revisionsCount, NonPersistentDocumentFlags nonPersistentFlags, string changeVector, long lastModifiedTicks)
        {
            var moreRevisionToDelete = false;
            if (nonPersistentFlags.Contain(NonPersistentDocumentFlags.FromSmuggler))
                return false;

            if (nonPersistentFlags.Contain(NonPersistentDocumentFlags.FromReplication))
                return false;

            if (configuration.MinimumRevisionsToKeep.HasValue == false &&
                configuration.MinimumRevisionAgeToKeep.HasValue == false)
                return false;

            long numberOfRevisionsToDelete;
            if (configuration.MinimumRevisionsToKeep.HasValue)
            {
                numberOfRevisionsToDelete = revisionsCount - configuration.MinimumRevisionsToKeep.Value;
                if (numberOfRevisionsToDelete > 0 && configuration.MaximumRevisionsToDeleteUponDocumentUpdate.HasValue && configuration.MaximumRevisionsToDeleteUponDocumentUpdate.Value < numberOfRevisionsToDelete)
                {
                    numberOfRevisionsToDelete = configuration.MaximumRevisionsToDeleteUponDocumentUpdate.Value;
                    moreRevisionToDelete = true;
                }

                if (numberOfRevisionsToDelete <= 0)
                    return false;
            }
            else
            {
                moreRevisionToDelete = configuration.MaximumRevisionsToDeleteUponDocumentUpdate.HasValue;
                // delete all revisions which age has passed
                numberOfRevisionsToDelete = configuration.MaximumRevisionsToDeleteUponDocumentUpdate ?? long.MaxValue;
            }

            var deletedRevisionsCount = DeleteRevisions(context, table, prefixSlice, collectionName, numberOfRevisionsToDelete, configuration.MinimumRevisionAgeToKeep, changeVector, lastModifiedTicks);

            Debug.Assert(numberOfRevisionsToDelete >= deletedRevisionsCount);
            IncrementCountOfRevisions(context, prefixSlice, -deletedRevisionsCount);
            return moreRevisionToDelete && deletedRevisionsCount == numberOfRevisionsToDelete;
        }

        public void DeleteRevisionsFor(DocumentsOperationContext context, string id, long? maximumRevisionsToDeleteUponDocumentUpdate = long.MaxValue)
        {
            using (DocumentIdWorker.GetSliceFromId(context, id, out Slice lowerId))
            using (GetKeyPrefix(context, lowerId, out Slice prefixSlice))
            {
                var collectionName = GetCollectionFor(context, prefixSlice);
                if (collectionName == null)
                {
                    if (_logger.IsInfoEnabled)
                        _logger.Info($"Tried to delete all revisions for '{id}' but no revisions found.");
                    return;
                }

                var table = EnsureRevisionTableCreated(context.Transaction.InnerTransaction, collectionName);
                var newEtag = _documentsStorage.GenerateNextEtag();
                var changeVector = _documentsStorage.GetNewChangeVector(context, newEtag);
                context.LastDatabaseChangeVector = changeVector;
                var lastModifiedTicks = _database.Time.GetUtcNow().Ticks;
                DeleteRevisions(context, table, prefixSlice, collectionName, maximumRevisionsToDeleteUponDocumentUpdate ?? long.MaxValue, null, changeVector, lastModifiedTicks);
                DeleteCountOfRevisions(context, prefixSlice);
            }
        }

        public void DeleteRevisionsBefore(DocumentsOperationContext context, string collection, DateTime time)
        {
            var collectionName = new CollectionName(collection);
            var table = EnsureRevisionTableCreated(context.Transaction.InnerTransaction, collectionName);
            table.DeleteByPrimaryKey(Slices.BeforeAllKeys, deleted =>
            {
                var lastModified = TableValueToDateTime((int)RevisionsTable.LastModified, ref deleted.Reader);
                if (lastModified >= time)
                    return false;

                // We won't create tombstones here as it might create LOTS of tombstones
                // with the same transaction marker and the same change vector.

                using (TableValueToSlice(context, (int)RevisionsTable.LowerId, ref deleted.Reader, out Slice lowerId))
                using (GetKeyPrefix(context, lowerId, out Slice prefixSlice))
                {
                    IncrementCountOfRevisions(context, prefixSlice, -1);
                }

                return true;
            });
        }

        private unsafe CollectionName GetCollectionFor(DocumentsOperationContext context, Slice prefixSlice)
        {
            var table = new Table(RevisionsSchema, context.Transaction.InnerTransaction);
            var tvr = table.SeekOneForwardFromPrefix(RevisionsSchema.Indexes[IdAndEtagSlice], prefixSlice);
            if (tvr == null)
                return null;

            var ptr = tvr.Reader.Read((int)RevisionsTable.Document, out int size);
            var data = new BlittableJsonReaderObject(ptr, size, context);

            return _documentsStorage.ExtractCollectionName(context, data);
        }

        public IEnumerable<string> GetCollections(Transaction transaction)
        {
            using (var it = transaction.LowLevelTransaction.RootObjects.Iterate(false))
            {
                it.SetRequiredPrefix(RevisionsPrefix);

                if (it.Seek(RevisionsPrefix) == false)
                    yield break;

                do
                {
                    var collection = it.CurrentKey.ToString();
                    yield return collection.Substring(RevisionsPrefix.Size);
                }
                while (it.MoveNext());
            }
        }

        private long DeleteRevisions(DocumentsOperationContext context, Table table, Slice prefixSlice, CollectionName collectionName,
            long numberOfRevisionsToDelete, TimeSpan? minimumTimeToKeep, string changeVector, long lastModifiedTicks)
        {
            long maxEtagDeleted = 0;
            Table writeTable = null;
            string currentCollection = null;
            var deletedRevisionsCount = 0;

            while (true)
            {
                var hasValue = false;

                foreach (var read in table.SeekForwardFrom(RevisionsSchema.Indexes[IdAndEtagSlice], prefixSlice, skip: 0, startsWith: true))
                {
                    if (numberOfRevisionsToDelete <= deletedRevisionsCount)
                        break;

                    var tvr = read.Result.Reader;
                    using (var revision = TableValueToRevision(context, ref tvr))
                    {
                        if (minimumTimeToKeep.HasValue &&
                            _database.Time.GetUtcNow() - revision.LastModified <= minimumTimeToKeep.Value)
                            return deletedRevisionsCount;

                        hasValue = true;

                        using (Slice.From(context.Allocator, revision.ChangeVector, out var keySlice))
                        {
                            CreateTombstone(context, keySlice, revision.Etag, collectionName, changeVector, lastModifiedTicks);

                            maxEtagDeleted = Math.Max(maxEtagDeleted, revision.Etag);
                            if ((revision.Flags & DocumentFlags.HasAttachments) == DocumentFlags.HasAttachments)
                            {
                                _documentsStorage.AttachmentsStorage.DeleteRevisionAttachments(context, revision, changeVector, lastModifiedTicks);
                            }

                            var docCollection = CollectionName.GetCollectionName(revision.Data);
                            if (writeTable == null || docCollection != currentCollection)
                            {
                                currentCollection = docCollection;
                                writeTable = EnsureRevisionTableCreated(context.Transaction.InnerTransaction, new CollectionName(docCollection));
                            }

                            writeTable.DeleteByKey(keySlice);
                        }
                    }

                    deletedRevisionsCount++;
                    break;
                }

                if (hasValue == false)
                    break;
            }

            _database.DocumentsStorage.EnsureLastEtagIsPersisted(context, maxEtagDeleted);
            return deletedRevisionsCount;
        }

        public void DeleteRevision(DocumentsOperationContext context, Slice key, string collection, string changeVector, long lastModifiedTicks)
        {
            var collectionName = _documentsStorage.ExtractCollectionName(context, collection);
            var table = EnsureRevisionTableCreated(context.Transaction.InnerTransaction, collectionName);

            long revisionEtag;
            if (table.ReadByKey(key, out TableValueReader tvr))
            {
                using (TableValueToSlice(context, (int)RevisionsTable.LowerId, ref tvr, out Slice lowerId))
                using (GetKeyPrefix(context, lowerId, out Slice prefixSlice))
                {
                    IncrementCountOfRevisions(context, prefixSlice, -1);
                }

                revisionEtag = TableValueToEtag((int)RevisionsTable.Etag, ref tvr);

                if (table.IsOwned(tvr.Id) == false)
                {
                    // We request to delete revision with the wrong collection
                    var revision = TableValueToRevision(context, ref tvr);
                    var currentCollection = _documentsStorage.ExtractCollectionName(context, revision.Data);
                    table = EnsureRevisionTableCreated(context.Transaction.InnerTransaction, currentCollection);

                    if (table.IsOwned(tvr.Id) == false) // this shouldn't happened
                        throw new VoronErrorException(
                            $"Failed to remove revision {key} (id:{revision.Id}) of collection '{currentCollection}' from table '{table.Name}', in order to replace with the collection '{collection}'");
                }
                table.Delete(tvr.Id);
            }
            else
            {
                var tombstoneTable = context.Transaction.InnerTransaction.OpenTable(TombstonesSchema, RevisionsTombstonesSlice);
                if (tombstoneTable.VerifyKeyExists(key))
                    return;

                // we need to generate a unique etag if we got a tombstone revisions from replication,
                // but we don't want to mess up the order of events so the delete revision etag we use is negative
                revisionEtag = _documentsStorage.GenerateNextEtagForReplicatedTombstoneMissingDocument(context);
            }
            CreateTombstone(context, key, revisionEtag, collectionName, changeVector, lastModifiedTicks);
        }

        private unsafe void CreateTombstone(DocumentsOperationContext context, Slice keySlice, long revisionEtag,
            CollectionName collectionName, string changeVector, long lastModifiedTicks)
        {
            var newEtag = _documentsStorage.GenerateNextEtag();

            var table = context.Transaction.InnerTransaction.OpenTable(TombstonesSchema, RevisionsTombstonesSlice);
            if (table.VerifyKeyExists(keySlice))
                return; // revisions (and revisions tombstones) are immutable, we can safely ignore this

            using (DocumentIdWorker.GetStringPreserveCase(context, collectionName.Name, out Slice collectionSlice))
            using (Slice.From(context.Allocator, changeVector, out var cv))
            using (table.Allocate(out TableValueBuilder tvb))
            {
                tvb.Add(keySlice.Content.Ptr, keySlice.Size);
                tvb.Add(Bits.SwapBytes(newEtag));
                tvb.Add(Bits.SwapBytes(revisionEtag));
                tvb.Add(context.GetTransactionMarker());
                tvb.Add((byte)Tombstone.TombstoneType.Revision);
                tvb.Add(collectionSlice);
                tvb.Add((int)DocumentFlags.None);
                tvb.Add(cv.Content.Ptr, cv.Size);
                tvb.Add(lastModifiedTicks);
                table.Set(tvb);
            }
        }

        private static long IncrementCountOfRevisions(DocumentsOperationContext context, Slice prefixedLowerId, long delta)
        {
            var numbers = context.Transaction.InnerTransaction.ReadTree(RevisionsCountSlice);
            return numbers.Increment(prefixedLowerId, delta);
        }

        private static void DeleteCountOfRevisions(DocumentsOperationContext context, Slice prefixedLowerId)
        {
            var numbers = context.Transaction.InnerTransaction.ReadTree(RevisionsCountSlice);
            numbers.Delete(prefixedLowerId);
        }

        public void Delete(DocumentsOperationContext context, string id, Slice lowerId, CollectionName collectionName, string changeVector,
            long lastModifiedTicks, NonPersistentDocumentFlags nonPersistentFlags, DocumentFlags flags)
        {
            using (DocumentIdWorker.GetStringPreserveCase(context, id, out Slice idPtr))
            {
                var deleteRevisionDocument = context.ReadObject(new DynamicJsonValue
                {
                    [Constants.Documents.Metadata.Key] = new DynamicJsonValue
                    {
                        [Constants.Documents.Metadata.Collection] = collectionName.Name
                    }
                }, "RevisionsBin");
                Delete(context, lowerId, idPtr, id, collectionName, deleteRevisionDocument, changeVector, lastModifiedTicks, nonPersistentFlags, flags);
            }
        }

        public void Delete(DocumentsOperationContext context, string id, BlittableJsonReaderObject deleteRevisionDocument,
            DocumentFlags flags, NonPersistentDocumentFlags nonPersistentFlags, string changeVector, long lastModifiedTicks)
        {
            BlittableJsonReaderObject.AssertNoModifications(deleteRevisionDocument, id, assertChildren: true);

            using (DocumentIdWorker.GetLowerIdSliceAndStorageKey(context, id, out Slice lowerId, out Slice idPtr))
            {
                var collectionName = _documentsStorage.ExtractCollectionName(context, deleteRevisionDocument);
                Delete(context, lowerId, idPtr, id, collectionName, deleteRevisionDocument, changeVector, lastModifiedTicks, nonPersistentFlags, flags);
            }
        }

        private unsafe void Delete(DocumentsOperationContext context, Slice lowerId, Slice idSlice, string id, CollectionName collectionName,
            BlittableJsonReaderObject deleteRevisionDocument, string changeVector,
            long lastModifiedTicks, NonPersistentDocumentFlags nonPersistentFlags, DocumentFlags flags)
        {
            if (nonPersistentFlags.Contain(NonPersistentDocumentFlags.SkipRevisionCreation))
                return;

            Debug.Assert(changeVector != null, "Change vector must be set");

            flags = flags.Strip(DocumentFlags.HasAttachments);
            flags |= DocumentFlags.HasRevisions;

            var fromReplication = nonPersistentFlags.Contain(NonPersistentDocumentFlags.FromReplication);

            var configuration = GetRevisionsConfiguration(collectionName.Name, flags);
            if (configuration.Disabled && fromReplication == false)
                return;

            var table = EnsureRevisionTableCreated(context.Transaction.InnerTransaction, collectionName);

            using (Slice.From(context.Allocator, changeVector, out var changeVectorSlice))
            {
                var revisionExists = table.ReadByKey(changeVectorSlice, out var tvr);
                if (revisionExists)
                {
                    MarkRevisionsAsConflictedIfNeeded(context, lowerId, idSlice, flags, tvr, table, changeVectorSlice);
                    return;
                }

                if (configuration.Disabled == false && configuration.PurgeOnDelete)
                {
                    using (GetKeyPrefix(context, lowerId, out var prefixSlice))
                    {
                        DeleteRevisions(context, table, prefixSlice, collectionName, long.MaxValue, null, changeVector, lastModifiedTicks);
                        DeleteCountOfRevisions(context, prefixSlice);
                    }
                    return;
                }

                PutFromRevisionIfChangeVectorIsGreater(context, null, id, changeVector, lastModifiedTicks, flags, nonPersistentFlags, collectionName);

                var newEtag = _database.DocumentsStorage.GenerateNextEtag();
                var newEtagSwapBytes = Bits.SwapBytes(newEtag);

                using (table.Allocate(out TableValueBuilder tvb))
                {
                    tvb.Add(changeVectorSlice.Content.Ptr, changeVectorSlice.Size);
                    tvb.Add(lowerId);
                    tvb.Add(SpecialChars.RecordSeparator);
                    tvb.Add(newEtagSwapBytes);
                    tvb.Add(idSlice);
                    tvb.Add(deleteRevisionDocument.BasePointer, deleteRevisionDocument.Size);
                    tvb.Add((int)(DocumentFlags.DeleteRevision | flags));
                    tvb.Add(newEtagSwapBytes);
                    tvb.Add(lastModifiedTicks);
                    tvb.Add(context.GetTransactionMarker());
                    if (flags.Contain(DocumentFlags.Resolved))
                    {
                        tvb.Add((int)DocumentFlags.Resolved);
                    }
                    else
                    {
                        tvb.Add(0);
                    }
                    tvb.Add(Bits.SwapBytes(lastModifiedTicks));
                    table.Insert(tvb);
                }

                DeleteOldRevisions(context, table, lowerId, collectionName, configuration, nonPersistentFlags, changeVector, lastModifiedTicks);
            }
        }

        private void MarkRevisionsAsConflictedIfNeeded(DocumentsOperationContext context, Slice lowerId, Slice idSlice, DocumentFlags flags, TableValueReader tvr, Table table,
            Slice changeVectorSlice)
        {
            // Revisions are immutable, but if there was a conflict we need to update the flags accordingly with the `Conflicted` flag.
            if (flags.Contain(DocumentFlags.Conflicted))
            {
                var currentFlags = TableValueToFlags((int)RevisionsTable.Flags, ref tvr);
                if (currentFlags.Contain(DocumentFlags.Conflicted) == false)
                {
                    MarkRevisionAsConflicted(context, tvr, table, changeVectorSlice, lowerId, idSlice);
                }
            }
        }

        private unsafe void MarkRevisionAsConflicted(DocumentsOperationContext context, TableValueReader tvr, Table table, Slice changeVectorSlice, Slice lowerId, Slice idSlice)
        {
            var revisionCopy = context.GetMemory(tvr.Size);
            // we have to copy it to the side because we might do a defrag during update, and that
            // can cause corruption if we read from the old value (which we just deleted)
            Memory.Copy(revisionCopy.Address, tvr.Pointer, tvr.Size);
            var copyTvr = new TableValueReader(revisionCopy.Address, tvr.Size);

            var revision = TableValueToRevision(context, ref copyTvr);
            var flags = revision.Flags | DocumentFlags.Conflicted;
            var newEtag = _database.DocumentsStorage.GenerateNextEtag();
            var deletedEtag = TableValueToEtag((int)RevisionsTable.DeletedEtag, ref tvr);
            var resolvedFlag = TableValueToFlags((int)RevisionsTable.Resolved, ref tvr);

            using (table.Allocate(out TableValueBuilder tvb))
            {
                tvb.Add(changeVectorSlice.Content.Ptr, changeVectorSlice.Size);
                tvb.Add(lowerId);
                tvb.Add(SpecialChars.RecordSeparator);
                tvb.Add(Bits.SwapBytes(newEtag));
                tvb.Add(idSlice);
                tvb.Add(revision.Data.BasePointer, revision.Data.Size);
                tvb.Add((int)flags);
                tvb.Add(Bits.SwapBytes(deletedEtag));
                tvb.Add(revision.LastModified.Ticks);
                tvb.Add(context.GetTransactionMarker());
                tvb.Add((int)resolvedFlag);
                tvb.Add(Bits.SwapBytes(revision.LastModified.Ticks));
                table.Set(tvb);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe ByteStringContext.InternalScope GetKeyPrefix(DocumentsOperationContext context, Slice lowerId, out Slice prefixSlice)
        {
            return GetKeyPrefix(context, lowerId.Content.Ptr, lowerId.Size, out prefixSlice);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe ByteStringContext.InternalScope GetKeyPrefix(DocumentsOperationContext context, byte* lowerId, int lowerIdSize, out Slice prefixSlice)
        {
            var scope = context.Allocator.Allocate(lowerIdSize + 1, out ByteString keyMem);

            Memory.Copy(keyMem.Ptr, lowerId, lowerIdSize);
            keyMem.Ptr[lowerIdSize] = SpecialChars.RecordSeparator;

            prefixSlice = new Slice(SliceOptions.Key, keyMem);
            return scope;
        }

        private static ByteStringContext.InternalScope GetLastKey(DocumentsOperationContext context, Slice lowerId, out Slice prefixSlice)
        {
            return GetKeyWithEtag(context, lowerId, long.MaxValue, out prefixSlice);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe ByteStringContext<ByteStringMemoryCache>.InternalScope GetKeyWithEtag(DocumentsOperationContext context, Slice lowerId, long etag, out Slice prefixSlice)
        {
            var scope = context.Allocator.Allocate(lowerId.Size + 1 + sizeof(long), out ByteString keyMem);

            Memory.Copy(keyMem.Ptr, lowerId.Content.Ptr, lowerId.Size);
            keyMem.Ptr[lowerId.Size] = SpecialChars.RecordSeparator;

            var maxValue = Bits.SwapBytes(etag);
            Memory.Copy(keyMem.Ptr + lowerId.Size + 1, (byte*)&maxValue, sizeof(long));

            prefixSlice = new Slice(SliceOptions.Key, keyMem);
            return scope;
        }

        private static long CountOfRevisions(DocumentsOperationContext context, Slice prefix)
        {
            var numbers = context.Transaction.InnerTransaction.ReadTree(RevisionsCountSlice);
            return numbers.Read(prefix)?.Reader.ReadLittleEndianInt64() ?? 0;
        }

        public Document GetRevisionBefore(DocumentsOperationContext context, string id, DateTime max)
        {
            using (DocumentIdWorker.GetSliceFromId(context, id, out Slice lowerId))
            using (GetKeyPrefix(context, lowerId, out Slice prefixSlice))
            using (GetLastKey(context, lowerId, out Slice lastKey))
            {
                // Here we assume a reasonable number of revisions and scan the entire history
                // This is because we want to handle out of order revisions from multiple nodes so the local etag
                // order is different than the last modified order
                Document result = null;
                var table = new Table(RevisionsSchema, context.Transaction.InnerTransaction);
                foreach (var tvr in table.SeekBackwardFrom(RevisionsSchema.Indexes[IdAndEtagSlice], prefixSlice, lastKey, 0))
                {
                    var lastModified = TableValueToDateTime((int)RevisionsTable.LastModified, ref tvr.Result.Reader);
                    if (lastModified > max)
                        continue;

                    if (result == null ||
                        result.LastModified < lastModified)
                    {
                        result = TableValueToRevision(context, ref tvr.Result.Reader);
                    }
                }
                return result;
            }
        }

        private unsafe Document GetRevisionBefore(DocumentsOperationContext context,
            Parameters parameters,
            string id,
            RevertResult progressResult)
        {
            var foundAfter = false;

            using (DocumentIdWorker.GetSliceFromId(context, id, out Slice lowerId))
            using (GetKeyPrefix(context, lowerId, out Slice prefixSlice))
            using (GetLastKey(context, lowerId, out Slice lastKey))
            {
                // Here we assume a reasonable number of revisions and scan the entire history
                // This is because we want to handle out of order revisions from multiple nodes so the local etag
                // order is different than the last modified order
                Document result = null;
                Document prev = null;
                string collection = null;

                var table = new Table(RevisionsSchema, context.Transaction.InnerTransaction);
                foreach (var tvr in table.SeekBackwardFrom(RevisionsSchema.Indexes[IdAndEtagSlice], prefixSlice, lastKey, 0))
                {
                    if (collection == null)
                    {
                        var ptr = tvr.Result.Reader.Read((int)RevisionsTable.Document, out var size);
                        var data = new BlittableJsonReaderObject(ptr, size, context);
                        collection = _documentsStorage.ExtractCollectionName(context, data).Name;
                    }

                    var etag = TableValueToEtag((int)RevisionsTable.Etag, ref tvr.Result.Reader);
                    if (etag > parameters.EtagBarrier)
                    {
                        progressResult.Warn(id, "This document wouldn't be reverted, because it changed after the revert progress started.");
                        return null;
                    }

                    var lastModified = TableValueToDateTime((int)RevisionsTable.LastModified, ref tvr.Result.Reader);
                    if (lastModified > parameters.Before)
                    {
                        foundAfter = true;
                        continue;
                    }

                    if (lastModified < parameters.MinimalDate)
                    {
                        // this is a very old revision, and we should stop here
                        if (result == null)
                        {
                            // we will take this old revision if no other was found
                            result = TableValueToRevision(context, ref tvr.Result.Reader);
                            prev = result;
                        }
                        break;
                    }

                    if (result == null)
                    {
                        result = TableValueToRevision(context, ref tvr.Result.Reader);
                        prev = result;
                        continue;
                    }

                    if (result.LastModified < lastModified)
                    {
                        prev = result;
                        result = TableValueToRevision(context, ref tvr.Result.Reader);
                        continue;
                    }

                    if (prev.LastModified < lastModified)
                    {
                        prev = TableValueToRevision(context, ref tvr.Result.Reader);
                    }
                }

                if (prev != result)
                {
                    // put at 8:50
                    // conflict at 9:10
                    // resolved at 9:30

                    // revert to 9:00 should work
                    // revert to 9:20 should fail

                    if (prev.Flags.Contain(DocumentFlags.Conflicted) && result.Flags.Contain(DocumentFlags.Conflicted))
                    {
                        // found two successive conflicted revisions, which means we were in a conflicted state.
                        progressResult.Warn(id, $"Skip revert, since the document was conflicted during '{parameters.Before}'.");
                        return null;
                    }
                }

                if (foundAfter == false)
                    return null; // nothing do to, no changes were found

                if (result == null) // no revision before POT was found
                {
                    var count = CountOfRevisions(context, prefixSlice);
                    var revisionsToKeep = GetRevisionsConfiguration(collection).MinimumRevisionsToKeep;
                    if (revisionsToKeep == null || count < revisionsToKeep)
                    {
                        var copy = lowerId.Clone(context.Allocator);

                        // document was created after POT so we need to delete it.
                        return new Document
                        {
                            Flags = DocumentFlags.DeleteRevision,
                            LowerId = context.AllocateStringValue(null, copy.Content.Ptr, copy.Size),
                            Id = context.GetLazyString(id)
                        };
                    }

                    var first = table.SeekOneForwardFromPrefix(RevisionsSchema.Indexes[IdAndEtagSlice], prefixSlice);
                    if (first == null)
                        return null;

                    // document reached max number of revisions. So we take the oldest.
                    progressResult.Warn(id,
                        $"Reverted to oldest revision, since no revision prior to '{parameters.Before}' was found and you reached the maximum number of revisions ({count}).");
                    return TableValueToRevision(context, ref first.Reader);
                }

                return result;
            }
        }

        public async Task<IOperationResult> EnforceConfiguration(Action<IOperationProgress> onProgress, OperationCancelToken token)
        {
            var parameters = new Parameters
            {
                Before = DateTime.MinValue,
                MinimalDate = DateTime.MinValue,
                EtagBarrier = _documentsStorage.GenerateNextEtag(),
                OnProgress = onProgress
            };

            parameters.LastScannedEtag = parameters.EtagBarrier;

            var result = new EnforceConfigurationResult();
            var ids = new List<string>();
            var sw = Stopwatch.StartNew();

            // send initial progress
            parameters.OnProgress?.Invoke(result);

            var hasMore = true;
            while (hasMore)
            {
                hasMore = false;
                ids.Clear();
                token.Delay();
                sw.Restart();

                using (_database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext ctx))
                {
                    using (ctx.OpenReadTransaction())
                    {
                        var revisions = new Table(RevisionsSchema, ctx.Transaction.InnerTransaction);
                        foreach (var tvr in revisions.SeekBackwardFrom(RevisionsSchema.FixedSizeIndexes[AllRevisionsEtagsSlice],
                            parameters.LastScannedEtag))
                        {
                            token.ThrowIfCancellationRequested();

                            var state = ShouldProcessNextRevisionId(ctx, ref tvr.Reader, parameters, result, out var id);
                            if (state == NextRevisionIdResult.Break)
                                break;
                            if (state == NextRevisionIdResult.Continue)
                            {
                                if (CanContinueBatch(ids, sw.Elapsed, ctx) == false)
                                {
                                    hasMore = true;
                                    break;
                                }
                                else
                                    continue;
                            }

                            ids.Add(id);

                            if (CanContinueBatch(ids, sw.Elapsed, ctx) == false)
                            {
                                hasMore = true;
                                break;
                            }
                        }
                    }

                    EnforceRevisionConfigurationCommand cmd;
                    do
                    {
                        token.Delay();
                        cmd = new EnforceRevisionConfigurationCommand(this, ids, result, token);
                        await _database.TxMerger.Enqueue(cmd);
                    } while (cmd.MoreWork);
                }
            }

            return result;

            bool CanContinueBatch(List<string> idsToCheck, TimeSpan elapsed, JsonOperationContext context)
            {
                if (idsToCheck.Count > 1024)
                    return false;

                if (elapsed > MaxEnforceConfigurationSingleBatchTime)
                    return false;

                if (context.AllocatedMemory > SizeLimit)
                    return false;

                return true;
            }
        }

        private static readonly RevisionsCollectionConfiguration ZeroConfiguration = new RevisionsCollectionConfiguration
        {
            MinimumRevisionsToKeep = 0
        };

        private long EnforceConfigurationFor(DocumentsOperationContext context, string id, ref bool moreWork)
        {
            using (DocumentIdWorker.GetSliceFromId(context, id, out var lowerId))
            using (GetKeyPrefix(context, lowerId, out var prefixSlice))
            {
                var collectionName = GetCollectionFor(context, prefixSlice);
                if (collectionName == null)
                {
                    if (_logger.IsInfoEnabled)
                        _logger.Info($"Tried to delete revisions for '{id}' but no revisions found.");
                    return 0;
                }

                var table = EnsureRevisionTableCreated(context.Transaction.InnerTransaction, collectionName);
                var newEtag = _documentsStorage.GenerateNextEtag();
                var changeVector = _documentsStorage.GetNewChangeVector(context, newEtag);
                context.LastDatabaseChangeVector = changeVector;
                var lastModifiedTicks = _database.Time.GetUtcNow().Ticks;

                var prevRevisionsCount = GetRevisionsCount(context, id);
                var configuration = GetRevisionsConfiguration(collectionName.Name);

                if (configuration == ConflictConfiguration.Default || configuration == _emptyConfiguration)
                {
                    configuration = ZeroConfiguration;
                }

                var needToDeleteMore = DeleteOldRevisions(context, table, prefixSlice, collectionName, configuration, prevRevisionsCount,
                    NonPersistentDocumentFlags.None,
                    changeVector, lastModifiedTicks);

                var currentRevisionsCount = GetRevisionsCount(context, id);

                if (needToDeleteMore && currentRevisionsCount > 0)
                    moreWork = true;

                if (currentRevisionsCount == 0)
                {
                    var res = _documentsStorage.GetDocumentOrTombstone(context, lowerId, throwOnConflict: false);
                    // need to strip the HasRevisions flag from the document/tombstone
                    if (res.Tombstone != null)
                        _documentsStorage.Delete(context, lowerId, id, null, nonPersistentFlags: NonPersistentDocumentFlags.ByEnforceRevisionConfiguration);

                    if (res.Document != null)
                        _documentsStorage.Put(context, id, null, res.Document.Data.Clone(context),
                            nonPersistentFlags: NonPersistentDocumentFlags.ByEnforceRevisionConfiguration);
                }

                return prevRevisionsCount - currentRevisionsCount;
            }
        }

        private class EnforceRevisionConfigurationCommand : TransactionOperationsMerger.MergedTransactionCommand
        {
            private readonly RevisionsStorage _revisionsStorage;
            private readonly List<string> _ids;
            private readonly EnforceConfigurationResult _result;
            private readonly OperationCancelToken _token;

            public bool MoreWork;

            public EnforceRevisionConfigurationCommand(
                RevisionsStorage revisionsStorage,
                List<string> ids,
                EnforceConfigurationResult result,
                OperationCancelToken token)
            {
                _revisionsStorage = revisionsStorage;
                _ids = ids;
                _result = result;
                _token = token;
            }

            protected override long ExecuteCmd(DocumentsOperationContext context)
            {
                MoreWork = false;
                foreach (var id in _ids)
                {
                    _token.ThrowIfCancellationRequested();
                    _result.RemovedRevisions += (int)_revisionsStorage.EnforceConfigurationFor(context, id, ref MoreWork);
                }

                return _ids.Count;
            }

            public override TransactionOperationsMerger.IReplayableCommandDto<TransactionOperationsMerger.MergedTransactionCommand> ToDto<TTransaction>(TransactionOperationContext<TTransaction> context)
            {
                return new EnforceRevisionConfigurationCommandDto(_revisionsStorage, _ids);
            }

            private class EnforceRevisionConfigurationCommandDto : TransactionOperationsMerger.IReplayableCommandDto<EnforceRevisionConfigurationCommand>
            {
                private readonly RevisionsStorage _revisionsStorage;
                private readonly List<string> _ids;

                public EnforceRevisionConfigurationCommandDto(RevisionsStorage revisionsStorage, List<string> ids)
                {
                    _revisionsStorage = revisionsStorage;
                    _ids = ids;
                }

                public EnforceRevisionConfigurationCommand ToCommand(DocumentsOperationContext context, DocumentDatabase database)
                {
                    return new EnforceRevisionConfigurationCommand(_revisionsStorage, _ids, new EnforceConfigurationResult(), OperationCancelToken.None);
                }
            }
        }

        private const long SizeLimit = 32 * 1_024 * 1_024;

        private class Parameters
        {
            public DateTime Before;
            public DateTime MinimalDate;
            public long EtagBarrier;
            public long LastScannedEtag;
            public readonly HashSet<string> ScannedIds = new HashSet<string>();
            public Action<IOperationProgress> OnProgress;
        }

        public async Task<IOperationResult> RevertRevisions(DateTime before, TimeSpan window, Action<IOperationProgress> onProgress, OperationCancelToken token)
        {
            var list = new List<Document>();
            var result = new RevertResult();

            var parameters = new Parameters
            {
                Before = before,
                MinimalDate = before.Add(-window), // since the documents/revisions are not sorted by date, stop searching if we reached this date.
                EtagBarrier = _documentsStorage.GenerateNextEtag(), // every change after this etag, will _not_ be reverted.
                OnProgress = onProgress
            };
            parameters.LastScannedEtag = parameters.EtagBarrier;

            // send initial progress
            parameters.OnProgress?.Invoke(result);

            var hasMore = true;
            while (hasMore)
            {
                token.Delay();

                using (_database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext writeCtx))
                {
                    hasMore = PrepareRevertedRevisions(writeCtx, parameters, result, list, token);
                    await WriteRevertedRevisions(list, token);
                }
            }

            return result;
        }

        private async Task WriteRevertedRevisions(List<Document> list, OperationCancelToken token)
        {
            if (list.Count == 0)
                return;

            await _database.TxMerger.Enqueue(new RevertDocumentsCommand(list, token));

            list.Clear();
        }

        private bool PrepareRevertedRevisions(DocumentsOperationContext writeCtx, Parameters parameters, RevertResult result, List<Document> list, OperationCancelToken token)
        {
            using (_database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext readCtx))
            using (readCtx.OpenReadTransaction())
            {
                var revisions = new Table(RevisionsSchema, readCtx.Transaction.InnerTransaction);
                foreach (var tvr in revisions.SeekBackwardFrom(RevisionsSchema.FixedSizeIndexes[AllRevisionsEtagsSlice],
                    parameters.LastScannedEtag))
                {
                    token.ThrowIfCancellationRequested();

                    var state = ShouldProcessNextRevisionId(readCtx, ref tvr.Reader, parameters, result, out var id);
                    if (state == NextRevisionIdResult.Break)
                        break;
                    if (state == NextRevisionIdResult.Continue)
                        continue;

                    RestoreRevision(readCtx, writeCtx, parameters, id, result, list);

                    if (readCtx.AllocatedMemory + writeCtx.AllocatedMemory > SizeLimit)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        private enum NextRevisionIdResult
        {
            Break,
            Continue,
            Found
        }

        private NextRevisionIdResult ShouldProcessNextRevisionId(DocumentsOperationContext context, ref TableValueReader reader, Parameters parameters, OperationResult result, out LazyStringValue id)
        {
            result.ScannedRevisions++;

            id = TableValueToId(context, (int)RevisionsTable.Id, ref reader);
            var etag = TableValueToEtag((int)RevisionsTable.Etag, ref reader);
            parameters.LastScannedEtag = etag;

            if (parameters.ScannedIds.Add(id) == false)
                return NextRevisionIdResult.Continue;

            result.ScannedDocuments++;

            if (etag > parameters.EtagBarrier)
            {
                result.Warn(id, "This document wouldn't be processed, because it changed after the process started.");
                return NextRevisionIdResult.Continue;
            }

            if (_documentsStorage.ConflictsStorage.HasConflictsFor(context, id))
            {
                result.Warn(id, "The document is conflicted and wouldn't be processed.");
                return NextRevisionIdResult.Continue;
            }

            var date = TableValueToDateTime((int)RevisionsTable.LastModified, ref reader);
            if (date < parameters.MinimalDate)
                return NextRevisionIdResult.Break;

            if (result.ScannedDocuments % 1024 == 0)
                parameters.OnProgress?.Invoke(result);

            return NextRevisionIdResult.Found;
        }

        private void RestoreRevision(DocumentsOperationContext readCtx,
            DocumentsOperationContext writeCtx,
            Parameters parameters,
            LazyStringValue id,
            RevertResult result,
            List<Document> list)
        {
            var revision = GetRevisionBefore(readCtx, parameters, id, result);
            if (revision == null)
                return;

            result.RevertedDocuments++;

            revision.Data = revision.Flags.Contain(DocumentFlags.DeleteRevision) ? null : revision.Data?.Clone(writeCtx);
            revision.LowerId = writeCtx.GetLazyString(revision.LowerId);
            revision.Id = writeCtx.GetLazyString(revision.Id);

            list.Add(revision);
        }

        internal class RevertDocumentsCommand : TransactionOperationsMerger.MergedTransactionCommand
        {
            private readonly List<Document> _list;
            private readonly CancellationToken _token;

            public RevertDocumentsCommand(List<Document> list, OperationCancelToken token)
            {
                _list = list;
                _token = token.Token;
            }

            protected override long ExecuteCmd(DocumentsOperationContext context)
            {
                var documentsStorage = context.DocumentDatabase.DocumentsStorage;
                foreach (var document in _list)
                {
                    _token.ThrowIfCancellationRequested();

                    if (document.Data != null)
                    {
                        CollectionName collectionName = RemoveOldMetadataInfo(context, documentsStorage, document);
                        InsertNewMetadataInfo(context, documentsStorage, document, collectionName);

                        var flag = document.Flags | DocumentFlags.Reverted;
                        documentsStorage.Put(context, document.Id, null, document.Data, flags: flag.Strip(DocumentFlags.Revision | DocumentFlags.Conflicted | DocumentFlags.Resolved));
                    }
                    else
                    {
                        using (DocumentIdWorker.GetSliceFromId(context, document.Id, out Slice lowerId))
                        {
                            var etag = documentsStorage.GenerateNextEtag();
                            var changeVector = documentsStorage.ConflictsStorage.GetMergedConflictChangeVectorsAndDeleteConflicts(context, lowerId, etag);
                            documentsStorage.Delete(context, lowerId, document.Id, null, changeVector: changeVector, documentFlags: DocumentFlags.Reverted);
                        }
                    }
                }

                return _list.Count;
            }

            private static void InsertNewMetadataInfo(DocumentsOperationContext context, DocumentsStorage documentsStorage, Document document, CollectionName collectionName)
            {
                documentsStorage.AttachmentsStorage.PutAttachmentRevert(context, document, out bool has);
                RevertCounters(context, documentsStorage, document, collectionName);

                document.Data = RevertSnapshotFlags(context, document.Data, document.Id);
            }

            private static void RevertCounters(DocumentsOperationContext context, DocumentsStorage documentsStorage, Document document, CollectionName collectionName)
            {
                if (document.TryGetMetadata(out BlittableJsonReaderObject metadata) &&
                    metadata.TryGet(Constants.Documents.Metadata.RevisionCounters, out BlittableJsonReaderObject counters))
                {
                    var counterNames = counters.GetPropertyNames();

                    foreach (var cn in counterNames)
                    {
                        var val = counters.TryGetMember(cn, out object value);
                        documentsStorage.CountersStorage.PutCounter(context, document.Id, collectionName.Name, cn, (long)value);
                    }
                }
            }

            private static CollectionName RemoveOldMetadataInfo(DocumentsOperationContext context, DocumentsStorage documentsStorage, Document document)
            {
                documentsStorage.AttachmentsStorage.DeleteAttachmentBeforeRevert(context, document.LowerId);
                var collectionName = documentsStorage.ExtractCollectionName(context, document.Data);
                documentsStorage.CountersStorage.DeleteCountersForDocument(context, document.Id, collectionName);

                return collectionName;
            }

            public override TransactionOperationsMerger.IReplayableCommandDto<TransactionOperationsMerger.MergedTransactionCommand> ToDto<TTransaction>(TransactionOperationContext<TTransaction> context)
            {
                return new RevertDocumentsCommandDto(_list);
            }
        }

        internal class RevertDocumentsCommandDto : TransactionOperationsMerger.IReplayableCommandDto<RevertDocumentsCommand>
        {
            public readonly List<Document> List;

            public RevertDocumentsCommandDto(List<Document> list)
            {
                List = list;
            }

            public RevertDocumentsCommand ToCommand(DocumentsOperationContext context, DocumentDatabase database)
            {
                return new RevertDocumentsCommand(List, OperationCancelToken.None);
            }
        }

        public long GetRevisionsCount(DocumentsOperationContext context, string id)
        {
            using (DocumentIdWorker.GetSliceFromId(context, id, out Slice lowerId))
            using (GetKeyPrefix(context, lowerId, out Slice prefixSlice))
            {
                var count = CountOfRevisions(context, prefixSlice);
                return count;
            }
        }

        public (Document[] Revisions, long Count) GetRevisions(DocumentsOperationContext context, string id, long start, long take)
        {
            using (DocumentIdWorker.GetSliceFromId(context, id, out Slice lowerId))
            using (GetKeyPrefix(context, lowerId, out Slice prefixSlice))
            using (GetLastKey(context, lowerId, out Slice lastKey))
            {
                var revisions = GetRevisions(context, prefixSlice, lastKey, start, take).ToArray();
                var count = CountOfRevisions(context, prefixSlice);
                return (revisions, count);
            }
        }

        private IEnumerable<Document> GetRevisions(DocumentsOperationContext context, Slice prefixSlice, Slice lastKey, long start, long take)
        {
            var table = new Table(RevisionsSchema, context.Transaction.InnerTransaction);
            foreach (var tvr in table.SeekBackwardFrom(RevisionsSchema.Indexes[IdAndEtagSlice], prefixSlice, lastKey, start))
            {
                if (take-- <= 0)
                    yield break;

                var document = TableValueToRevision(context, ref tvr.Result.Reader);
                yield return document;
            }
        }

        public void GetLatestRevisionsBinEntryEtag(DocumentsOperationContext context, long startEtag, out string latestChangeVector)
        {
            latestChangeVector = null;
            foreach (var entry in GetRevisionsBinEntries(context, startEtag, 1))
            {
                latestChangeVector = entry.ChangeVector;
            }
        }

        public IEnumerable<Document> GetRevisionsBinEntries(DocumentsOperationContext context, long startEtag, long take)
        {
            var table = new Table(RevisionsSchema, context.Transaction.InnerTransaction);
            using (GetEtagAsSlice(context, startEtag, out var slice))
            {
                foreach (var tvr in table.SeekBackwardFrom(RevisionsSchema.Indexes[DeleteRevisionEtagSlice], slice))
                {
                    if (take-- <= 0)
                        yield break;

                    var etag = TableValueToEtag((int)RevisionsTable.DeletedEtag, ref tvr.Result.Reader);
                    if (etag == NotDeletedRevisionMarker)
                        yield break;

                    using (TableValueToSlice(context, (int)RevisionsTable.LowerId, ref tvr.Result.Reader, out Slice lowerId))
                    {
                        if (IsRevisionsBinEntry(context, table, lowerId, etag) == false)
                            continue;
                    }

                    yield return TableValueToRevision(context, ref tvr.Result.Reader);
                }
            }
        }

        private bool IsRevisionsBinEntry(DocumentsOperationContext context, Table table, Slice lowerId, long revisionsBinEntryEtag)
        {
            using (GetKeyPrefix(context, lowerId, out Slice prefixSlice))
            using (GetLastKey(context, lowerId, out Slice lastKey))
            {
                var tvr = table.SeekOneBackwardFrom(RevisionsSchema.Indexes[IdAndEtagSlice], prefixSlice, lastKey);
                if (tvr == null)
                {
                    Debug.Assert(false, "Cannot happen.");
                    return true;
                }

                var etag = TableValueToEtag((int)RevisionsTable.Etag, ref tvr.Reader);
                var flags = TableValueToFlags((int)RevisionsTable.Flags, ref tvr.Reader);
                Debug.Assert(revisionsBinEntryEtag <= etag, $"Revisions bin entry for '{lowerId}' etag candidate ({etag}) cannot meet a bigger etag ({revisionsBinEntryEtag}).");
                return (flags & DocumentFlags.DeleteRevision) == DocumentFlags.DeleteRevision && revisionsBinEntryEtag >= etag;
            }
        }

        public Document GetRevision(DocumentsOperationContext context, string changeVector)
        {
            var table = new Table(RevisionsSchema, context.Transaction.InnerTransaction);

            using (Slice.From(context.Allocator, changeVector, out var cv))
            {
                if (table.ReadByKey(cv, out TableValueReader tvr) == false)
                    return null;
                return TableValueToRevision(context, ref tvr);
            }
        }

        public IEnumerable<Document> GetRevisionsFrom(DocumentsOperationContext context, long etag, long take)
        {
            var table = new Table(RevisionsSchema, context.Transaction.InnerTransaction);

            foreach (var tvr in table.SeekForwardFrom(RevisionsSchema.FixedSizeIndexes[AllRevisionsEtagsSlice], etag, 0))
            {
                if (take-- <= 0)
                    yield break;

                var document = TableValueToRevision(context, ref tvr.Reader);
                yield return document;
            }
        }

        public IEnumerable<Document> GetRevisionsFrom(DocumentsOperationContext context, string collection, long etag, long take)
        {
            var collectionName = _documentsStorage.GetCollection(collection, throwIfDoesNotExist: false);
            if (collectionName == null)
                yield break;

            var tableName = collectionName.GetTableName(CollectionTableType.Revisions);
            var table = context.Transaction.InnerTransaction.OpenTable(RevisionsSchema, tableName);
            if (table == null)
                yield break;

            foreach (var tvr in table.SeekForwardFrom(RevisionsSchema.FixedSizeIndexes[CollectionRevisionsEtagsSlice], etag, 0))
            {
                if (take-- <= 0)
                    yield break;

                var document = TableValueToRevision(context, ref tvr.Reader);
                yield return document;
            }
        }

        public long GetLastRevisionEtag(DocumentsOperationContext context, string collection)
        {
            Table.TableValueHolder result = null;
            if (LastRevision(context, collection, ref result) == false)
                return 0;

            return TableValueToEtag((int)RevisionsTable.Etag, ref result.Reader);
        }

        private bool LastRevision(DocumentsOperationContext context, string collection, ref Table.TableValueHolder result)
        {
            var collectionName = _documentsStorage.GetCollection(collection, throwIfDoesNotExist: false);
            if (collectionName == null)
                return false;

            var tableName = collectionName.GetTableName(CollectionTableType.Revisions);
            var table = context.Transaction.InnerTransaction.OpenTable(RevisionsSchema, tableName);
            // ReSharper disable once UseNullPropagation
            if (table == null)
                return false;

            result = table.ReadLast(RevisionsSchema.FixedSizeIndexes[CollectionRevisionsEtagsSlice]);
            if (result == null)
                return false;

            return true;
        }

        public IEnumerable<(Document Previous, Document Current)> GetCurrentAndPreviousRevisionsForSubscriptionsFrom(
            DocumentsOperationContext context,
            long etag,
            long start,
            long take)
        {
            var table = new Table(RevisionsSchema, context.Transaction.InnerTransaction);

            var iterator = table.SeekForwardFrom(RevisionsSchema.FixedSizeIndexes[AllRevisionsEtagsSlice], etag, start);

            return GetCurrentAndPreviousRevisionsFrom(context, iterator, table, take);
        }

        public IEnumerable<(Document Previous, Document Current)> GetCurrentAndPreviousRevisionsForSubscriptionsFrom(
            DocumentsOperationContext context,
            CollectionName collectionName,
            long etag,
            long take)
        {
            var tableName = collectionName.GetTableName(CollectionTableType.Revisions);
            var table = context.Transaction.InnerTransaction.OpenTable(RevisionsSchema, tableName);

            var iterator = table?.SeekForwardFrom(RevisionsSchema.FixedSizeIndexes[CollectionRevisionsEtagsSlice], etag, 0);

            return GetCurrentAndPreviousRevisionsFrom(context, iterator, table, take);
        }

        private static IEnumerable<(Document Previous, Document Current)> GetCurrentAndPreviousRevisionsFrom(
            DocumentsOperationContext context,
            IEnumerable<Table.TableValueHolder> iterator,
            Table table,
            long take)
        {
            if (table == null)
                yield break;

            if (iterator == null)
                yield break;

            var docsSchemaIndex = RevisionsSchema.Indexes[IdAndEtagSlice];

            foreach (var tvr in iterator)
            {
                if (take-- <= 0)
                    break;
                var current = TableValueToRevision(context, ref tvr.Reader);

                using (docsSchemaIndex.GetSlice(context.Allocator, ref tvr.Reader, out var idAndEtag))
                using (Slice.External(context.Allocator, idAndEtag, idAndEtag.Size - sizeof(long), out var prefix))
                {
                    bool hasPrevious = false;
                    foreach (var prevTvr in table.SeekBackwardFrom(docsSchemaIndex, prefix, idAndEtag, 1))
                    {
                        var previous = TableValueToRevision(context, ref prevTvr.Result.Reader);

                        yield return (previous, current);
                        hasPrevious = true;
                        break;
                    }
                    if (hasPrevious)
                        continue;
                }

                yield return (null, current);
            }
        }

        private static unsafe Document TableValueToRevision(JsonOperationContext context, ref TableValueReader tvr)
        {
            var result = new Document
            {
                StorageId = tvr.Id,
                LowerId = TableValueToString(context, (int)RevisionsTable.LowerId, ref tvr),
                Id = TableValueToId(context, (int)RevisionsTable.Id, ref tvr),
                Etag = TableValueToEtag((int)RevisionsTable.Etag, ref tvr),
                LastModified = TableValueToDateTime((int)RevisionsTable.LastModified, ref tvr),
                Flags = TableValueToFlags((int)RevisionsTable.Flags, ref tvr),
                TransactionMarker = *(short*)tvr.Read((int)RevisionsTable.TransactionMarker, out int size),
                ChangeVector = TableValueToChangeVector(context, (int)RevisionsTable.ChangeVector, ref tvr),
                Data = TableValueToData(context, ref tvr)
            };

            return result;
        }

        private static unsafe BlittableJsonReaderObject TableValueToData(JsonOperationContext context, ref TableValueReader tvr)
        {
            var ptr = tvr.Read((int)RevisionsTable.Document, out var size);
            return new BlittableJsonReaderObject(ptr, size, context);
        }

        public static unsafe Document ParseRawDataSectionRevisionWithValidation(JsonOperationContext context, ref TableValueReader tvr, int expectedSize, out long etag)
        {
            var ptr = tvr.Read((int)RevisionsTable.Document, out var size);
            if (size > expectedSize || size <= 0)
                throw new ArgumentException("Data size is invalid, possible corruption when parsing BlittableJsonReaderObject", nameof(size));

            var result = new Document
            {
                StorageId = tvr.Id,
                LowerId = TableValueToString(context, (int)RevisionsTable.LowerId, ref tvr),
                Id = TableValueToId(context, (int)RevisionsTable.Id, ref tvr),
                Etag = etag = TableValueToEtag((int)RevisionsTable.Etag, ref tvr),
                Data = new BlittableJsonReaderObject(ptr, size, context),
                LastModified = TableValueToDateTime((int)RevisionsTable.LastModified, ref tvr),
                Flags = TableValueToFlags((int)RevisionsTable.Flags, ref tvr),
                TransactionMarker = *(short*)tvr.Read((int)RevisionsTable.TransactionMarker, out size),
                ChangeVector = TableValueToChangeVector(context, (int)RevisionsTable.ChangeVector, ref tvr)
            };

            if (size != sizeof(short))
                throw new ArgumentException("TransactionMarker size is invalid, possible corruption when parsing BlittableJsonReaderObject", nameof(size));

            return result;
        }

        private unsafe ByteStringContext.ExternalScope GetResolvedSlice(DocumentsOperationContext context, DateTime date, out Slice slice)
        {
            var size = sizeof(int) + sizeof(long);
            var mem = context.GetMemory(size);
            var flag = (int)DocumentFlags.Resolved;
            Memory.Copy(mem.Address, (byte*)&flag, sizeof(int));
            var ticks = Bits.SwapBytes(date.Ticks);
            Memory.Copy(mem.Address + sizeof(int), (byte*)&ticks, sizeof(long));
            return Slice.External(context.Allocator, mem.Address, size, out slice);
        }

        public IEnumerable<Document> GetResolvedDocumentsSince(DocumentsOperationContext context, DateTime since, long take = 1024)
        {
            var table = new Table(RevisionsSchema, context.Transaction.InnerTransaction);
            using (GetResolvedSlice(context, since, out var slice))
            {
                foreach (var item in table.SeekForwardFrom(RevisionsSchema.Indexes[ResolvedFlagByEtagSlice], slice, 0))
                {
                    if (take == 0)
                    {
                        yield break;
                    }
                    take--;
                    yield return TableValueToRevision(context, ref item.Result.Reader);
                }
            }
        }

        public long GetNumberOfRevisionDocuments(DocumentsOperationContext context)
        {
            var table = new Table(RevisionsSchema, context.Transaction.InnerTransaction);
            return table.GetNumberOfEntriesFor(RevisionsSchema.FixedSizeIndexes[AllRevisionsEtagsSlice]);
        }
    }
}
