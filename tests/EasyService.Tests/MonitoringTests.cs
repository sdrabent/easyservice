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
        Assert(line.StartsWith("EASYSERVICE WARNUNG - "), $"unerwarteter Präfix: {line}");

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
