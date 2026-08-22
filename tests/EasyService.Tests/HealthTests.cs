using System.Net;
using System.Net.Sockets;
using System.Text;

using EasyService.Core;

namespace EasyService.Tests;

/// <summary>
/// Tests for the health check - the part that answers the question Windows cannot: is the
/// application still doing its job, or is it merely still a process?
///
/// The HTTP cases run against a hand-written listener rather than HttpListener, because that
/// one needs a URL reservation on Windows and would turn a test failure into a question about
/// the machine instead of about the code.
/// </summary>
internal static class HealthTests
{
    private static string _root = "";

    public static IEnumerable<(string Name, Action Test)> All(string root)
    {
        _root = Path.Combine(root, "health");
        Directory.CreateDirectory(_root);

        yield return ("HTTP-Check erkennt 200 und 503", HttpStatusDecides);
        yield return ("HTTP-Check kann einen bestimmten Status verlangen", HttpExpectedStatus);
        yield return ("HTTP-Check gibt nach dem Zeitlimit auf", HttpTimesOut);
        yield return ("TCP-Check unterscheidet offen und zu", TcpOpenOrClosed);
        yield return ("host:port wird richtig zerlegt", EndpointParsing);
        yield return ("Datei-Check achtet auf das Alter", FileFreshness);
        yield return ("Kommando-Check nimmt den Exit-Code", CommandExitCode);
        yield return ("Ein Aussetzer macht noch keinen kranken Dienst", OneFailureIsNotAnOutage);
        yield return ("Anhaltender Fehlschlag startet die Anwendung neu", FailureRestartsApplication);
        yield return ("Ein kranker Dienst wird kritisch gemeldet", UnhealthyIsCritical);
    }

    // ------------------------------------------------------------- Mini-Server ---

    /// <summary>A socket that answers every request with the same status, after a delay.</summary>
    private sealed class TinyServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public TinyServer(int status = 200, TimeSpan delay = default)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                    catch { return; }

                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            try
                            {
                                // Erst die Anfrage lesen. Wer antwortet und schliesst, ohne
                                // gelesen zu haben, bekommt von Windows ein RST - und damit
                                // verwirft der Client die Antwort, die schon unterwegs war.
                                var request = new byte[4096];
                                await client.GetStream().ReadAsync(request, _cts.Token);

                                if (delay > TimeSpan.Zero) await Task.Delay(delay, _cts.Token);

                                var body = Encoding.UTF8.GetBytes("ok");
                                var head = Encoding.ASCII.GetBytes(
                                    $"HTTP/1.1 {status} Test\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");

                                var stream = client.GetStream();
                                await stream.WriteAsync(head, _cts.Token);
                                await stream.WriteAsync(body, _cts.Token);
                                await stream.FlushAsync(_cts.Token);
                                client.Client.Shutdown(SocketShutdown.Send);
                            }
                            catch { /* der Test hat aufgelegt */ }
                        }
                    }, _cts.Token);
                }
            });
        }

        public int Port { get; }
        public string Url => $"http://127.0.0.1:{Port}/health";

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            _cts.Dispose();
        }
    }

    private static ServiceConfig Probe(HealthCheckType type, string target, int timeoutMs = 3000) => new()
    {
        ServiceName = "health-probe",
        HealthType = type,
        HealthTarget = target,
        HealthTimeoutMs = timeoutMs,
    };

    // ------------------------------------------------------------------ HTTP ---

    private static void HttpStatusDecides()
    {
        using (var server = new TinyServer(200))
        {
            var ok = HealthProbe.Run(Probe(HealthCheckType.Http, server.Url));
            Assert(ok.Healthy, $"200 wurde nicht als gesund gewertet: {ok.Detail}");
            Assert(ok.Detail.Contains("200"), $"der Status fehlt in der Meldung: {ok.Detail}");
        }

        using (var broken = new TinyServer(503))
        {
            var bad = HealthProbe.Run(Probe(HealthCheckType.Http, broken.Url));
            Assert(!bad.Healthy, "503 wurde als gesund gewertet");
            Assert(bad.Detail.Contains("503"), $"der Status fehlt in der Meldung: {bad.Detail}");
        }
    }

    private static void HttpExpectedStatus()
    {
        // Manche Anwendungen antworten auf ihrem Health-Pfad mit 204 oder 418.
        using var server = new TinyServer(418);

        var config = Probe(HealthCheckType.Http, server.Url);
        Assert(!HealthProbe.Run(config).Healthy, "418 gilt ohne Vorgabe zu Unrecht als gesund");

        config.HealthExpectStatus = 418;
        Assert(HealthProbe.Run(config).Healthy, "der ausdruecklich erwartete Status wurde nicht akzeptiert");
    }

    private static void HttpTimesOut()
    {
        using var slow = new TinyServer(200, TimeSpan.FromSeconds(5));

        var result = HealthProbe.Run(Probe(HealthCheckType.Http, slow.Url, timeoutMs: 700));
        Assert(!result.Healthy, "der haengende Server wurde als gesund gewertet");
        Assert(result.Duration < TimeSpan.FromSeconds(4),
            $"das Zeitlimit hat nicht gegriffen, es dauerte {result.Duration.TotalSeconds:F1}s");
    }

    // ------------------------------------------------------------------- TCP ---

    private static void TcpOpenOrClosed()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var open = HealthProbe.Run(Probe(HealthCheckType.Tcp, $"127.0.0.1:{port}"));
            Assert(open.Healthy, $"der offene Port wurde nicht erkannt: {open.Detail}");
        }
        finally
        {
            listener.Stop();
        }

        var closed = HealthProbe.Run(Probe(HealthCheckType.Tcp, $"127.0.0.1:{port}", timeoutMs: 1500));
        Assert(!closed.Healthy, "der geschlossene Port galt als offen");
    }

    private static void EndpointParsing()
    {
        Assert(HealthProbe.ParseEndpoint("localhost:5432") == ("localhost", 5432), "host:port wurde falsch zerlegt");
        Assert(HealthProbe.ParseEndpoint("[::1]:80") == ("::1", 80), "IPv6 in Klammern wurde falsch zerlegt");
        Assert(HealthProbe.ParseEndpoint("localhost") is null, "ein fehlender Port wurde akzeptiert");
        Assert(HealthProbe.ParseEndpoint("localhost:0") is null, "Port 0 wurde akzeptiert");
        Assert(HealthProbe.ParseEndpoint("localhost:99999") is null, "ein zu grosser Port wurde akzeptiert");
        Assert(HealthProbe.ParseEndpoint("") is null, "eine leere Angabe wurde akzeptiert");
    }

    // ------------------------------------------------------------------ Datei ---

    private static void FileFreshness()
    {
        var path = Path.Combine(_root, "heartbeat.txt");
        File.WriteAllText(path, "tick");

        var config = Probe(HealthCheckType.FileFresh, path);
        config.HealthMaxAgeSec = 60;
        Assert(HealthProbe.Run(config).Healthy, "die eben geschriebene Datei galt als veraltet");

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        var stale = HealthProbe.Run(config);
        Assert(!stale.Healthy, "eine fuenf Minuten alte Datei galt als frisch");
        Assert(stale.Detail.Contains("60"), $"das erlaubte Alter fehlt in der Meldung: {stale.Detail}");

        config.HealthTarget = Path.Combine(_root, "gibtesnicht.txt");
        Assert(!HealthProbe.Run(config).Healthy, "eine fehlende Datei galt als gesund");
    }

    // --------------------------------------------------------------- Kommando ---

    private static void CommandExitCode()
    {
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

        var good = HealthProbe.Run(Probe(HealthCheckType.Command, $"\"{cmd}\" /c exit 0"));
        Assert(good.Healthy, $"Exit-Code 0 galt als krank: {good.Detail}");

        var bad = HealthProbe.Run(Probe(HealthCheckType.Command, $"\"{cmd}\" /c exit 3"));
        Assert(!bad.Healthy, "Exit-Code 3 galt als gesund");
        Assert(bad.Detail.Contains("3"), $"der Exit-Code fehlt in der Meldung: {bad.Detail}");

        var (exe, arguments) = HealthProbe.SplitCommand("\"C:\\a b\\tool.exe\" --check now");
        Assert(exe == @"C:\a b\tool.exe", $"das Programm wurde falsch abgetrennt: {exe}");
        Assert(arguments == "--check now", $"die Argumente wurden falsch abgetrennt: {arguments}");
    }

    // ------------------------------------------------- Zusammenspiel mit dem Supervisor ---

    private static ServiceConfig Supervised(string name, HealthAction action)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);

        return new ServiceConfig
        {
            ServiceName = "health-" + name,
            Application = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            AppParameters = "/c \"ping -n 60 127.0.0.1 >nul\"",
            AppDirectory = dir,
            StdoutPath = Path.Combine(dir, "stdout.log"),
            StderrPath = Path.Combine(dir, "stderr.log"),
            LogServiceEvents = false,
            HistoryDays = 0,
            DefaultExitAction = ExitAction.Restart,
            RestartDelayMs = 200,
            ThrottleMs = 0,
            // Ein Ziel, das es nie geben wird: der Check schlaegt zuverlaessig fehl.
            HealthType = HealthCheckType.FileFresh,
            HealthTarget = Path.Combine(dir, "gibtesnicht.txt"),
            HealthIntervalMs = 1000,
            HealthTimeoutMs = 1000,
            HealthGraceMs = 0,
            HealthAction = action,
        };
    }

    private static void OneFailureIsNotAnOutage()
    {
        var config = Supervised("blip", HealthAction.Report);
        config.HealthFailures = 5;   // so viele kommen in der Testzeit nicht zusammen

        RunSupervised(config, TimeSpan.FromSeconds(3), state =>
        {
            Assert(state.HealthFailuresInARow >= 1, "es wurde ueberhaupt nicht geprueft");
            Assert(state.Health != HealthStatus.Unhealthy,
                $"nach {state.HealthFailuresInARow} von 5 Fehlschlaegen schon krank gemeldet");
        });
    }

    private static void FailureRestartsApplication()
    {
        var config = Supervised("restart", HealthAction.Restart);
        config.HealthFailures = 1;

        RunSupervised(config, TimeSpan.FromSeconds(6), state =>
        {
            Assert(state.HealthRestarts >= 1,
                $"der Health-Check hat nicht neu gestartet (Zustand {state.Health}, {state.HealthFailuresInARow} Fehlschlaege)");
            Assert(state.RestartCount >= 1, "der Neustart wurde nicht mitgezaehlt");
        });
    }

    private static void RunSupervised(ServiceConfig config, TimeSpan window, Action<ServiceState> check)
    {
        ServiceState.Delete(config.ServiceName);
        using (var supervisor = new ProcessSupervisor(config))
        {
            var task = Task.Run(supervisor.Run);
            try
            {
                Thread.Sleep(window);
                check(supervisor.State);
            }
            finally
            {
                supervisor.RequestStop();
                task.Wait(TimeSpan.FromSeconds(20));
            }
        }
        ServiceState.Delete(config.ServiceName);
    }

    // ------------------------------------------------------------- Monitoring ---

    private static void UnhealthyIsCritical()
    {
        var config = new ServiceConfig { ServiceName = "verdict", HealthType = HealthCheckType.Http };
        var info = new ServiceInfo("verdict", "Verdict", 4, 4711,
                                   StartupType.Automatic, "", "LocalSystem", true, "");

        var state = new ServiceState
        {
            ServiceName = "verdict",
            State = SupervisorState.Running,
            ApplicationPid = 4711,
            ApplicationStartedUtc = DateTime.UtcNow.AddMinutes(-10),
            UpdatedUtc = DateTime.UtcNow,
            Health = HealthStatus.Unhealthy,
            HealthDetail = "HTTP 503 Service Unavailable",
        };

        var (status, summary) = Monitoring.Evaluate(config, state, info);
        Assert(status == CheckStatus.Critical, $"erwartet: Critical, geliefert: {status}");
        Assert(summary.Contains("503"), $"der Grund fehlt in der Meldung: {summary}");

        state.Health = HealthStatus.Healthy;
        var (ok, okSummary) = Monitoring.Evaluate(config, state, info);
        Assert(ok == CheckStatus.Ok, $"ein gesunder Dienst wurde als {ok} gemeldet");
        Assert(okSummary.Length > 0, "die Zusammenfassung ist leer");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
