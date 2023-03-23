﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FastTests;
using FastTests.Client;
using NuGet.ContentModel;
using Orders;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions.Database;
using Raven.Client.Exceptions.Documents.Indexes;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Raven.Server;
using Raven.Server.Commercial;
using Raven.Server.Documents;
using Raven.Server.Documents.Indexes;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace SlowTests.Issues
{
    public class RavenDB_18554 : ClusterTestBase
    {
        public RavenDB_18554(ITestOutputHelper output) : base(output)
        {
        }


        [Fact]
        public async Task QueriesShouldFailoverIfDatabaseIsCompactingSingleNode()
        {
            var storeOptions = new Options {RunInMemory = false};
            using (var store = GetDocumentStore(storeOptions))
            {
                // Prepare Server For Test
                string categoryId;
                Category c = new Category { Name = $"n0", Description = $"d0" };
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(c);
                    await session.SaveChangesAsync();
                    categoryId = c.Id;
                }
                
                var index = new Categoroies_Details();
                index.Execute(store);
                Indexes.WaitForIndexing(store);

                // Test
                CompactSettings settings = new CompactSettings {DatabaseName = store.Database, Documents = true, Indexes = new[]{ index.IndexName } };

                Exception exception = null;

                var d = () =>
                {
                    try
                    {
                        using (var session = store.OpenSession()) 
                        { 
                            var l = session.Query<Categoroies_Details.Entity, Categoroies_Details>()
                                .ProjectInto<Categoroies_Details.Entity>()
                                .ToList();
                        }
                    }
                    catch (Exception e)
                    {
                        exception = e;
                    }
                };

                var database = await GetDatabase(store.Database);
                database.ForTestingPurposesOnly().CompactionAfterDatabaseUnload = d;

                var operation = store.Maintenance.Server.Send(new CompactDatabaseOperation(settings));
                operation.WaitForCompletion();

                Assert.NotNull(exception);
                Assert.True(exception is DatabaseDisabledException);
            }
        }

        [Fact]
        public async Task QueriesShouldFailoverIfDatabaseIsCompactingCluster()
        {
            var (nodes, leader) = await CreateRaftCluster(2);
            Assert.Equal(nodes.Count, 2);
            var storeOptions = new Options
            {
                Server = leader,
                ReplicationFactor = nodes.Count,
                RunInMemory = false,
                ModifyDocumentStore = (store) => store.Conventions.DisableTopologyUpdates = true
            };


            using (var store = GetDocumentStore(storeOptions))
            {
                // Prepare Server For Test
                string categoryId;
                Category c = new Category { Name = $"n0", Description = $"d0" };
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(c);
                    session.Advanced.WaitForReplicationAfterSaveChanges(replicas: 1);
                    await session.SaveChangesAsync();
                    categoryId = c.Id;
                }

                var index = new Categoroies_Details();
                index.Execute(store);
                Indexes.WaitForIndexing(store);

                // Test
                CompactSettings settings = new CompactSettings { DatabaseName = store.Database, Documents = true, Indexes = new[] { index.IndexName } };

                Exception exception = null;
                List<Categoroies_Details.Entity> l = null;
                var d = () =>
                {
                    try
                    {
                        using (var store2 = new DocumentStore() // DisableTopologyUpdates is false, for letting failover work (failover updates the topology)
                               {
                                   Urls = (from node in nodes select node.WebUrl).ToArray<string>(),
                                   Database = store.Database,
                               }.Initialize())
                        using (var session = store2.OpenSession())
                        {
                            l = session.Query<Categoroies_Details.Entity, Categoroies_Details>()
                                .ProjectInto<Categoroies_Details.Entity>()
                                .ToList();
                        }
                    }
                    catch (Exception e)
                    {
                        exception = e;
                    }
                };

                var responsibleNodeUrl = store.GetRequestExecutor(store.Database).Topology.Nodes[0].Url;
                var responsibleNode = nodes.Single(n => n.ServerStore.GetNodeHttpServerUrl() == responsibleNodeUrl);
                var database = await GetDatabase(responsibleNode, store.Database);
                database.ForTestingPurposesOnly().CompactionAfterDatabaseUnload = d;

                var operation = store.Maintenance.Server.Send(new CompactDatabaseOperation(settings));
                operation.WaitForCompletion();

                // Check if failover succeeded
                Assert.Null(exception);
                Assert.NotNull(l);
                Assert.Equal(1, l.Count);
                Assert.Equal(categoryId, l[0].Id);
                Assert.Equal(Categoroies_Details.GenDetails(c), l[0].Details);
            }
        }

        [Fact]
        public async Task QueriesShouldFailoverIfIndexIsCompactingSingleNode()
        {
            var storeOptions = new Options { RunInMemory = false };
            using (var store = GetDocumentStore(storeOptions))
            {
                // Prepare Server For Test
                string categoryId;
                Category c = new Category {Name = $"n0", Description = $"d0"};
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(c);
                    await session.SaveChangesAsync();
                    categoryId = c.Id;
                }

                // Wait for indexing in first node
                var index = new Categoroies_Details();
                index.Execute(store);
                Indexes.WaitForIndexing(store);

                // Test
                CompactSettings settings = new CompactSettings {DatabaseName = store.Database, Documents = true, Indexes = new[] { index.IndexName } };

                Exception exception = null;
                var d = () =>
                {
                    try
                    {
                        using (var session = store.OpenSession())
                        {
                            var l = session.Query<Categoroies_Details.Entity, Categoroies_Details>()
                                                .ProjectInto<Categoroies_Details.Entity>()
                                                .ToList();
                        }
                    }
                    catch (Exception e)
                    {
                        exception = e;
                    }
                };

                var database = await GetDatabase(store.Database);
                database.IndexStore.ForTestingPurposesOnly().IndexCompaction = d;

                var operation = await store.Maintenance.Server.SendAsync(new CompactDatabaseOperation(settings));
                await operation.WaitForCompletionAsync();

                Assert.NotNull(exception);
                Assert.True(exception is IndexCompactionInProgressException);
            }
        }

        [Fact]
        public async Task QueriesShouldFailoverIfIndexIsCompactingCluster()
        {
            var (nodes, leader) = await CreateRaftCluster(2, watcherCluster: true); // Prevent updating the db topology after getting it.
            Cluster.SuspendObserver(leader);
            var storeOptions = new Options
            {
                Server = leader,
                ReplicationFactor = nodes.Count,
                RunInMemory = false
            };

            using (var store = GetDocumentStore(storeOptions))
            {
                // Prepare Server For Test
                string categoryId;
                Category c = new Category { Name = $"n0", Description = $"d0" };
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(c);
                    session.Advanced.WaitForReplicationAfterSaveChanges(replicas: 1); //Wait for replication
                    await session.SaveChangesAsync();
                    categoryId = c.Id;
                }

                // Wait for indexing in first node and second node
                var index = new Categoroies_Details();
                await Cluster.CreateIndexInClusterAsync(store, index, nodes);

                foreach (var n in nodes)
                {
                    // wait for indexing to finish on the current node
                    var nodeTag = n.ServerStore.NodeTag;
                    Indexes.WaitForIndexing(store,
                        dbName: store.Database,
                        timeout: TimeSpan.FromMinutes(2),
                        nodeTag: nodeTag);

                    Assert.Equal(0, await GetStaleIndexesCount(store, nodeTag));
                }

                // Test
                CompactSettings settings = new CompactSettings { DatabaseName = store.Database, Documents = true, Indexes = new[] { index.IndexName } };
                Exception exception = null;
                List<Categoroies_Details.Entity> l = null;
                var responsibleNodeTag = store.GetRequestExecutor(store.Database).Topology.Nodes[0].ClusterTag;
                var d = () =>
                {
                    try
                    {
                        // Ensure that call this function once.
                        using (var session = store.OpenSession())
                        {
                            l = session.Query<Categoroies_Details.Entity, Categoroies_Details>()
                                .ProjectInto<Categoroies_Details.Entity>().ToList();
                        }
                    }
                    catch (Exception e)
                    {
                        exception = e;
                    }
                };


                var responsibleNode = nodes.Single(n => n.ServerStore.NodeTag == responsibleNodeTag);
                var database = await GetDatabase(responsibleNode, store.Database);
                database.IndexStore.ForTestingPurposesOnly().IndexCompaction = d;

                var operation = await store.Maintenance.Server.SendAsync(new CompactDatabaseOperation(settings, responsibleNodeTag));
                await operation.WaitForCompletionAsync();

                // Check if failover succeeded
                Assert.Null(exception);
                Assert.NotNull(l);
                Assert.Equal(1, l.Count);
                Assert.Equal(categoryId, l[0].Id);
                Assert.Equal(Categoroies_Details.GenDetails(c), l[0].Details);
            }
        }

        private async Task<int> GetStaleIndexesCount(IDocumentStore store, string nodeTag)
        {
            var databaseStatistics = await store.Maintenance.SendAsync(new GetStatisticsOperation("wait-for-indexing", nodeTag));
            var staleIndexesCount = databaseStatistics.Indexes
                .Count(x => x.IsStale || x.Name.StartsWith(Constants.Documents.Indexing.SideBySideIndexNamePrefix));
            return staleIndexesCount;
        }

        class Categoroies_Details : AbstractMultiMapIndexCreationTask<Categoroies_Details.Entity>
        {
            internal class Entity
            {
                public string Id { get; set; }
                public string Details { get; set; }
            }

            public Categoroies_Details()
            {
                AddMap<Category>(
                    categories =>
                        from c in categories
                        select new Entity
                        {
                            Id = c.Id,
                            Details = $"Id=\"{c.Id}\", Name=\"{c.Name}\", Description=\"{c.Description}\""
                        }
                );
                Store(x => x.Details, FieldStorage.Yes);
            }

            public static string GenDetails(Category c)
            {
                return $"Id=\"{c.Id}\", Name=\"{c.Name}\", Description=\"{c.Description}\"";
            }
        }
    }
}

