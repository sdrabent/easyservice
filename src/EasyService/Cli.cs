using System.Reflection;

using EasyService.Core;

using EasyService.Resources;

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

        var command = args[0].ToLowerInvariant();

        // Reading is allowed for everyone; writing is not. Saying so before the call keeps
        // the failure readable instead of "access denied" out of some P/Invoke.
        if (NeedsElevation(command) && !Elevation.IsElevated)
        {
            Console.Error.WriteLine(S.Cli_Err_NeedsAdmin(command));
            return Elevation.ExitCodeRequired;
        }

        try
        {
            return command switch
            {
                "list" => List(args),
                "install" => Install(args),
                "remove" or "uninstall" => Remove(args),
                "start" => Simple(args, n => ServiceRegistry.Start(n, TimeSpan.FromSeconds(60)), S.Cli_Started),
                "stop" => Simple(args, n => ServiceRegistry.Stop(n, TimeSpan.FromSeconds(60)), S.Cli_Stopped),
                "restart" => Simple(args, n => ServiceRegistry.Restart(n, TimeSpan.FromSeconds(60)), S.Cli_Restarted),
                "status" => Status(args),
                "export" => Export(args),
                "import" => Import(args),

                // monitoring integrations
                "checkmk" => Checkmk(),
                "prometheus" or "metrics" => Prometheus(args),
                "check" => Check(args),
                "health" => Health(args),
                "json" => Json(),
                "zabbix-discovery" => ZabbixDiscovery(),

                "-h" or "--help" or "/?" or "help" => Usage(0),
                "-v" or "--version" or "version" => Version(),
                _ => Usage(2, S.Cli_UnknownCommand(args[0])),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(S.Cli_Err(e.Message));
            return 1;
        }
    }

    private static bool NeedsElevation(string command) =>
        command is "install" or "remove" or "uninstall" or "start" or "stop" or "restart" or "import";

    private static int Version()
    {
        // Die informationelle Version traegt den Commit ("1.4.2+a1b2c3d"); im Supportfall
        // ist das der Unterschied zwischen "1.4.2" und "welche 1.4.2".
        var assembly = typeof(Cli).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(version))
            version = assembly.GetName().Version?.ToString(3) ?? "1.0.0";

        Console.WriteLine($"easyservice {version}");
        return 0;
    }

    private static int Usage(int code, string? message = null)
    {
        if (message is not null) Console.Error.WriteLine(message);
        Console.WriteLine(S.Cli_Usage);
        return code;
    }

    // ------------------------------------------------------------- verwalten ---

    private static int List(string[] args)
    {
        if (args.Contains("--json")) return Json();

        var services = ServiceRegistry.EnumerateServices()
                                      .OrderByDescending(s => s.ManagedByEasyService)
                                      .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"{"",-3}{S.Cli_Hdr_Name,-36}{S.Cli_Hdr_Status,-18}{S.Cli_Hdr_Startup,-24}{S.Cli_Hdr_DisplayName}");
        foreach (var s in services)
            Console.WriteLine($"{(s.ManagedByEasyService ? "ES" : ""),-3}{Trim(s.Name, 35),-36}{s.StateText,-18}{s.StartupText,-24}{s.DisplayName}");
        return 0;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static int Install(string[] args)
    {
        if (args.Length < 3) return Usage(2, S.Cli_InstallNeedsArgs);

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
            foreach (var p in problems) Console.Error.WriteLine(S.Cli_Err(p));
            return 2;
        }

        ServiceRegistry.Install(config);
        Console.WriteLine(S.Cli_Installed(config.ServiceName));
        Console.WriteLine(S.Cli_Logs(config.StdoutPath));
        return 0;
    }

    private static string Quote(string a) => a.Contains(' ') && !a.StartsWith('"') ? $"\"{a}\"" : a;

    private static int Remove(string[] args)
    {
        if (args.Length < 2) return Usage(2, S.Cli_NeedsName("remove"));
        var name = args[1];
        if (!ServiceRegistry.Exists(name))
        {
            Console.Error.WriteLine(S.Cli_NotExists(name));
            return 2;
        }
        ServiceRegistry.Remove(name);
        Console.WriteLine(S.Cli_Removed(name));
        return 0;
    }

    private static int Simple(string[] args, Action<string> action, Func<object?, string> message)
    {
        if (args.Length < 2) return Usage(2, S.Cli_NeedsName(args[0]));
        action(args[1]);
        Console.WriteLine(message(args[1]));
        return 0;
    }

    private static int Status(string[] args)
    {
        if (args.Length < 2) return Usage(2, S.Cli_NeedsName("status"));
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
            Console.Error.WriteLine(S.Cli_NotExists(name));
            return 2;
        }

        Console.WriteLine(S.Cli_St_Name(info.Name));
        Console.WriteLine(S.Cli_St_DisplayName(info.DisplayName));
        Console.WriteLine(S.Cli_St_Status(info.StateText));
        Console.WriteLine(S.Cli_St_Startup(info.StartupText));
        Console.WriteLine(S.Cli_St_Account(info.Account));
        Console.WriteLine(S.Cli_St_Managed(info.ManagedByEasyService ? S.Cli_St_Yes : S.Cli_St_No));

        if (info.ManagedByEasyService && ServiceConfig.Load(info.Name) is { } c)
        {
            Console.WriteLine(S.Cli_St_Program($"{c.Application} {c.AppParameters}".TrimEnd()));
            Console.WriteLine(S.Cli_St_Stdout(c.StdoutPath));
            Console.WriteLine(S.Cli_St_Stderr(c.StderrPath));

            if (ServiceState.Load(info.Name) is { } state)
            {
                Console.WriteLine();
                Console.WriteLine(S.Cli_St_Application(ServiceState.Describe(state.State)
                    + (state.ApplicationPid > 0 ? $" ({S.Mon_Pid(state.ApplicationPid)})" : "")));
                if (state.Uptime is { } up)
                    Console.WriteLine(S.Cli_St_Uptime(ServiceState.FormatDuration(up)));
                Console.WriteLine(S.Cli_St_CpuRam($"{state.CpuPercent:0.##}",
                    ServiceState.FormatBytes(state.WorkingSetBytes))
                    + (state.ProcessCount > 0 ? S.Cli_St_Processes(state.ProcessCount) : ""));
                Console.WriteLine(S.Cli_St_Restarts(state.RestartsLastHour, state.RestartsLastDay, state.RestartCount));
                if (state.LastExitCode is { } code)
                    Console.WriteLine(S.Cli_St_LastExit(code)
                        + (state.LastExitUtc is { } t ? S.Cli_St_LastExitAt($"{t.ToLocalTime():yyyy-MM-dd HH:mm:ss}") : ""));
                if (!string.IsNullOrWhiteSpace(state.LastError))
                    Console.WriteLine(S.Cli_St_LastError(state.LastError));
            }
        }
        return info.IsRunning ? 0 : 3;
    }

    // ------------------------------------------------ Konfiguration als Datei ---

    private static int Export(string[] args)
    {
        var all = args.Contains("--all");
        var name = args.Length >= 2 && !args[1].StartsWith('-') ? args[1] : null;
        if (!all && name is null) return Usage(2, S.Cli_ExportNeedsName);

        string json;
        int count;

        if (all)
        {
            var configs = ServiceRegistry.EnumerateServices()
                                         .Where(s => s.ManagedByEasyService)
                                         .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                                         .Select(s => ServiceRegistry.LoadComplete(s.Name))
                                         .Where(c => c is not null)
                                         .Select(c => c!)
                                         .ToList();
            if (configs.Count == 0)
            {
                Console.Error.WriteLine(S.Cli_NothingToExport);
                return 2;
            }
            json = ConfigTransfer.ExportMany(configs);
            count = configs.Count;
        }
        else
        {
            var config = ServiceRegistry.LoadComplete(name!);
            if (config is null)
            {
                Console.Error.WriteLine(S.Cli_NotExists(name!));
                return 2;
            }
            json = ConfigTransfer.Export(config);
            count = 1;
        }

        var output = ValueAfter(args, "--output");
        if (output is null)
        {
            Console.WriteLine(json);
            return 0;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(output, json, new System.Text.UTF8Encoding(false));
        Console.WriteLine(all ? S.Cfg_ExportedMany(count, output) : S.Cfg_Exported(output));
        return 0;
    }

    private static int Import(string[] args)
    {
        if (args.Length < 2 || args[1].StartsWith('-')) return Usage(2, S.Cli_ImportNeedsFile);

        var path = args[1];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine(S.Cli_FileNotFound(path));
            return 2;
        }

        List<ServiceConfig> configs;
        try
        {
            configs = ConfigTransfer.Import(File.ReadAllText(path));
        }
        catch (ConfigTransfer.TransferException e)
        {
            Console.Error.WriteLine(S.Cli_Err(e.Message));
            return 2;
        }

        var password = ConfigTransfer.PasswordFromEnvironment();
        var start = args.Contains("--start");
        var failed = false;

        foreach (var config in configs)
        {
            var existing = ServiceRegistry.Exists(config.ServiceName);
            if (existing && !ServiceRegistry.IsManaged(config.ServiceName))
            {
                Console.Error.WriteLine(S.Cfg_Err_Foreign(config.ServiceName));
                failed = true;
                continue;
            }

            // Beim Anlegen braucht ein Konto zwingend ein Kennwort; beim Aktualisieren
            // behaelt der SCM das gespeicherte, wenn keines mitkommt.
            if (config.Logon == LogonType.Account && password is null && !existing)
            {
                Console.Error.WriteLine(S.Cfg_Err_NeedsPassword(config.AccountName, ConfigTransfer.PasswordVariable));
                failed = true;
                continue;
            }
            config.Password = password ?? "";

            var problems = config.Validate(isNew: false).ToList();
            if (problems.Count > 0)
            {
                foreach (var problem in problems) Console.Error.WriteLine(S.Cli_Err(problem));
                failed = true;
                continue;
            }

            try
            {
                if (existing)
                {
                    ServiceRegistry.Update(config);
                    Console.WriteLine(S.Cfg_Imported_Updated(config.ServiceName));
                    if (ServiceRegistry.Query(config.ServiceName)?.IsRunning == true)
                        Console.WriteLine(S.Cfg_Import_Restart);
                }
                else
                {
                    ServiceRegistry.Install(config);
                    Console.WriteLine(S.Cfg_Imported_Created(config.ServiceName));
                    if (start) ServiceRegistry.Start(config.ServiceName, TimeSpan.FromSeconds(60));
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(S.Cli_Err(e.Message));
                failed = true;
            }
        }

        return failed ? 1 : 0;
    }

    private static string? ValueAfter(string[] args, string option)
    {
        var index = Array.FindIndex(args, a => a.Equals(option, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    // ------------------------------------------------------------ überwachung ---

    /// <summary>
    /// Runs the configured check once, right now. This is how an administrator finds out
    /// whether the check they just typed in actually works, without waiting for the interval
    /// and then reading the event log.
    /// </summary>
    private static int Health(string[] args)
    {
        if (args.Length < 2) return Usage(2, S.Cli_NeedsName("health"));

        var config = ServiceConfig.Load(args[1]);
        if (config is null)
        {
            Console.Error.WriteLine(S.Cli_NotExists(args[1]));
            return 2;
        }

        if (config.HealthType == HealthCheckType.None)
        {
            Console.WriteLine(S.Cli_Health_None(config.ServiceName));
            return 3;
        }

        var result = HealthProbe.Run(config);
        var milliseconds = (int)result.Duration.TotalMilliseconds;

        if (result.Healthy)
        {
            Console.WriteLine(S.Cli_Health_Ok(result.Detail, milliseconds));
            return 0;
        }

        Console.Error.WriteLine(S.Cli_Health_Failed(result.Detail, milliseconds));
        return 2;
    }

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

        if (index + 1 >= args.Length) return Usage(2, S.Cli_OutputNeedsFile);

        // node_exporter may read the file at any moment, so replace it atomically.
        var path = args[index + 1];
        var temp = path + ".tmp";
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
        Console.WriteLine(S.Cli_MetricsWritten(Monitoring.CheckAll().Count, path));
        return 0;
    }

    private static int Check(string[] args)
    {
        if (args.Length < 2) return Usage(2, S.Cli_NeedsName("check"));

        var result = Monitoring.Check(args[1]);
        if (result is null)
        {
            Console.WriteLine(S.Cli_MonitoringOff(args[1]));
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
