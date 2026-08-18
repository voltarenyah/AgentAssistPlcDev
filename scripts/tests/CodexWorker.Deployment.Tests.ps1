Describe 'Codex worker PR-close deployment handoff' {
    BeforeEach {
        Import-Module (Join-Path $PSScriptRoot '..\codex-worker\CodexWorker.psd1') -Force
    }

    It 'cleans up an unmerged close without creating deployment or done label' {
        $state = [pscustomobject]@{
            schemaVersion = 1
            issues = [pscustomobject]@{ '42' = [pscustomobject]@{
                issueNumber = 42; branch = 'codex/42-fix'; worktree = (Join-Path $TestDrive 'issue-42-fix'); status = 'pr-ready'
            } }
            deployment = $null
        }
        $labels = [System.Collections.Generic.List[string]]::new()
        $cleanup = { param($RepositoryRoot, $WorktreeRoot, $WorktreePath, $BranchName, $CommandRunner, $ProcessProvider) @() }
        $github = {
            param([string[]] $Arguments)
            if (($Arguments -join ' ') -match 'closingIssuesReferences') {
                return '{"number":17,"closingIssuesReferences":[{"number":42,"repository":{"nameWithOwner":"owner/repo"}}]}'
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
                issueNumber = 42; branch = 'codex/42-fix'; worktree = (Join-Path $TestDrive 'issue-42-fix'); status = 'pr-ready'
            } }
            deployment = $null
        }
        $labels = [System.Collections.Generic.List[string]]::new()
        $github = {
            param([string[]] $Arguments)
            if (($Arguments -join ' ') -match 'closingIssuesReferences') { return '{"number":17,"closingIssuesReferences":[{"number":42,"repository":{"nameWithOwner":"owner/repo"}}]}' }
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
        $state = [pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = [pscustomobject]@{ issueNumber = 42; branch = 'codex/42-fix'; worktree = (Join-Path $TestDrive 'issue-42-fix'); status = 'pr-ready' } }; deployment = $null }
        $comments = [System.Collections.Generic.List[string]]::new()
        $github = {
            param([string[]] $Arguments)
            if (($Arguments -join ' ') -match 'closingIssuesReferences') { return '{"number":17,"closingIssuesReferences":[{"number":42,"repository":{"nameWithOwner":"owner/repo"}}]}' }
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
}
