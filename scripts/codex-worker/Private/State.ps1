function Resolve-CodexWorkerPaths {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot,

        [string] $DataRoot
    )

    if (-not [System.IO.Path]::IsPathRooted($RepositoryRoot) -or
        ($RepositoryRoot -match '^[A-Za-z]:[^\\/]')) {
        throw 'RepositoryRoot must be absolute.'
    }

    if ([string]::IsNullOrWhiteSpace($DataRoot)) {
        $DataRoot = Join-Path $env:LOCALAPPDATA 'AutomationWorkbench\CodexWorker'
    }

    if (-not [System.IO.Path]::IsPathRooted($DataRoot) -or
        ($DataRoot -match '^[A-Za-z]:[^\\/]')) {
        throw 'DataRoot must be absolute.'
    }

    $resolvedRepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
    $resolvedDataRoot = [System.IO.Path]::GetFullPath($DataRoot)

    [ordered]@{
        RepositoryRoot = $resolvedRepositoryRoot
        WorktreeRoot = [System.IO.Path]::GetFullPath((Join-Path $resolvedRepositoryRoot '.worktrees'))
        DataRoot = $resolvedDataRoot
        StatePath = [System.IO.Path]::GetFullPath((Join-Path $resolvedDataRoot 'state.json'))
        RunRoot = [System.IO.Path]::GetFullPath((Join-Path $resolvedDataRoot 'runs'))
        ConfigPath = [System.IO.Path]::GetFullPath((Join-Path $resolvedDataRoot 'config.json'))
        LockPath = [System.IO.Path]::GetFullPath((Join-Path $resolvedDataRoot 'worker.lock'))
    }
}

function New-CodexWorkerDefaultState {
    [pscustomobject]@{
        schemaVersion = 1
        issues = [ordered]@{}
        deployment = $null
    }
}

function Read-CodexWorkerState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        return New-CodexWorkerDefaultState
    }

    try {
        $state = [System.IO.File]::ReadAllText($fullPath) | ConvertFrom-Json
        if ($null -eq $state) {
            throw 'State JSON was empty.'
        }

        return $state
    } catch {
        $directory = [System.IO.Path]::GetDirectoryName($fullPath)
        $baseName = [System.IO.Path]::GetFileNameWithoutExtension($fullPath)
        $extension = [System.IO.Path]::GetExtension($fullPath)
        $stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ', [Globalization.CultureInfo]::InvariantCulture)
        $quarantinePath = Join-Path $directory "$baseName.corrupt.$stamp$extension"
        $suffix = 1
        while (Test-Path -LiteralPath $quarantinePath) {
            $quarantinePath = Join-Path $directory "$baseName.corrupt.$stamp-$suffix$extension"
            $suffix++
        }

        Move-Item -LiteralPath $fullPath -Destination $quarantinePath
        return New-CodexWorkerDefaultState
    }
}

function Write-CodexWorkerState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [object] $State
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $directory = [System.IO.Path]::GetDirectoryName($fullPath)
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $temporaryPath = "$fullPath.tmp"
    $json = $State | ConvertTo-Json -Depth 20
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    try {
        [System.IO.File]::WriteAllText($temporaryPath, $json, $utf8)
        Move-Item -LiteralPath $temporaryPath -Destination $fullPath -Force
    } finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function New-CodexIssueAttemptState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [int] $IssueNumber,
        [int] $Attempt = 1,
        [string] $AgentProvider = 'Codex'
    )

    return [pscustomobject][ordered]@{
        issueNumber = $IssueNumber
        status = 'queued'
        attempt = $Attempt
        branch = $null
        worktree = $null
        threadId = $null
        runDirectory = $null
        commit = $null
        prUrl = $null
        retryCount = 0
        publicationStage = 'none'
        lastError = $null
        agentProvider = $AgentProvider
    }
}

function Get-CodexIssueAttemptState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [object] $State,
        [Parameter(Mandatory = $true)] [int] $IssueNumber
    )

    if ($null -eq $State) { return $null }
    $issuesProperty = $State.PSObject.Properties['issues']
    if ($null -eq $issuesProperty -or $null -eq $issuesProperty.Value) { return $null }
    $key = [string]$IssueNumber
    $issues = $issuesProperty.Value
    if ($issues -is [System.Collections.IDictionary]) {
        if ($issues.Contains($key)) { return $issues[$key] }
        return $null
    }
    $issueProperty = $issues.PSObject.Properties[$key]
    if ($null -eq $issueProperty) { return $null }
    return $issueProperty.Value
}

function Set-CodexIssueAttemptState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [object] $State,
        [Parameter(Mandatory = $true)] [int] $IssueNumber,
        [Parameter(Mandatory = $true)] [object] $AttemptState
    )

    if ($null -eq $State) { throw 'State is required.' }
    $issuesProperty = $State.PSObject.Properties['issues']
    if ($null -eq $issuesProperty -or $null -eq $issuesProperty.Value) {
        Add-Member -InputObject $State -NotePropertyName issues -NotePropertyValue ([ordered]@{}) -Force
        $issues = $State.issues
    } else {
        $issues = $issuesProperty.Value
    }

    $key = [string]$IssueNumber
    if ($issues -is [System.Collections.IDictionary]) {
        $issues[$key] = $AttemptState
    } elseif ($null -ne $issues.PSObject.Properties[$key]) {
        $issues.$key = $AttemptState
    } else {
        Add-Member -InputObject $issues -NotePropertyName $key -NotePropertyValue $AttemptState -Force
    }
    return $State
}

function Write-CodexIssueAttemptState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [int] $IssueNumber,
        [Parameter(Mandatory = $true)] [object] $AttemptState
    )

    $state = Read-CodexWorkerState -Path $Path
    Set-CodexIssueAttemptState -State $state -IssueNumber $IssueNumber -AttemptState $AttemptState | Out-Null
    Write-CodexWorkerState -Path $Path -State $state
    return $AttemptState
}

function Set-CodexOrchestrationField {
    param([object] $Object, [string] $Name, [object] $Value)
    if ($null -eq $Object) { return }
    if ($null -ne $Object.PSObject.Properties[$Name]) { $Object.$Name = $Value }
    else { Add-Member -InputObject $Object -NotePropertyName $Name -NotePropertyValue $Value -Force }
}

function Get-CodexOrchestrationField {
    param([object] $Object, [string] $Name, [object] $Default = $null)
    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return $Default }
    return $property.Value
}

function Get-CodexOrchestrationComments {
    param([object] $IssueContext)
    $comments = Get-CodexOrchestrationField -Object $IssueContext -Name 'comments' -Default @()
    return @($comments)
}

function Test-CodexMilestoneAlreadyPresent {
    param([object] $IssueContext, [string] $Heading)
    foreach ($comment in @(Get-CodexOrchestrationComments -IssueContext $IssueContext)) {
        $body = [string](Get-CodexOrchestrationField -Object $comment -Name 'body' -Default '')
        if ($body -like "*$Heading*") { return $true }
    }
    return $false
}

function Resolve-CodexIssueRunProvider {
    param([object] $AttemptState, [object] $Config, [string] $EventName)
    $configured = [string](Get-CodexOrchestrationField $Config 'provider' '')
    if ($null -eq $AttemptState) {
        if ([string]::IsNullOrWhiteSpace($configured)) { return Resolve-CodexWorkerProvider -Provider 'Codex' -EventName 'codex' }
        return Resolve-CodexWorkerProvider -Provider $configured -EventName $EventName
    }
    $persisted = [string](Get-CodexOrchestrationField $AttemptState 'agentProvider' 'Codex')
    if ([string]::IsNullOrWhiteSpace($persisted)) { $persisted = 'Codex'; Set-CodexOrchestrationField $AttemptState 'agentProvider' $persisted }
    if (-not [string]::IsNullOrWhiteSpace($configured) -and -not [string]::Equals($configured, $persisted, [StringComparison]::OrdinalIgnoreCase)) { throw "Configured provider '$configured' does not match persisted provider '$persisted'." }
    $providerEvent = if ([string]::IsNullOrWhiteSpace($configured) -and $persisted -eq 'Codex') { 'codex' } else { $EventName }
    return Resolve-CodexWorkerProvider -Provider $persisted -EventName $providerEvent
}

function Invoke-CodexIssueRun {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $Repository,
        [Parameter(Mandatory = $true)] [int] $IssueNumber,
        [Parameter(Mandatory = $true)] [string] $Actor,
        [Parameter(Mandatory = $true)] [string] $EventName,
        [string] $RepositoryRoot,
        [string] $DataRoot,
        [object] $Config,
        [string] $StatePath,
        [switch] $DryRun,
        [scriptblock] $GitHubCommandRunner,
        [scriptblock] $GitCommandRunner,
        [scriptblock] $WorktreeProvider,
        [scriptblock] $SetupProvider,
        [scriptblock] $CodexProvider,
        [scriptblock] $LockProvider,
        [scriptblock] $UnlockProvider,
        [scriptblock] $PublicationProvider,
        [scriptblock] $StateWriter,
        [scriptblock] $StateReader
    )

    if ($null -eq $Config) { $Config = [pscustomobject]@{} }
    if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = [string](Get-CodexOrchestrationField $Config 'repositoryRoot' (Get-Location).Path) }
    if ([string]::IsNullOrWhiteSpace($StatePath)) {
        $paths = Resolve-CodexWorkerPaths -RepositoryRoot $RepositoryRoot -DataRoot $DataRoot
        $StatePath = $paths.StatePath
    } else {
        $StatePath = [IO.Path]::GetFullPath($StatePath)
    }
    $effectiveDataRoot = $DataRoot
    if ([string]::IsNullOrWhiteSpace($effectiveDataRoot)) { $effectiveDataRoot = Split-Path -Parent $StatePath }
    $paths = Resolve-CodexWorkerPaths -RepositoryRoot $RepositoryRoot -DataRoot $effectiveDataRoot

    if ($DryRun) {
        Assert-TrustedGitHubActor -Repository $Repository -Actor $Actor -CommandRunner $GitHubCommandRunner | Out-Null
        $issue = Get-CodexIssueContext -Repository $Repository -IssueNumber $IssueNumber -CommandRunner $GitHubCommandRunner
        $development = Get-CodexIssueDevelopment -Repository $Repository -IssueNumber $IssueNumber -CommandRunner $GitHubCommandRunner
        $branchName = Get-CodexIssueBranchName -IssueNumber $IssueNumber -Title ([string](Get-CodexOrchestrationField $issue 'title' "Issue $IssueNumber"))
        Write-CodexWorkerMilestone -IssueNumber $IssueNumber -Phase 'STARTED' -Details ([string](Get-CodexOrchestrationField $issue 'title' "Issue $IssueNumber"))
        return [pscustomobject][ordered]@{
            DryRun = $true
            IssueNumber = $IssueNumber
            Branch = $branchName
            Issue = $issue
            Development = $development
            WorktreeCreated = $false
            CodexInvoked = $false
        }
    }

    $state = $null
    $attemptState = $null
    $provider = $null
    $issue = $null
    $development = $null
    $branchName = $null
    $existingStatus = ''
    $resumeEvent = $false
    $lockHandle = $null
    $lockPath = $paths.LockPath
    try {
        if ($null -ne $LockProvider) { $lockHandle = & $LockProvider $lockPath }
        else {
            $timeout = [int](Get-CodexOrchestrationField $Config 'workerLockTimeoutSeconds' 30)
            $lockHandle = Enter-CodexWorkerLock -Path $lockPath -TimeoutSeconds $timeout
        }

        # Re-read every mutable external context after taking the lock. This
        # prevents a queued duplicate or publication recovery from using data
        # observed before another worker released the lock.
        Assert-TrustedGitHubActor -Repository $Repository -Actor $Actor -CommandRunner $GitHubCommandRunner | Out-Null
        $issue = Get-CodexIssueContext -Repository $Repository -IssueNumber $IssueNumber -CommandRunner $GitHubCommandRunner
        $development = Get-CodexIssueDevelopment -Repository $Repository -IssueNumber $IssueNumber -CommandRunner $GitHubCommandRunner
        $branchName = Get-CodexIssueBranchName -IssueNumber $IssueNumber -Title ([string](Get-CodexOrchestrationField $issue 'title' "Issue $IssueNumber"))
        Write-CodexWorkerMilestone -IssueNumber $IssueNumber -Phase 'STARTED' -Details ([string](Get-CodexOrchestrationField $issue 'title' "Issue $IssueNumber"))
        if ($null -ne $StateReader) { $state = & $StateReader $StatePath }
        else { $state = Read-CodexWorkerState -Path $StatePath }
        $attemptState = Get-CodexIssueAttemptState -State $state -IssueNumber $IssueNumber
        $provider = Resolve-CodexIssueRunProvider -AttemptState $attemptState -Config $Config -EventName $EventName
        $existingStatus = [string](Get-CodexOrchestrationField $attemptState 'status' '')
        $resumeEvent = $EventName -match '(?i)(retry|revise)'

        $publicationStage = [string](Get-CodexOrchestrationField $attemptState 'publicationStage' 'none')
        $publicationCommand = Get-Command Publish-CodexIssue -ErrorAction SilentlyContinue
        if ($null -ne $attemptState -and $existingStatus -eq 'pr-ready' -and $publicationStage -ne 'pr-created' -and ($null -ne $PublicationProvider -or $null -ne $publicationCommand)) {
            if ($null -ne $PublicationProvider) { $publicationResult = & $PublicationProvider $attemptState $issue $Config $StatePath }
            else { $publicationResult = & $publicationCommand -AttemptState $attemptState -IssueContext $issue -Config $Config -StatePath $StatePath -Repository $Repository -DataRoot $paths.DataRoot }
            $newStage = [string](Get-CodexOrchestrationField $publicationResult 'publicationStage' $publicationStage)
            Set-CodexOrchestrationField $attemptState 'publicationStage' $newStage
            $newPrUrl = [string](Get-CodexOrchestrationField $publicationResult 'prUrl' '')
            if (-not [string]::IsNullOrWhiteSpace($newPrUrl)) { Set-CodexOrchestrationField $attemptState 'prUrl' $newPrUrl }
            if ($null -ne $StateWriter) { & $StateWriter $StatePath $IssueNumber $attemptState }
            else { Write-CodexIssueAttemptState -Path $StatePath -IssueNumber $IssueNumber -AttemptState $attemptState | Out-Null }
            $recoveryLabels = @(Get-CodexOrchestrationField $issue 'labels' @())
            Set-CodexIssueStatus -Repository $Repository -IssueNumber $IssueNumber -Status 'pr-ready' -Provider $provider -CurrentLabels $recoveryLabels -CommandRunner $GitHubCommandRunner | Out-Null
            Write-CodexWorkerMilestone -IssueNumber $IssueNumber -Phase (Get-CodexWorkerMilestonePhase -Provider $provider -CodexPhase 'PR READY' -ProviderPhase 'READY') -Details ("Publication recovered: {0}" -f $attemptState.prUrl)
            return [pscustomobject][ordered]@{ IssueNumber = $IssueNumber; Status = $attemptState.status; PublicationStage = $attemptState.publicationStage; PrUrl = $attemptState.prUrl; RecoveredPublication = $true }
        }
        if ($existingStatus -in @('running', 'pr-ready') -and -not ($resumeEvent -and $existingStatus -eq 'pr-ready')) {
            Write-CodexWorkerMilestone -IssueNumber $IssueNumber -Phase 'SKIPPED' -Details ("Existing worker state is {0}." -f $existingStatus)
            return [pscustomobject][ordered]@{ IssueNumber = $IssueNumber; Status = $existingStatus; NoOp = $true; State = $attemptState }
        }
        if ($existingStatus -eq 'blocked' -and -not $resumeEvent) {
            Write-CodexWorkerMilestone -IssueNumber $IssueNumber -Phase 'SKIPPED' -Details ("Existing worker state is blocked; add {0} to resume." -f (Get-CodexWorkerStatusLabel -Provider $provider -Status 'retry'))
            return [pscustomobject][ordered]@{ IssueNumber = $IssueNumber; Status = $existingStatus; NoOp = $true; State = $attemptState }
        }

        if ($null -eq $attemptState) {
            $attemptState = New-CodexIssueAttemptState -IssueNumber $IssueNumber -Attempt 1 -AgentProvider $provider.Name
        } else {
            foreach ($name in @('issueNumber', 'status', 'attempt', 'branch', 'worktree', 'threadId', 'runDirectory', 'commit', 'prUrl', 'retryCount', 'publicationStage', 'lastError', 'agentProvider')) {
                if ($null -eq $attemptState.PSObject.Properties[$name]) {
                    $defaultValue = $null
                    if ($name -eq 'issueNumber') { $defaultValue = $IssueNumber }
                    elseif ($name -eq 'retryCount') { $defaultValue = 0 }
                    elseif ($name -eq 'publicationStage') { $defaultValue = 'none' }
                    elseif ($name -eq 'agentProvider') { $defaultValue = 'Codex' }
                    Set-CodexOrchestrationField $attemptState $name $defaultValue
                }
            }
            if ($resumeEvent -and $existingStatus -in @('blocked', 'pr-ready')) {
                Set-CodexOrchestrationField $attemptState 'status' 'queued'
                Set-CodexOrchestrationField $attemptState 'lastError' $null
                Set-CodexOrchestrationField $attemptState 'attempt' ([int](Get-CodexOrchestrationField $attemptState 'attempt' 0) + 1)
                Set-CodexOrchestrationField $attemptState 'retryCount' 0
            }
        }

        $save = {
            param([object] $Current)
            Set-CodexIssueAttemptState -State $state -IssueNumber $IssueNumber -AttemptState $Current | Out-Null
            if ($null -ne $StateWriter) { & $StateWriter $StatePath $IssueNumber $Current }
            else { Write-CodexWorkerState -Path $StatePath -State $state }
        }.GetNewClosure()
        Set-CodexOrchestrationField $attemptState 'issueNumber' $IssueNumber
        Set-CodexOrchestrationField $attemptState 'branch' ([string](Get-CodexOrchestrationField $attemptState 'branch' $branchName))
        & $save $attemptState

        $labels = @(Get-CodexOrchestrationField $issue 'labels' @())
        if ($existingStatus -eq '') {
            Set-CodexOrchestrationField $attemptState 'status' 'queued'
            & $save $attemptState
            Set-CodexIssueStatus -Repository $Repository -IssueNumber $IssueNumber -Status 'queued' -Provider $provider -CurrentLabels $labels -CommandRunner $GitHubCommandRunner | Out-Null
            $labels = @($labels) + @(Get-CodexWorkerStatusLabel -Provider $provider -Status 'queued')
            & $save $attemptState
        }
        Set-CodexOrchestrationField $attemptState 'status' 'running'
        Set-CodexOrchestrationField $attemptState 'lastError' $null
        & $save $attemptState
        Set-CodexIssueStatus -Repository $Repository -IssueNumber $IssueNumber -Status 'running' -Provider $provider -CurrentLabels $labels -CommandRunner $GitHubCommandRunner | Out-Null
        Write-CodexWorkerMilestone -IssueNumber $IssueNumber -Phase 'RUNNING' -Details ("Attempt {0}; branch {1}" -f $attemptState.attempt, $attemptState.branch)
        $labels = @($labels) + @(Get-CodexWorkerStatusLabel -Provider $provider -Status 'running')
        & $save $attemptState

        $worktreeResult = $null
        $savedWorktree = [string](Get-CodexOrchestrationField $attemptState 'worktree' '')
        $reuseSavedWorktree = $false
        if (-not [string]::IsNullOrWhiteSpace($savedWorktree)) {
            try {
                $savedWorktree = [IO.Path]::GetFullPath($savedWorktree)
                Assert-PathUnderRoot -Path $savedWorktree -Root $paths.WorktreeRoot | Out-Null
                if (Test-Path -LiteralPath $savedWorktree -PathType Container) {
                    $registeredWorktrees = @(Get-RegisteredWorktrees -RepositoryRoot $RepositoryRoot -CommandRunner $GitCommandRunner)
                    $matchingWorktrees = @($registeredWorktrees | Where-Object {
                            [IO.Path]::GetFullPath([string]$_.Path) -eq $savedWorktree -and
                            [string]$_.Branch -eq [string]$attemptState.branch
                        })
                    $reuseSavedWorktree = $matchingWorktrees.Count -eq 1
                }
            } catch {
                $reuseSavedWorktree = $false
            }
        }
        if ($reuseSavedWorktree) {
            $worktreeResult = [pscustomobject]@{ Path = $savedWorktree; BranchName = $attemptState.branch; Reused = $true; Created = $false }
        } elseif ($null -ne $WorktreeProvider) {
            $worktreeResult = & $WorktreeProvider $RepositoryRoot $paths.WorktreeRoot $IssueNumber ([string]$issue.title) ([string]$attemptState.branch) ([string](Get-CodexOrchestrationField $Config 'defaultBranch' 'master')) $GitCommandRunner
        } else {
            $worktreeResult = Get-OrCreateCodexIssueWorktree -RepositoryRoot $RepositoryRoot -WorktreeRoot $paths.WorktreeRoot -IssueNumber $IssueNumber -Title ([string]$issue.title) -BranchName ([string]$attemptState.branch) -DefaultBranch ([string](Get-CodexOrchestrationField $Config 'defaultBranch' 'master')) -CommandRunner $GitCommandRunner
        }
        Set-CodexOrchestrationField $attemptState 'branch' ([string](Get-CodexOrchestrationField $worktreeResult 'BranchName' $attemptState.branch))
        Set-CodexOrchestrationField $attemptState 'worktree' ([IO.Path]::GetFullPath([string](Get-CodexOrchestrationField $worktreeResult 'Path')))
        & $save $attemptState
        Write-CodexWorkerMilestone -IssueNumber $IssueNumber -Phase 'WORKTREE READY' -Details ("{0} ({1})" -f $attemptState.branch, $attemptState.worktree)
        if (-not (Test-CodexMilestoneAlreadyPresent -IssueContext $issue -Heading 'Codex work claimed.')) {
            Add-CodexIssueMilestone -Repository $Repository -IssueNumber $IssueNumber -Milestone 'claimed' -Details ("Branch: {0}`nWorktree: {1}" -f $attemptState.branch, $attemptState.worktree) -CommandRunner $GitHubCommandRunner | Out-Null
            & $save $attemptState
        }

        if ($null -ne $SetupProvider) { & $SetupProvider $attemptState.worktree $Config (Join-Path $paths.DataRoot "runs\issue-$IssueNumber\activity.log") | Out-Null }
        else { Initialize-CodexIssueWorktree -Worktree $attemptState.worktree -Config $Config -ActivityLogPath (Join-Path $paths.DataRoot "runs\issue-$IssueNumber\activity.log") | Out-Null }

        $retryLoop = $true
        while ($retryLoop) {
            $retryLoop = $false
            $runDirectory = Join-Path $paths.RunRoot (Join-Path "issue-$IssueNumber" ([string](Get-CodexOrchestrationField $attemptState 'attempt' 1)))
            Set-CodexOrchestrationField $attemptState 'runDirectory' $runDirectory
            & $save $attemptState
            Write-CodexWorkerMilestone -IssueNumber $IssueNumber -Phase (Get-CodexWorkerMilestonePhase -Provider $provider -CodexPhase 'AGENT STARTED' -ProviderPhase 'AGENT STARTED') -Details ("Attempt {0}" -f $attemptState.attempt)
            if ($null -ne $CodexProvider) {
                $codexResult = & $CodexProvider $attemptState.worktree $issue $Config $runDirectory $StatePath
            } else {
                $codexResult = Invoke-CodexWorkerAgentRun -Provider $provider -RunParameters @{
                    IssueWorktree = $attemptState.worktree
                    IssueContext = $issue
                    Config = $Config
                    RunDirectory = $runDirectory
                    StatePath = $StatePath
                }
            }
            $classification = [string](Get-CodexOrchestrationField $codexResult 'Classification' '')
            $summary = Get-CodexOrchestrationField $codexResult 'Summary' $null
            $threadId = [string](Get-CodexOrchestrationField $codexResult 'ThreadId' '')
            if (-not [string]::IsNullOrWhiteSpace($threadId)) { Set-CodexOrchestrationField $attemptState 'threadId' $threadId }
            $commit = [string](Get-CodexOrchestrationField $codexResult 'Commit' '')
            if (-not [string]::IsNullOrWhiteSpace($commit)) { Set-CodexOrchestrationField $attemptState 'commit' $commit }
            $prUrl = [string](Get-CodexOrchestrationField $codexResult 'PrUrl' '')
            if (-not [string]::IsNullOrWhiteSpace($prUrl)) { Set-CodexOrchestrationField $attemptState 'prUrl' $prUrl }
            & $save $attemptState

            if ($classification -eq 'transient_service_unavailable') {
                $retryCount = [int](Get-CodexOrchestrationField $attemptState 'retryCount' 0)
                $errorText = [string](Get-CodexOrchestrationField $codexResult 'LastError' 'Transient service unavailable.')
                Set-CodexOrchestrationField $attemptState 'lastError' $errorText
                if ($retryCount -lt 1) {
                    Set-CodexOrchestrationField $attemptState 'retryCount' ($retryCount + 1)
                    Set-CodexOrchestrationField $attemptState 'status' 'retry'
                    & $save $attemptState
                    Set-CodexIssueStatus -Repository $Repository -IssueNumber $IssueNumber -Status 'retry' -Provider $provider -CurrentLabels $labels -CommandRunner $GitHubCommandRunner | Out-Null
                    $labels = @($labels) + @(Get-CodexWorkerStatusLabel -Provider $provider -Status 'retry')
                    & $save $attemptState
                    Set-CodexOrchestrationField $attemptState 'attempt' ([int](Get-CodexOrchestrationField $attemptState 'attempt' 1) + 1)
                    Set-CodexOrchestrationField $attemptState 'status' 'running'
                    & $save $attemptState
                    Set-CodexIssueStatus -Repository $Repository -IssueNumber $IssueNumber -Status 'running' -Provider $provider -CurrentLabels $labels -CommandRunner $GitHubCommandRunner | Out-Null
                    $labels = @($labels) + @(Get-CodexWorkerStatusLabel -Provider $provider -Status 'running')
                    $retryLoop = $true
                    continue
                }
                Set-CodexOrchestrationField $attemptState 'status' 'blocked'
                & $save $attemptState
                Write-CodexWorkerMilestone -IssueNumber $IssueNumber -Phase 'BLOCKED' -Details $errorText
                Set-CodexIssueStatus -Repository $Repository -IssueNumber $IssueNumber -Status 'blocked' -Provider $provider -CurrentLabels $labels -CommandRunner $GitHubCommandRunner | Out-Null
                $labels = @($labels) + @(Get-CodexWorkerStatusLabel -Provider $provider -Status 'blocked')
                & $save $attemptState
                Add-CodexIssueMilestone -Repository $Repository -IssueNumber $IssueNumber -Milestone 'blocked' -Details $errorText -CommandRunner $GitHubCommandRunner | Out-Null
                & $save $attemptState
                return [pscustomobject][ordered]@{ IssueNumber = $IssueNumber; Status = 'blocked'; State = $attemptState; Classification = $classification }
            }

            $codexStatus = [string](Get-CodexOrchestrationField $codexResult 'Status' '')
            $summaryRequiresInput = [bool](Get-CodexOrchestrationField $summary 'requiresHumanInput' $false)
            $summaryStatusValue = [string](Get-CodexOrchestrationField $summary 'status' '')
            $statusBlockedForHumanInput = $codexStatus -eq 'blocked' -and ($summaryRequiresInput -or $summaryStatusValue -eq 'blocked')
            if ($classification -ne 'completed' -or ($codexStatus -ne 'completed' -and -not $statusBlockedForHumanInput)) {
                $nonSuccessError = [string](Get-CodexOrchestrationField $codexResult 'LastError' '')
                if ([string]::IsNullOrWhiteSpace($nonSuccessError)) { $nonSuccessError = "Codex run did not complete successfully (classification=$classification; status=$codexStatus)." }
                Set-CodexOrchestrationField $attemptState 'lastError' $nonSuccessError
                Set-CodexOrchestrationField $attemptState 'status' 'blocked'
                & $save $attemptState
                Write-CodexWorkerMilestone -IssueNumber $IssueNumber -Phase 'BLOCKED' -Details $nonSuccessError
                Set-CodexIssueStatus -Repository $Repository -IssueNumber $IssueNumber -Status 'blocked' -Provider $provider -CurrentLabels $labels -CommandRunner $GitHubCommandRunner | Out-Null
                $labels = @($labels) + @(Get-CodexWorkerStatusLabel -Provider $provider -Status 'blocked')
                & $save $attemptState
                Add-CodexIssueMilestone -Repository $Repository -IssueNumber $IssueNumber -Milestone 'blocked' -Details $nonSuccessError -CommandRunner $GitHubCommandRunner | Out-Null
                & $save $attemptState
                return [pscustomobject][ordered]@{ IssueNumber = $IssueNumber; Status = 'blocked'; State = $attemptState; Classification = $classification }
            }

            $errorText = [string](Get-CodexOrchestrationField $codexResult 'LastError' '')
            if ([string]::IsNullOrWhiteSpace($errorText) -and $classification -notin @('', 'completed')) { $errorText = "Codex run classified as $classification." }
            if ($null -eq $summary -or -not (Test-CodexSummary -Summary $summary)) {
                if ([string]::IsNullOrWhiteSpace($errorText)) { $errorText = 'Codex did not return a valid final summary.' }
                Set-CodexOrchestrationField $attemptState 'lastError' $errorText
                Set-CodexOrchestrationField $attemptState 'status' 'blocked'
                & $save $attemptState
                Write-CodexWorkerMilestone -IssueNumber $IssueNumber -Phase 'BLOCKED' -Details $errorText
                Set-CodexIssueStatus -Repository $Repository -IssueNumber $IssueNumber -Status 'blocked' -Provider $provider -CurrentLabels $labels -CommandRunner $GitHubCommandRunner | Out-Null
                $labels = @($labels) + @(Get-CodexWorkerStatusLabel -Provider $provider -Status 'blocked')
                & $save $attemptState
                Add-CodexIssueMilestone -Repository $Repository -IssueNumber $IssueNumber -Milestone 'blocked' -Details $errorText -CommandRunner $GitHubCommandRunner | Out-Null
                & $save $attemptState
                return [pscustomobject][ordered]@{ IssueNumber = $IssueNumber; Status = 'blocked'; State = $attemptState; Classification = $classification }
            }

            $requiresInput = [bool](Get-CodexOrchestrationField $summary 'requiresHumanInput' $false)
            $humanQuestion = [string](Get-CodexOrchestrationField $summary 'humanQuestion' '')
            $summaryStatus = [string](Get-CodexOrchestrationField $summary 'status' '')
            if ($requiresInput -or $summaryStatus -in @('blocked', 'failed')) {
                if ([string]::IsNullOrWhiteSpace($humanQuestion)) { $humanQuestion = [string](Get-CodexOrchestrationField $summary 'rootCauseOrApproach' 'Codex requires human input.') }
                Set-CodexOrchestrationField $attemptState 'lastError' $humanQuestion
                Set-CodexOrchestrationField $attemptState 'status' 'blocked'
                & $save $attemptState
                Write-CodexWorkerMilestone -IssueNumber $IssueNumber -Phase 'BLOCKED' -Details $humanQuestion
                Set-CodexIssueStatus -Repository $Repository -IssueNumber $IssueNumber -Status 'blocked' -Provider $provider -CurrentLabels $labels -CommandRunner $GitHubCommandRunner | Out-Null
                $labels = @($labels) + @(Get-CodexWorkerStatusLabel -Provider $provider -Status 'blocked')
                & $save $attemptState
                Add-CodexIssueMilestone -Repository $Repository -IssueNumber $IssueNumber -Milestone 'blocked' -Details $humanQuestion -CommandRunner $GitHubCommandRunner | Out-Null
                & $save $attemptState
                return [pscustomobject][ordered]@{ IssueNumber = $IssueNumber; Status = 'blocked'; State = $attemptState; Summary = $summary }
            }

            $approach = [string](Get-CodexOrchestrationField $summary 'rootCauseOrApproach' '')
            if (-not [string]::IsNullOrWhiteSpace($approach) -and -not (Test-CodexMilestoneAlreadyPresent -IssueContext $issue -Heading 'Codex approach established.')) {
                Add-CodexIssueMilestone -Repository $Repository -IssueNumber $IssueNumber -Milestone 'approach' -Details $approach -CommandRunner $GitHubCommandRunner | Out-Null
                & $save $attemptState
            }
            $validation = @(Get-CodexOrchestrationField $summary 'validation' @())
            $validationDetails = @($validation | ForEach-Object {
                    $command = [string](Get-CodexOrchestrationField $_ 'command' '')
                    $outcome = [string](Get-CodexOrchestrationField $_ 'outcome' '')
                    $details = [string](Get-CodexOrchestrationField $_ 'details' '')
                    if ([string]::IsNullOrWhiteSpace($details)) { "- ${command}: $outcome" } else { "- ${command}: $outcome - $details" }
                }) -join "`n"
            if (-not (Test-CodexMilestoneAlreadyPresent -IssueContext $issue -Heading 'Codex validation result.')) {
                Add-CodexIssueMilestone -Repository $Repository -IssueNumber $IssueNumber -Milestone 'validation' -Details $validationDetails -CommandRunner $GitHubCommandRunner | Out-Null
                & $save $attemptState
            }
            Set-CodexOrchestrationField $attemptState 'status' 'pr-ready'
            Set-CodexOrchestrationField $attemptState 'publicationStage' 'ready'
            Set-CodexOrchestrationField $attemptState 'lastError' $null
            & $save $attemptState
            Write-CodexWorkerMilestone -IssueNumber $IssueNumber -Phase (Get-CodexWorkerMilestonePhase -Provider $provider -CodexPhase 'VALIDATED' -ProviderPhase 'READY') -Details 'Implementation is ready for wrapper publication.'
            Set-CodexIssueStatus -Repository $Repository -IssueNumber $IssueNumber -Status 'pr-ready' -Provider $provider -CurrentLabels $labels -CommandRunner $GitHubCommandRunner | Out-Null
            $labels = @($labels) + @(Get-CodexWorkerStatusLabel -Provider $provider -Status 'pr-ready')
            & $save $attemptState
            Add-CodexIssueMilestone -Repository $Repository -IssueNumber $IssueNumber -Milestone 'pr-ready' -Details ("Branch: {0}`nWorktree: {1}" -f $attemptState.branch, $attemptState.worktree) -CommandRunner $GitHubCommandRunner | Out-Null
            & $save $attemptState
            return [pscustomobject][ordered]@{ IssueNumber = $IssueNumber; Status = 'pr-ready'; State = $attemptState; Summary = $summary }
        }
    } catch {
        $originalError = $_
        $originalException = $originalError.Exception
        $originalErrorText = [string]$originalException.Message
        if ([string]::IsNullOrWhiteSpace($originalErrorText)) {
            $errorId = [string]$originalError.FullyQualifiedErrorId
            if ([string]::IsNullOrWhiteSpace($errorId)) { $errorId = 'unknown' }
            $originalErrorText = "PowerShell error '$errorId' ($($originalException.GetType().FullName))."
        }
        if ($null -ne $attemptState) {
            Set-CodexOrchestrationField $attemptState 'status' 'blocked'
            Set-CodexOrchestrationField $attemptState 'lastError' $originalErrorText
            try {
                Set-CodexIssueAttemptState -State $state -IssueNumber $IssueNumber -AttemptState $attemptState | Out-Null
                if ($null -ne $StateWriter) { & $StateWriter $StatePath $IssueNumber $attemptState }
                else { Write-CodexWorkerState -Path $StatePath -State $state }
            } catch {}
            $safeError = $originalErrorText
            try { $safeError = Redact-CodexString -Text $safeError -SecretValues (Get-CodexBlockedSecretValues) } catch {}
            try { Write-CodexWorkerMilestone -IssueNumber $IssueNumber -Phase 'FAILED' -Details $safeError } catch {}
            try {
                if ($null -ne $provider) {
                    Set-CodexIssueStatus -Repository $Repository -IssueNumber $IssueNumber -Status 'blocked' -Provider $provider -CurrentLabels $null -CommandRunner $GitHubCommandRunner | Out-Null
                }
                try {
                    if ($null -ne $StateWriter) { & $StateWriter $StatePath $IssueNumber $attemptState }
                    else { Write-CodexWorkerState -Path $StatePath -State $state }
                } catch {}
            } catch {}
            try {
                Add-CodexIssueMilestone -Repository $Repository -IssueNumber $IssueNumber -Milestone 'blocked' -Details $safeError -CommandRunner $GitHubCommandRunner | Out-Null
            } catch {}
            try {
                if ($null -ne $StateWriter) { & $StateWriter $StatePath $IssueNumber $attemptState }
                else { Write-CodexWorkerState -Path $StatePath -State $state }
            } catch {}
        }
        throw $originalException
    } finally {
        if ($null -ne $lockHandle) {
            if ($null -ne $UnlockProvider) { & $UnlockProvider $lockHandle }
            else { Exit-CodexWorkerLock -Handle $lockHandle }
        }
    }
}
