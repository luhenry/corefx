// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;

namespace System.Diagnostics
{
    public partial class Process : IDisposable
    {
        /// <summary>
        /// Creates an array of <see cref="Process"/> components that are associated with process resources on a
        /// remote computer. These process resources share the specified process name.
        /// </summary>
        public static Process[] GetProcessesByName(string processName, string machineName)
        {
            ProcessManager.ThrowIfRemoteMachine(machineName);
            if (processName == null)
            {
                processName = string.Empty;
            }

            var reusableReader = new ReusableTextReader();
            var processes = new List<Process>();
            foreach (int pid in ProcessManager.EnumerateProcessIds())
            {
                if (Interop.procfs.TryReadStatFile(pid, out Interop.procfs.ParsedStat parsedStat, reusableReader) &&
                    string.Equals(processName, parsedStat.comm, StringComparison.OrdinalIgnoreCase) &&
                    Interop.procfs.TryReadStatusFile(pid, out Interop.procfs.ParsedStatus parsedStatus, reusableReader))
                {
                    ProcessInfo processInfo = ProcessManager.CreateProcessInfo(parsedStat, parsedStatus, reusableReader);
                    processes.Add(new Process(machineName, false, processInfo.ProcessId, processInfo));
                }
            }

            return processes.ToArray();
        }

        /// <summary>Gets the amount of time the process has spent running code inside the operating system core.</summary>
        public TimeSpan PrivilegedProcessorTime
        {
            get
            {
                return TicksToTimeSpan(GetStat().stime);
            }
        }

        /// <summary>Gets the time the associated process was started.</summary>
        internal DateTime StartTimeCore
        {
            get
            {
                return BootTimeToDateTime(TicksToTimeSpan(GetStat().starttime));
            }
        }

        /// <summary>Computes a time based on a number of ticks since boot.</summary>
        /// <param name="timespanAfterBoot">The timespan since boot.</param>
        /// <returns>The converted time.</returns>
        internal static DateTime BootTimeToDateTime(TimeSpan timespanAfterBoot)
        {
            // And use that to determine the absolute time for timespan.
            DateTime dt = BootTime + timespanAfterBoot;

            // The return value is expected to be in the local time zone.
            // It is converted here (rather than starting with DateTime.Now) to avoid DST issues.
            return dt.ToLocalTime();
        }

        /// <summary>Gets the system boot time.</summary>
        private static DateTime BootTime
        {
            get
            {
                // '/proc/stat -> btime' gets the boot time.
                // btime is the time of system boot in seconds since the Unix epoch.
                // It includes suspended time and is updated based on the system time (settimeofday).
                const string StatFile = Interop.procfs.ProcStatFilePath;
                string text = File.ReadAllText(StatFile);
                int btimeLineStart = text.IndexOf("\nbtime ");
                if (btimeLineStart >= 0)
                {
                    int btimeStart = btimeLineStart + "\nbtime ".Length;
                    int btimeEnd = text.IndexOf('\n', btimeStart);
                    if (btimeEnd > btimeStart)
                    {
                        if (long.TryParse(text.AsSpan(btimeStart, btimeEnd - btimeStart), out long bootTimeSeconds))
                        {
                            return DateTime.UnixEpoch + TimeSpan.FromSeconds(bootTimeSeconds);
                        }
                    }
                }

                return DateTime.UtcNow;
            }
        }

        /// <summary>Gets the parent process ID</summary>
        private int ParentProcessId =>
            GetStat().ppid;

        /// <summary>Gets execution path</summary>
        private string GetPathToOpenFile()
        {
            string[] allowedProgramsToRun = { "xdg-open", "gnome-open", "kfmclient" };
            foreach (var program in allowedProgramsToRun)
            {
                string pathToProgram = FindProgramInPath(program);
                if (!string.IsNullOrEmpty(pathToProgram))
                {
                    return pathToProgram;
                }
            }
            return null;
        }

        /// <summary>
        /// Gets the amount of time the associated process has spent utilizing the CPU.
        /// It is the sum of the <see cref='System.Diagnostics.Process.UserProcessorTime'/> and
        /// <see cref='System.Diagnostics.Process.PrivilegedProcessorTime'/>.
        /// </summary>
        public TimeSpan TotalProcessorTime
        {
            get
            {
                Interop.procfs.ParsedStat stat = GetStat();
                return TicksToTimeSpan(stat.utime + stat.stime);
            }
        }

        /// <summary>
        /// Gets the amount of time the associated process has spent running code
        /// inside the application portion of the process (not the operating system core).
        /// </summary>
        public TimeSpan UserProcessorTime
        {
            get
            {
                return TicksToTimeSpan(GetStat().utime);
            }
        }

        partial void EnsureHandleCountPopulated()
        {
            if (_processInfo.HandleCount <= 0 && _haveProcessId)
            {
                // Don't get information for a PID that exited and has possibly been recycled.
                if (GetHasExited(refresh: false))
                {
                    return;
                }
                string path = Interop.procfs.GetFileDescriptorDirectoryPathForProcess(_processId);
                if (Directory.Exists(path))
                {
                    try
                    {
                        _processInfo.HandleCount = Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly).Length;
                    }
                    catch (DirectoryNotFoundException) // Occurs when the process is deleted between the Exists check and the GetFiles call.
                    {
                    }
                }
            }
        }

        /// <summary>
        /// Gets or sets which processors the threads in this process can be scheduled to run on.
        /// </summary>
        private unsafe IntPtr ProcessorAffinityCore
        {
            get
            {
                EnsureState(State.HaveNonExitedId);

                IntPtr set;
                if (Interop.Sys.SchedGetAffinity(_processId, out set) != 0)
                {
                    throw new Win32Exception(); // match Windows exception
                }

                return set;
            }
            set
            {
                EnsureState(State.HaveNonExitedId);

                if (Interop.Sys.SchedSetAffinity(_processId, ref value) != 0)
                {
                    throw new Win32Exception(); // match Windows exception
                }
            }
        }

        /// <summary>
        /// Make sure we have obtained the min and max working set limits.
        /// </summary>
        private void GetWorkingSetLimits(out IntPtr minWorkingSet, out IntPtr maxWorkingSet)
        {
            minWorkingSet = IntPtr.Zero; // no defined limit available

            // For max working set, try to respect container limits by reading
            // from cgroup, but if it's unavailable, fall back to reading from procfs.
            EnsureState(State.HaveNonExitedId);
            if (!Interop.cgroups.TryGetMemoryLimit(out ulong rsslim))
            {
                rsslim = GetStat().rsslim;
            }

            // rsslim is a ulong, but maxWorkingSet is an IntPtr, so we need to cap rsslim
            // at the max size of IntPtr.  This often happens when there is no configured
            // rsslim other than ulong.MaxValue, which without these checks would show up
            // as a maxWorkingSet == -1.
            switch (IntPtr.Size)
            {
                case 4:
                    if (rsslim > int.MaxValue)
                        rsslim = int.MaxValue;
                    break;
                case 8:
                    if (rsslim > long.MaxValue)
                        rsslim = long.MaxValue;
                    break;
            }

            maxWorkingSet = (IntPtr)rsslim;
        }

        /// <summary>Sets one or both of the minimum and maximum working set limits.</summary>
        /// <param name="newMin">The new minimum working set limit, or null not to change it.</param>
        /// <param name="newMax">The new maximum working set limit, or null not to change it.</param>
        /// <param name="resultingMin">The resulting minimum working set limit after any changes applied.</param>
        /// <param name="resultingMax">The resulting maximum working set limit after any changes applied.</param>
        private void SetWorkingSetLimitsCore(IntPtr? newMin, IntPtr? newMax, out IntPtr resultingMin, out IntPtr resultingMax)
        {
            // RLIMIT_RSS with setrlimit not supported on Linux > 2.4.30.
            throw new PlatformNotSupportedException(SR.MinimumWorkingSetNotSupported);
        }

        // -----------------------------
        // ---- PAL layer ends here ----
        // -----------------------------

        /// <summary>Gets the path to the executable for the process, or null if it could not be retrieved.</summary>
        /// <param name="processId">The pid for the target process, or -1 for the current process.</param>
        internal static string GetExePath(int processId = -1)
        {
            string exeFilePath = processId == -1 ?
                Interop.procfs.SelfExeFilePath :
                Interop.procfs.GetExeFilePathForProcess(processId);

            return Interop.Sys.ReadLink(exeFilePath);
        }

        // ----------------------------------
        // ---- Unix PAL layer ends here ----
        // ----------------------------------

        /// <summary>Reads the stats information for this process from the procfs file system.</summary>
        private Interop.procfs.ParsedStat GetStat()
        {
            EnsureState(State.HaveNonExitedId);
            Interop.procfs.ParsedStat stat;
            if (!Interop.procfs.TryReadStatFile(_processId, out stat, new ReusableTextReader()))
            {
                throw new Win32Exception(SR.ProcessInformationUnavailable);
            }
            return stat;
        }
    }
}
