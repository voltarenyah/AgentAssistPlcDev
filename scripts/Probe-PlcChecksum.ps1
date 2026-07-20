# Attaches to every running TIA Portal instance and reads:
#   - the PLC software checksum (PlcChecksumProvider.Software) per PLC
#   - per-block and per-UDT fingerprints (FingerprintProvider.GetFingerprints())
#   - whether tag tables expose a FingerprintProvider (docs say no)
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Probe-PlcChecksum.ps1 [-AssemblyPath <dll>]
param(
    [string]$AssemblyPath = 'C:\Program Files\Siemens\Automation\Portal V17\PublicAPI\V17\Siemens.Engineering.dll'
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

function Find-PlcTypes {
    param($Group, $Acc)
    foreach ($t in $Group.Types) { $Acc.Add($t) }
    foreach ($sub in $Group.Groups) { Find-PlcTypes -Group $sub -Acc $Acc }
}

function Show-Fingerprints {
    param($Obj, [string]$Label)
    $provider = Get-ServiceInstance -Obj $Obj -ServiceType ([Siemens.Engineering.SW.FingerprintProvider])
    if ($null -eq $provider) {
        Write-Output "  [$Label] FingerprintProvider: null (not supported)"
        return
    }
    try {
        $fps = $provider.GetFingerprints()
        $parts = @()
        foreach ($fp in $fps) { $parts += ("{0}={1}" -f $fp.Id, $fp.Value) }
        Write-Output ("  [{0}] {1}" -f $Label, ($parts -join ' '))
    }
    catch {
        Write-Output "  [$Label] GetFingerprints failed: $($_.Exception.GetType().Name): $($_.Exception.Message)"
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
            $provider = Get-ServiceInstance -Obj $plc -ServiceType ([Siemens.Engineering.SW.PlcChecksumProvider])
            if ($null -eq $provider) {
                Write-Output "  [$($plc.Name)] PlcChecksumProvider: null (checksum not supported by this PLC)"
            }
            else {
                try {
                    $checksum = $provider.Software
                    if ($null -eq $checksum) { $checksum = '(null - program not compiled)' }
                }
                catch {
                    $checksum = "<read failed: $($_.Exception.Message)>"
                }
                Write-Output "  [$($plc.Name)] Software checksum: $checksum"
            }

            $blocks = New-Object 'System.Collections.Generic.List[object]'
            Find-Blocks -Group $plc.BlockGroup -Acc $blocks
            foreach ($block in $blocks) {
                Show-Fingerprints -Obj $block -Label ("block {0} [{1}]" -f $block.Name, $block.GetType().Name)
            }

            $types = New-Object 'System.Collections.Generic.List[object]'
            Find-PlcTypes -Group $plc.TypeGroup -Acc $types
            foreach ($type in $types) {
                Show-Fingerprints -Obj $type -Label ("udt {0}" -f $type.Name)
            }

            foreach ($table in $plc.TagTableGroup.TagTables) {
                Show-Fingerprints -Obj $table -Label ("tagtable {0}" -f $table.Name)
            }
        }
    }
}
