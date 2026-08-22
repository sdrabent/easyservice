using EasyService.Core;

using EasyService.Resources;

namespace EasyService.Gui;

/// <summary>
/// The pane under the service list: what the selected service is doing right now, and the
/// last lines it wrote.
///
/// This is the answer to the question an administrator actually has in front of the list -
/// "why is that one red?" - and until now it took two clicks and a second window to get to
/// it. The commercial tools in this corner of the market all put the console output where it
/// can be seen without asking for it; that is the part of them worth copying.
/// </summary>
internal sealed class ServicePreview : UserControl
{
    private const int PreviewLines = 400;
    private const long PreviewBytes = 64 * 1024;

    private readonly Label _facts = new()
    {
        Dock = DockStyle.Top,
        AutoSize = false,
        AutoEllipsis = true,
        Height = 22,
        Padding = new Padding(8, 4, 8, 0),
    };

    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        WordWrap = false,
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.None,
    };

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 800 };

    private LogTail? _tail;
    private string? _serviceName;
    private string? _logPath;

    public ServicePreview()
    {
        _log.Font = Ui.MonoFont;

        Controls.Add(_log);
        Controls.Add(_facts);

        _timer.Tick += (_, _) => PollLog();
        VisibleChanged += (_, _) =>
        {
            if (Visible) _timer.Start(); else _timer.Stop();
        };

        Disposed += (_, _) =>
        {
            _timer.Stop();
            _tail?.Dispose();
        };
    }

    /// <summary>
    /// Points the pane at another service, or at none. Reopening the log file only happens
    /// when the service actually changed - the caller may call this on every refresh.
    /// </summary>
    public void Show(ServiceInfo? info, CheckResult? check)
    {
        if (info is null)
        {
            Detach();
            _facts.ForeColor = SystemColors.GrayText;
            _facts.Text = S.Prev_NoSelection;
            _log.Text = "";
            return;
        }

        if (!info.ManagedByEasyService)
        {
            // Fremde Dienste: den Laufzustand zeigen, aber kein Protokoll - EasyService
            // schreibt keines fuer sie und faende auch keines.
            Detach();
            _facts.ForeColor = ForeColor;
            _facts.Text = $"{info.Name} - {info.StateText}";
            _log.Text = "";
            return;
        }

        _facts.ForeColor = Ui.HealthColor(check?.Status, this, ForeColor);
        _facts.Text = check?.Summary ?? info.StateText;

        if (string.Equals(_serviceName, info.Name, StringComparison.OrdinalIgnoreCase)) return;

        Detach();
        _serviceName = info.Name;
        _logPath = ServiceConfig.Load(info.Name) is { } config
            ? Environment.ExpandEnvironmentVariables(config.StdoutPath)
            : null;

        OpenLog();
    }

    private void OpenLog()
    {
        if (string.IsNullOrWhiteSpace(_logPath))
        {
            _log.Text = "";
            return;
        }

        // Nur das Ende der Datei: eine Vorschau, die ein 200 MB grosses Protokoll einliest,
        // haelt bei jeder Auswahl das Fenster an.
        _tail = new LogTail(PreviewLines, PreviewBytes);
        if (!_tail.Open(_logPath))
        {
            _log.Text = S.Prev_NoLog(_logPath);
            return;
        }

        Render();
    }

    private void PollLog()
    {
        if (_tail is null)
        {
            // Die Datei entsteht erst, wenn der Dienst zum ersten Mal etwas schreibt.
            if (_logPath is not null && File.Exists(_logPath)) OpenLog();
            return;
        }

        if (_tail.Poll() != TailChange.None) Render();
    }

    private void Render()
    {
        if (_tail is null) return;

        var text = string.Join(Environment.NewLine, _tail.Lines);
        if (_log.Text == text) return;

        _log.Text = text;
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void Detach()
    {
        _tail?.Dispose();
        _tail = null;
        _serviceName = null;
        _logPath = null;
    }
}
