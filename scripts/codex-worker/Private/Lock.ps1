function Enter-CodexWorkerLock {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [int] $TimeoutSeconds = 30
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $directory = [System.IO.Path]::GetDirectoryName($fullPath)
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $deadline = [DateTime]::UtcNow.AddSeconds([Math]::Max(0, $TimeoutSeconds))
    while ($true) {
        try {
            return [System.IO.FileStream]::new(
                $fullPath,
                [System.IO.FileMode]::OpenOrCreate,
                [System.IO.FileAccess]::ReadWrite,
                [System.IO.FileShare]::None)
        } catch [System.IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) {
                throw 'Worker lock is busy.'
            }

            Start-Sleep -Milliseconds 250
        }
    }
}

function Exit-CodexWorkerLock {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [System.IDisposable] $Handle
    )

    $Handle.Dispose()
}
