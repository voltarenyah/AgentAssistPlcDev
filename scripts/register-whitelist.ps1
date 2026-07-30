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

# --- SHA-256 hash as base64. Avoid Get-FileHash so older Windows PowerShell works.
$sha256 = [System.Security.Cryptography.SHA256]::Create()
try {
    $bytes = $sha256.ComputeHash([System.IO.File]::ReadAllBytes($exe))
} finally {
    $sha256.Dispose()
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

# --- Attempt merge only when elevated. HKLM writes are optional for builds. ---
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isElevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($isElevated) {
    $output = & reg.exe import $regFile 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Host "[whitelist] merged into registry. TIA Openness firewall prompts should now be suppressed."
    } else {
        Write-Host "[whitelist] reg.exe import returned exit code $LASTEXITCODE"
        Write-Host $output
    }
} else {
    Write-Host "[whitelist] not merged - run as Administrator or double-click $regFile to apply."
}

exit 0
