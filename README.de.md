# EasyService

**Der Dienst-Wrapper für Administratoren, die den Pieper tragen.** Führt beliebige
Programme als Windows-Dienst aus, prüft, ob das Programm tatsächlich noch arbeitet, und
gibt die Antwort an Checkmk, Prometheus, Zabbix oder Nagios weiter, bevor jemand fragen
muss.

So sieht es in dieser Ecke aus: Die letzte stabile NSSM-Fassung stammt vom August 2014,
der neueste Build überhaupt ist eine Vorabversion von 2017. WinSW hat gar keine Oberfläche.
Die Werkzeuge, die den ganzen Weg gehen, werden pro Rechner lizenziert. EasyService macht
es in einer Programmdatei, umsonst, ohne Runtime.

Das Grundprinzip ist dasselbe wie bei NSSM: Ein Supervisor-Prozess sitzt zwischen dem
Dienst-Manager und der Anwendung. Der Unterschied ist, was dieser Supervisor weiß. Er zählt
Neustarts, misst CPU und Speicher des ganzen Prozessbaums, merkt sich Exit-Codes und fragt
die Anwendung jede halbe Minute, ob sie noch antwortet — denn ein verklemmter Prozess sieht
für Windows genauso aus wie ein gesunder, und das ist der Ausfall, für den niemand geweckt
wird.

[![build](https://github.com/sdrabent/easyservice/actions/workflows/build.yml/badge.svg)](https://github.com/sdrabent/easyservice/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**English:** [README.md](README.md)

## Stand

Das Projekt ist jung. Konkret heißt das:

* Supervisor, Monitoring-Ausgabe und Konfigurationsformat sind automatisiert getestet.
  Dazu läuft bei jedem Commit ein Test von Anfang bis Ende auf einem frischen
  Windows-Rechner in der CI: er legt einen echten Dienst an, startet ihn, prüft
  Protokolldatei und Ereignisprotokoll, beendet den Kindprozess hart, sieht ihn
  wiederkommen, stoppt und entfernt ihn.
* Gelaufen ist es auf Windows 11 und in der CI auf `windows-latest`. Auf Server 2016,
  2019 oder 2022 hat es meines Wissens noch niemand ausprobiert.
* Die Binärdateien sind nicht signiert, SmartScreen warnt also beim ersten Download.
  Siehe [Grenzen](#grenzen).
* Die französische, spanische und italienische Übersetzung ist nicht von
  Muttersprachlern gegengelesen.

Wenn es bei dir irgendwo klemmt, hilft ein Issue mit dem `…-easyservice.log` des Dienstes
im Anhang wirklich weiter.

## Was es kann

| | |
|---|---|
| Dienstverwaltung | Anlegen, bearbeiten, starten, beenden und entfernen, per Oberfläche oder Kommandozeile |
| Ausgabe mitschreiben | stdout und stderr in Dateien, getrennt oder zusammen, mit Rotation nach Größe und Zeit und begrenzter Archivanzahl |
| Protokollansicht | Hängt sich an die laufende Datei, folgt der Rotation, filtert nach Text, zeigt die passenden Windows-Ereignisse |
| Neustart-Richtlinie | Pro Exit-Code, mit einem Backoff, der Neustartschleifen beendet |
| Geplanter Neustart | Nachts zu fester Uhrzeit oder nach einer Laufzeit - nur die Anwendung, der Dienst bleibt |
| Herunterfahren | Strg+C, dann `WM_CLOSE`, dann `WM_QUIT`, dann hart; jede Stufe abschaltbar mit eigenem Zeitlimit |
| Prozessbaum | Kindprozesse laufen im Job-Objekt, werden also mit beendet und mitgezählt |
| Health-Checks | Die Anwendung selbst fragen: eine URL abrufen, einen TCP-Port öffnen, eine Datei beobachten oder ein Programm ausführen. Bei Fehlschlag melden oder neu starten |
| Monitoring | Ausgabe für Checkmk, Prometheus, Nagios/Icinga und Zabbix, dazu stabile Ereignis-IDs |
| Verlauf | CPU, Speicher und Neustarts je Minute, als CSV aufbewahrt |
| Konfiguration als Datei | Export und Import als JSON, im Fenster oder auf der Kommandozeile, um dieselbe Definition auf viele Rechner zu bringen |
| Ausgabe im Fenster | Dienst markieren, und unter der Liste läuft sein Protokoll mit |
| Sprachen | Englisch, Deutsch, Französisch, Spanisch, Italienisch |

![EasyService-Übersicht](assets/screenshot-overview.png)

## Monitoring

Ein Wrapper verdeckt genau das, was man wissen will. `sc query` meldet den Zustand des
Supervisor-Prozesses, nicht den der Anwendung dahinter. Ein Dienst, dessen Anwendung
jede Minute abstürzt und neu startet, steht deshalb weiterhin auf `RUNNING`.

EasyService zählt diese Neustarts und meldet sie. Eine Zeile im `local`-Verzeichnis des
Checkmk-Agenten genügt:

```bat
@"C:\Program Files\EasyService\easyservice.exe" checkmk
```

Danach ist jeder überwachte Dienst ein Checkmk-Service. Tatsächliche Ausgabe von einem
Testrechner, mit einem gesunden Dienst und einem, der seine Datenbank nicht erreicht:

```
0 EasyService_DemoWebApi   uptime=1070s|restarts_1h=0;3;10;0|cpu=7.41%;;;0;100|mem=75345920B|procs=2   Running for 17m 50s, PID 5868, 2 processes, CPU 7.41 %, RAM 71.9 MB, 0 restarts/h
2 EasyService_DemoImporter uptime=0s|restarts_1h=36;3;10;0|cpu=0%;;;0;100|mem=0B|procs=0               The application keeps restarting and is being throttled. 36 restarts in the last hour, last exit code 3.
```

Andere Systeme bekommen dieselben Daten in ihrem Format:

```
easyservice prometheus --output C:\...\easyservice.prom   Textfile-Collector, atomar ersetzt
easyservice check <Name>                                  Nagios/Icinga-Plugin, Exit-Code 0/1/2/3
easyservice zabbix-discovery                              Low-Level-Discovery
easyservice json                                          alles, für eigene Skripte
```

Meldungstext ist eine schlechte Grundlage für Alarme, deshalb trägt jedes Ereignis
zusätzlich eine stabile ID im Windows-Anwendungsprotokoll: 1004 wenn Neustarts gedrosselt
werden, 1005 bei einem gescheiterten Start, 1008 wenn die Anwendung hart beendet werden
musste. Diese IDs gehören zum Format und werden nicht übersetzt.

```powershell
Get-WinEvent -FilterHashtable @{ LogName='Application'; ProviderName='EasyService'; Id=1004,1005,1008 }
```

Die Konfigurationsschnipsel für die einzelnen Systeme stehen in
[docs/monitoring.de.md](docs/monitoring.de.md), samt `mk_logwatch`-Einrichtung, um die
Fehlerausgabe der eigenen Anwendung in Checkmk zu bekommen.

## Health-Checks

Ein laufender Prozess ist keine arbeitende Anwendung. Eine, die sich verklemmt hat, ihre
Datenbankverbindung verloren hat oder keine Anfragen mehr beantwortet, sieht für Windows
genauso aus wie eine gesunde — und das ist die Störung, für die niemand einen Alarm hat.

Also die Anwendung fragen. Vier Wege, auf der Registerkarte **Health-Check** im Editor:

```
Http        http://localhost:8080/health    Status 2xx oder der genannte
Tcp         localhost:5432                  der Port nimmt eine Verbindung an
FileFresh   C:\app\heartbeat.txt              in den letzten N Sekunden geschrieben
Command     "C:\app\check.exe" --quick        Exit-Code 0
```

Drumherum: wie oft, wie lange auf eine Antwort gewartet wird, wie lange die Anwendung nach
einem Start in Ruhe bleibt, und wie viele Fehlschläge hintereinander das Urteil kippen.
Eine einzelne fehlgeschlagene Prüfung während einer Garbage Collection ist kein Ausfall und
bleibt deshalb im Diagnoseprotokoll.

Danach entweder melden — der Dienst wird in Checkmk, Prometheus, Nagios und Zabbix
**kritisch**, noch vor jedem CPU-Schwellwert — oder die Anwendung neu starten und auch das
melden, mit Ereignis-ID 1013.

Ausprobieren, bevor man sich darauf verlässt:

```cmd
easyservice health MeinDaemon
gesund - HTTP 200 OK (34 ms)
```

Im Editor macht **Jetzt testen** dasselbe mit den Werten, die gerade im Dialog stehen. Eine
URL einzutippen und drei Minuten später aus dem Ereignisprotokoll zu erfahren, dass es die
falsche war, ist keine Art, etwas einzurichten.

## Wie es gegen die anderen dasteht

| | EasyService | NSSM | WinSW | AlwaysUp / FireDaemon |
|---|---|---|---|---|
| Preis | kostenlos, MIT | kostenlos | kostenlos | Lizenz pro Rechner |
| Grafische Oberfläche | ja | Installer-Dialog | keine | ja |
| Health-Check über „der Prozess existiert" hinaus | ja | nein | nein | ja |
| Ausgabe live im Fenster | ja | nein | nein | ja |
| CPU- und Speicherverlauf | ja | nein | nein | teilweise |
| Ausgabe für Checkmk / Prometheus / Nagios / Zabbix | ja | nein | nein | nein — E-Mail und eigene Weboberfläche |
| Ganze Definition als eine Datei | JSON | nein | XML | teilweise |
| Signierte Binärdateien | **nein** | nein | ja | **ja** |
| Support, den man anrufen kann | **nein** | nein | nein | **ja** |
| Zeitgesteuerte Neustarts | ja | nein | nein | ja |
| Letzte Veröffentlichung | diesen Monat | 2014 | Wartungsmodus | aktuell |

Die fett gesetzten Zeilen sind die, in denen Bezahlen die bessere Antwort ist. Wer signierte
Binärdateien braucht und einen Hersteller zum Eskalieren, kauft die Lizenz — das ist ein
echter Grund, und keine Funktionsliste ändert daran etwas.

## Verlauf

Ein Doppelklick auf einen Dienst zeigt, was er getan hat, statt nur was er gerade tut.
Der Supervisor verdichtet seine 5-Sekunden-Messungen auf eine Zeile pro Minute und legt
sie als CSV unter `%ProgramData%\EasyService\history\` ab — etwa 80 KB je Dienst und Tag,
also 2,3 MB für die voreingestellten 30 Tage.

![Verlauf eines Dienstes](assets/screenshot-history.png)

CPU und Speicher stehen in getrennten Diagrammen, weil es getrennte Skalen sind; zwei
y-Achsen in einem Rahmen ergeben ein Bild, das informativ aussieht und falsch gelesen
wird. Die Linie ist der Minutenmittelwert, die Fläche die Spitze, damit ein Dienst, der
bei 2 % dümpelt und auf 90 % springt, von einem bei konstant 40 % unterscheidbar bleibt.
Gepunktete Senkrechte markieren Starts der Anwendung.

Der Screenshot zeigt einen Demo-Dienst mit künstlichem Lastzyklus über eine halbe Stunde,
der sich alle fünf Minuten selbst recycelt.

## Einen Dienst einrichten

![Schnelleinrichtung](assets/screenshot-quicksetup.png)

Programm auswählen oder eine `.exe` ins Fenster ziehen. Dienstname, Startverzeichnis,
Protokollpfade, Rotation, Neustart-Richtlinie und Überwachungsschwellen werden vorbelegt
und angezeigt; Konto- und Kennwortfeld erscheinen nur, wenn man vom lokalen Systemkonto
abweicht. Der vollständige Editor mit neun Registerkarten liegt hinter **Erweiterte
Einstellungen…**.

Schlägt der erste Start fehl, bietet EasyService gleich das Protokoll an. In der Praxis
ist die Ursache ein falscher Pfad oder ein falsches Argument, und das Protokoll sagt
welches.

Aus einem Skript:

```cmd
easyservice install MeinDaemon "C:\apps\daemon.exe" --config C:\apps\daemon.yml
easyservice status MeinDaemon
```

`status` endet mit Exit-Code 0, wenn der Dienst läuft, und mit 3, wenn nicht.

## Auf viele Rechner ausrollen

Eine vollständige Definition — mitsamt Exit-Code-Regeln, Schwellwerten,
Umgebungsvariablen und den Shutdown-Stufen — lässt sich in eine Datei schreiben und
anderswo anwenden:

```cmd
easyservice export MeinDaemon --output daemon.json
easyservice import daemon.json
```

```powershell
# dieselbe Definition auf jeden Server
$server | ForEach-Object {
    Copy-Item daemon.json "\\$_\C$\temp\"
    Invoke-Command -ComputerName $_ { easyservice import C:\temp\daemon.json }
}

# was ist abgedriftet?
easyservice export MeinDaemon --output aktuell.json
git diff --no-index golden.json aktuell.json
```

Zwei Dinge zum Format. Kennwörter stehen nicht in der Datei, weil eine Datei, die in
einem Repository landet, kein Dienstkonto-Kennwort enthalten darf; beim Import kommt es
aus `EASYSERVICE_PASSWORD`, einer Umgebungsvariablen statt eines Arguments, weil
Kommandozeilen in der Prozessliste sichtbar sind. Wird ein bestehender Dienst ohne diese
Variable aktualisiert, bleibt das gespeicherte Kennwort unangetastet. Aufzählungswerte
stehen als Text in der Datei (`"startup": "AutomaticDelayed"`), damit ein Diff gegen eine
Referenzdatei lesbar bleibt.

`export --all` schreibt alle verwalteten Dienste in eine Datei, und `import` versteht
beide Formen.

Dieselben drei Aktionen liegen im Fenster unter **Konfiguration** in der Werkzeugleiste.
Von dort kommt üblicherweise die erste Datei: einen Dienst von Hand einrichten,
exportieren, ins Repository legen, per Kommandozeile ausrollen. Beim Import fragt das
Fenster nach dem Kennwort eines Dienstkontos, statt die Umgebungsvariable zu lesen.

## Installieren

Fertige Binärdateien liegen auf der [Releases-Seite](../../releases).

| Datei | |
|---|---|
| `easyservice.exe` | Alles enthalten, rund 63 MB, keine Runtime nötig |
| `easyservice-framework-dependent.exe` | Rund 300 KB, benötigt das [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) |

Der Pfad zur Programmdatei wird in jedem angelegten Dienst hinterlegt, also vor dem
ersten Dienst einen festen Ort wählen. `C:\Program Files\EasyService\` ist eine
vernünftige Wahl und hat den nützlichen Nebeneffekt, dass normale Benutzer dort nicht
schreiben können — der Dienst läuft als SYSTEM, ein beschreibbarer Ort wäre eine
Rechteausweitung mit Ansage.

Voraussetzungen: Windows 10 beziehungsweise Server 2016 oder neuer, x64. Die
Überwachungsbefehle laufen unter jedem Konto — genau deshalb kommt ein Monitoring-Agent
ohne privilegiertes Dienstkonto aus. Dienste anzulegen, zu ändern und zu entfernen braucht
Administratorrechte; ohne sie enden diese Befehle mit Code 5, und die Oberfläche fordert
die Erhöhung beim Start per UAC an.

Vor dem Ausrollen: [docs/deployment.de.md](docs/deployment.de.md) behandelt das Prüfen des
Downloads, die AppLocker- und WDAC-Regeln und die maschinenweite Spracheinstellung.

## Grenzen

* **Nicht signiert.** SmartScreen warnt beim ersten Download, AppLocker oder WDAC
  blockieren die Datei, bis man sie per Hash zulässt. Zu jedem Release gehören
  SHA256-Prüfsummen, eine CycloneDX-Stückliste und eine GitHub-Build-Attestation, die
  Herkunft ist also wenigstens nachprüfbar — siehe
  [docs/deployment.de.md](docs/deployment.de.md). Eine echte Signatur braucht ein Zertifikat.
* **Beim Start des Fensters blitzt eine Konsole auf, etwa eine fünftel Sekunde lang.**
  EasyService ist mit Absicht ein Konsolenprogramm, damit Shells darauf warten und
  `%ERRORLEVEL%` und `$LASTEXITCODE` etwas bedeuten. Windows gibt jedem Start eine Konsole,
  auch dem, der nur das Fenster will; die wird als Erstes versteckt und freigegeben, es
  bleiben rund 0,2 s schwarzer Kasten. Der Tausch ist gewollt: ein Werkzeug, das sich als
  skriptbar verkauft, muss sein Ergebnis melden.
* **Kein Installer.** Man kopiert die Exe irgendwohin und startet sie. Kein MSI, noch
  kein winget-Paket.
* **Nur x64.** Keine ARM64-Fassung.
* **Der Datenaustausch mit dem Desktop funktioniert nicht wirklich.** Die Option gibt es,
  weil die Dienst-API sie kennt, aber Windows isoliert Dienste in Sitzung 0 — die Fenster
  sieht also niemand.
* **Die Ressourcenmessung hängt am Job-Objekt.** Bricht ein Kindprozess daraus aus, wird
  er weder gezählt noch mit dem Dienst beendet. Das Diagnoseprotokoll des Dienstes
  vermerkt, wenn sich das Job-Objekt nicht einrichten ließ.

## Dokumentation

* [docs/deployment.de.md](docs/deployment.de.md) — Download prüfen, Anwendungssteuerung,
  wohin mit der Programmdatei, eine Protokollsprache für die ganze Flotte
* [docs/monitoring.de.md](docs/monitoring.de.md) — Checkmk, Prometheus, Zabbix, Nagios,
  die Ereignis-IDs und die Spracheinstellung für die Ausgabe
* Die Registerkarte **Überwachung** im Editor enthält Copy-paste-Schnipsel für die Agenten

## Selbst bauen

Benötigt das [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) unter Windows.

```cmd
git clone https://github.com/sdrabent/easyservice.git
cd easyservice
dotnet build EasyService.sln -c Release
dotnet run --project tests/EasyService.Tests -c Release
```

Der Test von Anfang bis Ende legt einen echten Dienst an und braucht deshalb eine erhöhte
Shell:

```powershell
dotnet publish src/EasyService/EasyService.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/standalone
powershell -ExecutionPolicy Bypass -File tests\e2e\Invoke-ServiceTest.ps1
```

Es gibt keine NuGet-Abhängigkeiten; die Windows-Aufrufe laufen über P/Invoke gegen
`advapi32`, `kernel32`, `user32` und `crypt32`. Die Oberflächentexte liegen als `.resx`
unter `src/EasyService/Resources/`; nach einer Änderung erzeugt
`python tools/generate-strings.py` die typsicheren Zugriffe neu. Die CI prüft, dass beides
nicht auseinanderläuft und dass jeder Text in allen fünf Sprachen existiert und seine
Platzhalter behält.

Die Tests steuern den Supervisor direkt an, brauchen also weder Administratorrechte noch
einen installierten Dienst.

## Wie es funktioniert

`easyservice.exe` hat zwei Betriebsarten, wie `nssm.exe`. Per Doppelklick ist es die
Verwaltungsoberfläche. Vom Dienst-Manager als `easyservice.exe run "MeinDienst"` gestartet,
liest es die Konfiguration aus `HKLM\SYSTEM\CurrentControlSet\Services\<Name>\Parameters`,
startet die Anwendung mit umgeleiteten Pipes und beaufsichtigt sie.

Die Anwendung wird einem Job-Objekt zugeordnet. Das macht es möglich, Kindprozesse
zuverlässig mit zu beenden — der übliche Schwachpunkt eines per `sc.exe` angelegten
Dienstes — und dient zugleich als Abrechnungsgrenze für CPU und Speicher, sodass eine
Batchdatei, die `java.exe` startet, korrekt gezählt wird statt null zu melden.

Zusätzlich sind die Windows-eigenen Wiederherstellungsaktionen gesetzt, als zweite
Absicherung für den Fall, dass der Supervisor-Prozess selbst ausfällt.

<details>
<summary>Registry-Referenz — alle Werte unter <code>Parameters</code></summary>

Mit `regedit` einsehbar, per Skript setzbar, und das, was `export` und `import` lesen
und schreiben.

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
| `HealthType` | DWORD | 0 keiner, 1 http, 2 tcp, 3 Datei, 4 Kommando |
| `HealthTarget` | EXPAND_SZ | URL, host:port, Dateipfad oder Kommandozeile |
| `HealthInterval` / `HealthTimeout` / `HealthGrace` | DWORD | Abstand, Zeitlimit, Anlaufzeit nach dem Start (ms) |
| `HealthFailures` | DWORD | Fehlschläge hintereinander, bis der Dienst als krank gilt |
| `HealthAction` | DWORD | 0 nur melden, 1 Anwendung neu starten |
| `HealthExpectStatus` | DWORD | HTTP: erwarteter Status, 0 nimmt 200-299 |
| `HealthMaxAge` | DWORD | Datei-Check: erlaubtes Alter der Datei in Sekunden |
| `RestartScheduleMode` | DWORD | 0 nie, 1 zu einer Uhrzeit, 2 nach einer Laufzeit |
| `RestartAtMinutes` | DWORD | Minuten nach Mitternacht, Ortszeit. 180 ist 03:00 |
| `RestartDays` | DWORD | Wochentagsmaske, Bit 0 ist Sonntag, 127 ist jeder Tag |
| `RestartEveryMinutes` | DWORD | Laufzeit, nach der die Anwendung neu gestartet wird |

Protokolle liegen standardmäßig unter `%ProgramData%\EasyService\logs\`, der Verlauf
unter `%ProgramData%\EasyService\history\`.

</details>

## Mitmachen

Issues und Pull Requests sind willkommen. Für alles, was über eine Korrektur hinausgeht,
spart ein Issue vorher beiden Seiten Zeit. `dotnet build` muss warnungsfrei bleiben und
die Tests müssen durchlaufen.

Übersetzungskorrekturen sind besonders willkommen — die `.resx`-Dateien sind die Quelle
und lassen sich mit jedem Übersetzungswerkzeug öffnen.

## Lizenz

MIT, siehe [LICENSE](LICENSE).
