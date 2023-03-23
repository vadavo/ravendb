﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Documents.Operations.Attachments;
using Raven.Client.Json.Serialization;
using Raven.Client.ServerWide;
using Raven.Client.Util;
using Raven.Server.Documents.Patch;
using Raven.Server.NotificationCenter.Notifications;
using Raven.Server.NotificationCenter.Notifications.Details;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Raven.Server.Smuggler.Documents;
using Raven.Server.Utils;
using Sparrow.Json;
using Sparrow.Logging;
using Sparrow.Utils;
using Voron;

namespace Raven.Server.Documents.Replication
{
    public class ResolveConflictOnReplicationConfigurationChange
    {
        private readonly DocumentDatabase _database;
        private readonly Logger _log;
        private readonly ReplicationLoader _replicationLoader;

        internal Dictionary<string, ScriptResolver> ScriptConflictResolversCache = new Dictionary<string, ScriptResolver>();

        public ResolveConflictOnReplicationConfigurationChange(ReplicationLoader replicationLoader, Logger log)
        {
            _replicationLoader = replicationLoader ??
                throw new ArgumentNullException($"{nameof(ResolveConflictOnReplicationConfigurationChange)} must have replicationLoader instance");
            _database = _replicationLoader.Database;
            _log = log;
        }

        public long ConflictsCount => _database.DocumentsStorage?.ConflictsStorage?.ConflictsCount ?? 0;

        private long _processedRaftIndex;

        private readonly SemaphoreSlim _runOnce = new SemaphoreSlim(1, 1);

        private async Task<IDisposable> RunOnceAsync()
        {
            await _runOnce.WaitAsync(_database.DatabaseShutdown);

            return new DisposableAction(() => _runOnce.Release());
        }

        public void WaitForBackgroundResolveTask()
        {
            if (_runOnce.Wait(TimeSpan.FromSeconds(60)) == false)
            {
                if (_log.IsInfoEnabled)
                {
                    _log.Info("Waited for 60 seconds to close 'ResolveConflictOnReplicationConfigurationChange' gracefully, will dispose it anyway.");
                }
            }
            _runOnce.Dispose();
        }

        public async Task RunConflictResolversOnce(ConflictSolver solver, long index)
        {
            // update to larger index;
            if (ThreadingHelper.InterlockedExchangeMax(ref _processedRaftIndex, index) == false)
                return;
            
            try
            {
                using (await RunOnceAsync())
                {
                    if (Interlocked.Read(ref _processedRaftIndex) > index)
                        return;

                    UpdateScriptResolvers(solver);

                    if (ConflictsCount > 0 && solver?.IsEmpty() == false)
                    {
                        await ResolveConflictsInBackground(solver);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
            catch (ObjectDisposedException)
            {
                // shutdown
            }
            catch (Exception e)
            {
                if (_log.IsInfoEnabled)
                    _log.Info("Failed to wait for a previous task of automatic conflict resolution", e);
            }
        }

        private async Task ResolveConflictsInBackground(ConflictSolver solver)
        {
            try
            {
                bool retry;
                do
                {
                    retry = false;
                    using (_database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
                    using (context.OpenReadTransaction())
                    {
                        var resolvedConflicts = new List<(DocumentConflict ResolvedConflict, long MaxConflictEtag, bool ResolvedToLatest)>();

                        var hadConflicts = false;

                        foreach (var conflicts in _database.DocumentsStorage.ConflictsStorage.GetAllConflictsBySameId(context))
                        {
                            if (_database.DatabaseShutdown.IsCancellationRequested)
                                break;

                            hadConflicts = true;

                            var collection = conflicts[0].Collection;

                            var maxConflictEtag = conflicts.Max(x => x.Etag);

                            DocumentConflict resolved;
                            if (ScriptConflictResolversCache.TryGetValue(collection, out var scriptResolver) && scriptResolver != null)
                            {
                                if (TryResolveConflictByScriptInternal(
                                    context,
                                    scriptResolver,
                                    conflicts,
                                    collection,
                                    resolvedConflict: out resolved))
                                {
                                    resolved.Flags = resolved.Flags.Strip(DocumentFlags.FromReplication);
                                    resolvedConflicts.Add((resolved, maxConflictEtag, ResolvedToLatest: false));

                                    //stats.AddResolvedBy(collection + " Script", conflictList.Count);
                                    continue;
                                }
                            }

                            if (solver?.ResolveToLatest == true)
                            {
                                resolved = ResolveToLatest(conflicts);
                                resolved.Flags = resolved.Flags.Strip(DocumentFlags.FromReplication);
                                resolvedConflicts.Add((resolved, maxConflictEtag, ResolvedToLatest: true));

                                //stats.AddResolvedBy("ResolveToLatest", conflictList.Count);
                            }
                        }

                        if (hadConflicts == false || _database.DatabaseShutdown.IsCancellationRequested)
                            return;

                        if (resolvedConflicts.Count > 0)
                        {
                            var cmd = new PutResolvedConflictsCommand(_database.DocumentsStorage.ConflictsStorage, resolvedConflicts, this);
                            await _database.TxMerger.Enqueue(cmd);
                            retry = cmd.RequiresRetry;
                        }
                    }
                } while (retry);
            }
            catch (Exception e)
            {
                if (_log.IsInfoEnabled)
                    _log.Info("Failed to run automatic conflict resolution", e);
            }
        }

        internal class PutResolvedConflictsCommand : TransactionOperationsMerger.MergedTransactionCommand
        {
            private readonly ConflictsStorage _conflictsStorage;
            private readonly List<(DocumentConflict ResolvedConflict, long MaxConflictEtag, bool resovedToLatest)> _resolvedConflicts;
            private readonly ResolveConflictOnReplicationConfigurationChange _resolver;
            public bool RequiresRetry;

            public PutResolvedConflictsCommand(ConflictsStorage conflictsStorage, List<(DocumentConflict, long, bool)> resolvedConflicts, ResolveConflictOnReplicationConfigurationChange resolver)
            {
                _conflictsStorage = conflictsStorage;
                _resolvedConflicts = resolvedConflicts;
                _resolver = resolver;
            }

            protected override long ExecuteCmd(DocumentsOperationContext context)
            {
                var count = 0;

                foreach (var item in _resolvedConflicts)
                {
                    count++;

                    using (Slice.External(context.Allocator, item.ResolvedConflict.LowerId, out var lowerId))
                    {
                        // let's check if nothing has changed since we resolved the conflict in the read tx
                        // in particular the conflict could be resolved externally before the tx merger opened this write tx

                        if (_conflictsStorage.ShouldThrowConcurrencyExceptionOnConflict(context, lowerId, item.MaxConflictEtag, out _))
                            continue;
                        RequiresRetry = true;
                    }

                    _resolver.PutResolvedDocument(context, item.ResolvedConflict, item.resovedToLatest);
                }

                return count;
            }

            public override TransactionOperationsMerger.IReplayableCommandDto<TransactionOperationsMerger.MergedTransactionCommand> ToDto<TTransaction>(TransactionOperationContext<TTransaction> context)
            {
                // The LowerId created as in memory LazyStringValue that doesn't have escape characters
                // so EscapePositions set to empty to avoid reference to escape bytes (after string bytes) while serializing
                foreach (var conflict in _resolvedConflicts)
                {
                    conflict.ResolvedConflict.LowerId.EscapePositions = Array.Empty<int>();
                }

                return new PutResolvedConflictsCommandDto
                {
                    ResolvedConflicts = _resolvedConflicts,
                };
            }
        }

        private void UpdateScriptResolvers(ConflictSolver conflictSolver)
        {
            if (conflictSolver?.ResolveByCollection == null)
            {
                if (ScriptConflictResolversCache.Count > 0)
                    ScriptConflictResolversCache = new Dictionary<string, ScriptResolver>();
                return;
            }
            var copy = new Dictionary<string, ScriptResolver>();
            foreach (var kvp in conflictSolver.ResolveByCollection)
            {
                var collection = kvp.Key;
                var script = kvp.Value.Script;
                if (string.IsNullOrEmpty(script.Trim()))
                {
                    continue;
                }
                copy[collection] = new ScriptResolver
                {
                    Script = script
                };
            }
            ScriptConflictResolversCache = copy;
        }

        private bool ValidatedResolveByScriptInput(ScriptResolver scriptResolver,
            IReadOnlyList<DocumentConflict> conflicts,
            LazyStringValue collection)
        {
            if (scriptResolver == null)
                return false;
            if (collection == null)
                return false;
            if (conflicts.Count < 2)
                return false;

            foreach (var documentConflict in conflicts)
            {
                if (collection != documentConflict.Collection)
                {
                    var msg = $"All conflicted documents must have same collection name, but we found conflicted document in {collection} and an other one in {documentConflict.Collection}";
                    if (_log.IsInfoEnabled)
                        _log.Info(msg);

                    var differentCollectionNameAlert = AlertRaised.Create(
                        _database.Name,
                        $"Script unable to resolve conflicted documents with the ID {documentConflict.Id}",
                        msg,
                        AlertType.Replication,
                        NotificationSeverity.Error,
                        "Mismatched Collections On Replication Resolve"
                        );
                    _database.NotificationCenter.Add(differentCollectionNameAlert);
                    return false;
                }
            }

            return true;
        }

        public void PutResolvedDocument(
           DocumentsOperationContext context,
           DocumentConflict resolved,
           bool resolvedToLatest,
           DocumentConflict incoming = null)
        {
            resolved.Flags = resolved.Flags.Strip(DocumentFlags.FromClusterTransaction);

            SaveConflictedDocumentsAsRevisions(context, resolved.Id, incoming);

            // Resolved document should generate a new change vector, since it was changed locally.
            // In a cluster this may cause a ping-pong replication which will be settled down by the fact that a conflict with identical content doesn't increase the local etag
            var changeVector = _database.DocumentsStorage.CreateNextDatabaseChangeVector(context, resolved.ChangeVector);

            if (resolved.Doc == null)
            {
                // we have to call Slice.From to ensure lowerId will get its own memory
                // resolved parameter was created from a tombstone that might be deleted during below Delete()
                // that can invalidate and override memory occupied by resolved.LowerId
                using (Slice.From(context.Allocator, resolved.LowerId, out var lowerId))
                {
                    _database.DocumentsStorage.Delete(context, lowerId, resolved.Id, null, null, changeVector, new CollectionName(resolved.Collection),
                        documentFlags: resolved.Flags | DocumentFlags.Resolved | DocumentFlags.HasRevisions, nonPersistentFlags: NonPersistentDocumentFlags.FromResolver | NonPersistentDocumentFlags.Resolved);
                    return;
                }
            }

            ResolveAttachmentsConflicts(context, resolved, resolvedToLatest);

            if (ReferenceEquals(incoming?.Doc, resolved.Doc))
            {
                // when saving the incoming document as revision, we might have made modifications to the document.
                // because incoming == resolved we need to remove these modifications before calling resolved.Clone()
                resolved.Doc.Modifications = null;
            }

            // because we are resolving to a conflict, and putting a document will
            // delete all the conflicts, we have to create a copy of the document
            // in order to avoid the data we are saving from being removed while
            // we are saving it

            // the resolved document could be an update of the existing document, so it's a good idea to clone it also before updating.
            using (var clone = resolved.Doc.Clone(context))
            {
                // handle the case where we resolve a conflict for a document from a different collection
                DeleteDocumentFromDifferentCollectionIfNeeded(context, resolved);

                ReplicationUtils.EnsureCollectionTag(clone, resolved.Collection);
                // we always want to merge the counters and attachments, even if the user specified a script
                var nonPersistentFlags = NonPersistentDocumentFlags.ResolveCountersConflict | NonPersistentDocumentFlags.ResolveAttachmentsConflict | NonPersistentDocumentFlags.ResolveTimeSeriesConflict |
                                         NonPersistentDocumentFlags.FromResolver | NonPersistentDocumentFlags.Resolved;
                _database.DocumentsStorage.Put(context, resolved.Id, null, clone, null, changeVector, null, resolved.Flags | DocumentFlags.Resolved, nonPersistentFlags: nonPersistentFlags);
            }
        }

        private void SaveConflictedDocumentsAsRevisions(DocumentsOperationContext context, string id, DocumentConflict incoming)
        {
            if (incoming == null)
                return;

            // we resolved the conflict on the fly, so we save the remote document as revisions
            if (incoming.Doc != null)
            {
                _database.DocumentsStorage.RevisionsStorage.Put(context, incoming.Id, incoming.Doc,
                    incoming.Flags.Strip(DocumentFlags.FromClusterTransaction) | DocumentFlags.Conflicted | DocumentFlags.HasRevisions,
                    NonPersistentDocumentFlags.FromResolver, incoming.ChangeVector, incoming.LastModified.Ticks);
            }
            else
            {
                using (Slice.External(context.Allocator, incoming.LowerId, out var lowerId))
                {
                    _database.DocumentsStorage.RevisionsStorage.Delete(context, incoming.Id, lowerId, new CollectionName(incoming.Collection), incoming.ChangeVector,
                        incoming.LastModified.Ticks, NonPersistentDocumentFlags.FromResolver, incoming.Flags | DocumentFlags.Conflicted | DocumentFlags.HasRevisions);
                }
            }

            if (_database.DocumentsStorage.ConflictsStorage.ConflictsCount != 0) // we have conflicts and we will resolve them in the put method, rest of the function is when we resolve on the fly
                return;

            SaveLocalAsRevision(context, id);
        }

        public void SaveLocalAsRevision(DocumentsOperationContext context, string id)
        {
            var existing = _database.DocumentsStorage.GetDocumentOrTombstone(context, id, throwOnConflict: false);
            if (existing.Document != null)
            {
                _database.DocumentsStorage.RevisionsStorage.Put(context, existing.Document.Id, existing.Document.Data,
                    existing.Document.Flags | DocumentFlags.Conflicted | DocumentFlags.HasRevisions,
                    NonPersistentDocumentFlags.FromResolver, existing.Document.ChangeVector, existing.Document.LastModified.Ticks);
            }
            else if (existing.Tombstone != null)
            {
                using (Slice.External(context.Allocator, existing.Tombstone.LowerId, out var key))
                {
                    _database.DocumentsStorage.RevisionsStorage.Delete(context, existing.Tombstone.LowerId, key, new CollectionName(existing.Tombstone.Collection),
                        existing.Tombstone.ChangeVector,
                        existing.Tombstone.LastModified.Ticks, NonPersistentDocumentFlags.FromResolver,
                        existing.Tombstone.Flags | DocumentFlags.Conflicted | DocumentFlags.HasRevisions);
                }
            }
        }

        private void DeleteDocumentFromDifferentCollectionIfNeeded(DocumentsOperationContext ctx, DocumentConflict conflict)
        {
            // if already conflicted, don't need to do anything
            var oldVersion = _database.DocumentsStorage.Get(ctx, conflict.LowerId, throwOnConflict: false);

            if (oldVersion == null)
                return;

            var oldVersionCollectionName = CollectionName.GetCollectionName(oldVersion.Data);
            if (oldVersionCollectionName.Equals(conflict.Collection, StringComparison.OrdinalIgnoreCase))
                return;

            _database.DocumentsStorage.DeleteWithoutCreatingTombstone(ctx, oldVersionCollectionName, oldVersion.StorageId, isTombstone: false);
        }

        public bool TryResolveConflictByScriptInternal(
            DocumentsOperationContext context,
            ScriptResolver scriptResolver,
            IReadOnlyList<DocumentConflict> conflicts,
            LazyStringValue collection,
            out DocumentConflict resolvedConflict)
        {
            resolvedConflict = null;
            DocumentConflict updatedConflict = null;
            try
            {
                if (ValidatedResolveByScriptInput(scriptResolver, conflicts, collection) == false)
                    return false;

                var patch = new PatchConflict(_database, conflicts);
                updatedConflict = conflicts[0];
                var patchRequest = new PatchRequest(scriptResolver.Script, PatchRequestType.Conflict);
                if (patch.TryResolveConflict(context, patchRequest, out BlittableJsonReaderObject resolved) == false)
                {
                    return false;
                }

                updatedConflict.Doc = resolved;
                updatedConflict.Collection = collection;
                updatedConflict.ChangeVector = ChangeVectorUtils.MergeVectors(conflicts.Select(c => c.ChangeVector).ToList());
                resolvedConflict = updatedConflict;

                return true;
            }
            catch (Exception e)
            {
                var msg = $"Script failed to resolve the conflict in doc: {updatedConflict?.Id} because exception was raised in it.";
                if (_log.IsInfoEnabled)
                    _log.Info(msg, e);
                
                var alert = AlertRaised.Create(
                    _database.Name,
                    "User-provided conflict script raised an error during conflict resolution, manual intervention required!",
                    msg,
                    AlertType.Replication,
                    NotificationSeverity.Error,
                    details: new ExceptionDetails(e));
                
                _database.NotificationCenter.Add(alert);
            }
            return false;
        }

        public DocumentConflict ResolveToLatest(List<DocumentConflict> conflicts)
        {
            // we have to sort this here because we need to ensure that all the nodes are always
            // arrive to the same conclusion, regardless of what time they go it
            conflicts.Sort((x, y) => string.Compare(x.ChangeVector, y.ChangeVector, StringComparison.Ordinal));

            var latestDoc = conflicts[0];
            var latestTime = latestDoc.LastModified.Ticks;

            foreach (var conflict in conflicts)
            {
                if (conflict.LastModified.Ticks > latestTime)
                {
                    latestDoc = conflict;
                    latestTime = conflict.LastModified.Ticks;
                }
            }

            latestDoc.ChangeVector = ChangeVectorUtils.MergeVectors(conflicts.Select(c => c.ChangeVector).ToList());

            return latestDoc;
        }

        private void ResolveAttachmentsConflicts(DocumentsOperationContext context, DocumentConflict resolved, bool resolvedToLatest)
        {
            using var scope = Slice.External(context.Allocator, resolved.LowerId, out var lowerId);
            var storageAttachmentsDetails = _database.DocumentsStorage.AttachmentsStorage.GetAttachmentDetailsForDocument(context, lowerId);
            List<AttachmentName> resolvedAttachmentsMetadata = null;

            foreach (var group in storageAttachmentsDetails.GroupBy(x => x.Name))
            {
                if (group.Count() == 1)
                    continue;

                resolvedAttachmentsMetadata ??= AttachmentsStorage.GetAttachmentsFromDocumentMetadata(resolved.Doc).Select(attachment => JsonDeserializationClient.AttachmentName(attachment)).ToList();
                var found = false;

                foreach (var attachment in group)
                {
                    if (found == false && resolvedAttachmentsMetadata.Any(x => x.Name == attachment.Name && x.Hash == attachment.Hash && x.ContentType == attachment.ContentType))
                    {
                        found = true;
                        continue;
                    }

                    if (resolvedToLatest)
                    {
                        // delete duplicates
                        _database.DocumentsStorage.AttachmentsStorage.DeleteAttachment(context, resolved.LowerId, attachment.Name, expectedChangeVector: null, updateDocument: false,
                            attachment.Hash, attachment.ContentType, usePartialKey: false);
                    }
                    else
                    {
                        if (found == false)
                        {
                            // keep one duplicate with original name
                            found = true;
                            continue;
                        }
                        // rename duplicates
                        var newName = _database.DocumentsStorage.AttachmentsStorage.ResolveAttachmentName(context, lowerId, attachment.Name);
                        _database.DocumentsStorage.AttachmentsStorage.MoveAttachment(context, resolved.LowerId, attachment.Name, resolved.LowerId, newName, changeVector: null,
                            attachment.Hash, attachment.ContentType, usePartialKey: false, updateDocument: false);
                    }
                }
            }
        }
    }

    internal class PutResolvedConflictsCommandDto : TransactionOperationsMerger.IReplayableCommandDto<ResolveConflictOnReplicationConfigurationChange.PutResolvedConflictsCommand>
    {
        public List<(DocumentConflict ResolvedConflict, long MaxConflictEtag, bool ResolvedToLatests)> ResolvedConflicts;

        public ResolveConflictOnReplicationConfigurationChange.PutResolvedConflictsCommand ToCommand(DocumentsOperationContext context, DocumentDatabase database)
        {
            var resolver = new ResolveConflictOnReplicationConfigurationChange(
                database.ReplicationLoader,
                LoggingSource.Instance.GetLogger<DatabaseDestination>(database.Name));

            return new ResolveConflictOnReplicationConfigurationChange.PutResolvedConflictsCommand(
                database.DocumentsStorage.ConflictsStorage,
                ResolvedConflicts,
                resolver);
        }
    }
}
