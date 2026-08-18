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

    It 'does not request a registration token under WhatIf' {
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
        @($requests | Where-Object { (($_.Arguments -join ' ') -match 'registration-token') }).Count | Should Be 0
    }

    It 'completes a fully injected install and keeps the token only in config child arguments' {
        $repoRoot = Join-Path $TestDrive 'positive-repo'
        $scriptsRoot = Join-Path $repoRoot 'scripts\codex-worker'
        New-Item -ItemType Directory -Path $scriptsRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Start-GitHubRunner.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Invoke-DeploymentNotifier.ps1'), '')
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://example.test/runner.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
        $requests = [Collections.Generic.List[object]]::new()
        $configRequests = [Collections.Generic.List[object]]::new()
        $command = {
            param($request)
            $requests.Add($request) | Out-Null
            $joined = $request.Arguments -join ' '
            if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }
            if ($request.FilePath -eq 'gh.exe' -and $joined -match 'registration-token') { return [pscustomobject]@{ ExitCode = 0; Stdout = '{"token":"short-lived-secret"}'; Stderr = '' } }
            if ($request.FilePath -eq 'gh.exe' -and $joined -match '/actions/runners($|\s)') { if ($configRequests.Count -eq 0) { return [pscustomobject]@{ ExitCode = 0; Stdout = '{"runners":[]}'; Stderr = '' } }; return [pscustomobject]@{ ExitCode = 0; Stdout = '{"runners":[{"name":"test-runner","status":"online","labels":[{"name":"agentassist-local"}]}]}'; Stderr = '' } }
            if ([IO.Path]::GetFileName($request.FilePath) -eq 'config.cmd') { $configRequests.Add($request) | Out-Null; return [pscustomobject]@{ ExitCode = 0; Stdout = ''; Stderr = '' } }
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
        $result = Invoke-CodexLocalWorkerSetup -Config $config -DataRoot (Join-Path $TestDrive 'positive-data') -CommandRunner $command -DownloadRunner { param($request) $requests.Add($request) | Out-Null }.GetNewClosure() -HashRunner { param($path) ('a' * 64) } -ExtractRunner { param($request) } -TaskRunner $taskRunner -TemporaryGitPath $TestDrive -WhatIf:$false
        $result.Config | ConvertTo-Json -Depth 20 | Should Not Match 'short-lived-secret'
        $result.Plan | ConvertTo-Json -Depth 20 | Should Not Match 'short-lived-secret'
        $positiveFiles = @(Get-ChildItem -LiteralPath (Join-Path $TestDrive 'positive-data') -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName }) -join "`n"
        $positiveFiles | Should Not Match 'short-lived-secret'
        @($tasks).Count | Should Be 2
        $configCall = @($requests | Where-Object { [IO.Path]::GetFileName($_.FilePath) -eq 'config.cmd' })[0]
        $configCall.Arguments -contains 'short-lived-secret' | Should Be $true
        @($requests | Where-Object { $_.FilePath -eq 'npm.cmd' -and $_.Arguments -contains 'install' }).Count | Should Be 0
        ($requests | Where-Object { [IO.Path]::GetFileName($_.FilePath) -ne 'config.cmd' } | ForEach-Object { $_ | ConvertTo-Json -Depth 20 }) -join "`n" | Should Not Match 'short-lived-secret'
        $authIndex = @($requests | ForEach-Object { if ($_.FilePath -eq 'gh.exe' -and $_.Arguments -contains 'status') { [array]::IndexOf($requests, $_) } })[0]
        $tokenIndex = @($requests | ForEach-Object { if ($_.FilePath -eq 'gh.exe' -and (($_.Arguments -join ' ') -match 'registration-token')) { [array]::IndexOf($requests, $_) } })[0]
        $authIndex -lt $tokenIndex | Should Be $true
        $inventoryIndex = @($requests | ForEach-Object { if ($_.FilePath -eq 'gh.exe' -and (($_.Arguments -join ' ') -match '/actions/runners($|\s)')) { [array]::IndexOf($requests, $_) } })[0]
        $inventoryIndex -lt $tokenIndex | Should Be $true
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
        $dataRoot = Join-Path $TestDrive 'reuse-data'
        $runnerRoot = Join-Path $dataRoot 'runner'
        New-Item -ItemType Directory -Path $scriptsRoot, $runnerRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Start-GitHubRunner.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Invoke-DeploymentNotifier.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $runnerRoot 'run.cmd'), '')
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://example.test/runner.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
        [IO.File]::WriteAllText((Join-Path $runnerRoot 'runner-install.json'), (@{ repositoryUrl = 'https://github.com/owner/repo'; repository = 'owner/repo'; runnerName = 'reuse-runner'; assetName = 'actions-runner-win-x64-1.0.0.zip'; releaseTag = 'v1.0.0'; labels = @('agentassist-local'); version = '1.0.0'; sha256 = ('a' * 64) } | ConvertTo-Json))
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
        $result = Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerLabel = 'agentassist-local'; runnerName = 'reuse-runner'; runnerRoot = $runnerRoot; runnerRelease = $release; codexCommand = 'codex'; bootstrapPython = 'python.exe' }) -DataRoot $dataRoot -CommandRunner $command -DownloadRunner { throw 'download should not run' } -ExtractRunner { throw 'extract should not run' } -TaskRunner { param($request) $tasks.Add($request) | Out-Null; [pscustomobject]@{ ExitCode = 0 } } -ProcessProvider { @() } -TemporaryGitPath $TestDrive
        $result.Plan.Runner.Reuse | Should Be $true
        @($requests | Where-Object { (($_.Arguments -join ' ') -match 'registration-token') }).Count | Should Be 0
        @($tasks).Count | Should Be 2
    }

    It 'refuses an active mismatched runner before download or extraction' {
        $repoRoot = Join-Path $TestDrive 'active-repo'
        $scriptsRoot = Join-Path $repoRoot 'scripts\codex-worker'
        $dataRoot = Join-Path $TestDrive 'active-data'
        $runnerRoot = Join-Path $dataRoot 'runner'
        New-Item -ItemType Directory -Path $scriptsRoot, $runnerRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Start-GitHubRunner.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Invoke-DeploymentNotifier.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $runnerRoot 'run.cmd'), '')
        [IO.File]::WriteAllText((Join-Path $runnerRoot 'runner-install.json'), (@{ repositoryUrl = 'https://github.com/other/repo'; repository = 'other/repo'; runnerName = 'active-runner'; assetName = 'actions-runner-win-x64-1.0.0.zip'; releaseTag = 'v1.0.0'; labels = @('other'); version = '1.0.0'; sha256 = ('a' * 64) } | ConvertTo-Json))
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://example.test/runner.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
        $command = { param($request) if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }; if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'exec') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'READY'; Stderr = '' } }; if ($request.FilePath -eq 'pwsh.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'PowerShell 7.4.0'; Stderr = '' } }; if ($request.FilePath -eq 'git.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'git version 2.49.0'; Stderr = '' } }; if ($request.FilePath -eq 'gh.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'gh version 2.70.0'; Stderr = '' } }; if ($request.FilePath -eq 'dotnet.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = '8.0.400'; Stderr = '' } }; if ($request.FilePath -eq 'node.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'v22.0.0'; Stderr = '' } }; if ($request.FilePath -eq 'npm.cmd') { return [pscustomobject]@{ ExitCode = 0; Stdout = '10.0.0'; Stderr = '' } }; if ($request.FilePath -eq 'python.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Python 3.11.9'; Stderr = '' } }; if ($request.FilePath -eq 'codex') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'codex-cli 1.0.0'; Stderr = '' } }; [pscustomobject]@{ ExitCode = 0; Stdout = 'ok'; Stderr = '' } }.GetNewClosure()
        $threw = $false
        try { Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerLabel = 'agentassist-local'; runnerRoot = $runnerRoot; runnerRelease = $release; codexCommand = 'codex'; bootstrapPython = 'python.exe' }) -DataRoot $dataRoot -CommandRunner $command -DownloadRunner { throw 'download should not run' } -ExtractRunner { throw 'extract should not run' } -ProcessProvider { [pscustomobject]@{ ProcessName = 'Runner.Listener' } } -TemporaryGitPath $TestDrive | Out-Null } catch { $threw = $true }
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

    It 'writes installer metadata and makes the second install reuse without runner mutation' {
        $repoRoot = Join-Path $TestDrive 'second-run-repo'
        $scriptsRoot = Join-Path $repoRoot 'scripts\codex-worker'
        $dataRoot = Join-Path $TestDrive 'second-run-data'
        $runnerRoot = Join-Path $dataRoot 'runner'
        New-Item -ItemType Directory -Path $scriptsRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Start-GitHubRunner.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Invoke-DeploymentNotifier.ps1'), '')
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://example.test/runner.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
        $requests = [Collections.Generic.List[object]]::new()
        $downloads = [Collections.Generic.List[object]]::new()
        $configs = [Collections.Generic.List[object]]::new()
        $tokens = [Collections.Generic.List[object]]::new()
        $configRequests = [Collections.Generic.List[object]]::new()
        $command = {
            param($request)
            $requests.Add($request) | Out-Null
            $joined = $request.Arguments -join ' '
            if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }
            if ($request.FilePath -eq 'gh.exe' -and $joined -match 'registration-token') { $tokens.Add($request) | Out-Null; return [pscustomobject]@{ ExitCode = 0; Stdout = '{"token":"second-run-secret"}'; Stderr = '' } }
            if ($request.FilePath -eq 'gh.exe' -and $joined -match '/actions/runners($|\s)') { if ($configRequests.Count -eq 0) { return [pscustomobject]@{ ExitCode = 0; Stdout = '{"runners":[]}'; Stderr = '' } }; return [pscustomobject]@{ ExitCode = 0; Stdout = '{"runners":[{"name":"second-runner","status":"online","labels":[{"name":"agentassist-local"}]}]}'; Stderr = '' } }
            if ([IO.Path]::GetFileName($request.FilePath) -eq 'config.cmd') { $configRequests.Add($request) | Out-Null; $configs.Add($request) | Out-Null; return [pscustomobject]@{ ExitCode = 0; Stdout = ''; Stderr = '' } }
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
        $extract = { param($request) $downloads.Add($request) | Out-Null; New-Item -ItemType Directory -Path $request.Destination -Force | Out-Null; [IO.File]::WriteAllText((Join-Path $request.Destination 'run.cmd'), ''); [IO.File]::WriteAllText((Join-Path $request.Destination 'config.cmd'), '') }.GetNewClosure()
        $taskRunner = { param($request) [pscustomobject]@{ ExitCode = 0 } }
        $config = [pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerLabel = 'agentassist-local'; runnerName = 'second-runner'; runnerRoot = $runnerRoot; runnerRelease = $release; codexCommand = 'codex'; bootstrapPython = 'python.exe' }
        Invoke-CodexLocalWorkerSetup -Config $config -DataRoot $dataRoot -CommandRunner $command -DownloadRunner { param($request) } -HashRunner { param($path) ('a' * 64) } -ExtractRunner $extract -TaskRunner $taskRunner -ProcessProvider { @() } -TemporaryGitPath $TestDrive | Out-Null
        $metadataPath = Join-Path $runnerRoot 'runner-install.json'
        (Test-Path -LiteralPath $metadataPath -PathType Leaf) | Should Be $true
        $metadata = Get-Content -Raw -LiteralPath $metadataPath | ConvertFrom-Json
        $metadata.version | Should Be '1.0.0'
        $metadata.sha256 | Should Be ('a' * 64)
        Invoke-CodexLocalWorkerSetup -Config $config -DataRoot $dataRoot -CommandRunner $command -DownloadRunner { throw 'download should not run on second install' } -HashRunner { throw 'hash should not run on second install' } -ExtractRunner { throw 'extract should not run on second install' } -TaskRunner $taskRunner -ProcessProvider { @() } -TemporaryGitPath $TestDrive | Out-Null
        $downloads.Count | Should Be 1
        $configs.Count | Should Be 1
        $tokens.Count | Should Be 1
    }

    It 'cleans staging and preserves the final runner root when token, config, or verification fails' {
        foreach ($mode in @('token', 'config', 'verify')) {
            $repoRoot = Join-Path $TestDrive ("transaction-$mode-repo")
            $scriptsRoot = Join-Path $repoRoot 'scripts\codex-worker'
            $dataRoot = Join-Path $TestDrive ("transaction-$mode-data")
            $runnerRoot = Join-Path $dataRoot 'runner'
            New-Item -ItemType Directory -Path $scriptsRoot -Force | Out-Null
            [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Start-GitHubRunner.ps1'), '')
            [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Invoke-DeploymentNotifier.ps1'), '')
            $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://example.test/runner.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
            $configRequests = [Collections.Generic.List[object]]::new()
            $emptyInventory = '{"runners":[]}'
            $onlineInventory = '{"runners":[{"name":"transaction-runner","status":"online","labels":[{"name":"agentassist-local"}]}]}'
            $command = {
                param($request)
                $joined = $request.Arguments -join ' '
                if ($request.FilePath -eq 'gh.exe') {
                    if ($request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }
                    if ($joined -match 'registration-token') {
                        if ($mode -eq 'token') { return [pscustomobject]@{ ExitCode = 1; Stdout = ''; Stderr = 'token denied' } }
                        return [pscustomobject]@{ ExitCode = 0; Stdout = '{"token":"transaction-secret"}'; Stderr = '' }
                    }
                    if ($joined -match '/actions/runners($|\s)') {
                        $inventoryText = if ($configRequests.Count -eq 0 -or $mode -eq 'verify') { $emptyInventory } else { $onlineInventory }
                        return [pscustomobject]@{ ExitCode = 0; Stdout = $inventoryText; Stderr = '' }
                    }
                    return [pscustomobject]@{ ExitCode = 0; Stdout = 'gh version 2.70.0'; Stderr = '' }
                }
                if ([IO.Path]::GetFileName($request.FilePath) -eq 'config.cmd') {
                    $configRequests.Add($request) | Out-Null
                    if ($mode -eq 'config') { return [pscustomobject]@{ ExitCode = 1; Stdout = ''; Stderr = 'config failed' } }
                    return [pscustomobject]@{ ExitCode = 0; Stdout = ''; Stderr = '' }
                }
                if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'exec') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'READY'; Stderr = '' } }
                if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'resume') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'resume supported'; Stderr = '' } }
                if ($request.FilePath -eq 'pwsh.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'PowerShell 7.4.0'; Stderr = '' } }
                if ($request.FilePath -eq 'git.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'git version 2.49.0'; Stderr = '' } }
                if ($request.FilePath -eq 'dotnet.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = '8.0.400'; Stderr = '' } }
                if ($request.FilePath -eq 'node.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'v22.0.0'; Stderr = '' } }
                if ($request.FilePath -eq 'npm.cmd') { return [pscustomobject]@{ ExitCode = 0; Stdout = '10.0.0'; Stderr = '' } }
                if ($request.FilePath -eq 'python.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Python 3.11.9'; Stderr = '' } }
                if ($request.FilePath -eq 'codex') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'codex-cli 1.0.0'; Stderr = '' } }
                return [pscustomobject]@{ ExitCode = 0; Stdout = 'ok'; Stderr = '' }
            }
            $command = $command.GetNewClosure()
            $extract = { param($request) New-Item -ItemType Directory -Path $request.Destination -Force | Out-Null; [IO.File]::WriteAllText((Join-Path $request.Destination 'run.cmd'), ''); [IO.File]::WriteAllText((Join-Path $request.Destination 'config.cmd'), '') }
            $threw = $false
            try { Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerLabel = 'agentassist-local'; runnerName = 'transaction-runner'; runnerRelease = $release; codexCommand = 'codex'; bootstrapPython = 'python.exe' }) -DataRoot $dataRoot -CommandRunner $command -DownloadRunner { param($request) } -HashRunner { param($path) ('a' * 64) } -ExtractRunner $extract -TaskRunner { param($request) [pscustomobject]@{ ExitCode = 0 } } -TemporaryGitPath $TestDrive | Out-Null } catch { $threw = $true }
            $threw | Should Be $true
            (Test-Path -LiteralPath $runnerRoot) | Should Be $false
            $stagingCount = @(Get-ChildItem -LiteralPath $dataRoot -Recurse -Force -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match '\.staging' }).Count
            if ($stagingCount -ne 0) { throw "staging remained for mode $mode" }
        }
    }

    It 'fails before mutation when the notifier target is deferred and missing' {
        $repoRoot = Join-Path $TestDrive 'deferred-target-repo'
        $scriptsRoot = Join-Path $repoRoot 'scripts\codex-worker'
        New-Item -ItemType Directory -Path $scriptsRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Start-GitHubRunner.ps1'), '')
        $calls = [Collections.Generic.List[object]]::new()
        $command = { param($request) $calls.Add($request) | Out-Null; [pscustomobject]@{ ExitCode = 0; Stdout = '8.0.0'; Stderr = '' } }.GetNewClosure()
        $threw = $false
        try { Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot }) -DataRoot (Join-Path $TestDrive 'deferred-data') -CommandRunner $command | Out-Null } catch { $threw = $true }
        $threw | Should Be $true
        @($calls).Count | Should Be 0
    }

    It 'rejects a runner root outside the trusted worker data root before probing' {
        $repoRoot = Join-Path $TestDrive 'root-jail-repo'
        $dataRoot = Join-Path $TestDrive 'root-jail-data'
        $outsideRoot = Join-Path $TestDrive 'root-jail-outside'
        $repoContained = Join-Path $repoRoot 'runner'
        $threwOutside = $false
        try { Get-CodexLocalWorkerPlan -Repository 'owner/repo' -RepositoryRoot $repoRoot -DataRoot $dataRoot -Config ([pscustomobject]@{ runnerRoot = $outsideRoot }) | Out-Null } catch { $threwOutside = $true }
        $threwOutside | Should Be $true
        $threwRepo = $false
        try { Get-CodexLocalWorkerPlan -Repository 'owner/repo' -RepositoryRoot $repoRoot -DataRoot $dataRoot -Config ([pscustomobject]@{ runnerRoot = $repoContained }) | Out-Null } catch { $threwRepo = $true }
        $threwRepo | Should Be $true
    }
}
