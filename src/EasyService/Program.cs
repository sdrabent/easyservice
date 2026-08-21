using EasyService.Core;
using EasyService.Gui;

using EasyService.Resources;

namespace EasyService;

internal static class Program
{
    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    [STAThread]
    private static int Main(string[] args)
    {
        // The language has to be settled before the first string is read - the supervisor
        // writes its log in it just as much as the GUI shows its labels in it.
        Localization.Initialize();

        // Mode 1: started by the Service Control Manager as "easyservice.exe run <name>".
        if (args.Length >= 2 && args[0].Equals("run", StringComparison.OrdinalIgnoreCase))
            return ServiceHost.Run(args[1]);

        // Mode 2: command line helpers, useful for scripting and CI.
        if (args.Length > 0 && !args[0].Equals("gui", StringComparison.OrdinalIgnoreCase))
            return Cli.Execute(args);

        // Mode 3: the GUI.
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

        // "gui --new" springt direkt in die Schnelleinrichtung - praktisch für eine
        // Desktop- oder Startmenü-Verknüpfung "Dienst hinzufügen".
        var openQuickAdd = args.Any(a => a.Equals("--new", StringComparison.OrdinalIgnoreCase));
        var startArg = args.Length >= 2 && !args[1].StartsWith('-') ? args[1] : null;
        Application.Run(new MainForm(startArg, openQuickAdd));
        return 0;
    }

    internal static void AttachConsole()
    {
        if (!Native.AttachConsole(ATTACH_PARENT_PROCESS)) return;
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);
        Console.SetError(stdout);
    }
}
