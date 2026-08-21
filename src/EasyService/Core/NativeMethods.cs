using System.Runtime.InteropServices;
using System.Text;

namespace EasyService.Core;

/// <summary>
/// Raw Win32 interop. EasyService deliberately has no NuGet dependencies so the
/// published binary stays one small executable, just like nssm.exe.
/// </summary>
internal static class Native
{
    // ---------------------------------------------------------------- SCM ---

    public const uint SC_MANAGER_CONNECT = 0x0001;
    public const uint SC_MANAGER_CREATE_SERVICE = 0x0002;
    public const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    public const uint SC_MANAGER_ALL_ACCESS = 0xF003F;

    public const uint SERVICE_QUERY_CONFIG = 0x0001;
    public const uint SERVICE_CHANGE_CONFIG = 0x0002;
    public const uint SERVICE_QUERY_STATUS = 0x0004;
    public const uint SERVICE_START = 0x0010;
    public const uint SERVICE_STOP = 0x0020;
    public const uint SERVICE_PAUSE_CONTINUE = 0x0040;
    public const uint SERVICE_ALL_ACCESS = 0xF01FF;
    public const uint DELETE = 0x00010000;

    public const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
    public const uint SERVICE_INTERACTIVE_PROCESS = 0x00000100;
    public const uint SERVICE_WIN32 = 0x00000030;

    public const uint SERVICE_BOOT_START = 0;
    public const uint SERVICE_SYSTEM_START = 1;
    public const uint SERVICE_AUTO_START = 2;
    public const uint SERVICE_DEMAND_START = 3;
    public const uint SERVICE_DISABLED = 4;

    public const uint SERVICE_ERROR_IGNORE = 0;
    public const uint SERVICE_ERROR_NORMAL = 1;
    public const uint SERVICE_ERROR_SEVERE = 2;
    public const uint SERVICE_ERROR_CRITICAL = 3;

    public const uint SERVICE_STOPPED = 0x00000001;
    public const uint SERVICE_START_PENDING = 0x00000002;
    public const uint SERVICE_STOP_PENDING = 0x00000003;
    public const uint SERVICE_RUNNING = 0x00000004;
    public const uint SERVICE_CONTINUE_PENDING = 0x00000005;
    public const uint SERVICE_PAUSE_PENDING = 0x00000006;
    public const uint SERVICE_PAUSED = 0x00000007;

    public const uint SERVICE_CONTROL_STOP = 0x00000001;
    public const uint SERVICE_CONTROL_PAUSE = 0x00000002;
    public const uint SERVICE_CONTROL_CONTINUE = 0x00000003;
    public const uint SERVICE_CONTROL_INTERROGATE = 0x00000004;
    public const uint SERVICE_CONTROL_SHUTDOWN = 0x00000005;
    public const uint SERVICE_CONTROL_PRESHUTDOWN = 0x0000000F;

    public const uint SERVICE_ACCEPT_STOP = 0x00000001;
    public const uint SERVICE_ACCEPT_PAUSE_CONTINUE = 0x00000002;
    public const uint SERVICE_ACCEPT_SHUTDOWN = 0x00000004;
    public const uint SERVICE_ACCEPT_PRESHUTDOWN = 0x00000100;

    public const uint SERVICE_CONFIG_DESCRIPTION = 1;
    public const uint SERVICE_CONFIG_FAILURE_ACTIONS = 2;
    public const uint SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;
    public const uint SERVICE_CONFIG_FAILURE_ACTIONS_FLAG = 4;
    public const uint SERVICE_CONFIG_PRESHUTDOWN_INFO = 7;

    public const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
    public const uint SC_STATUS_PROCESS_INFO = 0;
    public const uint SC_ENUM_PROCESS_INFO = 0;

    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_MORE_DATA = 234;
    public const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    public const int ERROR_SERVICE_EXISTS = 1073;
    public const int ERROR_SERVICE_MARKED_FOR_DELETE = 1072;
    public const int ERROR_SERVICE_NOT_ACTIVE = 1062;
    public const int ERROR_SERVICE_ALREADY_RUNNING = 1056;
    public const int ERROR_BROKEN_PIPE = 109;

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ENUM_SERVICE_STATUS_PROCESS
    {
        public IntPtr lpServiceName;
        public IntPtr lpDisplayName;
        public SERVICE_STATUS_PROCESS ServiceStatusProcess;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct QUERY_SERVICE_CONFIG
    {
        public uint dwServiceType;
        public uint dwStartType;
        public uint dwErrorControl;
        public IntPtr lpBinaryPathName;
        public IntPtr lpLoadOrderGroup;
        public uint dwTagId;
        public IntPtr lpDependencies;
        public IntPtr lpServiceStartName;
        public IntPtr lpDisplayName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SERVICE_DESCRIPTION
    {
        public IntPtr lpDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_DELAYED_AUTO_START_INFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fDelayedAutostart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_PRESHUTDOWN_INFO
    {
        public uint dwPreshutdownTimeout;
    }

    public const int SC_ACTION_NONE = 0;
    public const int SC_ACTION_RESTART = 1;
    public const int SC_ACTION_REBOOT = 2;
    public const int SC_ACTION_RUN_COMMAND = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct SC_ACTION
    {
        public int Type;
        public uint Delay;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SERVICE_FAILURE_ACTIONS
    {
        public uint dwResetPeriod;
        public IntPtr lpRebootMsg;
        public IntPtr lpCommand;
        public uint cActions;
        public IntPtr lpsaActions;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_FAILURE_ACTIONS_FLAG
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fFailureActionsOnNonCrashFailures;
    }

    public delegate void ServiceMainProc(int argc, IntPtr argv);

    public delegate uint HandlerEx(uint control, uint eventType, IntPtr eventData, IntPtr context);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SERVICE_TABLE_ENTRY
    {
        public IntPtr lpServiceName;
        public IntPtr lpServiceProc;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenServiceW(IntPtr scm, string serviceName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateServiceW(
        IntPtr scm, string serviceName, string displayName, uint desiredAccess,
        uint serviceType, uint startType, uint errorControl, string binaryPath,
        string? loadOrderGroup, IntPtr tagId, string? dependencies,
        string? serviceStartName, string? password);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceConfigW(
        IntPtr service, uint serviceType, uint startType, uint errorControl,
        string? binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies,
        string? serviceStartName, string? password, string? displayName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceConfig2W(IntPtr service, uint infoLevel, IntPtr info);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryServiceConfigW(IntPtr service, IntPtr config, uint bufSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryServiceConfig2W(IntPtr service, uint infoLevel, IntPtr buffer, uint bufSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryServiceStatusEx(IntPtr service, uint infoLevel, IntPtr buffer, uint bufSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumServicesStatusExW(
        IntPtr scm, uint infoLevel, uint serviceType, uint serviceState,
        IntPtr services, uint bufSize, out uint bytesNeeded, out uint servicesReturned,
        ref uint resumeHandle, string? groupName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool StartServiceW(IntPtr service, uint numArgs, IntPtr args);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ControlService(IntPtr service, uint control, ref SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool StartServiceCtrlDispatcherW(IntPtr serviceTable);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr RegisterServiceCtrlHandlerExW(string serviceName, HandlerEx handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetServiceStatus(IntPtr statusHandle, ref SERVICE_STATUS status);

    // ---------------------------------------------------------------- LSA ---

    [StructLayout(LayoutKind.Sequential)]
    public struct LSA_OBJECT_ATTRIBUTES
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LSA_UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    public const uint POLICY_CREATE_ACCOUNT = 0x00000010;
    public const uint POLICY_LOOKUP_NAMES = 0x00000800;

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern uint LsaOpenPolicy(IntPtr systemName, ref LSA_OBJECT_ATTRIBUTES attrs, uint access, out IntPtr policyHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern uint LsaAddAccountRights(IntPtr policyHandle, byte[] sid, LSA_UNICODE_STRING[] rights, uint countOfRights);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern uint LsaClose(IntPtr policyHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern int LsaNtStatusToWinError(uint status);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LookupAccountNameW(
        string? systemName, string accountName, byte[]? sid, ref uint sidSize,
        StringBuilder? referencedDomain, ref uint domainSize, out int use);

    // ------------------------------------------------------------ Processes ---

    public const uint CREATE_SUSPENDED = 0x00000004;
    public const uint CREATE_NEW_CONSOLE = 0x00000010;
    public const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    public const uint CREATE_NO_WINDOW = 0x08000000;
    public const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;

    public const uint IDLE_PRIORITY_CLASS = 0x00000040;
    public const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
    public const uint NORMAL_PRIORITY_CLASS = 0x00000020;
    public const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
    public const uint HIGH_PRIORITY_CLASS = 0x00000080;
    public const uint REALTIME_PRIORITY_CLASS = 0x00000100;

    public const uint STARTF_USESTDHANDLES = 0x00000100;
    public const uint STARTF_USESHOWWINDOW = 0x00000001;
    public const ushort SW_HIDE = 0;

    public const uint STILL_ACTIVE = 259;
    public const uint INFINITE = 0xFFFFFFFF;
    public const uint WAIT_OBJECT_0 = 0;
    public const uint WAIT_TIMEOUT = 258;

    public const uint HANDLE_FLAG_INHERIT = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    public struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars;
        public int dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateProcessW(
        string? applicationName, StringBuilder? commandLine,
        IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, ref SECURITY_ATTRIBUTES attrs, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessAffinityMask(IntPtr process, UIntPtr mask);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    public const uint PROCESS_TERMINATE = 0x0001;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint SYNCHRONIZE = 0x00100000;

    // ---------------------------------------------------------- Job objects ---

    public const int JobObjectExtendedLimitInformation = 9;
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    public const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800;

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateJobObjectW(IntPtr attrs, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    // -------------------------------------------------------------- Console ---

    public const uint CTRL_C_EVENT = 0;
    public const uint CTRL_BREAK_EVENT = 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetConsoleCtrlHandler(IntPtr handlerRoutine, [MarshalAs(UnmanagedType.Bool)] bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

    // -------------------------------------------------------------- Windows ---

    public const uint WM_CLOSE = 0x0010;
    public const uint WM_QUIT = 0x0012;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    // ------------------------------------------------------------- Toolhelp ---

    public const uint TH32CS_SNAPTHREAD = 0x00000004;
    public const uint TH32CS_SNAPPROCESS = 0x00000002;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    public struct THREADENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ThreadID;
        public uint th32OwnerProcessID;
        public int tpBasePri;
        public int tpDeltaPri;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Thread32First(IntPtr snapshot, ref THREADENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Thread32Next(IntPtr snapshot, ref THREADENTRY32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32 entry);

    // ------------------------------------------------------------------- IO ---

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadFile(IntPtr handle, byte[] buffer, uint toRead, out uint read, IntPtr overlapped);

    // ------------------------------------------------- Job accounting / RAM ---

    public const int JobObjectBasicAccountingInformation = 1;
    public const int JobObjectBasicProcessIdList = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length, out uint returned);

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_MEMORY_COUNTERS
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessMemoryInfo(IntPtr process, out PROCESS_MEMORY_COUNTERS counters, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
}
