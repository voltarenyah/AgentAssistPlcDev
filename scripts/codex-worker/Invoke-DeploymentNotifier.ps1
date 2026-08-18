[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $ConfigPath,
    [switch] $Watch,
    [int] $PollSeconds = 5
)

$modulePath = Join-Path $PSScriptRoot 'CodexWorker.psd1'
Import-Module $modulePath -Force

$loaded = Read-CodexDeploymentNotifierConfig -ConfigPath $ConfigPath
$parameters = @{
    Watch = $Watch
    Config = $loaded.Config
    RepositoryRoot = $loaded.Paths.RepositoryRoot
    DataRoot = $loaded.Paths.DataRoot
    StatePath = $loaded.Paths.StatePath
    PollSeconds = $PollSeconds
}
Invoke-CodexDeploymentNotifier @parameters
