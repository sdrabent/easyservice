using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EasyService.Resources;

namespace EasyService.Core;

/// <summary>
/// Reads and writes a complete service definition as JSON, so a configuration can be
/// version-controlled, reviewed and rolled out to many machines.
///
/// Two deliberate choices:
///
/// The password is never written out. A file that ends up in Git must not carry a
/// service account credential, and there is no way to export one "just for internal
/// use" - files travel. On import the password comes from the environment instead.
///
/// Enums are written as text, not as their numeric value. The point of this format is
/// that an administrator can diff a machine against a golden file and read the result;
/// "startup": "AutomaticDelayed" says something, "startup": 1 does not.
/// </summary>
public static class ConfigTransfer
{
    /// <summary>Bumped only when the format changes in a way older versions cannot read.</summary>
    public const int FormatVersion = 1;

    /// <summary>Environment variable carrying the service account password on import.</summary>
    public const string PasswordVariable = "EASYSERVICE_PASSWORD";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // camelCase, weil die Datei von Menschen gelesen und in Git angesehen wird.
        // Die Woerterbuchschluessel der Exit-Codes bleiben unangetastet - "0" ist "0".
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// The wire format. A separate type from ServiceConfig on purpose: it fixes the field
    /// order, keeps the password out by construction rather than by remembering to skip it,
    /// and lets ServiceConfig change without breaking files people have already written.
    /// </summary>
    private sealed class Dto
    {
        public int Easyservice { get; set; } = FormatVersion;
        public string ServiceName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Description { get; set; } = "";
        public StartupType Startup { get; set; }

        public string Application { get; set; } = "";
        public string AppDirectory { get; set; } = "";
        public string AppParameters { get; set; } = "";
        public ProcessPriority Priority { get; set; }
        public ulong AffinityMask { get; set; }
        public int StartupDelayMs { get; set; }

        public LogonType Logon { get; set; }
        public string AccountName { get; set; } = "";
        public bool InteractWithDesktop { get; set; }

        public List<string> Dependencies { get; set; } = new();
        public List<string> Environment { get; set; } = new();
        public bool ReplaceEnvironment { get; set; }

        public string StdoutPath { get; set; } = "";
        public string StderrPath { get; set; } = "";
        public bool AppendOutput { get; set; }
        public bool TimestampLines { get; set; }
        public bool RotateFiles { get; set; }
        public long RotateBytes { get; set; }
        public int RotateSeconds { get; set; }
        public int RotateKeep { get; set; }
        public bool LogServiceEvents { get; set; }

        public ExitAction DefaultExitAction { get; set; }
        public Dictionary<string, ExitAction> ExitActions { get; set; } = new();
        public int RestartDelayMs { get; set; }
        public int ThrottleMs { get; set; }

        public RestartScheduleMode RestartScheduleMode { get; set; }
        public int RestartAtMinutes { get; set; } = 180;
        public int RestartDays { get; set; } = RestartSchedule.AllDays;
        public int RestartEveryMinutes { get; set; } = 24 * 60;

        public bool MonitoringEnabled { get; set; }
        public int WarnCpuPercent { get; set; }
        public int CritCpuPercent { get; set; }
        public int WarnMemoryMb { get; set; }
        public int CritMemoryMb { get; set; }
        public int WarnRestartsPerHour { get; set; }
        public int CritRestartsPerHour { get; set; }
        public int HistoryDays { get; set; }

        // Mit denselben Vorgaben wie ServiceConfig: eine Datei aus einer aelteren Fassung
        // kennt diese Felder nicht, und eine 0 als Abstand zwischen zwei Pruefungen waere
        // keine Vorgabe, sondern ein Fehler.
        public HealthCheckType HealthType { get; set; }
        public string HealthTarget { get; set; } = "";
        public int HealthIntervalMs { get; set; } = 30_000;
        public int HealthTimeoutMs { get; set; } = 5_000;
        public int HealthGraceMs { get; set; } = 30_000;
        public int HealthFailures { get; set; } = 3;
        public HealthAction HealthAction { get; set; }
        public int HealthExpectStatus { get; set; }
        public int HealthMaxAgeSec { get; set; } = 120;

        public bool StopUseConsole { get; set; }
        public int StopConsoleMs { get; set; }
        public bool StopUseWindow { get; set; }
        public int StopWindowMs { get; set; }
        public bool StopUseThreads { get; set; }
        public int StopThreadsMs { get; set; }
        public bool StopUseTerminate { get; set; }
        public bool KillProcessTree { get; set; }
    }

    // ---------------------------------------------------------------- export ---

    public static string Export(ServiceConfig c) => JsonSerializer.Serialize(ToDto(c), Options);

    public static string ExportMany(IEnumerable<ServiceConfig> configs) =>
        JsonSerializer.Serialize(configs.Select(ToDto).ToList(), Options);

    private static Dto ToDto(ServiceConfig c) => new()
    {
        ServiceName = c.ServiceName,
        DisplayName = c.EffectiveDisplayName,
        Description = c.Description,
        Startup = c.Startup,
        Application = c.Application,
        AppDirectory = c.AppDirectory,
        AppParameters = c.AppParameters,
        Priority = c.Priority,
        AffinityMask = c.AffinityMask,
        StartupDelayMs = c.StartupDelayMs,
        Logon = c.Logon,
        AccountName = c.AccountName,
        InteractWithDesktop = c.InteractWithDesktop,
        Dependencies = new List<string>(c.Dependencies),
        Environment = new List<string>(c.Environment),
        ReplaceEnvironment = c.ReplaceEnvironment,
        StdoutPath = c.StdoutPath,
        StderrPath = c.StderrPath,
        AppendOutput = c.AppendOutput,
        TimestampLines = c.TimestampLines,
        RotateFiles = c.RotateFiles,
        RotateBytes = c.RotateBytes,
        RotateSeconds = c.RotateSeconds,
        RotateKeep = c.RotateKeep,
        LogServiceEvents = c.LogServiceEvents,
        DefaultExitAction = c.DefaultExitAction,
        // Keys sorted numerically so two exports of the same service are byte-identical.
        ExitActions = c.ExitActions.OrderBy(kv => kv.Key)
                                   .ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
        RestartDelayMs = c.RestartDelayMs,
        ThrottleMs = c.ThrottleMs,
        RestartScheduleMode = c.RestartScheduleMode,
        RestartAtMinutes = c.RestartAtMinutes,
        RestartDays = c.RestartDays,
        RestartEveryMinutes = c.RestartEveryMinutes,
        MonitoringEnabled = c.MonitoringEnabled,
        WarnCpuPercent = c.WarnCpuPercent,
        CritCpuPercent = c.CritCpuPercent,
        WarnMemoryMb = c.WarnMemoryMb,
        CritMemoryMb = c.CritMemoryMb,
        WarnRestartsPerHour = c.WarnRestartsPerHour,
        CritRestartsPerHour = c.CritRestartsPerHour,
        HistoryDays = c.HistoryDays,
        HealthType = c.HealthType,
        HealthTarget = c.HealthTarget,
        HealthIntervalMs = c.HealthIntervalMs,
        HealthTimeoutMs = c.HealthTimeoutMs,
        HealthGraceMs = c.HealthGraceMs,
        HealthFailures = c.HealthFailures,
        HealthAction = c.HealthAction,
        HealthExpectStatus = c.HealthExpectStatus,
        HealthMaxAgeSec = c.HealthMaxAgeSec,
        StopUseConsole = c.StopUseConsole,
        StopConsoleMs = c.StopConsoleMs,
        StopUseWindow = c.StopUseWindow,
        StopWindowMs = c.StopWindowMs,
        StopUseThreads = c.StopUseThreads,
        StopThreadsMs = c.StopThreadsMs,
        StopUseTerminate = c.StopUseTerminate,
        KillProcessTree = c.KillProcessTree,
    };

    // ---------------------------------------------------------------- import ---

    /// <summary>Thrown for a file that is not a usable EasyService configuration.</summary>
    public sealed class TransferException : Exception
    {
        public TransferException(string message) : base(message) { }
    }

    /// <summary>
    /// Parses one or many definitions. Accepts both a single object and an array, so a file
    /// produced by "export --all" imports the same way a single one does.
    /// </summary>
    public static List<ServiceConfig> Import(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException e)
        {
            throw new TransferException(S.Cfg_Err_NotJson(e.Message));
        }

        var items = root switch
        {
            JsonArray array => array.ToList(),
            JsonObject obj => new List<JsonNode?> { obj },
            _ => throw new TransferException(S.Cfg_Err_NotConfig),
        };

        var result = new List<ServiceConfig>();
        foreach (var item in items)
        {
            if (item is not JsonObject) throw new TransferException(S.Cfg_Err_NotConfig);

            var version = item["easyservice"]?.GetValue<int>();
            if (version is null) throw new TransferException(S.Cfg_Err_NotConfig);
            if (version > FormatVersion) throw new TransferException(S.Cfg_Err_NewerFormat(version.Value));

            Dto? dto;
            try
            {
                dto = item.Deserialize<Dto>(Options);
            }
            catch (JsonException e)
            {
                throw new TransferException(S.Cfg_Err_NotJson(e.Message));
            }

            if (dto is null || string.IsNullOrWhiteSpace(dto.ServiceName))
                throw new TransferException(S.Cfg_Err_NoServiceName);

            result.Add(FromDto(dto));
        }

        if (result.Count == 0) throw new TransferException(S.Cfg_Err_NotConfig);
        return result;
    }

    private static ServiceConfig FromDto(Dto d)
    {
        var c = new ServiceConfig
        {
            ServiceName = d.ServiceName.Trim(),
            DisplayName = d.DisplayName,
            Description = d.Description,
            Startup = d.Startup,
            Application = d.Application,
            AppDirectory = d.AppDirectory,
            AppParameters = d.AppParameters,
            Priority = d.Priority,
            AffinityMask = d.AffinityMask,
            StartupDelayMs = d.StartupDelayMs,
            Logon = d.Logon,
            AccountName = d.AccountName,
            InteractWithDesktop = d.InteractWithDesktop,
            Dependencies = new List<string>(d.Dependencies),
            Environment = new List<string>(d.Environment),
            ReplaceEnvironment = d.ReplaceEnvironment,
            StdoutPath = d.StdoutPath,
            StderrPath = d.StderrPath,
            AppendOutput = d.AppendOutput,
            TimestampLines = d.TimestampLines,
            RotateFiles = d.RotateFiles,
            RotateBytes = d.RotateBytes,
            RotateSeconds = d.RotateSeconds,
            RotateKeep = d.RotateKeep,
            LogServiceEvents = d.LogServiceEvents,
            DefaultExitAction = d.DefaultExitAction,
            RestartDelayMs = d.RestartDelayMs,
            ThrottleMs = d.ThrottleMs,
            RestartScheduleMode = d.RestartScheduleMode,
            RestartAtMinutes = d.RestartAtMinutes,
            RestartDays = d.RestartDays,
            RestartEveryMinutes = d.RestartEveryMinutes,
            MonitoringEnabled = d.MonitoringEnabled,
            WarnCpuPercent = d.WarnCpuPercent,
            CritCpuPercent = d.CritCpuPercent,
            WarnMemoryMb = d.WarnMemoryMb,
            CritMemoryMb = d.CritMemoryMb,
            WarnRestartsPerHour = d.WarnRestartsPerHour,
            CritRestartsPerHour = d.CritRestartsPerHour,
            HistoryDays = d.HistoryDays,
            HealthType = d.HealthType,
            HealthTarget = d.HealthTarget,
            HealthIntervalMs = d.HealthIntervalMs,
            HealthTimeoutMs = d.HealthTimeoutMs,
            HealthGraceMs = d.HealthGraceMs,
            HealthFailures = d.HealthFailures,
            HealthAction = d.HealthAction,
            HealthExpectStatus = d.HealthExpectStatus,
            HealthMaxAgeSec = d.HealthMaxAgeSec,
            StopUseConsole = d.StopUseConsole,
            StopConsoleMs = d.StopConsoleMs,
            StopUseWindow = d.StopUseWindow,
            StopWindowMs = d.StopWindowMs,
            StopUseThreads = d.StopUseThreads,
            StopThreadsMs = d.StopThreadsMs,
            StopUseTerminate = d.StopUseTerminate,
            KillProcessTree = d.KillProcessTree,
        };

        foreach (var (key, action) in d.ExitActions)
            if (uint.TryParse(key, out var code))
                c.ExitActions[code] = action;

        // A file written before the log paths were filled in still has to produce a
        // working service rather than one that silently logs nowhere.
        c.ApplyDefaultLogPaths();
        return c;
    }

    /// <summary>
    /// Reads the service account password from the environment. Deliberately not a command
    /// line argument: those are visible in the process list to every user on the machine.
    /// </summary>
    public static string? PasswordFromEnvironment()
    {
        var value = System.Environment.GetEnvironmentVariable(PasswordVariable);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
