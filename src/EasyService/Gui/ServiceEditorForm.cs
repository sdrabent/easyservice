using EasyService.Core;

using EasyService.Resources;

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
    private readonly ComboBox _startup = Ui.Combo(S.Svc_Startup_Automatic, S.Editor_Startup_AutoDelayed, S.Svc_Startup_Manual, S.Svc_Startup_Disabled);
    private readonly ComboBox _priority = Ui.Combo(S.Editor_Prio_Realtime, S.Editor_Prio_High, S.Editor_Prio_AboveNormal,
                                                  S.Editor_Prio_Normal, S.Editor_Prio_BelowNormal, S.Editor_Prio_Idle);
    private readonly CheckedListBox _affinity = new() { CheckOnClick = true, Height = 120, ColumnWidth = 90, MultiColumn = true };
    private readonly CheckBox _allProcessors = new() { Text = S.Editor_Chk_AllProcessors, Checked = true, AutoSize = true };
    private readonly NumericUpDown _startupDelay = Ui.Spin(0, 600_000, 0, 500);

    // Anmelden
    private readonly ComboBox _logon = Ui.Combo(S.Editor_Logon_LocalSystem, S.Editor_Logon_LocalService,
                                               S.Editor_Logon_NetworkService, S.Editor_Logon_Account);
    private readonly TextBox _account = new();
    private readonly TextBox _password = new() { UseSystemPasswordChar = true };
    private readonly CheckBox _interact = new() { Text = S.Editor_Chk_Interact, AutoSize = true };

    // Abhängigkeiten / Umgebung
    private readonly TextBox _dependencies = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 150 };
    private readonly TextBox _environment = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 150 };
    private readonly CheckBox _replaceEnvironment = new() { Text = S.Editor_Chk_ReplaceEnvironment, AutoSize = true };

    // Protokollierung
    private readonly TextBox _stdout;
    private readonly TextBox _stderr;
    private readonly CheckBox _append = new() { Text = S.Editor_Chk_Append, AutoSize = true };
    private readonly CheckBox _timestamp = new() { Text = S.Editor_Chk_Timestamp, AutoSize = true };
    private readonly CheckBox _rotate = new() { Text = S.Editor_Chk_Rotate, AutoSize = true };
    private readonly NumericUpDown _rotateMb = Ui.Spin(1, 10_240, 10);
    private readonly NumericUpDown _rotateHours = Ui.Spin(0, 8760, 0);
    private readonly NumericUpDown _rotateKeep = Ui.Spin(0, 999, 10);
    private readonly CheckBox _logEvents = new() { Text = S.Editor_Chk_LogEvents, AutoSize = true, Checked = true };

    // Beenden-Aktionen
    private readonly ComboBox _defaultExit = Ui.Combo(S.Editor_Exit_Restart, S.Editor_Exit_Ignore, S.Editor_Exit_Stop);
    private readonly NumericUpDown _restartDelay = Ui.Spin(0, 3_600_000, 1000, 500);
    private readonly NumericUpDown _throttle = Ui.Spin(0, 3_600_000, 5000, 500);
    private readonly ListBox _exitCodes = new() { Height = 130 };
    private readonly NumericUpDown _exitCode = Ui.Spin(0, int.MaxValue, 0);
    private readonly ComboBox _exitAction = Ui.Combo(S.Editor_ExitShort_Restart, S.Editor_ExitShort_Ignore, S.Editor_ExitShort_Stop);

    // Herunterfahren
    private readonly CheckBox _stopConsole = new() { Text = S.Editor_Chk_StopConsole, AutoSize = true, Checked = true };
    private readonly NumericUpDown _stopConsoleMs = Ui.Spin(0, 600_000, 1500, 250);
    private readonly CheckBox _stopWindow = new() { Text = S.Editor_Chk_StopWindow, AutoSize = true, Checked = true };
    private readonly NumericUpDown _stopWindowMs = Ui.Spin(0, 600_000, 1500, 250);
    private readonly CheckBox _stopThreads = new() { Text = S.Editor_Chk_StopThreads, AutoSize = true, Checked = true };
    private readonly NumericUpDown _stopThreadsMs = Ui.Spin(0, 600_000, 1500, 250);
    private readonly CheckBox _stopTerminate = new() { Text = S.Editor_Chk_StopTerminate, AutoSize = true, Checked = true };
    private readonly CheckBox _killTree = new() { Text = S.Editor_Chk_KillTree, AutoSize = true, Checked = true };

    // Überwachung
    private readonly CheckBox _monEnabled = new() { Text = S.Editor_Chk_MonitoringEnabled, AutoSize = true, Checked = true };
    private readonly NumericUpDown _warnRestarts = Ui.Spin(0, 10_000, 3);
    private readonly NumericUpDown _critRestarts = Ui.Spin(0, 10_000, 10);
    private readonly NumericUpDown _warnCpu = Ui.Spin(0, 100, 0);
    private readonly NumericUpDown _critCpu = Ui.Spin(0, 100, 0);
    private readonly NumericUpDown _warnMemory = Ui.Spin(0, 1_048_576, 0, 64);
    private readonly NumericUpDown _critMemory = Ui.Spin(0, 1_048_576, 0, 64);
    private readonly NumericUpDown _historyDays = Ui.Spin(0, 3650, 30);

    // Health-Check
    private readonly ComboBox _healthType = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _healthTarget = new();
    private readonly NumericUpDown _healthInterval = Ui.Spin(1000, 3_600_000, 30_000, 1000);
    private readonly NumericUpDown _healthTimeout = Ui.Spin(500, 600_000, 5_000, 500);
    private readonly NumericUpDown _healthGrace = Ui.Spin(0, 3_600_000, 30_000, 1000);
    private readonly NumericUpDown _healthFailures = Ui.Spin(1, 100, 3);
    private readonly ComboBox _healthAction = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _healthExpect = Ui.Spin(0, 599, 0);
    private readonly NumericUpDown _healthMaxAge = Ui.Spin(1, 86_400, 120, 10);
    private readonly Label _healthHint = Ui.Hint("");
    private readonly Label _healthResult = Ui.Hint("");
    private readonly TextBox _integration = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Height = 190,
        BackColor = SystemColors.Window,
    };

    private readonly CheckBox _startAfterSave = new() { Text = S.Editor_Chk_StartAfterSave, AutoSize = true, Checked = true };

    public ServiceEditorForm(ServiceConfig config, bool isNew)
    {
        Config = config;
        _isNew = isNew;

        Text = isNew ? S.Editor_Title_New : S.Editor_Title_Edit(config.ServiceName);
        Icon = Ui.AppIcon;
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
        (var outPanel, _stdout) = Ui.BrowseRow(folder: false, S.Common_FilterLog);
        (var errPanel, _stderr) = Ui.BrowseRow(folder: false, S.Common_FilterLog);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 6) };
        tabs.TabPages.Add(BuildApplicationTab(appPanel, dirPanel));
        tabs.TabPages.Add(BuildDetailsTab());
        tabs.TabPages.Add(BuildLogonTab());
        tabs.TabPages.Add(BuildDependenciesTab());
        tabs.TabPages.Add(BuildEnvironmentTab());
        tabs.TabPages.Add(BuildLoggingTab(outPanel, errPanel));
        tabs.TabPages.Add(BuildExitTab());
        tabs.TabPages.Add(BuildHealthTab());
        tabs.TabPages.Add(BuildMonitoringTab());
        tabs.TabPages.Add(BuildShutdownTab());

        var ok = new Button { Text = isNew ? S.Editor_Btn_Create : S.Common_Save, Width = 130, Height = 30, DialogResult = DialogResult.None };
        var cancel = new Button { Text = S.Common_Cancel, Width = 100, Height = 30, DialogResult = DialogResult.Cancel };
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
        var page = new TabPage(S.Editor_Tab_Application);
        var p = Ui.FormPanel();

        Ui.AddRow(p, S.Editor_Lbl_ServiceName, _serviceName);
        Ui.AddFullRow(p, Ui.Hint(S.Editor_Hint_ServiceName));
        Ui.AddRow(p, S.Editor_Lbl_Program, appPanel);
        Ui.AddRow(p, S.Editor_Lbl_Directory, dirPanel);
        Ui.AddRow(p, S.Editor_Lbl_Arguments, _appParameters);
        Ui.AddFullRow(p, Ui.Hint(S.Editor_Hint_Arguments));

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
        var page = new TabPage(S.Editor_Tab_Details);
        var p = Ui.FormPanel();

        Ui.AddRow(p, S.Editor_Lbl_DisplayName, _displayName);
        Ui.AddRow(p, S.Editor_Lbl_Description, _description);
        Ui.AddRow(p, S.Editor_Lbl_Startup, _startup);
        Ui.AddRow(p, S.Editor_Lbl_StartupDelay, _startupDelay);
        Ui.AddFullRow(p, Ui.Hint(S.Editor_Hint_StartupDelay));

        Ui.AddSpacer(p, S.Editor_Group_Process);
        Ui.AddRow(p, S.Editor_Lbl_Priority, _priority);
        Ui.AddFullRow(p, _allProcessors);
        Ui.AddRow(p, S.Editor_Lbl_Processors, _affinity);

        for (var i = 0; i < System.Environment.ProcessorCount && i < 64; i++)
            _affinity.Items.Add(S.Editor_Cpu(i), true);
        _allProcessors.CheckedChanged += (_, _) => _affinity.Enabled = !_allProcessors.Checked;

        page.Controls.Add(p);
        return page;
    }

    private TabPage BuildLogonTab()
    {
        var page = new TabPage(S.Editor_Tab_LogOn);
        var p = Ui.FormPanel();

        Ui.AddRow(p, S.Editor_Lbl_LogonAs, _logon);
        Ui.AddRow(p, S.Editor_Lbl_Account, _account);
        Ui.AddFullRow(p, Ui.Hint(S.Editor_Hint_Account));
        Ui.AddRow(p, S.Editor_Lbl_Password, _password);
        if (!_isNew)
            Ui.AddFullRow(p, Ui.Hint(S.Editor_Hint_KeepPassword));
        Ui.AddSpacer(p);
        Ui.AddFullRow(p, _interact);
        Ui.AddFullRow(p, Ui.Hint(S.Editor_Hint_Interact));

        page.Controls.Add(p);
        return page;
    }

    private TabPage BuildDependenciesTab()
    {
        var page = new TabPage(S.Editor_Tab_Dependencies);
        var p = Ui.FormPanel();

        Ui.AddFullRow(p, Ui.Hint(S.Editor_Hint_Dependencies));
        Ui.AddFullRow(p, _dependencies);

        var pick = new Button { Text = S.Editor_Btn_PickServices, AutoSize = true };
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
        var page = new TabPage(S.Editor_Tab_Environment);
        var p = Ui.FormPanel();

        Ui.AddFullRow(p, Ui.Hint(S.Editor_Hint_Environment));
        Ui.AddFullRow(p, _environment);
        Ui.AddFullRow(p, _replaceEnvironment);
        Ui.AddFullRow(p, Ui.Hint(S.Editor_Hint_ReplaceEnvironment));

        page.Controls.Add(p);
        return page;
    }

    private TabPage BuildLoggingTab(Control outPanel, Control errPanel)
    {
        var page = new TabPage(S.Editor_Tab_Logging);
        var p = Ui.FormPanel();

        Ui.AddFullRow(p, Ui.Hint(S.Editor_Hint_Logging));
        Ui.AddRow(p, S.Editor_Lbl_Stdout, outPanel);
        Ui.AddRow(p, S.Editor_Lbl_Stderr, errPanel);
        Ui.AddFullRow(p, _append);
        Ui.AddFullRow(p, _timestamp);

        Ui.AddSpacer(p, S.Editor_Group_Rotation);
        Ui.AddFullRow(p, _rotate);
        Ui.AddRow(p, S.Editor_Lbl_RotateMb, _rotateMb);
        Ui.AddRow(p, S.Editor_Lbl_RotateHours, _rotateHours);
        Ui.AddFullRow(p, Ui.Hint(S.Editor_Hint_RotateHours));
        Ui.AddRow(p, S.Editor_Lbl_RotateKeep, _rotateKeep);
        Ui.AddFullRow(p, Ui.Hint(S.Editor_Hint_RotateKeep));

        Ui.AddSpacer(p, S.Editor_Group_Diagnostics);
        Ui.AddFullRow(p, _logEvents);

        _rotate.CheckedChanged += (_, _) =>
            _rotateMb.Enabled = _rotateHours.Enabled = _rotateKeep.Enabled = _rotate.Checked;

        page.Controls.Add(p);
        return page;
    }

    private TabPage BuildExitTab()
    {
        var page = new TabPage(S.Editor_Tab_Exit);
        var p = Ui.FormPanel();

        Ui.AddFullRow(p, Ui.Hint(S.Editor_Hint_Exit));
        Ui.AddRow(p, S.Editor_Lbl_DefaultExit, _defaultExit);
        Ui.AddRow(p, S.Editor_Lbl_RestartDelay, _restartDelay);
        Ui.AddRow(p, S.Editor_Lbl_Throttle, _throttle);
        Ui.AddFullRow(p, Ui.Hint(S.Editor_Hint_Throttle));

        Ui.AddSpacer(p, S.Editor_Group_ExitCodes);
        Ui.AddFullRow(p, _exitCodes);

        var row = new FlowLayoutPanel { Height = 34, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _exitCode.Width = 110;
        _exitAction.Width = 160;
        var add = new Button { Text = S.Editor_Btn_Add, AutoSize = true };
        var del = new Button { Text = S.Editor_Btn_RemoveEntry, AutoSize = true };
        row.Controls.Add(new Label { Text = S.Editor_Lbl_ExitCode, AutoSize = true, Margin = new Padding(0, 8, 4, 0) });
        row.Controls.Add(_exitCode);
        row.Controls.Add(_exitAction);
        row.Controls.Add(add);
        row.Controls.Add(del);
        Ui.AddFullRow(p, row);
        Ui.AddFullRow(p, Ui.Hint(S.Editor_Hint_ExitCodes));

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

    private TabPage BuildHealthTab()
    {
        var page = new TabPage(S.Editor_Tab_Health);
        var p = Ui.FormPanel();

        _healthType.Items.AddRange(new object[]
        {
            S.Health_Type_None, S.Health_Type_Http, S.Health_Type_Tcp,
            S.Health_Type_File, S.Health_Type_Command,
        });
        _healthType.SelectedIndex = 0;
        _healthAction.Items.AddRange(new object[] { S.Health_Action_Report, S.Health_Action_Restart });
        _healthAction.SelectedIndex = 0;

        Ui.AddFullRow(p, Ui.Hint(S.Editor_Hint_Health));
        Ui.AddRow(p, S.Editor_Lbl_HealthType, _healthType);
        Ui.AddRow(p, S.Editor_Lbl_HealthTarget, _healthTarget);
        Ui.AddFullRow(p, _healthHint);

        Ui.AddSpacer(p);
        Ui.AddRow(p, S.Editor_Lbl_HealthInterval, _healthInterval);
        Ui.AddRow(p, S.Editor_Lbl_HealthTimeout, _healthTimeout);
        Ui.AddRow(p, S.Editor_Lbl_HealthGrace, _healthGrace);
        Ui.AddRow(p, S.Editor_Lbl_HealthFailures, _healthFailures);
        Ui.AddRow(p, S.Editor_Lbl_HealthAction, _healthAction);

        Ui.AddSpacer(p);
        Ui.AddRow(p, S.Editor_Lbl_HealthExpect, _healthExpect);
        Ui.AddFullRow(p, Ui.Hint(S.Editor_Hint_HealthExpect));
        Ui.AddRow(p, S.Editor_Lbl_HealthMaxAge, _healthMaxAge);

        Ui.AddSpacer(p);
        var test = new Button { Text = S.Editor_Btn_HealthTest, AutoSize = true };
        test.Click += (_, _) => TestHealthNow();
        Ui.AddFullRow(p, test);
        Ui.AddFullRow(p, _healthResult);

        _healthType.SelectedIndexChanged += (_, _) => UpdateHealthFields();
        UpdateHealthFields();

        page.Controls.Add(p);
        return page;
    }

    /// <summary>Only offer the fields the chosen kind of check actually uses.</summary>
    private void UpdateHealthFields()
    {
        var type = (HealthCheckType)Math.Max(0, _healthType.SelectedIndex);
        var configured = type != HealthCheckType.None;

        foreach (var control in new Control[] { _healthTarget, _healthInterval, _healthTimeout,
                                                _healthGrace, _healthFailures, _healthAction })
            control.Enabled = configured;

        _healthExpect.Enabled = type == HealthCheckType.Http;
        _healthMaxAge.Enabled = type == HealthCheckType.FileFresh;

        _healthHint.Text = type switch
        {
            HealthCheckType.Http => S.Editor_Hint_HealthHttp,
            HealthCheckType.Tcp => S.Editor_Hint_HealthTcp,
            HealthCheckType.FileFresh => S.Editor_Hint_HealthFile,
            HealthCheckType.Command => S.Editor_Hint_HealthCommand,
            _ => "",
        };
    }

    /// <summary>
    /// Runs the check as it stands in the dialog. Typing a URL and finding out three minutes
    /// later from the event log that it was the wrong one is not a way to configure anything.
    /// </summary>
    private void TestHealthNow()
    {
        var probe = new ServiceConfig
        {
            ServiceName = _serviceName.Text.Trim(),
            AppDirectory = _appDirectory.Text.Trim(),
            HealthType = (HealthCheckType)Math.Max(0, _healthType.SelectedIndex),
            HealthTarget = _healthTarget.Text.Trim(),
            HealthTimeoutMs = (int)_healthTimeout.Value,
            HealthExpectStatus = (int)_healthExpect.Value,
            HealthMaxAgeSec = (int)_healthMaxAge.Value,
        };

        if (probe.HealthType == HealthCheckType.None)
        {
            _healthResult.Text = S.Health_NotConfigured;
            _healthResult.ForeColor = SystemColors.GrayText;
            return;
        }

        _healthResult.Text = S.Editor_Health_Testing;
        _healthResult.ForeColor = SystemColors.GrayText;
        var previousCursor = Cursor;
        Cursor = Cursors.WaitCursor;
        Application.DoEvents();

        try
        {
            var result = HealthProbe.Run(probe);
            var milliseconds = (int)result.Duration.TotalMilliseconds;
            _healthResult.Text = result.Healthy
                ? S.Cli_Health_Ok(result.Detail, milliseconds)
                : S.Cli_Health_Failed(result.Detail, milliseconds);
            _healthResult.ForeColor = Ui.HealthColor(
                result.Healthy ? CheckStatus.Ok : CheckStatus.Critical, this, ForeColor);
        }
        finally
        {
            Cursor = previousCursor;
        }
    }

    private TabPage BuildMonitoringTab()
    {
        var page = new TabPage(S.Editor_Tab_Monitoring);
        var p = Ui.FormPanel();

        Ui.AddFullRow(p, Ui.Hint(S.Editor_Hint_Monitoring));
        Ui.AddFullRow(p, _monEnabled);

        Ui.AddSpacer(p, S.Editor_Group_Thresholds);
        Ui.AddFullRow(p, Ui.Hint(S.Editor_Hint_ZeroOff));
        Ui.AddRow(p, S.Editor_Lbl_WarnRestarts, _warnRestarts);
        Ui.AddRow(p, S.Editor_Lbl_CritRestarts, _critRestarts);
        Ui.AddRow(p, S.Editor_Lbl_WarnCpu, _warnCpu);
        Ui.AddRow(p, S.Editor_Lbl_CritCpu, _critCpu);
        Ui.AddFullRow(p, Ui.Hint(S.Editor_Hint_Cpu100));
        Ui.AddRow(p, S.Editor_Lbl_WarnMemory, _warnMemory);
        Ui.AddRow(p, S.Editor_Lbl_CritMemory, _critMemory);
        Ui.AddFullRow(p, Ui.Hint(S.Editor_Hint_Memory));

        Ui.AddRow(p, S.Editor_Lbl_HistoryDays, _historyDays);
        Ui.AddFullRow(p, Ui.Hint(S.Editor_Hint_HistoryDays));

        Ui.AddSpacer(p, S.Editor_Group_Integration);
        Ui.AddFullRow(p, _integration);
        _integration.Font = Ui.MonoFont;

        var copy = new Button { Text = S.Editor_Btn_Copy, AutoSize = true };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(_integration.Text); }
            catch (Exception e) { Ui.ShowError(this, S.Common_Clipboard, e); }
        };
        Ui.AddFullRow(p, copy);

        _serviceName.TextChanged += (_, _) => RefreshIntegrationText();
        RefreshIntegrationText();

        page.Controls.Add(p);
        return page;
    }

    private void RefreshIntegrationText()
    {
        var exe = Core.ServiceRegistry.ExecutablePath;
        var name = _serviceName.Text.Trim().Length > 0 ? _serviceName.Text.Trim() : S.Editor_Lbl_ServiceName;
        var nl = System.Environment.NewLine;

        _integration.Text = string.Join(nl, new[]
        {
            S.Editor_Integration_Checkmk,
            S.Editor_Integration_File(@"C:\ProgramData\checkmk\agent\local\easyservice.bat"),
            S.Editor_Integration_Content($"@\"{exe}\" checkmk"),
            "",
            S.Editor_Integration_Prometheus,
            $"  \"{exe}\" prometheus --output C:\\ProgramData\\node_exporter\\textfile\\easyservice.prom",
            "",
            S.Editor_Integration_Nagios,
            $"  \"{exe}\" check \"{name}\"",
            "",
            S.Editor_Integration_Zabbix,
            $"  UserParameter=easyservice.discovery,\"{exe}\" zabbix-discovery",
            $"  UserParameter=easyservice.check[*],\"{exe}\" check \"$1\"",
            "",
            S.Editor_Integration_Json,
            $"  \"{exe}\" json",
        });
    }

    private TabPage BuildShutdownTab()
    {
        var page = new TabPage(S.Editor_Tab_Shutdown);
        var p = Ui.FormPanel();

        Ui.AddFullRow(p, Ui.Hint(S.Editor_Hint_Shutdown));

        Ui.AddFullRow(p, _stopConsole);
        Ui.AddRow(p, S.Editor_Lbl_WaitMs, _stopConsoleMs);
        Ui.AddFullRow(p, _stopWindow);
        Ui.AddRow(p, S.Editor_Lbl_WaitMs, _stopWindowMs);
        Ui.AddFullRow(p, _stopThreads);
        Ui.AddRow(p, S.Editor_Lbl_WaitMs, _stopThreadsMs);
        Ui.AddSpacer(p);
        Ui.AddFullRow(p, _stopTerminate);
        Ui.AddFullRow(p, _killTree);
        Ui.AddFullRow(p, Ui.Hint(S.Editor_Hint_NoTerminate));

        page.Controls.Add(p);
        return page;
    }

    // ----------------------------------------------------------- data binding

    private sealed record ExitCodeEntry(uint Code, ExitAction Action)
    {
        public override string ToString() => S.Editor_ExitCodeEntry(Code, Action switch
        {
            ExitAction.Restart => S.Editor_ExitShort_Restart,
            ExitAction.Ignore => S.Editor_ExitShort_Ignore,
            _ => S.Editor_ExitShort_Stop,
        });
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

        _monEnabled.Checked = c.MonitoringEnabled;
        _warnRestarts.Value = Math.Clamp(c.WarnRestartsPerHour, 0, 10_000);
        _critRestarts.Value = Math.Clamp(c.CritRestartsPerHour, 0, 10_000);
        _warnCpu.Value = Math.Clamp(c.WarnCpuPercent, 0, 100);
        _critCpu.Value = Math.Clamp(c.CritCpuPercent, 0, 100);
        _warnMemory.Value = Math.Clamp(c.WarnMemoryMb, 0, 1_048_576);
        _critMemory.Value = Math.Clamp(c.CritMemoryMb, 0, 1_048_576);
        _historyDays.Value = Math.Clamp(c.HistoryDays, 0, 3650);

        _healthType.SelectedIndex = (int)c.HealthType;
        _healthTarget.Text = c.HealthTarget;
        _healthInterval.Value = Math.Clamp(c.HealthIntervalMs, 1000, 3_600_000);
        _healthTimeout.Value = Math.Clamp(c.HealthTimeoutMs, 500, 600_000);
        _healthGrace.Value = Math.Clamp(c.HealthGraceMs, 0, 3_600_000);
        _healthFailures.Value = Math.Clamp(c.HealthFailures, 1, 100);
        _healthAction.SelectedIndex = (int)c.HealthAction;
        _healthExpect.Value = Math.Clamp(c.HealthExpectStatus, 0, 599);
        _healthMaxAge.Value = Math.Clamp(c.HealthMaxAgeSec, 1, 86_400);
        UpdateHealthFields();

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

        c.MonitoringEnabled = _monEnabled.Checked;
        c.WarnRestartsPerHour = (int)_warnRestarts.Value;
        c.CritRestartsPerHour = (int)_critRestarts.Value;
        c.WarnCpuPercent = (int)_warnCpu.Value;
        c.CritCpuPercent = (int)_critCpu.Value;
        c.WarnMemoryMb = (int)_warnMemory.Value;
        c.CritMemoryMb = (int)_critMemory.Value;
        c.HistoryDays = (int)_historyDays.Value;

        c.HealthType = (HealthCheckType)Math.Max(0, _healthType.SelectedIndex);
        c.HealthTarget = _healthTarget.Text.Trim();
        c.HealthIntervalMs = (int)_healthInterval.Value;
        c.HealthTimeoutMs = (int)_healthTimeout.Value;
        c.HealthGraceMs = (int)_healthGrace.Value;
        c.HealthFailures = (int)_healthFailures.Value;
        c.HealthAction = (HealthAction)Math.Max(0, _healthAction.SelectedIndex);
        c.HealthExpectStatus = (int)_healthExpect.Value;
        c.HealthMaxAgeSec = (int)_healthMaxAge.Value;

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
            problems.Add(S.Editor_Err_NoProcessor);
        if (!_stopTerminate.Checked && !_stopConsole.Checked && !_stopWindow.Checked && !_stopThreads.Checked)
            problems.Add(S.Editor_Err_NoStopMethod);

        if (problems.Count > 0)
        {
            Ui.ShowError(this, S.Quick_Err_Title, string.Join(System.Environment.NewLine + "- ",
                problems.Prepend(S.Quick_Err_Intro + System.Environment.NewLine)));
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
    private readonly TextBox _filter = new() { Dock = DockStyle.Top, PlaceholderText = S.Editor_Picker_Filter };
    private List<ServiceInfo> _all = new();
    private readonly HashSet<string> _selected;

    public IEnumerable<string> SelectedServices => _selected.OrderBy(s => s, StringComparer.OrdinalIgnoreCase);

    public ServicePickerDialog(IEnumerable<string> preselected)
    {
        _selected = new HashSet<string>(preselected.Where(s => s.Trim().Length > 0).Select(s => s.Trim()),
                                        StringComparer.OrdinalIgnoreCase);

        Text = S.Editor_Picker_Title;
        Icon = Ui.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(460, 520);
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90, Height = 28 };
        var cancel = new Button { Text = S.Common_Cancel, DialogResult = DialogResult.Cancel, Width = 90, Height = 28 };
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
            Ui.FollowTheme(this);
            try { _all = ServiceRegistry.EnumerateServices(); }
            catch (Exception e) { Ui.ShowError(this, S.Main_Err_ListFailed, e); }
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
