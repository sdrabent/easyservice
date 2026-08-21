<div align="center">

# EasyService

**Jedes Programm zum Windows-Dienst machen — und endlich wissen, wie es ihm geht.**

Eine Open-Source-Alternative zu NSSM, gebaut für Administratoren, die geradestehen
müssen für das, was auf ihren Servern läuft.

**English:** [README.md](README.md)

[![build](https://github.com/sdrabent/easyservice/actions/workflows/build.yml/badge.svg)](https://github.com/sdrabent/easyservice/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Downloads](https://img.shields.io/github/downloads/sdrabent/easyservice/total)](../../releases)
[![Sprachen](https://img.shields.io/badge/UI-en%20%7C%20de%20%7C%20fr%20%7C%20es%20%7C%20it-informational)](#sprachen)

</div>

---

## Die Lüge, die Ihr Monitoring Ihnen erzählt

```
C:\> sc query MeinImporter

        SERVICE_NAME: MeinImporter
        STATE       : 4  RUNNING
```

Sieht gut aus. Ist es nicht.

Dieser Dienst ist ein Wrapper — NSSM, srvany, ein umgebogener Aufgabenplaner-Eintrag,
egal. Was Windows als `RUNNING` meldet, ist der *Wrapper*. Dahinter ist der eigentliche
Importer in der letzten Stunde **37-mal abgestürzt und neu gestartet worden**, und jeder
Check, den Sie auf `sc query`, `services.msc` oder einen Windows-Dienst-Check gebaut
haben, meldet **OK**.

Merken tut es zuerst der Kunde.

## Was EasyService dagegen tut

EasyService ist der Wrapper *und* der Zeuge. Es zählt die Neustarts, misst den
Prozessbaum und reicht das Urteil an das Monitoring weiter, das Sie ohnehin betreiben.

**Eine Zeile für den Checkmk-Agenten** — `C:\ProgramData\checkmk\agent\local\easyservice.bat`:

```bat
@"C:\Program Files\EasyService\easyservice.exe" checkmk
```

und jeder überwachte Dienst wird zu einem Checkmk-Service mit Graphen und Schwellwerten:

```
0 EasyService_MeinDaemon   uptime=86400s|restarts_1h=0;3;10;0|cpu=2.5%;80;95;0;100|mem=140509184B|procs=2   Läuft seit 1d 0h, PID 1234, CPU 2.5 %, RAM 134 MB, 0 Neustarts/h
2 EasyService_MeinImporter uptime=3s|restarts_1h=37;3;10;0|cpu=0%;80;95;0;100|mem=8192000B|procs=1          37 Neustarts in der letzten Stunde (kritisch ab 10) - Läuft seit 3s, PID 9876
```

Da ist es. **Kritisch**, mit Begründung, mit Perfdaten, ohne dass Sie ein einziges
Check-Skript schreiben.

Kein Checkmk im Haus? Dieselben Daten, Ihr Format:

| Befehl | Für |
|---|---|
| `easyservice checkmk` | Checkmk Local Check, eine Zeile je Dienst, mit Perfdaten |
| `easyservice prometheus --output …` | Prometheus-Textfile-Collector (atomar ersetzt) |
| `easyservice check <Name>` | Nagios/Icinga-Plugin, Exit-Code 0/1/2/3 |
| `easyservice zabbix-discovery` | Zabbix Low-Level-Discovery |
| `easyservice json` | Alles, für das, was Sie sich selbst gebaut haben |

Und weil Meldungstext eine denkbar schlechte Grundlage für Alarme ist, landet jedes
Ereignis zusätzlich mit einer **stabilen Ereignis-ID** im Windows-Anwendungsprotokoll:

| ID | Bedeutung |
|---|---|
| **1004** | Neustart gedrosselt — die Anwendung stirbt schneller, als sie startet |
| **1005** | Start fehlgeschlagen |
| **1008** | Anwendung hart beendet |

```powershell
Get-WinEvent -FilterHashtable @{ LogName='Application'; ProviderName='EasyService'; Id=1004,1005,1008 }
```

Diese IDs ändern sich nie und werden nie übersetzt. → **[docs/monitoring.md](docs/monitoring.de.md)**

## Logging, das Sie sich nicht selbst bauen müssen

Konsolenprogramme schreiben auf stdout und stderr. Windows-Dienste haben keine Konsole.
An dieser Lücke sterben die meisten selbstgebauten Wrapper-Skripte.

EasyService fängt beide Ströme über echte Pipes ab und schreibt sie in Dateien, mit:

- **Rotation** nach Größe und/oder Zeit, mit begrenzter Archivanzahl — kein 4-GB-Protokoll
  mehr, das über Weihnachten die Systemplatte auffrisst
- **Optionalen Zeitstempeln** pro Zeile, für Anwendungen, die sich das sparen
- **Zusammenführen oder Trennen** von stdout und stderr, wie Sie wollen
- Einem **Diagnoseprotokoll je Dienst**, das festhält, was der Supervisor selbst getan
  hat: gestartet, mit Code 3 beendet, gedrosselt, hart beendet

...und einem **eingebauten Live-Viewer**, damit Sie sich nicht per RDP anmelden und
Notepad öffnen müssen:

- hängt sich an die Datei, während der Dienst weiterschreibt (`FileShare.ReadWrite`)
- folgt der Rotation von allein
- bietet die archivierten Dateien in einer Auswahlliste an
- filtert nach Text
- eine zweite Registerkarte zeigt die Windows-Ereignisse dieses Dienstes

`mk_logwatch` auf `%ProgramData%\EasyService\logs\*-stderr.log` zeigen lassen, und die
Fehlerausgabe Ihrer Anwendung wird zum überwachten Protokoll — die Konfiguration steht im
[Monitoring-Leitfaden](docs/monitoring.de.md).

## Verlauf: Was hat das Ding heute Nacht getrieben?

![EasyService-Übersicht](assets/screenshot-overview.png)

*Drei überwachte Dienste. Einer gesund, einer im Neustart-Dauerlauf und rot markiert,
einer bewusst gestoppt. Windows würde die ersten beiden gleich melden.*

Doppelklick auf einen Dienst zeigt seine Vergangenheit, nicht nur seine Gegenwart:

- **CPU und Speicher über die Zeit** — getrennte Diagramme, weil es getrennte Skalen sind.
  Die Linie ist der Minutenmittelwert, die Fläche die Spitze, damit ein Dienst, der bei
  2 % dümpelt und jede Minute auf 90 % springt, sich nicht hinter einem flachen
  Mittelwert versteckt.
- **Neustart-Markierungen** auf der Zeitachse
- **Kennzahlen** für den Zeitraum: Neustarts, CPU im Mittel und in der Spitze, Speicher
  im Mittel und in der Spitze
- **Ereignisliste** mit Exit-Codes
- 1 Stunde bis 30 Tage, als CSV exportierbar

![Verlauf eines Dienstes](assets/screenshot-history.png)

*Die Aufnahmen zeigen die englische Oberfläche; zwei Bildersätze zu pflegen lohnt nicht.*

Abgelegt als schlichtes **CSV** unter `%ProgramData%\EasyService\history\` — rund 80 KB
je Dienst und Tag, also etwa 2,3 MB für die voreingestellten 30 Tage (einstellbar, 0
schaltet die Aufzeichnung ab). In fünf Jahren können Sie das immer noch in Excel öffnen,
auch ohne installiertes EasyService.

## 60 Sekunden bis zum ersten Dienst

![Schnelleinrichtung](assets/screenshot-quicksetup.png)

1. `easyservice.exe` starten, UAC bestätigen.
2. **Dienst hinzufügen…** — oder die `.exe` einfach ins Fenster ziehen.
3. Programm auswählen. Dienstname, Startverzeichnis, Protokollpfade, Rotation,
   Neustart-Richtlinie und Überwachungsschwellen werden vorbelegt und *angezeigt*.
4. **Dienst anlegen.** Fertig.

Vier Felder statt neun Registerkarten. Das Dienstkonto wird für den nächsten Dienst
gemerkt; das Kennwort bleibt nur für die Sitzung im Speicher, sofern Sie das Speichern
nicht ausdrücklich verlangen (dann per DPAPI, an Ihr Windows-Konto gebunden, nicht
maschinenweit lesbar).

Schlägt der erste Start fehl, bietet EasyService sofort das Protokoll an — es ist fast
immer ein falscher Pfad oder ein falsches Argument, und Sie sehen in vier Sekunden,
welches.

Mehr Kontrolle nötig? **Erweiterte Einstellungen…** öffnet den Editor mit neun
Registerkarten.

Lieber skriptgesteuert ausrollen?

```cmd
easyservice install MeinDaemon "C:\apps\daemon.exe" --config C:\apps\daemon.yml
easyservice status MeinDaemon   :: Exit-Code 0 = läuft, 3 = läuft nicht
```

## Im Vergleich

| | `sc.exe` | NSSM | **EasyService** |
|---|---|---|---|
| Beliebige Programme als Dienst | ✗ | ✓ | ✓ |
| stdout/stderr in Dateien | ✗ | ✓ | ✓ |
| Automatische Log-Rotation | ✗ | ✓ | ✓ |
| Neustart-Richtlinie pro Exit-Code | ✗ | ✓ | ✓ |
| Gestufter, sauberer Shutdown | ✗ | ✓ | ✓ |
| Prozessbaum sicher beenden | ✗ | ✓ | ✓ |
| Einzelne .exe, keine Installation | ✓ | ✓ | ✓ |
| Grafische Oberfläche | ✗ | teilweise | **✓ vollständig** |
| **Monitoring-Anbindung** | ✗ | ✗ | **✓ Checkmk, Prometheus, Zabbix, Nagios** |
| **Flapping-Erkennung** | ✗ | ✗ | **✓** |
| **Stabile Ereignis-IDs für Alarme** | ✗ | ✗ | **✓** |
| **Eingebaute Live-Protokollansicht** | ✗ | ✗ | **✓** |
| **Verlauf: CPU, Speicher, Neustarts** | ✗ | ✗ | **✓** |
| **Schnelleinrichtung mit Vorbelegung** | ✗ | ✗ | **✓** |
| **Fünf Sprachen** | ✗ | nur Englisch | **✓ en, de, fr, es, it** |
| Dark Mode | ✗ | ✗ | ✓ |
| Open Source | ✗ | ✓ (Public Domain) | ✓ (MIT) |

NSSM ist ein gutes Werkzeug, und EasyService verdankt ihm die Idee. Was es nicht tut:
Ihrem Monitoring irgendetwas erzählen.

## Download

**[→ Releases](../../releases)**

| Datei | Beschreibung |
|---|---|
| `easyservice.exe` | Alles enthalten, läuft sofort. Keine Installation. |
| `easyservice-framework-dependent.exe` | Nur ~300 KB, benötigt das [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0). |

> **Wichtig:** Der Pfad zur `easyservice.exe` wird in jedem angelegten Dienst hinterlegt.
> Wird die Datei später verschoben, starten diese Dienste nicht mehr. Gleich einen festen
> Ort wählen, etwa `C:\Program Files\EasyService\` — ein Verzeichnis, in das normale
> Benutzer nicht schreiben können, was auch deshalb zählt, weil der Dienst als SYSTEM läuft.

Voraussetzungen: Windows 10 / Server 2016 oder neuer, x64, Administratorrechte (Dienste
zu verwalten geht unter Windows grundsätzlich nur erhöht — EasyService fordert die Rechte
beim Start per UAC an).

Die Binärdateien sind **noch nicht signiert**, SmartScreen warnt daher beim ersten
Download. Zu jedem Release werden Prüfsummen veröffentlicht.

---

## Alles Weitere

### Der Editor, Registerkarte für Registerkarte

**Anwendung** — Programm, Startverzeichnis und Argumente.

**Details** — Anzeigename, Beschreibung, Starttyp (automatisch / verzögert / manuell /
deaktiviert), Prozesspriorität, Prozessor-Affinität und eine optionale Startverzögerung.

**Anmelden** — lokales Systemkonto, lokaler Dienst, Netzwerkdienst oder ein Benutzerkonto.
Bei einem Benutzerkonto vergibt EasyService automatisch das Recht *Als Dienst anmelden*
(`SeServiceLogonRight`), das sonst per `secpol.msc` von Hand gesetzt werden müsste.

**Abhängigkeiten** — andere Dienste, die vorher laufen müssen, per Auswahlliste.

**Umgebung** — zusätzliche Umgebungsvariablen (`NAME=WERT`), wahlweise ergänzend oder als
vollständiger Ersatz der Systemumgebung.

**Protokollierung** — die oben beschriebenen Datei-, Rotations- und Zeitstempeleinstellungen.

**Beenden-Aktionen** — neu starten, ignorieren oder den Dienst beenden, wahlweise pro
Exit-Code. Ein Throttle-Fenster verhindert Neustartschleifen: Beendet sich die Anwendung
schneller als eingestellt, verdoppelt EasyService die Wartezeit bis maximal 60 Sekunden.

**Überwachung** — Schwellwerte für Neustarts pro Stunde, CPU und Speicher, dazu fertige
Copy-paste-Schnipsel für die Agenten von Checkmk, Prometheus, Nagios und Zabbix.

**Herunterfahren** — der gestufte Shutdown. Jede Stufe einzeln abschaltbar, jede mit
eigenem Zeitlimit:

1. `Strg+C` an die Konsole der Anwendung (für Konsolenprogramme)
2. `WM_CLOSE` an alle Fenster der Anwendung
3. `WM_QUIT` an alle Threads der Anwendung
4. Harter Abbruch — wahlweise samt aller Kindprozesse

### Sicherheitsnetz beim Löschen

Dienste, die *nicht* mit EasyService angelegt wurden, lassen sich nicht bearbeiten und
nur nach einer zusätzlichen Bestätigung entfernen, bei der der Dienstname abgetippt
werden muss. Ein Fehlklick kann keinen Systemdienst zerstören.

### Sprachen

Englisch, Deutsch, Französisch, Spanisch und Italienisch. Ohne Zutun folgt EasyService
der Sprache, in der Windows läuft; über das Menü **Sprache** lässt sie sich festlegen.
Für Server, auf denen das Monitoring unter einem anderen Konto läuft als der
einrichtende Administrator:

```cmd
reg add HKLM\SOFTWARE\EasyService /v Language /t REG_SZ /d en /f
```

Statuscodes, Metriknamen, Perfdaten, JSON-Felder und Ereignis-IDs werden nie übersetzt —
nur die Meldungstexte, sodass Alarme über Sprachgrenzen hinweg funktionieren. Die
Übersetzungen liegen als `.resx` unter `src/EasyService/Resources/`.

> Die französische, spanische und italienische Übersetzung ist noch nicht von
> Muttersprachlern gegengelesen. Korrekturen sind sehr willkommen.

### Kommandozeile

```
easyservice list [--json]
easyservice install <Name> <Programm> [Argumente...]
easyservice remove <Name>
easyservice start|stop|restart|status <Name>
easyservice checkmk | prometheus [--output <Datei>] | check <Name> | json | zabbix-discovery
easyservice gui [Name] | gui --new
```

### Wie es funktioniert

`easyservice.exe` ist bewusst eine einzige Datei mit zwei Betriebsarten — genau wie
`nssm.exe`:

```
Doppelklick                      Dienst-Manager (SCM)
      │                                    │
      ▼                                    ▼
easyservice.exe            easyservice.exe run "MeinDienst"
      │                                    │
   GUI-Modus                        Supervisor-Modus
   (MainForm)                              │
                              ┌────────────┴────────────┐
                              │                         │
                      Konfiguration aus         CreateProcess mit
                      der Registry lesen        umgeleiteten Pipes
                                                         │
                                          ┌──────────────┼──────────────┐
                                          ▼              ▼              ▼
                                       stdout         stderr      Job-Objekt
                                          │              │      (Prozessbaum)
                                          ▼              ▼
                                    rotierende Protokolldateien
```

Beim Anlegen eines Dienstes trägt EasyService sich selbst als Dienstprogramm ein
(`"C:\Program Files\EasyService\easyservice.exe" run "MeinDienst"`) und legt die
Konfiguration unter `HKLM\SYSTEM\CurrentControlSet\Services\<Name>\Parameters` ab.
Startet Windows den Dienst, läuft dieselbe `.exe` im Supervisor-Modus, liest die
Konfiguration, startet die eigentliche Anwendung und beaufsichtigt sie.

Die Anwendung landet in einem Windows-Job-Objekt. Das macht es möglich, beim Beenden
zuverlässig auch alle Kindprozesse mitzunehmen — der klassische Fall, in dem ein per
`sc.exe` angelegter Dienst verwaiste Prozesse hinterlässt. Dasselbe Job-Objekt ist die
Abrechnungsgrenze für die CPU- und Speichermessung, sodass eine Batchdatei, die
`java.exe` nachlädt, korrekt gezählt wird statt null zu melden.

Zusätzlich werden die Windows-eigenen Wiederherstellungsaktionen gesetzt, als zweites
Sicherheitsnetz für den Fall, dass der Supervisor-Prozess selbst ausfällt.

### Registry-Referenz

Alle Werte liegen unter `HKLM\SYSTEM\CurrentControlSet\Services\<Name>\Parameters`, sind
mit `regedit` einsehbar und per Skript setzbar:

| Wert | Typ | Bedeutung |
|---|---|---|
| `Application` | EXPAND_SZ | Pfad zum Programm |
| `AppDirectory` | EXPAND_SZ | Startverzeichnis |
| `AppParameters` | EXPAND_SZ | Argumente |
| `AppPriority` | DWORD | 0 = Echtzeit … 5 = Niedrig |
| `AppAffinity` | QWORD | Prozessormaske, 0 = alle |
| `AppStartupDelay` | DWORD | Verzögerung vor dem ersten Start (ms) |
| `AppEnvironmentExtra` | MULTI_SZ | Zusätzliche Variablen `NAME=WERT` |
| `AppEnvironmentReplace` | DWORD | 1 = Systemumgebung ersetzen |
| `AppStdout` / `AppStderr` | EXPAND_SZ | Protokolldateien |
| `AppAppendOutput` | DWORD | 1 = anhängen, 0 = beim Start leeren |
| `AppTimestampLog` | DWORD | 1 = Zeitstempel je Zeile |
| `AppRotateFiles` | DWORD | 1 = Rotation aktiv |
| `AppRotateBytes` | QWORD | Rotationsgröße in Bytes |
| `AppRotateSeconds` | DWORD | Rotationsintervall, 0 = nur nach Größe |
| `AppRotateKeep` | DWORD | Anzahl Archive, 0 = unbegrenzt |
| `AppExitDefault` | DWORD | 0 = Neustart, 1 = Ignorieren, 2 = Dienst beenden |
| `AppExit\<Code>` | DWORD | Aktion für einen bestimmten Exit-Code |
| `AppRestartDelay` | DWORD | Wartezeit vor dem Neustart (ms) |
| `AppThrottle` | DWORD | Throttle-Fenster (ms) |
| `AppStopUseConsole` / `…Window` / `…Threads` | DWORD | Shutdown-Stufen aktiv |
| `AppStopConsoleDelay` / `…WindowDelay` / `…ThreadsDelay` | DWORD | Zeitlimit je Stufe (ms) |
| `AppStopUseTerminate` | DWORD | 1 = notfalls hart beenden |
| `AppKillProcessTree` | DWORD | 1 = Kindprozesse mitbeenden |
| `AppLogServiceEvents` | DWORD | 1 = Diagnoseprotokoll schreiben |
| `MonEnabled` | DWORD | 1 = an das Monitoring melden |
| `MonWarnCpu` / `MonCritCpu` | DWORD | CPU-Schwellen in %, 0 = nicht prüfen |
| `MonWarnMemoryMb` / `MonCritMemoryMb` | DWORD | Speicherschwellen in MB |
| `MonWarnRestartsPerHour` / `MonCritRestartsPerHour` | DWORD | Neustart-Schwellen |
| `HistoryDays` | DWORD | Aufbewahrung des Verlaufs in Tagen, 0 = aus |

Protokolle liegen standardmäßig unter `%ProgramData%\EasyService\logs\`, der Verlauf
unter `%ProgramData%\EasyService\history\`.

### Aus dem Quellcode bauen

Benötigt wird das [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (oder
neuer) auf einem Windows-Rechner.

```cmd
git clone https://github.com/sdrabent/easyservice.git
cd easyservice
dotnet build EasyService.sln -c Release
```

Einzeldatei:

```cmd
dotnet publish src/EasyService/EasyService.csproj -c Release -r win-x64 ^
  --self-contained true -p:PublishSingleFile=true ^
  -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o publish
```

Ohne NuGet-Abhängigkeiten; alle Windows-Aufrufe laufen direkt über P/Invoke (`advapi32`,
`kernel32`, `user32`, `crypt32`). Nach dem Bearbeiten einer `.resx` die typsicheren
Zugriffe mit `python tools/generate-strings.py` neu erzeugen — die CI prüft, dass beides
nicht auseinanderläuft.

### Tests

```cmd
dotnet run --project tests/EasyService.Tests -c Release
```

Die Tests steuern den Supervisor direkt an und brauchen weder Administratorrechte noch
einen installierten Dienst. Geprüft werden Ausgabeumleitung, Neustart-Richtlinie,
Exit-Code-Aktionen, Rotation samt Archivbegrenzung, das Beenden laufender Anwendungen,
Zeitstempel, Umgebungsvariablen, das Lesen der Dienstliste, der Aufbau aller Dialoge, die
komplette Monitoring-Kette bis zum Ausgabeformat für Checkmk und Prometheus, die
Verlaufsspeicherung samt Aufbewahrungsgrenze sowie dass jeder Text in allen fünf Sprachen
existiert und seine Platzhalter behält — eine verlorene `{0}` würde sonst erst zur
Laufzeit als `FormatException` auffallen.

### Fehlersuche

**Der Dienst startet nicht und beendet sich sofort.**
Erste Anlaufstelle ist das EasyService-Protokoll des Dienstes (`…-easyservice.log`, über
**Protokolle…** → Auswahlliste). Dort steht, ob das Programm gefunden wurde, mit welchem
Code es sich beendet hat und welche Aktion gegriffen hat. Dieselben Meldungen landen im
Windows-Anwendungsprotokoll unter der Quelle `EasyService`.

**Der Dienst läuft, aber die Anwendung tut nichts.**
Meist stimmt das Startverzeichnis nicht. Viele Programme suchen Konfigurationsdateien
relativ zum aktuellen Verzeichnis; ohne Angabe verwendet EasyService den Ordner des
Programms.

**Nach dem Beenden bleiben Prozesse übrig.**
*Auch alle Kindprozesse beenden* auf der Registerkarte *Herunterfahren* aktivieren.

**Die Anwendung wird dauernd neu gestartet.**
Beendet sie sich absichtlich mit Code 0, dann unter *Beenden-Aktionen* eine Regel
`Exit-Code 0 → Dienst beenden` anlegen.

**Ein Dienst lässt sich nicht anlegen: „ist zum Löschen vorgemerkt".**
Windows hält den Dienstschlüssel, solange irgendwo ein Handle offen ist — typisch bei
geöffnetem `services.msc`. Das Fenster schließen oder neu starten.

**Der Monitoring-Check meldet UNBEKANNT.**
Entweder läuft der Dienst noch mit einer älteren EasyService-Version (einmal neu
starten), oder seine Statusmeldung ist veraltet, was bedeutet, dass der überwachende
Prozess nicht mehr reagiert. Eine tote Messung wird absichtlich als unbekannt gemeldet
statt als gesund.

## Mitmachen

Fehlerberichte und Pull Requests sind willkommen. Für größere Änderungen bitte vorher ein
Issue anlegen, damit die Richtung geklärt ist. `dotnet build` muss warnungsfrei
durchlaufen und die Tests müssen grün sein. Übersetzungskorrekturen sind besonders
willkommen — die `.resx`-Dateien unter `src/EasyService/Resources/` sind die Quelle.

## Lizenz

MIT — siehe [LICENSE](LICENSE).
