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
    if ($existingStatus -in @('pending', 'snoozed')) {
        $oldTargetText = [string](Get-CodexDeploymentValue -Object $existing -Name 'targetCommit' '')
        if ($oldTargetText -match '^[0-9a-fA-F]{40}$') {
            $oldTarget = $oldTargetText.ToLowerInvariant()
            if ($oldTarget -ne $masterCommit) {
                try {
                    Invoke-CodexDeploymentGit -RepositoryRoot $RepositoryRoot -Arguments @('merge-base', '--is-ancestor', $oldTarget, $masterCommit) -CommandRunner $GitCommandRunner | Out-Null
                    $target = $masterCommit
                } catch {
                    # If the incoming merge advances the existing target, it is
                    # safe to coalesce to that exact merge. Otherwise retain the
                    # already verified target; never move a pending deployment
                    # backwards because a close event arrived out of order.
                    try {
                        Invoke-CodexDeploymentGit -RepositoryRoot $RepositoryRoot -Arguments @('merge-base', '--is-ancestor', $oldTarget, $mergeCommit) -CommandRunner $GitCommandRunner | Out-Null
                        $target = $mergeCommit
                    } catch {
                        $target = $oldTarget
                        $sourcePr = [int](Get-CodexDeploymentValue -Object $existing -Name 'sourcePr' $PullRequestNumber)
                    }
                }
            } else {
                $target = $oldTarget
                $sourcePr = [int](Get-CodexDeploymentValue -Object $existing -Name 'sourcePr' $PullRequestNumber)
            }
        }
    }

    $snooze = Get-CodexDeploymentValue -Object $existing -Name 'snoozeUntil' -Default $null
    $deployment = [pscustomobject][ordered]@{
        targetCommit = $target
        sourcePr = $sourcePr
        requestedAt = $Now.ToUniversalTime().ToString('o')
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
    param([string] $Repository, [int] $PullRequestNumber, [scriptblock] $CommandRunner)
    $context = Invoke-GhJson -Arguments @('pr', 'view', [string]$PullRequestNumber, '--repo', $Repository, '--json', 'number,closingIssuesReferences') -CommandRunner $CommandRunner
    $numberProperty = $context.PSObject.Properties['number']
    if ($null -eq $numberProperty -or [int]$numberProperty.Value -ne $PullRequestNumber) { throw 'The resolved pull request number does not match the requested pull request.' }
    $referencesProperty = $context.PSObject.Properties['closingIssuesReferences']
    $references = if ($null -eq $referencesProperty -or $null -eq $referencesProperty.Value) { @() } else { @($referencesProperty.Value) }
    if ($references.Count -ne 1) { throw 'The pull request must contain exactly one linked issue in its trusted closing references.' }
    $reference = $references[0]
    $issueProperty = $reference.PSObject.Properties['number']
    $repositoryProperty = $reference.PSObject.Properties['repository']
    $nameProperty = if ($null -ne $repositoryProperty -and $null -ne $repositoryProperty.Value) { $repositoryProperty.Value.PSObject.Properties['nameWithOwner'] } else { $null }
    if ($null -eq $issueProperty -or [int]$issueProperty.Value -le 0 -or $null -eq $nameProperty -or -not [string]::Equals([string]$nameProperty.Value, $Repository, [StringComparison]::OrdinalIgnoreCase)) { throw 'The pull request closing reference is invalid or belongs to another repository.' }
    return [int]$issueProperty.Value
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
        $linkedIssue = Resolve-CodexClosedPullRequestIssueNumber -Repository $Repository -PullRequestNumber $PullRequestNumber -CommandRunner $GitHubCommandRunner
        if ($IssueNumber -gt 0 -and $IssueNumber -ne $linkedIssue) { throw "The supplied issue number $IssueNumber does not match the pull request closing reference $linkedIssue." }
        $IssueNumber = $linkedIssue
        $state = if ($null -ne $StateReader) { & $StateReader $paths.StatePath } else { Read-CodexWorkerState -Path $paths.StatePath }
        $attempt = Get-CodexIssueAttemptState -State $state -IssueNumber $IssueNumber
        if ($null -eq $attempt) { throw "No saved Codex state exists for issue #$IssueNumber." }
        $branch = [string](Get-CodexDeploymentValue -Object $attempt -Name 'branch' '')
        $worktree = [string](Get-CodexDeploymentValue -Object $attempt -Name 'worktree' '')
        $blockers = [System.Collections.Generic.List[string]]::new()
        if ([string]::IsNullOrWhiteSpace($branch) -or $branch -ne $HeadBranch) { $blockers.Add('Pull request head branch does not match the saved Codex issue branch.') | Out-Null }
        if ([string]::IsNullOrWhiteSpace($worktree)) { $blockers.Add('Saved Codex issue state does not contain a worktree.') | Out-Null }

        $cleanedUp = $false
        if ($blockers.Count -eq 0) {
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
