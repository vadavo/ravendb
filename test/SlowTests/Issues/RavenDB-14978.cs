using System;
using System.Threading;
using System.Threading.Tasks;
using Nito.AsyncEx;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Operations.Configuration;
using Raven.Client.Http;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Issues
{
    public class RavenDB_14978 : ClusterTestBase
    {
        public RavenDB_14978(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public async Task Can_setup_write_load_balancing_on_client()
        {
            var (_, leader) = await CreateRaftCluster(3);
            var databaseName = GetDatabaseName();

            string context = "users/1";

            using var store = new DocumentStore
            {
                Urls = new[] { leader.WebUrl },
                Database = databaseName,
                Conventions = new DocumentConventions
                {
                    ReadBalanceBehavior = ReadBalanceBehavior.RoundRobin,
                    LoadBalanceBehavior = LoadBalanceBehavior.UseSessionContext,
                    LoadBalancerPerSessionContextSelector = db => context
                }
            }.Initialize();

            var (index, _) = await CreateDatabaseInCluster(databaseName, 3, leader.WebUrl);
            await Cluster.WaitForRaftIndexToBeAppliedInClusterAsync(index, TimeSpan.FromSeconds(30));

            Can_setup_write_load_balancing(store, ref context);
        }

        [Fact]
        public async Task Can_setup_write_load_balancing_on_server()
        {
            var (_, leader) = await CreateRaftCluster(3);
            var databaseName = GetDatabaseName();
            var context = "users/1";

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var store = new DocumentStore
            {
                Urls = new[] { leader.WebUrl },
                Database = databaseName,
                Conventions = new DocumentConventions
                {
                    LoadBalancerPerSessionContextSelector = db => context
                }
            }.Initialize();

            var configuration = new ClientConfiguration
            {
                ReadBalanceBehavior = ReadBalanceBehavior.RoundRobin, 
                LoadBalanceBehavior = LoadBalanceBehavior.UseSessionContext, 
                Disabled = false
            };

            var (index, _) = await CreateDatabaseInCluster(databaseName, 3, leader.WebUrl);
            await Cluster.WaitForRaftIndexToBeAppliedInClusterAsync(index, TimeSpan.FromSeconds(30));

            await store.Maintenance.SendAsync(new PutClientConfigurationOperation(configuration), cts.Token);

            Can_setup_write_load_balancing(store, ref context);
        }

        private static void Can_setup_write_load_balancing(IDocumentStore store, ref string context)
        {
            int s1Ctx = -1;

            using (var s1 = store.OpenSession())
            {
                var sessionInfo = s1.Advanced.SessionInfo;
                s1Ctx = sessionInfo.SessionId;
            }

            var s2Ctx = -1;
            using (var s2 = store.OpenSession())
            {
                var sessionInfo = s2.Advanced.SessionInfo;
                s2Ctx = sessionInfo.SessionId;
            }

            Assert.Equal(s2Ctx, s1Ctx);

            context = "users/2";

            var s3Ctx = -1;
            using (var s3 = store.OpenSession())
            {
                var sessionInfo = s3.Advanced.SessionInfo;
                s3Ctx = sessionInfo.SessionId;
            }

            Assert.NotEqual(s2Ctx, s3Ctx);

            var s4Ctx = -1;
            using (var s4 = store.OpenSession())
            {
                s4.Advanced.SessionInfo.SetContext("monkey");

                var sessionInfo = s4.Advanced.SessionInfo;
                s4Ctx = sessionInfo.SessionId;
            }

            Assert.NotEqual(s4Ctx, s3Ctx);
        }
    }
}
