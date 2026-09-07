# Attaches to the first running TIA Portal V17 process and verifies the read-only
# fingerprint-first critical section proposed for TIA Compare:
#   1. acquire TiaPortal.ExclusiveAccess,
#   2. read the compiled software checksum and lightweight evidence for all managed objects,
#   3. export one ordinary PLC block and one tag table while that lock remains held.
#
# It never imports, edits, compiles, or saves the TIA project. Output is confined to a new
# directory below the system temp folder unless -OutputDir is given.
# Run only against a disposable/local test workbench with a compiled PLC program open in TIA.
param(
    [string]$AssemblyPath = 'C:\Program Files\Siemens\Automation\Portal V17\PublicAPI\V17\Siemens.Engineering.dll',
    [string]$OutputDir = '',
    [int]$SessionId = 0
)

$ErrorActionPreference = 'Stop'
[Reflection.Assembly]::LoadFrom($AssemblyPath) | Out-Null

$getServiceMethod = [Siemens.Engineering.IEngineeringServiceProvider].GetMethod('GetService')

function Get-ServiceInstance {
    param($Object, [Type]$ServiceType)
    $generic = $script:getServiceMethod.MakeGenericMethod($ServiceType)
    return $generic.Invoke($Object, @())
}

function Find-PlcSoftware {
    param($Node, $Found)
    if ($Node -is [Siemens.Engineering.HW.DeviceItem]) {
        $container = Get-ServiceInstance $Node ([Siemens.Engineering.HW.Features.SoftwareContainer])
        if ($null -ne $container -and $container.Software -is [Siemens.Engineering.SW.PlcSoftware]) {
            $Found.Add($container.Software)
        }
    }
    foreach ($child in $Node.DeviceItems) { Find-PlcSoftware $child $Found }
}

function Find-Blocks {
    param($Group, $Found)
    foreach ($block in $Group.Blocks) { $Found.Add($block) }
    foreach ($child in $Group.Groups) { Find-Blocks $child $Found }
}

function Find-Types {
    param($Group, $Found)
    foreach ($type in $Group.Types) { $Found.Add($type) }
    foreach ($child in $Group.Groups) { Find-Types $child $Found }
}

function Find-Tags {
    param($Group, $Found)
    foreach ($table in $Group.TagTables) { $Found.Add($table) }
    foreach ($child in $Group.Groups) { Find-Tags $child $Found }
}

function Is-FailSafeBlock {
    param($Block)
    return $null -ne (Get-ServiceInstance $Block ([Siemens.Engineering.Safety.SafetySignatureProvider]))
}

function Read-FingerprintState {
    param($Object)
    try {
        $provider = Get-ServiceInstance $Object ([Siemens.Engineering.SW.FingerprintProvider])
        if ($null -eq $provider) { return 'unsupported' }
        $values = @($provider.GetFingerprints())
        return $(if ($values.Count -eq 0) { 'empty' } else { 'readable' })
    }
    catch { return 'read-failed' }
}

$process = $null
if ($SessionId -gt 0) {
    # Mirrors TiaV17Adapter.Attach: direct PID acquisition can succeed even when the broad
    # GetProcesses() enumeration has no visible sessions for this client process.
    $process = [Siemens.Engineering.TiaPortal]::GetProcess($SessionId, 30000)
    if ($null -eq $process) { throw "TIA Portal PID $SessionId could not be acquired." }
}
else {
    $process = @([Siemens.Engineering.TiaPortal]::GetProcesses() | Select-Object -First 1)
    if ($process.Count -ne 1) { throw 'No running TIA Portal process was found.' }
    $process = $process[0]
}
$portal = $process.Attach()
$project = @($portal.Projects | Select-Object -First 1)
if ($project.Count -ne 1) { throw 'The attached TIA Portal has no open project.' }

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path ([IO.Path]::GetTempPath()) ("tia-exclusive-evidence-probe-" + [Guid]::NewGuid().ToString('N'))
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$started = [Diagnostics.Stopwatch]::StartNew()
$access = $null
try {
    $access = $portal.ExclusiveAccess('Probe fingerprint-first read and candidate export')
    $lockAcquiredMs = $started.ElapsedMilliseconds

    $plcs = New-Object 'System.Collections.Generic.List[object]'
    foreach ($device in $project[0].Devices) { Find-PlcSoftware $device $plcs }
    if ($plcs.Count -eq 0) { throw 'No PLC software was found in the open project.' }

    $plc = $plcs[0]
    $checksumProvider = Get-ServiceInstance $plc ([Siemens.Engineering.SW.PlcChecksumProvider])
    $checksum = if ($null -eq $checksumProvider) { $null } else { $checksumProvider.Software }

    $blocks = New-Object 'System.Collections.Generic.List[object]'
    $types = New-Object 'System.Collections.Generic.List[object]'
    $tags = New-Object 'System.Collections.Generic.List[object]'
    Find-Blocks $plc.BlockGroup $blocks
    Find-Types $plc.TypeGroup $types
    Find-Tags $plc.TagTableGroup $tags

    $standardBlocks = @($blocks | Where-Object { -not (Is-FailSafeBlock $_) -and $_.GetType().Name -ne 'PlcInstanceDb' })
    $fBlocks = @($blocks | Where-Object { Is-FailSafeBlock $_ })
    $fingerprintStates = @($standardBlocks + @($types) | ForEach-Object { Read-FingerprintState $_ })
    $tagTimestampStates = @($tags | ForEach-Object {
        try { if ($null -eq $_.ModifiedTimeStamp) { 'missing' } else { 'readable' } } catch { 'read-failed' }
    })
    $fSignatureStates = @($fBlocks | ForEach-Object {
        try {
            $provider = Get-ServiceInstance $_ ([Siemens.Engineering.Safety.SafetySignatureProvider])
            $signature = $provider.Signatures.Find([Siemens.Engineering.Safety.SafetySignatureType]::BlockOfflineSignature)
            if ($null -eq $signature) { 'missing' } elseif ($signature.Value -eq 0) { 'invalidated' } else { 'readable' }
        }
        catch { 'read-failed' }
    })
    $evidenceReadMs = $started.ElapsedMilliseconds

    if ($standardBlocks.Count -eq 0) { throw 'The PLC has no exportable non-instance, non-F block for this probe.' }
    if ($tags.Count -eq 0) { throw 'The PLC has no tag table for this probe.' }

    $block = $standardBlocks[0]
    $tag = $tags[0]
    $blockPath = Join-Path $OutputDir 'block.xml'
    $tagPath = Join-Path $OutputDir 'tag-table.xml'
    $block.Export([IO.FileInfo]$blockPath, [Siemens.Engineering.SW.ExportOptions]::WithDefaults)
    $tag.Export([IO.FileInfo]$tagPath, [Siemens.Engineering.SW.ExportOptions]::WithDefaults)
    $candidateExportMs = $started.ElapsedMilliseconds

    [PSCustomObject]@{
        status = 'passed'
        tiaProcessId = $process.Id
        project = $project[0].Name
        plc = $plc.Name
        softwareChecksumPresent = -not [string]::IsNullOrWhiteSpace($checksum)
        lockAcquiredMs = $lockAcquiredMs
        evidenceReadMs = $evidenceReadMs - $lockAcquiredMs
        candidateExportMs = $candidateExportMs - $evidenceReadMs
        totalLockedMs = $candidateExportMs - $lockAcquiredMs
        managedObjectCounts = [PSCustomObject]@{
            standardBlocks = $standardBlocks.Count
            udts = $types.Count
            tagTables = $tags.Count
            fBlocks = $fBlocks.Count
            instanceDbsExcluded = @($blocks | Where-Object { $_.GetType().Name -eq 'PlcInstanceDb' }).Count
        }
        evidenceReadStates = [PSCustomObject]@{
            fingerprint = $fingerprintStates | Group-Object | ForEach-Object { @{ ($_.Name) = $_.Count } }
            tagTimestamp = $tagTimestampStates | Group-Object | ForEach-Object { @{ ($_.Name) = $_.Count } }
            fSignature = $fSignatureStates | Group-Object | ForEach-Object { @{ ($_.Name) = $_.Count } }
        }
        exportedCandidates = @(
            [PSCustomObject]@{ category = 'block'; name = $block.Name; path = $blockPath; exists = Test-Path -LiteralPath $blockPath },
            [PSCustomObject]@{ category = 'tagTable'; name = $tag.Name; path = $tagPath; exists = Test-Path -LiteralPath $tagPath }
        )
    } | ConvertTo-Json -Depth 8
}
finally {
    if ($null -ne $access) { $access.Dispose() }
    $started.Stop()
}
