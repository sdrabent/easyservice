using EasyService.Core;

namespace EasyService.Gui;

public sealed class MainForm : Form
{
    private readonly ListView _list;
    private readonly ToolStrip _toolbar;
    private readonly ToolStripStatusLabel _status;
    private readonly ToolStripTextBox _filterBox;
    private readonly ToolStripButton _onlyManaged;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    private readonly ToolStripButton _btnEdit, _btnStart, _btnStop, _btnRestart, _btnLogs, _btnRemove;

    private List<ServiceInfo> _services = new();
    private Dictionary<string, CheckResult> _checks = new(StringComparer.OrdinalIgnoreCase);
    private bool _loading;
    private int _sortColumn = -1;
    private bool _sortAscending = true;
    private readonly string? _initialSelection;

    public MainForm(string? selectService = null)
    {
        _initialSelection = selectService;

        Text = "EasyService - Windows-Dienste verwalten";
        Icon = Ui.AppIcon;
        MinimumSize = new Size(900, 480);
        Size = new Size(1320, 700);
        StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont ?? Font;

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            HideSelection = false,
            MultiSelect = false,
            UseCompatibleStateImageBehavior = false,
        };
        _list.Columns.Add("Name", 190);
        _list.Columns.Add("Status", 110);
        _list.Columns.Add("Anwendung", 140);
        _list.Columns.Add("CPU", 60, HorizontalAlignment.Right);
        _list.Columns.Add("RAM", 75, HorizontalAlignment.Right);
        _list.Columns.Add("Neust./h", 65, HorizontalAlignment.Right);
        _list.Columns.Add("Laufzeit", 85, HorizontalAlignment.Right);
        _list.Columns.Add("Starttyp", 145);
        _list.Columns.Add("PID", 60, HorizontalAlignment.Right);
        _list.Columns.Add("Anzeigename", 200);
        _list.Columns.Add("Konto", 140);
        _list.Columns.Add("Programm", 260);
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.DoubleClick += (_, _) => EditSelected();
        _list.ColumnClick += OnColumnClick;
        _list.KeyDown += OnListKeyDown;

        _toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 2, 6, 2) };
        _toolbar.Items.Add(Button("Dienst hinzufügen...", (_, _) => CreateNew(),
            "Schnell einrichten: Programm wählen, Rest wird vorbelegt (oder eine .exe ins Fenster ziehen)"));
        _btnEdit = Button("Bearbeiten...", (_, _) => EditSelected(), "Konfiguration des markierten Dienstes ändern");
        _toolbar.Items.Add(_btnEdit);
        _toolbar.Items.Add(new ToolStripSeparator());
        _btnStart = Button("Starten", (_, _) => Control(ServiceAction.Start), null);
        _btnStop = Button("Beenden", (_, _) => Control(ServiceAction.Stop), null);
        _btnRestart = Button("Neu starten", (_, _) => Control(ServiceAction.Restart), null);
        _toolbar.Items.AddRange(new ToolStripItem[] { _btnStart, _btnStop, _btnRestart });
        _toolbar.Items.Add(new ToolStripSeparator());
        _btnLogs = Button("Protokolle...", (_, _) => ShowLogs(), "Live-Ansicht der stdout/stderr-Protokolle");
        _toolbar.Items.Add(_btnLogs);
        _btnRemove = Button("Entfernen", (_, _) => RemoveSelected(), "Dienst beenden und löschen");
        _toolbar.Items.Add(_btnRemove);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(Button("Aktualisieren", (_, _) => Reload(), "Liste neu einlesen (F5)"));

        _toolbar.Items.Add(new ToolStripSeparator());
        _onlyManaged = new ToolStripButton("Nur EasyService")
        {
            CheckOnClick = true,
            Checked = true,
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Nur Dienste anzeigen, die mit EasyService angelegt wurden",
        };
        _onlyManaged.CheckedChanged += (_, _) => ApplyFilter();
        _toolbar.Items.Add(_onlyManaged);

        _toolbar.Items.Add(new ToolStripLabel("  Filter:"));
        _filterBox = new ToolStripTextBox { Width = 190 };
        _filterBox.TextChanged += (_, _) => ApplyFilter();
        _toolbar.Items.Add(_filterBox);

        _status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);

        _list.ShowItemToolTips = true;
        _list.ContextMenuStrip = BuildContextMenu();

        Controls.Add(_list);
        Controls.Add(_toolbar);
        Controls.Add(statusStrip);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _refreshTimer.Tick += (_, _) => Reload(silent: true);

        AllowDrop = true;
        DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
                CreateNew(files[0]);
        };

        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5) { Reload(); e.Handled = true; }
        };

        Load += (_, _) =>
        {
            Reload();
            _refreshTimer.Start();
        };
        FormClosed += (_, _) => _refreshTimer.Stop();
    }

    private static ToolStripButton Button(string text, EventHandler onClick, string? tip)
    {
        var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = tip };
        b.Click += onClick;
        return b;
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Bearbeiten...", null, (_, _) => EditSelected());
        menu.Items.Add("Protokolle...", null, (_, _) => ShowLogs());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Starten", null, (_, _) => Control(ServiceAction.Start));
        menu.Items.Add("Beenden", null, (_, _) => Control(ServiceAction.Stop));
        menu.Items.Add("Neu starten", null, (_, _) => Control(ServiceAction.Restart));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Programmordner öffnen", null, (_, _) =>
        {
            if (Selected is { } s && ServiceConfig.Load(s.Name) is { } c) Ui.OpenInExplorer(c.Application);
        });
        menu.Items.Add("In services.msc anzeigen", null, (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("services.msc") { UseShellExecute = true }); }
            catch (Exception e) { Ui.ShowError(this, "services.msc", e); }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Entfernen", null, (_, _) => RemoveSelected());
        menu.Opening += (_, e) =>
        {
            if (Selected is null) e.Cancel = true;
        };
        return menu;
    }

    private ServiceInfo? Selected =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as ServiceInfo : null;

    // ------------------------------------------------------------- loading ---

    private void Reload(bool silent = false)
    {
        if (_loading) return;
        _loading = true;
        if (!silent) _status.Text = "Dienste werden gelesen...";

        Task.Run(() =>
        {
            try
            {
                var services = ServiceRegistry.EnumerateServices();
                var checks = Monitoring.CheckAll(services)
                                       .ToDictionary(r => r.ServiceName, StringComparer.OrdinalIgnoreCase);
                BeginInvoke(() =>
                {
                    _services = services;
                    _checks = checks;
                    ApplyFilter();
                    _loading = false;
                });
            }
            catch (Exception e)
            {
                BeginInvoke(() =>
                {
                    _loading = false;
                    _refreshTimer.Stop();
                    Ui.ShowError(this, "Dienste konnten nicht gelesen werden", e);
                    _status.Text = "Fehler: " + e.Message;
                });
            }
        });
    }

    private void ApplyFilter()
    {
        var filter = _filterBox.Text.Trim();
        var onlyManaged = _onlyManaged.Checked;

        var visible = _services.Where(s =>
        {
            if (onlyManaged && !s.ManagedByEasyService) return false;
            if (filter.Length == 0) return true;
            return s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || s.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || s.BinaryPath.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        visible = Sort(visible);

        var previous = Selected?.Name;
        _list.BeginUpdate();
        try
        {
            // Update in place where possible so the list does not flicker on the auto refresh.
            while (_list.Items.Count > visible.Count) _list.Items.RemoveAt(_list.Items.Count - 1);
            for (var i = 0; i < visible.Count; i++)
            {
                var s = visible[i];
                _checks.TryGetValue(s.Name, out var check);
                var st = check?.State;

                var cells = new[]
                {
                    s.Name,
                    s.StateText,
                    st is null ? "" : ServiceState.Describe(st.State),
                    st is null || st.State != SupervisorState.Running ? "" : $"{st.CpuPercent:0.#} %",
                    st is null || st.State != SupervisorState.Running ? "" : ServiceState.FormatBytes(st.WorkingSetBytes),
                    st is null ? "" : st.RestartsLastHour.ToString(),
                    st?.Uptime is { } up ? ServiceState.FormatDuration(up) : "",
                    s.StartupText,
                    s.ProcessId == 0 ? "" : s.ProcessId.ToString(),
                    s.DisplayName,
                    s.Account,
                    s.Target,
                };

                ListViewItem item;
                if (i < _list.Items.Count)
                {
                    item = _list.Items[i];
                    for (var c = 0; c < cells.Length; c++)
                        if (item.SubItems[c].Text != cells[c]) item.SubItems[c].Text = cells[c];
                }
                else
                {
                    item = new ListViewItem(cells);
                    _list.Items.Add(item);
                }

                item.Tag = s;

                // Gesundheit schlaegt Laufzustand: ein Dienst, der laeuft und dabei staendig
                // abstuerzt, darf nicht beruhigend gruen aussehen.
                item.ForeColor = check?.Status switch
                {
                    CheckStatus.Critical => Color.FromArgb(190, 30, 30),
                    CheckStatus.Warning => Color.FromArgb(180, 95, 0),
                    CheckStatus.Unknown => SystemColors.GrayText,
                    CheckStatus.Ok => Color.FromArgb(0, 110, 40),
                    _ => s.IsRunning ? Color.FromArgb(0, 110, 40)
                        : s.Startup == StartupType.Disabled ? SystemColors.GrayText
                        : _list.ForeColor,
                };
                item.Font = s.ManagedByEasyService ? new Font(_list.Font, FontStyle.Bold) : _list.Font;
                item.ToolTipText = check?.Summary ?? "";
            }

            if (previous is not null)
            {
                foreach (ListViewItem item in _list.Items)
                    if (((ServiceInfo)item.Tag!).Name == previous)
                    {
                        item.Selected = true;
                        break;
                    }
            }
            else if (_initialSelection is not null)
            {
                foreach (ListViewItem item in _list.Items)
                    if (string.Equals(((ServiceInfo)item.Tag!).Name, _initialSelection, StringComparison.OrdinalIgnoreCase))
                    {
                        item.Selected = true;
                        item.EnsureVisible();
                        break;
                    }
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        var managed = _services.Count(s => s.ManagedByEasyService);
        var critical = _checks.Values.Count(c => c.Status == CheckStatus.Critical);
        var warning = _checks.Values.Count(c => c.Status == CheckStatus.Warning);
        var health = critical > 0 || warning > 0
            ? $" - {critical} kritisch, {warning} Warnung"
            : managed > 0 ? " - alle unauffällig" : "";
        _status.Text = $"{visible.Count} von {_services.Count} Diensten angezeigt - " +
                       $"{managed} von EasyService verwaltet{health}";
        UpdateButtons();
    }

    private List<ServiceInfo> Sort(List<ServiceInfo> items)
    {
        if (_sortColumn < 0)
            return items.OrderByDescending(s => s.ManagedByEasyService)
                        .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

        Func<ServiceInfo, object> key = _sortColumn switch
        {
            1 => s => s.StateText,
            2 => s => State(s)?.State.ToString() ?? "",
            3 => s => State(s)?.CpuPercent ?? -1,
            4 => s => State(s)?.WorkingSetBytes ?? -1,
            5 => s => State(s)?.RestartsLastHour ?? -1,
            6 => s => State(s)?.Uptime?.TotalSeconds ?? -1,
            7 => s => s.StartupText,
            8 => s => s.ProcessId,
            9 => s => s.DisplayName,
            10 => s => s.Account,
            11 => s => s.Target,
            _ => s => s.Name,
        };
        return (_sortAscending ? items.OrderBy(key) : items.OrderByDescending(key)).ToList();
    }

    private ServiceState? State(ServiceInfo info) =>
        _checks.TryGetValue(info.Name, out var check) ? check.State : null;

    private void OnColumnClick(object? sender, ColumnClickEventArgs e)
    {
        if (_sortColumn == e.Column) _sortAscending = !_sortAscending;
        else { _sortColumn = e.Column; _sortAscending = true; }
        ApplyFilter();
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete) { RemoveSelected(); e.Handled = true; }
        else if (e.KeyCode == Keys.Enter) { EditSelected(); e.Handled = true; }
    }

    private void UpdateButtons()
    {
        var s = Selected;
        var managed = s?.ManagedByEasyService == true;
        _btnEdit.Enabled = managed;
        _btnLogs.Enabled = managed;
        _btnRemove.Enabled = s is not null;
        _btnStart.Enabled = s is { IsStopped: true, Startup: not StartupType.Disabled };
        _btnStop.Enabled = s is { IsRunning: true };
        _btnRestart.Enabled = s is { IsRunning: true };
    }

    // ------------------------------------------------------------- actions ---

    private void CreateNew(string? program = null)
    {
        using var dlg = new QuickAddForm(program);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var config = dlg.Config;
        if (!Run($"Dienst \"{config.ServiceName}\" wird angelegt", () => ServiceRegistry.Install(config)))
        {
            Reload();
            return;
        }

        if (dlg.StartAfterCreate &&
            !Run($"Dienst \"{config.ServiceName}\" wird gestartet",
                 () => ServiceRegistry.Start(config.ServiceName, TimeSpan.FromSeconds(60))))
        {
            // Ein fehlgeschlagener erster Start ist fast immer ein falscher Pfad oder ein
            // falsches Argument - das steht im Protokoll, also direkt dorthin anbieten.
            if (Ui.Confirm(this, "Start fehlgeschlagen",
                    $"Der Dienst \"{config.ServiceName}\" wurde angelegt, ließ sich aber nicht starten." +
                    System.Environment.NewLine + System.Environment.NewLine +
                    "Die Ursache steht meist im Protokoll. Jetzt öffnen?"))
                new LogViewerForm(config).Show(this);
        }

        Reload();
    }

    private void EditSelected()
    {
        if (Selected is not { } s) return;
        if (!s.ManagedByEasyService)
        {
            Ui.ShowInfo(this, "Nicht bearbeitbar",
                $"\"{s.Name}\" wurde nicht mit EasyService angelegt und kann hier nicht bearbeitet werden.\n\n" +
                "EasyService bearbeitet nur Dienste, die es selbst verwaltet, damit fremde Dienste nicht beschädigt werden.");
            return;
        }

        var config = ServiceConfig.Load(s.Name);
        if (config is null)
        {
            Ui.ShowError(this, "Konfiguration fehlt", $"Für \"{s.Name}\" ist keine EasyService-Konfiguration hinterlegt.");
            return;
        }

        config.DisplayName = s.DisplayName;
        config.Description = ServiceRegistry.GetDescription(s.Name);
        config.Startup = s.Startup;
        config.Dependencies = ServiceRegistry.GetDependencies(s.Name);
        ApplyAccount(config, s.Account);

        using var dlg = new ServiceEditorForm(config, isNew: false);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var wasRunning = s.IsRunning;
        Run($"Dienst \"{s.Name}\" wird aktualisiert", () => ServiceRegistry.Update(dlg.Config));

        if (wasRunning && Ui.Confirm(this, "Neu starten?",
                $"Die Änderungen an \"{s.Name}\" werden erst nach einem Neustart des Dienstes wirksam.\n\nJetzt neu starten?"))
            Run($"Dienst \"{s.Name}\" wird neu gestartet",
                () => ServiceRegistry.Restart(s.Name, TimeSpan.FromSeconds(60)));

        Reload();
    }

    private static void ApplyAccount(ServiceConfig config, string account)
    {
        var a = (account ?? "").Trim();
        if (a.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase))
            config.Logon = LogonType.LocalSystem;
        else if (a.EndsWith(@"\LocalService", StringComparison.OrdinalIgnoreCase))
            config.Logon = LogonType.LocalService;
        else if (a.EndsWith(@"\NetworkService", StringComparison.OrdinalIgnoreCase))
            config.Logon = LogonType.NetworkService;
        else if (a.Length > 0)
        {
            config.Logon = LogonType.Account;
            config.AccountName = a;
        }
    }

    private enum ServiceAction { Start, Stop, Restart }

    private void Control(ServiceAction action)
    {
        if (Selected is not { } s) return;
        var timeout = TimeSpan.FromSeconds(60);
        switch (action)
        {
            case ServiceAction.Start:
                Run($"Dienst \"{s.Name}\" wird gestartet", () => ServiceRegistry.Start(s.Name, timeout));
                break;
            case ServiceAction.Stop:
                Run($"Dienst \"{s.Name}\" wird beendet", () => ServiceRegistry.Stop(s.Name, timeout));
                break;
            case ServiceAction.Restart:
                Run($"Dienst \"{s.Name}\" wird neu gestartet", () => ServiceRegistry.Restart(s.Name, timeout));
                break;
        }
        Reload();
    }

    private void RemoveSelected()
    {
        if (Selected is not { } s) return;

        var warning = s.ManagedByEasyService
            ? $"Der Dienst \"{s.Name}\" wird beendet und dauerhaft entfernt.\n\nDie Protokolldateien bleiben erhalten."
            : $"ACHTUNG: \"{s.Name}\" wurde NICHT mit EasyService angelegt.\n\n" +
              "Das Entfernen eines fremden Systemdienstes kann Windows oder installierte Software unbrauchbar machen.\n\n" +
              "Wirklich entfernen?";

        if (!Ui.Confirm(this, "Dienst entfernen", warning)) return;

        if (!s.ManagedByEasyService)
        {
            using var confirm = new TextConfirmDialog(s.Name);
            if (confirm.ShowDialog(this) != DialogResult.OK) return;
        }

        Run($"Dienst \"{s.Name}\" wird entfernt", () => ServiceRegistry.Remove(s.Name));
        Reload();
    }

    private void ShowLogs()
    {
        if (Selected is not { } s) return;
        var config = ServiceConfig.Load(s.Name);
        if (config is null)
        {
            Ui.ShowInfo(this, "Keine Protokolle", $"Für \"{s.Name}\" ist keine EasyService-Konfiguration hinterlegt.");
            return;
        }
        new LogViewerForm(config).Show(this);
    }

    /// <summary>
    /// Runs a blocking SCM call with an hourglass and turns failures into a dialog.
    /// Returns false when it failed, so callers can offer a next step.
    /// </summary>
    private bool Run(string what, Action action)
    {
        _refreshTimer.Stop();
        var previousCursor = Cursor;
        Cursor = Cursors.WaitCursor;
        _status.Text = what + "...";
        Application.DoEvents();
        try
        {
            action();
            _status.Text = what + " - fertig.";
            return true;
        }
        catch (Exception e)
        {
            Ui.ShowError(this, what, e);
            _status.Text = what + " - fehlgeschlagen.";
            return false;
        }
        finally
        {
            Cursor = previousCursor;
            _refreshTimer.Start();
        }
    }
}

/// <summary>Type-the-name confirmation used before deleting a service EasyService did not create.</summary>
internal sealed class TextConfirmDialog : Form
{
    public TextConfirmDialog(string expected)
    {
        Text = "Löschen bestätigen";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ClientSize = new Size(430, 150);

        var label = new Label
        {
            Text = $"Zum Bestätigen bitte den Dienstnamen eingeben:\n\n{expected}",
            AutoSize = false,
            Bounds = new Rectangle(14, 14, 400, 60),
        };
        var box = new TextBox { Bounds = new Rectangle(14, 78, 400, 24) };
        var ok = new Button { Text = "Entfernen", DialogResult = DialogResult.OK, Bounds = new Rectangle(228, 112, 90, 28), Enabled = false };
        var cancel = new Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(324, 112, 90, 28) };

        box.TextChanged += (_, _) => ok.Enabled = string.Equals(box.Text.Trim(), expected, StringComparison.Ordinal);

        Controls.AddRange(new Control[] { label, box, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
