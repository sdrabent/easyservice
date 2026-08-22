using System.ComponentModel;

using EasyService.Resources;

namespace EasyService.Gui;

/// <summary>
/// A ListView that does not flicker. The stock one repaints the whole control on every
/// update, which is very visible with a three-second auto refresh.
/// </summary>
internal sealed class BufferedListView : ListView
{
    public BufferedListView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }
}

/// <summary>Small helpers so the forms stay about layout instead of boilerplate.</summary>
internal static class Ui
{
    public static readonly Font MonoFont = MakeMono();

    private static Icon? _appIcon;
    private static bool _appIconLoaded;

    /// <summary>Application icon, embedded so it also works from a single-file publish.</summary>
    public static Icon? AppIcon
    {
        get
        {
            if (_appIconLoaded) return _appIcon;
            _appIconLoaded = true;
            try
            {
                using var stream = typeof(Ui).Assembly.GetManifestResourceStream("EasyService.AppIcon.ico");
                if (stream is not null) _appIcon = new Icon(stream);
            }
            catch (Exception)
            {
                _appIcon = null;   // a missing icon must never stop the app from starting
            }
            return _appIcon;
        }
    }

    /// <summary>True when the control sits on a dark background, so text colours can adapt.</summary>
    public static bool IsDark(Control control)
    {
        var c = control.BackColor;
        var luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        return luminance < 0.5;
    }

    /// <summary>
    /// Status colour that stays readable in both themes. The saturated greens and reds that
    /// work on white turn into mud on a dark background.
    /// </summary>
    public static Color HealthColor(Core.CheckStatus? status, Control control, Color fallback)
    {
        var dark = IsDark(control);
        return status switch
        {
            Core.CheckStatus.Critical => dark ? Color.FromArgb(255, 120, 110) : Color.FromArgb(190, 30, 30),
            Core.CheckStatus.Warning => dark ? Color.FromArgb(240, 175, 70) : Color.FromArgb(170, 90, 0),
            Core.CheckStatus.Ok => dark ? Color.FromArgb(105, 210, 130) : Color.FromArgb(0, 110, 40),
            Core.CheckStatus.Unknown => SystemColors.GrayText,
            _ => fallback,
        };
    }

    /// <summary>
    /// Paints the controls that the Windows Forms dark mode does not reach. A TextBox stays
    /// white and a StatusStrip stays light grey in an otherwise dark window, which reads as a
    /// hole in the middle of it. Call this once the form has its colours, not in a
    /// constructor - before the handle exists, every control still reports the default.
    /// </summary>
    public static void FollowTheme(Control control)
    {
        var form = control.FindForm();
        if (form is null || !IsDark(form)) return;

        var background = Color.FromArgb(32, 32, 32);
        var foreground = Color.FromArgb(224, 224, 224);

        foreach (var child in Descend(control))
        {
            switch (child)
            {
                case TextBox box:
                    box.BackColor = background;
                    box.ForeColor = foreground;
                    break;

                case StatusStrip strip:
                    strip.BackColor = Color.FromArgb(45, 45, 45);
                    strip.ForeColor = foreground;
                    break;
            }
        }
    }

    private static IEnumerable<Control> Descend(Control control) =>
        new[] { control }.Concat(control.Controls.Cast<Control>().SelectMany(Descend));

    // ---------------------------------------------------------------- glyphs ---

    /// <summary>
    /// Code points of the icon font. Named rather than sprinkled through the code, because
    /// "E768" tells nobody anything and picking the wrong one is invisible until it renders.
    /// </summary>
    public static class Glyphs
    {
        public const string Add = "";
        public const string Edit = "";
        public const string Play = "";
        public const string Stop = "";
        public const string Restart = "";
        public const string History = "";
        public const string Document = "";
        public const string Delete = "";
        public const string Refresh = "";
        public const string Details = "";
        public const string Settings = "";
        public const string Language = "";
        public const string Filter = "";
        public const string Copy = "";
        public const string Folder = "";
    }

    private static string? _iconFontName;
    private static bool _iconFontResolved;

    /// <summary>
    /// The Windows icon font, or null where there is none. Windows 11 ships Segoe Fluent
    /// Icons, Windows 10 Segoe MDL2 Assets, and a Server Core installation has neither -
    /// there the buttons simply stay text, which is what they were before.
    ///
    /// Gemerkt wird nur der Name, nicht die Schrift: jede Groesse braucht ihre eigene, und
    /// ein zwischengespeichertes Font-Objekt waere nach dem ersten Dispose unbrauchbar.
    /// </summary>
    private static Font? IconFont(float size)
    {
        if (!_iconFontResolved)
        {
            foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            {
                try
                {
                    using var probe = new Font(name, 12f);
                    if (probe.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        _iconFontName = name;
                        break;
                    }
                }
                catch (ArgumentException) { }
            }
            _iconFontResolved = true;
        }

        if (_iconFontName is null) return null;

        try { return new Font(_iconFontName, size, GraphicsUnit.Pixel); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// Draws one glyph into a bitmap in the colour of the control it will sit on. Drawing it
    /// rather than setting the font on the button keeps the label readable: a button whose
    /// font is the icon font shows its text as icons too.
    /// </summary>
    public static Image? Glyph(string codePoint, Control control)
    {
        var size = Math.Max(16, 16 * control.DeviceDpi / 96);
        var font = IconFont(size * 0.75f);
        if (font is null) return null;

        try
        {
            var bitmap = new Bitmap(size, size);
            using var g = Graphics.FromImage(bitmap);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            using var brush = new SolidBrush(control.ForeColor);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString(codePoint, font, brush, new RectangleF(0, 0, size, size), format);
            return bitmap;
        }
        finally
        {
            font.Dispose();
        }
    }

    /// <summary>Puts a glyph on an item, or leaves it as text where the font is missing.</summary>
    public static T WithGlyph<T>(this T item, string codePoint, Control owner) where T : ToolStripItem
    {
        var image = Glyph(codePoint, owner);
        if (image is null) return item;

        item.Image = image;
        item.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
        item.ImageScaling = ToolStripItemImageScaling.None;
        return item;
    }

    // ------------------------------------------------------------ status icons ---

    /// <summary>
    /// Glyphs for the service list. The shape carries the meaning as much as the colour
    /// does: roughly one man in twelve cannot tell the green from the red, and a list where
    /// health is a hue and nothing else is unreadable for them. Circle, triangle, square.
    /// </summary>
    public static ImageList BuildStatusIcons(Control control)
    {
        var size = Math.Max(16, 16 * control.DeviceDpi / 96);
        var list = new ImageList { ImageSize = new Size(size, size), ColorDepth = ColorDepth.Depth32Bit };

        foreach (var status in new Core.CheckStatus?[]
                 { Core.CheckStatus.Ok, Core.CheckStatus.Warning, Core.CheckStatus.Critical, Core.CheckStatus.Unknown, null })
            list.Images.Add(StatusGlyph(status, control, size));

        return list;
    }

    /// <summary>Index into <see cref="BuildStatusIcons"/>, in the same order.</summary>
    public static int StatusIconIndex(Core.CheckStatus? status) => status switch
    {
        Core.CheckStatus.Ok => 0,
        Core.CheckStatus.Warning => 1,
        Core.CheckStatus.Critical => 2,
        Core.CheckStatus.Unknown => 3,
        _ => 4,
    };

    private static Bitmap StatusGlyph(Core.CheckStatus? status, Control control, int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        if (status is null) return bitmap;   // nothing to say: an empty cell, not a grey dot

        var colour = HealthColor(status, control, control.ForeColor);
        using var brush = new SolidBrush(colour);
        var pad = Math.Max(2, size / 6);
        var box = new Rectangle(pad, pad, size - 2 * pad - 1, size - 2 * pad - 1);

        switch (status)
        {
            case Core.CheckStatus.Ok:
                g.FillEllipse(brush, box);
                break;

            case Core.CheckStatus.Warning:
                g.FillPolygon(brush, new[]
                {
                    new Point(box.Left + box.Width / 2, box.Top),
                    new Point(box.Right, box.Bottom),
                    new Point(box.Left, box.Bottom),
                });
                break;

            case Core.CheckStatus.Critical:
                g.FillRectangle(brush, box);
                break;

            default:   // Unknown: an outline, because there is nothing to report
                using (var pen = new Pen(colour, Math.Max(1.5f, size / 10f)))
                    g.DrawEllipse(pen, box);
                break;
        }

        return bitmap;
    }

    private static Font MakeMono()
    {
        foreach (var name in new[] { "Cascadia Mono", "Consolas", "Lucida Console" })
        {
            try
            {
                var f = new Font(name, 9f);
                if (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return f;
                f.Dispose();
            }
            catch (ArgumentException) { }
        }
        return new Font(FontFamily.GenericMonospace, 9f);
    }

    public static void ShowError(IWin32Window? owner, string caption, Exception ex)
    {
        var message = ex is Win32Exception or AggregateException ? ex.Message : ex.Message;
        if (ex.InnerException is not null) message += System.Environment.NewLine + ex.InnerException.Message;
        MessageBox.Show(owner, message, caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    public static void ShowError(IWin32Window? owner, string caption, string message) =>
        MessageBox.Show(owner, message, caption, MessageBoxButtons.OK, MessageBoxIcon.Error);

    public static void ShowInfo(IWin32Window? owner, string caption, string message) =>
        MessageBox.Show(owner, message, caption, MessageBoxButtons.OK, MessageBoxIcon.Information);

    public static bool Confirm(IWin32Window? owner, string caption, string message) =>
        MessageBox.Show(owner, message, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    // ------------------------------------------------------------- layout ---

    public static TableLayoutPanel FormPanel() => new()
    {
        Dock = DockStyle.Fill,
        ColumnCount = 2,
        AutoScroll = true,
        Padding = new Padding(12),
        GrowStyle = TableLayoutPanelGrowStyle.AddRows,
        ColumnStyles =
        {
            new ColumnStyle(SizeType.Absolute, 190),
            new ColumnStyle(SizeType.Percent, 100),
        },
    };

    public static T AddRow<T>(TableLayoutPanel panel, string label, T control) where T : Control =>
        AddLabelledRow(panel, label, control).Control;

    /// <summary>
    /// Same as AddRow but hands back the label too. Hiding both collapses the whole row,
    /// which is how optional fields disappear instead of sitting there greyed out.
    /// </summary>
    public static (Label Label, T Control) AddLabelledRow<T>(TableLayoutPanel panel, string label, T control)
        where T : Control
    {
        var lbl = new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 3, 3),
        };
        // Ohne Top zentriert der Layout-Panel den Inhalt in einer hoeheren Zeile - das reisst
        // sichtbare Loecher, sobald eine Zeile mehr Platz bekommt als ihr Inhalt braucht.
        control.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        control.Margin = new Padding(3, 4, 3, 4);
        panel.Controls.Add(lbl, 0, panel.RowCount);
        panel.Controls.Add(control, 1, panel.RowCount);
        panel.RowCount++;
        return (lbl, control);
    }

    public static T AddFullRow<T>(TableLayoutPanel panel, T control) where T : Control
    {
        control.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        control.Margin = new Padding(3, 4, 3, 4);
        panel.Controls.Add(control, 0, panel.RowCount);
        panel.SetColumnSpan(control, 2);
        panel.RowCount++;
        return control;
    }

    public static void AddSpacer(TableLayoutPanel panel, string? heading = null)
    {
        Control c = heading is null
            ? new Panel { Height = 10 }
            : new Label
            {
                Text = heading,
                AutoSize = true,
                Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
                Margin = new Padding(3, 14, 3, 2),
            };
        panel.Controls.Add(c, 0, panel.RowCount);
        panel.SetColumnSpan(c, 2);
        panel.RowCount++;
    }

    public static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(3, 0, 3, 8),
        MaximumSize = new Size(560, 0),
    };

    /// <summary>A textbox plus a "..." button that opens a file or folder picker.</summary>
    public static (Panel Panel, TextBox Box) BrowseRow(bool folder, string? filter = null)
    {
        // Kein Standardwert im Parameter: Ressourcen sind zur Compilezeit nicht konstant.
        filter ??= S.Common_FilterProgram;
        var panel = new Panel { Height = 27, Margin = new Padding(0) };
        var box = new TextBox { Left = 0, Top = 1, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        var button = new Button { Text = "...", Width = 32, Top = 0, Anchor = AnchorStyles.Right | AnchorStyles.Top };

        void Layout()
        {
            button.Left = Math.Max(40, panel.ClientSize.Width - button.Width);
            box.Width = Math.Max(20, button.Left - 6);
        }

        panel.Resize += (_, _) => Layout();
        panel.Controls.Add(box);
        panel.Controls.Add(button);
        Layout();

        button.Click += (_, _) =>
        {
            if (folder)
            {
                using var dlg = new FolderBrowserDialog { SelectedPath = SafePath(box.Text) };
                if (dlg.ShowDialog(panel) == DialogResult.OK) box.Text = dlg.SelectedPath;
            }
            else
            {
                using var dlg = new OpenFileDialog { Filter = filter, CheckFileExists = false };
                var current = SafePath(box.Text);
                if (current.Length > 0) dlg.InitialDirectory = current;
                if (dlg.ShowDialog(panel) == DialogResult.OK) box.Text = dlg.FileName;
            }
        };

        return (panel, box);
    }

    private static string SafePath(string text)
    {
        try
        {
            var expanded = System.Environment.ExpandEnvironmentVariables(text ?? "");
            if (expanded.Length == 0) return "";
            if (Directory.Exists(expanded)) return expanded;
            var dir = Path.GetDirectoryName(expanded);
            return dir is not null && Directory.Exists(dir) ? dir : "";
        }
        catch
        {
            return "";
        }
    }

    public static NumericUpDown Spin(int min, int max, int value, int increment = 1) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = Math.Clamp(value, min, max),
        Increment = increment,
        ThousandsSeparator = true,
        Width = 140,
    };

    public static ComboBox Combo(params string[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        c.Items.AddRange(items);
        c.SelectedIndex = 0;
        return c;
    }

    /// <summary>
    /// Asks for a single password. Used on import, where the file deliberately does not
    /// carry one. Returns null when the user cancels.
    /// </summary>
    public static string? PromptPassword(IWin32Window? owner, string caption, string prompt)
    {
        using var form = new Form
        {
            Text = caption,
            Icon = AppIcon,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(430, 132),
            Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont,
        };

        // AutoEllipsis, weil im Text ein Kontoname mit langer Domaene stehen kann.
        var label = new Label { Text = prompt, AutoSize = false, AutoEllipsis = true, Bounds = new Rectangle(14, 14, 400, 34) };
        var box = new TextBox { UseSystemPasswordChar = true, Bounds = new Rectangle(14, 52, 400, 24) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Bounds = new Rectangle(228, 92, 90, 28) };
        var cancel = new Button { Text = S.Common_Cancel, DialogResult = DialogResult.Cancel, Bounds = new Rectangle(324, 92, 90, 28) };

        form.Controls.AddRange(new Control[] { label, box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
    }

    public static void OpenInExplorer(string path)
    {
        try
        {
            var expanded = System.Environment.ExpandEnvironmentVariables(path ?? "");
            if (File.Exists(expanded))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{expanded}\"") { UseShellExecute = true });
            else if (Directory.Exists(expanded))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(expanded) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            ShowError(null, S.Common_Explorer, e);
        }
    }
}
