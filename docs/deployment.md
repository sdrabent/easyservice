# Deploying EasyService

What an administrator needs before putting an unknown executable on 200 machines:
where it came from, that it is the file the build produced, and how to allow it past
application control.

## Verifying a download

Every release carries `SHA256SUMS.txt` next to the binaries:

```powershell
$expected = (Select-String -Path SHA256SUMS.txt -Pattern 'easyservice.exe').Line.Split(' ')[0]
$actual   = (Get-FileHash easyservice.exe -Algorithm SHA256).Hash
if ($actual -ne $expected) { throw "Hash does not match" }
```

That proves the file is intact. It does not prove where it came from, because both the
file and the checksum come from the same release page. For that, releases from tags carry
a build attestation created by GitHub, which records which workflow built the file from
which commit:

```cmd
gh attestation verify easyservice.exe --owner sdrabent
```

The command succeeds only if the binary was produced by the `build` workflow in this
repository. If you have no cheap way to check attestations, build from source instead —
`dotnet publish` reproduces the same binary from the same tag.

Releases also include `easyservice-sbom.json`, a CycloneDX bill of materials. It is short:
the project has no NuGet dependencies, only the .NET runtime.

## Not signed

The binaries carry no Authenticode signature. Consequences to plan for:

- SmartScreen warns on the first start until enough people have run the file.
- AppLocker or WDAC policies that only permit signed publishers will block it.

Until that changes, allow the file by hash. AppLocker:

```powershell
$file = Get-AppLockerFileInformation -Path "C:\Program Files\EasyService\easyservice.exe"
$rule = New-AppLockerPolicy -FileInformation $file -RuleType Hash -User Everyone
$rule.ToXml() | Out-File easyservice-applocker.xml
```

Merge that XML into the policy you deploy by GPO. For WDAC, the same file information
goes into `New-CIPolicyRule -Level Hash`. Both rules are tied to the exact binary, so they
have to be renewed with every version — which is also the point: nothing else gets in.

## Where to put it

The path to `easyservice.exe` ends up in the `ImagePath` of every service it creates, so
pick a location and keep it. `C:\Program Files\EasyService\` is the obvious one; anything
under a user profile is not.

A running service keeps its executable open. Replacing the file while services are running
does not work; stop them, replace, start again. A command for this is on the list, it does
not exist yet.

## One language across the fleet

Log files and event log messages are written in the language of the machine. On a mixed
fleet that makes text-based log processing tedious. A machine-wide setting overrides it:

```
HKLM\SOFTWARE\EasyService
    Language  REG_SZ  en
```

Roll it out per GPO preference and every machine logs in English regardless of its own
locale. Values: `en`, `de`, `fr`, `es`, `it`. The numbers in the monitoring output are
invariant either way, and the event IDs 1000–1010 never change.

## Rights

Reading needs none: `list`, `status`, `check`, `checkmk`, `prometheus`, `json` and
`zabbix-discovery` run under any account, which is what lets a monitoring agent use them
without a privileged service account.

Changing needs administrator rights: `install`, `remove`, `start`, `stop`, `restart` and
`import` exit with code 5 when the process is not elevated. The graphical interface asks
for elevation when it starts.

## Rollout by file

The complete definition of a service travels as JSON. See the
[configuration section of the README](../README.md#rolling-out-to-many-machines) for
`export` and `import`, and remember that passwords are never in the file: on import they
come from `EASYSERVICE_PASSWORD`.
