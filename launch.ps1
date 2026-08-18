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

Default launches start a fresh App Assistant session and discard prior pending
approvals. -NoKill preserves the existing assistant checkpoint state.

The LangGraph App Assistant is always started for development launches.
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
    # Also catch instances hosted via "dotnet ApiHost.dll" (process name is
    # "dotnet"), otherwise they keep port 5239 bound and the new backend
    # fails to start with "address already in use".
    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match 'ApiHost\.dll' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match 'app_assistant\.server:app' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
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

# 4. Start the LangGraph App Assistant. The desktop host owns this lifecycle
#    in packaged mode; development launches always start the local sidecar.
$assistantRoot = Join-Path $root "agent-service"
$assistantLogRoot = Join-Path $root ".assistant-logs"
$assistantStdout = Join-Path $assistantLogRoot "stdout.log"
$assistantStderr = Join-Path $assistantLogRoot "stderr.log"
$assistantDataDir = $env:AUTOMATION_WORKBENCH_APP_ASSISTANT_DATA_DIR
if ([string]::IsNullOrWhiteSpace($assistantDataDir)) {
    $assistantDataDir = Join-Path $env:LOCALAPPDATA "AutomationWorkbench\AppAssistant"
}

if (-not (Test-Path (Join-Path $assistantRoot "pyproject.toml"))) {
    Write-Host "!!! LangGraph service was not found at $assistantRoot." -ForegroundColor Red
    exit 1
}

$assistantVenvPython = Join-Path $assistantRoot ".venv\Scripts\python.exe"
if (Test-Path $assistantVenvPython) {
    $assistantExecutable = $assistantVenvPython
    $assistantArguments = @("-m", "uvicorn", "app_assistant.server:app", "--host", "127.0.0.1", "--port", "8787")
} elseif (Get-Command py.exe -ErrorAction SilentlyContinue) {
    $assistantExecutable = "py.exe"
    $assistantArguments = @("-3.13", "-m", "uvicorn", "app_assistant.server:app", "--host", "127.0.0.1", "--port", "8787")
} else {
    Write-Host "!!! No Python runtime was found for the LangGraph App Assistant. Create agent-service\.venv or install py.exe." -ForegroundColor Red
    exit 1
}

New-Item -ItemType Directory -Force -Path $assistantLogRoot, $assistantDataDir | Out-Null

# A development launch is a new App Assistant session. Clear only LangGraph's
# checkpoint files so an interrupted approval from a previous run cannot be
# resumed accidentally. Keep the redacted event log for diagnostics. When
# -NoKill is used, the existing sidecar owns this state and must be preserved.
if (-not $NoKill) {
    & (Join-Path $root "scripts\Reset-AppAssistantState.ps1") -DataDirectory $assistantDataDir
}

$env:APP_ASSISTANT_APIHOST_URL = "http://127.0.0.1:5239"
$env:APP_ASSISTANT_DATA_DIR = $assistantDataDir

    # Match the packaged desktop host: forward the existing local DeepSeek
    # configuration to the sidecar without ever printing the credential.
    $assistantApiKey = $env:DEEPSEEK_API_KEY
    $assistantModel = $env:DEEPSEEK_MODEL
    $assistantBaseUrl = $env:DEEPSEEK_BASE_URL
    if ([string]::IsNullOrWhiteSpace($assistantApiKey) -or
        [string]::IsNullOrWhiteSpace($assistantModel) -or
        [string]::IsNullOrWhiteSpace($assistantBaseUrl)) {
        $assistantConfigPaths = @(
            (Join-Path $env:APPDATA "AutomationWorkbench\config.json"),
            (Join-Path $env:APPDATA "PlcAiAssistant\config.json")
        )
        foreach ($assistantConfigPath in $assistantConfigPaths) {
            if (-not (Test-Path $assistantConfigPath)) {
                continue
            }
            try {
                $assistantConfig = Get-Content -Raw $assistantConfigPath | ConvertFrom-Json
                if ([string]::IsNullOrWhiteSpace($assistantApiKey)) {
                    $keyProperty = $assistantConfig.PSObject.Properties["deepSeekApiKey"]
                    if ($null -eq $keyProperty) {
                        $keyProperty = $assistantConfig.PSObject.Properties["DeepSeek:ApiKey"]
                    }
                    if ($null -ne $keyProperty) {
                        $assistantApiKey = [string]$keyProperty.Value
                    }
                    $deepSeekSection = $assistantConfig.PSObject.Properties["DeepSeek"]
                    if ([string]::IsNullOrWhiteSpace($assistantApiKey) -and $null -ne $deepSeekSection) {
                        $nestedKey = $deepSeekSection.Value.PSObject.Properties["ApiKey"]
                        if ($null -ne $nestedKey) {
                            $assistantApiKey = [string]$nestedKey.Value
                        }
                    }
                }
                if ([string]::IsNullOrWhiteSpace($assistantModel)) {
                    $modelProperty = $assistantConfig.PSObject.Properties["deepSeekModel"]
                    if ($null -ne $modelProperty) {
                        $assistantModel = [string]$modelProperty.Value
                    }
                }
                if ([string]::IsNullOrWhiteSpace($assistantBaseUrl)) {
                    $baseUrlProperty = $assistantConfig.PSObject.Properties["deepSeekBaseUrl"]
                    if ($null -ne $baseUrlProperty) {
                        $assistantBaseUrl = [string]$baseUrlProperty.Value
                    }
                }
            } catch {
                # Ignore malformed optional config and let the sidecar use its fallback.
            }
            if (-not [string]::IsNullOrWhiteSpace($assistantApiKey) -and
                -not [string]::IsNullOrWhiteSpace($assistantModel) -and
                -not [string]::IsNullOrWhiteSpace($assistantBaseUrl)) {
                break
            }
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($assistantApiKey)) {
        $env:DEEPSEEK_API_KEY = $assistantApiKey.Trim()
    }
    if (-not [string]::IsNullOrWhiteSpace($assistantModel)) {
        $env:DEEPSEEK_MODEL = $assistantModel.Trim()
    }
    if (-not [string]::IsNullOrWhiteSpace($assistantBaseUrl)) {
        $env:DEEPSEEK_BASE_URL = $assistantBaseUrl.Trim()
    }

    Write-Host ">>> Starting LangGraph App Assistant (port 8787)..." -ForegroundColor Cyan
    $assistantProcess = Start-Process `
        -WindowStyle Hidden `
        -WorkingDirectory $assistantRoot `
        -FilePath $assistantExecutable `
        -ArgumentList $assistantArguments `
        -RedirectStandardOutput $assistantStdout `
        -RedirectStandardError $assistantStderr `
        -PassThru

    $assistantReady = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($assistantProcess.HasExited) {
            break
        }
        try {
            $health = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:8787/health" -TimeoutSec 1
            if ($health.StatusCode -eq 200) {
                $assistantReady = $true
                break
            }
        } catch {
            Start-Sleep -Milliseconds 250
        }
    }
    if (-not $assistantReady) {
        Write-Host "!!! LangGraph App Assistant failed health check. See $assistantStderr" -ForegroundColor Red
        exit 1
    }
    Write-Host "    LangGraph App Assistant is ready." -ForegroundColor Green

# 5. Launch Studio Vite dev server in a new window
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
