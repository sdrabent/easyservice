<#
.SYNOPSIS
    Drives a real Windows service through its whole life, against the real SCM.

.DESCRIPTION
    The unit tests exercise the supervisor in-process, which covers the interesting logic
    but never touches CreateService, the SCM state machine or the registry layout. This
    script does exactly what an administrator does: install, start, watch it log, kill the
    child and see it come back, stop, remove - and then checks that nothing is left over.

    Runs in an elevated PowerShell locally and unchanged in CI. Every wait has a limit;
    nothing sleeps for a fixed span and hopes.

.PARAMETER Exe
    The easyservice.exe under test. Defaults to the self-contained publish output.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tests\e2e\Invoke-ServiceTest.ps1
#>
[CmdletBinding()]
param(
    [string] $Exe = (Join-Path $PSScriptRoot "..\..\publish\standalone\easyservice.exe"),
    [string] $ServiceName = "easyservice-e2e"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$script:Checks = 0

function Step([string] $message) {
    Write-Host ""
    Write-Host "== $message" -ForegroundColor Cyan
}

function Confirm-That([bool] $condition, [string] $message) {
    $script:Checks++
    if (-not $condition) { throw "FEHLGESCHLAGEN: $message" }
    Write-Host "   ok  $message"
}

function Wait-Until {
    param(
        [scriptblock] $Condition,
        [string] $Description,
        [int] $TimeoutSeconds = 30
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        # Ein Fehler in der Bedingung heisst "noch nicht": die Statusdatei wird gerade
        # ersetzt, ein Feld fehlt noch, der Prozess ist eben verschwunden.
        $met = $false
        try { $met = [bool] (& $Condition) } catch { $met = $false }
        if ($met) {
            Confirm-That $true $Description
            return
        }
        Start-Sleep -Milliseconds 400
    } while ((Get-Date) -lt $deadline)

    throw "FEHLGESCHLAGEN nach ${TimeoutSeconds}s: $Description"
}

function Invoke-Es {
    param([string[]] $Arguments, [int] $ExpectedExitCode = 0)

    # Start-Process statt "& $Exe": der direkte Aufruf funktioniert seit dem Wechsel auf ein
    # Konsolenprogramm (siehe den Schritt "Rueckgabewerte in der Shell"), aber hier sollen
    # stdout und stderr getrennt bleiben, ohne dass PowerShell 5.1 jede stderr-Zeile in einen
    # Fehlerdatensatz verwandelt.
    $out = [IO.Path]::GetTempFileName()
    $err = [IO.Path]::GetTempFileName()
    try {
        $process = Start-Process -FilePath $Exe -ArgumentList $Arguments -NoNewWindow -Wait -PassThru `
                                 -RedirectStandardOutput $out -RedirectStandardError $err
        $text = (Get-Content $out -Raw), (Get-Content $err -Raw) -join ""
        $text -split "`r?`n" | Where-Object { $_ } | ForEach-Object { Write-Host "   | $_" }

        if ($process.ExitCode -ne $ExpectedExitCode) {
            throw "FEHLGESCHLAGEN: 'easyservice $($Arguments -join ' ')' endete mit $($process.ExitCode) statt $ExpectedExitCode"
        }
        $script:Checks++
        return $text
    }
    finally {
        Remove-Item $out, $err -ErrorAction SilentlyContinue
    }
}

function Get-State {
    $path = Join-Path $env:ProgramData "EasyService\state\$ServiceName.json"
    if (-not (Test-Path $path)) { return $null }
    try { Get-Content $path -Raw | ConvertFrom-Json } catch { $null }
}

function Test-EventPresent([int] $Id, [datetime] $Since) {
    $filter = @{ LogName = "Application"; ProviderName = "EasyService"; Id = $Id; StartTime = $Since }
    $null -ne (Get-WinEvent -FilterHashtable $filter -MaxEvents 1 -ErrorAction SilentlyContinue)
}

# ---------------------------------------------------------------------------

if (-not (Test-Path $Exe)) { throw "Nicht gefunden: $Exe" }
$Exe = (Resolve-Path $Exe).Path

# Die eingebaute Rolle, nicht der Gruppenname: IsInRole("Administrator") sucht eine Gruppe
# dieses Namens und findet sie nie - die Gruppe heisst "Administrators", auf Deutsch
# "Administratoren".
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal $identity
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Dieser Test legt echte Dienste an und braucht eine erhöhte PowerShell. Angemeldet als $($identity.Name)."
}

Write-Host "easyservice: $Exe"
Write-Host "Dienst:      $ServiceName"

$workDir = Join-Path ([IO.Path]::GetTempPath()) ("easyservice-e2e-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $workDir | Out-Null

# Ein Kind, das dauerhaft läuft und stetig auf stdout schreibt - beides braucht der Test.
$childScript = Join-Path $workDir "tick.ps1"
@'
$i = 0
while ($true) {
    $i++
    [Console]::Out.WriteLine("tick $i")
    [Console]::Out.Flush()
    Start-Sleep -Seconds 1
}
'@ | Set-Content -Path $childScript -Encoding UTF8

$powershell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
$logPath = Join-Path $env:ProgramData "EasyService\logs\$ServiceName-stdout.log"
$serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$startedAt = Get-Date

try {
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Write-Host "Rest aus einem früheren Lauf wird entfernt."
        Start-Process -FilePath $Exe -ArgumentList @("remove", $ServiceName) -NoNewWindow -Wait
        Start-Sleep -Seconds 2
    }
    Remove-Item $logPath -ErrorAction SilentlyContinue

    Step "Version"
    $version = Invoke-Es @("--version")
    Confirm-That ($version -match "easyservice \d+\.\d+\.\d+") "--version meldet eine Versionsnummer: $($version.Trim())"

    Step "Rueckgabewerte in der Shell"
    # Der eigentliche Test dieser Runde: easyservice ist ein Konsolenprogramm, also wartet
    # die Shell auf das Ende und $LASTEXITCODE stimmt. Als Windows-Subsystem-Programm kam
    # hier nie etwas an.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"   # stderr eines nativen Programms ist kein Abbruch
    try {
        $direct = & $Exe --version 2>&1
        Confirm-That ($LASTEXITCODE -eq 0) "ein direkter Aufruf setzt den Rueckgabewert auf 0"
        Confirm-That ((($direct -join " ") -match "easyservice")) "die Ausgabe kommt in der Shell an"

        & $Exe status ("gibtesnicht-" + [guid]::NewGuid().ToString("N")) 2>&1 | Out-Null
        Confirm-That ($LASTEXITCODE -ne 0) "ein fehlgeschlagener Aufruf meldet das ueber den Rueckgabewert ($LASTEXITCODE)"

        $piped = (& $Exe --version 2>&1 | Select-Object -First 1)
        Confirm-That ("$piped" -match "easyservice") "die Ausgabe laesst sich weiterleiten"
    }
    finally { $ErrorActionPreference = $previousPreference }

    Step "Anlegen"
    Invoke-Es @("install", $ServiceName, $powershell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $childScript) | Out-Null
    Confirm-That ($null -ne (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) "der SCM kennt den Dienst"

    $imagePath = (Get-ItemProperty $serviceKey).ImagePath
    Confirm-That ($imagePath -like "*easyservice.exe*") "ImagePath zeigt auf easyservice.exe"
    Confirm-That ($imagePath -like "*run*$ServiceName*") "ImagePath übergibt run und den Dienstnamen"

    $parameters = Get-ItemProperty "$serviceKey\Parameters"
    Confirm-That ($parameters.Application -eq $powershell) "Application steht unter Parameters"

    Step "Starten"
    Invoke-Es @("start", $ServiceName) | Out-Null
    Confirm-That ((Get-Service $ServiceName).Status -eq "Running") "der SCM meldet Running"

    Wait-Until { (Get-State) -ne $null } "die Statusdatei ist da"
    Wait-Until { (Get-State).State -eq "Running" } "der Supervisor meldet Running"

    $state = Get-State
    $firstPid = $state.ApplicationPid
    Confirm-That ($firstPid -gt 0) "eine Kind-PID steht in der Statusdatei ($firstPid)"
    Confirm-That ($null -ne (Get-Process -Id $firstPid -ErrorAction SilentlyContinue)) "der Kindprozess läuft wirklich"

    Step "Protokoll und Ereignisse"
    Wait-Until { (Test-Path $logPath) -and ((Get-Content $logPath -Raw) -match "tick") } "stdout des Kindes landet im Protokoll"
    $sizeBefore = (Get-Item $logPath).Length
    Wait-Until { (Get-Item $logPath).Length -gt $sizeBefore } "das Protokoll wächst weiter"
    Wait-Until { Test-EventPresent 1001 $startedAt } "Ereignis 1001 (Anwendung gestartet) steht im Anwendungsprotokoll"

    Step "Monitoring"
    Invoke-Es @("check", $ServiceName) | Out-Null
    $checkmk = Invoke-Es @("checkmk")
    Confirm-That (($checkmk -join "`n") -match "EasyService_$ServiceName") "der Checkmk-Ausgabe liegt eine Zeile für den Dienst bei"

    Step "Neustart nach Absturz des Kindes"
    # Erst die Drosselschwelle abwarten (Standard 5 s). Stirbt ein Kind frueher, meldet der
    # Supervisor zu Recht 1004 "Neustart gedrosselt" statt 1003 "wird neu gestartet" - der
    # Test will hier den gewoehnlichen Neustart sehen.
    $ranSince = Get-Date
    Wait-Until { ((Get-Date) - $ranSince).TotalSeconds -gt 6 } "das Kind läuft länger als die Drosselschwelle" -TimeoutSeconds 20

    Stop-Process -Id $firstPid -Force
    Wait-Until { (Get-State).RestartCount -ge 1 } "der Neustartzähler steht auf mindestens 1"
    Wait-Until { Test-EventPresent 1002 $startedAt } "Ereignis 1002 (Anwendung beendet) steht im Anwendungsprotokoll"
    Wait-Until {
        $s = Get-State
        $s.State -eq "Running" -and $s.ApplicationPid -ne $firstPid -and $s.ApplicationPid -gt 0
    } "das Kind läuft unter neuer PID weiter"
    Wait-Until { Test-EventPresent 1003 $startedAt } "Ereignis 1003 (Neustart) steht im Anwendungsprotokoll"
    $secondPid = (Get-State).ApplicationPid

    Step "Health-Check"
    # Der Dienst schreibt jede Sekunde eine Zeile. Ein Datei-Check auf sein eigenes Protokoll
    # ist damit gesund, solange er laeuft - und krank, sobald er es nicht mehr tut.
    $definition = Join-Path $workDir "definition.json"
    Invoke-Es @("export", $ServiceName, "--output", $definition) | Out-Null

    $json = Get-Content $definition -Raw | ConvertFrom-Json
    $json.healthType = "FileFresh"
    $json.healthTarget = $logPath
    $json.healthMaxAgeSec = 10
    $json.healthIntervalMs = 1000
    $json.healthGraceMs = 0
    $json.healthFailures = 1
    $json.healthAction = "Report"
    $json | ConvertTo-Json -Depth 6 | Set-Content $definition -Encoding UTF8

    Invoke-Es @("import", $definition) | Out-Null
    Invoke-Es @("restart", $ServiceName) | Out-Null

    Wait-Until { (Get-State).Health -eq "Healthy" } "der Health-Check meldet den Dienst als gesund"

    $health = Invoke-Es @("health", $ServiceName)
    Confirm-That ($health.Length -gt 0) "easyservice health gibt ein Ergebnis aus"

    # Und andersherum: ein Ziel, das es nicht gibt, muss auffallen.
    $json.healthTarget = Join-Path $workDir "gibtesnicht.txt"
    $json | ConvertTo-Json -Depth 6 | Set-Content $definition -Encoding UTF8
    Invoke-Es @("import", $definition) | Out-Null
    Invoke-Es @("health", $ServiceName) -ExpectedExitCode 2 | Out-Null

    Step "Geplanter Neustart"
    # Kuerzestes Intervall, das die Konfiguration zulaesst: nach einer Minute Laufzeit soll
    # die Anwendung von selbst neu starten - ohne dass der Dienst dabei stoppt.
    $json = Get-Content $definition -Raw | ConvertFrom-Json
    $json.healthType = "None"
    $json.restartScheduleMode = "Interval"
    $json.restartEveryMinutes = 1
    $json | ConvertTo-Json -Depth 6 | Set-Content $definition -Encoding UTF8

    Invoke-Es @("import", $definition) | Out-Null
    Invoke-Es @("restart", $ServiceName) | Out-Null
    Wait-Until { (Get-State).State -eq "Running" } "der Dienst laeuft wieder"

    $beforePid = (Get-State).ApplicationPid
    $plannedAt = Get-Date
    Wait-Until { (Get-State).ScheduledRestarts -ge 1 } "der Plan hat die Anwendung neu gestartet" -TimeoutSeconds 150
    Wait-Until { (Get-State).ApplicationPid -ne $beforePid -and (Get-State).ApplicationPid -gt 0 } "sie laeuft unter neuer PID"
    Confirm-That ((Get-Service $ServiceName).Status -eq "Running") "der Dienst selbst hat dabei nicht gestoppt"
    Wait-Until { Test-EventPresent 1014 $plannedAt } "Ereignis 1014 (planmaessiger Neustart) steht im Anwendungsprotokoll"

    Step "Stoppen"
    Invoke-Es @("stop", $ServiceName) | Out-Null
    Confirm-That ((Get-Service $ServiceName).Status -eq "Stopped") "der SCM meldet Stopped"
    Wait-Until { $null -eq (Get-Process -Id $secondPid -ErrorAction SilentlyContinue) } "der Kindprozess ist mitgegangen"

    Step "Entfernen"
    Invoke-Es @("remove", $ServiceName) | Out-Null
    Wait-Until { $null -eq (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) } "der SCM kennt den Dienst nicht mehr"
    Wait-Until { -not (Test-Path $serviceKey) } "der Registrierungsschlüssel ist weg"

    Write-Host ""
    Write-Host "Alle $script:Checks Prüfungen erfolgreich." -ForegroundColor Green
    exit 0
}
catch {
    Write-Host ""
    Write-Host $_.Exception.Message -ForegroundColor Red
    $state = Get-State
    if ($state) { Write-Host "Letzter bekannter Zustand:`n$($state | ConvertTo-Json -Depth 3)" }
    if (Test-Path $logPath) {
        Write-Host "Letzte Protokollzeilen:"
        Get-Content $logPath -Tail 20 | ForEach-Object { Write-Host "   | $_" }
    }
    exit 1
}
finally {
    # Ein fehlgeschlagener Lauf darf keinen Dienst zurücklassen, sonst scheitert der nächste
    # schon beim Anlegen.
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Start-Process -FilePath $Exe -ArgumentList @("remove", $ServiceName) -NoNewWindow -Wait
    }
    Remove-Item $workDir -Recurse -Force -ErrorAction SilentlyContinue
}
