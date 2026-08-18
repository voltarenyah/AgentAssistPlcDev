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
    return [pscustomobject][ordered]@{ Name = $name; Uri = $url; Sha256 = @($hashes)[0]; Version = ($tag -replace '^v', '') }
}

function Get-CodexCurrentUserId {
    $domain = [Environment]::UserDomainName
    $user = [Environment]::UserName
    if ([string]::IsNullOrWhiteSpace($domain)) { return $user }
    return "$domain\$user"
}

function New-CodexScheduledTaskXml {
    param([string] $UserId, [string] $FilePath, [string[]] $Arguments)
    $escapedFile = [Security.SecurityElement]::Escape($FilePath)
    $escapedArguments = [Security.SecurityElement]::Escape((($Arguments | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join ' '))
    return @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Author>Automation Workbench</Author></RegistrationInfo>
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
        [object] $Asset,
        [scriptblock] $ProcessProvider
    )
    $statePath = Join-Path $RunnerRoot '.runner'
    if (-not (Test-Path -LiteralPath $RunnerRoot -PathType Container)) { return [pscustomobject]@{ Exists = $false; Valid = $false; Active = $false; Reason = 'missing' } }
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) { return [pscustomobject]@{ Exists = $true; Valid = $false; Active = $false; Reason = 'runner state is missing' } }
    try { $state = [IO.File]::ReadAllText($statePath) | ConvertFrom-Json } catch { return [pscustomobject]@{ Exists = $true; Valid = $false; Active = $false; Reason = 'runner state is malformed' } }
    $labels = @((Get-CodexSetupProperty $state 'labels' @()) | ForEach-Object { if ($_ -is [string]) { $_ } else { [string](Get-CodexSetupProperty $_ 'name' '') } })
    $expectedUrl = "https://github.com/$Repository"
    $actualUrl = ([string](Get-CodexSetupProperty $state 'repositoryUrl' '')).TrimEnd('/')
    $version = [string](Get-CodexSetupProperty $state 'version' '')
    $hash = [string](Get-CodexSetupProperty $state 'sha256' (Get-CodexSetupProperty $state 'archiveSha256' ''))
    $valid = $actualUrl.Equals($expectedUrl, [StringComparison]::OrdinalIgnoreCase) -and $labels -contains $Label -and (Test-Path -LiteralPath (Join-Path $RunnerRoot 'run.cmd') -PathType Leaf)
    if ($valid -and $null -ne $Asset -and -not [string]::IsNullOrWhiteSpace($Asset.Version)) { $valid = $version -eq $Asset.Version }
    if ($valid -and $null -ne $Asset) { $valid = $hash.Equals([string]$Asset.Sha256, [StringComparison]::OrdinalIgnoreCase) }
    $processes = if ($null -ne $ProcessProvider) { @(& $ProcessProvider) } else { @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match '(?i)^(Runner\.Listener|runsvc|run)$' }) }
    [pscustomobject][ordered]@{ Exists = $true; Valid = [bool]$valid; Active = @($processes).Count -gt 0; Reason = if ($valid) { 'matching configured runner' } else { 'runner configuration does not match expected repository, label, version, or hash' }; State = $state }
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

function Get-CodexLocalWorkerPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $Repository,
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [string] $DataRoot,
        [object] $Config
    )
    $root = [IO.Path]::GetFullPath($RepositoryRoot)
    if ([string]::IsNullOrWhiteSpace($DataRoot)) { $DataRoot = Join-Path $env:LOCALAPPDATA 'AutomationWorkbench\CodexWorker' }
    $data = [IO.Path]::GetFullPath($DataRoot)
    $rootPrefix = $root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($data.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or $data.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Worker data must be outside the repository.'
    }
    $label = [string](Get-CodexSetupProperty $Config 'runnerLabel' 'agentassist-local')
    $runnerRoot = [string](Get-CodexSetupProperty $Config 'runnerRoot' (Join-Path $data 'runner'))
    $runnerRoot = [IO.Path]::GetFullPath($runnerRoot)
    $runnerName = [string](Get-CodexSetupProperty $Config 'runnerName' 'AutomationWorkbenchCodexRunner')
    $configPath = Join-Path $data 'config.json'
    $runnerScript = Join-Path $root 'scripts\codex-worker\Start-GitHubRunner.ps1'
    $notifierScript = Join-Path $root 'scripts\codex-worker\Invoke-DeploymentNotifier.ps1'
    $runnerArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-WindowStyle','Hidden','-File',$runnerScript,'-RunnerRoot',$runnerRoot,'-ConfigPath',$configPath)
    $notifierArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-Sta','-WindowStyle','Hidden','-File',$notifierScript,'-Watch','-ConfigPath',$configPath)
    $userId = Get-CodexCurrentUserId
    $runnerXml = New-CodexScheduledTaskXml -UserId $userId -FilePath 'pwsh.exe' -Arguments $runnerArgs
    $notifierXml = New-CodexScheduledTaskXml -UserId $userId -FilePath 'pwsh.exe' -Arguments $notifierArgs
    return [pscustomobject][ordered]@{
        Repository = $Repository; RepositoryRoot = $root; DataRoot = $data; ConfigPath = $configPath
        Runner = [pscustomobject][ordered]@{ Root = $runnerRoot; ServiceMode = $false; Label = $label; Name = $runnerName; Reuse = $false }
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
        [scriptblock] $ProcessProvider,
        [string] $TemporaryGitPath,
        [switch] $SkipPrerequisiteProbe,
        [switch] $WhatIf
    )
    if ([string]::IsNullOrWhiteSpace($Repository)) { $Repository = [string](Get-CodexSetupProperty $Config 'repository' '') }
    if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = [string](Get-CodexSetupProperty $Config 'repositoryRoot' (Get-Location).Path) }
    $plan = Get-CodexLocalWorkerPlan -Repository $Repository -RepositoryRoot $RepositoryRoot -DataRoot $DataRoot -Config $Config
    $whatIf = [bool]$WhatIf
    $mutations = 0
    if (-not $whatIf) {
        foreach ($scriptPath in @((Join-Path $plan.RepositoryRoot 'scripts\codex-worker\Start-GitHubRunner.ps1'), (Join-Path $plan.RepositoryRoot 'scripts\codex-worker\Invoke-DeploymentNotifier.ps1'))) {
            if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) { throw "Required task target does not exist: $scriptPath" }
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
    $existing = if ($null -ne $asset) { Get-CodexExistingRunnerState -RunnerRoot $plan.Runner.Root -Repository $Repository -Label $plan.Runner.Label -Asset $asset -ProcessProvider $ProcessProvider } else { [pscustomobject]@{ Exists = $false; Valid = $false; Active = $false; Reason = 'release not resolved' } }
    if ($existing.Valid) { $plan.Runner.Reuse = $true }
    elseif ($existing.Exists -and -not $whatIf) {
        if ($existing.Active) { throw "Existing runner is active but does not match the expected repository, label, version, and hash; refusing replacement." }
        throw "Existing runner cannot be safely reused: $($existing.Reason). Refusing in-place replacement."
    }
    if (-not $whatIf -and -not $plan.Runner.Reuse) {
        $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('actions-runner-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
        try {
            $archive = Join-Path $tempRoot $asset.Name
            $downloadRequest = [pscustomobject][ordered]@{ Uri = $asset.Uri; Destination = $archive }
            if ($null -ne $DownloadRunner) { & $DownloadRunner $downloadRequest } else { Invoke-WebRequest -UseBasicParsing -Uri $asset.Uri -OutFile $archive }
            $actualHash = if ($null -ne $HashRunner) { [string](& $HashRunner $archive) } else { (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash }
            if ($actualHash.ToLowerInvariant() -ne $asset.Sha256) { throw 'Downloaded runner checksum does not match the release checksum.' }
            if (-not (Test-Path -LiteralPath $plan.Runner.Root -PathType Container)) { New-Item -ItemType Directory -Path $plan.Runner.Root -Force | Out-Null }
            $extractRequest = [pscustomobject][ordered]@{ Archive = $archive; Destination = $plan.Runner.Root }
            if ($null -ne $ExtractRunner) { & $ExtractRunner $extractRequest } else { Expand-Archive -LiteralPath $archive -DestinationPath $plan.Runner.Root -Force }
            $mutations += 2
        } finally { if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force } }
        $tokenResult = Invoke-CodexInstallCommand -FilePath 'gh.exe' -Arguments @('api','--method','POST',"repos/$Repository/actions/runners/registration-token") -CommandRunner $CommandRunner
        if (-not (Test-CodexSetupSuccess $tokenResult)) { throw 'Could not obtain a runner registration token.' }
        $tokenPayload = Get-CodexSetupText $tokenResult | ConvertFrom-Json
        $token = [string](Get-CodexSetupProperty $tokenPayload 'token' '')
        if ([string]::IsNullOrWhiteSpace($token)) { throw 'GitHub returned an empty runner registration token.' }
        $configPath = Join-Path $plan.Runner.Root 'config.cmd'
        $runnerConfigArgs = @('--unattended','--replace','--url',"https://github.com/$Repository",'--token',$token,'--name',$plan.Runner.Name,'--labels',$plan.Runner.Label,'--work','_work')
        try { $configured = Invoke-CodexInstallCommand -FilePath $configPath -Arguments $runnerConfigArgs -WorkingDirectory $plan.Runner.Root -CommandRunner $CommandRunner }
        catch { throw 'GitHub Actions runner configuration failed.' }
        if (-not (Test-CodexSetupSuccess $configured)) { throw 'GitHub Actions runner configuration failed.' }
        $mutations++
    }

    $registeredRunner = $null
    if (-not $whatIf) {
        $registrationResult = Invoke-CodexInstallCommand -FilePath 'gh.exe' -Arguments @('api',"repos/$Repository/actions/runners") -CommandRunner $CommandRunner
        if (-not (Test-CodexSetupSuccess $registrationResult)) { throw 'GitHub Actions runner registration could not be verified.' }
        try { $registrationPayload = Get-CodexSetupText $registrationResult | ConvertFrom-Json } catch { throw 'GitHub Actions runner registration returned malformed JSON.' }
        $registeredRunner = Assert-CodexRunnerRegistration -Payload $registrationPayload -RunnerName $plan.Runner.Name -Label $plan.Runner.Label
    }

    $configToPersist = [ordered]@{
        repository = $Repository; repositoryRoot = $plan.RepositoryRoot; dataRoot = $plan.DataRoot
        runnerLabel = $plan.Runner.Label; runnerName = $plan.Runner.Name; runnerRoot = $plan.Runner.Root; runnerService = $false
        configPath = $plan.ConfigPath
    }
    if (-not $whatIf) {
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
        foreach ($task in @($plan.Tasks.Runner, $plan.Tasks.Notifier)) {
            $temporaryXml = $null
            try {
                if ($null -eq $TaskRunner) {
                    $temporaryXml = [IO.Path]::GetTempFileName()
                    [IO.File]::WriteAllText($temporaryXml, $task.Xml, (New-Object Text.UnicodeEncoding($false, $true)))
                }
                $taskArgs = @('/Create','/TN',$task.Name,'/XML',$(if ($null -ne $temporaryXml) { $temporaryXml } else { '<injected-task-xml>' }),'/F')
                $taskRequest = [pscustomobject][ordered]@{ FilePath = 'schtasks.exe'; Arguments = [string[]]$taskArgs; Task = $task; Xml = $task.Xml; InteractiveToken = $true; RestartCount = 3; RestartInterval = 'PT1M' }
                $taskResult = if ($null -ne $TaskRunner) { & $TaskRunner $taskRequest } else { Invoke-CodexInstallCommand -FilePath 'schtasks.exe' -Arguments $taskArgs -CommandRunner $CommandRunner }
            } finally { if ($null -ne $temporaryXml -and (Test-Path -LiteralPath $temporaryXml)) { Remove-Item -LiteralPath $temporaryXml -Force } }
            if (-not (Test-CodexSetupSuccess $taskResult)) { throw "Could not register scheduled task $($task.Name)." }
            $mutations++
        }
    }
    return [pscustomobject][ordered]@{ WhatIf = $whatIf; Mutations = $mutations; Plan = $plan; Prerequisites = @($prereqs); RunnerAsset = $asset; Config = [pscustomobject]$configToPersist }
}
