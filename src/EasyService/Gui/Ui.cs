using System.ComponentModel;

namespace EasyService.Gui;

/// <summary>Small helpers so the forms stay about layout instead of boilerplate.</summary>
internal static class Ui
{
    public static readonly Font MonoFont = MakeMono();

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

    public static T AddRow<T>(TableLayoutPanel panel, string label, T control) where T : Control
    {
        var lbl = new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 3, 3),
        };
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        control.Margin = new Padding(3, 4, 3, 4);
        panel.Controls.Add(lbl, 0, panel.RowCount);
        panel.Controls.Add(control, 1, panel.RowCount);
        panel.RowCount++;
        return control;
    }

    public static T AddFullRow<T>(TableLayoutPanel panel, T control) where T : Control
    {
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
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
    public static (Panel Panel, TextBox Box) BrowseRow(bool folder, string filter = "Programme (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|Alle Dateien (*.*)|*.*")
    {
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
            ShowError(null, "Explorer", e);
        }
    }
}
