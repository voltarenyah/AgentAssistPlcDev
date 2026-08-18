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
        $old = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'; $candidate = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'; $master = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
        $state = [pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{}; deployment = [pscustomobject]@{ targetCommit = $old; sourcePr = 3; requestedAt = '2026-08-18T00:00:00Z'; snoozeUntil = '2026-08-18T03:00:00Z'; status = 'pending' } }
        $git = { param([string[]] $Arguments) if ($Arguments -contains 'rev-parse') { return $master }; if ($Arguments -contains 'merge-base' -and $Arguments[-2] -eq $old -and $Arguments[-1] -eq $master) { return '' }; return '' }
        $result = Register-CodexPendingDeployment -RepositoryRoot 'C:\repo' -MergeCommitSha $candidate -PullRequestNumber 9 -State $state -GitCommandRunner $git -Now ([DateTime]::Parse('2026-08-18T01:00:00Z'))
        $result.targetCommit | Should Be $old
        $result.sourcePr | Should Be 3
        $result.requestedAt | Should Be '2026-08-18T00:00:00Z'
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
}
