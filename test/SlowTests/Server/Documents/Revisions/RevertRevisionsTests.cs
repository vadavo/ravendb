﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FastTests.Server.Replication;
using FastTests.Utils;
using Raven.Client;
using Raven.Client.Documents.Operations.Revisions;
using Raven.Client.ServerWide;
using Raven.Server.Documents;
using Raven.Server.ServerWide;
using Raven.Tests.Core.Utils.Entities;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Server.Documents.Revisions
{
    public class RevertRevisionsTests : ReplicationTestBase
    {
        public RevertRevisionsTests(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public async Task Revert()
        {
            var company = new Company { Name = "Company Name" };
            using (var store = GetDocumentStore())
            {
                DateTime last = default;
                await RevisionsHelper.SetupRevisions(Server.ServerStore, store.Database);
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(company);
                    await session.SaveChangesAsync();
                    last = DateTime.UtcNow;
                }

                using (var session = store.OpenAsyncSession())
                {
                    company.Name = "Hibernating Rhinos";
                    await session.StoreAsync(company);
                    await session.SaveChangesAsync();
                }

                var db = await Databases.GetDocumentDatabaseInstanceFor(store);

                RevertResult result;
                using (var token = new OperationCancelToken(db.Configuration.Databases.OperationTimeout.AsTimeSpan, db.DatabaseShutdown, CancellationToken.None))
                {
                    result = (RevertResult)await db.DocumentsStorage.RevisionsStorage.RevertRevisions(last, TimeSpan.FromMinutes(60), onProgress: null,
                        token: token);
                }

                Assert.Equal(2, result.ScannedRevisions);
                Assert.Equal(1, result.ScannedDocuments);
                Assert.Equal(1, result.RevertedDocuments);

                using (var session = store.OpenAsyncSession())
                {
                    var companiesRevisions = await session.Advanced.Revisions.GetForAsync<Company>(company.Id);
                    Assert.Equal(3, companiesRevisions.Count);

                    Assert.Equal("Company Name", companiesRevisions[0].Name);
                    Assert.Equal("Hibernating Rhinos", companiesRevisions[1].Name);
                    Assert.Equal("Company Name", companiesRevisions[2].Name);
                }
            }
        }

        [Fact]
        public async Task RevertNewDocumentToBin()
        {
            var company = new Company { Name = "Hibernating Rhinos" };
            var last = DateTime.UtcNow;

            using (var store = GetDocumentStore())
            {
                await RevisionsHelper.SetupRevisions(Server.ServerStore, store.Database);
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(company);
                    await session.SaveChangesAsync();
                }

                var db = await Databases.GetDocumentDatabaseInstanceFor(store);

                RevertResult result;
                using (var token = new OperationCancelToken(db.Configuration.Databases.OperationTimeout.AsTimeSpan, db.DatabaseShutdown, CancellationToken.None))
                {
                    result = (RevertResult)await db.DocumentsStorage.RevisionsStorage.RevertRevisions(last, TimeSpan.FromMinutes(60), onProgress: null,
                        token: token);
                }

                Assert.Equal(1, result.ScannedRevisions);
                Assert.Equal(1, result.ScannedDocuments);
                Assert.Equal(1, result.RevertedDocuments);

                using (var session = store.OpenAsyncSession())
                {
                    var companiesRevisions = await session.Advanced.Revisions.GetForAsync<Company>(company.Id);
                    Assert.Equal(2, companiesRevisions.Count);

                    Assert.Equal(null, companiesRevisions[0].Name);
                    Assert.Equal("Hibernating Rhinos", companiesRevisions[1].Name);
                }
            }
        }

        [Fact]
        public async Task RevertRevisionOutsideTheWindow()
        {
            var company = new Company { Name = "Company Name" };
            using (var store = GetDocumentStore())
            {
                await RevisionsHelper.SetupRevisions(Server.ServerStore, store.Database);
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(company);
                    await session.SaveChangesAsync();
                }

                await Task.Delay(2000);

                using (var session = store.OpenAsyncSession())
                {
                    company.Name = "Hibernating Rhinos";
                    await session.StoreAsync(company);
                    await session.SaveChangesAsync();
                }

                await Task.Delay(5000);

                DateTime last = DateTime.UtcNow;
                last = last.AddSeconds(-3);

                using (var session = store.OpenAsyncSession())
                {
                    company.Name = "Hibernating Rhinos 2";
                    await session.StoreAsync(company);
                    await session.SaveChangesAsync();
                }

                await Task.Delay(2000);

                var db = await Databases.GetDocumentDatabaseInstanceFor(store);

                RevertResult result;
                using (var token = new OperationCancelToken(db.Configuration.Databases.OperationTimeout.AsTimeSpan, db.DatabaseShutdown, CancellationToken.None))
                {
                    result = (RevertResult)await db.DocumentsStorage.RevisionsStorage.RevertRevisions(last, TimeSpan.FromSeconds(1), onProgress: null,
                        token: token);
                }

                Assert.Equal(3, result.ScannedRevisions);
                Assert.Equal(1, result.ScannedDocuments);
                Assert.Equal(1, result.RevertedDocuments);

                using (var session = store.OpenAsyncSession())
                {
                    var companiesRevisions = await session.Advanced.Revisions.GetForAsync<Company>(company.Id);
                    Assert.Equal(4, companiesRevisions.Count);

                    Assert.Equal("Hibernating Rhinos", companiesRevisions[0].Name);
                    Assert.Equal("Hibernating Rhinos 2", companiesRevisions[1].Name);
                    Assert.Equal("Hibernating Rhinos", companiesRevisions[2].Name);
                    Assert.Equal("Company Name", companiesRevisions[3].Name);
                }
            }
        }

        [Fact]
        public async Task RevertToOldestIfRevisionLimitReached()
        {
            var last = DateTime.UtcNow;

            using (var store = GetDocumentStore())
            {
                await RevisionsHelper.SetupRevisions(Server.ServerStore, store.Database);
                var name = "Hibernating Rhinos";
                var company = new Company();

                using (var session = store.OpenAsyncSession())
                {
                    for (int i = 0; i < 6; i++)
                    {
                        company.Name = name + " " + i;
                        await session.StoreAsync(company, "foo/bar");
                        await session.SaveChangesAsync();
                    }
                }

                var db = await Databases.GetDocumentDatabaseInstanceFor(store);

                RevertResult result;
                using (var token = new OperationCancelToken(db.Configuration.Databases.OperationTimeout.AsTimeSpan, db.DatabaseShutdown, CancellationToken.None))
                {
                    result = (RevertResult)await db.DocumentsStorage.RevisionsStorage.RevertRevisions(last, TimeSpan.FromMinutes(60), onProgress: null,
                        token: token);
                }

                Assert.Equal(5, result.ScannedRevisions);
                Assert.Equal(1, result.ScannedDocuments);
                Assert.Equal(1, result.RevertedDocuments);

                using (var session = store.OpenAsyncSession())
                {
                    var companiesRevisions = await session.Advanced.Revisions.GetForAsync<Company>("foo/bar");
                    Assert.Equal(5, companiesRevisions.Count);

                    Assert.Equal("Hibernating Rhinos 1", companiesRevisions[0].Name);
                    Assert.Equal("Hibernating Rhinos 5", companiesRevisions[1].Name);
                    Assert.Equal("Hibernating Rhinos 4", companiesRevisions[2].Name);
                    Assert.Equal("Hibernating Rhinos 3", companiesRevisions[3].Name);
                    Assert.Equal("Hibernating Rhinos 2", companiesRevisions[4].Name);
                }
            }
        }

        [Fact]
        public async Task DontRevertOldDocument()
        {
            var company = new Company { Name = "Company Name" };
            using (var store = GetDocumentStore())
            {
                await RevisionsHelper.SetupRevisions(Server.ServerStore, store.Database);
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(company);
                    await session.SaveChangesAsync();
                }

                using (var session = store.OpenAsyncSession())
                {
                    company.Name = "Hibernating Rhinos";
                    await session.StoreAsync(company);
                    await session.SaveChangesAsync();
                }
                DateTime last = DateTime.UtcNow;

                var db = await Databases.GetDocumentDatabaseInstanceFor(store);

                RevertResult result;
                using (var token = new OperationCancelToken(db.Configuration.Databases.OperationTimeout.AsTimeSpan, db.DatabaseShutdown, CancellationToken.None))
                {
                    result = (RevertResult)await db.DocumentsStorage.RevisionsStorage.RevertRevisions(last, TimeSpan.FromMinutes(60), onProgress: null,
                        token: token);
                }

                Assert.Equal(2, result.ScannedRevisions);
                Assert.Equal(1, result.ScannedDocuments);
                Assert.Equal(0, result.RevertedDocuments);

                using (var session = store.OpenAsyncSession())
                {
                    var companiesRevisions = await session.Advanced.Revisions.GetForAsync<Company>(company.Id);
                    Assert.Equal(2, companiesRevisions.Count);

                    Assert.Equal("Hibernating Rhinos", companiesRevisions[0].Name);
                    Assert.Equal("Company Name", companiesRevisions[1].Name);
                }
            }
        }

        [Fact]
        public async Task DontRevertNewDocument()
        {
            var company = new Company { Name = "Company Name" };
            using (var store = GetDocumentStore())
            {
                DateTime last = DateTime.UtcNow;
                await RevisionsHelper.SetupRevisions(Server.ServerStore, store.Database);
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(company);
                    await session.SaveChangesAsync();
                }

                var db = await Databases.GetDocumentDatabaseInstanceFor(store);

                RevertResult result;
                using (var token = new OperationCancelToken(db.Configuration.Databases.OperationTimeout.AsTimeSpan, db.DatabaseShutdown, CancellationToken.None))
                {
                    result = (RevertResult)await db.DocumentsStorage.RevisionsStorage.RevertRevisions(last.Add(TimeSpan.FromMinutes(1)), TimeSpan.FromMinutes(60),
                        onProgress: null, token: token);
                }

                Assert.Equal(1, result.ScannedRevisions);
                Assert.Equal(1, result.ScannedDocuments);
                Assert.Equal(0, result.RevertedDocuments);

                using (var session = store.OpenAsyncSession())
                {
                    var companiesRevisions = await session.Advanced.Revisions.GetForAsync<Company>(company.Id);
                    Assert.Equal(1, companiesRevisions.Count);
                    Assert.Equal("Company Name", companiesRevisions[0].Name);
                }
            }
        }

        [Fact]
        public async Task RevertFromDeleted()
        {
            var company = new Company { Name = "Company Name" };
            using (var store = GetDocumentStore())
            {
                DateTime last = default;

                await RevisionsHelper.SetupRevisions(Server.ServerStore, store.Database);
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(company);
                    await session.SaveChangesAsync();
                    last = DateTime.UtcNow;
                    session.Delete(company.Id);
                    await session.SaveChangesAsync();
                }

                var db = await Databases.GetDocumentDatabaseInstanceFor(store);

                RevertResult result;
                using (var token = new OperationCancelToken(db.Configuration.Databases.OperationTimeout.AsTimeSpan, db.DatabaseShutdown, CancellationToken.None))
                {
                    result = (RevertResult)await db.DocumentsStorage.RevisionsStorage.RevertRevisions(last, TimeSpan.FromMinutes(60), onProgress: null,
                        token: token);
                }

                Assert.Equal(2, result.ScannedRevisions);
                Assert.Equal(1, result.ScannedDocuments);
                Assert.Equal(1, result.RevertedDocuments);

                using (var session = store.OpenAsyncSession())
                {
                    var companiesRevisions = await session.Advanced.Revisions.GetForAsync<Company>(company.Id);
                    Assert.Equal(3, companiesRevisions.Count);

                    Assert.Equal("Company Name", companiesRevisions[0].Name);
                    Assert.Equal(null, companiesRevisions[1].Name);
                    Assert.Equal("Company Name", companiesRevisions[2].Name);
                }
            }
        }

        [Fact]
        public async Task RevertToDeleted()
        {
            var company = new Company { Name = "Company Name" };
            using (var store = GetDocumentStore())
            {
                DateTime last = default;
                await RevisionsHelper.SetupRevisions(Server.ServerStore, store.Database);
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(company);
                    await session.SaveChangesAsync();

                    session.Delete(company);
                    await session.SaveChangesAsync();

                    last = DateTime.UtcNow;

                    await session.StoreAsync(company);
                    await session.SaveChangesAsync();
                }

                var db = await Databases.GetDocumentDatabaseInstanceFor(store);

                RevertResult result;
                using (var token = new OperationCancelToken(db.Configuration.Databases.OperationTimeout.AsTimeSpan, db.DatabaseShutdown, CancellationToken.None))
                {
                    result = (RevertResult)await db.DocumentsStorage.RevisionsStorage.RevertRevisions(last, TimeSpan.FromMinutes(60), onProgress: null,
                        token: token);
                }

                Assert.Equal(3, result.ScannedRevisions);
                Assert.Equal(1, result.ScannedDocuments);
                Assert.Equal(1, result.RevertedDocuments);

                using (var session = store.OpenAsyncSession())
                {
                    var companiesRevisions = await session.Advanced.Revisions.GetForAsync<Company>(company.Id);
                    Assert.Equal(4, companiesRevisions.Count);

                    Assert.Equal(null, companiesRevisions[0].Name);
                    Assert.Equal("Company Name", companiesRevisions[1].Name);
                    Assert.Equal(null, companiesRevisions[2].Name);
                    Assert.Equal("Company Name", companiesRevisions[3].Name);
                }
            }
        }

        [Fact]
        public async Task DontRevertToConflicted()
        {
            // put at 8:30
            // conflicted at 8:50
            // resolved at 9:10
            // will revert to 9:00

            using (var store1 = GetDocumentStore(options: new Options
            {
                ModifyDatabaseRecord = record =>
                {
                    record.ConflictSolverConfig = new ConflictSolver
                    {
                        ResolveToLatest = false,
                        ResolveByCollection = new Dictionary<string, ScriptResolver>()
                    };
                }
            }))
            using (var store2 = GetDocumentStore())
            {
                DateTime last = default;
                await RevisionsHelper.SetupRevisions(Server.ServerStore, store1.Database);

                using (var session = store2.OpenAsyncSession())
                {
                    await session.StoreAsync(new Company(), "keep-conflicted-revision-insert-order");
                    await session.SaveChangesAsync();

                    var company = new Company
                    {
                        Name = "Name2"
                    };
                    await session.StoreAsync(company, "foo/bar");
                    await session.SaveChangesAsync();
                }

                using (var session = store1.OpenAsyncSession())
                {
                    var company = new Company
                    {
                        Name = "Name1"
                    };
                    await session.StoreAsync(company, "foo/bar");
                    await session.SaveChangesAsync();
                    var companiesRevisions = await session.Advanced.Revisions.GetForAsync<Company>("foo/bar");
                    Assert.Equal(1, companiesRevisions.Count);
                }

                await SetupReplicationAsync(store2, store1);
                WaitUntilHasConflict(store1, "foo/bar");

                last = DateTime.UtcNow;

                using (var session = store1.OpenAsyncSession())
                {
                    var company = new Company
                    {
                        Name = "Resolver"
                    };
                    await session.StoreAsync(company, "foo/bar");
                    await session.SaveChangesAsync();
                }

                var db = await Databases.GetDocumentDatabaseInstanceFor(store1);

                RevertResult result;
                using (var token = new OperationCancelToken(db.Configuration.Databases.OperationTimeout.AsTimeSpan, db.DatabaseShutdown, CancellationToken.None))
                {
                    result = (RevertResult)await db.DocumentsStorage.RevisionsStorage.RevertRevisions(last.Add(TimeSpan.FromMinutes(1)), TimeSpan.FromMinutes(60),
                        onProgress: null, token: token);
                }

                Assert.Equal(3, result.ScannedRevisions);
                Assert.Equal(1, result.ScannedDocuments);
                Assert.Equal(0, result.RevertedDocuments);

                using (var session = store1.OpenAsyncSession())
                {
                    var companiesRevisions = await session.Advanced.Revisions.GetForAsync<Company>("foo/bar");
                    Assert.Equal(3, companiesRevisions.Count);
                }
            }
        }

        [Fact]
        public async Task RevertResolvedConflictByRemoteToOriginal()
        {
            // put was at 8:50
            // conflict at 9:10
            // resolved at 9:15
            // will revert to 9:00

            using (var store1 = GetDocumentStore())
            using (var store2 = GetDocumentStore())
            {
                await RevisionsHelper.SetupRevisions(Server.ServerStore, store1.Database);
                DateTime last = default;

                using (var session = store1.OpenAsyncSession())
                {
                    var person = new Person
                    {
                        Name = "Name1"
                    };
                    await session.StoreAsync(person, "foo/bar");
                    await session.SaveChangesAsync();
                    last = session.Advanced.GetLastModifiedFor(person).Value;
                    var companiesRevisions = await session.Advanced.Revisions.GetForAsync<Person>("foo/bar");
                    Assert.Equal(1, companiesRevisions.Count);
                }

                using (var session = store2.OpenAsyncSession())
                {
                    var person = new Person
                    {
                        Name = "Name2"
                    };
                    await session.StoreAsync(person, "foo/bar");
                    await session.SaveChangesAsync();


                    await session.StoreAsync(new Person(), "marker");
                    await session.SaveChangesAsync();
                }

                await SetupReplicationAsync(store2, store1);
                WaitForDocument(store1, "marker");

                var db = await Databases.GetDocumentDatabaseInstanceFor(store1);

                RevertResult result;
                using (var token = new OperationCancelToken(db.Configuration.Databases.OperationTimeout.AsTimeSpan, db.DatabaseShutdown, CancellationToken.None))
                {
                    result = (RevertResult)await db.DocumentsStorage.RevisionsStorage.RevertRevisions(last, TimeSpan.FromMinutes(60), onProgress: null,
                        token: token);
                }

                Assert.Equal(3, result.ScannedRevisions);
                Assert.Equal(1, result.ScannedDocuments);
                Assert.Equal(1, result.RevertedDocuments);

                using (var session = store1.OpenAsyncSession())
                {
                    var persons = await session.Advanced.Revisions.GetForAsync<Person>("foo/bar");
                    Assert.Equal(4, persons.Count);

                    Assert.Equal("Name1", persons[0].Name);
                    var metadata = session.Advanced.GetMetadataFor(persons[0]);
                    Assert.Equal((DocumentFlags.HasRevisions | DocumentFlags.Revision | DocumentFlags.Reverted).ToString(), metadata.GetString(Constants.Documents.Metadata.Flags));

                    Assert.Equal("Name2", persons[1].Name);
                    metadata = session.Advanced.GetMetadataFor(persons[1]);
                    Assert.Equal((DocumentFlags.HasRevisions | DocumentFlags.Revision | DocumentFlags.Resolved).ToString(), metadata.GetString(Constants.Documents.Metadata.Flags));

                    Assert.Equal("Name1", persons[2].Name);
                    metadata = session.Advanced.GetMetadataFor(persons[2]);
                    Assert.Equal((DocumentFlags.HasRevisions | DocumentFlags.Revision | DocumentFlags.Conflicted).ToString(), metadata.GetString(Constants.Documents.Metadata.Flags));

                    Assert.Equal("Name2", persons[3].Name);
                    metadata = session.Advanced.GetMetadataFor(persons[3]);
                    Assert.Equal((DocumentFlags.HasRevisions | DocumentFlags.Revision | DocumentFlags.FromReplication | DocumentFlags.Conflicted).ToString(), metadata.GetString(Constants.Documents.Metadata.Flags));
                }
            }
        }

        [Fact]
        public async Task RevertResolvedConflictByLocalToOriginal()
        {
            // put was at 8:50
            // conflict at 9:10
            // resolved at 9:15
            // will revert to 9:00

            using (var store1 = GetDocumentStore())
            using (var store2 = GetDocumentStore())
            {
                await RevisionsHelper.SetupRevisions(Server.ServerStore, store1.Database);
                DateTime last = default;

                using (var session = store2.OpenAsyncSession())
                {
                    await session.StoreAsync(new Person(), "keep-conflicted-revision-insert-order");
                    await session.SaveChangesAsync();

                    var person = new Person
                    {
                        Name = "Name2"
                    };
                    await session.StoreAsync(person, "foo/bar");
                    await session.SaveChangesAsync();
                }

                using (var session = store1.OpenAsyncSession())
                {
                    var person = new Person
                    {
                        Name = "Name1"
                    };
                    await session.StoreAsync(person, "foo/bar");
                    await session.SaveChangesAsync();
                    last = session.Advanced.GetLastModifiedFor(person).Value;
                    var companiesRevisions = await session.Advanced.Revisions.GetForAsync<Person>("foo/bar");
                    Assert.Equal(1, companiesRevisions.Count);
                }

                using (var session = store2.OpenAsyncSession())
                {
                    await session.StoreAsync(new Person(), "marker");
                    await session.SaveChangesAsync();
                }

                await SetupReplicationAsync(store2, store1);
                WaitForDocument(store1, "marker");

                var db = await Databases.GetDocumentDatabaseInstanceFor(store1);

                RevertResult result;
                using (var token = new OperationCancelToken(db.Configuration.Databases.OperationTimeout.AsTimeSpan, db.DatabaseShutdown, CancellationToken.None))
                {
                    result = (RevertResult)await db.DocumentsStorage.RevisionsStorage.RevertRevisions(last, TimeSpan.FromMinutes(60), onProgress: null,
                        token: token);
                }

                Assert.Equal(3, result.ScannedRevisions);
                Assert.Equal(1, result.ScannedDocuments);
                Assert.Equal(1, result.RevertedDocuments);

                using (var session = store1.OpenAsyncSession())
                {
                    var persons = await session.Advanced.Revisions.GetForAsync<Person>("foo/bar");
                    Assert.Equal(4, persons.Count);

                    Assert.Equal("Name1", persons[0].Name);
                    var metadata = session.Advanced.GetMetadataFor(persons[0]);
                    Assert.Equal((DocumentFlags.HasRevisions | DocumentFlags.Revision | DocumentFlags.Reverted).ToString(), metadata.GetString(Constants.Documents.Metadata.Flags));

                    Assert.Equal("Name1", persons[1].Name);
                    metadata = session.Advanced.GetMetadataFor(persons[1]);
                    Assert.Equal((DocumentFlags.HasRevisions | DocumentFlags.Revision | DocumentFlags.Resolved).ToString(), metadata.GetString(Constants.Documents.Metadata.Flags));

                    Assert.Equal("Name1", persons[2].Name);
                    metadata = session.Advanced.GetMetadataFor(persons[2]);
                    Assert.Equal((DocumentFlags.HasRevisions | DocumentFlags.Revision | DocumentFlags.Conflicted).ToString(), metadata.GetString(Constants.Documents.Metadata.Flags));

                    Assert.Equal("Name2", persons[3].Name);
                    metadata = session.Advanced.GetMetadataFor(persons[3]);
                    Assert.Equal((DocumentFlags.HasRevisions | DocumentFlags.Revision | DocumentFlags.FromReplication | DocumentFlags.Conflicted).ToString(), metadata.GetString(Constants.Documents.Metadata.Flags));
                }
            }
        }

        [Fact]
        public async Task RevertResolvedConflictByRemoteToDeleted()
        {
            // deleted was at 8:50
            // conflict at 9:10
            // resolved at 9:15
            // will revert to 9:00

            using (var store1 = GetDocumentStore())
            using (var store2 = GetDocumentStore())
            {
                await RevisionsHelper.SetupRevisions(Server.ServerStore, store1.Database);
                DateTime last = default;

                using (var session = store1.OpenAsyncSession())
                {
                    var person = new Person
                    {
                        Name = "Name1"
                    };
                    await session.StoreAsync(person, "foo/bar");
                    await session.SaveChangesAsync();

                    session.Delete("foo/bar");
                    await session.SaveChangesAsync();
                    last = DateTime.UtcNow;
                }

                using (var session = store2.OpenAsyncSession())
                {
                    var person = new Person
                    {
                        Name = "Name2"
                    };
                    await session.StoreAsync(person, "foo/bar");
                    await session.SaveChangesAsync();


                    await session.StoreAsync(new Person(), "marker");
                    await session.SaveChangesAsync();
                }

                await SetupReplicationAsync(store2, store1);
                WaitForDocument(store1, "marker");

                var db = await Databases.GetDocumentDatabaseInstanceFor(store1);

                RevertResult result;
                using (var token = new OperationCancelToken(db.Configuration.Databases.OperationTimeout.AsTimeSpan, db.DatabaseShutdown, CancellationToken.None))
                {
                    result = (RevertResult)await db.DocumentsStorage.RevisionsStorage.RevertRevisions(last, TimeSpan.FromMinutes(60), onProgress: null,
                        token: token);
                }

                Assert.Equal(4, result.ScannedRevisions);
                Assert.Equal(1, result.ScannedDocuments);
                Assert.Equal(1, result.RevertedDocuments);

                using (var session = store1.OpenAsyncSession())
                {
                    var persons = await session.Advanced.Revisions.GetForAsync<Person>("foo/bar");
                    Assert.Equal(5, persons.Count);

                    Assert.Equal(null, persons[0].Name);
                    var metadata = session.Advanced.GetMetadataFor(persons[0]);
                    Assert.Equal((DocumentFlags.HasRevisions | DocumentFlags.DeleteRevision | DocumentFlags.Reverted).ToString(), metadata.GetString(Constants.Documents.Metadata.Flags));

                    Assert.Equal("Name2", persons[1].Name);
                    metadata = session.Advanced.GetMetadataFor(persons[1]);
                    Assert.Equal((DocumentFlags.HasRevisions | DocumentFlags.Revision | DocumentFlags.FromReplication | DocumentFlags.Resolved).ToString(), metadata.GetString(Constants.Documents.Metadata.Flags));

                    Assert.Equal(null, persons[2].Name);
                    metadata = session.Advanced.GetMetadataFor(persons[2]);
                    Assert.Equal((DocumentFlags.HasRevisions | DocumentFlags.DeleteRevision | DocumentFlags.Conflicted).ToString(), metadata.GetString(Constants.Documents.Metadata.Flags));

                    Assert.Equal("Name2", persons[3].Name);
                    metadata = session.Advanced.GetMetadataFor(persons[3]);
                    Assert.Equal((DocumentFlags.HasRevisions | DocumentFlags.Revision | DocumentFlags.FromReplication | DocumentFlags.Conflicted).ToString(), metadata.GetString(Constants.Documents.Metadata.Flags));

                    Assert.Equal("Name1", persons[4].Name);
                    metadata = session.Advanced.GetMetadataFor(persons[4]);
                    Assert.Equal((DocumentFlags.HasRevisions | DocumentFlags.Revision).ToString(), metadata.GetString(Constants.Documents.Metadata.Flags));
                }
            }
        }

        [Fact]
        public async Task RevertResolvedConflictByLocalToDeleted()
        {
            // deleted was at 8:50
            // conflict at 9:10
            // resolved at 9:15
            // will revert to 9:00

            using (var store1 = GetDocumentStore())
            using (var store2 = GetDocumentStore())
            {
                await RevisionsHelper.SetupRevisions(Server.ServerStore, store1.Database);
                DateTime last = default;

                using (var session = store2.OpenAsyncSession())
                {
                    var company = new Person
                    {
                        Name = "Name2"
                    };
                    await session.StoreAsync(company, "foo/bar");
                    await session.SaveChangesAsync();


                    await session.StoreAsync(new Company(), "marker");
                    await session.SaveChangesAsync();
                }

                using (var session = store1.OpenAsyncSession())
                {
                    var company = new Person
                    {
                        Name = "Name1"
                    };
                    await session.StoreAsync(company, "foo/bar");
                    await session.SaveChangesAsync();

                    session.Delete("foo/bar");
                    await session.SaveChangesAsync();

                    var companiesRevisions = await session.Advanced.Revisions.GetForAsync<Person>("foo/bar");
                    Assert.Equal(2, companiesRevisions.Count);

                    last = DateTime.UtcNow;
                }

                await SetupReplicationAsync(store2, store1);
                WaitForDocument(store1, "marker");

                using (var session = store1.OpenAsyncSession())
                {
                    var companiesRevisions = await session.Advanced.Revisions.GetForAsync<Person>("foo/bar");
                    Assert.Equal(4, companiesRevisions.Count);
                }

                var db = await Databases.GetDocumentDatabaseInstanceFor(store1);

                RevertResult result;
                using (var token = new OperationCancelToken(db.Configuration.Databases.OperationTimeout.AsTimeSpan, db.DatabaseShutdown, CancellationToken.None))
                {
                    result = (RevertResult)await db.DocumentsStorage.RevisionsStorage.RevertRevisions(last, TimeSpan.FromMinutes(60), onProgress: null,
                        token: token);
                }

                Assert.Equal(4, result.ScannedRevisions);
                Assert.Equal(1, result.ScannedDocuments);
                Assert.Equal(1, result.RevertedDocuments);

                using (var session = store1.OpenAsyncSession())
                {
                    var persons = await session.Advanced.Revisions.GetForAsync<Person>("foo/bar");
                    Assert.Equal(5, persons.Count);

                    Assert.Equal(null, persons[0].Name);
                    var metadata = session.Advanced.GetMetadataFor(persons[0]);
                    Assert.Equal((DocumentFlags.HasRevisions | DocumentFlags.DeleteRevision | DocumentFlags.Reverted).ToString(), metadata.GetString(Constants.Documents.Metadata.Flags));

                    Assert.Equal(null, persons[1].Name);
                    metadata = session.Advanced.GetMetadataFor(persons[1]);
                    Assert.Equal((DocumentFlags.HasRevisions | DocumentFlags.DeleteRevision | DocumentFlags.Resolved).ToString(), metadata.GetString(Constants.Documents.Metadata.Flags));

                    Assert.Equal(null, persons[2].Name);
                    metadata = session.Advanced.GetMetadataFor(persons[2]);
                    Assert.Equal((DocumentFlags.HasRevisions | DocumentFlags.DeleteRevision | DocumentFlags.Conflicted).ToString(), metadata.GetString(Constants.Documents.Metadata.Flags));

                    Assert.Equal("Name2", persons[3].Name);
                    metadata = session.Advanced.GetMetadataFor(persons[3]);
                    Assert.Equal((DocumentFlags.HasRevisions | DocumentFlags.Revision | DocumentFlags.FromReplication | DocumentFlags.Conflicted).ToString(), metadata.GetString(Constants.Documents.Metadata.Flags));

                    Assert.Equal("Name1", persons[4].Name);
                    metadata = session.Advanced.GetMetadataFor(persons[4]);
                    Assert.Equal((DocumentFlags.HasRevisions | DocumentFlags.Revision).ToString(), metadata.GetString(Constants.Documents.Metadata.Flags));
                }
            }
        }
    }
}
