using EasyService.Core;

namespace EasyService.Tests;

/// <summary>
/// Tests for the part an administrator plugs into Checkmk, Prometheus or Zabbix.
/// Two halves: the supervisor really has to produce the numbers, and the formatters
/// really have to emit what those tools can parse.
/// </summary>
internal static class MonitoringTests
{
    private static string _root = "";

    public static IEnumerable<(string Name, Action Test)> All(string root)
    {
        _root = root;
        yield return ("Zustandsdatei meldet die laufende Anwendung", StateFileReportsRunning);
        yield return ("Neustarts werden für die Flapping-Erkennung gezählt", StateFileCountsRestarts);
        yield return ("CPU und Arbeitsspeicher werden gemessen", ResourcesAreMeasured);
        yield return ("Checkmk-Ausgabe hat das erwartete Format", CheckmkFormatIsValid);
        yield return ("Prometheus-Ausgabe hat das erwartete Format", PrometheusFormatIsValid);
        yield return ("Nagios-Ausgabe trennt Text und Perfdaten", NagiosFormatIsValid);
        yield return ("Zahlen nutzen immer den Punkt als Dezimaltrenner", NumbersAreInvariant);
        yield return ("Schwellwerte lösen Warnung und kritisch aus", ThresholdsEscalate);
        yield return ("Veralteter Zustand gilt als unbekannt, nicht als gesund", StaleStateIsUnknown);
        yield return ("Gemerkte Zugangsdaten sind verschlüsselt und wiederherstellbar", CredentialRoundTrips);
        yield return ("Oberflächentexte lassen sich zwischen Englisch und Deutsch umschalten", LanguagesSwitch);
        yield return ("Jeder Text ist übersetzt und behält seine Platzhalter", TranslationsAreComplete);
    }

    // ----------------------------------------------------- supervisor produces ---

    private static void StateFileReportsRunning()
    {
        var config = NewConfig("state");
        config.AppParameters = "/c \"ping -n 30 127.0.0.1 >nul\"";
        ServiceState.Delete(config.ServiceName);

        using var supervisor = new ProcessSupervisor(config);
        var task = Task.Run(supervisor.Run);
        try
        {
            var state = WaitFor(config.ServiceName, s => s.State == SupervisorState.Running, TimeSpan.FromSeconds(15));
            Assert(state is not null, "die Zustandsdatei meldet nie den Zustand \"Running\"");
            Assert(state!.ApplicationPid > 0, "in der Zustandsdatei fehlt die PID der Anwendung");
            Assert(state.SupervisorPid == Environment.ProcessId, "die Supervisor-PID stimmt nicht");
            Assert(!state.IsStale, $"die Zustandsdatei gilt bereits als veraltet (Alter {state.Age.TotalSeconds:F0}s)");
        }
        finally
        {
            supervisor.RequestStop();
            task.Wait(TimeSpan.FromSeconds(20));
        }
    }

    private static void StateFileCountsRestarts()
    {
        var config = NewConfig("restartcount");
        config.AppParameters = "/c \"exit /b 1\"";
        config.DefaultExitAction = ExitAction.Restart;
        config.RestartDelayMs = 100;
        config.ThrottleMs = 0;
        ServiceState.Delete(config.ServiceName);

        using (var supervisor = new ProcessSupervisor(config))
        {
            var task = Task.Run(supervisor.Run);
            Thread.Sleep(3000);
            supervisor.RequestStop();
            task.Wait(TimeSpan.FromSeconds(15));
        }

        var state = ServiceState.Load(config.ServiceName);
        Assert(state is not null, "es wurde keine Zustandsdatei geschrieben");
        Assert(state!.RestartCount >= 2, $"erwartet: mindestens 2 gezählte Neustarts, tatsächlich: {state.RestartCount}");
        Assert(state.RestartsLastHour >= 2,
            $"erwartet: mindestens 2 Neustarts in der letzten Stunde, tatsächlich: {state.RestartsLastHour}");
        Assert(state.LastExitCode == 1,
            $"erwarteter letzter Exit-Code 1, gemeldet: {state.LastExitCode?.ToString() ?? "keiner"}");
    }

    private static void ResourcesAreMeasured()
    {
        var config = NewConfig("resources");
        // Busy loop: keeps one core occupied so the sampler has something to see.
        config.AppParameters = "/c \"for /l %i in (1,1,2000000000) do @rem\"";
        ServiceState.Delete(config.ServiceName);

        using var supervisor = new ProcessSupervisor(config);
        var task = Task.Run(supervisor.Run);
        try
        {
            var state = WaitFor(config.ServiceName,
                s => s.WorkingSetBytes > 0 && s.CpuPercent > 0, TimeSpan.FromSeconds(30));
            Assert(state is not null, "es wurden nie CPU- und Speicherwerte gemeldet");
            Assert(state!.WorkingSetBytes > 100 * 1024, $"unplausibler Speicherwert: {state.WorkingSetBytes} Bytes");
            Assert(state.CpuPercent is > 0 and <= 100, $"unplausibler CPU-Wert: {state.CpuPercent}");
            Assert(state.ProcessCount >= 1, $"unplausible Prozessanzahl: {state.ProcessCount}");
            Assert(state.CpuSecondsTotal > 0, "es wurde keine CPU-Zeit summiert");
        }
        finally
        {
            supervisor.RequestStop();
            task.Wait(TimeSpan.FromSeconds(20));
        }
    }

    // --------------------------------------------------------- formats parse ---

    private static void CheckmkFormatIsValid()
    {
        var text = MonitoringOutput.Checkmk(new[] { Sample() });
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert(lines.Length == 1, $"erwartet: eine Zeile, tatsächlich: {lines.Length}");

        // Checkmk zerlegt die ersten drei Felder an Leerzeichen; der Rest ist der Text.
        var parts = lines[0].Split(' ', 4);
        Assert(parts.Length == 4, $"eine Checkmk-Zeile braucht vier Felder, hat aber {parts.Length}: {lines[0]}");
        Assert(int.TryParse(parts[0], out var status) && status is >= 0 and <= 3, $"ungültiger Status: {parts[0]}");
        Assert(parts[1] == "EasyService_Mein_Dienst", $"unerwarteter Item-Name: {parts[1]}");
        Assert(!parts[2].Contains(' '), $"Perfdaten dürfen kein Leerzeichen enthalten: {parts[2]}");
        Assert(parts[2].Contains("cpu=2.5%;80;95;0;100"), $"unvollständige CPU-Perfdaten: {parts[2]}");
        Assert(parts[2].Contains("mem=140509184B"), $"Speicher-Perfdaten fehlen: {parts[2]}");
        Assert(parts[3].Length > 0, "der Meldungstext fehlt");
    }

    private static void PrometheusFormatIsValid()
    {
        var text = MonitoringOutput.Prometheus(new[] { Sample(), Sample("Zweiter Dienst") });
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert(lines.Count(l => l.StartsWith("# HELP easyservice_cpu_percent")) == 1,
            "HELP für easyservice_cpu_percent muss genau einmal vorkommen");
        Assert(lines.Count(l => l.StartsWith("# TYPE easyservice_cpu_percent")) == 1,
            "TYPE für easyservice_cpu_percent muss genau einmal vorkommen");

        // Prometheus verlangt, dass alle Samples einer Familie zusammenhängen.
        var help = Array.FindIndex(lines, l => l.StartsWith("# HELP easyservice_cpu_percent"));
        Assert(lines[help + 1].StartsWith("# TYPE easyservice_cpu_percent"), "auf HELP muss TYPE folgen");
        Assert(lines[help + 2].StartsWith("easyservice_cpu_percent{"), "auf TYPE muss ein Sample folgen");
        Assert(lines[help + 3].StartsWith("easyservice_cpu_percent{"), "beide Samples müssen zusammenstehen");

        var sample = lines[help + 2];
        Assert(sample.Contains("service=\"Mein Dienst\""), $"Label fehlt oder ist falsch: {sample}");
        Assert(sample.EndsWith(" 2.5"), $"unerwarteter Messwert: {sample}");
    }

    private static void NagiosFormatIsValid()
    {
        var line = MonitoringOutput.Nagios(Sample(status: CheckStatus.Warning));

        // Der Statustext ist übersetzt; geprüft wird gegen den Wert, den die Bewertung
        // selbst liefert, nicht gegen ein Literal einer bestimmten Sprache.
        var expected = $"EASYSERVICE {Monitoring.Describe(CheckStatus.Warning)} - ";
        Assert(line.StartsWith(expected), $"unerwarteter Präfix: {line}");
        Assert(expected == "EASYSERVICE WARNING - ",
            $"die Tests laufen nicht auf Englisch, sondern liefern: {expected}");

        var pipes = line.Count(c => c == '|');
        Assert(pipes == 1, $"ein Nagios-Plugin trennt Text und Perfdaten mit genau einem |, gefunden: {pipes}");
        Assert(line.Split('|')[1].Contains("cpu=2.5%"), $"in den Perfdaten fehlt der CPU-Wert: {line}");
    }

    private static void NumbersAreInvariant()
    {
        var outputs = new[]
        {
            ("checkmk", MonitoringOutput.Checkmk(new[] { Sample() })),
            ("prometheus", MonitoringOutput.Prometheus(new[] { Sample() })),
            ("json", MonitoringOutput.Json(new[] { Sample() })),
            ("nagios", MonitoringOutput.Nagios(Sample())),
        };

        foreach (var (name, text) in outputs)
        {
            Assert(text.Contains("2.5"), $"{name}: der Wert 2.5 fehlt");
            Assert(!text.Contains("2,5"), $"{name}: Komma als Dezimaltrenner gefunden - das bricht jeden Parser");
        }
    }

    private static void ThresholdsEscalate()
    {
        // Ein Dienst, der stündlich zweistellig oft neu startet, meldet der SCM weiterhin
        // als "läuft". Genau das muss die Auswertung als kritisch erkennen.
        var ok = Evaluate(restartsLastHour: 0, cpu: 5);
        var warn = Evaluate(restartsLastHour: 4, cpu: 5);
        var crit = Evaluate(restartsLastHour: 12, cpu: 5);
        var cpuCrit = Evaluate(restartsLastHour: 0, cpu: 99);

        Assert(ok == CheckStatus.Ok, $"ruhiger Dienst sollte OK sein, war: {ok}");
        Assert(warn == CheckStatus.Warning, $"4 Neustarts/h sollten warnen, waren: {warn}");
        Assert(crit == CheckStatus.Critical, $"12 Neustarts/h sollten kritisch sein, waren: {crit}");
        Assert(cpuCrit == CheckStatus.Critical, $"99 % CPU sollten kritisch sein, waren: {cpuCrit}");
    }

    private static void LanguagesSwitch()
    {
        var before = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            Localization.Apply("en");
            Assert(EasyService.Resources.S.Common_Cancel == "Cancel",
                $"Englisch greift nicht: \"{EasyService.Resources.S.Common_Cancel}\"");
            Assert(EasyService.Resources.S.Cfg_Err_AppNotFound("x.exe") == "Program not found: x.exe",
                "Platzhalter werden im Englischen nicht ersetzt");

            Localization.Apply("de");
            Assert(EasyService.Resources.S.Common_Cancel == "Abbrechen",
                $"Deutsch greift nicht - Satellite-Assembly fehlt? Gelesen: \"{EasyService.Resources.S.Common_Cancel}\"");
            Assert(EasyService.Resources.S.Cfg_Err_AppNotFound("x.exe") == "Programm nicht gefunden: x.exe",
                "Platzhalter werden im Deutschen nicht ersetzt");

            // Unbekannter Code darf nicht werfen, sondern still bei der bisherigen Sprache bleiben.
            Localization.Apply("kl-KL-nonsense");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = before;
        }
    }

    /// <summary>
    /// Findet die beiden Fehler, die bei Übersetzungen wirklich passieren: ein Schlüssel
    /// wird nur in einer Sprache gepflegt, oder jemand verliert beim Übersetzen ein {0}.
    /// Letzteres würde erst zur Laufzeit als FormatException auffallen.
    /// </summary>
    private static void TranslationsAreComplete()
    {
        var assembly = typeof(ServiceConfig).Assembly;
        var manager = new System.Resources.ResourceManager("EasyService.Resources.Strings", assembly);

        var neutral = manager.GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, true, true);
        Assert(neutral is not null, "die neutralen Ressourcen fehlen");

        var german = manager.GetResourceSet(System.Globalization.CultureInfo.GetCultureInfo("de"), true, false);
        Assert(german is not null, "die deutsche Satellite-Assembly fehlt");

        var missing = new List<string>();
        var mismatched = new List<string>();
        var count = 0;

        foreach (System.Collections.DictionaryEntry entry in neutral!)
        {
            var key = (string)entry.Key;
            var english = entry.Value as string;
            if (english is null) continue;
            count++;

            var translated = german!.GetString(key);
            if (translated is null) { missing.Add(key); continue; }
            if (Placeholders(english) != Placeholders(translated)) mismatched.Add(key);
        }

        Assert(count > 300, $"unerwartet wenige Schlüssel gefunden: {count}");
        Assert(missing.Count == 0, $"ohne deutsche Übersetzung: {string.Join(", ", missing.Take(10))}");
        Assert(mismatched.Count == 0,
            $"abweichende Platzhalter zwischen den Sprachen: {string.Join(", ", mismatched.Take(10))}");
    }

    /// <summary>Höchster verwendeter Platzhalterindex + 1, also die nötige Argumentzahl.</summary>
    private static int Placeholders(string text)
    {
        var highest = -1;
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(text, @"\{(\d+)[^}]*\}"))
            highest = Math.Max(highest, int.Parse(m.Groups[1].Value));
        return highest + 1;
    }

    private static void CredentialRoundTrips()
    {
        // Die Fast Lane merkt sich das Dienstkonto, damit der zweite Dienst schneller geht.
        // Im Klartext darf das Kennwort dabei nirgends landen.
        const string secret = "Str3ng-Geheim!";
        var bytes = System.Text.Encoding.UTF8.GetBytes(secret);

        var encrypted = Dpapi.Protect(bytes);
        Assert(encrypted is not null, "das Kennwort konnte nicht verschlüsselt werden");
        Assert(!System.Text.Encoding.UTF8.GetString(encrypted!).Contains(secret),
            "das Kennwort steht im Klartext im verschlüsselten Blob");
        Assert(encrypted!.Length > bytes.Length, "der verschlüsselte Wert ist verdächtig kurz");

        var decrypted = Dpapi.Unprotect(encrypted);
        Assert(decrypted is not null, "das Kennwort ließ sich nicht wieder entschlüsseln");
        Assert(System.Text.Encoding.UTF8.GetString(decrypted!) == secret, "das entschlüsselte Kennwort weicht ab");

        // Ein manipulierter Blob darf nicht stillschweigend Müll liefern.
        var tampered = (byte[])encrypted.Clone();
        tampered[^1] ^= 0xFF;
        Assert(Dpapi.Unprotect(tampered) is null, "ein manipulierter Blob wurde akzeptiert");
    }

    private static void StaleStateIsUnknown()
    {
        // Stirbt der Supervisor-Prozess, bleibt die Zustandsdatei liegen. Ihre Zahlen dann
        // weiter als gültig zu melden waere schlimmer als zuzugeben, dass man nichts weiss.
        var config = new ServiceConfig { ServiceName = "EasyServiceStaleProbe" };
        var info = new ServiceInfo(config.ServiceName, config.ServiceName, 4, 4242,
            StartupType.Automatic, "", "LocalSystem", true, "");

        var fresh = new ServiceState
        {
            ServiceName = config.ServiceName,
            State = SupervisorState.Running,
            ApplicationPid = 4242,
            ApplicationStartedUtc = DateTime.UtcNow.AddMinutes(-30),
            UpdatedUtc = DateTime.UtcNow,
        };
        Assert(Monitoring.Evaluate(config, fresh, info).Status == CheckStatus.Ok,
            "ein frischer Zustand sollte OK sein");

        var stale = new ServiceState
        {
            ServiceName = config.ServiceName,
            State = SupervisorState.Running,
            ApplicationPid = 4242,
            ApplicationStartedUtc = DateTime.UtcNow.AddMinutes(-30),
            UpdatedUtc = DateTime.UtcNow.AddMinutes(-10),
        };
        Assert(stale.IsStale, "ein 10 Minuten alter Zustand muss als veraltet gelten");
        Assert(Monitoring.Evaluate(config, stale, info).Status == CheckStatus.Unknown,
            "ein veralteter Zustand darf nicht als gesund gemeldet werden");

        Assert(Monitoring.Evaluate(config, null, info).Status == CheckStatus.Unknown,
            "ohne Zustandsdatei ist der Status unbekannt");
    }

    /// <summary>
    /// Drives the threshold logic through the real evaluator by writing a state file for a
    /// service name and asking Monitoring to judge it.
    /// </summary>
    private static CheckStatus Evaluate(int restartsLastHour, double cpu)
    {
        var config = new ServiceConfig
        {
            ServiceName = "EasyServiceThresholdProbe",
            WarnRestartsPerHour = 3,
            CritRestartsPerHour = 10,
            WarnCpuPercent = 80,
            CritCpuPercent = 95,
        };

        var state = new ServiceState
        {
            ServiceName = config.ServiceName,
            State = SupervisorState.Running,
            ApplicationPid = 4242,
            ApplicationStartedUtc = DateTime.UtcNow.AddHours(-2),
            CpuPercent = cpu,
            WorkingSetBytes = 50L * 1024 * 1024,
            ProcessCount = 1,
            RecentRestartsUtc = Enumerable.Range(0, restartsLastHour)
                                          .Select(i => DateTime.UtcNow.AddMinutes(-i))
                                          .ToList(),
            RestartCount = restartsLastHour,
            UpdatedUtc = DateTime.UtcNow,   // ohne das gilt der Zustand als veraltet
        };

        var info = new ServiceInfo(config.ServiceName, config.ServiceName, 4, 4242,
            StartupType.Automatic, "", "LocalSystem", true, "");
        return Monitoring.Evaluate(config, state, info).Status;
    }

    // ------------------------------------------------------------- helpers ---

    private static string Cmd => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    private static ServiceConfig NewConfig(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return new ServiceConfig
        {
            ServiceName = "EasyServiceMonTest_" + name,
            Application = Cmd,
            AppDirectory = dir,
            StdoutPath = Path.Combine(dir, "stdout.log"),
            StderrPath = Path.Combine(dir, "stderr.log"),
            RestartDelayMs = 100,
            ThrottleMs = 0,
        };
    }

    private static ServiceState? WaitFor(string name, Func<ServiceState, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var state = ServiceState.Load(name);
            if (state is not null && predicate(state)) return state;
            Thread.Sleep(200);
        }
        return null;
    }

    private static CheckResult Sample(string name = "Mein Dienst", CheckStatus status = CheckStatus.Ok) => new(
        name, name, status,
        "Läuft seit 1d 2h, PID 1234, CPU 2.5 %, RAM 134 MB",
        new[]
        {
            new Metric("cpu", 2.5, "%", 80, 95, 0, 100),
            new Metric("mem", 140509184, "B", Min: 0),
            new Metric("restarts_1h", 0, "", 3, 10, 0),
        },
        new ServiceState
        {
            ServiceName = name,
            State = SupervisorState.Running,
            ApplicationPid = 1234,
            ApplicationStartedUtc = DateTime.UtcNow.AddHours(-1),
            CpuPercent = 2.5,
            WorkingSetBytes = 140509184,
            ProcessCount = 2,
            UpdatedUtc = DateTime.UtcNow,
        },
        new ServiceInfo(name, name, 4, 1234, StartupType.Automatic, "", "LocalSystem", true, ""));

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
