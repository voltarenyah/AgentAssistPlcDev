# Dumps public members of types in the installed TIA Openness assembly.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Dump-OpennessApi.ps1 [-AssemblyPath <dll>] -TypeNames <full.type.name> [<more.names...>]
param(
    [string]$AssemblyPath = 'C:\Program Files\Siemens\Automation\Portal V17\PublicAPI\V17\Siemens.Engineering.dll',
    [Parameter(Position = 0, Mandatory = $true)]
    [string[]]$TypeNames
)

$asm = [Reflection.Assembly]::LoadFrom($AssemblyPath)

# Tolerate comma-joined invocation (e.g. from Git Bash): split every element on commas.
$TypeNames = $TypeNames | ForEach-Object { $_ -split ',' } | Where-Object { $_ }

foreach ($name in $TypeNames) {
    $t = $asm.GetType($name)
    if ($null -eq $t) {
        Write-Output "NOT FOUND: $name"
        continue
    }
    Write-Output "=== $name ==="
    if ($t.IsEnum) {
        [Enum]::GetNames($t) | ForEach-Object { "  enum: $_" }
        continue
    }
    $flags = [Reflection.BindingFlags]'Public,Instance,Static,DeclaredOnly'
    foreach ($p in $t.GetProperties($flags)) {
        Write-Output ("  prop:   {0} {1}" -f $p.PropertyType.Name, $p.Name)
    }
    foreach ($m in $t.GetMethods($flags)) {
        if ($m.IsSpecialName) { continue }
        $ps = ($m.GetParameters() | ForEach-Object { "{0} {1}" -f $_.ParameterType.Name, $_.Name }) -join ', '
        $static = if ($m.IsStatic) { 'static ' } else { '' }
        Write-Output ("  method: {0}{1} {2}({3})" -f $static, $m.ReturnType.Name, $m.Name, $ps)
    }
}
