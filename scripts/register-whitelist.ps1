<#
.SYNOPSIS
    Post-build script: generates (and optionally merges) the TIA Openness whitelist
    .reg file for the compiled Mcp.Engineering.exe.

    Called from the Mcp.Engineering.csproj post-build event.  The .reg file is
    written next to the .exe so it's easy to find and merge manually.

    When running in an elevated prompt the script also imports the .reg file
    immediately so the next TIA Openness call sees the whitelist entry.
#>

param (
    [Parameter(Mandatory)]
    [string]$TargetPath   # full path to the compiled .exe (e.g. ...\bin\Debug\net48\Mcp.Engineering.exe)
)

$ErrorActionPreference = "Stop"

$exe  = $TargetPath
$name = Split-Path $exe -Leaf
$outDir = Split-Path $exe -Parent
$regFile = Join-Path $outDir "register-whitelist.reg"

# --- SHA-256 hash (Get-FileHash returns hex string -> convert to bytes -> base64)
$hexHash = (Get-FileHash $exe -Algorithm SHA256).Hash
$bytes = [byte[]]::new($hexHash.Length / 2)
for ($i = 0; $i -lt $hexHash.Length; $i += 2) {
    $bytes[$i / 2] = [Convert]::ToByte($hexHash.Substring($i, 2), 16)
}
$hash = [Convert]::ToBase64String($bytes)

# --- LastWriteTime in Siemens format (yyyy/MM/dd HH:mm:ss.fff)
$date = (Get-Item $exe).LastWriteTimeUtc.ToString("yyyy/MM/dd HH:mm:ss.fff")

# --- Escape backslashes for the .reg file
$escapedPath = $exe -replace '\\', '\\'

# --- Write .reg file
$content = @"
Windows Registry Editor Version 5.00

[HKEY_LOCAL_MACHINE\SOFTWARE\Siemens\Automation\Openness\17.0\Whitelist\$name\Entry]
"Path"="$escapedPath"
"DateModified"="$date"
"FileHash"="$hash"

"@

$content | Out-File -FilePath $regFile -Encoding ascii

Write-Host "[whitelist] .reg file generated: $regFile"

# --- Attempt merge (succeeds only when elevated) -----------------------------
try {
    $output = & reg.exe import $regFile 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Host "[whitelist] merged into registry (elevated). TIA Openness firewall prompts should now be suppressed."
    } else {
        Write-Host "[whitelist] reg.exe import returned exit code $LASTEXITCODE"
    }
} catch {
    Write-Host "[whitelist] not merged - run as Administrator or double-click $regFile to apply."
}
