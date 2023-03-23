﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using Sparrow;
using Sparrow.Collections;
using Sparrow.Logging;
using Sparrow.Platform;
using Sparrow.Server;
using Sparrow.Server.Meters;
using Sparrow.Server.Platform;
using Sparrow.Server.Utils;
using Sparrow.Utils;
using Voron.Exceptions;
using Voron.Impl;
using Voron.Impl.FileHeaders;
using Voron.Impl.Journal;
using Voron.Impl.Paging;
using Voron.Impl.Scratch;
using Voron.Platform.Posix;
using Voron.Platform.Win32;
using Voron.Util;
using Voron.Util.Settings;
using Constants = Voron.Global.Constants;
using NativeMemory = Sparrow.Utils.NativeMemory;

namespace Voron
{
    public abstract class StorageEnvironmentOptions : IDisposable
    {
        public const string RecyclableJournalFileNamePrefix = "recyclable-journal";

        private ExceptionDispatchInfo _catastrophicFailure;
        private string _catastrophicFailureStack;

        [ThreadStatic]
        private static bool _skipCatastrophicFailureAssertion;
        private readonly CatastrophicFailureNotification _catastrophicFailureNotification;
        private readonly ConcurrentSet<CryptoPager> _activeCryptoPagers = new ConcurrentSet<CryptoPager>();

        public VoronPathSetting TempPath { get; }

        public VoronPathSetting JournalPath { get; private set; }

        public IoMetrics IoMetrics { get; set; }

        public bool GenerateNewDatabaseId { get; set; }

        public LazyWithExceptionRetry<DriveInfoByPath> DriveInfoByPath { get; private set; }

        public event EventHandler<RecoveryErrorEventArgs> OnRecoveryError;
        public event EventHandler<NonDurabilitySupportEventArgs> OnNonDurableFileSystemError;
        public event EventHandler<DataIntegrityErrorEventArgs> OnIntegrityErrorOfAlreadySyncedData;
        public event EventHandler<RecoverableFailureEventArgs> OnRecoverableFailure;

        private long _reuseCounter;
        private long _lastReusedJournalCountOnSync;

        public void SetLastReusedJournalCountOnSync(long journalNum)
        {
            _lastReusedJournalCountOnSync = journalNum;
        }

        public abstract override string ToString();

        private bool _forceUsing32BitsPager;
        public bool ForceUsing32BitsPager
        {
            get => _forceUsing32BitsPager;
            set
            {
                _forceUsing32BitsPager = value;
                MaxLogFileSize = (value ? 32 : 256) * Constants.Size.Megabyte;
                MaxScratchBufferSize = (value ? 32 : 256) * Constants.Size.Megabyte;
                MaxNumberOfPagesInJournalBeforeFlush = (value ? 4 : 32) * Constants.Size.Megabyte / Constants.Storage.PageSize;
            }
        }

        public bool EnablePrefetching = true;

        internal DisposableAction DisableOnRecoveryErrorHandler()
        {
            var handler = OnRecoveryError;
            OnRecoveryError = null;

            return new DisposableAction(() => OnRecoveryError = handler);
        }

        public void InvokeRecoveryError(object sender, string message, Exception e)
        {
            var handler = OnRecoveryError;
            if (handler == null)
            {
                throw new InvalidDataException(message + Environment.NewLine +
                                               $"An exception has been thrown because there isn't a listener to the {nameof(OnRecoveryError)} event on the storage options.", e);
            }

            handler(this, new RecoveryErrorEventArgs(message, e));
        }

        internal DisposableAction DisableOnIntegrityErrorOfAlreadySyncedDataHandler()
        {
            var handler = OnIntegrityErrorOfAlreadySyncedData;
            OnIntegrityErrorOfAlreadySyncedData = null;

            return new DisposableAction(() => OnIntegrityErrorOfAlreadySyncedData = handler);
        }

        public void InvokeIntegrityErrorOfAlreadySyncedData(object sender, string message, Exception e)
        {
            var handler = OnIntegrityErrorOfAlreadySyncedData;
            if (handler == null)
            {
                throw new InvalidDataException(message + Environment.NewLine +
                                               $"An exception has been thrown because there isn't a listener to the {nameof(OnIntegrityErrorOfAlreadySyncedData)} event on the storage options.", e);
            }

            handler(this, new DataIntegrityErrorEventArgs(message, e));
        }


        public void InvokeNonDurableFileSystemError(object sender, string message, Exception e, string details)
        {
            var handler = OnNonDurableFileSystemError;
            if (handler == null)
            {
                throw new InvalidDataException(message + Environment.NewLine +
                                               "An exception has been thrown because there isn't a listener to the OnNonDurableFileSystemError event on the storage options.",
                    e);
            }

            handler(this, new NonDurabilitySupportEventArgs(message, e, details));
        }

        public long? InitialFileSize { get; set; }

        public long MaxLogFileSize
        {
            get { return _maxLogFileSize; }
            set
            {
                if (value < _initialLogFileSize)
                    InitialLogFileSize = value;
                _maxLogFileSize = value;
            }
        }

        public long InitialLogFileSize
        {
            get { return _initialLogFileSize; }
            set
            {
                if (value > MaxLogFileSize)
                    MaxLogFileSize = value;
                if (value <= 0)
                    ThrowInitialLogFileSizeOutOfRange();
                _initialLogFileSize = value;
            }
        }

        private static void ThrowInitialLogFileSizeOutOfRange()
        {
            throw new ArgumentOutOfRangeException("InitialLogFileSize", "The initial log for the Voron must be above zero");
        }

        public StorageEncryptionOptions Encryption { get; } = new StorageEncryptionOptions();

        public int PageSize => Constants.Storage.PageSize;

        // if set to a non zero value, will check that the expected schema is there
        public int SchemaVersion { get; set; }

        public UpgraderDelegate SchemaUpgrader { get; set; }

        public Action<StorageEnvironment> BeforeSchemaUpgrade { get; set; }

        public ScratchSpaceUsageMonitor ScratchSpaceUsage { get; }

        public TimeSpan LongRunningFlushingWarning = TimeSpan.FromMinutes(5);

        public long MaxScratchBufferSize
        {
            get => _maxScratchBufferSize;
            set
            {
                if (value < 0)
                    throw new InvalidOperationException($"Cannot set {nameof(MaxScratchBufferSize)} to negative value: {value}");

                if (_forceUsing32BitsPager && _maxScratchBufferSize > 0)
                {
                    _maxScratchBufferSize = Math.Min(value, _maxScratchBufferSize);
                    return;
                }

                _maxScratchBufferSize = value;
            }
        }

        public bool OwnsPagers { get; set; }

        public bool ManualFlushing { get; set; }

        public bool IncrementalBackupEnabled { get; set; }

        public abstract AbstractPager DataPager { get; }

        public long MaxNumberOfPagesInJournalBeforeFlush { get; set; }

        public int IdleFlushTimeout { get; set; }

        public long? MaxStorageSize { get; set; }

        public abstract VoronPathSetting BasePath { get; }

        /// <summary>
        /// This mode is used in the Voron recovery tool and is not intended to be set otherwise.
        /// </summary>
        internal bool CopyOnWriteMode { get; set; }

        public abstract IJournalWriter CreateJournalWriter(long journalNumber, long journalSize);

        public abstract VoronPathSetting GetJournalPath(long journalNumber);

        protected bool Disposed;
        private long _initialLogFileSize;
        private long _maxLogFileSize;

        public Func<string, bool> ShouldUseKeyPrefix { get; set; }

        public Action<string> AddToInitLog;

        public event Action<StorageEnvironmentOptions> OnDirectoryInitialize;

        protected StorageEnvironmentOptions(VoronPathSetting tempPath, IoChangesNotifications ioChangesNotifications, CatastrophicFailureNotification catastrophicFailureNotification)
        {
            DisposeWaitTime = TimeSpan.FromSeconds(15);

            TempPath = tempPath;

            ShouldUseKeyPrefix = name => false;

            var shouldForceEnvVar = Environment.GetEnvironmentVariable("VORON_INTERNAL_ForceUsing32BitsPager");

            if (bool.TryParse(shouldForceEnvVar, out bool result))
                ForceUsing32BitsPager = result;


            bool shouldConfigPagersRunInLimitedMemoryEnvironment = PlatformDetails.Is32Bits || ForceUsing32BitsPager;
            MaxLogFileSize = ((shouldConfigPagersRunInLimitedMemoryEnvironment ? 4 : 256) * Constants.Size.Megabyte);            

            InitialLogFileSize = 64 * Constants.Size.Kilobyte;

            MaxScratchBufferSize = ((shouldConfigPagersRunInLimitedMemoryEnvironment ? 32 : 256) * Constants.Size.Megabyte);

            MaxNumberOfPagesInJournalBeforeFlush =
                ((shouldConfigPagersRunInLimitedMemoryEnvironment ? 4 : 32) * Constants.Size.Megabyte) / Constants.Storage.PageSize;

            IdleFlushTimeout = 5000; // 5 seconds

            OwnsPagers = true;

            IncrementalBackupEnabled = false;

            IoMetrics = ioChangesNotifications?.DisableIoMetrics == true ? 
                new IoMetrics(0, 0) : // disabled
                new IoMetrics(256, 256, ioChangesNotifications);

            _log = LoggingSource.Instance.GetLogger<StorageEnvironmentOptions>(tempPath.FullPath);

            _catastrophicFailureNotification = catastrophicFailureNotification ?? new CatastrophicFailureNotification((id, path, e, stacktrace) =>
            {
                if (_log.IsOperationsEnabled)
                    _log.Operations($"Catastrophic failure in {this}, StackTrace:'{stacktrace}'", e);
            });

            PrefetchSegmentSize = 4 * Constants.Size.Megabyte;
            PrefetchResetThreshold = shouldConfigPagersRunInLimitedMemoryEnvironment?256*(long)Constants.Size.Megabyte: 8 * (long)Constants.Size.Gigabyte;
            SyncJournalsCountThreshold = 2;

            ScratchSpaceUsage = new ScratchSpaceUsageMonitor();
        }

        public void SetCatastrophicFailure(ExceptionDispatchInfo exception)
        {
            _catastrophicFailureStack = Environment.StackTrace;
            _catastrophicFailure = exception;
            _catastrophicFailureNotification.RaiseNotificationOnce(_environmentId, ToString(), exception.SourceException, _catastrophicFailureStack);
        }

        public void InvokeRecoverableFailure(string failureMessage, Exception e)
        {
            var handler = OnRecoverableFailure;

            if (handler != null)
            {
                handler.Invoke(this, new RecoverableFailureEventArgs(failureMessage, _environmentId, ToString(), e));
            }
            else
            {
                if (_log.IsOperationsEnabled)
                    _log.Operations($"Recoverable failure in {this}. Error: {failureMessage}.", e);
            }
        }

        public bool IsCatastrophicFailureSet => _catastrophicFailure != null;

        internal void AssertNoCatastrophicFailure()
        {
            if (_catastrophicFailure == null)
                return;

            if (_skipCatastrophicFailureAssertion)
                return;

            if (_log.IsInfoEnabled)
                _log.Info($"CatastrophicFailure state, about to throw. Originally was set in the following stack trace : {_catastrophicFailureStack}");

            _catastrophicFailure.Throw(); // force re-throw of error
        }

        public IDisposable SkipCatastrophicFailureAssertion()
        {
            _skipCatastrophicFailureAssertion = true;

            return new DisposableAction(() => { _skipCatastrophicFailureAssertion = false; });
        }

        public static StorageEnvironmentOptions CreateMemoryOnly(string name, string tempPath, IoChangesNotifications ioChangesNotifications, CatastrophicFailureNotification catastrophicFailureNotification)
        {
            var tempPathSetting = new VoronPathSetting(tempPath ?? GetTempPath());
            return new PureMemoryStorageEnvironmentOptions(name, tempPathSetting, ioChangesNotifications, catastrophicFailureNotification);
        }

        public static StorageEnvironmentOptions CreateMemoryOnly()
        {
            return CreateMemoryOnly(null, null, null, null);
        }

        public static StorageEnvironmentOptions ForPath(string path, string tempPath, string journalPath, IoChangesNotifications ioChangesNotifications, CatastrophicFailureNotification catastrophicFailureNotification)
        {
            var pathSetting = new VoronPathSetting(path);
            var tempPathSetting = new VoronPathSetting(tempPath ?? GetTempPath(path));
            var journalPathSetting = journalPath != null ? new VoronPathSetting(journalPath) : pathSetting.Combine("Journals");

            return new DirectoryStorageEnvironmentOptions(pathSetting, tempPathSetting, journalPathSetting, ioChangesNotifications, catastrophicFailureNotification);
        }

        public static StorageEnvironmentOptions ForPath(string path)
        {
            return ForPath(path, null, null, null, null);
        }

        private static string GetTempPath(string basePath = null)
        {
            bool useSystemTemp = false;
            // We need to use a Temp directory for storage. There's two ways to do this: either the user provides a full path
            // to use as base (because they expect all temporary files to be stored under it too), or we use the current
            // running directory.
            string tempPath = Path.Combine(basePath ?? Directory.GetCurrentDirectory(), "Temp");
            try
            {
                Directory.CreateDirectory(tempPath);
            }
            catch (UnauthorizedAccessException)
            {
                useSystemTemp = true;
            }

            if (!useSystemTemp)
            {
                // Effective permissions are hard to compute, so we try to create a file and write to it as a check.
                try
                {
                    var tempFilePath = Path.Combine(tempPath, Guid.NewGuid().ToString());
                    File.Create(tempFilePath, 1024).Dispose();
                    File.Delete(tempFilePath);
                }
                catch (Exception)
                {
                    useSystemTemp = true;
                }

            }

            if (useSystemTemp)
                tempPath = Path.GetTempPath();

            return tempPath;
        }

        public class DirectoryStorageEnvironmentOptions : StorageEnvironmentOptions
        {
            public const string TempFileExtension = ".tmp";
            public const string BuffersFileExtension = ".buffers";

            private readonly VoronPathSetting _basePath;

            private readonly LazyWithExceptionRetry<AbstractPager> _dataPager;

            private readonly ConcurrentDictionary<string, LazyWithExceptionRetry<IJournalWriter>> _journals =
                new ConcurrentDictionary<string, LazyWithExceptionRetry<IJournalWriter>>(StringComparer.OrdinalIgnoreCase);

            public DirectoryStorageEnvironmentOptions(VoronPathSetting basePath, VoronPathSetting tempPath, VoronPathSetting journalPath,
                IoChangesNotifications ioChangesNotifications, CatastrophicFailureNotification catastrophicFailureNotification)
                : base(tempPath ?? basePath, ioChangesNotifications, catastrophicFailureNotification)
            {
                Debug.Assert(basePath != null);
                Debug.Assert(journalPath != null);

                _basePath = basePath;
                JournalPath = journalPath;

                if (Directory.Exists(_basePath.FullPath) == false)
                    Directory.CreateDirectory(_basePath.FullPath);

                if (Equals(_basePath, TempPath) == false && Directory.Exists(TempPath.FullPath) == false)
                    Directory.CreateDirectory(TempPath.FullPath);

                if (Equals(JournalPath, TempPath) == false && Directory.Exists(JournalPath.FullPath) == false)
                    Directory.CreateDirectory(JournalPath.FullPath);

                _dataPager = new LazyWithExceptionRetry<AbstractPager>(() =>
                {
                    FilePath = _basePath.Combine(Constants.DatabaseFilename);

                    return GetMemoryMapPager(this, InitialFileSize, FilePath, usePageProtection: true);
                });

                // have to be before the journal check, so we'll fail on files in use
                DeleteAllTempFiles();

                GatherRecyclableJournalFiles(); // if there are any (e.g. after a rude db shut down) let us reuse them

                InitializePathsInfo();
            }

            private void InitializePathsInfo()
            {
                DriveInfoByPath = new LazyWithExceptionRetry<DriveInfoByPath>(() =>
                {
                    var drivesInfo = PlatformDetails.RunningOnPosix ? DriveInfo.GetDrives() : null;
                    return new DriveInfoByPath
                    {
                        BasePath = DiskUtils.GetDriveInfo(BasePath.FullPath, drivesInfo, out _),
                        JournalPath = DiskUtils.GetDriveInfo(JournalPath.FullPath, drivesInfo, out _),
                        TempPath = DiskUtils.GetDriveInfo(TempPath.FullPath, drivesInfo, out _)
                    };
                });
            }

            private void GatherRecyclableJournalFiles()
            {
                foreach (var reusableFile in GetRecyclableJournalFiles())
                {
                    var reuseNameWithoutExt = Path.GetExtension(reusableFile).Substring(1);

                    long reuseNum;
                    if (long.TryParse(reuseNameWithoutExt, out reuseNum))
                    {
                        _reuseCounter = Math.Max(_reuseCounter, reuseNum);
                    }

                    try
                    {
                        var lastWriteTimeUtcTicks = new FileInfo(reusableFile).LastWriteTimeUtc.Ticks;

                        while (_journalsForReuse.ContainsKey(lastWriteTimeUtcTicks))
                        {
                            lastWriteTimeUtcTicks++;
                        }

                        _journalsForReuse[lastWriteTimeUtcTicks] = reusableFile;
                    }
                    catch (Exception ex)
                    {
                        if (_log.IsInfoEnabled)
                            _log.Info("On Storage Environment Options : Can't store journal for reuse : " + reusableFile, ex);
                        TryDelete(reusableFile);
                    }
                }
            }

            private string[] GetRecyclableJournalFiles()
            {
                try
                {
                    return Directory.GetFiles(JournalPath.FullPath, $"{RecyclableJournalFileNamePrefix}.*");
                }
                catch (Exception)
                {
                    return new string[0];
                }
            }

            public VoronPathSetting FilePath { get; private set; }

            public override string ToString()
            {
                return _basePath.FullPath;
            }

            public override AbstractPager DataPager => _dataPager.Value;

            public override VoronPathSetting BasePath => _basePath;

            public override AbstractPager OpenPager(VoronPathSetting filename)
            {
                return GetMemoryMapPagerInternal(this, null, filename);
            }

            public override IJournalWriter CreateJournalWriter(long journalNumber, long journalSize)
            {
                var name = JournalName(journalNumber);
                var path = JournalPath.Combine(name);
                if (File.Exists(path.FullPath) == false)
                    AttemptToReuseJournal(path, journalSize);

                var result = _journals.GetOrAdd(name, _ =>
                    new LazyWithExceptionRetry<IJournalWriter>(() => new JournalWriter(this, path, journalSize)));

                if (result.Value.Disposed)
                {
                    var newWriter = new LazyWithExceptionRetry<IJournalWriter>(() => new JournalWriter(this, path, journalSize));
                    if (_journals.TryUpdate(name, newWriter, result) == false)
                        throw new InvalidOperationException("Could not update journal pager");
                    result = newWriter;
                }

                return result.Value;
            }

            public override VoronPathSetting GetJournalPath(long journalNumber)
            {
                var name = JournalName(journalNumber);
                return JournalPath.Combine(name);
            }

            private static readonly long TickInHour = TimeSpan.FromHours(1).Ticks;

            public override void TryStoreJournalForReuse(VoronPathSetting filename)
            {
                var reusedCount = 0;
                var reusedLimit = Math.Min(_lastReusedJournalCountOnSync, MaxNumberOfRecyclableJournals);

                try
                {
                    var oldFileName = Path.GetFileName(filename.FullPath);
                    _journals.TryRemove(oldFileName, out _);

                    var fileModifiedDate = new FileInfo(filename.FullPath).LastWriteTimeUtc;
                    var counter = Interlocked.Increment(ref _reuseCounter);
                    var newName = Path.Combine(Path.GetDirectoryName(filename.FullPath), RecyclableJournalName(counter));
                    
                    File.Move(filename.FullPath, newName);
                    lock (_journalsForReuse)
                    {
                        reusedCount = _journalsForReuse.Count;

                        if (ShouldRemoveJournal())
                        {
                            if (File.Exists(newName))
                                File.Delete(newName);
                            return;
                        }

                        var ticks = fileModifiedDate.Ticks;

                        while (_journalsForReuse.ContainsKey(ticks))
                            ticks++;

                        _journalsForReuse[ticks] = newName;
                        
                    }
                }
                catch (Exception ex)
                {
                    if (_log.IsInfoEnabled)
                        _log.Info(ShouldRemoveJournal() ? "Can't remove" : "Can't store" + " journal for reuse : " + filename, ex);
                    try
                    {
                        if (File.Exists(filename.FullPath))
                            File.Delete(filename.FullPath);
                    }
                    catch
                    {
                        // nothing we can do about it
                    }
                }

                bool ShouldRemoveJournal()
                {
                    return reusedCount >= reusedLimit;
                }
            }

            public override int GetNumberOfJournalsForReuse()
            {
                return _journalsForReuse.Count;
            }

            private void AttemptToReuseJournal(VoronPathSetting desiredPath, long desiredSize)
            {
                lock (_journalsForReuse)
                {
                    var lastModified = DateTime.MinValue.Ticks;
                    while (_journalsForReuse.Count > 0)
                    {
                        lastModified = _journalsForReuse.Keys[_journalsForReuse.Count - 1];
                        var filename = _journalsForReuse.Values[_journalsForReuse.Count - 1];
                        _journalsForReuse.RemoveAt(_journalsForReuse.Count - 1);

                        try
                        {
                            var journalFile = new FileInfo(filename);
                            if (journalFile.Exists == false)
                                continue;

                            if (journalFile.Length > MaxLogFileSize && desiredSize <= MaxLogFileSize)
                            {
                                // delete journals that are bigger than MaxLogFileSize when tx desiredSize is smaller than MaxLogFileSize
                                TryDelete(filename);
                                continue;
                            }

                            journalFile.MoveTo(desiredPath.FullPath);
                            break;
                        }
                        catch (Exception ex)
                        {
                            TryDelete(filename);

                            if (_log.IsInfoEnabled)
                                _log.Info("Failed to rename " + filename + " to " + desiredPath, ex);
                        }
                    }

                    while (_journalsForReuse.Count > 0)
                    {
                        try
                        {
                            var fileInfo = new FileInfo(_journalsForReuse.Values[0]);
                            if (fileInfo.Exists == false)
                            {
                                _journalsForReuse.RemoveAt(0);
                                continue;
                            }

                            if (lastModified - fileInfo.LastWriteTimeUtc.Ticks > TickInHour * 72)
                            {
                                _journalsForReuse.RemoveAt(0);
                                TryDelete(fileInfo.FullName);
                                continue;
                            }

                            if (fileInfo.Length < desiredSize)
                            {
                                _journalsForReuse.RemoveAt(0);
                                TryDelete(fileInfo.FullName);

                                continue;
                            }

                            if (fileInfo.Length > MaxLogFileSize && desiredSize <= MaxLogFileSize)
                            {
                                _journalsForReuse.RemoveAt(0);
                                TryDelete(fileInfo.FullName);

                                continue;
                            }
                        }
                        catch (IOException)
                        {
                            // explicitly ignoring any such file errors
                            _journalsForReuse.RemoveAt(0);
                            TryDelete(_journalsForReuse.Values[0]);
                        }
                        break;
                    }

                }
            }

            protected override void Disposing()
            {
                if (Disposed)
                    return;

                Disposed = true;
                if (_dataPager.IsValueCreated)
                    _dataPager.Value.Dispose();
                foreach (var journal in _journals)
                {
                    if (journal.Value.IsValueCreated)
                        journal.Value.Value.Dispose();
                }

                lock (_journalsForReuse)
                {
                    foreach (var reusableFile in _journalsForReuse.Values)
                    {
                        TryDelete(reusableFile);
                    }
                }
            }

            public override bool JournalExists(long number)
            {
                var name = JournalName(number);
                var file = JournalPath.Combine(name);
                return File.Exists(file.FullPath);
            }

            public override bool TryDeleteJournal(long number)
            {
                var name = JournalName(number);

                if (_journals.TryRemove(name, out var lazy) && lazy.IsValueCreated)
                    lazy.Value.Dispose();

                var file = JournalPath.Combine(name);
                if (File.Exists(file.FullPath) == false)
                    return false;

                File.Delete(file.FullPath);

                return true;
            }

            public override unsafe bool ReadHeader(string filename, FileHeader* header)
            {
                var path = _basePath.Combine(filename);
                if (File.Exists(path.FullPath) == false)
                {
                    return false;
                }

                var success = RunningOnPosix ?
                    PosixHelper.TryReadFileHeader(header, path) :
                    Win32Helper.TryReadFileHeader(header, path);

                if (!success)
                    return false;

                return header->Hash == HeaderAccessor.CalculateFileHeaderHash(header);
            }


            public override unsafe void WriteHeader(string filename, FileHeader* header)
            {
                var path = _basePath.Combine(filename);
                var rc = Pal.rvn_write_header(path.FullPath, header, sizeof(FileHeader), out var errorCode);
                if (rc != PalFlags.FailCodes.Success)
                    PalHelper.ThrowLastError(rc, errorCode, $"Failed to rvn_write_header '{filename}', reason : {((PalFlags.FailCodes)rc).ToString()}");
            }

            public void DeleteAllTempFiles()
            {
                if (Directory.Exists(TempPath.FullPath) == false)
                    return;

                foreach (var file in Directory.GetFiles(TempPath.FullPath).Where(x => x.EndsWith(BuffersFileExtension, StringComparison.OrdinalIgnoreCase) || x.EndsWith(TempFileExtension, StringComparison.OrdinalIgnoreCase)))
                {
                    File.Delete(file);
                }
            }

            private AbstractPager GetTemporaryPager(string name, long initialSize, Func<StorageEnvironmentOptions, long?, VoronPathSetting, bool, bool, AbstractPager> getMemoryMapPagerFunc)
            {
                // here we can afford to rename the file if needed because this is a scratch / temp
                // file that is used. We _know_ that no one expects anything from it and that 
                // the name it uses isn't _that_ important in any way, shape or form. 
                int index = 0;
                void Rename()
                {
                    var ext = Path.GetExtension(name);
                    var filename = Path.GetFileNameWithoutExtension(name);
                    name = filename + "-ren-" + (index++) + ext;
                }
                Exception err = null;
                for (int i = 0; i < 15; i++)
                {
                    var tempFile = TempPath.Combine(name);
                    try
                    {
                        if (File.Exists(tempFile.FullPath))
                            File.Delete(tempFile.FullPath);
                    }
                    catch (IOException e)
                    {
                        // this can happen if someone is holding the file, shouldn't happen
                        // but might if there is some FS caching involved where it shouldn't
                        Rename();
                        err = e;
                        continue;
                    }
                    try
                    {
                        return getMemoryMapPagerFunc(this, initialSize, tempFile,
                            true, // deleteOnClose
                            false); //usePageProtection
                    }
                    catch (FileNotFoundException e)
                    {
                        // unique case, when file was previously deleted, but still exists. 
                        // This can happen on cifs mount, see RavenDB-10923
                        // if this is a temp file we can try recreate it in a different name
                        Rename();
                        err = e;
                    }
                }

                throw new InvalidOperationException("Unable to create temporary mapped file " + name + ", even after trying multiple times.", err);
            }

            public override AbstractPager CreateScratchPager(string name, long initialSize)
            {
                return GetTemporaryPager(name, initialSize, GetMemoryMapPager);
            }

            // This is used for special pagers that are used as temp buffers and don't 
            // require encryption: compression, recovery, lazyTxBuffer.
            public override AbstractPager CreateTemporaryBufferPager(string name, long initialSize)
            {
                var pager = GetTemporaryPager(name, initialSize, GetMemoryMapPagerInternal);

                if (Encryption.IsEnabled)
                {
                    // even though we don't need encryption here, we still need to ensure that this
                    // isn't paged to disk
                    pager.LockMemory = true;
                    pager.DoNotConsiderMemoryLockFailureAsCatastrophicError = DoNotConsiderMemoryLockFailureAsCatastrophicError;

                    // We need to call SetPagerState after setting LockMemory = true to ensure the initial temp buffer is protected.
                    pager.SetPagerState(pager.PagerState);
                }
                return pager;
            }


            private AbstractPager GetMemoryMapPager(StorageEnvironmentOptions options, long? initialSize, VoronPathSetting file,
                bool deleteOnClose = false,
                bool usePageProtection = false)
            {
                var pager = GetMemoryMapPagerInternal(options, initialSize, file, deleteOnClose, usePageProtection);

                return Encryption.IsEnabled == false
                    ? pager
                    : new CryptoPager(pager);
            }

            private AbstractPager GetMemoryMapPagerInternal(StorageEnvironmentOptions options, long? initialSize, VoronPathSetting file, bool deleteOnClose = false, bool usePageProtection = false)
            {
                if (RunningOnPosix)
                {
                    if (RunningOn32Bits)
                    {
                        return new Posix32BitsMemoryMapPager(options, file, initialSize,
                            usePageProtection: usePageProtection)
                        {
                            DeleteOnClose = deleteOnClose
                        };
                    }

                    return new RvnMemoryMapPager(options, file, initialSize, usePageProtection: usePageProtection, deleteOnClose: deleteOnClose);
                }

                var attributes = deleteOnClose
                    ? Win32NativeFileAttributes.DeleteOnClose | Win32NativeFileAttributes.Temporary | Win32NativeFileAttributes.RandomAccess
                    : Win32NativeFileAttributes.Normal;

                if (RunningOn32Bits)
                    return new Windows32BitsMemoryMapPager(options, file, initialSize, attributes, usePageProtection: usePageProtection);

                return new WindowsMemoryMapPager(options, file, initialSize, attributes, usePageProtection: usePageProtection);
            }

            public override AbstractPager OpenJournalPager(long journalNumber, JournalInfo journalInfo)
            {
                var name = JournalName(journalNumber);
                var path = JournalPath.Combine(name);
                var fileInfo = new FileInfo(path.FullPath);
                if (fileInfo.Exists == false)
                    throw new InvalidJournalException(journalNumber, path.FullPath, journalInfo);

                if (fileInfo.Length < InitialLogFileSize)
                {
                    EnsureMinimumSize(fileInfo, path);
                }

                if (RunningOnPosix)
                {
                    if (RunningOn32Bits)
                        return new Posix32BitsMemoryMapPager(this, path);
                    return new RvnMemoryMapPager(this, path);
                }

                if (RunningOn32Bits)
                    return new Windows32BitsMemoryMapPager(this, path, access: Win32NativeFileAccess.GenericRead,
                        fileAttributes: Win32NativeFileAttributes.SequentialScan);

                var windowsMemoryMapPager = new WindowsMemoryMapPager(this, path, access: Win32NativeFileAccess.GenericRead,
                    fileAttributes: Win32NativeFileAttributes.SequentialScan);
                windowsMemoryMapPager.TryPrefetchingWholeFile();
                return windowsMemoryMapPager;
            }

            private void EnsureMinimumSize(FileInfo fileInfo, VoronPathSetting path)
            {
                try
                {
                    using (var stream = fileInfo.Open(FileMode.OpenOrCreate))
                    {
                        stream.SetLength(InitialLogFileSize);
                    }
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException(
                        "Journal file " + path + " could not be opened because it's size is too small and we couldn't increase it",
                        e);
                }
            }
        }

        public class PureMemoryStorageEnvironmentOptions : StorageEnvironmentOptions
        {
            private readonly string _name;
            private static int _counter;

            private readonly LazyWithExceptionRetry<AbstractPager> _dataPager;

            private readonly Dictionary<string, IJournalWriter> _logs =
                new Dictionary<string, IJournalWriter>(StringComparer.OrdinalIgnoreCase);

            private readonly Dictionary<string, IntPtr> _headers =
                new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
            private readonly int _instanceId;



            public PureMemoryStorageEnvironmentOptions(string name, VoronPathSetting tempPath,
                IoChangesNotifications ioChangesNotifications, CatastrophicFailureNotification catastrophicFailureNotification)
                : base(tempPath, ioChangesNotifications, catastrophicFailureNotification)
            {
                _name = name;
                _instanceId = Interlocked.Increment(ref _counter);
                var guid = Guid.NewGuid();
                using (var currentProcess = Process.GetCurrentProcess())
                {
                    var filename = $"ravendb-{currentProcess.Id}-{_instanceId}-data.pager-{guid}";

                    if (Directory.Exists(tempPath.FullPath) == false)
                        Directory.CreateDirectory(tempPath.FullPath);

                    _dataPager = new LazyWithExceptionRetry<AbstractPager>(() => GetTempMemoryMapPager(this, TempPath.Combine(filename), InitialFileSize,
                        Win32NativeFileAttributes.RandomAccess | Win32NativeFileAttributes.DeleteOnClose | Win32NativeFileAttributes.Temporary));
                }
            }

            public override string ToString()
            {
                return "mem #" + _instanceId + " " + _name;
            }

            public override AbstractPager DataPager => _dataPager.Value;

            public override VoronPathSetting BasePath { get; } = new MemoryVoronPathSetting();

            public override IJournalWriter CreateJournalWriter(long journalNumber, long journalSize)
            {
                var name = JournalName(journalNumber);
                IJournalWriter value;
                if (_logs.TryGetValue(name, out value))
                    return value;

                var path = GetJournalPath(journalNumber);

                value = new JournalWriter(this, path, journalSize, PalFlags.JournalMode.PureMemory);

                _logs[name] = value;
                return value;
            }

            public override VoronPathSetting GetJournalPath(long journalNumber)
            {
                var name = JournalName(journalNumber);
                using (var currentProcess = Process.GetCurrentProcess())
                {
                    var filename = $"ravendb-{currentProcess.Id}-{_instanceId}-{name}-{Guid.NewGuid()}";

                    return TempPath.Combine(filename);
                }
            }

            public override void TryStoreJournalForReuse(VoronPathSetting filename)
            {
            }

            public override int GetNumberOfJournalsForReuse()
            {
                return 0;
            }

            protected override void Disposing()
            {
                if (Disposed)
                    return;
                Disposed = true;

                _dataPager.Value.Dispose();
                foreach (var virtualPager in _logs)
                {
                    virtualPager.Value.Dispose();
                }

                foreach (var headerSpace in _headers)
                {
                    Marshal.FreeHGlobal(headerSpace.Value);
                }

                _headers.Clear();
            }

            public override bool JournalExists(long number)
            {
                var name = JournalName(number);
                return _logs.ContainsKey(name);
            }

            public override bool TryDeleteJournal(long number)
            {
                var name = JournalName(number);
                IJournalWriter value;
                if (_logs.TryGetValue(name, out value) == false)
                    return false;
                _logs.Remove(name);
                value.Dispose();
                return true;
            }

            public override unsafe bool ReadHeader(string filename, FileHeader* header)
            {
                if (Disposed)
                    throw new ObjectDisposedException("PureMemoryStorageEnvironmentOptions");
                IntPtr ptr;
                if (_headers.TryGetValue(filename, out ptr) == false)
                {
                    return false;
                }
                *header = *((FileHeader*)ptr);

                return header->Hash == HeaderAccessor.CalculateFileHeaderHash(header);
            }

            public override unsafe void WriteHeader(string filename, FileHeader* header)
            {
                if (Disposed)
                    throw new ObjectDisposedException("PureMemoryStorageEnvironmentOptions");

                IntPtr ptr;
                if (_headers.TryGetValue(filename, out ptr) == false)
                {
                    ptr = (IntPtr)NativeMemory.AllocateMemory(sizeof(FileHeader));
                    _headers[filename] = ptr;
                }
                Memory.Copy((byte*)ptr, (byte*)header, sizeof(FileHeader));
            }

            private AbstractPager GetTempMemoryMapPager(PureMemoryStorageEnvironmentOptions options, VoronPathSetting path, long? intialSize, Win32NativeFileAttributes win32NativeFileAttributes)
            {
                var pager = GetTempMemoryMapPagerInternal(options, path, intialSize,
                    Win32NativeFileAttributes.RandomAccess | Win32NativeFileAttributes.DeleteOnClose | Win32NativeFileAttributes.Temporary);
                return Encryption.IsEnabled == false
                    ? pager
                    : new CryptoPager(pager);
            }

            private AbstractPager GetTempMemoryMapPagerInternal(PureMemoryStorageEnvironmentOptions options, VoronPathSetting path, long? intialSize, Win32NativeFileAttributes win32NativeFileAttributes)
            {
                if (RunningOnPosix)
                {
                    if (RunningOn32Bits)
                        return new PosixTempMemoryMapPager(options, path, intialSize); // need to change to 32 bit pager

                    return new PosixTempMemoryMapPager(options, path, intialSize);
                }
                if (RunningOn32Bits)
                    return new Windows32BitsMemoryMapPager(options, path, intialSize,
                        Win32NativeFileAttributes.RandomAccess | Win32NativeFileAttributes.DeleteOnClose | Win32NativeFileAttributes.Temporary);

                return new WindowsMemoryMapPager(options, path, intialSize,
                        Win32NativeFileAttributes.RandomAccess | Win32NativeFileAttributes.DeleteOnClose | Win32NativeFileAttributes.Temporary);
            }

            public override AbstractPager CreateScratchPager(string name, long initialSize)
            {
                var guid = Guid.NewGuid();
                using (var currentProcess = Process.GetCurrentProcess())
                {
                    var filename = $"ravendb-{currentProcess.Id}-{_instanceId}-{name}-{guid}";

                    return GetTempMemoryMapPager(this, TempPath.Combine(filename), initialSize,
                        Win32NativeFileAttributes.RandomAccess | Win32NativeFileAttributes.DeleteOnClose | Win32NativeFileAttributes.Temporary);
                }
            }

            public override AbstractPager CreateTemporaryBufferPager(string name, long initialSize)
            {
                var guid = Guid.NewGuid();
                using (var currentProcess = Process.GetCurrentProcess())
                {
                    var filename = $"ravendb-{currentProcess.Id}-{_instanceId}-{name}-{guid}";

                    return GetTempMemoryMapPagerInternal(this, TempPath.Combine(filename), initialSize,
                        Win32NativeFileAttributes.RandomAccess | Win32NativeFileAttributes.DeleteOnClose | Win32NativeFileAttributes.Temporary);
                }
            }

            public override AbstractPager OpenPager(VoronPathSetting filename)
            {
                var pager = OpenPagerInternal(filename);

                return Encryption.IsEnabled == false ? pager : new CryptoPager(pager);
            }

            private AbstractPager OpenPagerInternal(VoronPathSetting filename)
            {
                if (RunningOnPosix)
                {
                    if (RunningOn32Bits)
                        return new Posix32BitsMemoryMapPager(this, filename);
                    return new RvnMemoryMapPager(this, filename);
                }

                if (RunningOn32Bits)
                    return new Windows32BitsMemoryMapPager(this, filename);

                return new WindowsMemoryMapPager(this, filename);
            }

            public override AbstractPager OpenJournalPager(long journalNumber, JournalInfo journalInfo)
            {
                var name = JournalName(journalNumber);
                IJournalWriter value;
                if (_logs.TryGetValue(name, out value))
                    return value.CreatePager();
                throw new InvalidJournalException(journalNumber, journalInfo);
            }
        }

        public static string JournalName(long number)
        {
            return string.Format("{0:D19}.journal", number);
        }

        public static string RecyclableJournalName(long number)
        {
            return $"{RecyclableJournalFileNamePrefix}.{number:D19}";
        }

        public static string JournalRecoveryName(long number)
        {
            return string.Format("{0:D19}.recovery", number);
        }

        public static string ScratchBufferName(long number)
        {
            return $"scratch.{number:D10}{DirectoryStorageEnvironmentOptions.BuffersFileExtension}";
        }

        public void Dispose()
        {
            NullifyHandlers();

            Encryption.Dispose();

            ScratchSpaceUsage?.Dispose();

            Disposing();
        }

        public void NullifyHandlers()
        {
            SchemaUpgrader = null;
            OnRecoveryError = null;
            OnNonDurableFileSystemError = null;
            OnIntegrityErrorOfAlreadySyncedData = null;
            OnRecoverableFailure = null;
        }

        protected abstract void Disposing();

        public abstract bool JournalExists(long number);

        public abstract bool TryDeleteJournal(long number);

        public abstract unsafe bool ReadHeader(string filename, FileHeader* header);

        public abstract unsafe void WriteHeader(string filename, FileHeader* header);

        public abstract AbstractPager CreateScratchPager(string name, long initialSize);

        // Used for special temporary pagers (compression, recovery, lazyTX...) which should not be wrapped by the crypto pager.
        public abstract AbstractPager CreateTemporaryBufferPager(string name, long initialSize);

        public abstract AbstractPager OpenJournalPager(long journalNumber, JournalInfo journalInfo);

        public abstract AbstractPager OpenPager(VoronPathSetting filename);

        public bool DoNotConsiderMemoryLockFailureAsCatastrophicError;

        public static bool RunningOnPosix
            => RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
               RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        public bool RunningOn32Bits => PlatformDetails.Is32Bits || ForceUsing32BitsPager;


        public TransactionsMode TransactionsMode { get; set; }
        public PalFlags.DurabilityMode SupportDurabilityFlags { get; set; } = PalFlags.DurabilityMode.DurabililtySupported;

        public DateTime? NonSafeTransactionExpiration { get; set; }
        public TimeSpan DisposeWaitTime { get; set; }

        public int TimeToSyncAfterFlushInSec
        {
            get
            {
                if (_timeToSyncAfterFlushInSec < 1)
                    _timeToSyncAfterFlushInSec = 30;
                return _timeToSyncAfterFlushInSec;
            }
            set => _timeToSyncAfterFlushInSec = value;
        }

        public long PrefetchSegmentSize { get; set; }
        public long PrefetchResetThreshold { get; set; }
        public long SyncJournalsCountThreshold { get; set; }

        internal bool SimulateFailureOnDbCreation { get; set; }
        internal bool ManualSyncing { get; set; } = false;
        public bool? IgnoreInvalidJournalErrors { get; set; }
        public bool IgnoreDataIntegrityErrorsOfAlreadySyncedTransactions { get; set; }
        public bool SkipChecksumValidationOnDatabaseLoading { get; set; }

        public int MaxNumberOfRecyclableJournals { get; set; } = 32;
        public bool DiscardVirtualMemory { get; set; } = true;
        
        private readonly Logger _log;

        private readonly SortedList<long, string> _journalsForReuse = new SortedList<long, string>();

        private int _timeToSyncAfterFlushInSec;
        public long CompressTxAboveSizeInBytes;
        private Guid _environmentId;
        private long _maxScratchBufferSize;

        public abstract void TryStoreJournalForReuse(VoronPathSetting filename);

        public abstract int GetNumberOfJournalsForReuse();

        private void TryDelete(string file)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex)
            {
                if (_log.IsInfoEnabled)
                    _log.Info("Failed to delete " + file, ex);
            }
        }

        public void TryCleanupRecycledJournals()
        {
            if (Monitor.TryEnter(_journalsForReuse, 10) == false)
                return;

            try
            {
                foreach (var recyclableJournal in _journalsForReuse)
                {
                    try
                    {
                        var fileInfo = new FileInfo(recyclableJournal.Value);

                        if (fileInfo.Exists)
                            TryDelete(fileInfo.FullName);
                    }
                    catch (Exception ex)
                    {
                        if (_log.IsInfoEnabled)
                            _log.Info($"Couldn't delete recyclable journal: {recyclableJournal.Value}", ex);
                    }
                }

                _journalsForReuse.Clear();
            }
            finally
            {
                Monitor.Exit(_journalsForReuse);
            }
        }

        public void SetEnvironmentId(Guid environmentId)
        {
            _environmentId = environmentId;
        }

        public void InvokeOnDirectoryInitialize()
        {
            OnDirectoryInitialize?.Invoke(this);
        }

        public void SetDurability()
        {
            if (BasePath != null)
            {
                string testFile = Path.Combine(BasePath.FullPath, "test-" + Guid.NewGuid() + ".tmp");
                var rc = Pal.rvn_test_storage_durability(testFile, out var errorCode);
                switch (rc)
                {
                    case PalFlags.FailCodes.FailOpenFile:
                        {
                            if (_log.IsInfoEnabled)
                                _log.Info(
                                    $"Failed to create test file at '{testFile}'. Error:'{PalHelper.GetNativeErrorString(errorCode, "Failed to open test file", out _)}'. Cannot determine if O_DIRECT supported by the file system. Assuming it is");
                        }
                        break;

                    case PalFlags.FailCodes.FailAllocFile:
                        {
                            if (_log.IsInfoEnabled)
                                _log.Info(
                                    $"Failed to allocate test file at '{testFile}'. Error:'{PalHelper.GetNativeErrorString(errorCode, "Failed to allocate space for test file", out _)}'. Cannot determine if O_DIRECT supported by the file system. Assuming it is");
                        }
                        break;

                    case PalFlags.FailCodes.FailTestDurability:
                        {
                            SupportDurabilityFlags = PalFlags.DurabilityMode.DurabilityNotSupported;

                            var message = "Path " + BasePath +
                                          " not supporting O_DIRECT writes. As a result - data durability is not guaranteed";
                            var details =
                                $"Storage type '{PosixHelper.GetFileSystemOfPath(BasePath.FullPath)}' doesn't support direct write to disk (non durable file system)";
                            InvokeNonDurableFileSystemError(this, message, new NonDurableFileSystemException(message), details);
                        }
                        break;
                    case PalFlags.FailCodes.Success:
                        break;
                    default:
                        if (_log.IsInfoEnabled)
                            _log.Info(
                                $"Unknown failure on test file at '{testFile}'. Error:'{PalHelper.GetNativeErrorString(errorCode, "Unknown error while testing O_DIRECT", out _)}'. Cannot determine if O_DIRECT supported by the file system. Assuming it is");
                        break;
                }
            }
        }

        public void TrackCryptoPager(CryptoPager cryptoPager)
        {
            _activeCryptoPagers.Add(cryptoPager);
        }

        public void UntrackCryptoPager(CryptoPager cryptoPager)
        {
            _activeCryptoPagers.TryRemove(cryptoPager);
        }

        public ConcurrentSet<CryptoPager> GetActiveCryptoPagers()
        {
            return _activeCryptoPagers;
        }

        public class StorageEncryptionOptions : IDisposable
        {
            private IJournalCompressionBufferCryptoHandler _journalCompressionBufferHandler;

            public IJournalCompressionBufferCryptoHandler JournalCompressionBufferHandler
            {
                get
                {
                    if (HasExternalJournalCompressionBufferHandlerRegistration == false)
                        throw new InvalidOperationException($"You have to {nameof(RegisterForJournalCompressionHandler)} before you try to access {nameof(JournalCompressionBufferHandler)}");
                    
                    return _journalCompressionBufferHandler;
                }
                private set => _journalCompressionBufferHandler = value;
            }

            public byte[] MasterKey;

            public bool IsEnabled => MasterKey != null;

            public EncryptionBuffersPool EncryptionBuffersPool = EncryptionBuffersPool.Instance;

            public bool HasExternalJournalCompressionBufferHandlerRegistration { get; private set; }

            public void RegisterForJournalCompressionHandler()
            {
                if (IsEnabled == false)
                    return;

                HasExternalJournalCompressionBufferHandlerRegistration = true;
            }

            public void SetExternalCompressionBufferHandler(IJournalCompressionBufferCryptoHandler handler)
            {
                JournalCompressionBufferHandler = handler;
            }

            public unsafe void Dispose()
            {
                var copy = MasterKey;
                if (copy != null)
                {
                    fixed (byte* key = copy)
                    {
                        Sodium.sodium_memzero(key, (UIntPtr)copy.Length);
                        MasterKey = null;
                    }
                }
            }
        }

        internal TestingStuff ForTestingPurposes;

        internal TestingStuff ForTestingPurposesOnly()
        {
            if (ForTestingPurposes != null)
                return ForTestingPurposes;

            return ForTestingPurposes = new TestingStuff();
        }

        internal class TestingStuff
        {
            public int? WriteToJournalCompressionAcceleration = null;
        }
    }
}
