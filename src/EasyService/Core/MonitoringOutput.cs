using System.Globalization;
using System.Text;
using System.Text.Json;

namespace EasyService.Core;

/// <summary>
/// Renders check results in the formats the common monitoring systems speak.
///
/// All numbers use the invariant culture on purpose: a German "2,5" would silently
/// break every perfdata parser out there.
/// </summary>
public static class MonitoringOutput
{
    private static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private static string OneLine(string text) =>
        text.ReplaceLineEndings(" ").Replace('\t', ' ').Trim();

    // ------------------------------------------------------------- Checkmk ---

    /// <summary>
    /// Checkmk local check: "status item perfdata text", one line per service.
    /// Drop the output of this command into the agent's local/ directory and every
    /// supervised application becomes a Checkmk service with graphs.
    /// </summary>
    public static string Checkmk(IEnumerable<CheckResult> results)
    {
        var sb = new StringBuilder();
        foreach (var r in results)
        {
            var item = "EasyService_" + SanitizeItem(r.ServiceName);
            var perf = CheckmkPerfdata(r.Metrics);
            sb.Append((int)r.Status).Append(' ')
              .Append(item).Append(' ')
              .Append(perf.Length == 0 ? "-" : perf).Append(' ')
              .Append(OneLine(r.Summary))
              .Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Checkmk splits the first three fields on whitespace, so the item must not contain any.</summary>
    private static string SanitizeItem(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(char.IsWhiteSpace(c) || c == '|' ? '_' : c);
        return sb.ToString();
    }

    private static string CheckmkPerfdata(IReadOnlyList<Metric> metrics)
    {
        if (metrics.Count == 0) return "";
        var parts = new List<string>(metrics.Count);

        foreach (var m in metrics)
        {
            var fields = new[]
            {
                Num(m.Value) + m.Unit,
                m.Warn is { } w ? Num(w) : "",
                m.Crit is { } c ? Num(c) : "",
                m.Min is { } lo ? Num(lo) : "",
                m.Max is { } hi ? Num(hi) : "",
            };

            var value = string.Join(';', fields).TrimEnd(';');
            parts.Add($"{m.Name}={value}");
        }

        return string.Join('|', parts);
    }

    // ---------------------------------------------------------- Nagios/Icinga ---

    /// <summary>Single-service plugin output. The caller uses the status as the process exit code.</summary>
    public static string Nagios(CheckResult r)
    {
        var perf = CheckmkPerfdata(r.Metrics).Replace('|', ' ');
        var line = $"EASYSERVICE {Monitoring.Describe(r.Status)} - {OneLine(r.Summary)}";
        return perf.Length == 0 ? line : $"{line} | {perf}";
    }

    // ---------------------------------------------------------- Prometheus ---

    private sealed record Family(string Name, string Type, string Help, Func<CheckResult, double?> Value);

    private static readonly Family[] Families =
    {
        new("easyservice_service_running", "gauge", "1 = der Windows-Dienst laeuft",
            r => r.Info?.IsRunning == true ? 1 : 0),
        new("easyservice_application_running", "gauge", "1 = die ueberwachte Anwendung laeuft",
            r => r.State?.State == SupervisorState.Running ? 1 : 0),
        new("easyservice_check_status", "gauge", "0 = OK, 1 = Warnung, 2 = kritisch, 3 = unbekannt",
            r => (double)(int)r.Status),
        new("easyservice_uptime_seconds", "gauge", "Laufzeit der Anwendung seit dem letzten Start",
            r => r.State?.Uptime?.TotalSeconds ?? 0),
        new("easyservice_restarts_total", "counter", "Neustarts seit dem Start des Dienstes",
            r => r.State?.RestartCount ?? 0),
        new("easyservice_restarts_1h", "gauge", "Neustarts in der letzten Stunde",
            r => r.State?.RestartsLastHour ?? 0),
        new("easyservice_restarts_24h", "gauge", "Neustarts in den letzten 24 Stunden",
            r => r.State?.RestartsLastDay ?? 0),
        new("easyservice_cpu_percent", "gauge", "CPU-Last des Prozessbaums (100 = alle Kerne ausgelastet)",
            r => r.State?.CpuPercent ?? 0),
        new("easyservice_cpu_seconds_total", "counter", "Verbrauchte CPU-Zeit des Prozessbaums",
            r => r.State?.CpuSecondsTotal ?? 0),
        new("easyservice_memory_bytes", "gauge", "Arbeitsspeicher des Prozessbaums",
            r => r.State?.WorkingSetBytes ?? 0),
        new("easyservice_processes", "gauge", "Anzahl Prozesse im Prozessbaum",
            r => r.State?.ProcessCount ?? 0),
        new("easyservice_last_exit_code", "gauge", "Exit-Code des letzten Anwendungslaufs",
            r => r.State?.LastExitCode ?? 0),
        new("easyservice_state_age_seconds", "gauge", "Alter der letzten Statusmeldung des Supervisors",
            r => r.State is null ? -1 : r.State.Age.TotalSeconds),
    };

    /// <summary>
    /// Prometheus text exposition. Samples of one metric family have to be grouped together
    /// with a single HELP/TYPE header, so this iterates metric-first rather than service-first.
    /// </summary>
    public static string Prometheus(IEnumerable<CheckResult> results)
    {
        var list = results.ToList();
        var sb = new StringBuilder();

        foreach (var family in Families)
        {
            sb.Append("# HELP ").Append(family.Name).Append(' ').Append(family.Help).Append('\n');
            sb.Append("# TYPE ").Append(family.Name).Append(' ').Append(family.Type).Append('\n');
            foreach (var r in list)
            {
                var value = family.Value(r);
                if (value is null) continue;
                sb.Append(family.Name)
                  .Append("{service=\"").Append(EscapeLabel(r.ServiceName)).Append("\"} ")
                  .Append(Num(value.Value))
                  .Append('\n');
            }
        }

        return sb.ToString();
    }

    private static string EscapeLabel(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    // ---------------------------------------------------------------- JSON ---

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Json(IEnumerable<CheckResult> results)
    {
        var payload = results.Select(r => new
        {
            service = r.ServiceName,
            displayName = r.DisplayName,
            status = (int)r.Status,
            statusText = Monitoring.Describe(r.Status),
            summary = r.Summary,
            serviceRunning = r.Info?.IsRunning ?? false,
            startupType = r.Info?.StartupText,
            applicationState = r.State?.State.ToString(),
            applicationPid = r.State?.ApplicationPid,
            uptimeSeconds = r.State?.Uptime?.TotalSeconds,
            restartsTotal = r.State?.RestartCount,
            restartsLastHour = r.State?.RestartsLastHour,
            restartsLastDay = r.State?.RestartsLastDay,
            cpuPercent = r.State?.CpuPercent,
            memoryBytes = r.State?.WorkingSetBytes,
            processes = r.State?.ProcessCount,
            lastExitCode = r.State?.LastExitCode,
            lastExitUtc = r.State?.LastExitUtc,
            lastError = r.State?.LastError,
            stateUpdatedUtc = r.State?.UpdatedUtc,
        }).ToList();

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    // -------------------------------------------------------------- Zabbix ---

    /// <summary>
    /// Low-level discovery payload for Zabbix: a JSON array of macros so one template
    /// can create items for every supervised service automatically.
    /// </summary>
    public static string ZabbixDiscovery(IEnumerable<CheckResult> results)
    {
        var payload = new
        {
            data = results.Select(r => new Dictionary<string, string>
            {
                ["{#SERVICE}"] = r.ServiceName,
                ["{#DISPLAYNAME}"] = r.DisplayName,
            }).ToList(),
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }
}
