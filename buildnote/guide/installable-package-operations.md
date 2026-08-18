# Installable Package Operations Guide

This is the operational guide for generating, installing, upgrading, repairing, and uninstalling the Windows development installer for Automation Workbench.

Agents must read this guide before performing packaging or installer operations. Use a new version number for every generated package and preserve the user's AppData folders unless the user explicitly requests data removal.

## 1. Scope and safety rules

The installer contains the self-contained Windows release. It does not use the repository's `bin` folders at runtime and does not require the .NET SDK or Node.js on the target machine. The user-facing entry point is the WebView2 desktop shell; `ApiHost.exe` and the MCP executables are internal child processes.

The installed application is normally located at:

```text
C:\Program Files\Automation Workbench
```

The user-facing executable is:

```text
C:\Program Files\Automation Workbench\AutomationWorkbench.exe
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
- Launch the installed application through `AutomationWorkbench.exe`; do not use `ApiHost.exe` as the desktop shortcut target.
- The packaged shell starts the backend without a console window and displays Studio in an embedded WebView2 window. It must not open Chrome.
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

Run both scripts from the repository root, in this order. Use PowerShell 7 (`pwsh`), not Windows PowerShell 5.1 — the release script uses .NET APIs such as `Path.GetRelativePath` that do not exist on the .NET Framework runtime:

```powershell
Set-Location 'C:\Users\Ansel\orca\projects\AgentAssistPlcDev'

pwsh -NoProfile -File .\scripts\build-release.ps1 -Version 0.1.0-dev.3
pwsh -NoProfile -File .\scripts\build-installer.ps1 -Version 0.1.0-dev.3
```

If the machine routes traffic through a local HTTP proxy (for example `HTTP_PROXY` environment variables or a system proxy on `127.0.0.1`), clear those variables and set `DOTNET_SYSTEM_NET_HTTP_USEPROXY=false` for the build session. Otherwise the loopback health checks in the release test run (such as the live app-assistant sidecar test) are routed to the proxy and fail even though the services are healthy.

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

The release also includes the optional `agent-service` source under the install
root. It does not bundle Python or third-party Python packages. To enable the
Workbench App Assistant on a machine, install Python 3.13, install the service
dependencies from the installed `agent-service` directory, and set these user or
machine environment variables before launching the desktop shell:

```powershell
py -3.13 -m pip install -e 'C:\Program Files\Automation Workbench\agent-service'
[Environment]::SetEnvironmentVariable('AUTOMATION_WORKBENCH_APP_ASSISTANT_ENABLED', 'true', 'User')
[Environment]::SetEnvironmentVariable('DEEPSEEK_API_KEY', '<key>', 'User')
```

The shell passes the ApiHost URL and a writable user-local data directory to the
sidecar. If the sidecar cannot start, ApiHost and the existing PLC AgentLoop stay
available; inspect `%LOCALAPPDATA%\AutomationWorkbench\logs\backend.log` for the
diagnostic.

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
    ShellPresent = Test-Path "$root\AutomationWorkbench.exe"
    ApiHostPresent = Test-Path "$root\ApiHost.exe"
    Version = if (Test-Path "$root\ApiHost.exe") {
        (Get-Item "$root\ApiHost.exe").VersionInfo.ProductVersion
    }
}
```

Expected result: exit code `0`, `ShellPresent = True`, `ApiHostPresent = True`, and the requested package version.

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

## 5.1 Desktop shell smoke test

Run the installed shell from an elevated test session after confirming that port `5239` is free:

```powershell
$shell = Start-Process "$root\AutomationWorkbench.exe" -WorkingDirectory $root -PassThru
Start-Sleep 10
$status = Invoke-RestMethod 'http://127.0.0.1:5239/api/status'
$status

[pscustomobject]@{
    ShellProcessRunning = -not $shell.HasExited
    ApiHostRunning = @(Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -eq "$root\ApiHost.exe"
    }).Count -eq 1
    ShellWindow = Test-Path "$root\AutomationWorkbench.exe"
}
```

Expected result: an Automation Workbench window displays the Studio UI, the API reports the requested version, no console window is visible, and Chrome is not opened. The window has no visible native title bar; the Studio header acts as the caption — dragging empty header space moves the window, double-clicking it toggles maximize, and the minimize, maximize/restore, and close buttons sit at the right end of the header, immediately right of the theme (dark mode) toggle. The taskbar entry reads "Automation Workbench". Exercise the three in-app window buttons and confirm minimize, maximize/restore, and close all work.

Also verify the custom-chrome window behaviors in restored (non-maximized) mode:

- The restored window defaults to a size that fits the whole Studio layout, including the bottom status bar, without scrolling; the user can shrink it afterwards.
- Moving the pointer to any window edge or corner shows the matching resize cursor, and dragging resizes the window.
- On Windows 11 the window shows rounded corners and a subtle border line.
- Windows snap assist works: dragging the window to the left or right screen edge shows the snap preview, and Automation Workbench appears in the snap-suggestion list for the remaining half so another app (such as TIA Portal) can be placed beside it.

Closing the window must stop the shell-owned `ApiHost.exe` and MCP child processes.

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
pwsh -NoProfile -File .\scripts\build-installer.ps1 `
    -Version 0.1.0-dev.3 `
    -InnoSetupPath 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
```

### Release script fails with `Path.GetRelativePath` not found

The script was started with Windows PowerShell 5.1 (`powershell.exe`). Rerun it with PowerShell 7 (`pwsh.exe`).

### Release tests fail on loopback health checks while the services are healthy

A local HTTP proxy (`HTTP_PROXY` environment variables or an enabled system proxy) is intercepting the test runner's `127.0.0.1` requests. Clear the proxy variables and set `DOTNET_SYSTEM_NET_HTTP_USEPROXY=false`, then rerun the release build.

### Silent install exits with code 2 and installs nothing

The installer requires elevation (`PrivilegesRequired=admin`). Run the setup from an elevated session; a non-elevated `/VERYSILENT` run aborts before copying files.

### `npm ci` fails with `EPERM: operation not permitted, unlink ... .node`

A leftover Vite/Node process from a previous dev run still holds `studio\node_modules` open. Stop processes whose command line references the checkout (ApiHost, `Mcp.*`, `node`, and their wrappers), wait a few seconds, and rerun the release build.

### `AutomationWorkbench.exe` cannot start

Check `%LOCALAPPDATA%\AutomationWorkbench\logs\backend.log`. Confirm that the WebView2 Runtime is installed and that port `5239` is free. The shell starts `ApiHost.exe` with its working directory set to the installation root and passes `Application:OpenBrowserOnStart=false`.

### `ApiHost.exe` cannot start

Check that the working directory exists and that the process was started with the installation root as `-WorkingDirectory`. Check for a port collision:

```powershell
Get-NetTCPConnection -LocalPort 5239, 5255 -ErrorAction SilentlyContinue |
    Select-Object LocalAddress, LocalPort, OwningProcess
```

Use another free port for a smoke test rather than stopping an unrelated process.

### Installation files cannot be removed

An installed shell, `ApiHost`, or MCP process is still holding files open. Stop only processes whose `ExecutablePath` is beneath the exact installation or test root, wait a few seconds, and then run the uninstaller again. Do not force-delete the directory while files are locked.

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
- desktop shell window, hidden-process, WebView2, and in-app window-controls (minimize, maximize/restore, close, header drag, edge resize, snap assist) result;
- upgrade result, if performed;
- repair result, if performed;
- uninstall result and user-data preservation result;
- TIA V17 and whitelist result, if the machine has TIA V17;
- any deferred validation or environment limitation.

The current repository baseline is an installable development environment. Additional production-hardening, signing, broader machine-matrix testing, and deferred .NET 10 work remain separate future activities unless explicitly requested.
