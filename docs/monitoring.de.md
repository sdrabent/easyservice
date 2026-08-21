# Monitoring-Anbindung

EasyService ist so gebaut, dass ein Administrator es nicht anschauen muss, um zu wissen,
wie es den Diensten geht. Alle Zustände gehen an das vorhandene Monitoring.

**English:** [monitoring.md](monitoring.md)

## Warum das nötig ist

Der Windows-Dienst-Manager kennt nur einen Zustand: läuft der Dienstprozess oder nicht.
Bei einem Wrapper wie EasyService (oder NSSM) ist der Dienstprozess aber der *Wrapper* —
nicht die eigentliche Anwendung.

```
sc query MeinDaemon
        STATE : 4  RUNNING          <- sieht gut aus
```

Dahinter kann die Anwendung heute 412-mal abgestürzt und neu gestartet worden sein.
`sc query`, `services.msc` und jeder Check, der nur den Dienststatus abfragt, melden
trotzdem „alles in Ordnung".

Genau diese Lücke schließt EasyService: es weiß, wie oft die Anwendung neu gestartet
wurde, wie lange sie diesmal läuft, was sie an CPU und Speicher kostet und mit welchem
Exit-Code sie zuletzt gestorben ist — und gibt das in den Formaten aus, die die gängigen
Monitoring-Systeme direkt lesen.

## Was gemessen wird

| Wert | Bedeutung |
|---|---|
| `service_running` | Der Windows-Dienst selbst läuft |
| `app_running` | Die überwachte Anwendung läuft |
| `uptime` | Laufzeit der Anwendung seit dem letzten Start |
| `restarts_1h` / `restarts_24h` | Neustarts im Zeitfenster — die Flapping-Erkennung |
| `restarts_total` | Neustarts seit dem Start des Dienstes |
| `cpu` | CPU-Last des **gesamten Prozessbaums** (100 % = alle Kerne ausgelastet) |
| `mem` | Arbeitsspeicher des gesamten Prozessbaums |
| `procs` | Anzahl Prozesse im Baum |
| `cpu_seconds` | Verbrauchte CPU-Zeit, als Zähler für Raten |

Gemessen wird über das Job-Objekt, in dem die Anwendung ohnehin für das saubere Beenden
läuft. Ein Startskript, das `java.exe` nachlädt, wird dadurch mitgezählt — bei einer
Messung nur auf den Hauptprozess stünde dort eine glatte Null.

Die Werte werden alle 5 Sekunden aktualisiert und liegen als JSON unter
`%ProgramData%\EasyService\state\<Dienst>.json`. Bleibt diese Datei älter als zwei
Minuten stehen, meldet der Check **UNKNOWN** statt weiterhin die alten Zahlen — eine
tote Messung ist schlimmer als gar keine.

## Langzeitverlauf

Neben dem Momentanzustand schreibt der überwachende Prozess einen Verlauf: eine Zeile
pro Minute als CSV unter `%ProgramData%\EasyService\history\`.

| Datei | Inhalt |
|---|---|
| `<Dienst>-metrics.csv` | `utc,cpu_avg,cpu_max,mem_avg,mem_max,procs,restarts_total` |
| `<Dienst>-events.csv` | `utc,event_id,exit_code,detail` |

Zeitstempel sind UTC im Format `yyyy-MM-ddTHH:mm:ssZ`, Zahlen invariant formatiert —
beides direkt maschinenlesbar. Eine Zeile ist rund 56 Byte, macht etwa 80 KB je Dienst
und Tag oder 2,3 MB für die voreingestellten 30 Tage. Die Aufbewahrung steht pro Dienst
im Editor auf der Registerkarte *Überwachung* (0 schaltet ab); ältere Zeilen werden
täglich entfernt.

Wer die Werte lieber im eigenen Zeitreihensystem hätte, kann `metrics.csv` direkt
einlesen — oder gleich den Prometheus-Weg unten nehmen und die Historie dort führen.
In der Oberfläche zeigt ein Doppelklick auf den Dienst dieselben Daten als Diagramm.

## Bewertung

Aus den Rohwerten wird ein Status nach Nagios-Konvention (0 OK, 1 WARN, 2 CRIT, 3 UNKNOWN):

| Situation | Status |
|---|---|
| Dienst beendet, Starttyp *Automatisch* | **CRIT** |
| Dienst beendet, Starttyp *Manuell* oder *Deaktiviert* | OK |
| Anwendung startet ständig neu und wird gedrosselt | **CRIT** |
| Anwendung ließ sich nicht starten | **CRIT** |
| Anwendung beendet, kein Neustart konfiguriert | **WARN** |
| Statusmeldung älter als 2 Minuten | **UNKNOWN** |
| Schwellwerte für Neustarts/CPU/RAM überschritten | **WARN** / **CRIT** |

Die Schwellwerte stehen pro Dienst im Editor auf der Registerkarte **Überwachung**.
Voreingestellt sind 3 Neustarts pro Stunde als Warnung und 10 als kritisch; CPU und
Speicher sind ab Werk unbegrenzt (0 = nicht prüfen).

---

## Checkmk

### Zustände als Local Check

Ein Local Check reicht für alle überwachten Dienste — EasyService gibt eine Zeile je
Dienst aus, inklusive Perfdaten für die Graphen.

Datei `C:\ProgramData\checkmk\agent\local\easyservice.bat`:

```bat
@"C:\Program Files\EasyService\easyservice.exe" checkmk
```

Ergebnis (eine Zeile je Dienst):

```
0 EasyService_MeinDaemon uptime=86400s;;;0;|restarts_1h=0;3;10;0|cpu=2.5%;80;95;0;100|mem=140509184B;;;0|procs=2 Running for 1d 0h, PID 1234, CPU 2.5 %, RAM 134 MB, 0 restarts/h
2 EasyService_Importer uptime=3s;;;0;|restarts_1h=37;3;10;0|cpu=0%;80;95;0;100|mem=8192000B;;;0|procs=1 37 restarts in the last hour (critical from 10) - Running for 3s, PID 9876
```

Der Meldungstext folgt der eingestellten Sprache (siehe unten); Statuscode, Item-Name und
Perfdaten sind sprachunabhängig.

Danach `cmk -II <host>` und `cmk -O` auf dem Checkmk-Server, und jeder Dienst erscheint
als eigener Service mit Graphen für CPU, Speicher und Neustarts.

### Protokolldateien einbinden

Wichtig: Die `logfiles`-Sektion des Windows-Agenten ist für Textdateien **nicht**
zuständig — in `check_mk.user.yml` steht ausdrücklich „We do not support logfiles
monitoring in agent at the moment. Please, use plugin mk_logwatch". Der richtige Weg
ist das Plugin.

1. `mk_logwatch.py` vom Checkmk-Server (`~/share/check_mk/agents/plugins/`) nach
   `C:\ProgramData\checkmk\agent\plugins\` kopieren.
2. Konfiguration unter `C:\ProgramData\checkmk\agent\config\logwatch.cfg` anlegen:

```
C:\ProgramData\EasyService\logs\*-stderr.log
 C Exception
 C FATAL
 C ERROR
 W WARN
 I .*
```

Für das Diagnoseprotokoll von EasyService selbst (`*-easyservice.log`) gilt eine
Einschränkung: dessen Meldungstexte folgen der eingestellten Sprache, Textmuster wären
also nicht portabel. **Nutze dafür lieber die Ereignis-IDs** (siehe unten) — die sind
sprachunabhängig und genau dafür da. Wer trotzdem die Datei überwachen will, sollte die
Sprache maschinenweit auf Englisch festnageln:

```cmd
reg add HKLM\SOFTWARE\EasyService /v Language /t REG_SZ /d en /f
```

und dann auf den englischen Text matchen:

```
C:\ProgramData\EasyService\logs\*-easyservice.log
 C could not be started
 C terminating the process
 W throttle window
 I .*
```

Pfade und Plugin-Verzeichnis unterscheiden sich je nach Agent-Version; im Zweifel die
[Checkmk-Dokumentation zur Logdatei-Überwachung](https://docs.checkmk.com/latest/en/monitoring_logfiles.html)
gegenprüfen.

### Windows-Ereignisse

Der Windows-Agent liest das Anwendungsprotokoll ohnehin. EasyService schreibt dort mit
stabilen Ereignis-IDs (siehe unten), auf die sich ohne Textmustersuche alarmieren lässt.

---

## Prometheus

```cmd
"C:\Program Files\EasyService\easyservice.exe" prometheus
```

Für den Textfile-Collector des `node_exporter` — die Datei wird atomar ersetzt, der
Collector sieht also nie einen halben Stand:

```cmd
"C:\Program Files\EasyService\easyservice.exe" prometheus --output C:\ProgramData\node_exporter\textfile\easyservice.prom
```

Als Aufgabe, die minütlich läuft:

```cmd
schtasks /create /tn "EasyService Metrics" /sc minute /mo 1 /ru SYSTEM ^
  /tr "\"C:\Program Files\EasyService\easyservice.exe\" prometheus --output C:\ProgramData\node_exporter\textfile\easyservice.prom"
```

Ausgabe:

```
# HELP easyservice_restarts_1h Restarts in the last hour
# TYPE easyservice_restarts_1h gauge
easyservice_restarts_1h{service="MeinDaemon"} 0
easyservice_restarts_1h{service="Importer"} 37
```

Beispiel-Alarmregel:

```yaml
groups:
  - name: easyservice
    rules:
      - alert: EasyServiceFlapping
        expr: easyservice_restarts_1h > 10
        for: 5m
        annotations:
          summary: "{{ $labels.service }} startet ständig neu"

      - alert: EasyServiceApplicationDown
        expr: easyservice_service_running == 1 and easyservice_application_running == 0
        for: 2m
        annotations:
          summary: "{{ $labels.service }}: Dienst läuft, Anwendung nicht"

      - alert: EasyServiceStale
        expr: easyservice_state_age_seconds > 120
        for: 5m
        annotations:
          summary: "{{ $labels.service }} liefert keine Messwerte mehr"
```

Die mittlere Regel ist die, die `sc query` nicht kann.

---

## Zabbix

In `zabbix_agentd.conf`:

```
UserParameter=easyservice.discovery,"C:\Program Files\EasyService\easyservice.exe" zabbix-discovery
UserParameter=easyservice.check[*],"C:\Program Files\EasyService\easyservice.exe" check "$1"
UserParameter=easyservice.json,"C:\Program Files\EasyService\easyservice.exe" json
```

`easyservice.discovery` liefert die Low-Level-Discovery-Liste:

```json
{ "data": [ { "{#SERVICE}": "MeinDaemon", "{#DISPLAYNAME}": "Mein Daemon" } ] }
```

Damit legt ein Template automatisch Items für jeden überwachten Dienst an. Für die
Einzelwerte eignet sich `easyservice.json` mit einem Dependent Item und JSONPath-Vorverarbeitung,
etwa `$[?(@.service=='MeinDaemon')].restartsLastHour`.

---

## Nagios / Icinga

```cmd
"C:\Program Files\EasyService\easyservice.exe" check "MeinDaemon"
```

```
EASYSERVICE CRITICAL - 37 restarts in the last hour (critical from 10) - Running for 3s, PID 9876 | uptime=3s;;;0 restarts_1h=37;3;10;0 cpu=0%;80;95;0;100
```

Exit-Code: `0` OK, `1` Warnung, `2` kritisch, `3` unbekannt — die übliche Plugin-Konvention,
also direkt über NSClient++/NRPE verwendbar.

---

## Windows-Ereignisprotokoll

EasyService schreibt in das **Anwendungsprotokoll** unter der Quelle `EasyService`.
Die Ereignis-IDs sind Teil des öffentlichen Vertrags und werden nie umnummeriert —
alarmiere auf die ID, nicht auf den Meldungstext.

| ID | Bedeutung | Typ |
|---|---|---|
| 1000 | Überwachung gestartet | Information |
| 1001 | Anwendung gestartet | Information |
| 1002 | Anwendung beendet | Information / Warnung |
| 1003 | Anwendung wird neu gestartet | Information |
| **1004** | **Neustart gedrosselt** — die Anwendung stirbt schneller als sie startet | **Warnung** |
| **1005** | **Start fehlgeschlagen** | **Fehler** |
| 1006 | Dienst wird beendet | Information |
| 1007 | Durch Beenden-Aktion gestoppt | Information |
| **1008** | **Anwendung hart beendet** — sie hat auf keine Aufforderung reagiert | **Warnung** |
| 1009 | Konfigurationsproblem | Fehler |
| 1010 | Protokollierungsproblem | Warnung |

Die fett markierten sind die, auf die sich ein Alarm lohnt.

Abfrage per PowerShell, etwa für einen eigenen Check:

```powershell
Get-WinEvent -FilterHashtable @{ LogName='Application'; ProviderName='EasyService'; Id=1004,1005,1008 } -MaxEvents 20
```

---

## JSON für alles andere

```cmd
"C:\Program Files\EasyService\easyservice.exe" json
```

```json
[
  {
    "service": "MeinDaemon",
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

Alle Zahlen nutzen den Punkt als Dezimaltrenner, unabhängig von der Systemsprache —
ein deutsches „2,5" würde jeden Parser zerlegen. Das ist durch einen Test abgesichert.

Für einen einzelnen Dienst: `easyservice status <Name> --json`.

---

## Sprache der Ausgabe

EasyService spricht Englisch, Deutsch, Französisch, Spanisch und Italienisch. Welche
Sprache eine Ausgabe verwendet, ergibt sich in dieser Reihenfolge:

1. `HKCU\Software\EasyService\Language` — die Wahl im Menü **Sprache** der Oberfläche
2. `HKLM\SOFTWARE\EasyService\Language` — maschinenweite Vorgabe
3. die Sprache, in der Windows läuft

Gültige Werte sind `en`, `de`, `fr`, `es`, `it` und ein leerer Wert für „wie Windows".

Für das Monitoring ist Punkt 2 der wichtige: die Checks laufen unter dem Konto des Agenten,
nicht unter dem des Administrators, der sie eingerichtet hat. Wer englische Ausgaben auf
einem deutschen Server will, setzt

```cmd
reg add HKLM\SOFTWARE\EasyService /v Language /t REG_SZ /d en /f
```

**Sprachunabhängig sind in jedem Fall:** Statuscodes (0/1/2/3), Item-Namen, Metriknamen,
Perfdaten, JSON-Feldnamen und die Ereignis-IDs. Nur die Meldungstexte übersetzen sich.
Alarme sollten deshalb auf Codes und IDs aufsetzen, nicht auf Text.

---

## Fehlersuche

**Der Check meldet UNKNOWN und „meldet aber noch keinen Zustand".**
Der Dienst läuft noch mit einer EasyService-Version vor der Monitoring-Unterstützung.
Einmal neu starten genügt.

**Der Check meldet UNKNOWN und „Die letzte Statusmeldung ist … alt".**
Der überwachende Prozess hängt oder wurde hart abgeschossen. Die Zustandsdatei unter
`%ProgramData%\EasyService\state\` bleibt in dem Fall liegen; die Werte darin sind
absichtlich nicht mehr gültig.

**CPU steht dauerhaft auf 0.**
Die CPU-Last ist eine Differenz zwischen zwei Messungen; der erste Wert kommt daher
frühestens rund sieben Sekunden nach dem Start der Anwendung.

**Speicher wird zu niedrig gemeldet.**
Konnte der Prozess keinem Job-Objekt zugeordnet werden, misst EasyService nur den
Hauptprozess. Das steht dann als Hinweis im Diagnoseprotokoll des Dienstes.
