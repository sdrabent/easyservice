using System.Globalization;
using System.Text;
using EasyService.Core;
using EasyService.Resources;

namespace EasyService.Gui;

/// <summary>
/// What a service has cost and what has happened to it over time.
///
/// The state file answers "how is it right now"; this answers "how has it been
/// behaving". For a supervised application those are different questions: a service
/// that is fine at this second may have restarted forty times overnight.
/// </summary>
public sealed class HistoryForm : Form
{
    private readonly ServiceConfig _config;

    private readonly ComboBox _range = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly TimeSeriesChart _cpu = new();
    private readonly TimeSeriesChart _memory = new();
    private readonly ListView _events = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
    };
    private readonly FlowLayoutPanel _stats = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false,
        Padding = new Padding(4, 6, 4, 6),
    };
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 30_000 };

    private static readonly TimeSpan[] Ranges =
    {
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(24),
        TimeSpan.FromDays(7),
        TimeSpan.FromDays(30),
    };

    private List<MetricSample> _samples = new();
    private List<HistoryEvent> _history = new();

    public HistoryForm(ServiceConfig config)
    {
        _config = config;

        Text = S.Hist_Title(config.ServiceName);
        Icon = Ui.AppIcon;
        Size = new Size(1040, 780);
        MinimumSize = new Size(760, 560);
        StartPosition = FormStartPosition.CenterParent;
        Font = SystemFonts.MessageBoxFont ?? Font;

        _range.Items.AddRange(new object[] { S.Hist_Range_1h, S.Hist_Range_24h, S.Hist_Range_7d, S.Hist_Range_30d });
        _range.SelectedIndex = 1;
        _range.SelectedIndexChanged += (_, _) => Reload();

        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 2, 6, 2) };
        toolbar.Items.Add(new ToolStripLabel(S.Hist_Lbl_Range));
        toolbar.Items.Add(new ToolStripControlHost(_range));
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(Button(S.Main_Btn_Refresh, (_, _) => Reload()));
        toolbar.Items.Add(Button(S.Hist_Btn_Export, (_, _) => Export()));
        toolbar.Items.Add(Button(S.Hist_Btn_OpenFolder, (_, _) => Ui.OpenInExplorer(HistoryStore.DirectoryPath)));

        _cpu.Title = S.Hist_Chart_Cpu;
        _cpu.FixedMaximum = 100;
        _cpu.Format = v => v.ToString("0.#", CultureInfo.CurrentCulture) + " %";
        _cpu.EmptyText = config.HistoryDays > 0 ? S.Hist_Empty : S.Hist_Disabled;

        // Second categorical slot, so the two charts stay visually distinct even though
        // each is a single series and needs no legend of its own.
        _memory.Title = S.Hist_Chart_Memory;
        _memory.SeriesLight = Color.FromArgb(0x1b, 0xaf, 0x7a);
        _memory.SeriesDark = Color.FromArgb(0x19, 0x9e, 0x70);
        // FormatBytes liefert für 0 absichtlich einen Strich ("keine Daten") - auf einer
        // Achse ist der Nullpunkt aber ein echter Wert.
        _memory.Format = v => v <= 0 ? "0 B" : ServiceState.FormatBytes((long)v);
        _memory.EmptyText = _cpu.EmptyText;

        _events.Columns.Add(S.Hist_Col_Time, 145);
        _events.Columns.Add(S.Hist_Col_Event, 210);
        _events.Columns.Add(S.Hist_Col_ExitCode, 80, HorizontalAlignment.Right);
        _events.Columns.Add(S.Hist_Col_Detail, 560);

        var eventsPage = new TabPage(S.Hist_Group_Events);
        eventsPage.Controls.Add(_events);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(eventsPage);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            RowStyles =
            {
                new RowStyle(SizeType.AutoSize),
                new RowStyle(SizeType.Percent, 30),
                new RowStyle(SizeType.Percent, 30),
                new RowStyle(SizeType.AutoSize),
                new RowStyle(SizeType.Percent, 40),
            },
        };
        _stats.Height = 62;
        _cpu.Dock = DockStyle.Fill;
        _memory.Dock = DockStyle.Fill;

        layout.Controls.Add(_stats, 0, 0);
        layout.Controls.Add(_cpu, 0, 1);
        layout.Controls.Add(_memory, 0, 2);
        layout.Controls.Add(Ui.Hint(S.Hist_MarkerLegend), 0, 3);
        layout.Controls.Add(tabs, 0, 4);

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);

        Controls.Add(layout);
        Controls.Add(toolbar);
        Controls.Add(statusStrip);

        _refresh.Tick += (_, _) => Reload();
        Load += (_, _) => { Reload(); _refresh.Start(); };
        FormClosed += (_, _) => _refresh.Stop();
    }

    private static ToolStripButton Button(string text, EventHandler onClick)
    {
        var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text };
        b.Click += onClick;
        return b;
    }

    // ------------------------------------------------------------------ laden ---

    private void Reload()
    {
        var since = DateTime.UtcNow - Ranges[Math.Clamp(_range.SelectedIndex, 0, Ranges.Length - 1)];

        _samples = HistoryStore.ReadMetrics(_config.ServiceName, since);
        _history = HistoryStore.ReadEvents(_config.ServiceName, since);

        var starts = _history.Where(e => e.EventId == (int)EasyServiceEvent.ApplicationStarted)
                             .Select(e => e.Utc).ToList();

        _cpu.SetData(_samples.Select(s => new TimeSeriesChart.Point(s.Utc, s.CpuAverage, s.CpuPeak)).ToList(), starts);
        _memory.SetData(_samples.Select(s => new TimeSeriesChart.Point(s.Utc, s.MemoryAverage, s.MemoryPeak)).ToList(), starts);

        BuildStats(HistoryStore.Summarize(_samples, _history));
        BuildEvents();

        _status.Text = S.Hist_Status(_samples.Count, _history.Count);
    }

    private void BuildStats(HistoryStore.Summary summary)
    {
        _stats.SuspendLayout();
        _stats.Controls.Clear();

        // Restarts first: it is the number that tells an administrator whether the
        // service has been healthy, and the one Windows itself cannot answer.
        AddStat(S.Hist_Stat_Restarts, summary.Restarts.ToString(CultureInfo.CurrentCulture),
                summary.Restarts > 0);
        AddStat(S.Hist_Stat_CpuAvg, $"{summary.CpuAverage.ToString("0.#", CultureInfo.CurrentCulture)} %", false);
        AddStat(S.Hist_Stat_CpuPeak, $"{summary.CpuPeak.ToString("0.#", CultureInfo.CurrentCulture)} %", false);
        AddStat(S.Hist_Stat_MemAvg, ServiceState.FormatBytes(summary.MemoryAverage), false);
        AddStat(S.Hist_Stat_MemPeak, ServiceState.FormatBytes(summary.MemoryPeak), false);
        AddStat(S.Hist_Stat_Covered,
                summary.Covered > TimeSpan.Zero ? ServiceState.FormatDuration(summary.Covered) : "-", false);

        _stats.ResumeLayout();
    }

    /// <summary>A stat tile: the number reads first, the caption explains it.</summary>
    private void AddStat(string caption, string value, bool emphasise)
    {
        var panel = new Panel { Width = 150, Height = 48, Margin = new Padding(0, 0, 18, 0) };

        var big = new Label
        {
            Text = value,
            Font = new Font(Font.FontFamily, Font.Size + 5f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(0, 0),
            ForeColor = emphasise
                ? Ui.HealthColor(CheckStatus.Warning, this, ForeColor)
                : ForeColor,
        };
        var small = new Label
        {
            Text = caption,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Location = new Point(1, 27),
        };

        panel.Controls.Add(big);
        panel.Controls.Add(small);
        _stats.Controls.Add(panel);
    }

    private void BuildEvents()
    {
        _events.BeginUpdate();
        _events.Items.Clear();

        // Newest first: the interesting event is almost always the most recent one.
        foreach (var e in Enumerable.Reverse(_history))
        {
            var item = new ListViewItem(new[]
            {
                e.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                EventLogSink.Describe(e.EventId),
                e.ExitCode?.ToString(CultureInfo.CurrentCulture) ?? "",
                Strip(e.Detail),
            });

            if (e.EventId is (int)EasyServiceEvent.ApplicationStartFailed
                or (int)EasyServiceEvent.ConfigurationProblem)
                item.ForeColor = Ui.HealthColor(CheckStatus.Critical, _events, _events.ForeColor);
            else if (e.EventId is (int)EasyServiceEvent.RestartThrottled
                     or (int)EasyServiceEvent.ApplicationTerminated)
                item.ForeColor = Ui.HealthColor(CheckStatus.Warning, _events, _events.ForeColor);

            _events.Items.Add(item);
        }

        if (_events.Items.Count == 0)
            _events.Items.Add(new ListViewItem(new[] { "", "", "", S.Hist_NoEvents }));

        _events.EndUpdate();
    }

    /// <summary>The stored detail carries the service name prefix; the window already knows it.</summary>
    private string Strip(string detail)
    {
        var prefix = $"[{_config.ServiceName}] ";
        return detail.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? detail[prefix.Length..] : detail;
    }

    // ---------------------------------------------------------------- export ---

    private void Export()
    {
        using var dialog = new SaveFileDialog
        {
            FileName = $"{_config.ServiceName}-history.csv",
            Filter = S.Common_FilterSaveLog,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("utc,cpu_avg,cpu_max,mem_avg,mem_max,procs,restarts_total");
            foreach (var s in _samples)
                sb.AppendLine(string.Join(',',
                    s.Utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                    s.CpuAverage.ToString("0.##", CultureInfo.InvariantCulture),
                    s.CpuPeak.ToString("0.##", CultureInfo.InvariantCulture),
                    s.MemoryAverage.ToString(CultureInfo.InvariantCulture),
                    s.MemoryPeak.ToString(CultureInfo.InvariantCulture),
                    s.Processes.ToString(CultureInfo.InvariantCulture),
                    s.RestartsTotal.ToString(CultureInfo.InvariantCulture)));

            File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
            _status.Text = S.Hist_Exported(dialog.FileName);
        }
        catch (Exception e)
        {
            Ui.ShowError(this, S.Hist_Export_Failed, e);
        }
    }
}
