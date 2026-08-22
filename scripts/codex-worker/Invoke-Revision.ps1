[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Repository,
    [int]$IssueNumber = 0,
    [Parameter(Mandatory = $true)][string]$Actor,
    [string]$PullRequestNumber,
    [string]$EventName = 'codex:revise',
    [ValidateSet('Codex', 'Kimi')][string]$Provider = 'Codex',
    [string]$RepositoryRoot,
    [string]$DataRoot
)

if ($EventName -notmatch '(?i)revise') { throw "Revision entry requires a revise event; received '$EventName'." }

$modulePath = Join-Path $PSScriptRoot 'CodexWorker.psd1'
Import-Module $modulePath -Force
$resolvedProvider = Resolve-CodexWorkerProvider -Provider $Provider -EventName $EventName
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = (Get-Location).Path }
$paths = Resolve-CodexWorkerPaths -RepositoryRoot $RepositoryRoot -DataRoot $DataRoot
$IssueNumber = Resolve-CodexRevisionIssueNumber -Repository $Repository -IssueNumber $IssueNumber -PullRequestNumber $PullRequestNumber
$config = [pscustomobject]@{ repository = $Repository; dataRoot = $paths.DataRoot; defaultBranch = 'master' }
if (Test-Path -LiteralPath $paths.ConfigPath -PathType Leaf) {
    try { $config = [IO.File]::ReadAllText($paths.ConfigPath) | ConvertFrom-Json } catch { throw "Codex worker configuration is invalid: $($_.Exception.Message)" }
}
$enabledProviders = if ($null -ne $config.PSObject.Properties['enabledProviders']) { @($config.enabledProviders) } else { @('Codex') }
if ($resolvedProvider.Name -notin $enabledProviders) { throw "Worker provider '$($resolvedProvider.Name)' is not enabled by configuration." }
if ($null -eq $config.PSObject.Properties['provider']) {
    $config | Add-Member -NotePropertyName provider -NotePropertyValue $resolvedProvider.Name
} else {
    $config.provider = $resolvedProvider.Name
}
$params = @{
    Repository = $Repository
    IssueNumber = $IssueNumber
    Actor = $Actor
    PullRequestNumber = $PullRequestNumber
    RepositoryRoot = $paths.RepositoryRoot
    DataRoot = $paths.DataRoot
    Config = $config
    StatePath = $paths.StatePath
}
Invoke-CodexRevision @params
