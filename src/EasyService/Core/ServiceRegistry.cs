using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

using EasyService.Resources;

namespace EasyService.Core;

public sealed record ServiceInfo(
    string Name,
    string DisplayName,
    uint State,
    uint ProcessId,
    StartupType Startup,
    string BinaryPath,
    string Account,
    bool ManagedByEasyService,
    string Target)
{
    public string StateText => State switch
    {
        Native.SERVICE_STOPPED => S.Svc_State_Stopped,
        Native.SERVICE_START_PENDING => S.Svc_State_StartPending,
        Native.SERVICE_STOP_PENDING => S.Svc_State_StopPending,
        Native.SERVICE_RUNNING => S.Svc_State_Running,
        Native.SERVICE_CONTINUE_PENDING => S.Svc_State_ContinuePending,
        Native.SERVICE_PAUSE_PENDING => S.Svc_State_PausePending,
        Native.SERVICE_PAUSED => S.Svc_State_Paused,
        _ => S.Svc_State_Unknown,
    };

    public string StartupText => Startup switch
    {
        StartupType.Automatic => S.Svc_Startup_Automatic,
        StartupType.AutomaticDelayed => S.Svc_Startup_AutomaticDelayed,
        StartupType.Manual => S.Svc_Startup_Manual,
        StartupType.Disabled => S.Svc_Startup_Disabled,
        _ => S.Common_UnknownShort,
    };

    public bool IsRunning => State == Native.SERVICE_RUNNING;
    public bool IsStopped => State == Native.SERVICE_STOPPED;
}

/// <summary>Thin, exception-throwing wrapper around the Windows Service Control Manager.</summary>
public static class ServiceRegistry
{
    private static Win32Exception Fail(string what) => new(Marshal.GetLastWin32Error(), $"{what} ({Marshal.GetLastWin32Error()}): {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

    private sealed class ScmHandle : IDisposable
    {
        public IntPtr Handle { get; }
        public ScmHandle(IntPtr h) => Handle = h;
        public void Dispose() { if (Handle != IntPtr.Zero) Native.CloseServiceHandle(Handle); }
        public static implicit operator IntPtr(ScmHandle h) => h.Handle;
    }

    private static ScmHandle OpenManager(uint access)
    {
        var scm = Native.OpenSCManagerW(null, null, access);
        if (scm == IntPtr.Zero) throw Fail(S.Svc_Err_OpenScm);
        return new ScmHandle(scm);
    }

    private static ScmHandle OpenService(ScmHandle scm, string name, uint access)
    {
        var svc = Native.OpenServiceW(scm, name, access);
        if (svc == IntPtr.Zero) throw Fail(S.Svc_Err_OpenService(name));
        return new ScmHandle(svc);
    }

    public static string ExecutablePath =>
        System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
        ?? Path.Combine(AppContext.BaseDirectory, "easyservice.exe");

    public static string BuildBinaryPath(string serviceName) =>
        $"\"{ExecutablePath}\" run \"{serviceName}\"";

    // ------------------------------------------------------------ enumerate ---

    public static List<ServiceInfo> EnumerateServices()
    {
        var result = new List<ServiceInfo>();
        using var scm = OpenManager(Native.SC_MANAGER_CONNECT | Native.SC_MANAGER_ENUMERATE_SERVICE);

        uint resume = 0;
        Native.EnumServicesStatusExW(scm, Native.SC_ENUM_PROCESS_INFO, Native.SERVICE_WIN32, 3 /*ALL*/,
            IntPtr.Zero, 0, out var needed, out _, ref resume, null);

        if (needed == 0) return result;

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            resume = 0;
            if (!Native.EnumServicesStatusExW(scm, Native.SC_ENUM_PROCESS_INFO, Native.SERVICE_WIN32, 3,
                    buffer, needed, out _, out var count, ref resume, null))
                throw Fail(S.Svc_Err_Enumerate);

            var size = Marshal.SizeOf<Native.ENUM_SERVICE_STATUS_PROCESS>();
            for (var i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<Native.ENUM_SERVICE_STATUS_PROCESS>(buffer + i * size);
                var name = Marshal.PtrToStringUni(entry.lpServiceName) ?? "";
                var display = Marshal.PtrToStringUni(entry.lpDisplayName) ?? name;

                var (startup, binPath, account) = ReadStaticConfig(name);
                var managed = IsManaged(name);
                result.Add(new ServiceInfo(
                    name, display,
                    entry.ServiceStatusProcess.dwCurrentState,
                    entry.ServiceStatusProcess.dwProcessId,
                    startup, binPath, account,
                    managed, DescribeTarget(name, managed, binPath)));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }

        return result;
    }

    /// <summary>
    /// Opens a service's registry key. Some services (protected processes, hardened third-party
    /// installs) deny read access even to administrators, so a failure here must never be fatal:
    /// the service simply shows up with fewer details.
    /// </summary>
    private static RegistryKey? OpenServiceKey(string name, string? subKey = null)
    {
        var path = $@"{ServiceConfig.ServicesKey}\{name}{subKey}";
        try
        {
            return Registry.LocalMachine.OpenSubKey(path, false);
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads start type / image path / account straight from the registry. Much cheaper than
    /// opening every service with QueryServiceConfig when the list holds several hundred entries.
    /// </summary>
    private static (StartupType, string, string) ReadStaticConfig(string name)
    {
        try
        {
            using var key = OpenServiceKey(name);
            if (key is null) return (StartupType.Manual, "", "");

            var raw = Convert.ToInt32(key.GetValue("Start", 3));
            var delayed = Convert.ToInt32(key.GetValue("DelayedAutostart", 0)) != 0;
            var startup = raw switch
            {
                2 => delayed ? StartupType.AutomaticDelayed : StartupType.Automatic,
                4 => StartupType.Disabled,
                _ => StartupType.Manual,
            };
            var path = key.GetValue("ImagePath", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
            var account = key.GetValue("ObjectName", "") as string ?? "";
            return (startup, path, account);
        }
        catch
        {
            return (StartupType.Manual, "", "");
        }
    }

    /// <summary>
    /// What the service actually launches: for our own services the supervised application,
    /// for everything else the raw image path. Resolved during enumeration so the UI thread
    /// never has to touch the registry while refreshing the list.
    /// </summary>
    private static string DescribeTarget(string name, bool managed, string binaryPath)
    {
        if (!managed) return binaryPath;
        try
        {
            var config = ServiceConfig.Load(name);
            if (config is null) return binaryPath;
            return string.IsNullOrWhiteSpace(config.AppParameters)
                ? config.Application
                : $"{config.Application} {config.AppParameters}";
        }
        catch
        {
            return binaryPath;
        }
    }

    public static bool Exists(string name)
    {
        try
        {
            using var scm = OpenManager(Native.SC_MANAGER_CONNECT);
            var svc = Native.OpenServiceW(scm, name, Native.SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero) return false;
            Native.CloseServiceHandle(svc);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when the service was created by EasyService (it has our Parameters key).</summary>
    public static bool IsManaged(string name)
    {
        using var key = OpenServiceKey(name, ServiceConfig.ParametersKeySuffix);
        return key?.GetValue("Application") is string s && !string.IsNullOrWhiteSpace(s);
    }

    public static ServiceInfo? Query(string name)
    {
        try
        {
            using var scm = OpenManager(Native.SC_MANAGER_CONNECT);
            using var svc = OpenService(scm, name, Native.SERVICE_QUERY_STATUS);
            var status = QueryStatus(svc);
            var (startup, binPath, account) = ReadStaticConfig(name);
            var managed = IsManaged(name);
            return new ServiceInfo(name, GetDisplayName(name) ?? name, status.dwCurrentState,
                status.dwProcessId, startup, binPath, account, managed,
                DescribeTarget(name, managed, binPath));
        }
        catch
        {
            return null;
        }
    }

    public static string? GetDisplayName(string name)
    {
        using var key = OpenServiceKey(name);
        return key?.GetValue("DisplayName") as string;
    }

    public static string GetDescription(string name)
    {
        using var key = OpenServiceKey(name);
        return key?.GetValue("Description") as string ?? "";
    }

    private static Native.SERVICE_STATUS_PROCESS QueryStatus(IntPtr service)
    {
        var size = Marshal.SizeOf<Native.SERVICE_STATUS_PROCESS>();
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (!Native.QueryServiceStatusEx(service, Native.SC_STATUS_PROCESS_INFO, buf, (uint)size, out _))
                throw Fail(S.Svc_Err_QueryStatus);
            return Marshal.PtrToStructure<Native.SERVICE_STATUS_PROCESS>(buf);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // -------------------------------------------------------------- install ---

    public static void Install(ServiceConfig config)
    {
        if (config.Logon == LogonType.Account)
            LsaHelper.GrantServiceLogonRight(config.AccountName);

        using var scm = OpenManager(Native.SC_MANAGER_CONNECT | Native.SC_MANAGER_CREATE_SERVICE);

        var serviceType = Native.SERVICE_WIN32_OWN_PROCESS;
        if (config.InteractWithDesktop && config.Logon == LogonType.LocalSystem)
            serviceType |= Native.SERVICE_INTERACTIVE_PROCESS;

        var svcHandle = Native.CreateServiceW(
            scm,
            config.ServiceName,
            config.EffectiveDisplayName,
            Native.SERVICE_ALL_ACCESS,
            serviceType,
            ToScmStartType(config.Startup),
            Native.SERVICE_ERROR_NORMAL,
            BuildBinaryPath(config.ServiceName),
            null,
            IntPtr.Zero,
            DependencyString(config.Dependencies),
            config.AccountForScm,
            config.PasswordForScm);

        if (svcHandle == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, err switch
            {
                Native.ERROR_SERVICE_EXISTS => S.Svc_Err_Exists(config.ServiceName),
                Native.ERROR_SERVICE_MARKED_FOR_DELETE => S.Svc_Err_MarkedForDelete(config.ServiceName),
                _ => S.Svc_Err_Create(new Win32Exception(err).Message),
            });
        }

        using var svc = new ScmHandle(svcHandle);
        ApplyExtendedConfig(svc, config);
        config.Save();
    }

    public static void Update(ServiceConfig config)
    {
        if (config.Logon == LogonType.Account)
            LsaHelper.GrantServiceLogonRight(config.AccountName);

        using var scm = OpenManager(Native.SC_MANAGER_CONNECT);
        using var svc = OpenService(scm, config.ServiceName, Native.SERVICE_CHANGE_CONFIG | Native.SERVICE_QUERY_CONFIG | Native.SERVICE_QUERY_STATUS);

        var serviceType = Native.SERVICE_WIN32_OWN_PROCESS;
        if (config.InteractWithDesktop && config.Logon == LogonType.LocalSystem)
            serviceType |= Native.SERVICE_INTERACTIVE_PROCESS;

        // An empty password string means "keep the stored one"; null resets to no password.
        var password = config.Logon == LogonType.Account
            ? (string.IsNullOrEmpty(config.Password) ? null : config.Password)
            : "";

        if (!Native.ChangeServiceConfigW(
                svc, serviceType, ToScmStartType(config.Startup), Native.SERVICE_ERROR_NORMAL,
                BuildBinaryPath(config.ServiceName), null, IntPtr.Zero,
                DependencyString(config.Dependencies),
                config.AccountForScm, password, config.EffectiveDisplayName))
            throw Fail(S.Svc_Err_ChangeConfig);

        ApplyExtendedConfig(svc, config);
        config.Save();
    }

    private static void ApplyExtendedConfig(ScmHandle svc, ServiceConfig config)
    {
        SetDescription(svc, config.Description);
        SetDelayedAutoStart(svc, config.Startup == StartupType.AutomaticDelayed);
        SetPreshutdownTimeout(svc, (uint)Math.Max(30_000,
            config.StopConsoleMs + config.StopWindowMs + config.StopThreadsMs + 10_000));
        SetFailureActions(svc, config);
    }

    private static void SetDescription(IntPtr svc, string description)
    {
        var ptr = Marshal.StringToHGlobalUni(description ?? "");
        var infoPtr = IntPtr.Zero;
        try
        {
            var info = new Native.SERVICE_DESCRIPTION { lpDescription = ptr };
            infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf(info));
            Marshal.StructureToPtr(info, infoPtr, false);
            Native.ChangeServiceConfig2W(svc, Native.SERVICE_CONFIG_DESCRIPTION, infoPtr);
        }
        finally
        {
            if (infoPtr != IntPtr.Zero) Marshal.FreeHGlobal(infoPtr);
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static void SetDelayedAutoStart(IntPtr svc, bool delayed)
    {
        var info = new Native.SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = delayed };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(info));
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            Native.ChangeServiceConfig2W(svc, Native.SERVICE_CONFIG_DELAYED_AUTO_START_INFO, ptr);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private static void SetPreshutdownTimeout(IntPtr svc, uint ms)
    {
        var info = new Native.SERVICE_PRESHUTDOWN_INFO { dwPreshutdownTimeout = ms };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(info));
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            Native.ChangeServiceConfig2W(svc, Native.SERVICE_CONFIG_PRESHUTDOWN_INFO, ptr);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    /// <summary>
    /// The supervisor normally restarts the application itself; these SCM failure actions are the
    /// safety net for the (unlikely) case that the supervisor process itself dies.
    /// </summary>
    private static void SetFailureActions(IntPtr svc, ServiceConfig config)
    {
        var restart = config.DefaultExitAction == ExitAction.Restart;
        var actions = new Native.SC_ACTION[3];
        for (var i = 0; i < 3; i++)
            actions[i] = new Native.SC_ACTION
            {
                Type = restart ? Native.SC_ACTION_RESTART : Native.SC_ACTION_NONE,
                Delay = (uint)Math.Max(1000, config.RestartDelayMs),
            };

        var actionSize = Marshal.SizeOf<Native.SC_ACTION>();
        var actionsPtr = Marshal.AllocHGlobal(actionSize * actions.Length);
        var faPtr = IntPtr.Zero;
        var flagPtr = IntPtr.Zero;
        try
        {
            for (var i = 0; i < actions.Length; i++)
                Marshal.StructureToPtr(actions[i], actionsPtr + i * actionSize, false);

            var fa = new Native.SERVICE_FAILURE_ACTIONS
            {
                dwResetPeriod = 86400,
                lpRebootMsg = IntPtr.Zero,
                lpCommand = IntPtr.Zero,
                cActions = (uint)actions.Length,
                lpsaActions = actionsPtr,
            };
            faPtr = Marshal.AllocHGlobal(Marshal.SizeOf(fa));
            Marshal.StructureToPtr(fa, faPtr, false);
            Native.ChangeServiceConfig2W(svc, Native.SERVICE_CONFIG_FAILURE_ACTIONS, faPtr);

            var flag = new Native.SERVICE_FAILURE_ACTIONS_FLAG { fFailureActionsOnNonCrashFailures = true };
            flagPtr = Marshal.AllocHGlobal(Marshal.SizeOf(flag));
            Marshal.StructureToPtr(flag, flagPtr, false);
            Native.ChangeServiceConfig2W(svc, Native.SERVICE_CONFIG_FAILURE_ACTIONS_FLAG, flagPtr);
        }
        finally
        {
            if (flagPtr != IntPtr.Zero) Marshal.FreeHGlobal(flagPtr);
            if (faPtr != IntPtr.Zero) Marshal.FreeHGlobal(faPtr);
            Marshal.FreeHGlobal(actionsPtr);
        }
    }

    private static uint ToScmStartType(StartupType t) => t switch
    {
        StartupType.Automatic or StartupType.AutomaticDelayed => Native.SERVICE_AUTO_START,
        StartupType.Disabled => Native.SERVICE_DISABLED,
        _ => Native.SERVICE_DEMAND_START,
    };

    private static string? DependencyString(IEnumerable<string> deps)
    {
        var list = deps.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()).ToArray();
        if (list.Length == 0) return "\0";                 // explicitly clear dependencies
        return string.Join('\0', list) + "\0\0";
    }

    public static List<string> GetDependencies(string name)
    {
        using var key = OpenServiceKey(name);
        return (key?.GetValue("DependOnService") as string[] ?? Array.Empty<string>()).ToList();
    }

    // --------------------------------------------------------------- remove ---

    public static void Remove(string name, bool stopFirst = true)
    {
        if (stopFirst)
        {
            try { Stop(name, TimeSpan.FromSeconds(30)); } catch { /* delete anyway */ }
        }

        using (var scm = OpenManager(Native.SC_MANAGER_CONNECT))
        using (var svc = OpenService(scm, name, Native.DELETE))
        {
            if (!Native.DeleteService(svc))
            {
                var err = Marshal.GetLastWin32Error();
                if (err != Native.ERROR_SERVICE_MARKED_FOR_DELETE)
                    throw new Win32Exception(err, S.Svc_Err_Delete(new Win32Exception(err).Message));
            }
        }

        try { Registry.LocalMachine.DeleteSubKeyTree($@"{ServiceConfig.ServicesKey}\{name}", false); }
        catch { /* SCM removes the key once the last handle closes */ }

        ServiceState.Delete(name);
    }

    // ---------------------------------------------------------- start / stop ---

    public static void Start(string name, TimeSpan timeout)
    {
        using var scm = OpenManager(Native.SC_MANAGER_CONNECT);
        using var svc = OpenService(scm, name, Native.SERVICE_START | Native.SERVICE_QUERY_STATUS);

        if (!Native.StartServiceW(svc, 0, IntPtr.Zero))
        {
            var err = Marshal.GetLastWin32Error();
            if (err != Native.ERROR_SERVICE_ALREADY_RUNNING)
                throw new Win32Exception(err, S.Svc_Err_Start(new Win32Exception(err).Message));
        }
        WaitFor(svc, Native.SERVICE_RUNNING, timeout);
    }

    public static void Stop(string name, TimeSpan timeout)
    {
        using var scm = OpenManager(Native.SC_MANAGER_CONNECT);
        using var svc = OpenService(scm, name, Native.SERVICE_STOP | Native.SERVICE_QUERY_STATUS);

        var status = new Native.SERVICE_STATUS();
        if (!Native.ControlService(svc, Native.SERVICE_CONTROL_STOP, ref status))
        {
            var err = Marshal.GetLastWin32Error();
            if (err != Native.ERROR_SERVICE_NOT_ACTIVE)
                throw new Win32Exception(err, S.Svc_Err_Stop(new Win32Exception(err).Message));
        }
        WaitFor(svc, Native.SERVICE_STOPPED, timeout);
    }

    public static void Restart(string name, TimeSpan timeout)
    {
        try { Stop(name, timeout); } catch (Win32Exception e) when (e.NativeErrorCode == Native.ERROR_SERVICE_NOT_ACTIVE) { }
        Start(name, timeout);
    }

    public static void Pause(string name)
    {
        using var scm = OpenManager(Native.SC_MANAGER_CONNECT);
        using var svc = OpenService(scm, name, Native.SERVICE_PAUSE_CONTINUE | Native.SERVICE_QUERY_STATUS);
        var status = new Native.SERVICE_STATUS();
        if (!Native.ControlService(svc, Native.SERVICE_CONTROL_PAUSE, ref status))
            throw Fail(S.Svc_Err_Pause);
    }

    public static void Continue(string name)
    {
        using var scm = OpenManager(Native.SC_MANAGER_CONNECT);
        using var svc = OpenService(scm, name, Native.SERVICE_PAUSE_CONTINUE | Native.SERVICE_QUERY_STATUS);
        var status = new Native.SERVICE_STATUS();
        if (!Native.ControlService(svc, Native.SERVICE_CONTROL_CONTINUE, ref status))
            throw Fail(S.Svc_Err_Continue);
    }

    private static void WaitFor(IntPtr svc, uint desiredState, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var status = QueryStatus(svc);
            if (status.dwCurrentState == desiredState) return;
            if (desiredState == Native.SERVICE_RUNNING && status.dwCurrentState == Native.SERVICE_STOPPED
                && status.dwWin32ExitCode != 0)
                throw new Win32Exception((int)status.dwWin32ExitCode,
                    S.Svc_Err_DiedOnStart(status.dwWin32ExitCode));
            Thread.Sleep(250);
        }
        throw new TimeoutException(S.Svc_Err_Timeout);
    }
}

/// <summary>Grants "Log on as a service" so account-based services can actually start.</summary>
internal static class LsaHelper
{
    private const string SeServiceLogonRight = "SeServiceLogonRight";

    public static void GrantServiceLogonRight(string accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName)) return;

        var sid = LookupSid(accountName);
        if (sid is null) return;   // account may be a gMSA or remote; let the SCM report the problem

        var attrs = new Native.LSA_OBJECT_ATTRIBUTES { Length = Marshal.SizeOf<Native.LSA_OBJECT_ATTRIBUTES>() };
        var status = Native.LsaOpenPolicy(IntPtr.Zero, ref attrs,
            Native.POLICY_CREATE_ACCOUNT | Native.POLICY_LOOKUP_NAMES, out var policy);
        if (status != 0) return;

        var rightPtr = Marshal.StringToHGlobalUni(SeServiceLogonRight);
        try
        {
            var rights = new[]
            {
                new Native.LSA_UNICODE_STRING
                {
                    Buffer = rightPtr,
                    Length = (ushort)(SeServiceLogonRight.Length * 2),
                    MaximumLength = (ushort)((SeServiceLogonRight.Length + 1) * 2),
                },
            };
            Native.LsaAddAccountRights(policy, sid, rights, 1);
        }
        finally
        {
            Marshal.FreeHGlobal(rightPtr);
            Native.LsaClose(policy);
        }
    }

    private static byte[]? LookupSid(string accountName)
    {
        uint sidSize = 0, domainSize = 0;
        Native.LookupAccountNameW(null, accountName, null, ref sidSize, null, ref domainSize, out _);
        if (sidSize == 0) return null;

        var sid = new byte[sidSize];
        var domain = new StringBuilder((int)domainSize);
        return Native.LookupAccountNameW(null, accountName, sid, ref sidSize, domain, ref domainSize, out _)
            ? sid
            : null;
    }
}
