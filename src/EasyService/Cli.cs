using EasyService.Core;

namespace EasyService;

/// <summary>
/// Optional command line front end. The GUI is the primary interface, but scripted
/// installs (deployment, CI, Ansible/Chocolatey packages) want a non-interactive path.
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
                "list" => List(),
                "install" => Install(args),
                "remove" or "uninstall" => Remove(args),
                "start" => Simple(args, n => ServiceRegistry.Start(n, TimeSpan.FromSeconds(60)), "gestartet"),
                "stop" => Simple(args, n => ServiceRegistry.Stop(n, TimeSpan.FromSeconds(60)), "beendet"),
                "restart" => Simple(args, n => ServiceRegistry.Restart(n, TimeSpan.FromSeconds(60)), "neu gestartet"),
                "status" => Status(args),
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

              easyservice list
                  Zeigt alle Dienste an; von EasyService verwaltete sind markiert.

              easyservice install <Name> <Programm> [Argumente...]
                  Legt einen neuen Dienst an (Autostart, Logging unter %ProgramData%\EasyService\logs).

              easyservice remove <Name> [--force]
                  Beendet und entfernt den Dienst.

              easyservice start|stop|restart|status <Name>
                  Steuert einen vorhandenen Dienst.

              easyservice gui [Name]
                  Startet die Oberfläche, optional direkt beim angegebenen Dienst.

            Alle Befehle benötigen erhöhte Rechte (Administrator).
            """);
        return code;
    }

    private static int List()
    {
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
        var info = ServiceRegistry.Query(args[1]);
        if (info is null)
        {
            Console.Error.WriteLine($"Fehler: Der Dienst \"{args[1]}\" existiert nicht.");
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
        }
        return info.IsRunning ? 0 : 3;
    }
}
