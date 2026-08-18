[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Repository,
    [int]$IssueNumber = 0,
    [Parameter(Mandatory = $true)][string]$Actor,
    [string]$PullRequestNumber,
    [string]$EventName = 'codex:revise',
    [string]$RepositoryRoot,
    [string]$DataRoot
)

if ($EventName -notmatch '(?i)revise') { throw "Revision entry requires a revise event; received '$EventName'." }

$modulePath = Join-Path $PSScriptRoot 'CodexWorker.psd1'
Import-Module $modulePath -Force
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = (Get-Location).Path }
$paths = Resolve-CodexWorkerPaths -RepositoryRoot $RepositoryRoot -DataRoot $DataRoot
$IssueNumber = Resolve-CodexRevisionIssueNumber -Repository $Repository -IssueNumber $IssueNumber -PullRequestNumber $PullRequestNumber
$config = [pscustomobject]@{ repository = $Repository; dataRoot = $paths.DataRoot; defaultBranch = 'master' }
if (Test-Path -LiteralPath $paths.ConfigPath -PathType Leaf) {
    try { $config = [IO.File]::ReadAllText($paths.ConfigPath) | ConvertFrom-Json } catch { throw "Codex worker configuration is invalid: $($_.Exception.Message)" }
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
