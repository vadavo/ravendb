﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Http;
using Raven.Server.Documents.Sharding.Operations;
using Raven.Server.ServerWide;
using Sparrow.Json;

namespace Raven.Server.Documents.Sharding.Executors;

public abstract class AbstractExecutor
{
    private Dictionary<int, Exception> _exceptions;

    protected AbstractExecutor(ServerStore store)
    {
        store.Server.ServerCertificateChanged += OnCertificateChange;
    }

    protected abstract RequestExecutor GetRequestExecutorAt(int position);
    protected abstract Memory<int> GetAllPositions();

    protected abstract void OnCertificateChange(object sender, EventArgs e);

    public Task<TResult> ExecuteOneByOneForAllAsync<TResult>(IShardedOperation<TResult> operation)
        => ExecuteForShardsAsync<OneByOneExecution, ThrowOnFailure, TResult>(GetAllPositions(), operation);

    public Task<TCombinedResult> ExecuteParallelForAllAsync<TResult, TCombinedResult>(IShardedOperation<TResult, TCombinedResult> operation, CancellationToken token = default)
        => ExecuteForShardsAsync<ParallelExecution, ThrowOnFailure, TResult, TCombinedResult>(GetAllPositions(), operation, token);

    public Task<TResult> ExecuteParallelForAllAsync<TResult>(IShardedOperation<TResult> operation, CancellationToken token = default)
        => ExecuteForShardsAsync<ParallelExecution, ThrowOnFailure, TResult>(GetAllPositions(), operation, token);

    public Task<TResult> ExecuteForAllAsync<TExecutionMode, TFailureMode, TResult>(IShardedOperation<TResult> operation, CancellationToken token = default)
        where TExecutionMode : struct, IExecutionMode
        where TFailureMode : struct, IFailureMode
        => ExecuteForShardsAsync<TExecutionMode, TFailureMode, TResult>(GetAllPositions(), operation, token);

    protected Task<TResult> ExecuteForShardsAsync<TExecutionMode, TFailureMode, TResult>(Memory<int> shards, IShardedOperation<TResult, TResult> operation, CancellationToken token = default)
        where TExecutionMode : struct, IExecutionMode
        where TFailureMode : struct, IFailureMode
        => ExecuteForShardsAsync<TExecutionMode, TFailureMode, TResult, TResult>(shards, operation, token);

    public Task ExecuteParallelForShardsAsync(Memory<int> shards,
        IShardedOperation operation, CancellationToken token = default)
        => ExecuteForShardsAsync<ParallelExecution, ThrowOnFailure, object, object>(shards, operation, token);

    public Task<TResult> ExecuteParallelForShardsAsync<TResult>(Memory<int> shards,
        IShardedOperation<TResult> operation, CancellationToken token = default)
        => ExecuteForShardsAsync<ParallelExecution, ThrowOnFailure, TResult, TResult>(shards, operation, token);

    protected async Task<TCombinedResult> ExecuteForShardsAsync<TExecutionMode, TFailureMode, TResult, TCombinedResult>(Memory<int> shards,
        IShardedOperation<TResult, TCombinedResult> operation, CancellationToken token)
        where TExecutionMode : struct, IExecutionMode
        where TFailureMode : struct, IFailureMode
    {
        int position = 0;
        var commands = ArrayPool<CommandHolder<TResult>>.Shared.Rent(shards.Length);
        try
        {
            position = await ExecuteAsync<TExecutionMode, TFailureMode, TResult, TCombinedResult>(shards, operation, commands, token);

            if (operation is IShardedOperation)
                return default;

            return BuildResults(operation, position, commands);
        }
        finally
        {
            for (var index = 0; index < position; index++)
            {
                var command = commands[index];
                try
                {
                    command.ContextReleaser?.Dispose();
                    command.ContextReleaser = null; // we set it to null, since we pool it and might get old values if not cleared
                }
                catch
                {
                    // ignore
                }
            }

            ArrayPool<CommandHolder<TResult>>.Shared.Return(commands);
        }
    }

    private static TCombinedResult BuildResults<TResult, TCombinedResult>(
        IShardedOperation<TResult, TCombinedResult> operation,
        int position,
        CommandHolder<TResult>[] commands)
    {
        TCombinedResult result;
        TResult[] resultsArray = null;
        RavenCommand<TResult>[] cmdResultsArray = null;
        try
        {
            resultsArray = ArrayPool<TResult>.Shared.Rent(position);
            cmdResultsArray = ArrayPool<RavenCommand<TResult>>.Shared.Rent(position);

            for (int i = 0; i < position; i++)
            {
                cmdResultsArray[i] = commands[i].Command;
            }

            var commandsMemory = new Memory<RavenCommand<TResult>>(cmdResultsArray, 0, position);
            var resultsMemory = new Memory<TResult>(resultsArray, 0, position);
            result = operation.CombineCommands(commandsMemory, resultsMemory);

            if (typeof(TCombinedResult) == typeof(BlittableJsonReaderObject))
            {
                if (result == null)
                    return default;

                var blittable = result as BlittableJsonReaderObject;
                return (TCombinedResult)(object)blittable.Clone(operation.CreateOperationContext());
            }

            return result;
        }
        finally
        {
            if (resultsArray != null)
                ArrayPool<TResult>.Shared.Return(resultsArray);

            if (cmdResultsArray != null)
                ArrayPool<RavenCommand<TResult>>.Shared.Return(cmdResultsArray);
        }
    }

    private async Task<int> ExecuteAsync<TExecutionMode, TFailureMode, TResult, TCombinedResult>(
        Memory<int> shards,
        IShardedOperation<TResult, TCombinedResult> operation,
        CommandHolder<TResult>[] commands,
        CancellationToken token)

        where TExecutionMode : struct, IExecutionMode
        where TFailureMode : struct, IFailureMode
    {
        int position;
        for (position = 0; position < shards.Span.Length; position++)
        {
            int shard = shards.Span[position];

            var cmd = operation.CreateCommandForShard(shard);
            commands[position].Shard = shard;
            commands[position].Command = cmd;

            var executor = GetRequestExecutorAt(shard);
            var release = executor.ContextPool.AllocateOperationContext(out JsonOperationContext ctx);
            commands[position].ContextReleaser = release;

            var t = executor.ExecuteAsync(cmd, ctx, token: token);
            commands[position].Task = t;

            if (typeof(TExecutionMode) == typeof(OneByOneExecution))
            {
                try
                {
                    await t;
                }
                catch
                {
                    if (typeof(TFailureMode) == typeof(ThrowOnFailure))
                        throw;
                }
            }
        }
        
        for (var i = 0; i < position; i++)
        {
            var holder = commands[i];
            try
            {
                await holder.Task;
            }
            catch (Exception e)
            {
                if (typeof(TFailureMode) == typeof(ThrowOnFailure))
                    throw;

                _exceptions ??= new Dictionary<int, Exception>();
                _exceptions[holder.Shard] = e;
            }
        }

        return position;
    }

    public struct CommandHolder<T>
    {
        public int Shard;
        public RavenCommand<T> Command;
        public Task Task;
        public IDisposable ContextReleaser;
    }
}

public interface IExecutionMode
{

}

public interface IFailureMode
{

}

public struct ParallelExecution : IExecutionMode
{

}

public struct OneByOneExecution : IExecutionMode
{

}

public struct ThrowOnFailure : IFailureMode
{

}

public struct IgnoreFailure : IFailureMode
{

}
