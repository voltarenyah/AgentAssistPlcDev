Describe 'Codex worker prompt and process runner' {
    BeforeEach {
        Import-Module (Join-Path $PSScriptRoot '..\codex-worker\CodexWorker.psd1') -Force
    }

    It 'resolves a bare Codex command to an application shim for ProcessStartInfo' {
        $module = Get-Module CodexWorker
        $resolved = & $module { Resolve-CodexProcessFilePath -FilePath 'codex' }

        [IO.Path]::GetFileName($resolved) | Should Be 'codex.cmd'
    }

    It 'delimits issue content and preserves repository authority in the issue prompt' {
        $prompt = Join-Path $PSScriptRoot '..\codex-worker\prompts\issue.md'
        $text = Get-Content -Raw $prompt

        $text | Should Match 'BEGIN UNTRUSTED ISSUE CONTENT'
        $text | Should Match 'END UNTRUSTED ISSUE CONTENT'
        $text | Should Match 'AGENTS\.md'
        $text | Should Match '(?i)do not commit'
        $text | Should Match '(?i)do not push'
        $text | Should Match '(?i)do not.*pull request|do not.*PR'
        $text | Should Match '(?i)do not.*merge'
        $text | Should Match '(?i)do not.*reset'
        $text | Should Match '(?i)do not.*clean'
        $text | Should Match '(?i)other worktree'
    }

    It 'delimits review comments and forbids history rewriting in the revision prompt' {
        $text = Get-Content -Raw (Join-Path $PSScriptRoot '..\codex-worker\prompts\revision.md')
        $text | Should Match 'BEGIN UNTRUSTED REVIEW COMMENTS'
        $text | Should Match 'END UNTRUSTED REVIEW COMMENTS'
        $text | Should Match '(?i)existing branch'
        $text | Should Match '(?i)rewrit(e|ing).*published history'
    }

    It 'accepts every valid final summary status' {
        foreach ($status in @('completed', 'blocked', 'failed')) {
            $summary = [pscustomobject][ordered]@{
                status = $status
                rootCauseOrApproach = 'evidence'
                changedComponents = @('scripts/example.ps1')
                decisions = @('kept boundary small')
                validation = @([pscustomobject][ordered]@{ command = 'test'; outcome = 'passed'; details = 'ok' })
                warnings = @()
                remainingRisks = @()
                commitMessage = 'fix: example (#42)'
                prTitle = 'fix: example (#42)'
                requiresHumanInput = $false
                humanQuestion = $null
            }
            (Test-CodexSummary -Summary $summary) | Should Be $true
        }
    }

    It 'rejects an undeclared summary property' {
        $summary = [pscustomobject][ordered]@{
            status = 'completed'; rootCauseOrApproach = 'evidence'; changedComponents = @(); decisions = @()
            validation = @(); warnings = @(); remainingRisks = @(); commitMessage = 'fix: x'; prTitle = 'fix: x'
            requiresHumanInput = $false; humanQuestion = $null; unexpected = 'no'
        }
        (Test-CodexSummary -Summary $summary) | Should Be $false
    }

    It 'rejects scalar values where the schema requires arrays' {
        $summary = [pscustomobject][ordered]@{
            status = 'completed'; rootCauseOrApproach = 'evidence'; changedComponents = 'one'; decisions = 'one'
            validation = [pscustomobject]@{ command = 'test'; outcome = 'passed'; details = 'ok' }
            warnings = 'warning'; remainingRisks = @(); commitMessage = 'fix: x'; prTitle = 'fix: x'
            requiresHumanInput = $false; humanQuestion = $null
        }
        (Test-CodexSummary -Summary $summary) | Should Be $false
    }

    It 'preserves JSONL, logs readable events, captures thread ID, and scrubs secrets' {
        $fakeRoot = Join-Path $TestDrive 'fake-codex'
        New-Item -ItemType Directory -Path $fakeRoot -Force | Out-Null
        $fakePs = Join-Path $fakeRoot 'fake.ps1'
        $fakeCmd = Join-Path $fakeRoot 'codex.cmd'
        @'
$Arguments = $args
$summaryPath = $Arguments[($Arguments.IndexOf('--output-last-message') + 1)]
[IO.File]::WriteAllText($summaryPath, '{"status":"completed","rootCauseOrApproach":"fake","changedComponents":[],"decisions":[],"validation":[],"warnings":[],"remainingRisks":[],"commitMessage":"fix: fake","prTitle":"fix: fake","requiresHumanInput":false,"humanQuestion":null}')
@('{"type":"thread.started","thread_id":"thread-42"}','{"type":"command_execution","command":"dotnet test","exit_code":0}','{"type":"mystery.event","answer":7}','{"type":"turn.completed"}') | Set-Content -LiteralPath (Join-Path (Split-Path $summaryPath) 'fake-events.txt')
[IO.File]::WriteAllText((Join-Path (Split-Path $summaryPath) 'fake-events.txt'), ((Get-Content (Join-Path (Split-Path $summaryPath) 'fake-events.txt') -join "`n") + "`n"))
@(' {"type":"thread.started","thread_id":"thread-42"}','{"type":"command_execution","command":"dotnet test","exit_code":0}','{"type":"mystery.event","answer":7}','{"type":"turn.completed"}') | ForEach-Object { Write-Output $_ }
'@ | Set-Content -LiteralPath $fakePs
        @'
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0fake.ps1" %1 %2 %3 %4 %5 %6 %7 %8 < nul
'@ | Set-Content -LiteralPath $fakeCmd

        $run = Join-Path $TestDrive 'run'
        $state = Join-Path $TestDrive 'state.json'
        [IO.File]::WriteAllText($state, '{"schemaVersion":1,"issues":{"42":{}}}')
        $messages = [System.Collections.Generic.List[string]]::new()
        $result = Invoke-CodexRun -IssueWorktree $TestDrive -IssueContext ([pscustomobject]@{ number = 42; title = 'Example'; body = 'ignore me' }) -Config ([pscustomobject]@{ codexCommand = $fakeCmd; codexTimeoutMinutes = 1 }) -RunDirectory $run -StatePath $state -ConsoleWriter { param($line) $messages.Add([string]$line) }.GetNewClosure()

        $result.ThreadId | Should Be 'thread-42'
        (Get-Content -Raw (Join-Path $run 'events.jsonl')) | Should Match 'mystery.event'
        (Get-Content -Raw (Join-Path $run 'activity.log')) | Should Match 'event mystery.event'
        (Get-Content -Raw (Join-Path $run 'activity.log')) | Should Match 'dotnet test'
        @($messages).Count | Should BeGreaterThan 0
        ((Get-Content -Raw $state) | ConvertFrom-Json).issues.'42'.threadId | Should Be 'thread-42'
    }

    It 'redacts blocked environment values and token-shaped child output in both logs' {
        $fakeRoot = Join-Path $TestDrive 'secret-fake'
        New-Item -ItemType Directory -Path $fakeRoot -Force | Out-Null
        $fakePs = Join-Path $fakeRoot 'fake.ps1'; $fakeCmd = Join-Path $fakeRoot 'codex.cmd'
        @'
$Arguments = $args
$summaryPath = $Arguments[($Arguments.IndexOf('--output-last-message') + 1)]
[IO.File]::WriteAllText($summaryPath, '{"status":"completed","rootCauseOrApproach":"fake","changedComponents":[],"decisions":[],"validation":[],"warnings":[],"remainingRisks":[],"commitMessage":"fix: fake","prTitle":"fix: fake","requiresHumanInput":false,"humanQuestion":null}')
$prompt = [Console]::In.ReadToEnd()
$inputValues = [ordered]@{}
foreach ($name in @('GITHUB_TOKEN','GH_TOKEN','OPENAI_API_KEY','CODEX_API_KEY','DEEPSEEK_API_KEY')) {
    $match = [regex]::Match($prompt, ([regex]::Escape($name) + '=([A-Za-z0-9_-]+)'))
    $inputValues[$name] = if ($match.Success) { $match.Groups[1].Value } else { '' }
}
$event = [ordered]@{
    type = 'agent_message'
    item = [ordered]@{ text = (($inputValues.Values -join ' ') + ' github_pat_child_secret'); nested = [ordered]@{ value = 'sk-child-secret' } }
    environment = [ordered]@{
        GITHUB_TOKEN = [string]::IsNullOrEmpty($env:GITHUB_TOKEN)
        GH_TOKEN = [string]::IsNullOrEmpty($env:GH_TOKEN)
        OPENAI_API_KEY = [string]::IsNullOrEmpty($env:OPENAI_API_KEY)
        CODEX_API_KEY = [string]::IsNullOrEmpty($env:CODEX_API_KEY)
        DEEPSEEK_API_KEY = [string]::IsNullOrEmpty($env:DEEPSEEK_API_KEY)
    }
}
$event | ConvertTo-Json -Compress -Depth 5 | Write-Output
'@ | Set-Content -LiteralPath $fakePs
        @'
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0fake.ps1" %1 %2 %3 %4 %5 %6 %7 %8
'@ | Set-Content -LiteralPath $fakeCmd
        $oldSecrets = @{}
        foreach ($name in @('GITHUB_TOKEN','GH_TOKEN','OPENAI_API_KEY','CODEX_API_KEY','DEEPSEEK_API_KEY')) { $oldSecrets[$name] = [Environment]::GetEnvironmentVariable($name) }
        $syntheticSecrets = [ordered]@{ GITHUB_TOKEN = 'github-parent-secret'; GH_TOKEN = 'gh-parent-secret'; OPENAI_API_KEY = 'openai-parent-secret'; CODEX_API_KEY = 'codex-parent-secret'; DEEPSEEK_API_KEY = 'deepseek-parent-secret' }
        try {
            foreach ($name in $syntheticSecrets.Keys) { [Environment]::SetEnvironmentVariable($name, $syntheticSecrets[$name], 'Process') }
            $run = Join-Path $TestDrive 'secret-run'; $state = Join-Path $TestDrive 'secret-state.json'
            [IO.File]::WriteAllText($state, '{"schemaVersion":1,"issues":{"42":{}}}')
            $body = (($syntheticSecrets.Keys | ForEach-Object { '{0}={1}' -f $_, $syntheticSecrets[$_] }) -join ';')
            $result = Invoke-CodexRun -IssueWorktree $TestDrive -IssueContext ([pscustomobject]@{ number = 42; title = 'Example'; body = $body }) -Config ([pscustomobject]@{ codexCommand = $fakeCmd; codexTimeoutMinutes = 1 }) -RunDirectory $run -StatePath $state
            $events = Get-Content -Raw (Join-Path $run 'events.jsonl'); $activity = Get-Content -Raw (Join-Path $run 'activity.log')
            $eventObject = $events | ConvertFrom-Json
            $eventObject.environment.GITHUB_TOKEN | Should Be $true
            $eventObject.environment.GH_TOKEN | Should Be $true
            $eventObject.environment.OPENAI_API_KEY | Should Be $true
            $eventObject.environment.CODEX_API_KEY | Should Be $true
            $eventObject.environment.DEEPSEEK_API_KEY | Should Be $true
            $eventObject.item.text | Should Be '[REDACTED] [REDACTED] [REDACTED] [REDACTED] [REDACTED] [REDACTED]'
            $eventObject.item.nested.value | Should Be '[REDACTED]'
            foreach ($name in $syntheticSecrets.Keys) {
                $events | Should Not Match ([regex]::Escape($syntheticSecrets[$name]))
                $activity | Should Not Match ([regex]::Escape($syntheticSecrets[$name]))
            }
            $events | Should Not Match 'github_pat_child_secret|sk-child-secret'
            $activity | Should Not Match 'github_pat_child_secret|sk-child-secret'
        } finally { foreach ($name in $oldSecrets.Keys) { if ($null -eq $oldSecrets[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue } else { Set-Item -LiteralPath "Env:$name" -Value $oldSecrets[$name] } } }
    }

    It 'streams an activity line before a child exits' {
        $fakeRoot = Join-Path $TestDrive 'stream-fake'; New-Item -ItemType Directory -Path $fakeRoot -Force | Out-Null
        $fakePs = Join-Path $fakeRoot 'fake.ps1'; $fakeCmd = Join-Path $fakeRoot 'codex.cmd'; $gate = Join-Path $fakeRoot 'gate'
        @'
$Arguments = $args; $summaryPath = $Arguments[($Arguments.IndexOf('--output-last-message') + 1)]
Write-Output '{"type":"agent_message","text":"early"}'
$gate = [IO.Path]::Combine((Split-Path $summaryPath), 'gate')
for ($i = 0; $i -lt 40 -and -not (Test-Path $gate); $i++) { Start-Sleep -Milliseconds 50 }
[IO.File]::WriteAllText($summaryPath, '{"status":"completed","rootCauseOrApproach":"fake","changedComponents":[],"decisions":[],"validation":[],"warnings":[],"remainingRisks":[],"commitMessage":"fix: fake","prTitle":"fix: fake","requiresHumanInput":false,"humanQuestion":null}')
'@ | Set-Content -LiteralPath $fakePs
        @'
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0fake.ps1" %1 %2 %3 %4 %5 %6 %7 %8 < nul
'@ | Set-Content -LiteralPath $fakeCmd
        $run = Join-Path $TestDrive 'stream-run'; $state = Join-Path $TestDrive 'stream-state.json'; [IO.File]::WriteAllText($state, '{"schemaVersion":1,"issues":{"42":{}}}')
        $result = Invoke-CodexRun -IssueWorktree $TestDrive -IssueContext ([pscustomobject]@{ number = 42; title = 'Example' }) -Config ([pscustomobject]@{ codexCommand = $fakeCmd; codexTimeoutMinutes = 1 }) -RunDirectory $run -StatePath $state -ConsoleWriter { param($line) if ($line -match 'early') { [IO.File]::WriteAllText($gate, 'seen') } }.GetNewClosure()
        $result.Classification | Should Be 'completed'
        (Get-Content -Raw (Join-Path $run 'activity.log')) | Should Match 'early'
    }

    It 'terminates a timed-out child within a bounded duration' {
        $fakeRoot = Join-Path $TestDrive 'timeout-fake'; New-Item -ItemType Directory -Path $fakeRoot -Force | Out-Null
        $fakePs = Join-Path $fakeRoot 'fake.ps1'; $fakeCmd = Join-Path $fakeRoot 'codex.cmd'
        @'
$Arguments = $args
$summaryPath = $Arguments[($Arguments.IndexOf('--output-last-message') + 1)]
[IO.File]::WriteAllText($summaryPath, '{"status":"completed","rootCauseOrApproach":"fake","changedComponents":[],"decisions":[],"validation":[],"warnings":[],"remainingRisks":[],"commitMessage":"fix: fake","prTitle":"fix: fake","requiresHumanInput":false,"humanQuestion":null}')
Start-Sleep -Seconds 30
'@ | Set-Content -LiteralPath $fakePs
        @'
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0fake.ps1" %1 %2 %3 %4 %5 %6 %7 %8 < nul
'@ | Set-Content -LiteralPath $fakeCmd
        $run = Join-Path $TestDrive 'timeout-run'; $state = Join-Path $TestDrive 'timeout-state.json'; [IO.File]::WriteAllText($state, '{"schemaVersion":1,"issues":{"42":{}}}')
        $watch = [Diagnostics.Stopwatch]::StartNew(); $result = Invoke-CodexRun -IssueWorktree (Get-Location).Path -IssueContext ([pscustomobject]@{ number = 42; title = 'Example' }) -Config ([pscustomobject]@{ codexCommand = $fakeCmd; codexTimeoutMinutes = 0.01 }) -RunDirectory $run -StatePath $state; $watch.Stop()
        $result.Classification | Should Be 'timeout'; $result.Status | Should Be 'failed'; $watch.Elapsed.TotalSeconds | Should BeLessThan 8
    }

    It 'closes stdin after writing so an EOF-reading child completes normally' {
        $fakeRoot = Join-Path $TestDrive 'stdin-eof-fake'; New-Item -ItemType Directory -Path $fakeRoot -Force | Out-Null
        $fakePs = Join-Path $fakeRoot 'fake.ps1'; $fakeCmd = Join-Path $fakeRoot 'codex.cmd'
        @'
$Arguments = $args
$summaryPath = $Arguments[($Arguments.IndexOf('--output-last-message') + 1)]
[Console]::In.ReadToEnd() | Out-Null
[IO.File]::WriteAllText($summaryPath, '{"status":"completed","rootCauseOrApproach":"fake","changedComponents":[],"decisions":[],"validation":[],"warnings":[],"remainingRisks":[],"commitMessage":"fix: fake","prTitle":"fix: fake","requiresHumanInput":false,"humanQuestion":null}')
'@ | Set-Content -LiteralPath $fakePs
        @'
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0fake.ps1" %1 %2 %3 %4 %5 %6 %7 %8
'@ | Set-Content -LiteralPath $fakeCmd
        $run = Join-Path $TestDrive 'stdin-eof-run'; $state = Join-Path $TestDrive 'stdin-eof-state.json'; [IO.File]::WriteAllText($state, '{"schemaVersion":1,"issues":{"42":{}}}')
        $watch = [Diagnostics.Stopwatch]::StartNew(); $result = Invoke-CodexRun -IssueWorktree (Get-Location).Path -IssueContext ([pscustomobject]@{ number = 42 }) -Config ([pscustomobject]@{ codexCommand = $fakeCmd; codexTimeoutMinutes = 0.1 }) -RunDirectory $run -StatePath $state; $watch.Stop()
        $result.Classification | Should Be 'completed'; $result.Status | Should Be 'completed'; $watch.Elapsed.TotalSeconds | Should BeLessThan 8
    }

    It 'uses resume output controls when the installation capability is available' {
        $fakeRoot = Join-Path $TestDrive 'resume-fake'
        New-Item -ItemType Directory -Path $fakeRoot -Force | Out-Null
        $fakePs = Join-Path $fakeRoot 'fake.ps1'
        $fakeCmd = Join-Path $fakeRoot 'codex.cmd'
        @'
$Arguments = $args
$summaryPath = $Arguments[($Arguments.IndexOf('--output-last-message') + 1)]
[IO.File]::WriteAllText($summaryPath, '{"status":"completed","rootCauseOrApproach":"fake","changedComponents":[],"decisions":[],"validation":[],"warnings":[],"remainingRisks":[],"commitMessage":"fix: fake","prTitle":"fix: fake","requiresHumanInput":false,"humanQuestion":null}')
Write-Output '{"type":"thread.started","thread_id":"revision-thread"}'
'@ | Set-Content -LiteralPath $fakePs
        @'
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0fake.ps1" %1 %2 %3 %4 %5 %6 %7 %8 < nul
'@ | Set-Content -LiteralPath $fakeCmd
        $run = Join-Path $TestDrive 'resume-run'; $state = Join-Path $TestDrive 'resume-state.json'; [IO.File]::WriteAllText($state, '{"schemaVersion":1,"issues":{"42":{}}}')
        $result = Invoke-CodexRun -IssueWorktree $TestDrive -IssueContext ([pscustomobject]@{ number = 42; title = 'Example' }) -Config ([pscustomobject]@{ codexCommand = $fakeCmd; supportsResumeOutputControls = $true; codexTimeoutMinutes = 1 }) -RunDirectory $run -StatePath $state -Revision -ThreadId 'old-thread'
        $result.Arguments[8] | Should Be 'resume'
        $result.Arguments[9] | Should Be 'old-thread'
    }

    It 'records a fresh revision fallback when resume output controls are unavailable' {
        $fakeRoot = Join-Path $TestDrive 'fresh-fake'
        New-Item -ItemType Directory -Path $fakeRoot -Force | Out-Null
        $fakePs = Join-Path $fakeRoot 'fake.ps1'
        $fakeCmd = Join-Path $fakeRoot 'codex.cmd'
        @'
$Arguments = $args
$summaryPath = $Arguments[($Arguments.IndexOf('--output-last-message') + 1)]
[IO.File]::WriteAllText($summaryPath, '{"status":"completed","rootCauseOrApproach":"fake","changedComponents":[],"decisions":[],"validation":[],"warnings":[],"remainingRisks":[],"commitMessage":"fix: fake","prTitle":"fix: fake","requiresHumanInput":false,"humanQuestion":null}')
'@ | Set-Content -LiteralPath $fakePs
        @'
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0fake.ps1" %1 %2 %3 %4 %5 %6 %7 %8 < nul
'@ | Set-Content -LiteralPath $fakeCmd
        $run = Join-Path $TestDrive 'fresh-run'; $state = Join-Path $TestDrive 'fresh-state.json'; [IO.File]::WriteAllText($state, '{"schemaVersion":1,"issues":{"42":{}}}')
        $result = Invoke-CodexRun -IssueWorktree $TestDrive -IssueContext ([pscustomobject]@{ number = 42; title = 'Example' }) -Config ([pscustomobject]@{ codexCommand = $fakeCmd; supportsResumeOutputControls = $false; codexTimeoutMinutes = 1 }) -RunDirectory $run -StatePath $state -Revision -ThreadId 'old-thread'
        $result.RevisionFallback | Should Be $true
        (Get-Content -Raw (Join-Path $run 'activity.log')) | Should Match 'resume output controls unavailable; started fresh revision thread'
        $result.Arguments -contains 'resume' | Should Be $false
    }

    It 'initializes and atomically persists resume capability from help output' {
        $fakeRoot = Join-Path $TestDrive 'init-fake'; New-Item -ItemType Directory -Path $fakeRoot -Force | Out-Null
        $supported = Join-Path $fakeRoot 'supported.cmd'; $unsupported = Join-Path $fakeRoot 'unsupported.cmd'
        @'
@echo off
exit /b 0
'@ | Set-Content -LiteralPath $supported
        @'
@echo off
exit /b 1
'@ | Set-Content -LiteralPath $unsupported
        $configPath = Join-Path $TestDrive 'generatedconfig.json'; $config = [pscustomobject]@{ codexCommand = $supported }
        (Initialize-CodexResumeCapability -IssueWorktree $TestDrive -Config $config -ConfigPath $configPath) | Should Be $true
        (Get-Content -Raw $configPath) | Should Match 'supportsResumeOutputControls'
        $config2 = [pscustomobject]@{ codexCommand = $unsupported }
        (Initialize-CodexResumeCapability -IssueWorktree $TestDrive -Config $config2 -ConfigPath (Join-Path $TestDrive 'generatedconfig2.json')) | Should Be $false
    }

    It 'surfaces durable thread-state write failures' {
        $fakeRoot = Join-Path $TestDrive 'state-fail-fake'; New-Item -ItemType Directory -Path $fakeRoot -Force | Out-Null
        $fakePs = Join-Path $fakeRoot 'fake.ps1'; $fakeCmd = Join-Path $fakeRoot 'codex.cmd'
        @'
$Arguments = $args; $summaryPath = $Arguments[($Arguments.IndexOf('--output-last-message') + 1)]
[IO.File]::WriteAllText($summaryPath, '{"status":"completed","rootCauseOrApproach":"fake","changedComponents":[],"decisions":[],"validation":[],"warnings":[],"remainingRisks":[],"commitMessage":"fix: fake","prTitle":"fix: fake","requiresHumanInput":false,"humanQuestion":null}')
Write-Output '{"type":"thread.started","thread_id":"thread-fail"}'
'@ | Set-Content -LiteralPath $fakePs
        @'
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0fake.ps1" %1 %2 %3 %4 %5 %6 %7 %8 < nul
'@ | Set-Content -LiteralPath $fakeCmd
        $stateDirectory = Join-Path $TestDrive 'state-directory'; New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
        { Invoke-CodexRun -IssueWorktree $TestDrive -IssueContext ([pscustomobject]@{ number = 42 }) -Config ([pscustomobject]@{ codexCommand = $fakeCmd; codexTimeoutMinutes = 1 }) -RunDirectory (Join-Path $TestDrive 'state-fail-run') -StatePath $stateDirectory } | Should Throw
    }

    It 'classifies authentication, transient, ordinary nonzero, and malformed exits' {
        $fakeRoot = Join-Path $TestDrive 'classify-fake'; New-Item -ItemType Directory -Path $fakeRoot -Force | Out-Null
        $fakePs = Join-Path $fakeRoot 'fake.ps1'; $fakeCmd = Join-Path $fakeRoot 'codex.cmd'; $modePath = Join-Path $fakeRoot 'mode'
        @'
$Arguments = $args; $mode = Get-Content (Join-Path $PSScriptRoot 'mode') -Raw; $summaryPath = $Arguments[($Arguments.IndexOf('--output-last-message') + 1)]
if ($mode -match 'auth') { [Console]::Error.WriteLine('authentication failed'); exit 3 }
if ($mode -match 'network') { [Console]::Error.WriteLine('network service unavailable'); exit 3 }
if ($mode -match 'ordinary') { [IO.File]::WriteAllText($summaryPath, '{"status":"completed","rootCauseOrApproach":"fake","changedComponents":[],"decisions":[],"validation":[],"warnings":[],"remainingRisks":[],"commitMessage":"fix: fake","prTitle":"fix: fake","requiresHumanInput":false,"humanQuestion":null}'); exit 3 }
'@ | Set-Content -LiteralPath $fakePs
        @'
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0fake.ps1" %1 %2 %3 %4 %5 %6 %7 %8 < nul
'@ | Set-Content -LiteralPath $fakeCmd
        $state = Join-Path $TestDrive 'classify-state.json'; [IO.File]::WriteAllText($state, '{"schemaVersion":1,"issues":{"42":{}}}')
        foreach ($mode in @('auth','network','ordinary','malformed')) {
            Set-Content -LiteralPath $modePath -Value $mode
            $result = Invoke-CodexRun -IssueWorktree $TestDrive -IssueContext ([pscustomobject]@{ number = 42 }) -Config ([pscustomobject]@{ codexCommand = $fakeCmd; codexTimeoutMinutes = 1 }) -RunDirectory (Join-Path $TestDrive "classify-$mode") -StatePath $state
            if ($mode -eq 'auth') { $result.Classification | Should Be 'authentication'; $result.Status | Should Be 'failed' }
            elseif ($mode -eq 'network') { $result.Classification | Should Be 'transient_service_unavailable'; $result.Status | Should Be 'failed' }
            elseif ($mode -eq 'ordinary') { $result.Classification | Should Be 'process_failed'; $result.Status | Should Be 'failed' }
            else { $result.Classification | Should Be 'malformed_summary' }
        }
    }

    It 'classifies a missing Codex executable as missing_executable' {
        $state = Join-Path $TestDrive 'missing-executable-state.json'
        [IO.File]::WriteAllText($state, '{"schemaVersion":1,"issues":{"42":{}}}')
        $result = Invoke-CodexRun -IssueWorktree $TestDrive -IssueContext ([pscustomobject]@{ number = 42 }) -Config ([pscustomobject]@{ codexCommand = (Join-Path $TestDrive 'does-not-exist.cmd'); codexTimeoutMinutes = 1 }) -RunDirectory (Join-Path $TestDrive 'missing-executable-run') -StatePath $state
        $result.Classification | Should Be 'missing_executable'
    }
}
