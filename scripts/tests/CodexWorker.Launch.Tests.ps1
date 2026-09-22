Describe 'canonical launcher no-build contract' {
    It 'passes --no-build to dotnet run when NoBuild is selected' {
        $text = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\..\launch.ps1'))
        $text | Should Match '\$apiArguments\s*=\s*@\([\"'']run[\"'']\)'
        $text | Should Match '\$apiArguments\s*\+=\s*[\"'']--no-build[\"'']'
        $text | Should Match '-ArgumentList\s+\$apiArguments'
    }

    It 'bootstraps Studio dependencies from the lockfile before starting Vite' {
        $text = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\..\launch.ps1'))
        $text | Should Match 'Install-StudioDependenciesIfNeeded\s+-StudioRoot\s+\$studioRoot'
        $text | Should Match '&\s+npm\.cmd\s+ci\s+--no-audit\s+--no-fund'
        $text | Should Match 'ConvertFrom-Json\s+-AsHashtable'
        $text | Should Match 'node_modules\\\.bin\\vite\.cmd'
    }
}

