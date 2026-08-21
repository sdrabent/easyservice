using EasyService.Core;

namespace EasyService.Gui;

/// <summary>
/// The nssm-style property sheet: everything about one supervised application on
/// eight tabs, with sane defaults so the common case is "pick an exe and press OK".
/// </summary>
public sealed class ServiceEditorForm : Form
{
    public ServiceConfig Config { get; }
    public bool StartAfterSave => _startAfterSave.Checked;

    private readonly bool _isNew;

    // Anwendung
    private readonly TextBox _serviceName = new();
    private readonly TextBox _application;
    private readonly TextBox _appDirectory;
    private readonly TextBox _appParameters = new();

    // Details
    private readonly TextBox _displayName = new();
    private readonly TextBox _description = new();
    private readonly ComboBox _startup = Ui.Combo("Automatisch", "Automatisch (verzögerter Start)", "Manuell", "Deaktiviert");
    private readonly ComboBox _priority = Ui.Combo("Echtzeit", "Hoch", "Über normal", "Normal", "Unter normal", "Niedrig");
    private readonly CheckedListBox _affinity = new() { CheckOnClick = true, Height = 120, ColumnWidth = 90, MultiColumn = true };
    private readonly CheckBox _allProcessors = new() { Text = "Alle Prozessoren verwenden", Checked = true, AutoSize = true };
    private readonly NumericUpDown _startupDelay = Ui.Spin(0, 600_000, 0, 500);

    // Anmelden
    private readonly ComboBox _logon = Ui.Combo("Lokales Systemkonto", "Lokaler Dienst", "Netzwerkdienst", "Dieses Konto");
    private readonly TextBox _account = new();
    private readonly TextBox _password = new() { UseSystemPasswordChar = true };
    private readonly CheckBox _interact = new() { Text = "Datenaustausch zwischen Dienst und Desktop zulassen", AutoSize = true };

    // Abhängigkeiten / Umgebung
    private readonly TextBox _dependencies = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 150 };
    private readonly TextBox _environment = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 150 };
    private readonly CheckBox _replaceEnvironment = new() { Text = "Vorhandene Umgebung vollständig ersetzen", AutoSize = true };

    // Protokollierung
    private readonly TextBox _stdout;
    private readonly TextBox _stderr;
    private readonly CheckBox _append = new() { Text = "An vorhandene Dateien anhängen (statt beim Start zu leeren)", AutoSize = true };
    private readonly CheckBox _timestamp = new() { Text = "Jede Zeile mit Zeitstempel versehen", AutoSize = true };
    private readonly CheckBox _rotate = new() { Text = "Protokolle automatisch rotieren", AutoSize = true };
    private readonly NumericUpDown _rotateMb = Ui.Spin(1, 10_240, 10);
    private readonly NumericUpDown _rotateHours = Ui.Spin(0, 8760, 0);
    private readonly NumericUpDown _rotateKeep = Ui.Spin(0, 999, 10);
    private readonly CheckBox _logEvents = new() { Text = "Ereignisse von EasyService protokollieren (Start, Absturz, Neustart)", AutoSize = true, Checked = true };

    // Beenden-Aktionen
    private readonly ComboBox _defaultExit = Ui.Combo("Anwendung neu starten", "Nichts tun (Dienst bleibt aktiv)", "Dienst beenden");
    private readonly NumericUpDown _restartDelay = Ui.Spin(0, 3_600_000, 1000, 500);
    private readonly NumericUpDown _throttle = Ui.Spin(0, 3_600_000, 5000, 500);
    private readonly ListBox _exitCodes = new() { Height = 130 };
    private readonly NumericUpDown _exitCode = Ui.Spin(0, int.MaxValue, 0);
    private readonly ComboBox _exitAction = Ui.Combo("Neu starten", "Nichts tun", "Dienst beenden");

    // Herunterfahren
    private readonly CheckBox _stopConsole = new() { Text = "Strg+C senden (Konsolenanwendungen)", AutoSize = true, Checked = true };
    private readonly NumericUpDown _stopConsoleMs = Ui.Spin(0, 600_000, 1500, 250);
    private readonly CheckBox _stopWindow = new() { Text = "WM_CLOSE an Fenster senden", AutoSize = true, Checked = true };
    private readonly NumericUpDown _stopWindowMs = Ui.Spin(0, 600_000, 1500, 250);
    private readonly CheckBox _stopThreads = new() { Text = "WM_QUIT an Threads senden", AutoSize = true, Checked = true };
    private readonly NumericUpDown _stopThreadsMs = Ui.Spin(0, 600_000, 1500, 250);
    private readonly CheckBox _stopTerminate = new() { Text = "Prozess notfalls hart beenden", AutoSize = true, Checked = true };
    private readonly CheckBox _killTree = new() { Text = "Auch alle Kindprozesse beenden (Prozessbaum)", AutoSize = true, Checked = true };

    private readonly CheckBox _startAfterSave = new() { Text = "Dienst nach dem Anlegen starten", AutoSize = true, Checked = true };

    public ServiceEditorForm(ServiceConfig config, bool isNew)
    {
        Config = config;
        _isNew = isNew;

        Text = isNew ? "Neuen Dienst anlegen" : $"Dienst bearbeiten - {config.ServiceName}";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(720, 620);
        Size = new Size(780, 700);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Font = SystemFonts.MessageBoxFont ?? Font;

        (var appPanel, _application) = Ui.BrowseRow(folder: false);
        (var dirPanel, _appDirectory) = Ui.BrowseRow(folder: true);
        (var outPanel, _stdout) = Ui.BrowseRow(folder: false, "Protokolldateien (*.log;*.txt)|*.log;*.txt|Alle Dateien (*.*)|*.*");
        (var errPanel, _stderr) = Ui.BrowseRow(folder: false, "Protokolldateien (*.log;*.txt)|*.log;*.txt|Alle Dateien (*.*)|*.*");

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 6) };
        tabs.TabPages.Add(BuildApplicationTab(appPanel, dirPanel));
        tabs.TabPages.Add(BuildDetailsTab());
        tabs.TabPages.Add(BuildLogonTab());
        tabs.TabPages.Add(BuildDependenciesTab());
        tabs.TabPages.Add(BuildEnvironmentTab());
        tabs.TabPages.Add(BuildLoggingTab(outPanel, errPanel));
        tabs.TabPages.Add(BuildExitTab());
        tabs.TabPages.Add(BuildShutdownTab());

        var ok = new Button { Text = isNew ? "Dienst anlegen" : "Speichern", Width = 130, Height = 30, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "Abbrechen", Width = 100, Height = 30, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => OnSave();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 48,
            Padding = new Padding(12, 8, 12, 8),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        if (isNew)
        {
            _startAfterSave.Margin = new Padding(12, 8, 12, 0);
            buttons.Controls.Add(_startAfterSave);
        }

        Controls.Add(tabs);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        LoadFromConfig();
        WireUpEnabling();
    }

    // ------------------------------------------------------------------ tabs

    private TabPage BuildApplicationTab(Control appPanel, Control dirPanel)
    {
        var page = new TabPage("Anwendung");
        var p = Ui.FormPanel();

        Ui.AddRow(p, "Dienstname:", _serviceName);
        Ui.AddFullRow(p, Ui.Hint("Interner Name, unter dem Windows den Dienst führt. Nach dem Anlegen nicht mehr änderbar."));
        Ui.AddRow(p, "Programm:", appPanel);
        Ui.AddRow(p, "Startverzeichnis:", dirPanel);
        Ui.AddRow(p, "Argumente:", _appParameters);
        Ui.AddFullRow(p, Ui.Hint("Argumente werden unverändert an das Programm übergeben. " +
                                 "Pfade mit Leerzeichen bitte in Anführungszeichen setzen."));

        _application.TextChanged += (_, _) =>
        {
            if (!_isNew) return;
            if (_serviceName.Text.Length == 0 && _application.Text.Length > 0)
            {
                try { _serviceName.Text = Path.GetFileNameWithoutExtension(_application.Text); } catch { }
            }
            if (_appDirectory.Text.Length == 0)
            {
                try { _appDirectory.Text = Path.GetDirectoryName(_application.Text) ?? ""; } catch { }
            }
        };

        page.Controls.Add(p);
        return page;
    }

    private TabPage BuildDetailsTab()
    {
        var page = new TabPage("Details");
        var p = Ui.FormPanel();

        Ui.AddRow(p, "Anzeigename:", _displayName);
        Ui.AddRow(p, "Beschreibung:", _description);
        Ui.AddRow(p, "Starttyp:", _startup);
        Ui.AddRow(p, "Startverzögerung (ms):", _startupDelay);
        Ui.AddFullRow(p, Ui.Hint("Wartezeit, bevor die Anwendung nach dem Dienststart erstmals gestartet wird."));

        Ui.AddSpacer(p, "Prozess");
        Ui.AddRow(p, "Priorität:", _priority);
        Ui.AddFullRow(p, _allProcessors);
        Ui.AddRow(p, "Prozessoren:", _affinity);

        for (var i = 0; i < System.Environment.ProcessorCount && i < 64; i++)
            _affinity.Items.Add($"CPU {i}", true);
        _allProcessors.CheckedChanged += (_, _) => _affinity.Enabled = !_allProcessors.Checked;

        page.Controls.Add(p);
        return page;
    }

    private TabPage BuildLogonTab()
    {
        var page = new TabPage("Anmelden");
        var p = Ui.FormPanel();

        Ui.AddRow(p, "Anmelden als:", _logon);
        Ui.AddRow(p, "Konto:", _account);
        Ui.AddFullRow(p, Ui.Hint(@"Format: DOMÄNE\Benutzer oder .\Benutzer für lokale Konten. " +
                                 "EasyService vergibt dem Konto automatisch das Recht \"Als Dienst anmelden\"."));
        Ui.AddRow(p, "Kennwort:", _password);
        if (!_isNew)
            Ui.AddFullRow(p, Ui.Hint("Leer lassen, um das gespeicherte Kennwort beizubehalten."));
        Ui.AddSpacer(p);
        Ui.AddFullRow(p, _interact);
        Ui.AddFullRow(p, Ui.Hint("Nur mit dem lokalen Systemkonto möglich. Moderne Windows-Versionen isolieren " +
                                 "Dienste in Sitzung 0; Fenster der Anwendung sind für angemeldete Benutzer nicht sichtbar."));

        page.Controls.Add(p);
        return page;
    }

    private TabPage BuildDependenciesTab()
    {
        var page = new TabPage("Abhängigkeiten");
        var p = Ui.FormPanel();

        Ui.AddFullRow(p, Ui.Hint("Dienste, die vor diesem Dienst laufen müssen - ein Name pro Zeile. " +
                                 "Windows startet den Dienst erst, wenn alle genannten Dienste laufen."));
        Ui.AddFullRow(p, _dependencies);

        var pick = new Button { Text = "Dienste auswählen...", AutoSize = true };
        pick.Click += (_, _) =>
        {
            using var dlg = new ServicePickerDialog(_dependencies.Lines);
            if (dlg.ShowDialog(this) == DialogResult.OK)
                _dependencies.Lines = dlg.SelectedServices.ToArray();
        };
        Ui.AddFullRow(p, pick);

        page.Controls.Add(p);
        return page;
    }

    private TabPage BuildEnvironmentTab()
    {
        var page = new TabPage("Umgebung");
        var p = Ui.FormPanel();

        Ui.AddFullRow(p, Ui.Hint("Zusätzliche Umgebungsvariablen für die Anwendung - eine pro Zeile im Format NAME=WERT. " +
                                 "Bereits vorhandene Variablen wie %PATH% können darin verwendet werden."));
        Ui.AddFullRow(p, _environment);
        Ui.AddFullRow(p, _replaceEnvironment);
        Ui.AddFullRow(p, Ui.Hint("Achtung: Beim Ersetzen erhält die Anwendung ausschließlich die oben genannten Variablen. " +
                                 "Ohne PATH und SystemRoot starten viele Programme nicht."));

        page.Controls.Add(p);
        return page;
    }

    private TabPage BuildLoggingTab(Control outPanel, Control errPanel)
    {
        var page = new TabPage("Protokollierung");
        var p = Ui.FormPanel();

        Ui.AddFullRow(p, Ui.Hint("Alles, was die Anwendung auf stdout und stderr schreibt, landet in diesen Dateien. " +
                                 "Zeigen beide Felder auf dieselbe Datei, werden die Ströme zusammengeführt."));
        Ui.AddRow(p, "Ausgabe (stdout):", outPanel);
        Ui.AddRow(p, "Fehler (stderr):", errPanel);
        Ui.AddFullRow(p, _append);
        Ui.AddFullRow(p, _timestamp);

        Ui.AddSpacer(p, "Rotation");
        Ui.AddFullRow(p, _rotate);
        Ui.AddRow(p, "Rotieren ab (MB):", _rotateMb);
        Ui.AddRow(p, "Rotieren alle (Stunden):", _rotateHours);
        Ui.AddFullRow(p, Ui.Hint("0 Stunden = nur nach Größe rotieren."));
        Ui.AddRow(p, "Archive behalten:", _rotateKeep);
        Ui.AddFullRow(p, Ui.Hint("0 = alte Protokolle nie löschen."));

        Ui.AddSpacer(p, "Diagnose");
        Ui.AddFullRow(p, _logEvents);

        _rotate.CheckedChanged += (_, _) =>
            _rotateMb.Enabled = _rotateHours.Enabled = _rotateKeep.Enabled = _rotate.Checked;

        page.Controls.Add(p);
        return page;
    }

    private TabPage BuildExitTab()
    {
        var page = new TabPage("Beenden-Aktionen");
        var p = Ui.FormPanel();

        Ui.AddFullRow(p, Ui.Hint("Was soll passieren, wenn sich die Anwendung von selbst beendet?"));
        Ui.AddRow(p, "Standardaktion:", _defaultExit);
        Ui.AddRow(p, "Verzögerung (ms):", _restartDelay);
        Ui.AddRow(p, "Throttle-Fenster (ms):", _throttle);
        Ui.AddFullRow(p, Ui.Hint("Beendet sich die Anwendung schneller als das Throttle-Fenster, verdoppelt EasyService " +
                                 "die Wartezeit vor jedem weiteren Versuch (maximal 60 s). Das verhindert Neustart-Schleifen " +
                                 "bei einer dauerhaft fehlerhaften Konfiguration."));

        Ui.AddSpacer(p, "Aktionen für einzelne Exit-Codes");
        Ui.AddFullRow(p, _exitCodes);

        var row = new FlowLayoutPanel { Height = 34, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _exitCode.Width = 110;
        _exitAction.Width = 160;
        var add = new Button { Text = "Hinzufügen", AutoSize = true };
        var del = new Button { Text = "Entfernen", AutoSize = true };
        row.Controls.Add(new Label { Text = "Exit-Code:", AutoSize = true, Margin = new Padding(0, 8, 4, 0) });
        row.Controls.Add(_exitCode);
        row.Controls.Add(_exitAction);
        row.Controls.Add(add);
        row.Controls.Add(del);
        Ui.AddFullRow(p, row);
        Ui.AddFullRow(p, Ui.Hint("Beispiel: Exit-Code 0 auf \"Dienst beenden\" setzen, damit ein sauber beendetes " +
                                 "Programm nicht endlos neu gestartet wird."));

        add.Click += (_, _) =>
        {
            var code = (uint)_exitCode.Value;
            Config.ExitActions[code] = (ExitAction)_exitAction.SelectedIndex;
            RefreshExitCodes();
        };
        del.Click += (_, _) =>
        {
            if (_exitCodes.SelectedItem is ExitCodeEntry entry)
            {
                Config.ExitActions.Remove(entry.Code);
                RefreshExitCodes();
            }
        };

        page.Controls.Add(p);
        return page;
    }

    private TabPage BuildShutdownTab()
    {
        var page = new TabPage("Herunterfahren");
        var p = Ui.FormPanel();

        Ui.AddFullRow(p, Ui.Hint("Beim Beenden des Dienstes versucht EasyService die Anwendung stufenweise sauber zu " +
                                 "schließen. Jede aktivierte Stufe wartet die angegebene Zeit, bevor die nächste folgt."));

        Ui.AddFullRow(p, _stopConsole);
        Ui.AddRow(p, "Wartezeit (ms):", _stopConsoleMs);
        Ui.AddFullRow(p, _stopWindow);
        Ui.AddRow(p, "Wartezeit (ms):", _stopWindowMs);
        Ui.AddFullRow(p, _stopThreads);
        Ui.AddRow(p, "Wartezeit (ms):", _stopThreadsMs);
        Ui.AddSpacer(p);
        Ui.AddFullRow(p, _stopTerminate);
        Ui.AddFullRow(p, _killTree);
        Ui.AddFullRow(p, Ui.Hint("Ohne hartes Beenden kann ein hängender Prozess das Herunterfahren von Windows blockieren."));

        page.Controls.Add(p);
        return page;
    }

    // ----------------------------------------------------------- data binding

    private sealed record ExitCodeEntry(uint Code, ExitAction Action)
    {
        public override string ToString() => $"Exit-Code {Code}  ->  {Action switch
        {
            ExitAction.Restart => "Neu starten",
            ExitAction.Ignore => "Nichts tun",
            _ => "Dienst beenden",
        }}";
    }

    private void RefreshExitCodes()
    {
        _exitCodes.BeginUpdate();
        _exitCodes.Items.Clear();
        foreach (var (code, action) in Config.ExitActions.OrderBy(kv => kv.Key))
            _exitCodes.Items.Add(new ExitCodeEntry(code, action));
        _exitCodes.EndUpdate();
    }

    private void LoadFromConfig()
    {
        var c = Config;

        _serviceName.Text = c.ServiceName;
        _serviceName.ReadOnly = !_isNew;
        _application.Text = c.Application;
        _appDirectory.Text = c.AppDirectory;
        _appParameters.Text = c.AppParameters;

        _displayName.Text = c.DisplayName;
        _description.Text = c.Description;
        _startup.SelectedIndex = (int)c.Startup;
        _priority.SelectedIndex = (int)c.Priority;
        _startupDelay.Value = Math.Clamp(c.StartupDelayMs, 0, 600_000);

        _allProcessors.Checked = c.AffinityMask == 0;
        for (var i = 0; i < _affinity.Items.Count; i++)
            _affinity.SetItemChecked(i, c.AffinityMask == 0 || (c.AffinityMask & (1UL << i)) != 0);
        _affinity.Enabled = !_allProcessors.Checked;

        _logon.SelectedIndex = (int)c.Logon;
        _account.Text = c.AccountName;
        _password.Text = "";
        _interact.Checked = c.InteractWithDesktop;

        _dependencies.Lines = c.Dependencies.ToArray();
        _environment.Lines = c.Environment.ToArray();
        _replaceEnvironment.Checked = c.ReplaceEnvironment;

        if (_isNew && string.IsNullOrWhiteSpace(c.StdoutPath))
        {
            _serviceName.TextChanged += (_, _) =>
            {
                if (_serviceName.Text.Length == 0) return;
                var dir = ServiceConfig.DefaultLogDirectory;
                _stdout.Text = Path.Combine(dir, _serviceName.Text + "-stdout.log");
                _stderr.Text = Path.Combine(dir, _serviceName.Text + "-stderr.log");
            };
        }
        _stdout.Text = c.StdoutPath;
        _stderr.Text = c.StderrPath;
        _append.Checked = c.AppendOutput;
        _timestamp.Checked = c.TimestampLines;
        _rotate.Checked = c.RotateFiles;
        _rotateMb.Value = Math.Clamp(c.RotateBytes / (1024 * 1024), 1, 10_240);
        _rotateHours.Value = Math.Clamp(c.RotateSeconds / 3600, 0, 8760);
        _rotateKeep.Value = Math.Clamp(c.RotateKeep, 0, 999);
        _logEvents.Checked = c.LogServiceEvents;
        _rotateMb.Enabled = _rotateHours.Enabled = _rotateKeep.Enabled = _rotate.Checked;

        _defaultExit.SelectedIndex = (int)c.DefaultExitAction;
        _restartDelay.Value = Math.Clamp(c.RestartDelayMs, 0, 3_600_000);
        _throttle.Value = Math.Clamp(c.ThrottleMs, 0, 3_600_000);
        RefreshExitCodes();

        _stopConsole.Checked = c.StopUseConsole;
        _stopConsoleMs.Value = Math.Clamp(c.StopConsoleMs, 0, 600_000);
        _stopWindow.Checked = c.StopUseWindow;
        _stopWindowMs.Value = Math.Clamp(c.StopWindowMs, 0, 600_000);
        _stopThreads.Checked = c.StopUseThreads;
        _stopThreadsMs.Value = Math.Clamp(c.StopThreadsMs, 0, 600_000);
        _stopTerminate.Checked = c.StopUseTerminate;
        _killTree.Checked = c.KillProcessTree;
    }

    private void WireUpEnabling()
    {
        void Sync()
        {
            var isAccount = _logon.SelectedIndex == (int)LogonType.Account;
            _account.Enabled = isAccount;
            _password.Enabled = isAccount;
            _interact.Enabled = _logon.SelectedIndex == (int)LogonType.LocalSystem;
        }
        _logon.SelectedIndexChanged += (_, _) => Sync();
        Sync();

        _stopConsole.CheckedChanged += (_, _) => _stopConsoleMs.Enabled = _stopConsole.Checked;
        _stopWindow.CheckedChanged += (_, _) => _stopWindowMs.Enabled = _stopWindow.Checked;
        _stopThreads.CheckedChanged += (_, _) => _stopThreadsMs.Enabled = _stopThreads.Checked;
        _stopConsoleMs.Enabled = _stopConsole.Checked;
        _stopWindowMs.Enabled = _stopWindow.Checked;
        _stopThreadsMs.Enabled = _stopThreads.Checked;
    }

    private void SaveToConfig()
    {
        var c = Config;

        c.ServiceName = _serviceName.Text.Trim();
        c.Application = _application.Text.Trim();
        c.AppDirectory = _appDirectory.Text.Trim();
        c.AppParameters = _appParameters.Text.Trim();

        c.DisplayName = _displayName.Text.Trim();
        c.Description = _description.Text.Trim();
        c.Startup = (StartupType)_startup.SelectedIndex;
        c.Priority = (ProcessPriority)_priority.SelectedIndex;
        c.StartupDelayMs = (int)_startupDelay.Value;

        if (_allProcessors.Checked)
        {
            c.AffinityMask = 0;
        }
        else
        {
            ulong mask = 0;
            for (var i = 0; i < _affinity.Items.Count; i++)
                if (_affinity.GetItemChecked(i)) mask |= 1UL << i;
            c.AffinityMask = mask;   // 0 here means "nothing ticked" and is validated below
        }

        c.Logon = (LogonType)_logon.SelectedIndex;
        c.AccountName = _account.Text.Trim();
        c.Password = _password.Text;
        c.InteractWithDesktop = _interact.Checked && c.Logon == LogonType.LocalSystem;

        c.Dependencies = _dependencies.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        c.Environment = _environment.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        c.ReplaceEnvironment = _replaceEnvironment.Checked;

        c.StdoutPath = _stdout.Text.Trim();
        c.StderrPath = _stderr.Text.Trim();
        c.AppendOutput = _append.Checked;
        c.TimestampLines = _timestamp.Checked;
        c.RotateFiles = _rotate.Checked;
        c.RotateBytes = (long)_rotateMb.Value * 1024 * 1024;
        c.RotateSeconds = (int)_rotateHours.Value * 3600;
        c.RotateKeep = (int)_rotateKeep.Value;
        c.LogServiceEvents = _logEvents.Checked;

        c.DefaultExitAction = (ExitAction)_defaultExit.SelectedIndex;
        c.RestartDelayMs = (int)_restartDelay.Value;
        c.ThrottleMs = (int)_throttle.Value;

        c.StopUseConsole = _stopConsole.Checked;
        c.StopConsoleMs = (int)_stopConsoleMs.Value;
        c.StopUseWindow = _stopWindow.Checked;
        c.StopWindowMs = (int)_stopWindowMs.Value;
        c.StopUseThreads = _stopThreads.Checked;
        c.StopThreadsMs = (int)_stopThreadsMs.Value;
        c.StopUseTerminate = _stopTerminate.Checked;
        c.KillProcessTree = _killTree.Checked;
    }

    private void OnSave()
    {
        SaveToConfig();

        var problems = Config.Validate(_isNew).ToList();
        if (!_allProcessors.Checked && Config.AffinityMask == 0)
            problems.Add("Es muss mindestens ein Prozessor ausgewählt sein.");
        if (!_stopTerminate.Checked && !_stopConsole.Checked && !_stopWindow.Checked && !_stopThreads.Checked)
            problems.Add("Es muss mindestens eine Methode zum Beenden der Anwendung aktiviert sein.");

        if (problems.Count > 0)
        {
            Ui.ShowError(this, "Eingaben prüfen", string.Join(System.Environment.NewLine + "- ",
                problems.Prepend("Bitte folgende Punkte korrigieren:" + System.Environment.NewLine)));
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>Checkbox list of every installed service, used to pick dependencies.</summary>
internal sealed class ServicePickerDialog : Form
{
    private readonly CheckedListBox _list = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    private readonly TextBox _filter = new() { Dock = DockStyle.Top, PlaceholderText = "Filtern..." };
    private List<ServiceInfo> _all = new();
    private readonly HashSet<string> _selected;

    public IEnumerable<string> SelectedServices => _selected.OrderBy(s => s, StringComparer.OrdinalIgnoreCase);

    public ServicePickerDialog(IEnumerable<string> preselected)
    {
        _selected = new HashSet<string>(preselected.Where(s => s.Trim().Length > 0).Select(s => s.Trim()),
                                        StringComparer.OrdinalIgnoreCase);

        Text = "Abhängigkeiten auswählen";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(460, 520);
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90, Height = 28 };
        var cancel = new Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel, Width = 90, Height = 28 };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        _list.ItemCheck += (_, e) =>
        {
            var name = ((ServiceInfo)_list.Items[e.Index]).Name;
            if (e.NewValue == CheckState.Checked) _selected.Add(name);
            else _selected.Remove(name);
        };
        _filter.TextChanged += (_, _) => Populate();

        Controls.Add(_list);
        Controls.Add(_filter);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        Load += (_, _) =>
        {
            try { _all = ServiceRegistry.EnumerateServices(); }
            catch (Exception e) { Ui.ShowError(this, "Dienste konnten nicht gelesen werden", e); }
            Populate();
        };
    }

    private void Populate()
    {
        var filter = _filter.Text.Trim();
        var items = _all
            .Where(s => filter.Length == 0
                        || s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || s.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var s in items)
            _list.Items.Add(s, _selected.Contains(s.Name));
        _list.DisplayMember = nameof(ServiceInfo.Name);
        _list.EndUpdate();
    }
}
