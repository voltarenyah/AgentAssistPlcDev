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
    $config = [IO.File]::ReadAllText($ConfigPath) | ConvertFrom-Json
}
if (-not [string]::IsNullOrWhiteSpace($Repository)) { Add-Member -InputObject $config -NotePropertyName repository -NotePropertyValue $Repository -Force }
if (-not [string]::IsNullOrWhiteSpace($RepositoryRoot)) { Add-Member -InputObject $config -NotePropertyName repositoryRoot -NotePropertyValue $RepositoryRoot -Force }

$params = @{ Config = $config; Repository = $Repository; RepositoryRoot = $RepositoryRoot; DataRoot = $DataRoot }
if ($SkipPrerequisiteProbe) { $params.SkipPrerequisiteProbe = $true }
if ($WhatIfPreference) { $params.WhatIf = $true }
Invoke-CodexLocalWorkerSetup @params
