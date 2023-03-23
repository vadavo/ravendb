﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Client.Documents.Attachments;
using Raven.Client.Documents.Operations.Attachments;
using Raven.Client.Documents.Operations.Replication;
using Raven.Client.Documents.Replication;
using Raven.Client.Documents.Replication.Messages;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Documents;
using Raven.Client.Extensions;
using Raven.Client.ServerWide.Tcp;
using Raven.Client.Util;
using Raven.Server.Documents.Handlers;
using Raven.Server.Documents.Replication.ReplicationItems;
using Raven.Server.Documents.TcpHandlers;
using Raven.Server.Documents.TimeSeries;
using Raven.Server.Exceptions;
using Raven.Server.NotificationCenter.Notifications;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Sparrow;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Sparrow.Json.Sync;
using Sparrow.Logging;
using Sparrow.Platform;
using Sparrow.Server;
using Sparrow.Server.Json.Sync;
using Sparrow.Threading;
using Sparrow.Utils;
using Voron;
using Reader = Raven.Server.Documents.Replication.ReplicationItems.Reader;
using Size = Sparrow.Size;

namespace Raven.Server.Documents.Replication
{
    public class IncomingReplicationHandler : IDisposable
    {
        private readonly DocumentDatabase _database;
        private readonly TcpClient _tcpClient;
        private readonly Stream _stream;
        private readonly ReplicationLoader _parent;
        private PoolOfThreads.LongRunningWork _incomingWork;
        private readonly CancellationTokenSource _cts;
        private readonly Logger _log;

        public event Action<IncomingReplicationHandler, Exception> Failed;

        public event Action<IncomingReplicationHandler> DocumentsReceived;
        
        public event Action<IncomingReplicationHandler,int> AttachmentStreamsReceived;

        public event Action<LiveReplicationPulsesCollector.ReplicationPulse> HandleReplicationPulse;

        public void ClearEvents()
        {
            Failed = null;
            DocumentsReceived = null;
            HandleReplicationPulse = null;
        }

        public long LastDocumentEtag => _lastDocumentEtag;
        public long LastHeartbeatTicks;

        private readonly ConcurrentQueue<IncomingReplicationStatsAggregator> _lastReplicationStats = new ConcurrentQueue<IncomingReplicationStatsAggregator>();

        private IncomingReplicationStatsAggregator _lastStats;

        public readonly ReplicationLoader.PullReplicationParams _incomingPullReplicationParams;

        private readonly bool _preventIncomingSinkDeletions;
        public bool PullReplication => _incomingPullReplicationParams.Name != null;

        private readonly DisposeOnce<SingleAttempt> _disposeOnce;

        public readonly ReplicationLatestEtagRequest.ReplicationType ReplicationType;

        public IncomingReplicationHandler(
            TcpConnectionOptions options,
            ReplicationLatestEtagRequest replicatedLastEtag,
            ReplicationLoader parent,
            JsonOperationContext.MemoryBuffer bufferToCopy,
            ReplicationLatestEtagRequest.ReplicationType replicationType,
            ReplicationLoader.PullReplicationParams pullReplicationParams = null)
        {
            if (pullReplicationParams?.AllowedPaths != null && pullReplicationParams.AllowedPaths.Length > 0)
                _allowedPathsValidator = new AllowedPathsValidator(pullReplicationParams.AllowedPaths);

            _disposeOnce = new DisposeOnce<SingleAttempt>(DisposeInternal);

            _connectionOptions = options;
            ConnectionInfo = IncomingConnectionInfo.FromGetLatestEtag(replicatedLastEtag);

            _database = options.DocumentDatabase;

            _replicationFromAnotherSource = new AsyncManualResetEvent(_database.DatabaseShutdown);

            _tcpClient = options.TcpClient;
            _stream = options.Stream;
            SupportedFeatures = TcpConnectionHeaderMessage.GetSupportedFeaturesFor(options.Operation, options.ProtocolVersion);
            ConnectionInfo.RemoteIp = ((IPEndPoint)_tcpClient.Client.RemoteEndPoint).Address.ToString();
            _parent = parent;

            ReplicationType = replicationType;

            _incomingPullReplicationParams = new ReplicationLoader.PullReplicationParams
            {
                AllowedPaths = pullReplicationParams?.AllowedPaths,
                Mode = pullReplicationParams?.Mode ?? PullReplicationMode.None,
                Name = pullReplicationParams?.Name,
                PreventDeletionsMode = pullReplicationParams?.PreventDeletionsMode,
                Type = ReplicationLoader.PullReplicationParams.ConnectionType.Incoming
            };

            _preventIncomingSinkDeletions = _incomingPullReplicationParams.PreventDeletionsMode?.HasFlag(PreventDeletionsMode.PreventSinkToHubDeletions) == true &&
                                                _incomingPullReplicationParams.Mode == PullReplicationMode.SinkToHub;


            CertificateThumbprint = options.Certificate?.Thumbprint;

            _log = LoggingSource.Instance.GetLogger<IncomingReplicationHandler>(_database.Name);
            _cts = CancellationTokenSource.CreateLinkedTokenSource(_database.DatabaseShutdown);

            _conflictManager = new ConflictManager(_database, _parent.ConflictResolver);

            _attachmentStreamsTempFile = _database.DocumentsStorage.AttachmentsStorage.GetTempFile("replication");
            _copiedBuffer = bufferToCopy.Clone(_connectionOptions.ContextPool);

            LastHeartbeatTicks = _database.Time.GetUtcNow().Ticks;
        }

        public IncomingReplicationPerformanceStats[] GetReplicationPerformance()
        {
            var lastStats = _lastStats;

            return _lastReplicationStats
                .Select(x => x == lastStats ? x.ToReplicationPerformanceLiveStatsWithDetails() : x.ToReplicationPerformanceStats())
                .ToArray();
        }

        public IncomingReplicationStatsAggregator GetLatestReplicationPerformance()
        {
            return _lastStats;
        }

        private string IncomingReplicationThreadName => $"Incoming replication {FromToString}";

        public void Start()
        {
            if (_incomingWork != null)
                return;

            lock (this)
            {
                if (_incomingWork != null)
                    return; // already set by someone else, they can start it

                _incomingWork = PoolOfThreads.GlobalRavenThreadPool.LongRunning(x => { DoIncomingReplication(); }, null, IncomingReplicationThreadName);
            }

            if (_log.IsInfoEnabled)
                _log.Info($"Incoming replication thread started ({FromToString})");
        }

        public void DoIncomingReplication()
        {
            try
            {
                ReceiveReplicationBatches();
            }
            catch (Exception e)
            {
                if (_log.IsInfoEnabled)
                    _log.Info($"Error in accepting replication request ({FromToString})", e);
            }
        }

        [ThreadStatic]
        public static bool IsIncomingReplication;

        static IncomingReplicationHandler()
        {
            ThreadLocalCleanup.ReleaseThreadLocalState += () => IsIncomingReplication = false;
        }

        private readonly AsyncManualResetEvent _replicationFromAnotherSource;

        public void OnReplicationFromAnotherSource()
        {
            _replicationFromAnotherSource.Set();
        }

        private void ReceiveReplicationBatches()
        {
            NativeMemory.EnsureRegistered();
            try
            {
                _parent.ForTestingPurposes?.OnIncomingReplicationHandlerStart?.Invoke();
                using (_connectionOptionsDisposable = _connectionOptions.ConnectionProcessingInProgress("Replication"))
                using (_stream)
                using (var interruptibleRead = new InterruptibleRead(_database.DocumentsStorage.ContextPool, _stream))
                {
                    while (_cts.IsCancellationRequested == false)
                    {
                        try
                        {
                            AddReplicationPulse(ReplicationPulseDirection.IncomingInitiate);

                            using (var msg = interruptibleRead.ParseToMemory(
                                _replicationFromAnotherSource,
                                "IncomingReplication/read-message",
                                Timeout.Infinite,
                                _copiedBuffer.Buffer,
                                _database.DatabaseShutdown))
                            {
                                if (msg.Document != null)
                                {
                                    _parent.EnsureNotDeleted(_parent._server.NodeTag);

                                    using (var writer = new BlittableJsonTextWriter(msg.Context, _stream))
                                    {
                                        HandleSingleReplicationBatch(msg.Context,
                                            msg.Document,
                                            writer);
                                    }
                                }
                                else // notify peer about new change vector
                                {
                                    using (_database.DocumentsStorage.ContextPool.AllocateOperationContext(
                                            out DocumentsOperationContext documentsContext))
                                    using (var writer = new BlittableJsonTextWriter(documentsContext, _stream))
                                    {
                                        SendHeartbeatStatusToSource(
                                            documentsContext,
                                            writer,
                                            _lastDocumentEtag,
                                            "Notify");
                                    }
                                }
                                // we reset it after every time we send to the remote server
                                // because that is when we know that it is up to date with our
                                // status, so no need to send again
                                _replicationFromAnotherSource.Reset();
                            }
                        }
                        catch (Exception e)
                        {
                            AddReplicationPulse(ReplicationPulseDirection.IncomingInitiateError, e.Message);

                            if (_log.IsInfoEnabled)
                            {
                                if (e is AggregateException ae &&
                                    ae.InnerExceptions.Count == 1 &&
                                    ae.InnerException is SocketException ase)
                                {
                                    HandleSocketException(ase);
                                }
                                else if (e.InnerException is SocketException se)
                                {
                                    HandleSocketException(se);
                                }
                                else
                                {
                                    //if we are disposing, do not notify about failure (not relevant)
                                    if (_cts.IsCancellationRequested == false)
                                        if (_log.IsInfoEnabled)
                                            _log.Info("Received unexpected exception while receiving replication batch.", e);
                                }
                            }

                            throw;
                        }

                        void HandleSocketException(SocketException e)
                        {
                            if (_log.IsInfoEnabled)
                                _log.Info("Failed to read data from incoming connection. The incoming connection will be closed and re-created.", e);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                //if we are disposing, do not notify about failure (not relevant)
                if (_cts.IsCancellationRequested == false)
                {
                    if (_log.IsInfoEnabled)
                        _log.Info($"Connection error {FromToString}: an exception was thrown during receiving incoming document replication batch.", e);


                    _parent.ForTestingPurposes?.OnIncomingReplicationHandlerFailure?.Invoke(e);


                    OnFailed(e, this);
                }
            }
        }

        private Task _prevChangeVectorUpdate;

        private void HandleSingleReplicationBatch(
            DocumentsOperationContext documentsContext,
            BlittableJsonReaderObject message,
            BlittableJsonTextWriter writer)
        {
            message.BlittableValidation();
            //note: at this point, the valid messages are heartbeat and replication batch.
            _cts.Token.ThrowIfCancellationRequested();
            string messageType = null;
            try
            {
                if (!message.TryGet(nameof(ReplicationMessageHeader.Type), out messageType))
                    throw new InvalidDataException("Expected the message to have a 'Type' field. The property was not found");

                if (!message.TryGet(nameof(ReplicationMessageHeader.LastDocumentEtag), out _lastDocumentEtag))
                    throw new InvalidOperationException("Expected LastDocumentEtag property in the replication message, " +
                                                        "but didn't find it..");

                switch (messageType)
                {
                    case ReplicationMessageType.Documents:
                        AddReplicationPulse(ReplicationPulseDirection.IncomingBegin);

                        var stats = _lastStats = new IncomingReplicationStatsAggregator(_parent.GetNextReplicationStatsId(), _lastStats);
                        AddReplicationPerformance(stats);

                        try
                        {
                            using (var scope = stats.CreateScope())
                            {
                                try
                                {
                                    scope.RecordLastEtag(_lastDocumentEtag);

                                    HandleReceivedDocumentsAndAttachmentsBatch(documentsContext, message, _lastDocumentEtag, scope);
                                    break;
                                }
                                catch (Exception e)
                                {
                                    AddReplicationPulse(ReplicationPulseDirection.IncomingError, e.Message);
                                    scope.AddError(e);
                                    throw;
                                }
                            }
                        }
                        finally
                        {
                            AddReplicationPulse(ReplicationPulseDirection.IncomingEnd);
                            stats.Complete();
                        }
                    case ReplicationMessageType.Heartbeat:
                        AddReplicationPulse(ReplicationPulseDirection.IncomingHeartbeat);
                        if (message.TryGet(nameof(ReplicationMessageHeader.DatabaseChangeVector), out string changeVector))
                        {
                            // saving the change vector and the last received document etag
                            long lastEtag;
                            string lastChangeVector;
                            using (documentsContext.OpenReadTransaction())
                            {
                                lastEtag = DocumentsStorage.GetLastReplicatedEtagFrom(documentsContext, ConnectionInfo.SourceDatabaseId);
                                lastChangeVector = DocumentsStorage.GetDatabaseChangeVector(documentsContext);
                            }

                            var status = ChangeVectorUtils.GetConflictStatus(changeVector, lastChangeVector);
                            if (status == ConflictStatus.Update || _lastDocumentEtag > lastEtag)
                            {
                                if (_log.IsInfoEnabled)
                                {
                                    _log.Info(
                                        $"Try to update the current database change vector ({lastChangeVector}) with {changeVector} in status {status}" +
                                        $"with etag: {_lastDocumentEtag} (new) > {lastEtag} (old)");
                                }

                                var cmd = new MergedUpdateDatabaseChangeVectorCommand(changeVector, _lastDocumentEtag, ConnectionInfo.SourceDatabaseId,
                                    _replicationFromAnotherSource, _incomingPullReplicationParams.Mode == PullReplicationMode.SinkToHub);

                                if (_prevChangeVectorUpdate != null && _prevChangeVectorUpdate.IsCompleted == false)
                                {
                                    if (_log.IsInfoEnabled)
                                    {
                                        _log.Info(
                                            $"The previous task of updating the database change vector was not completed and has the status of {_prevChangeVectorUpdate.Status}, " +
                                            "nevertheless we create an additional task.");
                                    }
                                }
                                else
                                {
                                    _prevChangeVectorUpdate = _database.TxMerger.Enqueue(cmd);
                                }
                            }
                        }

                        break;

                    default:
                        throw new ArgumentOutOfRangeException("Unknown message type: " + messageType);
                }

                SendHeartbeatStatusToSource(documentsContext, writer, _lastDocumentEtag, messageType);
            }
            catch (ObjectDisposedException)
            {
                //we are shutting down replication, this is ok
            }
            catch (EndOfStreamException e)
            {
                if (_log.IsInfoEnabled)
                    _log.Info("Received unexpected end of stream while receiving replication batches. " +
                              "This might indicate an issue with network.", e);
                throw;
            }
            catch (Exception e)
            {
                //if we are disposing, ignore errors
                if (_cts.IsCancellationRequested)
                    return;

                DynamicJsonValue returnValue;

                if (e.ExtractSingleInnerException() is MissingAttachmentException mae)
                {
                    returnValue = new DynamicJsonValue
                    {
                        [nameof(ReplicationMessageReply.Type)] = ReplicationMessageReply.ReplyType.MissingAttachments.ToString(),
                        [nameof(ReplicationMessageReply.MessageType)] = messageType,
                        [nameof(ReplicationMessageReply.LastEtagAccepted)] = -1,
                        [nameof(ReplicationMessageReply.Exception)] = mae.ToString()
                    };

                    documentsContext.Write(writer, returnValue);
                    writer.Flush();

                    return;
                }

                if (_log.IsInfoEnabled)
                    _log.Info($"Failed replicating documents {FromToString}.", e);

                //return negative ack
                returnValue = new DynamicJsonValue
                {
                    [nameof(ReplicationMessageReply.Type)] = ReplicationMessageReply.ReplyType.Error.ToString(),
                    [nameof(ReplicationMessageReply.MessageType)] = messageType,
                    [nameof(ReplicationMessageReply.LastEtagAccepted)] = -1,
                    [nameof(ReplicationMessageReply.Exception)] = e.ToString()
                };

                documentsContext.Write(writer, returnValue);
                writer.Flush();

                throw;
            }
        }

        private void HandleReceivedDocumentsAndAttachmentsBatch(DocumentsOperationContext documentsContext, BlittableJsonReaderObject message, long lastDocumentEtag, IncomingReplicationStatsScope stats)
        {
            if (!message.TryGet(nameof(ReplicationMessageHeader.ItemsCount), out int itemsCount))
                throw new InvalidDataException($"Expected the '{nameof(ReplicationMessageHeader.ItemsCount)}' field, " +
                                               $"but had no numeric field of this value, this is likely a bug");

            if (!message.TryGet(nameof(ReplicationMessageHeader.AttachmentStreamsCount), out int attachmentStreamCount))
                throw new InvalidDataException($"Expected the '{nameof(ReplicationMessageHeader.AttachmentStreamsCount)}' field, " +
                                               $"but had no numeric field of this value, this is likely a bug");

            ReceiveSingleDocumentsBatch(documentsContext, itemsCount, attachmentStreamCount, lastDocumentEtag, stats);

            AttachmentStreamsReceived?.Invoke(this, attachmentStreamCount);

            OnDocumentsReceived(this);
        }

        public class DataForReplicationCommand : IDisposable
        {
            internal DocumentDatabase DocumentDatabase { get; set; }

            internal ConflictManager ConflictManager { get; set; }

            internal string SourceDatabaseId { get; set; }

            internal ReplicationBatchItem[] ReplicatedItems { get; set; }

            internal Dictionary<Slice, AttachmentReplicationItem> ReplicatedAttachmentStreams { get; set; }

            public TcpConnectionHeaderMessage.SupportedFeatures SupportedFeatures { get; set; }

            public Logger Logger { get; set; }

            public void Dispose()
            {
                if (ReplicatedAttachmentStreams != null)
                {
                    foreach (var item in ReplicatedAttachmentStreams.Values)
                    {
                        item?.Dispose();
                    }

                    ReplicatedAttachmentStreams?.Clear();
                }

                if (ReplicatedItems != null)
                {
                    foreach (var item in ReplicatedItems)
                    {
                        item?.Dispose();
                    }
                }

                ReplicatedItems = null;
            }
        }

        private void ReceiveSingleDocumentsBatch(DocumentsOperationContext documentsContext, int replicatedItemsCount, int attachmentStreamCount, long lastEtag, IncomingReplicationStatsScope stats)
        {
            if (_log.IsInfoEnabled)
            {
                _log.Info($"Receiving replication batch with {replicatedItemsCount} documents starting with {lastEtag} from {ConnectionInfo}");
            }

            var sw = Stopwatch.StartNew();
            Task task = null;

            using (_attachmentStreamsTempFile.Scope())
            using (var incomingReplicationAllocator = new IncomingReplicationAllocator(documentsContext, _database))
            using (var dataForReplicationCommand = new DataForReplicationCommand
            {
                DocumentDatabase = _database,
                ConflictManager = _conflictManager,
                SourceDatabaseId = ConnectionInfo.SourceDatabaseId,
                SupportedFeatures = SupportedFeatures,
                Logger = _log
            })
            {
                try
                {
                    using (var networkStats = stats.For(ReplicationOperation.Incoming.Network))
                    {
                        // this will read the documents to memory from the network
                        // without holding the write tx open
                        var reader = new Reader(_stream, _copiedBuffer, incomingReplicationAllocator);

                        ReadItemsFromSource(replicatedItemsCount, documentsContext, dataForReplicationCommand, reader, networkStats);
                        ReadAttachmentStreamsFromSource(attachmentStreamCount, documentsContext, dataForReplicationCommand, reader, networkStats);
                    }

                    _parent._server.DatabasesLandlord.ForTestingPurposes?.DelayIncomingReplication?.Invoke(_cts.Token);

                    if (_allowedPathsValidator != null || _preventIncomingSinkDeletions)
                    {
                        // if the other side sends us any information that we shouldn't get from them,
                        // we abort the connection and send an error back
                        ValidateIncomingReplicationItemsPaths(dataForReplicationCommand);
                    }

                    if (_log.IsInfoEnabled)
                    {
                        _log.Info(
                            $"Replication connection {FromToString}: " +
                            $"received {replicatedItemsCount:#,#;;0} items, " +
                            $"{attachmentStreamCount:#,#;;0} attachment streams, " +
                            $"total size: {new Size(incomingReplicationAllocator.TotalDocumentsSizeInBytes, SizeUnit.Bytes)}, " +
                            $"took: {sw.ElapsedMilliseconds:#,#;;0}ms");
                    }

                    _connectionOptions._lastEtagReceived = _lastDocumentEtag;
                    _connectionOptions.RegisterBytesReceived(incomingReplicationAllocator.TotalDocumentsSizeInBytes);

                    using (stats.For(ReplicationOperation.Incoming.Storage))
                    {
                        var replicationCommand = new MergedDocumentReplicationCommand(dataForReplicationCommand, lastEtag, _incomingPullReplicationParams.Mode);
                        replicationCommand.BeforeSendingToTxMerger(_incomingPullReplicationParams?.PreventDeletionsMode, documentsContext);
                        task = _database.TxMerger.Enqueue(replicationCommand);
                        //We need a new context here
                        using (_database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext msgContext))
                        using (var writer = new BlittableJsonTextWriter(msgContext, _stream))
                        using (var msg = msgContext.ReadObject(new DynamicJsonValue
                        {
                            [nameof(ReplicationMessageReply.MessageType)] = "Processing"
                        }, "heartbeat message"))
                        {
                            while (task.Wait(Math.Min(3000, (int)(_database.Configuration.Replication.ActiveConnectionTimeout.AsTimeSpan.TotalMilliseconds * 2 / 3))) ==
                                   false)
                            {
                                // send heartbeats while batch is processed in TxMerger. We wait until merger finishes with this command without timeouts
                                msgContext.Write(writer, msg);
                                writer.Flush();
                            }

                            task = null;
                        }
                    }

                    sw.Stop();

                    if (_log.IsInfoEnabled)
                        _log.Info($"Replication connection {FromToString}: " +
                                  $"received and written {replicatedItemsCount:#,#;;0} items to database in {sw.ElapsedMilliseconds:#,#;;0}ms, " +
                                  $"with last etag = {lastEtag}.");
                }
                catch (Exception e)
                {
                    if (_log.IsInfoEnabled)
                    {
                        //This is the case where we had a missing attachment, it is rare but expected.
                        if (e.ExtractSingleInnerException() is MissingAttachmentException mae)
                        {
                            _log.Info("Replication batch contained missing attachments will request the batch to be re-sent with those attachments.", mae);
                        }
                        else
                        {
                            _log.Info("Failed to receive documents replication batch. This is not supposed to happen, and is likely a bug.", e);
                        }
                    }
                    throw;
                }
                finally
                {
                    // before we dispose the buffer we must ensure it is not being processed in TxMerger, so we wait for it
                    try
                    {
                        task?.Wait();
                    }
                    catch (Exception)
                    {
                        // ignore this failure, if this failed, we are already
                        // in a bad state and likely in the process of shutting
                        // down
                    }
                }
            }
        }

        private void ValidateIncomingReplicationItemsPaths(DataForReplicationCommand dataForReplicationCommand)
        {
            HashSet<Slice> expectedAttachmentStreams = null;

            foreach (var item in dataForReplicationCommand.ReplicatedItems)
            {
                if (_allowedPathsValidator != null)
                {
                if (_allowedPathsValidator.ShouldAllow(item) == false)
                {
                    throw new InvalidOperationException("Attempted to replicate " + _allowedPathsValidator.GetItemInformation(item) +
                                                        ", which is not allowed, according to the allowed paths policy. Replication aborted");
                }

                switch (item)
                {
                    case AttachmentReplicationItem a:
                        expectedAttachmentStreams ??= new HashSet<Slice>(SliceComparer.Instance);
                        expectedAttachmentStreams.Add(a.Key);
                        break;
                }
            }

                if (_preventIncomingSinkDeletions)
                {
                    if (ReplicationLoader.IsOfTypePreventDeletions(item))
                    {
                        using (var infoHelper = new DocumentInfoHelper())
                        {
                            throw new InvalidOperationException(
                                $"This hub does not allow for tombstone replication via pull replication '{_incomingPullReplicationParams.Name}'." +
                                $" Replication of item '{infoHelper.GetItemInformation(item)}' has been aborted for sink connection: '{this.ConnectionInfo.ToString()}'.");
                        }
                    }
                }
            }

            if (dataForReplicationCommand.ReplicatedAttachmentStreams != null)
            {
                foreach (var kvp in dataForReplicationCommand.ReplicatedAttachmentStreams)
                {
                    if (expectedAttachmentStreams == null || expectedAttachmentStreams.Contains(kvp.Key))
                    {
                        throw new InvalidOperationException("Attempted to attachment with hash: " + kvp.Key +
                                                            ", but without a matching attachment key.");
                    }
                }
            }
        }

        private void SendHeartbeatStatusToSource(DocumentsOperationContext documentsContext, BlittableJsonTextWriter writer, long lastDocumentEtag, string handledMessageType)
        {
            AddReplicationPulse(ReplicationPulseDirection.IncomingHeartbeatAcknowledge);

            string databaseChangeVector;
            long currentLastEtagMatchingChangeVector;

            using (documentsContext.OpenReadTransaction())
            {
                // we need to get both of them in a transaction, the other side will check if its known change vector
                // is the same or higher then ours, and if so, we'll update the change vector on the sibling to reflect
                // our own latest etag. This allows us to have effective synchronization points, since each change will
                // be able to tell (roughly) where it is at on the entire cluster.
                databaseChangeVector = DocumentsStorage.GetDatabaseChangeVector(documentsContext);
                currentLastEtagMatchingChangeVector = DocumentsStorage.ReadLastEtag(documentsContext.Transaction.InnerTransaction);
            }
            if (_log.IsInfoEnabled)
            {
                _log.Info($"Sending heartbeat ok => {FromToString} with last document etag = {lastDocumentEtag}, " +
                          $"last document change vector: {databaseChangeVector}");
            }
            var heartbeat = new DynamicJsonValue
            {
                [nameof(ReplicationMessageReply.Type)] = "Ok",
                [nameof(ReplicationMessageReply.MessageType)] = handledMessageType,
                [nameof(ReplicationMessageReply.LastEtagAccepted)] = lastDocumentEtag,
                [nameof(ReplicationMessageReply.CurrentEtag)] = currentLastEtagMatchingChangeVector,
                [nameof(ReplicationMessageReply.Exception)] = null,
                [nameof(ReplicationMessageReply.DatabaseChangeVector)] = databaseChangeVector,
                [nameof(ReplicationMessageReply.DatabaseId)] = _database.DbId.ToString(),
                [nameof(ReplicationMessageReply.NodeTag)] = _parent._server.NodeTag
            };

            documentsContext.Write(writer, heartbeat);

            writer.Flush();
            LastHeartbeatTicks = _database.Time.GetUtcNow().Ticks;
        }

        public string SourceFormatted => $"{ConnectionInfo.SourceUrl}/databases/{ConnectionInfo.SourceDatabaseName} ({ConnectionInfo.SourceDatabaseId})";

        public string FromToString => $"In database {_database.ServerStore.NodeTag}-{_database.Name} @ {_database.ServerStore.GetNodeTcpServerUrl()} " +
                                      $"from {ConnectionInfo.SourceTag}-{ConnectionInfo.SourceDatabaseName} @ {ConnectionInfo.SourceUrl}" +
                                      $"{(_incomingPullReplicationParams?.Name == null ? null : $"(pull definition: {_incomingPullReplicationParams?.Name})")}";

        public IncomingConnectionInfo ConnectionInfo { get; }

        private readonly StreamsTempFile _attachmentStreamsTempFile;
        private long _lastDocumentEtag;
        private readonly TcpConnectionOptions _connectionOptions;
        private readonly ConflictManager _conflictManager;
        private IDisposable _connectionOptionsDisposable;
        private (IDisposable ReleaseBuffer, JsonOperationContext.MemoryBuffer Buffer) _copiedBuffer;
        private AllowedPathsValidator _allowedPathsValidator;
        public string CertificateThumbprint;
        public TcpConnectionHeaderMessage.SupportedFeatures SupportedFeatures { get; set; }

        private void ReadItemsFromSource(int replicatedDocs, DocumentsOperationContext context, DataForReplicationCommand data, Reader reader,
            IncomingReplicationStatsScope stats)
        {
            if (data.ReplicatedItems == null)
                data.ReplicatedItems = new ReplicationBatchItem[replicatedDocs];

            for (var i = 0; i < replicatedDocs; i++)
            {
                stats.RecordInputAttempt();

                var item = ReplicationBatchItem.ReadTypeAndInstantiate(reader);
                item.ReadChangeVectorAndMarker();
                item.Read(context, stats);
                data.ReplicatedItems[i] = item;
            }
        }

        public unsafe class IncomingReplicationAllocator : IDisposable
        {
            private readonly DocumentsOperationContext _context;
            private readonly long _maxSizeForContextUseInBytes;
            private readonly long _minSizeToAllocateNonContextUseInBytes;
            public long TotalDocumentsSizeInBytes { get; private set; }

            private List<Allocation> _nativeAllocationList;
            private Allocation _currentAllocation;

            public IncomingReplicationAllocator(DocumentsOperationContext context, DocumentDatabase database)
            {
                _context = context;

                var maxSizeForContextUse = database.Configuration.Replication.MaxSizeToSend * 2 ??
                              new Size(128, SizeUnit.Megabytes);

                _maxSizeForContextUseInBytes = maxSizeForContextUse.GetValue(SizeUnit.Bytes);
                var minSizeToNonContextAllocationInMb = PlatformDetails.Is32Bits ? 4 : 16;
                _minSizeToAllocateNonContextUseInBytes = new Size(minSizeToNonContextAllocationInMb, SizeUnit.Megabytes).GetValue(SizeUnit.Bytes);
            }

            public byte* AllocateMemory(int size)
            {
                TotalDocumentsSizeInBytes += size;
                if (TotalDocumentsSizeInBytes <= _maxSizeForContextUseInBytes)
                {
                    _context.Allocator.Allocate(size, out var output);
                    return output.Ptr;
                }

                if (_currentAllocation == null || _currentAllocation.Free < size)
                {
                    // first allocation or we don't have enough space on the currently allocated chunk

                    // there can be a document that is larger than the minimum
                    var sizeToAllocate = Math.Max(size, _minSizeToAllocateNonContextUseInBytes);

                    var allocation = new Allocation(sizeToAllocate);
                    if (_nativeAllocationList == null)
                        _nativeAllocationList = new List<Allocation>();

                    _nativeAllocationList.Add(allocation);
                    _currentAllocation = allocation;
                }

                return _currentAllocation.GetMemory(size);
            }

            public void Dispose()
            {
                if (_nativeAllocationList == null)
                    return;

                foreach (var allocation in _nativeAllocationList)
                {
                    allocation.Dispose();
                }
            }

            private class Allocation : IDisposable
            {
                private readonly byte* _ptr;
                private readonly long _allocationSize;
                private readonly NativeMemory.ThreadStats _threadStats;
                private long _used;
                public long Free => _allocationSize - _used;

                public Allocation(long allocationSize)
                {
                    _ptr = NativeMemory.AllocateMemory(allocationSize, out var threadStats);
                    _allocationSize = allocationSize;
                    _threadStats = threadStats;
                }

                public byte* GetMemory(long size)
                {
                    ThrowOnPointerOutOfRange(size);

                    var mem = _ptr + _used;
                    _used += size;
                    return mem;
                }

                [Conditional("DEBUG")]
                private void ThrowOnPointerOutOfRange(long size)
                {
                    if (_used + size > _allocationSize)
                        throw new InvalidOperationException(
                            $"Not enough space to allocate the requested size: {new Size(size, SizeUnit.Bytes)}, " +
                            $"used: {new Size(_used, SizeUnit.Bytes)}, " +
                            $"total allocation size: {new Size(_allocationSize, SizeUnit.Bytes)}");
                }

                public void Dispose()
                {
                    NativeMemory.Free(_ptr, _allocationSize, _threadStats);
                }
            }
        }

        private void ReadAttachmentStreamsFromSource(int attachmentStreamCount,
            DocumentsOperationContext context, DataForReplicationCommand dataForReplicationCommand, Reader reader, IncomingReplicationStatsScope stats)
        {
            if (attachmentStreamCount == 0)
                return;

            var replicatedAttachmentStreams = new Dictionary<Slice, AttachmentReplicationItem>(SliceComparer.Instance);

            for (var i = 0; i < attachmentStreamCount; i++)
            {
                var attachment = (AttachmentReplicationItem)ReplicationBatchItem.ReadTypeAndInstantiate(reader);
                Debug.Assert(attachment.Type == ReplicationBatchItem.ReplicationItemType.AttachmentStream);

                using (stats.For(ReplicationOperation.Incoming.AttachmentRead))
                {
                    attachment.ReadStream(context, _attachmentStreamsTempFile);
                    replicatedAttachmentStreams[attachment.Base64Hash] = attachment;
                }
            }

            dataForReplicationCommand.ReplicatedAttachmentStreams = replicatedAttachmentStreams;
        }

        private void AddReplicationPulse(ReplicationPulseDirection direction, string exceptionMessage = null)
        {
            HandleReplicationPulse?.Invoke(new LiveReplicationPulsesCollector.ReplicationPulse
            {
                OccurredAt = SystemTime.UtcNow,
                Direction = direction,
                From = ConnectionInfo,
                ExceptionMessage = exceptionMessage
            });
        }

        private void AddReplicationPerformance(IncomingReplicationStatsAggregator stats)
        {
            _lastReplicationStats.Enqueue(stats);

            while (_lastReplicationStats.Count > 25)
                _lastReplicationStats.TryDequeue(out stats);
        }

        public LiveReplicationPerformanceCollector.ReplicationPerformanceType GetReplicationPerformanceType()
        {
            return ReplicationType == ReplicationLatestEtagRequest.ReplicationType.Internal
                ? LiveReplicationPerformanceCollector.ReplicationPerformanceType.IncomingInternal
                : LiveReplicationPerformanceCollector.ReplicationPerformanceType.IncomingExternal;
        }

        public bool IsDisposed => _disposeOnce.Disposed;

        public void Dispose()
        {
            _disposeOnce.Dispose();
            if (_incomingWork != PoolOfThreads.LongRunningWork.Current)
            {
                try
                {
                    _incomingWork?.Join(int.MaxValue);
                }
                catch (ThreadStateException)
                {
                    // expected if the thread hasn't been started yet
                }
            }
            _incomingWork = null;
        }

        private void DisposeInternal()
        {
            var releaser = _copiedBuffer.ReleaseBuffer;
            try
            {
                if (_log.IsInfoEnabled)
                    _log.Info($"Disposing IncomingReplicationHandler ({FromToString})");
                _cts.Cancel();
                try
                {
                    _connectionOptionsDisposable?.Dispose();
                }
                catch (Exception)
                {
                }
                try
                {
                    _stream.Dispose();
                }
                catch (Exception)
                {
                }
                try
                {
                    _tcpClient.Dispose();
                }
                catch (Exception)
                {
                }

                try
                {
                    _connectionOptions.Dispose();
                }
                catch
                {
                    // do nothing
                }

                _replicationFromAnotherSource.Set();

                try
                {
                    _allowedPathsValidator?.Dispose();
                }
                catch
                {
                    // nothing to do
                }
                _cts.Dispose();

                _attachmentStreamsTempFile.Dispose();
                _replicationFromAnotherSource.Dispose();
            }
            finally
            {
                try
                {
                    releaser?.Dispose();
                }
                catch (Exception)
                {
                    // can't do anything about it...
                }
            }
        }

        protected void OnFailed(Exception exception, IncomingReplicationHandler instance) => Failed?.Invoke(instance, exception);

        protected void OnDocumentsReceived(IncomingReplicationHandler instance) => DocumentsReceived?.Invoke(instance);

        internal class MergedUpdateDatabaseChangeVectorCommand : TransactionOperationsMerger.MergedTransactionCommand
        {
            private readonly string _changeVector;
            private readonly long _lastDocumentEtag;
            private readonly string _sourceDatabaseId;
            private readonly AsyncManualResetEvent _trigger;
            private readonly bool _isHub;

            public MergedUpdateDatabaseChangeVectorCommand(string changeVector, long lastDocumentEtag, string sourceDatabaseId, AsyncManualResetEvent trigger, bool isHub)
            {
                _changeVector = changeVector;
                _lastDocumentEtag = lastDocumentEtag;
                _sourceDatabaseId = sourceDatabaseId;
                _trigger = trigger;
                _isHub = isHub;
            }

            protected override long ExecuteCmd(DocumentsOperationContext context)
            {
                var operationsCount = 0;
                var lastReplicatedEtag = DocumentsStorage.GetLastReplicatedEtagFrom(context, _sourceDatabaseId);
                if (_lastDocumentEtag > lastReplicatedEtag)
                {
                    DocumentsStorage.SetLastReplicatedEtagFrom(context, _sourceDatabaseId, _lastDocumentEtag);
                    operationsCount++;
                }

                if (_isHub)
                    return operationsCount;

                var current = context.LastDatabaseChangeVector ?? DocumentsStorage.GetDatabaseChangeVector(context);
                var conflictStatus = ChangeVectorUtils.GetConflictStatus(_changeVector, current);
                if (conflictStatus != ConflictStatus.Update)
                    return operationsCount;

                operationsCount++;

                context.LastDatabaseChangeVector = ChangeVectorUtils.MergeVectors(current, _changeVector);
                context.Transaction.InnerTransaction.LowLevelTransaction.OnDispose += _ =>
                {
                    try
                    {
                        _trigger.Set();
                    }
                    catch
                    {
                        //
                    }
                };

                return operationsCount;
            }

            public override TransactionOperationsMerger.IReplayableCommandDto<TransactionOperationsMerger.MergedTransactionCommand> ToDto<TTransaction>(TransactionOperationContext<TTransaction> context)
            {
                return new MergedUpdateDatabaseChangeVectorCommandDto
                {
                    ChangeVector = _changeVector,
                    LastDocumentEtag = _lastDocumentEtag,
                    SourceDatabaseId = _sourceDatabaseId,
                    IsHub = _isHub
                };
            }
        }

        internal class MergedDocumentReplicationCommand : TransactionOperationsMerger.MergedTransactionCommand
        {
            private readonly long _lastEtag;
            private readonly PullReplicationMode _mode;
            private readonly DataForReplicationCommand _replicationInfo;
            private readonly bool _isHub;
            private readonly bool _isSink;

            public MergedDocumentReplicationCommand(DataForReplicationCommand replicationInfo, long lastEtag, PullReplicationMode mode)
            {
                _replicationInfo = replicationInfo;
                _lastEtag = lastEtag;
                _mode = mode;
                _isHub = mode == PullReplicationMode.SinkToHub;
                _isSink = mode == PullReplicationMode.HubToSink;
            }

            public void BeforeSendingToTxMerger(PreventDeletionsMode? preventDeletionsMode, DocumentsOperationContext ctx)
            {
                if (preventDeletionsMode?.HasFlag(PreventDeletionsMode.PreventSinkToHubDeletions) == false ||
                                      _mode != PullReplicationMode.SinkToHub)
                    return;

                foreach (var item in _replicationInfo.ReplicatedItems)
                {
                    if (item is DocumentReplicationItem doc)
                    {
                        if (doc.Data == null)
                            continue;

                        RemoveExpiresFromSinkBatchItem(doc, ctx);
                    }
                }
            }

            protected override long ExecuteCmd(DocumentsOperationContext context)
            {
                var toDispose = new List<IDisposable>();

                try
                {
                    IsIncomingReplication = true;

                    var operationsCount = 0;

                    var database = _replicationInfo.DocumentDatabase;
                    var lastTransactionMarker = 0;
                    HashSet<LazyStringValue> docCountersToRecreate = null;
                    var handledAttachmentStreams = new HashSet<Slice>(SliceComparer.Instance);
                    context.LastDatabaseChangeVector ??= DocumentsStorage.GetDatabaseChangeVector(context);

                    foreach (var item in _replicationInfo.ReplicatedItems)
                    {
                        if (lastTransactionMarker != item.TransactionMarker)
                        {
                            context.TransactionMarkerOffset++;
                            lastTransactionMarker = item.TransactionMarker;
                        }

                        operationsCount++;

                        if (_isSink)
                            ReplaceKnownSinkEntries(context, ref item.ChangeVector);

                        var changeVectorToMerge = item.ChangeVector;

                        if (_isHub)
                            changeVectorToMerge = ReplaceUnknownEntriesWithSinkTag(context, ref item.ChangeVector);

                        var rcvdChangeVector = item.ChangeVector;

                        context.LastDatabaseChangeVector = ChangeVectorUtils.MergeVectors(changeVectorToMerge, context.LastDatabaseChangeVector);

                        TimeSeriesStorage tss;
                        LazyStringValue docId;
                        LazyStringValue name;

                        switch (item)
                        {
                            case AttachmentReplicationItem attachment:

                                var localAttachment = database.DocumentsStorage.AttachmentsStorage.GetAttachmentByKey(context, attachment.Key);
                                if (_replicationInfo.ReplicatedAttachmentStreams != null && _replicationInfo.ReplicatedAttachmentStreams.TryGetValue(attachment.Base64Hash, out var attachmentStream))
                                {
                                    if (database.DocumentsStorage.AttachmentsStorage.AttachmentExists(context, attachment.Base64Hash) == false)
                                    {
                                        Debug.Assert(localAttachment == null || AttachmentsStorage.GetAttachmentTypeByKey(attachment.Key) != AttachmentType.Revision,
                                            "the stream should have been written when the revision was added by the document");
                                        database.DocumentsStorage.AttachmentsStorage.PutAttachmentStream(context, attachment.Key, attachmentStream.Base64Hash, attachmentStream.Stream);
                                    }

                                    handledAttachmentStreams.Add(attachment.Base64Hash);
                                }

                                toDispose.Add(DocumentIdWorker.GetLowerIdSliceAndStorageKey(context, attachment.Name, out _, out Slice attachmentName));
                                toDispose.Add(DocumentIdWorker.GetLowerIdSliceAndStorageKey(context, attachment.ContentType, out _, out Slice contentType));

                                if (localAttachment == null || ChangeVectorUtils.GetConflictStatus(attachment.ChangeVector, localAttachment.ChangeVector) !=
                                    ConflictStatus.AlreadyMerged)
                                {
                                    database.DocumentsStorage.AttachmentsStorage.PutDirect(context, attachment.Key, attachmentName,
                                        contentType, attachment.Base64Hash, attachment.ChangeVector);
                                }

                                break;

                            case AttachmentTombstoneReplicationItem attachmentTombstone:

                                var tombstone = AttachmentsStorage.GetAttachmentTombstoneByKey(context, attachmentTombstone.Key);
                                if (tombstone != null && ChangeVectorUtils.GetConflictStatus(item.ChangeVector, tombstone.ChangeVector) == ConflictStatus.AlreadyMerged)
                                    continue;

                                database.DocumentsStorage.AttachmentsStorage.DeleteAttachmentDirect(context, attachmentTombstone.Key, false, "$fromReplication", null,
                                    rcvdChangeVector,
                                    attachmentTombstone.LastModifiedTicks);
                                break;

                            case RevisionTombstoneReplicationItem revisionTombstone:

                                Slice id;
                                if (_isSink)
                                {
                                    var currentId = revisionTombstone.Id.ToString();
                                    ReplaceKnownSinkEntries(context, ref currentId);
                                    toDispose.Add(Slice.From(context.Allocator, currentId, out id));
                                }
                                else
                                {
                                    toDispose.Add(Slice.From(context.Allocator, revisionTombstone.Id, out id));
                                }

                                database.DocumentsStorage.RevisionsStorage.DeleteRevision(context, id, revisionTombstone.Collection,
                                    rcvdChangeVector, revisionTombstone.LastModifiedTicks);
                                break;

                            case CounterReplicationItem counter:
                                var changed = database.DocumentsStorage.CountersStorage.PutCounters(context, counter.Id, counter.Collection, counter.ChangeVector,
                                    counter.Values);
                                if (changed && _replicationInfo.SupportedFeatures.Replication.CaseInsensitiveCounters == false)
                                {
                                    // 4.2 counters
                                    docCountersToRecreate ??= new HashSet<LazyStringValue>(LazyStringValueComparer.Instance);
                                    docCountersToRecreate.Add(counter.Id);
                                }

                                break;

                            case TimeSeriesDeletedRangeItem deletedRange:
                                tss = database.DocumentsStorage.TimeSeriesStorage;

                                TimeSeriesValuesSegment.ParseTimeSeriesKey(deletedRange.Key, context, out docId, out name);

                                var deletionRangeRequest = new TimeSeriesStorage.DeletionRangeRequest
                                {
                                    DocumentId = docId,
                                    Collection = deletedRange.Collection,
                                    Name = name,
                                    From = deletedRange.From,
                                    To = deletedRange.To
                                };
                                var removedChangeVector = tss.DeleteTimestampRange(context, deletionRangeRequest, rcvdChangeVector);
                                if (removedChangeVector != null)
                                    context.LastDatabaseChangeVector = ChangeVectorUtils.MergeVectors(removedChangeVector, rcvdChangeVector);

                                break;

                            case TimeSeriesReplicationItem segment:
                                tss = database.DocumentsStorage.TimeSeriesStorage;
                                TimeSeriesValuesSegment.ParseTimeSeriesKey(segment.Key, context, out docId, out _, out var baseline);
                                if (_replicationInfo.SupportedFeatures.Replication.IncrementalTimeSeries == false &&
                                    TimeSeriesHandler.CheckIfIncrementalTs(segment.Name))
                                {
                                    var msg = $"Detected an item of type Incremental-TimeSeries : '{segment.Name}' on document '{docId}', " +
                                              $"while replication protocol version '{_replicationInfo.SupportedFeatures.ProtocolVersion}' does not support Incremental-TimeSeries feature.";
                                    
                                    database.NotificationCenter.Add(AlertRaised.Create(
                                        database.Name,
                                        "Incoming Replication",
                                        msg,
                                        AlertType.Replication,
                                        NotificationSeverity.Error));

                                    throw new LegacyReplicationViolationException(msg);
                                }
                                UpdateTimeSeriesNameIfNeeded(context, docId, segment, tss);

                                if (tss.TryAppendEntireSegment(context, segment, docId, segment.Name, baseline))
                                {
                                    var databaseChangeVector = context.LastDatabaseChangeVector ?? DocumentsStorage.GetDatabaseChangeVector(context);
                                    context.LastDatabaseChangeVector = ChangeVectorUtils.MergeVectors(databaseChangeVector, segment.ChangeVector);
                                    continue;
                                }
                                
                                var options = new TimeSeriesStorage.AppendOptions
                                {
                                    VerifyName = false,
                                    ChangeVectorFromReplication = segment.ChangeVector
                                };

                                var values = segment.Segment.YieldAllValues(context, context.Allocator, baseline);
                                var changeVector = tss.AppendTimestamp(context, docId, segment.Collection, segment.Name, values, options);
                                context.LastDatabaseChangeVector = ChangeVectorUtils.MergeVectors(changeVector, segment.ChangeVector);

                                break;

                            case DocumentReplicationItem doc:
                                Debug.Assert(doc.Flags.Contain(DocumentFlags.Artificial) == false);

                                BlittableJsonReaderObject document = doc.Data;

                                if (doc.Data != null)
                                {
                                    // if something throws at this point, this means something is really wrong and we should stop receiving documents.
                                    // the other side will receive negative ack and will retry sending again.
                                    try
                                    {
                                        AssertAttachmentsFromReplication(context, doc.Id, document);
                                    }
                                    catch (MissingAttachmentException)
                                    {
                                        if (_replicationInfo.SupportedFeatures.Replication.MissingAttachments)
                                        {
                                            throw;
                                        }

                                        database.NotificationCenter.Add(AlertRaised.Create(
                                            database.Name,
                                            "Incoming Replication",
                                            $"Detected missing attachments for document '{doc.Id}'. Existing attachments in metadata:" +
                                            $" ({string.Join(',', GetAttachmentsNameAndHash(document).Select(x => $"name: {x.Name}, hash: {x.Hash}"))}).",
                                            AlertType.ReplicationMissingAttachments,
                                            NotificationSeverity.Warning));
                                    }
                                }

                                var nonPersistentFlags = NonPersistentDocumentFlags.FromReplication;
                                if (doc.Flags.Contain(DocumentFlags.Revision))
                                {
                                    database.DocumentsStorage.RevisionsStorage.Put(
                                        context,
                                        doc.Id,
                                        document,
                                        doc.Flags,
                                        nonPersistentFlags,
                                        rcvdChangeVector,
                                        doc.LastModifiedTicks);
                                    continue;
                                }

                                if (doc.Flags.Contain(DocumentFlags.DeleteRevision))
                                {
                                    database.DocumentsStorage.RevisionsStorage.Delete(
                                        context,
                                        doc.Id,
                                        document,
                                        doc.Flags,
                                        nonPersistentFlags,
                                        rcvdChangeVector,
                                        doc.LastModifiedTicks);
                                    continue;
                                }

                                var hasRemoteClusterTx = doc.Flags.Contain(DocumentFlags.FromClusterTransaction);
                                var conflictStatus = ConflictsStorage.GetConflictStatusForDocument(context, doc.Id, doc.ChangeVector, out var hasLocalClusterTx);
                                var flags = doc.Flags;
                                var resolvedDocument = document;

                                switch (conflictStatus)
                                {
                                    case ConflictStatus.Update:

                                        if (resolvedDocument != null)
                                        {
                                            if (flags.Contain(DocumentFlags.HasCounters) &&
                                                _replicationInfo.SupportedFeatures.Replication.CaseInsensitiveCounters == false)
                                            {
                                                var oldDoc = context.DocumentDatabase.DocumentsStorage.Get(context, doc.Id);
                                                if (oldDoc == null)
                                                {
                                                    // 4.2 documents might have counter names in metadata which don't exist in storage
                                                    // we need to replace metadata counters with the counter names from storage

                                                    nonPersistentFlags |= NonPersistentDocumentFlags.ResolveCountersConflict;
                                                }
                                            }

                                            try
                                            {
                                                database.DocumentsStorage.Put(context, doc.Id, null, resolvedDocument, doc.LastModifiedTicks,
                                                    rcvdChangeVector, null, flags, nonPersistentFlags);
                                            }
                                            catch (DocumentCollectionMismatchException)
                                            {
                                                goto case ConflictStatus.Conflict;
                                            }
                                        }
                                        else
                                        {
                                            using (DocumentIdWorker.GetSliceFromId(context, doc.Id, out Slice keySlice))
                                            {
                                                database.DocumentsStorage.Delete(
                                                    context, keySlice, doc.Id, null,
                                                    doc.LastModifiedTicks,
                                                    rcvdChangeVector,
                                                    new CollectionName(doc.Collection),
                                                    nonPersistentFlags,
                                                    flags);
                                            }
                                        }

                                        break;

                                    case ConflictStatus.Conflict:
                                        if (_replicationInfo.Logger.IsInfoEnabled)
                                            _replicationInfo.Logger.Info(
                                                $"Conflict check resolved to Conflict operation, resolving conflict for doc = {doc.Id}, with change vector = {doc.ChangeVector}");

                                        if (hasLocalClusterTx == hasRemoteClusterTx)
                                        {
                                            // when hasLocalClusterTx and hasRemoteClusterTx both 'true'
                                            // it is a case of a conflict between documents which were modified in a cluster transaction
                                            // in two _different clusters_, so we will treat it as a "normal" conflict

                                            IsIncomingReplication = false;
                                            _replicationInfo.ConflictManager.HandleConflictForDocument(context, doc.Id, doc.Collection, doc.LastModifiedTicks,
                                                document, rcvdChangeVector, doc.Flags);
                                            continue;
                                        }

                                        // cluster tx has precedence over regular tx

                                        if (hasLocalClusterTx)
                                            goto case ConflictStatus.AlreadyMerged;

                                        if (hasRemoteClusterTx)
                                            goto case ConflictStatus.Update;

                                        break;

                                    case ConflictStatus.AlreadyMerged:
                                        // we have to do nothing here
                                        break;

                                    default:
                                        throw new ArgumentOutOfRangeException(nameof(conflictStatus),
                                            "Invalid ConflictStatus: " + conflictStatus);
                                }

                                break;

                            default:
                                throw new ArgumentOutOfRangeException(item.GetType().ToString());
                        }
                    }

                    if (docCountersToRecreate != null)
                    {
                        foreach (var id in docCountersToRecreate)
                        {
                            context.DocumentDatabase.DocumentsStorage.DocumentPut.Recreate<DocumentPutAction.RecreateCounters>(context, id);
                        }
                    }

                    Debug.Assert(_replicationInfo.ReplicatedAttachmentStreams == null ||
                                 _replicationInfo.ReplicatedAttachmentStreams.Count == handledAttachmentStreams.Count,
                        "We should handle all attachment streams during WriteAttachment.");

                    //context.LastDatabaseChangeVector being an empty string is valid in the case of receiving docs into
                    //an empty database via filtered pull replication, since it does not modify the db's change vector
                    Debug.Assert(context.LastDatabaseChangeVector != null, $"database: {context.DocumentDatabase.Name};");

                    // instead of : SetLastReplicatedEtagFrom -> _incoming.ConnectionInfo.SourceDatabaseId, _lastEtag , we will store in context and write once right before commit (one time instead of repeating on all docs in the same Tx)
                    if (context.LastReplicationEtagFrom == null)
                        context.LastReplicationEtagFrom = new Dictionary<string, long>();
                    context.LastReplicationEtagFrom[_replicationInfo.SourceDatabaseId] = _lastEtag;
                    return operationsCount;
                }
                finally
                {
                    foreach (var item in toDispose)
                    {
                        item.Dispose();
                    }

                    IsIncomingReplication = false;
                }
            }

            private static void RemoveExpiresFromSinkBatchItem(DocumentReplicationItem doc, JsonOperationContext context)
            {
                if (doc.Data.TryGet(Constants.Documents.Metadata.Key, out BlittableJsonReaderObject metadata) == false)
                    return;

                if (metadata.TryGet(Constants.Documents.Metadata.Expires, out string _) == false)
                    return;
                metadata.Modifications ??= new DynamicJsonValue(metadata);
                metadata.Modifications.Remove(Constants.Documents.Metadata.Expires);
                using (var old = doc.Data)
                {
                    doc.Data = context.ReadObject(doc.Data, doc.Id, BlittableJsonDocumentBuilder.UsageMode.ToDisk);
                }
            }

            private static string ReplaceUnknownEntriesWithSinkTag(DocumentsOperationContext context, ref string changeVector)
            {
                var globalDbIds = context.LastDatabaseChangeVector?.ToChangeVectorList()?.Select(x => x.DbId).ToList();
                var incoming = changeVector.ToChangeVectorList();
                var knownEntries = new List<ChangeVectorEntry>();
                var newIncoming = new List<ChangeVectorEntry>();

                foreach (var entry in incoming)
                {
                    if (globalDbIds?.Contains(entry.DbId) == true)
                    {
                        newIncoming.Add(entry);
                        knownEntries.Add(entry);
                    }
                    else
                    {
                        newIncoming.Add(new ChangeVectorEntry
                        {
                            DbId = entry.DbId,
                            Etag = entry.Etag,
                            NodeTag = ChangeVectorParser.SinkInt
                        });

                        context.DbIdsToIgnore ??= new HashSet<string>();
                        context.DbIdsToIgnore.Add(entry.DbId);
                    }
                }

                changeVector = newIncoming.SerializeVector();

                return knownEntries.Count > 0 ?
                    knownEntries.SerializeVector() :
                    null;
            }

            private static void ReplaceKnownSinkEntries(DocumentsOperationContext context, ref string changeVector)
            {
                if (changeVector.Contains("SINK", StringComparison.OrdinalIgnoreCase) == false)
                    return;

                var global = context.LastDatabaseChangeVector?.ToChangeVectorList();
                var incoming = changeVector.ToChangeVectorList();
                var newIncoming = new List<ChangeVectorEntry>();

                foreach (var entry in incoming)
                {
                    if (entry.NodeTag == ChangeVectorParser.SinkInt)
                    {
                        var found = global?.Find(x => x.DbId == entry.DbId) ?? default;
                        if (found.Etag > 0)
                        {
                            newIncoming.Add(new ChangeVectorEntry
                            {
                                DbId = entry.DbId,
                                Etag = entry.Etag,
                                NodeTag = found.NodeTag
                            });
                            continue;
                        }
                    }

                    newIncoming.Add(entry);
                }

                changeVector = newIncoming.SerializeVector();
            }

            private static void UpdateTimeSeriesNameIfNeeded(DocumentsOperationContext context, LazyStringValue docId, TimeSeriesReplicationItem segment, TimeSeriesStorage tss)
            {
                using (var slicer = new TimeSeriesSliceHolder(context, docId, segment.Name))
                {
                    var localName = tss.Stats.GetTimeSeriesNameOriginalCasing(context, slicer.StatsKey);
                    if (localName == null || localName.CompareTo(segment.Name) <= 0)
                        return;

                    // the incoming ts-segment name exists locally but under a different casing
                    // lexical value of local name > lexical value of remote name =>
                    // need to replace the local name by the remote name, in TimeSeriesStats and in document's metadata

                    var collectionName = new CollectionName(segment.Collection);
                    tss.Stats.UpdateTimeSeriesName(context, collectionName, slicer);
                    tss.ReplaceTimeSeriesNameInMetadata(context, docId, localName, segment.Name);
                }
            }

            public void AssertAttachmentsFromReplication(DocumentsOperationContext context, string id, BlittableJsonReaderObject document)
            {
                foreach (var attachment in AttachmentsStorage.GetAttachmentsFromDocumentMetadata(document))
                {
                    if (attachment.TryGet(nameof(AttachmentName.Hash), out LazyStringValue hash) == false)
                        continue;

                    if (_replicationInfo.DocumentDatabase.DocumentsStorage.AttachmentsStorage.AttachmentExists(context, hash))
                        continue;

                    using (Slice.From(context.Allocator, hash, out var hashSlice))
                    {
                        if (_replicationInfo.ReplicatedAttachmentStreams != null && _replicationInfo.ReplicatedAttachmentStreams.TryGetValue(hashSlice, out _))
                        {
                            // attachment exists but not in the correct order of items (RavenDB-13341)
                            continue;
                        }

                        attachment.TryGet(nameof(AttachmentName.Name), out LazyStringValue attachmentName);

                        var msg = $"Document '{id}' has attachment " +
                                  $"named: '{attachmentName?.ToString() ?? "unknown"}', hash: '{hash?.ToString() ?? "unknown"}' " +
                                  $"listed as one of its attachments but it doesn't exist in the attachment storage";

                        throw new MissingAttachmentException(msg);
                    }
                }
            }

            private IEnumerable<(string Name, string Hash)> GetAttachmentsNameAndHash(BlittableJsonReaderObject document)
            {
                foreach (var attachment in AttachmentsStorage.GetAttachmentsFromDocumentMetadata(document))
                {
                    attachment.TryGet(nameof(AttachmentName.Name), out LazyStringValue name);
                    attachment.TryGet(nameof(AttachmentName.Hash), out LazyStringValue hash);

                    yield return (Name: name, Hash: hash);
                }
            }

            public override TransactionOperationsMerger.IReplayableCommandDto<TransactionOperationsMerger.MergedTransactionCommand> ToDto<TTransaction>(TransactionOperationContext<TTransaction> context)
            {
                var replicatedAttachmentStreams = _replicationInfo.ReplicatedAttachmentStreams?
                    .Select(kv => KeyValuePair.Create(kv.Key.ToString(), kv.Value.Stream))
                    .ToArray();

                return new MergedDocumentReplicationCommandDto
                {
                    Mode = _mode,
                    LastEtag = _lastEtag,
                    SupportedFeatures = _replicationInfo.SupportedFeatures,
                    ReplicatedItemDtos = _replicationInfo.ReplicatedItems.Select(i => i.Clone(context, context.Allocator)).ToArray(),
                    SourceDatabaseId = _replicationInfo.SourceDatabaseId,
                    ReplicatedAttachmentStreams = replicatedAttachmentStreams
                };
            }
        }
    }

    internal class MergedUpdateDatabaseChangeVectorCommandDto : TransactionOperationsMerger.IReplayableCommandDto<IncomingReplicationHandler.MergedUpdateDatabaseChangeVectorCommand>
    {
        public string ChangeVector;
        public long LastDocumentEtag;
        public string SourceDatabaseId;
        public bool IsHub;

        public IncomingReplicationHandler.MergedUpdateDatabaseChangeVectorCommand ToCommand(DocumentsOperationContext context, DocumentDatabase database)
        {
            var command = new IncomingReplicationHandler.MergedUpdateDatabaseChangeVectorCommand(ChangeVector, LastDocumentEtag, SourceDatabaseId,
                new AsyncManualResetEvent(), IsHub);
            return command;
        }
    }

    internal class MergedDocumentReplicationCommandDto : TransactionOperationsMerger.IReplayableCommandDto<IncomingReplicationHandler.MergedDocumentReplicationCommand>
    {
        public ReplicationBatchItem[] ReplicatedItemDtos;
        public long LastEtag;
        public PullReplicationMode Mode;
        public TcpConnectionHeaderMessage.SupportedFeatures SupportedFeatures;
        public string SourceDatabaseId;
        public KeyValuePair<string, Stream>[] ReplicatedAttachmentStreams;

        public IncomingReplicationHandler.MergedDocumentReplicationCommand ToCommand(DocumentsOperationContext context, DocumentDatabase database)
        {
            var replicatedItemsCount = ReplicatedItemDtos.Length;
            var replicationItems = new ReplicationBatchItem[replicatedItemsCount];
            for (var i = 0; i < replicatedItemsCount; i++)
            {
                replicationItems[i] = ReplicatedItemDtos[i].Clone(context, context.Allocator);
            }

            Dictionary<Slice, AttachmentReplicationItem> replicatedAttachmentStreams = null;
            if (ReplicatedAttachmentStreams != null)
            {
                replicatedAttachmentStreams = new Dictionary<Slice, AttachmentReplicationItem>(SliceComparer.Instance);
                var attachmentStreamsCount = ReplicatedAttachmentStreams.Length;
                for (var i = 0; i < attachmentStreamsCount; i++)
                {
                    var replicationAttachmentStream = ReplicatedAttachmentStreams[i];
                    var item = CreateReplicationAttachmentStream(context, replicationAttachmentStream);
                    replicatedAttachmentStreams[item.Base64Hash] = item;
                }
            }

            var dataForReplicationCommand = new IncomingReplicationHandler.DataForReplicationCommand
            {
                DocumentDatabase = database,
                ConflictManager = new ConflictManager(database, database.ReplicationLoader.ConflictResolver),
                SourceDatabaseId = SourceDatabaseId,
                ReplicatedItems = replicationItems,
                ReplicatedAttachmentStreams = replicatedAttachmentStreams,
                SupportedFeatures = SupportedFeatures,
                Logger = LoggingSource.Instance.GetLogger<IncomingReplicationHandler>(database.Name)
            };

            return new IncomingReplicationHandler.MergedDocumentReplicationCommand(dataForReplicationCommand, LastEtag, Mode);
        }

        private AttachmentReplicationItem CreateReplicationAttachmentStream(DocumentsOperationContext context, KeyValuePair<string, Stream> arg)
        {
            var attachmentStream = new AttachmentReplicationItem
            {
                Type = ReplicationBatchItem.ReplicationItemType.AttachmentStream,
                Stream = arg.Value
            };
            attachmentStream.ToDispose(Slice.From(context.Allocator, arg.Key, ByteStringType.Immutable, out attachmentStream.Base64Hash));
            return attachmentStream;
        }
    }
}
