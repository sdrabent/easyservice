using EasyService.Core;
using EasyService.Resources;

namespace EasyService.Tests;

/// <summary>
/// Tests for the file format services are rolled out with. The property that matters most
/// is that a definition survives the trip unchanged - a rollout that quietly drops a
/// setting is worse than one that fails.
/// </summary>
internal static class TransferTests
{
    public static IEnumerable<(string Name, Action Test)> All()
    {
        yield return ("Konfiguration übersteht Export und Import unverändert", RoundTripIsLossless);
        yield return ("Das Kennwort landet nie in der Datei", PasswordNeverLeaves);
        yield return ("Mehrere Dienste in einer Datei", ArrayImports);
        yield return ("Unbrauchbare Dateien werden abgelehnt", BadInputIsRejected);
        yield return ("Aufzählungen stehen als Text in der Datei", EnumsAreReadable);
        yield return ("Export und Import hängen in der Werkzeugleiste", MenuIsWiredUp);
    }

    /// <summary>
    /// The dropdown is built in code, so a renamed handler or a missing resource key only
    /// shows up when somebody opens the window. This builds it once and looks inside.
    /// </summary>
    private static void MenuIsWiredUp()
    {
        using var form = new Gui.MainForm();

        var toolbar = Descend(form).OfType<ToolStrip>().FirstOrDefault();
        Assert(toolbar is not null, "keine Werkzeugleiste gefunden");

        var menu = toolbar!.Items.OfType<ToolStripDropDownButton>()
                                 .FirstOrDefault(i => i.Text == S.Main_Menu_Config);
        Assert(menu is not null, $"kein Menüpunkt \"{S.Main_Menu_Config}\" in der Werkzeugleiste");

        var entries = menu!.DropDownItems.OfType<ToolStripMenuItem>().Select(i => i.Text).ToList();
        foreach (var expected in new[] { S.Main_Btn_Export, S.Main_Btn_ExportAll, S.Main_Btn_Import })
            Assert(entries.Contains(expected), $"Eintrag fehlt: {expected}");
    }

    private static IEnumerable<Control> Descend(Control control) =>
        new[] { control }.Concat(control.Controls.Cast<Control>().SelectMany(Descend));

    private static ServiceConfig Sample() => new()
    {
        ServiceName = "RollOutProbe",
        DisplayName = "Roll Out Probe",
        Description = "Beschreibung mit Komma, \"Anführungszeichen\" und Umlauten äöü",
        Startup = StartupType.AutomaticDelayed,
        Application = @"C:\apps\daemon.exe",
        AppDirectory = @"C:\apps",
        AppParameters = "--config app.yml --verbose",
        Priority = ProcessPriority.BelowNormal,
        AffinityMask = 0b1010,
        StartupDelayMs = 2500,
        Logon = LogonType.Account,
        AccountName = @"DOMAIN\svc_daemon",
        Password = "streng-geheim",
        InteractWithDesktop = false,
        Dependencies = new List<string> { "EventLog", "Tcpip" },
        Environment = new List<string> { "ASPNETCORE_ENVIRONMENT=Production", "TZ=Europe/Berlin" },
        ReplaceEnvironment = true,
        StdoutPath = @"C:\logs\daemon-out.log",
        StderrPath = @"C:\logs\daemon-err.log",
        AppendOutput = false,
        TimestampLines = true,
        RotateFiles = true,
        RotateBytes = 50L * 1024 * 1024,
        RotateSeconds = 86400,
        RotateKeep = 14,
        LogServiceEvents = true,
        DefaultExitAction = ExitAction.Restart,
        ExitActions = new Dictionary<uint, ExitAction> { [0] = ExitAction.Stop, [3] = ExitAction.Ignore },
        RestartDelayMs = 2000,
        ThrottleMs = 15000,
        MonitoringEnabled = true,
        WarnCpuPercent = 70,
        CritCpuPercent = 90,
        WarnMemoryMb = 512,
        CritMemoryMb = 1024,
        WarnRestartsPerHour = 2,
        CritRestartsPerHour = 6,
        HistoryDays = 60,
        StopUseConsole = false,
        StopConsoleMs = 900,
        StopUseWindow = true,
        StopWindowMs = 3000,
        StopUseThreads = false,
        StopThreadsMs = 700,
        StopUseTerminate = true,
        KillProcessTree = false,
    };

    private static void RoundTripIsLossless()
    {
        var original = Sample();
        var json = ConfigTransfer.Export(original);
        var back = ConfigTransfer.Import(json).Single();

        // Feld für Feld statt nur "sieht ähnlich aus": ein stillschweigend verlorenes
        // Schwellwert-Feld faellt sonst erst beim Rollout auf.
        Assert(back.ServiceName == original.ServiceName, "Dienstname");
        Assert(back.DisplayName == original.DisplayName, "Anzeigename");
        Assert(back.Description == original.Description, $"Beschreibung: {back.Description}");
        Assert(back.Startup == original.Startup, "Starttyp");
        Assert(back.Application == original.Application, "Programm");
        Assert(back.AppDirectory == original.AppDirectory, "Startverzeichnis");
        Assert(back.AppParameters == original.AppParameters, "Argumente");
        Assert(back.Priority == original.Priority, "Priorität");
        Assert(back.AffinityMask == original.AffinityMask, "Affinität");
        Assert(back.StartupDelayMs == original.StartupDelayMs, "Startverzögerung");
        Assert(back.Logon == original.Logon, "Anmeldeart");
        Assert(back.AccountName == original.AccountName, "Kontoname");
        Assert(back.Dependencies.SequenceEqual(original.Dependencies), "Abhängigkeiten");
        Assert(back.Environment.SequenceEqual(original.Environment), "Umgebungsvariablen");
        Assert(back.ReplaceEnvironment == original.ReplaceEnvironment, "Umgebung ersetzen");
        Assert(back.StdoutPath == original.StdoutPath, "stdout-Pfad");
        Assert(back.StderrPath == original.StderrPath, "stderr-Pfad");
        Assert(back.AppendOutput == original.AppendOutput, "Anhängen");
        Assert(back.TimestampLines == original.TimestampLines, "Zeitstempel");
        Assert(back.RotateBytes == original.RotateBytes, "Rotationsgröße");
        Assert(back.RotateSeconds == original.RotateSeconds, "Rotationsintervall");
        Assert(back.RotateKeep == original.RotateKeep, "Archivanzahl");
        Assert(back.DefaultExitAction == original.DefaultExitAction, "Standardaktion");
        Assert(back.ExitActions.Count == 2 && back.ExitActions[0] == ExitAction.Stop
               && back.ExitActions[3] == ExitAction.Ignore, "Exit-Code-Aktionen");
        Assert(back.RestartDelayMs == original.RestartDelayMs, "Neustartverzögerung");
        Assert(back.ThrottleMs == original.ThrottleMs, "Throttle-Fenster");
        Assert(back.WarnCpuPercent == original.WarnCpuPercent, "CPU-Warnschwelle");
        Assert(back.CritCpuPercent == original.CritCpuPercent, "CPU-Kritischschwelle");
        Assert(back.WarnMemoryMb == original.WarnMemoryMb, "Speicher-Warnschwelle");
        Assert(back.CritMemoryMb == original.CritMemoryMb, "Speicher-Kritischschwelle");
        Assert(back.WarnRestartsPerHour == original.WarnRestartsPerHour, "Neustart-Warnschwelle");
        Assert(back.CritRestartsPerHour == original.CritRestartsPerHour, "Neustart-Kritischschwelle");
        Assert(back.HistoryDays == original.HistoryDays, "Verlaufsaufbewahrung");
        Assert(back.StopUseConsole == original.StopUseConsole, "Strg+C-Stufe");
        Assert(back.StopConsoleMs == original.StopConsoleMs, "Strg+C-Wartezeit");
        Assert(back.StopUseWindow == original.StopUseWindow, "WM_CLOSE-Stufe");
        Assert(back.StopUseThreads == original.StopUseThreads, "WM_QUIT-Stufe");
        Assert(back.StopUseTerminate == original.StopUseTerminate, "Hartes Beenden");
        Assert(back.KillProcessTree == original.KillProcessTree, "Prozessbaum beenden");

        // Zweimal exportieren muss byteweise dasselbe ergeben, sonst rauscht jeder
        // Vergleich gegen eine Referenzdatei.
        Assert(ConfigTransfer.Export(back) == json, "zweiter Export weicht ab");
    }

    private static void PasswordNeverLeaves()
    {
        var json = ConfigTransfer.Export(Sample());
        Assert(!json.Contains("streng-geheim"), "das Kennwort steht in der exportierten Datei");
        Assert(!json.Contains("assword", StringComparison.OrdinalIgnoreCase),
            "die Datei enthält überhaupt ein Kennwortfeld");

        // Der Kontoname darf mit, sonst weiss der Import nicht, wofür er ein Kennwort braucht.
        Assert(json.Contains("svc_daemon"), "der Kontoname fehlt");

        var back = ConfigTransfer.Import(json).Single();
        Assert(string.IsNullOrEmpty(back.Password), "nach dem Import ist ein Kennwort gesetzt");
    }

    private static void ArrayImports()
    {
        var a = Sample();
        var b = Sample();
        b.ServiceName = "RollOutProbe2";

        var json = ConfigTransfer.ExportMany(new[] { a, b });
        var back = ConfigTransfer.Import(json);

        Assert(back.Count == 2, $"erwartet: 2 Definitionen, gelesen: {back.Count}");
        Assert(back[0].ServiceName == "RollOutProbe" && back[1].ServiceName == "RollOutProbe2",
            "die Reihenfolge oder die Namen stimmen nicht");
    }

    private static void BadInputIsRejected()
    {
        Reject("{ kein json", "kaputtes JSON");
        Reject("{ \"foo\": 1 }", "JSON ohne Formatkennung");
        Reject("[]", "leeres Array");
        Reject("42", "Zahl statt Objekt");

        // Eine Datei aus der Zukunft ist gefährlicher als eine kaputte: sie sieht
        // brauchbar aus, koennte aber Felder enthalten, die wir stillschweigend verwerfen.
        Reject("{ \"easyservice\": 999, \"serviceName\": \"X\" }", "neueres Format");

        // Formatkennung vorhanden, aber kein Dienstname
        Reject("{ \"easyservice\": 1, \"serviceName\": \"  \" }", "leerer Dienstname");
    }

    private static void Reject(string json, string what)
    {
        try
        {
            ConfigTransfer.Import(json);
            throw new Exception($"{what} wurde angenommen statt abgelehnt");
        }
        catch (ConfigTransfer.TransferException)
        {
            // so soll es sein
        }
    }

    private static void EnumsAreReadable()
    {
        var json = ConfigTransfer.Export(Sample());

        // Der ganze Zweck des Formats ist, dass ein Admin es gegen eine Referenzdatei
        // diffen und das Ergebnis lesen kann.
        Assert(json.Contains("\"AutomaticDelayed\""), $"Starttyp steht nicht als Text in der Datei");
        Assert(json.Contains("\"BelowNormal\""), "Priorität steht nicht als Text in der Datei");
        Assert(json.Contains("\"Account\""), "Anmeldeart steht nicht als Text in der Datei");
        Assert(json.Contains("\"Stop\"") && json.Contains("\"Ignore\""),
            "Exit-Code-Aktionen stehen nicht als Text in der Datei");
        Assert(!System.Text.RegularExpressions.Regex.IsMatch(json, "\"startup\":\\s*\\d"),
            "der Starttyp steht als Zahl in der Datei");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
