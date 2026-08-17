Describe 'Codex worker paths' {
    BeforeEach {
        Import-Module (Join-Path $PSScriptRoot '..\codex-worker\CodexWorker.psd1') -Force
    }

    It 'keeps durable state outside the repository' {
        $paths = Resolve-CodexWorkerPaths -RepositoryRoot 'C:\repo' -DataRoot (Join-Path $TestDrive 'worker')
        $paths.RepositoryRoot | Should Be 'C:\repo'
        $paths.WorktreeRoot | Should Be 'C:\repo\.worktrees'
        $paths.StatePath | Should Be (Join-Path $TestDrive 'worker\state.json')
        $paths.RunRoot | Should Be (Join-Path $TestDrive 'worker\runs')
    }

    It 'rejects a relative repository root' {
        { Resolve-CodexWorkerPaths -RepositoryRoot '.\repo' -DataRoot (Join-Path $TestDrive 'worker') } |
            Should Throw 'RepositoryRoot must be absolute.'
    }

    It 'returns a schema-version-1 default for missing state' {
        $path = Join-Path $TestDrive 'missing-state.json'

        $state = Read-CodexWorkerState -Path $path

        $state.schemaVersion | Should Be 1
        $state.issues.Count | Should Be 0
        $state.deployment | Should Be $null
    }

    It 'round trips durable state atomically' {
        $path = Join-Path $TestDrive 'state.json'
        Write-CodexWorkerState -Path $path -State ([pscustomobject]@{
            schemaVersion = 1
            issues = @{}
            deployment = $null
        })

        (Read-CodexWorkerState -Path $path).schemaVersion | Should Be 1
        Test-Path "$path.tmp" | Should Be $false
    }

    It 'quarantines corrupt state without overwriting evidence' {
        $path = Join-Path $TestDrive 'state.json'
        [System.IO.File]::WriteAllText($path, '{not-json')

        $state = Read-CodexWorkerState -Path $path

        $state.schemaVersion | Should Be 1
        Test-Path $path | Should Be $false
        $quarantine = Get-ChildItem $TestDrive -Filter 'state.corrupt.*.json'
        $quarantine.Count | Should Be 1
        $quarantine[0].Name | Should Match '^state\.corrupt\.\d{8}T\d{6}Z(?:-\d+)?\.json$'
        (Get-Content -Raw $quarantine[0].FullName) | Should Be '{not-json'
    }

    It 'allows only one lock holder' {
        $lockPath = Join-Path $TestDrive 'worker.lock'
        $first = Enter-CodexWorkerLock -Path $lockPath -TimeoutSeconds 1
        try {
            { Enter-CodexWorkerLock -Path $lockPath -TimeoutSeconds 0 } | Should Throw 'Worker lock is busy.'
        } finally {
            Exit-CodexWorkerLock -Handle $first
        }
    }

    It 'exports the state and lock APIs' {
        $module = Get-Module CodexWorker
        foreach ($name in @(
                'Resolve-CodexWorkerPaths',
                'Read-CodexWorkerState',
                'Write-CodexWorkerState',
                'Enter-CodexWorkerLock',
                'Exit-CodexWorkerLock')) {
            $module.ExportedFunctions.ContainsKey($name) | Should Be $true
        }
    }

    It 'exports the issue orchestration boundary' {
        $module = Get-Module CodexWorker
        $module.ExportedFunctions.ContainsKey('Invoke-CodexIssueRun') | Should Be $true
    }

    It 'persists a queued issue through running to pr-ready with bounded milestones' {
        $statePath = Join-Path $TestDrive 'lifecycle-state.json'
        $events = New-Object 'System.Collections.Generic.List[string]'
        $issueJson = ([ordered]@{
                number = 42; title = 'Fix the station'; body = 'Issue body';
                author = @{ login = 'reporter' }; comments = @(); labels = @(@{ name = 'codex' }, @{ name = 'codex:queued' });
                state = 'OPEN'; url = 'https://github.com/owner/repo/issues/42'
            } | ConvertTo-Json -Depth 10)
        $github = {
            param([string[]] $Arguments)
            if (@($Arguments | Where-Object { $_ -match 'permission' }).Count -gt 0) { return '{"permission":"write"}' }
            if (($Arguments -contains 'view') -and ($Arguments -contains 'issue')) { return $issueJson }
            if (($Arguments -contains 'develop') -and ($Arguments -contains '--list')) { return '' }
            if (($Arguments -contains 'pr') -and ($Arguments -contains 'list')) { return '[]' }
            if ($Arguments -contains '--add-label') {
                $events.Add('status:' + $Arguments[$Arguments.IndexOf('--add-label') + 1]) | Out-Null
                return ''
            }
            if ($Arguments -contains '--body') {
                $events.Add('comment:' + $Arguments[$Arguments.IndexOf('--body') + 1]) | Out-Null
                return ''
            }
            throw ('Unexpected GitHub call: ' + ($Arguments -join ' '))
        }.GetNewClosure()
        $worktree = {
            param([string] $RepositoryRoot, [string] $WorktreeRoot, [int] $IssueNumber, [string] $Title, [string] $BranchName, [string] $DefaultBranch, [scriptblock] $CommandRunner)
            $events.Add('worktree') | Out-Null
            return [pscustomobject]@{ Path = (Join-Path $TestDrive 'issue-42-fix'); BranchName = 'codex/42-fix'; Created = $true; Reused = $false }
        }.GetNewClosure()
        $setup = { param($Worktree, $Config, $ActivityLogPath) $events.Add('setup') | Out-Null }.GetNewClosure()
        $codex = {
            param($IssueWorktree, $IssueContext, $Config, $RunDirectory, $StatePath)
            $events.Add('codex') | Out-Null
            [pscustomobject]@{
                Status = 'completed'; Classification = 'completed'; ThreadId = 'thread-42'; RunDirectory = $RunDirectory; Commit = 'abc123'
                Summary = [pscustomobject]@{ status = 'completed'; rootCauseOrApproach = 'Use the existing adapter.'; changedComponents = @('adapter'); decisions = @('reuse'); validation = @([pscustomobject]@{ command = 'test'; outcome = 'passed'; details = 'pass' }); warnings = @(); remainingRisks = @(); commitMessage = 'fix: adapter'; prTitle = 'fix: adapter'; requiresHumanInput = $false; humanQuestion = $null }
            }
        }.GetNewClosure()

        $result = Invoke-CodexIssueRun -Repository 'owner/repo' -IssueNumber 42 -Actor 'trusted-user' -EventName 'labeled' `
            -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -Config ([pscustomobject]@{ defaultBranch = 'master'; codexTimeoutMinutes = 1 }) `
            -StatePath $statePath -GitHubCommandRunner $github -WorktreeProvider $worktree -SetupProvider $setup -CodexProvider $codex

        $result.Status | Should Be 'pr-ready'
        $saved = Read-CodexWorkerState -Path $statePath
        $saved.issues.'42'.status | Should Be 'pr-ready'
        $saved.issues.'42'.attempt | Should Be 1
        $saved.issues.'42'.threadId | Should Be 'thread-42'
        $saved.issues.'42'.commit | Should Be 'abc123'
        @($events | Where-Object { $_ -eq 'worktree' }).Count | Should Be 1
        @($events | Where-Object { $_ -eq 'codex' }).Count | Should Be 1
        @($events | Where-Object { $_ -like 'comment:*' }).Count | Should Be 4
        @($events | Where-Object { $_ -eq 'comment:' -or $_ -like 'comment:*claimed*' }).Count | Should BeGreaterThan 0
    }

    It 'turns a human-input summary into a blocked durable state' {
        $statePath = Join-Path $TestDrive 'human-state.json'
        $events = New-Object 'System.Collections.Generic.List[string]'
        $github = {
            param([string[]] $Arguments)
            if (@($Arguments | Where-Object { $_ -match 'permission' }).Count -gt 0) { return '{"permission":"write"}' }
            if (($Arguments -contains 'view') -and ($Arguments -contains 'issue')) { return '{"number":42,"title":"Need a decision","body":"body","author":{"login":"reporter"},"comments":[],"labels":[{"name":"codex"}],"state":"OPEN","url":"https://github.com/owner/repo/issues/42"}' }
            if (($Arguments -contains 'develop') -and ($Arguments -contains '--list')) { return '' }
            if (($Arguments -contains 'pr') -and ($Arguments -contains 'list')) { return '[]' }
            if ($Arguments -contains '--add-label') { $events.Add('status:' + $Arguments[$Arguments.IndexOf('--add-label') + 1]) | Out-Null; return '' }
            if ($Arguments -contains '--body') { $events.Add($Arguments[$Arguments.IndexOf('--body') + 1]) | Out-Null; return '' }
            throw 'Unexpected GitHub call.'
        }.GetNewClosure()
        $worktree = { param($RepositoryRoot, $WorktreeRoot, $IssueNumber, $Title, $BranchName, $DefaultBranch, $CommandRunner) [pscustomobject]@{ Path = (Join-Path $TestDrive 'issue'); BranchName = 'codex/42-need-a-decision' } }.GetNewClosure()
        $setup = { param($Worktree, $Config, $ActivityLogPath) }.GetNewClosure()
        $codex = { param($IssueWorktree, $IssueContext, $Config, $RunDirectory, $StatePath) [pscustomobject]@{ Status = 'blocked'; Classification = 'completed'; ThreadId = 'thread-blocked'; RunDirectory = $RunDirectory; Summary = [pscustomobject]@{ status = 'completed'; rootCauseOrApproach = 'Need a product choice.'; changedComponents = @(); decisions = @(); validation = @(); warnings = @(); remainingRisks = @(); commitMessage = 'fix: choice'; prTitle = 'fix: choice'; requiresHumanInput = $true; humanQuestion = 'Which baseline should be used?' } } }.GetNewClosure()

        $result = Invoke-CodexIssueRun -Repository 'owner/repo' -IssueNumber 42 -Actor 'trusted-user' -EventName 'labeled' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive `
            -Config ([pscustomobject]@{ defaultBranch = 'master' }) -StatePath $statePath -GitHubCommandRunner $github -WorktreeProvider $worktree -SetupProvider $setup -CodexProvider $codex

        $result.Status | Should Be 'blocked'
        (Read-CodexWorkerState -Path $statePath).issues.'42'.lastError | Should Match 'Which baseline'
        @($events | Where-Object { $_ -like '*Which baseline*' }).Count | Should Be 1
    }

    It 'retries one transient Codex failure and blocks after the second' {
        $statePath = Join-Path $TestDrive 'retry-state.json'
        $statuses = New-Object 'System.Collections.Generic.List[string]'
        $attempts = New-Object 'System.Collections.Generic.List[int]'
        $github = {
            param([string[]] $Arguments)
            if (@($Arguments | Where-Object { $_ -match 'permission' }).Count -gt 0) { return '{"permission":"write"}' }
            if (($Arguments -contains 'view') -and ($Arguments -contains 'issue')) { return '{"number":42,"title":"Retry me","body":"body","author":{"login":"reporter"},"comments":[],"labels":[{"name":"codex"}],"state":"OPEN","url":"https://github.com/owner/repo/issues/42"}' }
            if (($Arguments -contains 'develop') -and ($Arguments -contains '--list')) { return '' }
            if (($Arguments -contains 'pr') -and ($Arguments -contains 'list')) { return '[]' }
            if ($Arguments -contains '--add-label') { $statuses.Add($Arguments[$Arguments.IndexOf('--add-label') + 1]) | Out-Null; return '' }
            if ($Arguments -contains '--body') { return '' }
            throw 'Unexpected GitHub call.'
        }.GetNewClosure()
        $worktree = { param($RepositoryRoot, $WorktreeRoot, $IssueNumber, $Title, $BranchName, $DefaultBranch, $CommandRunner) [pscustomobject]@{ Path = (Join-Path $TestDrive 'issue'); BranchName = 'codex/42-retry-me' } }.GetNewClosure()
        $setup = { param($Worktree, $Config, $ActivityLogPath) }.GetNewClosure()
        $codex = {
            param($IssueWorktree, $IssueContext, $Config, $RunDirectory, $StatePath)
            $attempts.Add(1) | Out-Null
            [pscustomobject]@{ Status = 'failed'; Classification = 'transient_service_unavailable'; RunDirectory = $RunDirectory; Summary = $null; LastError = 'network unavailable' }
        }.GetNewClosure()

        $result = Invoke-CodexIssueRun -Repository 'owner/repo' -IssueNumber 42 -Actor 'trusted-user' -EventName 'labeled' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive `
            -Config ([pscustomobject]@{ defaultBranch = 'master' }) -StatePath $statePath -GitHubCommandRunner $github -WorktreeProvider $worktree -SetupProvider $setup -CodexProvider $codex

        $result.Status | Should Be 'blocked'
        $attempts.Count | Should Be 2
        $saved = Read-CodexWorkerState -Path $statePath
        $saved.issues.'42'.retryCount | Should Be 1
        $saved.issues.'42'.attempt | Should Be 2
        $saved.issues.'42'.lastError | Should Match 'network unavailable'
        @($statuses | Where-Object { $_ -eq 'codex:retry' }).Count | Should Be 1
        @($statuses | Where-Object { $_ -eq 'codex:blocked' }).Count | Should Be 1
    }

    It 'dry-runs issue context without writing state or mutating a worktree' {
        $statePath = Join-Path $TestDrive 'dry-run-state.json'
        $calls = New-Object 'System.Collections.Generic.List[object]'
        $github = {
            param([string[]] $Arguments)
            $calls.Add(@($Arguments)) | Out-Null
            if (@($Arguments | Where-Object { $_ -match 'permission' }).Count -gt 0) { return '{"permission":"write"}' }
            if (($Arguments -contains 'view') -and ($Arguments -contains 'issue')) { return '{"number":42,"title":"Plan me","body":"body","author":{"login":"reporter"},"comments":[],"labels":[{"name":"codex"}],"state":"OPEN","url":"https://github.com/owner/repo/issues/42"}' }
            if (($Arguments -contains 'develop') -and ($Arguments -contains '--list')) { return '' }
            if (($Arguments -contains 'pr') -and ($Arguments -contains 'list')) { return '[]' }
            throw 'Dry-run attempted a GitHub mutation.'
        }.GetNewClosure()
        $worktree = { throw 'Dry-run attempted worktree creation.' }
        $codex = { throw 'Dry-run attempted Codex execution.' }

        $result = Invoke-CodexIssueRun -Repository 'owner/repo' -IssueNumber 42 -Actor 'trusted-user' -EventName 'labeled' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive `
            -Config ([pscustomobject]@{ defaultBranch = 'master' }) -StatePath $statePath -DryRun -GitHubCommandRunner $github -WorktreeProvider $worktree -CodexProvider $codex

        $result.DryRun | Should Be $true
        $result.IssueNumber | Should Be 42
        Test-Path -LiteralPath $statePath | Should Be $false
        @($calls | Where-Object { $_ -contains 'edit' -or $_ -contains 'comment' }).Count | Should Be 0
    }
}
