Describe 'Codex worker deployment notification' {
    BeforeEach {
        Import-Module (Join-Path $PSScriptRoot '..\codex-worker\CodexWorker.psd1') -Force
    }

    function New-NotificationState {
        param(
            [string] $Status = 'pending',
            [string] $SnoozeUntil = $null,
            [string] $TargetCommit = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
        )

        [pscustomobject]@{
            schemaVersion = 1
            issues = [pscustomobject]@{}
            deployment = [pscustomobject]@{
                targetCommit = $TargetCommit
                sourcePr = 17
                requestedAt = '2026-08-18T00:00:00.0000000Z'
                snoozeUntil = $SnoozeUntil
                status = $Status
            }
        }
    }

    It 'does not show a dialog when the interactive session is unavailable' {
        $state = New-NotificationState
        $shown = 0

        $result = Invoke-CodexDeploymentNotificationCycle -StatePath (Join-Path $TestDrive 'state.json') `
            -StateReader { param($Path) $state } -SessionProbe { $false } `
            -LockProvider { throw 'lock must not be attempted' } `
            -DialogProvider { $shown++; 'Deploy' } -DeployAction { throw 'deploy must not run' }

        $result.Status | Should Be 'SessionUnavailable'
        $shown | Should Be 0
    }

    It 'does not show a dialog while the worker lock is busy' {
        $state = New-NotificationState
        $shown = 0

        $result = Invoke-CodexDeploymentNotificationCycle -StatePath (Join-Path $TestDrive 'state.json') `
            -StateReader { param($Path) $state } -SessionProbe { $true } `
            -LockProvider { $null } -DialogProvider { $shown++; 'Deploy' }

        $result.Status | Should Be 'LockBusy'
        $shown | Should Be 0
    }

    It 'returns Deploy when the dialog provider has no response' {
        $state = New-NotificationState
        $events = New-Object 'System.Collections.Generic.List[string]'
        $durable = [pscustomobject]@{ Value = $state }
        $writer = { param($Path, $Desired) $durable.Value = (($Desired | ConvertTo-Json -Depth 20) | ConvertFrom-Json) }.GetNewClosure()
        $result = Invoke-CodexDeploymentNotificationCycle -StatePath (Join-Path $TestDrive 'state.json') `
            -StateReader { param($Path) $durable.Value } -StateWriter $writer -SessionProbe { $true } `
            -LockProvider { $events.Add('lock') | Out-Null; 'held' } `
            -UnlockProvider { param($Handle) $events.Add('unlock') | Out-Null } `
            -DialogProvider { $null } -DeployAction { $events.Add('deploy') | Out-Null }

        $result.Decision | Should Be 'Deploy'
        $events[0] | Should Be 'lock'
        $events[1] | Should Be 'deploy'
        $events[2] | Should Be 'unlock'
    }

    It 'snoozes Later for exactly five minutes and does not deploy' {
        $now = [DateTime]::Parse('2026-08-18T01:00:00Z')
        $state = New-NotificationState
        $durable = [pscustomobject]@{ Value = $state }
        $writes = New-Object 'System.Collections.Generic.List[object]'
        $deploys = 0
        $unlocks = New-Object 'System.Collections.Generic.List[object]'
        $reader = { param($Path) return $durable.Value }.GetNewClosure()
        $writer = {
            param($Path, $Desired)
            $writes.Add($Desired) | Out-Null
            $durable.Value = (($Desired | ConvertTo-Json -Depth 20) | ConvertFrom-Json)
        }.GetNewClosure()

        $result = Invoke-CodexDeploymentNotificationCycle -StatePath (Join-Path $TestDrive 'state.json') `
            -NowProvider { $now } -StateReader $reader -StateWriter $writer -SessionProbe { $true } `
            -LockProvider { 'held' } -UnlockProvider { $unlocks.Add('unlock') | Out-Null } -DialogProvider { 'Later' } `
            -DeployAction { $deploys++ }

        $result.Status | Should Be 'Snoozed'
        $actualSnooze = $durable.Value.deployment.snoozeUntil
        if ($actualSnooze -isnot [DateTime]) { $actualSnooze = [DateTimeOffset]::Parse([string]$actualSnooze).UtcDateTime }
        else { $actualSnooze = $actualSnooze.ToUniversalTime() }
        $actualSnooze | Should Be $now.AddMinutes(5).ToUniversalTime()
        $durable.Value.deployment.status | Should Be 'snoozed'
        $writes.Count | Should Be 1
        $deploys | Should Be 0
        $unlocks.Count | Should Be 1
    }

    It 'clears only the pending deployment when Cancel is selected' {
        $state = New-NotificationState
        $durable = [pscustomobject]@{ Value = $state }
        $writer = {
            param($Path, $Desired)
            $durable.Value = (($Desired | ConvertTo-Json -Depth 20) | ConvertFrom-Json)
        }.GetNewClosure()
        $deploys = 0

        $result = Invoke-CodexDeploymentNotificationCycle -StatePath (Join-Path $TestDrive 'state.json') `
            -StateReader { param($Path) $durable.Value } -StateWriter $writer -SessionProbe { $true } `
            -LockProvider { 'held' } -UnlockProvider { } -DialogProvider { 'Cancel' } `
            -DeployAction { $deploys++ }

        $result.Status | Should Be 'Cancelled'
        $durable.Value.deployment | Should Be $null
        $deploys | Should Be 0
    }

    It 'treats a closed dialog as Later' {
        $now = [DateTime]::Parse('2026-08-18T01:00:00Z')
        $state = New-NotificationState
        $durable = [pscustomobject]@{ Value = $state }
        $writer = {
            param($Path, $Desired)
            $durable.Value = (($Desired | ConvertTo-Json -Depth 20) | ConvertFrom-Json)
        }.GetNewClosure()

        $result = Invoke-CodexDeploymentNotificationCycle -StatePath (Join-Path $TestDrive 'state.json') `
            -NowProvider { $now } -StateReader { param($Path) $durable.Value } -StateWriter $writer `
            -SessionProbe { $true } -LockProvider { 'held' } -UnlockProvider { } `
            -DialogProvider { 'Closed' }

        $result.Decision | Should Be 'Later'
        $actualSnooze = $durable.Value.deployment.snoozeUntil
        if ($actualSnooze -isnot [DateTime]) { $actualSnooze = [DateTimeOffset]::Parse([string]$actualSnooze).UtcDateTime }
        else { $actualSnooze = $actualSnooze.ToUniversalTime() }
        $actualSnooze | Should Be $now.AddMinutes(5).ToUniversalTime()
    }

    It 'counts down through the shared controller state and cleans up after zero' {
        $events = New-Object 'System.Collections.Generic.List[string]'
        $controller = New-CodexDeploymentDialogController -Seconds 10 `
            -StartTimer { $events.Add('start') | Out-Null } `
            -StopTimer { $events.Add('stop') | Out-Null } `
            -DisposeTimer { $events.Add('dispose') | Out-Null } `
            -CloseWindow { $events.Add('close') | Out-Null } `
            -SetMessage { param($text) $events.Add($text) | Out-Null }

        $controller.State.Topmost | Should Be $true
        $controller.State.Message | Should Be 'Automation Workbench will rebuild in 10 seconds.'
        $controller.State.LaterLabel | Should Be 'Later (5 min)'
        $controller.State.CancelLabel | Should Be 'Cancel'
        $events.Count | Should Be 0
        & $controller.Tick
        $controller.State.Remaining | Should Be 10

        & $controller.ContentRendered
        $events[0] | Should Be 'start'
        for ($i = 0; $i -lt 10; $i++) { & $controller.Tick }

        $controller.State.Remaining | Should Be 0
        $controller.State.Message | Should Be 'Automation Workbench will rebuild in 0 seconds.'
        $controller.State.Decision | Should Be 'Deploy'
        ($events -join '|') | Should Match 'start.*stop.*dispose.*close'
        $controller.State.TimerStopped | Should Be $true
        $controller.State.TimerDisposed | Should Be $true
    }

    It 'starts the timer only after ContentRendered and maps Later, Cancel, and close' {
        foreach ($choice in @('Later', 'Cancel', 'Closed')) {
            $events = New-Object 'System.Collections.Generic.List[string]'
            $controller = New-CodexDeploymentDialogController -Seconds 10 `
                -StartTimer { $events.Add('start') | Out-Null } `
                -StopTimer { $events.Add('stop') | Out-Null } `
                -DisposeTimer { $events.Add('dispose') | Out-Null } `
                -CloseWindow { $events.Add('close') | Out-Null }
            $events.Count | Should Be 0
            & $controller.ContentRendered
            $events[0] | Should Be 'start'
            if ($choice -eq 'Later') { & $controller.Later }
            elseif ($choice -eq 'Cancel') { & $controller.Cancel }
            else { & $controller.Closed }
            $controller.State.Decision | Should Be $choice.Replace('Closed', 'Later')
            $events -join '|' | Should Match 'start\|stop\|dispose'
            if ($choice -ne 'Closed') { $events -join '|' | Should Match 'close$' }
            $controller.State.TimerStarted | Should Be $true
            & $controller.ContentRendered
            @($events | Where-Object { $_ -eq 'start' }).Count | Should Be 1
        }
    }

    It 'reads configured state through the installed pwsh STA notifier from an unrelated cwd' {
        $repoRoot = Join-Path $TestDrive 'repo'
        $dataRoot = Join-Path $TestDrive 'worker-data'
        $unrelatedRoot = Join-Path $TestDrive 'unrelated'
        New-Item -ItemType Directory -Path $repoRoot, $dataRoot, $unrelatedRoot -Force | Out-Null
        $configPath = Join-Path $dataRoot 'config.json'
        $statePath = Join-Path $dataRoot 'state.json'
        $config = [ordered]@{ repositoryRoot = $repoRoot; dataRoot = $dataRoot; configPath = $configPath }
        [IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
        $snoozed = New-NotificationState -Status 'snoozed' -SnoozeUntil ([DateTime]::UtcNow.AddHours(1).ToString('o'))
        [IO.File]::WriteAllText($statePath, ($snoozed | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding($false)))
        $scriptPath = Join-Path $PSScriptRoot '..\codex-worker\Invoke-DeploymentNotifier.ps1'
        $invokeNotifier = {
            param([string] $Path)
            Push-Location $unrelatedRoot
            try {
                $text = & pwsh.exe -NoProfile -Sta -File $scriptPath -ConfigPath $Path 2>&1 | Out-String
                return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $text }
            } finally {
                Pop-Location
            }
        }.GetNewClosure()

        $valid = & $invokeNotifier $configPath
        $valid.ExitCode | Should Be 0
        $valid.Output | Should Not Match 'unknown parameter.*ConfigPath'
        $valid.Output | Should Match 'snoozed'
        $valid.Output | Should Not Match 'Idle'

        $malformedPath = Join-Path $dataRoot 'malformed.json'
        [IO.File]::WriteAllText($malformedPath, '{not-json')
        $malformed = & $invokeNotifier $malformedPath
        $malformed.ExitCode | Should Not Be 0
        $malformed.Output | Should Match 'malformed'

        $untrustedDataRoot = Join-Path $repoRoot 'worker-data'
        New-Item -ItemType Directory -Path $untrustedDataRoot -Force | Out-Null
        $untrustedPath = Join-Path $untrustedDataRoot 'config.json'
        $untrustedConfig = [ordered]@{ repositoryRoot = $repoRoot; dataRoot = $untrustedDataRoot; configPath = $untrustedPath }
        [IO.File]::WriteAllText($untrustedPath, ($untrustedConfig | ConvertTo-Json))
        $untrusted = & $invokeNotifier $untrustedPath
        $untrusted.ExitCode | Should Not Be 0
        $untrusted.Output | Should Match 'outside the repository'
    }

    It 'rejects missing, malformed, and repository-contained notifier configs' {
        { Read-CodexDeploymentNotifierConfig -ConfigPath (Join-Path $TestDrive 'missing.json') } | Should Throw 'not found'
        $bad = Join-Path $TestDrive 'bad.json'; [IO.File]::WriteAllText($bad, '{bad-json')
        { Read-CodexDeploymentNotifierConfig -ConfigPath $bad } | Should Throw 'malformed'
        $repo = Join-Path $TestDrive 'repo'; New-Item -ItemType Directory -Path $repo -Force | Out-Null
        $inside = Join-Path $repo 'config.json'
        [IO.File]::WriteAllText($inside, (([ordered]@{ repositoryRoot = $repo; dataRoot = $repo; configPath = $inside } | ConvertTo-Json)))
        { Read-CodexDeploymentNotifierConfig -ConfigPath $inside } | Should Throw 'outside the repository'
    }

    It 'polls independent durable snapshots and only shows due available state' {
        $first = New-NotificationState -Status 'snoozed' -SnoozeUntil ([DateTime]::UtcNow.AddHours(1).ToString('o'))
        $second = New-NotificationState
        $durable = [pscustomobject]@{ Value = $second }
        $reads = New-Object 'System.Collections.Generic.List[string]'
        $snapshots = New-Object 'System.Collections.Generic.List[object]'
        $sleeps = New-Object 'System.Collections.Generic.List[object]'
        $dialogs = New-Object 'System.Collections.Generic.List[string]'
        $readerState = [pscustomobject]@{ Number = 0 }
        $reader = {
            param($Path)
            $readerState.Number++
            $source = if ($readerState.Number -eq 1) { $first } else { $durable.Value }
            $snapshot = (($source | ConvertTo-Json -Depth 20) | ConvertFrom-Json)
            $reads.Add($Path) | Out-Null
            $snapshots.Add($snapshot) | Out-Null
            return $snapshot
        }.GetNewClosure()
        $writer = {
            param($Path, $Desired)
            $durable.Value = (($Desired | ConvertTo-Json -Depth 20) | ConvertFrom-Json)
        }.GetNewClosure()
        $result = @(Invoke-CodexDeploymentNotifier -Watch -MaxCycles 2 -PollSeconds 5 `
            -StatePath (Join-Path $TestDrive 'state.json') -StateReader $reader -StateWriter $writer `
            -SessionProbe { $true } -LockProvider { 'held' } -UnlockProvider { } `
            -DialogProvider { $dialogs.Add('Cancel') | Out-Null; 'Cancel' } `
            -SleepProvider { param($duration) $sleeps.Add($duration) | Out-Null })

        $reads.Count | Should Be 4
        $sleeps.Count | Should Be 1
        $result.Count | Should Be 2
        $result[0].Status | Should Be 'snoozed'
        $result[1].Status | Should Be 'Cancelled'
        $dialogs.Count | Should Be 1
        $sleeps[0] | Should Be ([TimeSpan]::FromSeconds(5))
        [object]::ReferenceEquals($snapshots[0], $snapshots[1]) | Should Be $false
        [object]::ReferenceEquals($snapshots[1], $snapshots[2]) | Should Be $false
        $durable.Value.deployment | Should Be $null
    }

    It 'fails closed for a Later no-op writer and releases the lock' {
        $durable = [pscustomobject]@{ Value = New-NotificationState }
        $unlocks = New-Object 'System.Collections.Generic.List[object]'
        $threw = $false
        try {
            Invoke-CodexDeploymentNotificationCycle -StatePath (Join-Path $TestDrive 'state.json') `
                -StateReader { param($Path) (($durable.Value | ConvertTo-Json -Depth 20) | ConvertFrom-Json) } `
                -StateWriter { param($Path, $Desired) } -SessionProbe { $true } -LockProvider { 'held' } `
                -UnlockProvider { $unlocks.Add('unlock') | Out-Null } -DialogProvider { 'Later' } | Out-Null
        } catch { $threw = $true }
        $threw | Should Be $true
        $durable.Value.deployment.status | Should Be 'pending'
        $unlocks.Count | Should Be 1
    }

    It 'fails closed for a Later throwing writer and releases the lock' {
        $durable = [pscustomobject]@{ Value = New-NotificationState }
        $unlocks = New-Object 'System.Collections.Generic.List[object]'
        $threw = $false
        try {
            Invoke-CodexDeploymentNotificationCycle -StatePath (Join-Path $TestDrive 'state.json') `
                -StateReader { param($Path) (($durable.Value | ConvertTo-Json -Depth 20) | ConvertFrom-Json) } `
                -StateWriter { throw 'writer failed' } -SessionProbe { $true } -LockProvider { 'held' } `
                -UnlockProvider { $unlocks.Add('unlock') | Out-Null } -DialogProvider { 'Later' } | Out-Null
        } catch { $threw = $true }
        $threw | Should Be $true
        $durable.Value.deployment.status | Should Be 'pending'
        $unlocks.Count | Should Be 1
    }

    It 'fails closed for a Cancel no-op writer and does not clear durable state' {
        $durable = [pscustomobject]@{ Value = New-NotificationState }
        $unlocks = New-Object 'System.Collections.Generic.List[object]'
        $threw = $false
        try {
            Invoke-CodexDeploymentNotificationCycle -StatePath (Join-Path $TestDrive 'state.json') `
                -StateReader { param($Path) (($durable.Value | ConvertTo-Json -Depth 20) | ConvertFrom-Json) } `
                -StateWriter { param($Path, $Desired) } -SessionProbe { $true } -LockProvider { 'held' } `
                -UnlockProvider { $unlocks.Add('unlock') | Out-Null } -DialogProvider { 'Cancel' } | Out-Null
        } catch { $threw = $true }
        $threw | Should Be $true
        $durable.Value.deployment | Should Not Be $null
        $unlocks.Count | Should Be 1
    }

    It 'fails closed for a Cancel throwing writer and does not deploy' {
        $durable = [pscustomobject]@{ Value = New-NotificationState }
        $deploys = New-Object 'System.Collections.Generic.List[object]'
        $unlocks = New-Object 'System.Collections.Generic.List[object]'
        $threw = $false
        try {
            Invoke-CodexDeploymentNotificationCycle -StatePath (Join-Path $TestDrive 'state.json') `
                -StateReader { param($Path) (($durable.Value | ConvertTo-Json -Depth 20) | ConvertFrom-Json) } `
                -StateWriter { throw 'writer failed' } -SessionProbe { $true } -LockProvider { 'held' } `
                -UnlockProvider { $unlocks.Add('unlock') | Out-Null } -DialogProvider { 'Cancel' } `
                -DeployAction { $deploys.Add('deploy') | Out-Null } | Out-Null
        } catch { $threw = $true }
        $threw | Should Be $true
        $durable.Value.deployment | Should Not Be $null
        $deploys.Count | Should Be 0
        $unlocks.Count | Should Be 1
    }

    It 'fails closed for a Later corrupt writer and releases the lock' {
        $durable = [pscustomobject]@{ Value = New-NotificationState }
        $original = (($durable.Value | ConvertTo-Json -Depth 20) | ConvertFrom-Json)
        $unlocks = New-Object 'System.Collections.Generic.List[object]'
        $deploys = New-Object 'System.Collections.Generic.List[object]'
        $threw = $false
        try {
            Invoke-CodexDeploymentNotificationCycle -StatePath (Join-Path $TestDrive 'state.json') `
                -StateReader { param($Path) (($durable.Value | ConvertTo-Json -Depth 20) | ConvertFrom-Json) } `
                -StateWriter {
                    param($Path, $Desired)
                    $durable.Value = New-NotificationState -TargetCommit ('b' * 40)
                } -SessionProbe { $true } -LockProvider { 'held' } `
                -UnlockProvider { $unlocks.Add('unlock') | Out-Null } -DialogProvider { 'Later' } `
                -DeployAction { $deploys.Add('deploy') | Out-Null } | Out-Null
        } catch { $threw = $true }
        $threw | Should Be $true
        [object]::ReferenceEquals($durable.Value, $original) | Should Be $false
        $durable.Value.deployment.targetCommit | Should Be ('b' * 40)
        $deploys.Count | Should Be 0
        $unlocks.Count | Should Be 1
    }

    It 'fails closed for a Cancel corrupt writer and releases the lock' {
        $durable = [pscustomobject]@{ Value = New-NotificationState }
        $original = (($durable.Value | ConvertTo-Json -Depth 20) | ConvertFrom-Json)
        $unlocks = New-Object 'System.Collections.Generic.List[object]'
        $deploys = New-Object 'System.Collections.Generic.List[object]'
        $threw = $false
        try {
            Invoke-CodexDeploymentNotificationCycle -StatePath (Join-Path $TestDrive 'state.json') `
                -StateReader { param($Path) (($durable.Value | ConvertTo-Json -Depth 20) | ConvertFrom-Json) } `
                -StateWriter {
                    param($Path, $Desired)
                    $durable.Value = New-NotificationState -TargetCommit ('c' * 40)
                } -SessionProbe { $true } -LockProvider { 'held' } `
                -UnlockProvider { $unlocks.Add('unlock') | Out-Null } -DialogProvider { 'Cancel' } `
                -DeployAction { $deploys.Add('deploy') | Out-Null } | Out-Null
        } catch { $threw = $true }
        $threw | Should Be $true
        [object]::ReferenceEquals($durable.Value, $original) | Should Be $false
        $durable.Value.deployment.targetCommit | Should Be ('c' * 40)
        $durable.Value.deployment | Should Not Be $null
        $deploys.Count | Should Be 0
        $unlocks.Count | Should Be 1
    }

    It 'keeps the lock through the injected deploy action' {
        $state = New-NotificationState
        $events = New-Object 'System.Collections.Generic.List[string]'
        $durable = [pscustomobject]@{ Value = $state }
        $writer = {
            param($Path, $Desired)
            $events.Add('persist') | Out-Null
            $durable.Value = (($Desired | ConvertTo-Json -Depth 20) | ConvertFrom-Json)
        }.GetNewClosure()

        $result = Invoke-CodexDeploymentNotificationCycle -StatePath (Join-Path $TestDrive 'state.json') `
            -StateReader { param($Path) $durable.Value } -StateWriter $writer -SessionProbe { $true } `
            -LockProvider { $events.Add('lock') | Out-Null; 'held' } `
            -UnlockProvider { $events.Add('unlock') | Out-Null } -DialogProvider { 'Deploy' } `
            -DeployAction { $events.Add('deploy') | Out-Null }

        $result.Status | Should Be 'Deployed'
        ($events -join ',') | Should Be 'lock,deploy,persist,unlock'
        $durable.Value.deployment | Should Be $null
    }

    It 'preserves pending deployment when the deployment action returns Boolean false' {
        $durable = [pscustomobject]@{ Value = New-NotificationState }
        $unlocks = New-Object 'System.Collections.Generic.List[string]'
        $threw = $false
        try {
            Invoke-CodexDeploymentNotificationCycle -StatePath (Join-Path $TestDrive 'state.json') `
                -StateReader { param($Path) (($durable.Value | ConvertTo-Json -Depth 20) | ConvertFrom-Json) } `
                -StateWriter { param($Path, $Desired) $durable.Value = (($Desired | ConvertTo-Json -Depth 20) | ConvertFrom-Json) } `
                -SessionProbe { $true } -LockProvider { 'held' } -UnlockProvider { $unlocks.Add('unlock') | Out-Null } `
                -DialogProvider { 'Deploy' } -DeployAction { $false } | Out-Null
        } catch { $threw = $true }
        $threw | Should Be $true
        $durable.Value.deployment | Should Not Be $null
        $unlocks.Count | Should Be 1
    }

    It 'preserves failed deployment evidence when the action returns a failed deployment object' {
        $durable = [pscustomobject]@{ Value = New-NotificationState }
        $unlocks = New-Object 'System.Collections.Generic.List[string]'
        $failed = [pscustomobject]@{ Success = $false; State = [pscustomobject]@{ activeSlot = 'runtime-a'; deployment = [pscustomobject]@{ status = 'rollback-failed'; evidence = [pscustomobject]@{ logs = @('failed.log','rollback.log') } } } }
        $threw = $false
        try {
            Invoke-CodexDeploymentNotificationCycle -StatePath (Join-Path $TestDrive 'state.json') `
                -StateReader { param($Path) (($durable.Value | ConvertTo-Json -Depth 20) | ConvertFrom-Json) } `
                -StateWriter { param($Path, $Desired) $durable.Value = (($Desired | ConvertTo-Json -Depth 20) | ConvertFrom-Json) } `
                -SessionProbe { $true } -LockProvider { 'held' } -UnlockProvider { $unlocks.Add('unlock') | Out-Null } `
                -DialogProvider { 'Deploy' } -DeployAction { $failed } | Out-Null
        } catch { $threw = $true }
        $threw | Should Be $true
        $durable.Value.deployment | Should Not Be $null
        $unlocks.Count | Should Be 1
    }

    It 'does not show a snoozed deployment until its due time' {
        $now = [DateTime]::Parse('2026-08-18T01:00:00Z')
        $state = New-NotificationState -Status 'snoozed' -SnoozeUntil $now.AddMinutes(1).ToString('o')
        $shown = 0
        $result = Invoke-CodexDeploymentNotificationCycle -NowProvider { $now } `
            -StateReader { param($Path) $state } -SessionProbe { throw 'session must not be probed' } `
            -DialogProvider { $shown++; 'Deploy' }

        $result.Status | Should Be 'Snoozed'
        $shown | Should Be 0
    }

    It 'preserves an existing snooze when Task 10 coalesces a newer merge' {
        $old = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
        $new = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
        $snooze = '2026-08-18T03:00:00Z'
        $state = New-NotificationState -Status 'snoozed' -SnoozeUntil $snooze -TargetCommit $old
        $master = 'cccccccccccccccccccccccccccccccccccccccc'
        $git = {
            param([string[]] $Arguments)
            if ($Arguments -contains 'rev-parse') { return $master }
            if ($Arguments -contains 'merge-base') {
                $left = $Arguments[$Arguments.IndexOf('--is-ancestor') + 1]
                $right = $Arguments[$Arguments.IndexOf('--is-ancestor') + 2]
                if (($left -eq $new -and $right -eq $master) -or ($left -eq $old -and $right -eq $master) -or ($left -eq $old -and $right -eq $new)) { return '' }
                throw 'not ancestor'
            }
            return ''
        }

        $result = Register-CodexPendingDeployment -RepositoryRoot 'C:\repo' -DataRoot $TestDrive `
            -MergeCommitSha $new -PullRequestNumber 18 -State $state -GitCommandRunner $git `
            -Now ([DateTime]::Parse('2026-08-18T01:00:00Z'))

        $result.targetCommit | Should Be $new
        $result.snoozeUntil | Should Be $snooze
        $result.status | Should Be 'snoozed'
    }

    It 'exports the notification boundary and keeps the real dialog behind a provider seam' {
        $module = Get-Module CodexWorker
        $module.ExportedFunctions.ContainsKey('Invoke-CodexDeploymentNotificationCycle') | Should Be $true
        $module.ExportedFunctions.ContainsKey('Invoke-CodexDeploymentNotifier') | Should Be $true
        $module.ExportedFunctions.ContainsKey('Test-CodexInteractiveSession') | Should Be $true
        (Get-Command Show-CodexDeploymentDialog -Module CodexWorker).CommandType | Should Be 'Function'
    }
}
