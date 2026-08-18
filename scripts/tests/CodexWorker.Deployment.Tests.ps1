Describe 'Codex worker PR-close deployment handoff' {
    BeforeEach {
        Import-Module (Join-Path $PSScriptRoot '..\codex-worker\CodexWorker.psd1') -Force
    }

    It 'cleans up an unmerged close without creating deployment or done label' {
        $state = [pscustomobject]@{
            schemaVersion = 1
            issues = [pscustomobject]@{ '42' = [pscustomobject]@{
                issueNumber = 42; branch = 'codex/42-fix'; worktree = (Join-Path $TestDrive 'issue-42-fix'); status = 'pr-ready'; prUrl = 'https://github.com/owner/repo/pull/17'
            } }
            deployment = $null
        }
        $labels = [System.Collections.Generic.List[string]]::new()
        $cleanup = { param($RepositoryRoot, $WorktreeRoot, $WorktreePath, $BranchName, $CommandRunner, $ProcessProvider) @() }
        $github = {
            param([string[]] $Arguments)
            if (($Arguments -join ' ') -match 'closingIssuesReferences') {
                return '{"number":17,"state":"CLOSED","url":"https://github.com/owner/repo/pull/17","headRefName":"codex/42-fix","baseRefName":"master","headRepository":{"nameWithOwner":"owner/repo"},"baseRepository":{"nameWithOwner":"owner/repo"},"mergedAt":null,"mergeCommit":null,"closingIssuesReferences":[{"number":42,"repository":{"nameWithOwner":"owner/repo"}}]}'
            }
            if ($Arguments -contains '--add-label') { $labels.Add($Arguments[$Arguments.IndexOf('--add-label') + 1]) }
            return ''
        }.GetNewClosure()
        $reader = { param($Path) $state }
        $writer = { param($Path, $Current) $state = $Current }.GetNewClosure()

        $result = Register-CodexPullRequestClosed -Repository 'owner/repo' -PullRequestNumber 17 -Merged:$false -MergeCommitSha '' -HeadBranch 'codex/42-fix' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -StateReader $reader -StateWriter $writer -GitHubCommandRunner $github -CleanupProvider $cleanup -LockProvider { 'lock' } -UnlockProvider { param($h) }

        $result.DeploymentCreated | Should Be $false
        $result.CleanedUp | Should Be $true
        (@($labels) -contains 'codex:done') | Should Be $false
        $state.deployment | Should Be $null
    }

    It 'records the exact full merge SHA and marks the linked issue done after verification' {
        $sha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
        $state = [pscustomobject]@{
            schemaVersion = 1
            issues = [pscustomobject]@{ '42' = [pscustomobject]@{
                issueNumber = 42; branch = 'codex/42-fix'; worktree = (Join-Path $TestDrive 'issue-42-fix'); status = 'pr-ready'; prUrl = 'https://github.com/owner/repo/pull/17'
            } }
            deployment = $null
        }
        $labels = [System.Collections.Generic.List[string]]::new()
        $github = {
            param([string[]] $Arguments)
            if (($Arguments -join ' ') -match 'closingIssuesReferences') { return '{"number":17,"state":"CLOSED","url":"https://github.com/owner/repo/pull/17","headRefName":"codex/42-fix","baseRefName":"master","headRepository":{"nameWithOwner":"owner/repo"},"baseRepository":{"nameWithOwner":"owner/repo"},"mergedAt":"2026-08-18T00:00:00Z","mergeCommit":{"oid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"closingIssuesReferences":[{"number":42,"repository":{"nameWithOwner":"owner/repo"}}]}' }
            if ($Arguments -contains '--add-label') { $labels.Add($Arguments[$Arguments.IndexOf('--add-label') + 1]) }
            return ''
        }.GetNewClosure()
        $git = {
            param([string[]] $Arguments)
            if ($Arguments -contains 'rev-parse') { return $sha }
            if ($Arguments -contains 'merge-base') { return '' }
            return ''
        }
        $reader = { param($Path) $state }
        $writer = { param($Path, $Current) $state = $Current }.GetNewClosure()

        $result = Register-CodexPullRequestClosed -Repository 'owner/repo' -PullRequestNumber 17 -Merged:$true -MergeCommitSha $sha -HeadBranch 'codex/42-fix' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -StateReader $reader -StateWriter $writer -GitHubCommandRunner $github -GitCommandRunner $git -CleanupProvider { @() } -LockProvider { 'lock' } -UnlockProvider { param($h) }

        $result.DeploymentCreated | Should Be $true
        $state.deployment.targetCommit | Should Be $sha
        $state.deployment.sourcePr | Should Be 17
        $state.deployment.status | Should Be 'pending'
        (@($labels) -contains 'codex:done') | Should Be $true
    }

    It 'coalesces a later verified origin master commit and preserves a later snooze' {
        $old = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
        $new = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
        $snooze = [DateTime]::UtcNow.AddMinutes(4).ToString('o')
        $state = [pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{}; deployment = [pscustomobject]@{ targetCommit = $old; sourcePr = 3; requestedAt = '2026-08-18T00:00:00Z'; snoozeUntil = $snooze; status = 'pending' } }
        $calls = [System.Collections.Generic.List[object]]::new()
        $git = {
            param([string[]] $Arguments)
            $calls.Add($Arguments) | Out-Null
            if ($Arguments -contains 'rev-parse') { return $new }
            if ($Arguments -contains 'merge-base') {
                $left = $Arguments[$Arguments.IndexOf('--is-ancestor') + 1]; $right = $Arguments[$Arguments.IndexOf('--is-ancestor') + 2]
                if (($left -eq $new -and $right -eq $new) -or ($left -eq $old -and $right -eq $new)) { return '' }
                throw 'not ancestor'
            }
            return ''
        }.GetNewClosure()

        $result = Register-CodexPendingDeployment -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -MergeCommitSha $new -PullRequestNumber 9 -State $state -GitCommandRunner $git -Now ([DateTime]::Parse('2026-08-18T01:00:00Z'))

        $result.targetCommit | Should Be $new
        $result.sourcePr | Should Be 9
        $result.snoozeUntil | Should Be $snooze
        @($calls | Where-Object { $_ -contains 'fetch' -and $_ -contains 'master' }).Count | Should Be 1
    }

    It 'preserves a dirty worktree and reports blockers without removing it' {
        $state = [pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = [pscustomobject]@{ issueNumber = 42; branch = 'codex/42-fix'; worktree = (Join-Path $TestDrive 'issue-42-fix'); status = 'pr-ready'; prUrl = 'https://github.com/owner/repo/pull/17' } }; deployment = $null }
        $comments = [System.Collections.Generic.List[string]]::new()
        $github = {
            param([string[]] $Arguments)
            if (($Arguments -join ' ') -match 'closingIssuesReferences') { return '{"number":17,"state":"CLOSED","url":"https://github.com/owner/repo/pull/17","headRefName":"codex/42-fix","baseRefName":"master","headRepository":{"nameWithOwner":"owner/repo"},"baseRepository":{"nameWithOwner":"owner/repo"},"mergedAt":null,"mergeCommit":null,"closingIssuesReferences":[{"number":42,"repository":{"nameWithOwner":"owner/repo"}}]}' }
            if ($Arguments -contains '--body') { $comments.Add($Arguments[$Arguments.IndexOf('--body') + 1]) }
            return ''
        }.GetNewClosure()
        $cleanup = { param($RepositoryRoot, $WorktreeRoot, $WorktreePath, $BranchName, $CommandRunner, $ProcessProvider) @('Worktree has uncommitted changes.') }
        $reader = { param($Path) $state }
        $writer = { param($Path, $Current) $state = $Current }.GetNewClosure()

        $result = Register-CodexPullRequestClosed -Repository 'owner/repo' -PullRequestNumber 17 -Merged:$false -MergeCommitSha '' -HeadBranch 'codex/42-fix' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -StateReader $reader -StateWriter $writer -GitHubCommandRunner $github -CleanupProvider $cleanup -LockProvider { 'lock' } -UnlockProvider { param($h) }

        $result.CleanedUp | Should Be $false
        (@($result.Blockers) -contains 'Worktree has uncommitted changes.') | Should Be $true
        @($comments | Where-Object { $_ -match 'uncommitted' }).Count | Should Be 1
    }

    It 'rejects a fork PR before invoking cleanup' {
        $state = [pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = [pscustomobject]@{ issueNumber = 42; branch = 'codex/42-fix'; worktree = (Join-Path $TestDrive 'issue-42-fix'); status = 'pr-ready'; prUrl = 'https://github.com/owner/repo/pull/17' } }; deployment = $null }
        $cleanupCalls = 0
        $github = { param([string[]] $Arguments) if (($Arguments -join ' ') -match 'closingIssuesReferences') { return '{"number":17,"state":"CLOSED","url":"https://github.com/owner/repo/pull/17","headRefName":"codex/42-fix","baseRefName":"master","headRepository":{"nameWithOwner":"fork/repo"},"baseRepository":{"nameWithOwner":"owner/repo"},"mergedAt":null,"mergeCommit":null,"closingIssuesReferences":[{"number":42,"repository":{"nameWithOwner":"owner/repo"}}]}' }; return '' }
        $cleanup = { $cleanupCalls++; @() }.GetNewClosure()
        $reader = { param($Path) $state }
        $threw = $false; try { Register-CodexPullRequestClosed -Repository 'owner/repo' -PullRequestNumber 17 -Merged:$false -HeadBranch 'codex/42-fix' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -StateReader $reader -GitHubCommandRunner $github -CleanupProvider $cleanup -LockProvider { 'lock' } -UnlockProvider { param($h) } } catch { $threw = $true }
        $threw | Should Be $true
        $cleanupCalls | Should Be 0
    }

    It 'persists cleared worktree cleanup state and makes duplicate close idempotent' {
        $state = [pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = [pscustomobject]@{ issueNumber = 42; branch = 'codex/42-fix'; worktree = (Join-Path $TestDrive 'issue-42-fix'); status = 'pr-ready'; prUrl = 'https://github.com/owner/repo/pull/17' } }; deployment = $null }
        $writes = 0
        $github = { param([string[]] $Arguments) if (($Arguments -join ' ') -match 'closingIssuesReferences') { return '{"number":17,"state":"CLOSED","url":"https://github.com/owner/repo/pull/17","headRefName":"codex/42-fix","baseRefName":"master","headRepository":{"nameWithOwner":"owner/repo"},"baseRepository":{"nameWithOwner":"owner/repo"},"mergedAt":null,"mergeCommit":null,"closingIssuesReferences":[{"number":42,"repository":{"nameWithOwner":"owner/repo"}}]}' }; return '' }
        $cleanup = { @() }
        $reader = { param($Path) $state }
        $writer = { param($Path, $Current) $writes++; $state = $Current }.GetNewClosure()
        $first = Register-CodexPullRequestClosed -Repository 'owner/repo' -PullRequestNumber 17 -Merged:$false -HeadBranch 'codex/42-fix' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -StateReader $reader -StateWriter $writer -GitHubCommandRunner $github -CleanupProvider $cleanup -LockProvider { 'lock' } -UnlockProvider { param($h) }
        $state.issues.'42'.worktree | Should BeNullOrEmpty
        $state.issues.'42'.cleanupStatus | Should Be 'completed'
        $second = Register-CodexPullRequestClosed -Repository 'owner/repo' -PullRequestNumber 17 -Merged:$false -HeadBranch 'codex/42-fix' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -StateReader $reader -StateWriter $writer -GitHubCommandRunner $github -CleanupProvider { throw 'duplicate must not invoke cleanup' } -LockProvider { 'lock' } -UnlockProvider { param($h) }
        $second.CleanedUp | Should Be $true
        $second.Blockers.Count | Should Be 0
    }

    It 'retains the existing deployment tuple when an older close arrives' {
        $candidate = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'; $old = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'; $master = 'cccccccccccccccccccccccccccccccccccccccc'
        $state = [pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{}; deployment = [pscustomobject]@{ targetCommit = $old; sourcePr = 3; requestedAt = '2026-08-18T00:00:00Z'; snoozeUntil = '2026-08-18T03:00:00Z'; status = 'pending' } }
        $before = $state.deployment | ConvertTo-Json -Depth 5
        $git = {
            param([string[]] $Arguments)
            if ($Arguments -contains 'rev-parse') { return $master }
            if ($Arguments -contains 'merge-base') {
                $left = $Arguments[$Arguments.IndexOf('--is-ancestor') + 1]; $right = $Arguments[$Arguments.IndexOf('--is-ancestor') + 2]
                if (($left -eq $candidate -and $right -eq $master) -or ($left -eq $old -and $right -eq $master) -or ($left -eq $candidate -and $right -eq $old)) { return '' }
                throw 'not ancestor'
            }
            return ''
        }
        $result = Register-CodexPendingDeployment -RepositoryRoot 'C:\repo' -MergeCommitSha $candidate -PullRequestNumber 9 -State $state -GitCommandRunner $git -Now ([DateTime]::Parse('2026-08-18T01:00:00Z'))
        ($result | ConvertTo-Json -Depth 5) | Should Be ($state.deployment | ConvertTo-Json -Depth 5)
        ($state.deployment | ConvertTo-Json -Depth 5) | Should Be $before
    }

    It 'replaces the tuple only when the existing target is an ancestor of the incoming merge' {
        $old = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'; $incoming = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'; $master = 'cccccccccccccccccccccccccccccccccccccccc'
        $state = [pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{}; deployment = [pscustomobject]@{ targetCommit = $old; sourcePr = 3; requestedAt = '2026-08-18T00:00:00Z'; snoozeUntil = '2026-08-18T03:00:00Z'; status = 'pending' } }
        $git = {
            param([string[]] $Arguments)
            if ($Arguments -contains 'rev-parse') { return $master }
            if ($Arguments -contains 'merge-base') {
                $left = $Arguments[$Arguments.IndexOf('--is-ancestor') + 1]; $right = $Arguments[$Arguments.IndexOf('--is-ancestor') + 2]
                if (($left -eq $incoming -and $right -eq $master) -or ($left -eq $old -and $right -eq $master) -or ($left -eq $old -and $right -eq $incoming)) { return '' }
                throw 'not ancestor'
            }
            return ''
        }
        $result = Register-CodexPendingDeployment -RepositoryRoot 'C:\repo' -MergeCommitSha $incoming -PullRequestNumber 9 -State $state -GitCommandRunner $git -Now ([DateTime]::Parse('2026-08-18T01:00:00Z'))
        $result.targetCommit | Should Be $incoming
        $result.sourcePr | Should Be 9
        $result.requestedAt | Should Be '2026-08-18T01:00:00.0000000Z'
        $result.snoozeUntil | Should Be '2026-08-18T03:00:00Z'
    }

    It 'retains the full existing tuple for an equal or newer existing target' {
        $old = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'; $incoming = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'; $master = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
        $state = [pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{}; deployment = [pscustomobject]@{ targetCommit = $old; sourcePr = 3; requestedAt = '2026-08-18T00:00:00Z'; snoozeUntil = '2026-08-18T03:00:00Z'; status = 'snoozed' } }
        $before = $state.deployment | ConvertTo-Json -Depth 5
        $git = { param([string[]] $Arguments) if ($Arguments -contains 'rev-parse') { return $master }; if ($Arguments -contains 'merge-base') { $left=$Arguments[$Arguments.IndexOf('--is-ancestor')+1]; $right=$Arguments[$Arguments.IndexOf('--is-ancestor')+2]; if ($left -eq $incoming -and $right -eq $old) { return '' }; if ($left -eq $old -and $right -eq $master) { return '' }; throw 'not ancestor' }; return '' }
        $result = Register-CodexPendingDeployment -RepositoryRoot 'C:\repo' -MergeCommitSha $incoming -PullRequestNumber 9 -State $state -GitCommandRunner $git -Now ([DateTime]::Parse('2026-08-18T01:00:00Z'))
        ($result | ConvertTo-Json -Depth 5) | Should Be ($state.deployment | ConvertTo-Json -Depth 5)
        ($state.deployment | ConvertTo-Json -Depth 5) | Should Be $before
    }

    It 'fails closed for divergent and unreachable deployment candidates without changing state' {
        $old = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'; $incoming = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'; $master = 'cccccccccccccccccccccccccccccccccccccccc'
        foreach ($mode in @('divergent', 'unreachable')) {
            $state = [pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{}; deployment = [pscustomobject]@{ targetCommit = $old; sourcePr = 3; requestedAt = '2026-08-18T00:00:00Z'; snoozeUntil = $null; status = 'pending' } }
            $before = $state.deployment | ConvertTo-Json -Depth 5
            $git = { param([string[]] $Arguments) if ($Arguments -contains 'rev-parse') { return $master }; if ($Arguments -contains 'merge-base') { if ($mode -eq 'unreachable') { throw 'not ancestor' }; throw 'divergent' }; return '' }.GetNewClosure()
            $threw = $false; try { Register-CodexPendingDeployment -RepositoryRoot 'C:\repo' -MergeCommitSha $incoming -PullRequestNumber 9 -State $state -GitCommandRunner $git } catch { $threw = $true }
            $threw | Should Be $true
            ($state.deployment | ConvertTo-Json -Depth 5) | Should Be $before
        }
    }

    It 'uses real cleanup guards for outside-root and dirty evidence' {
        $root = Join-Path $TestDrive 'repo'; $worktreeRoot = Join-Path $root '.worktrees'; $worktree = Join-Path $worktreeRoot 'issue-42-fix'
        New-Item -ItemType Directory -Path $worktree -Force | Out-Null
        $runner = { param([string[]] $Arguments) if ($Arguments -contains 'list') { return "worktree $worktree`nHEAD aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa`nbranch refs/heads/codex/42-fix`n" }; if ($Arguments -contains 'status') { return ' M tracked.txt' }; if ($Arguments -contains 'log') { return '' }; return '' }.GetNewClosure()
        $dirty = @(Test-CodexWorktreeCleanup -RepositoryRoot $root -WorktreeRoot $worktreeRoot -WorktreePath $worktree -BranchName 'codex/42-fix' -CommandRunner $runner -ProcessProvider { @() })
        (@($dirty) -contains 'Worktree has uncommitted changes.') | Should Be $true
        $outside = @(Test-CodexWorktreeCleanup -RepositoryRoot $root -WorktreeRoot $worktreeRoot -WorktreePath $root -BranchName 'codex/42-fix' -CommandRunner $runner -ProcessProvider { @() })
        (@($outside) -match 'outside') | Should Be $true
        $busy = @(Test-CodexWorktreeCleanup -RepositoryRoot $root -WorktreeRoot $worktreeRoot -WorktreePath $worktree -BranchName 'codex/42-fix' -CommandRunner $runner -ProcessProvider { [pscustomobject]@{ CommandLine = (Join-Path $worktree 'pwsh.exe') } }.GetNewClosure())
        (@($busy) -match 'active process') | Should Be $true
        $unpushedRunner = { param([string[]] $Arguments) if ($Arguments -contains 'list') { return "worktree $worktree`nHEAD aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa`nbranch refs/heads/codex/42-fix`n" }; if ($Arguments -contains 'log') { return 'abc123' }; return '' }.GetNewClosure()
        $unpushed = @(Test-CodexWorktreeCleanup -RepositoryRoot $root -WorktreeRoot $worktreeRoot -WorktreePath $worktree -BranchName 'codex/42-fix' -CommandRunner $unpushedRunner -ProcessProvider { @() })
        (@($unpushed) -match 'not present on its remote') | Should Be $true
    }

    It 'returns completed cleanup state without cleanup, comments, writes, or timestamp changes' {
        $cleanupAt = '2026-08-18T00:00:00Z'
        $state = [pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = [pscustomobject]@{ issueNumber = 42; branch = 'codex/42-fix'; worktree = $null; status = 'pr-ready'; prUrl = 'https://github.com/owner/repo/pull/17'; cleanupStatus = 'completed'; cleanupAt = $cleanupAt; cleanupBlockers = @() } }; deployment = $null }
        $writes = 0; $cleanupCalls = 0; $comments = 0
        $github = { param([string[]] $Arguments) if (($Arguments -join ' ') -match 'closingIssuesReferences') { return '{"number":17,"state":"CLOSED","url":"https://github.com/owner/repo/pull/17","headRefName":"codex/42-fix","baseRefName":"master","headRepository":{"nameWithOwner":"owner/repo"},"baseRepository":{"nameWithOwner":"owner/repo"},"mergedAt":null,"mergeCommit":null,"closingIssuesReferences":[{"number":42,"repository":{"nameWithOwner":"owner/repo"}}]}' }; if ($Arguments -contains '--body') { $comments++ }; return '' }.GetNewClosure()
        $reader = { param($Path) $state }
        $writer = { param($Path, $Current) $writes++ }.GetNewClosure()
        $cleanup = { $cleanupCalls++; @() }.GetNewClosure()
        $result = Register-CodexPullRequestClosed -Repository 'owner/repo' -PullRequestNumber 17 -Merged:$false -HeadBranch 'codex/42-fix' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -StateReader $reader -StateWriter $writer -GitHubCommandRunner $github -CleanupProvider $cleanup -LockProvider { 'lock' } -UnlockProvider { param($h) }
        $result.CleanedUp | Should Be $true
        $writes | Should Be 0
        $cleanupCalls | Should Be 0
        $comments | Should Be 0
        $state.issues.'42'.cleanupAt | Should Be $cleanupAt
    }

    It 'repairs a merged completed cleanup state when deployment registration was lost' {
        $sha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
        $cleanupAt = '2026-08-18T00:00:00Z'
        $state = [pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = [pscustomobject]@{ issueNumber = 42; branch = 'codex/42-fix'; worktree = $null; status = 'pr-ready'; prUrl = 'https://github.com/owner/repo/pull/17'; cleanupStatus = 'completed'; cleanupAt = $cleanupAt; cleanupBlockers = @() } }; deployment = $null }
        $writes = [System.Collections.Generic.List[object]]::new(); $cleanupCalls = 0; $comments = 0; $labels = [System.Collections.Generic.List[string]]::new()
        $github = { param([string[]] $Arguments) if (($Arguments -join ' ') -match 'closingIssuesReferences') { return ([pscustomobject]@{ number = 17; state = 'CLOSED'; url = 'https://github.com/owner/repo/pull/17'; headRefName = 'codex/42-fix'; baseRefName = 'master'; headRepository = @{ nameWithOwner = 'owner/repo' }; baseRepository = @{ nameWithOwner = 'owner/repo' }; mergedAt = '2026-08-18T00:00:00Z'; mergeCommit = @{ oid = $sha }; closingIssuesReferences = @(@{ number = 42; repository = @{ nameWithOwner = 'owner/repo' } }) } | ConvertTo-Json -Depth 10) }; if ($Arguments -contains '--body') { $comments++ }; if ($Arguments -contains '--add-label') { $labels.Add($Arguments[$Arguments.IndexOf('--add-label') + 1]) | Out-Null }; return '' }.GetNewClosure()
        $git = { param([string[]] $Arguments) if ($Arguments -contains 'rev-parse') { return $sha }; if ($Arguments -contains 'merge-base') { return '' }; return '' }
        $reader = { param($Path) $state }
        $writer = { param($Path, $Current) $writes.Add($Current) | Out-Null }.GetNewClosure()
        $cleanup = { $cleanupCalls++; throw 'completed cleanup must not run again' }.GetNewClosure()

        $result = Register-CodexPullRequestClosed -Repository 'owner/repo' -PullRequestNumber 17 -Merged:$true -MergeCommitSha $sha -HeadBranch 'codex/42-fix' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -StateReader $reader -StateWriter $writer -GitHubCommandRunner $github -GitCommandRunner $git -CleanupProvider $cleanup -LockProvider { 'lock' } -UnlockProvider { param($h) } -Now ([DateTime]::Parse('2026-08-18T01:00:00Z'))

        $result.DeploymentCreated | Should Be $true
        $state.deployment.targetCommit | Should Be $sha
        $state.deployment.sourcePr | Should Be 17
        $writes.Count | Should Be 1
        $cleanupCalls | Should Be 0
        $comments | Should Be 0
        $labels.Count | Should Be 1
        $state.issues.'42'.cleanupAt | Should Be $cleanupAt
    }

    It 'does nothing for a merged completed cleanup state with a verified deployment' {
        $sha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
        $cleanupAt = '2026-08-18T00:00:00Z'
        $requestedAt = '2026-08-18T00:30:00Z'
        $state = [pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = [pscustomobject]@{ issueNumber = 42; branch = 'codex/42-fix'; worktree = $null; status = 'pr-ready'; prUrl = 'https://github.com/owner/repo/pull/17'; cleanupStatus = 'completed'; cleanupAt = $cleanupAt; cleanupBlockers = @() } }; deployment = [pscustomobject]@{ targetCommit = $sha; sourcePr = 17; requestedAt = $requestedAt; snoozeUntil = '2026-08-18T03:00:00Z'; status = 'snoozed' } }
        $before = $state.deployment | ConvertTo-Json -Depth 5
        $writes = [System.Collections.Generic.List[object]]::new(); $cleanupCalls = 0; $comments = 0; $labels = [System.Collections.Generic.List[string]]::new()
        $github = { param([string[]] $Arguments) if (($Arguments -join ' ') -match 'closingIssuesReferences') { return ([pscustomobject]@{ number = 17; state = 'CLOSED'; url = 'https://github.com/owner/repo/pull/17'; headRefName = 'codex/42-fix'; baseRefName = 'master'; headRepository = @{ nameWithOwner = 'owner/repo' }; baseRepository = @{ nameWithOwner = 'owner/repo' }; mergedAt = '2026-08-18T00:00:00Z'; mergeCommit = @{ oid = $sha }; closingIssuesReferences = @(@{ number = 42; repository = @{ nameWithOwner = 'owner/repo' } }) } | ConvertTo-Json -Depth 10) }; if ($Arguments -contains '--body') { $comments++ }; if ($Arguments -contains '--add-label') { $labels.Add($Arguments[$Arguments.IndexOf('--add-label') + 1]) | Out-Null }; return '' }.GetNewClosure()
        $git = { param([string[]] $Arguments) if ($Arguments -contains 'rev-parse') { return $sha }; if ($Arguments -contains 'merge-base') { return '' }; return '' }
        $reader = { param($Path) $state }
        $writer = { param($Path, $Current) $writes.Add($Current) | Out-Null }.GetNewClosure()
        $cleanup = { $cleanupCalls++; throw 'verified duplicate must not run cleanup' }.GetNewClosure()

        $result = Register-CodexPullRequestClosed -Repository 'owner/repo' -PullRequestNumber 17 -Merged:$true -MergeCommitSha $sha -HeadBranch 'codex/42-fix' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -StateReader $reader -StateWriter $writer -GitHubCommandRunner $github -GitCommandRunner $git -CleanupProvider $cleanup -LockProvider { 'lock' } -UnlockProvider { param($h) } -Now ([DateTime]::Parse('2026-08-18T01:00:00Z'))

        $result.DeploymentCreated | Should Be $false
        ($state.deployment | ConvertTo-Json -Depth 5) | Should Be $before
        $writes.Count | Should Be 0
        $cleanupCalls | Should Be 0
        $comments | Should Be 0
        $labels.Count | Should Be 0
        $state.issues.'42'.cleanupAt | Should Be $cleanupAt
    }

    It 'repairs an equal-target deployment with the wrong source PR without repeating cleanup' {
        $sha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
        $cleanupAt = '2026-08-18T00:00:00Z'
        $requestedAt = '2026-08-18T00:30:00Z'
        $snooze = '2026-08-18T03:00:00Z'
        $now = [DateTime]::Parse('2026-08-18T01:00:00Z')
        $state = [pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = [pscustomobject]@{ issueNumber = 42; branch = 'codex/42-fix'; worktree = $null; status = 'pr-ready'; prUrl = 'https://github.com/owner/repo/pull/17'; cleanupStatus = 'completed'; cleanupAt = $cleanupAt; cleanupBlockers = @() } }; deployment = [pscustomobject]@{ targetCommit = $sha; sourcePr = 99; requestedAt = $requestedAt; snoozeUntil = $snooze; status = 'pending' } }
        $writes = [System.Collections.Generic.List[object]]::new(); $cleanupCalls = 0; $comments = 0; $labels = [System.Collections.Generic.List[string]]::new()
        $github = { param([string[]] $Arguments) if (($Arguments -join ' ') -match 'closingIssuesReferences') { return ([pscustomobject]@{ number = 17; state = 'CLOSED'; url = 'https://github.com/owner/repo/pull/17'; headRefName = 'codex/42-fix'; baseRefName = 'master'; headRepository = @{ nameWithOwner = 'owner/repo' }; baseRepository = @{ nameWithOwner = 'owner/repo' }; mergedAt = '2026-08-18T00:00:00Z'; mergeCommit = @{ oid = $sha }; closingIssuesReferences = @(@{ number = 42; repository = @{ nameWithOwner = 'owner/repo' } }) } | ConvertTo-Json -Depth 10) }; if ($Arguments -contains '--body') { $comments++ }; if ($Arguments -contains '--add-label') { $labels.Add($Arguments[$Arguments.IndexOf('--add-label') + 1]) | Out-Null }; return '' }.GetNewClosure()
        $git = { param([string[]] $Arguments) if ($Arguments -contains 'rev-parse') { return $sha }; if ($Arguments -contains 'merge-base') { return '' }; return '' }.GetNewClosure()
        $reader = { param($Path) $state }
        $writer = { param($Path, $Current) $writes.Add($Current) | Out-Null }.GetNewClosure()
        $cleanup = { $cleanupCalls++; throw 'completed cleanup must not run again' }.GetNewClosure()

        $result = Register-CodexPullRequestClosed -Repository 'owner/repo' -PullRequestNumber 17 -Merged:$true -MergeCommitSha $sha -HeadBranch 'codex/42-fix' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -StateReader $reader -StateWriter $writer -GitHubCommandRunner $github -GitCommandRunner $git -CleanupProvider $cleanup -LockProvider { 'lock' } -UnlockProvider { param($h) } -Now $now

        $result.DeploymentCreated | Should Be $true
        $state.deployment.targetCommit | Should Be $sha
        $state.deployment.sourcePr | Should Be 17
        $state.deployment.requestedAt | Should Be $now.ToUniversalTime().ToString('o')
        $state.deployment.snoozeUntil | Should Be $snooze
        $state.deployment.status | Should Be 'pending'
        $writes.Count | Should Be 1
        $cleanupCalls | Should Be 0
        $comments | Should Be 0
        $labels.Count | Should Be 1
        $state.issues.'42'.cleanupAt | Should Be $cleanupAt
    }

    It 'repairs malformed equal-target deployment metadata without preserving malformed snooze data' {
        $sha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
        $cleanupAt = '2026-08-18T00:00:00Z'
        $now = [DateTime]::Parse('2026-08-18T01:00:00Z')
        $state = [pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = [pscustomobject]@{ issueNumber = 42; branch = 'codex/42-fix'; worktree = $null; status = 'pr-ready'; prUrl = 'https://github.com/owner/repo/pull/17'; cleanupStatus = 'completed'; cleanupAt = $cleanupAt; cleanupBlockers = @() } }; deployment = [pscustomobject]@{ targetCommit = $sha; sourcePr = 0; requestedAt = ''; snoozeUntil = 'not-a-timestamp'; status = 'pending' } }
        $writes = [System.Collections.Generic.List[object]]::new(); $cleanupCalls = 0; $comments = 0; $labels = [System.Collections.Generic.List[string]]::new()
        $github = { param([string[]] $Arguments) if (($Arguments -join ' ') -match 'closingIssuesReferences') { return ([pscustomobject]@{ number = 17; state = 'CLOSED'; url = 'https://github.com/owner/repo/pull/17'; headRefName = 'codex/42-fix'; baseRefName = 'master'; headRepository = @{ nameWithOwner = 'owner/repo' }; baseRepository = @{ nameWithOwner = 'owner/repo' }; mergedAt = '2026-08-18T00:00:00Z'; mergeCommit = @{ oid = $sha }; closingIssuesReferences = @(@{ number = 42; repository = @{ nameWithOwner = 'owner/repo' } }) } | ConvertTo-Json -Depth 10) }; if ($Arguments -contains '--body') { $comments++ }; if ($Arguments -contains '--add-label') { $labels.Add($Arguments[$Arguments.IndexOf('--add-label') + 1]) | Out-Null }; return '' }.GetNewClosure()
        $git = { param([string[]] $Arguments) if ($Arguments -contains 'rev-parse') { return $sha }; if ($Arguments -contains 'merge-base') { return '' }; return '' }.GetNewClosure()
        $reader = { param($Path) $state }
        $writer = { param($Path, $Current) $writes.Add($Current) | Out-Null }.GetNewClosure()
        $cleanup = { $cleanupCalls++; throw 'completed cleanup must not run again' }.GetNewClosure()

        $result = Register-CodexPullRequestClosed -Repository 'owner/repo' -PullRequestNumber 17 -Merged:$true -MergeCommitSha $sha -HeadBranch 'codex/42-fix' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -StateReader $reader -StateWriter $writer -GitHubCommandRunner $github -GitCommandRunner $git -CleanupProvider $cleanup -LockProvider { 'lock' } -UnlockProvider { param($h) } -Now $now

        $result.DeploymentCreated | Should Be $true
        $state.deployment.targetCommit | Should Be $sha
        $state.deployment.sourcePr | Should Be 17
        $state.deployment.requestedAt | Should Be $now.ToUniversalTime().ToString('o')
        $state.deployment.snoozeUntil | Should BeNullOrEmpty
        $state.deployment.status | Should Be 'pending'
        $writes.Count | Should Be 1
        $cleanupCalls | Should Be 0
        $comments | Should Be 0
        $labels.Count | Should Be 1
        $state.issues.'42'.cleanupAt | Should Be $cleanupAt
    }

    It 'fails closed when repaired deployment persistence fails before done mutation' {
        $sha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
        $cleanupAt = '2026-08-18T00:00:00Z'
        $writes = [System.Collections.Generic.List[object]]::new(); $cleanupCalls = 0; $comments = 0; $labels = 0
        $state = [pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = [pscustomobject]@{ issueNumber = 42; branch = 'codex/42-fix'; worktree = $null; status = 'pr-ready'; prUrl = 'https://github.com/owner/repo/pull/17'; cleanupStatus = 'completed'; cleanupAt = $cleanupAt; cleanupBlockers = @() } }; deployment = $null }
        $github = { param([string[]] $Arguments) if (($Arguments -join ' ') -match 'closingIssuesReferences') { return ([pscustomobject]@{ number = 17; state = 'CLOSED'; url = 'https://github.com/owner/repo/pull/17'; headRefName = 'codex/42-fix'; baseRefName = 'master'; headRepository = @{ nameWithOwner = 'owner/repo' }; baseRepository = @{ nameWithOwner = 'owner/repo' }; mergedAt = '2026-08-18T00:00:00Z'; mergeCommit = @{ oid = $sha }; closingIssuesReferences = @(@{ number = 42; repository = @{ nameWithOwner = 'owner/repo' } }) } | ConvertTo-Json -Depth 10) }; if ($Arguments -contains '--body') { $comments++ }; if ($Arguments -contains '--add-label') { $labels++ }; return '' }.GetNewClosure()
        $git = { param([string[]] $Arguments) if ($Arguments -contains 'rev-parse') { return $sha }; if ($Arguments -contains 'merge-base') { return '' }; return '' }.GetNewClosure()
        $reader = { param($Path) $state }
        $writer = { param($Path, $Current) $writes.Add($Current) | Out-Null; throw 'simulated state persistence failure' }.GetNewClosure()
        $cleanup = { $cleanupCalls++; throw 'completed cleanup must not run again' }.GetNewClosure()

        $threw = $false
        try { Register-CodexPullRequestClosed -Repository 'owner/repo' -PullRequestNumber 17 -Merged:$true -MergeCommitSha $sha -HeadBranch 'codex/42-fix' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -StateReader $reader -StateWriter $writer -GitHubCommandRunner $github -GitCommandRunner $git -CleanupProvider $cleanup -LockProvider { 'lock' } -UnlockProvider { param($h) } } catch { $threw = $true }

        $threw | Should Be $true
        $writes.Count | Should Be 1
        $cleanupCalls | Should Be 0
        $comments | Should Be 0
        $labels | Should Be 0
        $state.issues.'42'.cleanupAt | Should Be $cleanupAt
    }

    It 'fails closed when a non-throwing deployment writer leaves the durable state stale' {
        $sha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
        $cleanupAt = '2026-08-18T00:00:00Z'
        $inMemoryState = [pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = [pscustomobject]@{ issueNumber = 42; branch = 'codex/42-fix'; worktree = $null; status = 'pr-ready'; prUrl = 'https://github.com/owner/repo/pull/17'; cleanupStatus = 'completed'; cleanupAt = $cleanupAt; cleanupBlockers = @() } }; deployment = $null }
        $durable = [pscustomobject]@{ Value = (($inMemoryState | ConvertTo-Json -Depth 20) | ConvertFrom-Json) }
        $writes = [System.Collections.Generic.List[object]]::new(); $reads = [System.Collections.Generic.List[object]]::new(); $cleanupCalls = 0; $comments = 0; $labels = 0
        $github = { param([string[]] $Arguments) if (($Arguments -join ' ') -match 'closingIssuesReferences') { return ([pscustomobject]@{ number = 17; state = 'CLOSED'; url = 'https://github.com/owner/repo/pull/17'; headRefName = 'codex/42-fix'; baseRefName = 'master'; headRepository = @{ nameWithOwner = 'owner/repo' }; baseRepository = @{ nameWithOwner = 'owner/repo' }; mergedAt = '2026-08-18T00:00:00Z'; mergeCommit = @{ oid = $sha }; closingIssuesReferences = @(@{ number = 42; repository = @{ nameWithOwner = 'owner/repo' } }) } | ConvertTo-Json -Depth 10) }; if ($Arguments -contains '--body') { $comments++ }; if ($Arguments -contains '--add-label') { $labels++ }; return '' }.GetNewClosure()
        $git = { param([string[]] $Arguments) if ($Arguments -contains 'rev-parse') { return $sha }; if ($Arguments -contains 'merge-base') { return '' }; return '' }.GetNewClosure()
        $reader = { param($Path) $reads.Add($Path) | Out-Null; return (($durable.Value | ConvertTo-Json -Depth 20) | ConvertFrom-Json) }.GetNewClosure()
        $writer = { param($Path, $Current) $writes.Add($Current) | Out-Null }.GetNewClosure()
        $cleanup = { $cleanupCalls++; throw 'completed cleanup must not run again' }.GetNewClosure()

        $threw = $false
        try { Register-CodexPullRequestClosed -Repository 'owner/repo' -PullRequestNumber 17 -Merged:$true -MergeCommitSha $sha -HeadBranch 'codex/42-fix' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -StateReader $reader -StateWriter $writer -GitHubCommandRunner $github -GitCommandRunner $git -CleanupProvider $cleanup -LockProvider { 'lock' } -UnlockProvider { param($h) } -Now ([DateTime]::Parse('2026-08-18T01:00:00Z')) } catch { $threw = $true }

        $threw | Should Be $true
        $writes.Count | Should Be 1
        $reads.Count | Should Be 2
        $durable.Value.deployment | Should Be $null
        $cleanupCalls | Should Be 0
        $comments | Should Be 0
        $labels | Should Be 0
        $inMemoryState.issues.'42'.cleanupAt | Should Be $cleanupAt
    }

    It 'fails closed when the durable deployment tuple is corrupt after a successful writer return' {
        $sha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
        $corruptSha = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
        $cleanupAt = '2026-08-18T00:00:00Z'
        $inMemoryState = [pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = [pscustomobject]@{ issueNumber = 42; branch = 'codex/42-fix'; worktree = $null; status = 'pr-ready'; prUrl = 'https://github.com/owner/repo/pull/17'; cleanupStatus = 'completed'; cleanupAt = $cleanupAt; cleanupBlockers = @() } }; deployment = $null }
        $durable = [pscustomobject]@{ Value = (($inMemoryState | ConvertTo-Json -Depth 20) | ConvertFrom-Json) }
        $writes = [System.Collections.Generic.List[object]]::new(); $reads = [System.Collections.Generic.List[object]]::new(); $cleanupCalls = 0; $comments = 0; $labels = 0
        $github = { param([string[]] $Arguments) if (($Arguments -join ' ') -match 'closingIssuesReferences') { return ([pscustomobject]@{ number = 17; state = 'CLOSED'; url = 'https://github.com/owner/repo/pull/17'; headRefName = 'codex/42-fix'; baseRefName = 'master'; headRepository = @{ nameWithOwner = 'owner/repo' }; baseRepository = @{ nameWithOwner = 'owner/repo' }; mergedAt = '2026-08-18T00:00:00Z'; mergeCommit = @{ oid = $sha }; closingIssuesReferences = @(@{ number = 42; repository = @{ nameWithOwner = 'owner/repo' } }) } | ConvertTo-Json -Depth 10) }; if ($Arguments -contains '--body') { $comments++ }; if ($Arguments -contains '--add-label') { $labels++ }; return '' }.GetNewClosure()
        $git = { param([string[]] $Arguments) if ($Arguments -contains 'rev-parse') { return $sha }; if ($Arguments -contains 'merge-base') { return '' }; return '' }.GetNewClosure()
        $reader = { param($Path) $reads.Add($Path) | Out-Null; return (($durable.Value | ConvertTo-Json -Depth 20) | ConvertFrom-Json) }.GetNewClosure()
        $writer = { param($Path, $Current) $writes.Add($Current) | Out-Null; $durable.Value = (($Current | ConvertTo-Json -Depth 20) | ConvertFrom-Json); $durable.Value.deployment.targetCommit = $corruptSha }.GetNewClosure()
        $cleanup = { $cleanupCalls++; throw 'completed cleanup must not run again' }.GetNewClosure()

        $threw = $false
        try { Register-CodexPullRequestClosed -Repository 'owner/repo' -PullRequestNumber 17 -Merged:$true -MergeCommitSha $sha -HeadBranch 'codex/42-fix' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -StateReader $reader -StateWriter $writer -GitHubCommandRunner $github -GitCommandRunner $git -CleanupProvider $cleanup -LockProvider { 'lock' } -UnlockProvider { param($h) } -Now ([DateTime]::Parse('2026-08-18T01:00:00Z')) } catch { $threw = $true }

        $threw | Should Be $true
        $writes.Count | Should Be 1
        $reads.Count | Should Be 2
        $durable.Value.deployment.targetCommit | Should Be $corruptSha
        $cleanupCalls | Should Be 0
        $comments | Should Be 0
        $labels | Should Be 0
        $inMemoryState.issues.'42'.cleanupAt | Should Be $cleanupAt
    }

    It 'marks a merged event done only after a successful durable deployment reread' {
        $sha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
        $cleanupAt = '2026-08-18T00:00:00Z'
        $inMemoryState = [pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = [pscustomobject]@{ issueNumber = 42; branch = 'codex/42-fix'; worktree = $null; status = 'pr-ready'; prUrl = 'https://github.com/owner/repo/pull/17'; cleanupStatus = 'completed'; cleanupAt = $cleanupAt; cleanupBlockers = @() } }; deployment = $null }
        $durable = [pscustomobject]@{ Value = (($inMemoryState | ConvertTo-Json -Depth 20) | ConvertFrom-Json) }
        $writes = [System.Collections.Generic.List[object]]::new(); $reads = [System.Collections.Generic.List[object]]::new(); $cleanupCalls = 0; $comments = 0; $labels = [System.Collections.Generic.List[string]]::new()
        $github = { param([string[]] $Arguments) if (($Arguments -join ' ') -match 'closingIssuesReferences') { return ([pscustomobject]@{ number = 17; state = 'CLOSED'; url = 'https://github.com/owner/repo/pull/17'; headRefName = 'codex/42-fix'; baseRefName = 'master'; headRepository = @{ nameWithOwner = 'owner/repo' }; baseRepository = @{ nameWithOwner = 'owner/repo' }; mergedAt = '2026-08-18T00:00:00Z'; mergeCommit = @{ oid = $sha }; closingIssuesReferences = @(@{ number = 42; repository = @{ nameWithOwner = 'owner/repo' } }) } | ConvertTo-Json -Depth 10) }; if ($Arguments -contains '--body') { $comments++ }; if ($Arguments -contains '--add-label') { $labels.Add($Arguments[$Arguments.IndexOf('--add-label') + 1]) | Out-Null }; return '' }.GetNewClosure()
        $git = { param([string[]] $Arguments) if ($Arguments -contains 'rev-parse') { return $sha }; if ($Arguments -contains 'merge-base') { return '' }; return '' }.GetNewClosure()
        $reader = { param($Path) $reads.Add($Path) | Out-Null; return (($durable.Value | ConvertTo-Json -Depth 20) | ConvertFrom-Json) }.GetNewClosure()
        $writer = { param($Path, $Current) $writes.Add($Current) | Out-Null; $durable.Value = (($Current | ConvertTo-Json -Depth 20) | ConvertFrom-Json) }.GetNewClosure()
        $cleanup = { $cleanupCalls++; throw 'completed cleanup must not run again' }.GetNewClosure()

        $result = Register-CodexPullRequestClosed -Repository 'owner/repo' -PullRequestNumber 17 -Merged:$true -MergeCommitSha $sha -HeadBranch 'codex/42-fix' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -StateReader $reader -StateWriter $writer -GitHubCommandRunner $github -GitCommandRunner $git -CleanupProvider $cleanup -LockProvider { 'lock' } -UnlockProvider { param($h) } -Now ([DateTime]::Parse('2026-08-18T01:00:00Z'))

        $result.DeploymentCreated | Should Be $true
        $writes.Count | Should Be 1
        $reads.Count | Should Be 2
        $durable.Value.deployment.targetCommit | Should Be $sha
        $durable.Value.deployment.sourcePr | Should Be 17
        $labels.Count | Should Be 1
        $comments | Should Be 0
        $cleanupCalls | Should Be 0
        $inMemoryState.issues.'42'.cleanupAt | Should Be $cleanupAt
    }

    It 'fails before mutation for every invalid trusted close context' {
        $sha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'; $cases = @(
            @{ Name = 'saved URL'; Change = { param($p) $p.url = 'https://github.com/owner/repo/pull/18' } },
            @{ Name = 'PR number'; Change = { param($p) $p.number = 18 } },
            @{ Name = 'fork'; Change = { param($p) $p.headRepository.nameWithOwner = 'fork/repo' } },
            @{ Name = 'base'; Change = { param($p) $p.baseRefName = 'develop' } },
            @{ Name = 'head'; Change = { param($p) $p.headRefName = 'other-branch' } },
            @{ Name = 'state'; Change = { param($p) $p.state = 'OPEN' } },
            @{ Name = 'merged flag'; Change = { param($p) $p.mergedAt = '2026-08-18T00:00:00Z'; $p.mergeCommit = @{ oid = $sha } } },
            @{ Name = 'merged SHA'; Change = { param($p) $p.mergedAt = '2026-08-18T00:00:00Z'; $p.mergeCommit = @{ oid = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' } } },
            @{ Name = 'linked issue'; Change = { param($p) $p.closingIssuesReferences[0].number = 43 } },
            @{ Name = 'empty event branch'; Change = { param($p) } ; EventBranch = '' },
            @{ Name = 'empty saved branch'; Change = { param($p) } ; SavedBranch = '' },
            @{ Name = 'empty context branch'; Change = { param($p) $p.headRefName = '' } },
            @{ Name = 'unreachable merge'; Change = { param($p) $p.mergedAt = '2026-08-18T00:00:00Z'; $p.mergeCommit = @{ oid = $sha } } ; Unreachable = $true }
        )
        foreach ($case in $cases) {
            $savedBranch = if ($case.SavedBranch -eq '') { '' } else { 'codex/42-fix' }
            $state = [pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = [pscustomobject]@{ issueNumber = 42; branch = $savedBranch; worktree = (Join-Path $TestDrive 'issue-42-fix'); status = 'pr-ready'; prUrl = 'https://github.com/owner/repo/pull/17' } }; deployment = $null }
            $payload = [pscustomobject]@{ number = 17; state = 'CLOSED'; url = 'https://github.com/owner/repo/pull/17'; headRefName = 'codex/42-fix'; baseRefName = 'master'; headRepository = [pscustomobject]@{ nameWithOwner = 'owner/repo' }; baseRepository = [pscustomobject]@{ nameWithOwner = 'owner/repo' }; mergedAt = $null; mergeCommit = $null; closingIssuesReferences = @([pscustomobject]@{ number = 42; repository = [pscustomobject]@{ nameWithOwner = 'owner/repo' } }) }
            & $case.Change $payload
            $cleanupCalls = 0; $writes = 0; $labels = 0
            $github = { param([string[]] $Arguments) if (($Arguments -join ' ') -match '--json') { return ($payload | ConvertTo-Json -Depth 10) }; if ($Arguments -contains '--add-label') { $labels++ }; return '' }.GetNewClosure()
            $git = { param([string[]] $Arguments) if ($Arguments -contains 'rev-parse') { return $sha }; if ($Arguments -contains 'merge-base' -and $case.Unreachable) { throw 'not reachable' }; return '' }.GetNewClosure()
            $reader = { param($Path) $state }
            $writer = { param($Path, $Current) $writes++ }.GetNewClosure()
            $cleanup = { $cleanupCalls++; @() }.GetNewClosure()
            $merged = $case.Name -in @('merged SHA', 'unreachable merge')
            $eventSha = if ($merged) { $sha } else { '' }
            $eventBranch = if ($null -ne $case.EventBranch) { $case.EventBranch } else { 'codex/42-fix' }
            $threw = $false; try { Register-CodexPullRequestClosed -Repository 'owner/repo' -PullRequestNumber 17 -Merged:$merged -MergeCommitSha $eventSha -HeadBranch $eventBranch -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -StateReader $reader -StateWriter $writer -GitHubCommandRunner $github -GitCommandRunner $git -CleanupProvider $cleanup -LockProvider { 'lock' } -UnlockProvider { param($h) } } catch { $threw = $true }
            if (-not $threw) { throw "Case did not fail closed: $($case.Name)" }
            $cleanupCalls | Should Be 0
            $writes | Should Be 0
            $labels | Should Be 0
            $state.deployment | Should Be $null
        }
    }
}

Describe 'Codex runtime deployment' {
    BeforeEach {
        Import-Module (Join-Path $PSScriptRoot '..\codex-worker\CodexWorker.psd1') -Force
    }

    function New-RuntimeFixture {
        $root = Join-Path $TestDrive 'repo'
        $data = Join-Path $TestDrive 'data'
        New-Item -ItemType Directory -Path $root, $data -Force | Out-Null
        $sha = 'a' * 40
        $masterSha = 'b' * 40
        $state = [pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{}; activeSlot = 'runtime-a'; deployment = [pscustomobject]@{ targetCommit = $sha; sourcePr = 17; requestedAt = '2026-08-18T00:00:00Z'; status = 'pending' } }
        $events = [System.Collections.Generic.List[string]]::new()
        $durable = [pscustomobject]@{ Value = (($state | ConvertTo-Json -Depth 20) | ConvertFrom-Json) }
        $config = [pscustomobject]@{ runtimeSlots = @('runtime-a','runtime-b'); healthTimeoutSeconds = 3; bootstrapPython = 'python.exe'; tiaWhitelistPath = ''; repositoryRoot = $root }
        $git = {
            param([string[]] $Arguments)
            $events.Add("git:$($Arguments -join ' ')") | Out-Null
            if ($Arguments -contains 'rev-parse' -and $Arguments -contains 'origin/master^{commit}') { return $masterSha }
            if ($Arguments -contains 'rev-parse' -and $Arguments -contains 'HEAD') { return $sha }
            return ''
        }.GetNewClosure()
        $process = {
            param([string] $FilePath, [string[]] $Arguments)
            $events.Add("run:$FilePath $($Arguments -join ' ')") | Out-Null
            return [pscustomobject]@{ ExitCode = 0; Output = ''; ProcessId = 77; CommandLine = "$FilePath $($Arguments -join ' ')" }
        }.GetNewClosure()
        $http = {
            param([string] $Uri)
            $events.Add("http:$Uri") | Out-Null
            if ($Uri -match '8787') { return [pscustomobject]@{ StatusCode = 200; Body = '{"status":"ok","model":"fallback","fallback":true}' } }
            return [pscustomobject]@{ StatusCode = 200; Body = '{}' }
        }.GetNewClosure()
        $reader = { param([string] $Path) return (($durable.Value | ConvertTo-Json -Depth 20) | ConvertFrom-Json) }.GetNewClosure()
        $writer = { param([string] $Path, [object] $Value) $durable.Value = (($Value | ConvertTo-Json -Depth 20) | ConvertFrom-Json) }.GetNewClosure()
        return [pscustomobject]@{ Root = $root; Data = $data; Sha = $sha; MasterSha = $masterSha; State = $state; Durable = $durable; Config = $config; Events = $events; Git = $git; Process = $process; Http = $http; Reader = $reader; Writer = $writer }
    }

    It 'selects only the inactive durable slot and prepares the exact target before switching' {
        $f = New-RuntimeFixture
        $result = Invoke-CodexDeployment -RepositoryRoot $f.Root -DataRoot $f.Data -Config $f.Config -Deployment $f.State.deployment -StateReader $f.Reader -StateWriter $f.Writer -GitCommandRunner $f.Git -ProcessRunner $f.Process -HttpRunner $f.Http -ProcessProvider { @() } -PathInspector { param($Path) [pscustomobject]@{ IsReparsePoint = $false } }
        $result.Success | Should Be $true
        $f.Durable.Value.activeSlot | Should Be 'runtime-b'
        ($f.Events -join "`n") | Should Match 'runtime-b'
        ($f.Events -join "`n") | Should Not Match 'runtime-a.*worktree remove'
        $f.Durable.Value.lastDeployment.targetCommit | Should Be $f.Sha
    }

    It 'runs restore, build, npm, venv, pip, whitelist in strict order before launcher and probes all endpoints' {
        $f = New-RuntimeFixture
        $f.Config.tiaWhitelistPath = Join-Path $f.Root 'register-whitelist.reg'
        [IO.File]::WriteAllText($f.Config.tiaWhitelistPath, 'REGEDIT4')
        $result = Invoke-CodexDeployment -RepositoryRoot $f.Root -DataRoot $f.Data -Config $f.Config -Deployment $f.State.deployment -StateReader $f.Reader -StateWriter $f.Writer -GitCommandRunner $f.Git -ProcessRunner $f.Process -HttpRunner $f.Http -RegistryRunner $f.Process -ProcessProvider { @() } -PathInspector { param($Path) [pscustomobject]@{ IsReparsePoint = $false } }
        $result.Success | Should Be $true
        $runEvents = @($f.Events | Where-Object { $_ -like 'run:*' -or $_ -like 'http:*' })
        ($runEvents -join "`n") | Should Match 'dotnet restore'
        $runEvents.IndexOf(($runEvents | Where-Object { $_ -match 'dotnet restore' } | Select-Object -First 1)) | Should BeLessThan $runEvents.IndexOf(($runEvents | Where-Object { $_ -match 'dotnet build' } | Select-Object -First 1))
        $runEvents.IndexOf(($runEvents | Where-Object { $_ -match 'dotnet build' } | Select-Object -First 1)) | Should BeLessThan $runEvents.IndexOf(($runEvents | Where-Object { $_ -match 'npm\.cmd ci' } | Select-Object -First 1))
        $runEvents.IndexOf(($runEvents | Where-Object { $_ -match 'npm\.cmd run build' } | Select-Object -First 1)) | Should BeLessThan $runEvents.IndexOf(($runEvents | Where-Object { $_ -match 'powershell\.exe' } | Select-Object -First 1))
        @($f.Events | Where-Object { $_ -like 'http:*' }).Count | Should Be 3
    }

    It 'rejects a busy inactive slot before removing it' {
        $f = New-RuntimeFixture
        $busy = { [pscustomobject]@{ CommandLine = (Join-Path $f.Root '.worktrees\runtime-b') } }.GetNewClosure()
        { Invoke-CodexDeployment -RepositoryRoot $f.Root -DataRoot $f.Data -Config $f.Config -Deployment $f.State.deployment -StateReader $f.Reader -StateWriter $f.Writer -GitCommandRunner $f.Git -ProcessRunner $f.Process -HttpRunner $f.Http -ProcessProvider $busy -PathInspector { param($Path) [pscustomobject]@{ IsReparsePoint = $false } } } | Should Throw 'process is using'
        @($f.Events | Where-Object { $_ -match 'worktree remove' }).Count | Should Be 0
    }

    It 'keeps the previous active slot and records rollback evidence when health fails' {
        $f = New-RuntimeFixture
        $phase = @{ value = 0 }
        $f.Process = { param([string] $FilePath, [string[]] $Arguments) if ($FilePath -eq 'powershell.exe') { $phase.value++ }; return [pscustomobject]@{ ExitCode = 0; Output = ''; ProcessId = 77; CommandLine = "$FilePath $($Arguments -join ' ')" } }.GetNewClosure()
        $f.Http = { param([string] $Uri) if ($phase.value -eq 1) { return [pscustomobject]@{ StatusCode = 500; Body = '' } }; if ($Uri -match '8787') { return [pscustomobject]@{ StatusCode = 200; Body = '{"status":"ok"}' } }; return [pscustomobject]@{ StatusCode = 200; Body = '{}' } }.GetNewClosure()
        $result = Invoke-CodexDeployment -RepositoryRoot $f.Root -DataRoot $f.Data -Config $f.Config -Deployment $f.State.deployment -StateReader $f.Reader -StateWriter $f.Writer -GitCommandRunner $f.Git -ProcessRunner $f.Process -HttpRunner $f.Http -ProcessProvider { @() } -PathInspector { param($Path) [pscustomobject]@{ IsReparsePoint = $false } }
        $result.Success | Should Be $false
        $result.RollbackSucceeded | Should Be $true
        $f.Durable.Value.activeSlot | Should Be 'runtime-a'
        $f.Durable.Value.deployment.status | Should Be 'failed'
        $f.Durable.Value.deployment.evidence.rollback | Should Not Be $null
    }

    It 'marks rollback-failed and emits a high-priority issue comment when rollback probes fail' {
        $f = New-RuntimeFixture
        $comments = [System.Collections.Generic.List[string]]::new()
        $f.Http = { param([string] $Uri) return [pscustomobject]@{ StatusCode = 503; Body = '' } }
        $github = { param([string[]] $Arguments) if ($Arguments -contains '--body') { $comments.Add($Arguments[$Arguments.IndexOf('--body') + 1]) | Out-Null }; return '' }.GetNewClosure()
        $result = Invoke-CodexDeployment -RepositoryRoot $f.Root -DataRoot $f.Data -Config $f.Config -Deployment $f.State.deployment -StateReader $f.Reader -StateWriter $f.Writer -GitCommandRunner $f.Git -ProcessRunner $f.Process -HttpRunner $f.Http -GitHubCommandRunner $github -ProcessProvider { @() } -PathInspector { param($Path) [pscustomobject]@{ IsReparsePoint = $false } }
        $result.Success | Should Be $false
        $result.RollbackSucceeded | Should Be $false
        $f.Durable.Value.deployment.status | Should Be 'rollback-failed'
        $comments.Count | Should Be 1
        $comments[0] | Should Match '(?i)priority|rollback'
    }

    It 'fails closed on preparation failure without changing active slot' {
        $f = New-RuntimeFixture
        $f.Process = { param([string] $FilePath, [string[]] $Arguments) $f.Events.Add("run:$FilePath $($Arguments -join ' ')") | Out-Null; if ($Arguments -contains 'build') { return [pscustomobject]@{ ExitCode = 1; Output = 'compile failed' } }; return [pscustomobject]@{ ExitCode = 0; Output = '' } }.GetNewClosure()
        $result = Invoke-CodexDeployment -RepositoryRoot $f.Root -DataRoot $f.Data -Config $f.Config -Deployment $f.State.deployment -StateReader $f.Reader -StateWriter $f.Writer -GitCommandRunner $f.Git -ProcessRunner $f.Process -HttpRunner $f.Http -ProcessProvider { @() } -PathInspector { param($Path) [pscustomobject]@{ IsReparsePoint = $false } }
        $result.Success | Should Be $false
        $f.Durable.Value.activeSlot | Should Be 'runtime-a'
        $f.Durable.Value.deployment.status | Should Be 'failed'
        @($f.Events | Where-Object { $_ -like 'http:*' }).Count | Should Be 0
    }

    It 'records the actual sidecar model contract without inventing fallback fields' {
        $f = New-RuntimeFixture
        $f.Http = { param([string] $Uri)
            if ($Uri -match '8787') { return [pscustomobject]@{ StatusCode = 200; Body = '{"status":"ok","modelConfigured":false,"modelMode":"deterministic-fallback"}' } }
            return [pscustomobject]@{ StatusCode = 200; Body = '{}' }
        }.GetNewClosure()
        $result = Invoke-CodexDeployment -RepositoryRoot $f.Root -DataRoot $f.Data -Config $f.Config -Deployment $f.State.deployment -StateReader $f.Reader -StateWriter $f.Writer -GitCommandRunner $f.Git -ProcessRunner $f.Process -HttpRunner $f.Http -ProcessProvider { @() } -PathInspector { param($Path) [pscustomobject]@{ IsReparsePoint = $false } }
        $sidecar = $result.Evidence.health.'http://localhost:8787/health'
        $sidecar.modelConfigured | Should Be $false
        $sidecar.modelMode | Should Be 'deterministic-fallback'
        $sidecar.fallback | Should Be $true
        $sidecar.status | Should Be 'ok'
    }

    It 'fails closed on an uninspectable runtime path before any destructive git command' {
        $f = New-RuntimeFixture
        $inspected = [System.Collections.Generic.List[string]]::new()
        $gitCalls = [System.Collections.Generic.List[string]]::new()
        $f.Git = { param([string[]] $Arguments) $gitCalls.Add(($Arguments -join ' ')) | Out-Null; return $f.Sha }.GetNewClosure()
        $inspector = { param([string] $Path) $inspected.Add($Path) | Out-Null; throw 'ACL inspection denied' }.GetNewClosure()
        { Invoke-CodexDeployment -RepositoryRoot $f.Root -DataRoot $f.Data -Config $f.Config -Deployment $f.State.deployment -StateReader $f.Reader -StateWriter $f.Writer -GitCommandRunner $f.Git -ProcessRunner $f.Process -HttpRunner $f.Http -ProcessProvider { @() } -PathInspector $inspector } | Should Throw 'ACL inspection denied'
        @($gitCalls | Where-Object { $_ -match 'worktree remove|worktree add' }).Count | Should Be 0
    }

    It 'preserves stdout stderr and process identity for launcher evidence' {
        $f = New-RuntimeFixture
        $f.Process = { param([string] $FilePath, [string[]] $Arguments, [string] $WorkingDirectory)
            [pscustomobject]@{ ExitCode = 0; StdOut = 'launcher out'; StdErr = 'launcher err'; ProcessId = 9021; CommandLine = "$FilePath $($Arguments -join ' ')" }
        }.GetNewClosure()
        $providerState = @{ Calls = 0 }
        $processProvider = { $providerState.Calls++; if ($providerState.Calls -lt 4) { return @() }; return @([pscustomobject]@{ ProcessId = 9021; CommandLine = (Join-Path $f.Root '.worktrees\runtime-b\launch.ps1') }) }.GetNewClosure()
        $result = Invoke-CodexDeployment -RepositoryRoot $f.Root -DataRoot $f.Data -Config $f.Config -Deployment $f.State.deployment -StateReader $f.Reader -StateWriter $f.Writer -GitCommandRunner $f.Git -ProcessRunner $f.Process -HttpRunner $f.Http -ProcessProvider $processProvider -PathInspector { param($Path) [pscustomobject]@{ IsReparsePoint = $false } }
        $result.Evidence.launch.stdout | Should Be 'launcher out'
        $result.Evidence.launch.stderr | Should Be 'launcher err'
        @($result.Evidence.processes).Count | Should BeGreaterThan 0
        $result.Evidence.processes[0].processId | Should Be 9021
    }

    It 'compensates a partially persisted activation and verifies the previous active slot durably' {
        $f = New-RuntimeFixture
        $writeState = @{ Count = 0 }
        $f.Writer = { param([string] $Path, [object] $Value)
            $writeState.Count++
            if ($writeState.Count -eq 1) {
                $f.Durable.Value = (($Value | ConvertTo-Json -Depth 30) | ConvertFrom-Json)
                throw 'simulated partial activation write'
            }
            $f.Durable.Value = (($Value | ConvertTo-Json -Depth 30) | ConvertFrom-Json)
        }.GetNewClosure()
        $result = Invoke-CodexDeployment -RepositoryRoot $f.Root -DataRoot $f.Data -Config $f.Config -Deployment $f.State.deployment -StateReader $f.Reader -StateWriter $f.Writer -GitCommandRunner $f.Git -ProcessRunner $f.Process -HttpRunner $f.Http -ProcessProvider { @() } -PathInspector { param($Path) [pscustomobject]@{ IsReparsePoint = $false } }
        $result.Success | Should Be $false
        $result.Evidence.activationCompensation.verified | Should Be $true
        $f.Durable.Value.activeSlot | Should Be 'runtime-a'
        $f.Durable.Value.deployment.status | Should Be 'failed'
    }

    It 'marks rollback failed when durable activation compensation cannot be verified' {
        $f = New-RuntimeFixture
        $f.Writer = { param([string] $Path, [object] $Value) throw 'state writer unavailable' }.GetNewClosure()
        $githubCalls = [System.Collections.Generic.List[string]]::new()
        $github = { param([string[]] $Arguments) if ($Arguments -contains '--body') { $githubCalls.Add($Arguments[$Arguments.IndexOf('--body') + 1]) | Out-Null }; return '' }.GetNewClosure()
        $result = Invoke-CodexDeployment -RepositoryRoot $f.Root -DataRoot $f.Data -Config $f.Config -Deployment $f.State.deployment -StateReader $f.Reader -StateWriter $f.Writer -GitCommandRunner $f.Git -ProcessRunner $f.Process -HttpRunner $f.Http -GitHubCommandRunner $github -ProcessProvider { @() } -PathInspector { param($Path) [pscustomobject]@{ IsReparsePoint = $false } }
        $result.Success | Should Be $false
        $result.Evidence.activationCompensation.verified | Should Be $false
        $result.State.deployment.status | Should Be 'rollback-failed'
        $githubCalls.Count | Should Be 1
    }

    It 'rejects invalid durable slots and mismatched exact targets before git mutation' {
        $f = New-RuntimeFixture
        $f.Durable.Value.activeSlot = 'runtime-c'
        { Invoke-CodexDeployment -RepositoryRoot $f.Root -DataRoot $f.Data -Config $f.Config -Deployment $f.State.deployment -StateReader $f.Reader -StateWriter $f.Writer -GitCommandRunner $f.Git -ProcessRunner $f.Process -HttpRunner $f.Http -ProcessProvider { @() } -PathInspector { param($Path) [pscustomobject]@{ IsReparsePoint = $false } } } | Should Throw 'not configured'
        $f = New-RuntimeFixture
        $f.State.deployment.targetCommit = 'c' * 40
        { Invoke-CodexDeployment -RepositoryRoot $f.Root -DataRoot $f.Data -Config $f.Config -Deployment $f.State.deployment -StateReader $f.Reader -StateWriter $f.Writer -GitCommandRunner $f.Git -ProcessRunner $f.Process -HttpRunner $f.Http -ProcessProvider { @() } -PathInspector { param($Path) [pscustomobject]@{ IsReparsePoint = $false } } } | Should Throw 'does not match'
        @($f.Events | Where-Object { $_ -match 'worktree remove|worktree add' }).Count | Should Be 0
    }

    It 'fails closed on a process inspection error before destructive recreation' {
        $f = New-RuntimeFixture
        $gitCalls = [System.Collections.Generic.List[string]]::new()
        $git = { param([string[]] $Arguments) $gitCalls.Add(($Arguments -join ' ')) | Out-Null; return $f.Sha }.GetNewClosure()
        $processError = { [pscustomobject]@{ Succeeded = $false; Error = 'WMI unavailable' } }
        { Invoke-CodexDeployment -RepositoryRoot $f.Root -DataRoot $f.Data -Config $f.Config -Deployment $f.State.deployment -StateReader $f.Reader -StateWriter $f.Writer -GitCommandRunner $git -ProcessRunner $f.Process -HttpRunner $f.Http -ProcessProvider $processError -PathInspector { param($Path) [pscustomobject]@{ IsReparsePoint = $false } } } | Should Throw 'Unable to inspect active processes'
        @($gitCalls | Where-Object { $_ -match 'worktree remove|worktree add' }).Count | Should Be 0
    }

    It 'does not launch or activate when the optional whitelist import fails' {
        $f = New-RuntimeFixture
        $f.Config.tiaWhitelistPath = Join-Path $f.Root 'register-whitelist.reg'
        [IO.File]::WriteAllText($f.Config.tiaWhitelistPath, 'REGEDIT4')
        $registry = { param([string] $FilePath, [string[]] $Arguments, [string] $WorkingDirectory) [pscustomobject]@{ ExitCode = 5; StdOut = 'registry out'; StdErr = 'registry denied'; ProcessId = 44 } }
        $result = Invoke-CodexDeployment -RepositoryRoot $f.Root -DataRoot $f.Data -Config $f.Config -Deployment $f.State.deployment -StateReader $f.Reader -StateWriter $f.Writer -GitCommandRunner $f.Git -ProcessRunner $f.Process -HttpRunner $f.Http -RegistryRunner $registry -ProcessProvider { @() } -PathInspector { param($Path) [pscustomobject]@{ IsReparsePoint = $false } }
        $result.Success | Should Be $false
        $f.Durable.Value.activeSlot | Should Be 'runtime-a'
        @($f.Events | Where-Object { $_ -match 'powershell.exe' }).Count | Should Be 0
        $result.Evidence.error | Should Match 'reg.exe'
    }

    It 'returns truthful rollback-failed evidence when authoritative rereads throw' {
        $f = New-RuntimeFixture
        $readerState = @{ Calls = 0 }
        $reader = { param([string] $Path)
            $readerState.Calls++ | Out-Null
            if ($readerState.Calls -le 2) { return (($f.Durable.Value | ConvertTo-Json -Depth 30) | ConvertFrom-Json) }
            throw 'state file became unreadable'
        }.GetNewClosure()
        $writeState = @{ Calls = 0 }
        $writer = { param([string] $Path, [object] $Value)
            $writeState.Calls++ | Out-Null
            $f.Durable.Value = (($Value | ConvertTo-Json -Depth 30) | ConvertFrom-Json)
            throw 'partial write then throw'
        }.GetNewClosure()
        $comments = [System.Collections.Generic.List[string]]::new()
        $github = { param([string[]] $Arguments) if ($Arguments -contains '--body') { $comments.Add($Arguments[$Arguments.IndexOf('--body') + 1]) | Out-Null }; return '' }.GetNewClosure()
        $result = Invoke-CodexDeployment -RepositoryRoot $f.Root -DataRoot $f.Data -Config $f.Config -Deployment $f.State.deployment -StateReader $reader -StateWriter $writer -GitCommandRunner $f.Git -ProcessRunner $f.Process -HttpRunner $f.Http -GitHubCommandRunner $github -ProcessProvider { @() } -PathInspector { param($Path) [pscustomobject]@{ IsReparsePoint = $false } }
        $result.Success | Should Be $false
        $result.Evidence.activationCompensation.verified | Should Be $false
        $result.Evidence.stateReadError | Should Match 'unreadable'
        $result.State.deployment.status | Should Be 'rollback-failed'
        $comments.Count | Should Be 1
        @($result.Evidence.logs).Count | Should Be 2
    }

    It 'rejects a reparse swap at the final add boundary without invoking add' {
        $f = New-RuntimeFixture
        $worktreeRoot = Join-Path $f.Root '.worktrees'
        New-Item -ItemType Directory -Path (Join-Path $worktreeRoot 'runtime-a'), (Join-Path $worktreeRoot 'runtime-b') -Force | Out-Null
        $phase = @{ Value = 'initial'; ProcessCalls = 0; AddCalls = 0 }
        $git = { param([string[]] $Arguments)
            if ($Arguments -contains 'worktree' -and $Arguments -contains 'add') { $phase.AddCalls++ }
            return if ($Arguments -contains 'rev-parse' -and $Arguments -contains 'origin/master^{commit}') { $f.MasterSha } elseif ($Arguments -contains 'rev-parse' -and $Arguments -contains 'HEAD') { $f.Sha } else { '' }
        }.GetNewClosure()
        $inspector = { param([string] $Path)
            if ($phase.Value -eq 'swap' -and $Path -like '*runtime-b') { return [pscustomobject]@{ IsReparsePoint = $true } }
            [pscustomobject]@{ IsReparsePoint = $false }
        }.GetNewClosure()
        $process = { $phase.ProcessCalls++; if ($phase.ProcessCalls -ge 3) { $phase.Value = 'swap' }; @() }.GetNewClosure()
        $result = Invoke-CodexDeployment -RepositoryRoot $f.Root -DataRoot $f.Data -Config $f.Config -Deployment $f.State.deployment -StateReader $f.Reader -StateWriter $f.Writer -GitCommandRunner $git -ProcessRunner $f.Process -HttpRunner $f.Http -ProcessProvider $process -PathInspector $inspector
        $result.Success | Should Be $false
        $phase.AddCalls | Should Be 0
    }

    It 'records a nonzero candidate launcher and keeps health uncalled' {
        $f = New-RuntimeFixture
        $httpCalls = [System.Collections.Generic.List[string]]::new()
        $f.Process = { param([string] $FilePath, [string[]] $Arguments, [string] $WorkingDirectory)
            if ($FilePath -eq 'powershell.exe') { return [pscustomobject]@{ ExitCode = 17; StdOut = 'launch out'; StdErr = 'launch err'; ProcessId = 701; CommandLine = "$FilePath $($Arguments -join ' ')" } }
            [pscustomobject]@{ ExitCode = 0; StdOut = 'prep out'; StdErr = 'prep err'; ProcessId = 702; CommandLine = "$FilePath $($Arguments -join ' ')" }
        }.GetNewClosure()
        $f.Http = { param([string] $Uri) $httpCalls.Add($Uri) | Out-Null; [pscustomobject]@{ StatusCode = 200; Body = '{}' } }.GetNewClosure()
        $result = Invoke-CodexDeployment -RepositoryRoot $f.Root -DataRoot $f.Data -Config $f.Config -Deployment $f.State.deployment -StateReader $f.Reader -StateWriter $f.Writer -GitCommandRunner $f.Git -ProcessRunner $f.Process -HttpRunner $f.Http -ProcessProvider { @() } -PathInspector { param($Path) [pscustomobject]@{ IsReparsePoint = $false } }
        $result.Success | Should Be $false
        $result.Evidence.launch.exitCode | Should Be 17
        $result.Evidence.launch.stderr | Should Be 'launch err'
        $httpCalls.Count | Should Be 0
    }

    It 'rejects an unreachable exact target before removing the inactive slot' {
        $f = New-RuntimeFixture
        $target = 'c' * 40
        $f.Durable.Value.deployment.targetCommit = $target
        $gitCalls = [System.Collections.Generic.List[string]]::new()
        $git = { param([string[]] $Arguments)
            $gitCalls.Add(($Arguments -join ' ')) | Out-Null
            if ($Arguments -contains 'rev-parse' -and $Arguments -contains 'origin/master^{commit}') { return $f.MasterSha }
            if ($Arguments -contains 'merge-base') { throw 'not reachable' }
            return $f.Sha
        }.GetNewClosure()
        $result = Invoke-CodexDeployment -RepositoryRoot $f.Root -DataRoot $f.Data -Config $f.Config -Deployment $f.Durable.Value.deployment -StateReader $f.Reader -StateWriter $f.Writer -GitCommandRunner $git -ProcessRunner $f.Process -HttpRunner $f.Http -ProcessProvider { @() } -PathInspector { param($Path) [pscustomobject]@{ IsReparsePoint = $false } }
        $result.Success | Should Be $false
        @($gitCalls | Where-Object { $_ -match 'worktree remove|worktree add' }).Count | Should Be 0
    }

    It 'rejects a recreated slot whose detached HEAD differs from the requested SHA' {
        $f = New-RuntimeFixture
        $head = 'd' * 40
        $git = { param([string[]] $Arguments)
            if ($Arguments -contains 'rev-parse' -and $Arguments -contains 'origin/master^{commit}') { return $f.MasterSha }
            if ($Arguments -contains 'rev-parse' -and $Arguments -contains 'HEAD') { return $head }
            return ''
        }.GetNewClosure()
        $result = Invoke-CodexDeployment -RepositoryRoot $f.Root -DataRoot $f.Data -Config $f.Config -Deployment $f.State.deployment -StateReader $f.Reader -StateWriter $f.Writer -GitCommandRunner $git -ProcessRunner $f.Process -HttpRunner $f.Http -ProcessProvider { @() } -PathInspector { param($Path) [pscustomobject]@{ IsReparsePoint = $false } }
        $result.Success | Should Be $false
        @($f.Events | Where-Object { $_ -match 'powershell.exe|dotnet restore' }).Count | Should Be 0
    }

    It 'marks activation failure when the state writer is a no-op and reread stays pending' {
        $f = New-RuntimeFixture
        $result = Invoke-CodexDeployment -RepositoryRoot $f.Root -DataRoot $f.Data -Config $f.Config -Deployment $f.State.deployment -StateReader $f.Reader -StateWriter { param($Path, $Value) } -GitCommandRunner $f.Git -ProcessRunner $f.Process -HttpRunner $f.Http -ProcessProvider { @() } -PathInspector { param($Path) [pscustomobject]@{ IsReparsePoint = $false } }
        $result.Success | Should Be $false
        $result.Evidence.failureStateUnverified | Should Be $true
        $result.State.deployment.status | Should Be 'rollback-failed'
    }
}
