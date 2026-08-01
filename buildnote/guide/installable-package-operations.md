# Installable Package Operations Guide

This is the operational guide for generating, installing, upgrading, repairing, and uninstalling the Windows development installer for Automation Workbench.

Agents must read this guide before performing packaging or installer operations. Use a new version number for every generated package and preserve the user's AppData folders unless the user explicitly requests data removal.

## 1. Scope and safety rules

The installer contains the self-contained Windows release. It does not use the repository's `bin` folders at runtime and does not require the .NET SDK or Node.js on the target machine.

The installed application is normally located at:

```text
C:\Program Files\Automation Workbench
```

The installer does not own or delete user data. Treat these locations as user data and do not remove them during package testing or uninstall:

```text
%APPDATA%\AutomationWorkbench
%APPDATA%\PlcAiAssistant
%LOCALAPPDATA%\AutomationWorkbench
%LOCALAPPDATA%\PlcAiAssistant
```

Important runtime rules:

- Do not run the installed and development `ApiHost` processes on the same default port (`5239`) at the same time. Use a separate development port such as `5255` for smoke tests.
- Do not open the same workbench root concurrently from installed and development environments. SQLite databases, Git worktrees, exports, and staging files are workbench data.
- Stop only processes belonging to the installation or test root being operated on. Do not kill unrelated TIA Portal or `ApiHost` processes.
- Use the uninstaller for removal. Do not manually delete `C:\Program Files\Automation Workbench` while application processes are running.
- Do not use broad recursive deletion against `%APPDATA%`, `%LOCALAPPDATA%`, `C:\Program Files`, a home directory, or a repository root.

## 2. Choose the source checkout and version

Build from the checkout containing the intended `master` commit. Confirm the branch and commit before building:

```powershell
Set-Location 'C:\Users\Ansel\orca\projects\AgentAssistPlcDev'

git branch --show-current
git rev-parse --short HEAD
git status --short
```

The expected branch is `master`. The working tree should normally be clean. If it is not clean, stop and determine whether the changes are intentional before packaging.

Choose a new semantic version. For example, after `0.1.0-dev.2`, use `0.1.0-dev.3`:

```powershell
$version = '0.1.0-dev.3'
```

Do not reuse an older package version for a new codebase state.

## 3. Generate the release and installer

Run both scripts from the repository root, in this order:

```powershell
Set-Location 'C:\Users\Ansel\orca\projects\AgentAssistPlcDev'

.\scripts\build-release.ps1 -Version 0.1.0-dev.3
.\scripts\build-installer.ps1 -Version 0.1.0-dev.3
```

`build-release.ps1` performs the release build and verification, including solution tests and the Studio frontend checks. It rebuilds:

```text
artifacts\release\win-x64
```

`build-installer.ps1` packages that release with Inno Setup and creates:

```text
artifacts\installer\AutomationWorkbench-0.1.0-dev.3-win-x64-setup.exe
artifacts\installer\AutomationWorkbench-0.1.0-dev.3-win-x64-setup.exe.sha256
```

The build requires the .NET 8 SDK, Node/npm, Git, and Inno Setup 6. The current packaging baseline intentionally remains on the existing framework targets; do not upgrade it to .NET 10 as part of routine package generation.

Verify the outputs before installing:

```powershell
$installer = 'C:\Users\Ansel\orca\projects\AgentAssistPlcDev\artifacts\installer\AutomationWorkbench-0.1.0-dev.3-win-x64-setup.exe'
$hashFile = "$installer.sha256"

Test-Path $installer
Test-Path $hashFile
Get-FileHash $installer -Algorithm SHA256
Get-Content $hashFile
```

The installer script must not be run by itself unless `artifacts\release\win-x64` was already produced by the matching release build.

## 4. Clean-install smoke test in a temporary directory

Use a temporary install root for package validation so that the production installation is not disturbed:

```powershell
$root = 'C:\Temp\Automation Workbench Installer Test'
$installer = 'C:\Users\Ansel\orca\projects\AgentAssistPlcDev\artifacts\installer\AutomationWorkbench-0.1.0-dev.3-win-x64-setup.exe'

if (Test-Path "$root\unins000.exe") {
    & "$root\unins000.exe" /VERYSILENT /NORESTART
    Start-Sleep 3
}

Get-CimInstance Win32_Process |
    Where-Object { $_.ExecutablePath -like "$root\*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

Start-Sleep 2
if (Test-Path $root) {
    Remove-Item -LiteralPath $root -Recurse -Force
}

$dirArg = "/DIR=\"$root\""
$install = Start-Process -FilePath $installer `
    -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', $dirArg) `
    -Wait -PassThru

[pscustomobject]@{
    InstallerExitCode = $install.ExitCode
    ApiHostPresent = Test-Path "$root\ApiHost.exe"
    Version = if (Test-Path "$root\ApiHost.exe") {
        (Get-Item "$root\ApiHost.exe").VersionInfo.ProductVersion
    }
}
```

Expected result: exit code `0`, `ApiHostPresent = True`, and the requested package version.

## 5. API smoke test

Use a non-default port if another development or installed `ApiHost` is running:

```powershell
$api = Start-Process "$root\ApiHost.exe" `
    -WorkingDirectory $root `
    -ArgumentList '--Application:Port', '5255', '--Application:OpenBrowserOnStart', 'false' `
    -WindowStyle Hidden -PassThru

Start-Sleep 10
$status = Invoke-RestMethod 'http://127.0.0.1:5255/api/status'
$status

Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue
```

Expected result: HTTP status `200`, with the requested application version. If the request fails, check that `ApiHost.exe` exists, that the selected port is free, and that the process was started with `$root` as its working directory.

## 6. Upgrade test

For a real upgrade test, use two different package versions and the same temporary install root. The first installer establishes the installation; the second replaces the binaries in place.

```powershell
$root = 'C:\Temp\Automation Workbench Upgrade Test'
$a = 'C:\Users\Ansel\orca\projects\AgentAssistPlcDev\artifacts\installer\AutomationWorkbench-0.1.0-dev.1-win-x64-setup.exe'
$b = 'C:\Users\Ansel\orca\projects\AgentAssistPlcDev\artifacts\installer\AutomationWorkbench-0.1.0-dev.2-win-x64-setup.exe'

Get-CimInstance Win32_Process |
    Where-Object { $_.ExecutablePath -like "$root\*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

Start-Sleep 3
if (Test-Path $root) {
    Remove-Item -LiteralPath $root -Recurse -Force
}

$dirArg = "/DIR=\"$root\""
Start-Process -FilePath $a -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', $dirArg) -Wait

$api = Start-Process "$root\ApiHost.exe" `
    -WorkingDirectory $root `
    -ArgumentList '--Application:Port', '5255', '--Application:OpenBrowserOnStart', 'false' `
    -WindowStyle Hidden -PassThru
Start-Sleep 10
Invoke-RestMethod 'http://127.0.0.1:5255/api/status'

# The installer is configured to close applications during replacement.
$upgrade = Start-Process -FilePath $b `
    -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', $dirArg) `
    -Wait -PassThru

[pscustomobject]@{
    UpgradeExitCode = $upgrade.ExitCode
    ApiHostVersion = (Get-Item "$root\ApiHost.exe").VersionInfo.ProductVersion
}
```

Expected result: exit code `0`, the previous `ApiHost` is stopped by the upgrade, and the installed executable reports the new version. Repeat the upgrade cycle if validating locking behavior; the established packaging validation used five consecutive successful cycles.

After the upgrade, start the new `ApiHost` and repeat the API status check. Verify that a sentinel file placed in a user-data directory or a custom workbench root remains present. Do not place the sentinel inside the installation directory because that directory is expected to be replaced.

## 7. Repair test

Repair is an in-place reinstall of the same package. Use a temporary root for destructive validation:

1. Install the package into the temporary root.
2. Stop all processes whose executable path begins with that root.
3. Remove a test installation binary such as `$root\ApiHost.exe`.
4. Run the same installer again against the same `/DIR`.
5. Confirm the missing binary is restored and the API starts.

Do not delete user-data directories to simulate a repair. A successful repair restores installation files while preserving user data and whitelist state.

## 8. TIA V17 and whitelist verification

Engineering integration requires TIA Portal V17. Run the following from an elevated PowerShell session after installation:

```powershell
$root = 'C:\Program Files\Automation Workbench'
$helper = "$root\tools\AutomationWorkbench.OpennessWhitelist.exe"
$engineering = "$root\mcp\engineering\Mcp.Engineering.exe"

& $helper register --exe $engineering
$registerExit = $LASTEXITCODE

& $helper verify --exe $engineering
$verifyExit = $LASTEXITCODE

[pscustomobject]@{
    RegisterExitCode = $registerExit
    VerifyExitCode = $verifyExit
}
```

Expected result is `0` for both commands. If the helper is not found, verify the installation root and confirm that the package contains `tools\AutomationWorkbench.OpennessWhitelist.exe`. If registration fails, rerun from an elevated session and check that the TIA V17 Openness registry key exists.

For real integration validation, open the known TIA V17 test project, perform the tested export-block workflow, and confirm the exported files and API status. Do not terminate TIA Portal as part of cleanup.

## 9. Uninstall test

Stop processes belonging to the installation before invoking the uninstaller:

```powershell
$root = 'C:\Temp\Automation Workbench Upgrade Test'

Get-CimInstance Win32_Process |
    Where-Object { $_.ExecutablePath -like "$root\*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

Start-Sleep 3

if (Test-Path "$root\unins000.exe") {
    $uninstall = Start-Process -FilePath "$root\unins000.exe" `
        -ArgumentList @('/VERYSILENT', '/NORESTART') `
        -Wait -PassThru
}

[pscustomobject]@{
    UninstallExitCode = if ($uninstall) { $uninstall.ExitCode } else { $null }
    InstallRootRemoved = -not (Test-Path $root)
}
```

Expected result: exit code `0` and the installation root is removed. The uninstaller also attempts to remove the installed engineering executable from the TIA whitelist. User-data directories must remain. Verify this with sentinels before and after uninstall:

```powershell
Test-Path "$env:APPDATA\AutomationWorkbench"
Test-Path "$env:APPDATA\PlcAiAssistant"
Test-Path "$env:LOCALAPPDATA\AutomationWorkbench"
Test-Path "$env:LOCALAPPDATA\PlcAiAssistant"
```

The directories may be absent on a clean machine; the important rule is that an existing directory and its contents are not removed by uninstall.

## 10. Troubleshooting checklist

### Installer build fails with an Inno Setup error

Confirm that Inno Setup 6 is installed and that `ISCC.exe` is available. The build script searches the command path and these locations:

```text
C:\Program Files (x86)\Inno Setup 6\ISCC.exe
C:\Program Files\Inno Setup 6\ISCC.exe
```

If required, provide the compiler explicitly:

```powershell
.\scripts\build-installer.ps1 `
    -Version 0.1.0-dev.3 `
    -InnoSetupPath 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
```

### `ApiHost.exe` cannot start

Check that the working directory exists and that the process was started with the installation root as `-WorkingDirectory`. Check for a port collision:

```powershell
Get-NetTCPConnection -LocalPort 5239, 5255 -ErrorAction SilentlyContinue |
    Select-Object LocalAddress, LocalPort, OwningProcess
```

Use another free port for a smoke test rather than stopping an unrelated process.

### Installation files cannot be removed

An installed `ApiHost` or MCP process is still holding files open. Stop only processes whose `ExecutablePath` is beneath the exact installation or test root, wait a few seconds, and then run the uninstaller again. Do not force-delete the directory while files are locked.

### Whitelist helper returns a nonzero code

Use an elevated PowerShell session, confirm the helper and engineering executable paths, and rerun `register` followed by `verify`. A successful `verify` confirms the final whitelist state, but a failed registration should still be investigated and recorded.

## 11. Completion record

For each package intended for distribution or pre-production testing, record:

- source branch and commit;
- package version;
- release and installer output paths;
- release test result;
- installer SHA-256;
- clean-install API status;
- upgrade result, if performed;
- repair result, if performed;
- uninstall result and user-data preservation result;
- TIA V17 and whitelist result, if the machine has TIA V17;
- any deferred validation or environment limitation.

The current repository baseline is an installable development environment. Additional production-hardening, signing, broader machine-matrix testing, and deferred .NET 10 work remain separate future activities unless explicitly requested.
