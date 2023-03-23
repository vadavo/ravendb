﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Esprima.Ast;
using Lucene.Net.Documents;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.ServerWide;
using Raven.Server;
using Raven.Server.Monitoring.Snmp;
using Raven.Server.Rachis;
using Raven.Server.ServerWide.Context;
using Sparrow.Json.Parsing;
using Tests.Infrastructure;
using Xunit;

namespace FastTests;

public partial class RavenTestBase
{
    public readonly ClusterTestBase2 Cluster;

    public class ClusterTestBase2
    {
        private readonly RavenTestBase _parent;

        public ClusterTestBase2(RavenTestBase parent)
        {
            _parent = parent ?? throw new ArgumentNullException(nameof(parent));
        }

        public async Task WaitForRaftCommandToBeAppliedInClusterAsync(RavenServer leader, string commandType)
        {
            var updateIndex = LastRaftIndexForCommand(leader, commandType);
            await WaitForRaftIndexToBeAppliedInClusterAsync(updateIndex, TimeSpan.FromSeconds(10));
        }

        public async Task WaitForRaftCommandToBeAppliedInLocalServerAsync(string commandType)
        {
            var updateIndex = LastRaftIndexForCommand(_parent.Server, commandType);
            await _parent.Server.ServerStore.Cluster.WaitForIndexNotification(updateIndex, TimeSpan.FromSeconds(10));
        }

        public async Task CreateIndexInClusterAsync(IDocumentStore store, AbstractIndexCreationTask index, List<RavenServer> nodes = null)
        {
            var results = (await store.Maintenance.ForDatabase(store.Database)
                                        .SendAsync(new PutIndexesOperation(index.CreateIndexDefinition())))
                                        .Single(r => r.Index == index.IndexName);

            // wait for index creation on cluster
            nodes ??= _parent.Servers;
            await WaitForRaftIndexToBeAppliedOnClusterNodesAsync(results.RaftCommandIndex, nodes);
        }

        public long LastRaftIndexForCommand(RavenServer server, string commandType)
        {
            var updateIndex = 0L;
            var commandFound = false;
            using (server.ServerStore.Engine.ContextPool.AllocateOperationContext(out ClusterOperationContext context))
            using (context.OpenReadTransaction())
            {
                foreach (var entry in server.ServerStore.Engine.LogHistory.GetHistoryLogs(context))
                {
                    var type = entry[nameof(RachisLogHistory.LogHistoryColumn.Type)].ToString();
                    if (type == commandType)
                    {
                        commandFound = true;
                        Assert.True(long.TryParse(entry[nameof(RachisLogHistory.LogHistoryColumn.Index)].ToString(), out updateIndex));
                    }
                }
            }

            Assert.True(commandFound, $"{commandType} wasn't found in the log.");
            return updateIndex;
        }

        public IEnumerable<DynamicJsonValue> GetRaftCommands(RavenServer server, string commandType = null)
        {
            using (server.ServerStore.Engine.ContextPool.AllocateOperationContext(out ClusterOperationContext context))
            using (context.OpenReadTransaction())
            {
                foreach (var entry in server.ServerStore.Engine.LogHistory.GetHistoryLogs(context))
                {
                    var type = entry[nameof(RachisLogHistory.LogHistoryColumn.Type)].ToString();
                    if (commandType == null || commandType == type)
                        yield return entry;
                }
            }
        }

        public string GetRaftHistory(RavenServer server)
        {
            var sb = new StringBuilder();

            using (server.ServerStore.Engine.ContextPool.AllocateOperationContext(out ClusterOperationContext context))
            using (context.OpenReadTransaction())
            {
                foreach (var entry in server.ServerStore.Engine.LogHistory.GetHistoryLogs(context))
                {
                    sb.AppendLine(context.ReadObject(entry, "raft-command-history").ToString());
                }
            }

            return sb.ToString();
        }

        public async Task WaitForRaftIndexToBeAppliedInClusterWithNodesValidationAsync(long index, TimeSpan? timeout = null)
        {
            var notDisposed = _parent.Servers.Count(s => s.ServerStore.Disposed == false);
            var notPassive = _parent.Servers.Count(s => s.ServerStore.Engine.CurrentState != RachisState.Passive);

            Assert.True(_parent.Servers.Count == notDisposed, $"Unequal not disposed nodes {_parent.Servers.Count} != {notDisposed}");
            Assert.True(_parent.Servers.Count == notPassive, $"Unequal not passive nodes {_parent.Servers.Count} != {notPassive}");

            await WaitForRaftIndexToBeAppliedInClusterAsync(index, timeout);
        }

        public async Task WaitForRaftIndexToBeAppliedInClusterAsync(long index, TimeSpan? timeout = null)
        {
            await WaitForRaftIndexToBeAppliedOnClusterNodesAsync(index, _parent.Servers, timeout);
        }

        public async Task WaitForRaftIndexToBeAppliedOnClusterNodesAsync(long index, List<RavenServer> nodes, TimeSpan? timeout = null)
        {
            if (nodes.Count == 0)
                throw new InvalidOperationException("Cannot wait for raft index to be applied when the cluster is empty. Make sure you are using the right server.");

            if (timeout.HasValue == false)
                timeout = Debugger.IsAttached ? TimeSpan.FromSeconds(300) : TimeSpan.FromSeconds(60);

            var tasks = nodes.Where(s => s.ServerStore.Disposed == false &&
                                          s.ServerStore.Engine.CurrentState != RachisState.Passive)
                .Select(server => server.ServerStore.Cluster.WaitForIndexNotification(index))
                .ToList();

            if (await Task.WhenAll(tasks).WaitWithoutExceptionAsync(timeout.Value))
                return;

            ThrowTimeoutException(nodes, tasks, index, timeout.Value);
        }

        private static void ThrowTimeoutException(List<RavenServer> nodes, List<Task> tasks, long index, TimeSpan timeout)
        {
            var message = $"Timed out after {timeout} waiting for index {index} because out of {nodes.Count} servers" +
                          " we got confirmations that it was applied only on the following servers: ";

            for (var i = 0; i < tasks.Count; i++)
            {
                message += $"{Environment.NewLine}Url: {nodes[i].WebUrl}. Applied: {tasks[i].IsCompleted}.";
                if (tasks[i].IsCompleted == false)
                {
                    using (nodes[i].ServerStore.Engine.ContextPool.AllocateOperationContext(out ClusterOperationContext context))
                    {
                        context.OpenReadTransaction();
                        message += $"{Environment.NewLine}Log state for non responsing server:{Environment.NewLine}{nodes[i].ServerStore.Engine.LogHistory.GetHistoryLogsAsString(context)}";
                    }
                }
            }

            throw new TimeoutException(message);
        }

        public string CollectLogsFromNodes(List<RavenServer> nodes)
        {
            var message = "";
            for (var i = 0; i < nodes.Count; i++)
            {
                message += $"{Environment.NewLine}Url: {nodes[i].WebUrl}.";
                using (nodes[i].ServerStore.Engine.ContextPool.AllocateOperationContext(out ClusterOperationContext context))
                using (context.OpenReadTransaction())
                {
                    message += CollectLogs(context, nodes[i]);
                }
            }

            return message;
        }

        public string CollectLogs(ClusterOperationContext context, RavenServer server)
        {
            return
                $"{Environment.NewLine}Log for server '{server.ServerStore.NodeTag}':" +
                $"{Environment.NewLine}Last notified Index '{server.ServerStore.Cluster.LastNotifiedIndex}':" +
                $"{Environment.NewLine}{context.ReadObject(server.ServerStore.GetLogDetails(context, max: int.MaxValue), "LogSummary/" + server.ServerStore.NodeTag)}" +
                $"{Environment.NewLine}{server.ServerStore.Engine.LogHistory.GetHistoryLogsAsString(context)}";
        }

        public string GetLastStatesFromAllServersOrderedByTime()
        {
            List<(string tag, RachisConsensus.StateTransition transition)> states = new List<(string tag, RachisConsensus.StateTransition transition)>();
            foreach (var s in _parent.Servers)
            {
                foreach (var state in s.ServerStore.Engine.PrevStates)
                {
                    states.Add((s.ServerStore.NodeTag, state));
                }
            }
            return string.Join(Environment.NewLine, states.OrderBy(x => x.transition.When).Select(x => $"State for {x.tag}-term{x.Item2.CurrentTerm}:{Environment.NewLine}{x.Item2.From}=>{x.Item2.To} at {x.Item2.When:o} {Environment.NewLine}because {x.Item2.Reason}"));
        }

        public void SuspendObserver(RavenServer server)
        {
            // observer is set in the background task, hence we are waiting for it to not be null
            WaitForValue(() => server.ServerStore.Observer != null, true);

            server.ServerStore.Observer.Suspended = true;
        }
    }
}
