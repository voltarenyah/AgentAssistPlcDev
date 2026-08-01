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

# 1. Kill lingering processes from the previous run (unless flagged off)
#    Must happen BEFORE build to release locked binaries.
if (-not $NoKill) {
    Write-Host ">>> Cleaning up old processes..." -ForegroundColor Cyan
    # Kill ApiHost directly (it shows as process name "ApiHost", not "dotnet")
    Get-Process -Name "ApiHost" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Get-Process -Name "node" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
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
Start-Process `
    -WindowStyle Normal `
    -WorkingDirectory $root `
    -FilePath "dotnet" `
    -ArgumentList @("run", "--project", $apiProject, "--", "Application:OpenBrowserOnStart=false")

# 4. Launch Studio Vite dev server in a new window
Write-Host ">>> Starting Studio (port 5173)..." -ForegroundColor Cyan
$studioRoot = Join-Path $root "studio"
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
