[CmdletBinding()]
param(
    [switch] $Watch,
    [string] $RepositoryRoot,
    [string] $DataRoot,
    [string] $StatePath,
    [int] $PollSeconds = 5
)

$modulePath = Join-Path $PSScriptRoot 'CodexWorker.psd1'
Import-Module $modulePath -Force

$parameters = @{
    Watch = $Watch
    RepositoryRoot = $RepositoryRoot
    DataRoot = $DataRoot
    StatePath = $StatePath
    PollSeconds = $PollSeconds
}
Invoke-CodexDeploymentNotifier @parameters
