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
        $command = { param($request) $calls.Add($request) | Out-Null; if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }; [pscustomobject]@{ ExitCode = 0; Stdout = '1.0.0'; Stderr = '' } }.GetNewClosure()
        $download = { param($request) $calls.Add($request) | Out-Null; throw 'download should not run' }.GetNewClosure()
        $task = { param($request) $calls.Add($request) | Out-Null; throw 'task should not run' }.GetNewClosure()
        $config = [pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = 'C:\repo'; runnerLabel = 'agentassist-local' }

        $result = Invoke-CodexLocalWorkerSetup -Config $config -DataRoot (Join-Path $TestDrive 'data') -WhatIf -CommandRunner $command -DownloadRunner $download -TaskRunner $task

        @($calls).Count | Should BeGreaterThan 0
        @($calls | Where-Object { $_.FilePath -eq 'gh.exe' -and (($_.Arguments -join ' ') -match 'registration-token') }).Count | Should Be 0
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
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://example.test/runner.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' }
        $download = { param($request) $requests.Add($request) | Out-Null }.GetNewClosure()
        $hash = { param($path) 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' }
        $config = [pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = 'C:\repo'; runnerLabel = 'agentassist-local'; runnerRelease = $release }

        { Invoke-CodexLocalWorkerSetup -Config $config -DataRoot (Join-Path $TestDrive 'data') -CommandRunner $command -DownloadRunner $download -HashRunner $hash -WhatIf } | Should Not Throw
        ($requests | ForEach-Object { $_ | ConvertTo-Json -Depth 10 }) -join "`n" | Should Not Match 'short-lived-secret'
    }

    It 'completes a fully injected install and keeps the token only in config child arguments' {
        $repoRoot = Join-Path $TestDrive 'positive-repo'
        $scriptsRoot = Join-Path $repoRoot 'scripts\codex-worker'
        New-Item -ItemType Directory -Path $scriptsRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Start-GitHubRunner.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Invoke-DeploymentNotifier.ps1'), '')
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://example.test/runner.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
        $requests = [Collections.Generic.List[object]]::new()
        $command = {
            param($request)
            $requests.Add($request) | Out-Null
            $joined = $request.Arguments -join ' '
            if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }
            if ($request.FilePath -eq 'gh.exe' -and $joined -match 'registration-token') { return [pscustomobject]@{ ExitCode = 0; Stdout = '{"token":"short-lived-secret"}'; Stderr = '' } }
            if ($request.FilePath -eq 'gh.exe' -and $joined -match '/actions/runners($|\s)') { return [pscustomobject]@{ ExitCode = 0; Stdout = '{"runners":[{"name":"test-runner","status":"online","labels":[{"name":"agentassist-local"}]}]}'; Stderr = '' } }
            if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'read-only') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'READY'; Stderr = '' } }
            if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'resume') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'resume supported'; Stderr = '' } }
            if ($request.FilePath -eq 'codex' -and $request.Arguments -contains '--version') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'codex-cli 1.0.0'; Stderr = '' } }
            if ($request.FilePath -eq 'pwsh.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'PowerShell 7.4.0'; Stderr = '' } }
            if ($request.FilePath -eq 'git.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'git version 2.49.0'; Stderr = '' } }
            if ($request.FilePath -eq 'gh.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'gh version 2.70.0'; Stderr = '' } }
            if ($request.FilePath -eq 'dotnet.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = '8.0.400'; Stderr = '' } }
            if ($request.FilePath -eq 'node.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'v22.0.0'; Stderr = '' } }
            if ($request.FilePath -eq 'npm.cmd') { return [pscustomobject]@{ ExitCode = 0; Stdout = '10.0.0'; Stderr = '' } }
            if ($request.FilePath -eq 'python.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Python 3.11.9'; Stderr = '' } }
            return [pscustomobject]@{ ExitCode = 0; Stdout = 'ok'; Stderr = '' }
        }.GetNewClosure()
        $tasks = [Collections.Generic.List[object]]::new()
        $taskRunner = { param($request) $tasks.Add($request) | Out-Null; [pscustomobject]@{ ExitCode = 0 } }.GetNewClosure()
        $config = [pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerLabel = 'agentassist-local'; runnerName = 'test-runner'; runnerRelease = $release; codexCommand = 'codex'; bootstrapPython = 'python.exe' }
        $result = Invoke-CodexLocalWorkerSetup -Config $config -DataRoot (Join-Path $TestDrive 'positive-data') -CommandRunner $command -DownloadRunner { param($request) } -HashRunner { param($path) ('a' * 64) } -ExtractRunner { param($request) } -TaskRunner $taskRunner -TemporaryGitPath $TestDrive -WhatIf:$false
        $result.Config | ConvertTo-Json -Depth 20 | Should Not Match 'short-lived-secret'
        @($tasks).Count | Should Be 2
        $configCall = @($requests | Where-Object { [IO.Path]::GetFileName($_.FilePath) -eq 'config.cmd' })[0]
        $configCall.Arguments -contains 'short-lived-secret' | Should Be $true
        @($requests | Where-Object { $_.FilePath -eq 'npm.cmd' -and $_.Arguments -contains 'install' }).Count | Should Be 0
        ($requests | Where-Object { [IO.Path]::GetFileName($_.FilePath) -ne 'config.cmd' } | ForEach-Object { $_ | ConvertTo-Json -Depth 20 }) -join "`n" | Should Not Match 'short-lived-secret'
        $authIndex = @($requests | ForEach-Object { if ($_.FilePath -eq 'gh.exe' -and $_.Arguments -contains 'status') { [array]::IndexOf($requests, $_) } })[0]
        $tokenIndex = @($requests | ForEach-Object { if ($_.FilePath -eq 'gh.exe' -and (($_.Arguments -join ' ') -match 'registration-token')) { [array]::IndexOf($requests, $_) } })[0]
        $authIndex -lt $tokenIndex | Should Be $true
    }

    It 'keeps the Task 5 resume probe injectable' {
        $config = [pscustomobject]@{ codexCommand = 'codex' }
        $probe = { param($request) [pscustomobject]@{ ExitCode = 0; Stdout = 'resume supported'; Stderr = '' } }
        $path = Join-Path $TestDrive 'resume.json'
        (Initialize-CodexResumeCapability -IssueWorktree $TestDrive -Config $config -ConfigPath $path -ProcessRunner $probe) | Should Be $true
        (Get-Content -Raw $path) | Should Match 'supportsResumeOutputControls'
    }

    It 'runs read-only prerequisite and GitHub auth probes under WhatIf without mutations' {
        $calls = [Collections.Generic.List[object]]::new()
        $command = {
            param($request)
            $calls.Add($request) | Out-Null
            if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }
            $version = switch -Regex ($request.FilePath) {
                'pwsh' { 'PowerShell 7.4.0' }; 'git' { 'git version 2.49.0' }; 'gh' { 'gh version 2.70.0' }
                'dotnet' { '8.0.400' }; 'node' { 'v22.0.0' }; 'npm' { '10.0.0' }; 'python' { 'Python 3.11.9' }; default { 'codex-cli 1.0.0' }
            }
            return [pscustomobject]@{ ExitCode = 0; Stdout = $version; Stderr = '' }
        }.GetNewClosure()
        $config = [pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = 'C:\repo'; bootstrapPython = 'python.exe'; codexCommand = 'codex' }
        $result = Invoke-CodexLocalWorkerSetup -Config $config -DataRoot (Join-Path $TestDrive 'whatif-data') -WhatIf -CommandRunner $command -DownloadRunner { throw 'download mutation' } -TaskRunner { throw 'task mutation' }
        @($calls | Where-Object { $_.FilePath -eq 'gh.exe' -and $_.Arguments -contains 'status' }).Count | Should Be 1
        @($calls | Where-Object { $_.FilePath -eq 'gh.exe' -and (($_.Arguments -join ' ') -match 'registration-token') }).Count | Should Be 0
        $result.WhatIf | Should Be $true
        (Test-Path -LiteralPath (Join-Path $TestDrive 'whatif-data')) | Should Be $false
    }

    It 'rejects missing required tools during a normal install' {
        $calls = [Collections.Generic.List[object]]::new()
        $command = { param($request) $calls.Add($request) | Out-Null; [pscustomobject]@{ ExitCode = 1; Stdout = 'missing'; Stderr = '' } }.GetNewClosure()
        $config = [pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = 'C:\repo'; codexCommand = 'codex' }
        $threw = $false
        try { Invoke-CodexLocalWorkerSetup -Config $config -DataRoot (Join-Path $TestDrive 'missing-data') -CommandRunner $command | Out-Null } catch { $threw = $true }
        $threw | Should Be $true
        @($calls | Where-Object { $_.FilePath -eq 'npm.cmd' -and $_.Arguments -contains 'install' }).Count | Should Be 0
    }

    It 'enforces the minimum tool version policy' {
        (Test-CodexPrerequisitePolicy -Prerequisite ([pscustomobject]@{ Name = 'PowerShell 7'; Version = 'PowerShell 7.4.0'; Installed = $true })).Valid | Should Be $true
        (Test-CodexPrerequisitePolicy -Prerequisite ([pscustomobject]@{ Name = '.NET 8'; Version = '7.0.401'; Installed = $true })).Valid | Should Be $false
        (Test-CodexPrerequisitePolicy -Prerequisite ([pscustomobject]@{ Name = 'Node.js'; Version = 'node unknown'; Installed = $true })).Valid | Should Be $false
        (Test-CodexPrerequisitePolicy -Prerequisite ([pscustomobject]@{ Name = 'Bootstrap Python'; Version = 'Python 3.14.0'; Installed = $true })).Valid | Should Be $false
    }

    It 'distinguishes an installed Codex CLI from a missing one' {
        (Test-CodexPrerequisitePolicy -Prerequisite ([pscustomobject]@{ Name = 'Codex CLI'; Version = 'codex-cli 1.0.0'; Installed = $true })).Valid | Should Be $true
        (Test-CodexPrerequisitePolicy -Prerequisite ([pscustomobject]@{ Name = 'Codex CLI'; Version = ''; Installed = $false })).Valid | Should Be $false
    }

    It 'builds interactive-token restart-on-failure XML for hidden hosts and STA notifier' {
        $plan = Get-CodexLocalWorkerPlan -Repository 'owner/repo' -RepositoryRoot 'C:\repo' -DataRoot (Join-Path $TestDrive 'tasks')
        $plan.Tasks.Runner.Xml | Should Match '(?i)InteractiveToken'
        $plan.Tasks.Runner.Xml | Should Match '(?i)RestartOnFailure'
        $plan.Tasks.Runner.Xml | Should Match '(?i)PT1M'
        ($plan.Tasks.Runner.Arguments -join ' ') | Should Match '(?i)WindowStyle.*Hidden'
        ($plan.Tasks.Notifier.Arguments -join ' ') | Should Match '(?i)-Sta'
        $plan.Tasks.Notifier.Xml | Should Match '(?i)InteractiveToken'
    }

    It 'reuses an exact existing runner without download, extraction, or token requests' {
        $repoRoot = Join-Path $TestDrive 'reuse-repo'
        $scriptsRoot = Join-Path $repoRoot 'scripts\codex-worker'
        $runnerRoot = Join-Path $TestDrive 'reuse-runner'
        New-Item -ItemType Directory -Path $scriptsRoot, $runnerRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Start-GitHubRunner.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Invoke-DeploymentNotifier.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $runnerRoot 'run.cmd'), '')
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://example.test/runner.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
        [IO.File]::WriteAllText((Join-Path $runnerRoot '.runner'), (@{ repositoryUrl = 'https://github.com/owner/repo'; labels = @('agentassist-local'); version = '1.0.0'; sha256 = ('a' * 64) } | ConvertTo-Json))
        $requests = [Collections.Generic.List[object]]::new()
        $command = {
            param($request)
            $requests.Add($request) | Out-Null
            $joined = $request.Arguments -join ' '
            if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }
            if ($request.FilePath -eq 'gh.exe' -and $joined -match '/actions/runners($|\s)') { return [pscustomobject]@{ ExitCode = 0; Stdout = '{"runners":[{"name":"reuse-runner","status":"online","labels":[{"name":"agentassist-local"}]}]}'; Stderr = '' } }
            if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'exec') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'READY'; Stderr = '' } }
            if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'resume') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'resume supported'; Stderr = '' } }
            if ($request.FilePath -eq 'pwsh.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'PowerShell 7.4.0'; Stderr = '' } }
            if ($request.FilePath -eq 'git.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'git version 2.49.0'; Stderr = '' } }
            if ($request.FilePath -eq 'gh.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'gh version 2.70.0'; Stderr = '' } }
            if ($request.FilePath -eq 'dotnet.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = '8.0.400'; Stderr = '' } }
            if ($request.FilePath -eq 'node.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'v22.0.0'; Stderr = '' } }
            if ($request.FilePath -eq 'npm.cmd') { return [pscustomobject]@{ ExitCode = 0; Stdout = '10.0.0'; Stderr = '' } }
            if ($request.FilePath -eq 'python.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Python 3.11.9'; Stderr = '' } }
            if ($request.FilePath -eq 'codex') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'codex-cli 1.0.0'; Stderr = '' } }
            return [pscustomobject]@{ ExitCode = 0; Stdout = 'ok'; Stderr = '' }
        }.GetNewClosure()
        $tasks = [Collections.Generic.List[object]]::new()
        $result = Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerLabel = 'agentassist-local'; runnerName = 'reuse-runner'; runnerRoot = $runnerRoot; runnerRelease = $release; codexCommand = 'codex'; bootstrapPython = 'python.exe' }) -DataRoot (Join-Path $TestDrive 'reuse-data') -CommandRunner $command -DownloadRunner { throw 'download should not run' } -ExtractRunner { throw 'extract should not run' } -TaskRunner { param($request) $tasks.Add($request) | Out-Null; [pscustomobject]@{ ExitCode = 0 } } -ProcessProvider { @() } -TemporaryGitPath $TestDrive
        $result.Plan.Runner.Reuse | Should Be $true
        @($requests | Where-Object { (($_.Arguments -join ' ') -match 'registration-token') }).Count | Should Be 0
        @($tasks).Count | Should Be 2
    }

    It 'refuses an active mismatched runner before download or extraction' {
        $repoRoot = Join-Path $TestDrive 'active-repo'
        $scriptsRoot = Join-Path $repoRoot 'scripts\codex-worker'
        $runnerRoot = Join-Path $TestDrive 'active-runner'
        New-Item -ItemType Directory -Path $scriptsRoot, $runnerRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Start-GitHubRunner.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Invoke-DeploymentNotifier.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $runnerRoot 'run.cmd'), '')
        [IO.File]::WriteAllText((Join-Path $runnerRoot '.runner'), (@{ repositoryUrl = 'https://github.com/other/repo'; labels = @('other'); version = '1.0.0'; sha256 = ('a' * 64) } | ConvertTo-Json))
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://example.test/runner.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
        $command = { param($request) if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }; if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'exec') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'READY'; Stderr = '' } }; if ($request.FilePath -eq 'pwsh.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'PowerShell 7.4.0'; Stderr = '' } }; if ($request.FilePath -eq 'git.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'git version 2.49.0'; Stderr = '' } }; if ($request.FilePath -eq 'gh.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'gh version 2.70.0'; Stderr = '' } }; if ($request.FilePath -eq 'dotnet.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = '8.0.400'; Stderr = '' } }; if ($request.FilePath -eq 'node.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'v22.0.0'; Stderr = '' } }; if ($request.FilePath -eq 'npm.cmd') { return [pscustomobject]@{ ExitCode = 0; Stdout = '10.0.0'; Stderr = '' } }; if ($request.FilePath -eq 'python.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Python 3.11.9'; Stderr = '' } }; if ($request.FilePath -eq 'codex') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'codex-cli 1.0.0'; Stderr = '' } }; [pscustomobject]@{ ExitCode = 0; Stdout = 'ok'; Stderr = '' } }.GetNewClosure()
        $threw = $false
        try { Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerLabel = 'agentassist-local'; runnerRoot = $runnerRoot; runnerRelease = $release; codexCommand = 'codex'; bootstrapPython = 'python.exe' }) -DataRoot (Join-Path $TestDrive 'active-data') -CommandRunner $command -DownloadRunner { throw 'download should not run' } -ExtractRunner { throw 'extract should not run' } -ProcessProvider { [pscustomobject]@{ ProcessName = 'Runner.Listener' } } -TemporaryGitPath $TestDrive | Out-Null } catch { $threw = $true }
        $threw | Should Be $true
    }

    It 'rejects a checksum mismatch before extraction' {
        $repoRoot = Join-Path $TestDrive 'hash-repo'
        $scriptsRoot = Join-Path $repoRoot 'scripts\codex-worker'
        New-Item -ItemType Directory -Path $scriptsRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Start-GitHubRunner.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Invoke-DeploymentNotifier.ps1'), '')
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://example.test/runner.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
        $extractCount = 0
        $command = { param($request) if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }; if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'exec') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'READY'; Stderr = '' } }; if ($request.FilePath -eq 'pwsh.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'PowerShell 7.4.0'; Stderr = '' } }; if ($request.FilePath -eq 'git.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'git version 2.49.0'; Stderr = '' } }; if ($request.FilePath -eq 'gh.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'gh version 2.70.0'; Stderr = '' } }; if ($request.FilePath -eq 'dotnet.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = '8.0.400'; Stderr = '' } }; if ($request.FilePath -eq 'node.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'v22.0.0'; Stderr = '' } }; if ($request.FilePath -eq 'npm.cmd') { return [pscustomobject]@{ ExitCode = 0; Stdout = '10.0.0'; Stderr = '' } }; if ($request.FilePath -eq 'python.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Python 3.11.9'; Stderr = '' } }; if ($request.FilePath -eq 'codex') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'codex-cli 1.0.0'; Stderr = '' } }; [pscustomobject]@{ ExitCode = 0; Stdout = 'ok'; Stderr = '' } }.GetNewClosure()
        $threw = $false
        try { Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerLabel = 'agentassist-local'; runnerRelease = $release; codexCommand = 'codex'; bootstrapPython = 'python.exe' }) -DataRoot (Join-Path $TestDrive 'hash-data') -CommandRunner $command -DownloadRunner { param($request) } -HashRunner { param($path) ('b' * 64) } -ExtractRunner { param($request) $extractCount++ } -TemporaryGitPath $TestDrive | Out-Null } catch { $threw = $true }
        $threw | Should Be $true
        $extractCount | Should Be 0
    }

    It 'rejects duplicate runner registrations even when one duplicate is online' {
        $repoRoot = Join-Path $TestDrive 'duplicate-repo'
        $scriptsRoot = Join-Path $repoRoot 'scripts\codex-worker'
        New-Item -ItemType Directory -Path $scriptsRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Start-GitHubRunner.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Invoke-DeploymentNotifier.ps1'), '')
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://example.test/runner.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
        $command = { param($request) $joined = $request.Arguments -join ' '; if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }; if ($request.FilePath -eq 'gh.exe' -and $joined -match '/actions/runners($|\s)') { return [pscustomobject]@{ ExitCode = 0; Stdout = '{"runners":[{"name":"duplicate-runner","status":"online","labels":[{"name":"agentassist-local"}]},{"name":"duplicate-runner","status":"offline","labels":[{"name":"agentassist-local"}]}]}'; Stderr = '' } }; if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'exec') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'READY'; Stderr = '' } }; if ($request.FilePath -eq 'pwsh.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'PowerShell 7.4.0'; Stderr = '' } }; if ($request.FilePath -eq 'git.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'git version 2.49.0'; Stderr = '' } }; if ($request.FilePath -eq 'gh.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'gh version 2.70.0'; Stderr = '' } }; if ($request.FilePath -eq 'dotnet.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = '8.0.400'; Stderr = '' } }; if ($request.FilePath -eq 'node.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'v22.0.0'; Stderr = '' } }; if ($request.FilePath -eq 'npm.cmd') { return [pscustomobject]@{ ExitCode = 0; Stdout = '10.0.0'; Stderr = '' } }; if ($request.FilePath -eq 'python.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Python 3.11.9'; Stderr = '' } }; if ($request.FilePath -eq 'codex') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'codex-cli 1.0.0'; Stderr = '' } }; [pscustomobject]@{ ExitCode = 0; Stdout = 'ok'; Stderr = '' } }.GetNewClosure()
        $threw = $false
        try { Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerLabel = 'agentassist-local'; runnerName = 'duplicate-runner'; runnerRelease = $release; codexCommand = 'codex'; bootstrapPython = 'python.exe' }) -DataRoot (Join-Path $TestDrive 'duplicate-data') -CommandRunner $command -DownloadRunner { param($request) } -HashRunner { param($path) ('a' * 64) } -ExtractRunner { param($request) } -TemporaryGitPath $TestDrive | Out-Null } catch { $threw = $true }
        $threw | Should Be $true
    }
}
