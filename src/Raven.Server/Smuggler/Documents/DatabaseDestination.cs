﻿using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Client.Documents.Attachments;
using Raven.Client.Documents.Commands.Batches;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Attachments;
using Raven.Client.Documents.Operations.Counters;
using Raven.Client.Documents.Operations.OngoingTasks;
using Raven.Client.Documents.Operations.Replication;
using Raven.Client.Documents.Smuggler;
using Raven.Client.Documents.Subscriptions;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Documents;
using Raven.Client.ServerWide;
using Raven.Client.Util;
using Raven.Server.Documents;
using Raven.Server.Documents.Handlers;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.PeriodicBackup;
using Raven.Server.Documents.TransactionCommands;
using Raven.Server.Integrations.PostgreSQL.Commands;
using Raven.Server.Routing;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Commands;
using Raven.Server.ServerWide.Commands.Analyzers;
using Raven.Server.ServerWide.Commands.ConnectionStrings;
using Raven.Server.ServerWide.Commands.ETL;
using Raven.Server.ServerWide.Commands.PeriodicBackup;
using Raven.Server.ServerWide.Commands.Sorters;
using Raven.Server.ServerWide.Commands.Subscriptions;
using Raven.Server.ServerWide.Context;
using Raven.Server.Smuggler.Documents.Data;
using Raven.Server.Smuggler.Documents.Processors;
using Raven.Server.Utils;
using Sparrow;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Sparrow.Logging;
using Sparrow.Platform;
using Sparrow.Server.Utils;
using Voron;
using Constants = Voron.Global.Constants;
using Size = Sparrow.Size;

namespace Raven.Server.Smuggler.Documents
{
    public class DatabaseDestination : ISmugglerDestination
    {
        private readonly DocumentDatabase _database;
        private readonly CancellationToken _token;
        internal DuplicateDocsHandler _duplicateDocsHandler;

        private readonly Logger _log;
        private BuildVersionType _buildType;
        private DatabaseSmugglerOptionsServerSide _options;

        public DatabaseDestination(DocumentDatabase database, CancellationToken token = default)
        {
            _database = database;
            _token = token;
            _log = LoggingSource.Instance.GetLogger<DatabaseDestination>(database.Name);
            _duplicateDocsHandler = new DuplicateDocsHandler(_database);
        }

        public IAsyncDisposable InitializeAsync(DatabaseSmugglerOptionsServerSide options, SmugglerResult result, long buildVersion)
        {
            _buildType = BuildVersion.Type(buildVersion);
            _options = options;

            return new AsyncDisposableAction(() =>
            {
                _duplicateDocsHandler.Dispose();
                return Task.CompletedTask;
            });
        }

        public IDatabaseRecordActions DatabaseRecord()
        {
            return new DatabaseRecordActions(_database, log: _log);
        }

        public IDocumentActions Documents(bool throwOnCollectionMismatchError = true)
        {
            return new DatabaseDocumentActions(_database, _buildType, _options, isRevision: false, _log, _duplicateDocsHandler, throwOnCollectionMismatchError);
        }

        public IDocumentActions RevisionDocuments()
        {
            return new DatabaseDocumentActions(_database, _buildType, _options, isRevision: true, _log, _duplicateDocsHandler, throwOnCollectionMismatchError: true);
        }

        public IDocumentActions Tombstones()
        {
            return new DatabaseDocumentActions(_database, _buildType, _options, isRevision: false, _log, _duplicateDocsHandler, throwOnCollectionMismatchError: true);
        }

        public IDocumentActions Conflicts()
        {
            return new DatabaseDocumentActions(_database, _buildType, _options, isRevision: false, _log, _duplicateDocsHandler, throwOnCollectionMismatchError: true);
        }

        public IKeyValueActions<long> Identities()
        {
            return new DatabaseKeyValueActions(_database);
        }

        public ICompareExchangeActions CompareExchange(JsonOperationContext context, BackupKind? backupKind, bool withDocuments)
        {
            if (withDocuments == false)
                return CreateActions();

            switch (backupKind)
            {
                case null:
                case BackupKind.None:
                    return null; // do not optimize for Import
                case BackupKind.Full:
                case BackupKind.Incremental:
                    return CreateActions();
                default:
                    throw new ArgumentOutOfRangeException(nameof(backupKind), backupKind, null);
            }

            DatabaseCompareExchangeActions CreateActions()
            {
                return new DatabaseCompareExchangeActions(_database, context, backupKind, _token);
            }
        }

        public ICompareExchangeActions CompareExchangeTombstones(JsonOperationContext context)
        {
            return new DatabaseCompareExchangeActions(_database, context, backupKind: null, _token);
        }

        public ICounterActions Counters(SmugglerResult result)
        {
            return new CounterActions(_database, result);
        }

        public ISubscriptionActions Subscriptions()
        {
            return new SubscriptionActions(_database);
        }

        public IReplicationHubCertificateActions ReplicationHubCertificates()
        {
            return new ReplicationHubCertificateActions(_database);
        }

        public ITimeSeriesActions TimeSeries()
        {
            return new TimeSeriesActions(_database);
        }

        public IIndexActions Indexes()
        {
            return new DatabaseIndexActions(_database);
        }

        private class DatabaseIndexActions : IIndexActions
        {
            private readonly DocumentDatabase _database;
            private readonly IndexStore.IndexBatchScope _batch;

            public DatabaseIndexActions(DocumentDatabase database)
            {
                _database = database;

                if (_database.IndexStore.CanUseIndexBatch())
                    _batch = _database.IndexStore.CreateIndexBatch();
            }

            public async ValueTask WriteIndexAsync(IndexDefinitionBaseServerSide indexDefinition, IndexType indexType)
            {
                if (_batch != null)
                {
                    _batch.AddIndex(indexDefinition, _source, _database.Time.GetUtcNow(), RaftIdGenerator.DontCareId, _database.Configuration.Indexing.HistoryRevisionsNumber);
                    AsyncHelpers.RunSync(_batch.SaveIfNeeded);
                    return;
                }

                await _database.IndexStore.CreateIndex(indexDefinition, RaftIdGenerator.DontCareId);
            }

            public async ValueTask WriteIndexAsync(IndexDefinition indexDefinition)
            {
                if (_batch != null)
                {
                    _batch.AddIndex(indexDefinition, _source, _database.Time.GetUtcNow(), RaftIdGenerator.DontCareId, _database.Configuration.Indexing.HistoryRevisionsNumber);
                    AsyncHelpers.RunSync(_batch.SaveIfNeeded);
                    return;
                }

                await _database.IndexStore.CreateIndex(indexDefinition, RaftIdGenerator.DontCareId, _source);
            }

            private const string _source = "Smuggler";

            public async ValueTask DisposeAsync()
            {
                if (_batch == null)
                    return;

                await _batch.SaveAsync();
            }
        }

        public class DuplicateDocsHandler : IDisposable
        {
            private readonly DocumentDatabase _database;
            private DocumentsOperationContext _context;
            private IDisposable _returnContext;

            public List<DocumentItem> DocumentsWithDuplicateCollection;
            internal bool _markForDispose;

            public DuplicateDocsHandler(DocumentDatabase database)
            {
                _database = database;
            }

            private void InitializeIfNeeded()
            {
                _returnContext ??= _database.DocumentsStorage.ContextPool.AllocateOperationContext(out _context);
                DocumentsWithDuplicateCollection ??= new List<DocumentItem>();
            }

            public void AddDocument(DocumentItem item)
            {
                InitializeIfNeeded();

                DocumentsWithDuplicateCollection.Add(new DocumentItem
                {
                    Document = item.Document.Clone(_context)
                });
            }

            public void Dispose()
            {
                _returnContext?.Dispose();
                _returnContext = null;
            }
        }

        public class DatabaseDocumentActions : IDocumentActions
        {
            private readonly DocumentDatabase _database;
            private readonly BuildVersionType _buildType;
            private readonly DatabaseSmugglerOptionsServerSide _options;
            private readonly bool _isRevision;
            private readonly Logger _log;
            private MergedBatchPutCommand _command;
            private MergedBatchPutCommand _prevCommand;
            private Task _prevCommandTask = Task.CompletedTask;

            private MergedBatchDeleteRevisionCommand _revisionDeleteCommand;
            private MergedBatchDeleteRevisionCommand _prevRevisionDeleteCommand;
            private Task _prevRevisionCommandTask = Task.CompletedTask;

            private MergedBatchFixDocumentMetadataCommand _fixDocumentMetadataCommand;
            private MergedBatchFixDocumentMetadataCommand _prevFixDocumentMetadataCommand;
            private Task _prevFixDocumentMetadataCommandTask = Task.CompletedTask;

            private readonly Sparrow.Size _enqueueThreshold;
            private readonly ConcurrentDictionary<string, CollectionName> _missingDocumentsForRevisions;
            private readonly HashSet<string> _documentIdsOfMissingAttachments;
            private readonly DuplicateDocsHandler _duplicateDocsHandler;
            private bool _throwOnCollectionMismatchError;

            public DatabaseDocumentActions(DocumentDatabase database, BuildVersionType buildType, DatabaseSmugglerOptionsServerSide options, bool isRevision, Logger log, DuplicateDocsHandler duplicateDocsHandler, bool throwOnCollectionMismatchError)
            {
                _database = database;
                _buildType = buildType;
                _options = options;
                _isRevision = isRevision;
                _log = log;
                _enqueueThreshold = new Size(database.Is32Bits ? 2 : 32, SizeUnit.Megabytes);
                _duplicateDocsHandler = duplicateDocsHandler;
                _throwOnCollectionMismatchError = throwOnCollectionMismatchError;

                _missingDocumentsForRevisions = isRevision || buildType == BuildVersionType.V3 ? new ConcurrentDictionary<string, CollectionName>() : null;
                _documentIdsOfMissingAttachments = isRevision ? null : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _command = new MergedBatchPutCommand(database, buildType, log, _missingDocumentsForRevisions, _documentIdsOfMissingAttachments)
                {
                    IsRevision = isRevision,
                };

                if (_throwOnCollectionMismatchError == false)
                    _command.DocumentCollectionMismatchHandler = item => _duplicateDocsHandler.AddDocument(item);
            }

            public async ValueTask WriteDocumentAsync(DocumentItem item, SmugglerProgressBase.CountsWithLastEtagAndAttachments progress)
            {
                if (item.Attachments != null)
                {
                    if (_options.OperateOnTypes.HasFlag(DatabaseItemType.Attachments))
                        progress.Attachments.ReadCount += item.Attachments.Count;
                    else
                        progress.Attachments.Skipped = true;
                }

                _command.Add(item);
                await HandleBatchOfDocumentsIfNecessaryAsync();
            }

            public async ValueTask WriteTombstoneAsync(Tombstone tombstone, SmugglerProgressBase.CountsWithLastEtag progress)
            {
                _command.Add(new DocumentItem
                {
                    Tombstone = tombstone
                });
                await HandleBatchOfDocumentsIfNecessaryAsync();
            }

            public async ValueTask WriteConflictAsync(DocumentConflict conflict, SmugglerProgressBase.CountsWithLastEtag progress)
            {
                _command.Add(new DocumentItem
                {
                    Conflict = conflict
                });
                await HandleBatchOfDocumentsIfNecessaryAsync();
            }

            public async ValueTask DeleteDocumentAsync(string id)
            {
                await _database.TxMerger.Enqueue(new DeleteDocumentCommand(id, null, _database));
            }

            public IEnumerable<DocumentItem> GetDocumentsWithDuplicateCollection()
            {
                if (_duplicateDocsHandler.DocumentsWithDuplicateCollection == null)
                    yield break;

                if (_duplicateDocsHandler.DocumentsWithDuplicateCollection.Count == 0)
                    yield break;

                foreach (var item in _duplicateDocsHandler.DocumentsWithDuplicateCollection)
                {
                    yield return item;
                }

                _duplicateDocsHandler._markForDispose = true;
            }

            public Stream GetTempStream()
            {
                if (_command.AttachmentStreamsTempFile == null)
                    _command.AttachmentStreamsTempFile = _database.DocumentsStorage.AttachmentsStorage.GetTempFile("smuggler");

                return _command.AttachmentStreamsTempFile.StartNewStream();
            }

            public DocumentsOperationContext GetContextForNewDocument()
            {
                _command.Context.CachedProperties.NewDocument();
                return _command.Context;
            }

            public async ValueTask DisposeAsync()
            {
                await FinishBatchOfDocumentsAsync();
                await FixDocumentMetadataIfNecessaryAsync();
                await DeleteRevisionsForNonExistingDocumentsAsync();

                if (_duplicateDocsHandler._markForDispose)
                    _duplicateDocsHandler.Dispose();
            }

            private async ValueTask FixDocumentMetadataIfNecessaryAsync()
            {
                if (_documentIdsOfMissingAttachments == null ||
                    _documentIdsOfMissingAttachments.Count == 0)
                    return;

                _fixDocumentMetadataCommand = new MergedBatchFixDocumentMetadataCommand(_database, _log);

                foreach (var docId in _documentIdsOfMissingAttachments)
                {
                    _fixDocumentMetadataCommand.Add(docId);
                    await HandleBatchOfFixDocumentsMetadataIfNecessaryAsync();
                }

                await FinishBatchOfFixDocumentsMetadataAsync();
            }

            private async ValueTask HandleBatchOfFixDocumentsMetadataIfNecessaryAsync()
            {
                var prevDoneAndHasEnough = _fixDocumentMetadataCommand.Context.AllocatedMemory > Constants.Size.Megabyte && _prevRevisionCommandTask.IsCompleted;
                var currentReachedLimit = _fixDocumentMetadataCommand.Context.AllocatedMemory > _enqueueThreshold.GetValue(SizeUnit.Bytes);

                if (currentReachedLimit == false && prevDoneAndHasEnough == false)
                    return;

                var prevCommand = _prevFixDocumentMetadataCommand;
                var prevCommandTask = _prevFixDocumentMetadataCommandTask;
                var commandTask = _database.TxMerger.Enqueue(_fixDocumentMetadataCommand);
                // we ensure that we first enqueue the command to if we
                // fail to do that, we won't be waiting on the previous
                // one
                _prevFixDocumentMetadataCommand = _fixDocumentMetadataCommand;
                _prevFixDocumentMetadataCommandTask = commandTask;

                if (prevCommand != null)
                {
                    using (prevCommand)
                    {
                        await prevCommandTask;
                        Debug.Assert(prevCommand.IsDisposed == false,
                            "we rely on reusing this context on the next batch, so it has to be disposed here");
                    }
                }

                _fixDocumentMetadataCommand = new MergedBatchFixDocumentMetadataCommand(_database, _log);
            }

            private async ValueTask FinishBatchOfFixDocumentsMetadataAsync()
            {
                if (_prevFixDocumentMetadataCommand != null)
                {
                    using (_prevFixDocumentMetadataCommand)
                        await _prevFixDocumentMetadataCommandTask;

                    _prevFixDocumentMetadataCommand = null;
                }

                using (_fixDocumentMetadataCommand)
                {
                    if (_fixDocumentMetadataCommand.Ids.Count > 0)
                    {
                        await _database.TxMerger.Enqueue(_fixDocumentMetadataCommand);
                    }
                }

                _fixDocumentMetadataCommand = null;
            }

            private async ValueTask DeleteRevisionsForNonExistingDocumentsAsync()
            {
                if (_missingDocumentsForRevisions == null ||
                    _missingDocumentsForRevisions.Count == 0)
                    return;

                _revisionDeleteCommand = new MergedBatchDeleteRevisionCommand(_database, _log);

                foreach (var docId in _missingDocumentsForRevisions)
                {
                    _revisionDeleteCommand.Add(docId);
                    await HandleBatchOfRevisionsIfNecessaryAsync();
                }

                await FinishBatchOfRevisionsAsync();
            }

            private async ValueTask HandleBatchOfRevisionsIfNecessaryAsync()
            {
                var prevDoneAndHasEnough = _revisionDeleteCommand.Context.AllocatedMemory > Constants.Size.Megabyte && _prevRevisionCommandTask.IsCompleted;
                var currentReachedLimit = _revisionDeleteCommand.Context.AllocatedMemory > _enqueueThreshold.GetValue(SizeUnit.Bytes);

                if (currentReachedLimit == false && prevDoneAndHasEnough == false)
                    return;

                var prevCommand = _prevRevisionDeleteCommand;
                var prevCommandTask = _prevRevisionCommandTask;
                var commandTask = _database.TxMerger.Enqueue(_revisionDeleteCommand);
                // we ensure that we first enqueue the command to if we
                // fail to do that, we won't be waiting on the previous
                // one
                _prevRevisionDeleteCommand = _revisionDeleteCommand;
                _prevRevisionCommandTask = commandTask;

                if (prevCommand != null)
                {
                    using (prevCommand)
                    {
                        await prevCommandTask;
                        Debug.Assert(prevCommand.IsDisposed == false,
                            "we rely on reusing this context on the next batch, so it has to be disposed here");
                    }
                }

                _revisionDeleteCommand = new MergedBatchDeleteRevisionCommand(_database, _log);
            }

            private async ValueTask FinishBatchOfRevisionsAsync()
            {
                if (_prevRevisionDeleteCommand != null)
                {
                    using (_prevRevisionDeleteCommand)
                        await _prevRevisionCommandTask;

                    _prevRevisionDeleteCommand = null;
                }

                using (_revisionDeleteCommand)
                {
                    if (_revisionDeleteCommand.Ids.Count > 0)
                    {
                        await _database.TxMerger.Enqueue(_revisionDeleteCommand);
                    }
                }

                _revisionDeleteCommand = null;
            }

            private async ValueTask HandleBatchOfDocumentsIfNecessaryAsync()
            {
                var commandSize = _command.GetCommandAllocationSize();
                var prevDoneAndHasEnough = commandSize > Constants.Size.Megabyte && _prevCommandTask.IsCompleted;
                var currentReachedLimit = commandSize > _enqueueThreshold.GetValue(SizeUnit.Bytes);

                if (currentReachedLimit == false && prevDoneAndHasEnough == false)
                    return;

                var prevCommand = _prevCommand;
                var prevCommandTask = _prevCommandTask;

                var commandTask = _database.TxMerger.Enqueue(_command);
                // we ensure that we first enqueue the command to if we
                // fail to do that, we won't be waiting on the previous
                // one
                _prevCommand = _command;
                _prevCommandTask = commandTask;

                if (prevCommand != null)
                {
                    using (prevCommand)
                    {
                        await prevCommandTask;
                        Debug.Assert(prevCommand.IsDisposed == false,
                            "we rely on reusing this context on the next batch, so it has to be disposed here");
                    }
                }

                _command = new MergedBatchPutCommand(_database, _buildType, _log,
                    _missingDocumentsForRevisions, _documentIdsOfMissingAttachments)
                {
                    IsRevision = _isRevision,
                };

                if (_throwOnCollectionMismatchError == false)
                    _command.DocumentCollectionMismatchHandler = item => _duplicateDocsHandler.AddDocument(item);
            }

            private async ValueTask FinishBatchOfDocumentsAsync()
            {
                if (_prevCommand != null)
                {
                    using (_prevCommand)
                        await _prevCommandTask;

                    _prevCommand = null;
                }

                using (_command)
                {
                    if (_command.Documents.Count > 0)
                        await _database.TxMerger.Enqueue(_command);
                }

                _command = null;
            }
        }

        private class DatabaseCompareExchangeActions : ICompareExchangeActions
        {
            private const int BatchSize = 10 * 1024;

            private readonly Size _compareExchangeValuesBatchSize;
            private Size _compareExchangeValuesSize;

            private readonly Size _clusterTransactionCommandsBatchSize;
            private Size _clusterTransactionCommandsSize;

            private readonly DocumentDatabase _database;
            private readonly JsonOperationContext _context;

            private readonly List<RemoveCompareExchangeCommand> _compareExchangeRemoveCommands = new List<RemoveCompareExchangeCommand>();
            private readonly List<AddOrUpdateCompareExchangeCommand> _compareExchangeAddOrUpdateCommands = new List<AddOrUpdateCompareExchangeCommand>();
            private DisposableReturnedArray<ClusterTransactionCommand.ClusterTransactionDataCommand> _clusterTransactionCommands = new DisposableReturnedArray<ClusterTransactionCommand.ClusterTransactionDataCommand>(BatchSize);
            private readonly DocumentContextHolder _documentContextHolder;
            private long? _lastAddOrUpdateOrRemoveResultIndex;
            private long? _lastClusterTransactionIndex;
            private readonly BackupKind? _backupKind;
            private readonly CancellationToken _token;

            public DatabaseCompareExchangeActions(DocumentDatabase database, JsonOperationContext context, BackupKind? backupKind, CancellationToken token)
            {
                _database = database;
                _context = context;
                _backupKind = backupKind;
                _token = token;
                _documentContextHolder = new DocumentContextHolder(database);

                _compareExchangeValuesBatchSize = new Size(database.Is32Bits ? 2 : 4, SizeUnit.Megabytes);
                _compareExchangeValuesSize = new Size(0, SizeUnit.Megabytes);

                _clusterTransactionCommandsBatchSize = new Size(database.Is32Bits ? 2 : 16, SizeUnit.Megabytes);
                _clusterTransactionCommandsSize = new Size(0, SizeUnit.Megabytes);
            }

            public async ValueTask WriteKeyValueAsync(string key, BlittableJsonReaderObject value, Document existingDocument)
            {
                if (_compareExchangeValuesSize >= _compareExchangeValuesBatchSize || _compareExchangeAddOrUpdateCommands.Count >= BatchSize)
                {
                    await SendAddOrUpdateCommandsAsync(_context);
                    _compareExchangeValuesSize.Set(0, SizeUnit.Bytes);
                }

                if (_clusterTransactionCommandsSize >= _clusterTransactionCommandsBatchSize || _clusterTransactionCommands.Length >= BatchSize)
                {
                    await SendClusterTransactionsAsync();
                    _clusterTransactionCommandsSize.Set(0, SizeUnit.Bytes);
                }

                if (ClusterTransactionCommand.IsAtomicGuardKey(key, out var docId))
                {
                    value?.Dispose();

                    var ctx = _documentContextHolder.GetContextForRead();

                    Document doc;
                    if (existingDocument != null)
                    {
                        doc = existingDocument;
                        doc.Data = doc.Data.Clone(ctx);
                    }
                    else
                    {
                        if (_backupKind is BackupKind.Full or BackupKind.Incremental)
                        {
                            // if we are restoring from a backup, we'll check if the atomic guard already exists
                            // if it does, we don't need to save it again
                            using (_database.ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
                            using (context.OpenReadTransaction())
                            {
                                var result = _database.ServerStore.Cluster.GetCompareExchangeValue(context, CompareExchangeKey.GetStorageKey(_database.Name, key));
                                if (result.Value != null)
                                    return;
                            }
                        }

                        doc = _database.DocumentsStorage.Get(ctx, docId, DocumentFields.Data | DocumentFields.ChangeVector | DocumentFields.Id);
                        if (doc == null)
                            return;
                    }

                    _clusterTransactionCommands.Push(new ClusterTransactionCommand.ClusterTransactionDataCommand
                    {
                        Id = doc.Id,
                        Document = doc.Data,
                        Type = CommandType.PUT,
                        ChangeVector = ctx.GetLazyString(doc.ChangeVector),
                        FromBackup = _backupKind
                    });

                    _clusterTransactionCommandsSize.Add(doc.Data.Size, SizeUnit.Bytes);
                }
                else
                {
                    _compareExchangeAddOrUpdateCommands.Add(new AddOrUpdateCompareExchangeCommand(_database.Name, key, value, 0, _context, RaftIdGenerator.DontCareId,
                        fromBackup: true));

                    _compareExchangeValuesSize.Add(value.Size, SizeUnit.Bytes);
                }
            }

            public async ValueTask WriteTombstoneKeyAsync(string key)
            {
                var index = _database.ServerStore.LastRaftCommitIndex;
                _compareExchangeRemoveCommands.Add(new RemoveCompareExchangeCommand(_database.Name, key, index, _context, RaftIdGenerator.DontCareId, fromBackup: true));

                if (_compareExchangeRemoveCommands.Count < BatchSize)
                    return;

                await SendRemoveCommandsAsync(_context);
            }

            public async ValueTask DisposeAsync()
            {
                using (_documentContextHolder)
                using (_clusterTransactionCommands)
                {
                    await SendClusterTransactionsAsync();
                    await SendAddOrUpdateCommandsAsync(_context);
                    await SendRemoveCommandsAsync(_context);

                    if (_lastAddOrUpdateOrRemoveResultIndex != null)
                        await _database.ServerStore.Cluster.WaitForIndexNotification(_lastAddOrUpdateOrRemoveResultIndex.Value, TimeSpan.FromMinutes(1));

                    if (_lastClusterTransactionIndex != null)
                    {
                        await _database.ServerStore.Cluster.WaitForIndexNotification(_lastClusterTransactionIndex.Value, TimeSpan.FromMinutes(1));

                        if (_backupKind is null or BackupKind.None)
                        {
                            // waiting for the commands to be applied
                            await _database.RachisLogIndexNotifications.WaitForIndexNotification(_lastClusterTransactionIndex.Value, _token);
                        }
                    }
                }
            }

            private async ValueTask SendClusterTransactionsAsync()
            {
                if (_clusterTransactionCommands.Length == 0)
                    return;

                var parsedCommands = _clusterTransactionCommands.GetArraySegment();

                var raftRequestId = RaftIdGenerator.NewId();
                var options = new ClusterTransactionCommand.ClusterTransactionOptions(string.Empty, disableAtomicDocumentWrites: false, ClusterCommandsVersionManager.CurrentClusterMinimalVersion);
                var topology = _database.ServerStore.LoadDatabaseTopology(_database.Name);

                var clusterTransactionCommand = new ClusterTransactionCommand(_database.Name, _database.IdentityPartsSeparator, topology, parsedCommands, options, raftRequestId);
                clusterTransactionCommand.FromBackup = true;

                var clusterTransactionResult = await _database.ServerStore.SendToLeaderAsync(clusterTransactionCommand);
                for (int i = 0; i < _clusterTransactionCommands.Length; i++)
                {
                    _clusterTransactionCommands[i].Document.Dispose();
                }

                _clusterTransactionCommands.Clear();
                _documentContextHolder.Reset();

                if (clusterTransactionResult.Result is List<ClusterTransactionCommand.ClusterTransactionErrorInfo> errors)
                    throw new ClusterTransactionConcurrencyException($"Failed to execute cluster transaction due to the following issues: {string.Join(Environment.NewLine, errors.Select(e => e.Message))}")
                    {
                        ConcurrencyViolations = errors.Select(e => e.Violation).ToArray()
                    };

                _lastClusterTransactionIndex = clusterTransactionResult.Index;
            }

            private async ValueTask SendAddOrUpdateCommandsAsync(JsonOperationContext context)
            {
                if (_compareExchangeAddOrUpdateCommands.Count == 0)
                    return;

                var addOrUpdateResult = await _database.ServerStore.SendToLeaderAsync(context, new AddOrUpdateCompareExchangeBatchCommand(_compareExchangeAddOrUpdateCommands, context, RaftIdGenerator.DontCareId));
                foreach (var command in _compareExchangeAddOrUpdateCommands)
                {
                    command.Value.Dispose();
                }
                _compareExchangeAddOrUpdateCommands.Clear();

                _lastAddOrUpdateOrRemoveResultIndex = addOrUpdateResult.Index;
            }

            private async ValueTask SendRemoveCommandsAsync(JsonOperationContext context)
            {
                if (_compareExchangeRemoveCommands.Count == 0)
                    return;
                var addOrUpdateResult = await _database.ServerStore.SendToLeaderAsync(context, new AddOrUpdateCompareExchangeBatchCommand(_compareExchangeRemoveCommands, context, RaftIdGenerator.DontCareId));
                _compareExchangeRemoveCommands.Clear();

                _lastAddOrUpdateOrRemoveResultIndex = addOrUpdateResult.Index;
            }

            public JsonOperationContext GetContextForNewCompareExchangeValue()
            {
                return _context;
            }

            private struct DisposableReturnedArray<T> : IDisposable
            {
                private readonly T[] _array;

                private readonly int _maxLength;

                public int Length;

                public DisposableReturnedArray(int length)
                {
                    _array = ArrayPool<T>.Shared.Rent(length);
                    _maxLength = length;
                    Length = 0;
                }

                public void Push(T item)
                {
                    if (Length >= _maxLength)
                        throw new InvalidOperationException($"Cannot put more than {_maxLength} elements to the array.");

                    _array[Length] = item;
                    Length++;
                }

                public T this[int index] => _array[index];

                public ArraySegment<T> GetArraySegment() => new ArraySegment<T>(_array, 0, Length);

                public void Clear() => Length = 0;

                public void Dispose() => ArrayPool<T>.Shared.Return(_array);
            }

            private class DocumentContextHolder : IDisposable
            {
                private readonly DocumentDatabase _database;
                private IDisposable _returnContext;
                private DocumentsTransaction _readTx;
                private DocumentsOperationContext _current;

                public DocumentContextHolder(DocumentDatabase database)
                {
                    _database = database;
                }

                public DocumentsOperationContext GetContextForRead()
                {
                    if (_current != null)
                        return _current;

                    _returnContext = _database.DocumentsStorage.ContextPool.AllocateOperationContext(out _current);
                    _readTx = _current.OpenReadTransaction();
                    return _current;
                }

                public void Reset()
                {
                    using (_returnContext)
                    using (_readTx)
                    {

                    }

                    _returnContext = null;
                    _readTx = null;
                    _current = null;
                }

                public void Dispose()
                {
                    using (_returnContext)
                    using (_readTx)
                    {
                    }
                }
            }
        }

        private class DatabaseKeyValueActions : IKeyValueActions<long>
        {
            private readonly DocumentDatabase _database;
            private readonly Dictionary<string, long> _identities;

            public DatabaseKeyValueActions(DocumentDatabase database)
            {
                _database = database;
                _identities = new Dictionary<string, long>();
            }

            public async ValueTask WriteKeyValueAsync(string key, long value)
            {
                const int batchSize = 1024;

                _identities[key] = value;

                if (_identities.Count < batchSize)
                    return;

                await SendIdentitiesAsync();
            }

            public async ValueTask DisposeAsync()
            {
                if (_identities.Count == 0)
                    return;

                await SendIdentitiesAsync();
            }

            private async ValueTask SendIdentitiesAsync()
            {
                //fire and forget, do not hold-up smuggler operations waiting for Raft command
                await _database.ServerStore.SendToLeaderAsync(new UpdateClusterIdentityCommand(_database.Name, _identities, false, RaftIdGenerator.NewId()));

                _identities.Clear();
            }
        }

        private class DatabaseRecordActions : IDatabaseRecordActions
        {
            private readonly DocumentDatabase _database;
            private readonly Logger _log;

            public DatabaseRecordActions(DocumentDatabase database, Logger log)
            {
                _database = database;
                _log = log;
            }

            public async ValueTask WriteDatabaseRecordAsync(DatabaseRecord databaseRecord, SmugglerProgressBase.DatabaseRecordProgress progress, AuthorizationStatus authorizationStatus, DatabaseRecordItemType databaseRecordItemType)
            {
                var currentDatabaseRecord = _database.ReadDatabaseRecord();
                var tasks = new List<Task<(long Index, object Result)>>();

                if (databaseRecord == null)
                    return;

                if (databaseRecord.ConflictSolverConfig != null && databaseRecordItemType.HasFlag(DatabaseRecordItemType.ConflictSolverConfig))
                {
                    if (currentDatabaseRecord?.ConflictSolverConfig != null)
                    {
                        foreach (var collection in currentDatabaseRecord.ConflictSolverConfig.ResolveByCollection)
                        {
                            if ((databaseRecord.ConflictSolverConfig.ResolveByCollection.ContainsKey(collection.Key)) == false)
                            {
                                databaseRecord.ConflictSolverConfig.ResolveByCollection.Add(collection.Key, collection.Value);
                            }
                        }
                    }

                    if (_log.IsInfoEnabled)
                        _log.Info("Configuring conflict solver config from smuggler");
                    tasks.Add(_database.ServerStore.SendToLeaderAsync(new ModifyConflictSolverCommand(_database.Name, RaftIdGenerator.DontCareId)
                    {
                        Solver = databaseRecord.ConflictSolverConfig
                    }));
                    progress.ConflictSolverConfigUpdated = true;
                }

                if (databaseRecord.PeriodicBackups.Count > 0 && databaseRecordItemType.HasFlag(DatabaseRecordItemType.PeriodicBackups))
                {
                    if (_log.IsInfoEnabled)
                        _log.Info("Configuring periodic backups configuration from smuggler");
                    foreach (var backupConfig in databaseRecord.PeriodicBackups)
                    {
                        currentDatabaseRecord?.PeriodicBackups.ForEach(x =>
                        {
                            if (x.Name.Equals(backupConfig.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                tasks.Add(_database.ServerStore.SendToLeaderAsync(new DeleteOngoingTaskCommand(x.TaskId, OngoingTaskType.Backup, _database.Name, RaftIdGenerator.DontCareId)));
                            }
                        });

                        backupConfig.TaskId = 0;
                        backupConfig.Disabled = true;
                        tasks.Add(_database.ServerStore.SendToLeaderAsync(new UpdatePeriodicBackupCommand(backupConfig, _database.Name, RaftIdGenerator.DontCareId)));
                    }
                    progress.PeriodicBackupsUpdated = true;
                }

                if (databaseRecord.SinkPullReplications.Count > 0 && databaseRecordItemType.HasFlag(DatabaseRecordItemType.SinkPullReplications))
                {
                    if (_log.IsInfoEnabled)
                        _log.Info("Configuring sink pull replication configuration from smuggler");
                    foreach (var pullReplication in databaseRecord.SinkPullReplications)
                    {
                        currentDatabaseRecord?.SinkPullReplications.ForEach(x =>
                        {
                            if (x.Name.Equals(pullReplication.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                tasks.Add(_database.ServerStore.SendToLeaderAsync(new DeleteOngoingTaskCommand(x.TaskId, OngoingTaskType.PullReplicationAsSink, _database.Name, RaftIdGenerator.DontCareId)));
                            }
                        });
                        pullReplication.TaskId = 0;
                        pullReplication.Disabled = true;
                        tasks.Add(_database.ServerStore.SendToLeaderAsync(new UpdatePullReplicationAsSinkCommand(_database.Name, RaftIdGenerator.DontCareId)
                        {
                            PullReplicationAsSink = pullReplication
                        }));
                    }
                    progress.SinkPullReplicationsUpdated = true;
                }

                if (databaseRecord.HubPullReplications.Count > 0 && databaseRecordItemType.HasFlag(DatabaseRecordItemType.HubPullReplications))
                {
                    if (_log.IsInfoEnabled)
                        _log.Info("Configuring hub pull replication configuration from smuggler");
                    foreach (var pullReplication in databaseRecord.HubPullReplications)
                    {
                        currentDatabaseRecord?.HubPullReplications.ForEach(x =>
                        {
                            if (x.Name.Equals(pullReplication.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                tasks.Add(_database.ServerStore.SendToLeaderAsync(new DeleteOngoingTaskCommand(x.TaskId, OngoingTaskType.PullReplicationAsHub, _database.Name, RaftIdGenerator.DontCareId)));
                            }
                        });
                        pullReplication.TaskId = 0;
                        pullReplication.Disabled = true;
                        tasks.Add(_database.ServerStore.SendToLeaderAsync(new UpdatePullReplicationAsHubCommand(_database.Name, RaftIdGenerator.DontCareId)
                        {
                            Definition = pullReplication
                        }
                        ));
                    }
                    progress.HubPullReplicationsUpdated = true;
                }

                if (databaseRecord.Sorters.Count > 0 && databaseRecordItemType.HasFlag(DatabaseRecordItemType.Sorters))
                {
                    if (_log.IsInfoEnabled)
                        _log.Info("Configuring sorters configuration from smuggler");

                    tasks.Add(_database.ServerStore.SendToLeaderAsync(new PutSortersCommand(_database.Name, RaftIdGenerator.DontCareId)
                    {
                        Sorters = databaseRecord.Sorters.Values.ToList()
                    }));

                    progress.SortersUpdated = true;
                }

                if (databaseRecord.Analyzers.Count > 0 && databaseRecordItemType.HasFlag(DatabaseRecordItemType.Analyzers))
                {
                    if (_log.IsInfoEnabled)
                        _log.Info("Configuring analyzers configuration from smuggler");

                    tasks.Add(_database.ServerStore.SendToLeaderAsync(new PutAnalyzersCommand(_database.Name, RaftIdGenerator.DontCareId)
                    {
                        Analyzers = databaseRecord.Analyzers.Values.ToList()
                    }));

                    progress.AnalyzersUpdated = true;
                }

                if (databaseRecord.ExternalReplications.Count > 0 && databaseRecordItemType.HasFlag(DatabaseRecordItemType.ExternalReplications))
                {
                    if (_log.IsInfoEnabled)
                        _log.Info("Configuring external replications configuration from smuggler");
                    foreach (var replication in databaseRecord.ExternalReplications)
                    {
                        currentDatabaseRecord?.ExternalReplications.ForEach(x =>
                        {
                            if (x.Name.Equals(replication.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                tasks.Add(_database.ServerStore.SendToLeaderAsync(new DeleteOngoingTaskCommand(x.TaskId, OngoingTaskType.Replication, _database.Name, RaftIdGenerator.DontCareId)));
                            }
                        });
                        replication.TaskId = 0;
                        replication.Disabled = true;
                        tasks.Add(_database.ServerStore.SendToLeaderAsync(new UpdateExternalReplicationCommand(_database.Name, RaftIdGenerator.DontCareId)
                        {
                            Watcher = replication
                        }));
                    }
                    progress.ExternalReplicationsUpdated = true;
                }

                if (databaseRecord.RavenConnectionStrings.Count > 0 && databaseRecordItemType.HasFlag(DatabaseRecordItemType.RavenConnectionStrings))
                {
                    if (_log.IsInfoEnabled)
                        _log.Info("Configuring Raven connection strings configuration from smuggler");
                    foreach (var connectionString in databaseRecord.RavenConnectionStrings)
                    {
                        tasks.Add(_database.ServerStore.SendToLeaderAsync(new PutRavenConnectionStringCommand(connectionString.Value, _database.Name, RaftIdGenerator.DontCareId)));
                    }
                    progress.RavenConnectionStringsUpdated = true;
                }
                
                if (databaseRecord.RavenEtls.Count > 0 && databaseRecordItemType.HasFlag(DatabaseRecordItemType.RavenEtls))
                {
                    if (_log.IsInfoEnabled)
                        _log.Info("Configuring raven etls configuration from smuggler");
                    foreach (var etl in databaseRecord.RavenEtls)
                    {
                        currentDatabaseRecord?.RavenEtls.ForEach(x =>
                        {
                            if (x.Name.Equals(etl.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                tasks.Add(_database.ServerStore.SendToLeaderAsync(new DeleteOngoingTaskCommand(x.TaskId, OngoingTaskType.RavenEtl, _database.Name, RaftIdGenerator.DontCareId)));
                            }
                        });
                        etl.TaskId = 0;
                        etl.Disabled = true;
                        tasks.Add(_database.ServerStore.SendToLeaderAsync(new AddRavenEtlCommand(etl, _database.Name, RaftIdGenerator.DontCareId)));
                    }
                    progress.RavenEtlsUpdated = true;
                }

                if (databaseRecord.SqlConnectionStrings.Count > 0 && databaseRecordItemType.HasFlag(DatabaseRecordItemType.SqlConnectionStrings))
                {
                    if (_log.IsInfoEnabled)
                        _log.Info("Configuring SQL connection strings from smuggler");
                    foreach (var connectionString in databaseRecord.SqlConnectionStrings)
                    {
                        tasks.Add(_database.ServerStore.SendToLeaderAsync(new PutSqlConnectionStringCommand(connectionString.Value, _database.Name, RaftIdGenerator.DontCareId)));
                    }
                    progress.SqlConnectionStringsUpdated = true;
                }
                
                if (databaseRecord.SqlEtls.Count > 0 && databaseRecordItemType.HasFlag(DatabaseRecordItemType.SqlEtls))
                {
                    if (_log.IsInfoEnabled)
                        _log.Info("Configuring sql etls configuration from smuggler");
                    foreach (var etl in databaseRecord.SqlEtls)
                    {
                        currentDatabaseRecord?.SqlEtls.ForEach(x =>
                        {
                            if (x.Name.Equals(etl.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                tasks.Add(_database.ServerStore.SendToLeaderAsync(new DeleteOngoingTaskCommand(x.TaskId, OngoingTaskType.SqlEtl, _database.Name, RaftIdGenerator.DontCareId)));
                            }
                        });
                        etl.TaskId = 0;
                        etl.Disabled = true;
                        tasks.Add(_database.ServerStore.SendToLeaderAsync(new AddSqlEtlCommand(etl, _database.Name, RaftIdGenerator.DontCareId)));
                    }
                    progress.SqlEtlsUpdated = true;
                }

                if (databaseRecord.TimeSeries != null && databaseRecordItemType.HasFlag(DatabaseRecordItemType.TimeSeries))
                {
                    if (currentDatabaseRecord?.TimeSeries != null)
                    {
                        foreach (var collection in currentDatabaseRecord.TimeSeries.Collections)
                        {
                            if ((databaseRecord.TimeSeries.Collections.ContainsKey(collection.Key)) == false)
                            {
                                databaseRecord.TimeSeries.Collections.Add(collection.Key, collection.Value);
                            }
                        }
                    }
                    if (_log.IsInfoEnabled)
                        _log.Info("Configuring time-series from smuggler");
                    tasks.Add(_database.ServerStore.SendToLeaderAsync(new EditTimeSeriesConfigurationCommand(databaseRecord.TimeSeries, _database.Name, RaftIdGenerator.DontCareId)));
                    progress.TimeSeriesConfigurationUpdated = true;
                }

                if (databaseRecord.DocumentsCompression != null && databaseRecordItemType.HasFlag(DatabaseRecordItemType.DocumentsCompression))
                {
                    if (currentDatabaseRecord?.DocumentsCompression?.Collections?.Length > 0 || currentDatabaseRecord?.DocumentsCompression?.CompressAllCollections == true)
                    {
                        var collectionsToAdd = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        foreach (var collection in currentDatabaseRecord.DocumentsCompression.Collections)
                        {
                            if (currentDatabaseRecord?.DocumentsCompression?.CompressAllCollections == true ||
                                databaseRecord.DocumentsCompression.Collections.Contains(collection) == false)
                            {
                                collectionsToAdd.Add(collection);
                            }
                        }

                        if (collectionsToAdd.Count > 0)
                        {
                            databaseRecord.DocumentsCompression.Collections = collectionsToAdd.Concat(currentDatabaseRecord.DocumentsCompression.Collections).ToArray();
                        }
                    }

                    if (_log.IsInfoEnabled)
                        _log.Info("Configuring documents compression from smuggler");
                    tasks.Add(_database.ServerStore.SendToLeaderAsync(new EditDocumentsCompressionCommand(databaseRecord.DocumentsCompression, _database.Name, RaftIdGenerator.DontCareId)));
                    progress.DocumentsCompressionConfigurationUpdated = true;
                }

                if (databaseRecord.Revisions != null && databaseRecordItemType.HasFlag(DatabaseRecordItemType.Revisions))
                {
                    if (currentDatabaseRecord?.Revisions != null)
                    {
                        foreach (var collection in currentDatabaseRecord.Revisions.Collections)
                        {
                            if ((databaseRecord.Revisions.Collections.ContainsKey(collection.Key)) == false)
                            {
                                databaseRecord.Revisions.Collections.Add(collection.Key, collection.Value);
                            }
                        }
                    }
                    if (_log.IsInfoEnabled)
                        _log.Info("Configuring revisions from smuggler");
                    tasks.Add(_database.ServerStore.SendToLeaderAsync(new EditRevisionsConfigurationCommand(databaseRecord.Revisions, _database.Name, RaftIdGenerator.DontCareId)));
                    progress.RevisionsConfigurationUpdated = true;
                }

                if (databaseRecord.RevisionsForConflicts != null && databaseRecordItemType.HasFlag(DatabaseRecordItemType.Revisions))
                {
                    if (_log.IsInfoEnabled)
                        _log.Info("Configuring revisions for conflicts from smuggler");
                    tasks.Add(_database.ServerStore.SendToLeaderAsync(new EditRevisionsForConflictsConfigurationCommand(databaseRecord.RevisionsForConflicts, _database.Name, RaftIdGenerator.DontCareId)));
                }

                if (databaseRecord.Expiration != null && databaseRecordItemType.HasFlag(DatabaseRecordItemType.Expiration))
                {
                    if (_log.IsInfoEnabled)
                        _log.Info("Configuring expiration from smuggler");
                    tasks.Add(_database.ServerStore.SendToLeaderAsync(new EditExpirationCommand(databaseRecord.Expiration, _database.Name, RaftIdGenerator.DontCareId)));
                    progress.ExpirationConfigurationUpdated = true;
                }

                if (databaseRecord.Refresh != null && databaseRecordItemType.HasFlag(DatabaseRecordItemType.Expiration))
                {
                    if (_log.IsInfoEnabled)
                        _log.Info("Configuring refresh from smuggler");
                    tasks.Add(_database.ServerStore.SendToLeaderAsync(new EditRefreshCommand(databaseRecord.Refresh, _database.Name, RaftIdGenerator.DontCareId)));
                    progress.RefreshConfigurationUpdated = true;
                }

                if (databaseRecord.Client != null && databaseRecordItemType.HasFlag(DatabaseRecordItemType.Client))
                {
                    if (_log.IsInfoEnabled)
                        _log.Info("Configuring client configuration from smuggler");

                    tasks.Add(_database.ServerStore.SendToLeaderAsync(new EditDatabaseClientConfigurationCommand(databaseRecord.Client, _database.Name, RaftIdGenerator.DontCareId)));
                    progress.ClientConfigurationUpdated = true;
                }

                if (databaseRecord.Integrations?.PostgreSql != null && databaseRecordItemType.HasFlag(DatabaseRecordItemType.PostgreSQLIntegration))
                {
                    if (_log.IsInfoEnabled)
                        _log.Info("Configuring PostgreSQL integration from smuggler");
                    tasks.Add(_database.ServerStore.SendToLeaderAsync(new EditPostgreSqlConfigurationCommand(databaseRecord.Integrations.PostgreSql, _database.Name, RaftIdGenerator.DontCareId)));
                    progress.PostreSQLConfigurationUpdated = true;
                }

                if (databaseRecord.UnusedDatabaseIds != null && databaseRecord.UnusedDatabaseIds.Count > 0)
                {
                    if (_log.IsInfoEnabled)
                        _log.Info("Set unused database Ids from smuggler");

                    tasks.Add(_database.ServerStore.SendToLeaderAsync(new UpdateUnusedDatabaseIdsCommand(_database.Name, databaseRecord.UnusedDatabaseIds, RaftIdGenerator.DontCareId)));

                    progress.UnusedDatabaseIdsUpdated = true;
                }

                if (databaseRecordItemType.HasFlag(DatabaseRecordItemType.LockMode))
                {
                    if (_log.IsInfoEnabled)
                        _log.Info("Configuring database lock mode from smuggler");

                    tasks.Add(_database.ServerStore.SendToLeaderAsync(new EditLockModeCommand(_database.Name, databaseRecord.LockMode, RaftIdGenerator.DontCareId)));

                    progress.LockModeUpdated = true;
                }
                
                if (databaseRecord.OlapConnectionStrings.Count > 0 && databaseRecordItemType.HasFlag(DatabaseRecordItemType.OlapConnectionStrings))
                {
                    if (_log.IsInfoEnabled)
                        _log.Info("Configuring OLAP connection strings from smuggler");
                    foreach (var connectionString in databaseRecord.OlapConnectionStrings)
                    {
                        tasks.Add(_database.ServerStore.SendToLeaderAsync(new PutOlapConnectionStringCommand(connectionString.Value, _database.Name, RaftIdGenerator.DontCareId)));
                    }
                    progress.OlapConnectionStringsUpdated = true;
                }

                if (databaseRecord.OlapEtls.Count > 0 && databaseRecordItemType.HasFlag(DatabaseRecordItemType.OlapEtls))
                {
                    if (_log.IsInfoEnabled)
                        _log.Info("Configuring OLAP ETLs configuration from smuggler");
                    foreach (var etl in databaseRecord.OlapEtls)
                    {
                        currentDatabaseRecord?.OlapEtls.ForEach(x =>
                        {
                            if (x.Name.Equals(etl.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                tasks.Add(_database.ServerStore.SendToLeaderAsync(new DeleteOngoingTaskCommand(x.TaskId, OngoingTaskType.OlapEtl, _database.Name, RaftIdGenerator.DontCareId)));
                            }
                        });
                        etl.TaskId = 0;
                        etl.Disabled = true;
                        tasks.Add(_database.ServerStore.SendToLeaderAsync(new AddOlapEtlCommand(etl, _database.Name, RaftIdGenerator.DontCareId)));
                    }
                    progress.OlapEtlsUpdated = true;
                }
                
                if (databaseRecord.ElasticSearchConnectionStrings.Count > 0 && databaseRecordItemType.HasFlag(DatabaseRecordItemType.ElasticSearchConnectionStrings))
                {
                    if (_log.IsInfoEnabled)
                        _log.Info("Configuring ElasticSearch connection strings from smuggler");
                    foreach (var connectionString in databaseRecord.ElasticSearchConnectionStrings)
                    {
                        tasks.Add(_database.ServerStore.SendToLeaderAsync(new PutElasticSearchConnectionStringCommand(connectionString.Value, _database.Name, RaftIdGenerator.DontCareId)));
                    }
                    progress.ElasticSearchConnectionStringsUpdated = true;
                }
                
                if (databaseRecord.ElasticSearchEtls.Count > 0 && databaseRecordItemType.HasFlag(DatabaseRecordItemType.ElasticSearchEtls))
                {
                    if (_log.IsInfoEnabled)
                        _log.Info("Configuring ElasticSearch ETLs configuration from smuggler");
                    foreach (var etl in databaseRecord.ElasticSearchEtls)
                    {
                        currentDatabaseRecord?.ElasticSearchEtls.ForEach(x =>
                        {
                            if (x.Name.Equals(etl.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                tasks.Add(_database.ServerStore.SendToLeaderAsync(new DeleteOngoingTaskCommand(x.TaskId, OngoingTaskType.ElasticSearchEtl, _database.Name, RaftIdGenerator.DontCareId)));
                            }
                        });
                        etl.TaskId = 0;
                        etl.Disabled = true;
                        tasks.Add(_database.ServerStore.SendToLeaderAsync(new AddElasticSearchEtlCommand(etl, _database.Name, RaftIdGenerator.DontCareId)));
                    }
                    progress.ElasticSearchEtlsUpdated = true;
                }

                if (tasks.Count == 0)
                    return;

                long maxIndex = 0;
                foreach (var task in tasks)
                {
                    var (index, _) = await task;
                    if (index > maxIndex)
                        maxIndex = index;
                }

                await _database.RachisLogIndexNotifications.WaitForIndexNotification(maxIndex, _database.ServerStore.Engine.OperationTimeout);

                tasks.Clear();
            }

            public ValueTask DisposeAsync()
            {
                return default;
            }
        }

        public class MergedBatchPutCommand : TransactionOperationsMerger.MergedTransactionCommand, IDisposable
        {
            public bool IsRevision;
            public Action<DocumentItem> DocumentCollectionMismatchHandler;

            private readonly DocumentDatabase _database;
            private readonly BuildVersionType _buildType;
            private readonly Logger _log;

            public readonly List<DocumentItem> Documents = new List<DocumentItem>();
            public StreamsTempFile AttachmentStreamsTempFile;

            private IDisposable _resetContext;
            private bool _isDisposed;

            public bool IsDisposed => _isDisposed;
            private readonly ConcurrentDictionary<string, CollectionName> _missingDocumentsForRevisions;
            private readonly HashSet<string> _documentIdsOfMissingAttachments;
            private readonly DocumentsOperationContext _context;
            private long _attachmentsStreamSizeOverhead;

            public MergedBatchPutCommand(DocumentDatabase database, BuildVersionType buildType,
                Logger log,
                ConcurrentDictionary<string, CollectionName> missingDocumentsForRevisions = null,
                HashSet<string> documentIdsOfMissingAttachments = null)
            {
                _database = database;
                _buildType = buildType;
                _log = log;
                _resetContext = _database.DocumentsStorage.ContextPool.AllocateOperationContext(out _context);
                _missingDocumentsForRevisions = missingDocumentsForRevisions;
                _documentIdsOfMissingAttachments = documentIdsOfMissingAttachments;

                if (_database.Is32Bits)
                {
                    using (var ctx = DocumentsOperationContext.ShortTermSingleUse(database))
                    using (ctx.OpenReadTransaction())
                    {
                        _collectionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var collection in _database.DocumentsStorage.GetCollections(ctx))
                        {
                            _collectionNames.Add(collection.Name);
                        }
                    }
                }
            }

            public DocumentsOperationContext Context => _context;

            protected override long ExecuteCmd(DocumentsOperationContext context)
            {
                if (_log.IsInfoEnabled)
                    _log.Info($"Importing {Documents.Count:#,#0} documents");

                var idsOfDocumentsToUpdateAfterAttachmentDeletion = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var databaseChangeVector = context.LastDatabaseChangeVector ?? DocumentsStorage.GetDatabaseChangeVector(context);

                foreach (var documentType in Documents)
                {
                    var tombstone = documentType.Tombstone;
                    long newEtag;
                    if (tombstone != null)
                    {
                        using (Slice.External(context.Allocator, tombstone.LowerId, out Slice key))
                        {
                            newEtag = _database.DocumentsStorage.GenerateNextEtag();
                            tombstone.ChangeVector = _database.DocumentsStorage.GetNewChangeVector(context, newEtag);
                            databaseChangeVector = ChangeVectorUtils.MergeVectors(databaseChangeVector, tombstone.ChangeVector);
                            AddTrxnIfNeeded(context, tombstone.LowerId, ref tombstone.ChangeVector);
                            switch (tombstone.Type)
                            {
                                case Tombstone.TombstoneType.Document:
                                    _database.DocumentsStorage.Delete(context, key, tombstone.LowerId, null, tombstone.LastModified.Ticks, tombstone.ChangeVector, new CollectionName(tombstone.Collection), documentFlags: tombstone.Flags);
                                    break;

                                case Tombstone.TombstoneType.Attachment:
                                    var idEnd = key.Content.IndexOf(SpecialChars.RecordSeparator);
                                    if (idEnd < 1)
                                        throw new InvalidOperationException("Cannot find a document ID inside the attachment key");
                                    var attachmentId = key.Content.Substring(idEnd);
                                    idsOfDocumentsToUpdateAfterAttachmentDeletion.Add(attachmentId);

                                    _database.DocumentsStorage.AttachmentsStorage.DeleteAttachmentDirect(context, key, false, "$fromReplication", null, tombstone.ChangeVector, tombstone.LastModified.Ticks);
                                    break;

                                case Tombstone.TombstoneType.Revision:
                                    _database.DocumentsStorage.RevisionsStorage.DeleteRevision(context, key, tombstone.Collection, tombstone.ChangeVector, tombstone.LastModified.Ticks);
                                    break;

                                case Tombstone.TombstoneType.Counter:
                                    _database.DocumentsStorage.CountersStorage.DeleteCounter(context, key.ToString(), tombstone.Collection, null);
                                    break;
                            }
                        }

                        continue;
                    }

                    var conflict = documentType.Conflict;
                    if (conflict != null)
                    {
                        databaseChangeVector = ChangeVectorUtils.MergeVectors(databaseChangeVector, documentType.Conflict.ChangeVector);
                        _database.DocumentsStorage.ConflictsStorage.AddConflict(context, conflict.Id, conflict.LastModified.Ticks, conflict.Doc, conflict.ChangeVector,
                            conflict.Collection, conflict.Flags, NonPersistentDocumentFlags.FromSmuggler);

                        continue;
                    }

                    if (documentType.Attachments != null)
                    {
                        foreach (var attachment in documentType.Attachments)
                        {
                            _database.DocumentsStorage.AttachmentsStorage.PutAttachmentStream(context, attachment.Tag, attachment.Base64Hash, attachment.Stream);
                        }
                    }

                    var document = documentType.Document;
                    var id = document.Id;

                    if (IsRevision)
                    {
                        PutAttachments(context, document, isRevision: true, out var hasAttachments);
                        if (hasAttachments)
                        {
                            databaseChangeVector = ChangeVectorUtils.MergeVectors(databaseChangeVector, context.LastDatabaseChangeVector);
                        }

                        if ((document.NonPersistentFlags.Contain(NonPersistentDocumentFlags.FromSmuggler)) &&
                            (_missingDocumentsForRevisions != null))
                        {
                            if (_database.DocumentsStorage.Get(context, document.Id) == null)
                            {
                                var collection = _database.DocumentsStorage.ExtractCollectionName(context, document.Data);
                                _missingDocumentsForRevisions.TryAdd(document.Id.ToString(), collection);
                            }
                        }

                        if (document.Flags.Contain(DocumentFlags.DeleteRevision))
                        {
                            _missingDocumentsForRevisions?.TryRemove(id, out _);
                            _database.DocumentsStorage.RevisionsStorage.Delete(context, id, document.Data, document.Flags,
                                document.NonPersistentFlags, document.ChangeVector, document.LastModified.Ticks);
                        }
                        else
                        {
                            _database.DocumentsStorage.RevisionsStorage.Put(context, id, document.Data, document.Flags,
                                document.NonPersistentFlags, document.ChangeVector, document.LastModified.Ticks);
                        }

                        databaseChangeVector = ChangeVectorUtils.MergeVectors(databaseChangeVector, document.ChangeVector);

                        continue;
                    }

                    if (DatabaseSmuggler.IsPreV4Revision(_buildType, id, document))
                    {
                        // handle old revisions
                        if (_database.DocumentsStorage.RevisionsStorage.Configuration == null)
                            ThrowRevisionsDisabled();

                        var endIndex = id.IndexOf(DatabaseSmuggler.PreV4RevisionsDocumentId, StringComparison.OrdinalIgnoreCase);
                        var newId = id.Substring(0, endIndex);

                        Document parentDocument = null;
                        if (_database.DocumentsStorage.Get(context, newId, DocumentFields.Id) == null)
                        {
                            var collection = _database.DocumentsStorage.ExtractCollectionName(context, document.Data);
                            _missingDocumentsForRevisions.TryAdd(newId, collection);
                        }
                        else
                        {
                            // the order of revisions in v3.x is different than we have in v4.x
                            // in v4.x: rev1, rev2, rev3, document (the change vector of the last revision is identical to the document)
                            // in v3.x: rev1, rev2, document, rev3
                            parentDocument = _database.DocumentsStorage.Get(context, newId);
                            _missingDocumentsForRevisions.TryRemove(newId, out _);
                        }

                        document.Flags |= DocumentFlags.HasRevisions;
                        _database.DocumentsStorage.RevisionsStorage.Put(context, newId, document.Data, document.Flags,
                            document.NonPersistentFlags, document.ChangeVector, document.LastModified.Ticks);

                        if (parentDocument != null)
                        {
                            // the change vector of the document must be identical to the one of the last revision
                            databaseChangeVector = ChangeVectorUtils.MergeVectors(databaseChangeVector, document.ChangeVector);

                            using (parentDocument.Data)
                                parentDocument.Data = parentDocument.Data.Clone(context);

                            _database.DocumentsStorage.Put(context, parentDocument.Id, null,
                                parentDocument.Data, parentDocument.LastModified.Ticks, document.ChangeVector, null,
                                parentDocument.Flags, parentDocument.NonPersistentFlags);
                        }

                        continue;
                    }

                    PutAttachments(context, document, isRevision: false, out _);

                    newEtag = _database.DocumentsStorage.GenerateNextEtag();
                    document.ChangeVector = _database.DocumentsStorage.GetNewChangeVector(context, newEtag);
                    databaseChangeVector = ChangeVectorUtils.MergeVectors(databaseChangeVector, document.ChangeVector);
                    AddTrxnIfNeeded(context, id, ref document.ChangeVector);

                    try
                    {
                        _database.DocumentsStorage.Put(context, id, expectedChangeVector: null, document.Data, document.LastModified.Ticks, document.ChangeVector, null, document.Flags, document.NonPersistentFlags);
                    }
                    catch (DocumentCollectionMismatchException)
                    {
                        if (DocumentCollectionMismatchHandler == null)
                            throw;

                        DocumentCollectionMismatchHandler.Invoke(documentType);
                    }
                }

                context.LastDatabaseChangeVector = databaseChangeVector;

                foreach (var idToUpdate in idsOfDocumentsToUpdateAfterAttachmentDeletion)
                {
                    _database.DocumentsStorage.AttachmentsStorage.UpdateDocumentAfterAttachmentChange(context, idToUpdate);
                }

                return Documents.Count;
            }

            private void AddTrxnIfNeeded(DocumentsOperationContext context, string id, ref string changeVector)
            {
                using (var doc = _database.DocumentsStorage.Get(context, id, DocumentFields.ChangeVector))
                {
                    string oldChangeVector;

                    if (doc != null)
                    {
                        oldChangeVector = doc.ChangeVector;
                    }
                    else
                    {
                        using (var tombstone = _database.DocumentsStorage.GetDocumentOrTombstone(context, id).Tombstone)
                        {
                            oldChangeVector = tombstone?.ChangeVector;
                        }
                    }

                    //The ClusterTransactionId can be null if the database was migrated from version smaller then v5.2 
                    if (_database.ClusterTransactionId != null)
                    {
                        var trxn = ChangeVectorUtils.GetEtagById(oldChangeVector, _database.ClusterTransactionId);
                        if (trxn > 0)
                            changeVector += $",TRXN:{trxn}-{_database.ClusterTransactionId}";
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private unsafe void PutAttachments(DocumentsOperationContext context, Document document, bool isRevision, out bool hasAttachments)
            {
                hasAttachments = false;

                if (document.Data.TryGet(Client.Constants.Documents.Metadata.Key, out BlittableJsonReaderObject metadata) == false ||
                    metadata.TryGet(Client.Constants.Documents.Metadata.Attachments, out BlittableJsonReaderArray attachments) == false)
                    return;

                var attachmentsStorage = _database.DocumentsStorage.AttachmentsStorage;
                foreach (BlittableJsonReaderObject attachment in attachments)
                {
                    hasAttachments = true;

                    if (attachment.TryGet(nameof(AttachmentName.Name), out LazyStringValue name) == false ||
                        attachment.TryGet(nameof(AttachmentName.ContentType), out LazyStringValue contentType) == false ||
                        attachment.TryGet(nameof(AttachmentName.Hash), out LazyStringValue hash) == false)
                        throw new ArgumentException($"The attachment info in missing a mandatory value: {attachment}");

                    var cv = Slices.Empty;
                    var type = (document.Flags & DocumentFlags.Revision) == DocumentFlags.Revision ? AttachmentType.Revision : AttachmentType.Document;

                    if (isRevision == false && attachmentsStorage.AttachmentExists(context, hash) == false)
                    {
                        _documentIdsOfMissingAttachments.Add(document.Id);
                    }

                    using (DocumentIdWorker.GetSliceFromId(_context, document.Id, out Slice lowerDocumentId))
                    using (DocumentIdWorker.GetLowerIdSliceAndStorageKey(_context, name, out Slice lowerName, out Slice nameSlice))
                    using (DocumentIdWorker.GetLowerIdSliceAndStorageKey(_context, contentType, out Slice lowerContentType, out Slice contentTypeSlice))
                    using (Slice.External(_context.Allocator, hash, out Slice base64Hash))
                    using (type == AttachmentType.Revision ? Slice.From(_context.Allocator, document.ChangeVector, out cv) : (IDisposable)null)
                    using (attachmentsStorage.GetAttachmentKey(_context, lowerDocumentId.Content.Ptr, lowerDocumentId.Size, lowerName.Content.Ptr, lowerName.Size,
                        base64Hash, lowerContentType.Content.Ptr, lowerContentType.Size, type, cv, out Slice keySlice))
                    {
                        attachmentsStorage.PutDirect(context, keySlice, nameSlice, contentTypeSlice, base64Hash);
                    }
                }
            }

            private static void ThrowRevisionsDisabled()
            {
                throw new InvalidOperationException("Revisions needs to be enabled before import!");
            }

            public void Dispose()
            {
                if (_isDisposed)
                    return;

                _isDisposed = true;

                foreach (var doc in Documents)
                {
                    if (doc.Document != null)
                    {
                        doc.Document.Data.Dispose();

                        if (doc.Attachments != null)
                        {
                            foreach (var attachment in doc.Attachments)
                            {
                                attachment.Dispose();
                            }
                        }
                    }
                }
                Documents.Clear();
                _resetContext?.Dispose();
                _resetContext = null;

                AttachmentStreamsTempFile?.Dispose();
                AttachmentStreamsTempFile = null;
            }

            /// <summary>
            /// Return the actual size this command allocates including the stream sizes
            /// </summary>
            /// <returns></returns>
            public long GetCommandAllocationSize()
            {
                return Context.AllocatedMemory + _attachmentsStreamSizeOverhead + _schemaOverHeadSize;
            }

            private HashSet<string> _collectionNames;
            private int _schemaOverHeadSize;

            public void Add(DocumentItem document)
            {
                Documents.Add(document);
                if (document.Attachments != null)
                {
                    if (document.Document.TryGetMetadata(out var metadata)
                        && metadata.TryGet(Client.Constants.Documents.Metadata.Attachments, out BlittableJsonReaderArray attachments))
                    {
                        foreach (BlittableJsonReaderObject attachment in attachments)
                        {
                            if (attachment.TryGet(nameof(Attachment.Size), out long size))
                            {
                                _attachmentsStreamSizeOverhead += size;
                            }
                        }
                    }
                }

                if (_database.Is32Bits && document.Document != null)
                {
                    if (document.Document.TryGetMetadata(out var metadata)
                        && metadata.TryGet(Client.Constants.Documents.Metadata.Collection, out string collectionName)
                        && _collectionNames.Add(collectionName))
                    {
                        _schemaOverHeadSize += SchemaSize;
                    }
                }
            }

            private const int SchemaSize = 2 * 1024 * 1024;

            public override TransactionOperationsMerger.IReplayableCommandDto<TransactionOperationsMerger.MergedTransactionCommand> ToDto<TTransaction>(TransactionOperationContext<TTransaction> context)
            {
                return new MergedBatchPutCommandDto
                {
                    BuildType = _buildType,
                    Documents = Documents,
                    IsRevision = IsRevision
                };
            }
        }

        public class MergedBatchPutCommandDto : TransactionOperationsMerger.IReplayableCommandDto<MergedBatchPutCommand>
        {
            public BuildVersionType BuildType;
            public List<DocumentItem> Documents;
            public bool IsRevision;

            public MergedBatchPutCommand ToCommand(DocumentsOperationContext context, DocumentDatabase database)
            {
                var log = LoggingSource.Instance.GetLogger<DatabaseDestination>(database.Name);
                var command = new MergedBatchPutCommand(database, BuildType, log)
                {
                    IsRevision = IsRevision
                };
                foreach (var document in Documents)
                {
                    command.Add(document);
                }

                return command;
            }
        }

        internal class MergedBatchFixDocumentMetadataCommand : TransactionOperationsMerger.MergedTransactionCommand, IDisposable
        {
            private readonly Logger _log;
            public HashSet<string> Ids = new HashSet<string>();
            private readonly DocumentDatabase _database;
            private readonly DocumentsOperationContext _context;
            public DocumentsOperationContext Context => _context;
            private bool _isDisposed;
            private readonly IDisposable _returnContext;
            public bool IsDisposed => _isDisposed;

            public MergedBatchFixDocumentMetadataCommand(DocumentDatabase database, Logger log)
            {
                _database = database;
                _log = log;
                _returnContext = _database.DocumentsStorage.ContextPool.AllocateOperationContext(out _context);
            }

            protected override long ExecuteCmd(DocumentsOperationContext context)
            {
                if (_log.IsInfoEnabled)
                    _log.Info($"Trying to update {Ids.Count:#,#0} documents metadata if necessary");

                var count = 0;
                foreach (var id in Ids)
                {
                    using (DocumentIdWorker.GetSliceFromId(context, id, out var lowerId))
                    {
                        var document = _database.DocumentsStorage.Get(context, lowerId, throwOnConflict: false, skipValidationInDebug: true);
                        if (document == null)
                            continue;

                        if (document.Data.TryGet(Client.Constants.Documents.Metadata.Key, out BlittableJsonReaderObject metadata) == false ||
                            metadata.TryGet(Client.Constants.Documents.Metadata.Attachments, out BlittableJsonReaderArray attachments) == false)
                            continue;

                        var attachmentsToRemoveNames = new HashSet<LazyStringValue>();
                        var attachmentsToRemoveHashes = new HashSet<LazyStringValue>();

                        foreach (BlittableJsonReaderObject attachment in attachments)
                        {
                            if (attachment.TryGet(nameof(AttachmentName.Name), out LazyStringValue name) == false ||
                                attachment.TryGet(nameof(AttachmentName.ContentType), out LazyStringValue _) == false ||
                                attachment.TryGet(nameof(AttachmentName.Hash), out LazyStringValue hash) == false)
                                throw new ArgumentException($"The attachment info in missing a mandatory value: {attachment}");

                            var attachmentsStorage = _database.DocumentsStorage.AttachmentsStorage;
                            if (attachmentsStorage.AttachmentExists(context, hash) == false)
                            {
                                attachmentsToRemoveNames.Add(name);
                                attachmentsToRemoveHashes.Add(hash);
                            }
                        }

                        if (attachmentsToRemoveNames.Count == 0)
                            continue;

                        count++;
                        var attachmentsToSave = new DynamicJsonArray();

                        foreach (BlittableJsonReaderObject attachment in attachments)
                        {
                            attachment.TryGet(nameof(AttachmentName.Hash), out LazyStringValue hash);

                            if (attachmentsToRemoveHashes.Contains(hash))
                                continue;

                            attachmentsToSave.Add(attachment);
                        }

                        foreach (var toRemove in attachmentsToRemoveNames)
                        {
                            _database.DocumentsStorage.AttachmentsStorage.DeleteAttachment(context, id, toRemove, null, updateDocument: false);
                        }

                        metadata.Modifications = new DynamicJsonValue(metadata);
                        document.Data.Modifications = new DynamicJsonValue(document.Data)
                        {
                            [Client.Constants.Documents.Metadata.Key] = metadata
                        };

                        if (attachmentsToSave.Count == 0)
                        {
                            document.Flags = document.Flags.Strip(DocumentFlags.HasAttachments);
                            metadata.Modifications.Remove(Client.Constants.Documents.Metadata.Attachments);
                        }
                        else
                        {
                            document.Flags |= DocumentFlags.HasAttachments;
                            metadata.Modifications = new DynamicJsonValue(metadata)
                            {
                                [Client.Constants.Documents.Metadata.Attachments] = attachmentsToSave
                            };
                        }

                        using (var old = document.Data)
                        {
                            var newDocument = context.ReadObject(old, document.Id, BlittableJsonDocumentBuilder.UsageMode.ToDisk);
                            _database.DocumentsStorage.Put(context, document.Id, null, newDocument);
                        }
                    }
                }

                if (_log.IsInfoEnabled)
                    _log.Info($"Updated {count:#,#0} documents metadata");

                return count;
            }

            public void Add(string id)
            {
                Ids.Add(id);
            }

            public void Dispose()
            {
                if (_isDisposed)
                    return;

                _isDisposed = true;
                Ids.Clear();
                _returnContext.Dispose();
            }

            public override TransactionOperationsMerger.IReplayableCommandDto<TransactionOperationsMerger.MergedTransactionCommand> ToDto<TTransaction>(TransactionOperationContext<TTransaction> context)
            {
                return new MergedBatchFixDocumentMetadataCommandDto
                {
                    Ids = Ids
                };
            }

            internal class MergedBatchFixDocumentMetadataCommandDto : TransactionOperationsMerger.IReplayableCommandDto<MergedBatchFixDocumentMetadataCommand>
            {
                public HashSet<string> Ids = new HashSet<string>();

                public MergedBatchFixDocumentMetadataCommand ToCommand(DocumentsOperationContext context, DocumentDatabase database)
                {
                    var log = LoggingSource.Instance.GetLogger<DatabaseDestination>(database.Name);
                    var command = new MergedBatchFixDocumentMetadataCommand(database, log);

                    foreach (var id in Ids)
                    {
                        command.Add(id);
                    }

                    return command;
                }
            }
        }

        internal class MergedBatchDeleteRevisionCommand : TransactionOperationsMerger.MergedTransactionCommand, IDisposable
        {
            private readonly Logger _log;
            public readonly List<KeyValuePair<string, CollectionName>> Ids = new List<KeyValuePair<string, CollectionName>>();
            private readonly DocumentDatabase _database;
            private readonly DocumentsOperationContext _context;
            public DocumentsOperationContext Context => _context;
            private bool _isDisposed;
            private readonly IDisposable _returnContext;
            public bool IsDisposed => _isDisposed;

            public MergedBatchDeleteRevisionCommand(DocumentDatabase database, Logger log)
            {
                _database = database;
                _log = log;
                _returnContext = _database.DocumentsStorage.ContextPool.AllocateOperationContext(out _context);
            }

            protected override long ExecuteCmd(DocumentsOperationContext context)
            {
                if (_log.IsInfoEnabled)
                    _log.Info($"Deleting {Ids.Count:#,#0} revisions");

                foreach (var id in Ids)
                {
                    using (DocumentIdWorker.GetSliceFromId(context, id.Key, out var lowerId))
                    {
                        _database.DocumentsStorage.RevisionsStorage.Delete(context,
                            id.Key,
                            lowerId,
                            id.Value,
                            _database.DocumentsStorage.GetNewChangeVector(context, _database.DocumentsStorage.GenerateNextEtag()),
                            _database.Time.GetUtcNow().Ticks,
                            NonPersistentDocumentFlags.FromSmuggler,
                            DocumentFlags.DeleteRevision);
                    }
                }
                return 1;
            }

            public void Add(KeyValuePair<string, CollectionName> id)
            {
                Ids.Add(id);
            }

            public void Dispose()
            {
                if (_isDisposed)
                    return;

                _isDisposed = true;
                Ids.Clear();
                _returnContext.Dispose();
            }

            public override TransactionOperationsMerger.IReplayableCommandDto<TransactionOperationsMerger.MergedTransactionCommand> ToDto<TTransaction>(TransactionOperationContext<TTransaction> context)
            {
                return new MergedBatchDeleteRevisionCommandDto
                {
                    Ids = Ids
                };
            }
        }

        internal class MergedBatchDeleteRevisionCommandDto : TransactionOperationsMerger.IReplayableCommandDto<MergedBatchDeleteRevisionCommand>
        {
            public List<KeyValuePair<string, CollectionName>> Ids = new List<KeyValuePair<string, CollectionName>>();

            public MergedBatchDeleteRevisionCommand ToCommand(DocumentsOperationContext context, DocumentDatabase database)
            {
                var log = LoggingSource.Instance.GetLogger<DatabaseDestination>(database.Name);
                var command = new MergedBatchDeleteRevisionCommand(database, log);

                foreach (var id in Ids)
                {
                    command.Add(id);
                }

                return command;
            }
        }

        private class CounterActions : ICounterActions
        {
            private readonly DocumentDatabase _database;
            private CountersHandler.SmugglerCounterBatchCommand _cmd;
            private CountersHandler.SmugglerCounterBatchCommand _prevCommand;
            private Task _prevCommandTask = Task.CompletedTask;
            private int _countersCount;
            private readonly int _maxBatchSize;

            private SmugglerResult _result;

            public CounterActions(DocumentDatabase database, SmugglerResult result)
            {
                _database = database;
                _result = result;
                _cmd = new CountersHandler.SmugglerCounterBatchCommand(_database, _result);

                _maxBatchSize = _database.Is32Bits ? 2 * 1024 : 10 * 1024;
            }

            private void AddToBatch(CounterGroupDetail counterGroupDetail)
            {
                _cmd.Add(counterGroupDetail);

                counterGroupDetail.Values.TryGet(CountersStorage.Values, out BlittableJsonReaderObject counters);
                _countersCount += counters?.Count ?? 0;
            }

            private void AddToBatch(CounterDetail counter)
            {
                _cmd.AddLegacy(counter.DocumentId, counter);
                _countersCount++;
            }

            public async ValueTask WriteCounterAsync(CounterGroupDetail counterDetail)
            {
                AddToBatch(counterDetail);
                await HandleBatchOfCountersIfNecessaryAsync();
            }

            public async ValueTask WriteLegacyCounterAsync(CounterDetail counterDetail)
            {
                AddToBatch(counterDetail);
                await HandleBatchOfCountersIfNecessaryAsync();
            }

            public void RegisterForDisposal(IDisposable data)
            {
                _cmd.RegisterForDisposal(data);
            }

            public async ValueTask DisposeAsync()
            {
                await FinishBatchOfCountersAsync();
            }

            private async ValueTask HandleBatchOfCountersIfNecessaryAsync()
            {
                if (_countersCount < _maxBatchSize)
                    return;

                var prevCommand = _prevCommand;
                var prevCommandTask = _prevCommandTask;

                var commandTask = _database.TxMerger.Enqueue(_cmd);

                _prevCommand = _cmd;
                _prevCommandTask = commandTask;

                if (prevCommand != null)
                {
                    using (prevCommand)
                    {
                        await prevCommandTask;
                    }
                }

                _cmd = new CountersHandler.SmugglerCounterBatchCommand(_database, _result);

                _countersCount = 0;
            }

            private async ValueTask FinishBatchOfCountersAsync()
            {
                if (_prevCommand != null)
                {
                    using (_prevCommand)
                    {
                        await _prevCommandTask;
                    }

                    _prevCommand = null;
                }

                using (_cmd)
                {
                    if (_countersCount > 0)
                    {
                        await _database.TxMerger.Enqueue(_cmd);
                    }
                }

                _cmd = null;
            }

            public DocumentsOperationContext GetContextForNewDocument()
            {
                _cmd.Context.CachedProperties.NewDocument();
                return _cmd.Context;
            }

            public Stream GetTempStream()
            {
                throw new NotSupportedException("GetTempStream is never used in CounterActions. Shouldn't happen");
            }
        }

        private class SubscriptionActions : ISubscriptionActions
        {
            private readonly DocumentDatabase _database;
            private readonly List<PutSubscriptionCommand> _subscriptionCommands = new List<PutSubscriptionCommand>();

            public SubscriptionActions(DocumentDatabase database)
            {
                _database = database;
            }

            public async ValueTask DisposeAsync()
            {
                if (_subscriptionCommands.Count == 0)
                    return;

                await SendCommandsAsync();
            }

            public async ValueTask WriteSubscriptionAsync(SubscriptionState subscriptionState)
            {
                const int batchSize = 1024;

                _subscriptionCommands.Add(new PutSubscriptionCommand(_database.Name, subscriptionState.Query, null, RaftIdGenerator.DontCareId)
                {
                    SubscriptionName = subscriptionState.SubscriptionName,
                    //After restore/export , subscription will start from the start
                    InitialChangeVector = null
                });

                if (_subscriptionCommands.Count < batchSize)
                    return;

                await SendCommandsAsync();
            }

            private async ValueTask SendCommandsAsync()
            {
                await _database.ServerStore.SendToLeaderAsync(new PutSubscriptionBatchCommand(_subscriptionCommands, RaftIdGenerator.DontCareId));
                _subscriptionCommands.Clear();
            }
        }

        private class ReplicationHubCertificateActions : IReplicationHubCertificateActions
        {
            private readonly DocumentDatabase _database;
            private readonly List<RegisterReplicationHubAccessCommand> _commands = new List<RegisterReplicationHubAccessCommand>();

            public ReplicationHubCertificateActions(DocumentDatabase database)
            {
                _database = database;
            }

            public async ValueTask DisposeAsync()
            {
                if (_commands.Count == 0)
                    return;

                await SendCommandsAsync();
            }

            public async ValueTask WriteReplicationHubCertificateAsync(string hub, ReplicationHubAccess access)
            {
                const int batchSize = 128;

                byte[] buffer = Convert.FromBase64String(access.CertificateBase64);
                using var cert = CertificateLoaderUtil.CreateCertificate(buffer);

                _commands.Add(new RegisterReplicationHubAccessCommand(_database.Name, hub, access, cert, RaftIdGenerator.DontCareId));

                if (_commands.Count < batchSize)
                    return;

                await SendCommandsAsync();
            }

            private async ValueTask SendCommandsAsync()
            {
                await _database.ServerStore.SendToLeaderAsync(new BulkRegisterReplicationHubAccessCommand
                {
                    Commands = _commands,
                    Database = _database.Name,
                    UniqueRequestId = RaftIdGenerator.DontCareId
                });

                _commands.Clear();
            }
        }

        private class TimeSeriesActions : ITimeSeriesActions
        {
            private readonly DocumentDatabase _database;
            private TimeSeriesHandler.SmugglerTimeSeriesBatchCommand _cmd;
            private TimeSeriesHandler.SmugglerTimeSeriesBatchCommand _prevCommand;
            private Task _prevCommandTask = Task.CompletedTask;
            private Size _segmentsSize;
            private readonly Size _maxBatchSize;

            public TimeSeriesActions(DocumentDatabase database)
            {
                _database = database;
                _cmd = new TimeSeriesHandler.SmugglerTimeSeriesBatchCommand(database);

                _maxBatchSize = new Size(
                    PlatformDetails.Is32Bits || database.Configuration.Storage.ForceUsing32BitsPager
                        ? 1
                        : 16,
                    SizeUnit.Megabytes);

                _segmentsSize = new Size();
            }

            private void AddToBatch(TimeSeriesItem item)
            {
                if (_cmd.AddToDictionary(item))
                {
                    // RavenDB-19504 - if we have a lot of _small_ updates, that can add up quickly, but it won't 
                    // be accounted for that if we look at segment size alone. So we assume that any new item means
                    // updating the whole segment. This is especially important for encrypted databases, where we need
                    // to keep all the modified data in memory in one shot
                    _segmentsSize.Add(2, SizeUnit.Kilobytes);
                }
                _segmentsSize.Add(item.Segment.NumberOfBytes, SizeUnit.Bytes);
            }

            public async ValueTask DisposeAsync()
            {
                await FinishBatchOfTimeSeriesAsync();
            }

            public async ValueTask WriteTimeSeriesAsync(TimeSeriesItem ts)
            {
                AddToBatch(ts);
                await HandleBatchOfTimeSeriesIfNecessaryAsync();
            }

            private async ValueTask HandleBatchOfTimeSeriesIfNecessaryAsync()
            {
                if (_segmentsSize < _maxBatchSize)
                    return;

                var prevCommand = _prevCommand;
                var prevCommandTask = _prevCommandTask;

                var commandTask = _database.TxMerger.Enqueue(_cmd);

                _prevCommand = _cmd;
                _prevCommandTask = commandTask;

                if (prevCommand != null)
                {
                    await prevCommandTask;
                }

                _cmd = new TimeSeriesHandler.SmugglerTimeSeriesBatchCommand(_database);

                _segmentsSize.Set(0, SizeUnit.Bytes);
            }

            private async ValueTask FinishBatchOfTimeSeriesAsync()
            {
                if (_prevCommand != null)
                {
                    await _prevCommandTask;
                    _prevCommand = null;
                }

                if (_segmentsSize.GetValue(SizeUnit.Bytes) > 0)
                {
                    await _database.TxMerger.Enqueue(_cmd);
                }

                _cmd = null;
            }
        }
    }
}
