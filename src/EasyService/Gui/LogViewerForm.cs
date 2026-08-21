using System.Text;
using EasyService.Core;

using EasyService.Resources;

namespace EasyService.Gui;

/// <summary>
/// Live log viewer. Tails the file while the service keeps writing to it, follows
/// rotation, and offers the rotated archives plus the Windows event log entries.
/// </summary>
public sealed class LogViewerForm : Form
{
    private const int MaxLines = 20_000;

    private readonly ServiceConfig _config;

    private readonly ComboBox _fileSelector = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly TextBox _view = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.None,
    };
    private readonly ToolStripTextBox _filter = new() { Width = 200 };
    private readonly ToolStripButton _follow = new(S.Log_Btn_Follow)
    {
        CheckOnClick = true,
        Checked = true,
        DisplayStyle = ToolStripItemDisplayStyle.Text,
        ToolTipText = S.Log_Tip_Follow,
    };
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 750 };

    private readonly ListView _events = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
    };

    private readonly List<string> _lines = new();
    private FileStream? _stream;
    private string? _currentPath;
    private long _position;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private string _partialLine = "";
    private string _lastFilter = "";

    public LogViewerForm(ServiceConfig config)
    {
        _config = config;

        Text = S.Log_Title(config.ServiceName);
        Icon = Ui.AppIcon;
        Size = new Size(1060, 660);
        MinimumSize = new Size(720, 400);
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? Font;
        _view.Font = Ui.MonoFont;

        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 2, 6, 2) };
        toolbar.Items.Add(new ToolStripLabel(S.Log_Lbl_File));
        toolbar.Items.Add(new ToolStripControlHost(_fileSelector));
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(_follow);
        toolbar.Items.Add(new ToolStripLabel(S.Log_Lbl_Filter));
        toolbar.Items.Add(_filter);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(Button(S.Log_Btn_OpenFolder, (_, _) => Ui.OpenInExplorer(_currentPath ?? _config.StdoutPath)));
        toolbar.Items.Add(Button(S.Log_Btn_SaveAs, (_, _) => SaveAs()));
        toolbar.Items.Add(Button(S.Log_Btn_Clear, (_, _) => TruncateCurrent()));
        toolbar.Items.Add(Button(S.Log_Btn_Reload, (_, _) => OpenSelected(force: true)));

        var logPage = new TabPage(S.Log_Tab_File);
        logPage.Controls.Add(_view);

        _events.Columns.Add(S.Log_Col_Time, 150);
        _events.Columns.Add(S.Log_Col_Type, 90);
        _events.Columns.Add(S.Log_Col_Id, 55, HorizontalAlignment.Right);
        _events.Columns.Add(S.Log_Col_Event, 200);
        _events.Columns.Add(S.Log_Col_Message, 620);
        var eventsPage = new TabPage(S.Log_Tab_Events);
        var eventsToolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        eventsToolbar.Items.Add(Button(S.Log_Btn_RefreshEvents, (_, _) => LoadEvents()));
        eventsToolbar.Items.Add(Button(S.Log_Btn_EventViewer, (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("eventvwr.msc") { UseShellExecute = true }); }
            catch (Exception e) { Ui.ShowError(this, S.Log_EventViewer, e); }
        }));
        eventsPage.Controls.Add(_events);
        eventsPage.Controls.Add(eventsToolbar);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(logPage);
        tabs.TabPages.Add(eventsPage);
        tabs.SelectedIndexChanged += (_, _) =>
        {
            if (tabs.SelectedTab == eventsPage && _events.Items.Count == 0) LoadEvents();
        };

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);

        Controls.Add(tabs);
        Controls.Add(toolbar);
        Controls.Add(statusStrip);

        _fileSelector.SelectedIndexChanged += (_, _) => OpenSelected(force: true);
        _filter.TextChanged += (_, _) => Render(full: true);
        _timer.Tick += (_, _) => Poll();

        Load += (_, _) =>
        {
            PopulateFileList();
            OpenSelected(force: true);
            _timer.Start();
        };
        FormClosed += (_, _) =>
        {
            _timer.Stop();
            _stream?.Dispose();
        };
    }

    private static ToolStripButton Button(string text, EventHandler onClick)
    {
        var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text };
        b.Click += onClick;
        return b;
    }

    private sealed record LogFile(string Label, string Path)
    {
        public override string ToString() => Label;
    }

    private void PopulateFileList()
    {
        var previous = (_fileSelector.SelectedItem as LogFile)?.Path;

        var entries = new List<LogFile>();
        void AddGroup(string label, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var files = LogWriter.FindLogFiles(path);
            for (var i = 0; i < files.Count; i++)
                entries.Add(new LogFile(i == 0 && IsCurrent(files[i], path)
                    ? S.Log_Current(label)
                    : S.Log_Entry(label, Path.GetFileName(files[i])), files[i]));
        }

        AddGroup("stdout", _config.StdoutPath);
        if (!string.Equals(_config.StderrPath, _config.StdoutPath, StringComparison.OrdinalIgnoreCase))
            AddGroup("stderr", _config.StderrPath);
        AddGroup("EasyService", _config.ServiceLogPath);

        _fileSelector.BeginUpdate();
        _fileSelector.Items.Clear();
        foreach (var e in entries) _fileSelector.Items.Add(e);
        _fileSelector.EndUpdate();

        if (entries.Count == 0)
        {
            _status.Text = S.Log_NoFiles;
            return;
        }

        var index = previous is null ? 0 : entries.FindIndex(e => e.Path == previous);
        _fileSelector.SelectedIndex = index < 0 ? 0 : index;
    }

    private static bool IsCurrent(string file, string configured)
    {
        try
        {
            return string.Equals(Path.GetFullPath(file),
                Path.GetFullPath(System.Environment.ExpandEnvironmentVariables(configured)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void OpenSelected(bool force)
    {
        if (_fileSelector.SelectedItem is not LogFile file) return;
        if (!force && file.Path == _currentPath) return;

        _stream?.Dispose();
        _stream = null;
        _lines.Clear();
        _partialLine = "";
        _position = 0;
        _currentPath = file.Path;
        _view.Clear();

        try
        {
            _stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }
        catch (FileNotFoundException)
        {
            _status.Text = S.Log_NotFound(file.Path);
            return;
        }
        catch (Exception e)
        {
            _status.Text = S.Main_Status_Error(e.Message);
            return;
        }

        ReadNewData();
        Render(full: true);
        ScrollToEnd();
    }

    private void Poll()
    {
        if (_stream is null || _currentPath is null) return;
        try
        {
            var info = new FileInfo(_currentPath);
            if (!info.Exists || info.Length < _position)
            {
                // The file was rotated away underneath us: pick up the new one.
                PopulateFileList();
                OpenSelected(force: true);
                return;
            }
            if (info.Length == _position) return;

            var before = _lines.Count;
            ReadNewData();
            if (_lines.Count != before || _view.TextLength == 0) Render(full: false);
        }
        catch (IOException)
        {
        }
    }

    private void ReadNewData()
    {
        if (_stream is null) return;

        _stream.Seek(_position, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        var chars = new char[64 * 1024];
        var sb = new StringBuilder(_partialLine);
        _partialLine = "";

        int read;
        while ((read = _stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            var count = _decoder.GetChars(buffer, 0, read, chars, 0);
            sb.Append(chars, 0, count);
            _position += read;
        }

        var text = sb.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
        var parts = text.Split('\n');
        for (var i = 0; i < parts.Length - 1; i++) _lines.Add(parts[i]);
        _partialLine = parts[^1];

        if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
    }

    private void Render(bool full)
    {
        var filter = _filter.Text.Trim();
        var filterChanged = filter != _lastFilter;
        _lastFilter = filter;

        IEnumerable<string> lines = _lines;
        if (filter.Length > 0)
            lines = lines.Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase));

        if (full || filterChanged || filter.Length > 0)
        {
            var wasAtEnd = _follow.Checked;
            _view.Text = string.Join(System.Environment.NewLine, lines);
            if (wasAtEnd) ScrollToEnd();
        }
        else
        {
            // Fast path: nothing filtered, just append what arrived since the last render.
            var rendered = _view.Lines.Length;
            if (rendered < _lines.Count)
            {
                var appended = string.Join(System.Environment.NewLine, _lines.Skip(rendered));
                _view.AppendText((_view.TextLength > 0 ? System.Environment.NewLine : "") + appended);
                if (_follow.Checked) ScrollToEnd();
            }
        }

        var shownCount = filter.Length > 0 ? lines.Count() : _lines.Count;
        var size = "";
        try
        {
            if (_currentPath is not null && File.Exists(_currentPath))
                size = S.Log_Size($"{new FileInfo(_currentPath).Length / 1024.0:N1}");
        }
        catch (IOException) { }

        _status.Text = filter.Length > 0
            ? S.Log_Status_Filtered(shownCount, _lines.Count, size, _currentPath)
            : S.Log_Status_Plain(_lines.Count, size, _currentPath);
    }

    private void ScrollToEnd()
    {
        if (_view.TextLength == 0) return;
        _view.SelectionStart = _view.TextLength;
        _view.SelectionLength = 0;
        _view.ScrollToCaret();
    }

    private void SaveAs()
    {
        if (_currentPath is null) return;
        using var dlg = new SaveFileDialog
        {
            FileName = Path.GetFileName(_currentPath),
            Filter = S.Common_FilterSaveLog,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName, _view.Text);
            _status.Text = S.Log_Saved(dlg.FileName);
        }
        catch (Exception e)
        {
            Ui.ShowError(this, S.Log_SaveFailed, e);
        }
    }

    private void TruncateCurrent()
    {
        if (_currentPath is null) return;
        if (!Ui.Confirm(this, S.Log_Clear_Title, S.Log_Clear_Text(_currentPath)))
            return;

        try
        {
            _stream?.Dispose();
            _stream = null;
            using (var fs = new FileStream(_currentPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                fs.SetLength(0);
            OpenSelected(force: true);
        }
        catch (Exception e)
        {
            Ui.ShowError(this, S.Log_Clear_Failed,
                e is IOException ? S.Log_Clear_Locked(e.Message) : e.Message);
            OpenSelected(force: true);
        }
    }

    private void LoadEvents()
    {
        _events.BeginUpdate();
        _events.Items.Clear();
        foreach (var entry in EventLogSink.ReadRecent(_config.ServiceName))
        {
            var text = entry.Message;
            var prefix = $"[{_config.ServiceName}] ";
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) text = text[prefix.Length..];

            var item = new ListViewItem(new[]
            {
                entry.Time.ToString("yyyy-MM-dd HH:mm:ss"),
                entry.Type,
                entry.EventId.ToString(),
                EventLogSink.Describe(entry.EventId),
                text.ReplaceLineEndings(" "),
            });
            if (entry.Type.Equals("Error", StringComparison.OrdinalIgnoreCase)) item.ForeColor = Color.Firebrick;
            else if (entry.Type.Equals("Warning", StringComparison.OrdinalIgnoreCase)) item.ForeColor = Color.DarkOrange;
            _events.Items.Add(item);
        }
        _events.EndUpdate();
        if (_events.Items.Count == 0)
            _events.Items.Add(new ListViewItem(new[] { "", "", "", "", S.Log_NoEvents }));
    }
}
