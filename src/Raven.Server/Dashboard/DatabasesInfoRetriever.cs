﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Backups;
using Raven.Client.Documents.Operations.ETL;
using Raven.Client.Documents.Operations.ETL.OLAP;
using Raven.Client.Documents.Operations.ETL.SQL;
using Raven.Client.Documents.Operations.ETL.ElasticSearch;
using Raven.Client.Documents.Operations.Replication;
using Raven.Client.Documents.Subscriptions;
using Raven.Client.Json.Serialization;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Raven.Client.Util;
using Raven.Server.Config.Categories;
using Raven.Server.Documents;
using Raven.Server.Documents.ETL;
using Raven.Server.Documents.Replication;
using Raven.Server.Json;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Raven.Server.Storage;
using Raven.Server.Utils;
using Sparrow;
using Sparrow.Json;
using Sparrow.Server.Utils;
using Voron;
using Size = Sparrow.Size;

namespace Raven.Server.Dashboard
{
    public class DatabasesInfoRetriever : MetricCacher
    {
        private readonly ServerStore _serverStore;
        private readonly CanAccessDatabase _canAccessDatabase;
        private const string DatabasesInfoKey = "DatabasesInfo";

        public DatabasesInfoRetriever(ServerStore serverStore, CanAccessDatabase canAccessDatabase)
        {
            _serverStore = serverStore;
            _canAccessDatabase = canAccessDatabase;

            Initialize();
        }

        public static TimeSpan RefreshRate { get; } = TimeSpan.FromSeconds(3);

        public void Initialize()
        {
            Register(DatabasesInfoKey, TimeSpan.FromSeconds(3), CreateDatabasesInfo);
        }

        private List<AbstractDashboardNotification> CreateDatabasesInfo()
        {
            List<AbstractDashboardNotification> result = FetchDatabasesInfo(_serverStore, _canAccessDatabase, true, _serverStore.ServerShutdown).ToList();

            return result;
        }

        public DatabasesInfo GetDatabasesInfo()
        {
            return GetValue<List<AbstractDashboardNotification>>(DatabasesInfoKey).OfType<DatabasesInfo>().First();
        }

        public DatabasesOngoingTasksInfo GetDatabasesOngoingTasksInfo()
        {
            return GetValue<List<AbstractDashboardNotification>>(DatabasesInfoKey).OfType<DatabasesOngoingTasksInfo>().First();
        }
        
        public IndexingSpeed GetIndexingSpeed()
        {
            return GetValue<List<AbstractDashboardNotification>>(DatabasesInfoKey).OfType<IndexingSpeed>().First();
        }

        public TrafficWatch GetTrafficWatch()
        {
            return GetValue<List<AbstractDashboardNotification>>(DatabasesInfoKey).OfType<TrafficWatch>().First();
        }

        public DrivesUsage GetDrivesUsage()
        {
            return GetValue<List<AbstractDashboardNotification>>(DatabasesInfoKey).OfType<DrivesUsage>().First();
        }

        public static IEnumerable<AbstractDashboardNotification> FetchDatabasesInfo(ServerStore serverStore, CanAccessDatabase isValidFor, bool collectOngoingTasks, CancellationToken token)
        {
            var databasesInfo = new DatabasesInfo();
            var databasesOngoingTasksInfo = new DatabasesOngoingTasksInfo();
            var indexingSpeed = new IndexingSpeed();
            var trafficWatch = new TrafficWatch();
            var drivesUsage = new DrivesUsage();

            trafficWatch.AverageRequestDuration = serverStore.Server.Metrics.Requests.AverageDuration.GetRate();

            using (serverStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            using (context.OpenReadTransaction())
            {
                // 1. Fetch databases info
                foreach (var databaseTuple in serverStore.Cluster.ItemsStartingWith(context, Constants.Documents.Prefix, 0, long.MaxValue))
                {
                    var databaseName = databaseTuple.ItemName.Substring(Constants.Documents.Prefix.Length);
                    if (token.IsCancellationRequested)
                        yield break;

                    if (isValidFor != null && isValidFor(databaseName, false) == false)
                        continue;

                    if (serverStore.DatabasesLandlord.DatabasesCache.TryGetValue(databaseName, out var databaseTask) == false)
                    {
                        // database does not exist on this server, is offline or disabled
                        SetOfflineDatabaseInfo(serverStore, context, databaseName, databasesInfo, drivesUsage, disabled: DatabasesLandlord.IsDatabaseDisabled(databaseTuple.Value));
                        continue;
                    }

                    try
                    {
                        var databaseOnline = IsDatabaseOnline(databaseTask, out var database);
                        if (databaseOnline == false)
                        {
                            SetOfflineDatabaseInfo(serverStore, context, databaseName, databasesInfo, drivesUsage, disabled: false);
                            continue;
                        }

                        var rate = (int)RefreshRate.TotalSeconds;
                        
                        var indexingSpeedItem = new IndexingSpeedItem
                        {
                            Database = database.Name,
                            IndexedPerSecond = database.Metrics.MapIndexes.IndexedPerSec.GetRate(rate),
                            MappedPerSecond = database.Metrics.MapReduceIndexes.MappedPerSec.GetRate(rate),
                            ReducedPerSecond = database.Metrics.MapReduceIndexes.ReducedPerSec.GetRate(rate)
                        };
                        indexingSpeed.Items.Add(indexingSpeedItem);

                        var replicationFactor = GetReplicationFactor(databaseTuple.Value);
                        var documentsStorage = database.DocumentsStorage;
                        var indexStorage = database.IndexStore;

                        var trafficWatchItem = new TrafficWatchItem
                        {
                            Database = database.Name,
                            RequestsPerSecond = (int)Math.Ceiling(database.Metrics.Requests.RequestsPerSec.GetRate(rate)),
                            AverageRequestDuration = database.Metrics.Requests.AverageDuration.GetRate(),
                            DocumentWritesPerSecond = (int)Math.Ceiling(database.Metrics.Docs.PutsPerSec.GetRate(rate)),
                            AttachmentWritesPerSecond = (int)Math.Ceiling(database.Metrics.Attachments.PutsPerSec.GetRate(rate)),
                            CounterWritesPerSecond = (int)Math.Ceiling(database.Metrics.Counters.PutsPerSec.GetRate(rate)),
                            TimeSeriesWritesPerSecond = (int)Math.Ceiling(database.Metrics.TimeSeries.PutsPerSec.GetRate(rate)),
                            DocumentsWriteBytesPerSecond = database.Metrics.Docs.BytesPutsPerSec.GetRate(rate),
                            AttachmentsWriteBytesPerSecond = database.Metrics.Attachments.BytesPutsPerSec.GetRate(rate),
                            CountersWriteBytesPerSecond = database.Metrics.Counters.BytesPutsPerSec.GetRate(rate),
                            TimeSeriesWriteBytesPerSecond = database.Metrics.TimeSeries.BytesPutsPerSec.GetRate(rate)
                        };
                        trafficWatch.Items.Add(trafficWatchItem);
             
                        var ongoingTasksInfoItem = GetOngoingTasksInfoItem(database, serverStore, context, out var ongoingTasksCount);
                        if (collectOngoingTasks)
                        {
                            databasesOngoingTasksInfo.Items.Add(ongoingTasksInfoItem);
                        }

                        // TODO: RavenDB-17004 - hash should report on all relevant info 
                        var currentEnvironmentsHash = database.GetEnvironmentsHash();

                        if (CachedDatabaseInfo.TryGetValue(database.Name, out var item) &&
                            item.Hash == currentEnvironmentsHash &&
                            item.Item.OngoingTasksCount == ongoingTasksCount)
                        {
                            databasesInfo.Items.Add(item.Item);

                            if (item.NextDiskSpaceCheck < SystemTime.UtcNow)
                            {
                                item.MountPoints = new List<Client.ServerWide.Operations.MountPointUsage>();
                                DiskUsageCheck(item, database, drivesUsage, token);
                            }
                            else
                            {
                                foreach (var cachedMountPoint in item.MountPoints)
                                {
                                    UpdateMountPoint(database.Configuration.Storage, cachedMountPoint, database.Name, drivesUsage);
                                }
                            }
                        }
                        else
                        {
                            using (documentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext documentsContext))
                            using (documentsContext.OpenReadTransaction())
                            {
                                var databaseInfoItem = new DatabaseInfoItem
                                {
                                    Database = database.Name,
                                    DocumentsCount = documentsStorage.GetNumberOfDocuments(documentsContext),
                                    IndexesCount = database.IndexStore.Count,
                                    AlertsCount = database.NotificationCenter.GetAlertCount(),
                                    PerformanceHintsCount = database.NotificationCenter.GetPerformanceHintCount(),
                                    ReplicationFactor = replicationFactor,
                                    ErroredIndexesCount = indexStorage.GetIndexes().Count(index => index.State == IndexState.Error),
                                    IndexingErrorsCount = indexStorage.GetIndexes().Sum(index => index.GetErrorCount()),
                                    BackupInfo = database.PeriodicBackupRunner?.GetBackupInfo(context),
                                    OngoingTasksCount = ongoingTasksCount,
                                    Online = true
                                };
                                
                                databasesInfo.Items.Add(databaseInfoItem);
                                
                                CachedDatabaseInfo[database.Name] = item = new DatabaseInfoCache
                                {
                                    Hash = currentEnvironmentsHash,
                                    Item = databaseInfoItem
                                };
                            }

                            DiskUsageCheck(item, database, drivesUsage, token);
                        }
                    }
                    catch (Exception)
                    {
                        SetOfflineDatabaseInfo(serverStore, context, databaseName, databasesInfo, drivesUsage, disabled: false);
                    }
                }

                // 2. Fetch <system> info
                if (isValidFor == null)
                {
                    var currentSystemHash = serverStore._env.CurrentReadTransactionId;
                    var cachedSystemInfoCopy = CachedSystemInfo;

                    if (currentSystemHash != cachedSystemInfoCopy.Hash || cachedSystemInfoCopy.NextDiskSpaceCheck < SystemTime.UtcNow)
                    {
                        var systemInfo = new SystemInfoCache()
                        {
                            Hash = currentSystemHash,
                            NextDiskSpaceCheck = SystemTime.UtcNow.AddSeconds(30),
                            MountPoints = new List<Client.ServerWide.Operations.MountPointUsage>()
                        };

                        // Get new data
                        var systemEnv = new StorageEnvironmentWithType("<System>", StorageEnvironmentWithType.StorageEnvironmentType.System, serverStore._env);
                        var systemMountPoints = serverStore.GetMountPointUsageDetailsFor(systemEnv, includeTempBuffers: true);

                        foreach (var systemPoint in systemMountPoints)
                        {
                            UpdateMountPoint(serverStore.Configuration.Storage, systemPoint, "<System>", drivesUsage);
                            systemInfo.MountPoints.Add(systemPoint);
                        }

                        // Update the cache
                        Interlocked.Exchange(ref CachedSystemInfo, systemInfo);
                    }
                    else
                    {
                        // Use existing data but update IO stats (which has separate cache)
                        foreach (var systemPoint in cachedSystemInfoCopy.MountPoints)
                        {
                            var driveInfo = systemPoint.DiskSpaceResult.DriveName;
                            var ioStatsResult = serverStore.Server.DiskStatsGetter.Get(driveInfo);
                            if (ioStatsResult != null)
                                systemPoint.IoStatsResult = ServerStore.FillIoStatsResult(ioStatsResult); 
                            UpdateMountPoint(serverStore.Configuration.Storage, systemPoint, "<System>", drivesUsage);
                        }
                    }
                }
            }

            yield return databasesInfo;
            yield return indexingSpeed;
            yield return trafficWatch;
            yield return drivesUsage;
            
            if (collectOngoingTasks)
            {
                yield return databasesOngoingTasksInfo;
            }
        }

        private static DatabaseOngoingTasksInfoItem GetOngoingTasksInfoItem(DocumentDatabase database,  ServerStore serverStore, TransactionOperationContext context, out long ongoingTasksCount)
        {
            var dbRecord = database.ReadDatabaseRecord();

            var extRepCount = dbRecord.ExternalReplications.Count;
            long extRepCountOnNode = GetTaskCountOnNode<ExternalReplication>(database, dbRecord, serverStore, dbRecord.ExternalReplications,
                task => ReplicationLoader.GetExternalReplicationState(serverStore, database.Name, task.TaskId));

            long replicationHubCountOnNode = 0;
            var replicationHubCount = database.ReplicationLoader.OutgoingHandlers.Count(x => x.IsPullReplicationAsHub);
            replicationHubCountOnNode += replicationHubCount;

            var replicationSinkCount = dbRecord.SinkPullReplications.Count;
            long replicationSinkCountOnNode = GetTaskCountOnNode<PullReplicationAsSink>(database, dbRecord, serverStore, dbRecord.SinkPullReplications, task => null);
            
            var ravenEtlCount = database.EtlLoader.RavenDestinations.Count;
            long ravenEtlCountOnNode = GetTaskCountOnNode<RavenEtlConfiguration>(database, dbRecord, serverStore, database.EtlLoader.RavenDestinations,
                task => EtlLoader.GetProcessState(task.Transforms, database, task.Name));
            
            var sqlEtlCount = database.EtlLoader.SqlDestinations.Count;
            long sqlEtlCountOnNode = GetTaskCountOnNode<SqlEtlConfiguration>(database, dbRecord, serverStore, database.EtlLoader.SqlDestinations,
                task => EtlLoader.GetProcessState(task.Transforms, database, task.Name));
            
            var elasticSearchEtlCount = database.EtlLoader.ElasticSearchDestinations.Count;
            long elasticSearchEtlCountOnNode = GetTaskCountOnNode<ElasticSearchEtlConfiguration>(database, dbRecord, serverStore, database.EtlLoader.ElasticSearchDestinations,
                task => EtlLoader.GetProcessState(task.Transforms, database,task.Name));

            var olapEtlCount = database.EtlLoader.OlapDestinations.Count;
            long olapEtlCountOnNode = GetTaskCountOnNode<OlapEtlConfiguration>(database, dbRecord, serverStore, database.EtlLoader.OlapDestinations,
                task => EtlLoader.GetProcessState(task.Transforms, database,task.Name));
            
            var periodicBackupCount = database.PeriodicBackupRunner.PeriodicBackups.Count;
            long periodicBackupCountOnNode = GetTaskCountOnNode<PeriodicBackupConfiguration>(database, dbRecord, serverStore,
                database.PeriodicBackupRunner.PeriodicBackups.Select(x => x.Configuration),
                task => database.PeriodicBackupRunner.GetBackupStatus(task.TaskId),
                task => task.Name.StartsWith("Server Wide") == false);
            
            var subscriptionCount = database.SubscriptionStorage.GetAllSubscriptionsCount();
            long subscriptionCountOnNode = GetSubscriptionCountOnNode(database, dbRecord, serverStore, context);

            ongoingTasksCount = extRepCount + replicationHubCount + replicationSinkCount +
                                ravenEtlCount + sqlEtlCount + elasticSearchEtlCount + olapEtlCount + periodicBackupCount + subscriptionCount;
            
            return new DatabaseOngoingTasksInfoItem()
            {
                Database = database.Name,
                ExternalReplicationCount = extRepCountOnNode,
                ReplicationHubCount = replicationHubCountOnNode,
                ReplicationSinkCount = replicationSinkCountOnNode,
                RavenEtlCount = ravenEtlCountOnNode,
                SqlEtlCount = sqlEtlCountOnNode,
                ElasticSearchEtlCount = elasticSearchEtlCountOnNode,
                OlapEtlCount = olapEtlCountOnNode,
                PeriodicBackupCount = periodicBackupCountOnNode,
                SubscriptionCount = subscriptionCountOnNode
            };
        }
        
        private static long GetTaskCountOnNode<T>(DocumentDatabase database,
            DatabaseRecord dbRecord, ServerStore serverStore, IEnumerable<IDatabaseTask> tasks,
            Func<T, IDatabaseTaskStatus> getTaskStatus, Func<T, bool> filter = null) where T: IDatabaseTask
        {
            long taskCountOnNode = 0;
            foreach (var task in tasks)
            {
                if (filter != null && filter((T)task) == false)
                    continue;

                var state = getTaskStatus((T)task);
                var taskTag = database.WhoseTaskIsIt(dbRecord.Topology, task, state);
                if (serverStore.NodeTag == taskTag)
                {
                    taskCountOnNode++;
                }
            }
            return taskCountOnNode;
        }
        
        private static long GetSubscriptionCountOnNode(DocumentDatabase database, DatabaseRecord dbRecord, ServerStore serverStore, TransactionOperationContext context) 
        {
            long taskCountOnNode = 0;
            foreach (var keyValue in ClusterStateMachine.ReadValuesStartingWith(context, SubscriptionState.SubscriptionPrefix(database.Name)))
            {
                var subscriptionState = JsonDeserializationClient.SubscriptionState(keyValue.Value);
                var taskTag = database.WhoseTaskIsIt(dbRecord.Topology, subscriptionState, subscriptionState);
                if (serverStore.NodeTag == taskTag)
                {
                    taskCountOnNode++;
                }
            }

            return taskCountOnNode;
        }

        private static readonly ConcurrentDictionary<string, DatabaseInfoCache> CachedDatabaseInfo =
            new ConcurrentDictionary<string, DatabaseInfoCache>(StringComparer.OrdinalIgnoreCase);

        private class DatabaseInfoCache
        {
            public long Hash;
            public DatabaseInfoItem Item;
            public List<Client.ServerWide.Operations.MountPointUsage> MountPoints = new List<Client.ServerWide.Operations.MountPointUsage>();
            public DateTime NextDiskSpaceCheck;
        }

        private static SystemInfoCache CachedSystemInfo = new SystemInfoCache();

        private class SystemInfoCache
        {
            public long Hash;
            public List<Client.ServerWide.Operations.MountPointUsage> MountPoints = new List<Client.ServerWide.Operations.MountPointUsage>();
            public DateTime NextDiskSpaceCheck;
        }

        private static void DiskUsageCheck(DatabaseInfoCache item, DocumentDatabase database, DrivesUsage drivesUsage, CancellationToken token)
        {
            foreach (var mountPointUsage in database.GetMountPointsUsage(includeTempBuffers: true))
            {
                if (token.IsCancellationRequested)
                    return;

                UpdateMountPoint(database.Configuration.Storage, mountPointUsage, database.Name, drivesUsage);
                item.MountPoints.Add(mountPointUsage);
            }

            item.NextDiskSpaceCheck = SystemTime.UtcNow.AddSeconds(30);
        }

        private static void UpdateMountPoint(StorageConfiguration storageConfiguration, Client.ServerWide.Operations.MountPointUsage mountPointUsage,
            string databaseName, DrivesUsage drivesUsage)
        {
            var mountPoint = mountPointUsage.DiskSpaceResult.DriveName;
            var usage = drivesUsage.Items.FirstOrDefault(x => x.MountPoint == mountPoint);
            if (usage == null)
            {
                usage = new MountPointUsage
                {
                    MountPoint = mountPoint,
                };
                drivesUsage.Items.Add(usage);
            }

            usage.VolumeLabel = mountPointUsage.DiskSpaceResult.VolumeLabel;
            usage.FreeSpace = mountPointUsage.DiskSpaceResult.TotalFreeSpaceInBytes;
            usage.TotalCapacity = mountPointUsage.DiskSpaceResult.TotalSizeInBytes;
            usage.IoStatsResult = mountPointUsage.IoStatsResult;
            usage.IsLowSpace = StorageSpaceMonitor.IsLowSpace(new Size(usage.FreeSpace, SizeUnit.Bytes), new Size(usage.TotalCapacity, SizeUnit.Bytes), storageConfiguration, out string _);

            var existingDatabaseUsage = usage.Items.FirstOrDefault(x => x.Database == databaseName);
            if (existingDatabaseUsage == null)
            {
                existingDatabaseUsage = new DatabaseDiskUsage
                {
                    Database = databaseName
                };
                usage.Items.Add(existingDatabaseUsage);
            }

            existingDatabaseUsage.Size += mountPointUsage.UsedSpace;
            existingDatabaseUsage.TempBuffersSize += mountPointUsage.UsedSpaceByTempBuffers;
        }

        private static void SetOfflineDatabaseInfo(
            ServerStore serverStore,
            TransactionOperationContext context,
            string databaseName,
            DatabasesInfo existingDatabasesInfo,
            DrivesUsage existingDrivesUsage,
            bool disabled)
        {
            using (var databaseRecord = serverStore.Cluster.ReadRawDatabaseRecord(context, databaseName))
            {
                if (databaseRecord == null)
                {
                    // database doesn't exist
                    return;
                }

                var databaseTopology = databaseRecord.Topology;

                var irrelevant = databaseTopology == null ||
                                 databaseTopology.AllNodes.Contains(serverStore.NodeTag) == false;
                var databaseInfoItem = new DatabaseInfoItem
                {
                    Database = databaseName,
                    Online = false,
                    Disabled = disabled,
                    Irrelevant = irrelevant
                };

                if (irrelevant == false)
                {
                    // nothing to fetch if irrelevant on this node
                    UpdateDatabaseInfo(databaseRecord, serverStore, databaseName, existingDrivesUsage, databaseInfoItem);
                }

                existingDatabasesInfo.Items.Add(databaseInfoItem);
            }
        }

        private static void UpdateDatabaseInfo(RawDatabaseRecord databaseRecord, ServerStore serverStore, string databaseName, DrivesUsage existingDrivesUsage,
            DatabaseInfoItem databaseInfoItem)
        {
            DatabaseInfo databaseInfo = null;
            if (serverStore.DatabaseInfoCache.TryGet(databaseName, databaseInfoJson =>
            {
                databaseInfo = JsonDeserializationServer.DatabaseInfo(databaseInfoJson);
            }) == false)
                return;

            Debug.Assert(databaseInfo != null);
            var databaseTopology = databaseRecord.Topology;
            var indexesCount = databaseRecord.CountOfIndexes;

            databaseInfoItem.DocumentsCount = databaseInfo.DocumentsCount ?? 0;
            databaseInfoItem.IndexesCount = databaseInfo.IndexesCount ?? indexesCount;
            databaseInfoItem.ReplicationFactor = databaseTopology?.ReplicationFactor ?? databaseInfo.ReplicationFactor;
            databaseInfoItem.ErroredIndexesCount = databaseInfo.IndexingErrors ?? 0;

            if (databaseInfo.MountPointsUsage == null)
                return;

            foreach (var mountPointUsage in databaseInfo.MountPointsUsage)
            {
                var driveName = mountPointUsage.DiskSpaceResult.DriveName;
                var diskSpaceResult = DiskUtils.GetDiskSpaceInfo(
                    mountPointUsage.DiskSpaceResult.DriveName,
                    new DriveInfoBase
                    {
                        DriveName = driveName
                    });

                if (diskSpaceResult != null)
                {
                    // update the latest drive info
                    mountPointUsage.DiskSpaceResult = new Client.ServerWide.Operations.DiskSpaceResult
                    {
                        DriveName = diskSpaceResult.DriveName,
                        VolumeLabel = diskSpaceResult.VolumeLabel,
                        TotalFreeSpaceInBytes = diskSpaceResult.TotalFreeSpace.GetValue(SizeUnit.Bytes),
                        TotalSizeInBytes = diskSpaceResult.TotalSize.GetValue(SizeUnit.Bytes)
                    };
                }
                
                var diskStatsResult = serverStore.Server.DiskStatsGetter.Get(driveName);
                if (diskStatsResult != null)
                {
                    mountPointUsage.IoStatsResult = new IoStatsResult
                    {
                        IoReadOperations = diskStatsResult.IoReadOperations,
                        IoWriteOperations = diskStatsResult.IoWriteOperations,
                        ReadThroughputInKb = diskStatsResult.ReadThroughput.GetValue(SizeUnit.Kilobytes),
                        WriteThroughputInKb = diskStatsResult.WriteThroughput.GetValue(SizeUnit.Kilobytes),
                        QueueLength = diskStatsResult.QueueLength,
                    };
                }
                
                UpdateMountPoint(serverStore.Configuration.Storage, mountPointUsage, databaseName, existingDrivesUsage);
            }
        }

        private static int GetReplicationFactor(BlittableJsonReaderObject databaseRecordBlittable)
        {
            if (databaseRecordBlittable.TryGet(nameof(DatabaseRecord.Topology), out BlittableJsonReaderObject topology) == false)
                return 1;

            if (topology.TryGet(nameof(DatabaseTopology.ReplicationFactor), out int replicationFactor) == false)
                return 1;

            return replicationFactor;
        }

        private static bool IsDatabaseOnline(Task<DocumentDatabase> databaseTask, out DocumentDatabase database)
        {
            if (databaseTask.IsCanceled || databaseTask.IsFaulted || databaseTask.IsCompleted == false)
            {
                database = null;
                return false;
            }

            database = databaseTask.Result;
            return database.DatabaseShutdown.IsCancellationRequested == false;
        }
    }
}
