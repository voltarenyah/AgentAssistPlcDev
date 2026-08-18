Set-StrictMode -Version Latest

function Get-CodexDeploymentValue {
    param([object] $Object, [string] $Name, [object] $Default = $null)
    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return $Default }
    return $property.Value
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
        [DateTime] $Now = ([DateTime]::UtcNow)
    )

    if ($PullRequestNumber -le 0) { throw 'A positive pull request number is required for deployment registration.' }
    $mergeCommit = ConvertTo-CodexFullCommit -Commit $MergeCommitSha -Name 'merge commit'
    $masterCommit = Get-CodexVerifiedMasterCommit -RepositoryRoot $RepositoryRoot -GitCommandRunner $GitCommandRunner
    Assert-CodexCommitReachableFromMaster -RepositoryRoot $RepositoryRoot -Commit $mergeCommit -MasterCommit $masterCommit -GitCommandRunner $GitCommandRunner | Out-Null

    $existing = Get-CodexDeploymentValue -Object $State -Name 'deployment' -Default $null
    $existingStatus = [string](Get-CodexDeploymentValue -Object $existing -Name 'status' '')
    $target = $mergeCommit
    $sourcePr = $PullRequestNumber
    $requestedAt = $Now.ToUniversalTime().ToString('o')
    $replaced = $false
    if ($existingStatus -in @('pending', 'snoozed')) {
        $oldTargetText = [string](Get-CodexDeploymentValue -Object $existing -Name 'targetCommit' '')
        if ($oldTargetText -notmatch '^[0-9a-fA-F]{40}$') { throw 'Existing pending deployment has an invalid target commit.' }
        $oldSourcePr = [int](Get-CodexDeploymentValue -Object $existing -Name 'sourcePr' 0)
        $oldRequestedAt = [string](Get-CodexDeploymentValue -Object $existing -Name 'requestedAt' '')
        if ($oldSourcePr -le 0 -or [string]::IsNullOrWhiteSpace($oldRequestedAt)) { throw 'Existing pending deployment tuple is incomplete.' }
        if ($oldTargetText -match '^[0-9a-fA-F]{40}$') {
            $oldTarget = $oldTargetText.ToLowerInvariant()
            Assert-CodexCommitReachableFromMaster -RepositoryRoot $RepositoryRoot -Commit $oldTarget -MasterCommit $masterCommit -GitCommandRunner $GitCommandRunner | Out-Null
            if ($oldTarget -ne $masterCommit) {
                Invoke-CodexDeploymentGit -RepositoryRoot $RepositoryRoot -Arguments @('merge-base', '--is-ancestor', $oldTarget, $masterCommit) -CommandRunner $GitCommandRunner | Out-Null
                $target = $masterCommit
                $replaced = $true
            } else {
                $target = $oldTarget
            }
            if (-not $replaced) {
                $sourcePr = $oldSourcePr
                $requestedAt = $oldRequestedAt
            }
        }
    }

    $snooze = Get-CodexDeploymentValue -Object $existing -Name 'snoozeUntil' -Default $null
    $deployment = [pscustomobject][ordered]@{
        targetCommit = $target
        sourcePr = $sourcePr
        requestedAt = $requestedAt
        snoozeUntil = $snooze
        status = 'pending'
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
    $savedUrl = [string](Get-CodexDeploymentValue -Object $Attempt -Name 'prUrl' '')
    if ([string]::IsNullOrWhiteSpace($savedUrl) -or $null -eq $url -or -not [string]::Equals($savedUrl, [string]$url.Value, [StringComparison]::OrdinalIgnoreCase)) { throw 'Pull request URL does not match the saved Codex attempt.' }
    if ([string]$url.Value -notmatch ('/pull/' + [regex]::Escape([string]$PullRequestNumber) + '$')) { throw 'Pull request URL does not identify the close event pull request.' }
    if ($null -eq $state -or [string]$state.Value -ne 'CLOSED') { throw 'Pull request is not closed.' }
    if ($null -eq $base -or [string]$base.Value -ne 'master') { throw 'Pull request base branch is not master.' }
    if ($null -eq $head -or [string]$head.Value -ne $HeadBranch) { throw 'Pull request head branch does not match the close event.' }
    if ([string](Get-CodexDeploymentValue -Object $Attempt -Name 'branch' '') -ne $HeadBranch) { throw 'Pull request head branch does not match the saved Codex branch.' }
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
        $branch = [string](Get-CodexDeploymentValue -Object $attempt -Name 'branch' '')
        $worktree = [string](Get-CodexDeploymentValue -Object $attempt -Name 'worktree' '')
        $blockers = [System.Collections.Generic.List[string]]::new()
        if ([string]::IsNullOrWhiteSpace($branch) -or $branch -ne $HeadBranch) { $blockers.Add('Pull request head branch does not match the saved Codex issue branch.') | Out-Null }
        $cleanupStatus = [string](Get-CodexDeploymentValue -Object $attempt -Name 'cleanupStatus' '')
        if ([string]::IsNullOrWhiteSpace($worktree) -and $cleanupStatus -ne 'completed') { $blockers.Add('Saved Codex issue state does not contain a worktree.') | Out-Null }

        $cleanedUp = $false
        if ([string]::IsNullOrWhiteSpace($worktree) -and $cleanupStatus -eq 'completed') {
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
        if ($cleanedUp -and $blockers.Count -eq 0) {
            Set-CodexOrchestrationField $attempt 'worktree' $null
            Set-CodexOrchestrationField $attempt 'cleanupStatus' 'completed'
            Set-CodexOrchestrationField $attempt 'cleanupAt' $Now.ToUniversalTime().ToString('o')
            Set-CodexOrchestrationField $attempt 'cleanupBlockers' @()
        } elseif ($blockers.Count -gt 0) {
            Set-CodexOrchestrationField $attempt 'cleanupStatus' 'blocked'
            Set-CodexOrchestrationField $attempt 'cleanupAt' $Now.ToUniversalTime().ToString('o')
            Set-CodexOrchestrationField $attempt 'cleanupBlockers' @($blockers.ToArray())
        }
        if ($cleanedUp -or $blockers.Count -gt 0) { Write-CodexDeploymentState -StatePath $paths.StatePath -State $state -StateWriter $StateWriter }
        Add-CodexCleanupBlockerComment -Repository $Repository -PullRequestNumber $PullRequestNumber -IssueNumber $IssueNumber -Blockers @($blockers.ToArray()) -GitHubCommandRunner $GitHubCommandRunner

        $deploymentCreated = $false
        if ($Merged) {
            if ([string]::IsNullOrWhiteSpace($MergeCommitSha)) { throw 'A merged pull request must provide its merge commit SHA.' }
            $deployment = Register-CodexPendingDeployment -RepositoryRoot $paths.RepositoryRoot -DataRoot $paths.DataRoot -MergeCommitSha $MergeCommitSha -PullRequestNumber $PullRequestNumber -State $state -GitCommandRunner $GitCommandRunner -Now $Now
            Write-CodexDeploymentState -StatePath $paths.StatePath -State $state -StateWriter $StateWriter
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
