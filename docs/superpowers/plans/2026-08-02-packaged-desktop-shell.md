# Packaged Desktop Shell Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the installed Automation Workbench behave like a Windows desktop application with a dedicated app window, no visible backend console, no Chrome launch, and clean backend shutdown.

**Architecture:** Add a small WinForms/WebView2 shell executable that starts the existing `ApiHost.exe` as a hidden child process, waits for its loopback health endpoint, and displays the existing Studio UI in an embedded WebView2 window. The shell owns startup, diagnostics, and shutdown; the ASP.NET backend remains the application server and continues to host the compiled Studio assets.

**Tech Stack:** .NET 8, WinForms, Microsoft WebView2, ASP.NET Core/Kestrel, Inno Setup 6, existing self-contained `win-x64` release pipeline.

---

## File map

Create:

- `src/AutomationWorkbench.Desktop/AutomationWorkbench.Desktop.csproj` — Windows desktop shell project targeting `net8.0-windows` and publishing as a GUI executable.
- `src/AutomationWorkbench.Desktop/Program.cs` — single-instance startup, backend startup, and main-form lifetime.
- `src/AutomationWorkbench.Desktop/MainWindow.cs` — WinForms window and WebView2 navigation.
- `src/AutomationWorkbench.Desktop/BackendProcessHost.cs` — hidden `ApiHost.exe` process lifecycle, health polling, log capture, and graceful shutdown.
- `src/AutomationWorkbench.Desktop/RuntimePaths.cs` — installed paths, loopback URL, log path, and WebView2 user-data path.
- `tests/AutomationWorkbench.Desktop.Tests/AutomationWorkbench.Desktop.Tests.csproj` — desktop-shell unit test project.
- `tests/AutomationWorkbench.Desktop.Tests/BackendProcessHostTests.cs` — deterministic process-start, argument, health timeout, and shutdown tests.
- `assets/AutomationWorkbench.ico` — installer and shell icon.

Modify:

- `AgentAssistPlcDev.sln` — add the desktop shell and desktop test project.
- `src/ApiHost/Program.cs` — add a token-protected local shutdown endpoint and retain browser launch only for explicit development/direct-server use.
- `src/ApiHost/ApplicationStartupOptions.cs` — add the shutdown-token configuration value used by the shell.
- `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs` or a new `tests/ApiHost.Tests/LifecycleEndpointsTests.cs` — verify shutdown authorization and accepted shutdown behavior.
- `src/ApiHost/appsettings.json` — set installed/direct production startup to `OpenBrowserOnStart: false`.
- `scripts/build-release.ps1` — build desktop tests, publish the shell, copy it into the release root, require it in release verification, and record its target framework.
- `installer/AutomationWorkbench.iss` — install the shell icon, create shortcuts to the shell, and make the optional post-install launch task start the shell.
- `buildnote/guide/installable-package-operations.md` — document the shell executable, WebView2 prerequisite, hidden backend, and desktop smoke test.

## Task 1: Add the shell project and its Windows dependencies

**Files:** create `src/AutomationWorkbench.Desktop/AutomationWorkbench.Desktop.csproj`, modify `AgentAssistPlcDev.sln`.

- [ ] Create a WinForms project with these properties:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>AutomationWorkbench</AssemblyName>
    <RootNamespace>AutomationWorkbench.Desktop</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Web.WebView2" Version="1.0.4078.44" />
  </ItemGroup>

  <ItemGroup>
    <None Include="..\..\assets\AutomationWorkbench.ico" Link="AutomationWorkbench.ico" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

  Pin one WebView2 SDK version in the project file and use the same version for all subsequent builds. The implementation must verify that the installed Evergreen WebView2 Runtime is available at startup; do not silently fall back to Chrome.

- [ ] Add the project to the `src` solution folder and add a desktop test project under `tests`.
- [ ] Run `dotnet restore AgentAssistPlcDev.sln --runtime win-x64` and confirm the shell restores on the supported Windows build environment.

## Task 2: Implement runtime paths and hidden backend ownership

**Files:** create `src/AutomationWorkbench.Desktop/RuntimePaths.cs`, create `src/AutomationWorkbench.Desktop/BackendProcessHost.cs`, test `tests/AutomationWorkbench.Desktop.Tests/BackendProcessHostTests.cs`.

- [ ] Define a runtime-path object rooted at `AppContext.BaseDirectory`:

```csharp
public sealed record RuntimePaths(string InstallRoot, string ApiHostPath, string BaseUrl, string LogDirectory)
{
    public string BackendLogPath => Path.Combine(LogDirectory, "backend.log");
    public string WebViewUserDataPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AutomationWorkbench", "WebView2");
}
```

- [ ] Implement `BackendProcessHost.StartAsync` so it:

  - creates `%LOCALAPPDATA%\AutomationWorkbench\logs` without deleting existing files;
  - generates a per-shell random shutdown token;
  - starts `ApiHost.exe` with `UseShellExecute = false`, `CreateNoWindow = true`, `WindowStyle = Hidden`, and redirected stdout/stderr;
  - passes `--Application:OpenBrowserOnStart false`, `--Application:ShutdownToken <token>`, and the existing port `5239` arguments;
  - drains both redirected streams into `backend.log` so hidden startup failures remain diagnosable;
  - polls `http://127.0.0.1:5239/api/status` every 250 ms for at most 30 seconds;
  - fails with a user-readable exception containing the log path if the process exits or health never becomes ready.

- [ ] Implement `StopAsync` so it first posts to the local shutdown endpoint with the token, waits up to 5 seconds for exit, then uses `Kill(entireProcessTree: true)` only as a fallback. Never terminate processes by name; only operate on the `ApiHost.exe` process object started by this shell.
- [ ] Make the coordinator disposable and cancellation-aware so closing the window cannot leave a hidden backend running.
- [ ] Unit-test the exact process settings and arguments, health timeout behavior, log-directory creation, and graceful-shutdown fallback through injected process/HTTP abstractions. Do not launch the real installed backend from unit tests.

## Task 3: Add the protected backend shutdown endpoint

**Files:** modify `src/ApiHost/ApplicationStartupOptions.cs`, `src/ApiHost/Program.cs`; create or modify `tests/ApiHost.Tests/LifecycleEndpointsTests.cs`.

- [ ] Add `Application:ShutdownToken` to startup configuration. Keep it optional so direct development launches continue to work without a shell token.
- [ ] Add a POST endpoint before the general API mappings:

```csharp
app.MapPost("/api/lifecycle/shutdown", (
    HttpRequest request,
    IConfiguration configuration,
    IHostApplicationLifetime lifetime) =>
{
    var expected = configuration["Application:ShutdownToken"];
    var supplied = request.Headers["X-AutomationWorkbench-Shutdown-Token"].ToString();
    if (string.IsNullOrWhiteSpace(expected)
        || !CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(supplied)))
        return Results.Unauthorized();

    lifetime.StopApplication();
    return Results.Accepted();
});
```

  Use the existing loopback-only production binding. The token is a coordination guard against accidental shutdown requests, not a substitute for OS-level security.

- [ ] Add tests for missing token (`401`), wrong token (`401`), correct token (`202`), and the fact that the endpoint does not exist as a browser GET route.
- [ ] Keep `BrowserLauncher` available for explicit development/direct-server use, but ensure the shell always passes `OpenBrowserOnStart=false`.

## Task 4: Build the native app window

**Files:** create `src/AutomationWorkbench.Desktop/Program.cs`, `src/AutomationWorkbench.Desktop/MainWindow.cs`.

- [ ] Configure the shell as a GUI application with the title `Automation Workbench`, the new icon, DPI-aware sizing, and a sensible default client area such as 1440×900 with a minimum of 960×640.
- [ ] Acquire a named mutex such as `Local\AutomationWorkbench` before creating the window. If another shell instance owns it, exit without starting a second backend on port `5239`.
- [ ] Start `BackendProcessHost` before creating the WebView2 control. Once health succeeds, create the WebView2 environment with the user-data folder under `%LOCALAPPDATA%\AutomationWorkbench\WebView2` and navigate to the shell base URL.
- [ ] Handle missing WebView2 Runtime with a native message box that explains the prerequisite and points the user to the Microsoft WebView2 Runtime installation page. Do not open Chrome as a fallback.
- [ ] Handle backend startup failure with a native message box containing the backend log path. Do not show a console window.
- [ ] On `FormClosing`, cancel navigation, await `BackendProcessHost.StopAsync`, release the mutex, and then exit. Ensure the form remains responsive while the shutdown request is in flight.
- [ ] Avoid exposing arbitrary external navigation: allow only the configured `http://127.0.0.1:5239/` origin and open any external links through the user’s default browser only when the user explicitly clicks them.

## Task 5: Update release assembly and installer entry points

**Files:** modify `scripts/build-release.ps1`, `installer/AutomationWorkbench.iss`, `src/ApiHost/appsettings.json`, add `assets/AutomationWorkbench.ico`.

- [ ] Publish the shell self-contained for `win-x64` into the existing release staging directory, then copy it into the release root as `AutomationWorkbench.exe`.
- [ ] Add the desktop test project to the release test-project list.
- [ ] Add `AutomationWorkbench.exe` to required release files and set the manifest target framework to `net8.0-windows`.
- [ ] Set the production appsettings value to:

```json
"Application": {
  "Host": "127.0.0.1",
  "Port": 5239,
  "OpenBrowserOnStart": false
}
```

- [ ] Change both Start Menu and desktop shortcuts from `{app}\ApiHost.exe` to `{app}\AutomationWorkbench.exe`.
- [ ] Change the optional launch task to run `{app}\AutomationWorkbench.exe` after installation.
- [ ] Keep `ApiHost.exe`, MCP servers, and the whitelist helper as internal installed files; do not create user-facing shortcuts to them.
- [ ] Preserve all existing installer behavior for TIA whitelist registration, application closing during upgrade, and user-data directories.
- [ ] Add a clear installer prerequisite note for the Evergreen WebView2 Runtime, and ensure a missing runtime produces the shell’s native diagnostic rather than a browser launch.

## Task 6: Add tests and development documentation

**Files:** modify `tests/AutomationWorkbench.Desktop.Tests/BackendProcessHostTests.cs`, `tests/ApiHost.Tests/LifecycleEndpointsTests.cs`, `buildnote/guide/installable-package-operations.md`, optionally `launch.ps1`.

- [ ] Add a desktop-shell test that verifies the shell passes `Application:OpenBrowserOnStart=false` and never invokes `ProcessStartInfo.UseShellExecute=true` for the app URL.
- [ ] Keep `launch.ps1` as the developer workflow, but document that it may continue to run the backend and browser/dev server for development. The packaged shell behavior is only for release builds.
- [ ] Update the installer guide with:

  - installed entry point: `AutomationWorkbench.exe`;
  - backend location and hidden-process behavior;
  - WebView2 Runtime prerequisite;
  - log path `%LOCALAPPDATA%\AutomationWorkbench\logs\backend.log`;
  - clean-install expectation: no visible backend console and no Chrome window;
  - shutdown expectation: closing the app leaves no package-owned `ApiHost` or MCP processes.

## Task 7: Verify with the full packaging workflow

**Files:** no source changes; use the existing guide and release scripts.

- [ ] Run the complete release build and confirm all existing .NET and Studio tests pass, including the new desktop tests.
- [ ] Run the installer build with a new semantic version, then verify the installer and `.sha256` file.
- [ ] From an elevated PowerShell session, install into `C:\Temp\Automation Workbench Desktop Test` using `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`.
- [ ] Verify:

  - installer exit code is `0`;
  - `AutomationWorkbench.exe` exists and starts a native app window;
  - no visible console window appears for `ApiHost.exe` or MCP children;
  - Chrome does not open;
  - the WebView2 window loads the Studio UI and `/api/status` reports the package version;
  - closing the window removes the shell-owned backend process tree;
  - existing `%APPDATA%\AutomationWorkbench`, `%APPDATA%\PlcAiAssistant`, `%LOCALAPPDATA%\AutomationWorkbench`, and `%LOCALAPPDATA%\PlcAiAssistant` contents remain unchanged.

- [ ] Run repair validation by removing only `AutomationWorkbench.exe` from the temporary install root, rerunning the same installer, and confirming the shell is restored.
- [ ] Run uninstall validation using `unins000.exe`, confirming the temporary install root is removed and all user-data sentinels remain.
- [ ] Run the TIA V17 whitelist verification only on a machine with TIA V17 installed and elevated; do not terminate TIA Portal during cleanup.
- [ ] Record source commit, package version, installer hash, test results, WebView2 runtime status, and any deferred validation in the packaging completion record.

## Self-review

- The visible console problem is addressed by making the shell the only user-facing executable and launching `ApiHost.exe` with `CreateNoWindow=true`.
- The Chrome problem is addressed by passing `OpenBrowserOnStart=false` and navigating WebView2 directly to the loopback origin.
- Backend cleanup is covered by a token-protected graceful endpoint plus process-tree fallback.
- Installer shortcuts, release assembly, tests, user-data preservation, repair, uninstall, and TIA whitelist behavior are all covered.
- The only external runtime dependency is WebView2; the plan includes detection and a user-facing diagnostic rather than an implicit browser fallback.
