# EasyService

**The service wrapper for administrators who carry the pager.** It runs any program as a
Windows service, checks whether that program is still actually working, and hands the
answer to Checkmk, Prometheus, Zabbix or Nagios before anyone has to ask.

Here is the state of this corner of the world. NSSM's last stable release is from August
2014, its newest build of any kind a 2017 prerelease. WinSW has no interface at all. The
tools that do the whole job are licensed per machine. EasyService does it in one
executable, for nothing, and needs no runtime.

It works the way NSSM does: a supervisor process sits between the Service Control Manager
and your application. The difference is what the supervisor knows. It counts restarts,
measures CPU and memory of the whole process tree, keeps exit codes, and asks the
application every half minute whether it is still answering — because a deadlocked process
looks exactly like a healthy one to Windows, and that is the outage nobody gets woken for.

[![build](https://github.com/sdrabent/easyservice/actions/workflows/build.yml/badge.svg)](https://github.com/sdrabent/easyservice/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**Deutsch:** [README.de.md](README.de.md)

## Status

This is a young project. What that means concretely:

* The supervisor, the monitoring output and the configuration format have automated
  tests. On top of that, every commit runs an end-to-end test on a clean Windows machine
  in CI: it creates a real service, starts it, checks the log file and the event log,
  kills the child process and watches it come back, stops it and removes it again.
* It has been run on Windows 11 and in CI on `windows-latest`. Nobody has run it on
  Server 2016, 2019 or 2022 yet, as far as I know.
* The binaries are not code-signed, so SmartScreen warns on first download. See
  [Limitations](#limitations).
* The French, Spanish and Italian translations have not been reviewed by native speakers.

If you try it somewhere and it breaks, an issue with the service's `…-easyservice.log`
attached is genuinely useful.

## What it does

| | |
|---|---|
| Service management | Create, edit, start, stop and remove services from a GUI or the command line |
| Output capture | stdout and stderr to files, separate or merged, with size- and time-based rotation and a capped number of archives |
| Log viewer | Attaches to the live file, follows rotation, filters by text, shows the matching Windows event log entries |
| Restart policy | Per exit code, with an exponential back-off that stops restart loops |
| Shutdown | Ctrl+C, then `WM_CLOSE`, then `WM_QUIT`, then terminate; each stage optional with its own timeout |
| Process tree | Children run inside a job object, so they are terminated with the service and counted in its resource usage |
| Health checks | Ask the application itself: fetch a URL, open a TCP port, watch a file, or run a program. Report it or restart on failure |
| Monitoring | Checkmk, Prometheus, Nagios/Icinga and Zabbix output, plus stable event IDs |
| History | Per-minute CPU, memory and restart records, kept as CSV |
| Configuration as a file | JSON export and import, in the window or on the command line, for rolling the same definition out to many machines |
| Live output in the window | Select a service and the pane below the list tails its log while it runs |
| Languages | English, German, French, Spanish, Italian |

![EasyService overview](assets/screenshot-overview.png)

## Monitoring

A wrapper hides the thing you want to know. `sc query` reports the state of the
supervisor process, not of the application behind it, so a service whose application
crashes and restarts every minute still shows up as `RUNNING`.

EasyService counts those restarts and reports them. Put one line in the Checkmk agent's
`local` directory:

```bat
@"C:\Program Files\EasyService\easyservice.exe" checkmk
```

and each supervised service becomes a Checkmk service. Actual output from a test machine,
with one healthy service and one that could not reach its database:

```
0 EasyService_DemoWebApi   uptime=1070s|restarts_1h=0;3;10;0|cpu=7.41%;;;0;100|mem=75345920B|procs=2   Running for 17m 50s, PID 5868, 2 processes, CPU 7.41 %, RAM 71.9 MB, 0 restarts/h
2 EasyService_DemoImporter uptime=0s|restarts_1h=36;3;10;0|cpu=0%;;;0;100|mem=0B|procs=0               The application keeps restarting and is being throttled. 36 restarts in the last hour, last exit code 3.
```

Other systems get the same data in their own format:

```
easyservice prometheus --output C:\...\easyservice.prom   textfile collector, replaced atomically
easyservice check <name>                                  Nagios/Icinga plugin, exit code 0/1/2/3
easyservice zabbix-discovery                              low-level discovery
easyservice json                                          everything, for your own scripts
```

Message text is a poor thing to alert on, so every event also carries a stable ID in the
Windows application log: 1004 when restarts are being throttled, 1005 when a start failed,
1008 when the application had to be terminated. Those IDs are part of the format and are
not translated.

```powershell
Get-WinEvent -FilterHashtable @{ LogName='Application'; ProviderName='EasyService'; Id=1004,1005,1008 }
```

Configuration snippets for each system are in [docs/monitoring.md](docs/monitoring.md),
including the `mk_logwatch` setup for feeding your application's stderr into Checkmk.

## Health checks

A running process is not a working application. One that has deadlocked, lost its database
connection or stopped serving requests looks exactly like a healthy one to Windows, and
that is the failure mode nobody has an alert for.

So ask the application. Four ways, on the **Health check** tab of the editor:

```
Http        http://localhost:8080/health    status 2xx, or the one you name
Tcp         localhost:5432                  the port accepts a connection
FileFresh   C:\app\heartbeat.txt              written to within the last N seconds
Command     "C:\app\check.exe" --quick        exit code 0
```

Around it: how often, how long to wait, how long to leave the application alone after a
start, and how many failures in a row it takes before the verdict changes. One failed probe
during a garbage collection is not an outage, so it stays in the diagnostic log.

Then either report it — the service goes **critical** in Checkmk, Prometheus, Nagios and
Zabbix, ahead of any CPU threshold — or restart the application and report that too, with
event ID 1013.

Trying it before trusting it:

```cmd
easyservice health MyDaemon
healthy - HTTP 200 OK (34 ms)
```

The editor has a **Test now** button doing the same with the values on screen. Typing a URL
and finding out three minutes later from the event log that it was the wrong one is not a
way to configure anything.

## Where it stands against the others

| | EasyService | NSSM | WinSW | AlwaysUp / FireDaemon |
|---|---|---|---|---|
| Price | free, MIT | free | free | licensed per machine |
| Graphical interface | yes | installer dialog | none | yes |
| Health check beyond "the process exists" | yes | no | no | yes |
| Live output in the window | yes | no | no | yes |
| CPU and memory history | yes | no | no | partly |
| Checkmk / Prometheus / Nagios / Zabbix output | yes | no | no | no — email and their own web UI |
| Whole definition as one file | JSON | no | XML | partly |
| Signed binaries | **no** | no | yes | **yes** |
| Support you can phone | **no** | no | no | **yes** |
| Scheduled restarts | **not yet** | no | no | **yes** |
| Last release | this month | 2014 | maintenance | current |

The bold rows are the ones where paying is the better answer. If your policy needs signed
binaries and a vendor to escalate to, buy the licence — that is a real reason, and no
feature list changes it.

## History

Double-clicking a service shows what it has been doing rather than what it is doing right
now. The supervisor condenses its 5-second samples into one row per minute and keeps them
as CSV under `%ProgramData%\EasyService\history\` — about 80 KB per service and day, or
2.3 MB for the 30 days kept by default.

![Service history](assets/screenshot-history.png)

CPU and memory are drawn in separate charts because they are separate scales; two y-axes
in one frame produce a picture that looks informative and reads wrong. The line is the
per-minute average and the band the peak, which keeps a service that idles at 2 % and
spikes to 90 % distinguishable from one sitting at 40 %. Dotted verticals mark application
starts.

The screenshot above is a demo service running a synthetic load cycle for half an hour and
recycling itself every five minutes.

## Setting up a service

![Quick setup](assets/screenshot-quicksetup.png)

Pick a program, or drop an `.exe` onto the window. Service name, startup directory, log
paths, rotation, restart policy and monitoring thresholds get filled in and shown; the
account and password fields only appear if you switch away from the local system account.
The full editor with all nine tabs is behind **Advanced settings…**.

When the first start fails, EasyService offers the log straight away. In practice the
cause is a wrong path or a wrong argument, and the log says which.

From a script:

```cmd
easyservice install MyDaemon "C:\apps\daemon.exe" --config C:\apps\daemon.yml
easyservice status MyDaemon
```

`status` exits with 0 when the service is running and 3 when it is not.

## Rolling out to many machines

A complete definition — including exit-code rules, thresholds, environment variables and
the shutdown stages — can be written to a file and applied elsewhere:

```cmd
easyservice export MyDaemon --output daemon.json
easyservice import daemon.json
```

```powershell
# same definition on every server
$servers | ForEach-Object {
    Copy-Item daemon.json "\\$_\C$\temp\"
    Invoke-Command -ComputerName $_ { easyservice import C:\temp\daemon.json }
}

# what drifted?
easyservice export MyDaemon --output current.json
git diff --no-index golden.json current.json
```

Two things about the format. Passwords are not written to the file, since a file that
ends up in a repository must not carry a service credential; on import the password is
read from `EASYSERVICE_PASSWORD`, which keeps it out of the process list where a command
line argument would be visible. Updating an existing service without that variable leaves
the stored password alone. Enum values are written as text (`"startup": "AutomaticDelayed"`)
so that a diff against a reference file can be read.

`export --all` writes every managed service into one file, and `import` accepts both
shapes.

The same three actions sit under **Configuration** in the window's toolbar, which is
usually where the first file comes from: set a service up by hand, export it, put it in
the repository, roll it out from the command line. On import the window asks for the
password of a service account instead of reading the environment variable.

## Install

Binaries are on the [releases page](../../releases).

| File | |
|---|---|
| `easyservice.exe` | Self-contained, about 63 MB, no runtime needed |
| `easyservice-framework-dependent.exe` | About 300 KB, needs the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) |

The path to the executable is stored in every service you create, so pick a permanent
location before creating anything. `C:\Program Files\EasyService\` is a reasonable choice
and has the useful property that ordinary users cannot write to it — the service runs as
SYSTEM, so a writable location would be a privilege escalation waiting to happen.

Requirements: Windows 10 or Server 2016 and newer, x64. The monitoring commands run
under any account — that is what lets a monitoring agent use them without a privileged
service account. Creating, changing and removing services needs administrator rights;
those commands exit with code 5 without it, and the interface asks for elevation via UAC
when it starts.

Before rolling it out: [docs/deployment.md](docs/deployment.md) covers verifying the
download, the AppLocker and WDAC rules, and the machine-wide language setting.

## Limitations

* **Not code-signed.** SmartScreen warns on first download, and AppLocker or WDAC will
  block it outright until you allow it by hash. Releases carry SHA256 checksums, a
  CycloneDX SBOM and a GitHub build attestation, so the origin is at least verifiable —
  see [docs/deployment.md](docs/deployment.md). A real signature needs a certificate.
* **Exit codes are not visible to a shell.** `easyservice.exe` is a Windows subsystem
  program, so cmd and PowerShell do not wait for it and `%ERRORLEVEL%` / `$LASTEXITCODE`
  stay empty. Output redirection and pipes work; for the exit code, use
  `Start-Process -Wait -PassThru`. Monitoring agents that spawn the process themselves are
  not affected.
* **No installer.** You copy the exe somewhere and run it. No MSI, no winget package yet.
* **x64 only.** No ARM64 build.
* **Interacting with the desktop does not really work.** The option exists because the
  service API has it, but Windows isolates services in session 0, so nobody sees those
  windows.
* **Resource measurement depends on the job object.** If a child process escapes it, that
  process is neither counted nor terminated with the service. The service's diagnostic log
  notes when the job object could not be set up.

## Documentation

* [docs/deployment.md](docs/deployment.md) — verifying a download, application control,
  where to put the executable, one log language across the fleet
* [docs/monitoring.md](docs/monitoring.md) — Checkmk, Prometheus, Zabbix, Nagios, the
  event IDs, and the language setting for check output
* The editor's **Monitoring** tab carries copy-paste snippets for each agent

## Building

Needs the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) on Windows.

```cmd
git clone https://github.com/sdrabent/easyservice.git
cd easyservice
dotnet build EasyService.sln -c Release
dotnet run --project tests/EasyService.Tests -c Release
```

The end-to-end test creates a real service, so it needs an elevated shell:

```powershell
dotnet publish src/EasyService/EasyService.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/standalone
powershell -ExecutionPolicy Bypass -File tests\e2e\Invoke-ServiceTest.ps1
```

There are no NuGet dependencies; the Windows calls go through P/Invoke against
`advapi32`, `kernel32`, `user32` and `crypt32`. Interface texts live in `.resx` files
under `src/EasyService/Resources/`; after editing one, run `python tools/generate-strings.py`
to regenerate the typed accessors. CI checks that the two have not drifted apart, and that
every string exists in all five languages with its placeholders intact.

The tests drive the supervisor directly, so they need neither administrator rights nor an
installed service.

## How it works

`easyservice.exe` has two modes, like `nssm.exe`. Double-clicked it is the management
GUI. Started by the Service Control Manager as `easyservice.exe run "MyService"` it reads
the configuration from `HKLM\SYSTEM\CurrentControlSet\Services\<name>\Parameters`,
launches the application with redirected pipes, and supervises it.

The application is assigned to a job object. That is what makes it possible to take child
processes down reliably, which is the usual failure of a service created with `sc.exe`,
and it doubles as the accounting boundary for the CPU and memory figures — a batch file
that starts `java.exe` is counted properly rather than reported as zero.

Windows' own service recovery actions are configured as a second line of defence in case
the supervisor process itself fails.

<details>
<summary>Registry reference — every value under <code>Parameters</code></summary>

Inspectable with `regedit`, settable from a script, and what `export` and `import`
read and write.

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
| `HealthType` | DWORD | 0 none, 1 http, 2 tcp, 3 file, 4 command |
| `HealthTarget` | EXPAND_SZ | URL, host:port, file path or command line |
| `HealthInterval` / `HealthTimeout` / `HealthGrace` | DWORD | Interval, time limit, grace after a start (ms) |
| `HealthFailures` | DWORD | Failures in a row before the service counts as unhealthy |
| `HealthAction` | DWORD | 0 report only, 1 restart the application |
| `HealthExpectStatus` | DWORD | HTTP: expected status, 0 accepts 200-299 |
| `HealthMaxAge` | DWORD | File check: how old the file may be, in seconds |

Logs default to `%ProgramData%\EasyService\logs\`, history to
`%ProgramData%\EasyService\history\`.

</details>

## Contributing

Issues and pull requests are welcome. For anything larger than a fix, an issue first
saves us both time. `dotnet build` has to stay warning-free and the tests have to pass.

Translation corrections are especially welcome — the `.resx` files are the source of
truth and open in any translation tool.

## License

MIT, see [LICENSE](LICENSE).
