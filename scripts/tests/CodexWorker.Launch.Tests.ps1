Describe 'canonical launcher no-build contract' {
    It 'passes --no-build to dotnet run when NoBuild is selected' {
        $text = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\..\launch.ps1'))
        $text | Should Match '\$apiArguments\s*=\s*@\([\"'']run[\"'']\)'
        $text | Should Match '\$apiArguments\s*\+=\s*[\"'']--no-build[\"'']'
        $text | Should Match '-ArgumentList\s+\$apiArguments'
    }
}

Describe 'App Assistant Python resolution' {
    BeforeAll {
        . (Join-Path $PSScriptRoot '..\AppAssistantRuntime.ps1')
    }

    It 'repairs a stale venv with the configured bootstrap interpreter' {
        $root = Join-Path $TestDrive 'agent-service'
        $venv = Join-Path $root '.venv\Scripts\python.exe'
        New-Item -ItemType Directory -Force -Path (Split-Path $venv) | Out-Null
        New-Item -ItemType File -Path $venv | Out-Null

        $spec = Get-AppAssistantLaunchSpec `
            -AgentServiceRoot $root `
            -BootstrapPython 'conda.exe' `
            -CanRunPython { param($path) $path -eq $venv } `
            -RepairVenv {
                param($bootstrapPython, $venvPath, $agentServiceRoot)
                if ($bootstrapPython -ne 'conda.exe' -or
                    $venvPath -ne (Join-Path $root '.venv') -or
                    $agentServiceRoot -ne $root) {
                    throw 'repair did not receive the configured bootstrap interpreter and sidecar paths'
                }
            }.GetNewClosure()

        $spec.Executable | Should Be $venv
        $spec.Arguments | Should Be @('-m', 'uvicorn', 'app_assistant.server:app', '--host', '127.0.0.1', '--port', '8787')
    }

    It 'rejects a missing configured bootstrap interpreter' {
        $root = Join-Path $TestDrive 'agent-service'
        $specAction = {
            Get-AppAssistantLaunchSpec `
                -AgentServiceRoot $root `
                -CanRunPython { param($path) $false }
        }

        $specAction | Should Throw 'No usable Python runtime was found. Configure bootstrapPython to repair agent-service\.venv.'
    }
}
