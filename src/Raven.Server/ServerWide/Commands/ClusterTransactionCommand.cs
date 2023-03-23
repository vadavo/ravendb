﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raven.Client;
using Raven.Client.Documents.Commands.Batches;
using Raven.Client.ServerWide;
using Raven.Client.Util;
using Raven.Server.Documents.Handlers;
using Raven.Server.Documents.PeriodicBackup;
using Raven.Server.Json;
using Raven.Server.Rachis;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Sparrow.Binary;
using Sparrow.Extensions;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Sparrow.Server;
using Voron;
using Voron.Data.Tables;
using static Raven.Client.Exceptions.ClusterTransactionConcurrencyException;

namespace Raven.Server.ServerWide.Commands
{
    public class ClusterTransactionCommand : CommandBase
    {
        public string DatabaseName;

        public string DatabaseRecordId;
        public string ClusterTransactionId;

        public long DatabaseCommandsCount;
        //We take the current ticks in advance to ensure consistent results of the command execution on all nodes
        public long CommandCreationTicks = long.MinValue;

        public class ClusterTransactionDataCommand
        {
            public CommandType Type;
            public string Id;
            public BlittableJsonReaderObject Document;
            public string ChangeVector;
            public long Index;
            public BackupKind? FromBackup;
            public string Error;

            public static ClusterTransactionDataCommand FromCommandData(BatchRequestParser.CommandData command)
            {
                if (command.IdPrefixed)
                {
                    throw new RachisApplyException("Deleting by prefix is not supported on cluster transaction");
                }

                return new ClusterTransactionDataCommand
                {
                    Id = command.Id,
                    ChangeVector = command.OriginalChangeVector ?? command.ChangeVector,
                    Document = command.Document,
                    Index = command.Index,
                    Type = command.Type,
                    FromBackup = null
                };
            }

            public DynamicJsonValue ToJson(JsonOperationContext context)
            {
                var djv = new DynamicJsonValue
                {
                    [nameof(Type)] = Type,
                    [nameof(Id)] = Id,
                    [nameof(Index)] = Index,
                    [nameof(ChangeVector)] = ChangeVector,
                    [nameof(Document)] = Document?.Clone(context),
                    [nameof(Error)] = Error
                };

                if (FromBackup.HasValue)
                    djv[nameof(FromBackup)] = FromBackup.Value;

                return djv;
            }
        }

        public class ClusterTransactionErrorInfo : IDynamicJsonValueConvertible
        {
            public string Message;
            public ConcurrencyViolation Violation;

            public DynamicJsonValue ToJson()
            {
                return new DynamicJsonValue
                {
                    [nameof(Message)] = Message,
                    [nameof(Violation)] = Violation.ToJson()
                };
            }
        }

        public class ClusterTransactionOptions : IDynamicJson
        {
            public string TaskId;
            public TimeSpan? WaitForIndexesTimeout;
            public bool WaitForIndexThrow;
            public List<string> SpecifiedIndexesQueryString;
            public bool? DisableAtomicDocumentWrites;

            public ClusterTransactionOptions() { }

            public ClusterTransactionOptions(string taskId, bool disableAtomicDocumentWrites, int clusterMinVersion)
            {
                TaskId = taskId;
                DisableAtomicDocumentWrites = disableAtomicDocumentWrites || clusterMinVersion < 52_000; // for mixed cluster, retain the old behaviour
            }

            public DynamicJsonValue ToJson()
            {
                return new DynamicJsonValue
                {
                    [nameof(TaskId)] = TaskId,
                    [nameof(WaitForIndexesTimeout)] = WaitForIndexesTimeout,
                    [nameof(WaitForIndexThrow)] = WaitForIndexThrow,
                    [nameof(SpecifiedIndexesQueryString)] = SpecifiedIndexesQueryString != null ? new DynamicJsonArray(SpecifiedIndexesQueryString) : null,
                    [nameof(DisableAtomicDocumentWrites)] = DisableAtomicDocumentWrites
                };
            }
        }

        public List<ClusterTransactionDataCommand> ClusterCommands = new List<ClusterTransactionDataCommand>();
        public BlittableJsonReaderObject SerializedDatabaseCommands;

        [JsonDeserializationIgnore]
        public ClusterTransactionOptions Options;

        [JsonDeserializationIgnore]
        public readonly List<ClusterTransactionDataCommand> DatabaseCommands = new List<ClusterTransactionDataCommand>();

        public bool FromBackup;

        public ClusterTransactionCommand() { }

        public ClusterTransactionCommand(
            string databaseName,
            char identityPartsSeparator,
            DatabaseTopology topology,
            ArraySegment<BatchRequestParser.CommandData> commandParsedCommands,
            ClusterTransactionOptions options, string uniqueRequestId) : this(databaseName, identityPartsSeparator, topology, options, uniqueRequestId)
        {
            foreach (var commandData in commandParsedCommands)
            {
                var command = ClusterTransactionDataCommand.FromCommandData(commandData);
                ClusterCommandValidation(command, identityPartsSeparator);
                switch (command.Type)
                {
                    case CommandType.PUT:
                    case CommandType.DELETE:
                        DatabaseCommands.Add(command);
                        break;
                    case CommandType.CompareExchangePUT:
                    case CommandType.CompareExchangeDELETE:
                        ClusterCommands.Add(command);
                        break;
                    default:
                        throw new RachisApplyException($"The type '{commandData.Type}' is not supported in '{nameof(ClusterTransactionCommand)}.'");
                }
            }

            DatabaseCommandsCount = DatabaseCommands.Count;
        }

        public ClusterTransactionCommand(
            string databaseName,
            char identityPartsSeparator,
            DatabaseTopology topology,
            ArraySegment<ClusterTransactionDataCommand> commandParsedCommands,
            ClusterTransactionOptions options,
            string uniqueRequestId) : this(databaseName, identityPartsSeparator, topology, options, uniqueRequestId)
        {
            foreach (var command in commandParsedCommands)
            {
                ClusterCommandValidation(command, identityPartsSeparator);
                switch (command.Type)
                {
                    case CommandType.PUT:
                    case CommandType.DELETE:
                        DatabaseCommands.Add(command);
                        break;
                    case CommandType.CompareExchangePUT:
                    case CommandType.CompareExchangeDELETE:
                        ClusterCommands.Add(command);
                        break;
                    default:
                        throw new RachisApplyException($"The type '{command.Type}' is not supported in '{nameof(ClusterTransactionCommand)}.'");
                }
            }

            DatabaseCommandsCount = DatabaseCommands.Count;
        }

        private ClusterTransactionCommand(
            string databaseName,
            char identityPartsSeparator,
            DatabaseTopology topology,
            ClusterTransactionOptions options,
            string uniqueRequestId) : base(uniqueRequestId)
        {
            DatabaseName = databaseName;
            DatabaseRecordId = topology.DatabaseTopologyIdBase64 ?? Guid.NewGuid().ToBase64Unpadded();
            ClusterTransactionId = topology.ClusterTransactionIdBase64 ?? Guid.NewGuid().ToBase64Unpadded();
            Options = options;
            CommandCreationTicks = SystemTime.UtcNow.Ticks;
        }

        private static void ClusterCommandValidation(ClusterTransactionDataCommand command, char identityPartsSeparator)
        {
            if (string.IsNullOrWhiteSpace(command.Id))
                throw new RachisApplyException($"In {nameof(ClusterTransactionDataCommand)} document id cannot be null, empty or white spaces as part of cluster transaction. " +
                                               $"{nameof(command.Type)}:({command.Type}), {nameof(command.Index)}:({command.Index})");

            var lastChar = command.Id[^1];
            if (lastChar == identityPartsSeparator || lastChar == '|')
                throw new RachisApplyException($"Document id {command.Id} cannot end with '|' or '{identityPartsSeparator}' as part of cluster transaction");
        }

        public List<ClusterTransactionErrorInfo> ExecuteCompareExchangeCommands(DatabaseTopology dbTopology, ClusterOperationContext context, long index, Table items)
        {
            if (Options?.DisableAtomicDocumentWrites == false)
                EnsureAtomicDocumentWrites(dbTopology, context, items, index);

            if (ClusterCommands == null || ClusterCommands.Count == 0)
                return null;

            var toExecute = new List<CompareExchangeCommandBase>(ClusterCommands.Count);
            var errors = new List<ClusterTransactionErrorInfo>();
            foreach (var clusterCommand in ClusterCommands)
            {
                long current;
                switch (clusterCommand.Type)
                {
                    case CommandType.CompareExchangePUT:
                        var put = new AddOrUpdateCompareExchangeCommand(DatabaseName, clusterCommand.Id, clusterCommand.Document, clusterCommand.Index, context, null);
                        put.CurrentTicks = CommandCreationTicks;
                        if (put.Validate(context, items, clusterCommand.Index, out current) == false)
                        {
                            errors.Add(GenerateErrorInfo(clusterCommand, current));
                            continue;
                        }
                        toExecute.Add(put);
                        break;
                    case CommandType.CompareExchangeDELETE:
                        var delete = new RemoveCompareExchangeCommand(DatabaseName, clusterCommand.Id, clusterCommand.Index, context, null);
                        if (delete.Validate(context, items, clusterCommand.Index, out current) == false)
                        {
                            errors.Add(GenerateErrorInfo(clusterCommand, current, delete: true));
                            continue;
                        }
                        toExecute.Add(delete);
                        break;
                    default:
                        throw new RachisApplyException(
                            $"Invalid cluster command detected: {clusterCommand.Type}! Only " +
                            "CompareExchangePUT and CompareExchangeDELETE are supported.");
                }
            }

            if (errors.Count > 0)
            {
                return errors;
            }

            foreach (var command in toExecute)
            {
                command.Execute(context, items, index);
            }

            return null;
        }

        private static ClusterTransactionErrorInfo GenerateErrorInfo(ClusterTransactionDataCommand clusterCommand, long actualIndex, bool delete = false)
        {
            var msg = $"Concurrency check failed for {(delete ? "deleting" : "putting")} the key '{clusterCommand.Id}'. " +
                      $"Requested index: {clusterCommand.Index}, actual index: {actualIndex}";

            var type = ViolationOnType.CompareExchange;

            if (clusterCommand.Error != null)
            {
                msg = $"{clusterCommand.Error}{Environment.NewLine}{msg}";
                type = ViolationOnType.Document;
            }

            return new ClusterTransactionErrorInfo
            {
                Message = msg,
                Violation = new ConcurrencyViolation
                {
                    Id = clusterCommand.Id,
                    Actual = actualIndex,
                    Expected = clusterCommand.Index,
                    Type = type
                }
            };
        }

        private void EnsureAtomicDocumentWrites(DatabaseTopology dbTopology, ClusterOperationContext context, Table items, long index)
        {
            if (SerializedDatabaseCommands == null)
                return;

            if (SerializedDatabaseCommands.TryGet(nameof(DatabaseCommands), out BlittableJsonReaderArray commands) == false)
                return;

            ClusterCommands ??= new List<ClusterTransactionDataCommand>();
            foreach (BlittableJsonReaderObject dbCmd in commands)
            {
                var cmdType = dbCmd[nameof(ClusterTransactionDataCommand.Type)].ToString();
                var docId = dbCmd[nameof(ClusterTransactionDataCommand.Id)].ToString();
                var atomicGuardKey = GetAtomicGuardKey(docId);
                var changeVector = dbCmd[nameof(ClusterTransactionDataCommand.ChangeVector)]?.ToString();
                long changeVectorIndex = 0;

                if (changeVector != null)
                    changeVectorIndex = ChangeVectorUtils.GetEtagById(changeVector, dbTopology.ClusterTransactionIdBase64);

                if (FromBackup)
                    changeVectorIndex = GetCurrentIndex(context, items, atomicGuardKey) ?? 0;

                var clusterTransactionDataCommand = new ClusterTransactionDataCommand
                {
                    Id = atomicGuardKey,
                    Index = changeVectorIndex,
                    Error = $"Guard compare exchange value '{atomicGuardKey}' index does not match the transaction index's {changeVectorIndex} change vector on {docId}"
                };

                switch (cmdType)
                {
                    case nameof(CommandType.PUT):
                        clusterTransactionDataCommand.Type = CommandType.CompareExchangePUT;

                        var dynamicJsonValue = new DynamicJsonValue
                        {
                            [Constants.CompareExchange.ObjectFieldName] = new DynamicJsonValue { ["Id"] = docId }
                        };

                        if (dbCmd.TryGet(nameof(ClusterTransactionDataCommand.Document), out BlittableJsonReaderObject document)
                            && document.TryGet(Constants.Documents.Metadata.Key, out BlittableJsonReaderObject metadata)
                            && metadata.TryGet(Constants.Documents.Metadata.Expires, out LazyStringValue expires))
                        {
                            dynamicJsonValue[Constants.Documents.Metadata.Key] = new DynamicJsonValue { [Constants.Documents.Metadata.Expires] = expires };
                        }

                        clusterTransactionDataCommand.Document = context.ReadObject(dynamicJsonValue, "cmp-xchg-content");
                        break;
                    case nameof(CommandType.DELETE):
                        if (changeVector == null)
                        {
                            var current = GetCurrentIndex(context, items, atomicGuardKey);
                            if (current == null)
                                continue; // trying to delete non-existing key

                            clusterTransactionDataCommand.Index = current.Value;
                        }
                        clusterTransactionDataCommand.Type = CommandType.CompareExchangeDELETE;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                ClusterCommands.Add(clusterTransactionDataCommand);
            }
        }

        private unsafe long? GetCurrentIndex(ClusterOperationContext context, Table items, string key)
        {
            using (Slice.From(context.Allocator, CompareExchangeKey.GetStorageKey(DatabaseName, key), out Slice keySlice))
            {
                if (items.ReadByKey(keySlice, out var reader))
                    return *(long*)reader.Read((int)ClusterStateMachine.CompareExchangeTable.Index, out var _);
            }

            return null;
        }

        public static bool IsAtomicGuardKey(string id, out string docId)
        {
            if (id.StartsWith(Constants.CompareExchange.RvnAtomicPrefix) == false)
            {
                docId = null;
                return false;
            }

            docId = id.Substring(Constants.CompareExchange.RvnAtomicPrefix.Length);
            return true;
        }

        public static string GetAtomicGuardKey(string docId)
        {
            return Constants.CompareExchange.RvnAtomicPrefix + docId;
        }

        public unsafe void SaveCommandsBatch(ClusterOperationContext context, long index)
        {
            if (HasDocumentsInTransaction == false)
                return;

            var items = context.Transaction.InnerTransaction.OpenTable(ClusterStateMachine.TransactionCommandsSchema, ClusterStateMachine.TransactionCommands);
            var commandsCountPerDatabase = context.Transaction.InnerTransaction.ReadTree(ClusterStateMachine.TransactionCommandsCountPerDatabase);
            var commands = context.ReadObject(SerializedDatabaseCommands, "serialized-tx-commands");

            using (GetPrefix(context, DatabaseName, out var databaseSlice))
            {
                var count = commandsCountPerDatabase.ReadInt64(databaseSlice) ?? 0;
                using (GetPrefix(context, DatabaseName, out var prefixSlice, count))
                using (items.Allocate(out TableValueBuilder tvb))
                {
                    tvb.Add(prefixSlice.Content.Ptr, prefixSlice.Size);
                    tvb.Add(commands.BasePointer, commands.Size);
                    tvb.Add(index);
                    items.Insert(tvb);
                    using (commandsCountPerDatabase.DirectAdd(databaseSlice, sizeof(long), out var ptr))
                        *(long*)ptr = count + DatabaseCommandsCount;
                }
            }
        }

        public bool HasDocumentsInTransaction => SerializedDatabaseCommands != null && DatabaseCommandsCount != 0;

        public enum TransactionCommandsColumn
        {
            // Database, Separator, PrevCount
            Key,
            Commands,
            RaftIndex,
        }

        public static SingleClusterDatabaseCommand ReadFirstClusterTransaction(ClusterOperationContext context, string database)
        {
            var items = context.Transaction.InnerTransaction.OpenTable(ClusterStateMachine.TransactionCommandsSchema, ClusterStateMachine.TransactionCommands);
            using (GetPrefix(context, database, out var prefixSlice))
            {
                if (items.SeekOnePrimaryKeyPrefix(prefixSlice, out var reader) == false)
                    return null;

                return ReadCommand(context, reader);
            }
        }

        public const byte Separator = 30;

        public static unsafe bool DeleteCommands<TTransaction>(TransactionOperationContext<TTransaction> context, string database, long upToCommandCount)
            where TTransaction : RavenTransaction
        {
            var items = context.Transaction.InnerTransaction.OpenTable(ClusterStateMachine.TransactionCommandsSchema, ClusterStateMachine.TransactionCommands);

            using (GetPrefix(context, database, out var prefixSlice))
            {
                return items.DeleteByPrimaryKeyPrefix(prefixSlice, shouldAbort: (tvb) =>
                {
                    var value = tvb.Reader.Read((int)ClusterTransactionCommand.TransactionCommandsColumn.Key, out var size);
                    var prevCommandsCount = Bits.SwapBytes(*(long*)(value + size - sizeof(long)));
                    return prevCommandsCount > upToCommandCount;
                });
            }
        }

        public static unsafe ByteStringContext.InternalScope GetPrefix<TTransaction>(TransactionOperationContext<TTransaction> context, string database, out Slice prefixSlice, long? index = null)
            where TTransaction : RavenTransaction
        {
            var maxSize = database.GetUtf8MaxSize() + sizeof(byte);
            if (index.HasValue)
                maxSize += sizeof(long);

            var lowerBufferSize = database.Length * sizeof(char);


            var scope = context.Allocator.Allocate(maxSize + lowerBufferSize, out var prefixBuffer);
            try
            {

                fixed (char* pDb = database)
                {
                    var lowerBufferStart = (char*)(prefixBuffer.Ptr + maxSize);
                    for (int i = 0; i < database.Length; i++)
                    {
                        lowerBufferStart[i] = char.ToLowerInvariant(pDb[i]);
                    }

                    var dbLen = Encoding.UTF8.GetBytes(lowerBufferStart, database.Length, prefixBuffer.Ptr, prefixBuffer.Length);
                    prefixBuffer.Ptr[dbLen] = Separator;
                    var actualSize = dbLen + 1;
                    if (index.HasValue)
                    {
                        *(long*)(prefixBuffer.Ptr + actualSize) = Bits.SwapBytes(index.Value);
                        actualSize += sizeof(long);
                    }
                    prefixBuffer.Truncate(actualSize);
                    prefixSlice = new Slice(prefixBuffer);
                    return scope;
                }
            }
            catch
            {
                scope.Dispose();
                throw;
            }
        }

        public class SingleClusterDatabaseCommand : IDynamicJson
        {
            public ClusterTransactionOptions Options;
            public BlittableJsonReaderArray Commands;
            public long Index;
            public long PreviousCount;
            public string Database;

            public bool Processed;

            public DynamicJsonValue ToJson()
            {
                return new DynamicJsonValue
                {
                    [nameof(Database)] = Database,
                    [nameof(PreviousCount)] = PreviousCount,
                    [nameof(Index)] = Index,
                    [nameof(Options)] = Options.ToJson(),
                    [nameof(Index)] = new DynamicJsonArray(Commands)
                };
            }
        }

        public static SingleClusterDatabaseCommand ReadSingleCommand(ClusterOperationContext context, string database, long? fromCount)
        {
            var lowerDb = database.ToLowerInvariant();
            var items = context.Transaction.InnerTransaction.OpenTable(ClusterStateMachine.TransactionCommandsSchema, ClusterStateMachine.TransactionCommands);
            using (GetPrefix(context, database, out Slice prefixSlice, fromCount))
            {
                var commandsBulk = items.SeekByPrimaryKey(prefixSlice, 0);
                foreach (var command in commandsBulk)
                {
                    var reader = command.Reader;
                    var result = ReadCommand(context, reader);
                    if (result == null)
                        return null;
                    if (result.Database != lowerDb) // beware of reading commands of other databases.
                        continue;
                    if (result.PreviousCount < fromCount)
                        continue;
                    return result;
                }

                return null;
            }
        }


        public static IEnumerable<SingleClusterDatabaseCommand> ReadCommandsBatch<TTransaction>(TransactionOperationContext<TTransaction> context, string database, long? fromCount, long take = 128)
            where TTransaction : RavenTransaction
        {
            var lowerDb = database.ToLowerInvariant();
            var items = context.Transaction.InnerTransaction.OpenTable(ClusterStateMachine.TransactionCommandsSchema, ClusterStateMachine.TransactionCommands);
            using (GetPrefix(context, database, out Slice prefixSlice, fromCount))
            {
                var commandsBulk = items.SeekByPrimaryKey(prefixSlice, 0);
                foreach (var command in commandsBulk)
                {
                    var reader = command.Reader;
                    var result = ReadCommand(context, reader);
                    if (result == null)
                        yield break;
                    if (result.Database != lowerDb) // beware of reading commands of other databases.
                        continue;
                    if (result.PreviousCount < fromCount)
                        continue;
                    if (take <= 0)
                        yield break;
                    take--;
                    yield return result;
                }
            }
        }

        private static unsafe SingleClusterDatabaseCommand ReadCommand<TTransaction>(TransactionOperationContext<TTransaction> context, TableValueReader reader)
            where TTransaction : RavenTransaction
        {
            var ptr = reader.Read((int)TransactionCommandsColumn.Commands, out var size);
            if (ptr == null)
                return null;

            var blittable = new BlittableJsonReaderObject(ptr, size, context);
            blittable.TryGet(nameof(DatabaseCommands), out BlittableJsonReaderArray array);

            ClusterTransactionOptions options = null;
            if (blittable.TryGet(nameof(Options), out BlittableJsonReaderObject blittableOptions))
            {
                options = JsonDeserializationServer.ClusterTransactionOptions(blittableOptions);
            }

            var index = *(long*)reader.Read((int)TransactionCommandsColumn.RaftIndex, out _);
            var keyPtr = reader.Read((int)TransactionCommandsColumn.Key, out size);
            var database = Encoding.UTF8.GetString(keyPtr, size - sizeof(long) - 1);

            return new SingleClusterDatabaseCommand
            {
                Options = options,
                Commands = array,
                Index = index,
                PreviousCount = Bits.SwapBytes(*(long*)(keyPtr + size - sizeof(long))),
                Database = database
            };
        }

        public override object FromRemote(object remoteResult)
        {
            var errors = new List<ClusterTransactionErrorInfo>();
            if (remoteResult is BlittableJsonReaderArray array)
            {
                foreach (var o in array)
                {
                    if (o is not BlittableJsonReaderObject blittable)
                        continue;

                    errors.Add(ToClusterTransactionErrorInfo(blittable));
                }

                return errors;
            }
            return base.FromRemote(remoteResult);
        }

        private static ClusterTransactionErrorInfo ToClusterTransactionErrorInfo(BlittableJsonReaderObject bjro)
        {
            var current = new ConcurrencyViolation();
            var errorInfo = new ClusterTransactionErrorInfo { Violation = current };
            bjro.TryGet(nameof(ClusterTransactionErrorInfo.Message), out errorInfo.Message);

            if (!bjro.TryGet(nameof(ClusterTransactionErrorInfo.Violation), out BlittableJsonReaderObject violation))
                return errorInfo;

            if (violation.TryGet(nameof(ConcurrencyViolation.Id), out string id))
                current.Id = id;

            if (violation.TryGet(nameof(ConcurrencyViolation.Type), out ViolationOnType type))
                current.Type = type;

            if (violation.TryGet(nameof(ConcurrencyViolation.Expected), out long expected))
                current.Expected = expected;

            if (violation.TryGet(nameof(ConcurrencyViolation.Actual), out long actual))
                current.Actual = actual;

            return errorInfo;
        }

        public override string AdditionalDebugInformation(Exception exception)
        {
            return $"guid: {UniqueRequestId} {string.Join(", ", ClusterCommands.Select(c => c.Id))}";
        }

        public override DynamicJsonValue ToJson(JsonOperationContext context)
        {
            var djv = base.ToJson(context);
            djv[nameof(ClusterCommands)] = new DynamicJsonArray(ClusterCommands.Select(x => x.ToJson(context)));
            djv[nameof(SerializedDatabaseCommands)] = SerializedDatabaseCommands?.Clone(context);
            if (SerializedDatabaseCommands == null && DatabaseCommands.Count > 0)
            {
                var databaseCommands = new DynamicJsonValue
                {
                    [nameof(DatabaseCommands)] = new DynamicJsonArray(DatabaseCommands.Select(x => x.ToJson(context))),
                    [nameof(Options)] = Options.ToJson(),
                };
                djv[nameof(SerializedDatabaseCommands)] = context.ReadObject(databaseCommands, "read database commands");
            }
            djv[nameof(DatabaseName)] = DatabaseName;
            djv[nameof(DatabaseRecordId)] = DatabaseRecordId;
            djv[nameof(ClusterTransactionId)] = ClusterTransactionId;
            djv[nameof(DatabaseCommandsCount)] = DatabaseCommandsCount;
            djv[nameof(FromBackup)] = FromBackup;
            djv[nameof(CommandCreationTicks)] = CommandCreationTicks;

            return djv;
        }
    }
}
