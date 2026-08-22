using System.Text.Json;
using System.Text.Json.Serialization;

using EasyService.Resources;

namespace EasyService.Core;

/// <summary>What the health check says about the application.</summary>
public enum HealthStatus
{
    /// <summary>No check configured. Windows-level "the process exists" is all there is.</summary>
    Unconfigured,

    /// <summary>Configured, but no verdict yet - the application is still inside its grace period.</summary>
    Pending,

    /// <summary>The application answered.</summary>
    Healthy,

    /// <summary>It failed often enough in a row to count as broken.</summary>
    Unhealthy,
}

/// <summary>What the supervisor is currently doing with the application.</summary>
public enum SupervisorState
{
    /// <summary>The service is not running at all.</summary>
    Stopped,

    /// <summary>Supervisor is up, application not launched yet.</summary>
    Starting,

    /// <summary>Application is running.</summary>
    Running,

    /// <summary>Application exited, waiting out the restart delay.</summary>
    Restarting,

    /// <summary>Application keeps dying faster than the throttle window; back-off is growing.</summary>
    Throttled,

    /// <summary>Application exited and the exit policy says not to restart it.</summary>
    Ignored,

    /// <summary>Application could not be started at all.</summary>
    Failed,
}

/// <summary>
/// Runtime facts about one supervised service, written by the supervisor and read by
/// the monitoring commands and the GUI.
///
/// This is the piece plain Windows cannot give an administrator: the SCM happily reports
/// RUNNING while the wrapped application has crashed and restarted four hundred times
/// today. Restart counts, uptime and resource usage live here.
///
/// Written atomically (temp file + move) so a reader never sees a half-written file, and
/// stored under ProgramData so a monitoring agent can pick it up without touching the registry.
/// </summary>
public sealed class ServiceState
{
    public int Version { get; set; } = 1;
    public string ServiceName { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SupervisorState State { get; set; } = SupervisorState.Stopped;

    public int SupervisorPid { get; set; }
    public int ApplicationPid { get; set; }

    public DateTime SupervisorStartedUtc { get; set; }
    public DateTime? ApplicationStartedUtc { get; set; }

    /// <summary>Restarts since the service itself was started.</summary>
    public int RestartCount { get; set; }

    /// <summary>Restart timestamps of the last 24 hours, for flapping detection.</summary>
    public List<DateTime> RecentRestartsUtc { get; set; } = new();

    public uint? LastExitCode { get; set; }
    public DateTime? LastExitUtc { get; set; }
    public string? LastError { get; set; }

    /// <summary>CPU load of the whole process tree in percent of one machine (100 % = all cores busy).</summary>
    public double CpuPercent { get; set; }

    public long WorkingSetBytes { get; set; }
    public int ProcessCount { get; set; }
    public double CpuSecondsTotal { get; set; }

    // --- health check --------------------------------------------------------

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HealthStatus Health { get; set; } = HealthStatus.Unconfigured;

    public DateTime? HealthCheckedUtc { get; set; }

    /// <summary>Result of the last probe in words - "HTTP 503", "No answer within 5000 ms".</summary>
    public string? HealthDetail { get; set; }

    /// <summary>Failures since the last success. One blip is not an outage.</summary>
    public int HealthFailuresInARow { get; set; }

    /// <summary>How often a failed check restarted the application.</summary>
    public int HealthRestarts { get; set; }

    public DateTime UpdatedUtc { get; set; }

    // --------------------------------------------------------------- derived ---

    [JsonIgnore]
    public TimeSpan? Uptime =>
        ApplicationStartedUtc is { } started && State == SupervisorState.Running
            ? DateTime.UtcNow - started
            : null;

    [JsonIgnore]
    public TimeSpan Age => DateTime.UtcNow - UpdatedUtc;

    /// <summary>
    /// A state file that stopped being updated means the supervisor died without cleaning up.
    /// Callers treat that as "no trustworthy measurements" rather than as good news.
    /// </summary>
    [JsonIgnore]
    public bool IsStale => Age > TimeSpan.FromMinutes(2);

    public int RestartsWithin(TimeSpan window)
    {
        var cutoff = DateTime.UtcNow - window;
        return RecentRestartsUtc.Count(t => t >= cutoff);
    }

    [JsonIgnore]
    public int RestartsLastHour => RestartsWithin(TimeSpan.FromHours(1));

    [JsonIgnore]
    public int RestartsLastDay => RestartsWithin(TimeSpan.FromDays(1));

    // ----------------------------------------------------------- persistence ---

    public static string DirectoryPath => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData),
        "EasyService", "state");

    public static string PathFor(string serviceName) =>
        Path.Combine(DirectoryPath, SanitizeFileName(serviceName) + ".json");

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly object WriteLock = new();

    public void Save()
    {
        UpdatedUtc = DateTime.UtcNow;
        TrimHistory();

        var path = PathFor(ServiceName);
        var temp = path + ".tmp";
        try
        {
            lock (WriteLock)
            {
                Directory.CreateDirectory(DirectoryPath);
                File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
                File.Move(temp, path, overwrite: true);
            }
        }
        catch (Exception)
        {
            // Monitoring bookkeeping must never take the supervised application down.
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private void TrimHistory()
    {
        if (RecentRestartsUtc.Count == 0) return;
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
        RecentRestartsUtc.RemoveAll(t => t < cutoff);

        // Hard cap so a service in a tight restart loop cannot grow the file without bound.
        const int max = 2000;
        if (RecentRestartsUtc.Count > max)
            RecentRestartsUtc.RemoveRange(0, RecentRestartsUtc.Count - max);
    }

    public static ServiceState? Load(string serviceName)
    {
        var path = PathFor(serviceName);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<ServiceState>(stream, Options);
            }
            catch (IOException)
            {
                Thread.Sleep(50);          // writer is mid-replace, retry
            }
            catch (JsonException)
            {
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }
        return null;
    }

    public static void Delete(string serviceName)
    {
        try { File.Delete(PathFor(serviceName)); } catch { }
    }

    public static string Describe(SupervisorState state) => state switch
    {
        SupervisorState.Stopped => S.State_Stopped,
        SupervisorState.Starting => S.State_Starting,
        SupervisorState.Running => S.State_Running,
        SupervisorState.Restarting => S.State_Restarting,
        SupervisorState.Throttled => S.State_Throttled,
        SupervisorState.Ignored => S.State_Ignored,
        SupervisorState.Failed => S.State_Failed,
        _ => state.ToString(),
    };

    public static string FormatDuration(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{(int)t.TotalSeconds}s";
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "-";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value >= 100 ? $"{value:F0} {units[unit]}" : $"{value:F1} {units[unit]}";
    }
}
