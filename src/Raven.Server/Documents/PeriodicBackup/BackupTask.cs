using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Raven.Client;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Backups;
using Raven.Client.Documents.Smuggler;
using Raven.Client.ServerWide.Operations.Configuration;
using Raven.Client.Util;
using Raven.Server.Config.Settings;
using Raven.Server.Documents.PeriodicBackup.Restore;
using Raven.Server.Documents.PeriodicBackup.Retention;
using Raven.Server.Json;
using Raven.Server.NotificationCenter.Notifications;
using Raven.Server.NotificationCenter.Notifications.Details;
using Raven.Server.Rachis;
using Raven.Server.Routing;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Commands;
using Raven.Server.ServerWide.Commands.PeriodicBackup;
using Raven.Server.ServerWide.Context;
using Raven.Server.Smuggler.Documents;
using Raven.Server.Smuggler.Documents.Data;
using Raven.Server.Utils;
using Sparrow.Json;
using Sparrow.Logging;
using Sparrow.Server.Json.Sync;
using DatabaseSmuggler = Raven.Server.Smuggler.Documents.DatabaseSmuggler;
using StorageEnvironmentType = Voron.StorageEnvironmentWithType.StorageEnvironmentType;

namespace Raven.Server.Documents.PeriodicBackup
{
    public class BackupTask
    {
        public static string DateTimeFormat = "yyyy-MM-dd-HH-mm-ss";
        private const string LegacyDateTimeFormat = "yyyy-MM-dd-HH-mm";
        private const string InProgressExtension = ".in-progress";

        private readonly DocumentDatabase _database;
        private readonly BackupConfiguration _configuration;
        private readonly PeriodicBackupStatus _previousBackupStatus;
        internal readonly bool _isFullBackup;
        private readonly bool _isOneTimeBackup;
        private readonly bool _backupToLocalFolder;
        private readonly long _operationId;
        private readonly PathSetting _tempBackupPath;
        private readonly Logger _logger;
        public readonly OperationCancelToken TaskCancelToken;
        private readonly BackupResult _backupResult;
        private readonly bool _isServerWide;
        private readonly bool _isBackupEncrypted;
        private readonly RetentionPolicyBaseParameters _retentionPolicyParameters;
        private Action<IOperationProgress> _onProgress;
        private readonly string _taskName;
        internal PeriodicBackupRunner.TestingStuff _forTestingPurposes;
        private readonly DateTime _startTimeUtc;
        public BackupTask(DocumentDatabase database, BackupParameters backupParameters, BackupConfiguration configuration, Logger logger, PeriodicBackupRunner.TestingStuff forTestingPurposes = null)
        {
            _database = database;
            _taskName = backupParameters.Name;
            _operationId = backupParameters.OperationId;
            _previousBackupStatus = backupParameters.BackupStatus;
            _startTimeUtc = backupParameters.StartTimeUtc;
            _isOneTimeBackup = backupParameters.IsOneTimeBackup;
            _isFullBackup = backupParameters.IsFullBackup;
            _backupToLocalFolder = backupParameters.BackupToLocalFolder;
            _tempBackupPath = backupParameters.TempBackupPath;
            _configuration = configuration;
            _logger = logger;
            _isServerWide = backupParameters.Name?.StartsWith(ServerWideBackupConfiguration.NamePrefix, StringComparison.OrdinalIgnoreCase) ?? false;
            _isBackupEncrypted = IsBackupEncrypted(_database, _configuration);
            _forTestingPurposes = forTestingPurposes;
            _backupResult = GenerateBackupResult();
            TaskCancelToken = new OperationCancelToken(_database.DatabaseShutdown, CancellationToken.None);

            _retentionPolicyParameters = new RetentionPolicyBaseParameters
            {
                RetentionPolicy = backupParameters.RetentionPolicy,
                DatabaseName = _database.Name,
                IsFullBackup = _isFullBackup,
                OnProgress = AddInfo,
                CancellationToken = TaskCancelToken.Token
            };
        }

        public BackupResult RunPeriodicBackup(Action<IOperationProgress> onProgress, ref PeriodicBackupStatus runningBackupStatus)
        {
            _onProgress = onProgress;
            AddInfo($"Started task: '{_taskName}'");

            var totalSw = Stopwatch.StartNew();
            var operationCanceled = false;

            try
            {
                if (_forTestingPurposes != null && _forTestingPurposes.SimulateFailedBackup)
                    throw new Exception(nameof(_forTestingPurposes.SimulateFailedBackup));
                if (_forTestingPurposes != null && _forTestingPurposes.OnBackupTaskRunHoldBackupExecution != null)
                    _forTestingPurposes.OnBackupTaskRunHoldBackupExecution.Task.Wait();
                if (_database.ForTestingPurposes != null && _database.ForTestingPurposes.ActionToCallOnGetTempPath != null)
                    _database.ForTestingPurposes.ActionToCallOnGetTempPath.Invoke(_tempBackupPath);

                if (runningBackupStatus.LocalBackup == null)
                    runningBackupStatus.LocalBackup = new LocalBackup();

                if (runningBackupStatus.LastRaftIndex == null)
                    runningBackupStatus.LastRaftIndex = new LastRaftIndex();

                runningBackupStatus.IsFull = _isFullBackup;

                if (_logger.IsInfoEnabled)
                {
                    var fullBackupText = "a " + (_configuration.BackupType == BackupType.Backup ? "full backup" : "snapshot");
                    _logger.Info($"Creating {(_isFullBackup ? fullBackupText : "an incremental backup")}");
                }

                if (_isFullBackup == false)
                {
                    // if we come from old version the _previousBackupStatus won't have LastRaftIndex
                    _previousBackupStatus.LastRaftIndex ??= new LastRaftIndex();

                    // no-op if nothing has changed
                    var (currentLastEtag, currentChangeVector) = _database.ReadLastEtagAndChangeVector();
                    var currentLastRaftIndex = GetDatabaseEtagForBackup();

                    // if we come from old version the _previousBackupStatus won't have LastRaftIndex
                    _previousBackupStatus.LastRaftIndex ??= new LastRaftIndex();

                    if (currentLastEtag == _previousBackupStatus.LastEtag
                        && currentChangeVector == _previousBackupStatus.LastDatabaseChangeVector
                        && currentLastRaftIndex == _previousBackupStatus.LastRaftIndex.LastEtag)
                    {
                        var message = $"Skipping incremental backup because no changes were made from last full backup on {_previousBackupStatus.LastFullBackup}.";

                        if (_logger.IsInfoEnabled)
                            _logger.Info(message);

                        runningBackupStatus.LastIncrementalBackup = _startTimeUtc;
                        runningBackupStatus.LocalBackup.LastIncrementalBackup = _startTimeUtc;
                        runningBackupStatus.LocalBackup.IncrementalBackupDurationInMs = 0;
                        DatabaseSmuggler.EnsureProcessed(_backupResult);
                        AddInfo(message);

                        return _backupResult;
                    }
                }

                // update the local configuration before starting the local backup
                var localSettings = GetBackupConfigurationFromScript(_configuration.LocalSettings, x => JsonDeserializationServer.LocalSettings(x),
                    settings => PutServerWideBackupConfigurationCommand.UpdateSettingsForLocal(settings, _database.Name));

                GenerateFolderNameAndBackupDirectory(localSettings, _startTimeUtc, out var nowAsString, out var folderName, out var backupDirectory);
                var startEtag = _isFullBackup == false ? _previousBackupStatus.LastEtag : null;
                var startRaftIndex = _isFullBackup == false ? _previousBackupStatus.LastRaftIndex.LastEtag : null;

                var fileName = GetFileName(_isFullBackup, backupDirectory.FullPath, nowAsString, _configuration.BackupType, out string backupFilePath);
                var internalBackupResult = CreateLocalBackupOrSnapshot(runningBackupStatus, backupFilePath, startEtag, startRaftIndex);

                runningBackupStatus.LocalBackup.BackupDirectory = _backupToLocalFolder ? backupDirectory.FullPath : null;
                runningBackupStatus.LocalBackup.TempFolderUsed = _backupToLocalFolder == false;
                runningBackupStatus.IsEncrypted = _isBackupEncrypted;

                try
                {
                    UploadToServer(backupFilePath, folderName, fileName);
                }
                finally
                {
                    runningBackupStatus.UploadToS3 = _backupResult.S3Backup;
                    runningBackupStatus.UploadToAzure = _backupResult.AzureBackup;
                    runningBackupStatus.UploadToGoogleCloud = _backupResult.GoogleCloudBackup;
                    runningBackupStatus.UploadToGlacier = _backupResult.GlacierBackup;
                    runningBackupStatus.UploadToFtp = _backupResult.FtpBackup;

                    _backupResult.LocalBackup = new LocalBackup
                    {
                        BackupDirectory = folderName,
                        FileName = fileName
                    };

                    // if user did not specify local folder we delete the temporary file
                    if (_backupToLocalFolder == false)
                    {
                        DeleteFile(backupFilePath);
                    }
                }

                runningBackupStatus.LastEtag = internalBackupResult.LastEtag;
                runningBackupStatus.LastDatabaseChangeVector = internalBackupResult.LastDatabaseChangeVector;
                runningBackupStatus.LastRaftIndex.LastEtag = internalBackupResult.LastRaftIndex;
                runningBackupStatus.FolderName = folderName;

                if (_isFullBackup)
                    runningBackupStatus.LastFullBackup = _startTimeUtc;
                else
                    runningBackupStatus.LastIncrementalBackup = _startTimeUtc;

                totalSw.Stop();

                if (_logger.IsInfoEnabled)
                {
                    var fullBackupText = "a " + (_configuration.BackupType == BackupType.Backup ? " full backup" : " snapshot");
                    _logger.Info($"Successfully created {(_isFullBackup ? fullBackupText : "an incremental backup")} " +
                                 $"in {totalSw.ElapsedMilliseconds:#,#;;0} ms");
                }

                return _backupResult;
            }
            catch (OperationCanceledException)
            {
                operationCanceled = TaskCancelToken.Token.IsCancellationRequested;
                throw;
            }
            catch (ObjectDisposedException)
            {
                // shutting down, probably
                operationCanceled = true;
                throw;
            }
            catch (Exception e)
            {
                const string message = "Error when performing periodic backup";

                runningBackupStatus.Error = new Error
                {
                    Exception = e.ToString(),
                    At = DateTime.UtcNow
                };

                if (_logger.IsOperationsEnabled)
                    _logger.Operations(message, e);

                _database.NotificationCenter.Add(AlertRaised.Create(
                    _database.Name,
                    $"Periodic Backup task: '{_taskName}'",
                    message,
                    AlertType.PeriodicBackup,
                    NotificationSeverity.Error,
                    details: new ExceptionDetails(e)));

                throw;
            }
            finally
            {
                if (operationCanceled == false)
                {
                    // whether we succeeded or not,
                    // in periodic backup we need to update the last backup time to avoid
                    // starting a new backup right after this one
                    if (_isFullBackup)
                        runningBackupStatus.LastFullBackupInternal = _startTimeUtc;
                    else
                        runningBackupStatus.LastIncrementalBackupInternal = _startTimeUtc;

                    runningBackupStatus.NodeTag = _database.ServerStore.NodeTag;
                    runningBackupStatus.DurationInMs = totalSw.ElapsedMilliseconds;
                    UpdateOperationId(runningBackupStatus);

                    if (_isOneTimeBackup == false)
                    {
                        runningBackupStatus.Version = ++_previousBackupStatus.Version;
                        // save the backup status
                        AddInfo("Saving backup status");
                        SaveBackupStatus(runningBackupStatus, _database, _logger, _backupResult);
                    }
                }
            }
        }

        private T GetBackupConfigurationFromScript<T>(T backupSettings, Func<BlittableJsonReaderObject, T> deserializeSettingsFunc,
            Action<T> updateServerWideSettingsFunc)
            where T : BackupSettings
        {
            return GetBackupConfigurationFromScript(backupSettings, deserializeSettingsFunc, _database, updateServerWideSettingsFunc, _isServerWide);
        }

        internal static T GetBackupConfigurationFromScript<T>(T backupSettings, Func<BlittableJsonReaderObject, T> deserializeSettingsFunc, DocumentDatabase documentDatabase,
            Action<T> updateServerWideSettingsFunc, bool serverWide)
            where T : BackupSettings
        {
            if (backupSettings == null)
                return null;

            if (backupSettings.GetBackupConfigurationScript == null || backupSettings.Disabled)
                return backupSettings;

            if (string.IsNullOrEmpty(backupSettings.GetBackupConfigurationScript.Exec))
                return backupSettings;

            var command = backupSettings.GetBackupConfigurationScript.Exec;
            var arguments = backupSettings.GetBackupConfigurationScript.Arguments;

            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            Process process;

            try
            {
                process = Process.Start(startInfo);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"Unable to get backup configuration by executing {command} {arguments}. Failed to start process.", e);
            }

            using (var ms = new MemoryStream())
            {
                var readErrors = process.StandardError.ReadToEndAsync();
                var readStdOut = process.StandardOutput.BaseStream.CopyToAsync(ms);
                var timeoutInMs = backupSettings.GetBackupConfigurationScript.TimeoutInMs;

                string GetStdError()
                {
                    try
                    {
                        return readErrors.Result;
                    }
                    catch
                    {
                        return "Unable to get stdout";
                    }
                }

                try
                {
                    readStdOut.Wait(timeoutInMs);
                    readErrors.Wait(timeoutInMs);
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException($"Unable to get backup configuration by executing {command} {arguments}, waited for {timeoutInMs}ms but the process didn't exit. Stderr: {GetStdError()}", e);
                }

                if (process.WaitForExit(timeoutInMs) == false)
                {
                    process.Kill();

                    throw new InvalidOperationException($"Unable to get backup configuration by executing {command} {arguments}, waited for {timeoutInMs}ms but the process didn't exit. Stderr: {GetStdError()}");
                }

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Unable to get backup configuration by executing {command} {arguments}, the exit code was {process.ExitCode}. Stderr: {GetStdError()}");
                }

                using (documentDatabase.DocumentsStorage.ContextPool.AllocateOperationContext(out JsonOperationContext context))
                {
                    ms.Position = 0;
                    var configuration = context.Sync.ReadForMemory(ms, "backup-configuration-from-script");
                    var result = deserializeSettingsFunc(configuration);
                    if (serverWide)
                        updateServerWideSettingsFunc?.Invoke(result);

                    return result;
                }
            }
        }

        public static bool IsBackupEncrypted(DocumentDatabase database, BackupConfiguration configuration)
        {
            if (database.MasterKey != null && configuration.BackupEncryptionSettings == null)
                return true;

            return configuration.BackupEncryptionSettings != null &&
                   configuration.BackupEncryptionSettings.EncryptionMode != EncryptionMode.None;
        }

        private long GetDatabaseEtagForBackup()
        {
            using (_database.ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
            using (context.OpenReadTransaction())
            {
                var rawRecord = _database.ServerStore.Cluster.ReadRawDatabaseRecord(context, _database.Name);

                return rawRecord.EtagForBackup;
            }
        }

        private void GenerateFolderNameAndBackupDirectory(LocalSettings localSettings, DateTime startTimeInUtc, out string nowAsString, out string folderName, out PathSetting backupDirectory)
        {
            var nowInLocalTime = startTimeInUtc.ToLocalTime();

            if (_isFullBackup)
            {
                while (true)
                {
                    nowAsString = GetFormattedDate(nowInLocalTime);
                    folderName = $"{nowAsString}.ravendb-{_database.Name}-{_database.ServerStore.NodeTag}-{_configuration.BackupType.ToString().ToLower()}";
                    backupDirectory = _backupToLocalFolder ? new PathSetting(localSettings.FolderPath).Combine(folderName) : _tempBackupPath;

                    if (_backupToLocalFolder == false || DirectoryContainsBackupFiles(backupDirectory.FullPath, IsAnyBackupFile) == false)
                        break;

                    // the backup folder contains backup files
                    nowInLocalTime = DateTime.Now;
                }

                if (Directory.Exists(backupDirectory.FullPath) == false)
                    Directory.CreateDirectory(backupDirectory.FullPath);
            }
            else
            {
                Debug.Assert(_previousBackupStatus.FolderName != null);

                folderName = _previousBackupStatus.FolderName;
                backupDirectory = _backupToLocalFolder ? new PathSetting(_previousBackupStatus.LocalBackup.BackupDirectory) : _tempBackupPath;
                nowAsString = GetFormattedDate(nowInLocalTime);
            }
        }

        private static string GetFormattedDate(DateTime dateTime)
        {
            return dateTime.ToString(DateTimeFormat, CultureInfo.InvariantCulture);
        }

        private BackupResult GenerateBackupResult()
        {
            return new BackupResult
            {
                SnapshotBackup =
                {
                    Skipped = _isFullBackup == false || _configuration.BackupType == BackupType.Backup
                },
                S3Backup =
                {
                    // will be set before the actual upload if needed
                    Skipped = true
                },
                AzureBackup =
                {
                    Skipped = true
                },
                GoogleCloudBackup =
                {
                    Skipped = true
                },
                GlacierBackup =
                {
                    Skipped = true
                },
                FtpBackup =
                {
                    Skipped = true
                }
            };
        }

        public static bool DirectoryContainsBackupFiles(string fullPath, Func<string, bool> isBackupFile)
        {
            if (Directory.Exists(fullPath) == false)
                return false;

            var files = Directory.GetFiles(fullPath);
            if (files.Length == 0)
                return false;

            return files.Any(isBackupFile);
        }

        private static bool IsAnyBackupFile(string filePath)
        {
            if (RestorePointsBase.IsBackupOrSnapshot(filePath))
                return true;

            var extension = Path.GetExtension(filePath);
            return InProgressExtension.Equals(extension, StringComparison.OrdinalIgnoreCase);
        }

        private string GetFileName(
            bool isFullBackup,
            string backupFolder,
            string nowAsString,
            BackupType backupType,
            out string backupFilePath)
        {
            var backupExtension = GetBackupExtension(backupType, isFullBackup);
            var fileName = isFullBackup ?
                GetFileNameFor(backupExtension, nowAsString, backupFolder, out backupFilePath, throwWhenFileExists: true) :
                GetFileNameFor(backupExtension, nowAsString, backupFolder, out backupFilePath);

            return fileName;
        }

        private string GetBackupExtension(BackupType type, bool isFullBackup)
        {
            if (isFullBackup == false)
                return _isBackupEncrypted ? Constants.Documents.PeriodicBackup.EncryptedIncrementalBackupExtension :
                    Constants.Documents.PeriodicBackup.IncrementalBackupExtension;

            switch (type)
            {
                case BackupType.Backup:
                    return _isBackupEncrypted ?
                        Constants.Documents.PeriodicBackup.EncryptedFullBackupExtension : Constants.Documents.PeriodicBackup.FullBackupExtension;

                case BackupType.Snapshot:
                    return _isBackupEncrypted ?
                        Constants.Documents.PeriodicBackup.EncryptedSnapshotExtension : Constants.Documents.PeriodicBackup.SnapshotExtension;

                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
        }

        private static string GetFileNameFor(
            string backupExtension,
            string nowAsString,
            string backupFolder,
            out string backupFilePath,
            bool throwWhenFileExists = false)
        {
            var fileName = $"{nowAsString}{backupExtension}";
            backupFilePath = Path.Combine(backupFolder, fileName);

            if (File.Exists(backupFilePath))
            {
                if (throwWhenFileExists)
                    throw new InvalidOperationException($"File '{backupFilePath}' already exists!");

                while (true)
                {
                    fileName = $"{GetFormattedDate(DateTime.Now)}{backupExtension}";
                    backupFilePath = Path.Combine(backupFolder, fileName);

                    if (File.Exists(backupFilePath) == false)
                        break;
                }
            }

            return fileName;
        }

        private class InternalBackupResult
        {
            public long LastEtag { get; set; }
            public string LastDatabaseChangeVector { get; set; }
            public long LastRaftIndex { get; set; }
        }

        private InternalBackupResult CreateLocalBackupOrSnapshot(PeriodicBackupStatus status, string backupFilePath, long? startEtag, long? startRaftIndex)
        {
            var internalBackupResult = new InternalBackupResult();

            using (status.LocalBackup.UpdateStats(_isFullBackup))
            {
                // will rename the file after the backup is finished
                var tempBackupFilePath = backupFilePath + InProgressExtension;

                try
                {
                    BackupTypeValidation();

                    AddInfo($"Started {GetBackupDescription(_configuration.BackupType, _isFullBackup)}");
                    if (_configuration.BackupType == BackupType.Backup ||
                        _configuration.BackupType == BackupType.Snapshot && _isFullBackup == false)
                    {
                        // smuggler backup
                        var options = new DatabaseSmugglerOptionsServerSide
                        {
                            AuthorizationStatus = AuthorizationStatus.DatabaseAdmin,
                            IncludeArtificial = true // we want to include artificial in backup
                        };

                        options.OperateOnTypes |= DatabaseItemType.Tombstones;
                        options.OperateOnTypes |= DatabaseItemType.CompareExchangeTombstones;

                        var currentBackupResult = CreateBackup(options, tempBackupFilePath, startEtag, startRaftIndex);

                        if (_isFullBackup)
                        {
                            internalBackupResult = currentBackupResult;
                        }
                        else
                        {
                            if (_backupResult.GetLastEtag() == _previousBackupStatus.LastEtag &&
                                _backupResult.GetLastRaftIndex() == _previousBackupStatus.LastRaftIndex.LastEtag)
                            {
                                internalBackupResult.LastEtag = startEtag ?? 0;
                                internalBackupResult.LastDatabaseChangeVector = _previousBackupStatus.LastDatabaseChangeVector;
                                internalBackupResult.LastRaftIndex = startRaftIndex ?? 0;
                            }
                            else
                            {
                                internalBackupResult = currentBackupResult;
                            }
                        }
                    }
                    else
                    {
                        // snapshot backup
                        ValidateFreeSpaceForSnapshot(tempBackupFilePath);

                        (internalBackupResult.LastEtag, internalBackupResult.LastDatabaseChangeVector) = _database.ReadLastEtagAndChangeVector();
                        internalBackupResult.LastRaftIndex = GetDatabaseEtagForBackup();
                        var databaseSummary = _database.GetDatabaseSummary();
                        var indexesCount = _database.IndexStore.Count;

                        var totalSw = Stopwatch.StartNew();
                        var sw = Stopwatch.StartNew();
                        var compressionLevel = _configuration.SnapshotSettings?.CompressionLevel ?? CompressionLevel.Optimal;
                        var excludeIndexes = _configuration.SnapshotSettings?.ExcludeIndexes ?? false;
                        var smugglerResult = _database.FullBackupTo(tempBackupFilePath, compressionLevel, excludeIndexes,
                            info =>
                            {
                                AddInfo(info.Message);

                                _backupResult.SnapshotBackup.ReadCount += info.FilesCount;
                                if (sw.ElapsedMilliseconds > 0 && info.FilesCount > 0)
                                {
                                    AddInfo($"Backed up {_backupResult.SnapshotBackup.ReadCount} " +
                                            $"file{(_backupResult.SnapshotBackup.ReadCount > 1 ? "s" : string.Empty)}");
                                    sw.Restart();
                                }
                            }, TaskCancelToken.Token);

                        EnsureSnapshotProcessed(databaseSummary, smugglerResult, indexesCount);

                        AddInfo($"Backed up {_backupResult.SnapshotBackup.ReadCount} files, " +
                                $"took: {totalSw.ElapsedMilliseconds:#,#;;0}ms");
                    }

                    IOExtensions.RenameFile(tempBackupFilePath, backupFilePath);

                    status.LocalBackup.Exception = null;
                }
                catch (Exception e)
                {
                    status.LocalBackup.Exception = e.ToString();

                    // deleting the temp backup file if the backup failed
                    DeleteFile(tempBackupFilePath);
                    throw;
                }
            }

            if (_backupToLocalFolder)
            {
                var sp = Stopwatch.StartNew();
                var localRetentionPolicy = new LocalRetentionPolicyRunner(_retentionPolicyParameters, _configuration.LocalSettings.FolderPath);
                localRetentionPolicy.Execute();
                sp.Stop();
                status.LocalRetentionDurationInMs = sp.ElapsedMilliseconds;
            }

            return internalBackupResult;
        }

        public static string GetBackupDescription(BackupType backupType, bool isFull)
        {
            var isFullText = isFull ? "a full" : "an incremental";
            var backupTypeText = backupType == BackupType.Snapshot ? "snapshot backup" : "backup";
            return $"{isFullText} {backupTypeText}";
        }

        private void DeleteFile(string path)
        {
            try
            {
                IOExtensions.DeleteFile(path);
            }
            catch (Exception e)
            {
                if (_logger.IsInfoEnabled)
                    _logger.Info($"Failed to delete file: {path}", e);
            }
        }

        private void ValidateFreeSpaceForSnapshot(string filePath)
        {
            long totalUsedSpace = 0;
            foreach (var mountPointUsage in _database.GetMountPointsUsage(includeTempBuffers: false))
            {
                if(mountPointUsage.Type == nameof(StorageEnvironmentType.Index) &&
                   _configuration.SnapshotSettings is { ExcludeIndexes: true })
                    continue;

                totalUsedSpace += mountPointUsage.UsedSpace;
            }

            var directoryPath = Path.GetDirectoryName(filePath);

            BackupHelper.AssertFreeSpaceForSnapshot(directoryPath, totalUsedSpace, "create a snapshot", _logger);
        }

        private void BackupTypeValidation()
        {
            if (_database.MasterKey == null &&
                _configuration.BackupEncryptionSettings?.EncryptionMode == EncryptionMode.UseDatabaseKey)
                throw new InvalidOperationException("Can't use database key for backup encryption, the key doesn't exist");

            if (_configuration.BackupType == BackupType.Snapshot && _isFullBackup &&
                _configuration.BackupEncryptionSettings != null &&
                _configuration.BackupEncryptionSettings.EncryptionMode == EncryptionMode.UseProvidedKey)
                throw new InvalidOperationException("Can't snapshot an encrypted database with a different key");
        }

        private void EnsureSnapshotProcessed(DatabaseSummary databaseSummary, SmugglerResult snapshotSmugglerResult, long indexesCount)
        {
            _backupResult.SnapshotBackup.Processed = true;
            _backupResult.DatabaseRecord.Processed = true;
            _backupResult.RevisionDocuments.Attachments.Processed = true;
            _backupResult.Tombstones.Processed = true;
            _backupResult.Indexes.Processed = true;
            _backupResult.Indexes.ReadCount = indexesCount;

            _backupResult.Documents.Processed = true;
            _backupResult.Documents.ReadCount = databaseSummary.DocumentsCount;
            _backupResult.Documents.Attachments.Processed = true;
            _backupResult.Documents.Attachments.ReadCount = databaseSummary.AttachmentsCount;
            _backupResult.Counters.Processed = true;
            _backupResult.Counters.ReadCount = databaseSummary.CounterEntriesCount;
            _backupResult.RevisionDocuments.Processed = true;
            _backupResult.RevisionDocuments.ReadCount = databaseSummary.RevisionsCount;
            _backupResult.Conflicts.Processed = true;
            _backupResult.Conflicts.ReadCount = databaseSummary.ConflictsCount;

            _backupResult.Identities.Processed = true;
            _backupResult.Identities.ReadCount = snapshotSmugglerResult.Identities.ReadCount;
            _backupResult.CompareExchange.Processed = true;
            _backupResult.CompareExchange.ReadCount = snapshotSmugglerResult.CompareExchange.ReadCount;
            _backupResult.CompareExchangeTombstones.Processed = true;
            _backupResult.Subscriptions.Processed = true;
            _backupResult.Subscriptions.ReadCount = snapshotSmugglerResult.Subscriptions.ReadCount;

            _backupResult.TimeSeries.Processed = true;
            _backupResult.TimeSeries.ReadCount = databaseSummary.TimeSeriesSegmentsCount;
        }

        private void AddInfo(string message)
        {
            _backupResult.AddInfo(message);
            _onProgress.Invoke(_backupResult.Progress);
        }

        private InternalBackupResult CreateBackup(
            DatabaseSmugglerOptionsServerSide options, string backupFilePath, long? startDocumentEtag, long? startRaftIndex)
        {
            // the last etag is already included in the last backup
            var currentBackupResults = new InternalBackupResult();
            startDocumentEtag = startDocumentEtag == null ? 0 : ++startDocumentEtag;
            startRaftIndex = startRaftIndex == null ? 0 : ++startRaftIndex;

            using (Stream fileStream = File.Open(backupFilePath, FileMode.CreateNew))
            using (var outputStream = GetOutputStream(fileStream))
            using (_database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            {
                var smugglerSource = new DatabaseSource(_database, startDocumentEtag.Value, startRaftIndex.Value, _logger);
                var smugglerDestination = new StreamDestination(outputStream, context, smugglerSource);
                var smuggler = new DatabaseSmuggler(_database,
                    smugglerSource,
                    smugglerDestination,
                    _database.Time,
                    options: options,
                    result: _backupResult,
                    onProgress: _onProgress,
                    token: TaskCancelToken.Token);

                smuggler.ExecuteAsync().Wait();

                switch (outputStream)
                {
                    case EncryptingXChaCha20Poly1305Stream encryptedStream:
                        encryptedStream.Flush(flushToDisk: true);
                        break;

                    case FileStream file:
                        file.Flush(flushToDisk: true);
                        break;

                    default:
                        throw new InvalidOperationException($" {outputStream.GetType()} not supported");
                }

                currentBackupResults.LastEtag = smugglerSource.LastEtag;
                currentBackupResults.LastDatabaseChangeVector = smugglerSource.LastDatabaseChangeVector;
                currentBackupResults.LastRaftIndex = smugglerSource.LastRaftIndex;

                return currentBackupResults;
            }
        }

        public Stream GetOutputStream(Stream fileStream)
        {
            if (_isBackupEncrypted == false)
                return fileStream;

            if (_database.MasterKey != null && _configuration.BackupEncryptionSettings == null)
                return new EncryptingXChaCha20Poly1305Stream(fileStream, _database.MasterKey);

            if (_configuration.BackupEncryptionSettings.EncryptionMode == EncryptionMode.UseDatabaseKey)
                return new EncryptingXChaCha20Poly1305Stream(fileStream, _database.MasterKey);

            return new EncryptingXChaCha20Poly1305Stream(fileStream,
                Convert.FromBase64String(_configuration.BackupEncryptionSettings.Key));
        }

        private void UploadToServer(string backupPath, string folderName, string fileName)
        {
            var s3Settings = GetBackupConfigurationFromScript(_configuration.S3Settings, x => JsonDeserializationServer.S3Settings(x),
                settings => PutServerWideBackupConfigurationCommand.UpdateSettingsForS3(settings, _database.Name));
            var glacierSettings = GetBackupConfigurationFromScript(_configuration.GlacierSettings, x => JsonDeserializationServer.GlacierSettings(x),
                settings => PutServerWideBackupConfigurationCommand.UpdateSettingsForGlacier(settings, _database.Name));
            var azureSettings = GetBackupConfigurationFromScript(_configuration.AzureSettings, x => JsonDeserializationServer.AzureSettings(x),
                settings => PutServerWideBackupConfigurationCommand.UpdateSettingsForAzure(settings, _database.Name));
            var googleCloudSettings = GetBackupConfigurationFromScript(_configuration.GoogleCloudSettings, x => JsonDeserializationServer.GoogleCloudSettings(x),
                settings => PutServerWideBackupConfigurationCommand.UpdateSettingsForGoogleCloud(settings, _database.Name));
            var ftpSettings = GetBackupConfigurationFromScript(_configuration.FtpSettings, x => JsonDeserializationServer.FtpSettings(x),
                settings => PutServerWideBackupConfigurationCommand.UpdateSettingsForFtp(settings, _database.Name));

            TaskCancelToken.Token.ThrowIfCancellationRequested();

            var uploaderSettings = new UploaderSettings(_database.Configuration.Backup)
            {
                S3Settings = s3Settings,
                GlacierSettings = glacierSettings,
                AzureSettings = azureSettings,
                GoogleCloudSettings = googleCloudSettings,
                FtpSettings = ftpSettings,

                FilePath = backupPath,
                FolderName = folderName,
                FileName = fileName,
                DatabaseName = _database.Name,
                TaskName = _taskName,

                BackupType = _configuration.BackupType
            };

            var backupUploader = new BackupUploader(uploaderSettings, _retentionPolicyParameters, _logger, _backupResult, _onProgress, TaskCancelToken);
            backupUploader.ExecuteUpload();
        }

        private void UpdateOperationId(PeriodicBackupStatus runningBackupStatus)
        {
            runningBackupStatus.LastOperationId = _operationId;
            if (_previousBackupStatus.LastOperationId == null ||
                _previousBackupStatus.NodeTag != _database.ServerStore.NodeTag ||
                _previousBackupStatus.Error != null)
                return;

            // dismiss the previous operation
            var id = $"{NotificationType.OperationChanged}/{_previousBackupStatus.LastOperationId.Value}";
            _database.NotificationCenter.Dismiss(id);
        }

        public static void SaveBackupStatus(PeriodicBackupStatus status, DocumentDatabase documentDatabase, Logger logger, BackupResult backupResult)
        {
            try
            {
                var command = new UpdatePeriodicBackupStatusCommand(documentDatabase.Name, RaftIdGenerator.NewId())
                {
                    PeriodicBackupStatus = status
                };

                var result = AsyncHelpers.RunSync(() => documentDatabase.ServerStore.SendToLeaderAsync(command));
                AsyncHelpers.RunSync(() => documentDatabase.ServerStore.WaitForCommitIndexChange(RachisConsensus.CommitIndexModification.GreaterOrEqual, result.Index));

                if (logger.IsInfoEnabled)
                    logger.Info($"Periodic backup status with task id {status.TaskId} was updated");
            }
            catch (Exception e)
            {
                const string message = "Error saving the periodic backup status";

                if (logger.IsOperationsEnabled)
                    logger.Operations(message, e);

                backupResult?.AddError($"{message}{Environment.NewLine}{e}");
            }
        }

        public static string GetDateTimeFormat(string fileName)
        {
            return fileName.Length == LegacyDateTimeFormat.Length ? LegacyDateTimeFormat : DateTimeFormat;
        }
    }
}
