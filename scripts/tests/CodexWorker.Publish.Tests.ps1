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

    It 'returns stdout when Git emits a successful stderr warning under Stop' {
        $warningWorktree = Join-Path $TestDrive 'line-ending-warning-worktree'
        New-Item -ItemType Directory -Path $warningWorktree -Force | Out-Null
        & git.exe -C $warningWorktree init -q
        & git.exe -C $warningWorktree config user.email 'test@example.invalid'
        & git.exe -C $warningWorktree config user.name 'Codex Worker Test'
        [IO.File]::WriteAllText((Join-Path $warningWorktree 'sample.txt'), "one`ntwo`n", [Text.UTF8Encoding]::new($false))
        & git.exe -C $warningWorktree -c core.autocrlf=false add sample.txt
        & git.exe -C $warningWorktree -c core.autocrlf=false commit -qm initial
        & git.exe -C $warningWorktree config core.autocrlf true
        [IO.File]::WriteAllText((Join-Path $warningWorktree 'sample.txt'), "one`nthree`n", [Text.UTF8Encoding]::new($false))
        $module = Get-Module CodexWorker

        $output = & $module {
            param($Worktree)
            $previousPreference = $ErrorActionPreference
            $ErrorActionPreference = 'Stop'
            try {
                Invoke-CodexPublicationGit -Worktree $Worktree -Arguments @('diff', '--name-only')
            } finally {
                $ErrorActionPreference = $previousPreference
            }
        } $warningWorktree

        $output | Should Be 'sample.txt'
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

    It 'requests and returns draft and state fields for an explicit pull request' {
        $captured = [System.Collections.Generic.List[object]]::new()
        $gh = { param([string[]] $Arguments) $captured.Add($Arguments) | Out-Null; return '{"number":7,"state":"OPEN","isDraft":true,"headRefName":"codex/42-publication","baseRefName":"master"}' }.GetNewClosure()
        $pr = Get-CodexPullRequestContext -Repository 'owner/repo' -PullRequestNumber 7 -CommandRunner $gh
        (($captured[0] -join ',') -match 'isDraft') | Should Be $true
        (($captured[0] -join ',') -match 'state') | Should Be $true
        (@($captured[0] | Where-Object { $_ -isnot [string] }).Count) | Should Be 0
        $pr.isDraft | Should Be $true
        $pr.state | Should Be 'OPEN'
    }

    It 'creates a non-interactive draft pull request with an explicit title' {
        $captured = [System.Collections.Generic.List[object]]::new()
        $gh = { param([string[]] $Arguments) $captured.Add($Arguments) | Out-Null; return 'https://example.test/pr/7' }.GetNewClosure()
        $bodyPath = Join-Path $TestDrive 'pull-request.md'
        Set-Content -LiteralPath $bodyPath -Value 'body'

        $url = New-CodexDraftPullRequest -Repository 'owner/repo' -BaseBranch 'master' -HeadBranch 'codex/42-publication' -Title 'fix: example (#42)' -BodyPath $bodyPath -CommandRunner $gh

        $url | Should Be 'https://example.test/pr/7'
        $arguments = @($captured[0])
        $arguments -contains '--title' | Should Be $true
        $arguments[([array]::IndexOf($arguments, '--title') + 1)] | Should Be 'fix: example (#42)'
        $arguments -contains '--body-file' | Should Be $true
    }

    It 'rejects closed, non-draft, wrong-head, and wrong-base revision PRs' {
        $repoRoot = Join-Path $TestDrive 'repo'
        $dataRoot = Join-Path $TestDrive 'revision-data'
        $worktree = Join-Path $repoRoot '.worktrees\issue-42-publication'
        New-Item -ItemType Directory -Path $repoRoot,$dataRoot,$worktree -Force | Out-Null
        $statePath = Join-Path $dataRoot 'state.json'
        $attempt = [pscustomobject]@{ issueNumber = 42; status = 'pr-ready'; attempt = 1; branch = 'codex/42-publication'; worktree = $worktree; threadId = 'old-thread'; runDirectory = $null; commit = 'old'; prUrl = 'https://example.test/pr/7'; retryCount = 0; publicationStage = 'pr-created'; lastError = $null }
        $git = { param([string[]] $Arguments) if ($Arguments -contains 'worktree' -and $Arguments -contains 'list') { return "worktree $worktree`nHEAD old`nbranch refs/heads/codex/42-publication`n" }; return '' }.GetNewClosure()
        $codexCalls = [pscustomobject]@{ Value = 0 }
        $codex = { param($a,$b,$c,$d,$e,$f,$g) $codexCalls.Value++; throw 'Codex must not run for invalid PR metadata.' }.GetNewClosure()
        foreach ($case in @(
            [pscustomobject]@{ state = 'CLOSED'; isDraft = $true; headRefName = 'codex/42-publication'; baseRefName = 'master' },
            [pscustomobject]@{ state = 'OPEN'; isDraft = $false; headRefName = 'codex/42-publication'; baseRefName = 'master' },
            [pscustomobject]@{ state = 'OPEN'; isDraft = $true; headRefName = 'codex/other'; baseRefName = 'master' },
            [pscustomobject]@{ state = 'OPEN'; isDraft = $true; headRefName = 'codex/42-publication'; baseRefName = 'develop' }
        )) {
            Write-CodexWorkerState -Path $statePath -State ([pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = $attempt }; deployment = $null })
            $gh = { param([string[]] $Arguments)
                if (($Arguments -join '/') -match '/permission$') { return '{"permission":"write"}' }
                if ($Arguments -contains 'issue') { return '{"number":42,"title":"Issue","body":"body","labels":[],"comments":[]}' }
                return ([pscustomobject]@{ number = 7; state = $case.state; isDraft = $case.isDraft; headRefName = $case.headRefName; baseRefName = $case.baseRefName; body = 'Fixes #42'; comments = @(); reviews = @() } | ConvertTo-Json -Compress)
            }.GetNewClosure()
            $pullRequestArgument = if ($case.state -eq 'CLOSED') { '' } else { '7' }
            { Invoke-CodexRevision -Repository 'owner/repo' -IssueNumber 42 -Actor 'trusted-user' -PullRequestNumber $pullRequestArgument -RepositoryRoot $repoRoot -DataRoot $dataRoot -Config ([pscustomobject]@{ repository = 'owner/repo'; defaultBranch = 'master'; dataRoot = $dataRoot }) -StatePath $statePath -LockProvider { param($p) [pscustomobject]@{} } -UnlockProvider { param($h) } -GitCommandRunner $git -GitHubCommandRunner $gh -CodexProvider $codex } | Should Throw
        }
        $codexCalls.Value | Should Be 0
    }

    It 'rejects malformed or incomplete summaries before publication' {
        $malformed = [pscustomobject]@{ status = 'completed'; requiresHumanInput = $false }
        $git = { param($Arguments) if ($Arguments -contains '--name-only') { return 'src/change.ps1' }; return '' }
        $review = Test-CodexPublication -Summary $malformed -Worktree $publishWorktree -IssueNumber 42 -GitCommandRunner $git
        $review.Allowed | Should Be $false
        ($review.Blockers -join ' ') | Should Match '(?i)summary'
    }

    It 'inspects staged, untracked, and committed paths for secrets and conflict markers' {
        Set-Content -LiteralPath (Join-Path $publishWorktree 'staged.ps1') -Value "safe`n" -NoNewline
        Set-Content -LiteralPath (Join-Path $publishWorktree 'new.ps1') -Value "safe`n" -NoNewline
        Set-Content -LiteralPath (Join-Path $publishWorktree 'committed.ps1') -Value "<<<<<<< HEAD`n" -NoNewline
        Set-Content -LiteralPath (Join-Path $publishWorktree 'private.key') -Value "secret`n" -NoNewline
        $seen = [System.Collections.Generic.List[object]]::new()
        $git = { param($Arguments)
            $seen.Add([pscustomobject]@{
                Arguments = $Arguments
                Type = $Arguments.GetType()
                IsStringArray = ($Arguments -is [string[]])
            }) | Out-Null
            if ($Arguments -contains '--name-only' -and $Arguments -contains '--cached') { return "staged.ps1`nprivate.key" }
            if ($Arguments -contains '--others') { return "new.ps1" }
            if ($Arguments -contains 'HEAD^' -and $Arguments -contains '--name-only') { return "committed.ps1" }
            if ($Arguments -contains '--name-only') { return '' }
            if ($Arguments -contains '--check') { return '' }
            if ($Arguments -contains 'HEAD^' -and $Arguments -contains '--no-ext-diff') { return "diff --git a/committed.ps1 b/committed.ps1`n+<<<<<<< HEAD" }
            return ''
        }.GetNewClosure()
        $review = Test-CodexPublication -Summary $publishSummary -Worktree $publishWorktree -IssueNumber 42 -GitCommandRunner $git
        $review.Allowed | Should Be $false
        ($review.Blockers -join ' ') | Should Match '(?i)secret|credential|conflict'
        ($review.ChangedPaths -join ' ') | Should Match 'staged.ps1'
        ($review.ChangedPaths -join ' ') | Should Match 'new.ps1'
        ($review.ChangedPaths -join ' ') | Should Match 'committed.ps1'
        (@($seen | Where-Object { ($_.Arguments -join ' ') -match 'diff --name-only' }).Count) | Should Be 1
        (@($seen | Where-Object { ($_.Arguments -join ' ') -match 'diff --cached --name-only' }).Count) | Should Be 1
        (@($seen | Where-Object { ($_.Arguments -join ' ') -match 'ls-files --others --exclude-standard' }).Count) | Should Be 1
        (@($seen | Where-Object { ($_.Arguments -join ' ') -match 'diff HEAD\^ HEAD --name-only' }).Count) | Should Be 1
        (@($seen | Where-Object { -not $_.IsStringArray }).Count) | Should Be 0
        $stringArrayType = ([string[]]::new(0)).GetType()
        (@($seen | Where-Object { $_.Type -ne $stringArrayType }).Count) | Should Be 0

        $root = [IO.Path]::GetFullPath($publishWorktree)
        $expectedPathQueries = @(
            [string[]]@('-C', $root, 'diff', '--name-only'),
            [string[]]@('-C', $root, 'diff', '--cached', '--name-only'),
            [string[]]@('-C', $root, 'ls-files', '--others', '--exclude-standard'),
            [string[]]@('-C', $root, 'diff', 'HEAD^', 'HEAD', '--name-only')
        )
        foreach ($expected in $expectedPathQueries) {
            $expectedText = $expected -join "`0"
            (@($seen | Where-Object { ($_.Arguments -join "`0") -eq $expectedText }).Count) | Should Be 1
        }

        $expectedContentQueries = @(
            [string[]]@('-C', $root, 'diff', '--no-ext-diff', 'HEAD'),
            [string[]]@('-C', $root, 'diff', '--cached', '--no-ext-diff'),
            [string[]]@('-C', $root, 'diff', 'HEAD^', 'HEAD', '--no-ext-diff')
        )
        foreach ($expected in $expectedContentQueries) {
            $expectedText = $expected -join "`0"
            (@($seen | Where-Object { ($_.Arguments -join "`0") -eq $expectedText }).Count) | Should Be 1
        }
    }

    It 'blocks trailing whitespace reported by the unstaged content diff' {
        $seen = [System.Collections.Generic.List[object]]::new()
        $root = [IO.Path]::GetFullPath($publishWorktree)
        $git = { param($Arguments)
            $seen.Add([pscustomobject]@{
                Arguments = $Arguments
                Type = $Arguments.GetType()
                IsStringArray = ($Arguments -is [string[]])
            }) | Out-Null
            if ($Arguments -contains '--name-only') { return 'src/change.ps1' }
            if ($Arguments -contains '--no-ext-diff' -and $Arguments -contains 'HEAD' -and $Arguments -notcontains 'HEAD^') {
                return "diff --git a/src/change.ps1 b/src/change.ps1`n@@ -1 +1 @@`n-safe`n+unsafe  `n"
            }
            return ''
        }.GetNewClosure()
        $review = Test-CodexPublication -Summary $publishSummary -Worktree $publishWorktree -IssueNumber 42 -GitCommandRunner $git
        $review.Allowed | Should Be $false
        ($review.Blockers -join ' ') | Should Match '(?i)trailing whitespace|whitespace errors'
        $expected = [string[]]@('-C', $root, 'diff', '--no-ext-diff', 'HEAD')
        $expectedText = $expected -join "`0"
        $stringArrayType = ([string[]]::new(0)).GetType()
        (@($seen | Where-Object { $_.IsStringArray -and $_.Type -eq $stringArrayType -and ($_.Arguments -join "`0") -eq $expectedText }).Count) | Should Be 1
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
        $issueLabelCommands = [System.Collections.Generic.List[string]]::new()
        $gh = { param($Arguments)
            if (($Arguments -join '/') -match '/permission$') { return '{"permission":"write"}' }
            if ($Arguments -contains 'issue' -and $Arguments -contains 'view') { return '{"number":42,"title":"Issue","body":"body","labels":[{"name":"codex"},{"name":"codex:revise"}],"comments":[]}' }
            if ($Arguments -contains 'issue' -and $Arguments -contains 'edit') { $issueLabelCommands.Add(($Arguments -join ' ')) | Out-Null; return '' }
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
        $result = Invoke-CodexRevision -Repository 'owner/repo' -IssueNumber 42 -Actor 'trusted-user' -PullRequestNumber '7' -RepositoryRoot $repoRoot -DataRoot $dataRoot -Config ([pscustomobject]@{ repository = 'owner/repo'; defaultBranch = 'develop'; dataRoot = $dataRoot }) -StatePath $statePath -StateWriter $stateWriter -LockProvider $lock -UnlockProvider $unlock -GitCommandRunner $git -GitHubCommandRunner $gh -CodexProvider $codex
        $result.PullRequestNumber | Should Be 7
        ($events -contains 'comment') | Should Be $true
        ($events -contains 'edit') | Should Be $true
        ($events -contains 'lock') | Should Be $true
        (Get-CodexIssueAttemptState -State (Read-CodexWorkerState -Path $statePath) -IssueNumber 42).threadId | Should Be 'new-thread'
        ($issueLabelCommands -join ' ') | Should Match '--remove-label codex:revise'
        ($issueLabelCommands -join ' ') | Should Match '--add-label codex:pr-ready'
        ($events -join ' ') | Should Not Match '(?i)create|force|merge|ready'
    }

    It 'resolves a branch PR number then fetches full context and passes review comments' {
        $repoRoot = Join-Path $TestDrive 'repo'
        $dataRoot = Join-Path $TestDrive 'revision-data'
        $worktree = Join-Path $repoRoot '.worktrees\issue-42-publication'
        New-Item -ItemType Directory -Path $repoRoot,$dataRoot,$worktree -Force | Out-Null
        $statePath = Join-Path $dataRoot 'state.json'
        $attempt = [pscustomobject]@{ issueNumber = 42; status = 'pr-ready'; attempt = 1; branch = 'codex/42-publication'; worktree = $worktree; threadId = 'old-thread'; runDirectory = $null; commit = 'old'; prUrl = 'https://example.test/pr/7'; retryCount = 0; publicationStage = 'pr-created'; lastError = $null }
        Write-CodexWorkerState -Path $statePath -State ([pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = $attempt }; deployment = $null })
        $events = [System.Collections.Generic.List[string]]::new()
        $viewCount = [pscustomobject]@{ Value = 0 }
        $git = { param([string[]] $Arguments)
            if ($Arguments -contains 'worktree' -and $Arguments -contains 'list') { return "worktree $worktree`nHEAD old`nbranch refs/heads/codex/42-publication`n" }
            if ($Arguments -contains 'status') { return ' M src/change.ps1' }
            if ($Arguments -contains '--name-only') { return 'src/change.ps1' }
            if ($Arguments -contains '--others') { return '' }
            if ($Arguments -contains '--check' -or $Arguments -contains '--no-ext-diff') { return '' }
            if ($Arguments -contains 'add' -or $Arguments -contains 'commit' -or $Arguments -contains 'push') { return '' }
            if ($Arguments -contains 'rev-parse') { return 'newsha' }
            return ''
        }.GetNewClosure()
        $gh = { param([string[]] $Arguments)
            if (($Arguments -join '/') -match '/permission$') { return '{"permission":"write"}' }
            if ($Arguments -contains 'issue') { return '{"number":42,"title":"Issue","body":"body","labels":[],"comments":[]}' }
            if ($Arguments -contains 'list') { return '[{"number":7,"url":"https://example.test/pr/7","state":"OPEN","isDraft":true,"headRefName":"codex/42-publication","baseRefName":"master","body":"Fixes #42"}]' }
            if ($Arguments -contains 'view') { $viewCount.Value++; return '{"number":7,"state":"OPEN","isDraft":true,"headRefName":"codex/42-publication","baseRefName":"master","body":"Fixes #42","comments":[{"body":"Please retain the existing user edit."}],"reviews":[{"body":"Review says preserve behavior."}]}' }
            if ($Arguments -contains 'edit' -or $Arguments -contains 'comment') { $events.Add(($Arguments -join ' ')) | Out-Null; return '' }
            throw 'unexpected GitHub operation'
        }.GetNewClosure()
        $revisionSummary = $publishSummary.PSObject.Copy()
        $codex = { param($WorktreePath,$Issue,$Config,$RunDirectory,$Path,$Review,$Thread) $events.Add($Review) | Out-Null; [pscustomobject]@{ Status = 'completed'; Classification = 'completed'; ThreadId = 'new-thread'; Summary = $revisionSummary } }.GetNewClosure()
        $result = Invoke-CodexRevision -Repository 'owner/repo' -IssueNumber 42 -Actor 'trusted-user' -RepositoryRoot $repoRoot -DataRoot $dataRoot -Config ([pscustomobject]@{ repository = 'owner/repo'; defaultBranch = 'master'; dataRoot = $dataRoot }) -StatePath $statePath -LockProvider { param($p) [pscustomobject]@{} } -UnlockProvider { param($h) } -GitCommandRunner $git -GitHubCommandRunner $gh -CodexProvider $codex
        $result.PullRequestNumber | Should Be 7
        $viewCount.Value | Should Be 1
        ($events -join ' ') | Should Match 'Please retain the existing user edit'
        ($events -join ' ') | Should Match 'Review says preserve behavior'
    }

    It 'fails closed when branch PR context returns a different number before Codex or publication' {
        $repoRoot = Join-Path $TestDrive 'repo'
        $dataRoot = Join-Path $TestDrive 'revision-data'
        $worktree = Join-Path $repoRoot '.worktrees\issue-42-publication'
        New-Item -ItemType Directory -Path $repoRoot,$dataRoot,$worktree -Force | Out-Null
        $statePath = Join-Path $dataRoot 'state.json'
        $attempt = [pscustomobject]@{ issueNumber = 42; status = 'pr-ready'; attempt = 1; branch = 'codex/42-publication'; worktree = $worktree; threadId = 'old-thread'; runDirectory = $null; commit = 'old'; prUrl = $null; retryCount = 0; publicationStage = 'pr-created'; lastError = $null }
        Write-CodexWorkerState -Path $statePath -State ([pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = $attempt }; deployment = $null })
        $events = [System.Collections.Generic.List[string]]::new()
        $codexCalls = [pscustomobject]@{ Value = 0 }
        $git = { param([string[]] $Arguments)
            if ($Arguments -contains 'worktree' -and $Arguments -contains 'list') { return "worktree $worktree`nHEAD old`nbranch refs/heads/codex/42-publication`n" }
            return ''
        }.GetNewClosure()
        $gh = { param([string[]] $Arguments)
            if (($Arguments -join '/') -match '/permission$') { return '{"permission":"write"}' }
            if ($Arguments -contains 'issue') { return '{"number":42,"title":"Issue","body":"body","labels":[],"comments":[]}' }
            if ($Arguments -contains 'list') { return '[{"number":7,"url":"https://example.test/pr/7","state":"OPEN","isDraft":true,"headRefName":"codex/42-publication","baseRefName":"master","body":"Fixes #42"}]' }
            if ($Arguments -contains 'view') { return '{"number":8,"state":"OPEN","isDraft":true,"headRefName":"codex/42-publication","baseRefName":"master","body":"Fixes #42","comments":[],"reviews":[]}' }
            $events.Add(($Arguments -join ' ')) | Out-Null
            return ''
        }.GetNewClosure()
        $codex = { param($WorktreePath,$Issue,$Config,$RunDirectory,$Path,$Review,$Thread) $codexCalls.Value++; throw 'Codex must not run.' }.GetNewClosure()
        { Invoke-CodexRevision -Repository 'owner/repo' -IssueNumber 42 -Actor 'trusted-user' -RepositoryRoot $repoRoot -DataRoot $dataRoot -Config ([pscustomobject]@{ repository = 'owner/repo'; defaultBranch = 'master'; dataRoot = $dataRoot }) -StatePath $statePath -LockProvider { param($p) [pscustomobject]@{} } -UnlockProvider { param($h) } -GitCommandRunner $git -GitHubCommandRunner $gh -CodexProvider $codex | Out-Null } | Should Throw
        $codexCalls.Value | Should Be 0
        ($events -join ' ') | Should Not Match '(?i)comment|edit|create|push|publication'
    }

    It 'persists the revision thread but blocks malformed summaries before publication' {
        $repoRoot = Join-Path $TestDrive 'repo'
        $dataRoot = Join-Path $TestDrive 'revision-data'
        $worktree = Join-Path $repoRoot '.worktrees\issue-42-publication'
        New-Item -ItemType Directory -Path $repoRoot,$dataRoot,$worktree -Force | Out-Null
        $statePath = Join-Path $dataRoot 'state.json'
        $attempt = [pscustomobject]@{ issueNumber = 42; status = 'pr-ready'; attempt = 1; branch = 'codex/42-publication'; worktree = $worktree; threadId = 'old-thread'; runDirectory = $null; commit = 'old'; prUrl = 'https://example.test/pr/7'; retryCount = 0; publicationStage = 'pr-created'; lastError = $null }
        Write-CodexWorkerState -Path $statePath -State ([pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = $attempt }; deployment = $null })
        $events = [System.Collections.Generic.List[string]]::new()
        $git = { param([string[]] $Arguments)
            if ($Arguments -contains 'worktree' -and $Arguments -contains 'list') { return "worktree $worktree`nHEAD old`nbranch refs/heads/codex/42-publication`n" }
            if ($Arguments -contains 'status') { return '' }
            if ($Arguments -contains '--name-only' -or $Arguments -contains '--others') { return '' }
            return ''
        }.GetNewClosure()
        $gh = { param([string[]] $Arguments)
            if (($Arguments -join '/') -match '/permission$') { return '{"permission":"write"}' }
            if ($Arguments -contains 'issue') { return '{"number":42,"title":"Issue","body":"body","labels":[],"comments":[]}' }
            if ($Arguments -contains 'view') { return '{"number":7,"state":"OPEN","isDraft":true,"headRefName":"codex/42-publication","baseRefName":"master","body":"Fixes #42","comments":[],"reviews":[]}' }
            if ($Arguments -contains 'list') { return '[{"number":7,"state":"OPEN","isDraft":true,"headRefName":"codex/42-publication","baseRefName":"master","body":"Fixes #42"}]' }
            $events.Add('github-mutation') | Out-Null
            return ''
        }.GetNewClosure()
        $codex = { param($WorktreePath,$Issue,$Config,$RunDirectory,$Path,$Review,$Thread) [pscustomobject]@{ Status = 'completed'; Classification = 'completed'; ThreadId = 'new-thread'; Summary = [pscustomobject]@{ status = 'completed' } } }
        $failed = $false
        try { Invoke-CodexRevision -Repository 'owner/repo' -IssueNumber 42 -Actor 'trusted-user' -PullRequestNumber '7' -RepositoryRoot $repoRoot -DataRoot $dataRoot -Config ([pscustomobject]@{ repository = 'owner/repo'; defaultBranch = 'master'; dataRoot = $dataRoot }) -StatePath $statePath -LockProvider { param($p) [pscustomobject]@{} } -UnlockProvider { param($h) } -GitCommandRunner $git -GitHubCommandRunner $gh -CodexProvider $codex | Out-Null } catch { $failed = $true }
        $failed | Should Be $true
        $saved = (Read-CodexWorkerState -Path $statePath).issues.'42'
        $saved.threadId | Should Be 'new-thread'
        $saved.status | Should Be 'blocked'
        $saved.publicationStage | Should Not Be 'pr-ready'
        ($events -join ' ') | Should Not Match '(?i)mutation|create|push|comment|edit'
    }

    It 'persists the revision thread and truthful blocked state when Codex fails' {
        $repoRoot = Join-Path $TestDrive 'repo'
        $dataRoot = Join-Path $TestDrive 'revision-data'
        $worktree = Join-Path $repoRoot '.worktrees\issue-42-publication'
        New-Item -ItemType Directory -Path $repoRoot,$dataRoot,$worktree -Force | Out-Null
        $statePath = Join-Path $dataRoot 'state.json'
        $attempt = [pscustomobject]@{ issueNumber = 42; status = 'pr-ready'; attempt = 1; branch = 'codex/42-publication'; worktree = $worktree; threadId = 'old-thread'; runDirectory = $null; commit = 'old'; prUrl = 'https://example.test/pr/7'; retryCount = 0; publicationStage = 'pr-created'; lastError = $null }
        Write-CodexWorkerState -Path $statePath -State ([pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = $attempt }; deployment = $null })
        $git = { param([string[]] $Arguments)
            if ($Arguments -contains 'worktree' -and $Arguments -contains 'list') { return "worktree $worktree`nHEAD old`nbranch refs/heads/codex/42-publication`n" }
            return ''
        }.GetNewClosure()
        $gh = { param([string[]] $Arguments)
            if (($Arguments -join '/') -match '/permission$') { return '{"permission":"write"}' }
            if ($Arguments -contains 'issue') { return '{"number":42,"title":"Issue","body":"body","labels":[],"comments":[]}' }
            return '{"number":7,"state":"OPEN","isDraft":true,"headRefName":"codex/42-publication","baseRefName":"master","body":"Fixes #42","comments":[],"reviews":[]}'
        }.GetNewClosure()
        $codex = { param($WorktreePath,$Issue,$Config,$RunDirectory,$Path,$Review,$Thread) [pscustomobject]@{ Status = 'failed'; Classification = 'transient_failure'; ThreadId = 'new-thread'; Summary = $null } }
        $failed = $false
        try { Invoke-CodexRevision -Repository 'owner/repo' -IssueNumber 42 -Actor 'trusted-user' -PullRequestNumber '7' -RepositoryRoot $repoRoot -DataRoot $dataRoot -Config ([pscustomobject]@{ repository = 'owner/repo'; defaultBranch = 'master'; dataRoot = $dataRoot }) -StatePath $statePath -LockProvider { param($p) [pscustomobject]@{} } -UnlockProvider { param($h) } -GitCommandRunner $git -GitHubCommandRunner $gh -CodexProvider $codex | Out-Null } catch { $failed = $true }
        $failed | Should Be $true
        $saved = (Read-CodexWorkerState -Path $statePath).issues.'42'
        $saved.threadId | Should Be 'new-thread'
        $saved.status | Should Be 'blocked'
        $saved.publicationStage | Should Not Be 'pr-ready'
    }

    It 'allows a clean revision with no pre-existing user edits' {
        $repoRoot = Join-Path $TestDrive 'repo'
        $dataRoot = Join-Path $TestDrive 'revision-data'
        $worktree = Join-Path $repoRoot '.worktrees\issue-42-publication'
        New-Item -ItemType Directory -Path $repoRoot,$dataRoot,$worktree -Force | Out-Null
        $statePath = Join-Path $dataRoot 'state.json'
        $attempt = [pscustomobject]@{ issueNumber = 42; status = 'pr-ready'; attempt = 1; branch = 'codex/42-publication'; worktree = $worktree; threadId = 'old-thread'; runDirectory = $null; commit = 'old'; prUrl = 'https://example.test/pr/7'; retryCount = 0; publicationStage = 'pr-created'; lastError = $null }
        Write-CodexWorkerState -Path $statePath -State ([pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = $attempt }; deployment = $null })
        $phase = [pscustomobject]@{ CodexFinished = $false }
        $events = [System.Collections.Generic.List[string]]::new()
        $git = { param([string[]] $Arguments)
            if ($Arguments -contains 'worktree' -and $Arguments -contains 'list') { return "worktree $worktree`nHEAD old`nbranch refs/heads/codex/42-publication`n" }
            if ($Arguments -contains 'status' -or $Arguments -contains '--name-only' -or $Arguments -contains '--others') { if ($phase.CodexFinished) { return 'generated.ps1' }; return '' }
            if ($Arguments -contains 'add' -or $Arguments -contains 'commit' -or $Arguments -contains 'push') { $events.Add(($Arguments -join ' ')) | Out-Null; return '' }
            if ($Arguments -contains 'rev-parse') { return 'newsha' }
            return ''
        }.GetNewClosure()
        $gh = { param([string[]] $Arguments)
            if (($Arguments -join '/') -match '/permission$') { return '{"permission":"write"}' }
            if ($Arguments -contains 'issue') { return '{"number":42,"title":"Issue","body":"body","labels":[],"comments":[]}' }
            if ($Arguments -contains 'view') { return '{"number":7,"state":"OPEN","isDraft":true,"headRefName":"codex/42-publication","baseRefName":"master","body":"Fixes #42","comments":[],"reviews":[]}' }
            if ($Arguments -contains 'list') { return '[{"number":7,"state":"OPEN","isDraft":true,"headRefName":"codex/42-publication","baseRefName":"master","body":"Fixes #42"}]' }
            if ($Arguments -contains 'edit' -or $Arguments -contains 'comment') { $events.Add(($Arguments -join ' ')) | Out-Null }
            return ''
        }.GetNewClosure()
        $revisionSummary = $publishSummary.PSObject.Copy()
        $codex = { param($WorktreePath,$Issue,$Config,$RunDirectory,$Path,$Review,$Thread) Set-Content -LiteralPath (Join-Path $WorktreePath 'generated.ps1') -Value "generated`n" -NoNewline; ($phase.CodexFinished = $true) | Out-Null; [pscustomobject]@{ Status = 'completed'; Classification = 'completed'; ThreadId = 'new-thread'; Summary = $revisionSummary } }.GetNewClosure()
        $result = Invoke-CodexRevision -Repository 'owner/repo' -IssueNumber 42 -Actor 'trusted-user' -PullRequestNumber '7' -RepositoryRoot $repoRoot -DataRoot $dataRoot -Config ([pscustomobject]@{ repository = 'owner/repo'; defaultBranch = 'master'; dataRoot = $dataRoot }) -StatePath $statePath -LockProvider { param($p) [pscustomobject]@{} } -UnlockProvider { param($h) } -GitCommandRunner $git -GitHubCommandRunner $gh -CodexProvider $codex
        $result.Status | Should Be 'pr-ready'
        ($events -join ' ') | Should Match '(?i)commit'
    }

    It 'allows a revision when a pre-existing user edit is preserved' {
        $repoRoot = Join-Path $TestDrive 'repo'
        $dataRoot = Join-Path $TestDrive 'revision-data'
        $worktree = Join-Path $repoRoot '.worktrees\issue-42-publication'
        New-Item -ItemType Directory -Path $repoRoot,$dataRoot,$worktree -Force | Out-Null
        $userFile = Join-Path $worktree 'user.ps1'; Set-Content -LiteralPath $userFile -Value "user-value`n" -NoNewline
        $statePath = Join-Path $dataRoot 'state.json'
        $attempt = [pscustomobject]@{ issueNumber = 42; status = 'pr-ready'; attempt = 1; branch = 'codex/42-publication'; worktree = $worktree; threadId = 'old-thread'; runDirectory = $null; commit = 'old'; prUrl = 'https://example.test/pr/7'; retryCount = 0; publicationStage = 'pr-created'; lastError = $null }
        Write-CodexWorkerState -Path $statePath -State ([pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = $attempt }; deployment = $null })
        $phase = [pscustomobject]@{ CodexFinished = $false }
        $events = [System.Collections.Generic.List[string]]::new()
        $git = { param([string[]] $Arguments)
            if ($Arguments -contains 'worktree' -and $Arguments -contains 'list') { return "worktree $worktree`nHEAD old`nbranch refs/heads/codex/42-publication`n" }
            if ($Arguments -contains 'status' -or $Arguments -contains '--name-only') { if ($phase.CodexFinished) { return "user.ps1`ngenerated.ps1" }; return 'user.ps1' }
            if ($Arguments -contains '--others') { return '' }
            if ($Arguments -contains 'add' -or $Arguments -contains 'commit' -or $Arguments -contains 'push') { $events.Add(($Arguments -join ' ')) | Out-Null; return '' }
            if ($Arguments -contains 'rev-parse') { return 'newsha' }
            return ''
        }.GetNewClosure()
        $gh = { param([string[]] $Arguments)
            if (($Arguments -join '/') -match '/permission$') { return '{"permission":"write"}' }
            if ($Arguments -contains 'issue') { return '{"number":42,"title":"Issue","body":"body","labels":[],"comments":[]}' }
            if ($Arguments -contains 'view') { return '{"number":7,"state":"OPEN","isDraft":true,"headRefName":"codex/42-publication","baseRefName":"master","body":"Fixes #42","comments":[],"reviews":[]}' }
            if ($Arguments -contains 'list') { return '[{"number":7,"state":"OPEN","isDraft":true,"headRefName":"codex/42-publication","baseRefName":"master","body":"Fixes #42"}]' }
            if ($Arguments -contains 'edit' -or $Arguments -contains 'comment') { $events.Add(($Arguments -join ' ')) | Out-Null }
            return ''
        }.GetNewClosure()
        $revisionSummary = $publishSummary.PSObject.Copy()
        $codex = { param($WorktreePath,$Issue,$Config,$RunDirectory,$Path,$Review,$Thread) Set-Content -LiteralPath (Join-Path $WorktreePath 'generated.ps1') -Value "generated`n" -NoNewline; ($phase.CodexFinished = $true) | Out-Null; [pscustomobject]@{ Status = 'completed'; Classification = 'completed'; ThreadId = 'new-thread'; Summary = $revisionSummary } }.GetNewClosure()
        $result = Invoke-CodexRevision -Repository 'owner/repo' -IssueNumber 42 -Actor 'trusted-user' -PullRequestNumber '7' -RepositoryRoot $repoRoot -DataRoot $dataRoot -Config ([pscustomobject]@{ repository = 'owner/repo'; defaultBranch = 'master'; dataRoot = $dataRoot }) -StatePath $statePath -LockProvider { param($p) [pscustomobject]@{} } -UnlockProvider { param($h) } -GitCommandRunner $git -GitHubCommandRunner $gh -CodexProvider $codex
        $result.Status | Should Be 'pr-ready'
        (Get-Content -LiteralPath $userFile -Raw) | Should Be "user-value`n"
        ($events -join ' ') | Should Match '(?i)commit'
    }

    It 'blocks revision publication when Codex overwrites a pre-existing user edit' {
        $repoRoot = Join-Path $TestDrive 'repo'
        $dataRoot = Join-Path $TestDrive 'revision-data'
        $worktree = Join-Path $repoRoot '.worktrees\issue-42-publication'
        New-Item -ItemType Directory -Path $repoRoot,$dataRoot,$worktree -Force | Out-Null
        $userFile = Join-Path $worktree 'user.ps1'; Set-Content -LiteralPath $userFile -Value "user-value`n" -NoNewline
        $statePath = Join-Path $dataRoot 'state.json'
        $attempt = [pscustomobject]@{ issueNumber = 42; status = 'pr-ready'; attempt = 1; branch = 'codex/42-publication'; worktree = $worktree; threadId = 'old-thread'; runDirectory = $null; commit = 'old'; prUrl = 'https://example.test/pr/7'; retryCount = 0; publicationStage = 'pr-created'; lastError = $null }
        Write-CodexWorkerState -Path $statePath -State ([pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = $attempt }; deployment = $null })
        $events = [System.Collections.Generic.List[string]]::new()
        $git = { param([string[]] $Arguments)
            if ($Arguments -contains 'worktree' -and $Arguments -contains 'list') { return "worktree $worktree`nHEAD old`nbranch refs/heads/codex/42-publication`n" }
            if ($Arguments -contains 'status' -or $Arguments -contains '--name-only') { return 'user.ps1' }
            if ($Arguments -contains '--others') { return '' }
            if ($Arguments -contains 'add' -or $Arguments -contains 'commit' -or $Arguments -contains 'push') { $events.Add(($Arguments -join ' ')) | Out-Null; return '' }
            return ''
        }.GetNewClosure()
        $gh = { param([string[]] $Arguments)
            if (($Arguments -join '/') -match '/permission$') { return '{"permission":"write"}' }
            if ($Arguments -contains 'issue') { return '{"number":42,"title":"Issue","body":"body","labels":[],"comments":[]}' }
            return '{"number":7,"state":"OPEN","isDraft":true,"headRefName":"codex/42-publication","baseRefName":"master","body":"Fixes #42","comments":[],"reviews":[]}'
        }.GetNewClosure()
        $codex = { param($WorktreePath,$Issue,$Config,$RunDirectory,$Path,$Review,$Thread) Set-Content -LiteralPath $userFile -Value "codex-value`n" -NoNewline; [pscustomobject]@{ Status = 'completed'; Classification = 'completed'; ThreadId = 'new-thread'; Summary = $publishSummary } }.GetNewClosure()
        { Invoke-CodexRevision -Repository 'owner/repo' -IssueNumber 42 -Actor 'trusted-user' -PullRequestNumber '7' -RepositoryRoot $repoRoot -DataRoot $dataRoot -Config ([pscustomobject]@{ repository = 'owner/repo'; defaultBranch = 'master'; dataRoot = $dataRoot }) -StatePath $statePath -LockProvider { param($p) [pscustomobject]@{} } -UnlockProvider { param($h) } -GitCommandRunner $git -GitHubCommandRunner $gh -CodexProvider $codex } | Should Throw
        ($events -join ' ') | Should Not Match '(?i)add|commit|push|edit|comment|create'
        (Read-CodexWorkerState -Path $statePath).issues.'42'.status | Should Be 'blocked'
    }

    It 'blocks revision publication when Codex deletes a pre-existing user edit' {
        $repoRoot = Join-Path $TestDrive 'repo'
        $dataRoot = Join-Path $TestDrive 'revision-data'
        $worktree = Join-Path $repoRoot '.worktrees\issue-42-publication'
        New-Item -ItemType Directory -Path $repoRoot,$dataRoot,$worktree -Force | Out-Null
        $userFile = Join-Path $worktree 'user.ps1'; Set-Content -LiteralPath $userFile -Value "user-value`n" -NoNewline
        $statePath = Join-Path $dataRoot 'state.json'
        $attempt = [pscustomobject]@{ issueNumber = 42; status = 'pr-ready'; attempt = 1; branch = 'codex/42-publication'; worktree = $worktree; threadId = 'old-thread'; runDirectory = $null; commit = 'old'; prUrl = 'https://example.test/pr/7'; retryCount = 0; publicationStage = 'pr-created'; lastError = $null }
        Write-CodexWorkerState -Path $statePath -State ([pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = $attempt }; deployment = $null })
        $events = [System.Collections.Generic.List[string]]::new()
        $git = { param([string[]] $Arguments)
            if ($Arguments -contains 'worktree' -and $Arguments -contains 'list') { return "worktree $worktree`nHEAD old`nbranch refs/heads/codex/42-publication`n" }
            if ($Arguments -contains 'status' -or $Arguments -contains '--name-only') { return 'user.ps1' }
            if ($Arguments -contains '--others') { return '' }
            if ($Arguments -contains 'add' -or $Arguments -contains 'commit' -or $Arguments -contains 'push') { $events.Add(($Arguments -join ' ')) | Out-Null; return '' }
            return ''
        }.GetNewClosure()
        $gh = { param([string[]] $Arguments)
            if (($Arguments -join '/') -match '/permission$') { return '{"permission":"write"}' }
            if ($Arguments -contains 'issue') { return '{"number":42,"title":"Issue","body":"body","labels":[],"comments":[]}' }
            return '{"number":7,"state":"OPEN","isDraft":true,"headRefName":"codex/42-publication","baseRefName":"master","body":"Fixes #42","comments":[],"reviews":[]}'
        }.GetNewClosure()
        $codex = { param($WorktreePath,$Issue,$Config,$RunDirectory,$Path,$Review,$Thread) Remove-Item -LiteralPath $userFile; [pscustomobject]@{ Status = 'completed'; Classification = 'completed'; ThreadId = 'new-thread'; Summary = $publishSummary } }.GetNewClosure()
        { Invoke-CodexRevision -Repository 'owner/repo' -IssueNumber 42 -Actor 'trusted-user' -PullRequestNumber '7' -RepositoryRoot $repoRoot -DataRoot $dataRoot -Config ([pscustomobject]@{ repository = 'owner/repo'; defaultBranch = 'master'; dataRoot = $dataRoot }) -StatePath $statePath -LockProvider { param($p) [pscustomobject]@{} } -UnlockProvider { param($h) } -GitCommandRunner $git -GitHubCommandRunner $gh -CodexProvider $codex } | Should Throw
        ($events -join ' ') | Should Not Match '(?i)add|commit|push|edit|comment|create'
        (Read-CodexWorkerState -Path $statePath).issues.'42'.status | Should Be 'blocked'
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
