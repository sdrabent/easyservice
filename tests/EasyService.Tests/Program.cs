using System.Diagnostics;
using EasyService.Core;

namespace EasyService.Tests;

/// <summary>
/// Smoke tests for the supervisor half of EasyService. They drive ProcessSupervisor
/// directly, so they need no administrator rights and no installed service - which is
/// exactly the part that is hard to verify by clicking around in the GUI.
/// </summary>
internal static class Program
{
    private static int _failures;
    private static string _root = "";

    [STAThread]
    private static int Main()
    {
        // Die Tests auf Englisch festnageln: sonst haengen Zusicherungen an Meldungstexten
        // von der Sprache der Maschine ab und laufen lokal durch, auf dem CI-Runner aber nicht.
        Localization.Apply("en");

        _root = Path.Combine(Path.GetTempPath(), "easyservice-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        Console.WriteLine($"Arbeitsverzeichnis: {_root}");
        Console.WriteLine();

        try
        {
            Run("Ausgabe von stdout und stderr wird protokolliert", OutputIsCaptured);
            Run("Beendete Anwendung wird neu gestartet", RestartPolicyRelaunches);
            Run("Exit-Code-Aktion beendet den Dienst", ExitCodeActionStopsService);
            Run("Aktion \"Nichts tun\" startet nicht neu", IgnoreActionDoesNotRelaunch);
            Run("Protokolle werden rotiert und Archive begrenzt", RotationCreatesArchives);
            Run("Stoppen beendet die laufende Anwendung", StopTerminatesChild);
            Run("Zeitstempel werden pro Zeile ergänzt", TimestampsArePrefixed);
            Run("Umgebungsvariablen erreichen die Anwendung", EnvironmentIsPassedThrough);
            Run("Dienstliste kann gelesen werden", ServiceListIsReadable);
            Run("GUI-Dialoge lassen sich aufbauen", GuiFormsConstruct);

            foreach (var (name, test) in MonitoringTests.All(_root))
                Run(name, test);

            foreach (var (name, test) in HistoryTests.All(_root))
                Run(name, test);

            foreach (var (name, test) in TransferTests.All())
                Run(name, test);

            foreach (var (name, test) in LockTests.All())
                Run(name, test);

            foreach (var (name, test) in GuiTests.All())
                Run(name, test);
        }
        finally
        {
            TryDeleteRoot();
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? "Alle Tests erfolgreich."
            : $"{_failures} Test(s) fehlgeschlagen.");
        return _failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ tests

    private static void OutputIsCaptured()
    {
        var c = NewConfig("capture");
        c.Application = Cmd;
        c.AppParameters = "/c \"echo hallo-stdout & echo hallo-stderr 1>&2\"";
        c.DefaultExitAction = ExitAction.Stop;

        RunSupervisor(c, TimeSpan.FromSeconds(20));

        AssertFileContains(c.StdoutPath, "hallo-stdout");
        AssertFileContains(c.StderrPath, "hallo-stderr");
    }

    private static void RestartPolicyRelaunches()
    {
        var c = NewConfig("restart");
        var counter = Path.Combine(_root, "restart-counter.txt");
        c.Application = Cmd;
        c.AppParameters = $"/c \"echo start>>\"\"{counter}\"\"\"";
        c.DefaultExitAction = ExitAction.Restart;
        c.RestartDelayMs = 100;
        c.ThrottleMs = 0;                    // no back-off, we want fast relaunches

        RunSupervisorFor(c, TimeSpan.FromSeconds(4));

        var starts = File.Exists(counter) ? File.ReadAllLines(counter).Length : 0;
        Assert(starts >= 3, $"erwartet: mindestens 3 Starts, tatsächlich: {starts}");
    }

    private static void ExitCodeActionStopsService()
    {
        var c = NewConfig("exitcode");
        c.Application = Cmd;
        c.AppParameters = "/c \"exit /b 42\"";
        c.DefaultExitAction = ExitAction.Restart;
        c.ExitActions[42] = ExitAction.Stop;
        c.RestartDelayMs = 100;

        uint? reported = null;
        var stopped = RunSupervisor(c, TimeSpan.FromSeconds(20), code => reported = code);

        Assert(stopped, "der Supervisor hat sich nicht selbst beendet");
        Assert(reported == 42, $"erwarteter Exit-Code 42, gemeldet: {reported?.ToString() ?? "keiner"}");
    }

    private static void IgnoreActionDoesNotRelaunch()
    {
        var c = NewConfig("ignore");
        var counter = Path.Combine(_root, "ignore-counter.txt");
        c.Application = Cmd;
        c.AppParameters = $"/c \"echo start>>\"\"{counter}\"\"\"";
        c.DefaultExitAction = ExitAction.Ignore;

        RunSupervisorFor(c, TimeSpan.FromSeconds(3));

        var starts = File.Exists(counter) ? File.ReadAllLines(counter).Length : 0;
        Assert(starts == 1, $"erwartet: genau 1 Start, tatsächlich: {starts}");
    }

    private static void RotationCreatesArchives()
    {
        var c = NewConfig("rotate");
        c.Application = Cmd;
        c.AppParameters = "/c \"for /l %i in (1,1,4000) do @echo Dies ist eine Beispielzeile mit Nummer %i\"";
        c.DefaultExitAction = ExitAction.Stop;
        c.RotateFiles = true;
        c.RotateBytes = 16 * 1024;
        c.RotateKeep = 3;

        RunSupervisor(c, TimeSpan.FromSeconds(60));

        var files = LogWriter.FindLogFiles(c.StdoutPath);
        var archives = files.Count(f => !string.Equals(f, Path.GetFullPath(c.StdoutPath), StringComparison.OrdinalIgnoreCase));
        Assert(archives > 0, "es wurde kein Archiv angelegt");
        Assert(archives <= 3, $"erwartet: höchstens 3 Archive, tatsächlich: {archives}");
    }

    private static void StopTerminatesChild()
    {
        var c = NewConfig("stop");
        c.Application = Cmd;
        c.AppParameters = "/c \"ping -n 120 127.0.0.1 >nul\"";
        c.DefaultExitAction = ExitAction.Restart;
        c.StopConsoleMs = 500;
        c.StopWindowMs = 300;
        c.StopThreadsMs = 300;

        using var supervisor = new ProcessSupervisor(c);
        var task = Task.Run(supervisor.Run);

        uint pid = 0;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && pid == 0)
        {
            pid = supervisor.CurrentProcessId;
            if (pid == 0) Thread.Sleep(100);
        }
        Assert(pid != 0, "die Anwendung wurde nicht gestartet");

        supervisor.RequestStop();
        Assert(task.Wait(TimeSpan.FromSeconds(20)), "der Supervisor hat nicht rechtzeitig angehalten");

        var alive = true;
        for (var i = 0; i < 40 && alive; i++)
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                alive = !p.HasExited;
            }
            catch (ArgumentException)
            {
                alive = false;
            }
            if (alive) Thread.Sleep(100);
        }
        Assert(!alive, $"der Prozess {pid} läuft nach dem Stoppen noch");
    }

    private static void TimestampsArePrefixed()
    {
        var c = NewConfig("timestamp");
        c.Application = Cmd;
        c.AppParameters = "/c \"echo eine-zeile\"";
        c.DefaultExitAction = ExitAction.Stop;
        c.TimestampLines = true;

        RunSupervisor(c, TimeSpan.FromSeconds(20));

        var text = ReadAll(c.StdoutPath);
        var line = text.Split('\n').FirstOrDefault(l => l.Contains("eine-zeile")) ?? "";
        Assert(line.StartsWith('['), $"die Zeile trägt keinen Zeitstempel: \"{line.Trim()}\"");
    }

    private static void EnvironmentIsPassedThrough()
    {
        var c = NewConfig("environment");
        c.Application = Cmd;
        c.AppParameters = "/c \"echo WERT=%EASYSERVICE_TEST%\"";
        c.DefaultExitAction = ExitAction.Stop;
        c.Environment.Add("EASYSERVICE_TEST=funktioniert");

        RunSupervisor(c, TimeSpan.FromSeconds(20));

        AssertFileContains(c.StdoutPath, "WERT=funktioniert");
    }

    private static void ServiceListIsReadable()
    {
        var services = ServiceRegistry.EnumerateServices();
        Assert(services.Count > 10, $"erwartet: viele Dienste, gelesen: {services.Count}");
        Assert(services.Any(s => s.Name.Equals("EventLog", StringComparison.OrdinalIgnoreCase)),
            "der Standarddienst \"EventLog\" wurde nicht gefunden");
        Assert(services.All(s => s.StateText.Length > 0), "ein Dienst hat keinen lesbaren Status");
    }

    private static void GuiFormsConstruct()
    {
        var config = NewConfig("gui");
        config.Application = Cmd;
        config.AppParameters = "/c echo hallo";
        config.Description = "Testbeschreibung";
        config.Environment.Add("A=B");
        config.Dependencies.Add("EventLog");
        config.ExitActions[3] = ExitAction.Stop;
        config.Logon = LogonType.Account;
        config.AccountName = @".	estuser";
        config.AffinityMask = 1;

        using (var editor = new Gui.ServiceEditorForm(config, isNew: false))
            editor.CreateControl();

        using (var editor = new Gui.ServiceEditorForm(new ServiceConfig(), isNew: true))
            editor.CreateControl();

        using (var viewer = new Gui.LogViewerForm(config))
            viewer.CreateControl();

        using (var main = new Gui.MainForm())
            main.CreateControl();

        using (var quick = new Gui.QuickAddForm())
            quick.CreateControl();

        using (var quick = new Gui.QuickAddForm(Cmd))
            quick.CreateControl();

        using (var history = new Gui.HistoryForm(config))
            history.CreateControl();
    }

    // ------------------------------------------------------------- infrastructure

    private static string Cmd => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    private static ServiceConfig NewConfig(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return new ServiceConfig
        {
            ServiceName = "EasyServiceTest_" + name,
            AppDirectory = dir,
            StdoutPath = Path.Combine(dir, "stdout.log"),
            StderrPath = Path.Combine(dir, "stderr.log"),
            LogServiceEvents = true,
            RestartDelayMs = 100,
            ThrottleMs = 0,
        };
    }

    /// <summary>Runs the supervisor until it ends on its own. Returns true if it did.</summary>
    private static bool RunSupervisor(ServiceConfig c, TimeSpan timeout, Action<uint>? onStopRequested = null)
    {
        using var supervisor = new ProcessSupervisor(c);
        if (onStopRequested is not null) supervisor.StopServiceRequested += onStopRequested;

        var task = Task.Run(supervisor.Run);
        var finished = task.Wait(timeout);
        if (!finished)
        {
            supervisor.RequestStop();
            task.Wait(TimeSpan.FromSeconds(10));
        }
        return finished;
    }

    /// <summary>Lets the supervisor run for a while, then stops it.</summary>
    private static void RunSupervisorFor(ServiceConfig c, TimeSpan duration)
    {
        using var supervisor = new ProcessSupervisor(c);
        var task = Task.Run(supervisor.Run);
        Thread.Sleep(duration);
        supervisor.RequestStop();
        task.Wait(TimeSpan.FromSeconds(15));
    }

    private static string ReadAll(string path)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                return reader.ReadToEnd();
            }
            catch (FileNotFoundException)
            {
                Thread.Sleep(100);
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
        }
        return "";
    }

    private static void AssertFileContains(string path, string needle)
    {
        var text = ReadAll(path);
        Assert(text.Contains(needle, StringComparison.OrdinalIgnoreCase),
            $"\"{needle}\" fehlt in {Path.GetFileName(path)} (Inhalt: {Shorten(text)})");
    }

    private static string Shorten(string s)
    {
        s = s.ReplaceLineEndings(" | ").Trim();
        return s.Length <= 160 ? s : s[..160] + "...";
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Run(string name, Action test)
    {
        Console.Write($"  {name,-58}");
        var sw = Stopwatch.StartNew();
        try
        {
            test();
            Console.WriteLine($"OK   ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception e)
        {
            _failures++;
            Console.WriteLine($"FEHLER ({sw.ElapsedMilliseconds} ms)");
            Console.WriteLine($"      -> {e.Message}");
        }
    }

    private static void TryDeleteRoot()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(300);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(300);
            }
        }
    }
}
