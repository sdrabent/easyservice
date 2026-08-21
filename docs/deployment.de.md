# EasyService verteilen

Was ein Administrator wissen will, bevor eine unbekannte Programmdatei auf 200 Rechner
geht: woher sie stammt, ob sie unverändert ist und wie man sie an der Anwendungssteuerung
vorbeibekommt.

## Download prüfen

Zu jedem Release gehört `SHA256SUMS.txt`:

```powershell
$erwartet = (Select-String -Path SHA256SUMS.txt -Pattern 'easyservice.exe').Line.Split(' ')[0]
$tatsaechlich = (Get-FileHash easyservice.exe -Algorithm SHA256).Hash
if ($tatsaechlich -ne $erwartet) { throw "Hash stimmt nicht" }
```

Das belegt, dass die Datei unversehrt ist. Woher sie kommt, belegt es nicht — Datei und
Prüfsumme liegen auf derselben Seite. Dafür tragen Releases aus einem Tag eine
Build-Attestation von GitHub, die festhält, welcher Workflow die Datei aus welchem Commit
erzeugt hat:

```cmd
gh attestation verify easyservice.exe --owner sdrabent
```

Der Befehl gelingt nur, wenn das Binary aus dem `build`-Workflow dieses Repositorys stammt.
Wer Attestationen nicht prüfen kann, baut stattdessen selbst: `dotnet publish` erzeugt aus
demselben Tag dasselbe Ergebnis.

Ebenfalls im Release: `easyservice-sbom.json`, eine Stückliste im CycloneDX-Format. Sie ist
kurz, weil das Projekt keine NuGet-Abhängigkeiten hat, nur die .NET-Laufzeit.

## Nicht signiert

Die Binärdateien tragen keine Authenticode-Signatur. Damit ist zu rechnen:

- SmartScreen warnt beim ersten Start, bis genug Leute die Datei ausgeführt haben.
- AppLocker- oder WDAC-Richtlinien, die nur signierte Herausgeber zulassen, blockieren sie.

Bis sich das ändert, hilft eine Hashregel. AppLocker:

```powershell
$datei = Get-AppLockerFileInformation -Path "C:\Program Files\EasyService\easyservice.exe"
$regel = New-AppLockerPolicy -FileInformation $datei -RuleType Hash -User Everyone
$regel.ToXml() | Out-File easyservice-applocker.xml
```

Das XML kommt in die Richtlinie, die per GPO verteilt wird. Für WDAC gehen dieselben
Dateiangaben an `New-CIPolicyRule -Level Hash`. Beide Regeln hängen am konkreten Binary,
müssen also mit jeder Version erneuert werden — was zugleich der Sinn der Sache ist.

## Wohin damit

Der Pfad zu `easyservice.exe` landet im `ImagePath` jedes angelegten Dienstes. Also einen
Ort wählen und dabei bleiben; `C:\Program Files\EasyService\` ist der naheliegende,
irgendetwas unter einem Benutzerprofil nicht.

Ein laufender Dienst hält seine Programmdatei offen. Sie zu ersetzen, während Dienste
laufen, funktioniert nicht: erst stoppen, dann tauschen, dann starten. Ein Befehl dafür
steht auf der Liste, es gibt ihn noch nicht.

## Eine Sprache für die ganze Flotte

Protokolldateien und Ereignisse werden in der Sprache des Rechners geschrieben. Auf einer
gemischten Flotte macht das textbasierte Auswertung mühsam. Eine maschinenweite
Einstellung überstimmt das:

```
HKLM\SOFTWARE\EasyService
    Language  REG_SZ  en
```

Per GPO-Einstellung verteilt, protokolliert jeder Rechner auf Englisch, unabhängig von
seinem Gebietsschema. Mögliche Werte: `en`, `de`, `fr`, `es`, `it`. Die Zahlen der
Monitoring-Ausgabe sind ohnehin invariant, und die Ereignis-IDs 1000–1010 ändern sich nie.

## Rechte

Lesen braucht keine: `list`, `status`, `check`, `checkmk`, `prometheus`, `json` und
`zabbix-discovery` laufen unter jedem Konto. Genau deshalb kommt ein Monitoring-Agent ohne
privilegiertes Dienstkonto aus.

Ändern braucht Administratorrechte: `install`, `remove`, `start`, `stop`, `restart` und
`import` enden mit Code 5, wenn der Prozess nicht erhöht läuft. Die Oberfläche fordert die
Erhöhung beim Start an.

## Ausrollen per Datei

Die vollständige Definition eines Dienstes reist als JSON. `export` und `import` stehen im
[entsprechenden Abschnitt der README](../README.de.md#auf-viele-rechner-ausrollen).
Kennwörter stehen nie in der Datei; beim Import kommen sie aus `EASYSERVICE_PASSWORD`.
