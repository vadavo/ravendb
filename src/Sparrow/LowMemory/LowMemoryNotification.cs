﻿using System;
using System.Collections.Generic;
using System.Threading;
using Sparrow.Collections;
using Sparrow.Logging;
using Sparrow.Platform;
using Sparrow.Utils;

namespace Sparrow.LowMemory
{
    public class LowMemoryNotification
    {
        private const string NotificationThreadName = "Low memory notification thread";

        private readonly Logger _logger;

        private readonly ConcurrentSet<WeakReference<ILowMemoryHandler>> _lowMemoryHandlers = new ConcurrentSet<WeakReference<ILowMemoryHandler>>();

        internal enum LowMemReason
        {
            None = 0,
            LowMemOnTimeoutChk,
            BackToNormal,
            BackToNormalSimulation,
            LowMemStateSimulation,
            BackToNormalHandler,
            LowMemHandler
        }

        internal class LowMemEventDetails
        {
            public LowMemReason Reason;
            public long FreeMem;
            public DateTime Time;
            public long CurrentCommitCharge { get; set; }
            public long PhysicalMem { get; set; }
            public long TotalUnmanaged { get; set; }
            public long TotalScratchDirty { get; set; }
            public long LowMemThreshold { get; set; }
        }

        internal LowMemEventDetails[] LowMemEventDetailsStack = new LowMemEventDetails[256];
        private int _lowMemEventDetailsIndex;
        private int _clearInactiveHandlersCounter;
        private bool _wasLowMemory;
        private DateTime _lastLoggedLowMemory = DateTime.MinValue;
        private readonly TimeSpan _logLowMemoryInterval = TimeSpan.FromSeconds(5);

        private void RunLowMemoryHandlers(bool isLowMemory, MemoryInfoResult memoryInfo, LowMemorySeverity lowMemorySeverity = LowMemorySeverity.ExtremelyLow)
        {
            try
            {
                try
                {
                    var now = DateTime.UtcNow;
                    if (isLowMemory && _logger.IsOperationsEnabled && now - _lastLoggedLowMemory > _logLowMemoryInterval)
                    {
                        _lastLoggedLowMemory = now;
                        _logger.Operations($"Running {_lowMemoryHandlers.Count} low memory handlers with severity: {lowMemorySeverity}. " +
                                           $"{MemoryUtils.GetExtendedMemoryInfo(memoryInfo)}");
                    }
                }
                catch
                {
                    // can fail because of oom error
                }

                foreach (var lowMemoryHandler in _lowMemoryHandlers)
                {
                    if (lowMemoryHandler.TryGetTarget(out var handler))
                    {
                        try
                        {
                            if (isLowMemory)
                                handler.LowMemory(lowMemorySeverity);
                            else if (_wasLowMemory)
                                handler.LowMemoryOver();
                        }
                        catch (Exception e)
                        {
                            try
                            {
                                if (_logger.IsInfoEnabled)
                                    _logger.Info("Failure to process low memory notification (low memory handler - " + handler + ")", e);
                            }
                            catch
                            {
                                // ignored
                            }
                        }
                    }
                    else
                    {
                        // make sure that we aren't allocating here, we reserve 128 items
                        // and worst case we'll get it in the next run
                        if (_inactiveHandlers.Count < _inactiveHandlers.Capacity)
                            _inactiveHandlers.Add(lowMemoryHandler);
                    }
                }
                foreach (var x in _inactiveHandlers)
                {
                    if (x == null)
                        continue;
                    _lowMemoryHandlers.TryRemove(x);
                }
            }
            finally
            {
                _wasLowMemory = isLowMemory;
                _inactiveHandlers.Clear();
            }
        }

        private void ClearInactiveHandlers()
        {
            var inactiveHandlers = new List<WeakReference<ILowMemoryHandler>>();

            foreach (var lowMemoryHandler in _lowMemoryHandlers)
            {
                if (lowMemoryHandler.TryGetTarget(out _) == false)
                    inactiveHandlers.Add(lowMemoryHandler);
            }

            inactiveHandlers.ForEach(x => _lowMemoryHandlers.TryRemove(x));
        }

        public void RegisterLowMemoryHandler(ILowMemoryHandler handler)
        {
            _lowMemoryHandlers.Add(new WeakReference<ILowMemoryHandler>(handler));
        }

        public static readonly LowMemoryNotification Instance = new LowMemoryNotification();

        public bool LowMemoryState { get; private set; }

        public bool IsEarlyOutOfMemory { get; private set; }

        public DirtyMemoryState DirtyMemoryState { get; private set; } = new DirtyMemoryState {IsHighDirty = false};

        public Size LowMemoryThreshold { get; private set; }

        public Size ExtremelyLowMemoryThreshold { get; private set; }

        public bool UseTotalDirtyMemInsteadOfMemUsage { get; private set; }

        public float TemporaryDirtyMemoryAllowedPercentage { get; private set; }

        public void Initialize(
            Size lowMemoryThreshold, 
            bool useTotalDirtyMemInsteadOfMemUsage,
            bool enableHighTemporaryDirtyMemoryUse,
            float temporaryDirtyMemoryAllowedPercentage,
            AbstractLowMemoryMonitor monitor,
            CancellationToken shutdownNotification)
        {
            if (_initialized)
                return;

            lock (this)
            {
                if (_initialized)
                    return;

                _initialized = true;
                LowMemoryThreshold = lowMemoryThreshold;
                ExtremelyLowMemoryThreshold = lowMemoryThreshold * 0.2;
                UseTotalDirtyMemInsteadOfMemUsage = useTotalDirtyMemInsteadOfMemUsage;
                TemporaryDirtyMemoryAllowedPercentage = temporaryDirtyMemoryAllowedPercentage;
                _enableHighTemporaryDirtyMemoryUse = enableHighTemporaryDirtyMemoryUse;

                _lowMemoryMonitor = monitor;

                var thread = new Thread(MonitorMemoryUsage)
                {
                    IsBackground = true,
                    Name = NotificationThreadName
                };

                thread.Start();

                _cancellationTokenRegistration = shutdownNotification.Register(() => _shutdownRequested.Set());
            }
        }

        private readonly ManualResetEvent _simulatedLowMemory = new ManualResetEvent(false);
        private readonly ManualResetEvent _shutdownRequested = new ManualResetEvent(false);
        private readonly List<WeakReference<ILowMemoryHandler>> _inactiveHandlers = new List<WeakReference<ILowMemoryHandler>>(128);
        private AbstractLowMemoryMonitor _lowMemoryMonitor;
        private bool _initialized;
        private bool _enableHighTemporaryDirtyMemoryUse;
        private CancellationTokenRegistration _cancellationTokenRegistration;

        internal LowMemoryNotification()
        {
            _logger = LoggingSource.Instance.GetLogger<LowMemoryNotification>("Server");
        }

        private void MonitorMemoryUsage()
        {
            NativeMemory.EnsureRegistered();
            var memoryAvailableHandles = new WaitHandle[] { _simulatedLowMemory, _shutdownRequested };
            var timeout = 5 * 1000;
            try
            {
                while (true)
                {
                    try
                    {
                        var result = WaitHandle.WaitAny(memoryAvailableHandles, timeout);
                        switch (result)
                        {
                            case WaitHandle.WaitTimeout:
                                timeout = CheckMemoryStatus(_lowMemoryMonitor);
                                break;
                            case 0:
                                SimulateLowMemory();
                                timeout = 1000; // on EarlyOOM just run cleaners once (CheckMemoryStatus will run in 1000mSec and will return system to normal)
                                break;
                            case 1: // shutdown requested
                                _cancellationTokenRegistration.Dispose();
                                return;
                            default:
                                return;
                        }
                    }
                    catch (OutOfMemoryException)
                    {
                        try
                        {
                            if (_logger.IsInfoEnabled)
                                _logger.Info("Out of memory error in the low memory notification thread, will wait 5 seconds before trying to check memory status again. The system is likely running out of memory");
                        }
                        catch
                        {
                            // can't even log, nothing we can do here
                        }

                        if (_shutdownRequested.WaitOne(5000))
                            return; // shutdown requested
                    }
                }
            }
            catch (Exception e)
            {
                if (_logger.IsOperationsEnabled)
                {
                    _logger.Operations("Catastrophic failure in low memory notification", e);
                }
            }
        }

        private void SimulateLowMemory()
        {
            _simulatedLowMemory.Reset();
            LowMemoryState = !LowMemoryState;

            MemoryInfoResult memInfoForLog = default;

            if (_lowMemoryMonitor != null)
            {
                memInfoForLog = _lowMemoryMonitor.GetMemoryInfoOnce();
                var availableMemForLog = memInfoForLog.AvailableMemoryForProcessing.GetValue(SizeUnit.Bytes);

                AddLowMemEvent(LowMemoryState ? LowMemReason.LowMemStateSimulation : LowMemReason.BackToNormalSimulation,
                    availableMemForLog,
                    -2,
                    memInfoForLog.TotalScratchDirtyMemory.GetValue(SizeUnit.Bytes),
                    memInfoForLog.TotalPhysicalMemory.GetValue(SizeUnit.Bytes),
                    memInfoForLog.CurrentCommitCharge.GetValue(SizeUnit.Bytes));
            }

            if (_logger.IsInfoEnabled)
                _logger.Info("Simulating : " + (LowMemoryState ? "Low memory event" : "Back to normal memory usage"));

            RunLowMemoryHandlers(LowMemoryState, memInfoForLog, LowMemorySeverity.ExtremelyLow);
        }

        internal int CheckMemoryStatus(AbstractLowMemoryMonitor monitor)
        {
            int timeout;
            LowMemorySeverity isLowMemory;
            long totalUnmanagedAllocations;
            MemoryInfoResult memoryInfo;

            try
            {
                if (_enableHighTemporaryDirtyMemoryUse)
                    DirtyMemoryState = monitor.GetDirtyMemoryState();

                totalUnmanagedAllocations = AbstractLowMemoryMonitor.GetUnmanagedAllocationsInBytes();
                isLowMemory = GetLowMemory(out memoryInfo, monitor);
            }
            catch (OutOfMemoryException)
            {
                isLowMemory = LowMemorySeverity.ExtremelyLow;
                memoryInfo = default;
                totalUnmanagedAllocations = -1;
            }
            if (isLowMemory != LowMemorySeverity.None)
            {
                if (LowMemoryState == false)
                {
                    try
                    {
                        if (_logger.IsInfoEnabled)
                        {

                            _logger.Info("Low memory detected, will try to reduce memory usage...");

                        }
                        AddLowMemEvent(LowMemReason.LowMemOnTimeoutChk,
                            memoryInfo.AvailableMemory.GetValue(SizeUnit.Bytes),
                            totalUnmanagedAllocations,
                            memoryInfo.TotalScratchDirtyMemory.GetValue(SizeUnit.Bytes),
                            memoryInfo.TotalPhysicalMemory.GetValue(SizeUnit.Bytes),
                            memoryInfo.CurrentCommitCharge.GetValue(SizeUnit.Bytes));
                    }
                    catch (OutOfMemoryException)
                    {
                        // nothing we can do, we'll wait and try again
                    }
                }
                LowMemoryState = true;

                timeout = 500;

                if (isLowMemory == LowMemorySeverity.Low &&
                    (PlatformDetails.RunningOnLinux == false || PlatformDetails.RunningOnMacOsx))
                {
                    isLowMemory = LowMemorySeverity.ExtremelyLow; // On linux we want two severity steps
                }
                _clearInactiveHandlersCounter = 0;
                RunLowMemoryHandlers(true, memoryInfo, isLowMemory);
            }
            else
            {
                if (LowMemoryState)
                {
                    if (_logger.IsInfoEnabled)
                        _logger.Info("Back to normal memory usage detected");
                    AddLowMemEvent(LowMemReason.BackToNormal,
                        memoryInfo.AvailableMemory.GetValue(SizeUnit.Bytes),
                        totalUnmanagedAllocations,
                        memoryInfo.TotalScratchDirtyMemory.GetValue(SizeUnit.Bytes),
                        memoryInfo.TotalPhysicalMemory.GetValue(SizeUnit.Bytes),
                        memoryInfo.CurrentCommitCharge.GetValue(SizeUnit.Bytes));
                }
                LowMemoryState = false;
                RunLowMemoryHandlers(false, memoryInfo);
                timeout = memoryInfo.AvailableMemory < LowMemoryThreshold * 2 ? 1000 : 5000;
            }

            return timeout;
        }

        private LowMemorySeverity GetLowMemory(out MemoryInfoResult memoryInfo, AbstractLowMemoryMonitor monitor)
        {
            if (++_clearInactiveHandlersCounter > 60) // 5 minutes == WaitAny 5 Secs * 60
            {
                _clearInactiveHandlersCounter = 0;
                ClearInactiveHandlers();
            }

            memoryInfo = monitor.GetMemoryInfo();
            var isLowMemory = IsLowMemory(memoryInfo, monitor, out _);

            // memInfo.AvailableMemory is updated in IsLowMemory for Linux (adding shared clean)
            return isLowMemory;
        }

        internal LowMemorySeverity IsLowMemory(MemoryInfoResult memInfo, AbstractLowMemoryMonitor monitor, out Size commitChargeThreshold)
        {
            // We consider low memory only if we don't have enough free physical memory or
            // the commited memory size if larger than our physical memory.
            // This is to ensure that from one hand we don't hit the disk to do page faults and from the other hand
            // we don't want to stay in low memory due to retained memory.
            var isLowMemory = IsAvailableMemoryBelowThreshold(memInfo);
            if (isLowMemory != LowMemorySeverity.None && memInfo.IsExtended == false && PlatformDetails.RunningOnMacOsx == false)
            {
                // getting extendedInfo (for windows: Process.GetCurrentProcess) or using the smaps might be expensive
                // we'll do it if we suspect low memory
                memInfo = monitor.GetMemoryInfo(extended: true);
                isLowMemory = IsAvailableMemoryBelowThreshold(memInfo);
            }

            IsEarlyOutOfMemory = monitor.IsEarlyOutOfMemory(memInfo, out commitChargeThreshold);
            if (IsEarlyOutOfMemory)
                isLowMemory = LowMemorySeverity.ExtremelyLow;
            return isLowMemory;
        }

        private LowMemorySeverity IsAvailableMemoryBelowThreshold(MemoryInfoResult memInfo)
        {
            if (memInfo.AvailableMemoryForProcessing < ExtremelyLowMemoryThreshold)
                return LowMemorySeverity.ExtremelyLow;

            if (memInfo.AvailableMemoryForProcessing < LowMemoryThreshold)
                return LowMemorySeverity.Low;
            
            return LowMemorySeverity.None;
        }

        private void AddLowMemEvent(LowMemReason reason, long availableMem, long totalUnmanaged, long totalScratchDirty, long physicalMem, long currentcommitCharge)
        {
            var lowMemEventDetails = new LowMemEventDetails
            {
                Reason = reason,
                FreeMem = availableMem,
                TotalUnmanaged = totalUnmanaged,
                TotalScratchDirty = totalScratchDirty,
                PhysicalMem = physicalMem,
                LowMemThreshold = LowMemoryThreshold.GetValue(SizeUnit.Bytes),
                CurrentCommitCharge = currentcommitCharge,
                Time = DateTime.UtcNow
            };

            LowMemEventDetailsStack[_lowMemEventDetailsIndex++] = lowMemEventDetails;
            if (_lowMemEventDetailsIndex == LowMemEventDetailsStack.Length)
                _lowMemEventDetailsIndex = 0;
        }

        public void SimulateLowMemoryNotification()
        {
            _simulatedLowMemory.Set();
        }

        internal static void AssertNotAboutToRunOutOfMemory()
        {
            Instance._lowMemoryMonitor?.AssertNotAboutToRunOutOfMemory();
        }
    }
}
