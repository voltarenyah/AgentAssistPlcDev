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
        $durable.Value.deployment.snoozeUntil | Should Be $now.AddMinutes(5).ToUniversalTime().ToString('o')
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
            -DialogProvider { 'Later' }

        $result.Decision | Should Be 'Later'
        $durable.Value.deployment.snoozeUntil | Should Be $now.AddMinutes(5).ToUniversalTime().ToString('o')
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
