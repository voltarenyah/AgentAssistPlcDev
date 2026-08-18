[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $RunnerRoot,
    [string] $ConfigPath
)

Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath($RunnerRoot)
if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Runner root does not exist: $root" }
$runCommand = Join-Path $root 'run.cmd'
if (-not (Test-Path -LiteralPath $runCommand -PathType Leaf)) { throw "Runner executable does not exist: $runCommand" }
Push-Location $root
try {
    # This is intentionally the interactive runner process. Do not replace it with
    # config.cmd install/service mode: the notifier needs the user's desktop.
    & $runCommand
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally {
    Pop-Location
}
