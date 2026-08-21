using System.Globalization;

namespace EasyService.Core;

/// <summary>Nagios/Checkmk status convention - the numbers are part of the contract.</summary>
public enum CheckStatus
{
    Ok = 0,
    Warning = 1,
    Critical = 2,
    Unknown = 3,
}

/// <summary>One performance value. Warn/crit/min/max are optional, as in Nagios perfdata.</summary>
public sealed record Metric(string Name, double Value, string Unit = "", double? Warn = null, double? Crit = null,
                            double? Min = null, double? Max = null)
{
    public string Format(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}

public sealed record CheckResult(
    string ServiceName,
    string DisplayName,
    CheckStatus Status,
    string Summary,
    IReadOnlyList<Metric> Metrics,
    ServiceState? State,
    ServiceInfo? Info);

/// <summary>
/// Turns the raw service state into the answer an administrator actually wants:
/// is this thing healthy, and if not, why.
///
/// The interesting judgement is the one Windows cannot make on its own. The SCM reports
/// RUNNING as long as our supervisor process lives, even while the wrapped application
/// crashes in a loop. Restart frequency is therefore treated as a first-class health
/// signal, not as a footnote.
/// </summary>
public static class Monitoring
{
    public static List<CheckResult> CheckAll() => CheckAll(ServiceRegistry.EnumerateServices());

    /// <summary>Overload for callers that already enumerated the services, so the SCM is only walked once.</summary>
    public static List<CheckResult> CheckAll(IEnumerable<ServiceInfo> services)
    {
        var results = new List<CheckResult>();
        foreach (var info in services.Where(s => s.ManagedByEasyService))
        {
            var result = Check(info.Name, info);
            if (result is not null) results.Add(result);
        }
        return results.OrderBy(r => r.ServiceName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static CheckResult? Check(string serviceName, ServiceInfo? info = null)
    {
        info ??= ServiceRegistry.Query(serviceName);
        var config = ServiceConfig.Load(serviceName);

        if (info is null)
            return new CheckResult(serviceName, serviceName, CheckStatus.Unknown,
                "Der Dienst existiert nicht (mehr).", Array.Empty<Metric>(), null, null);

        if (config is null)
            return new CheckResult(serviceName, info.DisplayName, CheckStatus.Unknown,
                "Für diesen Dienst ist keine EasyService-Konfiguration hinterlegt.",
                Array.Empty<Metric>(), null, info);

        if (!config.MonitoringEnabled) return null;

        var state = ServiceState.Load(serviceName);
        var metrics = BuildMetrics(config, state, info);
        var (status, summary) = Evaluate(config, state, info);

        return new CheckResult(serviceName, info.DisplayName, status, summary, metrics, state, info);
    }

    /// <summary>Public so the threshold logic can be exercised directly by tests.</summary>
    public static (CheckStatus Status, string Summary) Evaluate(ServiceConfig config, ServiceState? state, ServiceInfo info)
    {
        // --- the service itself -------------------------------------------------
        if (!info.IsRunning)
        {
            if (info.Startup == StartupType.Disabled)
                return (CheckStatus.Ok, "Der Dienst ist deaktiviert.");

            if (info.Startup is StartupType.Automatic or StartupType.AutomaticDelayed)
                return (CheckStatus.Critical,
                    $"Der Dienst ist {info.StateText.ToLowerInvariant()}, obwohl der Starttyp \"{info.StartupText}\" ist.");

            return (CheckStatus.Ok, $"Der Dienst ist beendet (Starttyp {info.StartupText}).");
        }

        // --- the supervisor's own report ---------------------------------------
        if (state is null)
            return (CheckStatus.Unknown,
                "Der Dienst läuft, meldet aber noch keinen Zustand. Läuft er mit einer älteren EasyService-Version?");

        if (state.IsStale)
            return (CheckStatus.Unknown,
                $"Die letzte Statusmeldung ist {ServiceState.FormatDuration(state.Age)} alt - " +
                "der überwachende Prozess reagiert möglicherweise nicht mehr.");

        switch (state.State)
        {
            case SupervisorState.Failed:
                return (CheckStatus.Critical,
                    "Die Anwendung konnte nicht gestartet werden: " + (state.LastError ?? "unbekannter Fehler"));

            case SupervisorState.Throttled:
                return (CheckStatus.Critical,
                    $"Die Anwendung startet ständig neu und wird gedrosselt. " +
                    $"{state.RestartsLastHour} Neustarts in der letzten Stunde, " +
                    $"zuletzt Exit-Code {state.LastExitCode?.ToString() ?? "?"}.");

            case SupervisorState.Ignored:
                return (CheckStatus.Warning,
                    $"Die Anwendung hat sich beendet (Exit-Code {state.LastExitCode?.ToString() ?? "?"}) und wird " +
                    "laut Konfiguration nicht neu gestartet. Der Dienst läuft ohne Anwendung.");

            case SupervisorState.Restarting:
                return (CheckStatus.Warning, "Die Anwendung wird gerade neu gestartet.");

            case SupervisorState.Starting:
                return (CheckStatus.Ok, "Der Dienst startet.");

            case SupervisorState.Stopped:
                return (CheckStatus.Warning, "Der Dienst läuft, die Anwendung ist aber nicht aktiv.");
        }

        // --- thresholds on a healthy, running application -----------------------
        var status = CheckStatus.Ok;
        var problems = new List<string>();

        void Raise(CheckStatus s, string text)
        {
            if (s > status) status = s;
            problems.Add(text);
        }

        var restarts = state.RestartsLastHour;
        if (config.CritRestartsPerHour > 0 && restarts >= config.CritRestartsPerHour)
            Raise(CheckStatus.Critical, $"{restarts} Neustarts in der letzten Stunde (kritisch ab {config.CritRestartsPerHour})");
        else if (config.WarnRestartsPerHour > 0 && restarts >= config.WarnRestartsPerHour)
            Raise(CheckStatus.Warning, $"{restarts} Neustarts in der letzten Stunde (Warnung ab {config.WarnRestartsPerHour})");

        if (config.CritCpuPercent > 0 && state.CpuPercent >= config.CritCpuPercent)
            Raise(CheckStatus.Critical, $"CPU {Num(state.CpuPercent)} % (kritisch ab {config.CritCpuPercent} %)");
        else if (config.WarnCpuPercent > 0 && state.CpuPercent >= config.WarnCpuPercent)
            Raise(CheckStatus.Warning, $"CPU {Num(state.CpuPercent)} % (Warnung ab {config.WarnCpuPercent} %)");

        var memoryMb = state.WorkingSetBytes / (1024.0 * 1024.0);
        if (config.CritMemoryMb > 0 && memoryMb >= config.CritMemoryMb)
            Raise(CheckStatus.Critical, $"RAM {Num(memoryMb)} MB (kritisch ab {config.CritMemoryMb} MB)");
        else if (config.WarnMemoryMb > 0 && memoryMb >= config.WarnMemoryMb)
            Raise(CheckStatus.Warning, $"RAM {Num(memoryMb)} MB (Warnung ab {config.WarnMemoryMb} MB)");

        var baseline = Describe(state);
        return problems.Count == 0
            ? (status, baseline)
            : (status, string.Join(", ", problems) + " - " + baseline);
    }

    private static string Describe(ServiceState state)
    {
        var parts = new List<string>();

        parts.Add(state.Uptime is { } up
            ? $"Läuft seit {ServiceState.FormatDuration(up)}"
            : "Läuft");

        if (state.ApplicationPid > 0) parts.Add($"PID {state.ApplicationPid}");
        if (state.ProcessCount > 1) parts.Add($"{state.ProcessCount} Prozesse");
        parts.Add($"CPU {Num(state.CpuPercent)} %");
        parts.Add($"RAM {ServiceState.FormatBytes(state.WorkingSetBytes)}");
        parts.Add($"{state.RestartsLastHour} Neustarts/h");
        if (state.RestartCount > 0) parts.Add($"{state.RestartCount} Neustarts gesamt");

        return string.Join(", ", parts);
    }

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static List<Metric> BuildMetrics(ServiceConfig config, ServiceState? state, ServiceInfo info)
    {
        var metrics = new List<Metric>
        {
            new("service_running", info.IsRunning ? 1 : 0, Min: 0, Max: 1),
        };

        if (state is null) return metrics;

        metrics.Add(new Metric("app_running", state.State == SupervisorState.Running ? 1 : 0, Min: 0, Max: 1));
        metrics.Add(new Metric("uptime", state.Uptime?.TotalSeconds ?? 0, "s", Min: 0));
        metrics.Add(new Metric("restarts_1h", state.RestartsLastHour, "",
            config.WarnRestartsPerHour > 0 ? config.WarnRestartsPerHour : null,
            config.CritRestartsPerHour > 0 ? config.CritRestartsPerHour : null, 0));
        metrics.Add(new Metric("restarts_24h", state.RestartsLastDay, "", Min: 0));
        metrics.Add(new Metric("restarts_total", state.RestartCount, "", Min: 0));
        metrics.Add(new Metric("cpu", state.CpuPercent, "%",
            config.WarnCpuPercent > 0 ? config.WarnCpuPercent : null,
            config.CritCpuPercent > 0 ? config.CritCpuPercent : null, 0, 100));
        metrics.Add(new Metric("mem", state.WorkingSetBytes, "B",
            config.WarnMemoryMb > 0 ? config.WarnMemoryMb * 1024L * 1024 : null,
            config.CritMemoryMb > 0 ? config.CritMemoryMb * 1024L * 1024 : null, 0));
        metrics.Add(new Metric("procs", state.ProcessCount, "", Min: 0));
        metrics.Add(new Metric("cpu_seconds", state.CpuSecondsTotal, "s", Min: 0));

        return metrics;
    }

    public static string Describe(CheckStatus status) => status switch
    {
        CheckStatus.Ok => "OK",
        CheckStatus.Warning => "WARNUNG",
        CheckStatus.Critical => "KRITISCH",
        _ => "UNBEKANNT",
    };
}
