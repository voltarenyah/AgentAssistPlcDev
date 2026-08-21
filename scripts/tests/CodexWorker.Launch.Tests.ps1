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

    It 'falls back from a stale venv to the Python launcher' {
        $root = Join-Path $TestDrive 'agent-service'
        $venv = Join-Path $root '.venv\Scripts\python.exe'
        New-Item -ItemType Directory -Force -Path (Split-Path $venv) | Out-Null
        New-Item -ItemType File -Path $venv | Out-Null

        $spec = Get-AppAssistantLaunchSpec `
            -AgentServiceRoot $root `
            -CanRunPython { param($path) $path -eq 'py.exe' } `
            -FindPython { 'py.exe' }

        $spec.Executable | Should Be 'py.exe'
        $spec.Arguments | Should Be @('-3.13', '-m', 'uvicorn', 'app_assistant.server:app', '--host', '127.0.0.1', '--port', '8787')
    }

    It 'rejects a Python launcher that cannot run the sidecar' {
        $root = Join-Path $TestDrive 'agent-service'
        $specAction = {
            Get-AppAssistantLaunchSpec `
                -AgentServiceRoot $root `
                -CanRunPython { param($path) $false } `
                -FindPython { 'py.exe' }
        }

        $specAction | Should Throw 'No usable Python runtime was found. Repair agent-service\.venv or install py.exe.'
    }
}
