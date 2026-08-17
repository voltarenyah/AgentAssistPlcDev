Set-StrictMode -Version Latest

function Invoke-CodexPublicationGit {
    param(
        [Parameter(Mandatory = $true)][string] $Worktree,
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [scriptblock] $CommandRunner
    )
    $root = [IO.Path]::GetFullPath($Worktree)
    $full = [string[]] (@('-C', $root) + @($Arguments))
    if ($null -ne $CommandRunner) {
        $result = & $CommandRunner $full
        if ($null -eq $result) { return '' }
        return (($result | ForEach-Object { [string]$_ }) -join [Environment]::NewLine)
    }
    $result = & git.exe @full 2>&1
    if ($LASTEXITCODE -ne 0) { throw (($result | Out-String).Trim()) }
    return (($result | ForEach-Object { [string]$_ }) -join [Environment]::NewLine)
}

function Get-CodexPublicationValue {
    param([object] $Object, [string] $Name, [object] $Default = $null)
    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return $Default }
    return $property.Value
}

function Get-CodexPublicationChangedPaths {
    param([string] $DiffNames)
    return @($DiffNames -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Test-CodexPublicationSuspiciousPath {
    param([string] $Path)
    $normalized = $Path.Replace('\', '/')
    $leaf = [IO.Path]::GetFileName($normalized)
    if ($leaf -match '^(?i)(\.env(?:\..*)?|auth\.json|id_rsa(?:\..*)?|id_ed25519(?:\..*)?)$') { return $true }
    if ($leaf -match '(?i)\.(pem|key|pfx|p12|cer|crt)$') { return $true }
    return $false
}

function Test-CodexPublication {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object] $Summary,
        [Parameter(Mandatory = $true)][string] $Worktree,
        [Parameter(Mandatory = $true)][int] $IssueNumber,
        [string] $DataRoot,
        [scriptblock] $GitCommandRunner
    )
    $blockers = [System.Collections.Generic.List[string]]::new()
    $risks = [System.Collections.Generic.List[string]]::new()
    $changed = @()
    if ($null -eq $Summary -or [string](Get-CodexPublicationValue $Summary 'status' '') -ne 'completed') { $blockers.Add('Summary status must be completed.') | Out-Null }
    if ([bool](Get-CodexPublicationValue $Summary 'requiresHumanInput' $true)) { $blockers.Add('Summary requires human input.') | Out-Null }
    if ([string]::IsNullOrWhiteSpace($Worktree) -or -not (Test-Path -LiteralPath $Worktree -PathType Container)) { $blockers.Add('Issue worktree does not exist.') | Out-Null }
    else {
        try {
            $nameText = Invoke-CodexPublicationGit -Worktree $Worktree -Arguments @('diff', '--name-only') -CommandRunner $GitCommandRunner
            $changed = @(Get-CodexPublicationChangedPaths $nameText)
            if ($changed.Count -eq 0) { $blockers.Add('Worktree diff is empty.') | Out-Null }
            foreach ($path in $changed) {
                if (Test-CodexPublicationSuspiciousPath $path) { $blockers.Add("Changed path '$path' looks like a credential or secret file.") | Out-Null }
                if (-not [string]::IsNullOrWhiteSpace($DataRoot)) {
                    $root = [IO.Path]::GetFullPath($DataRoot).TrimEnd('\', '/')
                    $candidate = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetFullPath($Worktree)) $path))
                    if ($candidate.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or $candidate.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { $blockers.Add("Changed path '$path' is inside the durable data root.") | Out-Null }
                }
            }
            $check = Invoke-CodexPublicationGit -Worktree $Worktree -Arguments @('diff', '--check') -CommandRunner $GitCommandRunner
            if (-not [string]::IsNullOrWhiteSpace($check)) { $blockers.Add('git diff --check reported whitespace errors.') | Out-Null }
            $diff = Invoke-CodexPublicationGit -Worktree $Worktree -Arguments @('diff', '--no-ext-diff') -CommandRunner $GitCommandRunner
            if ($diff -match '(?m)^(<<<<<<<|=======|>>>>>>>)') { $blockers.Add('Diff contains conflict markers.') | Out-Null }
        } catch { $blockers.Add("Unable to validate publication diff: $($_.Exception.Message)") | Out-Null }
    }
    foreach ($entry in @((Get-CodexPublicationValue $Summary 'validation' @()))) {
        $outcome = [string](Get-CodexPublicationValue $entry 'outcome' '')
        $command = [string](Get-CodexPublicationValue $entry 'command' '')
        $details = [string](Get-CodexPublicationValue $entry 'details' '')
        if ($outcome -eq 'skipped') { $risks.Add("Skipped validation: $command") | Out-Null }
        elseif ($outcome -eq 'failed') {
            if (($command + ' ' + $details) -match '(?i)optional|non[- ]required|not required') { $risks.Add("Non-required validation failed: $command") | Out-Null }
            else { $blockers.Add("Required validation failed: $command") | Out-Null }
        }
    }
    foreach ($item in @((Get-CodexPublicationValue $Summary 'warnings' @()))) { if (-not [string]::IsNullOrWhiteSpace([string]$item)) { $risks.Add([string]$item) | Out-Null } }
    foreach ($item in @((Get-CodexPublicationValue $Summary 'remainingRisks' @()))) { if (-not [string]::IsNullOrWhiteSpace([string]$item)) { $risks.Add([string]$item) | Out-Null } }
    return [pscustomobject][ordered]@{ Allowed = ($blockers.Count -eq 0); Valid = ($blockers.Count -eq 0); Blockers = @($blockers.ToArray()); Risks = @($risks.ToArray()); ChangedPaths = @($changed); IssueNumber = $IssueNumber }
}

function ConvertTo-CodexPullRequestBody {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][object] $Summary, [object] $IssueContext, [Parameter(Mandatory = $true)][int] $IssueNumber, [string[]] $AdditionalRisks)
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('## Summary'); $lines.Add([string](Get-CodexPublicationValue $Summary 'rootCauseOrApproach' 'Codex implementation completed.'))
    $lines.Add(''); $lines.Add('## Problem'); $lines.Add([string](Get-CodexPublicationValue $IssueContext 'body' 'Issue context was supplied by GitHub.'))
    $lines.Add(''); $lines.Add('## Root Cause / Design'); $lines.Add([string](Get-CodexPublicationValue $Summary 'rootCauseOrApproach' 'Not provided.'))
    $lines.Add(''); $lines.Add('## Changes')
    $changes = @(Get-CodexPublicationValue $Summary 'changedComponents' @()); if ($changes.Count -eq 0) { $lines.Add('- No component list was supplied.') } else { foreach ($item in $changes) { $lines.Add('- ' + [string]$item) } }
    $lines.Add(''); $lines.Add('## Validation')
    $validation = @(Get-CodexPublicationValue $Summary 'validation' @()); if ($validation.Count -eq 0) { $lines.Add('- No validation entries were supplied.') } else { foreach ($entry in $validation) { $validationLine = '- {0}: {1} - {2}' -f (Get-CodexPublicationValue $entry 'command' ''), (Get-CodexPublicationValue $entry 'outcome' ''), (Get-CodexPublicationValue $entry 'details' ''); $lines.Add($validationLine) } }
    $lines.Add(''); $lines.Add('## Risks')
    $validationRisks = @($validation | Where-Object { (Get-CodexPublicationValue $_ 'outcome' '') -in @('failed', 'skipped') } | ForEach-Object { 'Validation {0}: {1}' -f (Get-CodexPublicationValue $_ 'outcome' ''), (Get-CodexPublicationValue $_ 'command' '') })
    $risks = @((Get-CodexPublicationValue $Summary 'warnings' @())) + @((Get-CodexPublicationValue $Summary 'remainingRisks' @())) + $validationRisks + @($AdditionalRisks)
    if ($risks.Count -eq 0) { $lines.Add('- None reported.') } else { foreach ($risk in $risks) { if (-not [string]::IsNullOrWhiteSpace([string]$risk)) { $lines.Add('- ' + [string]$risk) } } }
    $lines.Add(''); $lines.Add('## Issue'); $lines.Add("Fixes #$IssueNumber")
    return ($lines -join "`n")
}

function Get-CodexCommitTitle {
    param([string] $Suggested, [int] $IssueNumber)
    $title = ([string]$Suggested -replace '[\r\n]+', ' ').Trim()
    $title = $title -replace ('\s*\(#' + [regex]::Escape([string]$IssueNumber) + '\)\s*$', '')
    if ([string]::IsNullOrWhiteSpace($title)) { $title = 'chore: apply Codex changes' }
    return "$title (#$IssueNumber)"
}

function Write-CodexPublicationAttemptState {
    param([string] $StatePath, [int] $IssueNumber, [object] $AttemptState, [scriptblock] $StateWriter)
    if ($null -ne $StateWriter) { & $StateWriter $StatePath $IssueNumber $AttemptState | Out-Null }
    else { Write-CodexIssueAttemptState -Path $StatePath -IssueNumber $IssueNumber -AttemptState $AttemptState | Out-Null }
}

function Publish-CodexIssue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object] $AttemptState,
        [Parameter(Mandatory = $true)][object] $IssueContext,
        [object] $Config,
        [Parameter(Mandatory = $true)][string] $StatePath,
        [scriptblock] $StateWriter,
        [scriptblock] $GitCommandRunner,
        [scriptblock] $GitHubCommandRunner,
        [string] $DataRoot,
        [string] $Repository
    )
    $issueNumber = [int](Get-CodexPublicationValue $AttemptState 'issueNumber' (Get-CodexPublicationValue $IssueContext 'number' 0))
    $worktree = [string](Get-CodexPublicationValue $AttemptState 'worktree' '')
    $branch = [string](Get-CodexPublicationValue $AttemptState 'branch' '')
    $stage = [string](Get-CodexPublicationValue $AttemptState 'publicationStage' 'none')
    if ($null -eq $Config) { $Config = [pscustomobject]@{} }
    if ([string]::IsNullOrWhiteSpace($Repository)) { $Repository = [string](Get-CodexPublicationValue $Config 'repository' $env:GITHUB_REPOSITORY) }
    if ([string]::IsNullOrWhiteSpace($DataRoot)) { $DataRoot = [string](Get-CodexPublicationValue $Config 'dataRoot' '') }
    if ([string]::IsNullOrWhiteSpace($DataRoot)) { $DataRoot = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($StatePath)) }
    $runDirectory = [string](Get-CodexPublicationValue $AttemptState 'runDirectory' ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($StatePath))))
    $summaryPath = Join-Path $runDirectory 'final-summary.json'
    $summary = $null
    if (Test-Path -LiteralPath $summaryPath -PathType Leaf) { try { $summary = [IO.File]::ReadAllText($summaryPath) | ConvertFrom-Json } catch { throw 'Publication summary is malformed.' } }
    if ($null -eq $summary) { $summary = Get-CodexPublicationValue $AttemptState 'summary' $null }
    if ($null -eq $summary) { throw 'Publication summary is required.' }
    if ($stage -notin @('committed','pushed','pr-created')) {
        $existingHead = ''
        try {
            $pendingChanges = Invoke-CodexPublicationGit -Worktree $worktree -Arguments @('status', '--porcelain') -CommandRunner $GitCommandRunner
            if ([string]::IsNullOrWhiteSpace($pendingChanges)) { $existingHead = (Invoke-CodexPublicationGit -Worktree $worktree -Arguments @('rev-parse', 'HEAD') -CommandRunner $GitCommandRunner).Trim() }
        } catch { $existingHead = '' }
        if (-not [string]::IsNullOrWhiteSpace($existingHead)) {
            Set-CodexOrchestrationField $AttemptState 'commit' $existingHead
            Set-CodexOrchestrationField $AttemptState 'publicationStage' 'committed'
            Write-CodexPublicationAttemptState -StatePath $StatePath -IssueNumber $issueNumber -AttemptState $AttemptState -StateWriter $StateWriter
            $stage = 'committed'
        } else {
            $review = Test-CodexPublication -Summary $summary -Worktree $worktree -IssueNumber $issueNumber -DataRoot $DataRoot -GitCommandRunner $GitCommandRunner
            if (-not $review.Allowed) { throw ('Publication blocked: ' + ($review.Blockers -join ' ')) }
            $title = Get-CodexCommitTitle -Suggested (Get-CodexPublicationValue $summary 'commitMessage' '') -IssueNumber $issueNumber
            Invoke-CodexPublicationGit -Worktree $worktree -Arguments @('add', '-A') -CommandRunner $GitCommandRunner | Out-Null
            Invoke-CodexPublicationGit -Worktree $worktree -Arguments @('commit', '-m', $title) -CommandRunner $GitCommandRunner | Out-Null
            $sha = Invoke-CodexPublicationGit -Worktree $worktree -Arguments @('rev-parse', 'HEAD') -CommandRunner $GitCommandRunner
            Set-CodexOrchestrationField $AttemptState 'commit' $sha.Trim()
            Set-CodexOrchestrationField $AttemptState 'publicationStage' 'committed'
            Write-CodexPublicationAttemptState -StatePath $StatePath -IssueNumber $issueNumber -AttemptState $AttemptState -StateWriter $StateWriter
            $stage = 'committed'
        }
    }
    if ($stage -eq 'committed') {
        Invoke-CodexPublicationGit -Worktree $worktree -Arguments @('push', 'origin', $branch) -CommandRunner $GitCommandRunner | Out-Null
        Set-CodexOrchestrationField $AttemptState 'publicationStage' 'pushed'
        Write-CodexPublicationAttemptState -StatePath $StatePath -IssueNumber $issueNumber -AttemptState $AttemptState -StateWriter $StateWriter
        $stage = 'pushed'
    }
    if ($stage -eq 'pushed') {
        $body = ConvertTo-CodexPullRequestBody -Summary $summary -IssueContext $IssueContext -IssueNumber $issueNumber
        $bodyPath = Join-Path $runDirectory 'pull-request.md'
        [IO.File]::WriteAllText($bodyPath, $body, (New-Object Text.UTF8Encoding($false)))
        $existing = Get-CodexPullRequestForBranch -Repository $Repository -BranchName $branch -CommandRunner $GitHubCommandRunner
        if ($null -ne $existing) {
            $prNumber = [int](Get-CodexPublicationValue $existing 'number' 0)
            $prUrl = [string](Get-CodexPublicationValue $existing 'url' '')
            Set-CodexPullRequestBody -Repository $Repository -PullRequestNumber $prNumber -BodyPath $bodyPath -CommandRunner $GitHubCommandRunner | Out-Null
        } else {
            $created = New-CodexDraftPullRequest -Repository $Repository -BaseBranch ([string](Get-CodexPublicationValue $Config 'defaultBranch' 'master')) -HeadBranch $branch -BodyPath $bodyPath -CommandRunner $GitHubCommandRunner
            $prUrl = [string]$created
        }
        Set-CodexOrchestrationField $AttemptState 'prUrl' $prUrl
        Set-CodexOrchestrationField $AttemptState 'publicationStage' 'pr-created'
        Write-CodexPublicationAttemptState -StatePath $StatePath -IssueNumber $issueNumber -AttemptState $AttemptState -StateWriter $StateWriter
    }
    return [pscustomobject][ordered]@{ publicationStage = $AttemptState.publicationStage; prUrl = $AttemptState.prUrl; commit = $AttemptState.commit; bodyPath = (Join-Path $runDirectory 'pull-request.md') }
}

function Invoke-CodexRevision {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Repository,
        [Parameter(Mandatory = $true)][int] $IssueNumber,
        [Parameter(Mandatory = $true)][string] $Actor,
        [string] $PullRequestNumber,
        [Parameter(Mandatory = $true)][string] $RepositoryRoot,
        [Parameter(Mandatory = $true)][string] $DataRoot,
        [object] $Config,
        [Parameter(Mandatory = $true)][string] $StatePath,
        [scriptblock] $StateReader,
        [scriptblock] $StateWriter,
        [scriptblock] $LockProvider,
        [scriptblock] $UnlockProvider,
        [scriptblock] $GitCommandRunner,
        [scriptblock] $GitHubCommandRunner,
        [scriptblock] $CodexProvider
    )
    Assert-TrustedGitHubActor -Repository $Repository -Actor $Actor -CommandRunner $GitHubCommandRunner | Out-Null
    $paths = Resolve-CodexWorkerPaths -RepositoryRoot $RepositoryRoot -DataRoot $DataRoot
    $lock = $null
    try {
        if ($null -ne $LockProvider) { $lock = & $LockProvider $paths.LockPath } else { $lock = Enter-CodexWorkerLock -Path $paths.LockPath }
        $issue = Get-CodexIssueContext -Repository $Repository -IssueNumber $IssueNumber -CommandRunner $GitHubCommandRunner
        $pr = $null
        if (-not [string]::IsNullOrWhiteSpace($PullRequestNumber)) { $pr = Get-CodexPullRequestContext -Repository $Repository -PullRequestNumber ([int]$PullRequestNumber) -CommandRunner $GitHubCommandRunner }
        if ($null -eq $pr) { $pr = Get-CodexPullRequestForBranch -Repository $Repository -BranchName ([string](Get-CodexPublicationValue $attempt 'branch' '')) -CommandRunner $GitHubCommandRunner }
        if ($null -eq $pr) { throw 'Revision requires an existing pull request; refusing to create another PR.' }
        $state = if ($null -ne $StateReader) { & $StateReader $StatePath } else { Read-CodexWorkerState -Path $StatePath }
        $attempt = Get-CodexIssueAttemptState -State $state -IssueNumber $IssueNumber
        if ($null -eq $attempt -or [string]::IsNullOrWhiteSpace([string](Get-CodexPublicationValue $attempt 'worktree' ''))) { throw 'Existing issue worktree state is required for revision.' }
        $comments = @((Get-CodexPublicationValue $pr 'comments' @())) + @((Get-CodexPublicationValue $pr 'reviews' @()))
        $reviewText = ($comments | ForEach-Object { [string](Get-CodexPublicationValue $_ 'body' (Get-CodexPublicationValue $_ 'comment' '')) } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join "`n`n"
        $attemptNumber = [int](Get-CodexPublicationValue $attempt 'attempt' 1) + 1
        Set-CodexOrchestrationField $attempt 'attempt' $attemptNumber
        Set-CodexOrchestrationField $attempt 'publicationStage' 'none'
        Set-CodexOrchestrationField $attempt 'status' 'running'
        $attemptRun = Join-Path $paths.RunRoot (Join-Path "issue-$IssueNumber" ([string]$attemptNumber))
        Set-CodexOrchestrationField $attempt 'runDirectory' $attemptRun
        if ($null -ne $StateWriter) { & $StateWriter $StatePath $IssueNumber $attempt | Out-Null } else { Write-CodexIssueAttemptState -Path $StatePath -IssueNumber $IssueNumber -AttemptState $attempt | Out-Null }
        if ($null -ne $CodexProvider) { $codex = & $CodexProvider $attempt.worktree $issue $Config $attemptRun $StatePath $reviewText $attempt.threadId } else { $codex = Invoke-CodexRun -IssueWorktree $attempt.worktree -IssueContext $issue -Config $Config -RunDirectory $attemptRun -StatePath $StatePath -Revision -ThreadId $attempt.threadId -ReviewComments $reviewText }
        $summary = Get-CodexPublicationValue $codex 'Summary' $null
        if ([string](Get-CodexPublicationValue $codex 'Classification' '') -ne 'completed' -or [string](Get-CodexPublicationValue $codex 'Status' '') -ne 'completed' -or $null -eq $summary) { throw 'Revision Codex run did not complete successfully.' }
        Set-CodexOrchestrationField $attempt 'status' 'pr-ready'
        Set-CodexOrchestrationField $attempt 'summary' $summary
        if ($null -ne $StateWriter) { & $StateWriter $StatePath $IssueNumber $attempt | Out-Null } else { Write-CodexIssueAttemptState -Path $StatePath -IssueNumber $IssueNumber -AttemptState $attempt | Out-Null }
        $published = Publish-CodexIssue -AttemptState $attempt -IssueContext $issue -Config $Config -StatePath $StatePath -StateWriter $StateWriter -GitCommandRunner $GitCommandRunner -GitHubCommandRunner $GitHubCommandRunner -DataRoot $DataRoot -Repository $Repository
        $evidence = "Codex revision validation completed for commit $($published.commit). Existing draft PR was updated; no new PR was created."
        if ($null -ne $PullRequestNumber) { Add-CodexPullRequestComment -Repository $Repository -PullRequestNumber ([int]$PullRequestNumber) -Body $evidence -CommandRunner $GitHubCommandRunner | Out-Null }
        return [pscustomobject][ordered]@{ IssueNumber = $IssueNumber; Status = 'pr-ready'; PublicationStage = $published.publicationStage; PrUrl = $published.prUrl; ExistingPullRequest = $true }
    } finally {
        if ($null -ne $lock) { if ($null -ne $UnlockProvider) { & $UnlockProvider $lock } else { Exit-CodexWorkerLock -Handle $lock } }
    }
}
