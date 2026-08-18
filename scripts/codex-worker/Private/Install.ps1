Set-StrictMode -Version Latest

$script:CodexLifecycleLabels = [ordered]@{
    'codex:queued' = '1f6feb'
    'codex:running' = 'fbca04'
    'codex:pr-ready' = '8250df'
    'codex:blocked' = 'd1242f'
    'codex:retry' = 'f0883e'
    'codex:revise' = 'bf8700'
    'codex:done' = '2da44e'
}

function Get-CodexSetupProperty {
    param([object] $Object, [string] $Name, [object] $Default = $null)
    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) { return $property.Value }
    return $Default
}

function New-CodexSetupRequest {
    param(
        [Parameter(Mandatory = $true)] [string] $FilePath,
        [Parameter(Mandatory = $true)] [string[]] $Arguments,
        [string] $WorkingDirectory,
        [switch] $Interactive
    )
    return [pscustomobject][ordered]@{
        FilePath = $FilePath
        Arguments = [string[]]$Arguments
        WorkingDirectory = $WorkingDirectory
        Interactive = [bool]$Interactive
    }
}

function Invoke-CodexInstallCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $FilePath,
        [Parameter(Mandatory = $true)] [string[]] $Arguments,
        [string] $WorkingDirectory,
        [Alias('CommandRunner')] [object] $Runner,
        [switch] $Interactive
    )
    $request = New-CodexSetupRequest -FilePath $FilePath -Arguments $Arguments -WorkingDirectory $WorkingDirectory -Interactive:$Interactive
    if ($null -ne $Runner) { return (& $Runner $request) }
    if ([string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $output = & $FilePath @Arguments 2>&1
    } else {
        Push-Location $WorkingDirectory
        try { $output = & $FilePath @Arguments 2>&1 } finally { Pop-Location }
    }
    $exitCode = $LASTEXITCODE
    $text = (($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine)
    return [pscustomobject]@{ ExitCode = $exitCode; Stdout = $text; Stderr = '' }
}

function Test-CodexSetupSuccess {
    param([object] $Result)
    if ($null -eq $Result) { return $true }
    $property = $Result.PSObject.Properties['ExitCode']
    if ($null -eq $property) { return $true }
    return ([int]$property.Value -eq 0)
}

function Get-CodexSetupText {
    param([object] $Result)
    if ($null -eq $Result) { return '' }
    $stdout = Get-CodexSetupProperty $Result 'Stdout' ''
    if ([string]::IsNullOrWhiteSpace([string]$stdout)) { $stdout = Get-CodexSetupProperty $Result 'Output' '' }
    if ([string]::IsNullOrWhiteSpace([string]$stdout)) { $stdout = Get-CodexSetupProperty $Result 'Version' '' }
    if ([string]::IsNullOrWhiteSpace([string]$stdout)) { $stdout = [string]$Result }
    return [string]$stdout
}

function ConvertTo-CodexVersion {
    param([AllowNull()][string] $Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    $match = [regex]::Match($Text, '(?<!\d)(?<version>\d+\.\d+(?:\.\d+)?)(?!\d)')
    if (-not $match.Success) { return $null }
    try { return [version]$match.Groups['version'].Value } catch { return $null }
}

function Test-CodexPrerequisitePolicy {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] [object] $Prerequisite)
    $name = [string](Get-CodexSetupProperty $Prerequisite 'Name' '')
    $installed = [bool](Get-CodexSetupProperty $Prerequisite 'Installed' $false)
    $text = [string](Get-CodexSetupProperty $Prerequisite 'Version' '')
    $parsed = ConvertTo-CodexVersion $text
    $minimum = $null
    $maximumExclusive = $null
    switch ($name) {
        'PowerShell 7' { $minimum = [version]'7.0' }
        '.NET 8' { $minimum = [version]'8.0' }
        'Node.js' { $minimum = [version]'20.0' }
        'Bootstrap Python' { $minimum = [version]'3.11'; $maximumExclusive = [version]'3.14' }
        default { }
    }
    $valid = $installed -and $null -ne $parsed
    if ($valid -and $null -ne $minimum) { $valid = $parsed -ge $minimum }
    if ($valid -and $null -ne $maximumExclusive) { $valid = $parsed -lt $maximumExclusive }
    [pscustomobject][ordered]@{
        Name = $name; Installed = $installed; Version = $text; ParsedVersion = $parsed
        Minimum = $minimum; MaximumExclusive = $maximumExclusive; Valid = [bool]$valid
        Error = if (-not $installed) { 'missing' } elseif ($null -eq $parsed) { 'unparseable version' } elseif ($null -ne $minimum -and $parsed -lt $minimum) { 'version is too old' } elseif ($null -ne $maximumExclusive -and $parsed -ge $maximumExclusive) { 'version is outside the supported range' } else { $null }
    }
}

function Get-CodexPrerequisitePlan {
    [CmdletBinding()]
    param(
        [object] $Config,
        [object] $CommandRunner,
        [switch] $Probe
    )
    $python = [string](Get-CodexSetupProperty $Config 'bootstrapPython' 'python.exe')
    $checks = @(
        [pscustomobject]@{ Name = 'PowerShell 7'; FilePath = 'pwsh.exe'; Arguments = @('--version'); Required = $true }
        [pscustomobject]@{ Name = 'Git'; FilePath = 'git.exe'; Arguments = @('--version'); Required = $true }
        [pscustomobject]@{ Name = 'GitHub CLI'; FilePath = 'gh.exe'; Arguments = @('--version'); Required = $true }
        [pscustomobject]@{ Name = '.NET 8'; FilePath = 'dotnet.exe'; Arguments = @('--version'); Required = $true }
        [pscustomobject]@{ Name = 'Node.js'; FilePath = 'node.exe'; Arguments = @('--version'); Required = $true }
        [pscustomobject]@{ Name = 'npm'; FilePath = 'npm.cmd'; Arguments = @('--version'); Required = $true }
        [pscustomobject]@{ Name = 'Bootstrap Python'; FilePath = $python; Arguments = @('--version'); Required = $true }
        [pscustomobject]@{ Name = 'Codex CLI'; FilePath = [string](Get-CodexSetupProperty $Config 'codexCommand' 'codex'); Arguments = @('--version'); Required = $false }
    )
    foreach ($check in $checks) {
        $installed = $null
        $version = $null
        if ($Probe) {
            try {
                $result = Invoke-CodexInstallCommand -FilePath $check.FilePath -Arguments $check.Arguments -CommandRunner $CommandRunner
                $installed = Test-CodexSetupSuccess $result
                $version = Get-CodexSetupText $result
            } catch { $installed = $false; $version = $_.Exception.Message }
        }
        $prerequisite = [pscustomobject][ordered]@{
            Name = $check.Name; FilePath = $check.FilePath; Arguments = [string[]]$check.Arguments
            Required = [bool]$check.Required; Installed = $installed; Version = $version
        }
        $prerequisite | Add-Member -NotePropertyName Policy -NotePropertyValue (Test-CodexPrerequisitePolicy -Prerequisite $prerequisite)
        $prerequisite
    }
}

function Resolve-CodexRunnerAsset {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] [object] $Release)
    $assets = @((Get-CodexSetupProperty $Release 'assets' @()) | Where-Object {
        $name = [string](Get-CodexSetupProperty $_ 'name' '')
        $name -match '(?i)^actions-runner-win-x64-[^/\\]+\.zip$'
    })
    if ($assets.Count -ne 1) { throw "Expected exactly one Windows x64 runner asset; found $($assets.Count)." }
    $asset = $assets[0]
    $name = [string](Get-CodexSetupProperty $asset 'name' '')
    $url = [string](Get-CodexSetupProperty $asset 'browser_download_url' '')
    if ([string]::IsNullOrWhiteSpace($url)) { throw 'Windows x64 runner asset has no download URL.' }
    $body = [string](Get-CodexSetupProperty $Release 'body' '')
    $escapedName = [regex]::Escape($name)
    $hashes = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($match in [regex]::Matches($body, "(?im)^.*$escapedName.*?(?<hash>[0-9a-f]{64}).*$")) {
        [void]$hashes.Add($match.Groups['hash'].Value.ToLowerInvariant())
    }
    if ($hashes.Count -ne 1) { throw "Could not identify one unambiguous SHA-256 checksum for $name." }
    $tag = [string](Get-CodexSetupProperty $Release 'tag_name' '')
    if ([string]::IsNullOrWhiteSpace($tag)) { throw "Windows x64 runner asset has no unambiguous release version." }
    return [pscustomobject][ordered]@{ Name = $name; Uri = $url; Sha256 = @($hashes)[0]; Tag = $tag; Version = ($tag -replace '^v', '') }
}

function Get-CodexCurrentUserId {
    $domain = [Environment]::UserDomainName
    $user = [Environment]::UserName
    if ([string]::IsNullOrWhiteSpace($domain)) { return $user }
    return "$domain\$user"
}

function Get-CodexCurrentUserSid {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($null -eq $identity -or $null -eq $identity.User) { throw 'The current Windows user SID could not be resolved.' }
    return $identity.User
}

function New-CodexScheduledTaskXml {
    param([string] $UserId, [string] $FilePath, [string[]] $Arguments, [string] $OwnershipMarker)
    $escapedFile = [Security.SecurityElement]::Escape($FilePath)
    $escapedArguments = [Security.SecurityElement]::Escape((($Arguments | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join ' '))
    $escapedMarker = [Security.SecurityElement]::Escape($OwnershipMarker)
    return @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Author>Automation Workbench</Author><Description>Automation Workbench installer ownership marker: $escapedMarker</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>$([Security.SecurityElement]::Escape($UserId))</UserId></LogonTrigger></Triggers>
  <Principals><Principal id="Author"><UserId>$([Security.SecurityElement]::Escape($UserId))</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><Hidden>true</Hidden><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure></Settings>
  <Actions Context="Author"><Exec><Command>$escapedFile</Command><Arguments>$escapedArguments</Arguments><WorkingDirectory>$([Security.SecurityElement]::Escape((Split-Path -Parent $FilePath)))</WorkingDirectory></Exec></Actions>
</Task>
"@
}

function Get-CodexExistingRunnerState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $RunnerRoot,
        [Parameter(Mandatory = $true)] [string] $Repository,
        [Parameter(Mandatory = $true)] [string] $Label,
        [Parameter(Mandatory = $true)] [string] $RunnerName,
        [object] $Asset,
        [scriptblock] $ProcessProvider
    )
    $statePath = Join-Path $RunnerRoot 'runner-install.json'
    if (-not (Test-Path -LiteralPath $RunnerRoot -PathType Container)) { return [pscustomobject]@{ Exists = $false; Valid = $false; Active = $false; Reason = 'missing' } }
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) { return [pscustomobject]@{ Exists = $true; Valid = $false; Active = $false; Reason = 'runner state is missing' } }
    try { $state = [IO.File]::ReadAllText($statePath) | ConvertFrom-Json } catch { return [pscustomobject]@{ Exists = $true; Valid = $false; Active = $false; Reason = 'runner state is malformed' } }
    $labels = @((Get-CodexSetupProperty $state 'labels' @()) | ForEach-Object { if ($_ -is [string]) { $_ } else { [string](Get-CodexSetupProperty $_ 'name' '') } })
    $expectedUrl = "https://github.com/$Repository"
    $actualUrl = ([string](Get-CodexSetupProperty $state 'repositoryUrl' ("https://github.com/" + [string](Get-CodexSetupProperty $state 'repository' '')))).TrimEnd('/')
    $version = [string](Get-CodexSetupProperty $state 'version' '')
    $hash = [string](Get-CodexSetupProperty $state 'sha256' '')
    $metadataName = [string](Get-CodexSetupProperty $state 'runnerName' '')
    $valid = $actualUrl.Equals($expectedUrl, [StringComparison]::OrdinalIgnoreCase) -and $metadataName -eq $RunnerName -and $labels -contains $Label -and (Test-Path -LiteralPath (Join-Path $RunnerRoot 'run.cmd') -PathType Leaf)
    if ($valid -and $null -ne $Asset -and -not [string]::IsNullOrWhiteSpace($Asset.Name)) { $valid = [string](Get-CodexSetupProperty $state 'assetName' '') -eq $Asset.Name }
    if ($valid -and $null -ne $Asset -and -not [string]::IsNullOrWhiteSpace($Asset.Tag)) { $valid = [string](Get-CodexSetupProperty $state 'releaseTag' '') -eq $Asset.Tag }
    if ($valid -and $null -ne $Asset -and -not [string]::IsNullOrWhiteSpace($Asset.Version)) { $valid = $version -eq $Asset.Version }
    if ($valid -and $null -ne $Asset) { $valid = $hash.Equals([string]$Asset.Sha256, [StringComparison]::OrdinalIgnoreCase) }
    $processes = if ($null -ne $ProcessProvider) { @(& $ProcessProvider) } else { @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match '(?i)^(Runner\.Listener|runsvc|run)$' }) }
    [pscustomobject][ordered]@{ Exists = $true; Valid = [bool]$valid; Active = @($processes).Count -gt 0; Reason = if ($valid) { 'matching configured runner' } else { 'runner configuration does not match expected repository, label, version, or hash' }; State = $state }
}

function Get-CodexRunnerInventory {
    param([string] $Repository, [object] $CommandRunner)
    $result = Invoke-CodexInstallCommand -FilePath 'gh.exe' -Arguments @('api',"repos/$Repository/actions/runners") -CommandRunner $CommandRunner
    if (-not (Test-CodexSetupSuccess $result)) { throw 'GitHub Actions runner inventory could not be read.' }
    try { return (Get-CodexSetupText $result | ConvertFrom-Json) } catch { throw 'GitHub Actions runner inventory returned malformed JSON.' }
}

function Assert-CodexRunnerInventoryCompatibility {
    param([object] $Payload, [string] $RunnerName, [string] $Label, [bool] $Reuse)
    $runners = @((Get-CodexSetupProperty $Payload 'runners' @()))
    $named = @($runners | Where-Object { [string](Get-CodexSetupProperty $_ 'name' '') -eq $RunnerName })
    if ($named.Count -gt 1) { throw "GitHub Actions has multiple runners named '$RunnerName'." }
    if ($named.Count -eq 0) { if ($Reuse) { throw "The configured runner '$RunnerName' is missing from GitHub Actions inventory." }; return $null }
    $labels = @((Get-CodexSetupProperty $named[0] 'labels' @()) | ForEach-Object { if ($_ -is [string]) { $_ } else { [string](Get-CodexSetupProperty $_ 'name' '') } })
    if (-not $Reuse -or $labels -notcontains $Label) {
        throw "GitHub Actions runner '$RunnerName' is already registered incompatibly."
    }
    return $named[0]
}

function Assert-CodexRunnerIdentity {
    param([object] $Payload, [string] $Repository, [string] $RunnerName, [string] $Label)
    $runners = @((Get-CodexSetupProperty $Payload 'runners' @()))
    $named = @($runners | Where-Object { [string](Get-CodexSetupProperty $_ 'name' '') -eq $RunnerName })
    if ($named.Count -ne 1) { throw "Expected one uniquely registered runner identity named '$RunnerName'; found $($named.Count)." }
    $labels = @((Get-CodexSetupProperty $named[0] 'labels' @()) | ForEach-Object { if ($_ -is [string]) { $_ } else { [string](Get-CodexSetupProperty $_ 'name' '') } })
    if ($labels -notcontains $Label) { throw "Registered runner '$RunnerName' is missing label '$Label'." }
    $remoteRepository = [string](Get-CodexSetupProperty $named[0] 'repository' '')
    if (-not [string]::IsNullOrWhiteSpace($remoteRepository) -and $remoteRepository.TrimEnd('/') -notin @($Repository, "https://github.com/$Repository")) { throw "Registered runner '$RunnerName' has an unexpected repository identity." }
    return $named[0]
}

function Assert-CodexRunnerRegistration {
    param([object] $Payload, [string] $RunnerName, [string] $Label)
    $runners = @((Get-CodexSetupProperty $Payload 'runners' @()))
    $named = @($runners | Where-Object { [string](Get-CodexSetupProperty $_ 'name' '') -eq $RunnerName })
    if ($named.Count -ne 1) { throw "Expected one uniquely registered runner named '$RunnerName'; found $($named.Count)." }
    $labels = @((Get-CodexSetupProperty $named[0] 'labels' @()) | ForEach-Object { if ($_ -is [string]) { $_ } else { [string](Get-CodexSetupProperty $_ 'name' '') } })
    if ([string](Get-CodexSetupProperty $named[0] 'status' '') -ne 'online' -or $labels -notcontains $Label) {
        throw "Expected registered runner '$RunnerName' to be online with label '$Label'."
    }
    return $named[0]
}

function Assert-CodexPathHasNoReparseAncestor {
    param([Parameter(Mandatory = $true)] [string] $Path, [scriptblock] $PathInspector)
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($true) {
        if (Test-Path -LiteralPath $cursor) {
            $inspection = if ($null -ne $PathInspector) { & $PathInspector $cursor } else { Get-Item -LiteralPath $cursor -Force -ErrorAction Stop }
            $isReparse = Test-CodexPathInspectionIsReparse -Inspection $inspection
            if ($isReparse) { throw "Refusing reparse-point path ancestry: $cursor" }
        }
        $parent = [IO.Directory]::GetParent($cursor)
        if ($null -eq $parent -or $parent.FullName.Equals($cursor, [StringComparison]::OrdinalIgnoreCase)) { break }
        $cursor = $parent.FullName
    }
}

function Test-CodexPathInspectionIsReparse {
    param([object] $Inspection)
    if ($Inspection -is [bool]) { return [bool]$Inspection }
    $property = $Inspection.PSObject.Properties['IsReparsePoint']
    if ($null -ne $property) { return [bool]$property.Value }
    $link = $Inspection.PSObject.Properties['LinkType']
    $attributes = $Inspection.PSObject.Properties['Attributes']
    return ($null -ne $link -and -not [string]::IsNullOrWhiteSpace([string]$link.Value)) -or ($null -ne $attributes -and ([IO.FileAttributes]$attributes.Value -band [IO.FileAttributes]::ReparsePoint) -ne 0)
}

function Assert-CodexPathTreeHasNoReparsePoint {
    param([Parameter(Mandatory = $true)] [string] $Path, [scriptblock] $PathInspector)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return }
    Assert-CodexPathHasNoReparseAncestor -Path $Path -PathInspector $PathInspector
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push([IO.Path]::GetFullPath($Path))
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        foreach ($item in @(Get-ChildItem -LiteralPath $current -Force -ErrorAction Stop)) {
            $inspection = if ($null -ne $PathInspector) { & $PathInspector $item.FullName } else { Get-Item -LiteralPath $item.FullName -Force -ErrorAction Stop }
            if (Test-CodexPathInspectionIsReparse -Inspection $inspection) { throw "Refusing reparse-point path: $($item.FullName)" }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
        }
    }
}

function Remove-CodexOwnedPath {
    param([Parameter(Mandatory = $true)] [string] $Path, [scriptblock] $PathInspector)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    try {
        Assert-CodexPathHasNoReparseAncestor -Path $Path -PathInspector $PathInspector
        $root = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
        $rootInspection = if ($null -ne $PathInspector) { & $PathInspector $Path } else { $root }
        if (Test-CodexPathInspectionIsReparse -Inspection $rootInspection) {
            # A swapped root is removed as a link only; never recurse through it.
            Assert-CodexPathHasNoReparseAncestor -Path (Split-Path -Parent $Path) -PathInspector $PathInspector
            Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
            return
        }
        if ($root.PSIsContainer) {
            # Walk the owned tree explicitly. Remove-Item -Recurse is never
            # used: every child is inspected immediately before its own
            # operation, and a swapped link is deleted as a link only.
            foreach ($child in @(Get-ChildItem -LiteralPath $Path -Force -ErrorAction Stop)) {
                Remove-CodexOwnedPath -Path $child.FullName -PathInspector $PathInspector
            }
        }
        Assert-CodexPathHasNoReparseAncestor -Path $Path -PathInspector $PathInspector
        Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    } catch { return }
}

function Open-CodexStagingPin {
    param([Parameter(Mandatory = $true)] [string] $Path, [scriptblock] $PathInspector)
    Assert-CodexPathHasNoReparseAncestor -Path $Path -PathInspector $PathInspector
    $marker = Join-Path $Path '.installer-pin'
    Assert-CodexPathHasNoReparseAncestor -Path $marker -PathInspector $PathInspector
    $stream = [IO.File]::Open($marker, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    return [pscustomobject][ordered]@{ Path = $Path; Marker = $marker; Stream = $stream }
}

function Close-CodexStagingPin {
    param([object] $Pin)
    if ($null -eq $Pin) { return }
    try { if ($null -ne $Pin.Stream) { $Pin.Stream.Dispose() } } catch { }
}

function Get-CodexAclRuleSid {
    param([Parameter(Mandatory = $true)] [object] $Rule)
    try { return $Rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]) }
    catch { throw "Could not resolve ACL identity '$($Rule.IdentityReference)'." }
}

function Assert-CodexTrustedDirectoryAcl {
    param([Parameter(Mandatory = $true)] [string] $Path)
    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    if (-not $acl.AreAccessRulesProtected) { throw "Trusted directory ACL remains inheritable: $Path" }
    $currentSid = Get-CodexCurrentUserSid
    $expected = @(
        [pscustomobject]@{ Sid = $currentSid; Rights = [Security.AccessControl.FileSystemRights]::FullControl }
        [pscustomobject]@{ Sid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18'); Rights = [Security.AccessControl.FileSystemRights]::FullControl }
        [pscustomobject]@{ Sid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'); Rights = [Security.AccessControl.FileSystemRights]::FullControl }
    )
    $rules = @($acl.Access)
    if ($rules.Count -ne $expected.Count) { throw "Trusted directory ACL contains unexpected access entries: $Path" }
    $ownerSid = $acl.GetOwner([Security.Principal.SecurityIdentifier])
    if ($ownerSid.Value -ne $currentSid.Value) { throw "Trusted directory owner is not the current user: $Path" }
    foreach ($item in $expected) {
        $matching = @($rules | Where-Object {
            (Get-CodexAclRuleSid -Rule $_).Value -eq $item.Sid.Value -and
            $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            -not $_.IsInherited -and $_.FileSystemRights -eq $item.Rights -and
            (($_.InheritanceFlags -band ([Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit)) -eq ([Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit)) -and
            $_.PropagationFlags -eq [Security.AccessControl.PropagationFlags]::None
        })
        if ($matching.Count -ne 1) { throw "Trusted directory ACL is missing the required entry for $($item.Sid.Value): $Path" }
    }
}

function Ensure-CodexTrustedDirectory {
    param([Parameter(Mandatory = $true)] [string] $Path, [scriptblock] $PathInspector)
    if ($null -ne $PathInspector) {
        $directInspection = & $PathInspector $Path
        if (Test-CodexPathInspectionIsReparse -Inspection $directInspection) { throw "Refusing reparse-point trusted directory: $Path" }
    }
    Assert-CodexPathHasNoReparseAncestor -Path $Path -PathInspector $PathInspector
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
    Assert-CodexPathHasNoReparseAncestor -Path $Path -PathInspector $PathInspector
    try {
        # Protect the DACL and purge every existing explicit identity before
        # adding the tiny allow-list. This preserves the existing security
        # descriptor's owner/audit metadata while removing broad ACEs alike.
        $directory = [IO.DirectoryInfo]::new($Path)
        $existingAcl = $directory.GetAccessControl([Security.AccessControl.AccessControlSections]::Access)
        $acl = $existingAcl
        $existingRules = @($acl.Access)
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($existingRule in $existingRules) { $acl.PurgeAccessRules($existingRule.IdentityReference) }
        $currentSid = Get-CodexCurrentUserSid
        $existingOwner = $existingAcl.GetOwner([Security.Principal.SecurityIdentifier])
        if ($null -eq $existingOwner -or $existingOwner.Value -ne $currentSid.Value) { $acl.SetOwner($currentSid) }
        $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
        foreach ($sid in @($currentSid, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
            $rule = [Security.AccessControl.FileSystemAccessRule]::new($sid, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
            $acl.AddAccessRule($rule)
        }
        $directory.SetAccessControl($acl)
        Assert-CodexTrustedDirectoryAcl -Path $Path
    } catch {
        throw "Trusted directory ACL could not be established: $($_.Exception.Message)"
    }
}

function Ensure-CodexTrustedDataRoot {
    param([Parameter(Mandatory = $true)] [string] $Path, [scriptblock] $PathInspector)
    Ensure-CodexTrustedDirectory -Path $Path -PathInspector $PathInspector
}

function Get-CodexInstallerStateSnapshot {
    param([Parameter(Mandatory = $true)] [string] $ConfigPath, [scriptblock] $PathInspector)
    $paths = @($ConfigPath, "$ConfigPath.tmp")
    $items = [ordered]@{}
    foreach ($path in $paths) {
        Assert-CodexPathHasNoReparseAncestor -Path $path -PathInspector $PathInspector
        $exists = Test-Path -LiteralPath $path -PathType Leaf
        $items[$path] = [pscustomobject][ordered]@{
            Exists = [bool]$exists
            Bytes = if ($exists) { [IO.File]::ReadAllBytes($path) } else { $null }
        }
    }
    return [pscustomobject][ordered]@{ Items = $items }
}

function Restore-CodexInstallerStateSnapshot {
    param([Parameter(Mandatory = $true)] [object] $Snapshot, [scriptblock] $PathInspector)
    foreach ($entry in $Snapshot.Items.GetEnumerator()) {
        $path = [string]$entry.Key
        $state = $entry.Value
        try {
            Assert-CodexPathHasNoReparseAncestor -Path $path -PathInspector $PathInspector
            if ($state.Exists) {
                $parent = Split-Path -Parent $path
                Assert-CodexPathHasNoReparseAncestor -Path $parent -PathInspector $PathInspector
                if (-not (Test-Path -LiteralPath $parent -PathType Container)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
                $temporary = "$path.rollback-$([Guid]::NewGuid().ToString('N')).tmp"
                try {
                    [IO.File]::WriteAllBytes($temporary, [byte[]]$state.Bytes)
                    Assert-CodexPathHasNoReparseAncestor -Path $path -PathInspector $PathInspector
                    Move-Item -LiteralPath $temporary -Destination $path -Force
                } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue } }
            } elseif (Test-Path -LiteralPath $path) {
                Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
            }
        } catch { }
    }
}

function Normalize-CodexTaskXml {
    param([AllowNull()][string] $Xml)
    if ([string]::IsNullOrWhiteSpace($Xml)) { return '' }
    try {
        $document = [Xml.XmlDocument]::new()
        $document.PreserveWhitespace = $false
        $document.LoadXml($Xml)
        foreach ($description in @($document.SelectNodes("//*[local-name()='RegistrationInfo']/*[local-name()='Description']"))) {
            if ($description.InnerText -match '(?i)^Automation Workbench installer ownership marker:\s*[0-9a-f]{32}$') {
                $description.InnerText = 'Automation Workbench installer ownership marker'
            }
        }
        return $document.OuterXml
    } catch { return $Xml.Trim() }
}

function Get-CodexTaskOwnershipMarker {
    param([AllowNull()][string] $Xml)
    if ([string]::IsNullOrWhiteSpace($Xml)) { return $null }
    try {
        $document = [Xml.XmlDocument]::new(); $document.LoadXml($Xml)
        foreach ($description in @($document.SelectNodes("//*[local-name()='RegistrationInfo']/*[local-name()='Description']"))) {
            $match = [regex]::Match($description.InnerText, '(?i)^Automation Workbench installer ownership marker:\s*(?<marker>[0-9a-f]{32})$')
            if ($match.Success) { return $match.Groups['marker'].Value.ToLowerInvariant() }
        }
    } catch { return $null }
    return $null
}

function Get-CodexScheduledTaskPreflight {
    param([Parameter(Mandatory = $true)] [object] $Task, [scriptblock] $TaskQueryRunner, [object] $CommandRunner)
    $request = [pscustomobject][ordered]@{ FilePath = 'schtasks.exe'; Arguments = @('/Query','/TN',$Task.Name,'/XML'); Task = $Task; Action = 'Query' }
    $result = if ($null -ne $TaskQueryRunner) { & $TaskQueryRunner $request } else { Invoke-CodexInstallCommand -FilePath 'schtasks.exe' -Arguments $request.Arguments -CommandRunner $CommandRunner }
    $existsProperty = $result.PSObject.Properties['Exists']
    if ($null -ne $existsProperty) {
        if (-not [bool]$existsProperty.Value) { return [pscustomobject]@{ Exists = $false; Owned = $false; Task = $Task } }
        $actual = [string](Get-CodexSetupProperty $result 'Xml' (Get-CodexSetupText $result))
        $marker = Get-CodexTaskOwnershipMarker -Xml $actual
        if ([string]::IsNullOrWhiteSpace($marker)) { throw "Scheduled task '$($Task.Name)' exists but has no installer ownership marker." }
        if ((Normalize-CodexTaskXml $actual) -ne (Normalize-CodexTaskXml $Task.Xml)) { throw "Scheduled task '$($Task.Name)' exists but is not the installer-owned definition." }
        return [pscustomobject]@{ Exists = $true; Owned = $true; Marker = $marker; Xml = $actual; Task = $Task }
    }
    if (-not (Test-CodexSetupSuccess $result)) {
        $queryText = ((Get-CodexSetupProperty $result 'Stdout' '') + ' ' + (Get-CodexSetupProperty $result 'Stderr' ''))
        if ($queryText -match '(?i)(task.*(not found|does not exist)|cannot find.*(specified|task))') {
            return [pscustomobject]@{ Exists = $false; Owned = $false; Task = $Task }
        }
        throw "Could not query scheduled task '$($Task.Name)' before installation."
    }
    $actualXml = Get-CodexSetupText $result
    # Test seams and older schtasks wrappers may return a successful textual
    # probe without XML; only an actual Task document proves the name exists.
    if ($actualXml -notmatch '(?i)<Task(?:\s|>)') { return [pscustomobject]@{ Exists = $false; Owned = $false; Task = $Task } }
    $marker = Get-CodexTaskOwnershipMarker -Xml $actualXml
    if ([string]::IsNullOrWhiteSpace($marker)) { throw "Scheduled task '$($Task.Name)' exists but has no installer ownership marker." }
    if ((Normalize-CodexTaskXml $actualXml) -ne (Normalize-CodexTaskXml $Task.Xml)) { throw "Scheduled task '$($Task.Name)' exists but is not the installer-owned definition." }
    return [pscustomobject]@{ Exists = $true; Owned = $true; Marker = $marker; Xml = $actualXml; Task = $Task }
}

function Confirm-CodexScheduledTaskAttemptOwnership {
    param([Parameter(Mandatory = $true)] [object] $Task, [Parameter(Mandatory = $true)] [string] $Marker, [scriptblock] $TaskQueryRunner, [object] $CommandRunner)
    try {
        $definition = Get-CodexScheduledTaskPreflight -Task $Task -TaskQueryRunner $TaskQueryRunner -CommandRunner $CommandRunner
        return [bool]$definition.Exists -and [string]$definition.Marker -eq $Marker
    } catch {
        # A foreign or unverifiable definition is never ours to remove.
        return $false
    }
}

function Invoke-CodexRunnerRemoteRemoval {
    param([string] $Repository, [string] $ConfigPath, [object] $CommandRunner)
    try {
        $result = Invoke-CodexInstallCommand -FilePath 'gh.exe' -Arguments @('api','--method','POST',"repos/$Repository/actions/runners/remove-token") -CommandRunner $CommandRunner
        if (-not (Test-CodexSetupSuccess $result)) { return }
        $payload = Get-CodexSetupText $result | ConvertFrom-Json
        $removalToken = [string](Get-CodexSetupProperty $payload 'token' '')
        if ([string]::IsNullOrWhiteSpace($removalToken)) { return }
        try { Invoke-CodexInstallCommand -FilePath $ConfigPath -Arguments @('remove','--unattended','--token',$removalToken) -WorkingDirectory (Split-Path -Parent $ConfigPath) -CommandRunner $CommandRunner | Out-Null } catch { }
    } catch { }
}

function Get-CodexLocalWorkerPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $Repository,
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [string] $DataRoot,
        [object] $Config,
        [scriptblock] $PathInspector
    )
    $root = [IO.Path]::GetFullPath($RepositoryRoot)
    if ([string]::IsNullOrWhiteSpace($DataRoot)) { $DataRoot = Join-Path $env:LOCALAPPDATA 'AutomationWorkbench\CodexWorker' }
    $data = [IO.Path]::GetFullPath($DataRoot)
    Assert-CodexPathHasNoReparseAncestor -Path $root -PathInspector $PathInspector
    Assert-CodexPathHasNoReparseAncestor -Path $data -PathInspector $PathInspector
    Assert-CodexPathTreeHasNoReparsePoint -Path $data -PathInspector $PathInspector
    $rootPrefix = $root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($data.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or $data.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Worker data must be outside the repository.'
    }
    $label = [string](Get-CodexSetupProperty $Config 'runnerLabel' 'agentassist-local')
    $runnerRoot = [string](Get-CodexSetupProperty $Config 'runnerRoot' (Join-Path $data 'runner'))
    $runnerRoot = [IO.Path]::GetFullPath($runnerRoot)
    $dataPrefix = $data.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $repositoryPrefix = $root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($runnerRoot.Equals($data, [StringComparison]::OrdinalIgnoreCase) -or -not $runnerRoot.StartsWith($dataPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Worker runner root must be a descendant of the trusted worker data root.'
    }
    if ($runnerRoot.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or $runnerRoot.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Worker runner root must remain outside the repository.'
    }
    Assert-CodexPathHasNoReparseAncestor -Path $runnerRoot -PathInspector $PathInspector
    Assert-CodexPathTreeHasNoReparsePoint -Path $runnerRoot -PathInspector $PathInspector
    $runnerName = [string](Get-CodexSetupProperty $Config 'runnerName' 'AutomationWorkbenchCodexRunner')
    $configPath = Join-Path $data 'config.json'
    $runnerScript = Join-Path $root 'scripts\codex-worker\Start-GitHubRunner.ps1'
    $notifierScript = Join-Path $root 'scripts\codex-worker\Invoke-DeploymentNotifier.ps1'
    $runnerArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-WindowStyle','Hidden','-File',$runnerScript,'-RunnerRoot',$runnerRoot,'-ConfigPath',$configPath)
    $notifierArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-Sta','-WindowStyle','Hidden','-File',$notifierScript,'-Watch','-ConfigPath',$configPath)
    $userId = Get-CodexCurrentUserId
    $ownershipMarker = [Guid]::NewGuid().ToString('N')
    $runnerXml = New-CodexScheduledTaskXml -UserId $userId -FilePath 'pwsh.exe' -Arguments $runnerArgs -OwnershipMarker $ownershipMarker
    $notifierXml = New-CodexScheduledTaskXml -UserId $userId -FilePath 'pwsh.exe' -Arguments $notifierArgs -OwnershipMarker $ownershipMarker
    return [pscustomobject][ordered]@{
        Repository = $Repository; RepositoryRoot = $root; DataRoot = $data; ConfigPath = $configPath; TaskOwnershipMarker = $ownershipMarker
        Runner = [pscustomobject][ordered]@{ Root = $runnerRoot; MetadataPath = (Join-Path $runnerRoot 'runner-install.json'); ServiceMode = $false; Label = $label; Name = $runnerName; Reuse = $false }
        Tasks = [pscustomobject][ordered]@{
            Runner = [pscustomobject][ordered]@{ Name = 'AutomationWorkbenchCodexRunner'; LogonTrigger = $true; Hidden = $true; FilePath = 'pwsh.exe'; Arguments = [string[]]$runnerArgs; Xml = $runnerXml }
            Notifier = [pscustomobject][ordered]@{ Name = 'AutomationWorkbenchCodexDeploymentNotifier'; LogonTrigger = $true; Hidden = $true; FilePath = 'pwsh.exe'; Arguments = [string[]]$notifierArgs; Xml = $notifierXml }
        }
    }
}

function Invoke-CodexAuthSmoke {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $CodexCommand,
        [object] $CommandRunner,
        [string] $TemporaryGitPath
    )
    $created = $false
    if ([string]::IsNullOrWhiteSpace($TemporaryGitPath)) {
        $TemporaryGitPath = Join-Path ([IO.Path]::GetTempPath()) ('codex-auth-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $TemporaryGitPath -Force | Out-Null
        $created = $true
    }
    try {
        $git = Invoke-CodexInstallCommand -FilePath 'git.exe' -Arguments @('init','--quiet') -WorkingDirectory $TemporaryGitPath -CommandRunner $CommandRunner
        if (-not (Test-CodexSetupSuccess $git)) { throw 'Temporary Git repository initialization failed.' }
        $result = Invoke-CodexInstallCommand -FilePath $CodexCommand -Arguments @('exec','--ephemeral','--sandbox','read-only','Reply exactly READY') -WorkingDirectory $TemporaryGitPath -CommandRunner $CommandRunner
        if (-not (Test-CodexSetupSuccess $result) -or (Get-CodexSetupText $result).Trim() -notmatch '(?m)^READY\s*$') { throw 'Codex authentication smoke test did not return READY.' }
        return $true
    } finally {
        if ($created -and (Test-Path -LiteralPath $TemporaryGitPath)) { Remove-Item -LiteralPath $TemporaryGitPath -Recurse -Force }
    }
}

function Invoke-CodexLocalWorkerSetup {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [object] $Config,
        [string] $Repository,
        [string] $RepositoryRoot,
        [string] $DataRoot,
        [object] $CommandRunner,
        [scriptblock] $DownloadRunner,
        [scriptblock] $HashRunner,
        [scriptblock] $ExtractRunner,
        [scriptblock] $TaskRunner,
        [scriptblock] $TaskQueryRunner,
        [scriptblock] $TaskStartRunner,
        [scriptblock] $TaskStopRunner,
        [scriptblock] $TaskRemoveRunner,
        [scriptblock] $DelayRunner,
        [scriptblock] $PathInspector,
        [scriptblock] $MetadataWriter,
        [scriptblock] $MoveRunner,
        [int] $OnlinePollAttempts = 10,
        [int] $PollDelayMilliseconds = 1000,
        [scriptblock] $ProcessProvider,
        [string] $TemporaryGitPath,
        [switch] $SkipPrerequisiteProbe,
        [switch] $WhatIf
    )
    if ([string]::IsNullOrWhiteSpace($Repository)) { $Repository = [string](Get-CodexSetupProperty $Config 'repository' '') }
    if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = [string](Get-CodexSetupProperty $Config 'repositoryRoot' (Get-Location).Path) }
    $plan = Get-CodexLocalWorkerPlan -Repository $Repository -RepositoryRoot $RepositoryRoot -DataRoot $DataRoot -Config $Config -PathInspector $PathInspector
    $whatIf = [bool]$WhatIf
    $mutations = 0
    $stateSnapshot = Get-CodexInstallerStateSnapshot -ConfigPath $plan.ConfigPath -PathInspector $PathInspector
    $taskPreflight = @()
    if (-not $whatIf) {
        foreach ($scriptPath in @((Join-Path $plan.RepositoryRoot 'scripts\codex-worker\Start-GitHubRunner.ps1'), (Join-Path $plan.RepositoryRoot 'scripts\codex-worker\Invoke-DeploymentNotifier.ps1'))) {
            if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) { throw "Required task target does not exist: $scriptPath" }
        }
        foreach ($task in @($plan.Tasks.Runner, $plan.Tasks.Notifier)) {
            $taskPreflight += Get-CodexScheduledTaskPreflight -Task $task -TaskQueryRunner $TaskQueryRunner -CommandRunner $CommandRunner
        }
        # Establish the trusted boundary before any installer state or runner
        # bytes are written. Reapply it on every run, including an existing
        # .staging directory left by an interrupted attempt.
        $dataRootExisted = Test-Path -LiteralPath $plan.DataRoot -PathType Container
        try {
            Ensure-CodexTrustedDataRoot -Path $plan.DataRoot -PathInspector $PathInspector
            Ensure-CodexTrustedDirectory -Path (Join-Path $plan.DataRoot '.staging') -PathInspector $PathInspector
        } catch {
            if (-not $dataRootExisted -and (Test-Path -LiteralPath $plan.DataRoot)) { Remove-CodexOwnedPath -Path $plan.DataRoot -PathInspector $PathInspector }
            throw
        }
    }
    $prereqs = @(Get-CodexPrerequisitePlan -Config $Config -CommandRunner $CommandRunner -Probe)
    $policies = @($prereqs | ForEach-Object { $_.Policy })
    $ghAuthResult = Invoke-CodexInstallCommand -FilePath 'gh.exe' -Arguments @('auth','status','--hostname','github.com') -CommandRunner $CommandRunner
    $ghAuthenticated = Test-CodexSetupSuccess $ghAuthResult
    if (-not $whatIf -and -not $ghAuthenticated) { throw 'GitHub CLI authentication is required before setup.' }
    $requiredFailures = @($policies | Where-Object { $_.Name -ne 'Codex CLI' -and -not $_.Valid })
    if (-not $whatIf -and $requiredFailures.Count -gt 0) { throw ('Required prerequisites failed: ' + (($requiredFailures | ForEach-Object { "$($_.Name): $($_.Error)" }) -join '; ')) }
    $codexPrereq = @($prereqs | Where-Object Name -eq 'Codex CLI')[0]
    $codex = [string](Get-CodexSetupProperty $Config 'codexCommand' 'codex')
    if (-not $whatIf) {
        $missing = $null -eq $codexPrereq -or -not [bool]$codexPrereq.Installed
        if (-not $missing -and -not [bool]$codexPrereq.Policy.Valid) { throw "Codex CLI prerequisite failed: $($codexPrereq.Policy.Error)." }
        if ($missing) {
            $install = Invoke-CodexInstallCommand -FilePath 'npm.cmd' -Arguments @('install','--global','@openai/codex') -CommandRunner $CommandRunner
            if (-not (Test-CodexSetupSuccess $install)) { throw 'Codex CLI installation failed.' }
            $mutations++
            $reprobe = @(Get-CodexPrerequisitePlan -Config $Config -CommandRunner $CommandRunner -Probe | Where-Object Name -eq 'Codex CLI')[0]
            if ($null -eq $reprobe -or -not $reprobe.Policy.Valid) { throw 'Codex CLI is still missing or has an unparseable version after installation.' }
            $login = Invoke-CodexInstallCommand -FilePath $codex -Arguments @('login') -CommandRunner $CommandRunner -Interactive
            if (-not (Test-CodexSetupSuccess $login)) { throw 'Codex login was not completed.' }
        }
        try { Invoke-CodexAuthSmoke -CodexCommand $codex -CommandRunner $CommandRunner -TemporaryGitPath $TemporaryGitPath | Out-Null }
        catch {
            $login = Invoke-CodexInstallCommand -FilePath $codex -Arguments @('login') -CommandRunner $CommandRunner -Interactive
            if (-not (Test-CodexSetupSuccess $login)) { throw 'Codex login was not completed.' }
            Invoke-CodexAuthSmoke -CodexCommand $codex -CommandRunner $CommandRunner -TemporaryGitPath $TemporaryGitPath | Out-Null
        }
    }

    $release = Get-CodexSetupProperty $Config 'runnerRelease' $null
    if ($null -eq $release -and -not $whatIf) {
        $releaseResult = Invoke-CodexInstallCommand -FilePath 'gh.exe' -Arguments @('api','repos/actions/runner/releases/latest') -CommandRunner $CommandRunner
        if (-not (Test-CodexSetupSuccess $releaseResult)) { throw 'Could not read the latest GitHub Actions runner release.' }
        $release = Get-CodexSetupText $releaseResult | ConvertFrom-Json
    }
    $asset = $null
    if ($null -ne $release) { $asset = Resolve-CodexRunnerAsset -Release $release }
    $inventoryPayload = $null
    if ($null -ne $asset) {
        try { $inventoryPayload = Get-CodexRunnerInventory -Repository $Repository -CommandRunner $CommandRunner }
        catch { if (-not $whatIf) { throw }; $inventoryPayload = $null }
    }
    $existing = if ($null -ne $asset) { Get-CodexExistingRunnerState -RunnerRoot $plan.Runner.Root -Repository $Repository -Label $plan.Runner.Label -RunnerName $plan.Runner.Name -Asset $asset -ProcessProvider $ProcessProvider } else { [pscustomobject]@{ Exists = $false; Valid = $false; Active = $false; Reason = 'release not resolved' } }
    if ($existing.Valid) { $plan.Runner.Reuse = $true }
    elseif ($existing.Exists -and -not $whatIf) {
        if ($existing.Active) { throw "Existing runner is active but does not match the expected repository, label, version, and hash; refusing replacement." }
        throw "Existing runner cannot be safely reused: $($existing.Reason). Refusing in-place replacement."
    }
    $registeredRunner = $null
    if ($null -ne $inventoryPayload) {
        try { $registeredRunner = Assert-CodexRunnerInventoryCompatibility -Payload $inventoryPayload -RunnerName $plan.Runner.Name -Label $plan.Runner.Label -Reuse:$plan.Runner.Reuse }
        catch { if (-not $whatIf) { throw }; $registeredRunner = $null }
    }
    $runnerConfigured = $false
    $runnerPromoted = $false
    $runnerOwnedPath = $null
    $createdTaskNames = [Collections.Generic.List[string]]::new()
    $reusedTaskNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($preflight in @($taskPreflight | Where-Object Exists)) { [void]$reusedTaskNames.Add($preflight.Task.Name) }
    $stagingPin = $null
    if (-not $whatIf -and -not $plan.Runner.Reuse) {
        $stagingRoot = Join-Path $plan.DataRoot ('.staging\runner-' + [Guid]::NewGuid().ToString('N'))
        Assert-CodexPathHasNoReparseAncestor -Path $stagingRoot -PathInspector $PathInspector
        New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
        Assert-CodexPathHasNoReparseAncestor -Path $stagingRoot -PathInspector $PathInspector
        Ensure-CodexTrustedDirectory -Path $stagingRoot -PathInspector $PathInspector
        $stagingPin = Open-CodexStagingPin -Path $stagingRoot -PathInspector $PathInspector
        try {
            $archive = Join-Path $stagingRoot $asset.Name
            Assert-CodexPathHasNoReparseAncestor -Path $stagingRoot -PathInspector $PathInspector
            Assert-CodexPathHasNoReparseAncestor -Path $archive -PathInspector $PathInspector
            $downloadRequest = [pscustomobject][ordered]@{ Uri = $asset.Uri; Destination = $archive }
            if ($null -ne $DownloadRunner) { & $DownloadRunner $downloadRequest } else { Invoke-WebRequest -UseBasicParsing -Uri $asset.Uri -OutFile $archive }
            $actualHash = if ($null -ne $HashRunner) { [string](& $HashRunner $archive) } else { (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash }
            if ($actualHash.ToLowerInvariant() -ne $asset.Sha256) { throw 'Downloaded runner checksum does not match the release checksum.' }
            Assert-CodexPathHasNoReparseAncestor -Path $archive -PathInspector $PathInspector
            Assert-CodexPathHasNoReparseAncestor -Path $stagingRoot -PathInspector $PathInspector
            $extractRequest = [pscustomobject][ordered]@{ Archive = $archive; Destination = $stagingRoot }
            if ($null -ne $ExtractRunner) { & $ExtractRunner $extractRequest } else { Expand-Archive -LiteralPath $archive -DestinationPath $stagingRoot -Force }
            Assert-CodexPathHasNoReparseAncestor -Path $archive -PathInspector $PathInspector
            if (Test-Path -LiteralPath $archive -PathType Leaf) { Remove-Item -LiteralPath $archive -Force }
            $tokenResult = Invoke-CodexInstallCommand -FilePath 'gh.exe' -Arguments @('api','--method','POST',"repos/$Repository/actions/runners/registration-token") -CommandRunner $CommandRunner
            if (-not (Test-CodexSetupSuccess $tokenResult)) { throw 'Could not obtain a runner registration token.' }
            $tokenPayload = Get-CodexSetupText $tokenResult | ConvertFrom-Json
            $token = [string](Get-CodexSetupProperty $tokenPayload 'token' '')
            if ([string]::IsNullOrWhiteSpace($token)) { throw 'GitHub returned an empty runner registration token.' }
            $configPath = Join-Path $stagingRoot 'config.cmd'
            $runnerConfigArgs = @('--unattended','--replace','--url',"https://github.com/$Repository",'--token',$token,'--name',$plan.Runner.Name,'--labels',$plan.Runner.Label,'--work','_work')
            Assert-CodexPathHasNoReparseAncestor -Path $configPath -PathInspector $PathInspector
            Assert-CodexPathHasNoReparseAncestor -Path $stagingRoot -PathInspector $PathInspector
            try { $configured = Invoke-CodexInstallCommand -FilePath $configPath -Arguments $runnerConfigArgs -WorkingDirectory $stagingRoot -CommandRunner $CommandRunner }
            catch { throw 'GitHub Actions runner configuration failed.' }
            if (-not (Test-CodexSetupSuccess $configured)) { throw 'GitHub Actions runner configuration failed.' }
            $runnerConfigured = $true
            $runnerOwnedPath = $stagingRoot
            $postInventory = Get-CodexRunnerInventory -Repository $Repository -CommandRunner $CommandRunner
            $registeredRunner = Assert-CodexRunnerIdentity -Payload $postInventory -Repository $Repository -RunnerName $plan.Runner.Name -Label $plan.Runner.Label
            $metadata = [ordered]@{
                schemaVersion = 1; assetName = $asset.Name; releaseTag = $asset.Tag; version = $asset.Version
                sha256 = $asset.Sha256; repository = $Repository; repositoryUrl = "https://github.com/$Repository"
                runnerName = $plan.Runner.Name; labels = @($plan.Runner.Label)
            }
            $metadataRequest = [pscustomobject][ordered]@{ Path = (Join-Path $stagingRoot 'runner-install.json'); Content = ($metadata | ConvertTo-Json -Depth 10) }
            Assert-CodexPathHasNoReparseAncestor -Path $metadataRequest.Path -PathInspector $PathInspector
            if ($null -ne $MetadataWriter) { & $MetadataWriter $metadataRequest } else { [IO.File]::WriteAllText($metadataRequest.Path, $metadataRequest.Content, (New-Object Text.UTF8Encoding($false))) }
            Assert-CodexPathHasNoReparseAncestor -Path $plan.Runner.Root -PathInspector $PathInspector
            if (Test-Path -LiteralPath $plan.Runner.Root) { throw 'Runner destination appeared during setup; refusing replacement.' }
            $moveRequest = [pscustomobject][ordered]@{ Source = $stagingRoot; Destination = $plan.Runner.Root }
            # The pin prevents replacement throughout staging. Windows cannot
            # rename a directory containing an exclusively-open child, so the
            # owned handle is released only after the final ancestry check and
            # immediately before the atomic promotion.
            Close-CodexStagingPin -Pin $stagingPin
            $stagingPin = $null
            Assert-CodexPathHasNoReparseAncestor -Path $stagingRoot -PathInspector $PathInspector
            Assert-CodexPathHasNoReparseAncestor -Path $plan.Runner.Root -PathInspector $PathInspector
            if ($null -ne $MoveRunner) { & $MoveRunner $moveRequest } else { Move-Item -LiteralPath $stagingRoot -Destination $plan.Runner.Root }
            if (-not (Test-Path -LiteralPath $plan.Runner.Root -PathType Container)) { throw 'Runner promotion did not create the expected destination.' }
            $runnerPromoted = $true
            $runnerOwnedPath = $plan.Runner.Root
            $mutations += 4
        } catch {
            Close-CodexStagingPin -Pin $stagingPin
            $stagingPin = $null
            if ($runnerConfigured -and -not $runnerPromoted -and (Test-Path -LiteralPath (Join-Path $plan.Runner.Root 'runner-install.json') -PathType Leaf)) { $runnerPromoted = $true; $runnerOwnedPath = $plan.Runner.Root }
            if ($runnerConfigured) { $rollbackConfig = Join-Path $(if ($runnerPromoted) { $plan.Runner.Root } else { $stagingRoot }) 'config.cmd'; Invoke-CodexRunnerRemoteRemoval -Repository $Repository -ConfigPath $rollbackConfig -CommandRunner $CommandRunner }
            if (Test-Path -LiteralPath $stagingRoot) { Remove-CodexOwnedPath -Path $stagingRoot -PathInspector $PathInspector }
            if ($null -ne $runnerOwnedPath) { Remove-CodexOwnedPath -Path $runnerOwnedPath -PathInspector $PathInspector }
            $stagingParent = Split-Path -Parent $stagingRoot
            if (Test-Path -LiteralPath $stagingParent -PathType Container) {
                $remainingStaging = @(Get-ChildItem -LiteralPath $stagingParent -Force -ErrorAction SilentlyContinue)
                if ($remainingStaging.Count -eq 0) {
                    try { Assert-CodexPathHasNoReparseAncestor -Path $stagingParent -PathInspector $PathInspector; [IO.Directory]::Delete($stagingParent) } catch { }
                }
            }
            throw
        }
        Close-CodexStagingPin -Pin $stagingPin
        $stagingPin = $null
    }

    $configToPersist = [ordered]@{
        repository = $Repository; repositoryRoot = $plan.RepositoryRoot; dataRoot = $plan.DataRoot
        runnerLabel = $plan.Runner.Label; runnerName = $plan.Runner.Name; runnerRoot = $plan.Runner.Root; runnerService = $false
        configPath = $plan.ConfigPath
    }
    if (-not $whatIf) {
        try {
        $parent = Split-Path -Parent $plan.ConfigPath
        if (-not (Test-Path -LiteralPath $parent -PathType Container)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        [IO.File]::WriteAllText($plan.ConfigPath, ($configToPersist | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding($false)))
        $mutations++
        # Task 5's resume capability probe is deliberately part of installation.
        $resumeProbe = {
            param($request)
            if ($null -ne $CommandRunner) {
                & $CommandRunner ([pscustomobject][ordered]@{
                    FilePath = $request.FilePath; Arguments = [string[]]$request.Arguments
                    WorkingDirectory = $request.WorkingDirectory; Interactive = $false
                })
            } else {
                Invoke-CodexProcess -FilePath $request.FilePath -Arguments $request.Arguments -WorkingDirectory $request.WorkingDirectory -Prompt '' -TimeoutMilliseconds $request.TimeoutMilliseconds
            }
        }.GetNewClosure()
        Initialize-CodexResumeCapability -IssueWorktree $plan.RepositoryRoot -Config ([pscustomobject]$configToPersist) -ConfigPath $plan.ConfigPath -ProcessRunner $resumeProbe | Out-Null

        $runnerTask = $plan.Tasks.Runner
        $temporaryXml = $null
        if (-not $reusedTaskNames.Contains($runnerTask.Name)) {
            try {
                if ($null -eq $TaskRunner) {
                    $temporaryXml = [IO.Path]::GetTempFileName()
                    [IO.File]::WriteAllText($temporaryXml, $runnerTask.Xml, (New-Object Text.UnicodeEncoding($false, $true)))
                }
                # No /F: an absent-task preflight must not overwrite a task
                # inserted by another actor between query and create.
                $taskArgs = @('/Create','/TN',$runnerTask.Name,'/XML',$(if ($null -ne $temporaryXml) { $temporaryXml } else { '<injected-task-xml>' }))
                $taskRequest = [pscustomobject][ordered]@{ FilePath = 'schtasks.exe'; Arguments = [string[]]$taskArgs; Task = $runnerTask; Xml = $runnerTask.Xml; OwnershipMarker = $plan.TaskOwnershipMarker; InteractiveToken = $true; RestartCount = 3; RestartInterval = 'PT1M'; Action = 'Create' }
                $taskResult = if ($null -ne $TaskRunner) { & $TaskRunner $taskRequest } else { Invoke-CodexInstallCommand -FilePath 'schtasks.exe' -Arguments $taskArgs -CommandRunner $CommandRunner }
            } catch {
                if (Confirm-CodexScheduledTaskAttemptOwnership -Task $runnerTask -Marker $plan.TaskOwnershipMarker -TaskQueryRunner $TaskQueryRunner -CommandRunner $CommandRunner) { $createdTaskNames.Add($runnerTask.Name) | Out-Null }
                throw
            } finally { if ($null -ne $temporaryXml -and (Test-Path -LiteralPath $temporaryXml)) { Remove-Item -LiteralPath $temporaryXml -Force } }
            if (-not (Test-CodexSetupSuccess $taskResult)) {
                if (Confirm-CodexScheduledTaskAttemptOwnership -Task $runnerTask -Marker $plan.TaskOwnershipMarker -TaskQueryRunner $TaskQueryRunner -CommandRunner $CommandRunner) { $createdTaskNames.Add($runnerTask.Name) | Out-Null }
                throw "Could not register scheduled task $($runnerTask.Name)."
            }
            $createdTaskNames.Add($runnerTask.Name) | Out-Null
            $mutations++
        }
        $startRequest = [pscustomobject][ordered]@{ FilePath = 'schtasks.exe'; Arguments = @('/Run','/TN',$runnerTask.Name); Task = $runnerTask; Action = 'Start' }
        $startResult = if ($null -ne $TaskStartRunner) { & $TaskStartRunner $startRequest } else { Invoke-CodexInstallCommand -FilePath 'schtasks.exe' -Arguments $startRequest.Arguments -CommandRunner $CommandRunner }
        if (-not (Test-CodexSetupSuccess $startResult)) { throw "Could not start scheduled task $($runnerTask.Name)." }
        $online = $false
        $lastOnlineError = $null
        for ($attempt = 0; $attempt -lt [Math]::Max(1, $OnlinePollAttempts); $attempt++) {
            try {
                $pollPayload = Get-CodexRunnerInventory -Repository $Repository -CommandRunner $CommandRunner
                Assert-CodexRunnerRegistration -Payload $pollPayload -RunnerName $plan.Runner.Name -Label $plan.Runner.Label | Out-Null
                $online = $true
                break
            } catch { $lastOnlineError = $_.Exception.Message }
            if ($attempt -lt ([Math]::Max(1, $OnlinePollAttempts) - 1)) {
                if ($null -ne $DelayRunner) { & $DelayRunner $PollDelayMilliseconds } elseif ($PollDelayMilliseconds -gt 0) { Start-Sleep -Milliseconds $PollDelayMilliseconds }
            }
        }
        if (-not $online) { throw "Runner did not become online within the bounded poll window: $lastOnlineError" }
        $labels = @($script:CodexLifecycleLabels.GetEnumerator())
        foreach ($entry in $labels) {
            $labelResult = Invoke-CodexInstallCommand -FilePath 'gh.exe' -Arguments @('label','create',$entry.Key,'--repo',$Repository,'--color',$entry.Value,'--force') -CommandRunner $CommandRunner
            if (-not (Test-CodexSetupSuccess $labelResult)) { throw "Could not create label $($entry.Key)." }
            $mutations++
        }
        foreach ($variable in @(@('CODEX_LOCAL_REPOSITORY',$plan.RepositoryRoot), @('CODEX_WORKER_DATA_ROOT',$plan.DataRoot))) {
            $variableResult = Invoke-CodexInstallCommand -FilePath 'gh.exe' -Arguments @('variable','set',$variable[0],'--repo',$Repository,'--body',[string]$variable[1]) -CommandRunner $CommandRunner
            if (-not (Test-CodexSetupSuccess $variableResult)) { throw "Could not set repository variable $($variable[0])." }
            $mutations++
        }
        foreach ($task in @($plan.Tasks.Notifier)) {
            if ($reusedTaskNames.Contains($task.Name)) { continue }
            $temporaryXml = $null
            try {
                if ($null -eq $TaskRunner) {
                    $temporaryXml = [IO.Path]::GetTempFileName()
                    [IO.File]::WriteAllText($temporaryXml, $task.Xml, (New-Object Text.UnicodeEncoding($false, $true)))
                }
                $taskArgs = @('/Create','/TN',$task.Name,'/XML',$(if ($null -ne $temporaryXml) { $temporaryXml } else { '<injected-task-xml>' }))
                $taskRequest = [pscustomobject][ordered]@{ FilePath = 'schtasks.exe'; Arguments = [string[]]$taskArgs; Task = $task; Xml = $task.Xml; OwnershipMarker = $plan.TaskOwnershipMarker; InteractiveToken = $true; RestartCount = 3; RestartInterval = 'PT1M'; Action = 'Create' }
                $taskResult = if ($null -ne $TaskRunner) { & $TaskRunner $taskRequest } else { Invoke-CodexInstallCommand -FilePath 'schtasks.exe' -Arguments $taskArgs -CommandRunner $CommandRunner }
            } catch {
                if (Confirm-CodexScheduledTaskAttemptOwnership -Task $task -Marker $plan.TaskOwnershipMarker -TaskQueryRunner $TaskQueryRunner -CommandRunner $CommandRunner) { $createdTaskNames.Add($task.Name) | Out-Null }
                throw
            } finally { if ($null -ne $temporaryXml -and (Test-Path -LiteralPath $temporaryXml)) { Remove-Item -LiteralPath $temporaryXml -Force } }
            if (-not (Test-CodexSetupSuccess $taskResult)) {
                if (Confirm-CodexScheduledTaskAttemptOwnership -Task $task -Marker $plan.TaskOwnershipMarker -TaskQueryRunner $TaskQueryRunner -CommandRunner $CommandRunner) { $createdTaskNames.Add($task.Name) | Out-Null }
                throw "Could not register scheduled task $($task.Name)."
            }
            $createdTaskNames.Add($task.Name) | Out-Null
            $mutations++
        }
        } catch {
            foreach ($taskName in @($createdTaskNames)) {
                try {
                    $stopRequest = [pscustomobject][ordered]@{ FilePath = 'schtasks.exe'; Arguments = @('/End','/TN',$taskName); Action = 'Stop'; TaskName = $taskName }
                    if ($null -ne $TaskStopRunner) { & $TaskStopRunner $stopRequest | Out-Null } else { Invoke-CodexInstallCommand -FilePath 'schtasks.exe' -Arguments $stopRequest.Arguments -CommandRunner $CommandRunner | Out-Null }
                } catch { }
                try {
                    $removeRequest = [pscustomobject][ordered]@{ FilePath = 'schtasks.exe'; Arguments = @('/Delete','/TN',$taskName,'/F'); Action = 'Delete'; TaskName = $taskName }
                    if ($null -ne $TaskRemoveRunner) { & $TaskRemoveRunner $removeRequest | Out-Null } else { Invoke-CodexInstallCommand -FilePath 'schtasks.exe' -Arguments $removeRequest.Arguments -CommandRunner $CommandRunner | Out-Null }
                } catch { }
            }
            if ($runnerConfigured) {
                $rollbackConfig = if ($runnerPromoted) { Join-Path $plan.Runner.Root 'config.cmd' } else { Join-Path $runnerOwnedPath 'config.cmd' }
                Invoke-CodexRunnerRemoteRemoval -Repository $Repository -ConfigPath $rollbackConfig -CommandRunner $CommandRunner
                if ($runnerPromoted) { Remove-CodexOwnedPath -Path $plan.Runner.Root -PathInspector $PathInspector }
            }
            Restore-CodexInstallerStateSnapshot -Snapshot $stateSnapshot -PathInspector $PathInspector
            throw
        }
    }
    return [pscustomobject][ordered]@{ WhatIf = $whatIf; Mutations = $mutations; Plan = $plan; Prerequisites = @($prereqs); RunnerAsset = $asset; Config = [pscustomobject]$configToPersist }
}
