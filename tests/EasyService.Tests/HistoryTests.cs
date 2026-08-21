using EasyService.Core;

namespace EasyService.Tests;

/// <summary>
/// Tests for the long-term memory of a service. Two things have to hold: what goes in
/// comes back out unharmed, and the files do not grow without bound.
/// </summary>
internal static class HistoryTests
{
    private static string _root = "";

    public static IEnumerable<(string Name, Action Test)> All(string root)
    {
        _root = root;
        yield return ("Messwerte überstehen Schreiben und Lesen", MetricsRoundTrip);
        yield return ("Ereignistexte mit Komma und Anführungszeichen bleiben heil", EventQuotingSurvives);
        yield return ("Alte Zeilen werden nach Ablauf entfernt", PruneDropsOldRows);
        yield return ("Der Supervisor schreibt Ereignisse in die Historie", SupervisorRecordsEvents);
        yield return ("Die Zusammenfassung rechnet Mittel, Spitze und Neustarts", SummaryAggregates);
        yield return ("Das Verlaufsfenster zeichnet sich fehlerfrei", HistoryWindowRenders);
    }

    // ------------------------------------------------------------- Datenhaltung

    private static void MetricsRoundTrip()
    {
        var service = Name("roundtrip");
        try
        {
            var now = DateTime.UtcNow;
            // Absichtlich in falscher Reihenfolge geschrieben: das Lesen muss sortieren.
            HistoryStore.AppendMetrics(service, new MetricSample(Minute(now, -1), 12.5, 88.25, 1000, 2000, 3, 7));
            HistoryStore.AppendMetrics(service, new MetricSample(Minute(now, -3), 1.5, 2.5, 100, 200, 1, 5));
            HistoryStore.AppendMetrics(service, new MetricSample(Minute(now, -2), 0, 0, 0, 0, 0, 6));

            var read = HistoryStore.ReadMetrics(service, now.AddHours(-1));
            Assert(read.Count == 3, $"erwartet: 3 Zeilen, gelesen: {read.Count}");
            Assert(read[0].Utc < read[1].Utc && read[1].Utc < read[2].Utc, "die Zeilen sind nicht zeitlich sortiert");

            var newest = read[^1];
            Assert(Math.Abs(newest.CpuAverage - 12.5) < 0.001, $"CPU-Mittel verfälscht: {newest.CpuAverage}");
            Assert(Math.Abs(newest.CpuPeak - 88.25) < 0.001, $"CPU-Spitze verfälscht: {newest.CpuPeak}");
            Assert(newest.MemoryPeak == 2000, $"Speicherspitze verfälscht: {newest.MemoryPeak}");
            Assert(newest.RestartsTotal == 7, $"Neustartzähler verfälscht: {newest.RestartsTotal}");

            // Der Zeitfilter muss greifen.
            var recent = HistoryStore.ReadMetrics(service, Minute(now, -2));
            Assert(recent.Count == 2, $"Zeitfilter liefert {recent.Count} statt 2 Zeilen");
        }
        finally
        {
            HistoryStore.Delete(service);
        }
    }

    private static void EventQuotingSurvives()
    {
        var service = Name("quoting");
        try
        {
            // Genau der Fall, an dem selbstgebaute CSV-Schreiber scheitern.
            const string nasty = "Beendet mit Code 3, \"unerwartet\" — Pfad C:\\a,b\\x.exe";
            HistoryStore.AppendEvent(service, new HistoryEvent(DateTime.UtcNow,
                (int)EasyServiceEvent.ApplicationExited, 3, nasty));

            var read = HistoryStore.ReadEvents(service, DateTime.UtcNow.AddMinutes(-5));
            Assert(read.Count == 1, $"erwartet: 1 Ereignis, gelesen: {read.Count}");
            Assert(read[0].Detail == nasty, $"Text verfälscht:\n  erwartet: {nasty}\n  gelesen:  {read[0].Detail}");
            Assert(read[0].ExitCode == 3, $"Exit-Code verfälscht: {read[0].ExitCode?.ToString() ?? "keiner"}");
            Assert(read[0].EventId == (int)EasyServiceEvent.ApplicationExited, "Ereignis-ID verfälscht");
        }
        finally
        {
            HistoryStore.Delete(service);
        }
    }

    private static void PruneDropsOldRows()
    {
        var service = Name("prune");
        try
        {
            var now = DateTime.UtcNow;
            HistoryStore.AppendMetrics(service, new MetricSample(now.AddDays(-40), 1, 1, 1, 1, 1, 0));
            HistoryStore.AppendMetrics(service, new MetricSample(now.AddDays(-10), 2, 2, 2, 2, 1, 0));
            HistoryStore.AppendMetrics(service, new MetricSample(now.AddMinutes(-5), 3, 3, 3, 3, 1, 0));
            HistoryStore.AppendEvent(service, new HistoryEvent(now.AddDays(-40),
                (int)EasyServiceEvent.ApplicationStarted, null, "alt"));
            HistoryStore.AppendEvent(service, new HistoryEvent(now.AddMinutes(-5),
                (int)EasyServiceEvent.ApplicationStarted, null, "neu"));

            HistoryStore.Prune(service, TimeSpan.FromDays(30));

            var metrics = HistoryStore.ReadMetrics(service, DateTime.MinValue);
            Assert(metrics.Count == 2, $"erwartet: 2 verbleibende Messzeilen, vorhanden: {metrics.Count}");
            Assert(metrics.All(m => m.Utc > now.AddDays(-31)), "eine zu alte Messzeile hat überlebt");

            var events = HistoryStore.ReadEvents(service, DateTime.MinValue);
            Assert(events.Count == 1, $"erwartet: 1 verbleibendes Ereignis, vorhanden: {events.Count}");
            Assert(events[0].Detail == "neu", "das falsche Ereignis wurde behalten");

            // Nach dem Kürzen muss die Datei weiter lesbar sein - also mit Kopfzeile.
            var lines = File.ReadAllLines(HistoryStore.MetricsPathFor(service));
            Assert(lines[0].StartsWith("utc,"), $"die Kopfzeile fehlt nach dem Kürzen: {lines[0]}");
        }
        finally
        {
            HistoryStore.Delete(service);
        }
    }

    private static void SummaryAggregates()
    {
        var now = DateTime.UtcNow;
        var samples = new List<MetricSample>
        {
            new(now.AddMinutes(-2), 10, 20, 100, 200, 1, 0),
            new(now.AddMinutes(-1), 30, 90, 300, 400, 1, 1),
        };
        var events = new List<HistoryEvent>
        {
            new(now.AddMinutes(-2), (int)EasyServiceEvent.ApplicationStarted, null, ""),
            new(now.AddMinutes(-1), (int)EasyServiceEvent.ApplicationStarted, null, ""),
            new(now.AddMinutes(-1), (int)EasyServiceEvent.ApplicationExited, 1, ""),
        };

        var summary = HistoryStore.Summarize(samples, events);
        Assert(Math.Abs(summary.CpuAverage - 20) < 0.001, $"CPU-Mittel: {summary.CpuAverage}");
        Assert(Math.Abs(summary.CpuPeak - 90) < 0.001, $"CPU-Spitze: {summary.CpuPeak}");
        Assert(summary.MemoryPeak == 400, $"Speicherspitze: {summary.MemoryPeak}");
        Assert(summary.Restarts == 2, $"gezählte Starts: {summary.Restarts}");
        Assert(HistoryStore.Summarize(new List<MetricSample>(), events).Samples == 0,
            "eine leere Messreihe muss ohne Absturz zusammengefasst werden");
    }

    // ---------------------------------------------------------- Supervisorlauf

    private static void SupervisorRecordsEvents()
    {
        var dir = Path.Combine(_root, "history");
        Directory.CreateDirectory(dir);

        var config = new ServiceConfig
        {
            ServiceName = Name("supervisor"),
            Application = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            AppParameters = "/c \"exit /b 4\"",
            AppDirectory = dir,
            StdoutPath = Path.Combine(dir, "stdout.log"),
            StderrPath = Path.Combine(dir, "stderr.log"),
            DefaultExitAction = ExitAction.Stop,
            HistoryDays = 30,
        };

        try
        {
            HistoryStore.Delete(config.ServiceName);

            using (var supervisor = new ProcessSupervisor(config))
            {
                var task = Task.Run(supervisor.Run);
                task.Wait(TimeSpan.FromSeconds(25));
            }

            var events = HistoryStore.ReadEvents(config.ServiceName, DateTime.UtcNow.AddMinutes(-10));
            Assert(events.Count >= 2, $"erwartet: mindestens 2 Ereignisse, aufgezeichnet: {events.Count}");

            Assert(events.Any(e => e.EventId == (int)EasyServiceEvent.ApplicationStarted),
                "der Start der Anwendung wurde nicht aufgezeichnet");

            var exited = events.FirstOrDefault(e => e.EventId == (int)EasyServiceEvent.ApplicationExited);
            Assert(exited is not null, "das Beenden der Anwendung wurde nicht aufgezeichnet");
            Assert(exited!.ExitCode == 4,
                $"der Exit-Code wurde nicht strukturiert festgehalten: {exited.ExitCode?.ToString() ?? "keiner"}");
        }
        finally
        {
            HistoryStore.Delete(config.ServiceName);
            ServiceState.Delete(config.ServiceName);
        }
    }

    /// <summary>
    /// Rendert das Fenster mit gefüllter Historie in eine Bitmap. Fängt Fehler ab, die
    /// beim blossen Aufbauen der Steuerelemente nicht auffallen - alles Zeichnen passiert
    /// erst in OnPaint.
    /// </summary>
    private static void HistoryWindowRenders()
    {
        var config = new ServiceConfig
        {
            ServiceName = Name("render"),
            Application = @"C:\demo\app.exe",
            HistoryDays = 30,
        };

        try
        {
            HistoryStore.Delete(config.ServiceName);
            Seed(config.ServiceName);

            using var form = new Gui.HistoryForm(config);
            form.Size = new Size(1040, 780);
            form.CreateControl();
            form.Show();
            Application.DoEvents();

            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
            form.Hide();

            // Ausserhalb des Testordners, damit das Bild das Aufraeumen ueberlebt
            // und man es sich nach dem Lauf ansehen kann.
            var target = Path.Combine(Path.GetTempPath(), "easyservice-history-window.png");
            bitmap.Save(target, System.Drawing.Imaging.ImageFormat.Png);

            // Eine leere Zeichenfläche wäre einfarbig; echter Inhalt bringt viele Farben mit.
            var distinct = new HashSet<int>();
            for (var x = 0; x < bitmap.Width; x += 7)
                for (var y = 0; y < bitmap.Height; y += 7)
                    distinct.Add(bitmap.GetPixel(x, y).ToArgb());

            Assert(distinct.Count > 12, $"das Fenster wirkt leer, nur {distinct.Count} verschiedene Farben");
        }
        finally
        {
            HistoryStore.Delete(config.ServiceName);
        }
    }

    /// <summary>Zwei Stunden plausibler Messwerte plus ein paar Ereignisse.</summary>
    private static void Seed(string service)
    {
        var start = DateTime.UtcNow.AddHours(-2);
        var random = new Random(1234);
        var restarts = 0;

        for (var i = 0; i < 120; i++)
        {
            var utc = start.AddMinutes(i);
            var baseline = 6 + 4 * Math.Sin(i / 9.0);
            var spike = i % 17 == 0 ? 55 : 0;

            if (i is 40 or 41 or 83)
            {
                restarts++;
                HistoryStore.AppendEvent(service, new HistoryEvent(utc,
                    (int)EasyServiceEvent.ApplicationStarted, null, "Anwendung gestartet (PID 4711)."));
                HistoryStore.AppendEvent(service, new HistoryEvent(utc.AddSeconds(-3),
                    (int)EasyServiceEvent.ApplicationExited, 3, "Anwendung beendet mit Code 3."));
            }

            HistoryStore.AppendMetrics(service, new MetricSample(utc,
                baseline + random.NextDouble() * 2,
                baseline + spike + random.NextDouble() * 6,
                (long)((160 + i * 0.9) * 1024 * 1024),
                (long)((180 + i * 1.1) * 1024 * 1024),
                2, restarts));
        }
    }

    // ------------------------------------------------------------------ Hilfen

    private static string Name(string suffix) => "EasyServiceHistTest_" + suffix;

    private static DateTime Minute(DateTime utc, int offset)
    {
        var m = utc.AddMinutes(offset);
        return new DateTime(m.Year, m.Month, m.Day, m.Hour, m.Minute, 0, DateTimeKind.Utc);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
