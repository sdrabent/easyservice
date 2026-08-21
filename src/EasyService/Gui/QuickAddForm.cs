using EasyService.Core;

namespace EasyService.Gui;

/// <summary>
/// The fast lane. Nearly every service an administrator sets up needs exactly four
/// decisions: which program, which arguments, which account, and does it start with
/// Windows. Everything else - log paths, rotation, restart policy, shutdown sequence,
/// monitoring thresholds - has a defensible default, so this dialog fills them in and
/// shows what it did rather than asking.
///
/// The nine-tab editor is one button away for the remaining cases.
/// </summary>
public sealed class QuickAddForm : Form
{
    public ServiceConfig Config { get; private set; } = new();
    public bool StartAfterCreate => _startAfter.Checked;

    private readonly TextBox _application;
    private readonly TextBox _arguments = new();
    private readonly TextBox _serviceName = new();
    private readonly ComboBox _logon = Ui.Combo("Lokales Systemkonto (empfohlen)", "Lokaler Dienst",
                                                "Netzwerkdienst", "Dieses Konto");
    private readonly TextBox _account = new();
    private readonly TextBox _password = new() { UseSystemPasswordChar = true };
    private readonly CheckBox _rememberPassword = new()
    {
        Text = "Zugangsdaten für weitere Dienste merken",
        AutoSize = true,
    };
    private readonly ComboBox _startup = Ui.Combo("Automatisch", "Automatisch (verzögerter Start)",
                                                  "Manuell", "Deaktiviert");
    private readonly CheckBox _startAfter = new()
    {
        Text = "Dienst sofort starten",
        AutoSize = true,
        Checked = true,
    };

    private readonly Label _summary = new()
    {
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(3, 2, 3, 6),
        MaximumSize = new Size(580, 0),
    };

    private Label _accountLabel = null!;
    private Label _passwordHint = null!;
    private Label _passwordLabel = null!;

    private bool _serviceNameEditedByHand;

    public QuickAddForm(string? preselectedProgram = null)
    {
        Text = "Dienst schnell einrichten";
        Icon = Ui.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        MinimumSize = new Size(660, 620);
        Size = new Size(700, 660);
        Font = SystemFonts.MessageBoxFont ?? Font;
        AllowDrop = true;

        (var appPanel, _application) = Ui.BrowseRow(folder: false);

        var p = Ui.FormPanel();

        Ui.AddFullRow(p, Ui.Hint("Programm auswählen oder hierher ziehen - alles Weitere wird sinnvoll vorbelegt."));
        Ui.AddRow(p, "Programm:", appPanel);
        Ui.AddRow(p, "Argumente:", _arguments);
        Ui.AddRow(p, "Dienstname:", _serviceName);

        Ui.AddSpacer(p, "Anmeldung und Start");
        Ui.AddRow(p, "Anmelden als:", _logon);
        (_accountLabel, _) = Ui.AddLabelledRow(p, "Konto:", _account);
        (_passwordLabel, _) = Ui.AddLabelledRow(p, "Kennwort:", _password);
        Ui.AddFullRow(p, _rememberPassword);
        _passwordHint = Ui.AddFullRow(p, Ui.Hint(
            "Gemerkt wird nur für Ihr Windows-Konto auf diesem Rechner (verschlüsselt per DPAPI). " +
            "Ohne Häkchen bleibt das Kennwort nur bis zum Schließen von EasyService im Speicher."));
        Ui.AddRow(p, "Starttyp:", _startup);

        Ui.AddSpacer(p, "Wird automatisch eingerichtet");
        Ui.AddFullRow(p, _summary);

        var advanced = new Button { Text = "Erweiterte Einstellungen...", AutoSize = true, Height = 30 };
        var create = new Button { Text = "Dienst anlegen", Width = 140, Height = 30 };
        var cancel = new Button { Text = "Abbrechen", Width = 100, Height = 30, DialogResult = DialogResult.Cancel };

        advanced.Click += (_, _) => OpenAdvanced();
        create.Click += (_, _) => OnCreate();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 52,
            Padding = new Padding(12, 10, 12, 10),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(create);
        _startAfter.Margin = new Padding(16, 8, 16, 0);
        buttons.Controls.Add(_startAfter);
        advanced.Margin = new Padding(16, 0, 16, 0);
        buttons.Controls.Add(advanced);

        Controls.Add(p);
        Controls.Add(buttons);
        AcceptButton = create;
        CancelButton = cancel;

        WireUp();
        ApplyDefaults();

        if (!string.IsNullOrWhiteSpace(preselectedProgram))
            _application.Text = preselectedProgram;

        DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
                _application.Text = files[0];
        };

        Shown += (_, _) => _application.Focus();
    }

    private void WireUp()
    {
        _application.TextChanged += (_, _) =>
        {
            if (!_serviceNameEditedByHand)
            {
                try { _serviceName.Text = SuggestName(_application.Text); }
                catch (ArgumentException) { }
            }
            RefreshSummary();
        };

        _serviceName.TextChanged += (_, _) =>
        {
            if (_serviceName.Focused) _serviceNameEditedByHand = true;
            RefreshSummary();
        };

        _logon.SelectedIndexChanged += (_, _) => SyncAccountFields();
        _rememberPassword.CheckedChanged += (_, _) => { };
    }

    /// <summary>Derives a service name from the executable, avoiding one that already exists.</summary>
    private static string SuggestName(string programPath)
    {
        if (string.IsNullOrWhiteSpace(programPath)) return "";

        var stem = Path.GetFileNameWithoutExtension(programPath.Trim('"'));
        if (string.IsNullOrWhiteSpace(stem)) return "";

        var candidate = new string(stem.Where(c => !char.IsWhiteSpace(c) && c != '/' && c != '\\').ToArray());
        if (candidate.Length == 0) return "";

        if (!ServiceRegistry.Exists(candidate)) return candidate;
        for (var i = 2; i < 100; i++)
            if (!ServiceRegistry.Exists($"{candidate}{i}"))
                return $"{candidate}{i}";
        return candidate;
    }

    /// <summary>
    /// The credential rows only exist for "Dieses Konto". Collapsing them keeps the fast lane
    /// to four fields in the case that covers nearly every service.
    /// </summary>
    private void SyncAccountFields()
    {
        var isAccount = _logon.SelectedIndex == (int)LogonType.Account;
        foreach (var c in new Control[] { _accountLabel, _account, _passwordLabel, _password,
                                          _rememberPassword, _passwordHint })
            c.Visible = isAccount;
    }

    private void ApplyDefaults()
    {
        _logon.SelectedIndex = (int)UserDefaults.LastLogon;
        _startup.SelectedIndex = (int)UserDefaults.LastStartup;
        _account.Text = UserDefaults.LastAccountName;

        var remembered = UserDefaults.GetPassword();
        if (!string.IsNullOrEmpty(remembered))
        {
            _password.Text = remembered;
            _rememberPassword.Checked = UserDefaults.RememberPassword;
        }

        SyncAccountFields();
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        var name = _serviceName.Text.Trim();
        var directory = UserDefaults.LogDirectory;
        var nl = System.Environment.NewLine;

        var logs = name.Length == 0
            ? directory
            : Path.Combine(directory, name + "-stdout.log") + nl
              + "                 " + Path.Combine(directory, name + "-stderr.log");

        _summary.Text = string.Join(nl, new[]
        {
            "Protokolle:      " + logs,
            "                 Rotation ab 10 MB, die letzten 10 Dateien bleiben erhalten.",
            "",
            "Neustart:        nach einem Absturz, 1 s Verzögerung, Drosselung bei Dauerabstürzen.",
            "",
            "Überwachung:     aktiv - Warnung ab 3, kritisch ab 10 Neustarts pro Stunde;",
            "                 abrufbar über checkmk, prometheus, check und json.",
        });
    }

    // ------------------------------------------------------------- ergebnis ---

    private ServiceConfig BuildConfig()
    {
        var name = _serviceName.Text.Trim();
        var config = new ServiceConfig
        {
            ServiceName = name,
            DisplayName = name,
            Application = _application.Text.Trim(),
            AppParameters = _arguments.Text.Trim(),
            Startup = (StartupType)_startup.SelectedIndex,
            Logon = (LogonType)_logon.SelectedIndex,
            AccountName = _account.Text.Trim(),
            Password = _password.Text,
        };

        try
        {
            var program = System.Environment.ExpandEnvironmentVariables(config.Application);
            config.AppDirectory = Path.GetDirectoryName(program) ?? "";
        }
        catch (ArgumentException)
        {
            config.AppDirectory = "";
        }

        var directory = UserDefaults.LogDirectory;
        if (name.Length > 0)
        {
            config.StdoutPath = Path.Combine(directory, name + "-stdout.log");
            config.StderrPath = Path.Combine(directory, name + "-stderr.log");
        }

        return config;
    }

    private void OpenAdvanced()
    {
        var config = BuildConfig();
        if (string.IsNullOrWhiteSpace(config.ServiceName) && !string.IsNullOrWhiteSpace(config.Application))
            config.ApplyDefaultLogPaths();

        using var editor = new ServiceEditorForm(config, isNew: true);
        if (editor.ShowDialog(this) != DialogResult.OK) return;

        Config = editor.Config;
        _startAfter.Checked = editor.StartAfterSave;
        Remember();
        DialogResult = DialogResult.OK;
        Close();
    }

    private void OnCreate()
    {
        var config = BuildConfig();

        var problems = config.Validate(isNew: true).ToList();
        if (problems.Count > 0)
        {
            Ui.ShowError(this, "Eingaben prüfen", string.Join(System.Environment.NewLine + "- ",
                problems.Prepend("Bitte folgende Punkte korrigieren:" + System.Environment.NewLine)));
            return;
        }

        Config = config;
        Remember();
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Remember() => UserDefaults.RememberFrom(Config, _rememberPassword.Checked);
}
