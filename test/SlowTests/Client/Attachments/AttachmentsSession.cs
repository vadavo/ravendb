﻿using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FastTests;
using Raven.Client;
using Raven.Client.Documents.Commands.Batches;
using Raven.Client.Documents.Operations.Attachments;
using Raven.Server.Config;
using Raven.Server.Documents;
using Raven.Server.ServerWide.Context;
using Raven.Tests.Core.Utils.Entities;
using Sparrow.Json;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Client.Attachments
{
    public class AttachmentsSession : RavenTestBase
    {
        public AttachmentsSession(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public async Task PutAttachments()
        {
            using (var store = GetDocumentStore())
            {
                var names = new[]
                {
                    "profile.png",
                    "background-photo.jpg",
                    "fileNAME_#$1^%_בעברית.txt"
                };

                using (var session = store.OpenSession())
                using (var profileStream = new MemoryStream(new byte[] { 1, 2, 3 }))
                using (var backgroundStream = new MemoryStream(new byte[] { 10, 20, 30, 40, 50 }))
                using (var fileStream = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 }))
                {
                    var user = new User { Name = "Fitzchak" };
                    session.Store(user, "users/1");

                    session.Advanced.Attachments.Store("users/1", names[0], profileStream, "image/png");
                    session.Advanced.Attachments.Store(user, names[1], backgroundStream, "ImGgE/jPeG");
                    session.Advanced.Attachments.Store(user, names[2], fileStream);

                    session.SaveChanges();
                }

                using (var session = store.OpenSession())
                {
                    var user = session.Load<User>("users/1");
                    var metadata = session.Advanced.GetMetadataFor(user);
                    Assert.Equal(DocumentFlags.HasAttachments.ToString(), metadata[Constants.Documents.Metadata.Flags]);
                    var attachments = metadata.GetObjects(Constants.Documents.Metadata.Attachments);
                    Assert.Equal(3, attachments.Length);
                    var orderedNames = names.OrderBy(x => x).ToArray();
                    for (var i = 0; i < names.Length; i++)
                    {
                        var name = orderedNames[i];
                        var attachment = attachments[i];
                        Assert.Equal(name, attachment.GetString(nameof(AttachmentName.Name)));
                        var hash = attachment.GetString(nameof(AttachmentName.Hash));
                        if (i == 0)
                        {
                            Assert.Equal("igkD5aEdkdAsAB/VpYm1uFlfZIP9M2LSUsD6f6RVW9U=", hash);
                            Assert.Equal(5, attachment.GetLong(nameof(AttachmentName.Size)));
                        }
                        else if (i == 1)
                        {
                            Assert.Equal("Arg5SgIJzdjSTeY6LYtQHlyNiTPmvBLHbr/Cypggeco=", hash);
                            Assert.Equal(5, attachment.GetLong(nameof(AttachmentName.Size)));
                        }
                        else if (i == 2)
                        {
                            Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", hash);
                            Assert.Equal(3, attachment.GetLong(nameof(AttachmentName.Size)));
                        }
                    }

                    AttachmentsCrud.AssertAttachmentCount(store, 3, 3);

                    var dbId1 = new Guid("00000000-48c4-421e-9466-000000000000");
                    await Databases.SetDatabaseId(store, dbId1);

                    var readBuffer = new byte[8];
                    for (var i = 0; i < names.Length; i++)
                    {
                        var name = names[i];
                        using (var attachmentStream = new MemoryStream(readBuffer))
                        using (var attachment = session.Advanced.Attachments.Get(user, name))
                        {
                            attachment.Stream.CopyTo(attachmentStream);
                            var expected = "A:" + (2 + i);
                            Assert.Equal(expected, attachment.Details.ChangeVector.Substring(0, expected.Length));
                            Assert.Equal(name, attachment.Details.Name);
                            Assert.Equal(i == 0 ? 3 : 5, attachmentStream.Position);
                            if (i == 0)
                            {
                                Assert.Equal(new byte[] { 1, 2, 3 }, readBuffer.Take(3));
                                Assert.Equal("image/png", attachment.Details.ContentType);
                                Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", attachment.Details.Hash);
                                Assert.Equal(3, attachment.Details.Size);
                            }
                            else if (i == 1)
                            {
                                Assert.Equal(new byte[] { 10, 20, 30, 40, 50 }, readBuffer.Take(5));
                                Assert.Equal("ImGgE/jPeG", attachment.Details.ContentType);
                                Assert.Equal("igkD5aEdkdAsAB/VpYm1uFlfZIP9M2LSUsD6f6RVW9U=", attachment.Details.Hash);
                                Assert.Equal(5, attachment.Details.Size);
                            }
                            else if (i == 2)
                            {
                                Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, readBuffer.Take(5));
                                Assert.Equal("", attachment.Details.ContentType);
                                Assert.Equal("Arg5SgIJzdjSTeY6LYtQHlyNiTPmvBLHbr/Cypggeco=", attachment.Details.Hash);
                                Assert.Equal(5, attachment.Details.Size);
                            }
                        }
                    }

                    using (var notExistsAttachment = session.Advanced.Attachments.Get("users/1", "not-there"))
                    {
                        Assert.Null(notExistsAttachment);
                    }
                }
            }
        }

        [Fact]
        public void ThrowIfStreamIsDisposed()
        {
            using (var store = GetDocumentStore())
            {
                var names = new[]
                {
                    "profile.png",
                    "background-photo.jpg",
                    "fileNAME_#$1^%_בעברית.txt"
                };

                using (var session = store.OpenSession())
                {
                    var user = new User { Name = "Fitzchak" };
                    session.Store(user, "users/1");

                    using (var profileStream = new MemoryStream(new byte[] { 1, 2, 3 }))
                        session.Advanced.Attachments.Store(user, names[0], profileStream, "image/png");
                    using (var backgroundStream = new MemoryStream(new byte[] { 10, 20, 30, 40, 50 }))
                        session.Advanced.Attachments.Store(user, names[1], backgroundStream, "ImGgE/jPeG");
                    using (var fileStream = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 }))
                        session.Advanced.Attachments.Store(user, names[2], fileStream);

                    var exception = Assert.Throws<InvalidOperationException>(() => session.SaveChanges());
                    Assert.Equal("Cannot put an attachment with a not readable stream. Make sure that the specified stream is readable and was not disposed.", exception.Message);
                }
            }
        }

        [Fact]
        public void ThrowIfStreamIsUseTwice()
        {
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenSession())
                using (var stream = new MemoryStream(new byte[] { 1, 2, 3 }))
                {
                    var user = new User { Name = "Fitzchak" };
                    session.Store(user, "users/1");

                    session.Advanced.Attachments.Store(user, "profile", stream, "image/png");
                    session.Advanced.Attachments.Store(user, "other", stream);

                    var exception = Assert.Throws<InvalidOperationException>(() => session.SaveChanges());
                    Assert.Equal("It is forbidden to re-use the same stream for more than one attachment. Use a unique stream per put attachment command.", exception.Message);
                }
            }
        }

        [Fact]
        public void ThrowWhenTwoAttachmentsWithTheSameNameInSession()
        {
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenSession())
                using (var stream = new MemoryStream(new byte[] { 1, 2, 3 }))
                using (var stream2 = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 }))
                {
                    var user = new User { Name = "Fitzchak" };
                    session.Store(user, "users/1");

                    session.Advanced.Attachments.Store(user, "profile", stream, "image/png");

                    var exception = Assert.Throws<InvalidOperationException>(() => session.Advanced.Attachments.Store(user, "profile", stream2));
                    Assert.Equal("Can't store attachment 'profile' of document 'users/1', there is a deferred command registered to create an attachment with 'profile' name.", exception.Message);
                }
            }
        }

        [Fact]
        public void PutDocumentAndAttachmentAndDeleteShouldThrow()
        {
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenSession())
                using (var profileStream = new MemoryStream(new byte[] { 1, 2, 3 }))
                {
                    var user = new User { Name = "Fitzchak" };
                    session.Store(user, "users/1");

                    session.Advanced.Attachments.Store(user, "profile.png", profileStream, "image/png");

                    session.Delete(user);

                    var exception = Assert.Throws<InvalidOperationException>(() => session.SaveChanges());
                    Assert.Equal("Cannot perform save because document users/1 has been deleted by the session and is also taking part in deferred AttachmentPUT command", exception.Message);
                }
            }
        }

        [Fact]
        public void PutAttachmentAndDeleteShouldThrow()
        {
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenSession())
                {
                    var user = new User { Name = "Fitzchak" };
                    session.Store(user, "users/1");
                    session.SaveChanges();
                }

                using (var session = store.OpenSession())
                using (var profileStream = new MemoryStream(new byte[] { 1, 2, 3 }))
                {
                    var user = session.Load<User>("users/1");
                    session.Advanced.Attachments.Store(user, "profile.png", profileStream, "image/png");
                    session.Delete(user);

                    var exception = Assert.Throws<InvalidOperationException>(() => session.SaveChanges());
                    Assert.Equal("Cannot perform save because document users/1 has been deleted by the session and is also taking part in deferred AttachmentPUT command", exception.Message);
                }
            }
        }

        [Fact]
        public async Task DeleteAttachments()
        {
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenSession())
                {
                    var user = new User { Name = "Fitzchak" };
                    session.Store(user, "users/1");

                    using (var stream1 = new MemoryStream(Enumerable.Range(1, 3).Select(x => (byte)x).ToArray()))
                    using (var stream2 = new MemoryStream(Enumerable.Range(1, 6).Select(x => (byte)x).ToArray()))
                    using (var stream3 = new MemoryStream(Enumerable.Range(1, 9).Select(x => (byte)x).ToArray()))
                    using (var stream4 = new MemoryStream(Enumerable.Range(1, 12).Select(x => (byte)x).ToArray()))
                    {
                        session.Advanced.Attachments.Store(user, "file1", stream1, "image/png");
                        session.Advanced.Attachments.Store(user, "file2", stream2, "image/png");
                        session.Advanced.Attachments.Store(user, "file3", stream3, "image/png");
                        session.Advanced.Attachments.Store(user, "file4", stream4, "image/png");

                        session.SaveChanges();
                    }
                }

                AttachmentsCrud.AssertAttachmentCount(store, 4, documentsCount: 1);

                using (var session = store.OpenSession())
                {
                    var user = session.Load<User>("users/1");

                    session.Advanced.Attachments.Delete("users/1", "file2");
                    session.Advanced.Attachments.Delete(user, "file4");

                    session.SaveChanges();
                }
                AttachmentsCrud.AssertAttachmentCount(store, 2, documentsCount: 1);

                using (var session = store.OpenSession())
                {
                    var user = session.Load<User>("users/1");
                    var metadata = session.Advanced.GetMetadataFor(user);
                    Assert.Equal(DocumentFlags.HasAttachments.ToString(), metadata[Constants.Documents.Metadata.Flags]);
                    var attachments = metadata.GetObjects(Constants.Documents.Metadata.Attachments);
                    Assert.Equal(2, attachments.Length);
                    Assert.Equal("file1", attachments[0].GetString(nameof(AttachmentName.Name)));
                    Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", attachments[0].GetString(nameof(AttachmentName.Hash)));
                    Assert.Equal("file3", attachments[1].GetString(nameof(AttachmentName.Name)));
                    Assert.Equal("NRQuixiqj+xvEokF6MdQq1u+uH1dk/gk2PLChJQ58Vo=", attachments[1].GetString(nameof(AttachmentName.Hash)));
                }

                var dbId1 = new Guid("00000000-48c4-421e-9466-000000000000");
                await Databases.SetDatabaseId(store, dbId1);

                using (var session = store.OpenSession())
                {
                    var user = session.Load<User>("users/1");


                    var readBuffer = new byte[16];
                    using (var attachmentStream = new MemoryStream(readBuffer))
                    using (var attachment = session.Advanced.Attachments.Get("users/1", "file1"))
                    {
                        attachment.Stream.CopyTo(attachmentStream);
                        Assert.Equal("A:2", attachment.Details.ChangeVector.Substring(0, 3));
                        Assert.Equal("file1", attachment.Details.Name);
                        Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", attachment.Details.Hash);
                        Assert.Equal(3, attachmentStream.Position);
                        Assert.Equal(new byte[] { 1, 2, 3 }, readBuffer.Take(3));
                    }
                    using (var attachment = session.Advanced.Attachments.Get(user, "file2"))
                    {
                        Assert.Null(attachment);
                    }
                    using (var attachmentStream = new MemoryStream(readBuffer))
                    using (var attachment = session.Advanced.Attachments.Get(user, "file3"))
                    {
                        attachment.Stream.CopyTo(attachmentStream);
                        Assert.Equal("A:4", attachment.Details.ChangeVector.Substring(0, 3));
                        Assert.Equal("file3", attachment.Details.Name);
                        Assert.Equal("NRQuixiqj+xvEokF6MdQq1u+uH1dk/gk2PLChJQ58Vo=", attachment.Details.Hash);
                        Assert.Equal(9, attachmentStream.Position);
                        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, readBuffer.Take(9));
                    }
                    using (var attachment = session.Advanced.Attachments.Get(user, "file4"))
                    {
                        Assert.Null(attachment);
                    }

                    // Delete document should delete all the attachments
                    session.Delete(user);
                    session.SaveChanges();
                }
                AttachmentsCrud.AssertAttachmentCount(store, 0, documentsCount: 0);
            }
        }

        [Fact]
        public void DeleteDocumentAndThanItsAttachments_ThisIsNoOpButShouldBeSupported()
        {
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenSession())
                {
                    var user = new User { Name = "Fitzchak" };
                    session.Store(user, "users/1");

                    using (var stream = new MemoryStream(Enumerable.Range(1, 3).Select(x => (byte)x).ToArray()))
                    {
                        session.Advanced.Attachments.Store(user, "file", stream, "image/png");
                        session.SaveChanges();
                    }
                }

                AttachmentsCrud.AssertAttachmentCount(store, 1, documentsCount: 1);

                using (var session = store.OpenSession())
                {
                    var user = session.Load<User>("users/1");

                    session.Delete(user);
                    session.Advanced.Attachments.Delete(user, "file");
                    session.Advanced.Attachments.Delete(user, "file"); // this should be no-op

                    session.SaveChanges();
                }
                AttachmentsCrud.AssertAttachmentCount(store, 0, documentsCount: 0);
            }
        }

        [Fact]
        public void DeleteDocumentByCommandAndThanItsAttachments_ThisIsNoOpButShouldBeSupported()
        {
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenSession())
                {
                    session.Store(new User { Name = "Fitzchak" }, "users/1");

                    using (var stream = new MemoryStream(Enumerable.Range(1, 3).Select(x => (byte)x).ToArray()))
                    {
                        session.Advanced.Attachments.Store("users/1", "file", stream, "image/png");
                        session.SaveChanges();
                    }
                }

                AttachmentsCrud.AssertAttachmentCount(store, 1, documentsCount: 1);

                using (var session = store.OpenSession())
                {
                    session.Advanced.Defer(new DeleteCommandData("users/1", null));
                    session.Advanced.Attachments.Delete("users/1", "file");
                    session.Advanced.Attachments.Delete("users/1", "file"); // this should be no-op

                    session.SaveChanges();
                }
                AttachmentsCrud.AssertAttachmentCount(store, 0, documentsCount: 0);
            }
        }

        [Fact]
        public void GetAttachmentNames()
        {
            using (var store = GetDocumentStore())
            {
                var names = new[]
                {
                    "profile.png",
                    "background-photo.jpg",
                    "fileNAME_#$1^%_בעברית.txt"
                };

                using (var session = store.OpenSession())
                using (var profileStream = new MemoryStream(new byte[] { 1, 2, 3 }))
                using (var backgroundStream = new MemoryStream(new byte[] { 10, 20, 30, 40, 50 }))
                using (var fileStream = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 }))
                {
                    var user = new User { Name = "Fitzchak" };
                    session.Store(user, "users/1");

                    session.Advanced.Attachments.Store("users/1", names[0], profileStream, "image/png");
                    session.Advanced.Attachments.Store(user, names[1], backgroundStream, "ImGgE/jPeG");
                    session.Advanced.Attachments.Store(user, names[2], fileStream);

                    session.SaveChanges();
                }

                using (var session = store.OpenSession())
                {
                    var user = session.Load<User>("users/1");
                    var attachments = session.Advanced.Attachments.GetNames(user);
                    Assert.Equal(3, attachments.Length);
                    var orderedNames = names.OrderBy(x => x).ToArray();
                    for (var i = 0; i < names.Length; i++)
                    {
                        var name = orderedNames[i];
                        var attachment = attachments[i];
                        Assert.Equal(name, attachment.Name);
                        if (i == 0)
                        {
                            Assert.Equal("igkD5aEdkdAsAB/VpYm1uFlfZIP9M2LSUsD6f6RVW9U=", attachment.Hash);
                            Assert.Equal(5, attachment.Size);
                            Assert.Equal("ImGgE/jPeG", attachment.ContentType);
                        }
                        else if (i == 1)
                        {
                            Assert.Equal("Arg5SgIJzdjSTeY6LYtQHlyNiTPmvBLHbr/Cypggeco=", attachment.Hash);
                            Assert.Equal(5, attachment.Size);
                            Assert.Equal("", attachment.ContentType);
                        }
                        else if (i == 2)
                        {
                            Assert.Equal("EcDnm3HDl2zNDALRMQ4lFsCO3J2Lb1fM1oDWOk2Octo=", attachment.Hash);
                            Assert.Equal(3, attachment.Size);
                            Assert.Equal("image/png", attachment.ContentType);
                        }
                    }

                    var user2 = session.Load<User>("users/2");
                    Assert.Null(user2);
                    Assert.Throws<ArgumentNullException>(() => session.Advanced.Attachments.GetNames(user2));
                }
            }
        }

        [Theory]
        [InlineData(100)]
        public void PutLotOfAttachments(int count)
        {
            var streams = new MemoryStream[count];

            using (var store = GetDocumentStore())
            using (var session = store.OpenSession())
            {
                var user = new User { Name = "Fitzchak" };
                session.Store(user, "users/1");

                for (var i = 0; i < count; i++)
                {
                    var stream = new MemoryStream(new byte[] { 1, 2, 3 });
                    session.Advanced.Attachments.Store("users/1", "Big And Very Long File Name " + i, stream, "image/png");
                    streams[i] = stream;
                }

                session.SaveChanges();
            }

            foreach (var stream in streams)
                stream.Dispose();
        }

        [Fact]
        public void AttachmentExists()
        {
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenSession())
                using (var stream = new MemoryStream(new byte[] { 1, 2, 3 }))
                {
                    var user = new User { Name = "Fitzchak" };
                    session.Store(user, "users/1");

                    session.Advanced.Attachments.Store("users/1", "profile", stream, "image/png");
                    session.SaveChanges();
                }

                using (var session = store.OpenSession())
                {
                    Assert.True(session.Advanced.Attachments.Exists("users/1", "profile"));
                    Assert.False(session.Advanced.Attachments.Exists("users/1", "background-photo"));
                    Assert.False(session.Advanced.Attachments.Exists("users/2", "profile"));
                }
            }
        }

        [Fact]
        public async Task AttachmentsStore_TransactionMergerRerunBecauseFailureOfAnotherCommand_ShouldStoreTheAttahments()
        {
            const int attachmentsCount = 10;

            var store = GetDocumentStore(new Options
            {
                ModifyDatabaseRecord = doc =>
                {
                    doc.Settings[RavenConfiguration.GetKey(x => x.TransactionMergerConfiguration.MaxTimeToWaitForPreviousTx)] = int.MaxValue.ToString();
                }
            });
            const string attachmentName = "someName";

            const string documentId = "users/1";
            using (var session = store.OpenAsyncSession())
            {
                await session.StoreAsync(new User {Name = "Fitzchak"}, documentId);
                await session.SaveChangesAsync();
            }


            var tasks = Enumerable.Range(0, attachmentsCount).Select(i => Task.Run(async () =>
                {
                    using (var session = store.OpenAsyncSession())
                    await using (var stream0 = new MemoryStream(new byte[1_000_000]))
                    await using (var stream1 = new MemoryStream(new byte[1_000_000]))
                    await using (var stream2 = new MemoryStream(new byte[1_000_000]))
                    {
                        session.Advanced.Attachments.Store(documentId, attachmentName + i + '-' + 0, stream0, "image/png");
                        session.Advanced.Attachments.Store(documentId, attachmentName + i + '-' + 1, stream1, "image/png");
                        session.Advanced.Attachments.Store(documentId, attachmentName + i + '-' + 2, stream2, "image/png");

                        await session.SaveChangesAsync();
                    }
                })
            );

            var all = Task.WhenAll(tasks);

            var database = await Databases.GetDocumentDatabaseInstanceFor(store);

            while (true)
            {
                try
                {
                    await database.TxMerger.Enqueue(new ThrowCommand());
                }
                catch (Exception)
                {
                    if (all.IsCompleted)
                        break;
                    await Task.Delay(attachmentsCount);
                }
            }

            
            await all;
            using (var session = store.OpenAsyncSession())
            {
                var doc = await session.LoadAsync<User>(documentId);
                var attachmentNames = session.Advanced.Attachments.GetNames(doc).Select(n => n.Name).ToArray();
                for (int i = 0; i < attachmentsCount; i++)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        Assert.Contains(attachmentNames, s1 => s1.Equals(attachmentName + i + '-' + j));
                    }
                }
            }
        }

        private class ThrowCommand : TransactionOperationsMerger.MergedTransactionCommand
        {
            protected override long ExecuteCmd(DocumentsOperationContext context) => throw new Exception();
            public override TransactionOperationsMerger.IReplayableCommandDto<TransactionOperationsMerger.MergedTransactionCommand> ToDto<TTransaction>(TransactionOperationContext<TTransaction> context) 
                => throw new NotImplementedException();
            
        }
    }
}
