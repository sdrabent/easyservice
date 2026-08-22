using EasyService.Core;
using EasyService.Gui;

using EasyService.Resources;

namespace EasyService;

internal static class Program
{
    /// <summary>Guards against an endless relaunch loop if the elevated start still is not elevated.</summary>
    private const string ElevatedMarker = "--elevated";

    [STAThread]
    private static int Main(string[] args)
    {
        // The language has to be settled before the first string is read - the supervisor
        // writes its log in it just as much as the GUI shows its labels in it.
        Localization.Initialize();

        // Mode 1: started by the Service Control Manager as "easyservice.exe run <name>".
        if (args.Length >= 2 && args[0].Equals("run", StringComparison.OrdinalIgnoreCase))
        {
            // A service has nobody to write to. Give the console back before anything else
            // happens, so session 0 does not keep one around for the life of the service.
            LeaveConsole();
            return ServiceHost.Run(args[1]);
        }

        // Mode 2: command line helpers, useful for scripting and CI.
        if (args.Length > 0 && !args[0].Equals("gui", StringComparison.OrdinalIgnoreCase))
            return Cli.Execute(args);

        // Mode 3: the GUI. First order of business is getting rid of the console that comes
        // with being a console program - see LeaveConsole.
        LeaveConsole();

        // Everything the window offers - create, change, start, remove - needs elevation, so
        // it asks for it once up front instead of failing per button.
        if (!Elevation.IsElevated && !args.Contains(ElevatedMarker, StringComparer.OrdinalIgnoreCase))
        {
            // A dismissed UAC prompt is a decision, not an error, so this ends quietly either way.
            Elevation.RelaunchAsAdmin((args.Length == 0 ? new[] { "gui" } : args).Append(ElevatedMarker));
            return 0;
        }

        ApplicationConfiguration.Initialize();

        // Folgt der Windows-Einstellung "App-Modus": hell oder dunkel.
        try { Application.SetColorMode(SystemColorMode.System); }
        catch (Exception) { /* aeltere Runtime: dann eben klassisch hell */ }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Ui.ShowError(null, S.Common_UnexpectedError, e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Ui.ShowError(null, S.Common_UnexpectedError,
                e.ExceptionObject as Exception ?? new Exception(S.Common_UnexpectedError));

        EventLogSink.EnsureSource();

        // "gui --new" springt direkt in die Schnelleinrichtung, "gui --history <Dienst>"
        // direkt in den Verlauf - praktisch für Verknüpfungen und für den Link in einer
        // Alarmmail des Monitorings.
        var openQuickAdd = args.Any(a => a.Equals("--new", StringComparison.OrdinalIgnoreCase));
        var historyIndex = Array.FindIndex(args, a => a.Equals("--history", StringComparison.OrdinalIgnoreCase));
        var openHistory = historyIndex >= 0 && historyIndex + 1 < args.Length ? args[historyIndex + 1] : null;

        var startArg = openHistory ?? (args.Length >= 2 && !args[1].StartsWith('-') ? args[1] : null);
        Application.Run(new MainForm(startArg, openQuickAdd, openHistory));
        return 0;
    }

    /// <summary>
    /// Hands back the console.
    ///
    /// EasyService is built as a console program on purpose: a Windows subsystem program is
    /// one that no shell waits for, which means %ERRORLEVEL% and $LASTEXITCODE never see its
    /// result and a script cannot tell whether the service was really created. The price is
    /// that Windows gives every start a console, including the ones that only want the
    /// window - so those give it back here.
    ///
    /// Hiding only happens when the console belongs to us alone. Started from an open command
    /// prompt, the window on screen is the administrator's own, and hiding that would be a
    /// nasty surprise; detaching from it is enough.
    /// </summary>
    internal static void LeaveConsole()
    {
        try
        {
            var window = Native.GetConsoleWindow();
            if (window == IntPtr.Zero) return;

            var processes = new uint[4];
            if (Native.GetConsoleProcessList(processes, (uint)processes.Length) == 1)
                Native.ShowWindow(window, Native.SW_HIDE_WINDOW);

            Native.FreeConsole();
        }
        catch (Exception)
        {
            // Ohne Konsole zu leben ist kein Grund, nicht zu starten.
        }
    }

}
