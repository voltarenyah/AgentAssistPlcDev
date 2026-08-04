[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string] $Version,
    [string] $InnoSetupPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$releaseRoot = Join-Path $repoRoot 'artifacts\release\win-x64'
$installerRoot = Join-Path $repoRoot 'artifacts\installer'
$issPath = Join-Path $repoRoot 'installer\AutomationWorkbench.iss'

function Require-File {
    param([Parameter(Mandatory = $true)] [string] $Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required file was not found: $Path"
    }
}

function Find-InnoSetup {
    if (-not [string]::IsNullOrWhiteSpace($InnoSetupPath)) {
        Require-File $InnoSetupPath
        return (Resolve-Path -LiteralPath $InnoSetupPath).Path
    }

    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $knownPaths = @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    )
    foreach ($knownPath in $knownPaths) {
        if (Test-Path -LiteralPath $knownPath -PathType Leaf) {
            return $knownPath
        }
    }

    throw 'Inno Setup 6 ISCC.exe was not found. Install Inno Setup or pass -InnoSetupPath.'
}

Require-File $issPath
Require-File (Join-Path $releaseRoot 'ApiHost.exe')
Require-File (Join-Path $releaseRoot 'AutomationWorkbench.exe')
Require-File (Join-Path $releaseRoot 'mcp\engineering\Mcp.Engineering.exe')
Require-File (Join-Path $releaseRoot 'tools\AutomationWorkbench.OpennessWhitelist.exe')

$releaseRootFull = [IO.Path]::GetFullPath($releaseRoot)
$expectedReleaseParent = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\release'))
if (-not $releaseRootFull.StartsWith($expectedReleaseParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to package an unexpected release path: $releaseRootFull"
}

$iscc = Find-InnoSetup
New-Item -ItemType Directory -Force -Path $installerRoot | Out-Null
$outputPath = Join-Path $installerRoot "AutomationWorkbench-$Version-win-x64-setup.exe"
if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Force
}
if (Test-Path -LiteralPath "${outputPath}.sha256") {
    Remove-Item -LiteralPath "${outputPath}.sha256" -Force
}

& $iscc "/DMyAppVersion=$Version" "/DReleaseDir=$releaseRootFull" "/DOutputDir=$installerRoot" $issPath
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code ${LASTEXITCODE}."
}
Require-File $outputPath

$hash = (Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path -Leaf $outputPath)" | Set-Content -LiteralPath "${outputPath}.sha256" -Encoding ascii
Write-Host "Installer: $outputPath"
Write-Host "SHA-256: $hash"
