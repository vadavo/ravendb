﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Client.Documents.Changes;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Indexes.Counters;
using Raven.Client.Documents.Indexes.TimeSeries;
using Raven.Client.Exceptions.Documents.Compilation;
using Raven.Client.Exceptions.Documents.Indexes;
using Raven.Client.ServerWide;
using Raven.Client.Util;
using Raven.Server.Commercial;
using Raven.Server.Config.Categories;
using Raven.Server.Config.Settings;
using Raven.Server.Documents.Indexes.Analysis;
using Raven.Server.Documents.Indexes.Auto;
using Raven.Server.Documents.Indexes.Configuration;
using Raven.Server.Documents.Indexes.Errors;
using Raven.Server.Documents.Indexes.IndexMerging;
using Raven.Server.Documents.Indexes.MapReduce.Auto;
using Raven.Server.Documents.Indexes.MapReduce.OutputToCollection;
using Raven.Server.Documents.Indexes.MapReduce.Static;
using Raven.Server.Documents.Indexes.Persistence.Lucene;
using Raven.Server.Documents.Indexes.Sorting;
using Raven.Server.Documents.Indexes.Static;
using Raven.Server.Documents.Indexes.Static.Counters;
using Raven.Server.Documents.Indexes.Static.TimeSeries;
using Raven.Server.Documents.Queries.Dynamic;
using Raven.Server.NotificationCenter.Notifications;
using Raven.Server.NotificationCenter.Notifications.Details;
using Raven.Server.Rachis;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Commands.Indexes;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Sparrow.Collections;
using Sparrow.Logging;
using Sparrow.Threading;
using Sparrow.Utils;

namespace Raven.Server.Documents.Indexes
{
    public class IndexStore : IDisposable
    {
        private readonly DocumentDatabase _documentDatabase;
        private readonly ServerStore _serverStore;

        private readonly CollectionOfIndexes _indexes = new CollectionOfIndexes();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _indexLocks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        private bool _initialized;

        private bool _run = true;

        private long _lastSurpassedAutoIndexesDatabaseRecordEtag;

        public readonly IndexIdentities Identities = new IndexIdentities();

        public readonly Logger Logger;

        public SemaphoreSlim StoppedConcurrentIndexBatches { get; }

        internal Action<(string IndexName, bool DidWork)> IndexBatchCompleted;

        private const int PathLengthLimit = 259; // Roslyn's MetadataWriter.PathLengthLimit = 259

        internal static int MaxIndexNameLength = PathLengthLimit -
                                                 IndexCompiler.IndexNamePrefix.Length -
                                                 1 - // "."
                                                 36 - // new Guid()
                                                 IndexCompiler.IndexExtension.Length -
                                                 4; // ".dll"

        public IndexStore(DocumentDatabase documentDatabase, ServerStore serverStore)
        {
            _documentDatabase = documentDatabase;
            _serverStore = serverStore;
            Logger = LoggingSource.Instance.GetLogger<IndexStore>(_documentDatabase.Name);

            var stoppedConcurrentIndexBatches = _documentDatabase.Configuration.Indexing.NumberOfConcurrentStoppedBatchesIfRunningLowOnMemory;
            StoppedConcurrentIndexBatches = new SemaphoreSlim(stoppedConcurrentIndexBatches);
        }

        public int HandleDatabaseRecordChange(DatabaseRecord record, long raftIndex, bool startIndexes = true)
        {
            try
            {
                if (record == null)
                    return 0;

                HandleSorters(record, raftIndex);
                HandleAnalyzers(record, raftIndex);

                HandleDeletes(record, raftIndex);

                var newIndexesToStart = new List<Index>();
                ConcurrentSet<Index> indexesToDelete = null;

                try
                {
                    HandleChangesForStaticIndexes(record, raftIndex, newIndexesToStart);
                    HandleChangesForAutoIndexes(record, raftIndex, newIndexesToStart);

                    if (newIndexesToStart.Count <= 0)
                        return 0;

                    indexesToDelete = new ConcurrentSet<Index>();

                    if (startIndexes)
                    {
                        var sp = Stopwatch.StartNew();

                        if (Logger.IsInfoEnabled)
                            Logger.Info($"Starting {newIndexesToStart.Count} new index{(newIndexesToStart.Count > 1 ? "es" : string.Empty)}");

                        ExecuteForIndexes(newIndexesToStart, index =>
                        {
                            var indexLock = GetIndexLock(index.Name);

                            try
                            {
                                indexLock.Wait(_documentDatabase.DatabaseShutdown);
                            }
                            catch (OperationCanceledException e)
                            {
                                AddToIndexesToDelete(index);

                                _documentDatabase.RachisLogIndexNotifications.NotifyListenersAbout(raftIndex, e);
                                return;
                            }

                            try
                            {
                                StartIndex(index);
                            }
                            catch (Exception e)
                            {
                                AddToIndexesToDelete(index);

                                _documentDatabase.RachisLogIndexNotifications.NotifyListenersAbout(raftIndex, e);
                                if (Logger.IsInfoEnabled)
                                    Logger.Info($"Could not start index '{index.Name}'", e);
                            }
                            finally
                            {
                                indexLock.Release();
                            }
                        });

                        if (Logger.IsInfoEnabled)
                            Logger.Info(
                                $"Started {newIndexesToStart.Count} new index{(newIndexesToStart.Count > 1 ? "es" : string.Empty)}, took: {sp.ElapsedMilliseconds}ms");
                    }

                    var numberOfIndexesToDelete = HandleIndexesToDelete();

                    return newIndexesToStart.Count - numberOfIndexesToDelete;
                }
                catch
                {
                    HandleIndexesToDelete();

                    throw;
                }

                void AddToIndexesToDelete(Index index)
                {
                    if (index == null)
                        return;

                    // dispose only if we do not have this index
                    if (_indexes.TryGetByName(index.Name, out var oldIndex) == false || ReferenceEquals(oldIndex, index) == false)
                        indexesToDelete.Add(index);
                }

                int HandleIndexesToDelete()
                {
                    if (indexesToDelete == null)
                        return 0;

                    foreach (var index in indexesToDelete)
                        DeleteIndexInternal(index, raiseNotification: false);

                    return indexesToDelete.Count;
                }
            }
            finally
            {
                RollIndexesIfNeeded();
            }
        }

        private void ExecuteForIndexes(IEnumerable<Index> indexes, Action<Index> action)
        {
            var numberOfUtilizedCores = GetNumberOfUtilizedCores();

            Parallel.ForEach(indexes, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, numberOfUtilizedCores / 2)
            }, action);
        }

        private int GetNumberOfUtilizedCores()
        {
            int defaultNumberOfCores = ProcessorInfo.ProcessorCount;

            try
            {
                var licenseLimits = _documentDatabase.ServerStore.LoadLicenseLimits();

                return licenseLimits != null && licenseLimits.NodeLicenseDetails.TryGetValue(_serverStore.NodeTag, out DetailsPerNode detailsPerNode)
                    ? detailsPerNode.UtilizedCores
                    : defaultNumberOfCores;
            }
            catch (Exception e)
            {
                if (e.IsOutOfMemory() == false)
                {
                    if (Logger.IsOperationsEnabled)
                        Logger.Operations($"Failed to get number of utilized cores. Defaulting to {defaultNumberOfCores} cores", e);
                }

                return defaultNumberOfCores;
            }
        }

        private void HandleSorters(DatabaseRecord record, long index)
        {
            try
            {
                SorterCompilationCache.Instance.AddItems(record);
            }
            catch (Exception e)
            {
                _documentDatabase.RachisLogIndexNotifications.NotifyListenersAbout(index, e);
                if (Logger.IsInfoEnabled)
                    Logger.Info($"Could not update sorters", e);
            }
        }

        private void HandleAnalyzers(DatabaseRecord record, long index)
        {
            try
            {
                AnalyzerCompilationCache.Instance.AddItems(record);
            }
            catch (Exception e)
            {
                _documentDatabase.RachisLogIndexNotifications.NotifyListenersAbout(index, e);
                if (Logger.IsInfoEnabled)
                    Logger.Info($"Could not update analyzers", e);
            }
        }

        private void HandleChangesForAutoIndexes(DatabaseRecord record, long index, List<Index> indexesToStart)
        {
            var mode = _documentDatabase.Configuration.Indexing.AutoIndexDeploymentMode;

            foreach (var kvp in record.AutoIndexes)
            {
                _documentDatabase.DatabaseShutdown.ThrowIfCancellationRequested();

                var name = kvp.Key;
                try
                {
                    var definition = CreateAutoDefinition(kvp.Value, mode);

                    var indexToStart = HandleAutoIndexChange(name, definition);
                    if (indexToStart != null)
                    {
                        indexesToStart.Add(indexToStart);
                    }
                }
                catch (Exception e)
                {
                    _documentDatabase.RachisLogIndexNotifications.NotifyListenersAbout(index, e);
                    if (Logger.IsInfoEnabled)
                        Logger.Info($"Could not create auto index {name}", e);
                }
            }
        }

        private Index HandleAutoIndexChange(string name, AutoIndexDefinitionBaseServerSide definition)
        {
            using (IndexLock(name))
            {
                var creationOptions = IndexCreationOptions.Create;
                var existingIndex = GetIndex(name);
                IndexDefinitionCompareDifferences differences = IndexDefinitionCompareDifferences.None;
                if (existingIndex != null)
                    creationOptions = GetIndexCreationOptions(definition, existingIndex, out differences);

                if (creationOptions == IndexCreationOptions.Noop)
                {
                    Debug.Assert(existingIndex != null);

                    return null;
                }

                if (creationOptions == IndexCreationOptions.UpdateWithoutUpdatingCompiledIndex || creationOptions == IndexCreationOptions.Update)
                {
                    Debug.Assert(existingIndex != null);

                    if ((differences & IndexDefinitionCompareDifferences.LockMode) != 0)
                    {
                        existingIndex.SetLock(definition.LockMode);
                    }

                    if ((differences & IndexDefinitionCompareDifferences.Priority) != 0)
                    {
                        existingIndex.SetPriority(definition.Priority);
                    }

                    if ((differences & IndexDefinitionCompareDifferences.State) != 0)
                    {
                        // this can only be set by cluster
                        // and if local state is disabled or error
                        // then we are ignoring this change
                        if (existingIndex.State == IndexState.Normal || existingIndex.State == IndexState.Idle)
                            existingIndex.SetState(definition.State);
                    }

                    existingIndex.Update(definition, existingIndex.Configuration);

                    return null;
                }

                Index index;

                if (definition is AutoMapIndexDefinition)
                    index = AutoMapIndex.CreateNew((AutoMapIndexDefinition)definition, _documentDatabase);
                else if (definition is AutoMapReduceIndexDefinition)
                    index = AutoMapReduceIndex.CreateNew((AutoMapReduceIndexDefinition)definition, _documentDatabase);
                else
                    throw new NotImplementedException($"Unknown index definition type: {definition.GetType().FullName}");

                return index;
            }
        }

        internal static AutoIndexDefinitionBaseServerSide CreateAutoDefinition(AutoIndexDefinition definition, IndexDeploymentMode indexDeployment)
        {
            var mapFields = definition
                .MapFields
                .Select(x =>
                {
                    var field = AutoIndexField.Create(x.Key, x.Value);

                    Debug.Assert(x.Value.GroupByArrayBehavior == GroupByArrayBehavior.NotApplicable);

                    return field;
                })
                .ToArray();

            if (definition.Type == IndexType.AutoMap)
            {
                var result = new AutoMapIndexDefinition(definition.Collection, mapFields, indexDeployment, IndexDefinitionBaseServerSide.IndexVersion.CurrentVersion);

                if (definition.Priority.HasValue)
                    result.Priority = definition.Priority.Value;

                if (definition.State.HasValue)
                    result.State = definition.State.Value;

                return result;
            }

            if (definition.Type == IndexType.AutoMapReduce)
            {
                var groupByFields = definition
                    .GroupByFields
                    .Select(x =>
                    {
                        var field = AutoIndexField.Create(x.Key, x.Value);

                        return field;
                    })
                    .ToArray();

                var result = new AutoMapReduceIndexDefinition(definition.Collection, mapFields, groupByFields, indexDeployment, IndexDefinitionBaseServerSide.IndexVersion.CurrentVersion);

                if (definition.Priority.HasValue)
                    result.Priority = definition.Priority.Value;

                if (definition.State.HasValue)
                    result.State = definition.State.Value;

                return result;
            }

            throw new NotSupportedException("Cannot create auto-index from " + definition.Type);
        }

        private void RollIndexesIfNeeded()
        {
            if (_indexes == null)
                return;

            foreach (var index in _indexes)
            {
                index.RollIfNeeded();
            }
        }

        private void HandleChangesForStaticIndexes(DatabaseRecord record, long index, List<Index> indexesToStart)
        {
            foreach (var kvp in record.Indexes)
            {
                _documentDatabase.DatabaseShutdown.ThrowIfCancellationRequested();

                var name = kvp.Key;
                var definition = kvp.Value;

                try
                {
                    if (definition.Name.StartsWith(Constants.Documents.Indexing.SideBySideIndexNamePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Index name cannot start with {Constants.Documents.Indexing.SideBySideIndexNamePrefix} but got {definition.Name} of index {name}");
                    }
                    var indexToStart = HandleStaticIndexChange(name, definition);
                    if (indexToStart != null)
                    {
                        indexesToStart.Add(indexToStart);
                    }
                }
                catch (Exception exception)
                {
                    _documentDatabase.RachisLogIndexNotifications.NotifyListenersAbout(index, exception);

                    var indexName = name;


                    if (exception is OperationCanceledException)
                        return;

                    if (Logger.IsOperationsEnabled)
                        Logger.Operations($"Could not update static index {name}", exception);

                    //If we don't have the index in memory this means that it is corrupted when trying to load it
                    //If we do have the index and it is not faulted this means that this is the replacement index that is faulty
                    //If we already have a replacement that is faulty don't add a new one
                    if (_indexes.TryGetByName(indexName, out Index i))
                    {
                        if (i is FaultyInMemoryIndex)
                            return;
                        indexName = Constants.Documents.Indexing.SideBySideIndexNamePrefix + name;
                        if (_indexes.TryGetByName(indexName, out Index j) && j is FaultyInMemoryIndex)
                            return;
                    }

                    var configuration = new FaultyInMemoryIndexConfiguration(_documentDatabase.Configuration.Indexing.StoragePath, _documentDatabase.Configuration);
                    var fakeIndex = new FaultyInMemoryIndex(exception, indexName, configuration, definition);
                    _indexes.Add(fakeIndex);
                }
            }
        }

        public bool ShouldSkipThisNodeWhenRolling(Index index, out string reason, out bool replace)
        {
            replace = false;
            if (index.IsRolling == false)
            {
                reason = "I'm not a rolling index";
                return false;
            }

            using (_serverStore.ContextPool.AllocateOperationContext(out TransactionOperationContext ctx))
            using (ctx.OpenReadTransaction())
            using (var rawRecord = _serverStore.Cluster.ReadRawDatabaseRecord(ctx, _documentDatabase.Name))
            {
                return ShouldSkipThisNode(rawRecord, index, out reason, out replace);
            }
        }

        private bool ShouldSkipThisNode(RawDatabaseRecord record, Index index, out string reason, out bool replace)
        {
            reason = null;
            replace = false;

            if (record.DeletionInProgress.TryGetValue(_serverStore.NodeTag, out var deletion))
            {
                if (deletion != DeletionInProgressStatus.No)
                {
                    reason = "This node being deleted";
                    return true;
                }
            }

            // Can happen if there's a race in the record --> we don't skip, we process the index
            if (record.RollingIndexes == null)
            {
                reason = "No rolling indexes";
                return false;
            }

            var originalName = index.NormalizedName;

            if (record.RollingIndexes.TryGetValue(originalName, out var rollingIndex) == false)
            {
                reason = "I'm not a rolling index";
                return false;
            }

            // Can happen when we add a node to the database while the rolling index is running --> we skip this node for now.
            // Adding the new node to the deployment will be handled in PutRollingIndexCommand.UpdateDatabaseRecord()
            if (rollingIndex.ActiveDeployments.TryGetValue(_serverStore.NodeTag, out var nodeDeployment) == false)
            {
                reason = "My node has no active deployment";
                return true;
            }

            if (nodeDeployment.State != RollingIndexState.Pending)
            {
                reason = $"My state is {nodeDeployment.State}";
                return false;
            }

            var didWork = DidWork(originalName);

            if (index.Definition.Name.StartsWith(Constants.Documents.Indexing.SideBySideIndexNamePrefix))
            {
                reason = "I'm a pending side-by-side";
                if (didWork == false)
                {
                    replace = true;
                }

                return true;
            }

            if (HasReplacement(index.Definition.Name) == false)
            {
                reason = "It isn't my turn to be deployed";
                return true;
            }

            if (didWork == false)
            {
                reason = "I have a side-by-side that will replace me";
                return true;
            }

            reason = "I'm the original index";
            return false;
        }

        private bool DidWork(string name)
        {
            bool didWork;
            if (_indexes.TryGetByName(name, out var originalIndex) == false)
                return true; // we can't tell, assume we did

            using (var context = QueryOperationContext.Allocate(_documentDatabase, originalIndex))
            using (context.OpenReadTransaction())
            {
                didWork = originalIndex.GetIndexingState(context).LastProcessedEtag != 0;
            }

            return didWork;
        }

        private Index HandleStaticIndexChange(string name, IndexDefinition definition)
        {
            using (IndexLock(name))
            {
                var creationOptions = IndexCreationOptions.Create;
                var currentIndex = GetIndex(name);
                IndexDefinitionCompareDifferences currentDifferences = IndexDefinitionCompareDifferences.None;

                if (currentIndex != null)
                    creationOptions = GetIndexCreationOptions(definition, currentIndex, out currentDifferences);

                var replacementIndexName = Constants.Documents.Indexing.SideBySideIndexNamePrefix + definition.Name;

                if (creationOptions == IndexCreationOptions.Noop)
                {
                    Debug.Assert(currentIndex != null);

                    var replacementIndex = GetIndex(replacementIndexName);

                    if (replacementIndex != null)
                    {
                        if (replacementIndex is MapReduceIndex replacementMapReduceIndex && replacementMapReduceIndex.OutputReduceToCollection != null)
                        {
                            if (replacementMapReduceIndex.Definition.ReduceOutputIndex != null &&
                                currentIndex is MapReduceIndex currentMapReduceIndex)
                            {
                                var prefix = OutputReduceToCollectionCommand.GetOutputDocumentPrefix(
                                    replacementMapReduceIndex.Definition.OutputReduceToCollection, replacementMapReduceIndex.Definition.ReduceOutputIndex.Value);

                                // original index needs to delete docs created by side-by-side indexing
                                currentMapReduceIndex.OutputReduceToCollection?.AddPrefixesOfDocumentsToDelete(new Dictionary<string, string>
                                {
                                    {prefix, replacementMapReduceIndex.Definition.PatternForOutputReduceToCollectionReferences}
                                });
                            }
                        }


                        DeleteIndexInternal(replacementIndex);
                    }

                    return null;
                }

                var definitionClone = new IndexDefinition();
                definition.CopyTo(definitionClone);
                definition = definitionClone;

                if (creationOptions == IndexCreationOptions.UpdateWithoutUpdatingCompiledIndex)
                {
                    Debug.Assert(currentIndex != null);

                    var replacementIndex = GetIndex(replacementIndexName);

                    if (replacementIndex != null)
                        DeleteIndexInternal(replacementIndex);

                    if (currentDifferences != IndexDefinitionCompareDifferences.None)
                        UpdateIndex(definition, currentIndex, currentDifferences);

                    return null;
                }

                UpdateIndexDefinition(definition, currentIndex, currentDifferences);

                var prefixesOfDocumentsToDelete = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                if (creationOptions == IndexCreationOptions.Update)
                {
                    Debug.Assert(currentIndex != null);

                    if (currentIndex is MapReduceIndex oldMapReduceIndex && oldMapReduceIndex.OutputReduceToCollection != null)
                    {
                        // we need to delete reduce output docs of existing index

                        CollectPrefixesOfDocumentsToDelete(oldMapReduceIndex, ref prefixesOfDocumentsToDelete);
                    }

                    definition.Name = replacementIndexName;
                    var replacementIndex = GetIndex(replacementIndexName);

                    if (replacementIndex != null)
                    {
                        creationOptions = GetIndexCreationOptions(definition, replacementIndex, out IndexDefinitionCompareDifferences sideBySideDifferences);
                        if (creationOptions == IndexCreationOptions.Noop)
                            return null;

                        if (creationOptions == IndexCreationOptions.UpdateWithoutUpdatingCompiledIndex)
                        {
                            UpdateIndex(definition, replacementIndex, sideBySideDifferences);
                            return null;
                        }

                        if (replacementIndex is MapReduceIndex oldReplacementMapReduceIndex && oldReplacementMapReduceIndex.OutputReduceToCollection != null)
                        {
                            // existing replacement index could already produce some reduce output documents, new replacement index needs to delete them

                            CollectPrefixesOfDocumentsToDelete(oldReplacementMapReduceIndex, ref prefixesOfDocumentsToDelete);
                        }

                        DeleteIndexInternal(replacementIndex);
                    }
                }

                Index index;
                switch (definition.SourceType)
                {
                    case IndexSourceType.Documents:
                        switch (definition.Type)
                        {
                            case IndexType.Map:
                            case IndexType.JavaScriptMap:
                                index = MapIndex.CreateNew(definition, _documentDatabase);
                                break;

                            case IndexType.MapReduce:
                            case IndexType.JavaScriptMapReduce:
                                var mapReduceIndex = MapReduceIndex.CreateNew<MapReduceIndex>(definition, _documentDatabase);

                                if (mapReduceIndex.OutputReduceToCollection != null && prefixesOfDocumentsToDelete.Count > 0)
                                    mapReduceIndex.OutputReduceToCollection.AddPrefixesOfDocumentsToDelete(prefixesOfDocumentsToDelete);

                                index = mapReduceIndex;
                                break;

                            default:
                                throw new NotSupportedException($"Cannot create {definition.Type} index from IndexDefinition");
                        }

                        break;

                    case IndexSourceType.TimeSeries:
                        switch (definition.Type)
                        {
                            case IndexType.Map:
                            case IndexType.JavaScriptMap:
                                index = MapTimeSeriesIndex.CreateNew(definition, _documentDatabase);
                                break;

                            case IndexType.MapReduce:
                            case IndexType.JavaScriptMapReduce:
                                var mapReduceIndex = MapReduceIndex.CreateNew<MapReduceTimeSeriesIndex>(definition, _documentDatabase);

                                if (mapReduceIndex.OutputReduceToCollection != null && prefixesOfDocumentsToDelete.Count > 0)
                                    mapReduceIndex.OutputReduceToCollection.AddPrefixesOfDocumentsToDelete(prefixesOfDocumentsToDelete);

                                index = mapReduceIndex;
                                break;

                            default:
                                throw new NotSupportedException($"Cannot create {definition.Type} index from TimeSeriesIndexDefinition");
                        }

                        break;

                    case IndexSourceType.Counters:
                        switch (definition.Type)
                        {
                            case IndexType.Map:
                            case IndexType.JavaScriptMap:
                                index = MapCountersIndex.CreateNew(definition, _documentDatabase);
                                break;

                            case IndexType.MapReduce:
                            case IndexType.JavaScriptMapReduce:
                                var mapReduceIndex = MapReduceIndex.CreateNew<MapReduceCountersIndex>(definition, _documentDatabase);

                                if (mapReduceIndex.OutputReduceToCollection != null && prefixesOfDocumentsToDelete.Count > 0)
                                    mapReduceIndex.OutputReduceToCollection.AddPrefixesOfDocumentsToDelete(prefixesOfDocumentsToDelete);

                                index = mapReduceIndex;
                                break;

                            default:
                                throw new NotSupportedException($"Cannot create {definition.Type} index from TimeSeriesIndexDefinition");
                        }

                        break;

                    default:
                        throw new NotSupportedException($"Not supported source type '{definition.SourceType}'.");
                }

                return index;
            }
        }

        private static void CollectPrefixesOfDocumentsToDelete(MapReduceIndex mapReduceIndex, ref Dictionary<string, string> prefixesOfDocumentsToDelete)
        {
            var definition = mapReduceIndex.Definition;

            if (definition.ReduceOutputIndex != null)
            {
                var prefix = OutputReduceToCollectionCommand.GetOutputDocumentPrefix(
                    definition.OutputReduceToCollection, definition.ReduceOutputIndex.Value);

                prefixesOfDocumentsToDelete.Add(prefix, mapReduceIndex.Definition.PatternForOutputReduceToCollectionReferences);
            }

            var toDelete = mapReduceIndex.OutputReduceToCollection.GetPrefixesOfDocumentsToDelete();

            if (toDelete != null)
            {
                foreach (var prefix in toDelete)
                {
                    if (prefixesOfDocumentsToDelete.ContainsKey(prefix.Key))
                        continue;

                    prefixesOfDocumentsToDelete.Add(prefix.Key, prefix.Value);
                }
            }
        }

        private void HandleDeletes(DatabaseRecord record, long raftLogIndex)
        {
            foreach (var index in _indexes)
            {
                _documentDatabase.DatabaseShutdown.ThrowIfCancellationRequested();

                var indexNormalizedName = index.Name;
                if (indexNormalizedName.StartsWith(Constants.Documents.Indexing.SideBySideIndexNamePrefix))
                {
                    indexNormalizedName = indexNormalizedName.Remove(0, Constants.Documents.Indexing.SideBySideIndexNamePrefix.Length);
                }

                if (record.Indexes.ContainsKey(indexNormalizedName) || record.AutoIndexes.ContainsKey(indexNormalizedName))
                    continue;

                try
                {
                    DeleteIndexInternal(index);
                }
                catch (Exception e)
                {
                    _documentDatabase.RachisLogIndexNotifications.NotifyListenersAbout(raftLogIndex, e);
                    if (Logger.IsInfoEnabled)
                        Logger.Info($"Could not delete index {index.Name}", e);
                }
            }
        }

        public Task InitializeAsync(DatabaseRecord record, long raftIndex, Action<string> addToInitLog)
        {
            if (_initialized)
                throw new InvalidOperationException($"{nameof(IndexStore)} was already initialized.");

            InitializePath(_documentDatabase.Configuration.Indexing.StoragePath);

            _initialized = true;

            return Task.Run(() =>
            {
                if (_documentDatabase.Configuration.Indexing.RunInMemory == false)
                    OpenIndexesFromRecord(record, raftIndex, addToInitLog);

                HandleSorters(record, raftIndex);
                HandleAnalyzers(record, raftIndex);
            });
        }

        public Index GetIndex(string name)
        {
            if (_indexes.TryGetByName(name, out Index index) == false)
                return null;

            return index;
        }

        public bool HasReplacement(string name)
        {
            var replacementName = Constants.Documents.Indexing.SideBySideIndexNamePrefix + name;
            return _indexes.TryGetByName(replacementName, out _);
        }

        public bool MaybeFinishRollingDeployment(string index, long? lastRollingDeploymentIndex)
        {
            var nodeTag = _serverStore.NodeTag;

            using (_serverStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            using (context.OpenReadTransaction())
            using (var rawRecord = _serverStore.Cluster.ReadRawDatabaseRecord(context, _documentDatabase.Name))
            {
                var rollingIndexes = rawRecord.RollingIndexes;

                if (rollingIndexes == null)
                    return false;

                if (rollingIndexes.TryGetValue(index, out var rollingIndex) == false)
                    return false;

                if (rollingIndex.RaftCommandIndex != lastRollingDeploymentIndex)
                    return false;

                if (rollingIndex.ActiveDeployments.TryGetValue(nodeTag, out var currentDeployment) == false)
                    return false;

                if (currentDeployment.State != RollingIndexState.Running)
                    return false;

                if (HasReplacement(index))
                    return false; // if exists, the replacement index should finish the rolling deployment 
            }

            return true;
        }

        public RollingIndex GetRollingProgress(string name)
        {
            using (_serverStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            using (context.OpenReadTransaction())
            using (var record = _serverStore.Cluster.ReadRawDatabaseRecord(context, _documentDatabase.Name, out _))
            {
                if (record == null)
                    return null;

                if (record.RollingIndexes == null)
                    return null;

                if (record.RollingIndexes.TryGetValue(name, out var progress) == false)
                    return null;

                return progress;
            }
        }

        public async Task<long> CreateIndexInternal(IndexDefinition definition, string raftRequestId, string source = null)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));

            ValidateStaticIndex(definition);

            var command = new PutIndexCommand(
                definition,
                _documentDatabase.Name,
                source,
                _documentDatabase.Time.GetUtcNow(),
                raftRequestId,
                _documentDatabase.Configuration.Indexing.HistoryRevisionsNumber,
                _documentDatabase.Configuration.Indexing.StaticIndexDeploymentMode
            );

            long index = 0;
            try
            {
                index = (await _serverStore.SendToLeaderAsync(command)).Index;
            }
            catch (Exception e)
            {
                ThrowIndexCreationException("static", definition.Name, e, "the cluster is probably down");
            }

            try
            {
                await _documentDatabase.RachisLogIndexNotifications.WaitForIndexNotification(index, _serverStore.Engine.OperationTimeout);
            }
            catch (TimeoutException toe)
            {
                ThrowIndexCreationException("static", definition.Name, toe, $"the operation timed out after: {_serverStore.Engine.OperationTimeout}.");
            }

            return index;
        }

        public async Task<Index> CreateIndex(IndexDefinition definition, string raftRequestId, string source = null)
        {
            await CreateIndexInternal(definition, raftRequestId, source);

            return GetIndex(definition.Name);
        }

        private bool NeedToCheckIfCollectionEmpty(IndexDefinition definition)
        {
            var currentIndex = GetIndex(definition.Name);
            var replacementIndexName = Constants.Documents.Indexing.SideBySideIndexNamePrefix + definition.Name;
            var replacementIndex = GetIndex(replacementIndexName);
            if (currentIndex == null && replacementIndex == null)
            {
                // new index
                return true;
            }

            if (currentIndex == null)
            {
                // we deleted the in memory index but didn't delete the replacement yet
                return true;
            }

            var creationOptions = GetIndexCreationOptions(definition, currentIndex, out var _);
            IndexCreationOptions replacementCreationOptions;
            if (replacementIndex != null)
            {
                replacementCreationOptions = GetIndexCreationOptions(definition, replacementIndex, out var _);
            }
            else
            {
                // the replacement index doesn't exist
                return IsCreateOrUpdate(creationOptions);
            }

            return IsCreateOrUpdate(creationOptions) ||
                   IsCreateOrUpdate(replacementCreationOptions);
        }

        private static bool IsCreateOrUpdate(IndexCreationOptions creationOptions)
        {
            return creationOptions == IndexCreationOptions.Create ||
                   creationOptions == IndexCreationOptions.Update;
        }

        public bool CanUseIndexBatch()
        {
            return ClusterCommandsVersionManager.CanPutCommand(nameof(PutIndexesCommand));
        }

        public IndexBatchScope CreateIndexBatch()
        {
            return new IndexBatchScope(this, GetNumberOfUtilizedCores());
        }

        public async Task<Index> CreateIndex(IndexDefinitionBaseServerSide definition, string raftRequestId)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));

            if (definition is MapIndexDefinition mapIndexDefinition)
                return await CreateIndex(mapIndexDefinition.IndexDefinition, raftRequestId);

            ValidateAutoIndex(definition);
            definition.DeploymentMode = _documentDatabase.Configuration.Indexing.AutoIndexDeploymentMode;

            var command = PutAutoIndexCommand.Create((AutoIndexDefinitionBaseServerSide)definition, _documentDatabase.Name, raftRequestId, _documentDatabase.Configuration.Indexing.AutoIndexDeploymentMode);

            long index = 0;
            try
            {
                index = (await _serverStore.SendToLeaderAsync(command).ConfigureAwait(false)).Index;
            }
            catch (Exception e)
            {
                ThrowIndexCreationException("auto", definition.Name, e, "the cluster is probably down");
            }

            try
            {
                await _documentDatabase.RachisLogIndexNotifications.WaitForIndexNotification(index, _serverStore.Engine.OperationTimeout);
            }
            catch (TimeoutException toe)
            {
                ThrowIndexCreationException("static", definition.Name, toe, $"the operation timed out after: {_serverStore.Engine.OperationTimeout}.");
            }

            ForTestingPurposes?.AfterIndexCreation?.Invoke(definition.Name);

            return GetIndex(definition.Name);
        }

        private void ValidateAutoIndex(IndexDefinitionBaseServerSide definition)
        {
            if (IsValidIndexName(definition.Name, false, out var errorMessage) == false)
            {
                throw new ArgumentException(errorMessage);
            }
        }

        private void ValidateStaticIndex(IndexDefinition definition)
        {
            if (IsValidIndexName(definition.Name, true, out var errorMessage) == false)
            {
                throw new ArgumentException((errorMessage));
            }

            _serverStore.LicenseManager.AssertCanAddAdditionalAssembliesFromNuGet(definition);

            var safeFileSystemIndexName = IndexDefinitionBaseServerSide.GetIndexNameSafeForFileSystem(definition.Name);

            var indexWithFileSystemNameCollision = GetIndexes().FirstOrDefault(x =>
                x.Name.Equals(definition.Name, StringComparison.OrdinalIgnoreCase) == false &&
                safeFileSystemIndexName.Equals(IndexDefinitionBaseServerSide.GetIndexNameSafeForFileSystem(x.Name), StringComparison.OrdinalIgnoreCase));

            if (indexWithFileSystemNameCollision != null)
                throw new IndexCreationException(
                    $"Could not create index '{definition.Name}' because it would result in directory name collision with '{indexWithFileSystemNameCollision.Name}' index");

            definition.RemoveDefaultValues();
            ValidateAnalyzers(definition);

            var instance = IndexCompilationCache.GetIndexInstance(definition, _documentDatabase.Configuration, IndexDefinitionBaseServerSide.IndexVersion.CurrentVersion); // pre-compile it and validate

            if (definition.Type == IndexType.MapReduce)
            {
                MapReduceIndex.ValidateReduceResultsCollectionName(definition, instance, _documentDatabase, NeedToCheckIfCollectionEmpty(definition));

                if (string.IsNullOrEmpty(definition.PatternForOutputReduceToCollectionReferences) == false)
                    OutputReferencesPattern.ValidatePattern(definition.PatternForOutputReduceToCollectionReferences, out _);
            }

        }

        public bool HasChanged(IndexDefinition definition)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));

            if (IsValidIndexName(definition.Name, true, out var errorMessage) == false)
            {
                throw new ArgumentException(errorMessage);
            }

            var existingIndex = GetIndex(definition.Name);
            if (existingIndex == null)
                return true;

            var creationOptions = GetIndexCreationOptions(definition, existingIndex, out IndexDefinitionCompareDifferences _);
            return creationOptions != IndexCreationOptions.Noop;
        }

        private void StartIndex(Index index)
        {
            Debug.Assert(index != null);
            Debug.Assert(string.IsNullOrEmpty(index.Name) == false);
            Debug.Assert(_indexLocks.ContainsKey(index.Name));

            ForTestingPurposes?.BeforeIndexStart?.Invoke(index);

            _indexes.Add(index);

            if (_serverStore.ForTestingPurposes?.StopIndex == true)
                return;

            if (_documentDatabase.Configuration.Indexing.Disabled == false && _run)
                index.Start();

            _documentDatabase.Changes.RaiseNotifications(
                new IndexChange
                {
                    Name = index.Name,
                    Type = IndexChangeTypes.IndexAdded
                });
        }

        private void UpdateIndex(IndexDefinition definition, Index existingIndex, IndexDefinitionCompareDifferences indexDifferences)
        {
            UpdateIndexDefinition(definition, existingIndex, indexDifferences);

            switch (definition.SourceType)
            {
                case IndexSourceType.Documents:

                    switch (definition.Type)
                    {
                        case IndexType.Map:
                        case IndexType.JavaScriptMap:
                            MapIndex.Update(existingIndex, definition, _documentDatabase);
                            break;

                        case IndexType.MapReduce:
                        case IndexType.JavaScriptMapReduce:
                            MapReduceIndex.Update(existingIndex, definition, _documentDatabase);
                            break;

                        default:
                            throw new NotSupportedException($"Cannot update {definition.Type} index from {nameof(IndexDefinition)}");
                    }

                    break;

                case IndexSourceType.Counters:
                    switch (definition.Type)
                    {
                        case IndexType.Map:
                        case IndexType.JavaScriptMap:
                            MapCountersIndex.Update(existingIndex, definition, _documentDatabase);
                            break;

                        case IndexType.MapReduce:
                        case IndexType.JavaScriptMapReduce:
                            MapReduceIndex.Update(existingIndex, definition, _documentDatabase);
                            break;

                        default:
                            throw new NotSupportedException($"Cannot create {definition.Type} index from {nameof(CountersIndexDefinition)}");
                    }

                    break;

                case IndexSourceType.TimeSeries:
                    switch (definition.Type)
                    {
                        case IndexType.Map:
                        case IndexType.JavaScriptMap:
                            MapTimeSeriesIndex.Update(existingIndex, definition, _documentDatabase);
                            break;

                        case IndexType.MapReduce:
                        case IndexType.JavaScriptMapReduce:
                            MapReduceIndex.Update(existingIndex, definition, _documentDatabase);
                            break;

                        default:
                            throw new NotSupportedException($"Cannot create {definition.Type} index from {nameof(TimeSeriesIndexDefinition)}");
                    }

                    break;

                default:
                    throw new NotSupportedException($"Not supported source type '{definition.SourceType}'.");
            }
        }

        private static void UpdateIndexDefinition(IndexDefinitionBase definition, Index existingIndex, IndexDefinitionCompareDifferences indexDifferences)
        {
            if (definition is IndexDefinition def)
            {
                if (def.LockMode.HasValue && (indexDifferences & IndexDefinitionCompareDifferences.LockMode) != 0)
                    existingIndex.SetLock(def.LockMode.Value);
            }
            if (definition.Priority.HasValue && (indexDifferences & IndexDefinitionCompareDifferences.Priority) != 0)
                existingIndex.SetPriority(definition.Priority.Value);
        }

        internal IndexCreationOptions GetIndexCreationOptions(object indexDefinition, Index existingIndex, out IndexDefinitionCompareDifferences differences)
        {
            differences = IndexDefinitionCompareDifferences.All;
            if (existingIndex == null)
                return IndexCreationOptions.Create;

            differences = IndexDefinitionCompareDifferences.None;

            var indexDef = indexDefinition as IndexDefinition;
            if (indexDef != null)
            {
                differences = existingIndex.Definition.Compare(indexDef);
                if (indexDef.ClusterState?.LastStateIndex > (existingIndex.Definition.ClusterState?.LastStateIndex ?? -1))
                {
                    differences |= IndexDefinitionCompareDifferences.State;
                }
            }

            var indexDefBase = indexDefinition as IndexDefinitionBaseServerSide;
            if (indexDefBase != null)
                differences = existingIndex.Definition.Compare(indexDefBase);

            if (differences == IndexDefinitionCompareDifferences.All)
                return IndexCreationOptions.Update;

            if (differences == IndexDefinitionCompareDifferences.None)
                return IndexCreationOptions.Noop;

            if ((differences & IndexDefinitionCompareDifferences.Maps) == IndexDefinitionCompareDifferences.Maps ||
                (differences & IndexDefinitionCompareDifferences.Reduce) == IndexDefinitionCompareDifferences.Reduce)
                return IndexCreationOptions.Update;

            if ((differences & IndexDefinitionCompareDifferences.Fields) == IndexDefinitionCompareDifferences.Fields)
                return IndexCreationOptions.Update;

            if ((differences & IndexDefinitionCompareDifferences.AdditionalSources) == IndexDefinitionCompareDifferences.AdditionalSources)
                return IndexCreationOptions.Update;

            if ((differences & IndexDefinitionCompareDifferences.AdditionalAssemblies) == IndexDefinitionCompareDifferences.AdditionalAssemblies)
                return IndexCreationOptions.Update;

            if ((differences & IndexDefinitionCompareDifferences.Configuration) == IndexDefinitionCompareDifferences.Configuration)
            {
                var currentConfiguration = existingIndex.Configuration as SingleIndexConfiguration;
                if (currentConfiguration == null) // should not happen
                    return IndexCreationOptions.Update;

                var newConfiguration = new SingleIndexConfiguration(indexDef.Configuration, _documentDatabase.Configuration);
                var configurationResult = currentConfiguration.CalculateUpdateType(newConfiguration);
                switch (configurationResult)
                {
                    case IndexUpdateType.None:
                        break;

                    case IndexUpdateType.Refresh:
                        return IndexCreationOptions.UpdateWithoutUpdatingCompiledIndex;

                    case IndexUpdateType.Reset:
                        return IndexCreationOptions.Update;

                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            if ((differences & IndexDefinitionCompareDifferences.Priority) == IndexDefinitionCompareDifferences.Priority)
                return IndexCreationOptions.UpdateWithoutUpdatingCompiledIndex;

            if ((differences & IndexDefinitionCompareDifferences.LockMode) == IndexDefinitionCompareDifferences.LockMode)
                return IndexCreationOptions.UpdateWithoutUpdatingCompiledIndex;

            if ((differences & IndexDefinitionCompareDifferences.State) == IndexDefinitionCompareDifferences.State)
                return IndexCreationOptions.UpdateWithoutUpdatingCompiledIndex;

            if ((differences & IndexDefinitionCompareDifferences.DeploymentMode) == IndexDefinitionCompareDifferences.DeploymentMode)
                return IndexCreationOptions.UpdateWithoutUpdatingCompiledIndex;

            return IndexCreationOptions.Update;
        }

        public static bool IsValidIndexName(string name, bool isStatic, out string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                errorMessage = "Index name cannot be empty!";
                return false;
            }
            if (name.Contains("//"))
            {
                errorMessage = $"Index name '{name}' is not permitted. Index name cannot contain // (double slashes)";
                return false;
            }
            if (isStatic && name.StartsWith("Auto/", StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = $"Index name '{name}' is not permitted. Static index name cannot start with 'Auto/'";
                return false;
            }
            if (isStatic && ResourceNameValidator.IsValidIndexName(name) == false)
            {
                var allowedCharacters = $"('{string.Join("', '", ResourceNameValidator.AllowedIndexNameCharacters.Select(Regex.Unescape))}')";
                errorMessage = $"Index name '{name}' is not permitted. Only letters, digits and characters {allowedCharacters} are allowed.";
                return false;
            }
            if (isStatic && name.Contains(".") && ResourceNameValidator.IsDotCharSurroundedByOtherChars(name) == false)
            {
                errorMessage = $"Index name '{name}' is not permitted. If a name contains '.' character then it must be surrounded by other allowed characters.";
                return false;
            }
            if (isStatic && name.Length > MaxIndexNameLength)
            {
                errorMessage = $"Index name '{name}' is not permitted. Index name cannot exceed {MaxIndexNameLength} characters.";
                return false;
            }

            errorMessage = null;
            return true;
        }

        public Index ResetIndex(string name)
        {
            var index = GetIndex(name);
            if (index == null)
                IndexDoesNotExistException.ThrowFor(name);

            return ResetIndexInternal(index);
        }

        public async Task<bool> TryDeleteIndexIfExists(string name, string raftRequestId)
        {
            var index = GetIndex(name);
            if (index == null)
                return false;

            if (name.StartsWith(Constants.Documents.Indexing.SideBySideIndexNamePrefix))
            {
                await HandleSideBySideIndexDelete(name, raftRequestId);
                return true;
            }

            var (etag, _) = await _serverStore.SendToLeaderAsync(new DeleteIndexCommand(index.Name, _documentDatabase.Name, raftRequestId));

            await _documentDatabase.RachisLogIndexNotifications.WaitForIndexNotification(etag, _serverStore.Engine.OperationTimeout);

            return true;
        }

        private async Task HandleSideBySideIndexDelete(string name, string raftRequestId)
        {
            var originalIndexName = name.Remove(0, Constants.Documents.Indexing.SideBySideIndexNamePrefix.Length);
            var originalIndex = GetIndex(originalIndexName);
            if (originalIndex == null)
            {
                // we cannot find the original index
                // but we need to remove the side by side one by the original name
                var (etag, _) = await _serverStore.SendToLeaderAsync(new DeleteIndexCommand(originalIndexName, _documentDatabase.Name, raftRequestId));

                await _documentDatabase.RachisLogIndexNotifications.WaitForIndexNotification(etag, _serverStore.Engine.OperationTimeout);

                return;
            }

            // deleting the side by side index means that we need to save the original one in the database record

            var indexDefinition = originalIndex.GetIndexDefinition();
            indexDefinition.Name = originalIndexName;
            await CreateIndex(indexDefinition, raftRequestId);
        }

        public async Task DeleteIndex(string name, string raftRequestId)
        {
            var index = GetIndex(name);
            if (index == null)
                IndexDoesNotExistException.ThrowFor(name);

            var (newEtag, _) = await _serverStore.SendToLeaderAsync(new DeleteIndexCommand(index.Name, _documentDatabase.Name, raftRequestId));

            await _documentDatabase.RachisLogIndexNotifications.WaitForIndexNotification(newEtag, _serverStore.Engine.OperationTimeout);
        }

        internal void DeleteIndexInternal(Index index, bool raiseNotification = true)
        {
            _indexes.TryRemoveByName(index.Name, index);

            try
            {
                index.Dispose();
            }
            catch (Exception e)
            {
                if (Logger.IsInfoEnabled)
                    Logger.Info($"Could not dispose index '{index.Name}'.", e);
            }

            if (raiseNotification)
            {
                _documentDatabase.Changes.RaiseNotifications(new IndexChange
                {
                    Name = index.Name,
                    Type = IndexChangeTypes.IndexRemoved
                });
            }

            var name = IndexDefinitionBaseServerSide.GetIndexNameSafeForFileSystem(index.Name);

            var indexPath = index.Configuration.StoragePath.Combine(name);

            var indexTempPath = index.Configuration.TempPath?.Combine(name);

            if (index.Configuration.RunInMemory)
            {
                // when running in-memory all storage environment files are temporary so deleted on close but
                // we want to delete the empty directories as well
                
                // but when running a replacement index we cannot stop environment and move its directory so we leave it as is
                // we can have temp files of different environments in the same temp dir (when index is updated multiple times)
                // so let's delete the directory only if there are no other files inside - last dispose will delete the dir

                string replacementPrefix = Constants.Documents.Indexing.SideBySideIndexNamePrefix.Trim('/');

                if (Directory.Exists(indexPath.FullPath) && indexPath.FullPath.Contains(replacementPrefix))
                {
                    var tempPath = index._environment.Options.TempPath.FullPath;

                    if (Directory.Exists(tempPath) && Directory.EnumerateFiles(tempPath).Any())
                        return;
                }
            }

            try
            {
                IOExtensions.DeleteDirectory(indexPath.FullPath);
            }
            catch (Exception e)
            {
                if (Logger.IsInfoEnabled)
                    Logger.Info($"Failed to delete the index {name} directory", e);
                throw;
            }

            if (indexTempPath != null)
                IOExtensions.DeleteDirectory(indexTempPath.FullPath);
        }

        public IndexRunningStatus Status
        {
            get
            {
                if (_documentDatabase.Configuration.Indexing.Disabled)
                    return IndexRunningStatus.Disabled;

                if (_run)
                    return IndexRunningStatus.Running;

                return IndexRunningStatus.Paused;
            }
        }

        public long Count => _indexes.Count;

        public void StartIndexing()
        {
            _run = true;

            StartIndexing(_indexes);
        }

        public void StartMapIndexes()
        {
            StartIndexing(_indexes.Where(x => x.Type.IsMap()));
        }

        public void StartMapReduceIndexes()
        {
            StartIndexing(_indexes.Where(x => x.Type.IsMapReduce()));
        }

        private void StartIndexing(IEnumerable<Index> indexes)
        {
            if (_documentDatabase.Configuration.Indexing.Disabled)
                return;

            ExecuteForIndexes(indexes, index =>
            {
                using (IndexLock(index.Name))
                {
                    try
                    {
                        index.Start();
                    }
                    catch (ObjectDisposedException)
                    {
                        // this can happen if we replaced or removed the index
                    }
                }
            });
        }

        public void StartIndex(string name)
        {
            var index = GetIndex(name);
            if (index == null)
                IndexDoesNotExistException.ThrowFor(name);

            using (IndexLock(index.Name))
            {
                index.Start();
            }
        }

        public void StopIndex(string name)
        {
            var index = GetIndex(name);
            if (index == null)
                IndexDoesNotExistException.ThrowFor(name);

            using (IndexLock(index.Name))
            {
                index.Stop(disableIndex: true);
            }

            _documentDatabase.Changes.RaiseNotifications(new IndexChange
            {
                Name = name,
                Type = IndexChangeTypes.IndexPaused
            });
        }

        private IDisposable IndexLock(string indexName)
        {
            var indexLock = GetIndexLock(indexName);
            indexLock.Wait(_documentDatabase.DatabaseShutdown);

            return new DisposableAction(() => indexLock.Release());
        }

        public void StopIndexing()
        {
            _run = false;

            StopIndexing(_indexes);
        }

        public void StopMapIndexes()
        {
            StopIndexing(_indexes.Where(x => x.Type.IsMap()));
        }

        public void StopMapReduceIndexes()
        {
            StopIndexing(_indexes.Where(x => x.Type.IsMapReduce()));
        }

        private void StopIndexing(IEnumerable<Index> indexes)
        {
            if (_documentDatabase.Configuration.Indexing.Disabled)
                return;

            var list = indexes.ToList();

            ExecuteForIndexes(list, index =>
            {
                using (IndexLock(index.Name))
                {
                    try
                    {
                        index.Stop(disableIndex: true);
                    }
                    catch (ObjectDisposedException)
                    {
                        // this can happen if we replaced or removed the index
                    }
                }
            });


            foreach (var index in list)
            {
                _documentDatabase.Changes.RaiseNotifications(new IndexChange
                {
                    Name = index.Name,
                    Type = IndexChangeTypes.IndexPaused
                });
            }
        }

        public SharedMultipleUseFlag IsDisposed = new SharedMultipleUseFlag(false);

        public void Dispose()
        {
            IsDisposed.Raise();

            var exceptionAggregator = new ExceptionAggregator(Logger, $"Could not dispose {nameof(IndexStore)}");

            // waiting for all the indexes that are currently being initialized to finish
            foreach (var indexLock in _indexLocks)
            {
                indexLock.Value.Wait();
            }

            ExecuteForIndexes(_indexes, index =>
            {
                if (index is FaultyInMemoryIndex)
                    return;

                exceptionAggregator.Execute(index.Dispose);
            });

            exceptionAggregator.Execute(() => SorterCompilationCache.Instance.Clear(_documentDatabase.Name));
            exceptionAggregator.Execute(() => AnalyzerCompilationCache.Instance.Clear(_documentDatabase.Name));

            exceptionAggregator.ThrowIfNeeded();
        }

        private Index ResetIndexInternal(Index index, bool start = true)
        {
            using (IndexLock(index.Name))
            {
                try
                {
                    DeleteIndexInternal(index);
                }
                catch (Exception toe)
                {
                    throw new IndexDeletionException($"Failed to reset index: {index.Name}.", toe);
                }

                try
                {
                    var definitionBase = index.Definition;
                    definitionBase.Reset();

                    if (definitionBase is FaultyAutoIndexDefinition faultyAutoIndexDefinition)
                        definitionBase = faultyAutoIndexDefinition.Definition;

                    if (definitionBase is AutoMapIndexDefinition)
                        index = AutoMapIndex.CreateNew((AutoMapIndexDefinition)definitionBase, _documentDatabase);
                    else if (definitionBase is AutoMapReduceIndexDefinition)
                        index = AutoMapReduceIndex.CreateNew((AutoMapReduceIndexDefinition)definitionBase, _documentDatabase);
                    else
                    {
                        var staticIndexDefinition = index.Definition.GetOrCreateIndexDefinitionInternal();
                        switch (staticIndexDefinition.SourceType)
                        {
                            case IndexSourceType.Documents:
                                switch (staticIndexDefinition.Type)
                                {
                                    case IndexType.Map:
                                    case IndexType.JavaScriptMap:
                                        index = MapIndex.CreateNew(staticIndexDefinition, _documentDatabase);
                                        break;

                                    case IndexType.MapReduce:
                                    case IndexType.JavaScriptMapReduce:
                                        index = MapReduceIndex.CreateNew<MapReduceIndex>(staticIndexDefinition, _documentDatabase, isIndexReset: true);
                                        break;

                                    default:
                                        throw new NotSupportedException($"Cannot create {staticIndexDefinition.Type} index from IndexDefinition");
                                }
                                break;

                            case IndexSourceType.Counters:
                                switch (staticIndexDefinition.Type)
                                {
                                    case IndexType.Map:
                                    case IndexType.JavaScriptMap:
                                        index = MapCountersIndex.CreateNew(staticIndexDefinition, _documentDatabase);
                                        break;

                                    case IndexType.MapReduce:
                                    case IndexType.JavaScriptMapReduce:
                                        index = MapReduceIndex.CreateNew<MapReduceCountersIndex>(staticIndexDefinition, _documentDatabase, isIndexReset: true);
                                        break;

                                    default:
                                        throw new NotSupportedException($"Cannot create {staticIndexDefinition.Type} index from IndexDefinition");
                                }
                                break;

                            case IndexSourceType.TimeSeries:
                                switch (staticIndexDefinition.Type)
                                {
                                    case IndexType.Map:
                                    case IndexType.JavaScriptMap:
                                        index = MapTimeSeriesIndex.CreateNew(staticIndexDefinition, _documentDatabase);
                                        break;

                                    case IndexType.MapReduce:
                                    case IndexType.JavaScriptMapReduce:
                                        index = MapReduceIndex.CreateNew<MapReduceTimeSeriesIndex>(staticIndexDefinition, _documentDatabase, isIndexReset: true);
                                        break;

                                    default:
                                        throw new NotSupportedException($"Cannot create {staticIndexDefinition.Type} index from IndexDefinition");
                                }
                                break;

                            default:
                                throw new ArgumentException($"Unknown index source type {staticIndexDefinition.SourceType} for index {staticIndexDefinition.Name}");
                        }
                    }

                    if (start)
                        StartIndex(index);

                    return index;
                }
                catch (TimeoutException toe)
                {
                    throw new IndexCreationException($"Failed to reset index: {index.Name}", toe);
                }
                catch (Exception e)
                {
                    throw new IndexCreationException($"Failed to reset index: {index.Name}", e);
                }
            }
        }

        private void OpenIndexesFromRecord(DatabaseRecord record, long raftIndex, Action<string> addToInitLog)
        {
            var path = _documentDatabase.Configuration.Indexing.StoragePath;

            if (Logger.IsInfoEnabled)
                Logger.Info("Starting to load indexes from record");

            List<Exception> exceptions = null;
            if (_documentDatabase.Configuration.Core.ThrowIfAnyIndexCannotBeOpened)
                exceptions = new List<Exception>();

            var totalSp = Stopwatch.StartNew();

            bool startIndex = false;
            switch (_documentDatabase.Configuration.Indexing.IndexStartupBehavior)
            {
                case IndexingConfiguration.IndexStartupBehaviorType.Default:
                case IndexingConfiguration.IndexStartupBehaviorType.Immediate:
                    startIndex = true;
                    break;
            }

            foreach (var kvp in record.Indexes)
            {
                if (_documentDatabase.DatabaseShutdown.IsCancellationRequested)
                    return;

                var name = kvp.Key;
                var definition = kvp.Value;

                var safeName = IndexDefinitionBaseServerSide.GetIndexNameSafeForFileSystem(definition.Name);
                var indexPath = path.Combine(safeName).FullPath;
                if (Directory.Exists(indexPath))
                {
                    var sp = Stopwatch.StartNew();

                    addToInitLog($"Initializing static index: `{name}`");
                    OpenIndex(path, indexPath, exceptions, name, startIndex, definition);

                    if (Logger.IsInfoEnabled)
                        Logger.Info($"Initialized static index: `{name}`, took: {sp.ElapsedMilliseconds:#,#;;0}ms");
                }
            }

            foreach (var kvp in record.AutoIndexes)
            {
                if (_documentDatabase.DatabaseShutdown.IsCancellationRequested)
                    return;

                var name = kvp.Key;
                var definition = kvp.Value;

                var safeName = IndexDefinitionBaseServerSide.GetIndexNameSafeForFileSystem(definition.Name);
                var indexPath = path.Combine(safeName).FullPath;
                if (Directory.Exists(indexPath))
                {
                    var sp = Stopwatch.StartNew();

                    addToInitLog($"Initializing auto index: `{name}`");
                    OpenIndex(path, indexPath, exceptions, name, startIndex, definition);

                    if (Logger.IsInfoEnabled)
                        Logger.Info($"Initialized auto index: `{name}`, took: {sp.ElapsedMilliseconds:#,#;;0}ms");
                }
            }

            ForTestingPurposesOnly().AfterIndexesOpen?.Invoke();

            switch (_documentDatabase.Configuration.Indexing.IndexStartupBehavior)
            {
                case IndexingConfiguration.IndexStartupBehaviorType.Delay:

                    ExecuteForIndexes(GetIndexes(), index =>
                    {
                        try
                        {
                            switch (index.Status)
                            {
                                case IndexRunningStatus.Paused:
                                    index.Start();
                                    break;
                            }
                        }
                        catch (Exception e)
                        {
                            addToInitLog($"Could not start index '{index.Name}': {e}");
                        }
                    });

                    break;
            }

            // loading the new indexes
            var startIndexSp = Stopwatch.StartNew();

            addToInitLog("Starting new indexes");
            startIndex = _documentDatabase.Configuration.Indexing.IndexStartupBehavior != IndexingConfiguration.IndexStartupBehaviorType.Pause;
            var startedIndexes = HandleDatabaseRecordChange(record, raftIndex, startIndex);

            addToInitLog($"Started {startedIndexes} new index{(startedIndexes > 1 ? "es" : string.Empty)}, took: {startIndexSp.ElapsedMilliseconds}ms");

            addToInitLog($"IndexStore initialization is completed, took: {totalSp.ElapsedMilliseconds:#,#;;0}ms");

            if (exceptions != null && exceptions.Count > 0)
                throw new AggregateException("Could not load some of the indexes", exceptions);
        }

        public void OpenFaultyIndex(Index index)
        {
            Debug.Assert(index is FaultyInMemoryIndex);

            var path = _documentDatabase.Configuration.Indexing.StoragePath;
            var safeName = IndexDefinitionBaseServerSide.GetIndexNameSafeForFileSystem(index.Name);
            var indexPath = path.Combine(safeName).FullPath;
            var exceptions = new List<Exception>();

            OpenIndex(path, indexPath, exceptions, index.Name, startIndex: true, index.GetIndexDefinition());

            if (exceptions.Count > 0)
            {
                // there will only one exception here
                throw exceptions.First();
            }

            _documentDatabase.Changes.RaiseNotifications(
                new IndexChange
                {
                    Name = index.Name,
                    Type = IndexChangeTypes.IndexAdded
                });
        }

        private void OpenIndex(PathSetting path, string indexPath, List<Exception> exceptions, string name, bool startIndex, IndexDefinitionBase indexDefinition)
        {
            Index index = null;

            try
            {
                index = Index.Open(indexPath, _documentDatabase, generateNewDatabaseId: false);

                var differences = IndexDefinitionCompareDifferences.None;

                if (indexDefinition is IndexDefinition def)
                {
                    if (def.LockMode != null && index.Definition.LockMode != def.LockMode)
                        differences |= IndexDefinitionCompareDifferences.LockMode;
                }
                if (indexDefinition.Priority != null && index.Definition.Priority != indexDefinition.Priority)
                    differences |= IndexDefinitionCompareDifferences.Priority;

                if (indexDefinition.State != null && index.Definition.State != indexDefinition.State)
                    differences |= IndexDefinitionCompareDifferences.State;

                index.Definition.ClusterState.LastStateIndex = (indexDefinition.ClusterState?.LastStateIndex ?? 0);

                if (differences != IndexDefinitionCompareDifferences.None)
                {
                    // database record has different lock mode / priority / state setting than persisted locally
                    UpdateIndexDefinition(indexDefinition, index, differences);
                }

                if (index.State == IndexState.Error)
                {
                    switch (_documentDatabase.Configuration.Indexing.ErrorIndexStartupBehavior)
                    {
                        case IndexingConfiguration.ErrorIndexStartupBehaviorType.Start:
                            index.SetState(IndexState.Normal);
                            break;

                        case IndexingConfiguration.ErrorIndexStartupBehaviorType.ResetAndStart:
                            index = ResetIndexInternal(index, startIndex);
                            startIndex = false; // reset will start the index if requested
                            break;
                    }
                }

                if (startIndex)
                    index.Start();

                if (Logger.IsInfoEnabled)
                    Logger.Info($"Started {index.Name} from {indexPath}");

                _indexes.Add(index);
            }
            catch (Exception e)
            {
                var alreadyFaulted = _indexes.TryGetByName(name, out var i) &&
                                     i is FaultyInMemoryIndex;

                index?.Dispose();
                exceptions?.Add(e);

                if (alreadyFaulted)
                    return;

                var configuration = new FaultyInMemoryIndexConfiguration(path, _documentDatabase.Configuration);

                var faultyIndex = (indexDefinition is AutoIndexDefinition)
                    ? new FaultyInMemoryIndex(e, name, configuration, CreateAutoDefinition((AutoIndexDefinition)indexDefinition, IndexDeploymentMode.Parallel))
                    : new FaultyInMemoryIndex(e, name, configuration, (IndexDefinition)indexDefinition);

                var message = $"Could not open index at '{indexPath}'. Created in-memory, fake instance: {faultyIndex.Name}";

                if (Logger.IsOperationsEnabled)
                    Logger.Operations(message, e);

                _indexes.Add(faultyIndex);

                switch (_documentDatabase.Configuration.Indexing.ErrorIndexStartupBehavior)
                {
                    case IndexingConfiguration.ErrorIndexStartupBehaviorType.ResetAndStart:
                        {
                            try
                            {
                                ResetIndexInternal(faultyIndex);
                                return;
                            }
                            catch (Exception ex)
                            {
                                if (Logger.IsOperationsEnabled)
                                    Logger.Operations($"Failed to reset and start faulty index '{faultyIndex.Name}' at '{indexPath}'", ex);

                                // make sure that if this fail, faulty index will be on the list of indexes
                                _indexes.Add(faultyIndex);
                            }
                        }
                        break;
                }

                _documentDatabase.NotificationCenter.Add(AlertRaised.Create(
                    _documentDatabase.Name,
                    "Indexes store initialization error",
                    message,
                    AlertType.IndexStore_IndexCouldNotBeOpened,
                    NotificationSeverity.Error,
                    key: faultyIndex.Name,
                    details: new ExceptionDetails(e)));
            }
        }

        public IEnumerable<Index> GetIndexesForCollection(string collection)
        {
            return _indexes.GetForCollection(collection);
        }

        public IEnumerable<Index> GetIndexes()
        {
            return _indexes;
        }

        public void RunIdleOperations(DatabaseCleanupMode mode = DatabaseCleanupMode.Regular)
        {
            var indexCleanupMode = IndexCleanup.None;
            switch (mode)
            {
                case DatabaseCleanupMode.Regular:
                    indexCleanupMode = IndexCleanup.Basic;
                    break;

                case DatabaseCleanupMode.Deep:
                    indexCleanupMode = IndexCleanup.All;
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
            }

            foreach (var index in _indexes)
            {
                var current = indexCleanupMode;
                if (index.NoQueryRecently())
                    current |= IndexCleanup.Readers;

                index.Cleanup(current);
            }

            long etag;
            using (_serverStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            using (context.OpenReadTransaction())
            using (_serverStore.Cluster.ReadRawDatabaseRecord(context, _documentDatabase.Name, out etag))
            {
            }

            AsyncHelpers.RunSync(() => RunIdleOperationsAsync(etag));
        }

        private async Task RunIdleOperationsAsync(long databaseRecordEtag)
        {
            await DeleteOrMergeSurpassedAutoIndexes(databaseRecordEtag, RaftIdGenerator.NewId());
        }

        private async Task DeleteOrMergeSurpassedAutoIndexes(long databaseRecordEtag, string raftRequestId)
        {
            if (_lastSurpassedAutoIndexesDatabaseRecordEtag >= databaseRecordEtag)
                return;

            var dynamicQueryToIndex = new DynamicQueryToIndexMatcher(this);

            var indexesToRemove = new HashSet<string>();
            var indexesToExtend = new Dictionary<string, DynamicQueryMapping>();

            foreach (var index in _indexes)
            {
                if (index.Type.IsAuto() == false)
                    continue;

                if (indexesToRemove.Contains(index.Name))
                    continue;

                var collection = index.Collections.First();

                var query = DynamicQueryMapping.Create(index);

                foreach (var indexToCheck in _indexes.GetForCollection(collection))
                {
                    if (index.Type != indexToCheck.Type)
                        continue;

                    if (index == indexToCheck)
                        continue;

                    if (indexesToRemove.Contains(indexToCheck.Name))
                        continue;

                    var definitionToCheck = (AutoIndexDefinitionBaseServerSide)indexToCheck.Definition;

                    var result = dynamicQueryToIndex.ConsiderUsageOfIndex(query, definitionToCheck);

                    if (result.MatchType == DynamicQueryMatchType.Complete || result.MatchType == DynamicQueryMatchType.CompleteButIdle)
                    {
                        var lastMappedEtagFor = index.GetLastMappedEtagFor(collection);
                        if (result.LastMappedEtag >= lastMappedEtagFor)
                        {
                            indexesToRemove.Add(index.Name);
                            indexesToExtend.Remove(index.Name);
                            break;
                        }
                    }

                    if (result.MatchType == DynamicQueryMatchType.Partial)
                    {
                        if (indexesToExtend.TryGetValue(index.Name, out var mapping) == false)
                            indexesToExtend[index.Name] = mapping = DynamicQueryMapping.Create(index);

                        mapping.ExtendMappingBasedOn(definitionToCheck);
                    }
                }
            }

            var moreWork = false;
            foreach (var kvp in indexesToExtend)
            {
                var definition = kvp.Value.CreateAutoIndexDefinition();

                if (string.Equals(definition.Name, kvp.Key, StringComparison.Ordinal))
                    continue;

                try
                {
                    await CreateIndex(definition, $"{raftRequestId}/{definition.Name}");
                    await TryDeleteIndexIfExists(kvp.Key, $"{raftRequestId}/{kvp.Key}");

                    moreWork = true;
                    break; // extending only one auto-index at a time
                }
                catch (Exception e)
                {
                    if (Logger.IsOperationsEnabled)
                        Logger.Operations($"Could not create extended index '{definition.Name}'.", e);

                    moreWork = true;
                }
            }

            foreach (var indexName in indexesToRemove)
            {
                try
                {
                    await TryDeleteIndexIfExists(indexName, $"{raftRequestId}/{indexName}");
                    if (Logger.IsInfoEnabled)
                        Logger.Info($"Deleted index '{indexName}' because it is surpassed.");
                }
                catch (Exception e)
                {
                    if (Logger.IsOperationsEnabled)
                        Logger.Operations($"Could not delete surpassed index '{indexName}'.", e);

                    moreWork = true;
                }
            }

            if (moreWork == false)
                _lastSurpassedAutoIndexesDatabaseRecordEtag = databaseRecordEtag;
        }

        private void InitializePath(PathSetting path)
        {
            if (Directory.Exists(path.FullPath) == false && _documentDatabase.Configuration.Indexing.RunInMemory == false)
                Directory.CreateDirectory(path.FullPath);
        }

        private void ValidateAnalyzers(IndexDefinition definition)
        {
            if (definition.Fields == null)
                return;

            foreach (var kvp in definition.Fields)
            {
                if (string.IsNullOrWhiteSpace(kvp.Value.Analyzer))
                    continue;

                try
                {
                    IndexingExtensions.GetAnalyzerType(kvp.Key, kvp.Value.Analyzer, _documentDatabase.Name);
                }
                catch (Exception e)
                {
                    throw new IndexCompilationException(e.Message, e);
                }
            }
        }

        public void ReplaceIndexes(string oldIndexName, string replacementIndexName, CancellationToken token)
        {
            var indexLock = GetIndexLock(oldIndexName);

            indexLock.Wait(token);

            try
            {
                if (_indexes.TryGetByName(replacementIndexName, out var newIndex) == false)
                {
                    return;
                }

                if (_indexes.TryGetByName(oldIndexName, out Index oldIndex))
                {
                    oldIndexName = oldIndex.Name;

                    if (oldIndex.Type.IsStatic() && newIndex.Type.IsStatic())
                    {
                        var oldIndexDefinition = oldIndex.GetIndexDefinition();
                        var newIndexDefinition = newIndex.Definition.GetOrCreateIndexDefinitionInternal();

                        if (newIndex.Definition.LockMode == IndexLockMode.Unlock &&
                            newIndexDefinition.LockMode.HasValue == false &&
                            oldIndexDefinition.LockMode.HasValue)
                            newIndex.SetLock(oldIndexDefinition.LockMode.Value);

                        if (newIndex.Definition.Priority == IndexPriority.Normal &&
                            newIndexDefinition.Priority.HasValue == false &&
                            oldIndexDefinition.Priority.HasValue)
                            newIndex.SetPriority(oldIndexDefinition.Priority.Value);

                        if (newIndex.Definition.State == IndexState.Normal &&
                            newIndexDefinition.State.HasValue == false &&
                            oldIndexDefinition.State.HasValue)
                            newIndex.SetState(oldIndexDefinition.State.Value);
                    }
                }

                _indexes.ReplaceIndex(oldIndexName, oldIndex, newIndex);

                while (true)
                {
                    try
                    {
                        ForTestingPurposes?.DuringIndexReplacement_AfterUpdatingCollectionOfIndexes?.Invoke();

                        using (newIndex.DrainRunningQueries()) // to ensure nobody will start index meanwhile if we stop it here
                        {
                            var needToStop = newIndex.Status == IndexRunningStatus.Running && PoolOfThreads.LongRunningWork.Current != newIndex._indexingThread;

                            if (needToStop)
                            {
                                // stop the indexing to allow renaming the index
                                // the write tx required to rename it might be hold by indexing thread
                                ExecuteIndexAction(() =>
                                {
                                    using (IndexLock(newIndex.Name))
                                    {
                                        newIndex.Stop(disableIndex: false);
                                    }
                                });
                            }

                            try
                            {
                                newIndex.Rename(oldIndexName);
                            }
                            finally
                            {
                                if (needToStop)
                                    ExecuteIndexAction(newIndex.Start);
                            }
                        }

                        newIndex.ResetIsSideBySideAfterReplacement();
                        break;
                    }
                    catch (Exception e)
                    {
                        if (Logger.IsOperationsEnabled)
                            Logger.Operations($"Failed to rename index '{replacementIndexName}' to '{oldIndexName}' during replacement. Retrying ... ", e);

                        if (_documentDatabase.DatabaseShutdown.IsCancellationRequested)
                            throw; // nothing we can do here

                        Thread.Sleep(500);
                    }
                }

                if (oldIndex != null)
                {
                    while (true)
                    {
                        try
                        {
                            ForTestingPurposes?.DuringIndexReplacement_OnOldIndexDeletion?.Invoke();

                            using (oldIndex.DrainRunningQueries())
                                DeleteIndexInternal(oldIndex, raiseNotification: false);

                            break;
                        }
                        catch (TimeoutException)
                        {
                        }
                        catch (IOException)
                        {
                            // we do not want to try again, letting index to continue running

                            try
                            {
                                // try to dispose old index
                                oldIndex.Dispose();
                            }
                            catch (Exception e)
                            {
                                if (Logger.IsOperationsEnabled)
                                    Logger.Operations($"Failed to dispose old index '{oldIndexName}' on its deletion during replacement.", e);
                            }

                            throw;
                        }
                        catch (Exception e)
                        {
                            if (Logger.IsOperationsEnabled)
                                Logger.Operations($"Failed to delete old index '{oldIndexName}' during replacement. Retrying ... ", e);

                            if (_documentDatabase.DatabaseShutdown.IsCancellationRequested)
                                throw; // nothing we can do here

                            Thread.Sleep(500);
                        }
                    }
                }

                if (newIndex.Configuration.RunInMemory == false)
                {
                    while (true)
                    {
                        try
                        {
                            using (newIndex.DrainRunningQueries())
                            {
                                var oldIndexDirectoryName = IndexDefinitionBaseServerSide.GetIndexNameSafeForFileSystem(oldIndexName);
                                var replacementIndexDirectoryName = IndexDefinitionBaseServerSide.GetIndexNameSafeForFileSystem(replacementIndexName);

                                using (newIndex.RestartEnvironment())
                                {
                                    IOExtensions.MoveDirectory(newIndex.Configuration.StoragePath.Combine(replacementIndexDirectoryName).FullPath,
                                        newIndex.Configuration.StoragePath.Combine(oldIndexDirectoryName).FullPath);

                                    if (newIndex.Configuration.TempPath != null)
                                    {
                                        IOExtensions.MoveDirectory(newIndex.Configuration.TempPath.Combine(replacementIndexDirectoryName).FullPath,
                                            newIndex.Configuration.TempPath.Combine(oldIndexDirectoryName).FullPath);
                                    }
                                }
                            }

                            break;
                        }
                        catch (TimeoutException)
                        {
                        }
                        catch (IOException)
                        {
                            // we do not want to try again, letting index to continue running
                            throw;
                        }
                        catch (Exception e)
                        {
                            if (Logger.IsOperationsEnabled)
                                Logger.Operations($"Failed to move directory of replacements index '{newIndex.Name}' during replacement. Retrying ... ", e);

                            if (_documentDatabase.DatabaseShutdown.IsCancellationRequested)
                                throw; // nothing we can do here

                            Thread.Sleep(500);
                        }
                    }
                }

                _documentDatabase.Changes.RaiseNotifications(
                    new IndexChange { Name = oldIndexName, Type = IndexChangeTypes.SideBySideReplace });
            }
            finally
            {
                indexLock.Release();
            }
        }

        private SemaphoreSlim GetIndexLock(string name)
        {
            return _indexLocks.GetOrAdd(name, n => new SemaphoreSlim(1, 1));
        }

        private void ExecuteIndexAction(Action action)
        {
            while (_documentDatabase.DatabaseShutdown.IsCancellationRequested == false)
            {
                try
                {
                    action();
                    break;
                }
                catch (TimeoutException)
                {
                }
                catch (Exception e)
                {
                    if (Logger.IsOperationsEnabled)
                        Logger.Operations("Error during index replacement in the new index stop before renaming", e);

                    throw;
                }
            }
        }

        public async Task SetLock(string name, IndexLockMode mode, string raftRequestId)
        {
            var index = GetIndex(name);
            if (index == null)
                IndexDoesNotExistException.ThrowFor(name);

            if (index.Type == IndexType.Faulty || index.Type.IsAuto())
            {
                index.SetLock(mode);  // this will throw proper exception
                return;
            }

            var command = new SetIndexLockCommand(name, mode, _documentDatabase.Name, raftRequestId);

            var (etag, _) = await _serverStore.SendToLeaderAsync(command);

            await _documentDatabase.RachisLogIndexNotifications.WaitForIndexNotification(etag, _serverStore.Engine.OperationTimeout);
        }

        public async Task SetPriority(string name, IndexPriority priority, string raftRequestId)
        {
            var index = GetIndex(name);
            if (index == null)
                IndexDoesNotExistException.ThrowFor(name);

            var faultyInMemoryIndex = index as FaultyInMemoryIndex;
            if (faultyInMemoryIndex != null)
            {
                faultyInMemoryIndex.SetPriority(priority); // this will throw proper exception
                return;
            }

            var command = new SetIndexPriorityCommand(name, priority, _documentDatabase.Name, raftRequestId);

            var (etag, _) = await _serverStore.SendToLeaderAsync(command);

            await _documentDatabase.RachisLogIndexNotifications.WaitForIndexNotification(etag, _serverStore.Engine.OperationTimeout);
        }

        public async Task SetState(string name, IndexState state, string raftRequestId)
        {
            var index = GetIndex(name);
            if (index == null)
                IndexDoesNotExistException.ThrowFor(name);

            var faultyInMemoryIndex = index as FaultyInMemoryIndex;
            if (faultyInMemoryIndex != null)
            {
                faultyInMemoryIndex.SetState(state); // this will throw proper exception
                return;
            }

            var command = new SetIndexStateCommand(name, state, _documentDatabase.Name, raftRequestId);

            var (etag, _) = await _serverStore.SendToLeaderAsync(command);

            await _documentDatabase.RachisLogIndexNotifications.WaitForIndexNotification(etag, _serverStore.Engine.OperationTimeout);
        }

        public IndexMergeResults ProposeIndexMergeSuggestions()
        {
            var dic = new Dictionary<string, IndexDefinition>();

            foreach (var index in GetIndexes())
            {
                dic[index.Name] = index.GetIndexDefinition();
            }

            var indexMerger = new IndexMerger(dic);

            return indexMerger.ProposeIndexMergeSuggestions();
        }

        private void ThrowIndexCreationException(string indexType, string indexName, Exception exception, string reason)
        {
            throw new IndexCreationException($"Failed to create {indexType} index '{indexName}', {reason}. Node {_serverStore.NodeTag} state is {_serverStore.LastStateChangeReason()}", exception);
        }

        public class IndexBatchScope
        {
            private readonly IndexStore _store;
            private readonly int _numberOfUtilizedCores;

            private PutIndexesCommand _command;
            private readonly IndexDeploymentMode _defaultAutoDeploymentMode;
            private readonly IndexDeploymentMode _defaultStaticDeploymentMode;

            public IndexBatchScope(IndexStore store, int numberOfUtilizedCores)
            {
                _store = store;
                _numberOfUtilizedCores = numberOfUtilizedCores;
                _defaultAutoDeploymentMode = store._documentDatabase.Configuration.Indexing.AutoIndexDeploymentMode;
                _defaultStaticDeploymentMode = store._documentDatabase.Configuration.Indexing.StaticIndexDeploymentMode;
            }

            public void AddIndex(IndexDefinitionBaseServerSide definition, string source, DateTime createdAt, string raftRequestId, int revisionsToKeep)
            {
                if (_command == null)
                    _command = new PutIndexesCommand(_store._documentDatabase.Name, source, createdAt, raftRequestId, revisionsToKeep, _defaultAutoDeploymentMode, _defaultStaticDeploymentMode);

                if (definition == null)
                    throw new ArgumentNullException(nameof(definition));

                if (definition is MapIndexDefinition indexDefinition)
                {
                    AddIndex(indexDefinition.IndexDefinition, source, createdAt, raftRequestId, revisionsToKeep);
                    return;
                }

                _store.ValidateAutoIndex(definition);

                var autoDefinition = (AutoIndexDefinitionBaseServerSide)definition;
                var indexType = PutAutoIndexCommand.GetAutoIndexType(autoDefinition);

                _command.Auto.Add(PutAutoIndexCommand.GetAutoIndexDefinition(autoDefinition, indexType));
            }

            public void AddIndex(IndexDefinition definition, string source, DateTime createdAt, string raftRequestId, int revisionsToKeep)
            {
                if (_command == null)
                    _command = new PutIndexesCommand(_store._documentDatabase.Name, source, createdAt, raftRequestId, revisionsToKeep, _defaultAutoDeploymentMode, _defaultStaticDeploymentMode);

                _store.ValidateStaticIndex(definition);

                _command.Static.Add(definition);
            }

            public async Task SaveIfNeeded()
            {
                if (_command == null)
                    return;

                if (_command.Static.Count + _command.Auto.Count > 50)
                {
                    await SaveAsync();
                }
            }

            public async Task SaveAsync()
            {
                if (_command == null || _command.Static.Count == 0 && _command.Auto.Count == 0)
                    return;

                try
                {
                    long index = 0;
                    try
                    {
                        index = (await _store._serverStore.SendToLeaderAsync(_command)).Index;
                    }
                    catch (Exception e)
                    {
                        ThrowIndexCreationException(e, "Cluster is probably down.");
                    }

                    var indexCount = _command.Static.Count + _command.Auto.Count;
                    var operationTimeout = _store._serverStore.Engine.OperationTimeout;
                    var timeout = TimeSpan.FromSeconds(((double)indexCount / _numberOfUtilizedCores) * operationTimeout.TotalSeconds);
                    if (operationTimeout > timeout)
                        timeout = operationTimeout;

                    try
                    {
                        await _store._documentDatabase.RachisLogIndexNotifications.WaitForIndexNotification(index, timeout);
                    }
                    catch (TimeoutException toe)
                    {
                        ThrowIndexCreationException(toe, $"Operation timed out after: {timeout}.");
                    }
                }
                finally
                {
                    _command = null;
                }
            }

            private void ThrowIndexCreationException(Exception exception, string reason)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Failed to create indexes. {reason}");
                if (_command.Static != null && _command.Static.Count > 0)
                    sb.AppendLine("Static: " + string.Join(", ", _command.Static.Select(x => x.Name)));

                if (_command.Auto != null && _command.Auto.Count > 0)
                    sb.AppendLine("Auto: " + string.Join(", ", _command.Auto.Select(x => x.Name)));

                sb.AppendLine($"Node {_store._serverStore.NodeTag} state is {_store._serverStore.LastStateChangeReason()}");

                throw new IndexCreationException(sb.ToString(), exception);
            }
        }

        internal TestingStuff ForTestingPurposes;

        internal TestingStuff ForTestingPurposesOnly()
        {
            if (ForTestingPurposes != null)
                return ForTestingPurposes;

            return ForTestingPurposes = new TestingStuff(this);
        }

        internal class TestingStuff
        {
            private readonly IndexStore _parent;
            internal Action DuringIndexReplacement_AfterUpdatingCollectionOfIndexes;
            internal Action DuringIndexReplacement_OnOldIndexDeletion;
            internal Action AfterIndexesOpen;
            internal Action<string> AfterIndexCreation;
            internal Action IndexCompaction;

            internal Action<Index> OnRollingIndexFinished;
            internal Action<Index> BeforeRollingIndexFinished;
            internal Action<Index> OnRollingIndexStart;
            internal Action<Index> BeforeRollingIndexStart;

            internal Action<Index> BeforeIndexThreadExit;
            internal Action<Index> BeforeIndexStart;

            public TestingStuff(IndexStore parent)
            {
                _parent = parent;
            }

            public void RunFakeIndex(Index index)
            {
                // create the SemaphoreSlim
                _parent.GetIndexLock(index.Name);

                _parent.StartIndex(index);
            }
        }
    }

    [Flags]
    public enum IndexCleanup
    {
        None = 0,
        Basic = 1,
        Readers = 2,
        Writers = 4,
        All = Basic | Readers | Writers
    }
}
