﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Parquet;
using Parquet.Data;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Operations.Backups;
using Raven.Client.Documents.Operations.ConnectionStrings;
using Raven.Client.Documents.Operations.ETL;
using Raven.Client.Documents.Operations.ETL.OLAP;
using Raven.Server.Documents;
using Raven.Server.Documents.ETL.Providers.OLAP;
using Raven.Server.Documents.PeriodicBackup.Aws;
using Raven.Server.Documents.PeriodicBackup.Restore;
using Tests.Infrastructure;
using Tests.Infrastructure.Entities;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Server.Documents.ETL.Olap
{
    public class S3Tests : EtlTestBase
    {
        public S3Tests(ITestOutputHelper output) : base(output)
        {
        }

        private readonly string _s3TestsPrefix = $"olap/tests/{nameof(S3Tests)}-{Guid.NewGuid()}";
        private const string CollectionName = "Orders";
        private static readonly HashSet<char> SpecialChars = new HashSet<char> { '&', '@', ':', ',', '$', '=', '+', '?', ';', ' ', '"', '^', '`', '>', '<', '{', '}', '[', ']', '#', '\'', '~', '|' };

        [AmazonS3Fact]
        public async Task CanUploadToS3()
        {
            var settings = GetS3Settings();

            try
            {
                using (var store = GetDocumentStore())
                {
                    var baseline = new DateTime(2020, 1, 1);

                    using (var session = store.OpenAsyncSession())
                    {
                        for (int i = 0; i < 31; i++)
                        {
                            await session.StoreAsync(new Order
                            {
                                Id = $"orders/{i}",
                                OrderedAt = baseline.AddDays(i),
                                ShipVia = $"shippers/{i}",
                                Company = $"companies/{i}"
                            });
                        }

                        for (int i = 0; i < 28; i++)
                        {
                            await session.StoreAsync(new Order
                            {
                                Id = $"orders/{i + 31}",
                                OrderedAt = baseline.AddMonths(1).AddDays(i),
                                ShipVia = $"shippers/{i + 31}",
                                Company = $"companies/{i + 31}"
                            });
                        }

                        await session.SaveChangesAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    var script = @"
var orderDate = new Date(this.OrderedAt);
var year = orderDate.getFullYear();
var month = orderDate.getMonth();
var key = new Date(year, month);

loadToOrders(partitionBy(key),
    {
        Company : this.Company,
        ShipVia : this.ShipVia
    })
";
                    SetupS3OlapEtl(store, script, settings);

                    etlDone.Wait(TimeSpan.FromMinutes(1));

                    using (var s3Client = new RavenAwsS3Client(settings, DefaultBackupConfiguration))
                    {
                        var prefix = $"{settings.RemoteFolderName}/{CollectionName}";
                        var cloudObjects = await s3Client.ListObjectsAsync(prefix, string.Empty, false);

                        Assert.Equal(2, cloudObjects.FileInfoDetails.Count);
                        Assert.Contains("2020-01-01", cloudObjects.FileInfoDetails[0].FullPath);
                        Assert.Contains("2020-02-01", cloudObjects.FileInfoDetails[1].FullPath);
                    }
                }
            }

            finally
            {
                await DeleteObjects(settings);
            }
        }

        [AmazonS3Fact]
        public async Task SimpleTransformation()
        {
            var settings = GetS3Settings();

            try
            {
                using (var store = GetDocumentStore())
                {
                    var baseline = new DateTime(2020, 1, 1);

                    using (var session = store.OpenAsyncSession())
                    {
                        for (int i = 1; i <= 10; i++)
                        {
                            var o = new Order
                            {
                                Id = $"orders/{i}",
                                OrderedAt = baseline.AddDays(i),
                                Company = $"companies/{i}"
                            };

                            await session.StoreAsync(o);
                        }

                        await session.SaveChangesAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    var script = @"
var orderDate = new Date(this.OrderedAt);
var year = orderDate.getFullYear();
var month = orderDate.getMonth();
var key = new Date(year, month);

loadToOrders(partitionBy(key),
    {
        Company : this.Company,
        ShipVia : this.ShipVia
    })
";
                    SetupS3OlapEtl(store, script, settings);

                    etlDone.Wait(TimeSpan.FromMinutes(1));

                    using (var s3Client = new RavenAwsS3Client(settings, DefaultBackupConfiguration))
                    {
                        var prefix = $"{settings.RemoteFolderName}/{CollectionName}";

                        var cloudObjects = await s3Client.ListObjectsAsync(prefix, delimiter: string.Empty, listFolders: false);

                        Assert.Equal(1, cloudObjects.FileInfoDetails.Count);

                        var fullPath = cloudObjects.FileInfoDetails[0].FullPath;
                        var blob = await s3Client.GetObjectAsync(fullPath);

                        await using var ms = new MemoryStream();
                        blob.Data.CopyTo(ms);

                        using (var parquetReader = new ParquetReader(ms))
                        {
                            Assert.Equal(1, parquetReader.RowGroupCount);

                            var expectedFields = new[] { "Company", ParquetTransformedItems.DefaultIdColumn, ParquetTransformedItems.LastModifiedColumn };

                            Assert.Equal(expectedFields.Length, parquetReader.Schema.Fields.Count);

                            using var rowGroupReader = parquetReader.OpenRowGroupReader(0);
                            foreach (var field in parquetReader.Schema.Fields)
                            {
                                Assert.True(field.Name.In(expectedFields));

                                var data = rowGroupReader.ReadColumn((DataField)field).Data;
                                Assert.True(data.Length == 10);

                                if (field.Name == ParquetTransformedItems.LastModifiedColumn)
                                    continue;

                                var count = 1;
                                foreach (var val in data)
                                {
                                    switch (field.Name)
                                    {
                                        case ParquetTransformedItems.DefaultIdColumn:
                                            Assert.Equal($"orders/{count}", val);
                                            break;
                                        case "Company":
                                            Assert.Equal($"companies/{count}", val);
                                            break;
                                    }

                                    count++;
                                }
                            }
                        }
                    }
                }
            }

            finally
            {
                await DeleteObjects(settings);
            }
        }

        [AmazonS3Fact]
        public async Task CanLoadToMultipleTables()
        {
            const string salesTableName = "Sales";
            var settings = GetS3Settings();

            try
            {
                using (var store = GetDocumentStore())
                {
                    var baseline = new DateTime(2020, 1, 1);

                    using (var session = store.OpenAsyncSession())
                    {
                        for (int i = 0; i < 31; i++)
                        {
                            var orderedAt = baseline.AddDays(i);
                            var lines = new List<OrderLine>();

                            for (int j = 1; j <= 5; j++)
                            {
                                lines.Add(new OrderLine
                                {
                                    Quantity = j * 10,
                                    PricePerUnit = i + j,
                                    Product = $"Products/{j}"
                                });
                            }

                            var o = new Order
                            {
                                Id = $"orders/{i}",
                                OrderedAt = orderedAt,
                                RequireAt = orderedAt.AddDays(7),
                                Company = $"companies/{i}",
                                Lines = lines
                            };

                            await session.StoreAsync(o);
                        }

                        baseline = baseline.AddMonths(1);

                        for (int i = 0; i < 28; i++)
                        {
                            var orderedAt = baseline.AddDays(i);
                            var lines = new List<OrderLine>();

                            for (int j = 1; j <= 5; j++)
                            {
                                lines.Add(new OrderLine
                                {
                                    Quantity = j * 10,
                                    PricePerUnit = i + j,
                                    Product = $"Products/{j}"
                                });
                            }

                            var o = new Order
                            {
                                Id = $"orders/{i + 31}",
                                OrderedAt = orderedAt,
                                RequireAt = orderedAt.AddDays(7),
                                Company = $"companies/{i}",
                                Lines = lines
                            };

                            await session.StoreAsync(o);
                        }

                        await session.SaveChangesAsync();
                    }

                    var database = await GetDatabase(store.Database);
                    var etlDone = new ManualResetEventSlim();
                    
                    database.EtlLoader.BatchCompleted += x =>
                    {
                        if (x.Statistics.LoadSuccesses > 0)
                            etlDone.Set();
                    };

                    const string script = @"
var orderData = {
    Company : this.Company,
    RequireAt : new Date(this.RequireAt),
    ItemsCount: this.Lines.length,
    TotalCost: 0
};

var orderDate = new Date(this.OrderedAt);
var year = orderDate.getFullYear();
var month = orderDate.getMonth();
var key = new Date(year, month);

for (var i = 0; i < this.Lines.length; i++) {
    var line = this.Lines[i];
    orderData.TotalCost += (line.PricePerUnit * line.Quantity);
    
    // load to 'sales' table

    loadToSales(partitionBy(key), {
        Qty: line.Quantity,
        Product: line.Product,
        Cost: line.PricePerUnit
    });
}

// load to 'orders' table
loadToOrders(partitionBy(key), orderData);
";


                    SetupS3OlapEtl(store, script, settings);

                    var timeout = database.DocumentsStorage.Environment.Options.RunningOn32Bits
                        ? TimeSpan.FromMinutes(2)
                        : TimeSpan.FromMinutes(1);

                    Assert.True(etlDone.Wait(timeout), $"olap etl to s3 did not finish in {timeout.TotalMinutes} minutes. stats : {GetPerformanceStats(database)}");

                    using (var s3Client = new RavenAwsS3Client(settings, DefaultBackupConfiguration))
                    {
                        var prefix = $"{settings.RemoteFolderName}/{CollectionName}";
                        var cloudObjects = await s3Client.ListObjectsAsync(prefix, string.Empty, false);

                        Assert.Equal(2, cloudObjects.FileInfoDetails.Count);
                        Assert.Contains("2020-01-01", cloudObjects.FileInfoDetails[0].FullPath);
                        Assert.Contains("2020-02-01", cloudObjects.FileInfoDetails[1].FullPath);

                        var fullPath = cloudObjects.FileInfoDetails[0].FullPath;
                        var blob = await s3Client.GetObjectAsync(fullPath);

                        await using var ms = new MemoryStream();
                        blob.Data.CopyTo(ms);

                        using (var parquetReader = new ParquetReader(ms))
                        {
                            Assert.Equal(1, parquetReader.RowGroupCount);

                            var expectedFields = new[] { "Company", "RequireAt", "ItemsCount", "TotalCost", ParquetTransformedItems.DefaultIdColumn, ParquetTransformedItems.LastModifiedColumn };
                            Assert.Equal(expectedFields.Length, parquetReader.Schema.Fields.Count);

                            using var rowGroupReader = parquetReader.OpenRowGroupReader(0);
                            foreach (var field in parquetReader.Schema.Fields)
                            {
                                Assert.True(field.Name.In(expectedFields));

                                var data = rowGroupReader.ReadColumn((DataField)field).Data;
                                Assert.True(data.Length == 31);
                            }
                        }
                    }

                    using (var s3Client = new RavenAwsS3Client(settings, DefaultBackupConfiguration))
                    {
                        var prefix = $"{settings.RemoteFolderName}/{salesTableName}";
                        var cloudObjects = await s3Client.ListObjectsAsync(prefix, string.Empty, false);

                        Assert.Equal(2, cloudObjects.FileInfoDetails.Count);
                        Assert.Contains("2020-01-01", cloudObjects.FileInfoDetails[0].FullPath);
                        Assert.Contains("2020-02-01", cloudObjects.FileInfoDetails[1].FullPath);

                        var fullPath = cloudObjects.FileInfoDetails[1].FullPath;
                        var blob = await s3Client.GetObjectAsync(fullPath);

                        await using var ms = new MemoryStream();
                        blob.Data.CopyTo(ms);

                        using (var parquetReader = new ParquetReader(ms))
                        {
                            Assert.Equal(1, parquetReader.RowGroupCount);

                            var expectedFields = new[] { "Qty", "Product", "Cost", ParquetTransformedItems.DefaultIdColumn, ParquetTransformedItems.LastModifiedColumn };
                            Assert.Equal(expectedFields.Length, parquetReader.Schema.Fields.Count);

                            using var rowGroupReader = parquetReader.OpenRowGroupReader(0);
                            foreach (var field in parquetReader.Schema.Fields)
                            {
                                Assert.True(field.Name.In(expectedFields));

                                var data = rowGroupReader.ReadColumn((DataField)field).Data;
                                Assert.True(data.Length == 28 * 5);
                            }
                        }
                    }
                }

            }
            finally
            {
                await DeleteObjects(settings, salesTableName);
            }
        }

        internal static string GetPerformanceStats(DocumentDatabase database)
        {
            var process = database.EtlLoader.Processes.First();
            var stats = process.GetPerformanceStats();
            return string.Join(Environment.NewLine, stats.Select(JsonConvert.SerializeObject));
        }

        [AmazonS3Fact]
        public async Task CanModifyPartitionColumnName()
        {
            var settings = GetS3Settings();

            try
            {
                using (var store = GetDocumentStore())
                {
                    const string partitionColumn = "order_date";

                    var baseline = new DateTime(2020, 1, 1);

                    using (var session = store.OpenAsyncSession())
                    {
                        for (int i = 0; i < 31; i++)
                        {
                            await session.StoreAsync(new Order
                            {
                                Id = $"orders/{i}",
                                OrderedAt = baseline.AddDays(i),
                                ShipVia = $"shippers/{i}",
                                Company = $"companies/{i}"
                            });
                        }

                        for (int i = 0; i < 28; i++)
                        {
                            await session.StoreAsync(new Order
                            {
                                Id = $"orders/{i + 31}",
                                OrderedAt = baseline.AddMonths(1).AddDays(i),
                                ShipVia = $"shippers/{i + 31}",
                                Company = $"companies/{i + 31}"
                            });
                        }

                        await session.SaveChangesAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    var script = @"
var orderDate = new Date(this.OrderedAt);
var year = orderDate.getFullYear();
var month = orderDate.getMonth();
var key = new Date(year, month);

loadToOrders(partitionBy(['order_date', key]),
    {
        Company : this.Company,
        ShipVia : this.ShipVia
    })
";
                    var connectionStringName = $"{store.Database} to S3";

                    var configuration = new OlapEtlConfiguration
                    {
                        Name = "olap-s3-test",
                        ConnectionStringName = connectionStringName,
                        RunFrequency = LocalTests.DefaultFrequency,
                        Transforms =
                        {
                            new Transformation
                            {
                                Name = "MonthlyOrders",
                                Collections = new List<string> {"Orders"},
                                Script = script
                            }
                        }
                    };

                    SetupS3OlapEtl(store, settings, configuration);

                    etlDone.Wait(TimeSpan.FromMinutes(1));

                    using (var s3Client = new RavenAwsS3Client(settings, DefaultBackupConfiguration))
                    {
                        var prefix = $"{settings.RemoteFolderName}/{CollectionName}";
                        var cloudObjects = await s3Client.ListObjectsAsync(prefix, string.Empty, false);

                        Assert.Equal(2, cloudObjects.FileInfoDetails.Count);

                        Assert.Contains($"{partitionColumn}=2020-01-01", cloudObjects.FileInfoDetails[0].FullPath);
                        Assert.Contains($"{partitionColumn}=2020-02-01", cloudObjects.FileInfoDetails[1].FullPath);
                    }
                }
            }

            finally
            {
                await DeleteObjects(settings);
            }
        }

        [AmazonS3Fact]
        public async Task SimpleTransformation_NoPartition()
        {
            var settings = GetS3Settings();
            try
            {
                using (var store = GetDocumentStore())
                {
                    var baseline = new DateTime(2020, 1, 1).ToUniversalTime();

                    using (var session = store.OpenAsyncSession())
                    {
                        for (int i = 0; i < 100; i++)
                        {
                            await session.StoreAsync(new Order
                            {
                                Id = $"orders/{i}",
                                OrderedAt = baseline.AddDays(i),
                                ShipVia = $"shippers/{i}",
                                Company = $"companies/{i}"
                            });
                        }

                        await session.SaveChangesAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    var script = @"
loadToOrders(noPartition(),
    {
        OrderDate : this.OrderedAt
        Company : this.Company,
        ShipVia : this.ShipVia
    });
";
                    SetupS3OlapEtl(store, script, settings);

                    etlDone.Wait(TimeSpan.FromMinutes(1));

                    using (var s3Client = new RavenAwsS3Client(settings, DefaultBackupConfiguration))
                    {
                        var prefix = $"{settings.RemoteFolderName}/{CollectionName}";

                        var cloudObjects = await s3Client.ListObjectsAsync(prefix, delimiter: string.Empty, listFolders: false);

                        Assert.Equal(1, cloudObjects.FileInfoDetails.Count);

                        var blob = await s3Client.GetObjectAsync(cloudObjects.FileInfoDetails[0].FullPath);
                        await using var ms = new MemoryStream();
                        blob.Data.CopyTo(ms);

                        using (var parquetReader = new ParquetReader(ms))
                        {
                            Assert.Equal(1, parquetReader.RowGroupCount);

                            var expectedFields = new[] { "OrderDate", "ShipVia", "Company", ParquetTransformedItems.DefaultIdColumn, ParquetTransformedItems.LastModifiedColumn };

                            Assert.Equal(expectedFields.Length, parquetReader.Schema.Fields.Count);

                            using var rowGroupReader = parquetReader.OpenRowGroupReader(0);
                            foreach (var field in parquetReader.Schema.Fields)
                            {
                                Assert.True(field.Name.In(expectedFields));

                                var data = rowGroupReader.ReadColumn((DataField)field).Data;
                                Assert.True(data.Length == 100);

                                if (field.Name == ParquetTransformedItems.LastModifiedColumn)
                                    continue;

                                var count = 0;
                                foreach (var val in data)
                                {
                                    if (field.Name == "OrderDate")
                                    {
                                        var expectedDto = new DateTimeOffset(DateTime.SpecifyKind(baseline.AddDays(count), DateTimeKind.Utc));
                                        Assert.Equal(expectedDto, val);
                                    }

                                    else
                                    {
                                        var expected = field.Name switch
                                        {
                                            ParquetTransformedItems.DefaultIdColumn => $"orders/{count}",
                                            "Company" => $"companies/{count}",
                                            "ShipVia" => $"shippers/{count}",
                                            _ => null
                                        };

                                        Assert.Equal(expected, val);
                                    }

                                    count++;
                                }
                            }
                        }
                    }

                }

            }
            finally
            {
                await DeleteObjects(settings);
            }
        }

        [AmazonS3Fact]
        public async Task SimpleTransformation_MultiplePartitions()
        {
            var settings = GetS3Settings();
            try
            {
                using (var store = GetDocumentStore())
                {
                    var baseline = DateTime.SpecifyKind(new DateTime(2020, 1, 1), DateTimeKind.Utc);

                    using (var session = store.OpenAsyncSession())
                    {
                        const int total = 31 + 28; // days in January + days in February 

                        for (int i = 0; i < total; i++)
                        {
                            var orderedAt = baseline.AddDays(i);
                            await session.StoreAsync(new Order
                            {
                                Id = $"orders/{i}",
                                OrderedAt = orderedAt,
                                RequireAt = orderedAt.AddDays(7),
                                ShipVia = $"shippers/{i}",
                                Company = $"companies/{i}"
                            });
                        }

                        for (int i = 1; i <= 37; i++)
                        {
                            var index = i + total;
                            var orderedAt = baseline.AddYears(1).AddMonths(1).AddDays(i);
                            await session.StoreAsync(new Order
                            {
                                Id = $"orders/{index}",
                                OrderedAt = orderedAt,
                                RequireAt = orderedAt.AddDays(7),
                                ShipVia = $"shippers/{index}",
                                Company = $"companies/{index}"
                            });
                        }

                        await session.SaveChangesAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    var script = @"
var orderDate = new Date(this.OrderedAt);

loadToOrders(partitionBy(
    ['year', orderDate.getFullYear()],
    ['month', orderDate.getMonth() + 1]
),
    {
        Company : this.Company,
        ShipVia : this.ShipVia,
        RequireAt : this.RequireAt
    });
";
                    SetupS3OlapEtl(store, script, settings);

                    etlDone.Wait(TimeSpan.FromMinutes(1));

                    var expectedFields = new[] { "RequireAt", "ShipVia", "Company", ParquetTransformedItems.DefaultIdColumn, ParquetTransformedItems.LastModifiedColumn };

                    using (var s3Client = new RavenAwsS3Client(settings, DefaultBackupConfiguration))
                    {
                        var prefix = $"{settings.RemoteFolderName}/{CollectionName}/";
                        var cloudObjects = await s3Client.ListObjectsAsync(prefix, delimiter: "/", listFolders: true);

                        Assert.Equal(2, cloudObjects.FileInfoDetails.Count);
                        Assert.Contains("Orders/year=2020/", cloudObjects.FileInfoDetails[0].FullPath);
                        Assert.Contains("Orders/year=2021/", cloudObjects.FileInfoDetails[1].FullPath);

                        for (var index = 1; index <= cloudObjects.FileInfoDetails.Count; index++)
                        {
                            var folder = cloudObjects.FileInfoDetails[index - 1];
                            var objectsInFolder = await s3Client.ListObjectsAsync(prefix: folder.FullPath, delimiter: "/", listFolders: true);

                            Assert.Equal(2, objectsInFolder.FileInfoDetails.Count);
                            Assert.Contains($"month={index}/", objectsInFolder.FileInfoDetails[0].FullPath);
                            Assert.Contains($"month={index + 1}/", objectsInFolder.FileInfoDetails[1].FullPath);
                        }

                        var files = await ListAllFilesInFolders(s3Client, cloudObjects);
                        Assert.Equal(4, files.Count);

                        foreach (var filePath in files)
                        {
                            var blob = await s3Client.GetObjectAsync(filePath);
                            await using var ms = new MemoryStream();
                            blob.Data.CopyTo(ms);

                            using (var parquetReader = new ParquetReader(ms))
                            {
                                Assert.Equal(1, parquetReader.RowGroupCount);
                                Assert.Equal(expectedFields.Length, parquetReader.Schema.Fields.Count);

                                using var rowGroupReader = parquetReader.OpenRowGroupReader(0);
                                foreach (var field in parquetReader.Schema.Fields)
                                {
                                    Assert.True(field.Name.In(expectedFields));
                                    var data = rowGroupReader.ReadColumn((DataField)field).Data;

                                    Assert.True(data.Length == 31 || data.Length == 28 || data.Length == 27 || data.Length == 10);
                                    if (field.Name != "RequireAt")
                                        continue;

                                    var count = data.Length switch
                                    {
                                        31 => 0,
                                        28 => 31,
                                        27 => 365 + 33,
                                        10 => 365 + 33 + 27,
                                        _ => throw new ArgumentOutOfRangeException()
                                    };

                                    foreach (var val in data)
                                    {
                                        var expectedOrderDate = new DateTimeOffset(DateTime.SpecifyKind(baseline.AddDays(count++), DateTimeKind.Utc));
                                        var expected = expectedOrderDate.AddDays(7);
                                        Assert.Equal(expected, val);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                await DeleteObjects(settings, prefix: $"{settings.RemoteFolderName}/{CollectionName}/", delimiter: "/", listFolder: true);
            }
        }

        [AmazonS3Fact]
        public async Task CanPartitionByCustomDataFieldViaScript()
        {
            var settings = GetS3Settings();
            try
            {
                using (var store = GetDocumentStore())
                {
                    var baseline = new DateTime(2020, 1, 1);

                    using (var session = store.OpenAsyncSession())
                    {
                        for (int i = 0; i < 31; i++)
                        {
                            await session.StoreAsync(new Order
                            {
                                Id = $"orders/{i}",
                                OrderedAt = baseline.AddDays(i),
                                ShipVia = $"shippers/{i}",
                                Company = $"companies/{i}"
                            });
                        }

                        for (int i = 0; i < 28; i++)
                        {
                            await session.StoreAsync(new Order
                            {
                                Id = $"orders/{i + 31}",
                                OrderedAt = baseline.AddMonths(1).AddDays(i),
                                ShipVia = $"shippers/{i + 31}",
                                Company = $"companies/{i + 31}"
                            });
                        }

                        await session.SaveChangesAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    var script = @"
var orderDate = new Date(this.OrderedAt);
var year = orderDate.getFullYear();
var month = orderDate.getMonth() + 1;

loadToOrders(partitionBy(['year', year], ['month', month], ['source', $customPartitionValue]),
{
    Company : this.Company,
    ShipVia : this.ShipVia
});
";

                    const string customPartition = "shop-16";
                    SetupS3OlapEtl(store, script, settings, customPartitionValue: customPartition);

                    etlDone.Wait(TimeSpan.FromMinutes(1));

                    using (var s3Client = new RavenAwsS3Client(settings, DefaultBackupConfiguration))
                    {
                        var prefix = $"{settings.RemoteFolderName}/{CollectionName}/";
                        var cloudObjects = await s3Client.ListObjectsAsync(prefix, delimiter: "/", listFolders: true);
                        Assert.Equal(1, cloudObjects.FileInfoDetails.Count);

                        var files = await ListAllFilesInFolders(s3Client, cloudObjects);
                        Assert.Equal(2, files.Count);
                        Assert.Contains($"/Orders/year=2020/month=1/source={customPartition}/", files[0]);
                        Assert.Contains($"/Orders/year=2020/month=2/source={customPartition}/", files[1]);
                    }
                }
            }
            finally
            {
                await DeleteObjects(settings, prefix: $"{settings.RemoteFolderName}/{CollectionName}/", delimiter: "/", listFolder: true);
            }
        }

        [AmazonS3Fact]
        public async Task CanHandleSpecialCharsInEtlName()
        {
            var settings = GetS3Settings();
            try
            {
                using (var store = GetDocumentStore())
                {
                    await store.Maintenance.SendAsync(new CreateSampleDataOperation());

                    Indexes.WaitForIndexing(store);

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    var script = @"
loadToOrders(noPartition(), {
    Company: this.Company,
    OrderedAt: this.OrderedAt
});"
;
                    SetupS3OlapEtl(store, script, settings, transformationName: "script#1=$'/orders'");

                    etlDone.Wait(TimeSpan.FromMinutes(1));

                    using (var s3Client = new RavenAwsS3Client(settings, DefaultBackupConfiguration))
                    {
                        var prefix = $"{settings.RemoteFolderName}/{CollectionName}";
                        var cloudObjects = await s3Client.ListObjectsAsync(prefix, delimiter: string.Empty, listFolders: false);

                        Assert.Equal(1, cloudObjects.FileInfoDetails.Count);
                        Assert.Contains("script#1=$'_orders'", cloudObjects.FileInfoDetails[0].FullPath);
                        Assert.Contains(".parquet", cloudObjects.FileInfoDetails[0].FullPath);
                    }
                }
            }
            finally
            {
                await DeleteObjects(settings, prefix: $"{settings.RemoteFolderName}/{CollectionName}", delimiter: string.Empty);

            }
        }

        [AmazonS3Fact]
        public async Task CanHandleSpecialCharsInFolderPath()
        {
            var settings = GetS3Settings();
            try
            {
                using (var store = GetDocumentStore())
                {
                    using (var session = store.OpenAsyncSession())
                    {
                        var today = DateTime.Today;
                        await session.StoreAsync(new Order
                        {
                            OrderedAt = today,
                            Lines = new List<OrderLine>
                            {
                                new OrderLine
                                {
                                    ProductName = "Wimmers gute Semmelknödel",
                                    PricePerUnit = 12
                                },
                                new OrderLine
                                {
                                    ProductName = "Guaraná Fantástica",
                                    PricePerUnit = 42
                                },

                                new OrderLine
                                {
                                    ProductName = "Thüringer Rostbratwurst",
                                    PricePerUnit = 19
                                }
                            }
                        });

                        await session.StoreAsync(new Order
                        {
                            OrderedAt = today.AddYears(-1),
                            Lines = new List<OrderLine>
                            {
                                new OrderLine
                                {
                                    ProductName = "Uncle Bob's Cajon Sauce",
                                    PricePerUnit = 11
                                },
                                new OrderLine
                                {
                                    ProductName = "Côte de Blaye",
                                    PricePerUnit = 25
                                }
                            }
                        });

                        await session.StoreAsync(new Order
                        {
                            OrderedAt = today.AddYears(-2),
                            Lines = new List<OrderLine>
                            {
                                new OrderLine
                                {
                                    ProductName = "גבינה צהובה",
                                    PricePerUnit = 20
                                },
                                new OrderLine
                                {
                                    ProductName = "במבה",
                                    PricePerUnit = 7
                                }
                            }
                        });

                        await session.SaveChangesAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    var script = @"
for (var i = 0; i < this.Lines.length; i++){
    var line  = this.Lines[i];
    loadToOrders(partitionBy(['product-name', line.ProductName]), {
        PricePerUnit: line.PricePerUnit,
        OrderedAt: this.OrderedAt
})}";
                    SetupS3OlapEtl(store, script, settings);

                    etlDone.Wait(TimeSpan.FromMinutes(1));

                    using (var s3Client = new RavenAwsS3Client(settings, DefaultBackupConfiguration))
                    {
                        var prefix = $"{settings.RemoteFolderName}/{CollectionName}";
                        var cloudObjects = await s3Client.ListObjectsAsync(prefix, delimiter: string.Empty, listFolders: false);

                        Assert.Equal(7, cloudObjects.FileInfoDetails.Count);

                        Assert.Contains("Côte de Blaye", cloudObjects.FileInfoDetails[0].FullPath);
                        Assert.Contains("Guaraná Fantástica", cloudObjects.FileInfoDetails[1].FullPath);
                        Assert.Contains("Thüringer Rostbratwurst", cloudObjects.FileInfoDetails[2].FullPath);
                        Assert.Contains("Uncle Bob's Cajon Sauce", cloudObjects.FileInfoDetails[3].FullPath);
                        Assert.Contains("Wimmers gute Semmelknödel", cloudObjects.FileInfoDetails[4].FullPath);
                        Assert.Contains("במבה", cloudObjects.FileInfoDetails[5].FullPath);
                        Assert.Contains("גבינה צהובה", cloudObjects.FileInfoDetails[6].FullPath);
                    }
                }
            }
            finally
            {
                await DeleteObjects(settings, prefix: $"{settings.RemoteFolderName}/{CollectionName}", delimiter: string.Empty);
            }
        }

        [AmazonS3Fact]
        public async Task CanHandleSlashInPartitionValue()
        {
            var settings = GetS3Settings();
            try
            {
                using (var store = GetDocumentStore())
                {
                    using (var session = store.OpenAsyncSession())
                    {
                        var baseline = new DateTime(2020, 1, 1);
                        for (int i = 0; i < 10; i++)
                        {
                            await session.StoreAsync(new Order
                            {
                                OrderedAt = baseline.AddDays(i),
                                Company = $"companies/{i % 5}"
                            });
                        }

                        await session.SaveChangesAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    var script = @"
    loadToOrders(partitionBy(['Company', this.Company]), {
        OrderDate : new Date(this.OrderedAt)
    })
";
                    SetupS3OlapEtl(store, script, settings);

                    etlDone.Wait(TimeSpan.FromMinutes(1));

                    using (var s3Client = new RavenAwsS3Client(settings, DefaultBackupConfiguration))
                    {
                        var prefix = $"{settings.RemoteFolderName}/{CollectionName}";
                        var cloudObjects = await s3Client.ListObjectsAsync(prefix, delimiter: string.Empty, listFolders: false);

                        Assert.Equal(5, cloudObjects.FileInfoDetails.Count);

                        Assert.Contains("/Company=companies_0", cloudObjects.FileInfoDetails[0].FullPath);
                        Assert.Contains("/Company=companies_1", cloudObjects.FileInfoDetails[1].FullPath);
                        Assert.Contains("/Company=companies_2", cloudObjects.FileInfoDetails[2].FullPath);
                        Assert.Contains("/Company=companies_3", cloudObjects.FileInfoDetails[3].FullPath);
                        Assert.Contains("/Company=companies_4", cloudObjects.FileInfoDetails[4].FullPath);
                    }
                }
            }
            finally
            {
                await DeleteObjects(settings, prefix: $"{settings.RemoteFolderName}/{CollectionName}", delimiter: string.Empty);
            }
        }

        [AmazonS3Fact]
        public async Task CanUpdateS3Settings()
        {
            var settings = GetS3Settings();
            S3Settings settings1 = default, settings2 = default;
            try
            {
                using (var store = GetDocumentStore())
                {
                    var dt = new DateTime(2020, 1, 1);

                    using (var session = store.OpenAsyncSession())
                    {
                        for (int i = 1; i <= 5; i++)
                        {
                            var o = new Order
                            {
                                Id = $"orders/{i}",
                                Company = $"companies/{i}",
                                Employee = $"employees/{i}",
                                OrderedAt = dt,
                            };

                            await session.StoreAsync(o);

                            dt = dt.AddYears(1);
                        }

                        await session.SaveChangesAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    var script = @"
var orderDate = new Date(this.OrderedAt);

loadToOrders(partitionBy(['year', orderDate.getFullYear()]), 
{
    company: this.Company,
    employee: this.Employee
}
);
";
                    var connectionStringName = $"{store.Database} to s3";

                    var configuration = new OlapEtlConfiguration
                    {
                        Name = "olap-test",
                        ConnectionStringName = connectionStringName,
                        RunFrequency = LocalTests.DefaultFrequency,
                        Transforms =
                        {
                            new Transformation
                            {
                                Name = "MonthlyOrders",
                                Collections = new List<string> {"Orders"},
                                Script = script
                            }
                        }
                    };

                    var originalRemoteFolder = settings.RemoteFolderName;
                    var remoteFolderName = $"{originalRemoteFolder}/test_1";

                    settings1 = new S3Settings
                    {
                        AwsAccessKey = settings.AwsAccessKey,
                        AwsSecretKey = settings.AwsSecretKey,
                        AwsRegionName = settings.AwsRegionName,
                        BucketName = settings.BucketName,
                        RemoteFolderName = remoteFolderName
                    };

                    var connectionString = new OlapConnectionString
                    {
                        Name = connectionStringName,
                        S3Settings = settings1
                    };

                    var result = AddEtl(store, configuration, connectionString);
                    var taskId = result.TaskId;

                    etlDone.Wait(TimeSpan.FromMinutes(1));

                    using (var s3Client = new RavenAwsS3Client(settings1, DefaultBackupConfiguration))
                    {
                        var prefix = $"{settings1.RemoteFolderName}/{CollectionName}";
                        var cloudObjects = await s3Client.ListObjectsAsync(prefix, delimiter: string.Empty, listFolders: false);

                        Assert.Equal(5, cloudObjects.FileInfoDetails.Count);

                        Assert.Contains("/year=2020", cloudObjects.FileInfoDetails[0].FullPath);
                        Assert.Contains("/year=2021", cloudObjects.FileInfoDetails[1].FullPath);
                        Assert.Contains("/year=2022", cloudObjects.FileInfoDetails[2].FullPath);
                        Assert.Contains("/year=2023", cloudObjects.FileInfoDetails[3].FullPath);
                        Assert.Contains("/year=2024", cloudObjects.FileInfoDetails[4].FullPath);
                    }

                    // disable task 

                    configuration.Disabled = true;
                    var update = store.Maintenance.Send(new UpdateEtlOperation<OlapConnectionString>(taskId, configuration));
                    taskId = update.TaskId;
                    Assert.NotNull(update.RaftCommandIndex);

                    // update connection string

                    remoteFolderName = $"{originalRemoteFolder}/test_2";
                    settings2 = new S3Settings
                    {
                        AwsAccessKey = settings.AwsAccessKey,
                        AwsSecretKey = settings.AwsSecretKey,
                        AwsRegionName = settings.AwsRegionName,
                        BucketName = settings.BucketName,
                        RemoteFolderName = remoteFolderName
                    };

                    connectionString.S3Settings = settings2;

                    var putResult = store.Maintenance.Send(new PutConnectionStringOperation<OlapConnectionString>(connectionString));
                    Assert.NotNull(putResult.RaftCommandIndex);

                    // add more data

                    using (var session = store.OpenAsyncSession())
                    {
                        for (int i = 6; i <= 10; i++)
                        {
                            var o = new Order
                            {
                                Id = $"orders/{i}",
                                Company = $"companies/{i}",
                                Employee = $"employees/{i}",
                                OrderedAt = dt,
                            };

                            await session.StoreAsync(o);

                            dt = dt.AddYears(1);
                        }

                        await session.SaveChangesAsync();
                    }

                    // re enable task

                    configuration.Disabled = false;
                    update = store.Maintenance.Send(new UpdateEtlOperation<OlapConnectionString>(taskId, configuration));
                    Assert.NotNull(update.RaftCommandIndex);

                    etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);
                    Assert.True(etlDone.Wait(TimeSpan.FromMinutes(1)));

                    using (var s3Client = new RavenAwsS3Client(settings2, DefaultBackupConfiguration))
                    {
                        var prefix = $"{settings2.RemoteFolderName}/{CollectionName}";
                        var cloudObjects = await s3Client.ListObjectsAsync(prefix, delimiter: string.Empty, listFolders: false);

                        Assert.Equal(5, cloudObjects.FileInfoDetails.Count);

                        Assert.Contains("/year=2025", cloudObjects.FileInfoDetails[0].FullPath);
                        Assert.Contains("/year=2026", cloudObjects.FileInfoDetails[1].FullPath);
                        Assert.Contains("/year=2027", cloudObjects.FileInfoDetails[2].FullPath);
                        Assert.Contains("/year=2028", cloudObjects.FileInfoDetails[3].FullPath);
                        Assert.Contains("/year=2029", cloudObjects.FileInfoDetails[4].FullPath);
                    }
                }
            }
            finally
            {
                await DeleteObjects(settings1, prefix: $"{settings1?.RemoteFolderName}/{CollectionName}", delimiter: string.Empty);
                await DeleteObjects(settings2, prefix: $"{settings2?.RemoteFolderName}/{CollectionName}", delimiter: string.Empty);
            }
        }

        [AmazonS3Fact]
        public async Task ShouldTrimRedundantSlashInRemoteFolderName()
        {
            var settings = GetS3Settings();
            try
            {
                using (var store = GetDocumentStore())
                {
                    using (var session = store.OpenAsyncSession())
                    {
                        var baseline = new DateTime(2020, 1, 1);
                        for (int i = 0; i < 10; i++)
                        {
                            await session.StoreAsync(new Order
                            {
                                OrderedAt = baseline.AddDays(i),
                                Company = $"companies/{i % 5}"
                            });
                        }

                        await session.SaveChangesAsync();
                    }

                    var etlDone = WaitForEtl(store, (n, statistics) => statistics.LoadSuccesses != 0);

                    var script = @"
    loadToOrders(partitionBy(['Company', this.Company]), {
        OrderDate : new Date(this.OrderedAt)
    })
";

                    if (settings.RemoteFolderName.EndsWith('/') == false)
                        settings.RemoteFolderName += '/';

                    SetupS3OlapEtl(store, script, settings);

                    etlDone.Wait(TimeSpan.FromMinutes(1));

                    using (var s3Client = new RavenAwsS3Client(settings, DefaultBackupConfiguration))
                    {
                        var prefix = $"{settings.RemoteFolderName}{CollectionName}";
                        Assert.False(prefix.EndsWith('/'));

                        var cloudObjects = await s3Client.ListObjectsAsync(prefix, delimiter: string.Empty, listFolders: false);

                        Assert.Equal(5, cloudObjects.FileInfoDetails.Count);

                        Assert.Contains($"{prefix}/Company=companies_0", cloudObjects.FileInfoDetails[0].FullPath);
                        Assert.Contains($"{prefix}/Company=companies_1", cloudObjects.FileInfoDetails[1].FullPath);
                        Assert.Contains($"{prefix}/Company=companies_2", cloudObjects.FileInfoDetails[2].FullPath);
                        Assert.Contains($"{prefix}/Company=companies_3", cloudObjects.FileInfoDetails[3].FullPath);
                        Assert.Contains($"{prefix}/Company=companies_4", cloudObjects.FileInfoDetails[4].FullPath);

                        foreach (var file in cloudObjects.FileInfoDetails)
                        {
                            Assert.DoesNotContain("//", file.FullPath);
                        }
                    }
                }
            }
            finally
            {
                await DeleteObjects(settings, prefix: $"{settings.RemoteFolderName}{CollectionName}", delimiter: string.Empty);
            }
        }

        private void SetupS3OlapEtl(DocumentStore store, string script, S3Settings settings, string customPartitionValue = null, string transformationName = null)
        {
            var connectionStringName = $"{store.Database} to S3";

            var configuration = new OlapEtlConfiguration
            {
                Name = "olap-s3-test",
                ConnectionStringName = connectionStringName,
                RunFrequency = LocalTests.DefaultFrequency,
                CustomPartitionValue = customPartitionValue,
                Transforms =
                {
                    new Transformation
                    {
                        Name = transformationName ?? "MonthlyOrders",
                        Collections = new List<string> {"Orders"},
                        Script = script
                    }
                }
            };
            AddEtl(store, configuration, new OlapConnectionString
            {
                Name = connectionStringName,
                S3Settings = settings
            });
        }

        private void SetupS3OlapEtl(DocumentStore store, S3Settings settings, OlapEtlConfiguration configuration)
        {
            var connectionStringName = $"{store.Database} to S3";
            AddEtl(store, configuration, new OlapConnectionString
            {
                Name = connectionStringName,
                S3Settings = settings
            });
        }

        private S3Settings GetS3Settings([CallerMemberName] string caller = null)
        {
            var s3Settings = AmazonS3FactAttribute.S3Settings;
            if (s3Settings == null)
                return null;

            var remoteFolderName = _s3TestsPrefix;
            if (string.IsNullOrEmpty(caller) == false)
                remoteFolderName = $"{remoteFolderName}/{caller}";

            if (string.IsNullOrEmpty(s3Settings.RemoteFolderName) == false)
                remoteFolderName = $"{s3Settings.RemoteFolderName}/{remoteFolderName}";


            return new S3Settings
            {
                BucketName = s3Settings.BucketName,
                RemoteFolderName = remoteFolderName,
                AwsAccessKey = s3Settings.AwsAccessKey,
                AwsSecretKey = s3Settings.AwsSecretKey,
                AwsRegionName = s3Settings.AwsRegionName
            };
        }

        private static async Task DeleteObjects(S3Settings s3Settings, string additionalTable = null)
        {
            if (s3Settings == null)
                return;

            await DeleteObjects(s3Settings, prefix: $"{s3Settings.RemoteFolderName}/{CollectionName}", delimiter: string.Empty);

            if (additionalTable == null)
                return;

            await DeleteObjects(s3Settings, prefix: $"{s3Settings.RemoteFolderName}/{additionalTable}", delimiter: string.Empty);
        }

        private static async Task DeleteObjects(S3Settings s3Settings, string prefix, string delimiter, bool listFolder = false)
        {
            if (s3Settings == null)
                return;

            try
            {
                using (var s3Client = new RavenAwsS3Client(s3Settings, DefaultBackupConfiguration))
                {
                    var cloudObjects = await s3Client.ListObjectsAsync(prefix, delimiter, listFolder);
                    if (cloudObjects.FileInfoDetails.Count == 0)
                        return;

                    if (listFolder == false)
                    {
                        var pathsToDelete = cloudObjects.FileInfoDetails.Select(x => x.FullPath).ToList();
                        s3Client.DeleteMultipleObjects(pathsToDelete);
                        return;
                    }

                    var filesToDelete = await ListAllFilesInFolders(s3Client, cloudObjects);
                    s3Client.DeleteMultipleObjects(filesToDelete);
                }
            }
            catch (Exception)
            {
                // ignored
            }
        }

        private static async Task<List<string>> ListAllFilesInFolders(RavenAwsS3Client s3Client, ListObjectsResult cloudObjects)
        {
            var files = new List<string>();
            foreach (var folder in cloudObjects.FileInfoDetails)
            {
                var objectsInFolder = await s3Client.ListObjectsAsync(prefix: folder.FullPath, delimiter: string.Empty, listFolders: false);
                files.AddRange(objectsInFolder.FileInfoDetails.Select(fi => fi.FullPath));
            }

            return files;
        }

    }
}
