using EasyService.Core;

namespace EasyService;

/// <summary>
/// Command line front end. The GUI is the primary interface, but scripted installs and -
/// more importantly - monitoring agents need a non-interactive path. Every check command
/// prints to stdout and sets a meaningful exit code.
/// </summary>
internal static class Cli
{
    public static int Execute(string[] args)
    {
        Program.AttachConsole();

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "list" => List(args),
                "install" => Install(args),
                "remove" or "uninstall" => Remove(args),
                "start" => Simple(args, n => ServiceRegistry.Start(n, TimeSpan.FromSeconds(60)), "gestartet"),
                "stop" => Simple(args, n => ServiceRegistry.Stop(n, TimeSpan.FromSeconds(60)), "beendet"),
                "restart" => Simple(args, n => ServiceRegistry.Restart(n, TimeSpan.FromSeconds(60)), "neu gestartet"),
                "status" => Status(args),

                // monitoring integrations
                "checkmk" => Checkmk(),
                "prometheus" or "metrics" => Prometheus(args),
                "check" => Check(args),
                "json" => Json(),
                "zabbix-discovery" => ZabbixDiscovery(),

                "-h" or "--help" or "/?" or "help" => Usage(0),
                "-v" or "--version" or "version" => Version(),
                _ => Usage(2, $"Unbekannter Befehl: {args[0]}"),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Fehler: " + e.Message);
            return 1;
        }
    }

    private static int Version()
    {
        Console.WriteLine($"easyservice {typeof(Cli).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}");
        return 0;
    }

    private static int Usage(int code, string? message = null)
    {
        if (message is not null) Console.Error.WriteLine(message);
        Console.WriteLine("""
            easyservice - Windows-Dienste per GUI verwalten (Alternative zu nssm.exe)

            Ohne Argumente startet die grafische Oberfläche.

            VERWALTEN
              easyservice list [--json]
                  Zeigt alle Dienste an; von EasyService verwaltete sind markiert.

              easyservice install <Name> <Programm> [Argumente...]
                  Legt einen neuen Dienst an (Autostart, Logging unter %ProgramData%\EasyService\logs).

              easyservice remove <Name>
                  Beendet und entfernt den Dienst.

              easyservice start|stop|restart|status <Name>
                  Steuert einen vorhandenen Dienst. status kennt zusätzlich --json.

            ÜBERWACHUNG
              easyservice checkmk
                  Checkmk-Local-Check: eine Zeile je überwachtem Dienst, mit Perfdata.

              easyservice prometheus [--output <Datei>]
                  Prometheus-Exposition. Mit --output für den Textfile-Collector
                  des node_exporter (die Datei wird atomar ersetzt).

              easyservice check <Name>
                  Nagios/Icinga-Plugin für einen Dienst.
                  Exit-Code 0 = OK, 1 = Warnung, 2 = kritisch, 3 = unbekannt.

              easyservice json
                  Vollständiger Zustand aller überwachten Dienste als JSON.

              easyservice zabbix-discovery
                  Low-Level-Discovery-Liste für Zabbix-Templates.

            Verwalten benötigt Administratorrechte; die Überwachungsbefehle lesen nur.
            """);
        return code;
    }

    // ------------------------------------------------------------- verwalten ---

    private static int List(string[] args)
    {
        if (args.Contains("--json")) return Json();

        var services = ServiceRegistry.EnumerateServices()
                                      .OrderByDescending(s => s.ManagedByEasyService)
                                      .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"{"",-3}{"NAME",-36}{"STATUS",-18}{"START",-24}ANZEIGENAME");
        foreach (var s in services)
            Console.WriteLine($"{(s.ManagedByEasyService ? "ES" : ""),-3}{Trim(s.Name, 35),-36}{s.StateText,-18}{s.StartupText,-24}{s.DisplayName}");
        return 0;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static int Install(string[] args)
    {
        if (args.Length < 3) return Usage(2, "install benötigt <Name> und <Programm>.");

        var config = new ServiceConfig
        {
            ServiceName = args[1],
            DisplayName = args[1],
            Application = Path.GetFullPath(args[2]),
            AppParameters = args.Length > 3 ? string.Join(' ', args.Skip(3).Select(Quote)) : "",
        };
        config.AppDirectory = Path.GetDirectoryName(config.Application) ?? "";
        config.ApplyDefaultLogPaths();

        var problems = config.Validate(isNew: true).ToList();
        if (problems.Count > 0)
        {
            foreach (var p in problems) Console.Error.WriteLine("Fehler: " + p);
            return 2;
        }

        ServiceRegistry.Install(config);
        Console.WriteLine($"Dienst \"{config.ServiceName}\" wurde angelegt.");
        Console.WriteLine($"Protokolle: {config.StdoutPath}");
        return 0;
    }

    private static string Quote(string a) => a.Contains(' ') && !a.StartsWith('"') ? $"\"{a}\"" : a;

    private static int Remove(string[] args)
    {
        if (args.Length < 2) return Usage(2, "remove benötigt <Name>.");
        var name = args[1];
        if (!ServiceRegistry.Exists(name))
        {
            Console.Error.WriteLine($"Fehler: Der Dienst \"{name}\" existiert nicht.");
            return 2;
        }
        ServiceRegistry.Remove(name);
        Console.WriteLine($"Dienst \"{name}\" wurde entfernt.");
        return 0;
    }

    private static int Simple(string[] args, Action<string> action, string pastTense)
    {
        if (args.Length < 2) return Usage(2, $"{args[0]} benötigt <Name>.");
        action(args[1]);
        Console.WriteLine($"Dienst \"{args[1]}\" wurde {pastTense}.");
        return 0;
    }

    private static int Status(string[] args)
    {
        if (args.Length < 2) return Usage(2, "status benötigt <Name>.");
        var name = args[1];

        if (args.Contains("--json"))
        {
            var single = Monitoring.Check(name);
            Console.WriteLine(MonitoringOutput.Json(single is null ? Array.Empty<CheckResult>() : new[] { single }));
            return single?.Status == CheckStatus.Ok ? 0 : 3;
        }

        var info = ServiceRegistry.Query(name);
        if (info is null)
        {
            Console.Error.WriteLine($"Fehler: Der Dienst \"{name}\" existiert nicht.");
            return 2;
        }

        Console.WriteLine($"Name        : {info.Name}");
        Console.WriteLine($"Anzeigename : {info.DisplayName}");
        Console.WriteLine($"Status      : {info.StateText}");
        Console.WriteLine($"Starttyp    : {info.StartupText}");
        Console.WriteLine($"Konto       : {info.Account}");
        Console.WriteLine($"Verwaltet   : {(info.ManagedByEasyService ? "ja (EasyService)" : "nein")}");

        if (info.ManagedByEasyService && ServiceConfig.Load(info.Name) is { } c)
        {
            Console.WriteLine($"Programm    : {c.Application} {c.AppParameters}".TrimEnd());
            Console.WriteLine($"stdout      : {c.StdoutPath}");
            Console.WriteLine($"stderr      : {c.StderrPath}");

            if (ServiceState.Load(info.Name) is { } state)
            {
                Console.WriteLine();
                Console.WriteLine($"Anwendung   : {ServiceState.Describe(state.State)}"
                                  + (state.ApplicationPid > 0 ? $" (PID {state.ApplicationPid})" : ""));
                if (state.Uptime is { } up)
                    Console.WriteLine($"Laufzeit    : {ServiceState.FormatDuration(up)}");
                Console.WriteLine($"CPU / RAM   : {state.CpuPercent:0.##} % / {ServiceState.FormatBytes(state.WorkingSetBytes)}"
                                  + (state.ProcessCount > 0 ? $" ({state.ProcessCount} Prozesse)" : ""));
                Console.WriteLine($"Neustarts   : {state.RestartsLastHour} in der letzten Stunde, "
                                  + $"{state.RestartsLastDay} an 24 h, {state.RestartCount} gesamt");
                if (state.LastExitCode is { } code)
                    Console.WriteLine($"Letzter Exit: Code {code}"
                                      + (state.LastExitUtc is { } t ? $" am {t.ToLocalTime():yyyy-MM-dd HH:mm:ss}" : ""));
                if (!string.IsNullOrWhiteSpace(state.LastError))
                    Console.WriteLine($"Letzter Fehler: {state.LastError}");
            }
        }
        return info.IsRunning ? 0 : 3;
    }

    // ------------------------------------------------------------ überwachung ---

    private static int Checkmk()
    {
        Console.Write(MonitoringOutput.Checkmk(Monitoring.CheckAll()));
        return 0;
    }

    private static int Prometheus(string[] args)
    {
        var text = MonitoringOutput.Prometheus(Monitoring.CheckAll());

        var index = Array.FindIndex(args, a => a.Equals("--output", StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            Console.Write(text);
            return 0;
        }

        if (index + 1 >= args.Length) return Usage(2, "--output benötigt einen Dateinamen.");

        // node_exporter may read the file at any moment, so replace it atomically.
        var path = args[index + 1];
        var temp = path + ".tmp";
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
        Console.WriteLine($"{Monitoring.CheckAll().Count} Dienste nach {path} geschrieben.");
        return 0;
    }

    private static int Check(string[] args)
    {
        if (args.Length < 2) return Usage(2, "check benötigt <Name>.");

        var result = Monitoring.Check(args[1]);
        if (result is null)
        {
            Console.WriteLine($"EASYSERVICE UNBEKANNT - Für \"{args[1]}\" ist die Überwachung deaktiviert.");
            return (int)CheckStatus.Unknown;
        }

        Console.WriteLine(MonitoringOutput.Nagios(result));
        return (int)result.Status;
    }

    private static int Json()
    {
        Console.WriteLine(MonitoringOutput.Json(Monitoring.CheckAll()));
        return 0;
    }

    private static int ZabbixDiscovery()
    {
        Console.WriteLine(MonitoringOutput.ZabbixDiscovery(Monitoring.CheckAll()));
        return 0;
    }
}
