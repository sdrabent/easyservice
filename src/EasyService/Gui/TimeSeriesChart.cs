using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace EasyService.Gui;

/// <summary>
/// A single-series time chart drawn with GDI+ - no charting library, in keeping with
/// the rest of the project.
///
/// Deliberately one measure per chart. CPU percent and memory bytes live on different
/// scales, and putting them on two y-axes in one frame is the classic way to make a
/// chart that looks informative and reads wrong. Two charts stacked share the x-axis
/// and stay honest.
///
/// The band shows the per-minute peak, the line the per-minute average: a service that
/// idles at 2 % but spikes to 90 % every minute looks very different from one that sits
/// at 40 %, and a single averaged line would hide that.
/// </summary>
internal sealed class TimeSeriesChart : Control
{
    public sealed record Point(DateTime Utc, double Average, double Peak);

    private IReadOnlyList<Point> _points = Array.Empty<Point>();
    private IReadOnlyList<DateTime> _markers = Array.Empty<DateTime>();
    private int? _hoverIndex;

    // Das Steuerelement wird ausschliesslich aus Code aufgebaut, nie im Designer -
    // deshalb ist die Designer-Serialisierung ueberall ausdruecklich abgeschaltet.

    /// <summary>Names the measure; with one series per chart this replaces a legend.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden), Browsable(false)]
    public string Title { get; set; } = "";

    /// <summary>Formats a value for the axis and the tooltip.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden), Browsable(false)]
    public Func<double, string> Format { get; set; } = v => v.ToString("0.##");

    /// <summary>Upper bound of the y-axis when the measure has a natural one (CPU: 100).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden), Browsable(false)]
    public double? FixedMaximum { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden), Browsable(false)]
    public string EmptyText { get; set; } = "";

    // Validated categorical steps: slot 1 (blue) and slot 3 (aqua), each with the
    // step chosen for the dark surface rather than an automatic lightening.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden), Browsable(false)]
    public Color SeriesLight { get; set; } = Color.FromArgb(0x2a, 0x78, 0xd6);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden), Browsable(false)]
    public Color SeriesDark { get; set; } = Color.FromArgb(0x39, 0x87, 0xe5);

    public TimeSeriesChart()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Height = 170;
    }

    public void SetData(IReadOnlyList<Point> points, IReadOnlyList<DateTime> markers)
    {
        _points = points;
        _markers = markers;
        _hoverIndex = null;
        Invalidate();
    }

    private bool IsDark => Ui.IsDark(this);
    private Color Series => IsDark ? SeriesDark : SeriesLight;

    // Status hue, reserved for events - never used as a data series.
    private Color MarkerColor => IsDark ? Color.FromArgb(0xe6, 0x67, 0x67) : Color.FromArgb(0xe3, 0x49, 0x48);

    private Color Ink => ForeColor;
    private Color MutedInk => IsDark ? Color.FromArgb(0xc3, 0xc2, 0xb7) : Color.FromArgb(0x52, 0x51, 0x4e);
    private Color GridColor => Color.FromArgb(IsDark ? 46 : 30, Ink);

    private const int PadLeft = 66;
    private const int PadRight = 12;
    private const int PadTop = 24;
    private const int PadBottom = 22;

    private Rectangle Plot => new(PadLeft, PadTop,
        Math.Max(10, Width - PadLeft - PadRight),
        Math.Max(10, Height - PadTop - PadBottom));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(BackColor);

        using (var titleBrush = new SolidBrush(Ink))
            g.DrawString(Title, Font, titleBrush, 4, 4);

        var plot = Plot;

        if (_points.Count < 2)
        {
            using var muted = new SolidBrush(MutedInk);
            using var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(EmptyText, Font, muted, plot, centred);
            return;
        }

        var max = NiceMaximum();
        var from = _points[0].Utc;
        var to = _points[^1].Utc;
        var span = Math.Max(1, (to - from).TotalSeconds);

        float X(DateTime t) => plot.Left + (float)((t - from).TotalSeconds / span * plot.Width);
        float Y(double v) => plot.Bottom - (float)(Math.Clamp(v / max, 0, 1) * plot.Height);

        DrawGrid(g, plot, max);
        DrawMarkers(g, plot, X);

        // Peak band first, average on top: the band is context, the line is the reading.
        var peak = new List<PointF>(_points.Count + 2) { new(X(_points[0].Utc), plot.Bottom) };
        peak.AddRange(_points.Select(p => new PointF(X(p.Utc), Y(p.Peak))));
        peak.Add(new PointF(X(_points[^1].Utc), plot.Bottom));

        using (var band = new SolidBrush(Color.FromArgb(IsDark ? 54 : 42, Series)))
            g.FillPolygon(band, peak.ToArray());

        using (var line = new Pen(Series, 2f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLines(line, _points.Select(p => new PointF(X(p.Utc), Y(p.Average))).ToArray());

        DrawTimeAxis(g, plot, from, to, X);
        DrawHover(g, plot, max, X, Y);
    }

    private double NiceMaximum()
    {
        if (FixedMaximum is { } fixedMax) return fixedMax;

        var peak = _points.Max(p => p.Peak);
        if (peak <= 0) return 1;

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(peak)));
        foreach (var step in new[] { 1.0, 2.0, 2.5, 5.0, 10.0 })
            if (peak <= step * magnitude)
                return step * magnitude;
        return 10 * magnitude;
    }

    private void DrawGrid(Graphics g, Rectangle plot, double max)
    {
        using var grid = new Pen(GridColor, 1f);
        using var text = new SolidBrush(MutedInk);
        using var right = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };

        const int lines = 4;
        for (var i = 0; i <= lines; i++)
        {
            var value = max * i / lines;
            var y = plot.Bottom - (float)((double)i / lines * plot.Height);
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            g.DrawString(Format(value), Font, text,
                new RectangleF(0, y - 9, PadLeft - 8, 18), right);
        }
    }

    private void DrawMarkers(Graphics g, Rectangle plot, Func<DateTime, float> X)
    {
        if (_markers.Count == 0) return;

        // Thin and translucent: an event is an annotation on the reading, not a competing series.
        using var pen = new Pen(Color.FromArgb(150, MarkerColor), 1f) { DashStyle = DashStyle.Dot };
        foreach (var marker in _markers)
        {
            var x = X(marker);
            if (x < plot.Left || x > plot.Right) continue;
            g.DrawLine(pen, x, plot.Top, x, plot.Bottom);
        }
    }

    private void DrawTimeAxis(Graphics g, Rectangle plot, DateTime from, DateTime to, Func<DateTime, float> X)
    {
        var span = to - from;
        var format = span.TotalHours <= 2 ? "HH:mm"
            : span.TotalDays <= 2 ? "HH:mm"
            : span.TotalDays <= 14 ? "dd.MM HH:mm"
            : "dd.MM";

        using var text = new SolidBrush(MutedInk);
        using var centre = new StringFormat { Alignment = StringAlignment.Center };
        using var axis = new Pen(GridColor, 1f);
        g.DrawLine(axis, plot.Left, plot.Bottom, plot.Right, plot.Bottom);

        const int ticks = 4;
        for (var i = 0; i <= ticks; i++)
        {
            var t = from + TimeSpan.FromSeconds(span.TotalSeconds * i / ticks);
            var x = X(t);
            var label = t.ToLocalTime().ToString(format);
            var width = 90f;
            var left = Math.Clamp(x - width / 2, plot.Left - 20, plot.Right - width + 20);
            g.DrawString(label, Font, text, new RectangleF(left, plot.Bottom + 3, width, 16), centre);
        }
    }

    // ------------------------------------------------------------------ hover ---

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_points.Count < 2) return;

        var plot = Plot;
        if (e.X < plot.Left - 4 || e.X > plot.Right + 4 || e.Y < plot.Top || e.Y > plot.Bottom)
        {
            if (_hoverIndex is not null) { _hoverIndex = null; Invalidate(); }
            return;
        }

        var from = _points[0].Utc;
        var span = Math.Max(1, (_points[^1].Utc - from).TotalSeconds);
        var ratio = (double)(e.X - plot.Left) / plot.Width;
        var target = from + TimeSpan.FromSeconds(ratio * span);

        var best = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < _points.Count; i++)
        {
            var distance = Math.Abs((_points[i].Utc - target).TotalSeconds);
            if (distance < bestDistance) { bestDistance = distance; best = i; }
        }

        if (_hoverIndex != best) { _hoverIndex = best; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex is not null) { _hoverIndex = null; Invalidate(); }
    }

    private void DrawHover(Graphics g, Rectangle plot, double max, Func<DateTime, float> X, Func<double, float> Y)
    {
        if (_hoverIndex is not { } index || index >= _points.Count) return;
        var point = _points[index];
        var x = X(point.Utc);

        using (var crosshair = new Pen(Color.FromArgb(110, Ink), 1f))
            g.DrawLine(crosshair, x, plot.Top, x, plot.Bottom);

        // A 2px surface ring keeps the dot readable wherever it lands on the band.
        var y = Y(point.Average);
        using (var ring = new SolidBrush(BackColor)) g.FillEllipse(ring, x - 5, y - 5, 10, 10);
        using (var dot = new SolidBrush(Series)) g.FillEllipse(dot, x - 3.5f, y - 3.5f, 7, 7);

        var lines = new[]
        {
            point.Utc.ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
            $"⌀ {Format(point.Average)}",
            $"▲ {Format(point.Peak)}",
        };

        var width = lines.Max(l => g.MeasureString(l, Font).Width) + 16;
        var height = lines.Length * (Font.Height + 2) + 10;
        var left = x + 12 + width > plot.Right ? x - 12 - width : x + 12;
        var top = Math.Max(plot.Top, Math.Min(y - height / 2, plot.Bottom - height));

        var box = new RectangleF(left, top, width, height);
        using (var fill = new SolidBrush(Color.FromArgb(242, BackColor))) g.FillRectangle(fill, box);
        using (var border = new Pen(Color.FromArgb(90, Ink), 1f)) g.DrawRectangle(border, box.X, box.Y, box.Width, box.Height);

        using var primary = new SolidBrush(Ink);
        using var muted = new SolidBrush(MutedInk);
        for (var i = 0; i < lines.Length; i++)
            g.DrawString(lines[i], Font, i == 0 ? muted : primary,
                box.X + 8, box.Y + 5 + i * (Font.Height + 2));
    }
}
