Describe 'canonical launcher no-build contract' {
    It 'passes --no-build to dotnet run when NoBuild is selected' {
        $text = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\..\launch.ps1'))
        $text | Should Match '\$apiArguments\s*=\s*@\([\"'']run[\"'']\)'
        $text | Should Match '\$apiArguments\s*\+=\s*[\"'']--no-build[\"'']'
        $text | Should Match '-ArgumentList\s+\$apiArguments'
    }
}
