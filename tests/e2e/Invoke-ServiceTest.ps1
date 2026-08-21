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

    # Start-Process mit -Wait statt "& $Exe": easyservice.exe ist ein Windows-Subsystem-
    # Programm, auf das eine Shell nicht wartet. Direkt aufgerufen kaeme weder die Ausgabe
    # noch der Rueckgabewert hier an.
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

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole("Administrator")) {
    throw "Dieser Test legt echte Dienste an und braucht eine erhöhte PowerShell."
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
    Stop-Process -Id $firstPid -Force
    Wait-Until { (Get-State).RestartCount -ge 1 } "der Neustartzähler steht auf mindestens 1"
    Wait-Until {
        $s = Get-State
        $s.State -eq "Running" -and $s.ApplicationPid -ne $firstPid -and $s.ApplicationPid -gt 0
    } "das Kind läuft unter neuer PID weiter"
    Wait-Until { Test-EventPresent 1003 $startedAt } "Ereignis 1003 (Neustart) steht im Anwendungsprotokoll"
    $secondPid = (Get-State).ApplicationPid

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
