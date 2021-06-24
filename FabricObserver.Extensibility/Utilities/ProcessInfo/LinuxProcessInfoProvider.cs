﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.Linq;

namespace FabricObserver.Observers.Utilities
{
    public class LinuxProcessInfoProvider : ProcessInfoProvider
    {
        public override float GetProcessPrivateWorkingSetInMB(int processId)
        {
            if (LinuxProcFS.TryParseStatusFile(processId, out ParsedStatus status))
            {
                return (status.VmRSS - status.RsSFile) / 1048576f;
            }

            // Could not read from /proc/[pid]/status - it is possible that process already exited.
            return 0f;
        }

        public override float GetProcessAllocatedHandles(int processId, StatelessServiceContext context)
        {
            if (processId < 0)
            {
                return -1f;
            }

            // We need the full path to the currently deployed FO CodePackage, which is where our 
            // proxy binary lives.
            string path = context.CodePackageActivationContext.GetCodePackageObject("Code").Path;
            string arg = processId.ToString();
            string bin = $"{path}/elevated_proc_fd";
            float result;

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                Arguments = arg,
                FileName = bin,
                UseShellExecute = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                CreateNoWindow = true
            };

            using (Process process = Process.Start(startInfo))
            {
                var stdOut = process.StandardOutput;
                string output = stdOut.ReadToEnd();

                process.WaitForExit();

                result = float.TryParse(output, out float ret) ? ret : -42f;

                if (process.ExitCode != 0)
                {
                    Logger.LogWarning($"elevated_proc_fd exited with: {process.ExitCode}");
                    return -1f;
                }
            }

            return result;
        }

        public override List<Process> GetChildProcesses(Process process)
        {
            // https://askubuntu.com/questions/512871/find-children-of-the-process
            string cmdResult = "ps -o ppid= -o pid= -A | awk '$1 == " + process.Id.ToString() + " {print $2}'".Bash();
            List<Process> childProcesses = new List<Process>();

            if (!string.IsNullOrWhiteSpace(cmdResult))
            {
                var sPids = cmdResult.Split('\n')?.ToList();

                if (sPids.Count > 0)
                {
                    foreach (string pid in sPids)
                    {
                        if (int.TryParse(pid, out int proc))
                        {
                            try
                            {
                                Process p = Process.GetProcessById(proc);
                                childProcesses.Add(p);
                            }
                            catch (ArgumentException)
                            {
                                // ignore -> process may no longer exist
                            }
                            catch (InvalidOperationException ie)
                            {
                                Logger.LogWarning("GetFlattenedProcessFamilyTree: Unsuccessful bash cmd (ps - o ppid = -o pid = -A | awk '$1 == " + process.Id.ToString() + " {print $2}')" + ie.ToString());
                            }
                        }
                    }
                }
            }

            return childProcesses;
        }

        protected override void Dispose(bool disposing)
        {
            // nothing to do here.
        }
    }
}