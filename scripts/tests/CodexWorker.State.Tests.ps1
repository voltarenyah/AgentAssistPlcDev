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

    It 'persists explicit Kimi provider identity while legacy attempts default to Codex' {
        $kimiAttempt = New-CodexIssueAttemptState -IssueNumber 71 -AgentProvider 'Kimi'
        $legacyAttempt = New-CodexIssueAttemptState -IssueNumber 72

        $kimiAttempt.agentProvider | Should Be 'Kimi'
        $legacyAttempt.agentProvider | Should Be 'Codex'
    }

    It 'persists a queued issue through running to pr-ready with bounded milestones' {
        $statePath = Join-Path $TestDrive 'lifecycle-state.json'
        $events = New-Object 'System.Collections.Generic.List[string]'
        $saveCounter = @{ Value = 0 }
        $stateWriter = {
            param($Path, $Number, $Attempt)
            Write-CodexIssueAttemptState -Path $Path -IssueNumber $Number -AttemptState $Attempt | Out-Null
            $saveCounter.Value++
            $snapshot = (Read-CodexWorkerState -Path $Path).issues."$Number"
            $events.Add(('save#{0}:status={1};attempt={2};retry={3};run={4};publication={5}' -f $saveCounter.Value, $snapshot.status, $snapshot.attempt, $snapshot.retryCount, $snapshot.runDirectory, $snapshot.publicationStage)) | Out-Null
            $events.Add('save:' + $snapshot.status) | Out-Null
        }.GetNewClosure()
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
                $statusLabel = $Arguments[$Arguments.IndexOf('--add-label') + 1]
                $events.Add('status:' + $statusLabel) | Out-Null
                if ($statusLabel -eq 'codex:pr-ready') { $events.Add('label:codex:pr-ready') | Out-Null }
                return ''
            }
            if ($Arguments -contains '--body') {
                $body = $Arguments[$Arguments.IndexOf('--body') + 1]
                if ($body -like 'Codex work claimed.*') { $events.Add('claimed-comment') | Out-Null }
                elseif ($body -like 'Codex approach established.*') { $events.Add('approach-comment') | Out-Null }
                elseif ($body -like 'Codex validation result.*') { $events.Add('validation-comment') | Out-Null }
                elseif ($body -like 'Codex implementation is ready for publication.*') { $events.Add('ready-comment') | Out-Null }
                else { $events.Add('comment:' + $body) | Out-Null }
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
            -StatePath $statePath -StateWriter $stateWriter -GitHubCommandRunner $github -WorktreeProvider $worktree -SetupProvider $setup -CodexProvider $codex

        $result.Status | Should Be 'pr-ready'
        $saved = Read-CodexWorkerState -Path $statePath
        $saved.issues.'42'.status | Should Be 'pr-ready'
        $saved.issues.'42'.issueNumber | Should Be 42
        $saved.issues.'42'.attempt | Should Be 1
        $saved.issues.'42'.branch | Should Be 'codex/42-fix'
        $saved.issues.'42'.worktree | Should Be (Join-Path $TestDrive 'issue-42-fix')
        $saved.issues.'42'.threadId | Should Be 'thread-42'
        $saved.issues.'42'.runDirectory | Should Be (Join-Path $TestDrive 'runs\issue-42\1')
        $saved.issues.'42'.commit | Should Be 'abc123'
        $saved.issues.'42'.prUrl | Should BeNullOrEmpty
        $saved.issues.'42'.retryCount | Should Be 0
        $saved.issues.'42'.publicationStage | Should Be 'ready'
        $saved.issues.'42'.lastError | Should BeNullOrEmpty
        foreach ($field in @('issueNumber', 'status', 'attempt', 'branch', 'worktree', 'threadId', 'runDirectory', 'commit', 'prUrl', 'retryCount', 'publicationStage', 'lastError')) {
            $saved.issues.'42'.PSObject.Properties.Name -contains $field | Should Be $true
        }
        @($events | Where-Object { $_ -eq 'worktree' }).Count | Should Be 1
        @($events | Where-Object { $_ -eq 'codex' }).Count | Should Be 1
        $events.IndexOf('save:queued') | Should BeLessThan $events.IndexOf('status:codex:queued')
        $events.IndexOf('save:running') | Should BeLessThan $events.IndexOf('codex')
        $events.IndexOf('save:running') | Should BeLessThan $events.IndexOf('status:codex:running')
        $events.IndexOf('save:pr-ready') | Should BeLessThan $events.IndexOf('ready-comment')
        $expectedMilestoneSnapshots = @{
            'claimed-comment' = 'status=running;attempt=1;retry=0;run=;publication=none'
            'approach-comment' = ('status=running;attempt=1;retry=0;run={0};publication=none' -f (Join-Path $TestDrive 'runs\issue-42\1'))
            'validation-comment' = ('status=running;attempt=1;retry=0;run={0};publication=none' -f (Join-Path $TestDrive 'runs\issue-42\1'))
            'ready-comment' = ('status=pr-ready;attempt=1;retry=0;run={0};publication=ready' -f (Join-Path $TestDrive 'runs\issue-42\1'))
        }
        foreach ($milestone in @('claimed-comment', 'approach-comment', 'validation-comment', 'ready-comment')) {
            $milestoneIndex = $events.IndexOf($milestone)
            $milestoneIndex | Should BeGreaterThan -1
            ($events[$milestoneIndex + 1] -replace '^save#\d+:', '') | Should Be $expectedMilestoneSnapshots[$milestone]
        }
        $readyLabelIndex = $events.IndexOf('label:codex:pr-ready')
        $readyCommentIndex = $events.IndexOf('ready-comment')
        $readyLabelIndex | Should BeLessThan $readyCommentIndex
        ($events[$readyLabelIndex + 1] -replace '^save#\d+:', '') | Should Be $expectedMilestoneSnapshots['ready-comment']
        ($events[$readyCommentIndex + 1] -replace '^save#\d+:', '') | Should Be $expectedMilestoneSnapshots['ready-comment']
        @($events | Where-Object { $_ -eq 'save:pr-ready' }).Count | Should BeGreaterThan 0
        @($events | Where-Object { $_ -in @('claimed-comment', 'approach-comment', 'validation-comment', 'ready-comment') }).Count | Should Be 4
        @($events | Where-Object { $_ -eq 'claimed-comment' }).Count | Should BeGreaterThan 0
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
            if ($Arguments -contains '--add-label') {
                $label = $Arguments[$Arguments.IndexOf('--add-label') + 1]
                $events.Add('status:' + $label) | Out-Null
                if ($label -eq 'codex:blocked') { $events.Add('label:codex:blocked') | Out-Null }
                return ''
            }
            if ($Arguments -contains '--body') {
                $body = $Arguments[$Arguments.IndexOf('--body') + 1]
                $events.Add($body) | Out-Null
                if ($body -like '*Which baseline*') { $events.Add('blocked-comment') | Out-Null }
                return ''
            }
            throw 'Unexpected GitHub call.'
        }.GetNewClosure()
        $worktree = { param($RepositoryRoot, $WorktreeRoot, $IssueNumber, $Title, $BranchName, $DefaultBranch, $CommandRunner) [pscustomobject]@{ Path = (Join-Path $TestDrive 'issue'); BranchName = 'codex/42-need-a-decision' } }.GetNewClosure()
        $setup = { param($Worktree, $Config, $ActivityLogPath) }.GetNewClosure()
        $saveCounter = @{ Value = 0 }
        $stateWriter = {
            param($Path, $Number, $Attempt)
            Write-CodexIssueAttemptState -Path $Path -IssueNumber $Number -AttemptState $Attempt | Out-Null
            $snapshot = (Read-CodexWorkerState -Path $Path).issues."$Number"
            $saveCounter.Value++
            $events.Add(('save#{0}:status={1};attempt={2};retry={3};run={4};publication={5}' -f $saveCounter.Value, $snapshot.status, $snapshot.attempt, $snapshot.retryCount, $snapshot.runDirectory, $snapshot.publicationStage)) | Out-Null
        }.GetNewClosure()
        $codex = { param($IssueWorktree, $IssueContext, $Config, $RunDirectory, $StatePath) [pscustomobject]@{ Status = 'blocked'; Classification = 'completed'; ThreadId = 'thread-blocked'; RunDirectory = $RunDirectory; Summary = [pscustomobject]@{ status = 'completed'; rootCauseOrApproach = 'Need a product choice.'; changedComponents = @(); decisions = @(); validation = @(); warnings = @(); remainingRisks = @(); commitMessage = 'fix: choice'; prTitle = 'fix: choice'; requiresHumanInput = $true; humanQuestion = 'Which baseline should be used?' } } }.GetNewClosure()

        $result = Invoke-CodexIssueRun -Repository 'owner/repo' -IssueNumber 42 -Actor 'trusted-user' -EventName 'labeled' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive `
            -Config ([pscustomobject]@{ defaultBranch = 'master' }) -StatePath $statePath -StateWriter $stateWriter -GitHubCommandRunner $github -WorktreeProvider $worktree -SetupProvider $setup -CodexProvider $codex

        $result.Status | Should Be 'blocked'
        (Read-CodexWorkerState -Path $statePath).issues.'42'.lastError | Should Match 'Which baseline'
        @($events | Where-Object { $_ -like '*Which baseline*' }).Count | Should Be 1
        $blockedLabelIndex = $events.IndexOf('label:codex:blocked')
        $blockedCommentIndex = $events.IndexOf('blocked-comment')
        $blockedLabelIndex | Should BeGreaterThan -1
        $blockedCommentIndex | Should BeGreaterThan $blockedLabelIndex
        $expectedBlockedSnapshot = 'status=blocked;attempt=1;retry=0;run={0};publication=none' -f (Join-Path $TestDrive 'runs\issue-42\1')
        ($events[$blockedLabelIndex + 1] -replace '^save#\d+:', '') | Should Be $expectedBlockedSnapshot
        ($events[$blockedCommentIndex + 1] -replace '^save#\d+:', '') | Should Be $expectedBlockedSnapshot
        $events[$blockedLabelIndex + 1] | Should Not Be $events[$blockedCommentIndex + 1]
    }

    It 'retries one transient Codex failure and blocks after the second' {
        $statePath = Join-Path $TestDrive 'retry-state.json'
        $statuses = New-Object 'System.Collections.Generic.List[string]'
        $events = New-Object 'System.Collections.Generic.List[string]'
        $attempts = New-Object 'System.Collections.Generic.List[int]'
        $worktreeCalls = New-Object 'System.Collections.Generic.List[string]'
        $setupCalls = New-Object 'System.Collections.Generic.List[string]'
        $codexWorktreePaths = New-Object 'System.Collections.Generic.List[string]'
        $github = {
            param([string[]] $Arguments)
            if (@($Arguments | Where-Object { $_ -match 'permission' }).Count -gt 0) { return '{"permission":"write"}' }
            if (($Arguments -contains 'view') -and ($Arguments -contains 'issue')) { return '{"number":42,"title":"Retry me","body":"body","author":{"login":"reporter"},"comments":[],"labels":[{"name":"codex"}],"state":"OPEN","url":"https://github.com/owner/repo/issues/42"}' }
            if (($Arguments -contains 'develop') -and ($Arguments -contains '--list')) { return '' }
            if (($Arguments -contains 'pr') -and ($Arguments -contains 'list')) { return '[]' }
            if ($Arguments -contains '--add-label') {
                $label = $Arguments[$Arguments.IndexOf('--add-label') + 1]
                $statuses.Add($label) | Out-Null
                if ($label -in @('codex:retry', 'codex:blocked')) { $events.Add('label:' + $label) | Out-Null }
                return ''
            }
            if ($Arguments -contains '--body') {
                if ($Arguments[$Arguments.IndexOf('--body') + 1] -like 'Codex work is blocked.*') { $events.Add('blocked-comment') | Out-Null }
                return ''
            }
            throw 'Unexpected GitHub call.'
        }.GetNewClosure()
        $worktree = { param($RepositoryRoot, $WorktreeRoot, $IssueNumber, $Title, $BranchName, $DefaultBranch, $CommandRunner) $worktreeCalls.Add($BranchName) | Out-Null; [pscustomobject]@{ Path = (Join-Path $TestDrive 'issue'); BranchName = 'codex/42-retry-me' } }.GetNewClosure()
        $setup = { param($Worktree, $Config, $ActivityLogPath) $setupCalls.Add($Worktree) | Out-Null }.GetNewClosure()
        $saveCounter = @{ Value = 0 }
        $stateWriter = {
            param($Path, $Number, $Attempt)
            Write-CodexIssueAttemptState -Path $Path -IssueNumber $Number -AttemptState $Attempt | Out-Null
            $saveCounter.Value++
            $snapshot = (Read-CodexWorkerState -Path $Path).issues."$Number"
            $events.Add(('save#{0}:status={1};attempt={2};retry={3};run={4};publication={5}' -f $saveCounter.Value, $snapshot.status, $snapshot.attempt, $snapshot.retryCount, $snapshot.runDirectory, $snapshot.publicationStage)) | Out-Null
        }.GetNewClosure()
        $codex = {
            param($IssueWorktree, $IssueContext, $Config, $RunDirectory, $StatePath)
            $attempts.Add(1) | Out-Null
            $codexWorktreePaths.Add([string]$IssueWorktree) | Out-Null
            [pscustomobject]@{ Status = 'failed'; Classification = 'transient_service_unavailable'; RunDirectory = $RunDirectory; Summary = $null; LastError = 'network unavailable' }
        }.GetNewClosure()

        $result = Invoke-CodexIssueRun -Repository 'owner/repo' -IssueNumber 42 -Actor 'trusted-user' -EventName 'labeled' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive `
            -Config ([pscustomobject]@{ defaultBranch = 'master' }) -StatePath $statePath -StateWriter $stateWriter -GitHubCommandRunner $github -WorktreeProvider $worktree -SetupProvider $setup -CodexProvider $codex

        $result.Status | Should Be 'blocked'
        $attempts.Count | Should Be 2
        $saved = Read-CodexWorkerState -Path $statePath
        $saved.issues.'42'.retryCount | Should Be 1
        $saved.issues.'42'.attempt | Should Be 2
        $saved.issues.'42'.lastError | Should Match 'network unavailable'
        $worktreeCalls.Count | Should Be 1
        $setupCalls.Count | Should Be 1
        $setupCalls[0] | Should Be $saved.issues.'42'.worktree
        $codexWorktreePaths.Count | Should Be 2
        $codexWorktreePaths[0] | Should Be $saved.issues.'42'.worktree
        $codexWorktreePaths[1] | Should Be $saved.issues.'42'.worktree
        $retryLabelIndex = $events.IndexOf('label:codex:retry')
        $retryLabelIndex | Should BeGreaterThan -1
        $firstRunDirectory = Join-Path $TestDrive 'runs\issue-42\1'
        ($events[$retryLabelIndex + 1] -replace '^save#\d+:', '') | Should Be ('status=retry;attempt=1;retry=1;run={0};publication=none' -f $firstRunDirectory)
        $blockedLabelIndex = $events.IndexOf('label:codex:blocked')
        $blockedCommentIndex = $events.IndexOf('blocked-comment')
        $blockedLabelIndex | Should BeLessThan $blockedCommentIndex
        $secondRunDirectory = Join-Path $TestDrive 'runs\issue-42\2'
        ($events[$blockedLabelIndex + 1] -replace '^save#\d+:', '') | Should Be ('status=blocked;attempt=2;retry=1;run={0};publication=none' -f $secondRunDirectory)
        ($events[$blockedCommentIndex + 1] -replace '^save#\d+:', '') | Should Be ('status=blocked;attempt=2;retry=1;run={0};publication=none' -f $secondRunDirectory)
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

    It 'acquires the issue lock before rereading mutable context' {
        $statePath = Join-Path $TestDrive 'lock-order-state.json'
        $events = New-Object 'System.Collections.Generic.List[string]'
        $github = {
            param([string[]] $Arguments)
            if (@($Arguments | Where-Object { $_ -match 'permission' }).Count -gt 0) { $events.Add('permission') | Out-Null; return '{"permission":"write"}' }
            if (($Arguments -contains 'view') -and ($Arguments -contains 'issue')) { $events.Add('issue-read') | Out-Null; return '{"number":42,"title":"Order","body":"body","author":{"login":"reporter"},"comments":[],"labels":[{"name":"codex"}],"state":"OPEN","url":"https://github.com/owner/repo/issues/42"}' }
            if (($Arguments -contains 'develop') -and ($Arguments -contains '--list')) { $events.Add('development-read') | Out-Null; return '' }
            if (($Arguments -contains 'pr') -and ($Arguments -contains 'list')) { $events.Add('pull-request-read') | Out-Null; return '[]' }
            if ($Arguments -contains '--add-label') { $events.Add('status') | Out-Null; return '' }
            if ($Arguments -contains '--body') { $events.Add('comment') | Out-Null; return '' }
            throw 'Unexpected GitHub call.'
        }.GetNewClosure()
        $lock = { param($Path) $events.Add('lock') | Out-Null; 'lock-handle' }.GetNewClosure()
        $unlock = { param($Handle) $events.Add('unlock') | Out-Null }.GetNewClosure()
        $stateReader = { param($Path) $events.Add('state-read') | Out-Null; Read-CodexWorkerState -Path $Path }.GetNewClosure()
        $worktree = { param($RepositoryRoot, $WorktreeRoot, $IssueNumber, $Title, $BranchName, $DefaultBranch, $CommandRunner) [pscustomobject]@{ Path = (Join-Path $TestDrive 'order-worktree'); BranchName = 'codex/42-order' } }.GetNewClosure()
        $setup = { param($Worktree, $Config, $ActivityLogPath) }.GetNewClosure()
        $codex = { param($IssueWorktree, $IssueContext, $Config, $RunDirectory, $StatePath) [pscustomobject]@{ Status = 'completed'; Classification = 'completed'; Summary = [pscustomobject]@{ status = 'completed'; rootCauseOrApproach = 'order'; changedComponents = @('x'); decisions = @(); validation = @(); warnings = @(); remainingRisks = @(); commitMessage = 'fix: order'; prTitle = 'fix: order'; requiresHumanInput = $false; humanQuestion = $null } } }.GetNewClosure()

        Invoke-CodexIssueRun -Repository 'owner/repo' -IssueNumber 42 -Actor 'trusted-user' -EventName 'labeled' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -Config ([pscustomobject]@{}) -StatePath $statePath -StateReader $stateReader -LockProvider $lock -UnlockProvider $unlock -GitHubCommandRunner $github -WorktreeProvider $worktree -SetupProvider $setup -CodexProvider $codex | Out-Null

        $events.IndexOf('lock') | Should BeLessThan $events.IndexOf('issue-read')
        $events.IndexOf('lock') | Should BeLessThan $events.IndexOf('permission')
        $events.IndexOf('lock') | Should BeLessThan $events.IndexOf('development-read')
        $events.IndexOf('lock') | Should BeLessThan $events.IndexOf('state-read')
        $events.IndexOf('unlock') | Should BeGreaterThan $events.IndexOf('comment')
    }

    It 'keeps dry-run lock-free when a lock boundary is injected' {
        $lockCalls = New-Object 'System.Collections.Generic.List[string]'
        $github = {
            param([string[]] $Arguments)
            if (@($Arguments | Where-Object { $_ -match 'permission' }).Count -gt 0) { return '{"permission":"write"}' }
            if (($Arguments -contains 'view') -and ($Arguments -contains 'issue')) { return '{"number":42,"title":"Dry","body":"body","author":{"login":"reporter"},"comments":[],"labels":[],"state":"OPEN","url":"https://github.com/owner/repo/issues/42"}' }
            if (($Arguments -contains 'develop') -and ($Arguments -contains '--list')) { return '' }
            if (($Arguments -contains 'pr') -and ($Arguments -contains 'list')) { return '[]' }
            throw 'Dry-run mutation.'
        }.GetNewClosure()
        $lock = { param($Path) $lockCalls.Add($Path) | Out-Null; throw 'lock must not be acquired' }.GetNewClosure()

        $result = Invoke-CodexIssueRun -Repository 'owner/repo' -IssueNumber 42 -Actor 'trusted-user' -EventName 'labeled' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -DryRun -LockProvider $lock -GitHubCommandRunner $github

        $result.DryRun | Should Be $true
        $lockCalls.Count | Should Be 0
    }

    It 'reconstructs a missing persisted worktree when retrying a blocked issue' {
        $statePath = Join-Path $TestDrive 'stale-worktree-state.json'
        $repositoryRoot = Join-Path $TestDrive 'repo'
        $staleWorktree = Join-Path $TestDrive 'deleted-worktree'
        $replacementWorktree = Join-Path $repositoryRoot '.worktrees\issue-42-resume'
        $state = [pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = [pscustomobject]@{ issueNumber = 42; status = 'blocked'; attempt = 1; branch = 'codex/42-resume'; worktree = $staleWorktree; threadId = $null; runDirectory = $null; commit = $null; prUrl = $null; retryCount = 0; publicationStage = 'none'; lastError = 'old' } }; deployment = $null }
        Write-CodexWorkerState -Path $statePath -State $state
        $worktreeCalls = New-Object 'System.Collections.Generic.List[string]'
        $github = {
            param([string[]] $Arguments)
            if (@($Arguments | Where-Object { $_ -match 'permission' }).Count -gt 0) { return '{"permission":"write"}' }
            if (($Arguments -contains 'view') -and ($Arguments -contains 'issue')) { return '{"number":42,"title":"Resume","body":"body","author":{"login":"reporter"},"comments":[],"labels":[{"name":"codex"}],"state":"OPEN","url":"https://github.com/owner/repo/issues/42"}' }
            if (($Arguments -contains 'develop') -and ($Arguments -contains '--list')) { return '' }
            if (($Arguments -contains 'pr') -and ($Arguments -contains 'list')) { return '[]' }
            if ($Arguments -contains '--add-label' -or $Arguments -contains '--body') { return '' }
            throw 'Unexpected GitHub call.'
        }.GetNewClosure()
        $worktree = {
            param($Root, $WorktreeRoot, $IssueNumber, $Title, $BranchName, $DefaultBranch, $CommandRunner)
            $worktreeCalls.Add($Root) | Out-Null
            return [pscustomobject]@{ Path = $replacementWorktree; BranchName = $BranchName; Reused = $false; Created = $true }
        }.GetNewClosure()
        $setup = { param($Worktree, $Config, $ActivityLogPath) }.GetNewClosure()
        $codex = { param($IssueWorktree, $IssueContext, $Config, $RunDirectory, $StatePath) [pscustomobject]@{ Status = 'failed'; Classification = 'process_failed'; LastError = 'stop after worktree selection' } }.GetNewClosure()

        Invoke-CodexIssueRun -Repository 'owner/repo' -IssueNumber 42 -Actor 'trusted-user' -EventName 'retry' -RepositoryRoot $repositoryRoot -DataRoot $TestDrive -Config ([pscustomobject]@{}) -StatePath $statePath -GitHubCommandRunner $github -WorktreeProvider $worktree -SetupProvider $setup -CodexProvider $codex | Out-Null

        $worktreeCalls.Count | Should Be 1
        $worktreeCalls[0] | Should Be $repositoryRoot
        (Read-CodexWorkerState -Path $statePath).issues.'42'.worktree | Should Be $replacementWorktree
    }

    It 'resets retry allowance for a resumed blocked attempt' {
        $statePath = Join-Path $TestDrive 'resume-retry-state.json'
        $state = [pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = [pscustomobject]@{ issueNumber = 42; status = 'blocked'; attempt = 1; branch = 'codex/42-resume'; worktree = (Join-Path $TestDrive 'resume-worktree'); threadId = $null; runDirectory = $null; commit = $null; prUrl = $null; retryCount = 1; publicationStage = 'none'; lastError = 'old' } }; deployment = $null }
        Write-CodexWorkerState -Path $statePath -State $state
        $codexAttempts = New-Object 'System.Collections.Generic.List[string]'
        $github = {
            param([string[]] $Arguments)
            if (@($Arguments | Where-Object { $_ -match 'permission' }).Count -gt 0) { return '{"permission":"write"}' }
            if (($Arguments -contains 'view') -and ($Arguments -contains 'issue')) { return '{"number":42,"title":"Resume","body":"body","author":{"login":"reporter"},"comments":[],"labels":[{"name":"codex"}],"state":"OPEN","url":"https://github.com/owner/repo/issues/42"}' }
            if (($Arguments -contains 'develop') -and ($Arguments -contains '--list')) { return '' }
            if (($Arguments -contains 'pr') -and ($Arguments -contains 'list')) { return '[]' }
            if ($Arguments -contains '--add-label' -or $Arguments -contains '--body') { return '' }
            throw 'Unexpected GitHub call.'
        }.GetNewClosure()
        $worktree = {
            param($RepositoryRoot, $WorktreeRoot, $IssueNumber, $Title, $BranchName, $DefaultBranch, $CommandRunner)
            [pscustomobject]@{ Path = (Join-Path $TestDrive 'resume-worktree'); BranchName = $BranchName; Reused = $false; Created = $true }
        }.GetNewClosure()
        $setup = { param($Worktree, $Config, $ActivityLogPath) }.GetNewClosure()
        $codex = {
            param($IssueWorktree, $IssueContext, $Config, $RunDirectory, $StatePath)
            $codexAttempts.Add($RunDirectory) | Out-Null
            if ($codexAttempts.Count -eq 1) { return [pscustomobject]@{ Status = 'failed'; Classification = 'transient_service_unavailable'; LastError = 'temporary' } }
            return [pscustomobject]@{ Status = 'completed'; Classification = 'completed'; Summary = [pscustomobject]@{ status = 'completed'; rootCauseOrApproach = 'resume'; changedComponents = @(); decisions = @(); validation = @(); warnings = @(); remainingRisks = @(); commitMessage = 'fix: resume'; prTitle = 'fix: resume'; requiresHumanInput = $false; humanQuestion = $null } }
        }.GetNewClosure()

        $result = Invoke-CodexIssueRun -Repository 'owner/repo' -IssueNumber 42 -Actor 'trusted-user' -EventName 'retry' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -Config ([pscustomobject]@{}) -StatePath $statePath -GitHubCommandRunner $github -WorktreeProvider $worktree -SetupProvider $setup -CodexProvider $codex

        $result.Status | Should Be 'pr-ready'
        $codexAttempts.Count | Should Be 2
        (Read-CodexWorkerState -Path $statePath).issues.'42'.retryCount | Should Be 1
    }

    It 'blocks a valid summary when Codex classification completed but status failed' {
        $statePath = Join-Path $TestDrive 'classification-status-failed.json'
        $events = New-Object 'System.Collections.Generic.List[string]'
        $github = {
            param([string[]] $Arguments)
            if (@($Arguments | Where-Object { $_ -match 'permission' }).Count -gt 0) { return '{"permission":"write"}' }
            if (($Arguments -contains 'view') -and ($Arguments -contains 'issue')) { return '{"number":59,"title":"Status failed","body":"body","author":{"login":"reporter"},"comments":[],"labels":[],"state":"OPEN","url":"https://github.com/owner/repo/issues/59"}' }
            if (($Arguments -contains 'develop') -and ($Arguments -contains '--list')) { return '' }
            if (($Arguments -contains 'pr') -and ($Arguments -contains 'list')) { return '[]' }
            if ($Arguments -contains '--add-label') {
                $label = $Arguments[$Arguments.IndexOf('--add-label') + 1]
                if ($label -eq 'codex:blocked') { $events.Add('label:codex:blocked') | Out-Null }
                return ''
            }
            if ($Arguments -contains '--body') {
                $body = $Arguments[$Arguments.IndexOf('--body') + 1]
                if ($body -like '*Codex work is blocked*') { $events.Add('blocked-comment') | Out-Null }
                return ''
            }
            throw 'Unexpected GitHub call.'
        }.GetNewClosure()
        $worktree = { param($RepositoryRoot, $WorktreeRoot, $IssueNumber, $Title, $BranchName, $DefaultBranch, $CommandRunner) [pscustomobject]@{ Path = (Join-Path $TestDrive 'status-failed'); BranchName = 'codex/59-status-failed' } }.GetNewClosure()
        $setup = { param($Worktree, $Config, $ActivityLogPath) }.GetNewClosure()
        $saveCounter = @{ Value = 0 }
        $stateWriter = {
            param($Path, $Number, $Attempt)
            Write-CodexIssueAttemptState -Path $Path -IssueNumber $Number -AttemptState $Attempt | Out-Null
            $snapshot = (Read-CodexWorkerState -Path $Path).issues."$Number"
            $saveCounter.Value++
            $events.Add(('save#{0}:status={1};attempt={2};retry={3};run={4};publication={5}' -f $saveCounter.Value, $snapshot.status, $snapshot.attempt, $snapshot.retryCount, $snapshot.runDirectory, $snapshot.publicationStage)) | Out-Null
        }.GetNewClosure()
        $summary = [pscustomobject]@{ status = 'completed'; rootCauseOrApproach = 'valid but failed'; changedComponents = @(); decisions = @(); validation = @(); warnings = @(); remainingRisks = @(); commitMessage = 'fix: failed'; prTitle = 'fix: failed'; requiresHumanInput = $false; humanQuestion = $null }
        $codex = { param($IssueWorktree, $IssueContext, $Config, $RunDirectory, $StatePath) [pscustomobject]@{ Status = 'failed'; Classification = 'completed'; Summary = $summary; LastError = 'process failed' } }.GetNewClosure()

        $result = Invoke-CodexIssueRun -Repository 'owner/repo' -IssueNumber 59 -Actor 'trusted-user' -EventName 'labeled' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -Config ([pscustomobject]@{}) -StatePath $statePath -StateWriter $stateWriter -GitHubCommandRunner $github -WorktreeProvider $worktree -SetupProvider $setup -CodexProvider $codex

        $result.Status | Should Be 'blocked'
        $blockedLabelIndex = $events.IndexOf('label:codex:blocked')
        $blockedCommentIndex = $events.IndexOf('blocked-comment')
        $blockedLabelIndex | Should BeGreaterThan -1
        $blockedCommentIndex | Should BeGreaterThan $blockedLabelIndex
        $expectedBlockedSnapshot = 'status=blocked;attempt=1;retry=0;run={0};publication=none' -f (Join-Path $TestDrive 'runs\issue-59\1')
        ($events[$blockedLabelIndex + 1] -replace '^save#\d+:', '') | Should Be $expectedBlockedSnapshot
        ($events[$blockedCommentIndex + 1] -replace '^save#\d+:', '') | Should Be $expectedBlockedSnapshot
        $events[$blockedLabelIndex + 1] | Should Not Be $events[$blockedCommentIndex + 1]
    }

    It 'blocks a valid completed summary for each representative non-success classification' {
        $classifications = @('failed', 'authentication', 'timeout', 'process_failed')
        foreach ($classification in $classifications) {
            $number = 60 + [array]::IndexOf($classifications, $classification)
            $statePath = Join-Path $TestDrive "classification-$number.json"
            $events = New-Object 'System.Collections.Generic.List[string]'
            $github = {
                param([string[]] $Arguments)
                if (@($Arguments | Where-Object { $_ -match 'permission' }).Count -gt 0) { return '{"permission":"write"}' }
                if (($Arguments -contains 'view') -and ($Arguments -contains 'issue')) { return ('{"number":' + $number + ',"title":"Classify","body":"body","author":{"login":"reporter"},"comments":[],"labels":[],"state":"OPEN","url":"https://github.com/owner/repo/issues/' + $number + '"}') }
                if (($Arguments -contains 'develop') -and ($Arguments -contains '--list')) { return '' }
                if (($Arguments -contains 'pr') -and ($Arguments -contains 'list')) { return '[]' }
                if ($Arguments -contains '--add-label') {
                    $label = $Arguments[$Arguments.IndexOf('--add-label') + 1]
                    if ($label -eq 'codex:blocked') { $events.Add('label:codex:blocked') | Out-Null }
                    return ''
                }
                if ($Arguments -contains '--body') {
                    $body = $Arguments[$Arguments.IndexOf('--body') + 1]
                    if ($body -like '*Codex work is blocked*') { $events.Add('blocked-comment') | Out-Null }
                    return ''
                }
                throw 'Unexpected GitHub call.'
            }.GetNewClosure()
            $worktree = { param($RepositoryRoot, $WorktreeRoot, $IssueNumber, $Title, $BranchName, $DefaultBranch, $CommandRunner) [pscustomobject]@{ Path = (Join-Path $TestDrive "classify-$number"); BranchName = "codex/$number-classify" } }.GetNewClosure()
            $setup = { param($Worktree, $Config, $ActivityLogPath) }.GetNewClosure()
            $saveCounter = @{ Value = 0 }
            $stateWriter = {
                param($Path, $Number, $Attempt)
                Write-CodexIssueAttemptState -Path $Path -IssueNumber $Number -AttemptState $Attempt | Out-Null
                $snapshot = (Read-CodexWorkerState -Path $Path).issues."$Number"
                $saveCounter.Value++
                $events.Add(('save#{0}:status={1};attempt={2};retry={3};run={4};publication={5}' -f $saveCounter.Value, $snapshot.status, $snapshot.attempt, $snapshot.retryCount, $snapshot.runDirectory, $snapshot.publicationStage)) | Out-Null
            }.GetNewClosure()
            $summary = [pscustomobject]@{ status = 'completed'; rootCauseOrApproach = 'valid but failed'; changedComponents = @(); decisions = @(); validation = @(); warnings = @(); remainingRisks = @(); commitMessage = 'fix: failed'; prTitle = 'fix: failed'; requiresHumanInput = $false; humanQuestion = $null }
            $codex = { param($IssueWorktree, $IssueContext, $Config, $RunDirectory, $StatePath) [pscustomobject]@{ Status = 'completed'; Classification = $classification; Summary = $summary; LastError = $classification } }.GetNewClosure()
            $result = Invoke-CodexIssueRun -Repository 'owner/repo' -IssueNumber $number -Actor 'trusted-user' -EventName 'labeled' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -Config ([pscustomobject]@{}) -StatePath $statePath -StateWriter $stateWriter -GitHubCommandRunner $github -WorktreeProvider $worktree -SetupProvider $setup -CodexProvider $codex
            $result.Status | Should Be 'blocked'
            $blockedLabelIndex = $events.IndexOf('label:codex:blocked')
            $blockedCommentIndex = $events.IndexOf('blocked-comment')
            $blockedLabelIndex | Should BeGreaterThan -1
            $blockedCommentIndex | Should BeGreaterThan $blockedLabelIndex
            $expectedBlockedSnapshot = 'status=blocked;attempt=1;retry=0;run={0};publication=none' -f (Join-Path $TestDrive "runs\issue-$number\1")
            ($events[$blockedLabelIndex + 1] -replace '^save#\d+:', '') | Should Be $expectedBlockedSnapshot
            ($events[$blockedCommentIndex + 1] -replace '^save#\d+:', '') | Should Be $expectedBlockedSnapshot
            $events[$blockedLabelIndex + 1] | Should Not Be $events[$blockedCommentIndex + 1]
        }
    }

    It 'persists blocked label and comment snapshots for an invalid summary' {
        $statePath = Join-Path $TestDrive 'invalid-summary-state.json'
        $events = New-Object 'System.Collections.Generic.List[string]'
        $github = {
            param([string[]] $Arguments)
            if (@($Arguments | Where-Object { $_ -match 'permission' }).Count -gt 0) { return '{"permission":"write"}' }
            if (($Arguments -contains 'view') -and ($Arguments -contains 'issue')) { return '{"number":63,"title":"Invalid summary","body":"body","author":{"login":"reporter"},"comments":[],"labels":[],"state":"OPEN","url":"https://github.com/owner/repo/issues/63"}' }
            if (($Arguments -contains 'develop') -and ($Arguments -contains '--list')) { return '' }
            if (($Arguments -contains 'pr') -and ($Arguments -contains 'list')) { return '[]' }
            if ($Arguments -contains '--add-label') {
                $label = $Arguments[$Arguments.IndexOf('--add-label') + 1]
                if ($label -eq 'codex:blocked') { $events.Add('label:codex:blocked') | Out-Null }
                return ''
            }
            if ($Arguments -contains '--body') {
                $body = $Arguments[$Arguments.IndexOf('--body') + 1]
                if ($body -like '*Codex work is blocked*') { $events.Add('blocked-comment') | Out-Null }
                return ''
            }
            throw 'Unexpected GitHub call.'
        }.GetNewClosure()
        $worktree = { param($RepositoryRoot, $WorktreeRoot, $IssueNumber, $Title, $BranchName, $DefaultBranch, $CommandRunner) [pscustomobject]@{ Path = (Join-Path $TestDrive 'invalid-summary'); BranchName = 'codex/63-invalid-summary' } }.GetNewClosure()
        $setup = { param($Worktree, $Config, $ActivityLogPath) }.GetNewClosure()
        $saveCounter = @{ Value = 0 }
        $stateWriter = {
            param($Path, $Number, $Attempt)
            Write-CodexIssueAttemptState -Path $Path -IssueNumber $Number -AttemptState $Attempt | Out-Null
            $snapshot = (Read-CodexWorkerState -Path $Path).issues."$Number"
            $saveCounter.Value++
            $events.Add(('save#{0}:status={1};attempt={2};retry={3};run={4};publication={5}' -f $saveCounter.Value, $snapshot.status, $snapshot.attempt, $snapshot.retryCount, $snapshot.runDirectory, $snapshot.publicationStage)) | Out-Null
        }.GetNewClosure()
        $codex = { param($IssueWorktree, $IssueContext, $Config, $RunDirectory, $StatePath) [pscustomobject]@{ Status = 'completed'; Classification = 'completed'; Summary = $null; LastError = 'summary parse failed' } }.GetNewClosure()

        $result = Invoke-CodexIssueRun -Repository 'owner/repo' -IssueNumber 63 -Actor 'trusted-user' -EventName 'labeled' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -Config ([pscustomobject]@{}) -StatePath $statePath -StateWriter $stateWriter -GitHubCommandRunner $github -WorktreeProvider $worktree -SetupProvider $setup -CodexProvider $codex

        $result.Status | Should Be 'blocked'
        $blockedLabelIndex = $events.IndexOf('label:codex:blocked')
        $blockedCommentIndex = $events.IndexOf('blocked-comment')
        $blockedLabelIndex | Should BeGreaterThan -1
        $blockedCommentIndex | Should BeGreaterThan $blockedLabelIndex
        $expectedBlockedSnapshot = 'status=blocked;attempt=1;retry=0;run={0};publication=none' -f (Join-Path $TestDrive 'runs\issue-63\1')
        ($events[$blockedLabelIndex + 1] -replace '^save#\d+:', '') | Should Be $expectedBlockedSnapshot
        ($events[$blockedCommentIndex + 1] -replace '^save#\d+:', '') | Should Be $expectedBlockedSnapshot
        $events[$blockedLabelIndex + 1] | Should Not Be $events[$blockedCommentIndex + 1]
    }

    It 'attempts blocked notification while preserving the original exception' {
        $statePath = Join-Path $TestDrive 'exception-state.json'
        $events = New-Object 'System.Collections.Generic.List[string]'
        $github = {
            param([string[]] $Arguments)
            if (@($Arguments | Where-Object { $_ -match 'permission' }).Count -gt 0) { return '{"permission":"write"}' }
            if (($Arguments -contains 'view') -and ($Arguments -contains 'issue')) { return '{"number":42,"title":"Exception","body":"body","author":{"login":"reporter"},"comments":[],"labels":[],"state":"OPEN","url":"https://github.com/owner/repo/issues/42"}' }
            if (($Arguments -contains 'develop') -and ($Arguments -contains '--list')) { return '' }
            if (($Arguments -contains 'pr') -and ($Arguments -contains 'list')) { return '[]' }
            if ($Arguments -contains '--add-label') {
                if ($Arguments[$Arguments.IndexOf('--add-label') + 1] -eq 'codex:blocked') { $events.Add('blocked-label-attempt') | Out-Null; throw 'blocked label failed' }
                return ''
            }
            if ($Arguments -contains '--body' -and $Arguments[$Arguments.IndexOf('--body') + 1] -match 'Codex work is blocked') { $events.Add('blocked-comment-attempt') | Out-Null; throw 'blocked milestone failed' }
            if ($Arguments -contains '--body') { return '' }
            throw 'Unexpected GitHub call.'
        }.GetNewClosure()
        $worktree = { param($RepositoryRoot, $WorktreeRoot, $IssueNumber, $Title, $BranchName, $DefaultBranch, $CommandRunner) [pscustomobject]@{ Path = (Join-Path $TestDrive 'exception-worktree'); BranchName = 'codex/42-exception' } }.GetNewClosure()
        $setup = { param($Worktree, $Config, $ActivityLogPath) throw 'setup failed with actionable detail' }.GetNewClosure()

        { Invoke-CodexIssueRun -Repository 'owner/repo' -IssueNumber 42 -Actor 'trusted-user' -EventName 'labeled' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -Config ([pscustomobject]@{}) -StatePath $statePath -GitHubCommandRunner $github -WorktreeProvider $worktree -SetupProvider $setup } | Should Throw 'setup failed with actionable detail'

        $saved = Read-CodexWorkerState -Path $statePath
        $saved.issues.'42'.status | Should Be 'blocked'
        $saved.issues.'42'.lastError | Should Be 'setup failed with actionable detail'
        @($events | Where-Object { $_ -eq 'blocked-label-attempt' }).Count | Should Be 1
        @($events | Where-Object { $_ -eq 'blocked-comment-attempt' }).Count | Should Be 1
    }

    It 'persists actionable detail when an exception message is empty' {
        $statePath = Join-Path $TestDrive 'empty-exception-state.json'
        $comments = New-Object 'System.Collections.Generic.List[string]'
        $github = {
            param([string[]] $Arguments)
            if (@($Arguments | Where-Object { $_ -match 'permission' }).Count -gt 0) { return '{"permission":"write"}' }
            if (($Arguments -contains 'view') -and ($Arguments -contains 'issue')) { return '{"number":65,"title":"Empty exception","body":"body","author":{"login":"reporter"},"comments":[],"labels":[{"name":"codex"}],"state":"OPEN","url":"https://github.com/owner/repo/issues/65"}' }
            if (($Arguments -contains 'develop') -and ($Arguments -contains '--list')) { return '' }
            if (($Arguments -contains 'pr') -and ($Arguments -contains 'list')) { return '[]' }
            if ($Arguments -contains '--add-label') { return '' }
            if ($Arguments -contains '--body') {
                $comments.Add([string]$Arguments[$Arguments.IndexOf('--body') + 1]) | Out-Null
                return ''
            }
            throw 'Unexpected GitHub call.'
        }.GetNewClosure()
        $worktree = { param($RepositoryRoot, $WorktreeRoot, $IssueNumber, $Title, $BranchName, $DefaultBranch, $CommandRunner) [pscustomobject]@{ Path = (Join-Path $TestDrive 'empty-exception'); BranchName = 'codex/65-empty-exception' } }.GetNewClosure()
        $setup = { param($Worktree, $Config, $ActivityLogPath) throw [System.Exception]::new('') }.GetNewClosure()

        { Invoke-CodexIssueRun -Repository 'owner/repo' -IssueNumber 65 -Actor 'trusted-user' -EventName 'labeled' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -Config ([pscustomobject]@{}) -StatePath $statePath -GitHubCommandRunner $github -WorktreeProvider $worktree -SetupProvider $setup } | Should Throw

        $saved = Read-CodexWorkerState -Path $statePath
        $saved.issues.'65'.status | Should Be 'blocked'
        $saved.issues.'65'.lastError | Should Not BeNullOrEmpty
        $saved.issues.'65'.lastError | Should Match 'System.Exception'
        (@($comments | Where-Object { $_ -like 'Codex work is blocked.*System.Exception*' })).Count | Should Be 1
    }

    It 'refreshes live labels before an exception transition to blocked' {
        $statePath = Join-Path $TestDrive 'exception-label-state.json'
        $issueReads = @{ Value = 0 }
        $blockedArguments = @{ Value = $null }
        $github = {
            param([string[]] $Arguments)
            if (@($Arguments | Where-Object { $_ -match 'permission' }).Count -gt 0) { return '{"permission":"write"}' }
            if (($Arguments -contains 'view') -and ($Arguments -contains 'issue')) {
                $issueReads.Value++
                $labels = if ($issueReads.Value -gt 1) { '[{"name":"codex"},{"name":"codex:running"}]' } else { '[{"name":"codex"}]' }
                return ('{"number":66,"title":"Refresh labels","body":"body","author":{"login":"reporter"},"comments":[],"labels":' + $labels + ',"state":"OPEN","url":"https://github.com/owner/repo/issues/66"}')
            }
            if (($Arguments -contains 'develop') -and ($Arguments -contains '--list')) { return '' }
            if (($Arguments -contains 'pr') -and ($Arguments -contains 'list')) { return '[]' }
            if ($Arguments -contains '--add-label') {
                $label = $Arguments[$Arguments.IndexOf('--add-label') + 1]
                if ($label -eq 'codex:blocked') { $blockedArguments.Value = @($Arguments) }
                return ''
            }
            if ($Arguments -contains '--body') { return '' }
            throw 'Unexpected GitHub call.'
        }.GetNewClosure()
        $worktree = { param($RepositoryRoot, $WorktreeRoot, $IssueNumber, $Title, $BranchName, $DefaultBranch, $CommandRunner) [pscustomobject]@{ Path = (Join-Path $TestDrive 'exception-labels'); BranchName = 'codex/66-refresh-labels' } }.GetNewClosure()
        $setup = { param($Worktree, $Config, $ActivityLogPath) throw 'setup failed' }.GetNewClosure()

        { Invoke-CodexIssueRun -Repository 'owner/repo' -IssueNumber 66 -Actor 'trusted-user' -EventName 'labeled' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -Config ([pscustomobject]@{}) -StatePath $statePath -GitHubCommandRunner $github -WorktreeProvider $worktree -SetupProvider $setup } | Should Throw 'setup failed'

        $issueReads.Value | Should Be 2
        ($blockedArguments.Value -contains '--remove-label') | Should Be $true
        ($blockedArguments.Value -contains 'codex:running') | Should Be $true
        ($blockedArguments.Value -contains 'codex:blocked') | Should Be $true
    }

    It 'persists distinct blocked snapshots around successful exception notifications' {
        $statePath = Join-Path $TestDrive 'exception-success-state.json'
        $events = New-Object 'System.Collections.Generic.List[string]'
        $github = {
            param([string[]] $Arguments)
            if (@($Arguments | Where-Object { $_ -match 'permission' }).Count -gt 0) { return '{"permission":"write"}' }
            if (($Arguments -contains 'view') -and ($Arguments -contains 'issue')) { return '{"number":64,"title":"Exception success","body":"body","author":{"login":"reporter"},"comments":[],"labels":[],"state":"OPEN","url":"https://github.com/owner/repo/issues/64"}' }
            if (($Arguments -contains 'develop') -and ($Arguments -contains '--list')) { return '' }
            if (($Arguments -contains 'pr') -and ($Arguments -contains 'list')) { return '[]' }
            if ($Arguments -contains '--add-label') {
                $label = $Arguments[$Arguments.IndexOf('--add-label') + 1]
                if ($label -eq 'codex:blocked') { $events.Add('label:codex:blocked') | Out-Null }
                return ''
            }
            if ($Arguments -contains '--body') {
                $body = $Arguments[$Arguments.IndexOf('--body') + 1]
                if ($body -like '*Codex work is blocked*') { $events.Add('blocked-comment') | Out-Null }
                return ''
            }
            throw 'Unexpected GitHub call.'
        }.GetNewClosure()
        $worktree = { param($RepositoryRoot, $WorktreeRoot, $IssueNumber, $Title, $BranchName, $DefaultBranch, $CommandRunner) [pscustomobject]@{ Path = (Join-Path $TestDrive 'exception-success'); BranchName = 'codex/64-exception-success' } }.GetNewClosure()
        $saveCounter = @{ Value = 0 }
        $stateWriter = {
            param($Path, $Number, $Attempt)
            Write-CodexIssueAttemptState -Path $Path -IssueNumber $Number -AttemptState $Attempt | Out-Null
            $snapshot = (Read-CodexWorkerState -Path $Path).issues."$Number"
            $saveCounter.Value++
            $events.Add(('save#{0}:status={1};attempt={2};retry={3};run={4};publication={5}' -f $saveCounter.Value, $snapshot.status, $snapshot.attempt, $snapshot.retryCount, $snapshot.runDirectory, $snapshot.publicationStage)) | Out-Null
        }.GetNewClosure()
        $setup = { param($Worktree, $Config, $ActivityLogPath) throw 'successful notification setup failure' }.GetNewClosure()

        { Invoke-CodexIssueRun -Repository 'owner/repo' -IssueNumber 64 -Actor 'trusted-user' -EventName 'labeled' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -Config ([pscustomobject]@{}) -StatePath $statePath -StateWriter $stateWriter -GitHubCommandRunner $github -WorktreeProvider $worktree -SetupProvider $setup } | Should Throw 'successful notification setup failure'

        $saved = Read-CodexWorkerState -Path $statePath
        $saved.issues.'64'.status | Should Be 'blocked'
        $saved.issues.'64'.lastError | Should Be 'successful notification setup failure'
        $blockedLabelIndex = $events.IndexOf('label:codex:blocked')
        $blockedCommentIndex = $events.IndexOf('blocked-comment')
        $blockedLabelIndex | Should BeGreaterThan -1
        $blockedCommentIndex | Should BeGreaterThan $blockedLabelIndex
        $expectedBlockedSnapshot = 'status=blocked;attempt=1;retry=0;run=;publication=none'
        ($events[$blockedLabelIndex + 1] -replace '^save#\d+:', '') | Should Be $expectedBlockedSnapshot
        ($events[$blockedCommentIndex + 1] -replace '^save#\d+:', '') | Should Be $expectedBlockedSnapshot
        $events[$blockedLabelIndex + 1] | Should Not Be $events[$blockedCommentIndex + 1]
    }

    It 'recovers publication and restores the pr-ready label after the locked reread' {
        $statePath = Join-Path $TestDrive 'publication-state.json'
        $state = [pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{ '42' = [pscustomobject]@{ issueNumber = 42; status = 'pr-ready'; attempt = 1; branch = 'codex/42-publication'; worktree = (Join-Path $TestDrive 'publication-worktree'); threadId = $null; runDirectory = $null; commit = 'abc'; prUrl = $null; retryCount = 0; publicationStage = 'committed'; lastError = $null } }; deployment = $null }
        Write-CodexWorkerState -Path $statePath -State $state
        $events = New-Object 'System.Collections.Generic.List[string]'
        $stateWriter = {
            param($Path, $Number, $Attempt)
            Write-CodexIssueAttemptState -Path $Path -IssueNumber $Number -AttemptState $Attempt | Out-Null
            $snapshot = (Read-CodexWorkerState -Path $Path).issues."$Number"
            $events.Add('save:' + $snapshot.publicationStage) | Out-Null
        }.GetNewClosure()
        $statusCommands = New-Object 'System.Collections.Generic.List[string]'
        $github = {
            param([string[]] $Arguments)
            if (@($Arguments | Where-Object { $_ -match 'permission' }).Count -gt 0) { return '{"permission":"write"}' }
            if (($Arguments -contains 'view') -and ($Arguments -contains 'issue')) { $events.Add('issue-read') | Out-Null; return '{"number":42,"title":"Publication","body":"body","author":{"login":"reporter"},"comments":[],"labels":[{"name":"codex"},{"name":"codex:retry"},{"name":"codex:pr-ready"}],"state":"OPEN","url":"https://github.com/owner/repo/issues/42"}' }
            if (($Arguments -contains 'develop') -and ($Arguments -contains '--list')) { $events.Add('development-read') | Out-Null; return '' }
            if (($Arguments -contains 'pr') -and ($Arguments -contains 'list')) { return '[]' }
            if ($Arguments -contains 'issue' -and $Arguments -contains 'edit') { $statusCommands.Add(($Arguments -join ' ')) | Out-Null; return '' }
            throw 'Unexpected GitHub operation.'
        }.GetNewClosure()
        $lock = { param($Path) $events.Add('lock') | Out-Null; 'handle' }.GetNewClosure()
        $unlock = { param($Handle) $events.Add('unlock') | Out-Null }.GetNewClosure()
        $publication = { param($Attempt, $Issue, $Config, $Path) $events.Add('publication') | Out-Null; [pscustomobject]@{ publicationStage = 'pr-created'; prUrl = 'https://github.example/pr/42' } }.GetNewClosure()

        $result = Invoke-CodexIssueRun -Repository 'owner/repo' -IssueNumber 42 -Actor 'trusted-user' -EventName 'retry' -RepositoryRoot 'C:\repo' -DataRoot $TestDrive -Config ([pscustomobject]@{}) -StatePath $statePath -StateWriter $stateWriter -LockProvider $lock -UnlockProvider $unlock -GitHubCommandRunner $github -PublicationProvider $publication

        $result.RecoveredPublication | Should Be $true
        $result.PublicationStage | Should Be 'pr-created'
        $result.PrUrl | Should Be 'https://github.example/pr/42'
        $saved = Read-CodexWorkerState -Path $statePath
        $saved.issues.'42'.publicationStage | Should Be 'pr-created'
        $saved.issues.'42'.prUrl | Should Be 'https://github.example/pr/42'
        ($statusCommands -join ' ') | Should Match '--remove-label codex:retry'
        ($statusCommands -join ' ') | Should Match '--add-label codex:pr-ready'
        $events.IndexOf('lock') | Should BeLessThan $events.IndexOf('issue-read')
        $events.IndexOf('issue-read') | Should BeLessThan $events.IndexOf('publication')
        $events.IndexOf('publication') | Should BeLessThan $events.IndexOf('save:pr-created')
        $events.IndexOf('save:pr-created') | Should BeLessThan $events.IndexOf('unlock')
    }
}
