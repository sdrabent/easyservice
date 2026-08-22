using EasyService.Core;

using EasyService.Resources;

namespace EasyService.Gui;

public sealed class MainForm : Form
{
    private readonly BufferedListView _list;
    private readonly ToolStrip _toolbar;
    private readonly ToolStripStatusLabel _status;
    private readonly ToolStripTextBox _filterBox;
    private readonly ToolStripButton _onlyManaged;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    private readonly ToolStripButton _btnEdit, _btnStart, _btnStop, _btnRestart, _btnLogs, _btnRemove, _btnHistory;

    private List<ServiceInfo> _services = new();
    private Dictionary<string, CheckResult> _checks = new(StringComparer.OrdinalIgnoreCase);
    private bool _loading;
    private int _sortColumn = -1;
    private bool _sortAscending = true;
    private readonly string? _initialSelection;

    private ToolStripMenuItem _menuExport = null!;
    private ImageList? _statusIcons;
    private Panel _empty = null!;
    private SplitContainer _split = null!;
    private ServicePreview _preview = null!;
    private ToolStripButton _btnPreview = null!;
    private Label _emptyTitle = null!, _emptyText = null!;
    private Button _emptyButton = null!;
    private Font? _rowFont, _rowFontBold;
    private readonly bool _openQuickAdd;
    private readonly string? _openHistoryFor;

    public MainForm(string? selectService = null, bool openQuickAdd = false, string? openHistoryFor = null)
    {
        _initialSelection = selectService;
        _openQuickAdd = openQuickAdd;
        _openHistoryFor = openHistoryFor;

        Text = S.Main_Title;
        Icon = Ui.AppIcon;
        MinimumSize = new Size(900, 480);
        Size = new Size(1320, 700);
        StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont ?? Font;

        _list = new BufferedListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            HideSelection = false,
            // Mehrfachauswahl: wer zehn Dienste nach einem Update neu starten muss, soll
            // das nicht zehnmal einzeln tun.
            MultiSelect = true,
            UseCompatibleStateImageBehavior = false,
        };
        _list.Columns.Add(S.Main_Col_Name, 190);
        _list.Columns.Add(S.Main_Col_Status, 110);
        _list.Columns.Add(S.Main_Col_Application, 140);
        _list.Columns.Add(S.Main_Col_Cpu, 60, HorizontalAlignment.Right);
        _list.Columns.Add(S.Main_Col_Ram, 75, HorizontalAlignment.Right);
        _list.Columns.Add(S.Main_Col_Restarts, 65, HorizontalAlignment.Right);
        _list.Columns.Add(S.Main_Col_Uptime, 85, HorizontalAlignment.Right);
        _list.Columns.Add(S.Main_Col_Startup, 145);
        _list.Columns.Add(S.Main_Col_Pid, 60, HorizontalAlignment.Right);
        _list.Columns.Add(S.Main_Col_DisplayName, 200);
        _list.Columns.Add(S.Main_Col_Account, 140);
        _list.Columns.Add(S.Main_Col_Program, 260);
        _list.SelectedIndexChanged += (_, _) => { UpdateButtons(); UpdatePreview(); };
        _list.DoubleClick += (_, _) => ShowHistory();
        _list.ColumnClick += OnColumnClick;
        _list.KeyDown += OnListKeyDown;

        _toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 2, 6, 2) };
        _toolbar.Items.Add(Button(S.Main_Btn_Add, (_, _) => CreateNew(), S.Main_Tip_Add));
        _btnEdit = Button(S.Main_Btn_Edit, (_, _) => EditSelected(), S.Main_Tip_Edit);
        _toolbar.Items.Add(_btnEdit);
        _toolbar.Items.Add(new ToolStripSeparator());
        _btnStart = Button(S.Main_Btn_Start, (_, _) => Control(ServiceAction.Start), null);
        _btnStop = Button(S.Main_Btn_Stop, (_, _) => Control(ServiceAction.Stop), null);
        _btnRestart = Button(S.Main_Btn_Restart, (_, _) => Control(ServiceAction.Restart), null);
        _toolbar.Items.AddRange(new ToolStripItem[] { _btnStart, _btnStop, _btnRestart });
        _toolbar.Items.Add(new ToolStripSeparator());
        _btnHistory = Button(S.Main_Btn_History, (_, _) => ShowHistory(), S.Main_Tip_History);
        _toolbar.Items.Add(_btnHistory);
        _btnLogs = Button(S.Main_Btn_Logs, (_, _) => ShowLogs(), S.Main_Tip_Logs);
        _toolbar.Items.Add(_btnLogs);
        _btnRemove = Button(S.Main_Btn_Remove, (_, _) => RemoveSelected(), S.Main_Tip_Remove);
        _toolbar.Items.Add(_btnRemove);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(Button(S.Main_Btn_Refresh, (_, _) => Reload(), S.Main_Tip_Refresh));
        _btnPreview = new ToolStripButton(S.Main_Btn_Details)
        {
            CheckOnClick = true,
            Checked = UserDefaults.PreviewVisible,
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = S.Main_Tip_Details,
        };
        _btnPreview.CheckedChanged += (_, _) => { ApplyPreviewLayout(); UpdatePreview(); };
        _toolbar.Items.Add(_btnPreview);
        _toolbar.Items.Add(BuildConfigMenu());
        _toolbar.Items.Add(BuildLanguageMenu());

        _toolbar.Items.Add(new ToolStripSeparator());
        _onlyManaged = new ToolStripButton(S.Main_Btn_OnlyManaged)
        {
            CheckOnClick = true,
            Checked = true,
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = S.Main_Tip_OnlyManaged,
        };
        _onlyManaged.CheckedChanged += (_, _) => ApplyFilter();
        _toolbar.Items.Add(_onlyManaged);

        _toolbar.Items.Add(new ToolStripLabel(S.Main_Lbl_Filter));
        _filterBox = new ToolStripTextBox { Width = 190 };
        _filterBox.TextChanged += (_, _) => ApplyFilter();
        _toolbar.Items.Add(_filterBox);

        _status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);

        _list.ShowItemToolTips = true;
        _list.ContextMenuStrip = BuildContextMenu();

        _empty = BuildEmptyState();
        _preview = new ServicePreview { Dock = DockStyle.Fill };

        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            FixedPanel = FixedPanel.Panel2,
            SplitterWidth = 6,
            Panel1MinSize = 120,
            Panel2MinSize = 90,
        };
        _split.Panel1.Controls.Add(_list);
        _split.Panel1.Controls.Add(_empty);
        _split.Panel2.Controls.Add(_preview);

        Controls.Add(_split);
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
            else if (e.Control && e.KeyCode == Keys.N) { CreateNew(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.E) { EditSelected(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.L) { ShowLogs(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.H) { ShowHistory(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.D) { DuplicateSelected(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.F)
            {
                // Strg+F springt ins Filterfeld und markiert, was drin steht - dann tippt
                // man die naechste Suche einfach drueber.
                _filterBox.Focus();
                _filterBox.SelectAll();
                e.Handled = true;
            }
        };

        Load += (_, _) =>
        {
            // Erst hier: vorher stehen weder die tatsaechliche Hintergrundfarbe (hell oder
            // dunkel) noch die DPI des Bildschirms fest, auf dem das Fenster landet.
            _statusIcons = Ui.BuildStatusIcons(_list);
            _list.SmallImageList = _statusIcons;
            _rowFont = _list.Font;
            _rowFontBold = new Font(_list.Font, FontStyle.Bold);
            Ui.FollowTheme(this);

            RestoreWindowState();
            ApplyPreviewLayout();
            Reload();
            _refreshTimer.Start();
            if (_openQuickAdd) BeginInvoke(() => CreateNew());
            if (_openHistoryFor is { } service) BeginInvoke(() => ShowHistoryFor(service));
        };
        FormClosing += (_, _) =>
        {
            SaveWindowState();
            UserDefaults.PreviewVisible = _btnPreview.Checked;
            if (_btnPreview.Checked) UserDefaults.PreviewHeight = _split.Panel2.Height;
        };
        FormClosed += (_, _) =>
        {
            _refreshTimer.Stop();
            _statusIcons?.Dispose();
            _rowFontBold?.Dispose();
        };
    }

    // ------------------------------------------------------- Fensterzustand ---

    private void RestoreWindowState()
    {
        if (UserDefaults.MainWindowBounds is { } bounds && IsOnAScreen(bounds))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
        }
        if (UserDefaults.MainWindowMaximized) WindowState = FormWindowState.Maximized;

        var widths = UserDefaults.ColumnWidths;
        for (var i = 0; i < Math.Min(widths.Length, _list.Columns.Count); i++)
            if (widths[i] > 10) _list.Columns[i].Width = widths[i];

        _sortColumn = UserDefaults.SortColumn;
        _sortAscending = UserDefaults.SortAscending;
        _onlyManaged.Checked = UserDefaults.OnlyManaged;
    }

    /// <summary>Guards against restoring onto a monitor that is no longer attached.</summary>
    private static bool IsOnAScreen(Rectangle bounds) =>
        bounds.Width > 200 && bounds.Height > 150 &&
        Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds));

    private void SaveWindowState()
    {
        UserDefaults.MainWindowMaximized = WindowState == FormWindowState.Maximized;
        if (WindowState == FormWindowState.Normal) UserDefaults.MainWindowBounds = Bounds;

        UserDefaults.ColumnWidths = _list.Columns.Cast<ColumnHeader>().Select(c => c.Width).ToArray();
        UserDefaults.SortColumn = _sortColumn;
        UserDefaults.SortAscending = _sortAscending;
        UserDefaults.OnlyManaged = _onlyManaged.Checked;
    }

    private static ToolStripButton Button(string text, EventHandler onClick, string? tip)
    {
        var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = tip };
        b.Click += onClick;
        return b;
    }

    /// <summary>
    /// Language picker. The choice is stored per user and applied on the next start -
    /// rebuilding every open window at runtime buys little for a setting changed once.
    /// </summary>
    private ToolStripDropDownButton BuildLanguageMenu()
    {
        var button = new ToolStripDropDownButton(S.Main_Menu_Language)
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
        };

        foreach (var language in Localization.Supported)
        {
            var caption = language.Code.Length == 0 ? S.Main_Menu_LanguageAuto : language.DisplayName;
            var item = new ToolStripMenuItem(caption)
            {
                Checked = string.Equals(Localization.UserChoice, language.Code, StringComparison.OrdinalIgnoreCase),
                CheckOnClick = false,
            };
            var code = language.Code;
            item.Click += (_, _) =>
            {
                Localization.UserChoice = code;
                foreach (ToolStripMenuItem other in button.DropDownItems)
                    other.Checked = ReferenceEquals(other, item);
                Ui.ShowInfo(this, S.Main_Menu_Language, S.Main_Language_Restart);
            };
            button.DropDownItems.Add(item);
        }

        return button;
    }

    /// <summary>
    /// What the window shows when the list has nothing in it. A first start otherwise looks
    /// like an empty table with twelve column headers and no hint what to do with it.
    /// </summary>
    private Panel BuildEmptyState()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Visible = false };

        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Anchor = AnchorStyles.None,
        };

        _emptyTitle = new Label
        {
            AutoSize = true,
            Font = new Font(Font.FontFamily, Font.Size + 3f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 10),
        };
        _emptyText = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 18),
        };
        _emptyButton = new Button
        {
            Text = S.Main_Btn_Add,
            AutoSize = true,
            Padding = new Padding(14, 6, 14, 6),
        };
        _emptyButton.Click += (_, _) => CreateNew();

        stack.Controls.Add(_emptyTitle);
        stack.Controls.Add(_emptyText);
        stack.Controls.Add(_emptyButton);
        panel.Controls.Add(stack);

        void Centre() => stack.Location = new Point(Math.Max(0, (panel.Width - stack.Width) / 2),
                                                    Math.Max(0, (panel.Height - stack.Height) / 2));
        panel.Resize += (_, _) => Centre();
        stack.SizeChanged += (_, _) => Centre();
        return panel;
    }

    /// <summary>
    /// Opens or closes the detail pane and restores the height the administrator left it at.
    /// </summary>
    private void ApplyPreviewLayout()
    {
        _split.Panel2Collapsed = !_btnPreview.Checked;
        if (!_btnPreview.Checked || _split.Height <= _split.Panel1MinSize + _split.Panel2MinSize) return;

        var wanted = UserDefaults.PreviewHeight > 0 ? UserDefaults.PreviewHeight : Math.Max(160, _split.Height / 4);
        var distance = _split.Height - wanted - _split.SplitterWidth;
        var lowest = _split.Panel1MinSize;
        var highest = _split.Height - _split.Panel2MinSize - _split.SplitterWidth;

        try { _split.SplitterDistance = Math.Clamp(distance, lowest, Math.Max(lowest, highest)); }
        catch (InvalidOperationException) { /* das Fenster ist gerade zu klein dafuer */ }
    }

    private void UpdatePreview()
    {
        if (_btnPreview is null || !_btnPreview.Checked) return;

        var info = Selected;
        _checks.TryGetValue(info?.Name ?? "", out var check);
        _preview.Show(info, check);
    }

    private void UpdateEmptyState(int visibleCount)
    {
        var filtering = _filterBox.Text.Trim().Length > 0;
        var show = visibleCount == 0;

        if (show)
        {
            _emptyTitle.Text = filtering ? S.Main_Empty_Filter_Title : S.Main_Empty_Title;
            _emptyText.Text = filtering
                ? S.Main_Empty_Filter_Text(_filterBox.Text.Trim(), S.Main_Btn_OnlyManaged)
                : S.Main_Empty_Text;
            _emptyButton.Visible = !filtering;
        }

        // Nur eines von beiden sichtbar: zwei auf Fill gedockte Steuerelemente teilen sich
        // sonst den Platz und keines bekommt ihn ganz.
        _list.Visible = !show;
        _empty.Visible = show;
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(MenuItem(S.Main_Btn_History, Keys.Control | Keys.H, ShowHistory));
        menu.Items.Add(MenuItem(S.Main_Btn_Edit, Keys.Control | Keys.E, EditSelected));
        menu.Items.Add(MenuItem(S.Main_Btn_Logs, Keys.Control | Keys.L, ShowLogs));
        menu.Items.Add(MenuItem(S.Main_Btn_Duplicate, Keys.Control | Keys.D, DuplicateSelected));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(S.Main_Btn_Start, null, (_, _) => Control(ServiceAction.Start));
        menu.Items.Add(S.Main_Btn_Stop, null, (_, _) => Control(ServiceAction.Stop));
        menu.Items.Add(S.Main_Btn_Restart, null, (_, _) => Control(ServiceAction.Restart));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(S.Main_Ctx_OpenFolder, null, (_, _) =>
        {
            if (Selected is { } s && ServiceConfig.Load(s.Name) is { } c) Ui.OpenInExplorer(c.Application);
        });
        menu.Items.Add(S.Main_Ctx_ServicesMsc, null, (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("services.msc") { UseShellExecute = true }); }
            catch (Exception e) { Ui.ShowError(this, "services.msc", e); }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(S.Main_Btn_Remove, null, (_, _) => RemoveSelected());
        menu.Opening += (_, e) =>
        {
            if (Selected is null) e.Cancel = true;
        };
        return menu;
    }

    /// <summary>
    /// Menu entry with a shortcut. Windows renders the key combination in the menu itself and
    /// handles it while the menu is closed, so the shortcut is documented where it is used.
    /// </summary>
    private static ToolStripMenuItem MenuItem(string text, Keys shortcut, Action action)
    {
        var item = new ToolStripMenuItem(text, null, (_, _) => action());
        if (shortcut == Keys.None) return item;

        item.ShortcutKeys = shortcut;
        item.ShowShortcutKeys = true;
        return item;
    }

    /// <summary>
    /// A second service from an existing one. Most services on a machine are variations of
    /// each other - same program, another port, another working directory - and setting the
    /// nine tabs up again by hand is how differences creep in.
    /// </summary>
    private void DuplicateSelected()
    {
        if (Selected is not { ManagedByEasyService: true } s) return;

        var config = ServiceRegistry.LoadComplete(s.Name);
        if (config is null)
        {
            Ui.ShowError(this, S.Main_MissingConfig_Title, S.Main_MissingConfig_Text(s.Name));
            return;
        }

        var copy = CopyName(s.Name);

        // Die Protokollpfade tragen den Dienstnamen. Ohne diesen Schritt schreiben Original
        // und Kopie in dieselbe Datei und keiner der beiden Ausgaben ist mehr zu trauen.
        config.StdoutPath = RenameInPath(config.StdoutPath, s.Name, copy);
        config.StderrPath = RenameInPath(config.StderrPath, s.Name, copy);

        config.ServiceName = copy;
        config.DisplayName = "";     // leitet sich wieder vom Namen ab
        config.Password = "";        // Kennwoerter gehoeren dem Original
        config.ApplyDefaultLogPaths();

        using var dlg = new ServiceEditorForm(config, isNew: true);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        Run(S.Main_Task_Install(dlg.Config.ServiceName), () => ServiceRegistry.Install(dlg.Config));
        Reload();
    }

    private static string CopyName(string original)
    {
        var candidate = original + "-copy";
        for (var n = 2; ServiceRegistry.Exists(candidate); n++) candidate = $"{original}-copy{n}";
        return candidate;
    }

    private static string RenameInPath(string path, string from, string to) =>
        string.IsNullOrWhiteSpace(path) ? path : path.Replace(from, to, StringComparison.OrdinalIgnoreCase);

    private ServiceInfo? Selected =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as ServiceInfo : null;

    private List<ServiceInfo> SelectedMany =>
        _list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<ServiceInfo>().ToList();

    // ------------------------------------------------------------- loading ---

    private void Reload(bool silent = false)
    {
        if (_loading) return;
        _loading = true;
        if (!silent) _status.Text = S.Main_Status_Loading;

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
                    Ui.ShowError(this, S.Main_Err_ListFailed, e);
                    _status.Text = S.Main_Status_Error(e.Message);
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

        var previous = _list.SelectedItems.Cast<ListViewItem>()
                            .Select(i => (i.Tag as ServiceInfo)?.Name)
                            .OfType<string>()
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
                var fallback = s.IsRunning ? CheckStatus.Ok
                    : s.Startup == StartupType.Disabled ? CheckStatus.Unknown
                    : (CheckStatus?)null;
                var status = check?.Status ?? fallback;

                // Symbol und Wort tragen den Zustand, nicht die Farbe der ganzen Zeile. Eine
                // durchgefaerbte Zeile ist erstens fuer Rotgruenblinde stumm und zweitens
                // schlecht zu lesen: der Kontoname wird nicht dadurch dringlich, dass der
                // Dienst haengt.
                item.UseItemStyleForSubItems = false;
                item.ImageIndex = Ui.StatusIconIndex(status);
                item.ForeColor = _list.ForeColor;
                item.SubItems[1].ForeColor = Ui.HealthColor(status, _list, _list.ForeColor);
                item.SubItems[2].ForeColor = Ui.HealthColor(status, _list, _list.ForeColor);
                item.Font = s.ManagedByEasyService ? _rowFontBold ?? _list.Font : _rowFont ?? _list.Font;
                item.ToolTipText = check?.Summary ?? "";
            }

            if (previous.Count > 0)
            {
                foreach (ListViewItem item in _list.Items)
                    item.Selected = previous.Contains(((ServiceInfo)item.Tag!).Name);
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
            ? S.Main_Status_Health(critical, warning)
            : managed > 0 ? S.Main_Status_AllFine : "";
        _status.Text = S.Main_Status_Summary(visible.Count, _services.Count, managed, health);
        UpdateEmptyState(visible.Count);
        UpdateButtons();
        UpdatePreview();
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
        ShowSortIndicator();
        ApplyFilter();
    }

    /// <summary>
    /// Marks the sorted column in its header. Without it a click sorts the list and leaves no
    /// trace of what it sorted by, which is the kind of thing that makes people click twice
    /// to find out.
    /// </summary>
    private void ShowSortIndicator()
    {
        for (var i = 0; i < _list.Columns.Count; i++)
        {
            var column = _list.Columns[i];
            var text = column.Text.TrimEnd(' ', '▲', '▼');
            column.Text = i == _sortColumn ? text + (_sortAscending ? " ▲" : " ▼") : text;
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete) { RemoveSelected(); e.Handled = true; }
        else if (e.KeyCode == Keys.Enter) { EditSelected(); e.Handled = true; }
    }

    private void UpdateButtons()
    {
        var many = SelectedMany;

        // Bearbeiten, Verlauf und Protokoll gibt es nur fuer genau einen Dienst - fuer zehn
        // gleichzeitig ergaeben sie kein Fenster, das jemand lesen will.
        var single = many.Count == 1 && many[0].ManagedByEasyService;
        _btnEdit.Enabled = single;
        _btnLogs.Enabled = single;
        _btnHistory.Enabled = single;
        _menuExport.Enabled = single;

        _btnRemove.Enabled = many.Count > 0;
        _btnStart.Enabled = many.Any(s => s.IsStopped && s.Startup != StartupType.Disabled);
        _btnStop.Enabled = many.Any(s => s.IsRunning);
        _btnRestart.Enabled = many.Any(s => s.IsRunning);
    }

    // ------------------------------------------------------------- actions ---

    private void CreateNew(string? program = null)
    {
        using var dlg = new QuickAddForm(program);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var config = dlg.Config;
        if (!Run(S.Main_Task_Install(config.ServiceName), () => ServiceRegistry.Install(config)))
        {
            Reload();
            return;
        }

        if (dlg.StartAfterCreate &&
            !Run(S.Main_Task_Start(config.ServiceName),
                 () => ServiceRegistry.Start(config.ServiceName, TimeSpan.FromSeconds(60))))
        {
            // Ein fehlgeschlagener erster Start ist fast immer ein falscher Pfad oder ein
            // falsches Argument - das steht im Protokoll, also direkt dorthin anbieten.
            if (Ui.Confirm(this, S.Main_StartFailed_Title, S.Main_StartFailed_Text(config.ServiceName)))
                new LogViewerForm(config).Show(this);
        }

        Reload();
    }

    private void EditSelected()
    {
        if (Selected is not { } s) return;
        if (!s.ManagedByEasyService)
        {
            Ui.ShowInfo(this, S.Main_NotEditable_Title, S.Main_NotEditable_Text(s.Name));
            return;
        }

        var config = ServiceRegistry.LoadComplete(s.Name);
        if (config is null)
        {
            Ui.ShowError(this, S.Main_MissingConfig_Title, S.Main_MissingConfig_Text(s.Name));
            return;
        }

        using var dlg = new ServiceEditorForm(config, isNew: false);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var wasRunning = s.IsRunning;
        Run(S.Main_Task_Update(s.Name), () => ServiceRegistry.Update(dlg.Config));

        if (wasRunning && Ui.Confirm(this, S.Main_Restart_Title, S.Main_Restart_Text(s.Name)))
            Run(S.Main_Task_Restart(s.Name), () => ServiceRegistry.Restart(s.Name, TimeSpan.FromSeconds(60)));

        Reload();
    }

    /// <summary>
    /// Export and import of a complete definition. The command line is the more likely
    /// route for a rollout, but the first template usually gets made from a service that
    /// was set up by hand, and that happens here.
    /// </summary>
    private ToolStripDropDownButton BuildConfigMenu()
    {
        var button = new ToolStripDropDownButton(S.Main_Menu_Config)
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
        };

        _menuExport = new ToolStripMenuItem(S.Main_Btn_Export, null, (_, _) => ExportSelected());
        button.DropDownItems.Add(_menuExport);
        button.DropDownItems.Add(new ToolStripMenuItem(S.Main_Btn_ExportAll, null, (_, _) => ExportAll()));
        button.DropDownItems.Add(new ToolStripSeparator());
        button.DropDownItems.Add(new ToolStripMenuItem(S.Main_Btn_Import, null, (_, _) => ImportFromFile()));
        return button;
    }

    private void ExportSelected()
    {
        if (Selected is not { ManagedByEasyService: true } s) return;

        var config = ServiceRegistry.LoadComplete(s.Name);
        if (config is null)
        {
            Ui.ShowError(this, S.Main_MissingConfig_Title, S.Main_MissingConfig_Text(s.Name));
            return;
        }

        WriteConfigFile(s.Name + ".json", () => ConfigTransfer.Export(config), path => S.Cfg_Exported(path));
    }

    private void ExportAll()
    {
        var configs = _services.Where(s => s.ManagedByEasyService)
                               .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                               .Select(s => ServiceRegistry.LoadComplete(s.Name))
                               .Where(c => c is not null)
                               .Select(c => c!)
                               .ToList();

        if (configs.Count == 0)
        {
            Ui.ShowInfo(this, S.Main_Menu_Config, S.Cli_NothingToExport);
            return;
        }

        WriteConfigFile("easyservice-services.json",
            () => ConfigTransfer.ExportMany(configs),
            path => S.Cfg_ExportedMany(configs.Count, path));
    }

    private void WriteConfigFile(string suggestedName, Func<string> content, Func<string, string> message)
    {
        using var dialog = new SaveFileDialog { FileName = suggestedName, Filter = S.Cfg_Filter };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            File.WriteAllText(dialog.FileName, content(), new System.Text.UTF8Encoding(false));
            _status.Text = message(dialog.FileName);
        }
        catch (Exception e)
        {
            Ui.ShowError(this, S.Cfg_Export_Failed, e);
        }
    }

    private void ImportFromFile()
    {
        using var dialog = new OpenFileDialog { Filter = S.Cfg_Filter, CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        List<ServiceConfig> configs;
        try
        {
            configs = ConfigTransfer.Import(File.ReadAllText(dialog.FileName));
        }
        catch (ConfigTransfer.TransferException e)
        {
            Ui.ShowError(this, S.Cfg_Import_Failed, e.Message);
            return;
        }
        catch (Exception e)
        {
            Ui.ShowError(this, S.Cfg_Import_Failed, e);
            return;
        }

        foreach (var config in configs)
        {
            var exists = ServiceRegistry.Exists(config.ServiceName);

            // Dieselbe Regel wie im Rest der Oberflaeche: fremde Dienste fasst
            // EasyService nicht an.
            if (exists && !ServiceRegistry.IsManaged(config.ServiceName))
            {
                Ui.ShowError(this, S.Cfg_Import_Failed, S.Cfg_Err_Foreign(config.ServiceName));
                continue;
            }

            if (exists && !Ui.Confirm(this, S.Cfg_Import_Title, S.Cfg_Import_Overwrite(config.ServiceName)))
                continue;

            // Die Datei enthaelt bewusst kein Kennwort. Beim Aktualisieren behaelt der
            // Dienst-Manager das gespeicherte, beim Anlegen brauchen wir eines.
            if (config.Logon == LogonType.Account && !exists)
            {
                var password = Ui.PromptPassword(this, S.Cfg_Import_Title,
                    S.Cfg_Import_PasswordPrompt(config.AccountName));
                if (password is null) continue;
                config.Password = password;
            }

            var definition = config;
            if (exists)
            {
                if (Run(S.Main_Task_Update(definition.ServiceName), () => ServiceRegistry.Update(definition))
                    && ServiceRegistry.Query(definition.ServiceName)?.IsRunning == true)
                    _status.Text = S.Cfg_Import_Restart;
            }
            else
            {
                Run(S.Main_Task_Install(definition.ServiceName), () => ServiceRegistry.Install(definition));
            }
        }

        Reload();
    }

    private enum ServiceAction { Start, Stop, Restart }

    private void Control(ServiceAction action)
    {
        // Nur die Dienste anfassen, bei denen die Aktion ueberhaupt etwas bedeutet: wer zehn
        // Zeilen markiert und "Beenden" drueckt, meint die laufenden davon.
        var targets = action == ServiceAction.Start
            ? SelectedMany.Where(s => s.IsStopped && s.Startup != StartupType.Disabled).ToList()
            : SelectedMany.Where(s => s.IsRunning).ToList();

        if (targets.Count == 0) return;

        if (targets.Count > 1)
        {
            var question = action switch
            {
                ServiceAction.Start => S.Main_Bulk_Start(targets.Count),
                ServiceAction.Stop => S.Main_Bulk_Stop(targets.Count),
                _ => S.Main_Bulk_Restart(targets.Count),
            };
            if (!Ui.Confirm(this, S.Main_Bulk_Title, question)) return;
        }

        RunMany(targets.Select(s => (Describe(action, s.Name), (Action)(() => Apply(action, s.Name)))).ToList());
        Reload();
    }

    private static void Apply(ServiceAction action, string name)
    {
        var timeout = TimeSpan.FromSeconds(60);
        switch (action)
        {
            case ServiceAction.Start: ServiceRegistry.Start(name, timeout); break;
            case ServiceAction.Stop: ServiceRegistry.Stop(name, timeout); break;
            case ServiceAction.Restart: ServiceRegistry.Restart(name, timeout); break;
        }
    }

    private static string Describe(ServiceAction action, string name) => action switch
    {
        ServiceAction.Start => S.Main_Task_Start(name),
        ServiceAction.Stop => S.Main_Task_Stop(name),
        _ => S.Main_Task_Restart(name),
    };

    private void RemoveSelected()
    {
        var targets = SelectedMany;
        if (targets.Count == 0) return;

        if (targets.Count == 1)
        {
            var s = targets[0];
            var warning = s.ManagedByEasyService
                ? S.Main_Remove_Managed(s.Name)
                : S.Main_Remove_Foreign(s.Name);

            if (!Ui.Confirm(this, S.Main_Remove_Title, warning)) return;
            if (!ConfirmForeign(s)) return;

            Run(S.Main_Task_Remove(s.Name), () => ServiceRegistry.Remove(s.Name));
            Reload();
            return;
        }

        var names = string.Join(Environment.NewLine, targets.Select(t => "    " + t.Name));
        if (!Ui.Confirm(this, S.Main_Remove_Title, S.Main_Remove_Many(targets.Count, names))) return;

        // Fremde Dienste bleiben einzeln zu bestaetigen. Der Tippschutz ist genau fuer den
        // Fall da, dass in einer Mehrfachauswahl versehentlich etwas Fremdes steckt.
        foreach (var foreign in targets.Where(t => !t.ManagedByEasyService))
            if (!ConfirmForeign(foreign)) return;

        RunMany(targets.Select(t => (S.Main_Task_Remove(t.Name), (Action)(() => ServiceRegistry.Remove(t.Name)))).ToList());
        Reload();
    }

    private bool ConfirmForeign(ServiceInfo s)
    {
        if (s.ManagedByEasyService) return true;
        using var confirm = new TextConfirmDialog(s.Name);
        return confirm.ShowDialog(this) == DialogResult.OK;
    }

    private void ShowHistory()
    {
        if (Selected is { } s) ShowHistoryFor(s.Name);
    }

    private void ShowHistoryFor(string serviceName)
    {
        var config = ServiceConfig.Load(serviceName);
        if (config is null)
        {
            Ui.ShowInfo(this, S.Main_NotEditable_Title, S.Main_MissingConfig_Text(serviceName));
            return;
        }
        new HistoryForm(config).Show(this);
    }

    private void ShowLogs()
    {
        if (Selected is not { } s) return;
        var config = ServiceConfig.Load(s.Name);
        if (config is null)
        {
            Ui.ShowInfo(this, S.Main_NoLogs_Title, S.Main_MissingConfig_Text(s.Name));
            return;
        }
        new LogViewerForm(config).Show(this);
    }

    /// <summary>
    /// Runs several SCM calls in a row. One failure must neither stop the remaining ones nor
    /// produce a message box of its own: ten stacked dialogs are worse than one list.
    /// </summary>
    private void RunMany(IReadOnlyList<(string What, Action Do)> tasks)
    {
        if (tasks.Count == 0) return;
        if (tasks.Count == 1) { Run(tasks[0].What, tasks[0].Do); return; }

        _refreshTimer.Stop();
        var previousCursor = Cursor;
        Cursor = Cursors.WaitCursor;
        var failures = new List<string>();
        try
        {
            foreach (var (what, action) in tasks)
            {
                _status.Text = S.Main_Task_Running(what);
                Application.DoEvents();
                try { action(); }
                catch (Exception e) { failures.Add($"{what}: {e.Message}"); }
            }
        }
        finally
        {
            Cursor = previousCursor;
            _refreshTimer.Start();
        }

        if (failures.Count > 0)
            Ui.ShowError(this, S.Main_Bulk_Failed, string.Join(Environment.NewLine, failures));

        _status.Text = S.Main_Bulk_Done(tasks.Count - failures.Count, tasks.Count);
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
        _status.Text = S.Main_Task_Running(what);
        Application.DoEvents();
        try
        {
            action();
            _status.Text = S.Main_Task_Done(what);
            return true;
        }
        catch (Exception e)
        {
            Ui.ShowError(this, what, e);
            _status.Text = S.Main_Task_Failed(what);
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
        Text = S.Confirm_Title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ClientSize = new Size(430, 150);

        var label = new Label
        {
            Text = S.Confirm_Text(expected),
            AutoSize = false,
            Bounds = new Rectangle(14, 14, 400, 60),
        };
        var box = new TextBox { Bounds = new Rectangle(14, 78, 400, 24) };
        var ok = new Button { Text = S.Common_Remove, DialogResult = DialogResult.OK, Bounds = new Rectangle(228, 112, 90, 28), Enabled = false };
        var cancel = new Button { Text = S.Common_Cancel, DialogResult = DialogResult.Cancel, Bounds = new Rectangle(324, 112, 90, 28) };

        box.TextChanged += (_, _) => ok.Enabled = string.Equals(box.Text.Trim(), expected, StringComparison.Ordinal);

        Controls.AddRange(new Control[] { label, box, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
