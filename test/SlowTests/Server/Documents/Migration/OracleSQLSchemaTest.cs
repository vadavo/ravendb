﻿using System.Linq;
using Raven.Server.SqlMigration;
using Raven.Server.SqlMigration.Schema;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Server.Documents.Migration
{
    public class OracleSchemaTest : SqlAwareTestBase
    {
        public OracleSchemaTest(ITestOutputHelper output) : base(output)
        {
        }

        [RequiresOracleSqlFact]
        public void CanFetchSchema()
        {
            using (WithSqlDatabase(MigrationProvider.Oracle, out var connectionString, out string schemaName, includeData: false))
            {
                var driver = DatabaseDriverDispatcher.CreateDriver(MigrationProvider.Oracle, connectionString);
                var schema = driver.FindSchema();

                Assert.NotNull(schema.CatalogName);
                Assert.Equal(10, schema.Tables.Count);

                var tables = schema.Tables;

                // validate NoPkTable
                var noPkTable = tables.First(x => x.TableName == "NOPKTABLE");
                Assert.NotNull(noPkTable);

                Assert.Equal(new[] { "ID" }, noPkTable.Columns.Select(x => x.Name).ToList());
                Assert.Equal(0, noPkTable.PrimaryKeyColumns.Count);

                // validate Order Table
                var orderTable = tables.First(x => x.TableName == "Order");

                Assert.Equal(4, orderTable.Columns.Count);
                Assert.Equal(new[] { "ID", "ORDERDATE", "CUSTOMERID", "TOTALAMOUNT" }.ToHashSet(), orderTable.Columns.Select(x => x.Name).ToHashSet());
                Assert.Equal(new[] { "ID" }, orderTable.PrimaryKeyColumns);

                var orderReferences = orderTable.References;
                Assert.Equal(1, orderReferences.Count);
                Assert.Equal("ORDERITEM", orderReferences[0].Table);
                Assert.Equal(new[] { "ORDERID" }, orderReferences[0].Columns);

                // validate UnsupportedTable

                var unsupportedTable = tables.First(x => x.TableName == "UNSUPPORTEDTABLE");
                Assert.True(unsupportedTable.Columns.Any(x => x.Type == ColumnType.Unsupported));

                // validate OrderItem (2 columns in PK)

                var orderItemTable = tables.First(x => x.TableName == "ORDERITEM");
                Assert.Equal(new[] { "ORDERID", "PRODUCTID" }.ToHashSet(), orderItemTable.PrimaryKeyColumns.ToHashSet());

                Assert.Equal(1, orderItemTable.References.Count);
                Assert.Equal("DETAILS", orderItemTable.References[0].Table);
                Assert.Equal(new[] { "ORDERID", "PRODUCTID" }.ToHashSet(), orderItemTable.References[0].Columns.ToHashSet());

                // all types are supported (except UnsupportedTable)
                Assert.True(tables.Where(x => x.TableName != "UNSUPPORTEDTABLE")
                    .All(x => x.Columns.All(y => y.Type != ColumnType.Unsupported)));

                // validate many - to - many
                var productsCategory = tables.First(x => x.TableName == "PRODUCTCATEGORY");
                Assert.Equal(0, productsCategory.References.Count);
                Assert.Equal(1, tables.First(x => x.TableName == "CATEGORY").References.Count);
                Assert.Equal(2, tables.First(x => x.TableName == "PRODUCT").References.Count);
            }
        }
    }
}
