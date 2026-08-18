[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Repository,
    [string] $RepositoryRoot,
    [string] $DataRoot,
    [string] $ConfigPath,
    [switch] $SkipPrerequisiteProbe
)

Set-StrictMode -Version Latest
$modulePath = Join-Path $PSScriptRoot 'CodexWorker.psd1'
Import-Module $modulePath -Force
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = (Get-Location).Path }
if ([string]::IsNullOrWhiteSpace($DataRoot)) { $DataRoot = Join-Path $env:LOCALAPPDATA 'AutomationWorkbench\CodexWorker' }
if ([string]::IsNullOrWhiteSpace($ConfigPath)) { $ConfigPath = Join-Path $DataRoot 'config.json' }

$config = [pscustomobject]@{}
if (Test-Path -LiteralPath $ConfigPath -PathType Leaf) {
    try { $config = [IO.File]::ReadAllText($ConfigPath) | ConvertFrom-Json } catch { throw "Worker config is invalid: $($_.Exception.Message)" }
    if ($null -eq $config -or $config -is [array] -or $config -is [string] -or $config -is [ValueType]) { throw 'Worker config must be a JSON object.' }
}
if (-not [string]::IsNullOrWhiteSpace($Repository)) { Add-Member -InputObject $config -NotePropertyName repository -NotePropertyValue $Repository -Force }
if (-not [string]::IsNullOrWhiteSpace($RepositoryRoot)) { Add-Member -InputObject $config -NotePropertyName repositoryRoot -NotePropertyValue $RepositoryRoot -Force }

$params = @{ Config = $config; Repository = $Repository; RepositoryRoot = $RepositoryRoot; DataRoot = $DataRoot; ConfigPath = $ConfigPath }
if ($SkipPrerequisiteProbe) { $params.SkipPrerequisiteProbe = $true }
if ($WhatIfPreference) { $params.WhatIf = $true }
Invoke-CodexLocalWorkerSetup @params
