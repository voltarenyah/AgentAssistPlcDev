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
    if ($null -eq $Summary -or -not (Test-CodexSummary -Summary $Summary)) { $blockers.Add('Summary is malformed or incomplete.') | Out-Null }
    if ([string](Get-CodexPublicationValue $Summary 'status' '') -ne 'completed') { $blockers.Add('Summary status must be completed.') | Out-Null }
    if ([bool](Get-CodexPublicationValue $Summary 'requiresHumanInput' $true)) { $blockers.Add('Summary requires human input.') | Out-Null }
    if ([string]::IsNullOrWhiteSpace($Worktree) -or -not (Test-Path -LiteralPath $Worktree -PathType Container)) { $blockers.Add('Issue worktree does not exist.') | Out-Null }
    else {
        try {
            $nameOutputs = @(
                (Invoke-CodexPublicationGit -Worktree $Worktree -Arguments @('diff', '--name-only') -CommandRunner $GitCommandRunner)
                (Invoke-CodexPublicationGit -Worktree $Worktree -Arguments @('diff', '--cached', '--name-only') -CommandRunner $GitCommandRunner)
                (Invoke-CodexPublicationGit -Worktree $Worktree -Arguments @('ls-files', '--others', '--exclude-standard') -CommandRunner $GitCommandRunner)
                (Invoke-CodexPublicationGit -Worktree $Worktree -Arguments @('diff', 'HEAD^', 'HEAD', '--name-only') -CommandRunner $GitCommandRunner)
            )
            $changed = @($nameOutputs | ForEach-Object { Get-CodexPublicationChangedPaths ([string]$_) } | Sort-Object -Unique)
            if ($changed.Count -eq 0) { $blockers.Add('Worktree diff is empty.') | Out-Null }
            foreach ($path in $changed) {
                if (Test-CodexPublicationSuspiciousPath $path) { $blockers.Add("Changed path '$path' looks like a credential or secret file.") | Out-Null }
                $candidate = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetFullPath($Worktree)) $path))
                $worktreePrefix = [IO.Path]::GetFullPath($Worktree).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
                if (-not $candidate.StartsWith($worktreePrefix, [StringComparison]::OrdinalIgnoreCase)) { $blockers.Add("Changed path '$path' escapes the issue worktree.") | Out-Null; continue }
                if (-not [string]::IsNullOrWhiteSpace($DataRoot)) {
                    $root = [IO.Path]::GetFullPath($DataRoot).TrimEnd('\', '/')
                    if ($candidate.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or $candidate.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { $blockers.Add("Changed path '$path' is inside the durable data root.") | Out-Null }
                }
                if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                    $content = [IO.File]::ReadAllText($candidate)
                    if ($content -match '(?m)^(<<<<<<<|=======|>>>>>>>)') { $blockers.Add("Changed file '$path' contains conflict markers.") | Out-Null }
                    if ($content -match '(?m)[ \t]+(?:\r?$)') { $blockers.Add("Changed file '$path' contains trailing whitespace.") | Out-Null }
                }
            }
            foreach ($checkArguments in @(@('diff', '--check'), @('diff', '--cached', '--check'), @('diff', 'HEAD^', 'HEAD', '--check'))) {
                $check = Invoke-CodexPublicationGit -Worktree $Worktree -Arguments ([string[]]$checkArguments) -CommandRunner $GitCommandRunner
                if (-not [string]::IsNullOrWhiteSpace($check)) { $blockers.Add('git diff --check reported whitespace errors.') | Out-Null }
            }
            foreach ($diffArguments in @(@('diff', '--no-ext-diff', 'HEAD'), @('diff', '--cached', '--no-ext-diff'), @('diff', 'HEAD^', 'HEAD', '--no-ext-diff'))) {
                $diff = Invoke-CodexPublicationGit -Worktree $Worktree -Arguments ([string[]]$diffArguments) -CommandRunner $GitCommandRunner
                if ($diff -match '(?m)^\+?(<<<<<<<|=======|>>>>>>>)') { $blockers.Add('Diff contains conflict markers.') | Out-Null }
                if ($diff -match '(?m)^\+(?!\+\+\+)[^\r\n]*[ \t]+$') { $blockers.Add('Diff contains trailing whitespace.') | Out-Null }
            }
        } catch { $blockers.Add("Unable to validate publication diff: $($_.Exception.Message)") | Out-Null }
    }
    foreach ($entry in @((Get-CodexPublicationValue $Summary 'validation' @()))) {
        $outcome = [string](Get-CodexPublicationValue $entry 'outcome' '')
        $command = [string](Get-CodexPublicationValue $entry 'command' '')
        $details = [string](Get-CodexPublicationValue $entry 'details' '')
        $explicitlyOptional = ($entry.PSObject.Properties['required'] -ne $null -and (Get-CodexPublicationValue $entry 'required' $true) -eq $false)
        if ($outcome -eq 'skipped') { $risks.Add("Skipped validation: $command") | Out-Null }
        elseif ($outcome -eq 'failed') {
            if ($explicitlyOptional) { $risks.Add("Non-required validation failed: $command") | Out-Null }
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
    $lines.Add(''); $lines.Add('## Issue')
    if ([string](Get-CodexPublicationValue $Summary 'status' '') -eq 'completed') { $lines.Add("Fixes #$IssueNumber") } else { $lines.Add("Issue #$IssueNumber") }
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

function Get-CodexRevisionFileFingerprint {
    param([string] $Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return 'missing' }
    $bytes = [IO.File]::ReadAllBytes($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-CodexRevisionUserEditSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Worktree,
        [scriptblock] $GitCommandRunner
    )
    $root = [IO.Path]::GetFullPath($Worktree)
    $outputs = @(
        (Invoke-CodexPublicationGit -Worktree $root -Arguments @('diff', '--name-only') -CommandRunner $GitCommandRunner)
        (Invoke-CodexPublicationGit -Worktree $root -Arguments @('diff', '--cached', '--name-only') -CommandRunner $GitCommandRunner)
        (Invoke-CodexPublicationGit -Worktree $root -Arguments @('ls-files', '--others', '--exclude-standard') -CommandRunner $GitCommandRunner)
    )
    $paths = @($outputs | ForEach-Object { Get-CodexPublicationChangedPaths ([string]$_) } | Sort-Object -Unique)
    $snapshot = [System.Collections.Generic.List[object]]::new()
    foreach ($path in $paths) {
        Assert-PathUnderRoot -Path ([IO.Path]::GetFullPath((Join-Path $root $path))) -Root $root | Out-Null
        $fullPath = [IO.Path]::GetFullPath((Join-Path $root $path))
        $snapshot.Add([pscustomobject][ordered]@{ Path = $path; Exists = (Test-Path -LiteralPath $fullPath -PathType Leaf); Fingerprint = (Get-CodexRevisionFileFingerprint -Path $fullPath) }) | Out-Null
    }
    return @($snapshot.ToArray())
}

function Test-CodexRevisionUserEditsPreserved {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Worktree,
        [object[]] $Snapshot
    )
    $root = [IO.Path]::GetFullPath($Worktree)
    foreach ($entry in @($Snapshot)) {
        $path = [string](Get-CodexPublicationValue $entry 'Path' '')
        if ([string]::IsNullOrWhiteSpace($path)) { return $false }
        $fullPath = [IO.Path]::GetFullPath((Join-Path $root $path))
        try { Assert-PathUnderRoot -Path $fullPath -Root $root | Out-Null } catch { return $false }
        $exists = Test-Path -LiteralPath $fullPath -PathType Leaf
        $fingerprint = Get-CodexRevisionFileFingerprint -Path $fullPath
        if ([bool](Get-CodexPublicationValue $entry 'Exists' $false) -ne $exists -or [string](Get-CodexPublicationValue $entry 'Fingerprint' '') -ne $fingerprint) { return $false }
    }
    return $true
}

function Format-CodexRevisionUserEditContext {
    param([object[]] $Snapshot)
    if (@($Snapshot).Count -eq 0) { return 'No pre-existing user edits were detected in the issue worktree.' }
    return (@($Snapshot | ForEach-Object { '[user-edit] {0} fingerprint={1}' -f $_.Path, $_.Fingerprint }) -join "`n")
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
        [string] $Repository,
        [switch] $RequireExistingPullRequest
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
    if ($stage -in @('committed','pushed','pr-created')) {
        $persistedCommit = [string](Get-CodexPublicationValue $AttemptState 'commit' '')
        if ([string]::IsNullOrWhiteSpace($persistedCommit)) { throw 'Publication recovery requires a persisted commit SHA.' }
        $head = (Invoke-CodexPublicationGit -Worktree $worktree -Arguments @('rev-parse', 'HEAD') -CommandRunner $GitCommandRunner).Trim()
        if ([string]::IsNullOrWhiteSpace($head) -or $head -ne $persistedCommit) { throw 'Persisted publication commit does not match worktree HEAD.' }
    }
    $review = Test-CodexPublication -Summary $summary -Worktree $worktree -IssueNumber $issueNumber -DataRoot $DataRoot -GitCommandRunner $GitCommandRunner
    if (-not $review.Allowed) { throw ('Publication blocked: ' + ($review.Blockers -join ' ')) }
    if ($stage -notin @('committed','pushed','pr-created')) {
            $title = Get-CodexCommitTitle -Suggested (Get-CodexPublicationValue $summary 'commitMessage' '') -IssueNumber $issueNumber
            Invoke-CodexPublicationGit -Worktree $worktree -Arguments @('add', '-A') -CommandRunner $GitCommandRunner | Out-Null
            Invoke-CodexPublicationGit -Worktree $worktree -Arguments @('commit', '-m', $title) -CommandRunner $GitCommandRunner | Out-Null
            $sha = Invoke-CodexPublicationGit -Worktree $worktree -Arguments @('rev-parse', 'HEAD') -CommandRunner $GitCommandRunner
            Set-CodexOrchestrationField $AttemptState 'commit' $sha.Trim()
            Set-CodexOrchestrationField $AttemptState 'publicationStage' 'committed'
            Write-CodexPublicationAttemptState -StatePath $StatePath -IssueNumber $issueNumber -AttemptState $AttemptState -StateWriter $StateWriter
            $stage = 'committed'
    }
    if ($stage -eq 'committed') {
        Invoke-CodexPublicationGit -Worktree $worktree -Arguments @('push', 'origin', $branch) -CommandRunner $GitCommandRunner | Out-Null
        Set-CodexOrchestrationField $AttemptState 'publicationStage' 'pushed'
        Write-CodexPublicationAttemptState -StatePath $StatePath -IssueNumber $issueNumber -AttemptState $AttemptState -StateWriter $StateWriter
        $stage = 'pushed'
    }
    if ($stage -eq 'pushed') {
        $body = ConvertTo-CodexPullRequestBody -Summary $summary -IssueContext $IssueContext -IssueNumber $issueNumber
        if (-not (Test-Path -LiteralPath $runDirectory -PathType Container)) { New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null }
        $bodyPath = Join-Path $runDirectory 'pull-request.md'
        [IO.File]::WriteAllText($bodyPath, $body, (New-Object Text.UTF8Encoding($false)))
        $existing = Get-CodexPullRequestForBranch -Repository $Repository -BranchName $branch -CommandRunner $GitHubCommandRunner
        if ($null -ne $existing) {
            $prNumber = [int](Get-CodexPublicationValue $existing 'number' 0)
            $prUrl = [string](Get-CodexPublicationValue $existing 'url' '')
            $prDraftProperty = $existing.PSObject.Properties['isDraft']
            if ($null -eq $prDraftProperty -or -not [bool]$prDraftProperty.Value) { throw 'Existing pull request is not a draft.' }
            if ([string](Get-CodexPublicationValue $existing 'state' '') -ne 'OPEN') { throw 'Existing pull request is not open.' }
            if ([string](Get-CodexPublicationValue $existing 'baseRefName' '') -ne 'master') { throw 'Existing pull request targets the wrong base branch.' }
            if ([string](Get-CodexPublicationValue $existing 'headRefName' '') -ne $branch) { throw 'Existing pull request targets the wrong head branch.' }
            if ([string](Get-CodexPublicationValue $existing 'body' '') -notmatch "(?i)#\s*$issueNumber\b") { throw 'Existing pull request does not identify the requested issue.' }
            Set-CodexPullRequestBody -Repository $Repository -PullRequestNumber $prNumber -BodyPath $bodyPath -CommandRunner $GitHubCommandRunner | Out-Null
        } else {
            if ($RequireExistingPullRequest) { throw 'Revision requires an existing pull request; refusing to create another PR.' }
            $created = New-CodexDraftPullRequest -Repository $Repository -BaseBranch 'master' -HeadBranch $branch -BodyPath $bodyPath -CommandRunner $GitHubCommandRunner
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
    $attempt = $null
    try {
        if ($null -ne $LockProvider) { $lock = & $LockProvider $paths.LockPath } else { $lock = Enter-CodexWorkerLock -Path $paths.LockPath }
        $state = if ($null -ne $StateReader) { & $StateReader $StatePath } else { Read-CodexWorkerState -Path $StatePath }
        $attempt = Get-CodexIssueAttemptState -State $state -IssueNumber $IssueNumber
        if ($null -eq $attempt) { throw 'Existing issue attempt state is required for revision.' }
        $branch = [string](Get-CodexPublicationValue $attempt 'branch' '')
        $worktree = [string](Get-CodexPublicationValue $attempt 'worktree' '')
        if ([string]::IsNullOrWhiteSpace($branch) -or [string]::IsNullOrWhiteSpace($worktree)) { throw 'Revision state must include the expected branch and worktree.' }
        Assert-PathUnderRoot -Path $worktree -Root $paths.WorktreeRoot | Out-Null
        $registered = @(Get-RegisteredWorktrees -RepositoryRoot $RepositoryRoot -CommandRunner $GitCommandRunner)
        $registeredMatch = @($registered | Where-Object { $_.Branch -eq $branch -and [IO.Path]::GetFullPath([string]$_.Path) -eq [IO.Path]::GetFullPath($worktree) })
        if ($registeredMatch.Count -ne 1) { throw 'Persisted revision worktree is not registered for the expected branch.' }
        $issue = Get-CodexIssueContext -Repository $Repository -IssueNumber $IssueNumber -CommandRunner $GitHubCommandRunner
        $pr = $null
        if (-not [string]::IsNullOrWhiteSpace($PullRequestNumber)) {
            $pr = Get-CodexPullRequestContext -Repository $Repository -PullRequestNumber ([int]$PullRequestNumber) -CommandRunner $GitHubCommandRunner
            if ([int](Get-CodexPublicationValue $pr 'number' 0) -ne [int]$PullRequestNumber) { throw 'The explicit pull request number does not match the resolved pull request.' }
        }
        if ($null -eq $pr) {
            $branchMatch = Get-CodexPullRequestForBranch -Repository $Repository -BranchName $branch -CommandRunner $GitHubCommandRunner
            if ($null -eq $branchMatch) { throw 'Revision requires an existing pull request; refusing to create another PR.' }
            $branchPrNumber = [int](Get-CodexPublicationValue $branchMatch 'number' 0)
            if ($branchPrNumber -le 0) { throw 'Branch pull request resolution did not return a valid number.' }
            $pr = Get-CodexPullRequestContext -Repository $Repository -PullRequestNumber $branchPrNumber -CommandRunner $GitHubCommandRunner
            if ([int](Get-CodexPublicationValue $pr 'number' 0) -ne $branchPrNumber) { throw 'The branch pull request context number does not match the resolved pull request.' }
        }
        if ($null -eq $pr) { throw 'Revision requires an existing pull request; refusing to create another PR.' }
        if ([string](Get-CodexPublicationValue $pr 'headRefName' '') -ne $branch) { throw 'Pull request head does not match persisted branch.' }
        if ([string](Get-CodexPublicationValue $pr 'state' '') -ne 'OPEN') { throw 'Pull request is not open.' }
        if ([string](Get-CodexPublicationValue $pr 'baseRefName' '') -ne 'master') { throw 'Pull request base does not match master.' }
        $draftProperty = $pr.PSObject.Properties['isDraft']
        if ($null -eq $draftProperty -or -not [bool]$draftProperty.Value) { throw 'Pull request is not a draft.' }
        if ([string](Get-CodexPublicationValue $pr 'body' '') -notmatch "(?i)#\s*$IssueNumber\b") { throw 'Pull request does not identify the requested issue.' }
        $resolvedPrNumber = [int](Get-CodexPublicationValue $pr 'number' 0)
        $comments = @((Get-CodexPublicationValue $pr 'comments' @())) + @((Get-CodexPublicationValue $pr 'reviews' @()))
        $reviewText = ($comments | ForEach-Object { [string](Get-CodexPublicationValue $_ 'body' (Get-CodexPublicationValue $_ 'comment' '')) } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join "`n`n"
        $userEditSnapshot = @(Get-CodexRevisionUserEditSnapshot -Worktree $worktree -GitCommandRunner $GitCommandRunner)
        $reviewText = (@($reviewText, (Format-CodexRevisionUserEditContext -Snapshot $userEditSnapshot)) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join "`n`n"
        $attemptNumber = [int](Get-CodexPublicationValue $attempt 'attempt' 1) + 1
        Set-CodexOrchestrationField $attempt 'attempt' $attemptNumber
        Set-CodexOrchestrationField $attempt 'publicationStage' 'none'
        Set-CodexOrchestrationField $attempt 'status' 'running'
        $attemptRun = Join-Path $paths.RunRoot (Join-Path "issue-$IssueNumber" ([string]$attemptNumber))
        Set-CodexOrchestrationField $attempt 'runDirectory' $attemptRun
        if ($null -ne $StateWriter) { & $StateWriter $StatePath $IssueNumber $attempt | Out-Null } else { Write-CodexIssueAttemptState -Path $StatePath -IssueNumber $IssueNumber -AttemptState $attempt | Out-Null }
        if ($null -ne $CodexProvider) { $codex = & $CodexProvider $attempt.worktree $issue $Config $attemptRun $StatePath $reviewText $attempt.threadId } else { $codex = Invoke-CodexRun -IssueWorktree $attempt.worktree -IssueContext $issue -Config $Config -RunDirectory $attemptRun -StatePath $StatePath -Revision -ThreadId $attempt.threadId -ReviewComments $reviewText }
        $newThreadId = [string](Get-CodexPublicationValue $codex 'ThreadId' '')
        if (-not [string]::IsNullOrWhiteSpace($newThreadId)) {
            $freshState = if ($null -ne $StateReader) { & $StateReader $StatePath } else { Read-CodexWorkerState -Path $StatePath }
            $freshAttempt = Get-CodexIssueAttemptState -State $freshState -IssueNumber $IssueNumber
            if ($null -ne $freshAttempt) { $attempt = $freshAttempt }
            Set-CodexOrchestrationField $attempt 'threadId' $newThreadId
            if ($null -ne $StateWriter) { & $StateWriter $StatePath $IssueNumber $attempt | Out-Null } else { Write-CodexIssueAttemptState -Path $StatePath -IssueNumber $IssueNumber -AttemptState $attempt | Out-Null }
        }
        $summary = Get-CodexPublicationValue $codex 'Summary' $null
        if ([string](Get-CodexPublicationValue $codex 'Classification' '') -ne 'completed' -or [string](Get-CodexPublicationValue $codex 'Status' '') -ne 'completed') { throw 'Revision Codex run did not complete successfully.' }
        if ($null -eq $summary -or -not (Test-CodexSummary -Summary $summary)) { throw 'Revision Codex run returned a malformed or incomplete summary.' }
        if (-not (Test-CodexRevisionUserEditsPreserved -Worktree $worktree -Snapshot $userEditSnapshot)) { throw 'Codex changed a pre-existing user edit; publication is blocked.' }
        Set-CodexOrchestrationField $attempt 'status' 'pr-ready'
        Set-CodexOrchestrationField $attempt 'summary' $summary
        if ($null -ne $StateWriter) { & $StateWriter $StatePath $IssueNumber $attempt | Out-Null } else { Write-CodexIssueAttemptState -Path $StatePath -IssueNumber $IssueNumber -AttemptState $attempt | Out-Null }
        $published = Publish-CodexIssue -AttemptState $attempt -IssueContext $issue -Config $Config -StatePath $StatePath -StateWriter $StateWriter -GitCommandRunner $GitCommandRunner -GitHubCommandRunner $GitHubCommandRunner -DataRoot $DataRoot -Repository $Repository -RequireExistingPullRequest
        $evidence = "Codex revision validation completed for commit $($published.commit). Existing draft PR was updated; no new PR was created."
        Add-CodexPullRequestComment -Repository $Repository -PullRequestNumber $resolvedPrNumber -Body $evidence -CommandRunner $GitHubCommandRunner | Out-Null
        return [pscustomobject][ordered]@{ IssueNumber = $IssueNumber; Status = 'pr-ready'; PublicationStage = $published.publicationStage; PrUrl = $published.prUrl; ExistingPullRequest = $true; PullRequestNumber = $resolvedPrNumber }
    } catch {
        if ($null -ne $attempt) {
            Set-CodexOrchestrationField $attempt 'status' 'blocked'
            Set-CodexOrchestrationField $attempt 'lastError' $_.Exception.Message
            try {
                if ($null -ne $StateWriter) { & $StateWriter $StatePath $IssueNumber $attempt | Out-Null } else { Write-CodexIssueAttemptState -Path $StatePath -IssueNumber $IssueNumber -AttemptState $attempt | Out-Null }
            } catch { }
        }
        throw
    } finally {
        if ($null -ne $lock) { if ($null -ne $UnlockProvider) { & $UnlockProvider $lock } else { Exit-CodexWorkerLock -Handle $lock } }
    }
}
