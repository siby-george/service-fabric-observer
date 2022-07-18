﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security;
using System.Text;

namespace FabricObserver.Observers.Utilities
{
    /// <summary>
    /// Win32 PInvoke helper methods. 
    /// </summary>
    [SuppressUnmanagedCodeSecurity]
    public static class NativeMethods
    {
        public const int AF_INET = 2;
        public const int AF_INET6 = 23;

        [Flags]
        public enum MINIDUMP_TYPE
        {
            MiniDumpNormal = 0x00000000,
            MiniDumpWithDataSegs = 0x00000001,
            MiniDumpWithFullMemory = 0x00000002,
            MiniDumpWithHandleData = 0x00000004,
            MiniDumpFilterMemory = 0x00000008,
            MiniDumpScanMemory = 0x00000010,
            MiniDumpWithUnloadedModules = 0x00000020,
            MiniDumpWithIndirectlyReferencedMemory = 0x00000040,
            MiniDumpFilterModulePaths = 0x00000080,
            MiniDumpWithProcessThreadData = 0x00000100,
            MiniDumpWithPrivateReadWriteMemory = 0x00000200,
            MiniDumpWithoutOptionalData = 0x00000400,
            MiniDumpWithFullMemoryInfo = 0x00000800,
            MiniDumpWithThreadInfo = 0x00001000,
            MiniDumpWithCodeSegs = 0x00002000,
            MiniDumpWithoutAuxiliaryState = 0x00004000,
            MiniDumpWithFullAuxiliaryState = 0x00008000,
            MiniDumpWithPrivateWriteCopyMemory = 0x00010000,
            MiniDumpIgnoreInaccessibleMemory = 0x00020000,
            MiniDumpWithTokenInformation = 0x00040000,
            MiniDumpWithModuleHeaders = 0x00080000,
            MiniDumpFilterTriage = 0x00100000,
            MiniDumpValidTypeFlags = 0x001fffff
        }

        [StructLayout(LayoutKind.Sequential)] 
        public struct PROCESS_MEMORY_COUNTERS_EX
        {
            public uint cb;
            public uint PageFaultCount;
            public IntPtr PeakWorkingSetSize;
            public IntPtr WorkingSetSize;
            public IntPtr QuotaPeakPagedPoolUsage;
            public IntPtr QuotaPagedPoolUsage;
            public IntPtr QuotaPeakNonPagedPoolUsage;
            public IntPtr QuotaNonPagedPoolUsage;
            public IntPtr PagefileUsage;
            public IntPtr PeakPagefileUsage;
            public IntPtr PrivateUsage;
        }

        [Flags]
        public enum CreateToolhelp32SnapshotFlags : uint
        {
            /// <summary>
            /// Indicates that the snapshot handle is to be inheritable.
            /// </summary>
            TH32CS_INHERIT = 0x80000000,

            /// <summary>
            /// Includes all heaps of the process specified in th32ProcessID in the snapshot.
            /// To enumerate the heaps, see Heap32ListFirst.
            /// </summary>
            TH32CS_SNAPHEAPLIST = 0x00000001,

            /// <summary>
            /// Includes all modules of the process specified in th32ProcessID in the snapshot.
            /// To enumerate the modules, see <see cref="Module32First(SafeObjectHandle,MODULEENTRY32*)"/>.
            /// If the function fails with <see cref="Win32ErrorCode.ERROR_BAD_LENGTH"/>, retry the function until
            /// it succeeds.
            /// <para>
            /// 64-bit Windows:  Using this flag in a 32-bit process includes the 32-bit modules of the process
            /// specified in th32ProcessID, while using it in a 64-bit process includes the 64-bit modules.
            /// To include the 32-bit modules of the process specified in th32ProcessID from a 64-bit process, use
            /// the <see cref="TH32CS_SNAPMODULE32"/> flag.
            /// </para>
            /// </summary>
            TH32CS_SNAPMODULE = 0x00000008,

            /// <summary>
            /// Includes all 32-bit modules of the process specified in th32ProcessID in the snapshot when called from
            /// a 64-bit process.
            /// This flag can be combined with <see cref="TH32CS_SNAPMODULE"/> or <see cref="TH32CS_SNAPALL"/>.
            /// If the function fails with <see cref="Win32ErrorCode.ERROR_BAD_LENGTH"/>, retry the function until it
            /// succeeds.
            /// </summary>
            TH32CS_SNAPMODULE32 = 0x00000010,

            /// <summary>
            /// Includes all processes in the system in the snapshot. To enumerate the processes, see
            /// <see cref="Process32First(SafeObjectHandle,PROCESSENTRY32*)"/>.
            /// </summary>
            TH32CS_SNAPPROCESS = 0x00000002,

            /// <summary>
            /// Includes all threads in the system in the snapshot. To enumerate the threads, see
            /// Thread32First.
            /// <para>
            /// To identify the threads that belong to a specific process, compare its process identifier to the
            /// th32OwnerProcessID member of the THREADENTRY32 structure when
            /// enumerating the threads.
            /// </para>
            /// </summary>
            TH32CS_SNAPTHREAD = 0x00000004,

            /// <summary>
            /// Includes all processes and threads in the system, plus the heaps and modules of the process specified in
            /// th32ProcessID.
            /// </summary>
            TH32CS_SNAPALL = TH32CS_SNAPHEAPLIST | TH32CS_SNAPMODULE | TH32CS_SNAPPROCESS | TH32CS_SNAPTHREAD,
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PROCESSENTRY32
        {
            const int MAX_PATH = 260;
            internal uint dwSize;
            internal uint cntUsage;
            internal uint th32ProcessID;
            internal IntPtr th32DefaultHeapID;
            internal uint th32ModuleID;
            internal uint cntThreads;
            internal uint th32ParentProcessID;
            internal int pcPriClassBase;
            internal uint dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH)]
            internal string szExeFile;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct THREADENTRY32
        {
            internal uint dwSize;
            internal uint cntUsage;
            internal uint th32ThreadID;
            internal uint th32OwnerProcessID;
            internal uint tpBasePri;
            internal uint tpDeltaPri;
            internal uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public class MEMORYSTATUSEX
        {
            /// <summary>
            /// Size of the structure, in bytes. You must set this member before calling GlobalMemoryStatusEx.
            /// </summary>
            public uint dwLength;

            /// <summary>
            /// Number between 0 and 100 that specifies the approximate percentage of physical memory that is in use (0 indicates no memory use and 100 indicates full memory use).
            /// </summary>
            public uint dwMemoryLoad;

            /// <summary>
            /// Total size of physical memory, in bytes.
            /// </summary>
            public ulong ullTotalPhys;

            /// <summary>
            /// Size of physical memory available, in bytes.
            /// </summary>
            public ulong ullAvailPhys;

            /// <summary>
            /// Size of the committed memory limit, in bytes. This is physical memory plus the size of the page file, minus a small overhead.
            /// </summary>
            public ulong ullTotalPageFile;

            /// <summary>
            /// Size of available memory to commit, in bytes. The limit is ullTotalPageFile.
            /// </summary>
            public ulong ullAvailPageFile;

            /// <summary>
            /// Total size of the user mode portion of the virtual address space of the calling process, in bytes.
            /// </summary>
            public ulong ullTotalVirtual;

            /// <summary>
            /// Size of unreserved and uncommitted memory in the user mode portion of the virtual address space of the calling process, in bytes.
            /// </summary>
            public ulong ullAvailVirtual;

            /// <summary>
            /// Size of unreserved and uncommitted memory in the extended portion of the virtual address space of the calling process, in bytes.
            /// </summary>
            public ulong ullAvailExtendedVirtual;

            /// <summary>
            /// Initializes a new instance of the <see cref="T:MEMORYSTATUSEX"/> class.
            /// </summary>
            public MEMORYSTATUSEX()
            {
                dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [Flags]
        public enum ProcessAccessFlags : uint
        {
            All = 0x001F0FFF,
            Terminate = 0x00000001,
            CreateThread = 0x00000002,
            VirtualMemoryOperation = 0x00000008,
            VirtualMemoryRead = 0x00000010,
            VirtualMemoryWrite = 0x00000020,
            DuplicateHandle = 0x00000040,
            CreateProcess = 0x000000080,
            SetQuota = 0x00000100,
            SetInformation = 0x00000200,
            QueryInformation = 0x00000400,
            QueryLimitedInformation = 0x00001000,
            Synchronize = 0x00100000
        }

        // Networking \\
        // Credit: http://pinvoke.net/default.aspx/iphlpapi/GetExtendedTcpTable.html

        public enum TCP_TABLE_CLASS
        {
            TCP_TABLE_BASIC_LISTENER,
            TCP_TABLE_BASIC_CONNECTIONS,
            TCP_TABLE_BASIC_ALL,
            TCP_TABLE_OWNER_PID_LISTENER,
            TCP_TABLE_OWNER_PID_CONNECTIONS,
            TCP_TABLE_OWNER_PID_ALL,
            TCP_TABLE_OWNER_MODULE_LISTENER,
            TCP_TABLE_OWNER_MODULE_CONNECTIONS,
            TCP_TABLE_OWNER_MODULE_ALL
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MIB_TCP6TABLE_OWNER_PID
        {
            public uint dwNumEntries;

            [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.Struct, SizeConst = 1)]
            public MIB_TCP6ROW_OWNER_PID[] table;
        }

        public enum MIB_TCP_STATE
        {
            MIB_TCP_STATE_CLOSED = 1,
            MIB_TCP_STATE_LISTEN = 2,
            MIB_TCP_STATE_SYN_SENT = 3,
            MIB_TCP_STATE_SYN_RCVD = 4,
            MIB_TCP_STATE_ESTAB = 5,
            MIB_TCP_STATE_FIN_WAIT1 = 6,
            MIB_TCP_STATE_FIN_WAIT2 = 7,
            MIB_TCP_STATE_CLOSE_WAIT = 8,
            MIB_TCP_STATE_CLOSING = 9,
            MIB_TCP_STATE_LAST_ACK = 10,
            MIB_TCP_STATE_TIME_WAIT = 11,
            MIB_TCP_STATE_DELETE_TCB = 12
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MIB_TCP6ROW_OWNER_PID
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] localAddr;
            public uint localScopeId;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public byte[] localPort;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] remoteAddr;
            public uint remoteScopeId;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public byte[] remotePort;
            public uint state;
            public uint owningPid;

            public uint ProcessId
            {
                get 
                { 
                    return owningPid; 
                }
            }

            public long LocalScopeId
            {
                get 
                {
                    return localScopeId; 
                }
            }

            public IPAddress LocalAddress
            {
                get 
                { 
                    return new IPAddress(localAddr, LocalScopeId); 
                }
            }

            public ushort LocalPort
            {
                get 
                { 
                    return BitConverter.ToUInt16(localPort.Take(2).Reverse().ToArray(), 0); 
                }
            }

            public long RemoteScopeId
            {
                get 
                { 
                    return remoteScopeId; 
                }
            }

            public IPAddress RemoteAddress
            {
                get 
                { 
                    return new IPAddress(remoteAddr, RemoteScopeId); 
                }
            }

            public ushort RemotePort
            {
                get 
                { 
                    return BitConverter.ToUInt16(remotePort.Take(2).Reverse().ToArray(), 0); 
                }
            }

            public MIB_TCP_STATE State
            {
                get 
                { 
                    return (MIB_TCP_STATE)state; 
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MIB_TCPTABLE_OWNER_PID
        {
            public uint dwNumEntries;

            [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.Struct, SizeConst = 1)]
            public MIB_TCPROW_OWNER_PID[] table;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MIB_TCPROW_OWNER_PID
        {
            public uint state;
            public uint localAddr;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public byte[] localPort;
            public uint remoteAddr;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public byte[] remotePort;
            public uint owningPid;

            public uint ProcessId
            {
                get 
                { 
                    return owningPid; 
                }
            }

            public IPAddress LocalAddress
            {
                get 
                { 
                    return new IPAddress(localAddr); 
                }
            }

            public ushort LocalPort
            {
                get
                {
                    return BitConverter.ToUInt16(new byte[2] { localPort[1], localPort[0] }, 0);
                }
            }

            public IPAddress RemoteAddress
            {
                get 
                { 
                    return new IPAddress(remoteAddr); 
                }
            }

            public ushort RemotePort
            {
                get
                {
                    return BitConverter.ToUInt16(new byte[2] { remotePort[1], remotePort[0] }, 0);
                }
            }

            public MIB_TCP_STATE State
            {
                get 
                { 
                    return (MIB_TCP_STATE)state; 
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PSS_THREAD_INFORMATION
        {
            /// <summary>
            /// <para>The count of threads in the snapshot.</para>
            /// </summary>
            public uint ThreadsCaptured;

            /// <summary>
            /// <para>The length of the <c>CONTEXT</c> record captured, in bytes.</para>
            /// </summary>
            public uint ContextLength;
        }

        // For PSCaptureSnapshot/PSQuerySnapshot \\

        [Flags]
        public enum PSS_CAPTURE_FLAGS : uint
        {
            /// <summary>Capture nothing.</summary>
            PSS_CAPTURE_NONE = 0x00000000,

            /// <summary>
            /// Capture a snapshot of all cloneable pages in the process. The clone includes all MEM_PRIVATE regions, as well as all sections
            /// (MEM_MAPPED and MEM_IMAGE) that are shareable. All Win32 sections created via CreateFileMapping are shareable.
            /// </summary>
            PSS_CAPTURE_VA_CLONE = 0x00000001,

            /// <summary>(Do not use.)</summary>
            PSS_CAPTURE_RESERVED_00000002 = 0x00000002,

            /// <summary>Capture the handle table (handle values only).</summary>
            PSS_CAPTURE_HANDLES = 0x00000004,

            /// <summary>Capture name information for each handle.</summary>
            PSS_CAPTURE_HANDLE_NAME_INFORMATION = 0x00000008,

            /// <summary>Capture basic handle information such as HandleCount, PointerCount, GrantedAccess, etc.</summary>
            PSS_CAPTURE_HANDLE_BASIC_INFORMATION = 0x00000010,

            /// <summary>Capture type-specific information for supported object types: Process, Thread, Event, Mutant, Section.</summary>
            PSS_CAPTURE_HANDLE_TYPE_SPECIFIC_INFORMATION = 0x00000020,

            /// <summary>Capture the handle tracing table.</summary>
            PSS_CAPTURE_HANDLE_TRACE = 0x00000040,

            /// <summary>Capture thread information (IDs only).</summary>
            PSS_CAPTURE_THREADS = 0x00000080,

            /// <summary>Capture the context for each thread.</summary>
            PSS_CAPTURE_THREAD_CONTEXT = 0x00000100,

            /// <summary>Capture extended context for each thread (e.g. CONTEXT_XSTATE).</summary>
            PSS_CAPTURE_THREAD_CONTEXT_EXTENDED = 0x00000200,

            /// <summary>(Do not use.)</summary>
            PSS_CAPTURE_RESERVED_00000400 = 0x00000400,

            /// <summary>
            /// Capture a snapshot of the virtual address space. The VA space is captured as an array of MEMORY_BASIC_INFORMATION structures.
            /// This flag does not capture the contents of the pages.
            /// </summary>
            PSS_CAPTURE_VA_SPACE = 0x00000800,

            /// <summary>
            /// For MEM_IMAGE and MEM_MAPPED regions, dumps the path to the file backing the sections (identical to what GetMappedFileName
            /// returns). For MEM_IMAGE regions, also dumps: The PROCESS_VM_READ access right is required on the process handle.
            /// </summary>
            PSS_CAPTURE_VA_SPACE_SECTION_INFORMATION = 0x00001000,

            /// <summary/>
            PSS_CAPTURE_IPT_TRACE = 0x00002000,

            /// <summary>
            /// The breakaway is optional. If the clone process fails to create as a breakaway, then it is created still inside the job. This
            /// flag must be specified in combination with either PSS_CREATE_FORCE_BREAKAWAY and/or PSS_CREATE_BREAKAWAY.
            /// </summary>
            PSS_CREATE_BREAKAWAY_OPTIONAL = 0x04000000,

            /// <summary>The clone is broken away from the parent process' job. This is equivalent to CreateProcess flag CREATE_BREAKAWAY_FROM_JOB.</summary>
            PSS_CREATE_BREAKAWAY = 0x08000000,

            /// <summary>The clone is forcefully broken away the parent process's job. This is only allowed for Tcb-privileged callers.</summary>
            PSS_CREATE_FORCE_BREAKAWAY = 0x10000000,

            /// <summary>
            /// The facility should not use the process heap for any persistent or transient allocations. The use of the heap may be
            /// undesirable in certain contexts such as creation of snapshots in the exception reporting path (where the heap may be corrupted).
            /// </summary>
            PSS_CREATE_USE_VM_ALLOCATIONS = 0x20000000,

            /// <summary>
            /// Measure performance of the facility. Performance counters can be retrieved via PssQuerySnapshot with the
            /// PSS_QUERY_PERFORMANCE_COUNTERS information class of PSS_QUERY_INFORMATION_CLASS.
            /// </summary>
            PSS_CREATE_MEASURE_PERFORMANCE = 0x40000000,

            /// <summary>
            /// The virtual address (VA) clone process does not hold a reference to the underlying image. This will cause functions such as
            /// QueryFullProcessImageName to fail on the VA clone process.
            /// </summary>
            PSS_CREATE_RELEASE_SECTION = 0x80000000
        }

        public enum PSS_QUERY_INFORMATION_CLASS
        {
            /// <summary>Returns a PSS_PROCESS_INFORMATION structure, with information about the original process.</summary>
            PSS_QUERY_PROCESS_INFORMATION,

            /// <summary>Returns a PSS_VA_CLONE_INFORMATION structure, with a handle to the VA clone.</summary>
            PSS_QUERY_VA_CLONE_INFORMATION,

            /// <summary>Returns a PSS_AUXILIARY_PAGES_INFORMATION structure, which contains the count of auxiliary pages captured.</summary>
            PSS_QUERY_AUXILIARY_PAGES_INFORMATION,

            /// <summary>Returns a PSS_VA_SPACE_INFORMATION structure, which contains the count of regions captured.</summary>
            PSS_QUERY_VA_SPACE_INFORMATION,

            /// <summary>Returns a PSS_HANDLE_INFORMATION structure, which contains the count of handles captured.</summary>
            PSS_QUERY_HANDLE_INFORMATION,

            /// <summary>Returns a PSS_THREAD_INFORMATION structure, which contains the count of threads captured.</summary>
            PSS_QUERY_THREAD_INFORMATION,

            /// <summary>
            /// Returns a PSS_HANDLE_TRACE_INFORMATION structure, which contains a handle to the handle trace section, and its size.
            /// </summary>
            PSS_QUERY_HANDLE_TRACE_INFORMATION,

            /// <summary>Returns a PSS_PERFORMANCE_COUNTERS structure, which contains various performance counters.</summary>
            PSS_QUERY_PERFORMANCE_COUNTERS,
        }

        [Flags]
        public enum PSS_THREAD_FLAGS
        {
            /// <summary>No flag.</summary>
            PSS_THREAD_FLAGS_NONE = 0x0000,

            /// <summary>The thread terminated.</summary>
            PSS_THREAD_FLAGS_TERMINATED = 0x0001
        }

        [Flags]
        public enum PSS_PROCESS_FLAGS
        {
            /// <summary>No flag.</summary>
            PSS_PROCESS_FLAGS_NONE = 0x00000000,

            /// <summary>The process is protected.</summary>
            PSS_PROCESS_FLAGS_PROTECTED = 0x00000001,

            /// <summary>The process is a 32-bit process running on a 64-bit native OS.</summary>
            PSS_PROCESS_FLAGS_WOW64 = 0x00000002,

            /// <summary>Undefined.</summary>
            PSS_PROCESS_FLAGS_RESERVED_03 = 0x00000004,

            /// <summary>Undefined.</summary>
            PSS_PROCESS_FLAGS_RESERVED_04 = 0x00000008,

            /// <summary>
            /// The process is frozen; for example, a debugger is attached and broken into the process or a Store process is suspended by a
            /// lifetime management service.
            /// </summary>
            PSS_PROCESS_FLAGS_FROZEN = 0x00000010
        }

        public enum ProcessorArchitecture : ushort
        {
            /// <summary>x86</summary>
            PROCESSOR_ARCHITECTURE_INTEL = 0,

            /// <summary>Unspecified</summary>
            PROCESSOR_ARCHITECTURE_MIPS = 1,

            /// <summary>Unspecified</summary>
            PROCESSOR_ARCHITECTURE_ALPHA = 2,

            /// <summary>Unspecified</summary>
            PROCESSOR_ARCHITECTURE_PPC = 3,

            /// <summary>Unspecified</summary>
            PROCESSOR_ARCHITECTURE_SHX = 4,

            /// <summary>ARM</summary>
            PROCESSOR_ARCHITECTURE_ARM = 5,

            /// <summary>Intel Itanium-based</summary>
            PROCESSOR_ARCHITECTURE_IA64 = 6,

            /// <summary>Unspecified</summary>
            PROCESSOR_ARCHITECTURE_ALPHA64 = 7,

            /// <summary>Unspecified</summary>
            PROCESSOR_ARCHITECTURE_MSIL = 8,

            /// <summary>x64 (AMD or Intel)</summary>
            PROCESSOR_ARCHITECTURE_AMD64 = 9,

            /// <summary>Unspecified</summary>
            PROCESSOR_ARCHITECTURE_IA32_ON_WIN64 = 10,

            /// <summary>Unspecified</summary>
            PROCESSOR_ARCHITECTURE_NEUTRAL = 11,

            /// <summary>Unspecified</summary>
            PROCESSOR_ARCHITECTURE_ARM64 = 12,

            /// <summary>Unspecified</summary>
            PROCESSOR_ARCHITECTURE_ARM32_ON_WIN64 = 13,

            /// <summary>Unknown architecture.</summary>
            PROCESSOR_ARCHITECTURE_UNKNOWN = 0xFFFF
        }

        public static class CONTEXT_FLAG
        {
            /// <summary>Undocumented.</summary>
            public const uint CONTEXT_AMD64 = 0x00100000;

            /// <summary>Undocumented.</summary>
            public const uint CONTEXT_ARM = 0x00200000;

            /// <summary>Undocumented.</summary>
            public const uint CONTEXT_EXCEPTION_ACTIVE = 0x08000000;

            /// <summary>Undocumented.</summary>
            public const uint CONTEXT_EXCEPTION_REPORTING = 0x80000000;

            /// <summary>Undocumented.</summary>
            public const uint CONTEXT_EXCEPTION_REQUEST = 0x40000000;

            /// <summary>Undocumented.</summary>
            public const uint CONTEXT_i386 = 0x00010000;

            /// <summary>Undocumented.</summary>
            public const uint CONTEXT_KERNEL_DEBUGGER = 0x04000000;

            /// <summary>Undocumented.</summary>
            public const uint CONTEXT_SERVICE_ACTIVE = 0x10000000;

            private static readonly uint systemContext;

            static CONTEXT_FLAG()
            {
                GetNativeSystemInfo(out var info);

                switch (info.wProcessorArchitecture)
                {
                    case ProcessorArchitecture.PROCESSOR_ARCHITECTURE_INTEL:
                        systemContext = CONTEXT_i386;
                        break;

                    case ProcessorArchitecture.PROCESSOR_ARCHITECTURE_ARM:
                        systemContext = CONTEXT_ARM;
                        break;

                    case ProcessorArchitecture.PROCESSOR_ARCHITECTURE_AMD64:
                        systemContext = CONTEXT_AMD64;
                        break;

                    default:
                        throw new InvalidOperationException("Processor context not recognized.");
                }
            }

            /// <summary>Undocumented.</summary>
            public static uint CONTEXT_ALL => CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_SEGMENTS | CONTEXT_FLOATING_POINT | CONTEXT_DEBUG_REGISTERS;

            /// <summary>Undocumented.</summary>
            public static uint CONTEXT_CONTROL => systemContext | 0x00000001;

            /// <summary>Undocumented.</summary>
            public static uint CONTEXT_DEBUG_REGISTERS => systemContext | 0x00000010;

            /// <summary>Undocumented.</summary>
            public static uint CONTEXT_EXTENDED_REGISTERS => systemContext | 0x00000020;

            /// <summary>Undocumented.</summary>
            public static uint CONTEXT_FLOATING_POINT => systemContext | 0x00000008;

            /// <summary>Undocumented.</summary>
            public static uint CONTEXT_FULL => CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_FLOATING_POINT;

            /// <summary>Undocumented.</summary>
            public static uint CONTEXT_INTEGER => systemContext | 0x00000002;

            /// <summary>Undocumented.</summary>
            public static uint CONTEXT_SEGMENTS => systemContext | 0x00000004;

            /// <summary>Undocumented.</summary>
            public static uint CONTEXT_XSTATE => systemContext | 0x00000040;
        }

        public struct SYSTEM_INFO
        {
            /// <summary>
            /// <para>The processor architecture of the installed operating system. This member can be one of the following values.</para>
            /// <para>
            /// <list type="table">
            /// <listheader>
            /// <term>Value</term>
            /// <term>Meaning</term>
            /// </listheader>
            /// <item>
            /// <term>PROCESSOR_ARCHITECTURE_AMD649</term>
            /// <term>x64 (AMD or Intel)</term>
            /// </item>
            /// <item>
            /// <term>PROCESSOR_ARCHITECTURE_ARM5</term>
            /// <term>ARM</term>
            /// </item>
            /// <item>
            /// <term>PROCESSOR_ARCHITECTURE_ARM6412</term>
            /// <term>ARM64</term>
            /// </item>
            /// <item>
            /// <term>PROCESSOR_ARCHITECTURE_IA646</term>
            /// <term>Intel Itanium-based</term>
            /// </item>
            /// <item>
            /// <term>PROCESSOR_ARCHITECTURE_INTEL0</term>
            /// <term>x86</term>
            /// </item>
            /// <item>
            /// <term>PROCESSOR_ARCHITECTURE_UNKNOWN0xffff</term>
            /// <term>Unknown architecture.</term>
            /// </item>
            /// </list>
            /// </para>
            /// </summary>
            public ProcessorArchitecture wProcessorArchitecture;

            /// <summary>This member is reserved for future use.</summary>
            public ushort wReserved;

            /// <summary>
            /// The page size and the granularity of page protection and commitment. This is the page size used by the <c>VirtualAlloc</c> function.
            /// </summary>
            public uint dwPageSize;

            /// <summary>A pointer to the lowest memory address accessible to applications and dynamic-link libraries (DLLs).</summary>
            public IntPtr lpMinimumApplicationAddress;

            /// <summary>A pointer to the highest memory address accessible to applications and DLLs.</summary>
            public IntPtr lpMaximumApplicationAddress;

            /// <summary>
            /// A mask representing the set of processors configured into the system. Bit 0 is processor 0; bit 31 is processor 31.
            /// </summary>
            public UIntPtr dwActiveProcessorMask;

            /// <summary>
            /// The number of logical processors in the current group. To retrieve this value, use the <c>GetLogicalProcessorInformation</c> function.
            /// </summary>
            public uint dwNumberOfProcessors;

            /// <summary>
            /// An obsolete member that is retained for compatibility. Use the <c>wProcessorArchitecture</c>, <c>wProcessorLevel</c>, and
            /// <c>wProcessorRevision</c> members to determine the type of processor.
            /// </summary>
            public uint dwProcessorType;

            /// <summary>
            /// The granularity for the starting address at which virtual memory can be allocated. For more information, see <c>VirtualAlloc</c>.
            /// </summary>
            public uint dwAllocationGranularity;

            /// <summary>
            /// <para>
            /// The architecture-dependent processor level. It should be used only for display purposes. To determine the feature set of a
            /// processor, use the <c>IsProcessorFeaturePresent</c> function.
            /// </para>
            /// <para>If <c>wProcessorArchitecture</c> is PROCESSOR_ARCHITECTURE_INTEL, <c>wProcessorLevel</c> is defined by the CPU vendor.</para>
            /// <para>If <c>wProcessorArchitecture</c> is PROCESSOR_ARCHITECTURE_IA64, <c>wProcessorLevel</c> is set to 1.</para>
            /// </summary>
            public ushort wProcessorLevel;

            /// <summary>
            /// <para>
            /// The architecture-dependent processor revision. The following table shows how the revision value is assembled for each type of
            /// processor architecture.
            /// </para>
            /// <para>
            /// <list type="table">
            /// <listheader>
            /// <term>Processor</term>
            /// <term>Value</term>
            /// </listheader>
            /// <item>
            /// <term>Intel Pentium, Cyrix, or NextGen 586</term>
            /// <term>
            /// The high byte is the model and the low byte is the stepping. For example, if the value is xxyy, the model number and stepping
            /// can be displayed as
            /// follows: Model xx, Stepping yy
            /// </term>
            /// </item>
            /// <item>
            /// <term>Intel 80386 or 80486</term>
            /// <term>
            /// A value of the form xxyz. If xx is equal to 0xFF, y - 0xA is the model number, and z is the stepping identifier.If xx is not
            /// equal to 0xFF, xx + 'A' is the stepping letter and yz is the minor stepping.
            /// </term>
            /// </item>
            /// <item>
            /// <term>ARM</term>
            /// <term>Reserved.</term>
            /// </item>
            /// </list>
            /// </para>
            /// </summary>
            public ushort wProcessorRevision;
        }

        // Method Imports \\

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern SafeObjectHandle CreateToolhelp32Snapshot([In] uint dwFlags, [In] uint th32ProcessID);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool Process32First([In] SafeObjectHandle hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool Thread32Next([In] SafeObjectHandle hSnapshot, ref THREADENTRY32 lppe);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool Thread32First([In] SafeObjectHandle hSnapshot, ref THREADENTRY32 lppe);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool Process32Next([In] SafeObjectHandle hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.Success)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetProcessMemoryInfo(SafeProcessHandle hProcess, [Out] out PROCESS_MEMORY_COUNTERS_EX counters, [In] uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetProcessHandleCount(SafeProcessHandle hProcess, out uint pdwHandleCount);

        // Process dump support.
        [DllImport("dbghelp.dll", EntryPoint = "MiniDumpWriteDump", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool MiniDumpWriteDump(SafeProcessHandle hProcess, uint processId, SafeHandle hFile, MINIDUMP_TYPE dumpType, IntPtr expParam, IntPtr userStreamParam, IntPtr callbackParam);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, TCP_TABLE_CLASS tblClass, uint reserved = 0);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeProcessHandle OpenProcess(uint processAccess, bool bInheritHandle,uint processId);

        [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern uint GetModuleBaseName(SafeProcessHandle hProcess, [Optional] IntPtr hModule, [MarshalAs(UnmanagedType.LPTStr)] StringBuilder lpBaseName, uint nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetProcessTimes(SafeProcessHandle ProcessHandle, out FILETIME CreationTime, out FILETIME ExitTime, out FILETIME KernelTime, out FILETIME UserTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        internal static extern int PssCaptureSnapshot(SafeProcessHandle ProcessHandle, PSS_CAPTURE_FLAGS CaptureFlags, uint ThreadContextFlags, out IntPtr SnapshotHandle);

        [DllImport("kernel32.dll", SetLastError = false, ExactSpelling = true)]
        public static extern int PssQuerySnapshot(IntPtr SnapshotHandle, PSS_QUERY_INFORMATION_CLASS InformationClass, IntPtr Buffer, uint BufferLength);

        [DllImport("kernel32.dll", SetLastError = false, ExactSpelling = true)]
        public static extern int PssFreeSnapshot(IntPtr ProcessHandle, IntPtr SnapshotHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = false, ExactSpelling = true)]
        public static extern void GetNativeSystemInfo(out SYSTEM_INFO lpSystemInfo);

        // Impls/Helpers \\

        private static readonly string[] ignoreProcessList = new string[]
        {
            "cmd.exe", "conhost.exe", "csrss.exe","fontdrvhost.exe", "lsass.exe", "backgroundTaskHost.exe",
            "LsaIso.exe", "services.exe", "smss.exe", "svchost.exe", "taskhostw.exe",
            "wininit.exe", "winlogon.exe", "WUDFHost.exe", "WmiPrvSE.exe",
            "TextInputHost.exe", "vmcompute.exe", "vmms.exe", "vmwp.exe", "vmmem",
            "Fabric.exe", "FabricHost.exe", "FabricApplicationGateway.exe", "FabricCAS.exe", 
            "FabricDCA.exe", "FabricDnsService.exe", "FabricFAS.exe", "FabricGateway.exe", 
            "FabricHost.exe", "FabricIS.exe", "FabricRM.exe", "FabricUS.exe",
            "System", "System interrupts", "Secure System", "Registry"
        };

        internal static SafeProcessHandle GetProcessHandle(uint id)
        {
            return OpenProcess((uint)ProcessAccessFlags.VirtualMemoryRead | (uint)ProcessAccessFlags.QueryInformation, false, id);
        }

        private static string GetProcessNameFromId(uint pid)
        {
            SafeProcessHandle hProc = null;
            StringBuilder sbProcName = new StringBuilder(1024);

            try
            {
                hProc = GetProcessHandle(pid);

                if (!hProc.IsInvalid && !hProc.IsClosed)
                {
                    // Get the name of the process.
                    if (GetModuleBaseName(hProc, IntPtr.Zero, sbProcName, (uint)sbProcName.Capacity) == 0)
                    {
                        throw new Win32Exception($"Failure in GetProcessNameFromId(uint): GetModuleBaseName -> {Marshal.GetLastWin32Error()}");
                    }
                }
                return sbProcName.ToString();
            }
            finally
            {
                sbProcName.Clear();
                sbProcName = null;
                hProc.Dispose();
                hProc = null;
            }
        }

        public static MEMORYSTATUSEX GetSystemMemoryInfo()
        {
            MEMORYSTATUSEX memory = new MEMORYSTATUSEX();

            if (!GlobalMemoryStatusEx(memory))
            {
                throw new Win32Exception($"NativeMethods.GetSystemMemoryInfo failed with Win32 error code {Marshal.GetLastWin32Error()}");
            }

            return memory;
        }

        /// <summary>
        /// Gets the child processes, if any, belonging to the process with supplied pid.
        /// </summary>
        /// <param name="parentpid">The process ID of parent process.</param>
        /// <param name="handleToSnapshot">Handle to process snapshot (created using NativeMethods.CreateToolhelp32Snapshot).</param>
        /// <returns>A List of tuple (string procName,  int procId) representing each child process.</returns>
        /// <exception cref="Win32Exception">A Win32 Error Code will be present in the exception Message.</exception>
        public static List<(string procName, int procId)> GetChildProcesses(int parentpid, SafeObjectHandle handleToSnapshot = null)
        {
            if (parentpid < 1)
            {
                return null;
            }

            bool isLocalSnapshot = false;

            try
            {
                if (handleToSnapshot == null || handleToSnapshot.IsInvalid || handleToSnapshot.IsClosed)
                {
                    isLocalSnapshot = true;
                    handleToSnapshot = CreateToolhelp32Snapshot((uint)CreateToolhelp32SnapshotFlags.TH32CS_SNAPPROCESS, 0);
                    
                    if (handleToSnapshot.IsInvalid)
                    {
                        throw new Win32Exception(
                            $"NativeMethods.CreateToolhelp32Snapshot: Failed to get process snapshot with error code {Marshal.GetLastWin32Error()}");
                    }
                }

                List<(string procName, int procId)> childProcs = new List<(string procName, int procId)>();
                PROCESSENTRY32 procEntry = new PROCESSENTRY32
                {
                    dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32))
                };

                if (!Process32First(handleToSnapshot, ref procEntry))
                {
                    throw new Win32Exception(
                        $"NativeMethods.GetChildProcesses({parentpid}): Failed to process snapshot at Process32First with Win32 error code {Marshal.GetLastWin32Error()}");
                }

                do
                {
                    try
                    {
                        if (procEntry.th32ProcessID == 0 || ignoreProcessList.Any(f => f == procEntry.szExeFile))
                        {
                            continue;
                        }

                        if (parentpid == (int)procEntry.th32ParentProcessID)
                        {
                            // Make sure the parent process is still the active process with supplied identifier.
                            string suppliedParentProcIdName = GetProcessNameFromId((uint)parentpid);
                            string parentSnapProcName = GetProcessNameFromId(procEntry.th32ParentProcessID);
                            
                            if (suppliedParentProcIdName.Equals(parentSnapProcName))
                            {
                                childProcs.Add((procEntry.szExeFile.Replace(".exe", ""), (int)procEntry.th32ProcessID));
                            }
                        }
                    }
                    catch (ArgumentException)
                    {

                    }
                    catch (Win32Exception)
                    {
                        // From GetProcessNameFromId.
                    }

                } while (Process32Next(handleToSnapshot, ref procEntry));

                return childProcs;
            }
            finally
            {
                if (isLocalSnapshot)
                {
                    handleToSnapshot.Dispose();
                    handleToSnapshot = null;
                }
            }
        }

        /// <summary>
        /// Gets the number of execution threads started by the process with supplied pid.
        /// </summary>
        /// <param name="pid">The id of the process (pid).</param>
        /// <returns>The number of execution threads started by the process.</returns>
        /// <exception cref="Win32Exception">A Win32 Error Code will be present in the exception Message.</exception>
        public static int GetProcessThreadCount(int pid)
        {
            uint threadCnt = 0;
            IntPtr snap = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;
            SafeProcessHandle hProc = null;
            const uint psProcHandleFlags =
                        (uint)ProcessAccessFlags.QueryInformation |
                        (uint)ProcessAccessFlags.VirtualMemoryOperation |
                        (uint)ProcessAccessFlags.VirtualMemoryRead |
                        (uint)ProcessAccessFlags.DuplicateHandle;

            try
            {
                hProc = OpenProcess(psProcHandleFlags, false, (uint)pid);

                if (hProc.IsInvalid)
                {
                    return 0;
                }

                int retSnap = PssCaptureSnapshot(hProc, PSS_CAPTURE_FLAGS.PSS_CAPTURE_THREADS, CONTEXT_FLAG.CONTEXT_ALL, out snap);

                if (retSnap != 0 || snap == IntPtr.Zero)
                {
                    throw new Win32Exception(
                       $"GetProcessThreadCount({pid}) [PssCaptureSnapshot]: Failed with Win32 error code {Marshal.GetLastWin32Error()}");
                }

                int size = Marshal.SizeOf(typeof(PSS_THREAD_INFORMATION));

                // For memory pressure case (underlying machine running out of available memory), let the OOM take FO down: Never catch OOM.
                buffer = Marshal.AllocHGlobal(size);

                int retQuery = PssQuerySnapshot(snap, PSS_QUERY_INFORMATION_CLASS.PSS_QUERY_THREAD_INFORMATION, buffer, (uint)size);
                if (retQuery != 0)
                {
                    throw new Win32Exception(
                       $"GetProcessThreadCount({pid}) [PssQuerySnapshot]: Failed with Win32 error code {Marshal.GetLastWin32Error()}");
                }

                PSS_THREAD_INFORMATION threadInfo = (PSS_THREAD_INFORMATION)Marshal.PtrToStructure(buffer, typeof(PSS_THREAD_INFORMATION));
                threadCnt = threadInfo.ThreadsCaptured;
            }
            catch (ArgumentException)
            {
                return 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                int success = PssFreeSnapshot(GetCurrentProcess(), snap);
                if (success != 0)
                {
                    //...
                }
                hProc.Dispose();
                hProc = null;
            }

            return (int)threadCnt;
        }

        /// <summary>
        /// Get the process name for the specified process identifier.
        /// </summary>
        /// <param name="pid">The process id.</param>
        /// <returns>Process name string, if successful. Else, null.</returns>
        public static string GetProcessNameFromId(int pid)
        {
            try
            {
                string s = GetProcessNameFromId((uint)pid);
                if (s?.Length == 0)
                {
                    return null;
                }

                return s.Replace(".exe", ""); 
            }
            catch (ArgumentException)
            {

            }
            catch (Win32Exception)
            {

            }

            return null;
        }

        public static DateTime GetProcessStartTime(int procId)
        {
            SafeProcessHandle procHandle = null;

            try
            {
                procHandle = GetProcessHandle((uint)procId);

                if (procHandle.IsInvalid)
                {
                    throw new Win32Exception($"Failure in GetProcessStartTime: {Marshal.GetLastWin32Error()}");
                }

                if (!GetProcessTimes(procHandle, out FILETIME ftCreation, out _, out _, out _))
                {
                    throw new Win32Exception($"Failure in GetProcessStartTime: {Marshal.GetLastWin32Error()}");
                }

                try
                {
                    ulong ufiletime = unchecked((((ulong)(uint)ftCreation.dwHighDateTime) << 32) | (uint)ftCreation.dwLowDateTime);
                    var startTime = DateTime.FromFileTimeUtc((long)ufiletime);
                    return startTime;
                }
                catch (ArgumentException)
                {

                }

                return DateTime.MinValue;
            }
            finally
            {
                procHandle?.Dispose();
                procHandle = null;
            }
        }

        // Networking \\
        // Credit: http://pinvoke.net/default.aspx/iphlpapi/GetExtendedTcpTable.html

        /// <summary>
        /// Gets a list of TCP (v4) connection info objects for use in determining TCP ports in use per process or machine-wide.
        /// </summary>
        /// <returns>List of MIB_TCPROW_OWNER_PID objects.</returns>
        public static List<MIB_TCPROW_OWNER_PID> GetAllTcpConnections()
        {
            return GetTCPConnections<MIB_TCPROW_OWNER_PID, MIB_TCPTABLE_OWNER_PID>(AF_INET);
        }

        public static List<MIB_TCP6ROW_OWNER_PID> GetAllTcpIpv6Connections()
        {
            return GetTCPConnections<MIB_TCP6ROW_OWNER_PID, MIB_TCP6TABLE_OWNER_PID>(AF_INET6);
        }

        private static List<IPR> GetTCPConnections<IPR, IPT>(int ipVersion)//IPR = Row Type, IPT = Table Type
        {
            IPR[] tableRows;
            int buffSize = 0;

            var dwNumEntriesField = typeof(IPT).GetField("dwNumEntries");

            // Determine how much memory to allocate.
            _ = GetExtendedTcpTable(IntPtr.Zero, ref buffSize, true, ipVersion, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL);
            IntPtr tcpTablePtr = Marshal.AllocHGlobal(buffSize);

            try
            {
                uint ret = GetExtendedTcpTable(tcpTablePtr, ref buffSize, true, ipVersion, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL);

                if (ret != 0)
                {
                    throw new Win32Exception($"NativeMethods.GetTCPConnections: Failed to get TCP connections with Win32 error {Marshal.GetLastWin32Error()}");
                }

                // get the number of entries in the table
                IPT table = (IPT)Marshal.PtrToStructure(tcpTablePtr, typeof(IPT));
                int rowStructSize = Marshal.SizeOf(typeof(IPR));
                uint numEntries = (uint)dwNumEntriesField.GetValue(table);

                // buffer we will be returning
                tableRows = new IPR[numEntries];
                IntPtr rowPtr = (IntPtr)((long)tcpTablePtr + 4);

                for (int i = 0; i < numEntries; ++i)
                {
                    IPR tcpRow = (IPR)Marshal.PtrToStructure(rowPtr, typeof(IPR));
                    tableRows[i] = tcpRow;
                    rowPtr = (IntPtr)((long)rowPtr + rowStructSize);   // next entry
                }
            }
            finally
            {
                // Free memory
                Marshal.FreeHGlobal(tcpTablePtr);
            }

            return tableRows != null ? tableRows.ToList() : new List<IPR>();
        }

        // Cleanup
        public static bool ReleaseHandle(IntPtr handle)
        {
            if (handle != IntPtr.Zero)
            {
                if (!CloseHandle(handle))
                {
                    throw new Win32Exception($"NativeMethods.ReleaseHandle: Failed to release handle with Win32 error {Marshal.GetLastWin32Error()}");
                }

                handle = IntPtr.Zero;
                return true;
            }
            return false;
        }

        // Safe object handle.
        public sealed class SafeObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeObjectHandle()
                : base(ownsHandle: true)
            {

            }

            protected override bool ReleaseHandle()
            {
                return NativeMethods.ReleaseHandle(handle);
            }
        }
    }
}
