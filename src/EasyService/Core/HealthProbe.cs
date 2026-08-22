using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;

using EasyService.Resources;

namespace EasyService.Core;

/// <summary>How the supervisor asks the application whether it is still working.</summary>
public enum HealthCheckType
{
    /// <summary>No check. A running process counts as healthy, which is all Windows knows.</summary>
    None = 0,

    /// <summary>Fetch a URL and look at the status code.</summary>
    Http = 1,

    /// <summary>Open a TCP connection and close it again.</summary>
    Tcp = 2,

    /// <summary>A file has to have been written to recently - a heartbeat, or the log itself.</summary>
    FileFresh = 3,

    /// <summary>Run a program; exit code 0 means healthy.</summary>
    Command = 4,
}

/// <summary>What happens once a service counts as unhealthy.</summary>
public enum HealthAction
{
    /// <summary>Only report it. The monitoring turns critical, the application is left alone.</summary>
    Report = 0,

    /// <summary>Restart the application, then report it.</summary>
    Restart = 1,
}

/// <summary>The verdict of a single probe.</summary>
public readonly record struct HealthResult(bool Healthy, string Detail, TimeSpan Duration);

/// <summary>
/// Runs one health check.
///
/// The point of it: Windows only knows whether a process exists. An application that has
/// deadlocked, lost its database connection or stopped answering requests looks exactly like
/// a healthy one to the Service Control Manager, and that is the outage nobody gets paged
/// for.
///
/// The HTTPS case deliberately does not verify the certificate. The question here is whether
/// the application answers, not who it is, and health endpoints on localhost carry
/// self-signed certificates more often than not. Whoever needs identity checked has the
/// command probe.
/// </summary>
public static class HealthProbe
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        // Kein Timeout am Client: der kommt pro Aufruf aus der Konfiguration.
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static HealthResult Run(ServiceConfig cfg)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Max(500, cfg.HealthTimeoutMs));
        var started = Stopwatch.StartNew();

        try
        {
            var (healthy, detail) = cfg.HealthType switch
            {
                HealthCheckType.Http => CheckHttp(cfg, timeout),
                HealthCheckType.Tcp => CheckTcp(cfg, timeout),
                HealthCheckType.FileFresh => CheckFile(cfg),
                HealthCheckType.Command => CheckCommand(cfg, timeout),
                _ => (true, S.Health_NotConfigured),
            };
            return new HealthResult(healthy, detail, started.Elapsed);
        }
        catch (Exception e)
        {
            return new HealthResult(false, Flatten(e), started.Elapsed);
        }
    }

    // ------------------------------------------------------------------ HTTP ---

    private static (bool, string) CheckHttp(ServiceConfig cfg, TimeSpan timeout)
    {
        var url = System.Environment.ExpandEnvironmentVariables(cfg.HealthTarget.Trim());
        using var cts = new CancellationTokenSource(timeout);

        try
        {
            using var response = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                                     .GetAwaiter().GetResult();
            var code = (int)response.StatusCode;

            var ok = cfg.HealthExpectStatus > 0
                ? code == cfg.HealthExpectStatus
                : code is >= 200 and <= 299;

            return (ok, S.Health_HttpStatus(code, response.ReasonPhrase ?? ""));
        }
        catch (OperationCanceledException)
        {
            return (false, S.Health_Timeout((int)timeout.TotalMilliseconds));
        }
        catch (HttpRequestException e)
        {
            return (false, Flatten(e));
        }
    }

    // ------------------------------------------------------------------- TCP ---

    /// <summary>Splits "host:port", brackets around an IPv6 address included. Null when it is not one.</summary>
    public static (string Host, int Port)? ParseEndpoint(string target)
    {
        var text = (target ?? "").Trim();
        if (text.Length == 0) return null;

        var colon = text.LastIndexOf(':');
        if (colon <= 0 || colon == text.Length - 1) return null;

        var host = text[..colon].Trim().Trim('[', ']');
        if (!int.TryParse(text[(colon + 1)..].Trim(), out var port) || port is < 1 or > 65535) return null;

        return host.Length == 0 ? null : (host, port);
    }

    private static (bool, string) CheckTcp(ServiceConfig cfg, TimeSpan timeout)
    {
        if (ParseEndpoint(cfg.HealthTarget) is not { } endpoint)
            return (false, S.Cfg_Err_HealthEndpoint(cfg.HealthTarget));

        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(timeout);

        try
        {
            client.ConnectAsync(endpoint.Host, endpoint.Port, cts.Token).AsTask().GetAwaiter().GetResult();
            return (true, S.Health_TcpOpen(endpoint.Host, endpoint.Port));
        }
        catch (OperationCanceledException)
        {
            return (false, S.Health_Timeout((int)timeout.TotalMilliseconds));
        }
        catch (SocketException e)
        {
            return (false, S.Health_TcpRefused(endpoint.Host, endpoint.Port, e.SocketErrorCode.ToString()));
        }
    }

    // ------------------------------------------------------------------ file ---

    private static (bool, string) CheckFile(ServiceConfig cfg)
    {
        var path = System.Environment.ExpandEnvironmentVariables(cfg.HealthTarget.Trim());
        var info = new FileInfo(path);

        if (!info.Exists) return (false, S.Health_FileMissing(path));

        var age = DateTime.UtcNow - info.LastWriteTimeUtc;
        var limit = TimeSpan.FromSeconds(Math.Max(1, cfg.HealthMaxAgeSec));

        return age <= limit
            ? (true, S.Health_FileFresh((int)age.TotalSeconds))
            : (false, S.Health_FileStale((int)age.TotalSeconds, (int)limit.TotalSeconds));
    }

    // --------------------------------------------------------------- command ---

    private static (bool, string) CheckCommand(ServiceConfig cfg, TimeSpan timeout)
    {
        var (exe, arguments) = SplitCommand(System.Environment.ExpandEnvironmentVariables(cfg.HealthTarget.Trim()));
        var workingDirectory = System.Environment.ExpandEnvironmentVariables(cfg.AppDirectory ?? "");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : "",
            },
        };

        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            // Ein haengender Check darf nicht seinerseits haengen bleiben.
            try { process.Kill(entireProcessTree: true); } catch { }
            return (false, S.Health_Timeout((int)timeout.TotalMilliseconds));
        }

        var text = FirstLine(output.GetAwaiter().GetResult());
        if (text.Length == 0) text = FirstLine(error.GetAwaiter().GetResult());

        return (process.ExitCode == 0, S.Health_CommandExit(process.ExitCode, text));
    }

    /// <summary>Splits a command line into program and arguments, honouring one pair of quotes.</summary>
    internal static (string Exe, string Arguments) SplitCommand(string commandLine)
    {
        var text = (commandLine ?? "").Trim();
        if (text.Length == 0) return ("", "");

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end < 0 ? (text.Trim('"'), "") : (text[1..end], text[(end + 1)..].Trim());
        }

        var space = text.IndexOf(' ');
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..].Trim());
    }

    private static string FirstLine(string text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return "";

        var end = trimmed.IndexOfAny(new[] { '\r', '\n' });
        var line = end < 0 ? trimmed : trimmed[..end];
        return line.Length <= 200 ? line : line[..200] + "...";
    }

    private static string Flatten(Exception e)
    {
        var message = e.Message;
        for (var inner = e.InnerException; inner is not null; inner = inner.InnerException)
            message += " - " + inner.Message;
        return message.Length <= 300 ? message : message[..300] + "...";
    }
}
