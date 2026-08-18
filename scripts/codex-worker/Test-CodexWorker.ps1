[CmdletBinding()]
param()

$tests = Get-ChildItem -Path (Join-Path $PSScriptRoot '..\tests') -Filter 'CodexWorker.*.Tests.ps1' -File |
    Sort-Object -Property Name |
    Select-Object -ExpandProperty FullName

$result = Invoke-Pester -Script $tests -PassThru
Write-Host "PassedCount: $($result.PassedCount)"
Write-Host "FailedCount: $($result.FailedCount)"

if ($result.FailedCount -gt 0) { exit 1 }
exit 0
