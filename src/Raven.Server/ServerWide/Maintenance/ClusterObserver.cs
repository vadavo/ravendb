﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Backups;
using Raven.Client.Exceptions;
using Raven.Client.Extensions;
using Raven.Client.Http;
using Raven.Client.ServerWide;
using Raven.Client.Util;
using Raven.Server.Config;
using Raven.Server.Config.Settings;
using Raven.Server.Json;
using Raven.Server.NotificationCenter.Notifications;
using Raven.Server.Rachis;
using Raven.Server.ServerWide.Commands;
using Raven.Server.ServerWide.Commands.Indexes;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Sparrow;
using Sparrow.Json;
using Sparrow.Logging;
using static Raven.Server.ServerWide.Maintenance.DatabaseStatus;
using Index = Raven.Server.Documents.Indexes.Index;

namespace Raven.Server.ServerWide.Maintenance
{
    internal class ClusterObserver : IDisposable
    {
        private readonly PoolOfThreads.LongRunningWork _observe;
        private readonly CancellationTokenSource _cts;
        private readonly ClusterMaintenanceSupervisor _maintenance;
        private readonly string _nodeTag;
        private readonly RachisConsensus<ClusterStateMachine> _engine;
        private readonly TransactionContextPool _contextPool;
        private readonly Logger _logger;

        private readonly TimeSpan _supervisorSamplePeriod;
        private readonly ServerStore _server;
        private readonly TimeSpan _stabilizationTime;
        private readonly long _stabilizationTimeMs;
        private readonly TimeSpan _breakdownTimeout;
        private readonly bool _hardDeleteOnReplacement;

        private readonly DateTime StartTime = DateTime.UtcNow;
        public SystemTime Time = new SystemTime();

        private NotificationCenter.NotificationCenter NotificationCenter => _server.NotificationCenter;

        public ClusterObserver(
            ServerStore server,
            ClusterMaintenanceSupervisor maintenance,
            RachisConsensus<ClusterStateMachine> engine,
            long term,
            TransactionContextPool contextPool,
            CancellationToken token)
        {
            _maintenance = maintenance;
            _nodeTag = server.NodeTag;
            _server = server;
            _engine = engine;
            _term = term;
            _contextPool = contextPool;
            _logger = LoggingSource.Instance.GetLogger<ClusterObserver>(_nodeTag);
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);

            var config = server.Configuration.Cluster;
            _supervisorSamplePeriod = config.SupervisorSamplePeriod.AsTimeSpan;
            _stabilizationTime = config.StabilizationTime.AsTimeSpan;
            _stabilizationTimeMs = (long)config.StabilizationTime.AsTimeSpan.TotalMilliseconds;
            _moveToRehabTimeMs = (long)config.MoveToRehabGraceTime.AsTimeSpan.TotalMilliseconds;
            _maxChangeVectorDistance = config.MaxChangeVectorDistance;
            _rotateGraceTimeMs = (long)config.RotatePreferredNodeGraceTime.AsTimeSpan.TotalMilliseconds;
            _breakdownTimeout = config.AddReplicaTimeout.AsTimeSpan;
            _hardDeleteOnReplacement = config.HardDeleteOnReplacement;

            _observe = PoolOfThreads.GlobalRavenThreadPool.LongRunning(_ =>
            {
                try
                {
                    Run(_cts.Token);
                }
                catch
                {
                    // nothing we can do here
                }
            }, null, $"Cluster observer for term {_term}");
        }

        public bool Suspended = false; // don't really care about concurrency here
        private readonly BlockingCollection<ClusterObserverLogEntry> _decisionsLog = new BlockingCollection<ClusterObserverLogEntry>();
        private long _iteration;
        private readonly long _term;
        private readonly long _moveToRehabTimeMs;
        private readonly long _maxChangeVectorDistance;
        private readonly long _rotateGraceTimeMs;
        private long _lastIndexCleanupTimeInTicks;
        internal long _lastTombstonesCleanupTimeInTicks;
        internal long _lastExpiredCompareExchangeCleanupTimeInTicks;
        private bool _hasMoreTombstones = false;

        public (ClusterObserverLogEntry[] List, long Iteration) ReadDecisionsForDatabase()
        {
            return (_decisionsLog.ToArray(), _iteration);
        }

        public void Run(CancellationToken token)
        {
            // we give some time to populate the stats.
            if (token.WaitHandle.WaitOne(_stabilizationTime))
                return;

            var prevStats = _maintenance.GetStats();

            // wait before collecting the stats again.
            if (token.WaitHandle.WaitOne(_supervisorSamplePeriod))
                return;

            while (_term == _engine.CurrentTerm && token.IsCancellationRequested == false)
            {
                try
                {
                    if (Suspended == false)
                    {
                        _iteration++;
                        var newStats = _maintenance.GetStats();

                        // ReSharper disable once MethodSupportsCancellation
                        // we explicitly not passing a token here, since it will throw operation cancelled,
                        // but the original task might continue to run (with an open tx)

                        AnalyzeLatestStats(newStats, prevStats).Wait();
                        prevStats = newStats;
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception e)
                {
                    Debug.Assert(e.InnerException is not KeyNotFoundException,
                        $"Got a '{nameof(KeyNotFoundException)}' while analyzing maintenance stats on node {_nodeTag} : {e}");

                    LogMessage($"An error occurred while analyzing maintenance stats on node {_nodeTag}.", e);
                }
                finally
                {
                    token.WaitHandle.WaitOne(_supervisorSamplePeriod);
                }
            }
        }

        private readonly Dictionary<string, long> _lastLogs = new Dictionary<string, long>();

        private void LogMessage(string message, Exception e = null, string database = null)
        {
            if (_iteration % 10_000 == 0)
                _lastLogs.Clear();

            if (_lastLogs.TryGetValue(message, out var last))
            {
                if (last + 60 > _iteration)
                    // each iteration occur every 500 ms, so we update the log with the _same_ message every 30 sec (60 * 0.5s)
                    return;
            }
            _lastLogs[message] = _iteration;
            AddToDecisionLog(database, message, e);

            if (_logger.IsInfoEnabled)
            {
                _logger.Info(message, e);
            }
        }

        private async Task AnalyzeLatestStats(
            Dictionary<string, ClusterNodeStatusReport> newStats,
            Dictionary<string, ClusterNodeStatusReport> prevStats)
        {
            var currentLeader = _engine.CurrentLeader;
            if (currentLeader == null)
                return;

            var updateCommands = new List<(UpdateTopologyCommand Update, string Reason)>();
            var cleanUnusedAutoIndexesCommands = new List<(UpdateDatabaseCommand Update, string Reason)>();
            var cleanCompareExchangeTombstonesCommands = new List<CleanCompareExchangeTombstonesCommand>();

            Dictionary<string, long> cleanUpState = null;
            List<DeleteDatabaseCommand> deletions = null;
            List<string> databases;

            using (_contextPool.AllocateOperationContext(out TransactionOperationContext context))
            using (context.OpenReadTransaction())
            {
                databases = _engine.StateMachine.GetDatabaseNames(context).ToList();
            }

            var now = Time.GetUtcNow();
            var cleanupIndexes = now.Ticks - _lastIndexCleanupTimeInTicks >= _server.Configuration.Indexing.CleanupInterval.AsTimeSpan.Ticks;
            var cleanupTombstones = now.Ticks - _lastTombstonesCleanupTimeInTicks >= _server.Configuration.Cluster.CompareExchangeTombstonesCleanupInterval.AsTimeSpan.Ticks;
            var cleanupExpiredCompareExchange = now.Ticks - _lastExpiredCompareExchangeCleanupTimeInTicks >= _server.Configuration.Cluster.CompareExchangeExpiredCleanupInterval.AsTimeSpan.Ticks;

            foreach (var database in databases)
            {
                using (_contextPool.AllocateOperationContext(out TransactionOperationContext context))
                using (context.OpenReadTransaction())
                {
                    var clusterTopology = _server.GetClusterTopology(context);

                    _cts.Token.ThrowIfCancellationRequested();

                    using (var rawRecord = _engine.StateMachine.ReadRawDatabaseRecord(context, database, out long etag))
                    {
                        if (rawRecord == null)
                        {
                            LogMessage($"Can't analyze the stats of database the {database}, because the database record is null.", database: database);
                            continue;
                        }

                        var databaseTopology = rawRecord.Topology;

                        if (databaseTopology == null)
                        {
                            LogMessage($"Can't analyze the stats of database the {database}, because the database topology is null.", database: database);
                            continue;
                        }

                        if (databaseTopology.Count == 0)
                        {
                            // database being deleted
                            LogMessage($"Skip analyze the stats of database the {database}, because it being deleted", database: database);
                            continue;
                        }

                        // handle legacy commands
                        if (databaseTopology.NodesModifiedAt == null ||
                            databaseTopology.NodesModifiedAt == DateTime.MinValue)
                        {
                            AddToDecisionLog(database, "Updating ModifiedAt");

                            var cmd = new UpdateTopologyCommand(database, now, RaftIdGenerator.NewId()) { Topology = databaseTopology, RaftCommandIndex = etag };

                            updateCommands.Add((cmd, "Updating ModifiedAt"));
                            continue;
                        }

                        var topologyStamp = databaseTopology.Stamp;
                        var graceIfLeaderChanged = _term > topologyStamp.Term && currentLeader.LeaderShipDuration < _stabilizationTimeMs;
                        var letStatsBecomeStable = _term == topologyStamp.Term &&
                                                   ((now - databaseTopology.NodesModifiedAt.Value).TotalMilliseconds < _stabilizationTimeMs);
                        if (graceIfLeaderChanged || letStatsBecomeStable)
                        {
                            LogMessage($"We give more time for the '{database}' stats to become stable, so we skip analyzing it for now.", database: database);
                            continue;
                        }

                        var state = new DatabaseObservationState
                        {
                            Name = database,
                            DatabaseTopology = databaseTopology,
                            ClusterTopology = clusterTopology,
                            Current = newStats,
                            Previous = prevStats,
                            RawDatabase = rawRecord,
                        };

                        if (state.ReadDatabaseDisabled())
                            continue;

                        var updateReason = UpdateDatabaseTopology(state, ref deletions);
                        if (updateReason != null)
                        {
                            AddToDecisionLog(database, updateReason);

                            var cmd = new UpdateTopologyCommand(database, now, RaftIdGenerator.NewId())
                            {
                                Topology = databaseTopology,
                                RaftCommandIndex = etag
                            };

                            updateCommands.Add((cmd, updateReason));
                        }

                        var cleanUp = CleanUpDatabaseValues(state);
                        if (cleanUp != null)
                        {
                            cleanUpState ??= new Dictionary<string, long>();
                            cleanUpState.Add(database, cleanUp.Value);
                        }

                        if (cleanupIndexes)
                        {
                            var cleanupCommandsForDatabase = GetUnusedAutoIndexes(state);
                            cleanUnusedAutoIndexesCommands.AddRange(cleanupCommandsForDatabase);
                        }

                        if (cleanupTombstones)
                        {
                            var cmd = GetCompareExchangeTombstonesToCleanup(database, state, context, out var cleanupState);
                            switch (cleanupState)
                            {
                                case CompareExchangeTombstonesCleanupState.InvalidDatabaseObservationState:
                                    _hasMoreTombstones = true;
                                    break;
                                case CompareExchangeTombstonesCleanupState.HasMoreTombstones:
                                    Debug.Assert(cmd != null);
                                    cleanCompareExchangeTombstonesCommands.Add(cmd);
                                    break;
                                case CompareExchangeTombstonesCleanupState.InvalidPeriodicBackupStatus:
                                case CompareExchangeTombstonesCleanupState.NoMoreTombstones:
                                    break;

                                default:
                                    throw new NotSupportedException($"Not supported state: '{cleanupState}'.");
                            }
                        }
                    }
                }
            }

            if (cleanupIndexes)
            {
                foreach (var (cmd, updateReason) in cleanUnusedAutoIndexesCommands)
                {
                    await _engine.PutAsync(cmd);
                    AddToDecisionLog(cmd.DatabaseName, updateReason);
                }

                _lastIndexCleanupTimeInTicks = now.Ticks;
            }

            if (cleanupTombstones)
            {
                foreach (var cmd in cleanCompareExchangeTombstonesCommands)
                {
                    var result = await _server.SendToLeaderAsync(cmd);
                    await _server.Cluster.WaitForIndexNotification(result.Index);
                    var hasMore = (bool)result.Result;

                    _hasMoreTombstones |= hasMore;
                }

                if (_hasMoreTombstones == false)
                    _lastTombstonesCleanupTimeInTicks = now.Ticks;
            }

            if (cleanupExpiredCompareExchange)
            {
                if (await RemoveExpiredCompareExchange(now.Ticks) == false)
                    _lastExpiredCompareExchangeCleanupTimeInTicks = now.Ticks;
            }

            foreach (var command in updateCommands)
            {
                try
                {
                    await UpdateTopology(command.Update);
                    var alert = AlertRaised.Create(
                        command.Update.DatabaseName,
                        $"Topology of database '{command.Update.DatabaseName}' was changed",
                        command.Reason,
                        AlertType.DatabaseTopologyWarning,
                        NotificationSeverity.Warning
                    );
                    NotificationCenter.Add(alert);
                }
                catch (Exception e) when (e.ExtractSingleInnerException() is ConcurrencyException)
                {
                    // this is sort of expected, if the database was
                    // modified by someone else, we'll avoid changing
                    // it and run the logic again on the next round
                    AddToDecisionLog(command.Update.DatabaseName,
                        $"Topology of database '{command.Update.DatabaseName}' was not changed, reason: {nameof(ConcurrencyException)}");
                }
            }

            if (deletions != null)
            {
                foreach (var command in deletions)
                {
                    AddToDecisionLog(command.DatabaseName,
                        $"We reached the replication factor on '{command.DatabaseName}', so we try to remove promotables/rehabs from: {string.Join(", ", command.FromNodes)}");

                    await Delete(command);
                }
            }

            if (cleanUpState != null)
            {
                var guid = "cleanup/" + GetCommandId(cleanUpState);
                if (_engine.ContainsCommandId(guid) == false)
                {
                    foreach (var kvp in cleanUpState)
                    {
                        AddToDecisionLog(kvp.Key, $"Should clean up values up to raft index {kvp.Value}.");
                    }

                    var cmd = new CleanUpClusterStateCommand(guid) { ClusterTransactionsCleanup = cleanUpState };

                    if (_engine.LeaderTag != _server.NodeTag)
                    {
                        throw new NotLeadingException("This node is no longer the leader, so abort the cleaning.");
                    }

                    await _engine.PutAsync(cmd);
                }
            }
        }

        private static string GetCommandId(Dictionary<string, long> dic)
        {
            if (dic == null)
                return Guid.Empty.ToString();

            var hash = 0UL;
            foreach (var kvp in dic)
            {
                hash = Hashing.XXHash64.CalculateRaw(kvp.Key) ^ (ulong)kvp.Value ^ hash;
            }

            return hash.ToString("X");
        }

        internal List<(UpdateDatabaseCommand Update, string Reason)> GetUnusedAutoIndexes(DatabaseObservationState databaseState)
        {
            const string autoIndexPrefix = "Auto/";
            var cleanupCommands = new List<(UpdateDatabaseCommand Update, string Reason)>();

            if (AllDatabaseNodesHasReport(databaseState) == false)
                return cleanupCommands;

            var indexes = new Dictionary<string, TimeSpan>();

            var lowestDatabaseUpTime = TimeSpan.MaxValue;
            var newestIndexQueryTime = TimeSpan.MaxValue;

            foreach (var node in databaseState.DatabaseTopology.AllNodes)
            {
                if (databaseState.Current.TryGetValue(node, out var nodeReport) == false)
                    return cleanupCommands;

                if (nodeReport.Report.TryGetValue(databaseState.Name, out var report) == false)
                    return cleanupCommands;

                if (report.UpTime.HasValue && lowestDatabaseUpTime > report.UpTime)
                    lowestDatabaseUpTime = report.UpTime.Value;

                foreach (var kvp in report.LastIndexStats)
                {
                    var lastQueried = kvp.Value.LastQueried;
                    if (lastQueried.HasValue == false)
                        continue;

                    if (newestIndexQueryTime > lastQueried.Value)
                        newestIndexQueryTime = lastQueried.Value;

                    var indexName = kvp.Key;
                    if (indexName.StartsWith(autoIndexPrefix, StringComparison.OrdinalIgnoreCase) == false)
                        continue;

                    if (indexes.TryGetValue(indexName, out var lq) == false || lq > lastQueried)
                    {
                        indexes[indexName] = lastQueried.Value;
                    }
                }
            }

            if (indexes.Count == 0)
                return cleanupCommands;

            var settings = databaseState.ReadSettings();
            var timeToWaitBeforeMarkingAutoIndexAsIdle = (TimeSetting)RavenConfiguration.GetValue(x => x.Indexing.TimeToWaitBeforeMarkingAutoIndexAsIdle, _server.Configuration, settings);
            var timeToWaitBeforeDeletingAutoIndexMarkedAsIdle = (TimeSetting)RavenConfiguration.GetValue(x => x.Indexing.TimeToWaitBeforeDeletingAutoIndexMarkedAsIdle, _server.Configuration, settings);

            foreach (var kvp in indexes)
            {
                TimeSpan difference;
                if (lowestDatabaseUpTime > kvp.Value)
                    difference = kvp.Value;
                else
                {
                    difference = kvp.Value - newestIndexQueryTime;
                    if (difference == TimeSpan.Zero && lowestDatabaseUpTime > kvp.Value)
                        difference = kvp.Value;
                }

                var state = IndexState.Normal;
                if (databaseState.TryGetAutoIndex(kvp.Key, out var definition) && definition.State.HasValue)
                    state = definition.State.Value;

                if (state == IndexState.Idle && difference >= timeToWaitBeforeDeletingAutoIndexMarkedAsIdle.AsTimeSpan)
                {
                    var deleteIndexCommand = new DeleteIndexCommand(kvp.Key, databaseState.Name, RaftIdGenerator.NewId());
                    var updateReason = $"Deleting idle auto-index '{kvp.Key}' because last query time value is '{difference}' and threshold is set to '{timeToWaitBeforeDeletingAutoIndexMarkedAsIdle.AsTimeSpan}'.";

                    cleanupCommands.Add((deleteIndexCommand, updateReason));
                    continue;
                }

                if (state == IndexState.Normal && difference >= timeToWaitBeforeMarkingAutoIndexAsIdle.AsTimeSpan)
                {
                    var setIndexStateCommand = new SetIndexStateCommand(kvp.Key, IndexState.Idle, databaseState.Name, RaftIdGenerator.NewId());
                    var updateReason = $"Marking auto-index '{kvp.Key}' as idle because last query time value is '{difference}' and threshold is set to '{timeToWaitBeforeMarkingAutoIndexAsIdle.AsTimeSpan}'.";
                    
                    cleanupCommands.Add((setIndexStateCommand, updateReason));
                    continue;
                }

                if (state == IndexState.Idle && difference < timeToWaitBeforeMarkingAutoIndexAsIdle.AsTimeSpan)
                {
                    var setIndexStateCommand = new SetIndexStateCommand(kvp.Key, IndexState.Normal, databaseState.Name, Guid.NewGuid().ToString());
                    var updateReason = $"Marking idle auto-index '{kvp.Key}' as normal because last query time value is '{difference}' and threshold is set to '{timeToWaitBeforeMarkingAutoIndexAsIdle.AsTimeSpan}'.";
                    
                    cleanupCommands.Add((setIndexStateCommand, updateReason));
                }
            }

            return cleanupCommands;
        }

        internal CleanCompareExchangeTombstonesCommand GetCompareExchangeTombstonesToCleanup(string databaseName, DatabaseObservationState state, TransactionOperationContext context, out CompareExchangeTombstonesCleanupState cleanupState)
        {
            const int amountToDelete = 8192;

            if (_server.Cluster.HasCompareExchangeTombstones(context, databaseName) == false)
            {
                cleanupState = CompareExchangeTombstonesCleanupState.NoMoreTombstones;
                return null;
            }

            cleanupState = GetMaxCompareExchangeTombstonesEtagToDelete(context, databaseName, state, out long maxEtag);

            return cleanupState == CompareExchangeTombstonesCleanupState.HasMoreTombstones 
                ? new CleanCompareExchangeTombstonesCommand(databaseName, maxEtag, amountToDelete, RaftIdGenerator.NewId()) 
                : null;
        }

        public enum CompareExchangeTombstonesCleanupState
        {
            HasMoreTombstones,
            InvalidDatabaseObservationState,
            InvalidPeriodicBackupStatus,
            NoMoreTombstones
        }

        private CompareExchangeTombstonesCleanupState GetMaxCompareExchangeTombstonesEtagToDelete(TransactionOperationContext context, string databaseName, DatabaseObservationState state, out long maxEtag)
        {
            List<long> periodicBackupTaskIds;
            maxEtag = long.MaxValue;

            if (state?.RawDatabase != null)
            {
                periodicBackupTaskIds = state.RawDatabase.PeriodicBackupsTaskIds;
            }
            else
            {
                using (var rawRecord = _server.Cluster.ReadRawDatabaseRecord(context, databaseName))
                    periodicBackupTaskIds = rawRecord.PeriodicBackupsTaskIds;
            }

            if (periodicBackupTaskIds != null && periodicBackupTaskIds.Count > 0)
            {
                foreach (var taskId in periodicBackupTaskIds)
                {
                    var singleBackupStatus = _server.Cluster.Read(context, PeriodicBackupStatus.GenerateItemName(databaseName, taskId));
                    if (singleBackupStatus == null)
                        continue;

                    if (singleBackupStatus.TryGet(nameof(PeriodicBackupStatus.LastFullBackupInternal), out DateTime? lastFullBackupInternal) == false || lastFullBackupInternal == null)
                    {
                        // never backed up yet
                        if (singleBackupStatus.TryGet(nameof(PeriodicBackupStatus.LastIncrementalBackupInternal), out DateTime? lastIncrementalBackupInternal) == false || lastIncrementalBackupInternal == null)
                            continue;
                    }

                    if (singleBackupStatus.TryGet(nameof(PeriodicBackupStatus.LastRaftIndex), out BlittableJsonReaderObject lastRaftIndexBlittable) == false ||
                        lastRaftIndexBlittable == null)
                    {
                        if (singleBackupStatus.TryGet(nameof(PeriodicBackupStatus.Error), out BlittableJsonReaderObject error) == false || error != null)
                        {
                            // backup errored on first run (lastRaftIndex == null) => cannot remove ANY tombstones
                            return CompareExchangeTombstonesCleanupState.InvalidPeriodicBackupStatus;
                        }

                        continue;
                    }

                    if (lastRaftIndexBlittable.TryGet(nameof(PeriodicBackupStatus.LastEtag), out long? lastRaftIndex) == false || lastRaftIndex == null)
                    {
                        continue;
                    }

                    if (lastRaftIndex < maxEtag)
                        maxEtag = lastRaftIndex.Value;

                    if (maxEtag == 0)
                        return CompareExchangeTombstonesCleanupState.NoMoreTombstones;
                }
            }

            if (state != null)
            {
                if (state.DatabaseTopology.Count != state.Current.Count) // we have a state change, do not remove anything
                    return CompareExchangeTombstonesCleanupState.InvalidDatabaseObservationState;

                foreach (var node in state.DatabaseTopology.AllNodes)
                {
                    if (state.Current.TryGetValue(node, out var nodeReport) == false)
                        continue;

                    if (nodeReport.Report.TryGetValue(state.Name, out var report) == false)
                        continue;

                    foreach (var kvp in report.LastIndexStats)
                    {
                        var lastIndexedCompareExchangeReferenceTombstoneEtag = kvp.Value.LastIndexedCompareExchangeReferenceTombstoneEtag;
                        if (lastIndexedCompareExchangeReferenceTombstoneEtag == null)
                            continue;

                        if (lastIndexedCompareExchangeReferenceTombstoneEtag < maxEtag)
                            maxEtag = lastIndexedCompareExchangeReferenceTombstoneEtag.Value;

                        if (maxEtag == 0)
                            return CompareExchangeTombstonesCleanupState.NoMoreTombstones;
                    }
                }
            }

            if (maxEtag == 0)
                return CompareExchangeTombstonesCleanupState.NoMoreTombstones;

            return CompareExchangeTombstonesCleanupState.HasMoreTombstones;
        }

        private async Task<bool> RemoveExpiredCompareExchange(long nowTicks)
        {
            const int batchSize = 1024;
            using (_contextPool.AllocateOperationContext(out TransactionOperationContext context))
            using (context.OpenReadTransaction())
            {
                if (CompareExchangeExpirationStorage.HasExpired(context, nowTicks) == false)
                    return false;
            }

            var result = await _server.SendToLeaderAsync(new DeleteExpiredCompareExchangeCommand(nowTicks, batchSize, RaftIdGenerator.NewId()));
            await _server.Cluster.WaitForIndexNotification(result.Index);
            return (bool)result.Result;
        }

        private long? CleanUpDatabaseValues(DatabaseObservationState state)
        {
            if (ClusterCommandsVersionManager.CurrentClusterMinimalVersion <
                ClusterCommandsVersionManager.ClusterCommandsVersions[nameof(CleanUpClusterStateCommand)])
            {
                return null;
            }

            if (AllDatabaseNodesHasReport(state) == false)
                return null;

            long commandCount = long.MaxValue;
            foreach (var node in state.DatabaseTopology.AllNodes)
            {
                if (state.Current.TryGetValue(node, out var nodeReport) == false)
                    return null;

                if (nodeReport.Report.TryGetValue(state.Name, out var report) == false)
                    return null;

                commandCount = Math.Min(commandCount, report.LastCompletedClusterTransaction);
            }

            if (commandCount <= state.ReadTruncatedClusterTransactionCommandsCount())
                return null;

            return commandCount;
        }

        private static bool AllDatabaseNodesHasReport(DatabaseObservationState state)
        {
            if (state.DatabaseTopology.Count == 0)
                return false; // database is being deleted, so no need to cleanup values

            foreach (var node in state.DatabaseTopology.AllNodes)
            {
                if (state.Current.ContainsKey(node) == false)
                    return false;
            }

            return true;
        }

        private void AddToDecisionLog(string database, string updateReason, Exception e)
        {
            if (e != null)
                updateReason += $"{Environment.NewLine}Error: {e}";

            AddToDecisionLog(database, updateReason);
        }

        private void AddToDecisionLog(string database, string updateReason)
        {
            if (_decisionsLog.Count > 99)
                _decisionsLog.Take();

            _decisionsLog.Add(new ClusterObserverLogEntry
            {
                Database = database,
                Iteration = _iteration,
                Message = updateReason,
                Date = DateTime.UtcNow
            });
        }

        private const string ThingsToCheck = "Things you may check: verify node is working, check for ports being blocked by firewall or similar software.";

        private void RaiseNoLivingNodesAlert(string alertMsg, string dbName)
        {
            var alert = AlertRaised.Create(
                dbName,
                $"Could not reach any node of '{dbName}' database",
                $"{alertMsg}. {ThingsToCheck}",
                AlertType.DatabaseTopologyWarning,
                NotificationSeverity.Warning
            );

            NotificationCenter.Add(alert, updateExisting: false);
            LogMessage(alertMsg, database: dbName);
        }

        private void RaiseNodeNotFoundAlert(string alertMsg, string node)
        {
            var alert = AlertRaised.Create(
                null,
                $"Node {node} not found.",
                $"{alertMsg}",
                AlertType.DatabaseTopologyWarning,
                NotificationSeverity.Warning
            );

            NotificationCenter.Add(alert, updateExisting: false);
            LogMessage(alertMsg);
        }

        private string UpdateDatabaseTopology(DatabaseObservationState state, ref List<DeleteDatabaseCommand> deletions)
        {
            var hasLivingNodes = false;

            var databaseTopology = state.DatabaseTopology;
            var current = state.Current;
            var previous = state.Previous;
            var dbName = state.Name;
            var clusterTopology = state.ClusterTopology;
            var deletionInProgress = state.ReadDeletionInProgress();

            var someNodesRequireMoreTime = false;
            var rotatePreferredNode = false;

            foreach (var member in databaseTopology.Members)
            {
                var status = None;
                if (current.TryGetValue(member, out var nodeStats) == false)
                {
                    // there isn't much we can do here, except for log it.
                    if (previous.TryGetValue(member, out _))
                    {
                        // if we found this node in the previous report, we will ignore it this time and wait for the next report.
                        continue;
                    }

                    var msg =
                        $"The member node {member} was not found in both current and previous reports of the cluster observer. " +
                        $"If this error continue to raise, check the latency between the cluster nodes.";
                    LogMessage(msg, database: dbName);
                    RaiseNodeNotFoundAlert(msg, member);
                    continue;
                }

                DatabaseStatusReport dbStats = null;
                if (nodeStats.Status == ClusterNodeStatusReport.ReportStatus.Ok &&
                    nodeStats.Report.TryGetValue(dbName, out dbStats))
                {
                    status = dbStats.Status;
                    if (status == Loaded ||
                        status == Unloaded ||
                        status == NoChange)
                    {
                        hasLivingNodes = true;

                        if (databaseTopology.PromotablesStatus.TryGetValue(member, out _) || 
                            databaseTopology.DemotionReasons.TryGetValue(member, out _))
                        {
                            databaseTopology.DemotionReasons.Remove(member);
                            databaseTopology.PromotablesStatus.Remove(member);
                            return $"Node {member} is online";
                        }
                        continue;
                    }
                }

                if (_server.DatabasesLandlord.ForTestingPurposes?.HoldDocumentDatabaseCreation != null)
                    _server.DatabasesLandlord.ForTestingPurposes.PreventedRehabOfIdleDatabase = true;

                if (ShouldGiveMoreTimeBeforeMovingToRehab(nodeStats.LastSuccessfulUpdateDateTime, dbStats?.UpTime))
                {
                    if (ShouldGiveMoreTimeBeforeRotating(nodeStats.LastSuccessfulUpdateDateTime, dbStats?.UpTime) == false)
                    {
                        // It seems that the node has some trouble.
                        // We will give him more time before moving to rehab, but we need to make sure he isn't the preferred node.
                        if (databaseTopology.Members.Count > 1 &&
                            databaseTopology.Members[0] == member)
                        {
                            rotatePreferredNode = true;
                        }
                    }

                    someNodesRequireMoreTime = true;
                    continue;
                }

                if (TryMoveToRehab(dbName, databaseTopology, current, member))
                    return $"Node {member} is currently not responding (with status: {status}) and moved to rehab ({DateTime.UtcNow - nodeStats.LastSuccessfulUpdateDateTime})";

                // database distribution is off and the node is down
                if (databaseTopology.DynamicNodesDistribution == false && (
                        databaseTopology.PromotablesStatus.TryGetValue(member, out var currentStatus) == false
                        || currentStatus != DatabasePromotionStatus.NotResponding))
                {
                    databaseTopology.DemotionReasons[member] = "Not responding";
                    databaseTopology.PromotablesStatus[member] = DatabasePromotionStatus.NotResponding;
                    return $"Node {member} is currently not responding with the status '{status}'";
                }
            }

            if (hasLivingNodes && rotatePreferredNode)
            {
                var member = databaseTopology.Members[0];
                databaseTopology.Members.Remove(member);
                databaseTopology.Members.Add(member);
                return $"The preferred Node {member} is currently not responding and moved to the end of the list";
            }

            if (hasLivingNodes == false)
            {
                var recoverable = new List<string>();

                foreach (var rehab in databaseTopology.Rehabs)
                {
                    if (FailedDatabaseInstanceOrNode(rehab, state) == DatabaseHealth.Good)
                        recoverable.Add(rehab);
                }

                if (databaseTopology.Members.Count == 0)
                {
                    // as last resort we will promote a promotable
                    foreach (var promotable in databaseTopology.Promotables)
                    {
                        if (FailedDatabaseInstanceOrNode(promotable, state) == DatabaseHealth.Good)
                            recoverable.Add(promotable);
                    }
                }

                if (recoverable.Count > 0)
                {
                    var node = FindMostUpToDateNode(recoverable, dbName, current);
                    databaseTopology.Rehabs.Remove(node);
                    databaseTopology.Promotables.Remove(node);
                    databaseTopology.Members.Add(node);

                    RaiseNoLivingNodesAlert($"None of '{dbName}' database nodes are responding to the supervisor, promoting {node} to avoid making the database completely unreachable.", dbName);
                    return $"None of '{dbName}' nodes are responding, promoting {node}";
                }

                if (databaseTopology.EntireDatabasePendingDeletion(deletionInProgress))
                {
                    return null; // We delete the whole database.
                }

                RaiseNoLivingNodesAlert($"None of '{dbName}' database nodes are responding to the supervisor, the database is unreachable.", dbName);
            }

            if (someNodesRequireMoreTime == false)
            {
                if (CheckMembersDistance(state, out string reason) == false)
                    return reason;

                if (databaseTopology.TryUpdateByPriorityOrder())
                    return "Reordering the member nodes to ensure the priority order.";
            }

            var shouldUpdateTopologyStatus = false;
            var updateTopologyStatusReason = new StringBuilder();

            foreach (var promotable in databaseTopology.Promotables)
            {
                if (FailedDatabaseInstanceOrNode(promotable, state) == DatabaseHealth.Bad)
                {
                    // database distribution is off and the node is down
                    if (databaseTopology.DynamicNodesDistribution == false)
                    {
                        if (databaseTopology.PromotablesStatus.TryGetValue(promotable, out var currentStatus) == false
                            || currentStatus != DatabasePromotionStatus.NotResponding)
                        {
                            databaseTopology.DemotionReasons[promotable] = "Not responding";
                            databaseTopology.PromotablesStatus[promotable] = DatabasePromotionStatus.NotResponding;
                            return $"Node {promotable} is currently not responding";
                        }
                        continue;
                    }

                    if (TryFindFitNode(promotable, state, out var node) == false)
                    {
                        if (databaseTopology.PromotablesStatus.TryGetValue(promotable, out var currentStatus) == false
                            || currentStatus != DatabasePromotionStatus.NotResponding)
                        {
                            databaseTopology.DemotionReasons[promotable] = "Not responding";
                            databaseTopology.PromotablesStatus[promotable] = DatabasePromotionStatus.NotResponding;
                            return $"Node {promotable} is currently not responding";
                        }
                        continue;
                    }

                    if (_server.LicenseManager.CanDynamicallyDistributeNodes(withNotification: false, out _) == false)
                        continue;

                    // replace the bad promotable otherwise we will continue to add more and more nodes.
                    databaseTopology.Promotables.Add(node);
                    databaseTopology.DemotionReasons[node] = $"Just replaced the promotable node {promotable}";
                    databaseTopology.PromotablesStatus[node] = DatabasePromotionStatus.WaitingForFirstPromotion;
                    var deletionCmd = new DeleteDatabaseCommand(dbName, RaftIdGenerator.NewId())
                    {
                        ErrorOnDatabaseDoesNotExists = false,
                        FromNodes = new[] { promotable },
                        HardDelete = _hardDeleteOnReplacement,
                        UpdateReplicationFactor = false
                    };

                    if (deletions == null)
                        deletions = new List<DeleteDatabaseCommand>();
                    deletions.Add(deletionCmd);
                    return $"The promotable {promotable} is not responsive, replace it with a node {node}";
                }

                if (TryGetMentorNode(dbName, databaseTopology, clusterTopology, promotable, out var mentorNode) == false)
                    continue;

                var tryPromote = TryPromote(state, mentorNode, promotable);
                if (tryPromote.Promote)
                {
                    databaseTopology.Promotables.Remove(promotable);
                    databaseTopology.Members.Add(promotable);
                    databaseTopology.PredefinedMentors.Remove(promotable);
                    RemoveOtherNodesIfNeeded(state, ref deletions);
                    databaseTopology.ReorderMembers();

                    return $"Promoting node {promotable} to member";
                }
                if (tryPromote.UpdateTopologyReason != null)
                {
                    shouldUpdateTopologyStatus = true;
                    updateTopologyStatusReason.AppendLine(tryPromote.UpdateTopologyReason);
                }
            }

            var goodMembers = GetNumberOfRespondingNodes(state);
            var pendingDelete = GetPendingDeleteNodes(deletionInProgress);
            foreach (var rehab in databaseTopology.Rehabs)
            {
                var health = FailedDatabaseInstanceOrNode(rehab, state);
                switch (health)
                {
                    case DatabaseHealth.Bad:
                        if (databaseTopology.DynamicNodesDistribution == false)
                            continue;

                        if (goodMembers < databaseTopology.ReplicationFactor &&
                            TryFindFitNode(rehab, state, out var node))
                        {
                            if (_server.LicenseManager.CanDynamicallyDistributeNodes(withNotification: false, out _) == false)
                                continue;

                            databaseTopology.Promotables.Add(node);
                            databaseTopology.DemotionReasons[node] = $"Maintain the replication factor and create new replica instead of node {rehab}";
                            databaseTopology.PromotablesStatus[node] = DatabasePromotionStatus.WaitingForFirstPromotion;
                            return $"The rehab node {rehab} was too long in rehabilitation, create node {node} to replace it";
                        }

                        if (databaseTopology.PromotablesStatus.TryGetValue(rehab, out var status) == false || status != DatabasePromotionStatus.NotResponding)
                        {
                            // was already online, but now we lost the connection again
                            if (TryMoveToRehab(dbName, databaseTopology, current, rehab))
                            {
                                return $"Node {rehab} is currently not responding";
                            }
                        }

                        break;

                    case DatabaseHealth.Good:

                        if (pendingDelete.Contains(rehab) && databaseTopology.PromotablesStatus.ContainsKey(rehab) == false)
                        {
                            // already tried to promote, so we just ignore and continue
                            continue;
                        }

                        if (TryGetMentorNode(dbName, databaseTopology, clusterTopology, rehab, out var mentorNode) == false)
                            continue;

                        var tryPromote = TryPromote(state, mentorNode, rehab);
                        if (tryPromote.Promote)
                        {
                            LogMessage($"The database {dbName} on {rehab} is reachable and up to date, so we promote it back to member.", database: dbName);

                            databaseTopology.Members.Add(rehab);
                            databaseTopology.Rehabs.Remove(rehab);
                            RemoveOtherNodesIfNeeded(state, ref deletions);
                            databaseTopology.ReorderMembers();

                            return $"Node {rehab} was recovered from rehabilitation and promoted back to member";
                        }
                        if (tryPromote.UpdateTopologyReason != null)
                        {
                            shouldUpdateTopologyStatus = true;
                            updateTopologyStatusReason.AppendLine(tryPromote.UpdateTopologyReason);
                        }
                        break;
                }
            }
            RemoveOtherNodesIfNeeded(state, ref deletions);

            if (shouldUpdateTopologyStatus)
            {
                return updateTopologyStatusReason.ToString();
            }

            return null;
        }

        private bool CheckMembersDistance(DatabaseObservationState state, out string reason)
        {
            // check every node pair, and if one of them is lagging behind, move him to rehab
            reason = null;
            var members = state.DatabaseTopology.Members;
            for (int i = 0; i < members.Count; i++)
            {
                var member1 = members[i];
                var current1 = state.GetCurrentDatabaseReport(member1);
                var prev1 = state.GetPreviousDatabaseReport(member1);
                if (current1 == null || prev1 == null)
                    continue;

                var myCurrentEtag = current1.LastEtag;
                var myPrevEtag = prev1.LastEtag;

                for (int j = 0; j < members.Count; j++)
                {
                    if (i == j)
                        continue;

                    var member2 = members[j];
                    var current2 = state.GetCurrentDatabaseReport(member2);
                    var prev2 = state.GetPreviousDatabaseReport(member2);
                    if (current2 == null || prev2 == null)
                        continue;

                    if (current1.LastSentEtag.TryGetValue(member2, out var currentLastSentEtag) == false)
                        continue;

                    if (prev1.LastSentEtag.TryGetValue(member2, out var prevLastSentEtag) == false)
                        continue;

                    var prevEtagDistance = myPrevEtag - prevLastSentEtag;
                    var currentEtagDistance = myCurrentEtag - currentLastSentEtag;

                    if (Math.Abs(currentEtagDistance) > _maxChangeVectorDistance && 
                        Math.Abs(prevEtagDistance)> _maxChangeVectorDistance)
                    {
                        // we rely both on the etag and change vector,
                        // because the data may find a path to the node even if the direct connection between them is broken.
                        var currentChangeVectorDistance = ChangeVectorUtils.Distance(current1.DatabaseChangeVector, current2.DatabaseChangeVector);
                        var prevChangeVectorDistance = ChangeVectorUtils.Distance(prev1.DatabaseChangeVector, prev2.DatabaseChangeVector);

                        if (Math.Abs(currentChangeVectorDistance) > _maxChangeVectorDistance &&
                            Math.Abs(prevChangeVectorDistance) > _maxChangeVectorDistance)
                        {
                            var rehab = currentChangeVectorDistance > 0 ? member2 : member1;
                            var rehabCheck = prevChangeVectorDistance > 0 ? member2 : member1;
                            if (rehab != rehabCheck)
                                continue; // inconsistent result, same node must be lagging

                            state.DatabaseTopology.Members.Remove(rehab);
                            state.DatabaseTopology.Rehabs.Add(rehab);
                            reason =
                                $"Node {rehab} for database '{state.Name}' moved to rehab, because he is lagging behind. (distance between {member1} and {member2} is {currentChangeVectorDistance})";
                            state.DatabaseTopology.DemotionReasons[rehab] = $"distance between {member1} and {member2} is {currentChangeVectorDistance}";

                            return false;
                        }
                    }
                }
            }

            return true;
        }

        private bool ShouldGiveMoreTimeBeforeMovingToRehab(DateTime lastSuccessfulUpdate, TimeSpan? databaseUpTime)
        {
            if (databaseUpTime.HasValue)
            {
                if (databaseUpTime.Value.TotalMilliseconds < _moveToRehabTimeMs)
                {
                    return true;
                }
            }

            return ShouldGiveMoreGrace(lastSuccessfulUpdate, databaseUpTime, _moveToRehabTimeMs);
        }

        private bool ShouldGiveMoreTimeBeforeRotating(DateTime lastSuccessfulUpdate, TimeSpan? databaseUpTime)
        {
            if (databaseUpTime.HasValue)
            {
                if (databaseUpTime.Value.TotalMilliseconds > _rotateGraceTimeMs)
                {
                    return false;
                }
            }

            return ShouldGiveMoreGrace(lastSuccessfulUpdate, databaseUpTime, _rotateGraceTimeMs);
        }

        private bool ShouldGiveMoreGrace(DateTime lastSuccessfulUpdate, TimeSpan? databaseUpTime, long graceMs)
        {
            var grace = DateTime.UtcNow.AddMilliseconds(-graceMs);

            if (lastSuccessfulUpdate == default) // the node hasn't send a single (good) report
            {
                if (grace < StartTime)
                    return true;
            }

            if (databaseUpTime.HasValue == false) // database isn't loaded
            {
                return grace < StartTime;
            }

            return grace < lastSuccessfulUpdate && graceMs > databaseUpTime.Value.TotalMilliseconds;
        }

        private int GetNumberOfRespondingNodes(DatabaseObservationState state)
        {
            var topology = state.DatabaseTopology;
            var dbName = state.Name;

            var goodMembers = topology.Members.Count;
            foreach (var promotable in topology.Promotables)
            {
                if (FailedDatabaseInstanceOrNode(promotable, state) != DatabaseHealth.Bad)
                    goodMembers++;
            }
            foreach (var rehab in topology.Rehabs)
            {
                if (FailedDatabaseInstanceOrNode(rehab, state) != DatabaseHealth.Bad)
                    goodMembers++;
            }
            return goodMembers;
        }

        private bool TryMoveToRehab(string dbName, DatabaseTopology topology, Dictionary<string, ClusterNodeStatusReport> current, string member)
        {
            DatabaseStatusReport dbStats = null;
            if (current.TryGetValue(member, out var nodeStats) &&
                nodeStats.Status == ClusterNodeStatusReport.ReportStatus.Ok &&
                nodeStats.Report.TryGetValue(dbName, out dbStats))
            {
                switch (dbStats.Status)
                {
                    case Loaded:
                    case Unloaded:
                    case Shutdown:
                    case NoChange:
                        return false;

                    case None:
                    case Loading:
                    case Faulted:
                        // continue the function
                        break;
                }
            }

            string reason;
            if (nodeStats == null)
            {
                reason = "Node in rehabilitation due to no status report in the latest cluster stats";
            }
            else if (nodeStats.Status != ClusterNodeStatusReport.ReportStatus.Ok)
            {
                switch (nodeStats.Status)
                {
                    case ClusterNodeStatusReport.ReportStatus.Timeout:
                        reason = $"Node in rehabilitation due to timeout reached trying to get stats from node.{Environment.NewLine}";
                        break;

                    case ClusterNodeStatusReport.ReportStatus.OutOfCredits:
                        reason = $"Node in rehabilitation because it run out of CPU credits.{Environment.NewLine}";
                        break;

                    case ClusterNodeStatusReport.ReportStatus.EarlyOutOfMemory:
                        reason = $"Node in rehabilitation because of early out of memory.{Environment.NewLine}";
                        break;

                    case ClusterNodeStatusReport.ReportStatus.HighDirtyMemory:
                        reason = $"Node in rehabilitation because of high dirty memory.{Environment.NewLine}";
                        break;

                    default:
                        reason = $"Node in rehabilitation due to last report status being '{nodeStats.Status}'.{Environment.NewLine}";
                        break;
                }
            }
            else if (nodeStats.Report.TryGetValue(dbName, out var stats) && stats.Status == Faulted)
            {
                reason = $"In rehabilitation because the DatabaseStatus for this node is {nameof(Faulted)}.{Environment.NewLine}";
            }
            else
            {
                reason = $"In rehabilitation because the node is reachable but had no report about the database (Status: {dbStats?.Status}).{Environment.NewLine}";
            }

            if (nodeStats?.Error != null)
            {
                reason += $". {nodeStats.Error}";
            }
            if (dbStats?.Error != null)
            {
                reason += $". {dbStats.Error}";
            }

            if (topology.Rehabs.Contains(member) == false)
            {
                topology.Members.Remove(member);
                topology.Rehabs.Add(member);
            }

            topology.DemotionReasons[member] = reason;
            topology.PromotablesStatus[member] = GetStatus();

            LogMessage($"Node {member} of database '{dbName}': {reason}", database: dbName);

            DatabasePromotionStatus GetStatus()
            {
                if (nodeStats != null)
                {
                    if (nodeStats.ServerReport.OutOfCpuCredits == true)
                        return DatabasePromotionStatus.OutOfCpuCredits;

                    if (nodeStats.ServerReport.EarlyOutOfMemory == true)
                        return DatabasePromotionStatus.EarlyOutOfMemory;

                    if (nodeStats.ServerReport.HighDirtyMemory == true)
                        return DatabasePromotionStatus.HighDirtyMemory;
                }

                return DatabasePromotionStatus.NotResponding;
            }

            return true;
        }

        private bool TryGetMentorNode(string dbName, DatabaseTopology topology, ClusterTopology clusterTopology, string promotable, out string mentorNode)
        {
            var url = clusterTopology.GetUrlFromTag(promotable);
            topology.PredefinedMentors.TryGetValue(promotable, out var mentor);
            var task = new PromotableTask(promotable, url, dbName, mentor);
            mentorNode = topology.WhoseTaskIsIt(_server.Engine.CurrentState, task, null);

            if (mentorNode == null)
            {
                // We are in passive mode and were kicked out of the cluster.
                return false;
            }

            return true;
        }

        private (bool Promote, string UpdateTopologyReason) TryPromote(DatabaseObservationState state, string mentorNode, string promotable)
        {
            var dbName = state.Name;
            var topology = state.DatabaseTopology;
            var current = state.Current;
            var previous = state.Previous;

            if (previous.TryGetValue(mentorNode, out var mentorPrevClusterStats) == false ||
                mentorPrevClusterStats.Report.TryGetValue(dbName, out var mentorPrevDbStats) == false)
            {
                LogMessage($"Can't find previous mentor {mentorNode} stats for node {promotable}", database: dbName);
                return (false, null);
            }

            if (previous.TryGetValue(promotable, out var promotablePrevClusterStats) == false ||
                promotablePrevClusterStats.Report.TryGetValue(dbName, out var promotablePrevDbStats) == false)
            {
                LogMessage($"Can't find previous stats for node {promotable}", database: dbName);
                return (false, null);
            }

            if (current.TryGetValue(mentorNode, out var mentorCurrClusterStats) == false ||
                mentorCurrClusterStats.Report.TryGetValue(dbName, out var mentorCurrDbStats) == false)
            {
                LogMessage($"Can't find current mentor {mentorNode} stats for node {promotable}", database: dbName);
                return (false, null);
            }

            if (current.TryGetValue(promotable, out var promotableClusterStats) == false ||
                promotableClusterStats.Report.TryGetValue(dbName, out var promotableDbStats) == false)
            {
                LogMessage($"Can't find current stats for node {promotable}", database: dbName);
                return (false, null);
            }

            if (promotableClusterStats.ServerReport.OutOfCpuCredits == true)
            {
                LogMessage($"Can't promote node {promotable}, it doesn't have enough CPU credits", database: dbName);
                return (false, null);
            }

            if (promotableClusterStats.ServerReport.EarlyOutOfMemory == true)
            {
                LogMessage($"Can't promote node {promotable}, it's in an early out of memory state", database: dbName);
                return (false, null);
            }

            if (promotableClusterStats.ServerReport.HighDirtyMemory == true)
            {
                LogMessage($"Can't promote node {promotable}, it's in high dirty memory state", database: dbName);
                return (false, null);
            }

            if (topology.Members.Count == topology.ReplicationFactor)
            {
                LogMessage($"Replication factor is reached", database: dbName);
                return (false, null);
            }

            var mentorsEtag = mentorPrevDbStats.LastEtag;
            if (mentorCurrDbStats.LastSentEtag.TryGetValue(promotable, out var lastSentEtag) == false)
            {
                LogMessage($"Can't find last sent etag of mentor {mentorNode} for {promotable}", database: dbName);
                return (false, null);
            }

            var timeDiff = mentorCurrClusterStats.LastSuccessfulUpdateDateTime - mentorPrevClusterStats.LastSuccessfulUpdateDateTime > 3 * _supervisorSamplePeriod;

            if (lastSentEtag < mentorsEtag || timeDiff)
            {
                var msg = $"The database '{dbName}' on {promotable} not ready to be promoted, because the mentor hasn't sent all of the documents yet." + Environment.NewLine +
                          $"Last sent Etag: {lastSentEtag:#,#;;0}" + Environment.NewLine +
                          $"Mentor's Etag: {mentorsEtag:#,#;;0}";

                LogMessage($"Mentor {mentorNode} hasn't sent all of the documents yet to {promotable} (time diff: {timeDiff}, sent etag: {lastSentEtag:#,#;;0}/{mentorsEtag:#,#;;0})", database: dbName);

                if (topology.DemotionReasons.TryGetValue(promotable, out var demotionReason) == false ||
                    msg.Equals(demotionReason) == false)
                {
                    topology.DemotionReasons[promotable] = msg;
                    topology.PromotablesStatus[promotable] = DatabasePromotionStatus.ChangeVectorNotMerged;
                    return (false, msg);
                }
                return (false, null);
            }

            var indexesCaughtUp = CheckIndexProgress(
                promotablePrevDbStats.LastEtag,
                promotablePrevDbStats.LastIndexStats,
                promotableDbStats.LastIndexStats,
                mentorCurrDbStats.LastIndexStats,
                out var reason);

            if (indexesCaughtUp)
            {
                LogMessage($"We try to promote the database '{dbName}' on {promotable} to be a full member", database: dbName);

                topology.PromotablesStatus.Remove(promotable);
                topology.DemotionReasons.Remove(promotable);

                return (true, $"Node {promotable} is up-to-date so promoting it to be member");
            }

            LogMessage($"The database '{dbName}' on {promotable} is not ready to be promoted, because {reason}{Environment.NewLine}", database: dbName);

            if (topology.PromotablesStatus.TryGetValue(promotable, out var currentStatus) == false
                || currentStatus != DatabasePromotionStatus.IndexNotUpToDate)
            {
                var msg = $"Node {promotable} not ready to be a member, because the indexes are not up-to-date";
                topology.PromotablesStatus[promotable] = DatabasePromotionStatus.IndexNotUpToDate;
                topology.DemotionReasons[promotable] = msg;
                return (false, msg);
            }
            return (false, null);
        }

        private void RemoveOtherNodesIfNeeded(DatabaseObservationState state, ref List<DeleteDatabaseCommand> deletions)
        {
            var topology = state.DatabaseTopology;
            var dbName = state.Name;
            var clusterTopology = state.ClusterTopology;

            if (topology.Members.Count < topology.ReplicationFactor)
                return;

            if (topology.Promotables.Count == 0 &&
                topology.Rehabs.Count == 0)
                return;

            var nodesToDelete = new List<string>();
            var mentorChangeVector = new Dictionary<string, string>();

            foreach (var node in topology.Promotables.Concat(topology.Rehabs))
            {
                if (TryGetMentorNode(dbName, topology, clusterTopology, node, out var mentorNode) == false ||
                    state.Current.TryGetValue(mentorNode, out var mentorStats) == false ||
                    mentorStats.Report.TryGetValue(dbName, out var dbReport) == false)
                {
                    continue;
                }
                if (state.ReadDeletionInProgress()?.ContainsKey(node) == true)
                {
                    continue;
                }
                nodesToDelete.Add(node);
                mentorChangeVector.Add(node, dbReport.DatabaseChangeVector);
            }

            if (nodesToDelete.Count == 0)
                return;

            LogMessage($"We reached the replication factor on database '{dbName}', so we try to remove redundant nodes from {string.Join(", ", nodesToDelete)}.", database: dbName);

            var deletionCmd = new DeleteDatabaseCommand(dbName, RaftIdGenerator.NewId())
            {
                ErrorOnDatabaseDoesNotExists = false,
                FromNodes = nodesToDelete.ToArray(),
                HardDelete = _hardDeleteOnReplacement,
                UpdateReplicationFactor = false,
            };

            if (deletions == null)
                deletions = new List<DeleteDatabaseCommand>();
            deletions.Add(deletionCmd);
        }

        private static List<string> GetPendingDeleteNodes(Dictionary<string, DeletionInProgressStatus> deletionInProgress)
        {
            var alreadyInDeletionProgress = new List<string>();
            alreadyInDeletionProgress.AddRange(deletionInProgress?.Keys);
            return alreadyInDeletionProgress;
        }

        private enum DatabaseHealth
        {
            NotEnoughInfo,
            Bad,
            Good
        }

        private DatabaseHealth FailedDatabaseInstanceOrNode(
            string node,
            DatabaseObservationState state)
        {
            var clusterTopology = state.ClusterTopology;
            var current = state.Current;
            var db = state.Name;

            if (clusterTopology.Contains(node) == false) // this node is no longer part of the *Cluster* databaseTopology and need to be replaced.
                return DatabaseHealth.Bad;

            var hasCurrent = current.TryGetValue(node, out var currentNodeStats);

            // Wait until we have more info
            if (hasCurrent == false)
                return DatabaseHealth.NotEnoughInfo;

            // if server is down we should reassign
            if (DateTime.UtcNow - currentNodeStats.LastSuccessfulUpdateDateTime > _breakdownTimeout)
            {
                if (DateTime.UtcNow - StartTime < _breakdownTimeout)
                    return DatabaseHealth.NotEnoughInfo;

                return DatabaseHealth.Bad;
            }

            if (currentNodeStats.LastGoodDatabaseStatus.TryGetValue(db, out var lastGoodTime) == false)
            {
                // here we have a problem, the databaseTopology says that the db needs to be in the node, but the node
                // doesn't know that the db is on it, that probably indicate some problem and we'll move it
                // to another node to resolve it.
                return DatabaseHealth.NotEnoughInfo;
            }
            if (lastGoodTime == default(DateTime) || lastGoodTime == DateTime.MinValue)
                return DatabaseHealth.NotEnoughInfo;

            return DateTime.UtcNow - lastGoodTime > _breakdownTimeout ? DatabaseHealth.Bad : DatabaseHealth.Good;
        }

        private bool TryFindFitNode(string badNode, DatabaseObservationState state, out string bestNode)
        {
            bestNode = null;
            var dbCount = int.MaxValue;

            var topology = state.DatabaseTopology;
            var clusterTopology = state.ClusterTopology;
            var current = state.Current;
            var db = state.Name;

            var databaseNodes = topology.AllNodes.ToList();

            if (topology.Members.Count == 0) // no one can be used as mentor
                return false;

            foreach (var node in clusterTopology.AllNodes.Keys)
            {
                if (databaseNodes.Contains(node))
                    continue;

                if (FailedDatabaseInstanceOrNode(node, state) == DatabaseHealth.Bad)
                    continue;

                if (current.TryGetValue(node, out var nodeReport) == false)
                {
                    if (bestNode == null)
                        bestNode = node;
                    continue;
                }

                if (dbCount > nodeReport.Report.Count)
                {
                    dbCount = nodeReport.Report.Count;
                    bestNode = node;
                }
            }

            if (bestNode == null)
            {
                LogMessage($"The database '{db}' on {badNode} has not responded for a long time, but there is no free node to reassign it.", database: db);
                return false;
            }
            LogMessage($"The database '{db}' on {badNode} has not responded for a long time, so we reassign it to {bestNode}.", database: db);

            return true;
        }

        private string FindMostUpToDateNode(List<string> nodes, string database, Dictionary<string, ClusterNodeStatusReport> current)
        {
            var updated = nodes[0];
            var highestChangeVectors = current[updated].Report[database].DatabaseChangeVector;
            var maxDocsCount = current[updated].Report[database].NumberOfDocuments;
            for (var index = 1; index < nodes.Count; index++)
            {
                var node = nodes[index];
                var report = current[node].Report[database];
                var cv = report.DatabaseChangeVector;
                var status = ChangeVectorUtils.GetConflictStatus(cv, highestChangeVectors);
                if (status == ConflictStatus.Update)
                {
                    highestChangeVectors = cv;
                }
                // In conflict we need to choose between 2 nodes that are not synced.
                // So we take the one with the most documents.
                if (status == ConflictStatus.Conflict)
                {
                    if (report.NumberOfDocuments > maxDocsCount)
                    {
                        highestChangeVectors = cv;
                        maxDocsCount = report.NumberOfDocuments;
                        updated = node;
                    }
                }
            }
            return updated;
        }

        private static bool CheckIndexProgress(
            long lastPrevEtag,
            Dictionary<string, DatabaseStatusReport.ObservedIndexStatus> previous,
            Dictionary<string, DatabaseStatusReport.ObservedIndexStatus> current,
            Dictionary<string, DatabaseStatusReport.ObservedIndexStatus> mentor,
            out string reason)
        {
            /*
            Here we are being a bit tricky. A database node is consider ready for promotion when
            it's replication is one cycle behind its mentor, but there are still indexes to consider.

            If we just replicate a whole bunch of stuff and indexes are catching up, we want to only
            promote when the indexes actually caught up. We do that by also requiring that all indexes
            will be either fully caught up (non stale) or that they are at most a single cycle behind.

            This is check by looking at the global etag from the previous round, and comparing it to the
            last etag that each index indexed in the current round. Note that technically, we need to compare
            on a per collection basis, but we can avoid it by noting that if the collection's last etag is
            not beyond the previous max etag, then the index will therefor not be non stale.

             */

            foreach (var mentorIndex in mentor)
            {
                // we go over all of the mentor indexes to validated that the promotable has them.
                // Since we don't save in the state machine the definition of side-by-side indexes, we will skip them, because
                // the promotable don't have them.

                if (mentorIndex.Value.IsSideBySide)
                    continue;

                if (mentorIndex.Value.State == IndexState.Idle)
                    continue;

                if (mentor.TryGetValue(Constants.Documents.Indexing.SideBySideIndexNamePrefix + mentorIndex.Key, out var mentorIndexStats) == false)
                {
                    mentorIndexStats = mentorIndex.Value;
                }

                if (previous.TryGetValue(mentorIndex.Key, out _) == false)
                {
                    reason = $"Index '{mentorIndex.Key}' is missing";
                    return false;
                }

                if (current.TryGetValue(mentorIndex.Key, out var currentIndexStats) == false)
                {
                    reason = $"Index '{mentorIndex.Key}' is missing";
                    return false;
                }

                if (currentIndexStats.State == IndexState.Error)
                {
                    if (mentorIndexStats.State == IndexState.Error)
                        continue;
                    reason = $"Index '{mentorIndex.Key}' is in state '{currentIndexStats.State}'";
                    return false;
                }

                if (currentIndexStats.IsStale == false)
                    continue;

                if (mentorIndexStats.LastIndexedEtag == (long)Index.IndexProgressStatus.Faulty)
                {
                    continue; // skip the check for faulty indexes
                }

                if (currentIndexStats.State == IndexState.Disabled)
                    continue;

                var lastIndexEtag = currentIndexStats.LastIndexedEtag;
                if (lastPrevEtag > lastIndexEtag)
                {
                    reason = $"Index '{mentorIndex.Key}' is in state '{currentIndexStats.State}' and not up-to-date (prev: {lastPrevEtag:#,#;;0}, current: {lastIndexEtag:#,#;;0}).";
                    return false;
                }
            }

            reason = null;
            return true;
        }

        private Task<(long Index, object Result)> UpdateTopology(UpdateTopologyCommand cmd)
        {
            if (_engine.LeaderTag != _server.NodeTag)
            {
                throw new NotLeadingException("This node is no longer the leader, so we abort updating the database databaseTopology");
            }

            return _engine.PutAsync(cmd);
        }

        private Task<(long Index, object Result)> Delete(DeleteDatabaseCommand cmd)
        {
            if (_engine.LeaderTag != _server.NodeTag)
            {
                throw new NotLeadingException("This node is no longer the leader, so we abort the deletion command");
            }
            return _engine.PutAsync(cmd);
        }

        public void Dispose()
        {
            _cts.Cancel();

            try
            {
                if (_observe.Join((int)TimeSpan.FromSeconds(30).TotalMilliseconds) == false)
                {
                    throw new ObjectDisposedException($"Cluster observer on node {_nodeTag} still running and can't be closed");
                }
            }
            finally
            {
                _cts.Dispose();
            }
        }

        internal class DatabaseObservationState
        {
            public string Name;
            public DatabaseTopology DatabaseTopology;
            public Dictionary<string, ClusterNodeStatusReport> Current;
            public Dictionary<string, ClusterNodeStatusReport> Previous;
            public ClusterTopology ClusterTopology;

            public RawDatabaseRecord RawDatabase;

            public long ReadTruncatedClusterTransactionCommandsCount()
            {
                RawDatabase.Raw.TryGet(nameof(DatabaseRecord.TruncatedClusterTransactionCommandsCount), out long count);
                return count;
            }

            public bool TryGetAutoIndex(string name, out AutoIndexDefinition definition)
            {
                BlittableJsonReaderObject autoDefinition = null;
                definition = null;
                RawDatabase.Raw.TryGet(nameof(DatabaseRecord.AutoIndexes), out BlittableJsonReaderObject autoIndexes);
                if (autoIndexes?.TryGet(name, out autoDefinition) == false)
                    return false;

                definition = JsonDeserializationServer.AutoIndexDefinition(autoDefinition);
                return true;
            }

            public Dictionary<string, DeletionInProgressStatus> ReadDeletionInProgress()
            {
                return RawDatabase.DeletionInProgress;
            }

            public bool ReadDatabaseDisabled()
            {
                return RawDatabase.IsDisabled;
            }

            public bool ReadRestoringInProgress()
            {
                return RawDatabase.DatabaseState == DatabaseStateStatus.RestoreInProgress;
            }

            public Dictionary<string, string> ReadSettings()
            {
                return RawDatabase.Settings;
            }

            public DatabaseStatusReport GetCurrentDatabaseReport(string node)
            {
                if (Current.TryGetValue(node, out var report) == false)
                    return null;

                if (report.Report.TryGetValue(Name, out var databaseReport) == false)
                    return null;

                return databaseReport;
            }

            public DatabaseStatusReport GetPreviousDatabaseReport(string node)
            {
                if (Previous.TryGetValue(node, out var report) == false)
                    return null;

                if (report.Report.TryGetValue(Name, out var databaseReport) == false)
                    return null;

                return databaseReport;
            }
        }
    }
}
