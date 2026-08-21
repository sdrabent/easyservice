using EasyService.Core;
using EasyService.Gui;

namespace EasyService;

internal static class Program
{
    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    [STAThread]
    private static int Main(string[] args)
    {
        // Mode 1: started by the Service Control Manager as "easyservice.exe run <name>".
        if (args.Length >= 2 && args[0].Equals("run", StringComparison.OrdinalIgnoreCase))
            return ServiceHost.Run(args[1]);

        // Mode 2: command line helpers, useful for scripting and CI.
        if (args.Length > 0 && !args[0].Equals("gui", StringComparison.OrdinalIgnoreCase))
            return Cli.Execute(args);

        // Mode 3: the GUI.
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Ui.ShowError(null, "Unerwarteter Fehler", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Ui.ShowError(null, "Unerwarteter Fehler", e.ExceptionObject as Exception ?? new Exception("Unbekannter Fehler"));

        EventLogSink.EnsureSource();

        var startArg = args.Length >= 2 ? args[1] : null;
        Application.Run(new MainForm(startArg));
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
