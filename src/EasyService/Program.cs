using EasyService.Core;
using EasyService.Gui;

using EasyService.Resources;

namespace EasyService;

internal static class Program
{
    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

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
            return ServiceHost.Run(args[1]);

        // Mode 2: command line helpers, useful for scripting and CI.
        if (args.Length > 0 && !args[0].Equals("gui", StringComparison.OrdinalIgnoreCase))
            return Cli.Execute(args);

        // Mode 3: the GUI. Everything it offers - create, change, start, remove - needs
        // elevation, so it asks for it once up front instead of failing per button.
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
    /// Makes the output of a command line run visible.
    ///
    /// This is a Windows subsystem program, so it starts without a console and its writes
    /// would go nowhere. AttachConsole borrows the calling shell's console - but only when
    /// there is nothing else to write to. If the caller redirected stdout to a file or a
    /// pipe, that handle is already the right one, and attaching would bend the standard
    /// handles back to the console and drop the redirection on the floor. That is what used
    /// to make "easyservice checkmk &gt; out.txt" produce an empty file.
    /// </summary>
    internal static void AttachConsole()
    {
        if (IsRedirected()) return;

        if (!Native.AttachConsole(ATTACH_PARENT_PROCESS)) return;
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);
        Console.SetError(stdout);
    }

    private static bool IsRedirected()
    {
        var handle = Native.GetStdHandle(Native.STD_OUTPUT_HANDLE);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;

        var type = Native.GetFileType(handle);
        return type is Native.FILE_TYPE_DISK or Native.FILE_TYPE_PIPE;
    }
}
