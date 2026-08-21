Describe 'Kimi worker adapter' {
    BeforeEach {
        Import-Module (Join-Path $PSScriptRoot '..\codex-worker\CodexWorker.psd1') -Force
        $run = Join-Path $TestDrive 'run'
        $state = Join-Path $TestDrive 'state.json'
        New-Item -ItemType Directory -Path $run -Force | Out-Null
        $fakeKimi = Join-Path $TestDrive 'fake-kimi.exe'
        $source = @'
using System;
class FakeKimi {
    static void Main(string[] args) {
        bool malformed = Environment.GetEnvironmentVariable("KIMI_FAKE_MALFORMED") == "1";
        string summary = malformed
            ? "{\"status\":\"completed\",\"rootCauseOrApproach\":\"pilot\"}"
            : "{\"status\":\"completed\",\"rootCauseOrApproach\":\"pilot\",\"changedComponents\":[],\"decisions\":[],\"validation\":[],\"warnings\":[],\"remainingRisks\":[],\"commitMessage\":\"feat: Kimi pilot\",\"prTitle\":\"fix: Kimi pilot\",\"requiresHumanInput\":false,\"humanQuestion\":null}";
        string escaped = summary.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string key = Environment.GetEnvironmentVariable("KIMI_FAKE_SECRET") ?? Environment.GetEnvironmentVariable("KIMI_API_KEY") ?? "";
        Console.WriteLine("{\"type\":\"assistant\",\"content\":\"" + key + " ```json " + escaped + " ```\"}");
    }
}
'@
        $sourcePath = Join-Path $TestDrive 'FakeKimi.cs'
        Set-Content -LiteralPath $sourcePath -Value $source -Encoding utf8
        $compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
        & $compiler /nologo /target:exe "/out:$fakeKimi" $sourcePath
        if ($LASTEXITCODE -ne 0) { throw "Fake Kimi compilation failed with exit code $LASTEXITCODE." }
        $issue = [pscustomobject]@{ number = 71; title = 'Kimi pilot'; body = 'body' }
        $config = [pscustomobject]@{ kimiCommand = $fakeKimi; kimiTimeoutMinutes = 5 }
        Remove-Item Env:KIMI_FAKE_MALFORMED -ErrorAction SilentlyContinue
        Remove-Item Env:KIMI_API_KEY -ErrorAction SilentlyContinue
        Remove-Item Env:KIMI_FAKE_SECRET -ErrorAction SilentlyContinue
    }

    It 'converts Kimi stream-json final output into the worker summary contract' {
        $result = Invoke-KimiRun -IssueWorktree $TestDrive -IssueContext $issue `
            -Config $config -RunDirectory $run -StatePath $state

        $result.Classification | Should Be 'completed'
        $result.Status | Should Be 'completed'
        $result.Summary.prTitle | Should Be 'fix: Kimi pilot'
        (Get-Content -Raw (Join-Path $run 'events.jsonl')) | Should Match 'assistant'
        $result.ThreadId | Should BeNullOrEmpty
    }

    It 'blocks malformed Kimi final output before publication' {
        $env:KIMI_FAKE_MALFORMED = '1'
        $result = Invoke-KimiRun -IssueWorktree $TestDrive -IssueContext $issue `
            -Config $config -RunDirectory $run -StatePath $state

        $result.Classification | Should Be 'malformed_summary'
        $result.Status | Should Be 'failed'
        $result.Summary | Should BeNullOrEmpty
    }

    It 'dispatches a Kimi provider without a session resume argument' {
        $provider = Resolve-CodexWorkerProvider -Provider 'Kimi' -EventName 'kimi'
        $result = Invoke-CodexWorkerAgentRun -Provider $provider -RunParameters @{
            IssueWorktree = $TestDrive; IssueContext = $issue; Config = $config; RunDirectory = $run; StatePath = $state
        }

        $result.Classification | Should Be 'completed'
        $result.ThreadId | Should BeNullOrEmpty
    }

    It 'accepts an ignored prior thread identifier for a non-resumed Kimi revision' {
        $provider = Resolve-CodexWorkerProvider -Provider 'Kimi' -EventName 'kimi-revise'
        $result = Invoke-CodexWorkerAgentRun -Provider $provider -RunParameters @{
            IssueWorktree = $TestDrive; IssueContext = $issue; Config = $config; RunDirectory = $run; StatePath = $state
            Revision = $true; ReviewComments = 'Please revise the implementation.'; ThreadId = 'must-not-be-resumed'
        }

        $result.Classification | Should Be 'completed'
        $result.ThreadId | Should BeNullOrEmpty
    }

    It 'redacts Kimi-shaped secrets from persisted stream output' {
        $env:KIMI_FAKE_SECRET = 'sk-kimi-redaction-test-12345678'
        $result = Invoke-KimiRun -IssueWorktree $TestDrive -IssueContext $issue `
            -Config $config -RunDirectory $run -StatePath $state

        $result.Classification | Should Be 'completed'
        $events = Get-Content -Raw (Join-Path $run 'events.jsonl')
        $activity = Get-Content -Raw (Join-Path $run 'activity.log')
        $events | Should Not Match 'sk-kimi-redaction-test-12345678'
        $activity | Should Not Match 'sk-kimi-redaction-test-12345678'
    }

    It 'does not expose Kimi API keys to the child process or persisted output' {
        $env:KIMI_API_KEY = 'sk-kimi-redaction-test-12345678'
        $result = Invoke-KimiRun -IssueWorktree $TestDrive -IssueContext $issue `
            -Config $config -RunDirectory $run -StatePath $state

        $result.Classification | Should Be 'completed'
        $events = Get-Content -Raw (Join-Path $run 'events.jsonl')
        $activity = Get-Content -Raw (Join-Path $run 'activity.log')
        $events | Should Not Match 'sk-kimi-redaction-test-12345678'
        $activity | Should Not Match 'sk-kimi-redaction-test-12345678'
    }
}
