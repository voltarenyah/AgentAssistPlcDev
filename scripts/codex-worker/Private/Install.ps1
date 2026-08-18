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
    if ([string]::IsNullOrWhiteSpace([string]$stdout)) { $stdout = [string]$Result }
    return [string]$stdout
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
        [pscustomobject][ordered]@{
            Name = $check.Name; FilePath = $check.FilePath; Arguments = [string[]]$check.Arguments
            Required = [bool]$check.Required; Installed = $installed; Version = $version
        }
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
    return [pscustomobject][ordered]@{ Name = $name; Uri = $url; Sha256 = @($hashes)[0] }
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
    $runnerRoot = Join-Path $data 'runner'
    $configPath = Join-Path $data 'config.json'
    $runnerScript = Join-Path $root 'scripts\codex-worker\Start-GitHubRunner.ps1'
    $notifierScript = Join-Path $root 'scripts\codex-worker\Invoke-DeploymentNotifier.ps1'
    $runnerArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-WindowStyle','Hidden','-File',$runnerScript,'-RunnerRoot',$runnerRoot,'-ConfigPath',$configPath)
    $notifierArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-Sta','-WindowStyle','Hidden','-File',$notifierScript,'-Watch','-ConfigPath',$configPath)
    return [pscustomobject][ordered]@{
        Repository = $Repository; RepositoryRoot = $root; DataRoot = $data; ConfigPath = $configPath
        Runner = [pscustomobject][ordered]@{ Root = $runnerRoot; ServiceMode = $false; Label = $label }
        Tasks = [pscustomobject][ordered]@{
            Runner = [pscustomobject][ordered]@{ Name = 'AutomationWorkbenchCodexRunner'; LogonTrigger = $true; Hidden = $true; FilePath = 'pwsh.exe'; Arguments = [string[]]$runnerArgs }
            Notifier = [pscustomobject][ordered]@{ Name = 'AutomationWorkbenchCodexDeploymentNotifier'; LogonTrigger = $true; Hidden = $true; FilePath = 'pwsh.exe'; Arguments = [string[]]$notifierArgs }
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
        [string] $TemporaryGitPath,
        [switch] $SkipPrerequisiteProbe,
        [switch] $WhatIf
    )
    if ([string]::IsNullOrWhiteSpace($Repository)) { $Repository = [string](Get-CodexSetupProperty $Config 'repository' '') }
    if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = [string](Get-CodexSetupProperty $Config 'repositoryRoot' (Get-Location).Path) }
    $plan = Get-CodexLocalWorkerPlan -Repository $Repository -RepositoryRoot $RepositoryRoot -DataRoot $DataRoot -Config $Config
    $whatIf = [bool]$WhatIf
    $mutations = 0
    $prereqs = if ($whatIf -or $SkipPrerequisiteProbe) { @(Get-CodexPrerequisitePlan -Config $Config) } else { @(Get-CodexPrerequisitePlan -Config $Config -CommandRunner $CommandRunner -Probe) }
    $codexPrereq = @($prereqs | Where-Object Name -eq 'Codex CLI')[0]
    $codex = [string](Get-CodexSetupProperty $Config 'codexCommand' 'codex')
    if (-not $whatIf) {
        $missing = $null -eq $codexPrereq -or -not [bool]$codexPrereq.Installed
        if ($missing) {
            $install = Invoke-CodexInstallCommand -FilePath 'npm.cmd' -Arguments @('install','--global','@openai/codex') -CommandRunner $CommandRunner
            if (-not (Test-CodexSetupSuccess $install)) { throw 'Codex CLI installation failed.' }
            $mutations++
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
    if (-not $whatIf) {
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
        $runnerConfigArgs = @('--unattended','--replace','--url',"https://github.com/$Repository",'--token',$token,'--labels',$plan.Runner.Label,'--work','_work')
        $configured = Invoke-CodexInstallCommand -FilePath $configPath -Arguments $runnerConfigArgs -WorkingDirectory $plan.Runner.Root -CommandRunner $CommandRunner
        if (-not (Test-CodexSetupSuccess $configured)) { throw 'GitHub Actions runner configuration failed.' }
        $mutations++
    }

    $configToPersist = [ordered]@{
        repository = $Repository; repositoryRoot = $plan.RepositoryRoot; dataRoot = $plan.DataRoot
        runnerLabel = $plan.Runner.Label; runnerRoot = $plan.Runner.Root; runnerService = $false
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
        $auth = Invoke-CodexInstallCommand -FilePath 'gh.exe' -Arguments @('auth','status','--hostname','github.com') -CommandRunner $CommandRunner
        if (-not (Test-CodexSetupSuccess $auth)) { throw 'GitHub CLI authentication is required.' }
        $runners = Invoke-CodexInstallCommand -FilePath 'gh.exe' -Arguments @('api',"repos/$Repository/actions/runners") -CommandRunner $CommandRunner
        if (-not (Test-CodexSetupSuccess $runners)) { throw 'GitHub Actions runner registration could not be verified.' }
        foreach ($task in @($plan.Tasks.Runner, $plan.Tasks.Notifier)) {
            $quotedTaskArguments = @(foreach ($argument in $task.Arguments) { '"' + ($argument -replace '"','\"') + '"' }) -join ' '
            $taskCommand = '"{0}" {1}' -f $task.FilePath, $quotedTaskArguments
            $taskArgs = @('/Create','/TN',$task.Name,'/TR',$taskCommand,'/SC','ONLOGON','/RL','LIMITED','/F')
            $taskResult = if ($null -ne $TaskRunner) { & $TaskRunner ([pscustomobject]@{ FilePath = 'schtasks.exe'; Arguments = [string[]]$taskArgs; Task = $task }) } else { Invoke-CodexInstallCommand -FilePath 'schtasks.exe' -Arguments $taskArgs -CommandRunner $CommandRunner }
            if (-not (Test-CodexSetupSuccess $taskResult)) { throw "Could not register scheduled task $($task.Name)." }
            $mutations++
        }
    }
    return [pscustomobject][ordered]@{ WhatIf = $whatIf; Mutations = $mutations; Plan = $plan; Prerequisites = @($prereqs); RunnerAsset = $asset; Config = [pscustomobject]$configToPersist }
}
