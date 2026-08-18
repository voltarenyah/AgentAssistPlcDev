[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $ConfigPath,
    [switch] $Watch,
    [int] $PollSeconds = 5
)

$modulePath = Join-Path $PSScriptRoot 'CodexWorker.psd1'
Import-Module $modulePath -Force

$loaded = Read-CodexDeploymentNotifierConfig -ConfigPath $ConfigPath
$deployAction = {
    param($deployment)
    Invoke-CodexDeployment -RepositoryRoot $loaded.Paths.RepositoryRoot -DataRoot $loaded.Paths.DataRoot -Config $loaded.Config -Deployment $deployment
}.GetNewClosure()
$parameters = @{
    Watch = $Watch
    Config = $loaded.Config
    RepositoryRoot = $loaded.Paths.RepositoryRoot
    DataRoot = $loaded.Paths.DataRoot
    StatePath = $loaded.Paths.StatePath
    PollSeconds = $PollSeconds
    DeployAction = $deployAction
}
Invoke-CodexDeploymentNotifier @parameters
