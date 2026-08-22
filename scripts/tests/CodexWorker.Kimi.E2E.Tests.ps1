Describe 'Kimi installer preflight' {
    BeforeEach {
        Import-Module (Join-Path $PSScriptRoot '..\codex-worker\CodexWorker.psd1') -Force
    }

    It 'runs the bounded read-only Kimi smoke check through an injected runner' {
        $module = Get-Module CodexWorker
        $requests = [Collections.Generic.List[object]]::new()
        $runner = {
            param($request)
            $requests.Add($request) | Out-Null
            return [pscustomobject]@{ ExitCode = 0; Stdout = 'READY'; Stderr = '' }
        }.GetNewClosure()

        $result = & $module { param($commandRunner) Invoke-KimiAuthSmoke -KimiCommand 'kimi' -CommandRunner $commandRunner } $runner

        $result | Should Be $true
        $requests.Count | Should Be 1
        $requests[0].FilePath | Should Be 'kimi'
        ($requests[0].Arguments -join '|') | Should Be '--auto|--prompt|Reply exactly READY|--output-format|text'
    }

    It 'refuses Kimi before worker invocation when the persisted config enables only Codex' {
        $dataRoot = Join-Path $TestDrive 'codex-only-config'
        New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $dataRoot 'config.json'), '{"enabledProviders":["Codex"]}')
        $issueEntry = Join-Path $PSScriptRoot '..\codex-worker\Invoke-Issue.ps1'
        $errorText = ''

        try {
            & $issueEntry -Repository 'owner/repo' -IssueNumber 71 -Actor 'trusted-user' -EventName 'kimi' -Provider 'Kimi' -RepositoryRoot 'C:\repo' -DataRoot $dataRoot | Out-Null
        } catch {
            $errorText = $_.Exception.Message
        }

        $errorText | Should Match "Worker provider 'Kimi' is not enabled by configuration\."
        $revisionEntry = Get-Content -Raw (Join-Path $PSScriptRoot '..\codex-worker\Invoke-Revision.ps1')
        $revisionEntry | Should Match '(?s)enabledProviders.*not enabled by configuration.*Invoke-CodexRevision'
    }
}
