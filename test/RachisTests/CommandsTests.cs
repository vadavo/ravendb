﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Raven.Client.Util;
using Raven.Server.Rachis;
using Raven.Server.ServerWide.Context;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace RachisTests
{
    public class CommandsTests : RachisConsensusTestBase
    {
        public CommandsTests(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public async Task Command_sent_twice_should_not_throw_timeout_error()
        {
            var leader = await CreateNetworkAndGetLeader(3);
            var nonLeader = GetRandomFollower();
            long lastIndex;
            using (leader.ContextPool.AllocateOperationContext(out ClusterOperationContext context))
            {
                var cmd = new TestCommandWithRaftId("test", RaftIdGenerator.NewId())
                {
                    RaftCommandIndex = 322
                };

                var t = leader.PutAsync(cmd);
                await leader.PutAsync(cmd);

                // this should not throw timeout exception.
                var exception = await Record.ExceptionAsync(async () => await t);
                Assert.Null(exception);

                using (context.OpenReadTransaction())
                    lastIndex = leader.GetLastEntryIndex(context);
            }
            var waitForAllCommits = nonLeader.WaitForCommitIndexChange(RachisConsensus.CommitIndexModification.GreaterOrEqual, lastIndex);
            Assert.True(await waitForAllCommits.WaitWithoutExceptionAsync(LongWaitTime), "didn't commit in time");
        }

        [Fact]
        public async Task When_command_committed_CompletionTaskSource_is_notified()
        {
            const int commandCount = 10;
            const int clusterSize = 3;
            var leader = await CreateNetworkAndGetLeader(clusterSize);
            var nonLeader = GetRandomFollower();
            var tasks = new List<Task>();
            long lastIndex;

            using (leader.ContextPool.AllocateOperationContext(out ClusterOperationContext context))
            {
                for (var i = 0; i < commandCount; i++)
                {
                    tasks.Add(leader.PutAsync(new TestCommand { Name = "test", Value = i }));
                }
                using (context.OpenReadTransaction())
                    lastIndex = leader.GetLastEntryIndex(context);
            }
            var waitForAllCommits = nonLeader.WaitForCommitIndexChange(RachisConsensus.CommitIndexModification.GreaterOrEqual, lastIndex);

            Assert.True(await Task.WhenAny(waitForAllCommits, Task.Delay(LongWaitTime)) == waitForAllCommits, "didn't commit in time");
            var waitForNotificationsOnTasks = Task.WhenAll(tasks);
            Assert.True(await Task.WhenAny(waitForNotificationsOnTasks, Task.Delay(LongWaitTime)) == waitForNotificationsOnTasks, "Some commands didn't complete");
        }

        [Fact]
        public async Task Command_not_committed_after_timeout_CompletionTaskSource_is_notified()
        {
            const int commandCount = 3;
            const int clusterSize = 3;
            var leader = await CreateNetworkAndGetLeader(clusterSize);
            var nonLeader = GetRandomFollower();
            var tasks = new List<Task>();
            long lastIndex;

            using (leader.ContextPool.AllocateOperationContext(out ClusterOperationContext context))
            {
                for (var i = 0; i < commandCount; i++)
                {
                    tasks.Add(leader.PutAsync(new TestCommand { Name = "test", Value = i }));
                }
                using (context.OpenReadTransaction())
                    lastIndex = leader.GetLastEntryIndex(context);
            }
            var waitForAllCommits = nonLeader.WaitForCommitIndexChange(RachisConsensus.CommitIndexModification.GreaterOrEqual, lastIndex);
            Assert.True(await waitForAllCommits.WaitWithoutExceptionAsync(LongWaitTime), "didn't commit in time");

            Assert.True(await Task.WhenAll(tasks).WaitWithoutExceptionAsync(TimeSpan.FromSeconds(15)), $"Some commands didn't complete");
            DisconnectFromNode(leader);
            
            try
            {
                var task = leader.PutAsync(new TestCommand { Name = "test", Value = commandCount });
                Assert.True(await task.WaitWithoutExceptionAsync((int)leader.ElectionTimeout.TotalMilliseconds * 10));
                await task;
                Assert.True(false, "We should have gotten an error");
            }
            // expecting either one of those
            catch (TimeoutException)
            {
            }
            catch (NotLeadingException)
            {
            }
        }
    }
}
