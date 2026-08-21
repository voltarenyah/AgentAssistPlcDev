function Get-AppAssistantLaunchSpec {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$AgentServiceRoot,

        [scriptblock]$CanRunPython = {
            param($Path, $Arguments)
            try {
                & $Path @Arguments -c 'import app_assistant, uvicorn' 2>$null
                return $LASTEXITCODE -eq 0
            } catch {
                return $false
            }
        },

        [scriptblock]$FindPython = {
            $command = Get-Command py.exe -ErrorAction SilentlyContinue
            if ($null -ne $command) { return $command.Source }
            return $null
        }
    )

    $venvPython = Join-Path $AgentServiceRoot '.venv\Scripts\python.exe'
    if ((Test-Path -LiteralPath $venvPython -PathType Leaf) -and (& $CanRunPython $venvPython @())) {
        return [pscustomobject]@{
            Executable = $venvPython
            Arguments = @('-m', 'uvicorn', 'app_assistant.server:app', '--host', '127.0.0.1', '--port', '8787')
        }
    }

    $pythonLauncher = & $FindPython
    if (-not [string]::IsNullOrWhiteSpace($pythonLauncher) -and (& $CanRunPython $pythonLauncher @('-3.13'))) {
        return [pscustomobject]@{
            Executable = $pythonLauncher
            Arguments = @('-3.13', '-m', 'uvicorn', 'app_assistant.server:app', '--host', '127.0.0.1', '--port', '8787')
        }
    }

    throw 'No usable Python runtime was found. Repair agent-service\.venv or install py.exe.'
}
