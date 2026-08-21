<div align="center">

# EasyService

**Turn any program into a Windows service — and actually know how it is doing.**

An open-source alternative to NSSM, built for administrators who have to answer for
what runs on their servers.

**Deutsch:** [README.de.md](README.de.md)

[![build](https://github.com/sdrabent/easyservice/actions/workflows/build.yml/badge.svg)](https://github.com/sdrabent/easyservice/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Downloads](https://img.shields.io/github/downloads/sdrabent/easyservice/total)](../../releases)
[![Languages](https://img.shields.io/badge/UI-en%20%7C%20de%20%7C%20fr%20%7C%20es%20%7C%20it-informational)](#languages)

</div>

---

## The lie your monitoring is telling you

```
C:\> sc query MyImporter

        SERVICE_NAME: MyImporter
        STATE       : 4  RUNNING
```

Looks fine. It is not.

That service is a wrapper — NSSM, srvany, a scheduled task hack, whatever. The thing
Windows reports as `RUNNING` is the *wrapper*. Behind it, the actual importer has crashed
and been restarted **37 times in the last hour**, and every check you have built on
`sc query`, `services.msc` or a Windows-service check in your monitoring says **OK**.

Nobody finds out until a customer calls.

## What EasyService does about it

EasyService is the wrapper *and* the witness. It counts the restarts, measures the
process tree, and hands the verdict to the monitoring you already run.

**One line in your Checkmk agent** — `C:\ProgramData\checkmk\agent\local\easyservice.bat`:

```bat
@"C:\Program Files\EasyService\easyservice.exe" checkmk
```

and every supervised service becomes a Checkmk service with graphs and thresholds:

```
0 EasyService_MyDaemon   uptime=86400s|restarts_1h=0;3;10;0|cpu=2.5%;80;95;0;100|mem=140509184B|procs=2   Running for 1d 0h, PID 1234, CPU 2.5 %, RAM 134 MB, 0 restarts/h
2 EasyService_MyImporter uptime=3s|restarts_1h=37;3;10;0|cpu=0%;80;95;0;100|mem=8192000B|procs=1          37 restarts in the last hour (critical from 10) - Running for 3s, PID 9876
```

There it is. **Critical**, with the reason, with perfdata, without you writing a single
check script.

Not a Checkmk shop? Same data, your format:

| Command | For |
|---|---|
| `easyservice checkmk` | Checkmk local check, one line per service, with perfdata |
| `easyservice prometheus --output …` | Prometheus textfile collector (atomic replace) |
| `easyservice check <name>` | Nagios/Icinga plugin, exit code 0/1/2/3 |
| `easyservice zabbix-discovery` | Zabbix low-level discovery |
| `easyservice json` | Everything, for whatever you have built yourself |

And because message text is a terrible thing to alert on, every event also lands in the
Windows application log with a **stable event ID**:

| ID | Meaning |
|---|---|
| **1004** | Restart throttled — the application is dying faster than it starts |
| **1005** | Start failed |
| **1008** | Application terminated by force |

```powershell
Get-WinEvent -FilterHashtable @{ LogName='Application'; ProviderName='EasyService'; Id=1004,1005,1008 }
```

Those IDs never change and never translate. → **[docs/monitoring.md](docs/monitoring.md)**

## Logging you do not have to build yourself

Console programs write to stdout and stderr. Windows services do not have a console.
That gap is where most homegrown wrapper scripts go to die.

EasyService captures both streams from a real pipe and writes them to files, with:

- **Rotation** by size and/or time, with a capped number of archives — no more 4 GB log
  eating the system drive over Christmas
- **Optional timestamps** per line, for applications that do not bother
- **Merge or separate** stdout and stderr, your call
- A **diagnostic log per service** recording what the supervisor itself did: started,
  exited with code 3, throttled, terminated

...and a **live viewer built in**, so you do not RDP in and open Notepad:

- attaches to the file while the service keeps writing (`FileShare.ReadWrite`)
- follows rotation on its own
- offers the archived files from a dropdown
- filters by text
- second tab shows the Windows event log entries for that service

Point `mk_logwatch` at `%ProgramData%\EasyService\logs\*-stderr.log` and your application's
own error output becomes a monitored log — the [monitoring guide](docs/monitoring.md) has
the config.

## History: what did this thing do last night?

![EasyService overview](assets/screenshot-uebersicht.png)

*Three supervised services. One healthy, one restarting in a loop and flagged red, one
deliberately stopped. Windows would report the first two identically.*

Double-click a service and you get its past, not just its present:

- **CPU and memory over time** — separate charts, because they are separate scales.
  The line is the per-minute average, the band the peak, so a service that idles at 2 %
  and spikes to 90 % every minute does not hide behind a flat average.
- **Restart markers** on the timeline
- **Key figures** for the window: restarts, CPU average and peak, memory average and peak
- **Event list** with exit codes
- 1 hour to 30 days, exportable as CSV

Stored as plain **CSV** under `%ProgramData%\EasyService\history\` — about 80 KB per
service and day, so roughly 2.3 MB for the 30 days kept by default (adjustable, 0 turns
recording off). In five years you will still be able to open it in Excel without
EasyService installed.

## 60 seconds to your first service

![Quick setup](assets/screenshot-schnelleinrichtung.png)

1. Start `easyservice.exe`, confirm UAC.
2. **Add service…** — or drop the `.exe` straight onto the window.
3. Pick the program. Service name, startup directory, log paths, rotation, restart policy
   and monitoring thresholds are filled in and *shown to you*.
4. **Create service.** Done.

Four fields instead of nine tabs. The service account is remembered for the next one;
the password stays in memory for the session only, unless you explicitly ask for it to be
stored (DPAPI, your Windows account, not machine-wide).

If the first start fails, EasyService offers you the log immediately — it is almost
always a wrong path or a wrong argument, and you will see which in about four seconds.

Need the full control panel? **Advanced settings…** opens the nine-tab editor.

Scripting a rollout instead?

```cmd
easyservice install MyDaemon "C:\apps\daemon.exe" --config C:\apps\daemon.yml
easyservice status MyDaemon   :: exit code 0 = running, 3 = not
```

## How it compares

| | `sc.exe` | NSSM | **EasyService** |
|---|---|---|---|
| Run any program as a service | ✗ | ✓ | ✓ |
| stdout/stderr to files | ✗ | ✓ | ✓ |
| Automatic log rotation | ✗ | ✓ | ✓ |
| Restart policy per exit code | ✗ | ✓ | ✓ |
| Staged, graceful shutdown | ✗ | ✓ | ✓ |
| Reliable process-tree cleanup | ✗ | ✓ | ✓ |
| Single .exe, no installation | ✓ | ✓ | ✓ |
| Graphical interface | ✗ | partly | **✓ complete** |
| **Monitoring integration** | ✗ | ✗ | **✓ Checkmk, Prometheus, Zabbix, Nagios** |
| **Flapping detection** | ✗ | ✗ | **✓** |
| **Stable event IDs to alert on** | ✗ | ✗ | **✓** |
| **Built-in live log viewer** | ✗ | ✗ | **✓** |
| **History: CPU, memory, restarts** | ✗ | ✗ | **✓** |
| **Quick setup with sensible defaults** | ✗ | ✗ | **✓** |
| **Five UI languages** | ✗ | English only | **✓ en, de, fr, es, it** |
| Dark mode | ✗ | ✗ | ✓ |
| Open source | ✗ | ✓ (public domain) | ✓ (MIT) |

NSSM is a good tool and EasyService owes it the idea. What it does not do is tell your
monitoring anything.

## Download

**[→ Releases](../../releases)**

| File | Description |
|---|---|
| `easyservice.exe` | Everything included, runs straight away. No installation. |
| `easyservice-framework-dependent.exe` | Only ~300 KB, needs the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0). |

> **Important:** the path to `easyservice.exe` is stored in every service you create.
> Move the file later and those services stop working. Pick a permanent location right
> away, for example `C:\Program Files\EasyService\` — a directory ordinary users cannot
> write to, which also matters because the service runs as SYSTEM.

Requirements: Windows 10 / Server 2016 or newer, x64, administrator rights (managing
services on Windows always requires elevation — EasyService asks via UAC at start).

The binaries are **not code-signed yet**, so SmartScreen will warn on first download.
Checksums are published with every release.

---

## Everything else

### The editor, tab by tab

**Application** — program, startup directory and arguments.

**Details** — display name, description, startup type (automatic / delayed / manual /
disabled), process priority, processor affinity and an optional startup delay.

**Log on** — local system account, local service, network service or a user account. For
a user account EasyService grants the *Log on as a service* right
(`SeServiceLogonRight`) automatically, which otherwise has to be set by hand in
`secpol.msc`.

**Dependencies** — other services that have to run first, from a picker.

**Environment** — additional environment variables (`NAME=VALUE`), either extending or
completely replacing the system environment.

**Logging** — the file, rotation and timestamp settings described above.

**Exit actions** — restart, ignore, or stop the service, optionally per exit code. A
throttle window prevents restart loops: if the application exits faster than configured,
EasyService doubles the wait, up to 60 seconds.

**Monitoring** — thresholds for restarts per hour, CPU and memory, plus ready-made
copy-paste snippets for the Checkmk, Prometheus, Nagios and Zabbix agents.

**Shutdown** — the staged shutdown. Every stage switchable, each with its own timeout:

1. `Ctrl+C` to the application's console (for console programs)
2. `WM_CLOSE` to all of the application's windows
3. `WM_QUIT` to all of the application's threads
4. Hard terminate — optionally including all child processes

### Safety net when deleting

Services *not* created with EasyService cannot be edited, and can only be removed after
an extra confirmation in which the service name has to be typed out. A misclick cannot
destroy a system service.

### Languages

English, German, French, Spanish and Italian. Left alone, EasyService follows the
language Windows runs in; the **Language** menu pins it. For servers where monitoring
runs under a different account than the administrator who set it up:

```cmd
reg add HKLM\SOFTWARE\EasyService /v Language /t REG_SZ /d en /f
```

Status codes, metric names, perfdata, JSON fields and event IDs never translate — only
the human-readable messages do, so alerts keep working across languages. Translations are
`.resx` files under `src/EasyService/Resources/`.

> The French, Spanish and Italian translations have not yet been reviewed by native
> speakers. Corrections are very welcome.

### Command line

```
easyservice list [--json]
easyservice install <name> <program> [arguments...]
easyservice remove <name>
easyservice start|stop|restart|status <name>
easyservice checkmk | prometheus [--output <file>] | check <name> | json | zabbix-discovery
easyservice gui [name] | gui --new
```

### How it works

`easyservice.exe` is deliberately a single file with two modes — exactly like `nssm.exe`:

```
Double-click                     Service Control Manager
      │                                    │
      ▼                                    ▼
easyservice.exe            easyservice.exe run "MyService"
      │                                    │
   GUI mode                        supervisor mode
   (MainForm)                              │
                              ┌────────────┴────────────┐
                              │                         │
                      read configuration        CreateProcess with
                      from the registry         redirected pipes
                                                         │
                                          ┌──────────────┼──────────────┐
                                          ▼              ▼              ▼
                                       stdout         stderr       job object
                                          │              │       (process tree)
                                          ▼              ▼
                                      rotating log files
```

When you create a service, EasyService registers itself as the service binary
(`"C:\Program Files\EasyService\easyservice.exe" run "MyService"`) and stores the
configuration under `HKLM\SYSTEM\CurrentControlSet\Services\<name>\Parameters`. When
Windows starts the service, the same `.exe` runs in supervisor mode, reads the
configuration, launches the real application and watches over it.

The application ends up inside a Windows job object. That is what makes it possible to
reliably take all child processes down with it — the classic case where a service created
with `sc.exe` leaves orphans behind. It is also the accounting boundary for the CPU and
memory measurements, so a batch file that spawns `java.exe` is counted properly instead
of reporting zero.

Windows' own service recovery actions are configured as well, as a second safety net in
case the supervisor process itself fails.

### Registry reference

All values live under `HKLM\SYSTEM\CurrentControlSet\Services\<name>\Parameters`,
inspectable with `regedit` and settable from a script:

| Value | Type | Meaning |
|---|---|---|
| `Application` | EXPAND_SZ | Path to the program |
| `AppDirectory` | EXPAND_SZ | Startup directory |
| `AppParameters` | EXPAND_SZ | Arguments |
| `AppPriority` | DWORD | 0 = realtime … 5 = low |
| `AppAffinity` | QWORD | Processor mask, 0 = all |
| `AppStartupDelay` | DWORD | Delay before the first start (ms) |
| `AppEnvironmentExtra` | MULTI_SZ | Additional variables `NAME=VALUE` |
| `AppEnvironmentReplace` | DWORD | 1 = replace the system environment |
| `AppStdout` / `AppStderr` | EXPAND_SZ | Log files |
| `AppAppendOutput` | DWORD | 1 = append, 0 = truncate at start |
| `AppTimestampLog` | DWORD | 1 = timestamp per line |
| `AppRotateFiles` | DWORD | 1 = rotation active |
| `AppRotateBytes` | QWORD | Rotation size in bytes |
| `AppRotateSeconds` | DWORD | Rotation interval, 0 = by size only |
| `AppRotateKeep` | DWORD | Number of archives, 0 = unlimited |
| `AppExitDefault` | DWORD | 0 = restart, 1 = ignore, 2 = stop service |
| `AppExit\<code>` | DWORD | Action for a specific exit code |
| `AppRestartDelay` | DWORD | Wait before restarting (ms) |
| `AppThrottle` | DWORD | Throttle window (ms) |
| `AppStopUseConsole` / `…Window` / `…Threads` | DWORD | Shutdown stages enabled |
| `AppStopConsoleDelay` / `…WindowDelay` / `…ThreadsDelay` | DWORD | Time limit per stage (ms) |
| `AppStopUseTerminate` | DWORD | 1 = terminate if necessary |
| `AppKillProcessTree` | DWORD | 1 = take child processes down too |
| `AppLogServiceEvents` | DWORD | 1 = write the diagnostic log |
| `MonEnabled` | DWORD | 1 = report to monitoring |
| `MonWarnCpu` / `MonCritCpu` | DWORD | CPU thresholds in %, 0 = do not check |
| `MonWarnMemoryMb` / `MonCritMemoryMb` | DWORD | Memory thresholds in MB |
| `MonWarnRestartsPerHour` / `MonCritRestartsPerHour` | DWORD | Restart thresholds |
| `HistoryDays` | DWORD | Days of history to keep, 0 = off |

Logs default to `%ProgramData%\EasyService\logs\`, history to
`%ProgramData%\EasyService\history\`.

### Building from source

You need the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (or newer) on
a Windows machine.

```cmd
git clone https://github.com/sdrabent/easyservice.git
cd easyservice
dotnet build EasyService.sln -c Release
```

Single file:

```cmd
dotnet publish src/EasyService/EasyService.csproj -c Release -r win-x64 ^
  --self-contained true -p:PublishSingleFile=true ^
  -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o publish
```

No NuGet dependencies; every Windows call goes straight through P/Invoke (`advapi32`,
`kernel32`, `user32`, `crypt32`). After editing a `.resx`, regenerate the typed string
accessors with `python tools/generate-strings.py` — CI checks that they have not drifted.

### Tests

```cmd
dotnet run --project tests/EasyService.Tests -c Release
```

The tests drive the supervisor directly and need neither administrator rights nor an
installed service. They cover output redirection, the restart policy, exit-code actions,
rotation including the archive cap, stopping running applications, timestamps,
environment variables, reading the service list, constructing every dialog, the complete
monitoring chain down to the Checkmk and Prometheus output formats, the history store and
its retention, and that every text exists in all five languages with its placeholders
intact — a lost `{0}` would otherwise surface as a `FormatException` at runtime.

### Troubleshooting

**The service does not start and stops immediately.**
Look at the service's EasyService log (`…-easyservice.log`, via **Logs…** → the file
picker). It records whether the program was found, which code it exited with, and which
action was taken. The same messages go to the Windows application log under the source
`EasyService`.

**The service runs but the application does nothing.**
Usually the startup directory is wrong. Many programs look for configuration files
relative to the current directory; without one specified, EasyService uses the program's
own folder.

**Processes are left behind after stopping.**
Enable *Also terminate all child processes* on the *Shutdown* tab.

**The application is restarted over and over.**
If it exits with code 0 on purpose, add a rule `exit code 0 → stop the service` under
*Exit actions*.

**A service cannot be created: "marked for deletion".**
Windows keeps the service key as long as a handle is still open — typically an open
`services.msc`. Close that window or reboot.

**The monitoring check says UNKNOWN.**
Either the service still runs an older EasyService version (restart it once), or its
status report is stale, which means the supervising process is not responding. A dead
measurement is reported as unknown on purpose rather than as healthy.

## Contributing

Bug reports and pull requests are welcome. For larger changes please open an issue first
so the direction is agreed. `dotnet build` has to run warning-free and the tests have to
pass. Translation fixes are especially welcome — the `.resx` files under
`src/EasyService/Resources/` are the source of truth.

## License

MIT — see [LICENSE](LICENSE).
