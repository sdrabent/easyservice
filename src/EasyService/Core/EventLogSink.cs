using System.Diagnostics;

using EasyService.Resources;

namespace EasyService.Core;

/// <summary>
/// Stable event IDs for the Windows Application log.
///
/// These are part of the public contract: an administrator builds Checkmk/Zabbix/Icinga
/// alerts on the ID, not on the German message text. Never renumber an existing entry -
/// only append.
/// </summary>
public enum EasyServiceEvent
{
    SupervisorStarted = 1000,
    ApplicationStarted = 1001,
    ApplicationExited = 1002,
    ApplicationRestarting = 1003,
    RestartThrottled = 1004,
    ApplicationStartFailed = 1005,
    ServiceStopping = 1006,
    StoppedByExitPolicy = 1007,
    ApplicationTerminated = 1008,
    ConfigurationProblem = 1009,
    LoggingProblem = 1010,
    HealthCheckFailed = 1011,
    HealthCheckRecovered = 1012,
    HealthCheckRestarted = 1013,
    ScheduledRestart = 1014,
}

/// <summary>
/// Mirrors supervisor messages into the Windows Application event log so they show up in
/// eventvwr.msc and can be collected by any monitoring agent.
/// </summary>
public static class EventLogSink
{
    public const string SourceName = "EasyService";
    private static bool _sourceChecked;
    private static bool _sourceUsable;

    public static void EnsureSource()
    {
        if (_sourceChecked) return;
        _sourceChecked = true;
        try
        {
            if (!EventLog.SourceExists(SourceName))
                EventLog.CreateEventSource(new EventSourceCreationData(SourceName, "Application"));
            _sourceUsable = true;
        }
        catch
        {
            _sourceUsable = false;   // no rights, or the source belongs to another log
        }
    }

    public static void Write(string serviceName, EasyServiceEvent id, string message, EventLogEntryType type)
    {
        EnsureSource();
        if (!_sourceUsable) return;
        try
        {
            var text = string.IsNullOrEmpty(serviceName) ? message : $"[{serviceName}] {message}";
            if (text.Length > 31000) text = text[..31000];
            EventLog.WriteEntry(SourceName, text, type, (int)id);
        }
        catch
        {
            // Logging must never take the service down.
        }
    }

    public static void Info(string serviceName, EasyServiceEvent id, string message) =>
        Write(serviceName, id, message, EventLogEntryType.Information);

    public static void Warn(string serviceName, EasyServiceEvent id, string message) =>
        Write(serviceName, id, message, EventLogEntryType.Warning);

    public static void Error(string serviceName, EasyServiceEvent id, string message) =>
        Write(serviceName, id, message, EventLogEntryType.Error);

    public sealed record Entry(DateTime Time, string Type, int EventId, string Message);

    /// <summary>Reads recent Application-log entries written by EasyService.</summary>
    public static List<Entry> ReadRecent(string? serviceName, int max = 300)
    {
        var result = new List<Entry>();
        try
        {
            using var log = new EventLog("Application");
            for (var i = log.Entries.Count - 1; i >= 0 && result.Count < max; i--)
            {
                EventLogEntry entry;
                try { entry = log.Entries[i]; } catch { continue; }
                if (!string.Equals(entry.Source, SourceName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(serviceName) &&
                    !entry.Message.StartsWith($"[{serviceName}]", StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add(new Entry(entry.TimeGenerated, entry.EntryType.ToString(),
                    (int)(entry.InstanceId & 0xFFFF), entry.Message));
            }
        }
        catch
        {
            // Event log unreadable (policy, corrupt log): the file-based log is still there.
        }
        return result;
    }

    public static string Describe(int eventId) => eventId switch
    {
        (int)EasyServiceEvent.SupervisorStarted => S.Evt_SupervisorStarted,
        (int)EasyServiceEvent.ApplicationStarted => S.Evt_ApplicationStarted,
        (int)EasyServiceEvent.ApplicationExited => S.Evt_ApplicationExited,
        (int)EasyServiceEvent.ApplicationRestarting => S.Evt_ApplicationRestarting,
        (int)EasyServiceEvent.RestartThrottled => S.Evt_RestartThrottled,
        (int)EasyServiceEvent.ApplicationStartFailed => S.Evt_ApplicationStartFailed,
        (int)EasyServiceEvent.ServiceStopping => S.Evt_ServiceStopping,
        (int)EasyServiceEvent.StoppedByExitPolicy => S.Evt_StoppedByExitPolicy,
        (int)EasyServiceEvent.ApplicationTerminated => S.Evt_ApplicationTerminated,
        (int)EasyServiceEvent.HealthCheckFailed => S.Evt_HealthCheckFailed,
        (int)EasyServiceEvent.HealthCheckRecovered => S.Evt_HealthCheckRecovered,
        (int)EasyServiceEvent.HealthCheckRestarted => S.Evt_HealthCheckRestarted,
        (int)EasyServiceEvent.ScheduledRestart => S.Evt_ScheduledRestart,
        (int)EasyServiceEvent.ConfigurationProblem => S.Evt_ConfigurationProblem,
        (int)EasyServiceEvent.LoggingProblem => S.Evt_LoggingProblem,
        _ => "",
    };
}
