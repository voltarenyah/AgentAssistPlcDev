# Attaches to every running TIA Portal instance and probes the failsafe (F-) surface:
#   1. SafetySignatureProvider per F-block (PlcBlock anchor per TIA Openness manual §5.27.4;
#      the provider is null on non-F-blocks AND on the DeviceItem/PlcSoftware anchors — verified
#      2026-09-02 against PEI_SinoARP_Master_V4.1.3, CPU 1515F-2 PN). A block signature of 0
#      means "missing or invalidated by a recent change". This is the source folded into
#      get_plc_checksums' FSignature.
#   2. SafetyAdministration (password state, safety system version, runtime groups).
#   3. Whether FingerprintProvider.GetFingerprints() returns values for F-blocks
#      (F-blocks cannot be exported, so fingerprints would be the fallback change signal;
#      verified working on all 55 F-blocks of the probe project, no license needed).
#   4. Optional: with -ProjectFilePath, unzips the .ap17/.apXX project and searches the XML
#      for signature-looking values (fallback channel if Openness exposes nothing).
# Run with a failsafe (F-CPU) project open in TIA Portal.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Probe-SafetySignature.ps1 [-AssemblyPath <dll>] [-ProjectFilePath <project.ap17>]
param(
    [string]$AssemblyPath = 'C:\Program Files\Siemens\Automation\Portal V17\PublicAPI\V17\Siemens.Engineering.dll',
    [string]$ProjectFilePath = ''
)

[Reflection.Assembly]::LoadFrom($AssemblyPath) | Out-Null

# Windows PowerShell 5.1 cannot bind generic methods directly ($obj.GetService([Type]) fails) —
# invoke IEngineeringServiceProvider.GetService<T>() through reflection instead.
$getServiceMethod = [Siemens.Engineering.IEngineeringServiceProvider].GetMethod('GetService')

function Get-ServiceInstance {
    param($Obj, [Type]$ServiceType)
    $generic = $script:getServiceMethod.MakeGenericMethod($ServiceType)
    return $generic.Invoke($Obj, @())
}

function Find-PlcSoftware {
    param($Node, $Acc)
    # Mirrors src/Mcp.Engineering/Adapter/PlcSoftwareResolver.cs: DeviceItem ->
    # GetService<SoftwareContainer>() -> Software as PlcSoftware, recursing DeviceItems.
    if ($Node -is [Siemens.Engineering.HW.DeviceItem]) {
        $container = Get-ServiceInstance -Obj $Node -ServiceType ([Siemens.Engineering.HW.Features.SoftwareContainer])
        $sw = $null
        if ($null -ne $container) { $sw = $container.Software }
        if ($sw -is [Siemens.Engineering.SW.PlcSoftware]) {
            $Acc.Add($sw)
        }
    }
    foreach ($child in $Node.DeviceItems) {
        Find-PlcSoftware -Node $child -Acc $Acc
    }
}

function Find-InDeviceGroup {
    param($Group, $Acc)
    # Grouped devices are not part of project.Devices (see PlcSoftwareResolver note 2026-07-19).
    foreach ($dev in $Group.Devices) { Find-PlcSoftware -Node $dev -Acc $Acc }
    foreach ($sub in $Group.Groups) { Find-InDeviceGroup -Group $sub -Acc $Acc }
}

function Find-Blocks {
    param($Group, $Acc)
    foreach ($b in $Group.Blocks) { $Acc.Add($b) }
    foreach ($sub in $Group.Groups) { Find-Blocks -Group $sub -Acc $Acc }
}

function Test-FailSafeBlock {
    param($Block)
    # Display-only sampling heuristic for this probe (F_* language or generated-block name
    # prefixes). Production detection is semantic: SafetySignatureProvider present on the block
    # (src/Mcp.Engineering/Adapter/FailSafeBlocks.cs).
    $language = $Block.ProgrammingLanguage.ToString()
    if ($language.StartsWith('F_')) { return $true }
    $name = $Block.Name.Trim()
    return $name.StartsWith('FOB_', 'OrdinalIgnoreCase') `
        -or $name.StartsWith('FFB_', 'OrdinalIgnoreCase') `
        -or $name.StartsWith('FFC_', 'OrdinalIgnoreCase') `
        -or $name.StartsWith('FDB_', 'OrdinalIgnoreCase')
}

function Get-DeviceItemAnchor {
    param($Plc)
    # Verified 2026-09-01 against a live F-CPU (CPU 1515F-2 PN, PEI_SinoARP_Master_V4.1.3):
    # the Siemens.Engineering.Safety services are anchored on the PLC's DeviceItem, NOT on
    # the PlcSoftware (GetService on PlcSoftware returns null for both).
    $node = $Plc.Parent
    while ($null -ne $node) {
        if ($node -is [Siemens.Engineering.HW.DeviceItem]) { return $node }
        $node = $node.Parent
    }
    return $null
}

function Show-SafetyDetection {
    param($Plc)
    # Detection rule used by TiaV17Adapter.ReadSafety: a PLC is a safety device when the
    # SafetyAdministration or SafetySignatureProvider service is present on its DeviceItem anchor.
    try {
        $anchor = Get-DeviceItemAnchor -Plc $Plc
        $admin = Get-ServiceInstance -Obj $anchor -ServiceType ([Siemens.Engineering.Safety.SafetyAdministration])
        $provider = Get-ServiceInstance -Obj $anchor -ServiceType ([Siemens.Engineering.Safety.SafetySignatureProvider])
        $isSafety = ($null -ne $admin) -or ($null -ne $provider)
        Write-Output ("  [{0}] DETECTION: safetyDevice={1} (SafetyAdministration present: {2}, SafetySignatureProvider present: {3}, anchor: {4})" -f `
            $Plc.Name, $isSafety, ($null -ne $admin), ($null -ne $provider), $(if ($null -ne $anchor) { $anchor.Name } else { 'none' }))
    }
    catch {
        Write-Output "  [$($Plc.Name)] DETECTION: read failed (treated as read-failed): $($_.Exception.Message)"
    }
}

function Show-SafetySignature {
    param($Plc)
    # Per TIA Openness manual §5.27.4 the provider lives on each F-block (PlcBlock); it is null
    # on non-F-blocks and on the DeviceItem/PlcSoftware anchors (verified 2026-09-02).
    $blocks = New-Object 'System.Collections.Generic.List[object]'
    Find-Blocks -Group $Plc.BlockGroup -Acc $blocks
    $found = 0
    foreach ($block in $blocks) {
        try {
            $provider = Get-ServiceInstance -Obj $block -ServiceType ([Siemens.Engineering.Safety.SafetySignatureProvider])
        }
        catch {
            Write-Output "  [$($Plc.Name)] $($block.Name): provider read failed: $($_.Exception.Message)"
            continue
        }
        if ($null -eq $provider) { continue }  # not an F-block
        $found++
        $sig = $null
        try { $sig = $provider.Signatures.Find([Siemens.Engineering.Safety.SafetySignatureType]::BlockOfflineSignature) } catch { }
        if ($null -eq $sig) {
            Write-Output ("  [{0}] {1}: F-block, no BlockOfflineSignature entry" -f $Plc.Name, $block.Name)
        } else {
            # 0 = signature missing or invalidated by a recent change (manual §5.27.4)
            Write-Output ("  [{0}] {1}: BlockOfflineSignature = 0x{2}" -f $Plc.Name, $block.Name, $sig.Value.ToString('X8'))
        }
    }
    if ($found -eq 0) {
        Write-Output "  [$($Plc.Name)] no F-block signatures (not a failsafe PLC, or safety program never compiled)"
    }
}

function Show-SafetyAdministration {
    param($Plc)
    $anchor = Get-DeviceItemAnchor -Plc $Plc
    $admin = Get-ServiceInstance -Obj $anchor -ServiceType ([Siemens.Engineering.Safety.SafetyAdministration])
    if ($null -eq $admin) {
        Write-Output "  [$($Plc.Name)] SafetyAdministration: null"
        return
    }
    try {
        Write-Output ("  [{0}] SafetyAdministration: passwordSet={1} loggedOn={2}" -f `
            $Plc.Name, $admin.IsSafetyOfflineProgramPasswordSet, $admin.IsLoggedOnToSafetyOfflineProgram)
        $version = $admin.Settings.SafetySystemVersion
        if ($null -ne $version) {
            Write-Output "  [$($Plc.Name)] SafetySystemVersion: $($version.Value)"
        }
        foreach ($group in $admin.RuntimeGroups) {
            Write-Output ("  [{0}] runtime group '{1}': mainSafetyBlock={2} fob={3} maxCycleTime={4}" -f `
                $Plc.Name, $group.Name, $group.MainSafetyBlockName, $group.FOBName, $group.MaximumCycleTime)
        }
    }
    catch {
        Write-Output "  [$($Plc.Name)] SafetyAdministration read failed: $($_.Exception.GetType().Name): $($_.Exception.Message)"
    }
}

function Show-BlockFingerprints {
    param($Plc)
    $blocks = New-Object 'System.Collections.Generic.List[object]'
    Find-Blocks -Group $Plc.BlockGroup -Acc $blocks
    $fCount = 0
    foreach ($block in $blocks) {
        $isF = Test-FailSafeBlock -Block $block
        if ($isF) { $fCount++ }
        $provider = Get-ServiceInstance -Obj $block -ServiceType ([Siemens.Engineering.SW.FingerprintProvider])
        if ($null -eq $provider) {
            Write-Output ("  [{0}] block {1} [{2}] F={3}: FingerprintProvider null" -f `
                $Plc.Name, $block.Name, $block.ProgrammingLanguage, $isF)
            continue
        }
        try {
            $fps = $provider.GetFingerprints()
            $parts = @()
            foreach ($fp in $fps) { $parts += ("{0}={1}" -f $fp.Id, $fp.Value) }
            Write-Output ("  [{0}] block {1} [{2}] F={3}: {4}" -f `
                $Plc.Name, $block.Name, $block.ProgrammingLanguage, $isF, ($parts -join ' '))
        }
        catch {
            Write-Output ("  [{0}] block {1} [{2}] F={3}: GetFingerprints failed: {4}" -f `
                $Plc.Name, $block.Name, $block.ProgrammingLanguage, $isF, $_.Exception.Message)
        }
    }
    Write-Output "  [$($Plc.Name)] blocks: $($blocks.Count) total, $fCount failsafe"
}

function Search-ProjectFileForSignature {
    param([string]$Path)
    Write-Output "--- project file scan: $Path"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = $null
    try {
        $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    }
    catch {
        Write-Output "  NOT A PLAIN ZIP: $($_.Exception.Message)"
        Write-Output "  (The .ap17 envelope is proprietary - the naive unzip fallback does not work directly.)"
        return
    }
    try {
        $hits = 0
        foreach ($entry in $zip.Entries) {
            if ($entry.Length -eq 0 -or $entry.Length -gt 20MB) { continue }
            if ($entry.FullName -notmatch '\.(xml|aml|dat|s7p|cfg)$' -and $entry.FullName -match '\.') { continue }
            $reader = New-Object System.IO.StreamReader($entry.Open())
            try {
                $text = $reader.ReadToEnd()
            }
            finally {
                $reader.Dispose()
            }
            $matches = [regex]::Matches($text, '.{0,60}(ollective|ignature).{0,80}', 'IgnoreCase')
            foreach ($match in $matches) {
                if ($hits -ge 50) { break }
                Write-Output ("  {0}: ...{1}..." -f $entry.FullName, ($match.Value -replace '\s+', ' '))
                $hits++
            }
            if ($hits -ge 50) { break }
        }
        Write-Output "  project file scan hits: $hits (capped at 50)"
    }
    finally {
        if ($null -ne $zip) { $zip.Dispose() }
    }
}

$processes = [Siemens.Engineering.TiaPortal]::GetProcesses()
Write-Output "TIA processes: $($processes.Count)"
foreach ($proc in $processes) {
    $projPath = $null
    try { $projPath = $proc.ProjectPath } catch { }
    Write-Output ("--- process {0} Mode={1} ProjectPath={2}" -f $proc.Id, $proc.Mode, $projPath)
    try {
        $portal = $proc.Attach()
    }
    catch {
        Write-Output "  Attach failed: $($_.Exception.Message)"
        continue
    }
    foreach ($project in $portal.Projects) {
        Write-Output "  Project: $($project.Name)"
        $plcs = New-Object 'System.Collections.Generic.List[object]'
        foreach ($dev in $project.Devices) { Find-PlcSoftware -Node $dev -Acc $plcs }
        foreach ($grp in $project.DeviceGroups) { Find-InDeviceGroup -Group $grp -Acc $plcs }
        Write-Output "  PLCs found: $($plcs.Count)"
        foreach ($plc in $plcs) {
            Show-SafetyDetection -Plc $plc
            Show-SafetySignature -Plc $plc
            Show-SafetyAdministration -Plc $plc
            Show-BlockFingerprints -Plc $plc
        }
    }
}

if (-not [string]::IsNullOrWhiteSpace($ProjectFilePath)) {
    Search-ProjectFileForSignature -Path $ProjectFilePath
}
