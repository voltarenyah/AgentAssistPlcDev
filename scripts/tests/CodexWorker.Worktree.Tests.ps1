Describe 'Codex worker worktrees' {
    BeforeEach {
        Import-Module (Join-Path $PSScriptRoot '..\codex-worker\CodexWorker.psd1') -Force
    }

    It 'builds a bounded issue branch name' {
        Get-CodexIssueBranchName -IssueNumber 42 -Title 'Fix TIA / Status: Name!' |
            Should Be 'codex/42-fix-tia-status-name'
    }

    It 'bounds long issue slugs to 48 characters' {
        $branch = Get-CodexIssueBranchName -IssueNumber 9 -Title ('A' * 100)
        $branch | Should Be 'codex/9-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
    }

    It 'parses registered porcelain worktrees' {
        $runner = {
            param([string[]] $Arguments)
            "worktree C:\repo`nHEAD abc123`nbranch refs/heads/codex/42-fix`n`nworktree C:\repo\.worktrees\other`nHEAD def456`nbranch refs/heads/master`n"
        }

        $records = @(Get-RegisteredWorktrees -RepositoryRoot 'C:\repo' -CommandRunner $runner)

        $records.Count | Should Be 2
        $records[0].Path | Should Be 'C:\repo'
        $records[0].Branch | Should Be 'codex/42-fix'
        $records[1].Head | Should Be 'def456'
    }

    It 'preserves successful Git progress output when the caller treats errors as terminating' {
        $runner = {
            param([string[]] $Arguments)
            $ErrorActionPreference = 'Continue'
            Write-Error 'Preparing worktree'
            return 'completed'
        }
        $previousPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Stop'
            $module = Get-Module CodexWorker
            $output = & $module {
                param($progressRunner)
                Invoke-CodexGit -RepositoryRoot 'C:\repo' -Arguments @('worktree', 'add') -CommandRunner $progressRunner
            } $runner
        } finally {
            $ErrorActionPreference = $previousPreference
        }

        $output | Should Match 'Preparing worktree'
        $output | Should Match 'completed'
    }

    It 'rejects cleanup outside the automation worktree root' {
        { Assert-PathUnderRoot -Path 'C:\repo' -Root 'C:\repo\.worktrees' } |
            Should Throw 'Path is outside the automation worktree root.'
    }

    It 'rejects sibling paths with a shared prefix' {
        { Assert-PathUnderRoot -Path 'C:\repo\.worktrees-other\x' -Root 'C:\repo\.worktrees' } |
            Should Throw 'Path is outside the automation worktree root.'
    }

    It 'reuses an existing registered issue worktree without creating one' {
        $calls = [System.Collections.Generic.List[object]]::new()
        $runner = {
            param([string[]] $Arguments)
            $calls.Add($Arguments)
            if ($Arguments -contains 'list') {
                return "worktree C:\repo\.worktrees\codex-42`nHEAD abc123`nbranch refs/heads/codex/42-fix`n"
            }
            return ''
        }.GetNewClosure()

        $result = Get-OrCreateCodexIssueWorktree -RepositoryRoot 'C:\repo' -WorktreeRoot 'C:\repo\.worktrees' -IssueNumber 42 -Title 'Fix' -CommandRunner $runner

        $result.BranchName | Should Be 'codex/42-fix'
        $result.Path | Should Be 'C:\repo\.worktrees\codex-42'
        @($calls | Where-Object { $_ -contains 'add' }).Count | Should Be 0
    }

    It 'reconstructs a missing registered worktree from its remote branch' {
        $calls = [System.Collections.Generic.List[object]]::new()
        $runner = {
            param([string[]] $Arguments)
            $calls.Add($Arguments)
            if ($Arguments -contains 'list') { return '' }
            if ($Arguments -contains 'show-ref' -and ($Arguments -contains 'refs/remotes/origin/codex/42-fix')) { return 'abc123 refs/remotes/origin/codex/42-fix' }
            return ''
        }.GetNewClosure()

        $result = Get-OrCreateCodexIssueWorktree -RepositoryRoot 'C:\repo' -WorktreeRoot 'C:\repo\.worktrees' -IssueNumber 42 -Title 'Fix' -CommandRunner $runner

        $result.BranchName | Should Be 'codex/42-fix'
        $result.Path | Should Be 'C:\repo\.worktrees\issue-42-fix'
        @($calls | Where-Object { $_ -contains 'worktree' -and $_ -contains 'add' }).Count | Should Be 1
        (@($calls | Where-Object { $_ -contains 'worktree' -and $_ -contains 'add' })[0] -contains 'origin/codex/42-fix') | Should Be $true
    }

    It 'creates a new issue worktree from origin master' {
        $calls = [System.Collections.Generic.List[object]]::new()
        $runner = {
            param([string[]] $Arguments)
            $calls.Add($Arguments)
            if ($Arguments -contains 'list') { return '' }
            if ($Arguments -contains 'show-ref') { throw 'not found' }
            return ''
        }.GetNewClosure()

        $result = Get-OrCreateCodexIssueWorktree -RepositoryRoot 'C:\repo' -WorktreeRoot 'C:\repo\.worktrees' -IssueNumber 42 -Title 'Fix' -CommandRunner $runner

        $result.BranchName | Should Be 'codex/42-fix'
        @($calls | Where-Object { $_ -contains 'worktree' -and $_ -contains 'add' }).Count | Should Be 1
        $add = @($calls | Where-Object { $_ -contains 'worktree' -and $_ -contains 'add' })[0]
        $add -contains '-b' | Should Be $true
        $add -contains 'origin/master' | Should Be $true
    }

    It 'reattaches an unregistered local issue branch without creating a branch' {
        $calls = [System.Collections.Generic.List[object]]::new()
        $runner = {
            param([string[]] $Arguments)
            $calls.Add($Arguments) | Out-Null
            if ($Arguments -contains 'list') { return '' }
            if ($Arguments -contains 'show-ref' -and ($Arguments -contains 'refs/heads/codex/42-fix')) { return 'abc123 refs/heads/codex/42-fix' }
            if ($Arguments -contains 'show-ref') { throw 'remote branch not found' }
            return ''
        }.GetNewClosure()

        $result = Get-OrCreateCodexIssueWorktree -RepositoryRoot 'C:\repo' -WorktreeRoot 'C:\repo\.worktrees' -IssueNumber 42 -Title 'Fix' -CommandRunner $runner

        $result.Created | Should Be $true
        $add = @($calls | Where-Object { $_ -contains 'worktree' -and $_ -contains 'add' })[0]
        $add -contains '-b' | Should Be $false
        $add -contains 'codex/42-fix' | Should Be $true
    }

    It 'does not use force when removing a blocked worktree' {
        $runner = { param([string[]] $Arguments) " M file.txt" }
        $blockers = @(Test-CodexWorktreeCleanup -RepositoryRoot 'C:\repo' -WorktreeRoot 'C:\repo\.worktrees' -WorktreePath 'C:\repo\.worktrees\x' -BranchName 'codex/x' -CommandRunner $runner -ProcessProvider { @() })
        $blockers.Count | Should BeGreaterThan 0
        { Remove-CodexWorktree -RepositoryRoot 'C:\repo' -WorktreeRoot 'C:\repo\.worktrees' -WorktreePath 'C:\repo\.worktrees\x' -BranchName 'codex/x' -CommandRunner $runner -ProcessProvider { @() } } |
            Should Throw
    }

    It 'refuses removal when process inspection reports a failure' {
        $runner = {
            param([string[]] $Arguments)
            if ($Arguments -contains 'list') { return "worktree C:\repo\.worktrees\issue-42-fix`nHEAD abc123`nbranch refs/heads/codex/42-fix`n" }
            return ''
        }
        $failedProcessProvider = { [pscustomobject]@{ Succeeded = $false; Error = 'access denied'; CommandLine = $null } }

        { Remove-CodexWorktree -RepositoryRoot 'C:\repo' -WorktreeRoot 'C:\repo\.worktrees' -WorktreePath 'C:\repo\.worktrees\issue-42-fix' -BranchName 'codex/42-fix' -CommandRunner $runner -ProcessProvider $failedProcessProvider } |
            Should Throw
    }

    It 'prepares dependencies through injectable process boundaries and redacts the activity log' {
        $worktree = Join-Path $TestDrive 'issue-worktree'
        New-Item -ItemType Directory -Path (Join-Path $worktree 'studio') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $worktree 'agent-service') -Force | Out-Null
        $logPath = Join-Path $TestDrive 'activity.log'
        $calls = [System.Collections.Generic.List[object]]::new()
        $runner = {
            param([string] $FilePath, [string[]] $Arguments)
            $calls.Add([pscustomobject]@{ FilePath = $FilePath; Arguments = $Arguments }) | Out-Null
            [pscustomobject]@{ ExitCode = if ($Arguments -contains '-c') { 1 } else { 0 }; Output = 'token=do-not-write-this' }
        }.GetNewClosure()

        Initialize-CodexIssueWorktree -Worktree $worktree -Config ([pscustomobject]@{ bootstrapPython = 'bootstrap-python' }) -ActivityLogPath $logPath -ProcessRunner $runner | Out-Null

        @($calls | Where-Object FilePath -eq 'dotnet').Count | Should Be 1
        @($calls | Where-Object FilePath -eq 'npm.cmd').Count | Should Be 1
        @($calls | Where-Object FilePath -eq 'bootstrap-python').Count | Should Be 1
        @($calls | Where-Object FilePath -like '*Scripts\python.exe' | Where-Object { $_.Arguments -contains 'pip' }).Count | Should Be 1
        (Get-Content -Raw $logPath) | Should Not Match 'do-not-write-this'
        (Get-Content -Raw $logPath) | Should Match '\[REDACTED\]'
    }

    It 'skips npm ci for a matching real-format npm hidden lockfile' {
        $worktree = Join-Path $TestDrive 'hidden-lock-worktree'
        $studio = Join-Path $worktree 'studio'
        New-Item -ItemType Directory -Path (Join-Path $studio 'node_modules') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $worktree 'agent-service') -Force | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $studio 'package-lock.json'), '{"name":"studio","version":"1.0.0","lockfileVersion":3,"packages":{"":{"name":"studio","version":"1.0.0","dependencies":{"left-pad":"1.3.0"}},"node_modules/left-pad":{"version":"1.3.0"}}}')
        [System.IO.File]::WriteAllText((Join-Path $studio 'node_modules\.package-lock.json'), '{"name":"studio","version":"1.0.0","lockfileVersion":3,"packages":{"node_modules/left-pad":{"version":"1.3.0"}}}')
        $calls = [System.Collections.Generic.List[object]]::new()
        $runner = {
            param([string] $FilePath, [string[]] $Arguments)
            $calls.Add([pscustomobject]@{ FilePath = $FilePath; Arguments = $Arguments }) | Out-Null
            [pscustomobject]@{ ExitCode = 0; Output = '' }
        }.GetNewClosure()

        Initialize-CodexIssueWorktree -Worktree $worktree -Config ([pscustomobject]@{ bootstrapPython = 'bootstrap-python' }) -ProcessRunner $runner | Out-Null

        @($calls | Where-Object FilePath -eq 'npm.cmd').Count | Should Be 0
    }
}
