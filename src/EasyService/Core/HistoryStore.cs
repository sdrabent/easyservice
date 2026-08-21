using System.Globalization;
using System.Text;

namespace EasyService.Core;

/// <summary>One minute of measurements, aggregated from the 5-second samples.</summary>
public sealed record MetricSample(
    DateTime Utc,
    double CpuAverage,
    double CpuPeak,
    long MemoryAverage,
    long MemoryPeak,
    int Processes,
    int RestartsTotal);

/// <summary>Something worth remembering that happened to a service.</summary>
public sealed record HistoryEvent(DateTime Utc, int EventId, uint? ExitCode, string Detail);

/// <summary>
/// The long-term memory of a service: what it cost and what happened to it.
///
/// Two CSV files per service, because CSV is the format an administrator can still
/// read in five years without EasyService installed - open it in Excel, grep it,
/// feed it to a script. No database, no binary format, no dependency.
///
/// Measurements are aggregated to one row per minute before they are written.
/// The raw 5-second samples would be 17280 rows per service per day; one row per
/// minute is 1440, which at ~56 bytes per row costs about 80 KB per service and day -
/// roughly 2.3 MB for the 30 days kept by default.
/// </summary>
public static class HistoryStore
{
    private const string MetricsHeader = "utc,cpu_avg,cpu_max,mem_avg,mem_max,procs,restarts_total";
    private const string EventsHeader = "utc,event_id,exit_code,detail";

    public static string DirectoryPath => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData),
        "EasyService", "history");

    public static string MetricsPathFor(string serviceName) => PathFor(serviceName, "metrics");

    public static string EventsPathFor(string serviceName) => PathFor(serviceName, "events");

    private static string PathFor(string serviceName, string kind)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(serviceName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return Path.Combine(DirectoryPath, $"{safe}-{kind}.csv");
    }

    private static readonly object WriteLock = new();

    private static string Stamp(DateTime utc) =>
        utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    // ----------------------------------------------------------------- write ---

    private static void Append(string path, string header, string line)
    {
        try
        {
            lock (WriteLock)
            {
                Directory.CreateDirectory(DirectoryPath);
                var isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                if (isNew) writer.WriteLine(header);
                writer.WriteLine(line);
            }
        }
        catch (Exception)
        {
            // History is a convenience. It must never take the supervised application down.
        }
    }

    public static void AppendMetrics(string serviceName, MetricSample s) =>
        Append(MetricsPathFor(serviceName), MetricsHeader, string.Join(',',
            Stamp(s.Utc), Num(s.CpuAverage), Num(s.CpuPeak),
            s.MemoryAverage.ToString(CultureInfo.InvariantCulture),
            s.MemoryPeak.ToString(CultureInfo.InvariantCulture),
            s.Processes.ToString(CultureInfo.InvariantCulture),
            s.RestartsTotal.ToString(CultureInfo.InvariantCulture)));

    public static void AppendEvent(string serviceName, HistoryEvent e) =>
        Append(EventsPathFor(serviceName), EventsHeader, string.Join(',',
            Stamp(e.Utc),
            e.EventId.ToString(CultureInfo.InvariantCulture),
            e.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "",
            Quote(e.Detail)));

    /// <summary>Minimal RFC4180 quoting - enough for text that may contain commas or quotes.</summary>
    private static string Quote(string value)
    {
        value = (value ?? "").ReplaceLineEndings(" ").Trim();
        if (value.Length == 0) return "";
        if (value.IndexOfAny(new[] { ',', '"' }) < 0) return value;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    // ------------------------------------------------------------------ read ---

    private static List<string> ReadLines(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return new List<string>();
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var lines = new List<string>();
                while (reader.ReadLine() is { } line)
                    if (line.Length > 0) lines.Add(line);
                return lines;
            }
            catch (IOException)
            {
                Thread.Sleep(40);
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }
        return new List<string>();
    }

    public static List<MetricSample> ReadMetrics(string serviceName, DateTime sinceUtc)
    {
        var result = new List<MetricSample>();
        foreach (var line in ReadLines(MetricsPathFor(serviceName)))
        {
            if (line.StartsWith("utc,", StringComparison.Ordinal)) continue;
            var f = line.Split(',');
            if (f.Length < 7) continue;
            if (!TryStamp(f[0], out var utc) || utc < sinceUtc) continue;

            result.Add(new MetricSample(utc,
                Dbl(f[1]), Dbl(f[2]), Lng(f[3]), Lng(f[4]), (int)Lng(f[5]), (int)Lng(f[6])));
        }
        result.Sort((a, b) => a.Utc.CompareTo(b.Utc));
        return result;
    }

    public static List<HistoryEvent> ReadEvents(string serviceName, DateTime sinceUtc)
    {
        var result = new List<HistoryEvent>();
        foreach (var line in ReadLines(EventsPathFor(serviceName)))
        {
            if (line.StartsWith("utc,", StringComparison.Ordinal)) continue;
            var f = SplitCsv(line);
            if (f.Count < 4) continue;
            if (!TryStamp(f[0], out var utc) || utc < sinceUtc) continue;

            uint? exit = uint.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : null;
            result.Add(new HistoryEvent(utc, (int)Lng(f[1]), exit, f[3]));
        }
        result.Sort((a, b) => a.Utc.CompareTo(b.Utc));
        return result;
    }

    private static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }

    private static bool TryStamp(string value, out DateTime utc) =>
        DateTime.TryParseExact(value, "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);

    private static double Dbl(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static long Lng(string s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    // ----------------------------------------------------------------- prune ---

    /// <summary>
    /// Drops rows older than the retention window by rewriting the file. Called rarely
    /// (service start, then daily), so the cost of the rewrite does not matter.
    /// </summary>
    public static void Prune(string serviceName, TimeSpan retention)
    {
        if (retention <= TimeSpan.Zero) return;
        var cutoff = DateTime.UtcNow - retention;

        PruneFile(MetricsPathFor(serviceName), MetricsHeader, cutoff);
        PruneFile(EventsPathFor(serviceName), EventsHeader, cutoff);
    }

    private static void PruneFile(string path, string header, DateTime cutoff)
    {
        try
        {
            if (!File.Exists(path)) return;

            var kept = new List<string>();
            var dropped = 0;
            foreach (var line in ReadLines(path))
            {
                if (line.StartsWith("utc,", StringComparison.Ordinal)) continue;
                var stamp = line.Split(',', 2)[0];
                if (TryStamp(stamp, out var utc) && utc < cutoff) { dropped++; continue; }
                kept.Add(line);
            }
            if (dropped == 0) return;

            var temp = path + ".tmp";
            lock (WriteLock)
            {
                File.WriteAllLines(temp, kept.Prepend(header), new UTF8Encoding(false));
                File.Move(temp, path, overwrite: true);
            }
        }
        catch (Exception)
        {
        }
    }

    public static void Delete(string serviceName)
    {
        foreach (var path in new[] { MetricsPathFor(serviceName), EventsPathFor(serviceName) })
        {
            try { File.Delete(path); } catch { }
        }
    }

    // --------------------------------------------------------------- summary ---

    public sealed record Summary(
        int Samples,
        double CpuAverage,
        double CpuPeak,
        long MemoryAverage,
        long MemoryPeak,
        int Restarts,
        TimeSpan Covered);

    /// <summary>Condenses a window of samples into the numbers worth putting on screen.</summary>
    public static Summary Summarize(IReadOnlyList<MetricSample> samples, IReadOnlyList<HistoryEvent> events)
    {
        var restarts = events.Count(e => e.EventId == (int)EasyServiceEvent.ApplicationStarted);

        if (samples.Count == 0)
            return new Summary(0, 0, 0, 0, 0, restarts, TimeSpan.Zero);

        return new Summary(
            samples.Count,
            samples.Average(s => s.CpuAverage),
            samples.Max(s => s.CpuPeak),
            (long)samples.Average(s => (double)s.MemoryAverage),
            samples.Max(s => s.MemoryPeak),
            restarts,
            samples[^1].Utc - samples[0].Utc);
    }
}
