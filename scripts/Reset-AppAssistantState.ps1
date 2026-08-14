param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$DataDirectory
)

$resolvedDataDirectory = [System.IO.Path]::GetFullPath($DataDirectory)
if (-not (Test-Path -LiteralPath $resolvedDataDirectory -PathType Container)) {
    return
}

foreach ($fileName in @(
    'checkpoints.sqlite',
    'checkpoints.sqlite-shm',
    'checkpoints.sqlite-wal'
)) {
    $checkpointPath = Join-Path $resolvedDataDirectory $fileName
    if (Test-Path -LiteralPath $checkpointPath -PathType Leaf) {
        Remove-Item -LiteralPath $checkpointPath -Force -ErrorAction Stop
    }
}
