[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Repository,
    [Parameter(Mandatory = $true)] [int] $PullRequestNumber,
    [int] $IssueNumber = 0,
    [Parameter(Mandatory = $true)] [bool] $Merged,
    [string] $MergeCommitSha,
    [Parameter(Mandatory = $true)] [string] $HeadBranch,
    [string] $RepositoryRoot,
    [string] $DataRoot
)

$modulePath = Join-Path $PSScriptRoot 'CodexWorker.psd1'
Import-Module $modulePath -Force

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = (Get-Location).Path }
Register-CodexPullRequestClosed -Repository $Repository -PullRequestNumber $PullRequestNumber -IssueNumber $IssueNumber -Merged:$Merged -MergeCommitSha $MergeCommitSha -HeadBranch $HeadBranch -RepositoryRoot $RepositoryRoot -DataRoot $DataRoot
