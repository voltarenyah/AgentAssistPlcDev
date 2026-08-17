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
