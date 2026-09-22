param(
    [switch]$NoBuild,
    [switch]$NoKill,
    [switch]$Help
)

if ($Help) {
    Write-Host @"
Usage: .\launch.ps1 [-NoBuild] [-NoKill]

Rebuilds and launches the full stack:
  - ApiHost (ASP.NET backend, port 5239)
  - Studio (Vite React frontend, port 5173)

Options:
  -NoBuild    Skip the dotnet build step (start faster when code is already compiled)
  -NoKill     Don't kill existing ApiHost / node processes before starting
"@
    return
}

$root = $PSScriptRoot

function Test-StudioDependenciesReady {
    param(
        [Parameter(Mandatory = $true)]
        [string]$StudioRoot
    )

    $packageLock = Join-Path $StudioRoot "package-lock.json"
    $installedLock = Join-Path $StudioRoot "node_modules\.package-lock.json"
    $viteCommand = Join-Path $StudioRoot "node_modules\.bin\vite.cmd"
    if ((-not (Test-Path -LiteralPath $packageLock -PathType Leaf)) -or
        (-not (Test-Path -LiteralPath $installedLock -PathType Leaf)) -or
        (-not (Test-Path -LiteralPath $viteCommand -PathType Leaf))) {
        return $false
    }

    try {
        $checkedIn = Get-Content -Raw -LiteralPath $packageLock | ConvertFrom-Json -AsHashtable
        $installed = Get-Content -Raw -LiteralPath $installedLock | ConvertFrom-Json -AsHashtable
        foreach ($property in @("name", "lockfileVersion")) {
            if ($checkedIn[$property] -ne $installed[$property]) {
                return $false
            }
        }
        return $true
    } catch {
        return $false
    }
}

function Install-StudioDependenciesIfNeeded {
    param(
        [Parameter(Mandatory = $true)]
        [string]$StudioRoot
    )

    if (Test-StudioDependenciesReady -StudioRoot $StudioRoot) {
        return
    }

    Write-Host ">>> Installing Studio dependencies (npm ci)..." -ForegroundColor Cyan
    Push-Location $StudioRoot
    try {
        & npm.cmd ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) {
            Write-Host "!!! Studio dependency installation failed." -ForegroundColor Red
            exit 1
        }
    } finally {
        Pop-Location
    }

    if (-not (Test-StudioDependenciesReady -StudioRoot $StudioRoot)) {
        Write-Host "!!! Studio dependencies are still incomplete after npm ci." -ForegroundColor Red
        exit 1
    }
}

# 1. Kill lingering processes from the previous run (unless flagged off)
#    Must happen BEFORE build to release locked binaries.
if (-not $NoKill) {
    Write-Host ">>> Cleaning up old processes..." -ForegroundColor Cyan
    # Kill ApiHost directly (it shows as process name "ApiHost", not "dotnet")
    Get-Process -Name "ApiHost" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    # Also catch instances hosted via "dotnet ApiHost.dll" (process name is
    # "dotnet"), otherwise they keep port 5239 bound and the new backend
    # fails to start with "address already in use".
    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match 'ApiHost\.dll' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    # Kill only THIS project's Vite dev server, identified by its command line, never
    # every node.exe. A blanket "Get-Process node | Stop-Process" also kills any other
    # Node application the user is running. The project-root path alone is not a safe
    # filter either: an agent harness whose workspace is this repository carries the
    # path in its command line, so a root-only match takes the harness - and the
    # terminal that started this script - down with the dev server.
    #
    # Requiring "vite" as well is still not sufficient. The harness runs each command
    # through a local subprocess runner whose command line contains both the workspace
    # path and the full text of the command being executed, so any command that merely
    # mentions vite - reading the Vite config, listing its processes - satisfies both
    # conditions and is killed mid-command. Two such runners were observed matching the
    # root-plus-vite rule.
    #
    # Match the Vite entry point itself, and exclude the harness runner outright.
    # Verified against the live process list: this still matches the dev server
    # (`node ...\vite\bin\vite.js --host`) and leaves every
    # `@deepseek-ai/dsh-subprocess-local/lib/runner.js` process alone.
    $projectRootPattern = [regex]::Escape($root)
    Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            $_.CommandLine -and
            $_.CommandLine -match $projectRootPattern -and
            $_.CommandLine -match 'vite[\\/]bin[\\/]vite' -and
            $_.CommandLine -notmatch 'dsh-subprocess-local|@deepseek-ai|dsh[\\/]lib'
        } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    foreach ($name in @("Mcp.Engineering", "Mcp.Knowledge", "Mcp.VersionControl")) {
        Get-Process -Name $name -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep 1
}

# 2. Build (unless flagged off)
if (-not $NoBuild) {
    Write-Host ">>> Building solution..." -ForegroundColor Cyan
    dotnet build "$root\AgentAssistPlcDev.sln" -v q
    if ($LASTEXITCODE -ne 0) {
        Write-Host "!!! Build failed - fix errors before launching." -ForegroundColor Red
        exit 1
    }
    Write-Host "    Build succeeded." -ForegroundColor Green

    # 2b. Register TIA Openness whitelist (post-build generates the .reg; elevated prompt = auto-merge)
    $regFile = "$root\src\Mcp.Engineering\bin\Debug\net48\register-whitelist.reg"
    if (Test-Path $regFile) {
        Write-Host ">>> Registering TIA Openness whitelist..." -ForegroundColor Cyan
        $null = & reg.exe import $regFile 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host "    Whitelist merged - TIA firewall prompt suppressed." -ForegroundColor Green
        } else {
            Write-Host "    Whitelist merge skipped (already current or not needed)." -ForegroundColor Gray
        }
    }
}

# 3. Launch ApiHost in a new window
#    The frontend is launched separately below, so don't ask ApiHost to open a
#    browser. This also keeps the backend alive in environments where Windows
#    cannot ShellExecute an http:// URL from a child process.
Write-Host ">>> Starting ApiHost (port 5239)..." -ForegroundColor Cyan
$apiProject = Join-Path $root "src\ApiHost\ApiHost.csproj"
$apiArguments = @("run")
if ($NoBuild) {
    $apiArguments += "--no-build"
}
$apiArguments += @("--project", $apiProject, "--", "Application:OpenBrowserOnStart=false")
Start-Process `
    -WindowStyle Normal `
    -WorkingDirectory $root `
    -FilePath "dotnet" `
    -ArgumentList $apiArguments

# 4. The Workbench Assistant is served in-process by ApiHost (ADR-0005):
#    there is no sidecar to start and no assistant checkpoint state to reset.

# 5. Launch Studio Vite dev server in a new window
Write-Host ">>> Starting Studio (port 5173)..." -ForegroundColor Cyan
$studioRoot = Join-Path $root "studio"
Install-StudioDependenciesIfNeeded -StudioRoot $studioRoot
Start-Process `
    -WindowStyle Normal `
    -WorkingDirectory $studioRoot `
    -FilePath "npm.cmd" `
    -ArgumentList @("run", "dev", "--", "--host")

Write-Host ""
Write-Host "=== Launched ===" -ForegroundColor Green
Write-Host "  Studio UI    http://localhost:5173" -ForegroundColor Yellow
Write-Host "  API status   http://localhost:5239/api/status" -ForegroundColor Yellow
Write-Host ""
Write-Host "Wait ~10 seconds for both to be ready, then check status:" -ForegroundColor Gray
Write-Host "  curl -s http://localhost:5239/api/status" -ForegroundColor Gray
