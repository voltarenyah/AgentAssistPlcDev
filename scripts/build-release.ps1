[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string] $Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$releaseRoot = Join-Path $repoRoot 'artifacts\release\win-x64'
$stagingRoot = Join-Path $repoRoot 'artifacts\.release-staging'
$solution = Join-Path $repoRoot 'AgentAssistPlcDev.sln'
$apiHostProject = Join-Path $repoRoot 'src\ApiHost\ApiHost.csproj'
$knowledgeProject = Join-Path $repoRoot 'src\Mcp.Knowledge\Mcp.Knowledge.csproj'
$sourceEditorProject = Join-Path $repoRoot 'src\Mcp.SourceEditor\Mcp.SourceEditor.csproj'
$versionControlProject = Join-Path $repoRoot 'src\Mcp.VersionControl\Mcp.VersionControl.csproj'
$engineeringProject = Join-Path $repoRoot 'src\Mcp.Engineering\Mcp.Engineering.csproj'
$whitelistProject = Join-Path $repoRoot 'src\Tools.OpennessWhitelist\Tools.OpennessWhitelist.csproj'
$desktopProject = Join-Path $repoRoot 'src\AutomationWorkbench.Desktop\AutomationWorkbench.Desktop.csproj'
$assistantServiceSource = Join-Path $repoRoot 'agent-service'
$studioRoot = Join-Path $repoRoot 'studio'

function Invoke-Tool {
    param(
        [Parameter(Mandatory = $true)] [string] $FilePath,
        [Parameter(Mandatory = $true)] [string[]] $Arguments
    )

    Write-Host "> $FilePath $($Arguments -join ' ')"
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($Arguments -join ' ')"
    }
}

function Require-Command {
    param([Parameter(Mandatory = $true)] [string] $Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required tool '$Name' was not found on PATH."
    }
}

function Require-File {
    param([Parameter(Mandatory = $true)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required release file was not produced: $Path"
    }
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)] [string] $Source,
        [Parameter(Mandatory = $true)] [string] $Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Build output directory was not produced: $Source"
    }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $Destination -Recurse -Force
}

if (-not (Test-Path -LiteralPath $solution -PathType Leaf)) {
    throw "Solution file was not found: $solution"
}
if (-not (Test-Path -LiteralPath (Join-Path $studioRoot 'package-lock.json') -PathType Leaf)) {
    throw "Frontend lockfile was not found: $(Join-Path $studioRoot 'package-lock.json')"
}
if (-not (Test-Path -LiteralPath (Join-Path $assistantServiceSource 'pyproject.toml') -PathType Leaf)) {
    throw "App Assistant service manifest was not found: $(Join-Path $assistantServiceSource 'pyproject.toml')"
}

Require-Command 'dotnet'
Require-Command 'npm.cmd'
Require-Command 'git'

$sdkLines = @(dotnet --list-sdks)
if (-not ($sdkLines -match '^8\.')) {
    throw 'The .NET 8 SDK is required to build this release.'
}

$releaseRootFull = [IO.Path]::GetFullPath($releaseRoot)
$expectedReleaseParent = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\release'))
if (-not $releaseRootFull.StartsWith($expectedReleaseParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean an unexpected release path: $releaseRootFull"
}

if (Test-Path -LiteralPath $releaseRootFull) {
    Remove-Item -LiteralPath $releaseRootFull -Recurse -Force
}
if (Test-Path -LiteralPath $stagingRoot) {
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $releaseRootFull, $stagingRoot | Out-Null

$gitCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitCommit)) {
    throw 'Unable to determine the Git commit for the release manifest.'
}
$buildTimestampUtc = (Get-Date).ToUniversalTime().ToString('o')
$versionProperties = @(
    "-p:Version=$Version",
    "-p:InformationalVersion=$Version",
    '-p:IncludeSourceRevisionInInformationalVersion=false'
)

Write-Host "Building Automation Workbench $Version ($gitCommit)."
Invoke-Tool 'dotnet' @('restore', $solution, '--runtime', 'win-x64')
$testProjects = @(
    'tests\Contracts.Tests\Contracts.Tests.csproj',
    'tests\Mcp.Knowledge.Tests\Mcp.Knowledge.Tests.csproj',
    'tests\Agent.Tests\Agent.Tests.csproj',
    'tests\Mcp.Engineering.Tests\Mcp.Engineering.Tests.csproj',
    'tests\Mcp.VersionControl.Tests\Mcp.VersionControl.Tests.csproj',
    'tests\Mcp.SourceEditor.Tests\Mcp.SourceEditor.Tests.csproj',
    'tests\ApiHost.Tests\ApiHost.Tests.csproj',
    'tests\AutomationWorkbench.Desktop.Tests\AutomationWorkbench.Desktop.Tests.csproj',
    'tests\E2E.Tests\E2E.Tests.csproj'
)
foreach ($testProject in $testProjects) {
    Invoke-Tool 'dotnet' @('test', (Join-Path $repoRoot $testProject), '--configuration', 'Release', '--no-restore')
}

Push-Location $studioRoot
try {
    Invoke-Tool 'npm.cmd' @('ci')
    Invoke-Tool 'npm.cmd' @('test', '--', '--run')
    Invoke-Tool 'npm.cmd' @('run', 'build')
}
finally {
    Pop-Location
}

$publishProperties = $versionProperties + @(
    '-p:PublishSingleFile=false',
    '-p:PublishTrimmed=false',
    '-p:PublishReadyToRun=false'
)
$desktopPublishProperties = $publishProperties + @(
    '-p:PublishSingleFile=true'
)
$apiStage = Join-Path $stagingRoot 'ApiHost'
$knowledgeStage = Join-Path $stagingRoot 'Mcp.Knowledge'
$sourceEditorStage = Join-Path $stagingRoot 'Mcp.SourceEditor'
$versionControlStage = Join-Path $stagingRoot 'Mcp.VersionControl'
$engineeringStage = Join-Path $stagingRoot 'Mcp.Engineering'
$whitelistStage = Join-Path $stagingRoot 'OpennessWhitelist'
$desktopStage = Join-Path $stagingRoot 'AutomationWorkbench.Desktop'

Invoke-Tool 'dotnet' (@('publish', $apiHostProject, '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'true', '--no-restore', '--output', $apiStage, '-p:BuildStudio=true') + $publishProperties)
Invoke-Tool 'dotnet' (@('publish', $knowledgeProject, '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'true', '--no-restore', '--output', $knowledgeStage) + $publishProperties)
Invoke-Tool 'dotnet' (@('publish', $sourceEditorProject, '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'true', '--no-restore', '--output', $sourceEditorStage) + $publishProperties)
Invoke-Tool 'dotnet' (@('publish', $versionControlProject, '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'true', '--no-restore', '--output', $versionControlStage) + $publishProperties)
Invoke-Tool 'dotnet' (@('build', $engineeringProject, '--configuration', 'Release', '--framework', 'net48', '--no-restore', '--output', $engineeringStage) + $versionProperties)
Invoke-Tool 'dotnet' (@('build', $whitelistProject, '--configuration', 'Release', '--framework', 'net48', '--no-restore', '--output', $whitelistStage) + $versionProperties)
Invoke-Tool 'dotnet' (@('publish', $desktopProject, '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'true', '--no-restore', '--output', $desktopStage) + $desktopPublishProperties)

$apiDestination = $releaseRootFull
$mcpRoot = Join-Path $releaseRootFull 'mcp'
$knowledgeDestination = Join-Path $mcpRoot 'knowledge'
$sourceEditorDestination = Join-Path $mcpRoot 'source-editor'
$versionControlDestination = Join-Path $mcpRoot 'version-control'
$engineeringDestination = Join-Path $mcpRoot 'engineering'
$toolsDestination = Join-Path $releaseRootFull 'tools'
$assistantServiceDestination = Join-Path $releaseRootFull 'agent-service'
Copy-DirectoryContents $apiStage $apiDestination
Copy-DirectoryContents $knowledgeStage $knowledgeDestination
Copy-DirectoryContents $sourceEditorStage $sourceEditorDestination
Copy-DirectoryContents $versionControlStage $versionControlDestination
Copy-DirectoryContents $engineeringStage $engineeringDestination
Copy-DirectoryContents $whitelistStage $toolsDestination
Copy-DirectoryContents $desktopStage $releaseRootFull
Copy-DirectoryContents $assistantServiceSource $assistantServiceDestination

$engineeringConfig = Join-Path $engineeringDestination 'Mcp.Engineering.exe.config'
Require-File (Join-Path $engineeringDestination 'Mcp.Engineering.exe')
Require-File $engineeringConfig
if ((Get-Content -LiteralPath $engineeringConfig -Raw) -notmatch 'supportedRuntime') {
    throw "Mcp.Engineering.exe.config does not contain the .NET Framework startup configuration: $engineeringConfig"
}

$sqliteNative = @(Get-ChildItem -LiteralPath $knowledgeDestination -Recurse -File -Filter 'e_sqlite3.dll')
if ($sqliteNative.Count -eq 0) {
    throw "SQLite native binary e_sqlite3.dll was not included in $knowledgeDestination"
}
$libGitNative = @(Get-ChildItem -LiteralPath $versionControlDestination -Recurse -File -Filter 'git2-*.dll')
if ($libGitNative.Count -eq 0) {
    throw "LibGit2Sharp native binary git2-*.dll was not included in $versionControlDestination"
}

$requiredExecutables = @(
    (Join-Path $releaseRootFull 'ApiHost.exe'),
    (Join-Path $toolsDestination 'AutomationWorkbench.OpennessWhitelist.exe'),
    (Join-Path $engineeringDestination 'Mcp.Engineering.exe'),
    (Join-Path $knowledgeDestination 'Mcp.Knowledge.exe'),
    (Join-Path $sourceEditorDestination 'Mcp.SourceEditor.exe'),
    (Join-Path $versionControlDestination 'Mcp.VersionControl.exe'),
    (Join-Path $releaseRootFull 'AutomationWorkbench.exe'),
    (Join-Path $assistantServiceDestination 'pyproject.toml'),
    (Join-Path $assistantServiceDestination 'langgraph.json')
)
foreach ($requiredExecutable in $requiredExecutables) {
    Require-File $requiredExecutable
}

$manifestFiles = @(Get-ChildItem -LiteralPath $releaseRootFull -Recurse -File | Where-Object { $_.Name -ne 'release-manifest.json' } | ForEach-Object {
    $relativePath = [IO.Path]::GetRelativePath($releaseRootFull, $_.FullName).Replace('\', '/')
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    [ordered]@{
        path = $relativePath
        size = $_.Length
        sha256 = $hash
    }
} | Sort-Object -Property path)

$manifest = [ordered]@{
    applicationVersion = $Version
    gitCommit = $gitCommit
    buildTimestampUtc = $buildTimestampUtc
    targetArchitecture = 'win-x64'
    targetFrameworks = [ordered]@{
        ApiHost = 'net8.0'
        Agent = 'net8.0'
        McpKnowledge = 'net8.0'
        McpSourceEditor = 'net8.0'
        McpVersionControl = 'net8.0'
        McpEngineering = 'net48'
        DesktopShell = 'net8.0-windows'
        AppAssistant = 'Python 3.13'
    }
    files = $manifestFiles
}
$manifestPath = Join-Path $releaseRootFull 'release-manifest.json'
$manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host "Release assembled at $releaseRootFull"
Write-Host "Manifest: $manifestPath"
