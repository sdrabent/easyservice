using System.Globalization;

using EasyService.Resources;

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
                S.Mon_NoService, Array.Empty<Metric>(), null, null);

        if (config is null)
            return new CheckResult(serviceName, info.DisplayName, CheckStatus.Unknown,
                S.Mon_NoConfig, Array.Empty<Metric>(), null, info);

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
                return (CheckStatus.Ok, S.Mon_Disabled);

            if (info.Startup is StartupType.Automatic or StartupType.AutomaticDelayed)
                return (CheckStatus.Critical,
                    S.Mon_StoppedButAuto(info.StateText.ToLowerInvariant(), info.StartupText));

            return (CheckStatus.Ok, S.Mon_StoppedOk(info.StartupText));
        }

        // --- the supervisor's own report ---------------------------------------
        if (state is null)
            return (CheckStatus.Unknown, S.Mon_NoState);

        if (state.IsStale)
            return (CheckStatus.Unknown, S.Mon_Stale(ServiceState.FormatDuration(state.Age)));

        switch (state.State)
        {
            case SupervisorState.Failed:
                return (CheckStatus.Critical, S.Mon_Failed(state.LastError ?? S.Mon_UnknownError));

            case SupervisorState.Throttled:
                return (CheckStatus.Critical, S.Mon_Throttled(state.RestartsLastHour,
                    state.LastExitCode?.ToString() ?? S.Common_UnknownShort));

            case SupervisorState.Ignored:
                return (CheckStatus.Warning,
                    S.Mon_Ignored(state.LastExitCode?.ToString() ?? S.Common_UnknownShort));

            case SupervisorState.Restarting:
                return (CheckStatus.Warning, S.Mon_Restarting);

            case SupervisorState.Starting:
                return (CheckStatus.Ok, S.Mon_Starting);

            case SupervisorState.Stopped:
                return (CheckStatus.Warning, S.Mon_RunningNoApp);
        }

        // --- thresholds on a healthy, running application -----------------------
        var status = CheckStatus.Ok;
        var problems = new List<string>();

        void Raise(CheckStatus s, string text)
        {
            if (s > status) status = s;
            problems.Add(text);
        }

        // Der Health-Check steht vor den Schwellwerten: eine Anwendung, die nicht mehr
        // antwortet, ist kein CPU-Problem, und die Meldung soll das zuerst sagen.
        if (state.Health == HealthStatus.Unhealthy)
            Raise(CheckStatus.Critical, S.Mon_Unhealthy(state.HealthDetail ?? S.Common_UnknownShort));

        var restarts = state.RestartsLastHour;
        if (config.CritRestartsPerHour > 0 && restarts >= config.CritRestartsPerHour)
            Raise(CheckStatus.Critical, S.Mon_RestartsCrit(restarts, config.CritRestartsPerHour));
        else if (config.WarnRestartsPerHour > 0 && restarts >= config.WarnRestartsPerHour)
            Raise(CheckStatus.Warning, S.Mon_RestartsWarn(restarts, config.WarnRestartsPerHour));

        if (config.CritCpuPercent > 0 && state.CpuPercent >= config.CritCpuPercent)
            Raise(CheckStatus.Critical, S.Mon_CpuCrit(Num(state.CpuPercent), config.CritCpuPercent));
        else if (config.WarnCpuPercent > 0 && state.CpuPercent >= config.WarnCpuPercent)
            Raise(CheckStatus.Warning, S.Mon_CpuWarn(Num(state.CpuPercent), config.WarnCpuPercent));

        var memoryMb = state.WorkingSetBytes / (1024.0 * 1024.0);
        if (config.CritMemoryMb > 0 && memoryMb >= config.CritMemoryMb)
            Raise(CheckStatus.Critical, S.Mon_MemCrit(Num(memoryMb), config.CritMemoryMb));
        else if (config.WarnMemoryMb > 0 && memoryMb >= config.WarnMemoryMb)
            Raise(CheckStatus.Warning, S.Mon_MemWarn(Num(memoryMb), config.WarnMemoryMb));

        var baseline = Describe(state);
        return problems.Count == 0
            ? (status, baseline)
            : (status, string.Join(", ", problems) + " - " + baseline);
    }

    private static string Describe(ServiceState state)
    {
        var parts = new List<string>();

        parts.Add(state.Uptime is { } up
            ? S.Mon_RunningSince(ServiceState.FormatDuration(up))
            : S.Mon_RunningPlain);

        if (state.ApplicationPid > 0) parts.Add(S.Mon_Pid(state.ApplicationPid));
        if (state.ProcessCount > 1) parts.Add(S.Mon_Processes(state.ProcessCount));
        parts.Add(S.Mon_Cpu(Num(state.CpuPercent)));
        parts.Add(S.Mon_Ram(ServiceState.FormatBytes(state.WorkingSetBytes)));
        parts.Add(S.Mon_RestartsPerHour(state.RestartsLastHour));
        if (state.RestartCount > 0) parts.Add(S.Mon_RestartsTotal(state.RestartCount));

        // Ein bestandener Health-Check gehoert in die Zeile: sonst sieht ein Admin nicht,
        // ob die Pruefung ueberhaupt laeuft.
        if (state.Health == HealthStatus.Healthy) parts.Add(S.Mon_Healthy);

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

        // Nur bei einem echten Urteil. Waehrend der Anlaufzeit gibt es keinen Messwert, und
        // eine erfundene 1 waere schlimmer als eine Luecke.
        if (state.Health is HealthStatus.Healthy or HealthStatus.Unhealthy)
        {
            metrics.Add(new Metric("health", state.Health == HealthStatus.Healthy ? 1 : 0, Min: 0, Max: 1));
            metrics.Add(new Metric("health_restarts", state.HealthRestarts, Min: 0));
        }
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
        CheckStatus.Ok => S.Mon_Status_Ok,
        CheckStatus.Warning => S.Mon_Status_Warning,
        CheckStatus.Critical => S.Mon_Status_Critical,
        _ => S.Mon_Status_Unknown,
    };
}
