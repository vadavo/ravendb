﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq; 
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastTests;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Tcp;
using Raven.Client.Util;
using Raven.Server;
using Raven.Server.Config;
using Raven.Server.Config.Settings;
using Raven.Server.Rachis;
using Raven.Server.Rachis.Remote;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Commands;
using Raven.Server.ServerWide.Context;
using Sparrow.Collections;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Sparrow.Logging;
using Sparrow.Server.Platform;
using Sparrow.Utils;
using Voron;
using Voron.Data;
using Voron.Data.BTrees;
using Voron.Exceptions;
using Xunit;
using Xunit.Abstractions;
using NativeMemory = Sparrow.Utils.NativeMemory;

namespace Tests.Infrastructure
{
    [Trait("Category", "Rachis")]
    public class RachisConsensusTestBase : XunitLoggingBase, IDisposable
    {
        static unsafe RachisConsensusTestBase()
        {
            XunitLogging.RedirectStreams = false;
            XunitLogging.Init();
            XunitLogging.EnableExceptionCapture();

            NativeMemory.GetCurrentUnmanagedThreadId = () => (ulong)Pal.rvn_get_current_thread_id();
            ZstdLib.CreateDictionaryException = message => new VoronErrorException(message);
            RachisStateMachine.EnableDebugLongCommit = true;

            Lucene.Net.Util.UnmanagedStringArray.Segment.AllocateMemory = NativeMemory.AllocateMemory;
            Lucene.Net.Util.UnmanagedStringArray.Segment.FreeMemory = NativeMemory.Free;
            JsonDeserializationCluster.Commands.Add(nameof(TestCommand), JsonDeserializationBase.GenerateJsonDeserializationRoutine<TestCommand>());
        }

        public RachisConsensusTestBase(ITestOutputHelper output, [CallerFilePath] string sourceFile = "") : base(output, sourceFile)
        {
        }

        protected bool PredictableSeeds;

        protected readonly Logger Log = LoggingSource.Instance.GetLogger<RachisConsensusTestBase>("RachisConsensusTest");

        protected int LongWaitTime = 15000; //under stress the thread pool may take time to schedule the task to complete the set of the TCS

        protected async Task<RachisConsensus<CountingStateMachine>> CreateNetworkAndGetLeader(int nodeCount, [CallerMemberName] string caller = null)
        {
            var initialCount = RachisConsensuses.Count;
            var leaderIndex = _random.Next(0, nodeCount);
            var timeout = TimeSpan.FromSeconds(10);
            var electionTimeout = Math.Max(300, nodeCount * 60); // We want to make it easier for the tests, since we are running multiple servers on the same machine. 
            for (var i = 0; i < nodeCount; i++)
            {
                // ReSharper disable once ExplicitCallerInfoArgument
                SetupServer(i == leaderIndex, electionTimeout: electionTimeout, caller: caller);
            }
            var leader = RachisConsensuses[leaderIndex + initialCount];
            for (var i = 0; i < nodeCount; i++)
            {
                if (i == leaderIndex)
                {
                    continue;
                }
                var follower = RachisConsensuses[i + initialCount];
                await leader.AddToClusterAsync(follower.Url);
                var done = await follower.WaitForTopology(Leader.TopologyModification.Voter).WaitWithoutExceptionAsync(timeout);
                Assert.True(done, "Waited for node to become a follower for too long");
            }
            var currentState = RachisConsensuses[leaderIndex + initialCount].CurrentState;
            Assert.True(currentState == RachisState.Leader ||
                        currentState == RachisState.LeaderElect,
                "The leader has changed while waiting for cluster to become stable, it is now " + currentState + " Beacuse: " + leader.LastStateChangeReason);
            return leader;
        }

        protected RachisConsensus<CountingStateMachine> GetRandomFollower()
        {
            var followers = GetFollowers();
            var indexOfFollower = _random.Next(followers.Count);
            return followers[indexOfFollower];
        }

        protected List<RachisConsensus<CountingStateMachine>> GetFollowers()
        {
            return RachisConsensuses.Where(
                     x => x.CurrentState != RachisState.Leader &&
                     x.CurrentState != RachisState.LeaderElect).ToList();
        }

        protected void DisconnectFromNode(RachisConsensus<CountingStateMachine> node)
        {
            foreach (var follower in RachisConsensuses.Where(x => x.Url != node.Url))
            {
                Disconnect(follower.Url, node.Url);
            }
        }

        protected void DisconnectBiDirectionalFromNode(RachisConsensus<CountingStateMachine> node)
        {
            foreach (var follower in RachisConsensuses.Where(x => x.Url != node.Url))
            {
                Disconnect(follower.Url, node.Url);
                Disconnect(node.Url, follower.Url);
            }
        }

        protected void ReconnectBiDirectionalFromNode(RachisConsensus<CountingStateMachine> node)
        {
            foreach (var follower in RachisConsensuses.Where(x => x.Url != node.Url))
            {
                Reconnect(follower.Url, node.Url);
                Reconnect(node.Url, follower.Url);
            }
        }

        protected void ReconnectToNode(RachisConsensus<CountingStateMachine> node)
        {
            foreach (var follower in RachisConsensuses.Where(x => x.Url != node.Url))
            {
                Reconnect(follower.Url, node.Url);
            }
        }

        protected RachisConsensus<CountingStateMachine> WaitForAnyToBecomeLeader(IEnumerable<RachisConsensus<CountingStateMachine>> nodes)
        {
            var waitingTasks = new List<Task>();

            foreach (var node in nodes)
            {
                waitingTasks.Add(node.WaitForState(RachisState.Leader, CancellationToken.None));
            }

            RavenTestHelper.AssertTrue(Task.WhenAny(waitingTasks).Wait(3000 * nodes.Count()), () => GetCandidateStatus(nodes));
            return nodes.FirstOrDefault(x => x.CurrentState == RachisState.Leader);
        }

        public static string GetCandidateStatus(IEnumerable<RachisConsensus<CountingStateMachine>> nodes)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Waited too long for a node to become a leader but no leader was elected.");
            foreach (var node in nodes)
            {
                var candidate = node.Candidate;
                if (candidate == null)
                {
                    sb.AppendLine($"'{node.Tag}' is {node.CurrentState} at term {node.CurrentTerm}, current candidate is null {node.LastStateChangeReason}");
                    continue;
                }

                sb.AppendLine($"'{node.Tag}' is {node.CurrentState} at term {node.CurrentTerm} (running: {candidate.Running})");
                sb.AppendJoin(Environment.NewLine, candidate.GetStatus().Values);
                sb.AppendLine();
            }

            return sb.ToString();
        }

        protected RachisConsensus<CountingStateMachine> SetupServer(bool bootstrap = false, int port = 0, int electionTimeout = 300, [CallerMemberName] string caller = null)
        {
            var tcpListener = new TcpListener(IPAddress.Loopback, port);
            tcpListener.Start();
            char ch;
            if (bootstrap)
            {
                ch = (char)65;
            }
            else
            {
                ch = (char)(65 + Interlocked.Increment(ref _count));
            }

            var url = $"tcp://localhost:{((IPEndPoint)tcpListener.LocalEndpoint).Port}/?{caller}#{ch}";

            var server = StorageEnvironmentOptions.CreateMemoryOnly();

            int seed = PredictableSeeds ? _random.Next(int.MaxValue) : (int)Interlocked.Read(ref _count);
            var configuration = RavenConfiguration.CreateForServer(caller);
            configuration.Initialize();
            configuration.Core.RunInMemory = true;
            configuration.Core.PublicServerUrl = new UriSetting($"http://localhost:{((IPEndPoint)tcpListener.LocalEndpoint).Port}");
            configuration.Cluster.ElectionTimeout = new TimeSetting(electionTimeout, TimeUnit.Milliseconds);
            var serverStore = new RavenServer(configuration) { ThrowOnLicenseActivationFailure = true }.ServerStore;
            serverStore.Initialize();
            var rachis = new RachisConsensus<CountingStateMachine>(serverStore, seed);
            var storageEnvironment = new StorageEnvironment(server);
            rachis.Initialize(storageEnvironment, configuration, new ClusterChanges(), configuration.Core.ServerUrls[0], out _);
            rachis.OnDispose += (sender, args) =>
            {
                serverStore.Dispose();
                storageEnvironment.Dispose();
            };
            if (bootstrap)
            {
                rachis.Bootstrap(url, "A");
            }

            rachis.Url = url;
            _listeners.Add(tcpListener);
            RachisConsensuses.Add(rachis);
            rachis.OnDispose += (sender, args) => tcpListener.Stop();

            for (int i = 0; i < 4; i++)
            {
                AcceptConnection(tcpListener, rachis);
            }

            return rachis;
        }

        private void AcceptConnection(TcpListener tcpListener, RachisConsensus rachis)
        {
            Task.Factory.StartNew(async () =>
            {
                TcpClient tcpClient;
                try
                {
                    tcpClient = await tcpListener.AcceptTcpClientAsync();
                    AcceptConnection(tcpListener, rachis);
                }
                catch (Exception e)
                {
                    if (rachis.IsDisposed)
                        return;

                    Assert.True(false, $"Unexpected TCP listener exception{Environment.NewLine}{e}");
                    throw;
                }

                try
                {
                    var stream = tcpClient.GetStream();
                    var remoteConnection = new RemoteConnection(rachis.Tag, rachis.CurrentTerm, stream,
                        features: new TcpConnectionHeaderMessage.SupportedFeatures.ClusterFeatures
                        {
                            MultiTree = true
                        }, () => tcpClient.Client?.Disconnect(false));

                    rachis.AcceptNewConnection(remoteConnection, tcpClient.Client.RemoteEndPoint, hello =>
                    {
                        if (rachis.Url == null)
                            return;

                        lock (this)
                        {
                            if (_rejectionList.TryGetValue(rachis.Url, out var set))
                            {
                                if (set.Contains(hello.DebugSourceIdentifier))
                                {
                                    throw new InvalidComObjectException("Simulated failure");
                                }
                            }

                            var connections = _connections.GetOrAdd(rachis.Url, _ => new ConcurrentSet<Tuple<string, TcpClient>>());
                            connections.Add(Tuple.Create(hello.DebugSourceIdentifier, tcpClient));
                        }
                    });
                }
                catch
                {
                    // expected
                }
            });
        }

        protected void Disconnect(string to, string from)
        {
            lock (this)
            {
                var rejections = _rejectionList.GetOrAdd(to, _ => new ConcurrentSet<string>());
                var fromTag = from.Substring(from.IndexOf('#') + 1);
                rejections.Add(fromTag);
                rejections.Add(from);
                if (_connections.TryGetValue(to, out var set))
                {
                    foreach (var tuple in set)
                    {
                        if (tuple.Item1 == from || tuple.Item1 == fromTag)
                        {
                            set.TryRemove(tuple);
                            tuple.Item2.Dispose();
                        }
                    }
                }
            }
        }

        protected void Reconnect(string to, string from)
        {
            lock (this)
            {
                if (_rejectionList.TryGetValue(to, out var rejectionList) == false)
                    return;
                var fromTag = from.Substring(from.IndexOf('#') + 1);
                rejectionList.TryRemove(from);
                rejectionList.TryRemove(fromTag);
            }
        }

        protected async Task<long> IssueCommandsAndWaitForCommit(int numberOfCommands, string name, int value)
        {
            for (var i = 0; i < numberOfCommands; i++)
            {
                await ActionWithLeader(l => l.PutAsync(new TestCommand
                {
                    Name = name,
                    Value = value
                }));
            }

            long index = -1;

            await ActionWithLeader(l =>
            {
                using (l.ContextPool.AllocateOperationContext(out ClusterOperationContext context))
                using (context.OpenReadTransaction())
                    index = l.GetLastEntryIndex(context);
                return Task.CompletedTask;
            });

            return index;
        }

        protected List<Task> IssueCommandsWithoutWaitingForCommits(RachisConsensus<CountingStateMachine> leader, int numberOfCommands, string name, int? value = null)
        {
            List<Task> waitingList = new List<Task>();
            for (var i = 1; i <= numberOfCommands; i++)
            {
                var task = leader.PutAsync(new TestCommand
                {
                    Name = name,
                    Value = value ?? i
                });

                waitingList.Add(task);
            }
            return waitingList;
        }

        protected async Task ActionWithLeader(Func<RachisConsensus<CountingStateMachine>, Task> action)
        {
            var retires = 5;
            Exception lastException;

            do
            {
                try
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    {
                        var tasks = RachisConsensuses.Select(x => x.WaitForState(RachisState.Leader, cts.Token));
                        await Task.WhenAny(tasks);
                        var leader = RachisConsensuses.Single(x => x.CurrentState == RachisState.Leader);
                        await action(leader);
                        return;
                    }
                }
                catch (Exception e)
                {
                    lastException = e;
                    await Task.Delay(50);
                }
            } while (retires-- > 0);

            if (lastException != null)
                throw new InvalidOperationException("Gave up after 5 retires", lastException);

            throw new InvalidOperationException("Should never happened!");
        }

        private readonly ConcurrentDictionary<string, ConcurrentSet<string>> _rejectionList = new ConcurrentDictionary<string, ConcurrentSet<string>>();
        private readonly ConcurrentDictionary<string, ConcurrentSet<Tuple<string, TcpClient>>> _connections = new ConcurrentDictionary<string, ConcurrentSet<Tuple<string, TcpClient>>>();
        private readonly List<TcpListener> _listeners = new List<TcpListener>();
        protected readonly List<RachisConsensus<CountingStateMachine>> RachisConsensuses = new List<RachisConsensus<CountingStateMachine>>();
        private readonly List<Task> _mustBeSuccessfulTasks = new List<Task>();
        private readonly Random _random = new Random();
        private long _count;

        public override void Dispose()
        {
            base.Dispose();

            foreach (var rc in RachisConsensuses)
            {
                rc.Dispose();
            }

            foreach (var listener in _listeners)
            {
                listener.Stop();
            }

            foreach (var mustBeSuccessfulTask in _mustBeSuccessfulTasks)
            {
                Assert.True(mustBeSuccessfulTask.Wait(250));
            }
        }

        public class CountingValidator : RachisVersionValidation
        {
            public override void AssertPutCommandToLeader(CommandBase cmd)
            {
            }

            public override void AssertEntryBeforeSendToFollower(BlittableJsonReaderObject entry, int version, string follower)
            {
            }
        }

        public class CountingStateMachine : RachisStateMachine
        {
            public string Read(ClusterOperationContext context, string name)
            {
                var tree = context.Transaction.InnerTransaction.ReadTree("values");
                var read = tree.Read(name);
                return read?.Reader.ToStringValue();
            }

            protected override void Apply(ClusterOperationContext context, BlittableJsonReaderObject cmd, long index, Leader leader, ServerStore serverStore)
            {
                Assert.True(cmd.TryGet(nameof(TestCommand.Name), out string name));
                Assert.True(cmd.TryGet(nameof(TestCommand.Value), out int val));

                var tree = context.Transaction.InnerTransaction.CreateTree("values");
                var current = tree.Read(name)?.Reader.ToStringValue();
                tree.Add(name, current + val);
            }

            protected override RachisVersionValidation InitializeValidator()
            {
                return new CountingValidator();
            }

            public override bool ShouldSnapshot(Slice slice, RootObjectType type)
            {
                return slice.ToString() == "values";
            }

            public override async Task<RachisConnection> ConnectToPeer(string url, string tag, X509Certificate2 certificate, CancellationToken token)
            {
                TimeSpan time;
                using (ContextPoolForReadOnlyOperations.AllocateOperationContext(out ClusterOperationContext ctx))
                using (ctx.OpenReadTransaction())
                {
                    time = _parent.ElectionTimeout * Math.Max(_parent.GetTopology(ctx).AllNodes.Count - 2, 1);
                }

                var tcpClient = await TcpUtils.ConnectAsync(url, time, token: token);
                try
                {
                    var stream = tcpClient.GetStream();
                    var conn = new RachisConnection
                    {
                        Stream = stream,
                        SupportedFeatures = TcpConnectionHeaderMessage.GetSupportedFeaturesFor(
                            TcpConnectionHeaderMessage.OperationTypes.Cluster, TcpConnectionHeaderMessage.ClusterWithMultiTree),
                        Disconnect = () =>
                        {
                            using (tcpClient)
                            {
                                tcpClient.Client.Disconnect(false);
                            }
                        }
                    };
                    return conn;
                }
                catch
                {
                    using (tcpClient)
                    {
                        tcpClient.Client.Disconnect(false);
                    }
                    throw;
                }
            }
        }

        public class TestCommand : CommandBase
        {
            public string Name;

            public object Value;

            public override DynamicJsonValue ToJson(JsonOperationContext context)
            {
                var djv = base.ToJson(context);
                UniqueRequestId ??= Guid.NewGuid().ToString();
                
                djv[nameof(UniqueRequestId)] = UniqueRequestId;
                djv[nameof(Name)] = Name;
                djv[nameof(Value)] = Value;

                return djv;
            }
        }

        internal class TestCommandWithRaftId : CommandBase
        {
            private string Name;

#pragma warning disable 649
            private object Value;
#pragma warning restore 649

            public TestCommandWithRaftId(string name, string uniqueRequestId) : base(uniqueRequestId)
            {
                Name = name;
            }

            public override DynamicJsonValue ToJson(JsonOperationContext context)
            {
                var djv = base.ToJson(context);
                djv[nameof(Name)] = Name;
                djv[nameof(Value)] = Value;

                return djv;
            }
        }
    }
}
