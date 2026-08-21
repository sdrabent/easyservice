using Microsoft.Win32;

namespace EasyService.Core;

public enum StartupType
{
    Automatic = 0,
    AutomaticDelayed = 1,
    Manual = 2,
    Disabled = 3,
}

public enum ExitAction
{
    /// <summary>Restart the application (default).</summary>
    Restart = 0,

    /// <summary>Leave the service running but do not restart the application.</summary>
    Ignore = 1,

    /// <summary>Stop the service cleanly.</summary>
    Stop = 2,
}

public enum ProcessPriority
{
    Realtime = 0,
    High = 1,
    AboveNormal = 2,
    Normal = 3,
    BelowNormal = 4,
    Idle = 5,
}

public enum LogonType
{
    LocalSystem = 0,
    LocalService = 1,
    NetworkService = 2,
    Account = 3,
}

/// <summary>
/// Everything EasyService needs to supervise one application. Persisted under
/// HKLM\SYSTEM\CurrentControlSet\Services\{name}\Parameters, mirroring the layout
/// nssm uses so the values stay inspectable with regedit.
/// </summary>
public sealed class ServiceConfig
{
    public const string ParametersKeySuffix = @"\Parameters";
    public const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    // --- identity -----------------------------------------------------------
    public string ServiceName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public StartupType Startup { get; set; } = StartupType.Automatic;

    // --- application --------------------------------------------------------
    public string Application { get; set; } = "";
    public string AppDirectory { get; set; } = "";
    public string AppParameters { get; set; } = "";
    public ProcessPriority Priority { get; set; } = ProcessPriority.Normal;
    public ulong AffinityMask { get; set; }          // 0 = all processors
    public int StartupDelayMs { get; set; }          // wait before first launch

    // --- log on -------------------------------------------------------------
    public LogonType Logon { get; set; } = LogonType.LocalSystem;
    public string AccountName { get; set; } = "";
    public string Password { get; set; } = "";
    public bool InteractWithDesktop { get; set; }

    // --- dependencies / environment ----------------------------------------
    public List<string> Dependencies { get; set; } = new();
    public List<string> Environment { get; set; } = new();   // KEY=VALUE
    public bool ReplaceEnvironment { get; set; }

    // --- logging ------------------------------------------------------------
    public string StdoutPath { get; set; } = "";
    public string StderrPath { get; set; } = "";
    public bool AppendOutput { get; set; } = true;
    public bool TimestampLines { get; set; }
    public bool RotateFiles { get; set; } = true;
    public long RotateBytes { get; set; } = 10L * 1024 * 1024;
    public int RotateSeconds { get; set; }                   // 0 = never by time
    public int RotateKeep { get; set; } = 10;                // 0 = keep everything
    public bool LogServiceEvents { get; set; } = true;       // supervisor own log

    // --- exit actions -------------------------------------------------------
    public ExitAction DefaultExitAction { get; set; } = ExitAction.Restart;
    public Dictionary<uint, ExitAction> ExitActions { get; set; } = new();
    public int RestartDelayMs { get; set; } = 1000;
    public int ThrottleMs { get; set; } = 5000;

    // --- monitoring ---------------------------------------------------------
    // Thresholds an administrator can alert on. 0 means "do not check this".
    public bool MonitoringEnabled { get; set; } = true;
    public int WarnCpuPercent { get; set; }
    public int CritCpuPercent { get; set; }
    public int WarnMemoryMb { get; set; }
    public int CritMemoryMb { get; set; }
    public int WarnRestartsPerHour { get; set; } = 3;
    public int CritRestartsPerHour { get; set; } = 10;

    // --- shutdown sequence --------------------------------------------------
    public bool StopUseConsole { get; set; } = true;
    public int StopConsoleMs { get; set; } = 1500;
    public bool StopUseWindow { get; set; } = true;
    public int StopWindowMs { get; set; } = 1500;
    public bool StopUseThreads { get; set; } = true;
    public int StopThreadsMs { get; set; } = 1500;
    public bool StopUseTerminate { get; set; } = true;
    public bool KillProcessTree { get; set; } = true;

    public string EffectiveDisplayName => string.IsNullOrWhiteSpace(DisplayName) ? ServiceName : DisplayName;

    public string AccountForScm => Logon switch
    {
        LogonType.LocalSystem => "LocalSystem",
        LogonType.LocalService => @"NT AUTHORITY\LocalService",
        LogonType.NetworkService => @"NT AUTHORITY\NetworkService",
        _ => AccountName,
    };

    public string? PasswordForScm => Logon == LogonType.Account ? Password : null;

    /// <summary>Default log file locations used when the user does not pick their own.</summary>
    public static string DefaultLogDirectory =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData),
                     "EasyService", "logs");

    public void ApplyDefaultLogPaths()
    {
        if (string.IsNullOrWhiteSpace(ServiceName)) return;
        var dir = DefaultLogDirectory;
        if (string.IsNullOrWhiteSpace(StdoutPath))
            StdoutPath = Path.Combine(dir, ServiceName + "-stdout.log");
        if (string.IsNullOrWhiteSpace(StderrPath))
            StderrPath = Path.Combine(dir, ServiceName + "-stderr.log");
    }

    public string ServiceLogPath =>
        Path.Combine(Path.GetDirectoryName(StdoutPath) is { Length: > 0 } d ? d : DefaultLogDirectory,
                     ServiceName + "-easyservice.log");

    // ------------------------------------------------------------- validation

    public IEnumerable<string> Validate(bool isNew)
    {
        if (string.IsNullOrWhiteSpace(ServiceName))
            yield return "Der Dienstname darf nicht leer sein.";
        else if (ServiceName.IndexOfAny(new[] { '/', '\\' }) >= 0)
            yield return @"Der Dienstname darf keine / oder \ enthalten.";
        else if (ServiceName.Length > 256)
            yield return "Der Dienstname ist zu lang (max. 256 Zeichen).";

        if (string.IsNullOrWhiteSpace(Application))
            yield return "Es wurde kein Programm (Pfad) angegeben.";
        else if (!File.Exists(System.Environment.ExpandEnvironmentVariables(Application)))
            yield return $"Programm nicht gefunden: {Application}";

        if (!string.IsNullOrWhiteSpace(AppDirectory) &&
            !Directory.Exists(System.Environment.ExpandEnvironmentVariables(AppDirectory)))
            yield return $"Startverzeichnis nicht gefunden: {AppDirectory}";

        if (Logon == LogonType.Account && string.IsNullOrWhiteSpace(AccountName))
            yield return "Für die Anmeldung als Konto muss ein Kontoname angegeben werden.";

        foreach (var e in Environment)
        {
            if (string.IsNullOrWhiteSpace(e)) continue;
            if (!e.Contains('='))
                yield return $"Ungültige Umgebungsvariable (erwartet NAME=WERT): {e}";
        }

        if (isNew && ServiceRegistry.Exists(ServiceName))
            yield return $"Ein Dienst mit dem Namen \"{ServiceName}\" existiert bereits.";
    }

    // ------------------------------------------------------------- persistence

    public void Save()
    {
        using var key = Registry.LocalMachine.CreateSubKey($@"{ServicesKey}\{ServiceName}{ParametersKeySuffix}", true)
                        ?? throw new IOException($"Registry-Schlüssel für {ServiceName} konnte nicht geöffnet werden.");

        key.SetValue("Application", Application, RegistryValueKind.ExpandString);
        key.SetValue("AppDirectory", AppDirectory, RegistryValueKind.ExpandString);
        key.SetValue("AppParameters", AppParameters, RegistryValueKind.ExpandString);
        key.SetValue("AppPriority", (int)Priority, RegistryValueKind.DWord);
        key.SetValue("AppAffinity", (long)AffinityMask, RegistryValueKind.QWord);
        key.SetValue("AppStartupDelay", StartupDelayMs, RegistryValueKind.DWord);

        key.SetValue("AppEnvironmentExtra", Environment.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray(),
                     RegistryValueKind.MultiString);
        key.SetValue("AppEnvironmentReplace", ReplaceEnvironment ? 1 : 0, RegistryValueKind.DWord);

        key.SetValue("AppStdout", StdoutPath, RegistryValueKind.ExpandString);
        key.SetValue("AppStderr", StderrPath, RegistryValueKind.ExpandString);
        key.SetValue("AppAppendOutput", AppendOutput ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("AppTimestampLog", TimestampLines ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("AppRotateFiles", RotateFiles ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("AppRotateBytes", RotateBytes, RegistryValueKind.QWord);
        key.SetValue("AppRotateSeconds", RotateSeconds, RegistryValueKind.DWord);
        key.SetValue("AppRotateKeep", RotateKeep, RegistryValueKind.DWord);
        key.SetValue("AppLogServiceEvents", LogServiceEvents ? 1 : 0, RegistryValueKind.DWord);

        key.SetValue("AppExitDefault", (int)DefaultExitAction, RegistryValueKind.DWord);
        key.SetValue("AppRestartDelay", RestartDelayMs, RegistryValueKind.DWord);
        key.SetValue("AppThrottle", ThrottleMs, RegistryValueKind.DWord);

        key.DeleteSubKeyTree("AppExit", throwOnMissingSubKey: false);
        if (ExitActions.Count > 0)
        {
            using var exit = key.CreateSubKey("AppExit", true)!;
            foreach (var (code, action) in ExitActions)
                exit.SetValue(code.ToString(), (int)action, RegistryValueKind.DWord);
        }

        key.SetValue("MonEnabled", MonitoringEnabled ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("MonWarnCpu", WarnCpuPercent, RegistryValueKind.DWord);
        key.SetValue("MonCritCpu", CritCpuPercent, RegistryValueKind.DWord);
        key.SetValue("MonWarnMemoryMb", WarnMemoryMb, RegistryValueKind.DWord);
        key.SetValue("MonCritMemoryMb", CritMemoryMb, RegistryValueKind.DWord);
        key.SetValue("MonWarnRestartsPerHour", WarnRestartsPerHour, RegistryValueKind.DWord);
        key.SetValue("MonCritRestartsPerHour", CritRestartsPerHour, RegistryValueKind.DWord);

        key.SetValue("AppStopUseConsole", StopUseConsole ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("AppStopConsoleDelay", StopConsoleMs, RegistryValueKind.DWord);
        key.SetValue("AppStopUseWindow", StopUseWindow ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("AppStopWindowDelay", StopWindowMs, RegistryValueKind.DWord);
        key.SetValue("AppStopUseThreads", StopUseThreads ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("AppStopThreadsDelay", StopThreadsMs, RegistryValueKind.DWord);
        key.SetValue("AppStopUseTerminate", StopUseTerminate ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("AppKillProcessTree", KillProcessTree ? 1 : 0, RegistryValueKind.DWord);
    }

    public static ServiceConfig? Load(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesKey}\{serviceName}{ParametersKeySuffix}", false);
        if (key is null) return null;

        var c = new ServiceConfig { ServiceName = serviceName };

        c.Application = Str(key, "Application");
        c.AppDirectory = Str(key, "AppDirectory");
        c.AppParameters = Str(key, "AppParameters");
        c.Priority = (ProcessPriority)Num(key, "AppPriority", (int)ProcessPriority.Normal);
        c.AffinityMask = (ulong)Num64(key, "AppAffinity", 0);
        c.StartupDelayMs = Num(key, "AppStartupDelay", 0);

        c.Environment = (key.GetValue("AppEnvironmentExtra") as string[] ?? Array.Empty<string>()).ToList();
        c.ReplaceEnvironment = Num(key, "AppEnvironmentReplace", 0) != 0;

        c.StdoutPath = Str(key, "AppStdout");
        c.StderrPath = Str(key, "AppStderr");
        c.AppendOutput = Num(key, "AppAppendOutput", 1) != 0;
        c.TimestampLines = Num(key, "AppTimestampLog", 0) != 0;
        c.RotateFiles = Num(key, "AppRotateFiles", 1) != 0;
        c.RotateBytes = Num64(key, "AppRotateBytes", 10L * 1024 * 1024);
        c.RotateSeconds = Num(key, "AppRotateSeconds", 0);
        c.RotateKeep = Num(key, "AppRotateKeep", 10);
        c.LogServiceEvents = Num(key, "AppLogServiceEvents", 1) != 0;

        c.DefaultExitAction = (ExitAction)Num(key, "AppExitDefault", (int)ExitAction.Restart);
        c.RestartDelayMs = Num(key, "AppRestartDelay", 1000);
        c.ThrottleMs = Num(key, "AppThrottle", 5000);

        using (var exit = key.OpenSubKey("AppExit", false))
        {
            if (exit is not null)
                foreach (var name in exit.GetValueNames())
                    if (uint.TryParse(name, out var code))
                        c.ExitActions[code] = (ExitAction)Convert.ToInt32(exit.GetValue(name, 0));
        }

        c.MonitoringEnabled = Num(key, "MonEnabled", 1) != 0;
        c.WarnCpuPercent = Num(key, "MonWarnCpu", 0);
        c.CritCpuPercent = Num(key, "MonCritCpu", 0);
        c.WarnMemoryMb = Num(key, "MonWarnMemoryMb", 0);
        c.CritMemoryMb = Num(key, "MonCritMemoryMb", 0);
        c.WarnRestartsPerHour = Num(key, "MonWarnRestartsPerHour", 3);
        c.CritRestartsPerHour = Num(key, "MonCritRestartsPerHour", 10);

        c.StopUseConsole = Num(key, "AppStopUseConsole", 1) != 0;
        c.StopConsoleMs = Num(key, "AppStopConsoleDelay", 1500);
        c.StopUseWindow = Num(key, "AppStopUseWindow", 1) != 0;
        c.StopWindowMs = Num(key, "AppStopWindowDelay", 1500);
        c.StopUseThreads = Num(key, "AppStopUseThreads", 1) != 0;
        c.StopThreadsMs = Num(key, "AppStopThreadsDelay", 1500);
        c.StopUseTerminate = Num(key, "AppStopUseTerminate", 1) != 0;
        c.KillProcessTree = Num(key, "AppKillProcessTree", 1) != 0;

        return c;
    }

    private static string Str(RegistryKey key, string name) =>
        key.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";

    private static int Num(RegistryKey key, string name, int fallback)
    {
        try { return Convert.ToInt32(key.GetValue(name, fallback)); }
        catch { return fallback; }
    }

    private static long Num64(RegistryKey key, string name, long fallback)
    {
        try { return Convert.ToInt64(key.GetValue(name, fallback)); }
        catch { return fallback; }
    }

    public ServiceConfig Clone()
    {
        var c = (ServiceConfig)MemberwiseClone();
        c.Dependencies = new List<string>(Dependencies);
        c.Environment = new List<string>(Environment);
        c.ExitActions = new Dictionary<uint, ExitAction>(ExitActions);
        return c;
    }
}
