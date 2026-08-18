Describe 'Codex worker publication' {
    BeforeEach {
        Import-Module (Join-Path $PSScriptRoot '..\codex-worker\CodexWorker.psd1') -Force
        $worktree = Join-Path $TestDrive 'worktree'
        New-Item -ItemType Directory -Path $worktree -Force | Out-Null
        $summary = [pscustomobject][ordered]@{
            status = 'completed'
            rootCauseOrApproach = 'Keep publication behind the wrapper.'
            changedComponents = @('scripts/codex-worker')
            decisions = @('Use typed argument arrays.')
            validation = @([pscustomobject]@{ command = 'focused tests'; outcome = 'passed'; details = 'all pass' })
            warnings = @()
            remainingRisks = @()
            commitMessage = 'feat: publish changes'
            prTitle = 'feat: publish changes'
            requiresHumanInput = $false
            humanQuestion = $null
        }
        $script:publishWorktree = $worktree
        $script:publishSummary = $summary
    }

    It 'rejects an empty diff' {
        $git = { param($Arguments) if ($Arguments -contains '--name-only') { return '' }; return '' }.GetNewClosure()
        $review = Test-CodexPublication -Summary $publishSummary -Worktree $publishWorktree -IssueNumber 42 -GitCommandRunner $git
        $review.Allowed | Should Be $false
        ($review.Blockers -join ' ') | Should Match '(?i)diff'
    }

    It 'blocks whitespace errors, suspicious credentials, and durable data paths' {
        $git = { param($Arguments)
            if ($Arguments -contains '--name-only') { return ".env`nconfig/auth.json`nrsa.pem`nworker-state/state.json" }
            if ($Arguments -contains '--check') { return 'file: trailing whitespace.' }
            return '<<<<<<< HEAD'
        }.GetNewClosure()
        $review = Test-CodexPublication -Summary $publishSummary -Worktree $publishWorktree -IssueNumber 42 -DataRoot (Join-Path $publishWorktree 'worker-state') -GitCommandRunner $git
        $review.Allowed | Should Be $false
        ($review.Blockers -join ' ') | Should Match '(?i)credential|secret|durable|whitespace|conflict'
    }

    It 'blocks completed summaries with a failed required validation' {
        $summary = $publishSummary.PSObject.Copy()
        $summary.validation = @([pscustomobject]@{ command = 'required test'; outcome = 'failed'; details = 'assertion failed' })
        $git = { param($Arguments) if ($Arguments -contains '--name-only') { return 'src/change.ps1' }; return '' }.GetNewClosure()
        $review = Test-CodexPublication -Summary $summary -Worktree $publishWorktree -IssueNumber 42 -GitCommandRunner $git
        $review.Allowed | Should Be $false
        ($review.Blockers -join ' ') | Should Match '(?i)validation'
    }

    It 'renders deterministic required PR headings and issue reference' {
        $body = ConvertTo-CodexPullRequestBody -Summary $publishSummary -IssueContext ([pscustomobject]@{ body = 'Reported behavior.' }) -IssueNumber 42
        $body | Should Match '## Summary'
        $body | Should Match '## Problem'
        $body | Should Match '## Root Cause / Design'
        $body | Should Match '## Changes'
        $body | Should Match '## Validation'
        $body | Should Match '## Risks'
        $body | Should Match '## Issue'
        $body | Should Match 'Fixes #42'
        ($body.IndexOf('## Summary') -lt $body.IndexOf('## Problem')) | Should Be $true
    }

    It 'recovers from push failure without creating another commit' {
        $dataRoot = Join-Path $TestDrive 'data'
        New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
        $run = Join-Path $TestDrive 'run'
        New-Item -ItemType Directory -Path $run -Force | Out-Null
        $statePath = Join-Path $dataRoot 'state.json'
        $attempt = [pscustomobject]@{ issueNumber = 42; status = 'pr-ready'; attempt = 1; branch = 'codex/42-publication'; worktree = $publishWorktree; threadId = $null; runDirectory = $run; commit = $null; prUrl = $null; retryCount = 0; publicationStage = 'ready'; lastError = $null }
        $publishSummary | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $run 'final-summary.json')
        Write-CodexWorkerState -Path $statePath -State ([pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = $attempt }; deployment = $null })
        $commitCount = [pscustomobject]@{ Value = 0 }
        $git = { param($Arguments)
            if ($Arguments -contains 'status') { return ' M src/change.ps1' }
            if ($Arguments -contains '--name-only') { return 'src/change.ps1' }
            if ($Arguments -contains '--check') { return '' }
            if ($Arguments -contains 'add') { return '' }
            if ($Arguments -contains 'commit') { $commitCount.Value++; return '' }
            if ($Arguments -contains 'rev-parse') { return 'abc123' }
            if ($Arguments -contains 'push') { throw 'push unavailable' }
            return ''
        }.GetNewClosure()
        $failed = $false
        try { Publish-CodexIssue -AttemptState $attempt -IssueContext ([pscustomobject]@{ body = 'body'; title = 'Issue' }) -Config ([pscustomobject]@{ repository = 'owner/repo'; defaultBranch = 'master'; dataRoot = $dataRoot }) -StatePath $statePath -GitCommandRunner $git -GitHubCommandRunner { param($Arguments) '[]' } | Out-Null } catch { $failed = $true }
        $failed | Should Be $true
        (Read-CodexWorkerState -Path $statePath).issues.'42'.publicationStage | Should Be 'committed'
        $commitCount.Value | Should Be 1
    }

    It 'recovers from a PR failure without pushing or creating twice' {
        $dataRoot = Join-Path $TestDrive 'data'
        $run = Join-Path $TestDrive 'run'
        New-Item -ItemType Directory -Path $dataRoot,$run -Force | Out-Null
        $statePath = Join-Path $dataRoot 'state.json'
        $attempt = [pscustomobject]@{ issueNumber = 42; status = 'pr-ready'; attempt = 1; branch = 'codex/42-publication'; worktree = $publishWorktree; threadId = $null; runDirectory = $run; commit = 'abc123'; prUrl = $null; retryCount = 0; publicationStage = 'committed'; lastError = $null; summary = $publishSummary }
        $publishSummary | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $run 'final-summary.json')
        Write-CodexWorkerState -Path $statePath -State ([pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = $attempt }; deployment = $null })
        $pushCount = [pscustomobject]@{ Value = 0 }
        $git = { param($Arguments)
            if ($Arguments -contains 'status') { return ' M src/change.ps1' }
            if ($Arguments -contains '--name-only') { return 'src/change.ps1' }
            if ($Arguments -contains '--check') { return '' }
            if ($Arguments -contains 'rev-parse') { return 'abc123' }
            if ($Arguments -contains 'push') { $pushCount.Value++; return '' }
            return ''
        }.GetNewClosure()
        $ghCalls = [System.Collections.Generic.List[string]]::new()
        $gh = { param($Arguments)
            $ghCalls.Add(($Arguments -join ' ')) | Out-Null
            if ($Arguments -contains 'list') {
                if ($ghCalls.Count -eq 1) { throw 'PR lookup unavailable' }
                return '[]'
            }
            if ($Arguments -contains 'create') { return 'https://example.test/pr/9' }
            return ''
        }.GetNewClosure()
        { Publish-CodexIssue -AttemptState $attempt -IssueContext ([pscustomobject]@{ body = 'body'; title = 'Issue' }) -Config ([pscustomobject]@{ repository = 'owner/repo'; defaultBranch = 'master'; dataRoot = $dataRoot }) -StatePath $statePath -GitCommandRunner $git -GitHubCommandRunner $gh | Out-Null } | Should Throw
        (Read-CodexWorkerState -Path $statePath).issues.'42'.publicationStage | Should Be 'pushed'
        $result = Publish-CodexIssue -AttemptState $attempt -IssueContext ([pscustomobject]@{ body = 'body'; title = 'Issue' }) -Config ([pscustomobject]@{ repository = 'owner/repo'; defaultBranch = 'master'; dataRoot = $dataRoot }) -StatePath $statePath -GitCommandRunner $git -GitHubCommandRunner $gh
        $result.publicationStage | Should Be 'pr-created'
        $pushCount.Value | Should Be 1
        (($ghCalls | Where-Object { $_ -match '\bcreate\b' }).Count) | Should Be 1
    }

    It 'reuses an existing PR during idempotent publication retry' {
        $gh = { param($Arguments) if ($Arguments -contains 'list') { return '[{"number":7,"url":"https://example.test/pr/7"}]' }; return '' }
        (Get-CodexPullRequestForBranch -Repository 'owner/repo' -BranchName 'codex/42-publication' -CommandRunner $gh).number | Should Be 7
    }

    It 'rejects malformed or incomplete summaries before publication' {
        $malformed = [pscustomobject]@{ status = 'completed'; requiresHumanInput = $false }
        $git = { param($Arguments) if ($Arguments -contains '--name-only') { return 'src/change.ps1' }; return '' }
        $review = Test-CodexPublication -Summary $malformed -Worktree $publishWorktree -IssueNumber 42 -GitCommandRunner $git
        $review.Allowed | Should Be $false
        ($review.Blockers -join ' ') | Should Match '(?i)summary'
    }

    It 'inspects staged, untracked, and committed paths for secrets and conflict markers' {
        $git = { param($Arguments)
            if ($Arguments -contains '--name-only' -and $Arguments -contains '--cached') { return 'staged.ps1`nprivate.key' }
            if ($Arguments -contains '--name-only') { return 'unstaged.ps1' }
            if ($Arguments -contains '--others') { return 'new.ps1' }
            if ($Arguments -contains '--check') { return '' }
            if ($Arguments -contains 'HEAD^') { return "diff --git a/new.ps1 b/new.ps1`n+<<<<<<< HEAD" }
            return ''
        }.GetNewClosure()
        $review = Test-CodexPublication -Summary $publishSummary -Worktree $publishWorktree -IssueNumber 42 -GitCommandRunner $git
        $review.Allowed | Should Be $false
        ($review.Blockers -join ' ') | Should Match '(?i)secret|credential|conflict'
        ($review.ChangedPaths -join ' ') | Should Match 'staged.ps1'
        ($review.ChangedPaths -join ' ') | Should Match 'new.ps1'
    }

    It 'treats a failed validation as required unless required is explicitly false' {
        $summary = $publishSummary.PSObject.Copy()
        $summary.validation = @([pscustomobject]@{ command = 'optional check'; outcome = 'failed'; details = 'failed'; required = $false })
        $git = { param($Arguments) if ($Arguments -contains '--name-only') { return 'src/change.ps1' }; return '' }
        $review = Test-CodexPublication -Summary $summary -Worktree $publishWorktree -IssueNumber 42 -GitCommandRunner $git
        $review.Allowed | Should Be $true
        ($review.Risks -join ' ') | Should Match '(?i)failed'
    }

    It 'does not resume a clean unproven worktree from HEAD alone' {
        $dataRoot = Join-Path $TestDrive 'data'
        $run = Join-Path $TestDrive 'run'
        New-Item -ItemType Directory -Path $dataRoot,$run -Force | Out-Null
        $statePath = Join-Path $dataRoot 'state.json'
        $attempt = [pscustomobject]@{ issueNumber = 42; status = 'pr-ready'; attempt = 1; branch = 'codex/42-publication'; worktree = $publishWorktree; runDirectory = $run; commit = $null; prUrl = $null; retryCount = 0; publicationStage = 'ready' }
        $publishSummary | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $run 'final-summary.json')
        Write-CodexWorkerState -Path $statePath -State ([pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = $attempt }; deployment = $null })
        $git = { param($Arguments) if ($Arguments -contains 'status') { return '' }; if ($Arguments -contains 'rev-parse') { return 'abc123' }; throw 'unexpected git operation' }
        $failed = $false
        try { Publish-CodexIssue -AttemptState $attempt -IssueContext ([pscustomobject]@{ body = 'body' }) -Config ([pscustomobject]@{ repository = 'owner/repo'; dataRoot = $dataRoot }) -StatePath $statePath -GitCommandRunner $git -GitHubCommandRunner { param($Arguments) '[]' } | Out-Null } catch { $failed = $true }
        $failed | Should Be $true
    }

    It 'requires a persisted commit SHA to match HEAD before resuming clean publication' {
        $dataRoot = Join-Path $TestDrive 'data'
        $run = Join-Path $TestDrive 'run'
        New-Item -ItemType Directory -Path $dataRoot,$run -Force | Out-Null
        $statePath = Join-Path $dataRoot 'state.json'
        $attempt = [pscustomobject]@{ issueNumber = 42; status = 'pr-ready'; attempt = 1; branch = 'codex/42-publication'; worktree = $publishWorktree; runDirectory = $run; commit = 'different'; prUrl = $null; retryCount = 0; publicationStage = 'committed' }
        $publishSummary | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $run 'final-summary.json')
        $git = { param($Arguments) if ($Arguments -contains 'status') { return '' }; if ($Arguments -contains 'rev-parse') { return 'abc123' }; throw 'unexpected git operation' }
        $failed = $false
        try { Publish-CodexIssue -AttemptState $attempt -IssueContext ([pscustomobject]@{ body = 'body' }) -Config ([pscustomobject]@{ repository = 'owner/repo'; dataRoot = $dataRoot }) -StatePath $statePath -GitCommandRunner $git -GitHubCommandRunner { param($Arguments) '[]' } | Out-Null } catch { $failed = $true }
        $failed | Should Be $true
    }

    It 'reuses the registered worktree and resolved draft PR during revision' {
        $repoRoot = Join-Path $TestDrive 'repo'
        $dataRoot = Join-Path $TestDrive 'revision-data'
        $worktree = Join-Path $repoRoot '.worktrees\issue-42-publication'
        New-Item -ItemType Directory -Path $repoRoot,$dataRoot,$worktree -Force | Out-Null
        $statePath = Join-Path $dataRoot 'state.json'
        $attempt = [pscustomobject]@{ issueNumber = 42; status = 'pr-ready'; attempt = 1; branch = 'codex/42-publication'; worktree = $worktree; threadId = 'old-thread'; runDirectory = $null; commit = 'old'; prUrl = 'https://example.test/pr/7'; retryCount = 0; publicationStage = 'pr-created'; lastError = $null }
        Write-CodexWorkerState -Path $statePath -State ([pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = $attempt }; deployment = $null })
        $summary = [pscustomobject][ordered]@{ status = 'completed'; rootCauseOrApproach = 'revision'; changedComponents = @('src/change.ps1'); decisions = @(); validation = @([pscustomobject]@{ command = 'focused'; outcome = 'passed'; details = 'ok' }); warnings = @(); remainingRisks = @(); commitMessage = 'fix: revision'; prTitle = 'fix: revision'; requiresHumanInput = $false; humanQuestion = $null }
        $events = [System.Collections.Generic.List[string]]::new()
        $git = { param($Arguments)
            if ($Arguments -contains 'worktree' -and $Arguments -contains 'list') { return "worktree $worktree`nHEAD old`nbranch refs/heads/codex/42-publication`n" }
            if ($Arguments -contains 'status') { return ' M src/change.ps1' }
            if ($Arguments -contains '--name-only') { return 'src/change.ps1' }
            if ($Arguments -contains '--check') { return '' }
            if ($Arguments -contains 'add' -or $Arguments -contains 'commit') { return '' }
            if ($Arguments -contains 'rev-parse') { return 'newsha' }
            if ($Arguments -contains 'push') { return '' }
            return ''
        }.GetNewClosure()
        $gh = { param($Arguments)
            if (($Arguments -join '/') -match '/permission$') { return '{"permission":"write"}' }
            if ($Arguments -contains 'issue') { return '{"number":42,"title":"Issue","body":"body","labels":[],"comments":[]}' }
            if ($Arguments -contains 'pr' -and $Arguments -contains 'view') { return '{"number":7,"url":"https://example.test/pr/7","state":"OPEN","isDraft":true,"headRefName":"codex/42-publication","baseRefName":"master","body":"Fixes #42","comments":[],"reviews":[]}' }
            if ($Arguments -contains 'pr' -and $Arguments -contains 'list') { return '[{"number":7,"url":"https://example.test/pr/7","state":"OPEN","isDraft":true,"headRefName":"codex/42-publication","baseRefName":"master","body":"Fixes #42"}]' }
            if ($Arguments -contains 'pr' -and $Arguments -contains 'comment') { $events.Add('comment') | Out-Null; return '' }
            if ($Arguments -contains 'pr' -and $Arguments -contains 'edit') { $events.Add('edit') | Out-Null; return '' }
            throw 'unexpected GitHub operation'
        }.GetNewClosure()
        $stateWriter = { param($Path,$Number,$Current) Write-CodexIssueAttemptState -Path $Path -IssueNumber $Number -AttemptState $Current | Out-Null }.GetNewClosure()
        $codex = { param($WorktreePath,$Issue,$Config,$RunDirectory,$Path,$Review,$Thread) $events.Add(('codex:{0}:{1}' -f $WorktreePath,$Review)) | Out-Null; [pscustomobject]@{ Status = 'completed'; Classification = 'completed'; ThreadId = 'new-thread'; Summary = $summary } }.GetNewClosure()
        $lock = { param($Path) $events.Add('lock') | Out-Null; return [pscustomobject]@{} }.GetNewClosure()
        $unlock = { param($Handle) $events.Add('unlock') | Out-Null }.GetNewClosure()
        $result = Invoke-CodexRevision -Repository 'owner/repo' -IssueNumber 42 -Actor 'trusted-user' -PullRequestNumber '7' -RepositoryRoot $repoRoot -DataRoot $dataRoot -Config ([pscustomobject]@{ repository = 'owner/repo'; defaultBranch = 'master'; dataRoot = $dataRoot }) -StatePath $statePath -StateWriter $stateWriter -LockProvider $lock -UnlockProvider $unlock -GitCommandRunner $git -GitHubCommandRunner $gh -CodexProvider $codex
        $result.PullRequestNumber | Should Be 7
        ($events -contains 'comment') | Should Be $true
        ($events -contains 'edit') | Should Be $true
        ($events -contains 'lock') | Should Be $true
        (Get-CodexIssueAttemptState -State (Read-CodexWorkerState -Path $statePath) -IssueNumber 42).threadId | Should Be 'new-thread'
        ($events -join ' ') | Should Not Match '(?i)create|force|merge|ready'
    }

    It 'rejects an explicit pull request number that resolves to another PR' {
        $repoRoot = Join-Path $TestDrive 'repo'
        $dataRoot = Join-Path $TestDrive 'revision-data'
        $worktree = Join-Path $repoRoot '.worktrees\issue-42-publication'
        New-Item -ItemType Directory -Path $repoRoot,$dataRoot,$worktree -Force | Out-Null
        $statePath = Join-Path $dataRoot 'state.json'
        $attempt = [pscustomobject]@{ issueNumber = 42; branch = 'codex/42-publication'; worktree = $worktree }
        Write-CodexWorkerState -Path $statePath -State ([pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = $attempt }; deployment = $null })
        $git = { param($Arguments) if ($Arguments -contains 'worktree' -and $Arguments -contains 'list') { return "worktree $worktree`nHEAD old`nbranch refs/heads/codex/42-publication`n" }; return '' }
        $gh = { param($Arguments) if (($Arguments -join '/') -match '/permission$') { return '{"permission":"write"}' }; if ($Arguments -contains 'pr') { return '{"number":8,"isDraft":true,"headRefName":"codex/42-publication","baseRefName":"master","body":"Fixes #42"}' }; return '{"number":42,"body":"body","comments":[]}' }
        { Invoke-CodexRevision -Repository 'owner/repo' -IssueNumber 42 -Actor 'trusted-user' -PullRequestNumber '7' -RepositoryRoot $repoRoot -DataRoot $dataRoot -StatePath $statePath -LockProvider { param($p) [pscustomobject]@{} } -UnlockProvider { param($h) } -GitCommandRunner $git -GitHubCommandRunner $gh -CodexProvider { param($a,$b,$c,$d,$e,$f,$g) } } | Should Throw
    }
}
