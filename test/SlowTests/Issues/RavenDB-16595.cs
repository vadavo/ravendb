﻿using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FastTests;
using FastTests.Server.Replication;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Backups;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Database;
using Raven.Client.ServerWide.Operations;
using Raven.Server;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Issues
{
    public class RavenDB_16595 : ReplicationTestBase
    {
        public RavenDB_16595(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public async Task Able_To_Expand_Database_Group_After_Tombstone_Clean()
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");
            var (nodes, leader) = await CreateRaftCluster(2, watcherCluster: true);

            using var store1 = GetDocumentStore(new Options { Server = leader, ReplicationFactor = 1 });

            // create 5 doc -> delete 4
            List<User> users = new List<User>();

            using (var session = store1.OpenSession())
            {
                for (int i = 0; i < 5; i++)
                {
                    var u = new User()
                    {
                        Name = $"Shahar{i}"
                    };
                    users.Add(u);
                    session.Store(u);
                }
                session.SaveChanges();
            }
            using (var session = store1.OpenSession())
            {
                for (int i = 0; i < 4; i++)
                {
                    session.Delete(users[i].Id);
                }
                session.SaveChanges();
            }

            // run cleaner
            var record = await store1.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(store1.Database));
            var dbServer = nodes.Single(s => record.Topology.AllNodes.Contains(s.ServerStore.NodeTag) == true);
            var database = await dbServer.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(store1.Database);
            await database.TombstoneCleaner.ExecuteCleanup();

            // add second node to database
            var notDbServer = nodes.Single(s => record.Topology.AllNodes.Contains(s.ServerStore.NodeTag) == false);
            using var store2 = GetDocumentStore(new Options
            {
                Server = notDbServer,
                CreateDatabase = false,
                ModifyDocumentStore = ds => ds.Conventions.DisableTopologyUpdates = true,
                ReplicationFactor = 1
            });

            await store2.Maintenance.Server.SendAsync(new AddDatabaseNodeOperation(store1.Database));
            var databaseCopy = await notDbServer.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(store1.Database, ignoreDisabledDatabase: true);

            // check if databaseCopy is a member (not promotable)
            await AssertWaitForValueAsync(async () => await GetMembersCount(store1, databaseName: store1.Database), 2);
        }

        [Fact]
        public async Task Able_To_Expand_Group_After_Tombstone_Clean_And_Restore_Database_From_Snapshot()
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");
            var (nodes, leader) = await CreateRaftCluster(2, watcherCluster: true);

            using var store1 = GetDocumentStore(new Options
            {
                Server = leader, 
                ReplicationFactor = 1
            });

            // create 5 doc -> delete 4
            List<User> users = new List<User>();

            using (var session = store1.OpenSession())
            {
                for (int i = 0; i < 5; i++)
                {
                    var u = new User()
                    {
                        Name = $"Shahar{i}"
                    };
                    users.Add(u);
                    session.Store(u);
                }
                session.SaveChanges();
            }
            using (var session = store1.OpenSession())
            {
                for (int i = 0; i < 4; i++)
                {
                    session.Delete(users[i].Id);
                }
                session.SaveChanges();
            }

            // run cleaner
            var record = await store1.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(store1.Database));
            var dbServer = nodes.Single(s => record.Topology.AllNodes.Contains(s.ServerStore.NodeTag) == true);
            var notDbServer = nodes.Single(s => record.Topology.AllNodes.Contains(s.ServerStore.NodeTag) == false);
            var database = await dbServer.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(store1.Database);
            await database.TombstoneCleaner.ExecuteCleanup();

            // snapshot backup
            var config = Backup.CreateBackupConfiguration(backupPath, backupType: BackupType.Snapshot);
            var backupTaskId = await Backup.UpdateConfigAndRunBackupAsync(dbServer, config, store1);
            await Backup.RunBackupAsync(dbServer, backupTaskId, store1, isFullBackup: false);

            // restore backup
            var databaseCopyName = store1.Database + "_Copy";
            using var d = Backup.RestoreDatabase(store1, 
                new RestoreBackupConfiguration { 
                    BackupLocation = Directory.GetDirectories(backupPath).First(), 
                    DatabaseName = databaseCopyName,
                }, nodeTag: dbServer.ServerStore.NodeTag );

            // add second node to databaseCopy
            using var store2 = GetDocumentStore(new Options
            {
                Server = notDbServer,
                CreateDatabase = false,
                ModifyDocumentStore = ds => ds.Conventions.DisableTopologyUpdates = true,
                ReplicationFactor = 1
            });

            await store2.Maintenance.Server.SendAsync(new AddDatabaseNodeOperation(databaseCopyName));
            var databaseCopy2 = await notDbServer.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(databaseCopyName, ignoreDisabledDatabase: true);

            // check if databaseCopy is a member (not promotable)
            await AssertWaitForValueAsync(async () => await GetMembersCount(store1, databaseName: databaseCopyName), 2);
        }

        public class User
        {
            public string Id { get; set; }
            public string Name { get; set; }
        }


    }
}
