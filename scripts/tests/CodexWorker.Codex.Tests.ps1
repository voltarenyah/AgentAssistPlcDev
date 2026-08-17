Describe 'Codex worker prompt and process runner' {
    BeforeEach {
        Import-Module (Join-Path $PSScriptRoot '..\codex-worker\CodexWorker.psd1') -Force
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
        $run = Join-Path $TestDrive 'resume-run'
        $result = Invoke-CodexRun -IssueWorktree $TestDrive -IssueContext ([pscustomobject]@{ number = 42; title = 'Example' }) -Config ([pscustomobject]@{ codexCommand = $fakeCmd; supportsResumeOutputControls = $true; codexTimeoutMinutes = 1 }) -RunDirectory $run -Revision -ThreadId 'old-thread'
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
        $run = Join-Path $TestDrive 'fresh-run'
        $result = Invoke-CodexRun -IssueWorktree $TestDrive -IssueContext ([pscustomobject]@{ number = 42; title = 'Example' }) -Config ([pscustomobject]@{ codexCommand = $fakeCmd; supportsResumeOutputControls = $false; codexTimeoutMinutes = 1 }) -RunDirectory $run -Revision -ThreadId 'old-thread'
        $result.RevisionFallback | Should Be $true
        (Get-Content -Raw (Join-Path $run 'activity.log')) | Should Match 'resume output controls unavailable; started fresh revision thread'
        $result.Arguments -contains 'resume' | Should Be $false
    }
}
