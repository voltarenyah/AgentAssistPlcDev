function Resolve-CodexWorkerPaths {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot,

        [string] $DataRoot
    )

    if (-not [System.IO.Path]::IsPathRooted($RepositoryRoot) -or
        ($RepositoryRoot -match '^[A-Za-z]:[^\\/]')) {
        throw 'RepositoryRoot must be absolute.'
    }

    if ([string]::IsNullOrWhiteSpace($DataRoot)) {
        $DataRoot = Join-Path $env:LOCALAPPDATA 'AutomationWorkbench\CodexWorker'
    }

    if (-not [System.IO.Path]::IsPathRooted($DataRoot) -or
        ($DataRoot -match '^[A-Za-z]:[^\\/]')) {
        throw 'DataRoot must be absolute.'
    }

    $resolvedRepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
    $resolvedDataRoot = [System.IO.Path]::GetFullPath($DataRoot)

    [ordered]@{
        RepositoryRoot = $resolvedRepositoryRoot
        WorktreeRoot = [System.IO.Path]::GetFullPath((Join-Path $resolvedRepositoryRoot '.worktrees'))
        DataRoot = $resolvedDataRoot
        StatePath = [System.IO.Path]::GetFullPath((Join-Path $resolvedDataRoot 'state.json'))
        RunRoot = [System.IO.Path]::GetFullPath((Join-Path $resolvedDataRoot 'runs'))
        ConfigPath = [System.IO.Path]::GetFullPath((Join-Path $resolvedDataRoot 'config.json'))
        LockPath = [System.IO.Path]::GetFullPath((Join-Path $resolvedDataRoot 'worker.lock'))
    }
}

function New-CodexWorkerDefaultState {
    [pscustomobject]@{
        schemaVersion = 1
        issues = [ordered]@{}
        deployment = $null
    }
}

function Read-CodexWorkerState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        return New-CodexWorkerDefaultState
    }

    try {
        $state = [System.IO.File]::ReadAllText($fullPath) | ConvertFrom-Json
        if ($null -eq $state) {
            throw 'State JSON was empty.'
        }

        return $state
    } catch {
        $directory = [System.IO.Path]::GetDirectoryName($fullPath)
        $baseName = [System.IO.Path]::GetFileNameWithoutExtension($fullPath)
        $extension = [System.IO.Path]::GetExtension($fullPath)
        $stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ', [Globalization.CultureInfo]::InvariantCulture)
        $quarantinePath = Join-Path $directory "$baseName.corrupt.$stamp$extension"
        $suffix = 1
        while (Test-Path -LiteralPath $quarantinePath) {
            $quarantinePath = Join-Path $directory "$baseName.corrupt.$stamp-$suffix$extension"
            $suffix++
        }

        Move-Item -LiteralPath $fullPath -Destination $quarantinePath
        return New-CodexWorkerDefaultState
    }
}

function Write-CodexWorkerState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [object] $State
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $directory = [System.IO.Path]::GetDirectoryName($fullPath)
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $temporaryPath = "$fullPath.tmp"
    $json = $State | ConvertTo-Json -Depth 20
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    try {
        [System.IO.File]::WriteAllText($temporaryPath, $json, $utf8)
        Move-Item -LiteralPath $temporaryPath -Destination $fullPath -Force
    } finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}
