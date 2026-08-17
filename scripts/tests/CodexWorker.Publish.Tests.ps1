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

    It 'reuses an existing PR during idempotent publication retry' {
        $gh = { param($Arguments) if ($Arguments -contains 'list') { return '[{"number":7,"url":"https://example.test/pr/7"}]' }; return '' }
        (Get-CodexPullRequestForBranch -Repository 'owner/repo' -BranchName 'codex/42-publication' -CommandRunner $gh).number | Should Be 7
    }
}
