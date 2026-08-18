Describe 'Codex local worker installation' {
    BeforeEach {
        $WhatIfPreference = $false
        Import-Module (Join-Path $PSScriptRoot '..\codex-worker\CodexWorker.psd1') -Force
    }

    It 'fails closed when the Windows x64 asset checksum is absent or ambiguous' {
        $release = [pscustomobject]@{
            assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://example.test/runner.zip' })
            body = "actions-runner-win-x64-1.0.0.zip`n11111111111111111111111111111111111111111111111111111111111111111111`n22222222222222222222222222222222222222222222222222222222222222222222"
        }
        $threw = $false
        try { Resolve-CodexRunnerAsset -Release $release | Out-Null } catch { $threw = $true }
        $threw | Should Be $true
    }

    It 'returns a non-service runner plan with interactive logon tasks' {
        $plan = Get-CodexLocalWorkerPlan -Repository 'owner/repo' -RepositoryRoot 'C:\repo' -DataRoot (Join-Path $TestDrive 'data')
        $plan.Runner.ServiceMode | Should Be $false
        $plan.Tasks.Runner.LogonTrigger | Should Be $true
        $plan.Tasks.Notifier.LogonTrigger | Should Be $true
        ($plan.Tasks.Runner.Arguments -join ' ') | Should Not Match '(?i)service|install'
        ($plan.Tasks.Notifier.Arguments -join ' ') | Should Match '(?i)-Sta'
        $plan.ConfigPath.StartsWith('C:\repo', [StringComparison]::OrdinalIgnoreCase) | Should Be $false
    }

    It 'does not invoke mutation seams under WhatIf' {
        $calls = [Collections.Generic.List[object]]::new()
        $command = { param($request) $calls.Add($request) | Out-Null; throw 'mutation command should not run' }.GetNewClosure()
        $download = { param($request) $calls.Add($request) | Out-Null; throw 'download should not run' }.GetNewClosure()
        $task = { param($request) $calls.Add($request) | Out-Null; throw 'task should not run' }.GetNewClosure()
        $config = [pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = 'C:\repo'; runnerLabel = 'agentassist-local' }

        $result = Invoke-CodexLocalWorkerSetup -Config $config -DataRoot (Join-Path $TestDrive 'data') -WhatIf -CommandRunner $command -DownloadRunner $download -TaskRunner $task

        @($calls).Count | Should Be 0
        $result.WhatIf | Should Be $true
        $result.Mutations | Should Be 0
    }

    It 'passes the short registration token only to the config child' {
        $requests = [Collections.Generic.List[object]]::new()
        $command = {
            param($request)
            $requests.Add($request) | Out-Null
            if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'registration-token') {
                return '{"token":"short-lived-secret"}'
            }
            if ($request.FilePath -eq 'gh.exe') { return '{"tag_name":"v1","assets":[],"body":""}' }
            return [pscustomobject]@{ ExitCode = 0; Stdout = 'ok'; Stderr = '' }
        }.GetNewClosure()
        $release = [pscustomobject]@{ assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://example.test/runner.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' }
        $download = { param($request) $requests.Add($request) | Out-Null }.GetNewClosure()
        $hash = { param($path) 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' }
        $config = [pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = 'C:\repo'; runnerLabel = 'agentassist-local'; runnerRelease = $release }

        { Invoke-CodexLocalWorkerSetup -Config $config -DataRoot (Join-Path $TestDrive 'data') -CommandRunner $command -DownloadRunner $download -HashRunner $hash -WhatIf } | Should Not Throw
        ($requests | ForEach-Object { $_ | ConvertTo-Json -Depth 10 }) -join "`n" | Should Not Match 'short-lived-secret'
    }

    It 'keeps the Task 5 resume probe injectable' {
        $config = [pscustomobject]@{ codexCommand = 'codex' }
        $probe = { param($request) [pscustomobject]@{ ExitCode = 0; Stdout = 'resume supported'; Stderr = '' } }
        $path = Join-Path $TestDrive 'resume.json'
        (Initialize-CodexResumeCapability -IssueWorktree $TestDrive -Config $config -ConfigPath $path -ProcessRunner $probe) | Should Be $true
        (Get-Content -Raw $path) | Should Match 'supportsResumeOutputControls'
    }

}
