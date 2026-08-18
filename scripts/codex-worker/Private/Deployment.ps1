Set-StrictMode -Version Latest

function Get-CodexDeploymentValue {
    param([object] $Object, [string] $Name, [object] $Default = $null)
    if ($null -eq $Object) { return $Default }
    if ($Object -is [System.Collections.IDictionary] -and $Object.Contains($Name)) {
        if ($null -ne $Object[$Name]) { return $Object[$Name] }
        return $Default
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return $Default }
    return $property.Value
}

function Copy-CodexDeploymentObject {
    param([object] $Object)
    if ($null -eq $Object) { return $null }
    return (($Object | ConvertTo-Json -Depth 30) | ConvertFrom-Json)
}

function Invoke-CodexDeploymentProcess {
    param([string] $FilePath, [string[]] $Arguments, [string] $WorkingDirectory, [scriptblock] $ProcessRunner)
    if ($null -ne $ProcessRunner) {
        $value = & $ProcessRunner $FilePath ([string[]]$Arguments) $WorkingDirectory
        if ($null -ne $value -and $value.PSObject.Properties['ExitCode']) {
            if (-not $value.PSObject.Properties['StdOut']) { Add-Member -InputObject $value -NotePropertyName StdOut -NotePropertyValue ([string](Get-CodexDeploymentValue $value 'Output' '')) -Force }
            if (-not $value.PSObject.Properties['StdErr']) { Add-Member -InputObject $value -NotePropertyName StdErr -NotePropertyValue '' -Force }
            if (-not $value.PSObject.Properties['Output']) { Add-Member -InputObject $value -NotePropertyName Output -NotePropertyValue ([string](Get-CodexDeploymentValue $value 'StdOut' '')) -Force }
            return $value
        }
        $text = (($value | ForEach-Object { [string]$_ }) -join [Environment]::NewLine)
        return [pscustomobject]@{ ExitCode = 0; Output = $text; StdOut = $text; StdErr = ''; ProcessId = $null; CommandLine = "$FilePath $($Arguments -join ' ')" }
    }
    Push-Location $WorkingDirectory
    try {
        $output = & $FilePath @Arguments 2>&1
        $text = (($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine)
        return [pscustomobject]@{ ExitCode = [int]$LASTEXITCODE; Output = $text; StdOut = $text; StdErr = ''; ProcessId = $null; CommandLine = "$FilePath $($Arguments -join ' ')" }
    } finally { Pop-Location }
}

function Get-CodexDeploymentRuntimeSlots {
    param([string] $RepositoryRoot, [object] $Config)
    $names = @((Get-CodexDeploymentValue -Object $Config -Name 'runtimeSlots' -Default @('runtime-a','runtime-b')) | ForEach-Object { [string]$_ })
    $invalid = @($names | Where-Object { $_ -notin @('runtime-a','runtime-b') })
    if ($names.Count -ne 2 -or $invalid.Count -gt 0 -or $names[0] -eq $names[1]) { throw 'runtimeSlots must contain exactly runtime-a and runtime-b.' }
    $root = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot '.worktrees'))
    $slots = [ordered]@{}
    foreach ($name in $names) { $slots[$name] = [IO.Path]::GetFullPath((Join-Path $root $name)) }
    return [pscustomobject][ordered]@{ Root = $root; Names = $names; Paths = $slots }
}

function Assert-CodexDeploymentSlotTrusted {
    param([string] $SlotName, [string] $SlotPath, [string] $WorktreeRoot, [scriptblock] $PathInspector, [switch] $RequireExisting)
    $expected = [IO.Path]::GetFullPath((Join-Path $WorktreeRoot $SlotName))
    if (-not [string]::Equals([IO.Path]::GetFullPath($SlotPath), $expected, [StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime slot path is outside the configured runtime slot.' }
    if ($null -eq $PathInspector) {
        $PathInspector = { param($Path)
            $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
            $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
            if ($null -eq $acl) { throw "Unable to inspect ACL for '$Path'." }
            [pscustomobject]@{ IsReparsePoint = (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0); ResolvedPath = $item.FullName }
        }
    }
    $cursor = [IO.Path]::GetFullPath($SlotPath)
    while ($true) {
        if ((Test-Path -LiteralPath $cursor -PathType Container -ErrorAction SilentlyContinue) -or ($RequireExisting -and [string]::Equals($cursor, [IO.Path]::GetFullPath($SlotPath), [StringComparison]::OrdinalIgnoreCase))) {
            try { $inspection = & $PathInspector $cursor } catch { throw "Unable to inspect runtime slot path '$cursor': $($_.Exception.Message)" }
            if ($null -eq $inspection -or -not $inspection.PSObject.Properties['IsReparsePoint']) { throw "Unable to inspect runtime slot path '$cursor'." }
            if ([bool]$inspection.IsReparsePoint) { throw "Refusing reparse-point runtime slot path: $cursor" }
            if ($inspection.PSObject.Properties['ResolvedPath'] -and -not [string]::Equals([IO.Path]::GetFullPath([string]$inspection.ResolvedPath), $cursor, [StringComparison]::OrdinalIgnoreCase)) { throw "Runtime slot path resolved outside the configured path: $cursor" }
        }
        if ([string]::Equals($cursor.TrimEnd('\','/'), $WorktreeRoot.TrimEnd('\','/'), [StringComparison]::OrdinalIgnoreCase)) { break }
        $parent = Split-Path -Parent $cursor
        if ([string]::Equals($parent, $cursor, [StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime slot path escaped the configured worktree root.' }
        $cursor = $parent
    }
    return $true
}

function Test-CodexDeploymentProcessUsesPath {
    param([object] $Process, [string] $Path)
    $property = $Process.PSObject.Properties['CommandLine']
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) { return $false }
    $command = ([string]$property.Value).Replace('/','\').TrimEnd('\').ToLowerInvariant()
    $target = ([IO.Path]::GetFullPath($Path)).Replace('/','\').TrimEnd('\').ToLowerInvariant()
    return $command.Contains($target)
}

function Assert-CodexDeploymentSlotNotBusy {
    param([string] $SlotPath, [scriptblock] $ProcessProvider)
    if ($null -eq $ProcessProvider) { $ProcessProvider = { @(Get-CimInstance Win32_Process -ErrorAction Stop) } }
    try {
        foreach ($process in @(& $ProcessProvider)) {
            if ($process.PSObject.Properties['Succeeded'] -and -not [bool]$process.Succeeded) { throw "Unable to inspect active processes: $($process.Error)" }
            if (Test-CodexDeploymentProcessUsesPath -Process $process -Path $SlotPath) { throw "A process is using runtime slot '$SlotPath'." }
        }
    } catch { if ($_.Exception.Message -match 'process is using|Unable to inspect') { throw } ; throw "Unable to inspect active processes: $($_.Exception.Message)" }
}

function Get-CodexDeploymentProcessEvidence {
    param([string] $SlotPath, [scriptblock] $ProcessProvider)
    if ($null -eq $ProcessProvider) { $ProcessProvider = { @(Get-CimInstance Win32_Process -ErrorAction Stop) } }
    try {
        $matches = @()
        foreach ($process in @(& $ProcessProvider)) {
            if ($process.PSObject.Properties['Succeeded'] -and -not [bool]$process.Succeeded) { throw "Unable to inspect active processes: $($process.Error)" }
            if (Test-CodexDeploymentProcessUsesPath -Process $process -Path $SlotPath) {
                $pid = Get-CodexDeploymentValue $process 'ProcessId' $null
                if ($null -eq $pid -or [string]::IsNullOrWhiteSpace([string]$pid)) { throw 'Relevant service process had no process ID.' }
                $matches += [pscustomobject]@{ processId = [int]$pid; commandLine = [string](Get-CodexDeploymentValue $process 'CommandLine' '') }
            }
        }
        return @($matches)
    } catch { throw "Unable to inspect deployment service processes: $($_.Exception.Message)" }
}

function Invoke-CodexDeploymentHealth {
    param([object] $Config, [scriptblock] $HttpRunner, [scriptblock] $SleepProvider)
    if ($null -eq $HttpRunner) { $HttpRunner = { param($Uri) Invoke-WebRequest -UseBasicParsing -Uri $Uri -TimeoutSec 2 } }
    $timeout = [Math]::Max(1, [int](Get-CodexDeploymentValue $Config 'healthTimeoutSeconds' 60))
    $uris = @('http://localhost:5173/','http://localhost:5239/api/status','http://localhost:8787/health')
    $last = [ordered]@{}
    $attempts = [Math]::Max(1, $timeout * 4)
    for ($i = 0; $i -lt $attempts; $i++) {
        $all = $true
        foreach ($uri in $uris) {
            try {
                $response = & $HttpRunner $uri
                $status = 200
                if ($response.PSObject.Properties['StatusCode']) { $status = [int]$response.StatusCode }
                $body = if ($response.PSObject.Properties['Body']) { [string]$response.Body } elseif ($response.PSObject.Properties['Content']) { [string]$response.Content } else { '' }
                $item = [ordered]@{ uri = $uri; statusCode = $status; body = $body }
                if ($uri -match '/health$') {
                    try {
                        $json = if (-not [string]::IsNullOrWhiteSpace($body)) { $body | ConvertFrom-Json } else { $response }
                        $item.status = [string](Get-CodexDeploymentValue $json 'status' '')
                        $item.modelConfigured = Get-CodexDeploymentValue $json 'modelConfigured' $null
                        $item.modelMode = [string](Get-CodexDeploymentValue $json 'modelMode' '')
                        $item.model = [string](Get-CodexDeploymentValue $json 'model' '')
                        $mode = $item.modelMode.ToLowerInvariant()
                        $item.fallback = if ($mode -eq 'deterministic-fallback' -or $mode -in @('fallback','deterministic','mock','test')) { $true } elseif ($mode -eq 'llm' -and [bool]$item.modelConfigured) { $false } else { $null }
                        if ($item.status -ne 'ok') { $all = $false }
                    } catch { $all = $false }
                }
                if ($status -ne 200) { $all = $false }
                $last[$uri] = [pscustomobject]$item
            } catch { $all = $false; $last[$uri] = [pscustomobject]@{ uri = $uri; statusCode = 0; body = ''; error = $_.Exception.Message } }
        }
        if ($all) { return [pscustomobject]@{ Success = $true; Endpoints = [pscustomobject]$last } }
        if ($null -ne $SleepProvider -and $i -lt ($attempts - 1)) { & $SleepProvider ([TimeSpan]::FromMilliseconds(250)) }
    }
    return [pscustomobject]@{ Success = $false; Endpoints = [pscustomobject]$last; Error = 'Runtime health checks did not pass before the timeout.' }
}

function Invoke-CodexDeployment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [string] $DataRoot,
        [Parameter(Mandatory = $true)] [object] $Config,
        [Parameter(Mandatory = $true)] [object] $Deployment,
        [scriptblock] $StateReader,
        [scriptblock] $StateWriter,
        [scriptblock] $GitCommandRunner,
        [scriptblock] $ProcessRunner,
        [scriptblock] $RegistryRunner,
        [scriptblock] $HttpRunner,
        [scriptblock] $SleepProvider,
        [scriptblock] $ProcessProvider,
        [scriptblock] $PathInspector,
        [scriptblock] $GitHubCommandRunner,
        [Alias('Clock')] [scriptblock] $NowProvider
    )
    if ($null -eq $NowProvider) { $NowProvider = { [DateTime]::UtcNow } }
    $now = ([DateTime](& $NowProvider)).ToUniversalTime()
    $paths = Resolve-CodexWorkerPaths -RepositoryRoot $RepositoryRoot -DataRoot $DataRoot
    $read = if ($null -ne $StateReader) { $StateReader } else { { param($Path) Read-CodexWorkerState -Path $Path } }
    $write = if ($null -ne $StateWriter) { $StateWriter } else { { param($Path, $Value) Write-CodexWorkerState -Path $Path -State $Value } }
    $state = & $read $paths.StatePath
    $previousState = Copy-CodexDeploymentObject $state
    $slots = Get-CodexDeploymentRuntimeSlots -RepositoryRoot $paths.RepositoryRoot -Config $Config
    $active = [string](Get-CodexDeploymentValue $state 'activeSlot' (Get-CodexDeploymentValue $Deployment 'activeSlot' 'runtime-a'))
    if ($active -notin $slots.Names) { throw "Durable activeSlot '$active' is not configured." }
    $inactive = @($slots.Names | Where-Object { $_ -ne $active })[0]
    $activePath = $slots.Paths[$active]; $inactivePath = $slots.Paths[$inactive]
    Assert-CodexDeploymentSlotTrusted -SlotName $active -SlotPath $activePath -WorktreeRoot $slots.Root -PathInspector $PathInspector -RequireExisting | Out-Null
    Assert-CodexDeploymentSlotTrusted -SlotName $inactive -SlotPath $inactivePath -WorktreeRoot $slots.Root -PathInspector $PathInspector -RequireExisting | Out-Null
    $target = ConvertTo-CodexFullCommit -Commit ([string](Get-CodexDeploymentValue $Deployment 'targetCommit' '')) -Name 'deployment target'
    $durableDeployment = Get-CodexDeploymentValue -Object $state -Name 'deployment' -Default $null
    $durableTargetText = [string](Get-CodexDeploymentValue -Object $durableDeployment -Name 'targetCommit' -Default '')
    if (-not [string]::IsNullOrWhiteSpace($durableTargetText)) {
        $durableTarget = ConvertTo-CodexFullCommit -Commit $durableTargetText -Name 'durable deployment target'
        if ($durableTarget -ne $target) { throw 'The durable deployment target does not match the requested deployment.' }
    }
    $evidence = [ordered]@{ targetCommit = $target; activeSlot = $active; previousActiveSlot = $active; targetSlot = $inactive; prepared = $false; steps = @(); logs = @(); rollback = $null; startedAt = $now.ToString('o'); previousStateSnapshot = $previousState }
    $activationAttempted = $false
    $logPath = Join-Path $paths.DataRoot ('runs\deployment-' + $now.ToString('yyyyMMddTHHmmssfffZ') + '.log')
    Assert-CodexDeploymentSlotNotBusy -SlotPath $inactivePath -ProcessProvider $ProcessProvider
    try {
        Invoke-CodexDeploymentGit -RepositoryRoot $paths.RepositoryRoot -Arguments @('fetch','origin','master') -CommandRunner $GitCommandRunner | Out-Null
        $master = ConvertTo-CodexFullCommit -Commit (Invoke-CodexDeploymentGit -RepositoryRoot $paths.RepositoryRoot -Arguments @('rev-parse','origin/master^{commit}') -CommandRunner $GitCommandRunner) -Name 'origin/master commit'
        Assert-CodexCommitReachableFromMaster -RepositoryRoot $paths.RepositoryRoot -Commit $target -MasterCommit $master -GitCommandRunner $GitCommandRunner | Out-Null
        Assert-CodexDeploymentSlotTrusted -SlotName $inactive -SlotPath $inactivePath -WorktreeRoot $slots.Root -PathInspector $PathInspector -RequireExisting | Out-Null
        Assert-CodexDeploymentSlotNotBusy -SlotPath $inactivePath -ProcessProvider $ProcessProvider
        Invoke-CodexDeploymentGit -RepositoryRoot $paths.RepositoryRoot -Arguments @('worktree','remove','--force',$inactivePath) -CommandRunner $GitCommandRunner | Out-Null
        Assert-CodexDeploymentSlotTrusted -SlotName $inactive -SlotPath $inactivePath -WorktreeRoot $slots.Root -PathInspector $PathInspector | Out-Null
        Invoke-CodexDeploymentGit -RepositoryRoot $paths.RepositoryRoot -Arguments @('worktree','add','--detach',$inactivePath,$target) -CommandRunner $GitCommandRunner | Out-Null
        $checkedOut = ConvertTo-CodexFullCommit -Commit (Invoke-CodexDeploymentGit -RepositoryRoot $inactivePath -Arguments @('rev-parse','HEAD') -CommandRunner $GitCommandRunner) -Name 'runtime slot HEAD'
        if ($checkedOut -ne $target) { throw 'Runtime slot did not resolve to the exact target SHA.' }

        $processInvoker = ${function:Invoke-CodexDeploymentProcess}
        $valueGetter = ${function:Get-CodexDeploymentValue}
        $run = { param([string]$File,[string[]]$CommandArgs)
            $result = & $processInvoker $File $CommandArgs $inactivePath $ProcessRunner
            $stdout = [string](& $valueGetter $result 'StdOut' (& $valueGetter $result 'Output' ''))
            $stderr = [string](& $valueGetter $result 'StdErr' '')
            $logDirectory = Split-Path -Parent $logPath
            if (-not (Test-Path -LiteralPath $logDirectory -PathType Container)) { New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null }
            Add-Content -LiteralPath $logPath -Value ("$File $($CommandArgs -join ' ')`nstdout: $stdout`nstderr: $stderr")
            $evidence.steps += [pscustomobject]@{ file = $File; arguments = $CommandArgs; workingDirectory = $inactivePath; exitCode = [int]$result.ExitCode; stdout = $stdout; stderr = $stderr; processId = & $valueGetter $result 'ProcessId' $null }
            if ([int]$result.ExitCode -ne 0) { throw "$File exited with code $($result.ExitCode)." }
            return $result
        }.GetNewClosure()
        & $run 'dotnet' @('restore','AgentAssistPlcDev.sln') | Out-Null
        & $run 'dotnet' @('build','AgentAssistPlcDev.sln','-v','q') | Out-Null
        & $run 'npm.cmd' @('ci','--prefix','studio') | Out-Null
        & $run 'npm.cmd' @('run','build','--prefix','studio') | Out-Null
        $bootstrap = [string](Get-CodexDeploymentValue $Config 'bootstrapPython' 'python.exe')
        & $run $bootstrap @('-m','venv','agent-service\.venv') | Out-Null
        & $run (Join-Path $inactivePath 'agent-service\.venv\Scripts\python.exe') @('-m','pip','install','-e','agent-service[test]') | Out-Null
        $regPath = [string](Get-CodexDeploymentValue $Config 'tiaWhitelistPath' '')
        if ([string]::IsNullOrWhiteSpace($regPath)) { $regPath = Join-Path $inactivePath 'src\Mcp.Engineering\bin\Debug\net48\register-whitelist.reg' }
        if (-not [string]::IsNullOrWhiteSpace($regPath) -and (Test-Path -LiteralPath $regPath -PathType Leaf)) {
            $reg = if ($null -ne $RegistryRunner) { & $RegistryRunner 'reg.exe' @('import',$regPath) $inactivePath } else { Invoke-CodexDeploymentProcess -FilePath 'reg.exe' -Arguments @('import',$regPath) -WorkingDirectory $inactivePath -ProcessRunner $ProcessRunner }
            if ($null -ne $reg.PSObject.Properties['ExitCode'] -and [int]$reg.ExitCode -ne 0) { throw "reg.exe exited with code $($reg.ExitCode)." }
            $evidence.steps += [pscustomobject]@{ file = 'reg.exe'; arguments = @('import',$regPath); workingDirectory = $inactivePath; exitCode = [int](Get-CodexDeploymentValue $reg 'ExitCode' 0); stdout = [string](Get-CodexDeploymentValue $reg 'StdOut' (Get-CodexDeploymentValue $reg 'Output' '')); stderr = [string](Get-CodexDeploymentValue $reg 'StdErr' ''); processId = Get-CodexDeploymentValue $reg 'ProcessId' $null }
        }
        $evidence.prepared = $true
        $launchArgs = @('-ExecutionPolicy','Bypass','-File',(Join-Path $inactivePath 'launch.ps1'),'-NoBuild')
        $launch = Invoke-CodexDeploymentProcess -FilePath 'powershell.exe' -Arguments $launchArgs -WorkingDirectory $inactivePath -ProcessRunner $ProcessRunner
        $evidence.launch = [pscustomobject]@{ exitCode = [int]$launch.ExitCode; stdout = [string](Get-CodexDeploymentValue $launch 'StdOut' (Get-CodexDeploymentValue $launch 'Output' '')); stderr = [string](Get-CodexDeploymentValue $launch 'StdErr' ''); processId = Get-CodexDeploymentValue $launch 'ProcessId' $null; commandLine = [string](Get-CodexDeploymentValue $launch 'CommandLine' ('powershell.exe ' + ($launchArgs -join ' '))); workingDirectory = $inactivePath }
        if ([int]$launch.ExitCode -ne 0) { throw 'The candidate runtime launcher failed.' }
        $health = Invoke-CodexDeploymentHealth -Config $Config -HttpRunner $HttpRunner -SleepProvider $SleepProvider
        $evidence.health = $health.Endpoints
        if (-not $health.Success) { throw $health.Error }
        $evidence.processes = @(Get-CodexDeploymentProcessEvidence -SlotPath $inactivePath -ProcessProvider $ProcessProvider)
        $after = & $read $paths.StatePath
        $desired = Copy-CodexDeploymentObject $after
        if ($null -eq $desired.PSObject.Properties['activeSlot']) { Add-Member -InputObject $desired -NotePropertyName activeSlot -NotePropertyValue $inactive -Force } else { $desired.activeSlot = $inactive }
        $completed = Get-CodexDeploymentValue $desired 'deployment' $null
        if ($null -eq $completed) { $completed = Copy-CodexDeploymentObject $Deployment; Add-Member -InputObject $desired -NotePropertyName deployment -NotePropertyValue $completed -Force }
        if ($null -ne $completed) { $completed.status = 'completed'; if ($null -eq $completed.PSObject.Properties['evidence']) { Add-Member -InputObject $completed -NotePropertyName evidence -NotePropertyValue ([pscustomobject]$evidence) -Force } else { $completed.evidence = [pscustomobject]$evidence } }
        if ($null -eq $desired.PSObject.Properties['lastDeployment']) { Add-Member -InputObject $desired -NotePropertyName lastDeployment -NotePropertyValue ([pscustomobject]$evidence) -Force } else { $desired.lastDeployment = [pscustomobject]$evidence }
        $activationAttempted = $true
        & $write $paths.StatePath $desired | Out-Null
        $verified = & $read $paths.StatePath
        if ([string](Get-CodexDeploymentValue $verified 'activeSlot' '') -ne $inactive) { throw 'Durable activation verification failed.' }
        return [pscustomobject]@{ Success = $true; ActiveSlot = $inactive; TargetCommit = $target; Evidence = $evidence; State = $verified; RollbackSucceeded = $null }
    } catch {
        $failure = $_.Exception.Message
        $evidence['error'] = $failure
        $rollbackLogPath = Join-Path $paths.DataRoot ('runs\deployment-' + $now.ToString('yyyyMMddTHHmmssfffZ') + '-rollback.log')
        $evidence['logs'] = @($logPath, $rollbackLogPath)
        $failureLogDirectory = Split-Path -Parent $logPath
        if (-not (Test-Path -LiteralPath $failureLogDirectory -PathType Container)) { New-Item -ItemType Directory -Path $failureLogDirectory -Force | Out-Null }
        Add-Content -LiteralPath $logPath -Value ("deployment target $target`nerror: $failure")
        $rollback = $null
        if ($evidence.prepared) {
            try {
                $rollbackArgs = @('-ExecutionPolicy','Bypass','-File',(Join-Path $activePath 'launch.ps1'),'-NoBuild')
                $rollbackLaunch = Invoke-CodexDeploymentProcess -FilePath 'powershell.exe' -Arguments $rollbackArgs -WorkingDirectory $activePath -ProcessRunner $ProcessRunner
                $rollbackHealth = if ([int]$rollbackLaunch.ExitCode -eq 0) { Invoke-CodexDeploymentHealth -Config $Config -HttpRunner $HttpRunner -SleepProvider $SleepProvider } else { [pscustomobject]@{ Success = $false; Endpoints = @{}; Error = 'Rollback launcher failed.' } }
                $rollbackProcesses = if ([int]$rollbackLaunch.ExitCode -eq 0 -and $rollbackHealth.Success) { @(Get-CodexDeploymentProcessEvidence -SlotPath $activePath -ProcessProvider $ProcessProvider) } else { @() }
                $rollbackStdout = [string](Get-CodexDeploymentValue $rollbackLaunch 'StdOut' (Get-CodexDeploymentValue $rollbackLaunch 'Output' ''))
                $rollbackStderr = [string](Get-CodexDeploymentValue $rollbackLaunch 'StdErr' '')
                $rollback = [pscustomobject]@{ launchExitCode = [int]$rollbackLaunch.ExitCode; stdout = $rollbackStdout; stderr = $rollbackStderr; processId = Get-CodexDeploymentValue -Object $rollbackLaunch -Name 'ProcessId' -Default $null; commandLine = [string](Get-CodexDeploymentValue -Object $rollbackLaunch -Name 'CommandLine' -Default ('powershell.exe ' + ($rollbackArgs -join ' '))); workingDirectory = $activePath; health = $rollbackHealth.Endpoints; processes = $rollbackProcesses; success = ([int]$rollbackLaunch.ExitCode -eq 0 -and $rollbackHealth.Success) }
                $rollbackDirectory = Split-Path -Parent $rollbackLogPath; if (-not (Test-Path -LiteralPath $rollbackDirectory -PathType Container)) { New-Item -ItemType Directory -Path $rollbackDirectory -Force | Out-Null }; Add-Content -LiteralPath $rollbackLogPath -Value ("powershell.exe $($rollbackArgs -join ' ')`nstdout: $rollbackStdout`nstderr: $rollbackStderr")
                $evidence['rollback'] = $rollback
            } catch { $evidence['rollback'] = [pscustomobject]@{ success = $false; error = $_.Exception.Message; log = $rollbackLogPath } }
        }
        $compensationSucceeded = $true
        if ($activationAttempted) {
            try {
                $observed = & $read $paths.StatePath
                $compensation = Copy-CodexDeploymentObject $observed
                if ($null -eq $compensation.PSObject.Properties['activeSlot']) { Add-Member -InputObject $compensation -NotePropertyName activeSlot -NotePropertyValue $active -Force } else { $compensation.activeSlot = $active }
                & $write $paths.StatePath $compensation | Out-Null
                $compensated = & $read $paths.StatePath
                if ([string](Get-CodexDeploymentValue $compensated 'activeSlot' '') -ne $active) { throw 'Durable active-slot compensation could not be verified.' }
                $evidence['activationCompensation'] = [pscustomobject]@{ attempted = $true; verified = $true; previousActiveSlot = $active }
            } catch {
                $compensationSucceeded = $false
                $evidence['activationCompensation'] = [pscustomobject]@{ attempted = $true; verified = $false; previousActiveSlot = $active; error = $_.Exception.Message }
            }
        }
        $evidence['failedTarget'] = Copy-CodexDeploymentObject ([pscustomobject]$evidence)
        $current = & $read $paths.StatePath
        $failedState = Copy-CodexDeploymentObject $current
        $failedDeployment = Get-CodexDeploymentValue $failedState 'deployment' $null
        if ($null -eq $failedDeployment) { $failedDeployment = Copy-CodexDeploymentObject $Deployment; Add-Member -InputObject $failedState -NotePropertyName deployment -NotePropertyValue $failedDeployment -Force }
        $rollbackEvidence = Get-CodexDeploymentValue -Object $evidence -Name 'rollback' -Default $null
        $failedDeployment.status = if (-not $compensationSucceeded -or ($null -ne $rollbackEvidence -and -not [bool]$rollbackEvidence.success)) { 'rollback-failed' } else { 'failed' }
        if ($null -eq $failedDeployment.PSObject.Properties['evidence']) { Add-Member -InputObject $failedDeployment -NotePropertyName evidence -NotePropertyValue ([pscustomobject]$evidence) -Force } else { $failedDeployment.evidence = [pscustomobject]$evidence }
        if ($null -eq $failedState.PSObject.Properties['lastDeployment']) { Add-Member -InputObject $failedState -NotePropertyName lastDeployment -NotePropertyValue ([pscustomobject]$evidence) -Force } else { $failedState.lastDeployment = [pscustomobject]$evidence }
        $failurePersistenceError = $null
        try { & $write $paths.StatePath $failedState | Out-Null } catch { $failurePersistenceError = $_.Exception.Message }
        if ($null -ne $failurePersistenceError) { $evidence['persistenceError'] = $failurePersistenceError }
        if ($failedDeployment.status -eq 'rollback-failed' -and $null -ne $GitHubCommandRunner) {
            $issue = [int](Get-CodexDeploymentValue $Deployment 'issueNumber' (Get-CodexDeploymentValue $Deployment 'sourcePr' 0))
            if ($issue -gt 0) { $repositoryName = [string](Get-CodexDeploymentValue -Object $Config -Name 'repository' -Default 'local/repository'); if ([string]::IsNullOrWhiteSpace($repositoryName)) { $repositoryName = 'local/repository' }; Add-CodexIssueComment -Repository $repositoryName -IssueNumber $issue -Body "[HIGH PRIORITY] Runtime deployment failed and rollback failed.`n`n$failure`n`nLogs: $($evidence.logs -join ', ')" -CommandRunner $GitHubCommandRunner | Out-Null }
        }
        return [pscustomobject]@{ Success = $false; ActiveSlot = $active; TargetCommit = $target; Evidence = [pscustomobject]$evidence; State = $failedState; RollbackSucceeded = if ($null -eq $rollback) { $null } else { [bool]$rollback.success }; Error = $failure; PersistenceError = $failurePersistenceError }
    }
}

function ConvertTo-CodexFullCommit {
    param([string] $Commit, [string] $Name = 'commit')
    $value = ([string]$Commit).Trim().ToLowerInvariant()
    if ($value -notmatch '^[0-9a-f]{40}$') { throw "The $Name must be a full 40-character commit SHA." }
    return $value
}

function Invoke-CodexDeploymentGit {
    param([string] $RepositoryRoot, [string[]] $Arguments, [scriptblock] $CommandRunner)
    return (Invoke-CodexGit -RepositoryRoot $RepositoryRoot -Arguments $Arguments -CommandRunner $CommandRunner).Trim()
}

function Get-CodexVerifiedMasterCommit {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [scriptblock] $GitCommandRunner
    )

    Invoke-CodexDeploymentGit -RepositoryRoot $RepositoryRoot -Arguments @('fetch', 'origin', 'master') -CommandRunner $GitCommandRunner | Out-Null
    $master = Invoke-CodexDeploymentGit -RepositoryRoot $RepositoryRoot -Arguments @('rev-parse', 'origin/master^{commit}') -CommandRunner $GitCommandRunner
    return ConvertTo-CodexFullCommit -Commit $master -Name 'origin/master commit'
}

function Assert-CodexCommitReachableFromMaster {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $Commit,
        [Parameter(Mandatory = $true)] [string] $MasterCommit,
        [scriptblock] $GitCommandRunner
    )

    try {
        Invoke-CodexDeploymentGit -RepositoryRoot $RepositoryRoot -Arguments @('merge-base', '--is-ancestor', $Commit, $MasterCommit) -CommandRunner $GitCommandRunner | Out-Null
    } catch {
        throw "Commit $Commit is not reachable from origin/master."
    }
    return $true
}

function Register-CodexPendingDeployment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [string] $DataRoot,
        [Parameter(Mandatory = $true)] [string] $MergeCommitSha,
        [Parameter(Mandatory = $true)] [int] $PullRequestNumber,
        [Parameter(Mandatory = $true)] [object] $State,
        [scriptblock] $GitCommandRunner,
        [DateTime] $Now = ([DateTime]::UtcNow),
        [string] $VerifiedMasterCommit
    )

    if ($PullRequestNumber -le 0) { throw 'A positive pull request number is required for deployment registration.' }
    $mergeCommit = ConvertTo-CodexFullCommit -Commit $MergeCommitSha -Name 'merge commit'
    $masterCommit = if ([string]::IsNullOrWhiteSpace($VerifiedMasterCommit)) { Get-CodexVerifiedMasterCommit -RepositoryRoot $RepositoryRoot -GitCommandRunner $GitCommandRunner } else { ConvertTo-CodexFullCommit -Commit $VerifiedMasterCommit -Name 'origin/master commit' }
    Assert-CodexCommitReachableFromMaster -RepositoryRoot $RepositoryRoot -Commit $mergeCommit -MasterCommit $masterCommit -GitCommandRunner $GitCommandRunner | Out-Null

    $existing = Get-CodexDeploymentValue -Object $State -Name 'deployment' -Default $null
    $existingStatus = [string](Get-CodexDeploymentValue -Object $existing -Name 'status' '')
    $target = $mergeCommit
    $sourcePr = $PullRequestNumber
    $requestedAt = $Now.ToUniversalTime().ToString('o')
    $replaced = $false
    $status = 'pending'
    if ($existingStatus -in @('pending', 'snoozed')) {
        $oldTargetText = [string](Get-CodexDeploymentValue -Object $existing -Name 'targetCommit' '')
        if ($oldTargetText -notmatch '^[0-9a-fA-F]{40}$') { throw 'Existing pending deployment has an invalid target commit.' }
        $oldSourcePr = [int](Get-CodexDeploymentValue -Object $existing -Name 'sourcePr' 0)
        $oldRequestedAt = [string](Get-CodexDeploymentValue -Object $existing -Name 'requestedAt' '')
        if ($oldSourcePr -le 0 -or [string]::IsNullOrWhiteSpace($oldRequestedAt)) { throw 'Existing pending deployment tuple is incomplete.' }
        if ($oldTargetText -match '^[0-9a-fA-F]{40}$') {
            $oldTarget = $oldTargetText.ToLowerInvariant()
            Assert-CodexCommitReachableFromMaster -RepositoryRoot $RepositoryRoot -Commit $oldTarget -MasterCommit $masterCommit -GitCommandRunner $GitCommandRunner | Out-Null
            $status = $existingStatus
            if ($oldTarget -eq $mergeCommit) {
                $target = $oldTarget
            } else {
                $incomingBeforeExisting = $false
                try { Invoke-CodexDeploymentGit -RepositoryRoot $RepositoryRoot -Arguments @('merge-base', '--is-ancestor', $mergeCommit, $oldTarget) -CommandRunner $GitCommandRunner | Out-Null; $incomingBeforeExisting = $true } catch { }
                if ($incomingBeforeExisting) {
                    $target = $oldTarget
                } else {
                    try { Invoke-CodexDeploymentGit -RepositoryRoot $RepositoryRoot -Arguments @('merge-base', '--is-ancestor', $oldTarget, $mergeCommit) -CommandRunner $GitCommandRunner | Out-Null; $target = $mergeCommit; $replaced = $true } catch { throw 'Pending deployment candidates are divergent.' }
                }
            }
            if (-not $replaced) {
                $sourcePr = $oldSourcePr; $requestedAt = $oldRequestedAt
            }
        }
    }

    $snooze = Get-CodexDeploymentValue -Object $existing -Name 'snoozeUntil' -Default $null
    $deployment = [pscustomobject][ordered]@{
        targetCommit = $target
        sourcePr = $sourcePr
        requestedAt = $requestedAt
        snoozeUntil = $snooze
        status = $status
    }
    if ($null -ne $State.PSObject.Properties['deployment']) { $State.deployment = $deployment }
    else { Add-Member -InputObject $State -NotePropertyName deployment -NotePropertyValue $deployment -Force }
    return $deployment
}

function Write-CodexDeploymentState {
    param([string] $StatePath, [object] $State, [scriptblock] $StateWriter)
    if ($null -ne $StateWriter) { & $StateWriter $StatePath $State | Out-Null }
    else { Write-CodexWorkerState -Path $StatePath -State $State }
}

function Set-CodexRepairedDeploymentMetadata {
    param([object] $Deployment, [object] $Existing, [bool] $SnoozeValid)
    if ($SnoozeValid) {
        $snooze = Get-CodexDeploymentValue -Object $Existing -Name 'snoozeUntil' -Default $null
        if ($null -ne $snooze -and -not [string]::IsNullOrWhiteSpace([string]$snooze)) {
            $Deployment.snoozeUntil = $snooze
            $existingStatus = [string](Get-CodexDeploymentValue -Object $Existing -Name 'status' '')
            if ($existingStatus -in @('pending', 'snoozed')) { $Deployment.status = $existingStatus }
        }
    }
    return $Deployment
}

function ConvertTo-CodexDeploymentDateTicks {
    param([object] $Value, [string] $Name)
    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) { return $null }
    $parsed = [DateTime]::MinValue
    if (-not [DateTime]::TryParse([string]$Value, [ref]$parsed)) { throw "The persisted deployment $Name is invalid." }
    return $parsed.ToUniversalTime().Ticks
}

function Assert-CodexDurablePendingDeployment {
    param([object] $State, [int] $IssueNumber, [object] $ExpectedDeployment, [string] $ExpectedTargetCommit, [int] $PullRequestNumber)
    $attempt = Get-CodexIssueAttemptState -State $State -IssueNumber $IssueNumber
    if ($null -eq $attempt) { throw "The persisted worker state does not contain issue #$IssueNumber." }
    $actual = Get-CodexDeploymentValue -Object $State -Name 'deployment' -Default $null
    if ($null -eq $actual) { throw 'The deployment state was not persisted.' }

    $expectedTarget = ConvertTo-CodexFullCommit -Commit $ExpectedTargetCommit -Name 'expected deployment target'
    $actualTarget = ConvertTo-CodexFullCommit -Commit ([string](Get-CodexDeploymentValue -Object $actual -Name 'targetCommit' '')) -Name 'persisted deployment target'
    if ($actualTarget -ne $expectedTarget) { throw 'The persisted deployment target does not match the trusted merge commit.' }

    $actualSourcePr = 0
    if (-not [int]::TryParse([string](Get-CodexDeploymentValue -Object $actual -Name 'sourcePr' 0), [ref]$actualSourcePr) -or $actualSourcePr -ne $PullRequestNumber) { throw 'The persisted deployment source pull request does not match the trusted close event.' }
    $expectedStatus = [string](Get-CodexDeploymentValue -Object $ExpectedDeployment -Name 'status' '')
    $actualStatus = [string](Get-CodexDeploymentValue -Object $actual -Name 'status' '')
    if ($expectedStatus -notin @('pending', 'snoozed') -or $actualStatus -ne $expectedStatus) { throw 'The persisted deployment status is not coherent.' }

    $expectedRequestedAt = ConvertTo-CodexDeploymentDateTicks -Value (Get-CodexDeploymentValue -Object $ExpectedDeployment -Name 'requestedAt' $null) -Name 'requestedAt'
    $actualRequestedAt = ConvertTo-CodexDeploymentDateTicks -Value (Get-CodexDeploymentValue -Object $actual -Name 'requestedAt' $null) -Name 'requestedAt'
    if ($null -eq $expectedRequestedAt -or $null -eq $actualRequestedAt -or $expectedRequestedAt -ne $actualRequestedAt) { throw 'The persisted deployment request timestamp does not match the expected tuple.' }

    $expectedSnooze = ConvertTo-CodexDeploymentDateTicks -Value (Get-CodexDeploymentValue -Object $ExpectedDeployment -Name 'snoozeUntil' $null) -Name 'snoozeUntil'
    $actualSnooze = ConvertTo-CodexDeploymentDateTicks -Value (Get-CodexDeploymentValue -Object $actual -Name 'snoozeUntil' $null) -Name 'snoozeUntil'
    if ($null -eq $expectedSnooze) { if ($null -ne $actualSnooze) { throw 'The persisted deployment snooze does not match the expected tuple.' } }
    elseif ($null -eq $actualSnooze -or $expectedSnooze -ne $actualSnooze) { throw 'The persisted deployment snooze does not match the expected tuple.' }
    return $actual
}

function Add-CodexCleanupBlockerComment {
    param([string] $Repository, [int] $PullRequestNumber, [int] $IssueNumber, [string[]] $Blockers, [scriptblock] $GitHubCommandRunner)
    if (@($Blockers).Count -eq 0) { return }
    $body = "Codex worktree cleanup was blocked; the worktree was preserved.`n`n" + (@($Blockers | ForEach-Object { "- $_" }) -join "`n")
    if ($PullRequestNumber -gt 0) {
        Add-CodexPullRequestComment -Repository $Repository -PullRequestNumber $PullRequestNumber -Body $body -CommandRunner $GitHubCommandRunner | Out-Null
    } elseif ($IssueNumber -gt 0) {
        Add-CodexIssueComment -Repository $Repository -IssueNumber $IssueNumber -Body $body -CommandRunner $GitHubCommandRunner | Out-Null
    }
}

function Resolve-CodexClosedPullRequestIssueNumber {
    param([Parameter(Mandatory = $true)][object] $Context, [string] $Repository, [int] $PullRequestNumber)
    $numberProperty = $Context.PSObject.Properties['number']
    if ($null -eq $numberProperty -or [int]$numberProperty.Value -ne $PullRequestNumber) { throw 'The resolved pull request number does not match the requested pull request.' }
    $referencesProperty = $Context.PSObject.Properties['closingIssuesReferences']
    $references = @(
        if ($null -ne $referencesProperty -and $null -ne $referencesProperty.Value) { @($referencesProperty.Value) }
    )
    if ($references.Count -ne 1) { throw 'The pull request must contain exactly one linked issue in its trusted closing references.' }
    $reference = $references[0]
    $issueProperty = $reference.PSObject.Properties['number']
    $repositoryProperty = $reference.PSObject.Properties['repository']
    $nameProperty = if ($null -ne $repositoryProperty -and $null -ne $repositoryProperty.Value) { $repositoryProperty.Value.PSObject.Properties['nameWithOwner'] } else { $null }
    if ($null -eq $issueProperty -or [int]$issueProperty.Value -le 0 -or $null -eq $nameProperty -or -not [string]::Equals([string]$nameProperty.Value, $Repository, [StringComparison]::OrdinalIgnoreCase)) { throw 'The pull request closing reference is invalid or belongs to another repository.' }
    return [int]$issueProperty.Value
}

function Get-CodexPrRepositoryName {
    param([object] $Context, [string] $PropertyName)
    $property = $Context.PSObject.Properties[$PropertyName]
    if ($null -eq $property -or $null -eq $property.Value) { return '' }
    if ($property.Value -is [string]) { return [string]$property.Value }
    $name = $property.Value.PSObject.Properties['nameWithOwner']
    if ($null -ne $name) { return [string]$name.Value }
    return ''
}

function Assert-CodexClosedPullRequestContext {
    param(
        [Parameter(Mandatory = $true)][object] $Context,
        [Parameter(Mandatory = $true)][object] $Attempt,
        [Parameter(Mandatory = $true)][string] $Repository,
        [Parameter(Mandatory = $true)][int] $PullRequestNumber,
        [Parameter(Mandatory = $true)][string] $HeadBranch,
        [Parameter(Mandatory = $true)][bool] $Merged,
        [string] $MergeCommitSha
    )
    $number = $Context.PSObject.Properties['number']
    $url = $Context.PSObject.Properties['url']
    $state = $Context.PSObject.Properties['state']
    $base = $Context.PSObject.Properties['baseRefName']
    $head = $Context.PSObject.Properties['headRefName']
    if ($null -eq $number -or [int]$number.Value -ne $PullRequestNumber) { throw 'Pull request context number does not match the close event.' }
    $savedBranch = [string](Get-CodexDeploymentValue -Object $Attempt -Name 'branch' '')
    if ([string]::IsNullOrWhiteSpace($HeadBranch) -or [string]::IsNullOrWhiteSpace($savedBranch) -or $null -eq $head -or [string]::IsNullOrWhiteSpace([string]$head.Value)) { throw 'Pull request and saved Codex branch names are required.' }
    $savedUrl = [string](Get-CodexDeploymentValue -Object $Attempt -Name 'prUrl' '')
    if ([string]::IsNullOrWhiteSpace($savedUrl) -or $null -eq $url -or -not [string]::Equals($savedUrl, [string]$url.Value, [StringComparison]::OrdinalIgnoreCase)) { throw 'Pull request URL does not match the saved Codex attempt.' }
    if ([string]$url.Value -notmatch ('/pull/' + [regex]::Escape([string]$PullRequestNumber) + '$')) { throw 'Pull request URL does not identify the close event pull request.' }
    if ($null -eq $state -or [string]$state.Value -ne 'CLOSED') { throw 'Pull request is not closed.' }
    if ($null -eq $base -or [string]$base.Value -ne 'master') { throw 'Pull request base branch is not master.' }
    if ($null -eq $head -or [string]$head.Value -ne $HeadBranch) { throw 'Pull request head branch does not match the close event.' }
    if ($savedBranch -ne $HeadBranch) { throw 'Pull request head branch does not match the saved Codex branch.' }
    if ((Get-CodexPrRepositoryName -Context $Context -PropertyName 'headRepository') -ne $Repository) { throw 'Pull request head repository does not match the current repository.' }
    if ((Get-CodexPrRepositoryName -Context $Context -PropertyName 'baseRepository') -ne $Repository) { throw 'Pull request base repository does not match the current repository.' }
    $mergedAt = $Context.PSObject.Properties['mergedAt']
    $mergeProperty = $Context.PSObject.Properties['mergeCommit']
    $mergeOid = ''
    if ($null -ne $mergeProperty -and $null -ne $mergeProperty.Value) {
        $oid = $mergeProperty.Value.PSObject.Properties['oid']
        if ($null -ne $oid) { $mergeOid = [string]$oid.Value }
        elseif ($mergeProperty.Value -is [string]) { $mergeOid = [string]$mergeProperty.Value }
    }
    $contextMerged = ($null -ne $mergedAt -and $null -ne $mergedAt.Value -and -not [string]::IsNullOrWhiteSpace([string]$mergedAt.Value)) -or -not [string]::IsNullOrWhiteSpace($mergeOid)
    if ($contextMerged -ne $Merged) { throw 'Pull request merged state does not match the close event.' }
    if ($Merged) {
        $expected = ConvertTo-CodexFullCommit -Commit $MergeCommitSha -Name 'merge commit'
        if ($mergeOid.ToLowerInvariant() -ne $expected) { throw 'Pull request merge commit does not match the close event.' }
    } elseif (-not [string]::IsNullOrWhiteSpace($MergeCommitSha)) {
        throw 'An unmerged pull request must not provide a merge commit SHA.'
    }
    return $true
}

function Register-CodexPullRequestClosed {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $Repository,
        [Parameter(Mandatory = $true)] [int] $PullRequestNumber,
        [int] $IssueNumber = 0,
        [Parameter(Mandatory = $true)] [bool] $Merged,
        [string] $MergeCommitSha,
        [Parameter(Mandatory = $true)] [string] $HeadBranch,
        [string] $RepositoryRoot,
        [string] $DataRoot,
        [scriptblock] $GitHubCommandRunner,
        [scriptblock] $GitCommandRunner,
        [scriptblock] $StateReader,
        [scriptblock] $StateWriter,
        [scriptblock] $LockProvider,
        [scriptblock] $UnlockProvider,
        [scriptblock] $CleanupProvider,
        [scriptblock] $ProcessProvider,
        [DateTime] $Now = ([DateTime]::UtcNow)
    )

    if ($PullRequestNumber -le 0) { throw 'A positive pull request number is required.' }
    if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = (Get-Location).Path }
    $paths = Resolve-CodexWorkerPaths -RepositoryRoot $RepositoryRoot -DataRoot $DataRoot
    $lock = $null
    $state = $null
    $linkedIssue = 0
    try {
        if ($null -ne $LockProvider) { $lock = & $LockProvider $paths.LockPath } else { $lock = Enter-CodexWorkerLock -Path $paths.LockPath }
        $state = if ($null -ne $StateReader) { & $StateReader $paths.StatePath } else { Read-CodexWorkerState -Path $paths.StatePath }
        $attempt = $null
        $context = Get-CodexPullRequestContext -Repository $Repository -PullRequestNumber $PullRequestNumber -CommandRunner $GitHubCommandRunner
        $savedIssue = if ($IssueNumber -gt 0) { Get-CodexIssueAttemptState -State $state -IssueNumber $IssueNumber } else { $null }
        if ($null -ne $savedIssue) { Assert-CodexClosedPullRequestContext -Context $context -Attempt $savedIssue -Repository $Repository -PullRequestNumber $PullRequestNumber -HeadBranch $HeadBranch -Merged $Merged -MergeCommitSha $MergeCommitSha | Out-Null }
        $linkedIssue = Resolve-CodexClosedPullRequestIssueNumber -Context $context -Repository $Repository -PullRequestNumber $PullRequestNumber
        if ($IssueNumber -gt 0 -and $IssueNumber -ne $linkedIssue) { throw "The supplied issue number $IssueNumber does not match the pull request closing reference $linkedIssue." }
        $IssueNumber = $linkedIssue
        $attempt = Get-CodexIssueAttemptState -State $state -IssueNumber $IssueNumber
        if ($null -eq $attempt) { throw "No saved Codex state exists for issue #$IssueNumber." }
        Assert-CodexClosedPullRequestContext -Context $context -Attempt $attempt -Repository $Repository -PullRequestNumber $PullRequestNumber -HeadBranch $HeadBranch -Merged $Merged -MergeCommitSha $MergeCommitSha | Out-Null
        if ([string]::IsNullOrWhiteSpace([string](Get-CodexDeploymentValue -Object $context -Name 'headRefName' '')) -or [string]::IsNullOrWhiteSpace($HeadBranch) -or [string]::IsNullOrWhiteSpace([string](Get-CodexDeploymentValue -Object $attempt -Name 'branch' ''))) { throw 'Pull request and saved Codex branch names are required.' }
        $cleanupStatus = [string](Get-CodexDeploymentValue -Object $attempt -Name 'cleanupStatus' '')
        $cleanupAlreadyCompleted = ($cleanupStatus -eq 'completed')
        if ($cleanupAlreadyCompleted -and -not $Merged) {
            return [pscustomobject][ordered]@{ PullRequestNumber = $PullRequestNumber; IssueNumber = $IssueNumber; Merged = $Merged; CleanedUp = $true; Blockers = @(); DeploymentCreated = $false; Deployment = $state.deployment; State = $state }
        }
        $verifiedMaster = $null
        $repairIncompleteDeployment = $false
        if ($Merged) {
            $verifiedMaster = Get-CodexVerifiedMasterCommit -RepositoryRoot $paths.RepositoryRoot -GitCommandRunner $GitCommandRunner
            $candidate = ConvertTo-CodexFullCommit -Commit $MergeCommitSha -Name 'merge commit'
            Assert-CodexCommitReachableFromMaster -RepositoryRoot $paths.RepositoryRoot -Commit $candidate -MasterCommit $verifiedMaster -GitCommandRunner $GitCommandRunner | Out-Null
            $previewState = (($state | ConvertTo-Json -Depth 20) | ConvertFrom-Json)
            $existingDeployment = Get-CodexDeploymentValue -Object $state -Name 'deployment' -Default $null
            $existingTarget = ''
            $existingTargetValid = $false
            try { $existingTarget = ConvertTo-CodexFullCommit -Commit ([string](Get-CodexDeploymentValue -Object $existingDeployment -Name 'targetCommit' '')) -Name 'existing deployment target'; $existingTargetValid = $true } catch { }
            $existingStatus = [string](Get-CodexDeploymentValue -Object $existingDeployment -Name 'status' '')
            $existingSourcePr = 0
            $existingSourcePrValid = [int]::TryParse([string](Get-CodexDeploymentValue -Object $existingDeployment -Name 'sourcePr' 0), [ref]$existingSourcePr) -and $existingSourcePr -gt 0
            $existingRequestedAt = [string](Get-CodexDeploymentValue -Object $existingDeployment -Name 'requestedAt' '')
            $existingRequestedAtValue = [DateTime]::MinValue
            $existingRequestedAtValid = [DateTime]::TryParse($existingRequestedAt, [ref]$existingRequestedAtValue) -and -not [string]::IsNullOrWhiteSpace($existingRequestedAt)
            $existingSnooze = Get-CodexDeploymentValue -Object $existingDeployment -Name 'snoozeUntil' -Default $null
            $existingSnoozeValid = $true
            if ($null -ne $existingSnooze -and -not [string]::IsNullOrWhiteSpace([string]$existingSnooze)) {
                $existingSnoozeValue = [DateTime]::MinValue
                $existingSnoozeValid = [DateTime]::TryParse([string]$existingSnooze, [ref]$existingSnoozeValue)
            }
            $existingTupleComplete = $existingTargetValid -and $existingStatus -in @('pending', 'snoozed') -and $existingSourcePrValid -and $existingRequestedAtValid -and $existingSnoozeValid
            $existingTargetMatches = $existingTargetValid -and $existingTarget -eq $candidate
            if ($cleanupAlreadyCompleted -and (-not $existingTupleComplete -or ($existingTargetMatches -and $existingSourcePr -ne $PullRequestNumber))) {
                $previewState.deployment = $null
                $repairIncompleteDeployment = $true
            }
            $previewDeployment = Register-CodexPendingDeployment -RepositoryRoot $paths.RepositoryRoot -DataRoot $paths.DataRoot -MergeCommitSha $MergeCommitSha -PullRequestNumber $PullRequestNumber -State $previewState -GitCommandRunner $GitCommandRunner -Now $Now -VerifiedMasterCommit $verifiedMaster
            if ($repairIncompleteDeployment) { Set-CodexRepairedDeploymentMetadata -Deployment $previewDeployment -Existing $existingDeployment -SnoozeValid $existingSnoozeValid | Out-Null }
            if ($cleanupAlreadyCompleted) {
                $previewTarget = ConvertTo-CodexFullCommit -Commit ([string]$previewDeployment.targetCommit) -Name 'preview deployment target'
                $previewSnooze = Get-CodexDeploymentValue -Object $previewDeployment -Name 'snoozeUntil' -Default $null
                $deploymentVerified = $existingTargetValid -and $existingStatus -in @('pending', 'snoozed') -and $existingSourcePr -eq $PullRequestNumber -and -not [string]::IsNullOrWhiteSpace($existingRequestedAt) -and $existingTarget -eq $previewTarget -and $existingStatus -eq [string]$previewDeployment.status -and [string]$existingSnooze -eq [string]$previewSnooze
                if ($deploymentVerified) {
                    return [pscustomobject][ordered]@{ PullRequestNumber = $PullRequestNumber; IssueNumber = $IssueNumber; Merged = $Merged; CleanedUp = $true; Blockers = @(); DeploymentCreated = $false; Deployment = $state.deployment; State = $state }
                }
            }
        }
        $branch = [string](Get-CodexDeploymentValue -Object $attempt -Name 'branch' '')
        $worktree = [string](Get-CodexDeploymentValue -Object $attempt -Name 'worktree' '')
        $blockers = [System.Collections.Generic.List[string]]::new()
        if ([string]::IsNullOrWhiteSpace($branch) -or $branch -ne $HeadBranch) { $blockers.Add('Pull request head branch does not match the saved Codex issue branch.') | Out-Null }
        if ([string]::IsNullOrWhiteSpace($worktree) -and $cleanupStatus -ne 'completed') { $blockers.Add('Saved Codex issue state does not contain a worktree.') | Out-Null }

        $cleanedUp = $false
        if ($cleanupAlreadyCompleted) {
            $cleanedUp = $true
        } elseif ($blockers.Count -eq 0) {
            if ($null -ne $CleanupProvider) {
                $cleanupResult = @(& $CleanupProvider $paths.RepositoryRoot $paths.WorktreeRoot $worktree $branch $GitCommandRunner $ProcessProvider)
                foreach ($item in $cleanupResult) { if (-not [string]::IsNullOrWhiteSpace([string]$item)) { $blockers.Add([string]$item) | Out-Null } }
            } else {
                $guardBlockers = @(Test-CodexWorktreeCleanup -RepositoryRoot $paths.RepositoryRoot -WorktreeRoot $paths.WorktreeRoot -WorktreePath $worktree -BranchName $branch -CommandRunner $GitCommandRunner -ProcessProvider $ProcessProvider)
                foreach ($item in $guardBlockers) { $blockers.Add([string]$item) | Out-Null }
                if ($blockers.Count -eq 0) { Remove-CodexWorktree -RepositoryRoot $paths.RepositoryRoot -WorktreeRoot $paths.WorktreeRoot -WorktreePath $worktree -BranchName $branch -CommandRunner $GitCommandRunner -ProcessProvider $ProcessProvider | Out-Null; $cleanedUp = $true }
            }
            if ($null -ne $CleanupProvider -and $blockers.Count -eq 0) { $cleanedUp = $true }
        }
        if ($cleanedUp -and $blockers.Count -eq 0 -and -not $cleanupAlreadyCompleted) {
            Set-CodexOrchestrationField $attempt 'worktree' $null
            Set-CodexOrchestrationField $attempt 'cleanupStatus' 'completed'
            Set-CodexOrchestrationField $attempt 'cleanupAt' $Now.ToUniversalTime().ToString('o')
            Set-CodexOrchestrationField $attempt 'cleanupBlockers' @()
        } elseif ($blockers.Count -gt 0) {
            Set-CodexOrchestrationField $attempt 'cleanupStatus' 'blocked'
            Set-CodexOrchestrationField $attempt 'cleanupAt' $Now.ToUniversalTime().ToString('o')
            Set-CodexOrchestrationField $attempt 'cleanupBlockers' @($blockers.ToArray())
        }
        if (($cleanedUp -or $blockers.Count -gt 0) -and -not $cleanupAlreadyCompleted) { Write-CodexDeploymentState -StatePath $paths.StatePath -State $state -StateWriter $StateWriter }
        Add-CodexCleanupBlockerComment -Repository $Repository -PullRequestNumber $PullRequestNumber -IssueNumber $IssueNumber -Blockers @($blockers.ToArray()) -GitHubCommandRunner $GitHubCommandRunner

        $deploymentCreated = $false
        if ($Merged) {
            if ([string]::IsNullOrWhiteSpace($MergeCommitSha)) { throw 'A merged pull request must provide its merge commit SHA.' }
            $registrationState = $state
            if ($repairIncompleteDeployment) {
                $registrationState = (($state | ConvertTo-Json -Depth 20) | ConvertFrom-Json)
                $registrationState.deployment = $null
            }
            $deployment = Register-CodexPendingDeployment -RepositoryRoot $paths.RepositoryRoot -DataRoot $paths.DataRoot -MergeCommitSha $MergeCommitSha -PullRequestNumber $PullRequestNumber -State $registrationState -GitCommandRunner $GitCommandRunner -Now $Now -VerifiedMasterCommit $verifiedMaster
            if ($repairIncompleteDeployment) {
                Set-CodexRepairedDeploymentMetadata -Deployment $deployment -Existing $existingDeployment -SnoozeValid $existingSnoozeValid | Out-Null
                if ($null -ne $state.PSObject.Properties['deployment']) { $state.deployment = $deployment }
                else { Add-Member -InputObject $state -NotePropertyName deployment -NotePropertyValue $deployment -Force }
            }
            Write-CodexDeploymentState -StatePath $paths.StatePath -State $state -StateWriter $StateWriter
            $durableState = if ($null -ne $StateReader) { & $StateReader $paths.StatePath } else { Read-CodexWorkerState -Path $paths.StatePath }
            $durableDeployment = Assert-CodexDurablePendingDeployment -State $durableState -IssueNumber $IssueNumber -ExpectedDeployment $deployment -ExpectedTargetCommit $MergeCommitSha -PullRequestNumber $PullRequestNumber
            $state = $durableState
            $deployment = $durableDeployment
            $deploymentCreated = $true
            $currentStatus = [string](Get-CodexDeploymentValue -Object $attempt -Name 'status' 'pr-ready')
            $labels = if ($currentStatus -match '^codex:') { @($currentStatus) } else { @("codex:$currentStatus") }
            Set-CodexIssueStatus -Repository $Repository -IssueNumber $IssueNumber -Status 'done' -CurrentLabels $labels -CommandRunner $GitHubCommandRunner | Out-Null
        }
        return [pscustomobject][ordered]@{ PullRequestNumber = $PullRequestNumber; IssueNumber = $IssueNumber; Merged = $Merged; CleanedUp = $cleanedUp; Blockers = @($blockers.ToArray()); DeploymentCreated = $deploymentCreated; Deployment = if ($deploymentCreated) { $state.deployment } else { $null } }
    } finally {
        if ($null -ne $lock) { if ($null -ne $UnlockProvider) { & $UnlockProvider $lock } else { Exit-CodexWorkerLock -Handle $lock } }
    }
}
