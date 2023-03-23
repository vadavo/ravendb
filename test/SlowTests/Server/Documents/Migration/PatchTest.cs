﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Raven.Server.ServerWide.Context;
using Raven.Server.SqlMigration;
using Raven.Server.SqlMigration.Model;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Server.Documents.Migration
{
    public class PatchTest : SqlAwareTestBase
    {
        public PatchTest(ITestOutputHelper output) : base(output)
        {
        }

        [NightlyBuildTheory]
        [RequiresMsSqlInlineData]
        [RequiresMySqlInlineData]
        [RequiresNpgSqlInlineData]
        [RequiresOracleSqlInlineData]
        public async Task SimplePatch(MigrationProvider provider)
        {
            using (WithSqlDatabase(provider, out var connectionString, out string schemaName, "basic"))
            {
                var driver = DatabaseDriverDispatcher.CreateDriver(provider, connectionString);
                using (var store = GetDocumentStore())
                {
                    var db = await Databases.GetDocumentDatabaseInstanceFor(store);

                    var settings = new MigrationSettings
                    {
                        Collections = new List<RootCollection>
                        {
                            new RootCollection(schemaName, "order", "Orders")
                            {
                                Patch = "this.NewField = 5;"
                            }
                        }
                    };

                    using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1)))
                    using (db.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
                    {
                        var schema = driver.FindSchema();
                        ApplyDefaultColumnNamesMapping(schema, settings);
                        await driver.Migrate(settings, schema, db, context, token: cts.Token);
                    }

                    using (var session = store.OpenSession())
                    {
                        var order = session.Load<JObject>("Orders/1");
                        Assert.NotNull(order);

                        Assert.Equal(5, order["NewField"]);
                    }
                }
            }
        }

        [NightlyBuildFact]
        public async Task PatchCanAccessNestedObjects()
        {
            using (WithSqlDatabase(MigrationProvider.MsSQL, out var connectionString, out string schemaName, "basic"))
            {
                var driver = DatabaseDriverDispatcher.CreateDriver(MigrationProvider.MsSQL, connectionString);
                using (var store = GetDocumentStore())
                {
                    var db = await Databases.GetDocumentDatabaseInstanceFor(store);

                    var settings = new MigrationSettings
                    {
                        Collections = new List<RootCollection>
                        {
                            new RootCollection(schemaName, "order", "Orders")
                            {
                                Patch = "this.JsTotal = this.Items.map(x => x.price).reduce((acc, cur) => acc + cur, 0)",
                                NestedCollections = new List<EmbeddedCollection>
                                {
                                    new EmbeddedCollection(schemaName, "order_item", RelationType.OneToMany, new List<string> { "order_id" }, "Items")
                                }
                            }
                        }
                    };

                    using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1)))
                    using (db.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
                    {
                        var schema = driver.FindSchema();
                        ApplyDefaultColumnNamesMapping(schema, settings);
                        await driver.Migrate(settings, schema, db, context, token: cts.Token);
                    }

                    using (var session = store.OpenSession())
                    {
                        var order = session.Load<JObject>("Orders/1");

                        Assert.NotNull(order);
                        Assert.Equal(440, order["JsTotal"]);
                    }
                }
            }
        }

        [NightlyBuildTheory]
        [RequiresMsSqlInlineData]
        [RequiresMySqlInlineData]
        [RequiresNpgSqlInlineData]
        [RequiresOracleSqlInlineData]
        public async Task SupportsDocumentSkip(MigrationProvider provider)
        {
            using (WithSqlDatabase(provider, out var connectionString, out string schemaName, "basic"))
            {
                var driver = DatabaseDriverDispatcher.CreateDriver(provider, connectionString);
                using (var store = GetDocumentStore())
                {
                    var db = await Databases.GetDocumentDatabaseInstanceFor(store);

                    var settings = new MigrationSettings
                    {
                        Collections = new List<RootCollection>
                        {
                            new RootCollection(schemaName, "order_item", "OrderItems")
                            {
                                Patch = "if (this.Price < 200) throw 'skip';"
                            }
                        }
                    };

                    using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1)))
                    using (db.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
                    {
                        var schema = driver.FindSchema();
                        ApplyDefaultColumnNamesMapping(schema, settings);
                        var result = new MigrationResult(settings);
                        await driver.Migrate(settings, schema, db, context, result, token: cts.Token);

                        Assert.Equal(2, result.PerCollectionCount["OrderItems"].ReadCount);
                        Assert.Equal(0, result.PerCollectionCount["OrderItems"].ErroredCount);
                        Assert.Equal(1, result.PerCollectionCount["OrderItems"].SkippedCount);
                    }

                    using (var session = store.OpenSession())
                    {
                        Assert.Equal(1, session.Advanced.LoadStartingWith<JObject>("OrderItems/").Length);
                    }
                }
            }
        }
    }
}
