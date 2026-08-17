Set-StrictMode -Version Latest

$privatePath = Join-Path $PSScriptRoot 'Private'
Get-ChildItem -Path $privatePath -Filter '*.ps1' -File |
    Sort-Object -Property Name |
    ForEach-Object { . $_.FullName }
