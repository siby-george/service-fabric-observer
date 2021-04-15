﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Fabric;

namespace FabricObserver.Observers.Utilities
{
    public class WindowsProcessInfoProvider : ProcessInfoProvider
    {
        const string CategoryName = "Process";
        private readonly object memPerfCounterLock = new object();
        private readonly object fileHandlesPerfCounterLock = new object();

        public override float GetProcessPrivateWorkingSetInMB(int processId)
        {
            const string WorkingSetCounterName = "Working Set - Private";
            string processName;

            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    processName = process.ProcessName;
                }
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is NotSupportedException)
            {
                // "Process with an Id of 12314 is not running."
                Logger.LogWarning($"Handled Exception in GetProcessPrivateWorkingSetInMB: {e.Message}");
                return 0F;
            }

            lock (memPerfCounterLock)
            {
                PerformanceCounter memProcessPrivateWorkingSetCounter = null;

                try
                {
                    memProcessPrivateWorkingSetCounter = new PerformanceCounter
                    {
                        CategoryName = CategoryName,
                        CounterName = WorkingSetCounterName,
                        InstanceName = processName
                    };

                    return memProcessPrivateWorkingSetCounter.NextValue() / (1024 * 1024);
                }
                catch (Exception e) when (e is ArgumentNullException || e is Win32Exception || e is UnauthorizedAccessException)
                {
                    Logger.LogWarning($"{CategoryName} {WorkingSetCounterName} PerfCounter handled error:{Environment.NewLine}{e}");

                    // Don't throw.
                    return 0F;
                }
                catch (Exception e)
                { 
                    Logger.LogError($"{CategoryName} {WorkingSetCounterName} PerfCounter unhandled error:{Environment.NewLine}{e}");

                    throw;
                }
                finally
                {
                    memProcessPrivateWorkingSetCounter?.Dispose();
                    memProcessPrivateWorkingSetCounter = null;
                }
            }
        }

        // File Handles
        public override float GetProcessAllocatedHandles(int processId, StatelessServiceContext context)
        {
            if (processId < 0)
            {
                return -1F;
            }

            const string FileHandlesCounterName = "Handle Count";
            string processName;

            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    processName = process.ProcessName;
                }
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is NotSupportedException)
            {
                // "Process with an Id of 12314 is not running."
                Logger.LogWarning($"Handled Exception in GetProcessAllocatedHandles: {e.Message}");
                return -1F;
            }

            lock (fileHandlesPerfCounterLock)
            {
                PerformanceCounter processFileHandleCounter = null;

                try
                {
                    processFileHandleCounter = new PerformanceCounter
                    {
                        CategoryName = CategoryName,
                        CounterName = FileHandlesCounterName,
                        InstanceName = processName
                    };

                    return processFileHandleCounter.NextValue();
                }
                catch (Exception e) when (e is InvalidOperationException || e is Win32Exception || e is UnauthorizedAccessException)
                {
                    Logger.LogWarning($"{CategoryName} {FileHandlesCounterName} PerfCounter handled error:{Environment.NewLine}{e}");

                    // Don't throw.
                    return -1F;
                }
                catch (Exception e)
                {
                    Logger.LogError($"{CategoryName} {FileHandlesCounterName} PerfCounter unhandled error:{Environment.NewLine}{e}");

                    throw;
                }
                finally
                {
                    processFileHandleCounter?.Dispose();
                    processFileHandleCounter = null;
                }
            }
        }
    }
}