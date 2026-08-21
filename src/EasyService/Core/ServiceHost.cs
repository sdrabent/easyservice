using System.Runtime.InteropServices;

namespace EasyService.Core;

/// <summary>
/// The service-side half of easyservice.exe. When the SCM starts us with
/// "run &lt;name&gt;" we hand control to the Windows service dispatcher and supervise
/// the configured application until we are told to stop.
/// </summary>
public static class ServiceHost
{
    private static string _serviceName = "";
    private static IntPtr _statusHandle = IntPtr.Zero;
    private static Native.SERVICE_STATUS _status;
    private static ProcessSupervisor? _supervisor;
    private static uint _checkPoint = 1;

    // The delegates are handed to native code; keeping them in statics stops the GC
    // from collecting them while the SCM still holds the pointers.
    private static Native.ServiceMainProc? _serviceMain;
    private static Native.HandlerEx? _handler;

    public static int Run(string serviceName)
    {
        _serviceName = serviceName;
        _serviceMain = ServiceMain;

        var namePtr = Marshal.StringToHGlobalUni(serviceName);
        var table = Marshal.AllocHGlobal(Marshal.SizeOf<Native.SERVICE_TABLE_ENTRY>() * 2);
        try
        {
            var entry = new Native.SERVICE_TABLE_ENTRY
            {
                lpServiceName = namePtr,
                lpServiceProc = Marshal.GetFunctionPointerForDelegate(_serviceMain),
            };
            Marshal.StructureToPtr(entry, table, false);
            Marshal.StructureToPtr(new Native.SERVICE_TABLE_ENTRY(), table + Marshal.SizeOf<Native.SERVICE_TABLE_ENTRY>(), false);

            if (!Native.StartServiceCtrlDispatcherW(table))
            {
                var err = Marshal.GetLastWin32Error();
                EventLogSink.Error(serviceName, EasyServiceEvent.ConfigurationProblem,
                    $"StartServiceCtrlDispatcher ist fehlgeschlagen ({err}). " +
                    "easyservice.exe run <Dienst> ist nur für den Start durch den Dienst-Manager gedacht.");
                return err;
            }
            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(table);
            Marshal.FreeHGlobal(namePtr);
        }
    }

    private static void ServiceMain(int argc, IntPtr argv)
    {
        _handler = HandlerEx;
        _statusHandle = Native.RegisterServiceCtrlHandlerExW(_serviceName, _handler, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero) return;

        _status = new Native.SERVICE_STATUS
        {
            dwServiceType = Native.SERVICE_WIN32_OWN_PROCESS,
            dwCurrentState = Native.SERVICE_START_PENDING,
            dwControlsAccepted = 0,
            dwWin32ExitCode = 0,
            dwCheckPoint = _checkPoint++,
            dwWaitHint = 10_000,
        };
        Native.SetServiceStatus(_statusHandle, ref _status);

        ServiceConfig? config;
        try
        {
            config = ServiceConfig.Load(_serviceName);
        }
        catch (Exception e)
        {
            Fail($"Die Konfiguration konnte nicht gelesen werden: {e.Message}", 1);
            return;
        }

        if (config is null || string.IsNullOrWhiteSpace(config.Application))
        {
            Fail("Für diesen Dienst ist keine EasyService-Konfiguration hinterlegt " +
                 @"(HKLM\SYSTEM\CurrentControlSet\Services\" + _serviceName + @"\Parameters).", 1);
            return;
        }

        try
        {
            _supervisor = new ProcessSupervisor(config);
            _supervisor.StopServiceRequested += OnStopServiceRequested;

            SetState(Native.SERVICE_RUNNING,
                Native.SERVICE_ACCEPT_STOP | Native.SERVICE_ACCEPT_SHUTDOWN | Native.SERVICE_ACCEPT_PRESHUTDOWN);

            _supervisor.Run();

            SetState(Native.SERVICE_STOPPED, 0);
        }
        catch (Exception e)
        {
            EventLogSink.Error(_serviceName, EasyServiceEvent.ApplicationStartFailed,
                "Der Dienst wurde durch einen Fehler beendet: " + e);
            Fail("Unerwarteter Fehler: " + e.Message, 1);
        }
        finally
        {
            _supervisor?.Dispose();
            _supervisor = null;
        }
    }

    private static void OnStopServiceRequested(uint exitCode)
    {
        // The exit policy asked us to end the service; report the application's code to the SCM
        // so configured recovery actions can react to it.
        _status.dwWin32ExitCode = exitCode == 0 ? 0u : 1067u /* ERROR_PROCESS_ABORTED */;
        _status.dwServiceSpecificExitCode = exitCode;
    }

    private static uint HandlerEx(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        switch (control)
        {
            case Native.SERVICE_CONTROL_STOP:
            case Native.SERVICE_CONTROL_SHUTDOWN:
            case Native.SERVICE_CONTROL_PRESHUTDOWN:
                SetState(Native.SERVICE_STOP_PENDING, 0, waitHint: 30_000);
                // The handler must return promptly, so the actual shutdown runs on its own thread.
                var t = new Thread(() =>
                {
                    try { _supervisor?.RequestStop(); }
                    catch (Exception e)
                    {
                        EventLogSink.Error(_serviceName, EasyServiceEvent.ServiceStopping,
                            "Fehler beim Beenden: " + e.Message);
                    }
                })
                { IsBackground = true, Name = "easyservice-stop" };
                t.Start();
                return 0;

            case Native.SERVICE_CONTROL_INTERROGATE:
                Native.SetServiceStatus(_statusHandle, ref _status);
                return 0;

            default:
                return 0; // ERROR_CALL_NOT_IMPLEMENTED would be 1053 but 0 is friendlier here
        }
    }

    private static void SetState(uint state, uint accepted, uint waitHint = 0)
    {
        _status.dwCurrentState = state;
        _status.dwControlsAccepted = accepted;
        _status.dwWaitHint = waitHint;
        _status.dwCheckPoint = state is Native.SERVICE_RUNNING or Native.SERVICE_STOPPED ? 0 : _checkPoint++;
        if (_statusHandle != IntPtr.Zero) Native.SetServiceStatus(_statusHandle, ref _status);
    }

    private static void Fail(string message, uint exitCode)
    {
        EventLogSink.Error(_serviceName, EasyServiceEvent.ConfigurationProblem, message);
        _status.dwWin32ExitCode = 1066; // ERROR_SERVICE_SPECIFIC_ERROR
        _status.dwServiceSpecificExitCode = exitCode;
        SetState(Native.SERVICE_STOPPED, 0);
    }
}
