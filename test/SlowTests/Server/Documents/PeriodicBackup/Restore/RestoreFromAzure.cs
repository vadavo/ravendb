﻿using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using FastTests;
using Newtonsoft.Json;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Backups;
using Raven.Client.Exceptions;
using Raven.Client.ServerWide.Operations;
using Raven.Server.Documents;
using Raven.Server.Documents.PeriodicBackup.Restore;
using Raven.Server.ServerWide.Context;
using Raven.Tests.Core.Utils.Entities;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Server.Documents.PeriodicBackup.Restore
{
    public class RestoreFromAzure : RavenTestBase
    {
        public RestoreFromAzure(ITestOutputHelper output) : base(output)
        {
        }

        [Fact, Trait("Category", "Smuggler")]
        public void restore_azure_cloud_settings_tests()
        {
            using (var store = GetDocumentStore(new Options
            {
                ModifyDatabaseName = s => $"{s}_2"
            }))
            {
                var databaseName = $"restored_database-{Guid.NewGuid()}";
                var restoreConfiguration = new RestoreFromAzureConfiguration
                {
                    DatabaseName = databaseName
                };

                var restoreBackupTask = new RestoreBackupOperation(restoreConfiguration);

                var e = Assert.Throws<RavenException>(() => store.Maintenance.Server.Send(restoreBackupTask));
                Assert.Contains($"{nameof(AzureSettings.AccountKey)} and {nameof(AzureSettings.SasToken)} cannot be both null or empty", e.InnerException.Message);

                restoreConfiguration.Settings.AccountKey = "test";
                restoreBackupTask = new RestoreBackupOperation(restoreConfiguration);
                e = Assert.Throws<RavenException>(() => store.Maintenance.Server.Send(restoreBackupTask));
                Assert.Contains($"{nameof(AzureSettings.AccountName)} cannot be null or empty", e.InnerException.Message);

                restoreConfiguration.Settings.AccountName = "test";
                restoreBackupTask = new RestoreBackupOperation(restoreConfiguration);
                e = Assert.Throws<RavenException>(() => store.Maintenance.Server.Send(restoreBackupTask));
                Assert.Contains($"{nameof(AzureSettings.StorageContainer)} cannot be null or empty", e.InnerException.Message);

                restoreConfiguration.Settings.StorageContainer = "test";
                restoreConfiguration.Settings.AccountKey = null;
                restoreConfiguration.Settings.SasToken = "testSasToken";
                restoreBackupTask = new RestoreBackupOperation(restoreConfiguration);
                e = Assert.Throws<RavenException>(() => store.Maintenance.Server.Send(restoreBackupTask));
                Assert.Contains($"{nameof(AzureSettings.SasToken)} isn't in the correct format", e.InnerException.Message);

                restoreConfiguration.Settings.AccountKey = "test";
                restoreBackupTask = new RestoreBackupOperation(restoreConfiguration);
                e = Assert.Throws<RavenException>(() => store.Maintenance.Server.Send(restoreBackupTask));
                Assert.Contains($"{nameof(AzureSettings.AccountKey)} and {nameof(AzureSettings.SasToken)} cannot be used simultaneously", e.InnerException.Message);
            }
        }

        [AzureFact, Trait("Category", "Smuggler")]
        public void can_backup_and_restore() => can_backup_and_restore_internal(oneTimeBackup: false);

        [AzureFact, Trait("Category", "Smuggler")]
        public void can_onetime_backup_and_restore() => can_backup_and_restore_internal(oneTimeBackup: true);

        private void can_backup_and_restore_internal(bool oneTimeBackup)
        {
            using (var holder = new Azure.AzureClientHolder(AzureFactAttribute.AzureSettings))
            {
                using (var store = GetDocumentStore())
                {
                    using (var session = store.OpenSession())
                    {
                        session.Store(new User { Name = "oren" }, "users/1");
                        session.CountersFor("users/1").Increment("likes", 100);
                        session.SaveChanges();
                    }

                    PeriodicBackupStatus status = null;
                    long backupTaskId = 0;
                    BackupResult backupResult = null;
                    if (oneTimeBackup == false)
                    {
                        var config = Backup.CreateBackupConfiguration(azureSettings: holder.Settings);
                        backupTaskId = Backup.UpdateConfigAndRunBackup(Server, config, store);
                        backupResult = (BackupResult)store.Maintenance.Send(new GetOperationStateOperation(Backup.GetBackupOperationId(store, backupTaskId))).Result;
                        Assert.NotNull(backupResult);
                        Assert.True(backupResult.Counters.Processed, "backupResult.Counters.Processed");
                        Assert.Equal(1, backupResult.Counters.ReadCount);
                    }

                    using (var session = store.OpenSession())
                    {
                        session.Store(new User { Name = "ayende" }, "users/2");
                        session.CountersFor("users/2").Increment("downloads", 200);

                        session.SaveChanges();
                    }

                    if (oneTimeBackup == false)
                    {
                        var lastEtag = store.Maintenance.Send(new GetStatisticsOperation()).LastDocEtag;
                        status = Backup.RunBackupAndReturnStatus(Server, backupTaskId, store, isFullBackup: false, expectedEtag: lastEtag);
                    }

                    if (oneTimeBackup)
                    {
                        var backupConfiguration = new BackupConfiguration
                        {
                            BackupType = BackupType.Backup,
                            AzureSettings = holder.Settings,
                        };

                        backupResult = (BackupResult)store.Maintenance.Send(new BackupOperation(backupConfiguration)).WaitForCompletion(TimeSpan.FromSeconds(15));
                        Assert.True(backupResult != null && backupResult.Counters.Processed, "backupResult != null && backupResult.Counters.Processed");
                        Assert.Equal(2, backupResult.Counters.ReadCount);
                    }

                    // restore the database with a different name
                    var databaseName = $"restored_database-{Guid.NewGuid()}";

                    holder.Settings.RemoteFolderName = oneTimeBackup ? $"{holder.Settings.RemoteFolderName}/{backupResult.LocalBackup.BackupDirectory}" : $"{holder.Settings.RemoteFolderName}/{status.FolderName}";
                    var restoreFromGoogleCloudConfiguration = new RestoreFromAzureConfiguration()
                    {
                        DatabaseName = databaseName,
                        Settings = holder.Settings,
                        DisableOngoingTasks = true
                    };
                    var googleCloudOperation = new RestoreBackupOperation(restoreFromGoogleCloudConfiguration);
                    var restoreOperation = store.Maintenance.Server.Send(googleCloudOperation);

                    restoreOperation.WaitForCompletion(TimeSpan.FromSeconds(30));
                    using (var store2 = GetDocumentStore(new Options() { CreateDatabase = false, ModifyDatabaseName = s => databaseName }))
                    {
                        using (var session = store2.OpenSession(databaseName))
                        {
                            var users = session.Load<User>(new[] { "users/1", "users/2" });
                            Assert.True(users.Any(x => x.Value.Name == "oren"));
                            Assert.True(users.Any(x => x.Value.Name == "ayende"));

                            var val = session.CountersFor("users/1").Get("likes");
                            Assert.Equal(100, val);
                            val = session.CountersFor("users/2").Get("downloads");
                            Assert.Equal(200, val);
                        }

                        var originalDatabase = Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(store.Database).Result;
                        var restoredDatabase = Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(databaseName).Result;
                        using (restoredDatabase.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext ctx))
                        using (ctx.OpenReadTransaction())
                        {
                            var databaseChangeVector = DocumentsStorage.GetDatabaseChangeVector(ctx);
                            Assert.Contains($"A:7-{originalDatabase.DbBase64Id}", databaseChangeVector);
                            Assert.Contains($"A:8-{restoredDatabase.DbBase64Id}", databaseChangeVector);
                        }
                    }
                }
            }
        }

        [AzureFact, Trait("Category", "Smuggler")]
        public async Task can_create_azure_snapshot_and_restore_using_restore_point()
        {
            using (var holder = new Azure.AzureClientHolder(AzureFactAttribute.AzureSettings))
            {
                using (var store = GetDocumentStore())
                {
                    using (var session = store.OpenSession())
                    {
                        session.Store(new User { Name = "oren" }, "users/1");
                        session.CountersFor("users/1").Increment("likes", 100);
                        session.SaveChanges();
                    }

                    var config = Backup.CreateBackupConfiguration(backupType: BackupType.Snapshot, azureSettings: holder.Settings);
                    var backupTaskId = Backup.UpdateConfigAndRunBackup(Server, config, store);
                    var status = (await store.Maintenance.SendAsync(new GetPeriodicBackupStatusOperation(backupTaskId))).Status;

                    var client = store.GetRequestExecutor().HttpClient;
                    var data = new StringContent(JsonConvert.SerializeObject(holder.Settings), Encoding.UTF8, "application/json");
                    var response = await client.PostAsync(store.Urls.First() + "/admin/restore/points?type=Azure ", data);
                    string result = response.Content.ReadAsStringAsync().Result;
                    var restorePoints = JsonConvert.DeserializeObject<RestorePoints>(result);
                    Assert.Equal(1, restorePoints.List.Count);
                    var point = restorePoints.List.First();

                    var databaseName = $"restored_database-{Guid.NewGuid()}";
                    holder.Settings.RemoteFolderName = holder.Settings.RemoteFolderName + "/" + status.FolderName;
                    var restoreOperation = await store.Maintenance.Server.SendAsync(new RestoreBackupOperation(new RestoreFromAzureConfiguration()
                    {
                        DatabaseName = databaseName,
                        Settings = holder.Settings,
                        DisableOngoingTasks = true,
                        LastFileNameToRestore = point.FileName,
                    }));

                    await restoreOperation.WaitForCompletionAsync(TimeSpan.FromSeconds(60));
                    using (var store2 = GetDocumentStore(new Options() { CreateDatabase = false, ModifyDatabaseName = s => databaseName }))
                    {
                        using (var session = store2.OpenSession(databaseName))
                        {
                            var users = session.Load<User>(new[] { "users/1", "users/2" });
                            Assert.True(users.Any(x => x.Value.Name == "oren"));

                            var val = session.CountersFor("users/1").Get("likes");
                            Assert.Equal(100, val);
                        }

                        var originalDatabase = Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(store.Database).Result;
                        var restoredDatabase = Server.ServerStore.DatabasesLandlord.TryGetOrCreateResourceStore(databaseName).Result;
                        using (restoredDatabase.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext ctx))
                        using (ctx.OpenReadTransaction())
                        {
                            var databaseChangeVector = DocumentsStorage.GetDatabaseChangeVector(ctx);

                            var expected1 = $"A:5-{restoredDatabase.DbBase64Id}, A:4-{originalDatabase.DbBase64Id}";
                            var expected2 = $"A:4-{originalDatabase.DbBase64Id}, A:5-{restoredDatabase.DbBase64Id}";
                            Assert.True(databaseChangeVector == expected1 || databaseChangeVector == expected2, $"Expected:\t\"{databaseChangeVector}\"\nActual:\t\"{expected1}\" or \"{expected2}\"\n");
                        }
                    }
                }
            }
        }
    }
}

