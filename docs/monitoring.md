# Monitoring integration

EasyService is built so that an administrator does not have to look at it to know how the
services are doing. Every state goes to the monitoring you already run.

**Deutsch:** [monitoring.de.md](monitoring.de.md)

## Why this is needed

The Windows service manager knows exactly one state: is the service process running or
not. With a wrapper like EasyService (or NSSM) the service process is the *wrapper* — not
the actual application.

```
sc query MyDaemon
        STATE : 4  RUNNING          <- looks fine
```

Behind that, the application may have crashed and restarted 412 times today. `sc query`,
`services.msc` and every check that only asks for the service state will still report
"all good".

That is exactly the gap EasyService closes: it knows how often the application was
restarted, how long it has been up this time, what it costs in CPU and memory, and which
exit code it died with last — and it emits that in the formats the common monitoring
systems read directly.

## What is measured

| Value | Meaning |
|---|---|
| `service_running` | The Windows service itself is running |
| `app_running` | The supervised application is running |
| `uptime` | Uptime of the application since its last start |
| `restarts_1h` / `restarts_24h` | Restarts in the window — the flapping detection |
| `restarts_total` | Restarts since the service was started |
| `cpu` | CPU load of the **whole process tree** (100 % = all cores busy) |
| `mem` | Memory of the whole process tree |
| `procs` | Number of processes in the tree |
| `cpu_seconds` | CPU time consumed, as a counter for rates |

Measurement goes through the job object the application already lives in for clean
shutdown. A launcher script that pulls in `java.exe` is therefore counted — measuring
only the main process would report a flat zero there.

The values are refreshed every 5 seconds and stored as JSON under
`%ProgramData%\EasyService\state\<service>.json`. If that file stops being updated for
more than two minutes, the check reports **UNKNOWN** instead of continuing to serve the
old numbers — a dead measurement is worse than none.

## Long-term history

Besides the current state, the supervising process records a history: one row per minute
as CSV under `%ProgramData%\EasyService\history\`.

| File | Contents |
|---|---|
| `<service>-metrics.csv` | `utc,cpu_avg,cpu_max,mem_avg,mem_max,procs,restarts_total` |
| `<service>-events.csv` | `utc,event_id,exit_code,detail` |

Timestamps are UTC in the form `yyyy-MM-ddTHH:mm:ssZ`, numbers are formatted invariantly —
both directly machine-readable. A row is about 56 bytes, which works out to roughly 80 KB
per service and day, or 2.3 MB for the 30 days kept by default. Retention is set per
service in the editor on the *Monitoring* tab (0 turns it off); older rows are dropped
daily.

If you would rather keep the values in your own time-series system, read `metrics.csv`
directly — or take the Prometheus route below and keep the history there. In the interface
a double-click on the service shows the same data as charts.

## Verdict

The raw values are turned into a status following the Nagios convention (0 OK, 1 WARN,
2 CRIT, 3 UNKNOWN):

| Situation | Status |
|---|---|
| Service stopped, startup type *Automatic* | **CRIT** |
| Service stopped, startup type *Manual* or *Disabled* | OK |
| Application restarting constantly and being throttled | **CRIT** |
| Application could not be started | **CRIT** |
| Application exited, no restart configured | **WARN** |
| Status report older than 2 minutes | **UNKNOWN** |
| Thresholds for restarts/CPU/RAM exceeded | **WARN** / **CRIT** |

Thresholds live per service in the editor on the **Monitoring** tab. The defaults are
3 restarts per hour for a warning and 10 for critical; CPU and memory are unlimited out
of the box (0 = do not check).

---

## Checkmk

### States as a local check

One local check covers all supervised services — EasyService prints one line per service,
including perfdata for the graphs.

File `C:\ProgramData\checkmk\agent\local\easyservice.bat`:

```bat
@"C:\Program Files\EasyService\easyservice.exe" checkmk
```

Result (one line per service):

```
0 EasyService_MyDaemon uptime=86400s;;;0;|restarts_1h=0;3;10;0|cpu=2.5%;80;95;0;100|mem=140509184B;;;0|procs=2 Running for 1d 0h, PID 1234, CPU 2.5 %, RAM 134 MB, 0 restarts/h
2 EasyService_Importer uptime=3s;;;0;|restarts_1h=37;3;10;0|cpu=0%;80;95;0;100|mem=8192000B;;;0|procs=1 37 restarts in the last hour (critical from 10) - Running for 3s, PID 9876
```

The message text follows the configured language (see below); status code, item name and
perfdata are language-independent.

Then run `cmk -II <host>` and `cmk -O` on the Checkmk server, and every service shows up
as its own service with graphs for CPU, memory and restarts.

### Wiring up the log files

Important: the `logfiles` section of the Windows agent is **not** responsible for text
files — `check_mk.user.yml` says so explicitly: "We do not support logfiles monitoring in
agent at the moment. Please, use plugin mk_logwatch". The plugin is the right way.

1. Copy `mk_logwatch.py` from the Checkmk server (`~/share/check_mk/agents/plugins/`) to
   `C:\ProgramData\checkmk\agent\plugins\`.
2. Create the configuration at `C:\ProgramData\checkmk\agent\config\logwatch.cfg`:

```
C:\ProgramData\EasyService\logs\*-stderr.log
 C Exception
 C FATAL
 C ERROR
 W WARN
 I .*
```

For EasyService's own diagnostic log (`*-easyservice.log`) there is a caveat: its message
texts follow the configured language, so text patterns would not be portable. **Prefer
the event IDs** (see below) — they are language-independent and exist for exactly this
purpose. If you still want to watch the file, pin the language machine-wide to English:

```cmd
reg add HKLM\SOFTWARE\EasyService /v Language /t REG_SZ /d en /f
```

and then match on the English text:

```
C:\ProgramData\EasyService\logs\*-easyservice.log
 C could not be started
 C terminating the process
 W throttle window
 I .*
```

Paths and the plugin directory differ between agent versions; when in doubt check the
[Checkmk documentation on log file monitoring](https://docs.checkmk.com/latest/en/monitoring_logfiles.html).

### Windows events

The Windows agent reads the application log anyway. EasyService writes there with stable
event IDs (see below), which can be alerted on without matching message text.

---

## Prometheus

```cmd
"C:\Program Files\EasyService\easyservice.exe" prometheus
```

For the textfile collector of `node_exporter` — the file is replaced atomically, so the
collector never sees a half-written state:

```cmd
"C:\Program Files\EasyService\easyservice.exe" prometheus --output C:\ProgramData\node_exporter\textfile\easyservice.prom
```

As a task that runs every minute:

```cmd
schtasks /create /tn "EasyService Metrics" /sc minute /mo 1 /ru SYSTEM ^
  /tr "\"C:\Program Files\EasyService\easyservice.exe\" prometheus --output C:\ProgramData\node_exporter\textfile\easyservice.prom"
```

Output:

```
# HELP easyservice_restarts_1h Restarts in the last hour
# TYPE easyservice_restarts_1h gauge
easyservice_restarts_1h{service="MyDaemon"} 0
easyservice_restarts_1h{service="Importer"} 37
```

Example alerting rules:

```yaml
groups:
  - name: easyservice
    rules:
      - alert: EasyServiceFlapping
        expr: easyservice_restarts_1h > 10
        for: 5m
        annotations:
          summary: "{{ $labels.service }} keeps restarting"

      - alert: EasyServiceApplicationDown
        expr: easyservice_service_running == 1 and easyservice_application_running == 0
        for: 2m
        annotations:
          summary: "{{ $labels.service }}: service up, application not"

      - alert: EasyServiceStale
        expr: easyservice_state_age_seconds > 120
        for: 5m
        annotations:
          summary: "{{ $labels.service }} stopped reporting measurements"
```

The middle rule is the one `sc query` cannot give you.

---

## Zabbix

In `zabbix_agentd.conf`:

```
UserParameter=easyservice.discovery,"C:\Program Files\EasyService\easyservice.exe" zabbix-discovery
UserParameter=easyservice.check[*],"C:\Program Files\EasyService\easyservice.exe" check "$1"
UserParameter=easyservice.json,"C:\Program Files\EasyService\easyservice.exe" json
```

`easyservice.discovery` returns the low-level discovery list:

```json
{ "data": [ { "{#SERVICE}": "MyDaemon", "{#DISPLAYNAME}": "My Daemon" } ] }
```

A template can use that to create items for every supervised service automatically. For
the individual values, `easyservice.json` works well with a dependent item and JSONPath
preprocessing, for example `$[?(@.service=='MyDaemon')].restartsLastHour`.

---

## Nagios / Icinga

```cmd
"C:\Program Files\EasyService\easyservice.exe" check "MyDaemon"
```

```
EASYSERVICE CRITICAL - 37 restarts in the last hour (critical from 10) - Running for 3s, PID 9876 | uptime=3s;;;0 restarts_1h=37;3;10;0 cpu=0%;80;95;0;100
```

Exit code: `0` OK, `1` warning, `2` critical, `3` unknown — the usual plugin convention,
so it can be used straight through NSClient++/NRPE.

---

## Windows event log

EasyService writes to the **application log** under the source `EasyService`. The event
IDs are part of the public contract and are never renumbered — alert on the ID, not on
the message text.

| ID | Meaning | Type |
|---|---|---|
| 1000 | Supervision started | Information |
| 1001 | Application started | Information |
| 1002 | Application exited | Information / Warning |
| 1003 | Application is restarting | Information |
| **1004** | **Restart throttled** — the application dies faster than it starts | **Warning** |
| **1005** | **Start failed** | **Error** |
| 1006 | Service is stopping | Information |
| 1007 | Stopped by exit action | Information |
| **1008** | **Application terminated** — it responded to no request to close | **Warning** |
| 1009 | Configuration problem | Error |
| 1010 | Logging problem | Warning |

The ones in bold are the ones worth an alert.

Querying with PowerShell, for example for a check of your own:

```powershell
Get-WinEvent -FilterHashtable @{ LogName='Application'; ProviderName='EasyService'; Id=1004,1005,1008 } -MaxEvents 20
```

---

## JSON for everything else

```cmd
"C:\Program Files\EasyService\easyservice.exe" json
```

```json
[
  {
    "service": "MyDaemon",
    "status": 0,
    "statusText": "OK",
    "summary": "Running for 1d 0h, PID 1234, CPU 2.5 %, RAM 134 MB, 0 restarts/h",
    "serviceRunning": true,
    "applicationState": "Running",
    "applicationPid": 1234,
    "uptimeSeconds": 86400,
    "restartsTotal": 0,
    "restartsLastHour": 0,
    "cpuPercent": 2.5,
    "memoryBytes": 140509184,
    "lastExitCode": null,
    "stateUpdatedUtc": "2026-08-21T14:22:49.1234567Z"
  }
]
```

All numbers use the dot as the decimal separator regardless of the system language — a
German "2,5" would take every parser apart. A test guards this.

For a single service: `easyservice status <name> --json`.

---

## Language of the output

EasyService speaks English, German, French, Spanish and Italian. Which language an output
uses is decided in this order:

1. `HKCU\Software\EasyService\Language` — the choice in the interface's **Language** menu
2. `HKLM\SOFTWARE\EasyService\Language` — machine-wide default
3. the language Windows itself runs in

Valid values are `en`, `de`, `fr`, `es`, `it` and an empty value for "follow Windows".

For monitoring, point 2 is the important one: the checks run under the agent's account,
not under the account of the administrator who configured them. To get English output on
a German server:

```cmd
reg add HKLM\SOFTWARE\EasyService /v Language /t REG_SZ /d en /f
```

**Language-independent in every case:** status codes (0/1/2/3), item names, metric names,
perfdata, JSON field names and the event IDs. Only the human-readable messages translate.
Alerts should therefore be built on codes and IDs, not on text.

---

## Troubleshooting

**The check reports UNKNOWN and "has not reported any state yet".**
The service is still running an EasyService version from before monitoring support.
Restarting it once is enough.

**The check reports UNKNOWN and "The last status report is … old".**
The supervising process is hung or was killed hard. The state file under
`%ProgramData%\EasyService\state\` stays behind in that case; its values are deliberately
no longer treated as valid.

**CPU stays at 0 permanently.**
CPU load is a difference between two measurements, so the first value arrives at the
earliest about seven seconds after the application starts.

**Memory is reported too low.**
If the process could not be assigned to a job object, EasyService only measures the main
process. That is noted in the service's diagnostic log.
