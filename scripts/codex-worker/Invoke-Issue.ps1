[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][int]$IssueNumber,
    [Parameter(Mandatory = $true)][string]$Actor,
    [Parameter(Mandatory = $true)][string]$EventName,
    [string]$RepositoryRoot,
    [string]$DataRoot,
    [switch]$DryRun
)

$modulePath = Join-Path $PSScriptRoot 'CodexWorker.psd1'
Import-Module $modulePath -Force

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = (Get-Location).Path }
$initialPaths = Resolve-CodexWorkerPaths -RepositoryRoot $RepositoryRoot -DataRoot $DataRoot
$config = [pscustomobject]@{}
if (Test-Path -LiteralPath $initialPaths.ConfigPath -PathType Leaf) {
    try {
        $config = [IO.File]::ReadAllText($initialPaths.ConfigPath) | ConvertFrom-Json
    } catch {
        throw "Codex worker configuration is invalid: $($_.Exception.Message)"
    }
}

$configuredRepositoryRoot = ''
if ($null -ne $config.PSObject.Properties['repositoryRoot']) {
    $configuredRepositoryRoot = [string]$config.repositoryRoot
}
if (-not [string]::IsNullOrWhiteSpace($configuredRepositoryRoot)) {
    if (-not [IO.Path]::IsPathRooted($configuredRepositoryRoot)) {
        throw 'Codex worker repositoryRoot must be an absolute path.'
    }
    $RepositoryRoot = [IO.Path]::GetFullPath($configuredRepositoryRoot)
}

$paths = Resolve-CodexWorkerPaths -RepositoryRoot $RepositoryRoot -DataRoot $DataRoot

# Configuration is data. The workflow-supplied repository and event values remain
# authoritative, while the trusted entry point owns module loading and path setup.
$invokeParameters = @{
    Repository = $Repository
    IssueNumber = $IssueNumber
    Actor = $Actor
    EventName = $EventName
    RepositoryRoot = $paths.RepositoryRoot
    DataRoot = $paths.DataRoot
    Config = $config
    StatePath = $paths.StatePath
}
if ($DryRun) { $invokeParameters.DryRun = $true }

Invoke-CodexIssueRun @invokeParameters
