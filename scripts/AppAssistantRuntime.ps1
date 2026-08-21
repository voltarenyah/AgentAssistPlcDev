function Get-AppAssistantLaunchSpec {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$AgentServiceRoot,

        [string]$BootstrapPython,

        [scriptblock]$CanRunPython = {
            param($Path, $Arguments)
            try {
                & $Path @Arguments -c 'import app_assistant, uvicorn' 2>$null
                return $LASTEXITCODE -eq 0
            } catch {
                return $false
            }
        },

        [scriptblock]$RepairVenv = {
            param($ConfiguredPython, $VenvPath, $ServiceRoot)
            & $ConfiguredPython -m venv $VenvPath
            if ($LASTEXITCODE -ne 0) {
                throw "Configured bootstrap Python failed to recreate $VenvPath."
            }

            $venvPythonPath = Join-Path $VenvPath 'Scripts\python.exe'
            & $venvPythonPath -m pip install -e "$ServiceRoot[test]"
            if ($LASTEXITCODE -ne 0) {
                throw "Configured bootstrap Python recreated the sidecar venv, but dependency installation failed."
            }
        }
    )

    $venvPython = Join-Path $AgentServiceRoot '.venv\Scripts\python.exe'
    if ((Test-Path -LiteralPath $venvPython -PathType Leaf) -and (& $CanRunPython $venvPython @())) {
        return [pscustomobject]@{
            Executable = $venvPython
            Arguments = @('-m', 'uvicorn', 'app_assistant.server:app', '--host', '127.0.0.1', '--port', '8787')
        }
    }

    if ([string]::IsNullOrWhiteSpace($BootstrapPython)) {
        throw 'No usable Python runtime was found. Configure bootstrapPython to repair agent-service\.venv.'
    }

    & $RepairVenv $BootstrapPython (Join-Path $AgentServiceRoot '.venv') $AgentServiceRoot
    if ((Test-Path -LiteralPath $venvPython -PathType Leaf) -and (& $CanRunPython $venvPython @())) {
        return [pscustomobject]@{
            Executable = $venvPython
            Arguments = @('-m', 'uvicorn', 'app_assistant.server:app', '--host', '127.0.0.1', '--port', '8787')
        }
    }

    throw 'No usable Python runtime was found after repairing agent-service\.venv with bootstrapPython.'
}
