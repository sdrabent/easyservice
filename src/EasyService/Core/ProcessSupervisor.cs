using System.Runtime.InteropServices;
using System.Text;

namespace EasyService.Core;

/// <summary>
/// Launches the configured application, streams its stdout/stderr into rotating log
/// files and applies the restart policy. This is the part that turns any ordinary
/// executable into a well-behaved Windows service.
/// </summary>
public sealed class ProcessSupervisor : IDisposable
{
    private readonly ServiceConfig _cfg;
    private readonly LogWriter? _events;

    private readonly object _childLock = new();
    private readonly ManualResetEventSlim _stopRequested = new(false);

    private IntPtr _process = IntPtr.Zero;
    private IntPtr _job = IntPtr.Zero;
    private uint _pid;

    private LogWriter? _stdout;
    private LogWriter? _stderr;
    private Thread? _outPump;
    private Thread? _errPump;

    /// <summary>Raised when the exit policy says the whole service should stop.</summary>
    public event Action<uint>? StopServiceRequested;

    public uint CurrentProcessId => _pid;

    public ProcessSupervisor(ServiceConfig cfg)
    {
        _cfg = cfg;
        if (cfg.LogServiceEvents)
        {
            try
            {
                _events = new LogWriter(Expand(cfg.ServiceLogPath), append: true, timestamp: false,
                    rotate: true, rotateBytes: 2 * 1024 * 1024, rotateSeconds: 0, keep: 5);
            }
            catch (Exception e)
            {
                EventLogSink.Warn(cfg.ServiceName, "Das EasyService-Ereignisprotokoll konnte nicht geöffnet werden: " + e.Message);
            }
        }
    }

    public void Log(string message)
    {
        _events?.WriteLine(message);
        EventLogSink.Info(_cfg.ServiceName, message);
    }

    private void LogQuiet(string message) => _events?.WriteLine(message);

    private static string Expand(string s) => System.Environment.ExpandEnvironmentVariables(s ?? "");

    // ----------------------------------------------------------------- loop ---

    /// <summary>Blocks until the service is asked to stop or the exit policy ends it.</summary>
    public void Run()
    {
        Log($"EasyService übernimmt die Überwachung von \"{Expand(_cfg.Application)}\".");

        OpenOutputLogs();

        if (_cfg.StartupDelayMs > 0)
            _stopRequested.Wait(_cfg.StartupDelayMs);

        var backoff = Math.Max(0, _cfg.RestartDelayMs);

        while (!_stopRequested.IsSet)
        {
            DateTime startedAt;
            try
            {
                Launch();
                startedAt = DateTime.UtcNow;
                Log($"Anwendung gestartet (PID {_pid}).");
            }
            catch (Exception e)
            {
                Log("Die Anwendung konnte nicht gestartet werden: " + e.Message);
                if (_cfg.DefaultExitAction != ExitAction.Restart)
                {
                    StopServiceRequested?.Invoke(1);
                    return;
                }
                if (!DelayBeforeRestart(ref backoff, ranBriefly: true)) return;
                continue;
            }

            var exitCode = WaitForChildExit();

            if (_stopRequested.IsSet)
            {
                LogQuiet($"Anwendung beendet (Code {exitCode}), Dienst wird gestoppt.");
                return;
            }

            var ranFor = DateTime.UtcNow - startedAt;
            var action = _cfg.ExitActions.TryGetValue(exitCode, out var specific) ? specific : _cfg.DefaultExitAction;
            Log($"Anwendung beendet mit Code {exitCode} nach {ranFor.TotalSeconds:F1}s. Aktion: {Describe(action)}.");

            CleanUpChild();

            switch (action)
            {
                case ExitAction.Stop:
                    StopServiceRequested?.Invoke(exitCode);
                    return;

                case ExitAction.Ignore:
                    Log("Die Anwendung wird nicht neu gestartet; der Dienst bleibt aktiv.");
                    _stopRequested.Wait();
                    return;

                default:
                    var ranBriefly = _cfg.ThrottleMs > 0 && ranFor.TotalMilliseconds < _cfg.ThrottleMs;
                    if (!ranBriefly) backoff = Math.Max(0, _cfg.RestartDelayMs);
                    if (!DelayBeforeRestart(ref backoff, ranBriefly)) return;
                    break;
            }
        }
    }

    private static string Describe(ExitAction a) => a switch
    {
        ExitAction.Restart => "Neu starten",
        ExitAction.Ignore => "Ignorieren",
        ExitAction.Stop => "Dienst beenden",
        _ => a.ToString(),
    };

    /// <summary>Returns false when a stop was requested while waiting.</summary>
    private bool DelayBeforeRestart(ref int backoff, bool ranBriefly)
    {
        var delay = Math.Max(0, _cfg.RestartDelayMs);
        if (ranBriefly)
        {
            delay = Math.Max(delay, backoff);
            LogQuiet($"Die Anwendung lief kürzer als das Throttle-Fenster ({_cfg.ThrottleMs} ms); " +
                     $"nächster Versuch in {delay} ms.");
            backoff = Math.Min(Math.Max(1000, backoff * 2), 60_000);
        }

        return delay <= 0 || !_stopRequested.Wait(delay);
    }

    // --------------------------------------------------------------- launch ---

    private void OpenOutputLogs()
    {
        _stdout = TryOpenLog(_cfg.StdoutPath, "stdout");
        if (!string.IsNullOrWhiteSpace(_cfg.StderrPath) &&
            string.Equals(Expand(_cfg.StderrPath), Expand(_cfg.StdoutPath), StringComparison.OrdinalIgnoreCase))
            _stderr = _stdout;   // both streams into one file
        else
            _stderr = TryOpenLog(_cfg.StderrPath, "stderr");
    }

    private LogWriter? TryOpenLog(string path, string which)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return new LogWriter(Expand(path), _cfg.AppendOutput, _cfg.TimestampLines,
                _cfg.RotateFiles, _cfg.RotateBytes, _cfg.RotateSeconds, _cfg.RotateKeep);
        }
        catch (Exception e)
        {
            Log($"Die {which}-Protokolldatei konnte nicht geöffnet werden ({path}): {e.Message}");
            return null;
        }
    }

    private void Launch()
    {
        var app = Expand(_cfg.Application);
        if (!File.Exists(app))
            throw new FileNotFoundException($"Das Programm wurde nicht gefunden: {app}", app);

        var workDir = Expand(_cfg.AppDirectory);
        if (string.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir))
            workDir = Path.GetDirectoryName(app) ?? AppContext.BaseDirectory;

        var commandLine = new StringBuilder();
        commandLine.Append('"').Append(app).Append('"');
        var args = Expand(_cfg.AppParameters);
        if (!string.IsNullOrWhiteSpace(args)) commandLine.Append(' ').Append(args);

        var sa = new Native.SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<Native.SECURITY_ATTRIBUTES>(),
            bInheritHandle = true,
            lpSecurityDescriptor = IntPtr.Zero,
        };

        IntPtr outRead = IntPtr.Zero, outWrite = IntPtr.Zero;
        IntPtr errRead = IntPtr.Zero, errWrite = IntPtr.Zero;
        IntPtr inRead = IntPtr.Zero, inWrite = IntPtr.Zero;
        var envBlock = IntPtr.Zero;

        try
        {
            if (!Native.CreatePipe(out outRead, out outWrite, ref sa, 0)) throw LastError("stdout-Pipe");
            if (!Native.CreatePipe(out errRead, out errWrite, ref sa, 0)) throw LastError("stderr-Pipe");
            if (!Native.CreatePipe(out inRead, out inWrite, ref sa, 0)) throw LastError("stdin-Pipe");

            // Our read ends must not leak into the child, otherwise EOF never arrives.
            Native.SetHandleInformation(outRead, Native.HANDLE_FLAG_INHERIT, 0);
            Native.SetHandleInformation(errRead, Native.HANDLE_FLAG_INHERIT, 0);
            Native.SetHandleInformation(inWrite, Native.HANDLE_FLAG_INHERIT, 0);

            var si = new Native.STARTUPINFO
            {
                cb = Marshal.SizeOf<Native.STARTUPINFO>(),
                dwFlags = Native.STARTF_USESTDHANDLES | Native.STARTF_USESHOWWINDOW,
                wShowWindow = Native.SW_HIDE,
                hStdInput = inRead,
                hStdOutput = outWrite,
                hStdError = errWrite,
            };

            envBlock = BuildEnvironmentBlock();

            var flags = Native.CREATE_SUSPENDED | Native.CREATE_NO_WINDOW | Native.CREATE_UNICODE_ENVIRONMENT;

            if (!Native.CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                    flags, envBlock, workDir, ref si, out var pi))
                throw LastError($"Start von {app}");

            lock (_childLock)
            {
                _process = pi.hProcess;
                _pid = pi.dwProcessId;
                _job = CreateJob(pi.hProcess);
            }

            ApplyPriorityAndAffinity(pi.hProcess);

            Native.ResumeThread(pi.hThread);
            Native.CloseHandle(pi.hThread);

            // Close our copies of the write ends so the pumps see EOF when the child dies.
            Native.CloseHandle(outWrite); outWrite = IntPtr.Zero;
            Native.CloseHandle(errWrite); errWrite = IntPtr.Zero;
            Native.CloseHandle(inRead); inRead = IntPtr.Zero;
            Native.CloseHandle(inWrite); inWrite = IntPtr.Zero;   // child sees stdin at EOF

            _outPump = StartPump(outRead, _stdout, "stdout");
            outRead = IntPtr.Zero;
            _errPump = StartPump(errRead, _stderr, "stderr");
            errRead = IntPtr.Zero;
        }
        catch
        {
            foreach (var h in new[] { outRead, outWrite, errRead, errWrite, inRead, inWrite })
                if (h != IntPtr.Zero) Native.CloseHandle(h);
            throw;
        }
        finally
        {
            if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);
        }
    }

    private static Exception LastError(string what)
    {
        var err = Marshal.GetLastWin32Error();
        return new System.ComponentModel.Win32Exception(err, $"{what} fehlgeschlagen: {new System.ComponentModel.Win32Exception(err).Message}");
    }

    private IntPtr CreateJob(IntPtr process)
    {
        var job = Native.CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;

        var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags =
            _cfg.KillProcessTree ? Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE : Native.JOB_OBJECT_LIMIT_BREAKAWAY_OK;

        var size = Marshal.SizeOf(info);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, ptr, (uint)size);
        }
        finally { Marshal.FreeHGlobal(ptr); }

        if (!Native.AssignProcessToJobObject(job, process))
        {
            LogQuiet("Hinweis: Der Prozess konnte keinem Job-Objekt zugeordnet werden; " +
                     "Kindprozesse werden beim Beenden eventuell nicht mit beendet.");
            Native.CloseHandle(job);
            return IntPtr.Zero;
        }
        return job;
    }

    private void ApplyPriorityAndAffinity(IntPtr process)
    {
        var priority = _cfg.Priority switch
        {
            ProcessPriority.Realtime => Native.REALTIME_PRIORITY_CLASS,
            ProcessPriority.High => Native.HIGH_PRIORITY_CLASS,
            ProcessPriority.AboveNormal => Native.ABOVE_NORMAL_PRIORITY_CLASS,
            ProcessPriority.BelowNormal => Native.BELOW_NORMAL_PRIORITY_CLASS,
            ProcessPriority.Idle => Native.IDLE_PRIORITY_CLASS,
            _ => Native.NORMAL_PRIORITY_CLASS,
        };
        if (priority != Native.NORMAL_PRIORITY_CLASS)
            Native.SetPriorityClass(process, priority);

        if (_cfg.AffinityMask != 0)
            Native.SetProcessAffinityMask(process, (UIntPtr)_cfg.AffinityMask);
    }

    private IntPtr BuildEnvironmentBlock()
    {
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!_cfg.ReplaceEnvironment)
            foreach (System.Collections.DictionaryEntry e in System.Environment.GetEnvironmentVariables())
                vars[(string)e.Key] = (string?)e.Value ?? "";

        foreach (var entry in _cfg.Environment)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var idx = entry.IndexOf('=');
            if (idx <= 0) continue;
            vars[entry[..idx].Trim()] = System.Environment.ExpandEnvironmentVariables(entry[(idx + 1)..]);
        }

        var sb = new StringBuilder();
        foreach (var (k, v) in vars) sb.Append(k).Append('=').Append(v).Append('\0');
        sb.Append('\0');
        return Marshal.StringToHGlobalUni(sb.ToString());
    }

    private Thread StartPump(IntPtr readHandle, LogWriter? sink, string name)
    {
        var handle = readHandle;
        var t = new Thread(() =>
        {
            var buffer = new byte[8192];
            var decoder = Encoding.UTF8.GetDecoder();
            var chars = new char[8192];
            try
            {
                while (true)
                {
                    if (!Native.ReadFile(handle, buffer, (uint)buffer.Length, out var read, IntPtr.Zero) || read == 0)
                        break;
                    if (sink is null) continue;
                    var count = decoder.GetChars(buffer, 0, (int)read, chars, 0);
                    if (count > 0) sink.Write(new string(chars, 0, count));
                }
            }
            catch (Exception e)
            {
                LogQuiet($"Der {name}-Leser wurde beendet: {e.Message}");
            }
            finally
            {
                Native.CloseHandle(handle);
            }
        })
        {
            IsBackground = true,
            Name = $"easyservice-{name}",
        };
        t.Start();
        return t;
    }

    // ----------------------------------------------------------------- wait ---

    private uint WaitForChildExit()
    {
        IntPtr process;
        lock (_childLock) process = _process;
        if (process == IntPtr.Zero) return uint.MaxValue;

        while (true)
        {
            var wait = Native.WaitForSingleObject(process, 200);
            if (wait == Native.WAIT_OBJECT_0) break;
            if (_stopRequested.IsSet)
            {
                StopChild();
                break;
            }
        }

        Native.GetExitCodeProcess(process, out var code);
        return code;
    }

    // ----------------------------------------------------------------- stop ---

    public void RequestStop()
    {
        _stopRequested.Set();
        StopChild();
    }

    /// <summary>Escalating shutdown: Ctrl-C, then WM_CLOSE, then WM_QUIT, then terminate.</summary>
    private void StopChild()
    {
        IntPtr process, job;
        uint pid;
        lock (_childLock)
        {
            process = _process;
            job = _job;
            pid = _pid;
        }
        if (process == IntPtr.Zero || pid == 0) return;

        if (HasExited(process)) return;

        if (_cfg.StopUseConsole && TrySendCtrlC(pid))
        {
            LogQuiet("Strg+C an die Anwendung gesendet.");
            if (WaitExit(process, _cfg.StopConsoleMs)) return;
        }

        if (_cfg.StopUseWindow && PostToWindows(pid))
        {
            LogQuiet("WM_CLOSE an die Fenster der Anwendung gesendet.");
            if (WaitExit(process, _cfg.StopWindowMs)) return;
        }

        if (_cfg.StopUseThreads && PostToThreads(pid))
        {
            LogQuiet("WM_QUIT an die Threads der Anwendung gesendet.");
            if (WaitExit(process, _cfg.StopThreadsMs)) return;
        }

        if (_cfg.StopUseTerminate)
        {
            if (_cfg.KillProcessTree && job != IntPtr.Zero)
            {
                Log("Der Prozessbaum wird beendet.");
                Native.TerminateJobObject(job, 0);
            }
            else
            {
                Log("Der Prozess wird beendet.");
                Native.TerminateProcess(process, 0);
            }
            WaitExit(process, 5000);
        }
        else
        {
            Log("Die Anwendung reagiert nicht und darf laut Konfiguration nicht hart beendet werden.");
        }
    }

    private static bool HasExited(IntPtr process) =>
        Native.GetExitCodeProcess(process, out var code) && code != Native.STILL_ACTIVE;

    private static bool WaitExit(IntPtr process, int ms) =>
        ms > 0 && Native.WaitForSingleObject(process, (uint)ms) == Native.WAIT_OBJECT_0;

    private static bool _ctrlCIgnored;

    /// <summary>
    /// Ctrl-C is delivered asynchronously to every process attached to the console, this one
    /// included. The "ignore" flag therefore has to be set once and left in place - restoring it
    /// right after GenerateConsoleCtrlEvent races the delivery and kills the supervisor itself.
    /// </summary>
    private static void EnsureCtrlCIgnored()
    {
        if (_ctrlCIgnored) return;
        Native.SetConsoleCtrlHandler(IntPtr.Zero, true);
        _ctrlCIgnored = true;
    }

    private bool TrySendCtrlC(uint pid)
    {
        try
        {
            EnsureCtrlCIgnored();
            Native.FreeConsole();
            if (!Native.AttachConsole(pid)) return false;
            try
            {
                return Native.GenerateConsoleCtrlEvent(Native.CTRL_C_EVENT, 0);
            }
            finally
            {
                Native.FreeConsole();
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool PostToWindows(uint pid)
    {
        var posted = false;
        Native.EnumWindows((hWnd, _) =>
        {
            Native.GetWindowThreadProcessId(hWnd, out var owner);
            if (owner == pid && Native.PostMessageW(hWnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero))
                posted = true;
            return true;
        }, IntPtr.Zero);
        return posted;
    }

    private static bool PostToThreads(uint pid)
    {
        var posted = false;
        var snapshot = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPTHREAD, 0);
        if (snapshot == Native.INVALID_HANDLE_VALUE) return false;
        try
        {
            var entry = new Native.THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<Native.THREADENTRY32>() };
            if (!Native.Thread32First(snapshot, ref entry)) return false;
            do
            {
                if (entry.th32OwnerProcessID == pid &&
                    Native.PostThreadMessageW(entry.th32ThreadID, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero))
                    posted = true;
                entry.dwSize = (uint)Marshal.SizeOf<Native.THREADENTRY32>();
            } while (Native.Thread32Next(snapshot, ref entry));
        }
        finally
        {
            Native.CloseHandle(snapshot);
        }
        return posted;
    }

    private void CleanUpChild()
    {
        Thread? outPump, errPump;
        lock (_childLock)
        {
            if (_process != IntPtr.Zero) { Native.CloseHandle(_process); _process = IntPtr.Zero; }
            if (_job != IntPtr.Zero) { Native.CloseHandle(_job); _job = IntPtr.Zero; }
            _pid = 0;
            outPump = _outPump;
            errPump = _errPump;
            _outPump = _errPump = null;
        }
        outPump?.Join(2000);
        errPump?.Join(2000);
    }

    public void Dispose()
    {
        RequestStop();
        CleanUpChild();
        if (!ReferenceEquals(_stdout, _stderr)) _stderr?.Dispose();
        _stdout?.Dispose();
        _events?.Dispose();
        _stopRequested.Dispose();
    }
}
