using System.Diagnostics;

namespace EasyService.Core;

/// <summary>
/// Mirrors supervisor messages into the Windows Application event log so they show up in
/// eventvwr.msc next to everything else the machine reports.
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

    private static void Write(string serviceName, string message, EventLogEntryType type)
    {
        EnsureSource();
        if (!_sourceUsable) return;
        try
        {
            var text = string.IsNullOrEmpty(serviceName) ? message : $"[{serviceName}] {message}";
            if (text.Length > 31000) text = text[..31000];
            EventLog.WriteEntry(SourceName, text, type);
        }
        catch
        {
            // Logging must never take the service down.
        }
    }

    public static void Info(string serviceName, string message) => Write(serviceName, message, EventLogEntryType.Information);
    public static void Warn(string serviceName, string message) => Write(serviceName, message, EventLogEntryType.Warning);
    public static void Error(string serviceName, string message) => Write(serviceName, message, EventLogEntryType.Error);

    /// <summary>Reads recent Application-log entries written by EasyService.</summary>
    public static List<(DateTime Time, string Type, string Message)> ReadRecent(string? serviceName, int max = 300)
    {
        var result = new List<(DateTime, string, string)>();
        try
        {
            using var log = new EventLog("Application");
            for (var i = log.Entries.Count - 1; i >= 0 && result.Count < max; i--)
            {
                EventLogEntry entry;
                try { entry = log.Entries[i]; } catch { continue; }
                if (!string.Equals(entry.Source, SourceName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(serviceName) && !entry.Message.StartsWith($"[{serviceName}]", StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add((entry.TimeGenerated, entry.EntryType.ToString(), entry.Message));
            }
        }
        catch
        {
            // Event log unreadable (policy, corrupt log): the file-based log is still there.
        }
        return result;
    }
}
