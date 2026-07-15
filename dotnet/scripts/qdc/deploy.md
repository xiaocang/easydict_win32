# QDC Deploy — Quick Resume

Deploy a signed ARM64 MSIX to a Qualcomm Device Cloud (QDC) Windows device for
Phi Silica / Snapdragon X testing. Every QDC reservation is fresh — nothing
persists between reservations.

## Per-session inputs

Each QDC reservation gives you two new values from the QDC portal; everything
else is constant.

| Var                  | Where to get it                                                   |
| -------------------- | ----------------------------------------------------------------- |
| `<PEM>`              | "Download key" on the reservation page → `~/Downloads/qdc_id_*.pem` |
| `<DEVICE>`           | Reservation page, e.g. `sa<NNNNN>.sa.svc.cluster.local`           |
| `<MSIX>`             | Local build output, e.g. `dotnet/msix/Easydict-v<X.Y.Z>-arm64.msix` |

Fixed: `User=hcktest`, `LocalPort=2222`, `RDP LocalPort=5555`, jump host
`sshtunnel@ssh.qdc.qualcomm.com`.

## Three-step session bring-up

All scripts live in `dotnet/scripts/qdc/`.

```powershell
# 1) Main tunnel (localhost:2222 -> <DEVICE>:22 via the QDC jump host)
.\Start-QdcTunnel.ps1 -IdentityFile <PEM> -DeviceHost <DEVICE>

# 2) RDP forward (localhost:5555 -> device:3389) — only needed for manual ops
.\Start-QdcRdpForward.ps1 -IdentityFile <PEM>

# 2a) ONE-TIME per reservation, before any RDP attempt (QDC docs):
#     RDP blocks blank-password logins by default and hcktest has no password.
ssh -i <PEM> -o NoHostAuthenticationForLocalhost=yes -p 2222 hcktest@localhost `
    "reg add HKLM\SYSTEM\CurrentControlSet\Control\Lsa /v LimitBlankPasswordUse /t REG_DWORD /d 0 /f"

mstsc /v:localhost:5555    # then install WindowsAppRuntime 2.0, see "Traps" below

# 3) Deploy
.\Deploy-ToQdc.ps1 `
    -RemoteHost localhost -Port 2222 -User hcktest `
    -IdentityFile <PEM> `
    -MsixPath <MSIX>
```

Sanity check (optional, prints OS build / arch / Phi Silica gate):
```powershell
.\Test-QdcConnection.ps1 -RemoteHost localhost -Port 2222 -User hcktest -IdentityFile <PEM>
```

Teardown:
```powershell
.\Start-QdcRdpForward.ps1 -Stop
.\Start-QdcTunnel.ps1 -Stop
```

## Traps (each one is already mitigated in the scripts — listed so you know what you're looking at if a future run regresses)

1. **WindowsAppRuntime 2.0 missing on fresh reservation.** Each new QDC
   device starts blank. `Install-OnQdc.ps1` tries `winget install
   Microsoft.WindowsAppRuntime.2.0`, but winget over the non-interactive SSH
   logon hits `Access is denied` on `winget.exe`. **Workaround:** RDP in via
   `mstsc /v:localhost:5555` and run the winget install (or install "Windows
   App Runtime 2.0" from the Microsoft Store) once per reservation, then
   redeploy.

2. **PLM `0x80070005` from SSH.** `Add-AppxPackage` always fails from a
   non-interactive logon. `Install-OnQdc.ps1` detects this and falls back to
   `Add-AppxProvisionedPackage -Online` + `Add-AppxPackage -Register
   <PackageFamilyName>\AppxManifest.xml`. Register itself sometimes returns a
   spurious `0x80070005` but actually binds — the script retries up to 4× with
   3s sleeps and confirms success via `Get-AppxPackage`.

3. **Same-version rebuild blocked (`0x80073CFB`).** The provisioned copy from
   the prior deploy survives `Remove-AppxPackage` (which is user-scope), and
   the next deploy of a rebuilt MSIX with the same version is rejected with
   "same identity, different contents". Step `[3/4]` now also calls
   `Remove-AppxProvisionedPackage` to evict the machine-scope copy.

4. **OpenSSH on Windows quoting / PQ advisory.** PowerShell 5.1 strips
   embedded `"…"` when passing argv to native EXEs, so embedded `|` in remote
   commands gets reparsed by `cmd.exe`. Deploy script wraps remote PowerShell
   in base64 via `Invoke-RemotePwsh` (`-EncodedCommand <b64>`) and uses
   `-OutputFormat Text` to prevent CLIXML-format output. EAP is set to
   `Continue` at script top so OpenSSH's stderr advisories (post-quantum
   warning, "Permanently added to known hosts") don't become fatal
   `RemoteException`s.

5. **Host key churn between reservations.** Both reservations bind the same
   `localhost:2222`, but each maps to a different physical device with its
   own SSH host key. `UserKnownHostsFile=NUL` + `StrictHostKeyChecking=no` is
   not sufficient on Windows OpenSSH — it still flags REMOTE HOST IDENTIFICATION
   HAS CHANGED and disables `-L` port forwarding. The fix in `Get-SshArgs` /
   `Get-ScpArgs` / `Start-QdcRdpForward.ps1` is
   `-o NoHostAuthenticationForLocalhost=yes` on every ssh that targets
   `hcktest@localhost`.

6. **PowerShell 5.1 ANSI parser.** Remote machine has no `pwsh`, only
   `powershell.exe` 5.1. PS 5.1 reads `.ps1` files as ANSI when there is no
   BOM, so any non-ASCII bytes (em dash, curly quotes) in the install/validate
   scripts break parsing. Keep both scripts ASCII-only.

## Validation gates

`Validate-QdcDeployment.ps1` checks four things and exits non-zero if any fail
(except the last, which only warns):

| # | Check                          | Pass criterion                            |
| - | ------------------------------ | ----------------------------------------- |
| 1 | Package registered             | `Get-AppxPackage xiaocang.EasydictforWindows` returns a record |
| 2 | Signing cert trusted           | Cert with subject `CN=33FC47D7-...-297D1476BB29` in TrustedPeople (CurrentUser or LocalMachine) |
| 3 | OS build floor for Phi Silica  | Build ≥ 26100 (Windows 11 24H2)            |
| 4 | CPU arch (warn-only)           | ARM64 (Snapdragon X) — otherwise no NPU path |

Launch from a shell on the device:
```
explorer.exe shell:AppsFolder\xiaocang.EasydictforWindows_9vtdeamnnxqwy!App
```

## Files

```
dotnet/scripts/qdc/
├── Start-QdcTunnel.ps1            # localhost:2222 -> device:22 via jump host
├── Start-QdcRdpForward.ps1        # localhost:5555 -> device:3389 (nested in 2222)
├── Test-QdcConnection.ps1         # SSH probe + env dump
├── Deploy-ToQdc.ps1               # local orchestrator
├── Install-OnQdc.ps1              # runs on device — cert import + Appx install with PLM fallback
├── Validate-QdcDeployment.ps1     # runs on device — 4 gates above
└── deploy.md                      # this file
```

PEM is the only per-session local input you have to remember (download fresh
each reservation; the previous PEM rarely works). Device hostname changes
every reservation too — copy from the QDC portal "ssh" snippet.
