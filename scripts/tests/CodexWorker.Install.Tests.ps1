Describe 'Codex local worker installation' {
    BeforeEach {
        $WhatIfPreference = $false
        $script:BootstrapPython = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\agent-service\.venv\Scripts\python.exe'))
        Import-Module (Join-Path $PSScriptRoot '..\codex-worker\CodexWorker.psd1') -Force
    }

    It 'fails closed when the Windows x64 asset checksum is absent or ambiguous' {
        $release = [pscustomobject]@{
            assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://github.com/actions/runner/releases/download/v1.0.0/actions-runner-win-x64-1.0.0.zip' })
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
        $config = [pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = 'C:\repo'; runnerLabel = 'agentassist-local'; bootstrapPython = $script:BootstrapPython }

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
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://github.com/actions/runner/releases/download/v1.0.0/actions-runner-win-x64-1.0.0.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' }
        $download = { param($request) $requests.Add($request) | Out-Null }.GetNewClosure()
        $hash = { param($path) 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' }
        $config = [pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = 'C:\repo'; runnerLabel = 'agentassist-local'; runnerRelease = $release; bootstrapPython = $script:BootstrapPython }

        { Invoke-CodexLocalWorkerSetup -Config $config -DataRoot (Join-Path $TestDrive 'data') -CommandRunner $command -DownloadRunner $download -HashRunner $hash -WhatIf } | Should Not Throw
        @($requests | Where-Object { (($_.Arguments -join ' ') -match 'registration-token') }).Count | Should Be 0
    }

    It 'loads a real ConfigPath, preserves operational settings, and derives protected identity' {
        $dataRoot = Join-Path $TestDrive 'config-data'
        New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
        $configPath = Join-Path $dataRoot 'config.json'
        $json = [ordered]@{
            repository = 'attacker/repo'; repositoryRoot = 'C:\attacker'; dataRoot = 'C:\attacker-data'
            runnerLabel = 'attacker-label'; runnerName = 'attacker-runner'; runnerRoot = 'C:\attacker-runner'
            defaultBranch = 'release'; codexCommand = 'codex-custom'; bootstrapPython = $script:BootstrapPython
            workerLockTimeoutSeconds = 41; codexTimeoutMinutes = 77; notificationSeconds = 12; snoozeMinutes = 6
            healthTimeoutSeconds = 88; runRetentionDays = 9; runtimeSlots = @('runtime-a','runtime-b')
        } | ConvertTo-Json -Depth 10
        [IO.File]::WriteAllText($configPath, $json)
        $command = { param($request) if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } } else { [pscustomobject]@{ ExitCode = 0; Stdout = '1.0.0'; Stderr = '' } } }

        $result = Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{}) -Repository 'trusted/repo' -RepositoryRoot 'C:\trusted-repo' -DataRoot $dataRoot -ConfigPath $configPath -WhatIf -CommandRunner $command

        $result.Config.repository | Should Be 'trusted/repo'
        $result.Config.repositoryRoot | Should Be 'C:\trusted-repo'
        $result.Config.defaultBranch | Should Be 'release'
        $result.Config.codexCommand | Should Be 'codex-custom'
        $result.Config.workerLockTimeoutSeconds | Should Be 41
        ($result.Config.runtimeSlots -join ',') | Should Be 'runtime-a,runtime-b'
        $result.Config.runnerLabel | Should Be 'agentassist-local'
        $result.Config.runnerName | Should Be 'AutomationWorkbenchCodexRunner'
        $result.Config.runnerRoot | Should Be ([IO.Path]::GetFullPath((Join-Path $dataRoot 'runner')))
        $result.Config.configPath | Should Be ([IO.Path]::GetFullPath($configPath))
    }

    It 'writes a complete bootstrap default when the config omits bootstrapPython' {
        $calls = [Collections.Generic.List[object]]::new()
        $command = {
            param($request)
            $calls.Add($request) | Out-Null
            if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }
            return [pscustomobject]@{ ExitCode = 0; Stdout = 'Python 3.11.9'; Stderr = '' }
        }.GetNewClosure()
        $result = Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')); bootstrapPython = $script:BootstrapPython }) -DataRoot (Join-Path $TestDrive 'default-data') -WhatIf -CommandRunner $command

        $result.Config.bootstrapPython | Should Be $script:BootstrapPython
        $result.Config.defaultBranch | Should Be 'master'
        $result.Config.runtimeSlots.Count | Should Be 2
        [string]::IsNullOrWhiteSpace([string]$result.Config.bootstrapPython) | Should Be $false
    }

    It 'rejects malformed config before any command or filesystem mutation' {
        $dataRoot = Join-Path $TestDrive 'invalid-data'
        New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
        $configPath = Join-Path $dataRoot 'config.json'
        [IO.File]::WriteAllText($configPath, (@{ workerLockTimeoutSeconds = 'not-an-integer'; runtimeSlots = @('slot-one','slot-two') } | ConvertTo-Json))
        $calls = [Collections.Generic.List[object]]::new()
        $command = { param($request) $calls.Add($request) | Out-Null; [pscustomobject]@{ ExitCode = 0 } }.GetNewClosure()
        $threw = $false
        try { Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{}) -Repository 'owner/repo' -RepositoryRoot 'C:\repo' -DataRoot $dataRoot -ConfigPath $configPath -WhatIf -CommandRunner $command | Out-Null } catch { $threw = $true }
        $threw | Should Be $true
        @($calls).Count | Should Be 0
        (Get-Content -Raw -LiteralPath $configPath) | Should Match 'not-an-integer'
    }

    It 'rejects a redirected runner release before any mutation' {
        $calls = [Collections.Generic.List[object]]::new()
        $release = [pscustomobject]@{
            tag_name = 'v1.0.0'
            assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'http://evil.example/runner.zip' })
            body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64)
        }
        $command = { param($request) $calls.Add($request) | Out-Null; [pscustomobject]@{ ExitCode = 0; Stdout = ''; Stderr = '' } }.GetNewClosure()
        $threw = $false
        try { Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = (Get-Location).Path; bootstrapPython = $script:BootstrapPython; runnerRelease = $release }) -DataRoot (Join-Path $TestDrive 'bad-release-data') -CommandRunner $command -WhatIf | Out-Null } catch { $threw = $true }
        $threw | Should Be $true
        @($calls).Count | Should Be 0
    }

    It 'rejects a real reparse ancestor for bootstrapPython before mutation' {
        $target = Join-Path $TestDrive 'python-target'
        $junction = Join-Path $TestDrive 'python-junction'
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $target 'python.exe'), '')
        New-Item -ItemType Junction -Path $junction -Target $target -Force | Out-Null
        $calls = [Collections.Generic.List[object]]::new()
        $command = { param($request) $calls.Add($request) | Out-Null; [pscustomobject]@{ ExitCode = 0; Stdout = ''; Stderr = '' } }.GetNewClosure()
        $threw = $false
        try { Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = (Get-Location).Path; bootstrapPython = (Join-Path $junction 'python.exe') }) -DataRoot (Join-Path $TestDrive 'junction-data') -CommandRunner $command -WhatIf | Out-Null } catch { $threw = $true }
        $threw | Should Be $true
        @($calls).Count | Should Be 0
    }

    It 'skips prerequisite probing while retaining GitHub auth validation' {
        $calls = [Collections.Generic.List[object]]::new()
        $command = {
            param($request)
            $calls.Add($request) | Out-Null
            if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }
            return [pscustomobject]@{ ExitCode = 0; Stdout = '1.0.0'; Stderr = '' }
        }.GetNewClosure()
        $result = Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = (Get-Location).Path; bootstrapPython = $script:BootstrapPython }) -DataRoot (Join-Path $TestDrive 'skip-data') -SkipPrerequisiteProbe -WhatIf -CommandRunner $command

        @($calls | Where-Object { $_.FilePath -in @('pwsh.exe','git.exe','dotnet.exe','node.exe','npm.cmd','python.exe') }).Count | Should Be 0
        @($calls | Where-Object { $_.FilePath -eq 'gh.exe' -and $_.Arguments -contains 'status' }).Count | Should Be 1
        $result.WhatIf | Should Be $true
    }

    It 'completes a fully injected install and keeps the token only in config child arguments' {
        $repoRoot = Join-Path $TestDrive 'positive-repo'
        $scriptsRoot = Join-Path $repoRoot 'scripts\codex-worker'
        New-Item -ItemType Directory -Path $scriptsRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Start-GitHubRunner.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Invoke-DeploymentNotifier.ps1'), '')
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://github.com/actions/runner/releases/download/v1.0.0/actions-runner-win-x64-1.0.0.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
        $requests = [Collections.Generic.List[object]]::new()
        $configRequests = [Collections.Generic.List[object]]::new()
        $runnerStarts = [Collections.Generic.List[object]]::new()
        $command = {
            param($request)
            $requests.Add($request) | Out-Null
            $joined = $request.Arguments -join ' '
            if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }
            if ($request.FilePath -eq 'gh.exe' -and $joined -match 'registration-token') { return [pscustomobject]@{ ExitCode = 0; Stdout = '{"token":"short-lived-secret"}'; Stderr = '' } }
            if ($request.FilePath -eq 'gh.exe' -and $joined -match '/actions/runners($|\s)') { if ($configRequests.Count -eq 0) { return [pscustomobject]@{ ExitCode = 0; Stdout = '{"runners":[]}'; Stderr = '' } }; $status = if ($runnerStarts.Count -eq 0) { 'offline' } else { 'online' }; return [pscustomobject]@{ ExitCode = 0; Stdout = ('{"runners":[{"name":"AutomationWorkbenchCodexRunner","status":"' + $status + '","labels":[{"name":"agentassist-local"}]}]}'); Stderr = '' } }
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
            if ([IO.Path]::GetFileName($request.FilePath) -eq 'python.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Python 3.11.9'; Stderr = '' } }
            return [pscustomobject]@{ ExitCode = 0; Stdout = 'ok'; Stderr = '' }
        }.GetNewClosure()
        $tasks = [Collections.Generic.List[object]]::new()
        $taskRunner = { param($request) $tasks.Add($request) | Out-Null; [pscustomobject]@{ ExitCode = 0 } }.GetNewClosure()
        $taskStartRunner = { param($request) $runnerStarts.Add($request) | Out-Null; [pscustomobject]@{ ExitCode = 0 } }.GetNewClosure()
        $positiveData = Join-Path $TestDrive 'positive-data'
        $config = [pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerLabel = 'agentassist-local'; runnerName = 'AutomationWorkbenchCodexRunner'; runnerRelease = $release; codexCommand = 'codex'; bootstrapPython = $script:BootstrapPython; token = 'short-lived-secret'; secret = 'must-not-persist' }
        $result = Invoke-CodexLocalWorkerSetup -Config $config -DataRoot $positiveData -ConfigPath (Join-Path $positiveData 'config.json') -SkipPrerequisiteProbe -CommandRunner $command -DownloadRunner { param($request) $requests.Add($request) | Out-Null }.GetNewClosure() -HashRunner { param($path) ('a' * 64) } -ExtractRunner { param($request) } -TaskRunner $taskRunner -TaskStartRunner $taskStartRunner -TemporaryGitPath $TestDrive -WhatIf:$false
        $result.Config | ConvertTo-Json -Depth 20 | Should Not Match 'short-lived-secret'
        $result.Plan | ConvertTo-Json -Depth 20 | Should Not Match 'short-lived-secret'
        $positiveFiles = @(Get-ChildItem -LiteralPath (Join-Path $TestDrive 'positive-data') -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName }) -join "`n"
        $positiveFiles | Should Not Match 'short-lived-secret'
        $persisted = Get-Content -Raw -LiteralPath (Join-Path $positiveData 'config.json') | ConvertFrom-Json
        $persisted.repository | Should Be 'owner/repo'
        $persisted.bootstrapPython | Should Be $script:BootstrapPython
        ($persisted.runtimeSlots -join ',') | Should Be 'runtime-a,runtime-b'
        ($persisted.PSObject.Properties.Name -join ',') | Should Not Match '(?i)token|secret'
        @($requests | Where-Object { $_.Arguments -contains '--version' }).Count | Should Be 0
        @($requests | Where-Object { $_.FilePath -eq 'gh.exe' -and $_.Arguments -contains 'status' }).Count | Should Be 1
        @($requests | Where-Object { $_.FilePath -eq 'codex' -and $_.Arguments -contains 'resume' }).Count | Should Be 1
        @($tasks).Count | Should Be 2
        @($runnerStarts).Count | Should Be 1
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
        $config = [pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = 'C:\repo'; bootstrapPython = $script:BootstrapPython; codexCommand = 'codex' }
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
        [xml]$runnerXml = $plan.Tasks.Runner.Xml
        $runnerXml.Task.Actions.Exec.WorkingDirectory | Should Be 'C:\repo\scripts\codex-worker'
        ($plan.Tasks.Notifier.Arguments -join ' ') | Should Match '(?i)-Sta'
        $plan.Tasks.Notifier.Xml | Should Match '(?i)InteractiveToken'
        [xml]$notifierXml = $plan.Tasks.Notifier.Xml
        $notifierXml.Task.Actions.Exec.WorkingDirectory | Should Be 'C:\repo\scripts\codex-worker'
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
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://github.com/actions/runner/releases/download/v1.0.0/actions-runner-win-x64-1.0.0.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
        [IO.File]::WriteAllText((Join-Path $runnerRoot 'runner-install.json'), (@{ repositoryUrl = 'https://github.com/owner/repo'; repository = 'owner/repo'; runnerName = 'AutomationWorkbenchCodexRunner'; assetName = 'actions-runner-win-x64-1.0.0.zip'; releaseTag = 'v1.0.0'; labels = @('agentassist-local'); version = '1.0.0'; sha256 = ('a' * 64) } | ConvertTo-Json))
        $requests = [Collections.Generic.List[object]]::new()
        $runnerStarts = [Collections.Generic.List[object]]::new()
        $command = {
            param($request)
            $requests.Add($request) | Out-Null
            $joined = $request.Arguments -join ' '
            if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }
            if ($request.FilePath -eq 'gh.exe' -and $joined -match '/actions/runners($|\s)') { $status = if ($runnerStarts.Count -eq 0) { 'offline' } else { 'online' }; return [pscustomobject]@{ ExitCode = 0; Stdout = ('{"runners":[{"name":"AutomationWorkbenchCodexRunner","status":"' + $status + '","labels":[{"name":"agentassist-local"}]}]}'); Stderr = '' } }
            if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'exec') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'READY'; Stderr = '' } }
            if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'resume') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'resume supported'; Stderr = '' } }
            if ($request.FilePath -eq 'pwsh.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'PowerShell 7.4.0'; Stderr = '' } }
            if ($request.FilePath -eq 'git.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'git version 2.49.0'; Stderr = '' } }
            if ($request.FilePath -eq 'gh.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'gh version 2.70.0'; Stderr = '' } }
            if ($request.FilePath -eq 'dotnet.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = '8.0.400'; Stderr = '' } }
            if ($request.FilePath -eq 'node.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'v22.0.0'; Stderr = '' } }
            if ($request.FilePath -eq 'npm.cmd') { return [pscustomobject]@{ ExitCode = 0; Stdout = '10.0.0'; Stderr = '' } }
            if ([IO.Path]::GetFileName($request.FilePath) -eq 'python.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Python 3.11.9'; Stderr = '' } }
            if ($request.FilePath -eq 'codex') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'codex-cli 1.0.0'; Stderr = '' } }
            return [pscustomobject]@{ ExitCode = 0; Stdout = 'ok'; Stderr = '' }
        }.GetNewClosure()
        $tasks = [Collections.Generic.List[object]]::new()
        $result = Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerLabel = 'agentassist-local'; runnerName = 'AutomationWorkbenchCodexRunner'; runnerRoot = $runnerRoot; runnerRelease = $release; codexCommand = 'codex'; bootstrapPython = $script:BootstrapPython }) -DataRoot $dataRoot -CommandRunner $command -DownloadRunner { throw 'download should not run' } -ExtractRunner { throw 'extract should not run' } -TaskRunner { param($request) $tasks.Add($request) | Out-Null; [pscustomobject]@{ ExitCode = 0 } } -TaskStartRunner { param($request) $runnerStarts.Add($request) | Out-Null; [pscustomobject]@{ ExitCode = 0 } }.GetNewClosure() -ProcessProvider { @() } -TemporaryGitPath $TestDrive
        $result.Plan.Runner.Reuse | Should Be $true
        @($requests | Where-Object { (($_.Arguments -join ' ') -match 'registration-token') }).Count | Should Be 0
        @($tasks).Count | Should Be 2
        @($runnerStarts).Count | Should Be 1
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
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://github.com/actions/runner/releases/download/v1.0.0/actions-runner-win-x64-1.0.0.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
        $command = { param($request) if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }; if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'exec') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'READY'; Stderr = '' } }; if ($request.FilePath -eq 'pwsh.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'PowerShell 7.4.0'; Stderr = '' } }; if ($request.FilePath -eq 'git.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'git version 2.49.0'; Stderr = '' } }; if ($request.FilePath -eq 'gh.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'gh version 2.70.0'; Stderr = '' } }; if ($request.FilePath -eq 'dotnet.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = '8.0.400'; Stderr = '' } }; if ($request.FilePath -eq 'node.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'v22.0.0'; Stderr = '' } }; if ($request.FilePath -eq 'npm.cmd') { return [pscustomobject]@{ ExitCode = 0; Stdout = '10.0.0'; Stderr = '' } }; if ([IO.Path]::GetFileName($request.FilePath) -eq 'python.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Python 3.11.9'; Stderr = '' } }; if ($request.FilePath -eq 'codex') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'codex-cli 1.0.0'; Stderr = '' } }; [pscustomobject]@{ ExitCode = 0; Stdout = 'ok'; Stderr = '' } }.GetNewClosure()
        $threw = $false
        try { Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerLabel = 'agentassist-local'; runnerRoot = $runnerRoot; runnerRelease = $release; codexCommand = 'codex'; bootstrapPython = $script:BootstrapPython }) -DataRoot $dataRoot -CommandRunner $command -DownloadRunner { throw 'download should not run' } -ExtractRunner { throw 'extract should not run' } -ProcessProvider { [pscustomobject]@{ ProcessName = 'Runner.Listener' } } -TemporaryGitPath $TestDrive | Out-Null } catch { $threw = $true }
        $threw | Should Be $true
    }

    It 'rejects a checksum mismatch before extraction' {
        $repoRoot = Join-Path $TestDrive 'hash-repo'
        $scriptsRoot = Join-Path $repoRoot 'scripts\codex-worker'
        New-Item -ItemType Directory -Path $scriptsRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Start-GitHubRunner.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Invoke-DeploymentNotifier.ps1'), '')
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://github.com/actions/runner/releases/download/v1.0.0/actions-runner-win-x64-1.0.0.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
        $extractCount = 0
        $command = { param($request) if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }; if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'exec') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'READY'; Stderr = '' } }; if ($request.FilePath -eq 'pwsh.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'PowerShell 7.4.0'; Stderr = '' } }; if ($request.FilePath -eq 'git.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'git version 2.49.0'; Stderr = '' } }; if ($request.FilePath -eq 'gh.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'gh version 2.70.0'; Stderr = '' } }; if ($request.FilePath -eq 'dotnet.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = '8.0.400'; Stderr = '' } }; if ($request.FilePath -eq 'node.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'v22.0.0'; Stderr = '' } }; if ($request.FilePath -eq 'npm.cmd') { return [pscustomobject]@{ ExitCode = 0; Stdout = '10.0.0'; Stderr = '' } }; if ([IO.Path]::GetFileName($request.FilePath) -eq 'python.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Python 3.11.9'; Stderr = '' } }; if ($request.FilePath -eq 'codex') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'codex-cli 1.0.0'; Stderr = '' } }; [pscustomobject]@{ ExitCode = 0; Stdout = 'ok'; Stderr = '' } }.GetNewClosure()
        $threw = $false
        try { Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerLabel = 'agentassist-local'; runnerRelease = $release; codexCommand = 'codex'; bootstrapPython = $script:BootstrapPython }) -DataRoot (Join-Path $TestDrive 'hash-data') -CommandRunner $command -DownloadRunner { param($request) } -HashRunner { param($path) ('b' * 64) } -ExtractRunner { param($request) $extractCount++ } -TemporaryGitPath $TestDrive | Out-Null } catch { $threw = $true }
        $threw | Should Be $true
        $extractCount | Should Be 0
    }

    It 'rejects duplicate runner registrations even when one duplicate is online' {
        $repoRoot = Join-Path $TestDrive 'duplicate-repo'
        $scriptsRoot = Join-Path $repoRoot 'scripts\codex-worker'
        New-Item -ItemType Directory -Path $scriptsRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Start-GitHubRunner.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Invoke-DeploymentNotifier.ps1'), '')
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://github.com/actions/runner/releases/download/v1.0.0/actions-runner-win-x64-1.0.0.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
        $command = { param($request) $joined = $request.Arguments -join ' '; if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }; if ($request.FilePath -eq 'gh.exe' -and $joined -match '/actions/runners($|\s)') { return [pscustomobject]@{ ExitCode = 0; Stdout = '{"runners":[{"name":"duplicate-runner","status":"online","labels":[{"name":"agentassist-local"}]},{"name":"duplicate-runner","status":"offline","labels":[{"name":"agentassist-local"}]}]}'; Stderr = '' } }; if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'exec') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'READY'; Stderr = '' } }; if ($request.FilePath -eq 'pwsh.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'PowerShell 7.4.0'; Stderr = '' } }; if ($request.FilePath -eq 'git.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'git version 2.49.0'; Stderr = '' } }; if ($request.FilePath -eq 'gh.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'gh version 2.70.0'; Stderr = '' } }; if ($request.FilePath -eq 'dotnet.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = '8.0.400'; Stderr = '' } }; if ($request.FilePath -eq 'node.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'v22.0.0'; Stderr = '' } }; if ($request.FilePath -eq 'npm.cmd') { return [pscustomobject]@{ ExitCode = 0; Stdout = '10.0.0'; Stderr = '' } }; if ([IO.Path]::GetFileName($request.FilePath) -eq 'python.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Python 3.11.9'; Stderr = '' } }; if ($request.FilePath -eq 'codex') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'codex-cli 1.0.0'; Stderr = '' } }; [pscustomobject]@{ ExitCode = 0; Stdout = 'ok'; Stderr = '' } }.GetNewClosure()
        $threw = $false
        try { Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerLabel = 'agentassist-local'; runnerName = 'AutomationWorkbenchCodexRunner'; runnerRelease = $release; codexCommand = 'codex'; bootstrapPython = $script:BootstrapPython }) -DataRoot (Join-Path $TestDrive 'duplicate-data') -CommandRunner $command -DownloadRunner { param($request) } -HashRunner { param($path) ('a' * 64) } -ExtractRunner { param($request) } -TemporaryGitPath $TestDrive | Out-Null } catch { $threw = $true }
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
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://github.com/actions/runner/releases/download/v1.0.0/actions-runner-win-x64-1.0.0.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
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
            if ($request.FilePath -eq 'gh.exe' -and $joined -match '/actions/runners($|\s)') { if ($configRequests.Count -eq 0) { return [pscustomobject]@{ ExitCode = 0; Stdout = '{"runners":[]}'; Stderr = '' } }; return [pscustomobject]@{ ExitCode = 0; Stdout = '{"runners":[{"name":"AutomationWorkbenchCodexRunner","status":"online","labels":[{"name":"agentassist-local"}]}]}'; Stderr = '' } }
            if ([IO.Path]::GetFileName($request.FilePath) -eq 'config.cmd') { $configRequests.Add($request) | Out-Null; $configs.Add($request) | Out-Null; return [pscustomobject]@{ ExitCode = 0; Stdout = ''; Stderr = '' } }
            if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'exec') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'READY'; Stderr = '' } }
            if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'resume') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'resume supported'; Stderr = '' } }
            if ($request.FilePath -eq 'pwsh.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'PowerShell 7.4.0'; Stderr = '' } }
            if ($request.FilePath -eq 'git.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'git version 2.49.0'; Stderr = '' } }
            if ($request.FilePath -eq 'gh.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'gh version 2.70.0'; Stderr = '' } }
            if ($request.FilePath -eq 'dotnet.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = '8.0.400'; Stderr = '' } }
            if ($request.FilePath -eq 'node.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'v22.0.0'; Stderr = '' } }
            if ($request.FilePath -eq 'npm.cmd') { return [pscustomobject]@{ ExitCode = 0; Stdout = '10.0.0'; Stderr = '' } }
            if ([IO.Path]::GetFileName($request.FilePath) -eq 'python.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Python 3.11.9'; Stderr = '' } }
            if ($request.FilePath -eq 'codex') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'codex-cli 1.0.0'; Stderr = '' } }
            return [pscustomobject]@{ ExitCode = 0; Stdout = 'ok'; Stderr = '' }
        }.GetNewClosure()
        $extract = { param($request) $downloads.Add($request) | Out-Null; New-Item -ItemType Directory -Path $request.Destination -Force | Out-Null; [IO.File]::WriteAllText((Join-Path $request.Destination 'run.cmd'), ''); [IO.File]::WriteAllText((Join-Path $request.Destination 'config.cmd'), '') }.GetNewClosure()
        $taskRunner = { param($request) [pscustomobject]@{ ExitCode = 0 } }
        $config = [pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerLabel = 'agentassist-local'; runnerName = 'AutomationWorkbenchCodexRunner'; runnerRoot = $runnerRoot; runnerRelease = $release; codexCommand = 'codex'; bootstrapPython = $script:BootstrapPython }
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
        foreach ($mode in @('token', 'config', 'verify', 'metadata', 'move')) {
            $repoRoot = Join-Path $TestDrive ("transaction-$mode-repo")
            $scriptsRoot = Join-Path $repoRoot 'scripts\codex-worker'
            $dataRoot = Join-Path $TestDrive ("transaction-$mode-data")
            $runnerRoot = Join-Path $dataRoot 'runner'
            New-Item -ItemType Directory -Path $scriptsRoot -Force | Out-Null
            [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Start-GitHubRunner.ps1'), '')
            [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Invoke-DeploymentNotifier.ps1'), '')
            $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://github.com/actions/runner/releases/download/v1.0.0/actions-runner-win-x64-1.0.0.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
            $configRequests = [Collections.Generic.List[object]]::new()
            $removalRequests = [Collections.Generic.List[object]]::new()
            $emptyInventory = '{"runners":[]}'
            $onlineInventory = '{"runners":[{"name":"AutomationWorkbenchCodexRunner","status":"online","labels":[{"name":"agentassist-local"}]}]}'
            $command = {
                param($request)
                $joined = $request.Arguments -join ' '
                if ($request.FilePath -eq 'gh.exe') {
                    if ($request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }
                    if ($joined -match 'remove-token') { return [pscustomobject]@{ ExitCode = 0; Stdout = '{"token":"transaction-removal-secret"}'; Stderr = '' } }
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
                    if ($request.Arguments -contains 'remove') { $removalRequests.Add($request) | Out-Null }
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
                if ([IO.Path]::GetFileName($request.FilePath) -eq 'python.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Python 3.11.9'; Stderr = '' } }
                if ($request.FilePath -eq 'codex') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'codex-cli 1.0.0'; Stderr = '' } }
                return [pscustomobject]@{ ExitCode = 0; Stdout = 'ok'; Stderr = '' }
            }
            $command = $command.GetNewClosure()
            $extract = { param($request) New-Item -ItemType Directory -Path $request.Destination -Force | Out-Null; [IO.File]::WriteAllText((Join-Path $request.Destination 'run.cmd'), ''); [IO.File]::WriteAllText((Join-Path $request.Destination 'config.cmd'), '') }
            $metadataWriter = if ($mode -eq 'metadata') { { param($request) throw 'metadata sentinel' } } else { $null }
            $moveRunner = if ($mode -eq 'move') { { param($request) throw 'move sentinel' } } else { $null }
            $threw = $false
            $errorText = ''
            try { Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerLabel = 'agentassist-local'; runnerName = 'AutomationWorkbenchCodexRunner'; runnerRelease = $release; codexCommand = 'codex'; bootstrapPython = $script:BootstrapPython }) -DataRoot $dataRoot -CommandRunner $command -DownloadRunner { param($request) } -HashRunner { param($path) ('a' * 64) } -ExtractRunner $extract -TaskRunner { param($request) [pscustomobject]@{ ExitCode = 0 } } -MetadataWriter $metadataWriter -MoveRunner $moveRunner -TemporaryGitPath $TestDrive | Out-Null } catch { $threw = $true; $errorText = $_.Exception.Message }
            $threw | Should Be $true
            if ($mode -eq 'metadata' -or $mode -eq 'move') { $errorText | Should Match $mode }
            if ($mode -eq 'verify' -or $mode -eq 'metadata' -or $mode -eq 'move') { @($removalRequests).Count | Should Be 1 }
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
        $reparseRoot = Join-Path $dataRoot 'reparse-runner'
        New-Item -ItemType Directory -Path $reparseRoot -Force | Out-Null
        $reparseInspector = { param($path) if ([IO.Path]::GetFullPath($path).Equals([IO.Path]::GetFullPath($reparseRoot), [StringComparison]::OrdinalIgnoreCase)) { return [pscustomobject]@{ IsReparsePoint = $true } }; [pscustomobject]@{ IsReparsePoint = $false } }.GetNewClosure()
        $threwReparse = $false
        try { Get-CodexLocalWorkerPlan -Repository 'owner/repo' -RepositoryRoot $repoRoot -DataRoot $dataRoot -Config ([pscustomobject]@{ runnerRoot = $reparseRoot }) -PathInspector $reparseInspector | Out-Null } catch { $threwReparse = $true }
        $threwReparse | Should Be $true
    }

    It 'rejects a reparse-point worker data root before any command or mutation' {
        $repoRoot = Join-Path $TestDrive 'reparse-repo'
        $dataRoot = Join-Path $TestDrive 'reparse-data'
        $calls = [Collections.Generic.List[object]]::new()
        $inspector = { param($path) if ([IO.Path]::GetFullPath($path).Equals([IO.Path]::GetFullPath($dataRoot), [StringComparison]::OrdinalIgnoreCase)) { return [pscustomobject]@{ IsReparsePoint = $true } }; [pscustomobject]@{ IsReparsePoint = $false } }.GetNewClosure()
        $threw = $false
        try { Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot }) -DataRoot $dataRoot -CommandRunner { param($request) $calls.Add($request) | Out-Null; [pscustomobject]@{ ExitCode = 0; Stdout = 'ok'; Stderr = '' } } -PathInspector $inspector | Out-Null } catch { $threw = $true }
        $threw | Should Be $true
        @($calls).Count | Should Be 0
        (Test-Path -LiteralPath $dataRoot) | Should Be $false
    }

    It 'rolls back a configured runner after bounded online timeout and preserves the original error' {
        $repoRoot = Join-Path $TestDrive 'online-timeout-repo'
        $scriptsRoot = Join-Path $repoRoot 'scripts\codex-worker'
        $dataRoot = Join-Path $TestDrive 'online-timeout-data'
        $runnerRoot = Join-Path $dataRoot 'runner'
        New-Item -ItemType Directory -Path $scriptsRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Start-GitHubRunner.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Invoke-DeploymentNotifier.ps1'), '')
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://github.com/actions/runner/releases/download/v1.0.0/actions-runner-win-x64-1.0.0.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
        $requests = [Collections.Generic.List[object]]::new()
        $configRequests = [Collections.Generic.List[object]]::new()
        $removals = [Collections.Generic.List[object]]::new()
        $runnerStops = [Collections.Generic.List[object]]::new()
        $taskState = @{}
        $command = {
            param($request)
            $requests.Add($request) | Out-Null
            $joined = $request.Arguments -join ' '
            if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }
            if ($request.FilePath -eq 'gh.exe' -and $joined -match '/actions/runners($|\s)') { if ($configRequests.Count -eq 0) { return [pscustomobject]@{ ExitCode = 0; Stdout = '{"runners":[]}'; Stderr = '' } }; return [pscustomobject]@{ ExitCode = 0; Stdout = '{"runners":[{"name":"AutomationWorkbenchCodexRunner","status":"offline","labels":[{"name":"agentassist-local"}]}]}'; Stderr = '' } }
            if ($request.FilePath -eq 'gh.exe' -and $joined -match 'registration-token') { return [pscustomobject]@{ ExitCode = 0; Stdout = '{"token":"registration-secret"}'; Stderr = '' } }
            if ($request.FilePath -eq 'gh.exe' -and $joined -match 'remove-token') { return [pscustomobject]@{ ExitCode = 0; Stdout = '{"token":"removal-secret"}'; Stderr = '' } }
            if ([IO.Path]::GetFileName($request.FilePath) -eq 'config.cmd') { $configRequests.Add($request) | Out-Null; if ($request.Arguments -contains 'remove') { $removals.Add($request) | Out-Null }; return [pscustomobject]@{ ExitCode = 0; Stdout = ''; Stderr = '' } }
            if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'exec') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'READY'; Stderr = '' } }
            if ($request.FilePath -eq 'pwsh.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'PowerShell 7.4.0'; Stderr = '' } }
            if ($request.FilePath -eq 'git.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'git version 2.49.0'; Stderr = '' } }
            if ($request.FilePath -eq 'gh.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'gh version 2.70.0'; Stderr = '' } }
            if ($request.FilePath -eq 'dotnet.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = '8.0.400'; Stderr = '' } }
            if ($request.FilePath -eq 'node.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'v22.0.0'; Stderr = '' } }
            if ($request.FilePath -eq 'npm.cmd') { return [pscustomobject]@{ ExitCode = 0; Stdout = '10.0.0'; Stderr = '' } }
            if ([IO.Path]::GetFileName($request.FilePath) -eq 'python.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Python 3.11.9'; Stderr = '' } }
            if ($request.FilePath -eq 'codex') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'codex-cli 1.0.0'; Stderr = '' } }
            return [pscustomobject]@{ ExitCode = 0; Stdout = 'ok'; Stderr = '' }
        }.GetNewClosure()
        $extract = { param($request) New-Item -ItemType Directory -Path $request.Destination -Force | Out-Null; [IO.File]::WriteAllText((Join-Path $request.Destination 'run.cmd'), ''); [IO.File]::WriteAllText((Join-Path $request.Destination 'config.cmd'), '') }
        $taskStart = { param($request) [pscustomobject]@{ ExitCode = 0 } }
        $taskQuery = { param($request) if ($taskState.ContainsKey($request.Task.Name)) { return [pscustomobject]@{ Exists = $true; Xml = $taskState[$request.Task.Name] } }; [pscustomobject]@{ Exists = $false } }.GetNewClosure()
        $taskCreate = { param($request) $taskState[$request.Task.Name] = $request.Xml; [pscustomobject]@{ ExitCode = 0 } }.GetNewClosure()
        $threw = $false
        $errorText = ''
        try { Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerLabel = 'agentassist-local'; runnerName = 'AutomationWorkbenchCodexRunner'; runnerRelease = $release; codexCommand = 'codex'; bootstrapPython = $script:BootstrapPython }) -DataRoot $dataRoot -CommandRunner $command -DownloadRunner { param($request) } -HashRunner { param($path) ('a' * 64) } -ExtractRunner $extract -TaskQueryRunner $taskQuery -TaskRunner $taskCreate -TaskStartRunner $taskStart -TaskStopRunner { param($request) $runnerStops.Add($request) | Out-Null; [pscustomobject]@{ ExitCode = 0 } }.GetNewClosure() -DelayRunner { param($milliseconds) } -OnlinePollAttempts 2 -PollDelayMilliseconds 0 -TemporaryGitPath $TestDrive | Out-Null } catch { $threw = $true; $errorText = $_.Exception.Message }
        $threw | Should Be $true
        $errorText | Should Match 'bounded poll window'
        @($removals).Count | Should Be 1
        @($runnerStops).Count | Should Be 1
        $runnerStops[0].Action | Should Be 'Stop'
        @($removals[0].Arguments | Where-Object { $_ -eq 'removal-secret' }).Count | Should Be 1
        (($requests | Where-Object { $_ -ne $removals[0] } | ForEach-Object { $_ | ConvertTo-Json -Depth 10 }) -join "`n") | Should Not Match 'removal-secret'
        (Test-Path -LiteralPath $runnerRoot) | Should Be $false
    }

    It 'rejects a reparse staging ancestry before download or extraction' {
        $repoRoot = Join-Path $TestDrive 'staging-reparse-repo'
        $scriptsRoot = Join-Path $repoRoot 'scripts\codex-worker'
        $dataRoot = Join-Path $TestDrive 'staging-reparse-data'
        New-Item -ItemType Directory -Path $scriptsRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Start-GitHubRunner.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Invoke-DeploymentNotifier.ps1'), '')
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://github.com/actions/runner/releases/download/v1.0.0/actions-runner-win-x64-1.0.0.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
        $calls = [Collections.Generic.List[object]]::new()
        $inspector = { param($path) if ($path -match '\.staging') { return [pscustomobject]@{ IsReparsePoint = $true } }; [pscustomobject]@{ IsReparsePoint = $false } }
        $command = { param($request) $calls.Add($request) | Out-Null; if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }; if ($request.FilePath -eq 'gh.exe' -and ($request.Arguments -join ' ') -match '/actions/runners') { return [pscustomobject]@{ ExitCode = 0; Stdout = '{"runners":[]}'; Stderr = '' } }; [pscustomobject]@{ ExitCode = 0; Stdout = '8.0.0'; Stderr = '' } }.GetNewClosure()
        $threw = $false
        try { Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerRelease = $release }) -DataRoot $dataRoot -CommandRunner $command -PathInspector $inspector -DownloadRunner { throw 'download must not run' } | Out-Null } catch { $threw = $true }
        $threw | Should Be $true
        @($calls | Where-Object { $_.FilePath -eq 'gh.exe' -and (($_.Arguments -join ' ') -match 'registration-token') }).Count | Should Be 0
        (Test-Path -LiteralPath $dataRoot) | Should Be $false
    }

    It 'rechecks staging ancestry immediately before download after a reparse swap' {
        $repoRoot = Join-Path $TestDrive 'staging-toctou-repo'
        $scriptsRoot = Join-Path $repoRoot 'scripts\codex-worker'
        $dataRoot = Join-Path $TestDrive 'staging-toctou-data'
        New-Item -ItemType Directory -Path $scriptsRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Start-GitHubRunner.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Invoke-DeploymentNotifier.ps1'), '')
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://github.com/actions/runner/releases/download/v1.0.0/actions-runner-win-x64-1.0.0.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
        $calls = [Collections.Generic.List[object]]::new()
        $stagingChecks = 0
        $inspector = {
            param($path)
            if ($path -match '\.staging') {
                $stagingChecks++
                if ($stagingChecks -gt 1) { return [pscustomobject]@{ IsReparsePoint = $true } }
            }
            return [pscustomobject]@{ IsReparsePoint = $false }
        }.GetNewClosure()
        $command = { param($request) $calls.Add($request) | Out-Null; if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }; if ($request.FilePath -eq 'gh.exe' -and ($request.Arguments -join ' ') -match '/actions/runners') { return [pscustomobject]@{ ExitCode = 0; Stdout = '{"runners":[]}'; Stderr = '' } }; [pscustomobject]@{ ExitCode = 0; Stdout = '8.0.0'; Stderr = '' } }.GetNewClosure()
        $downloaded = $false
        $threw = $false
        try { Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerRelease = $release }) -DataRoot $dataRoot -CommandRunner $command -PathInspector $inspector -TaskQueryRunner { param($request) [pscustomobject]@{ Exists = $false } } -DownloadRunner { param($request) $downloaded = $true }.GetNewClosure() | Out-Null } catch { $threw = $true }
        $threw | Should Be $true
        $downloaded | Should Be $false
    }

    It 'restores an existing config and resume temp state after installation failure' {
        $repoRoot = Join-Path $TestDrive 'config-rollback-repo'
        $scriptsRoot = Join-Path $repoRoot 'scripts\codex-worker'
        $dataRoot = Join-Path $TestDrive 'config-rollback-data'
        New-Item -ItemType Directory -Path $scriptsRoot, $dataRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Start-GitHubRunner.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Invoke-DeploymentNotifier.ps1'), '')
        $configPath = Join-Path $dataRoot 'config.json'; $tempPath = "$configPath.tmp"
        [IO.File]::WriteAllBytes($configPath, [Text.Encoding]::UTF8.GetBytes('{"preexisting":true}'))
        [IO.File]::WriteAllBytes($tempPath, [Text.Encoding]::UTF8.GetBytes('preexisting-temp'))
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://github.com/actions/runner/releases/download/v1.0.0/actions-runner-win-x64-1.0.0.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
        $configured = [Collections.Generic.List[object]]::new()
        $command = { param($request) $joined = $request.Arguments -join ' '; if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }; if ($request.FilePath -eq 'gh.exe' -and $joined -match 'registration-token') { return [pscustomobject]@{ ExitCode = 0; Stdout = '{"token":"registration-secret"}'; Stderr = '' } }; if ([IO.Path]::GetFileName($request.FilePath) -eq 'config.cmd') { $configured.Add($request) | Out-Null; return [pscustomobject]@{ ExitCode = 0; Stdout = ''; Stderr = '' } }; if ($request.FilePath -eq 'gh.exe' -and $joined -match '/actions/runners') { $json = if ($configured.Count -gt 0) { '{"runners":[{"name":"AutomationWorkbenchCodexRunner","repository":"owner/repo","status":"online","labels":[{"name":"agentassist-local"}]}]}' } else { '{"runners":[]}' }; return [pscustomobject]@{ ExitCode = 0; Stdout = $json; Stderr = '' } }; if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'exec') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'READY'; Stderr = '' } }; if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'resume') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'resume supported'; Stderr = '' } }; if ($request.FilePath -eq 'node.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'v22.0.0'; Stderr = '' } }; if ([IO.Path]::GetFileName($request.FilePath) -eq 'python.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Python 3.11.9'; Stderr = '' } }; [pscustomobject]@{ ExitCode = 0; Stdout = '8.0.0'; Stderr = '' } }.GetNewClosure()
        $configBefore = [IO.File]::ReadAllBytes($configPath); $tempBefore = [IO.File]::ReadAllBytes($tempPath)
        try { Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerRelease = $release }) -DataRoot $dataRoot -CommandRunner $command -TaskQueryRunner { param($request) [pscustomobject]@{ Exists = $false } } -DownloadRunner { param($request) } -HashRunner { param($path) ('a' * 64) } -ExtractRunner { param($request) } -TaskRunner { param($request) throw 'task failure' } -TemporaryGitPath $TestDrive | Out-Null } catch { }
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($configPath)) | Should Be ([Convert]::ToBase64String($configBefore))
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($tempPath)) | Should Be ([Convert]::ToBase64String($tempBefore))
    }

    It 'removes config and resume temp state created by a failed installation' {
        $repoRoot = Join-Path $TestDrive 'config-new-rollback-repo'
        $scriptsRoot = Join-Path $repoRoot 'scripts\codex-worker'; $dataRoot = Join-Path $TestDrive 'config-new-rollback-data'
        New-Item -ItemType Directory -Path $scriptsRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Start-GitHubRunner.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Invoke-DeploymentNotifier.ps1'), '')
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://github.com/actions/runner/releases/download/v1.0.0/actions-runner-win-x64-1.0.0.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
        $configured = [Collections.Generic.List[object]]::new()
        $command = { param($request) $joined = $request.Arguments -join ' '; if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }; if ($request.FilePath -eq 'gh.exe' -and $joined -match 'registration-token') { return [pscustomobject]@{ ExitCode = 0; Stdout = '{"token":"registration-secret"}'; Stderr = '' } }; if ([IO.Path]::GetFileName($request.FilePath) -eq 'config.cmd') { $configured.Add($request) | Out-Null; return [pscustomobject]@{ ExitCode = 0; Stdout = ''; Stderr = '' } }; if ($request.FilePath -eq 'gh.exe' -and $joined -match '/actions/runners') { $json = if ($configured.Count -gt 0) { '{"runners":[{"name":"AutomationWorkbenchCodexRunner","repository":"owner/repo","status":"online","labels":[{"name":"agentassist-local"}]}]}' } else { '{"runners":[]}' }; return [pscustomobject]@{ ExitCode = 0; Stdout = $json; Stderr = '' } }; if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'exec') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'READY'; Stderr = '' } }; if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'resume') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'resume supported'; Stderr = '' } }; if ($request.FilePath -eq 'node.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'v22.0.0'; Stderr = '' } }; if ([IO.Path]::GetFileName($request.FilePath) -eq 'python.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Python 3.11.9'; Stderr = '' } }; [pscustomobject]@{ ExitCode = 0; Stdout = '8.0.0'; Stderr = '' } }.GetNewClosure()
        try { Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerRelease = $release }) -DataRoot $dataRoot -CommandRunner $command -TaskQueryRunner { param($request) [pscustomobject]@{ Exists = $false } } -DownloadRunner { param($request) } -HashRunner { param($path) ('a' * 64) } -ExtractRunner { param($request) } -TaskRunner { param($request) throw 'task failure' } -TemporaryGitPath $TestDrive | Out-Null } catch { }
        (Test-Path -LiteralPath (Join-Path $dataRoot 'config.json')) | Should Be $false
        (Test-Path -LiteralPath (Join-Path $dataRoot 'config.json.tmp')) | Should Be $false
    }

    It 'preflights mismatched scheduled tasks before any installer mutation' {
        $repoRoot = Join-Path $TestDrive 'task-preflight-repo'; $scriptsRoot = Join-Path $repoRoot 'scripts\codex-worker'; $dataRoot = Join-Path $TestDrive 'task-preflight-data'
        New-Item -ItemType Directory -Path $scriptsRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Start-GitHubRunner.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Invoke-DeploymentNotifier.ps1'), '')
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://github.com/actions/runner/releases/download/v1.0.0/actions-runner-win-x64-1.0.0.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
        $downloaded = $false
        $query = { param($request) [pscustomobject]@{ Exists = $true; Xml = '<Task><Actions><Exec><Command>evil.exe</Command></Exec></Actions></Task>' } }
        $command = { param($request) [pscustomobject]@{ ExitCode = 0; Stdout = '8.0.0'; Stderr = '' } }
        $threw = $false
        try { Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerRelease = $release }) -DataRoot $dataRoot -CommandRunner $command -TaskQueryRunner $query -DownloadRunner { param($request) $downloaded = $true }.GetNewClosure() -TaskRunner { throw 'must not create' } -TemporaryGitPath $TestDrive | Out-Null } catch { $threw = $true }
        $threw | Should Be $true
        $downloaded | Should Be $false
        (Test-Path -LiteralPath $dataRoot) | Should Be $false
    }

    It 'marks an absent scheduled task attempt-owned before side-effecting create throws' {
        $repoRoot = Join-Path $TestDrive 'task-side-effect-repo'; $scriptsRoot = Join-Path $repoRoot 'scripts\codex-worker'; $dataRoot = Join-Path $TestDrive 'task-side-effect-data'
        New-Item -ItemType Directory -Path $scriptsRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Start-GitHubRunner.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Invoke-DeploymentNotifier.ps1'), '')
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://github.com/actions/runner/releases/download/v1.0.0/actions-runner-win-x64-1.0.0.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
        $removed = [Collections.Generic.List[object]]::new()
        $taskSeen = [Collections.Generic.List[object]]::new()
        $configured = [Collections.Generic.List[object]]::new()
        $taskState = @{}
        $command = { param($request) $joined = $request.Arguments -join ' '; if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }; if ($request.FilePath -eq 'gh.exe' -and $joined -match 'registration-token') { return [pscustomobject]@{ ExitCode = 0; Stdout = '{"token":"registration-secret"}'; Stderr = '' } }; if ([IO.Path]::GetFileName($request.FilePath) -eq 'config.cmd') { $configured.Add($request) | Out-Null; return [pscustomobject]@{ ExitCode = 0; Stdout = ''; Stderr = '' } }; if ($request.FilePath -eq 'gh.exe' -and $joined -match '/actions/runners') { $json = if ($configured.Count -gt 0) { '{"runners":[{"name":"AutomationWorkbenchCodexRunner","repository":"owner/repo","status":"online","labels":[{"name":"agentassist-local"}]}]}' } else { '{"runners":[]}' }; return [pscustomobject]@{ ExitCode = 0; Stdout = $json; Stderr = '' } }; if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'exec') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'READY'; Stderr = '' } }; if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'resume') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'resume supported'; Stderr = '' } }; if ($request.FilePath -eq 'node.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'v22.0.0'; Stderr = '' } }; if ([IO.Path]::GetFileName($request.FilePath) -eq 'python.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Python 3.11.9'; Stderr = '' } }; [pscustomobject]@{ ExitCode = 0; Stdout = '8.0.0'; Stderr = '' } }.GetNewClosure()
        $query = { param($request) if ($taskState.ContainsKey($request.Task.Name)) { return [pscustomobject]@{ Exists = $true; Xml = $taskState[$request.Task.Name] } }; [pscustomobject]@{ Exists = $false } }.GetNewClosure()
        $task = { param($request) $taskSeen.Add($request) | Out-Null; $taskState[$request.Task.Name] = $request.Xml; throw 'create side effect failure' }.GetNewClosure()
        $errorText = ''
        try { Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerRelease = $release; bootstrapPython = $script:BootstrapPython }) -DataRoot $dataRoot -CommandRunner $command -TaskQueryRunner $query -DownloadRunner { param($request) } -HashRunner { param($path) ('a' * 64) } -ExtractRunner { param($request) } -TaskRunner $task -TaskRemoveRunner { param($request) $removed.Add($request) | Out-Null; [pscustomobject]@{ ExitCode = 0 } }.GetNewClosure() -TemporaryGitPath $TestDrive | Out-Null } catch { $errorText = $_.Exception.ToString() }
        if (@($taskSeen).Count -eq 0) { throw $errorText }
        @($taskSeen).Count | Should Be 1
        @($removed | Where-Object { $_.Action -eq 'Delete' }).Count | Should BeGreaterThan 0
        ($taskSeen[0].Arguments -contains '/F') | Should Be $false
        $taskSeen[0].Xml | Should Match '(?i)ownership marker'
    }

    It 'leaves a concurrent foreign task untouched when an absent create fails' {
        $repoRoot = Join-Path $TestDrive 'task-concurrent-repo'; $scriptsRoot = Join-Path $repoRoot 'scripts\codex-worker'; $dataRoot = Join-Path $TestDrive 'task-concurrent-data'
        New-Item -ItemType Directory -Path $scriptsRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Start-GitHubRunner.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Invoke-DeploymentNotifier.ps1'), '')
        $runnerRoot = Join-Path $dataRoot 'runner'; New-Item -ItemType Directory -Path $runnerRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $runnerRoot 'run.cmd'), '')
        [IO.File]::WriteAllText((Join-Path $runnerRoot 'runner-install.json'), (@{ repositoryUrl = 'https://github.com/owner/repo'; runnerName = 'AutomationWorkbenchCodexRunner'; assetName = 'actions-runner-win-x64-1.0.0.zip'; releaseTag = 'v1.0.0'; labels = @('agentassist-local'); version = '1.0.0'; sha256 = ('a' * 64) } | ConvertTo-Json))
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://github.com/actions/runner/releases/download/v1.0.0/actions-runner-win-x64-1.0.0.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
        $removed = [Collections.Generic.List[object]]::new(); $state = @{}
        $foreignXml = '<Task><RegistrationInfo><Description>foreign task</Description></RegistrationInfo><Actions><Exec><Command>evil.exe</Command></Exec></Actions></Task>'
        $command = { param($request) $joined = $request.Arguments -join ' '; if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }; if ($request.FilePath -eq 'gh.exe' -and $joined -match 'registration-token') { return [pscustomobject]@{ ExitCode = 0; Stdout = '{"token":"registration-secret"}'; Stderr = '' } }; if ([IO.Path]::GetFileName($request.FilePath) -eq 'config.cmd') { return [pscustomobject]@{ ExitCode = 0; Stdout = ''; Stderr = '' } }; if ($request.FilePath -eq 'gh.exe' -and $joined -match '/actions/runners') { return [pscustomobject]@{ ExitCode = 0; Stdout = '{"runners":[{"name":"AutomationWorkbenchCodexRunner","repository":"owner/repo","status":"online","labels":[{"name":"agentassist-local"}]}]}'; Stderr = '' } }; if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'exec') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'READY'; Stderr = '' } }; if ($request.FilePath -eq 'node.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'v22.0.0'; Stderr = '' } }; if ([IO.Path]::GetFileName($request.FilePath) -eq 'python.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Python 3.11.9'; Stderr = '' } }; [pscustomobject]@{ ExitCode = 0; Stdout = '8.0.0'; Stderr = '' } }.GetNewClosure()
        $query = { param($request) if ($state.ContainsKey($request.Task.Name)) { return [pscustomobject]@{ Exists = $true; Xml = $state[$request.Task.Name] } }; [pscustomobject]@{ Exists = $false } }.GetNewClosure()
        $task = { param($request) $state[$request.Task.Name] = $foreignXml; throw 'concurrent task appeared' }.GetNewClosure()
        $errorText = ''; try { Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerRoot = $runnerRoot; runnerRelease = $release; bootstrapPython = $script:BootstrapPython }) -DataRoot $dataRoot -CommandRunner $command -TaskQueryRunner $query -DownloadRunner { param($request) } -HashRunner { param($path) ('a' * 64) } -ExtractRunner { param($request) } -TaskRunner $task -TaskRemoveRunner { param($request) $removed.Add($request) | Out-Null }.GetNewClosure() -TemporaryGitPath $TestDrive | Out-Null } catch { $errorText = $_.Exception.ToString() }
        if ($state.Count -eq 0) { throw $errorText }
        @($removed | Where-Object { $_.Action -eq 'Delete' }).Count | Should Be 0
        $state.ContainsValue($foreignXml) | Should Be $true
    }

    It 'revalidates every created task marker before rollback stop and delete' {
        $repoRoot = Join-Path $TestDrive 'task-rollback-marker-repo'; $scriptsRoot = Join-Path $repoRoot 'scripts\codex-worker'; $dataRoot = Join-Path $TestDrive 'task-rollback-marker-data'
        New-Item -ItemType Directory -Path $scriptsRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Start-GitHubRunner.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Invoke-DeploymentNotifier.ps1'), '')
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://github.com/actions/runner/releases/download/v1.0.0/actions-runner-win-x64-1.0.0.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
        $state = @{}
        $configured = [Collections.Generic.List[object]]::new()
        $created = [Collections.Generic.List[object]]::new()
        $stopped = [Collections.Generic.List[object]]::new()
        $removed = [Collections.Generic.List[object]]::new()
        $foreignXml = '<Task><RegistrationInfo><Description>foreign replacement</Description></RegistrationInfo><Actions><Exec><Command>evil.exe</Command></Exec></Actions></Task>'
        $command = {
            param($request)
            $joined = $request.Arguments -join ' '
            if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }
            if ($request.FilePath -eq 'gh.exe' -and $joined -match 'registration-token') { return [pscustomobject]@{ ExitCode = 0; Stdout = '{"token":"registration-secret"}'; Stderr = '' } }
            if ($request.FilePath -eq 'gh.exe' -and $joined -match '/actions/runners') { $json = if ($configured.Count -gt 0) { '{"runners":[{"name":"AutomationWorkbenchCodexRunner","repository":"owner/repo","status":"online","labels":[{"name":"agentassist-local"}]}]}' } else { '{"runners":[]}' }; return [pscustomobject]@{ ExitCode = 0; Stdout = $json; Stderr = '' } }
            if ([IO.Path]::GetFileName($request.FilePath) -eq 'config.cmd') { $configured.Add($request) | Out-Null; return [pscustomobject]@{ ExitCode = 0; Stdout = ''; Stderr = '' } }
            if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'exec') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'READY'; Stderr = '' } }
            if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'resume') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'resume supported'; Stderr = '' } }
            if ($request.FilePath -eq 'node.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'v22.0.0'; Stderr = '' } }
            if ([IO.Path]::GetFileName($request.FilePath) -eq 'python.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Python 3.11.9'; Stderr = '' } }
            [pscustomobject]@{ ExitCode = 0; Stdout = '8.0.0'; Stderr = '' }
        }.GetNewClosure()
        $query = { param($request) if ($state.ContainsKey($request.Task.Name)) { return [pscustomobject]@{ Exists = $true; Xml = $state[$request.Task.Name] } }; [pscustomobject]@{ Exists = $false } }.GetNewClosure()
        $task = {
            param($request)
            $created.Add($request) | Out-Null
            $state[$request.Task.Name] = $request.Xml
            if ($request.Task.Name -eq 'AutomationWorkbenchCodexDeploymentNotifier') {
                $state['AutomationWorkbenchCodexRunner'] = $foreignXml
                return [pscustomobject]@{ ExitCode = 1; Stdout = ''; Stderr = 'induced failure' }
            }
            [pscustomobject]@{ ExitCode = 0; Stdout = ''; Stderr = '' }
        }.GetNewClosure()
        $config = [pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerRelease = $release }
        $threw = $false
        try {
            Invoke-CodexLocalWorkerSetup -Config ($config | Add-Member -NotePropertyName bootstrapPython -NotePropertyValue $script:BootstrapPython -PassThru) -DataRoot $dataRoot -CommandRunner $command -TaskQueryRunner $query -DownloadRunner { param($request) } -HashRunner { param($path) ('a' * 64) } -ExtractRunner { param($request) } -TaskRunner $task -TaskStopRunner { param($request) $stopped.Add($request) | Out-Null; [pscustomobject]@{ ExitCode = 0 } }.GetNewClosure() -TaskRemoveRunner { param($request) $removed.Add($request) | Out-Null; [pscustomobject]@{ ExitCode = 0 } }.GetNewClosure() -TemporaryGitPath $TestDrive | Out-Null
        } catch { $threw = $true; $errorText = $_.Exception.ToString() }
        $threw | Should Be $true
        if (@($created).Count -ne 2) { throw $errorText }
        @($created).Count | Should Be 2
        @($stopped | Where-Object TaskName -eq 'AutomationWorkbenchCodexRunner').Count | Should Be 0
        @($removed | Where-Object TaskName -eq 'AutomationWorkbenchCodexRunner').Count | Should Be 0
        @($stopped | Where-Object TaskName -eq 'AutomationWorkbenchCodexDeploymentNotifier').Count | Should Be 1
        @($removed | Where-Object TaskName -eq 'AutomationWorkbenchCodexDeploymentNotifier').Count | Should Be 1
    }

    It 'reuses a prior installer task marker when stable task semantics match' {
        $repoRoot = Join-Path $TestDrive 'task-reuse-repo'; $scriptsRoot = Join-Path $repoRoot 'scripts\codex-worker'; $dataRoot = Join-Path $TestDrive 'task-reuse-data'
        New-Item -ItemType Directory -Path $scriptsRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Start-GitHubRunner.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Invoke-DeploymentNotifier.ps1'), '')
        $runnerRoot = Join-Path $dataRoot 'runner'; New-Item -ItemType Directory -Path $runnerRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $runnerRoot 'run.cmd'), '')
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://github.com/actions/runner/releases/download/v1.0.0/actions-runner-win-x64-1.0.0.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
        [IO.File]::WriteAllText((Join-Path $runnerRoot 'runner-install.json'), (@{ repositoryUrl = 'https://github.com/owner/repo'; runnerName = 'AutomationWorkbenchCodexRunner'; assetName = 'actions-runner-win-x64-1.0.0.zip'; releaseTag = 'v1.0.0'; labels = @('agentassist-local'); version = '1.0.0'; sha256 = ('a' * 64) } | ConvertTo-Json))
        $priorPlan = Get-CodexLocalWorkerPlan -Repository 'owner/repo' -RepositoryRoot $repoRoot -DataRoot $dataRoot -Config ([pscustomobject]@{ runnerRoot = $runnerRoot })
        $tasks = [Collections.Generic.List[object]]::new(); $starts = [Collections.Generic.List[object]]::new()
        $command = { param($request) $joined = $request.Arguments -join ' '; if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }; if ($request.FilePath -eq 'gh.exe' -and $joined -match '/actions/runners') { return [pscustomobject]@{ ExitCode = 0; Stdout = '{"runners":[{"name":"AutomationWorkbenchCodexRunner","repository":"owner/repo","status":"online","labels":[{"name":"agentassist-local"}]}]}'; Stderr = '' } }; if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'exec') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'READY'; Stderr = '' } }; if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'resume') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'resume supported'; Stderr = '' } }; if ($request.FilePath -eq 'node.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'v22.0.0'; Stderr = '' } }; if ([IO.Path]::GetFileName($request.FilePath) -eq 'python.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Python 3.11.9'; Stderr = '' } }; [pscustomobject]@{ ExitCode = 0; Stdout = '8.0.0'; Stderr = '' } }.GetNewClosure()
        $query = { param($request) $xml = if ($request.Task.Name -match 'Runner$') { $priorPlan.Tasks.Runner.Xml } else { $priorPlan.Tasks.Notifier.Xml }; [pscustomobject]@{ Exists = $true; Xml = $xml } }.GetNewClosure()
        $config = [pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerRoot = $runnerRoot; runnerRelease = $release; codexCommand = 'codex'; bootstrapPython = $script:BootstrapPython }
        $result = Invoke-CodexLocalWorkerSetup -Config $config -DataRoot $dataRoot -CommandRunner $command -TaskQueryRunner $query -TaskRunner { param($request) $tasks.Add($request) | Out-Null; [pscustomobject]@{ ExitCode = 0 } }.GetNewClosure() -TaskStartRunner { param($request) $starts.Add($request) | Out-Null; [pscustomobject]@{ ExitCode = 0 } }.GetNewClosure() -ProcessProvider { @() } -TemporaryGitPath $TestDrive
        $result.Plan.Tasks.Runner.Name | Should Be 'AutomationWorkbenchCodexRunner'
        @($tasks).Count | Should Be 0
        @($starts).Count | Should Be 1
    }

    It 'removes broad explicit and inherited write access before staging download' {
        $repoRoot = Join-Path $TestDrive 'acl-repo'; $scriptsRoot = Join-Path $repoRoot 'scripts\codex-worker'; $dataRoot = Join-Path $TestDrive 'acl-data'; $stagingParent = Join-Path $dataRoot '.staging'
        New-Item -ItemType Directory -Path $scriptsRoot, $stagingParent -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Start-GitHubRunner.ps1'), '')
        [IO.File]::WriteAllText((Join-Path $scriptsRoot 'Invoke-DeploymentNotifier.ps1'), '')
        $broadIdentity = New-Object System.Security.Principal.NTAccount('BUILTIN\Users')
        $acl = Get-Acl -LiteralPath $stagingParent
        $broadRule = [Security.AccessControl.FileSystemAccessRule]::new($broadIdentity, [Security.AccessControl.FileSystemRights]::Modify, [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit, [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
        $acl.AddAccessRule($broadRule); Set-Acl -LiteralPath $stagingParent -AclObject $acl
        $release = [pscustomobject]@{ tag_name = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'actions-runner-win-x64-1.0.0.zip'; browser_download_url = 'https://github.com/actions/runner/releases/download/v1.0.0/actions-runner-win-x64-1.0.0.zip' }); body = 'actions-runner-win-x64-1.0.0.zip sha256: ' + ('a' * 64) }
        $seen = [Collections.Generic.List[string]]::new()
        $stagingAclSnapshots = [Collections.Generic.List[object]]::new()
        $command = { param($request) if ($request.FilePath -eq 'gh.exe' -and $request.Arguments -contains 'status') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Logged in'; Stderr = '' } }; if ($request.FilePath -eq 'gh.exe' -and ($request.Arguments -join ' ') -match '/actions/runners') { return [pscustomobject]@{ ExitCode = 0; Stdout = '{"runners":[]}'; Stderr = '' } }; if ($request.FilePath -eq 'codex' -and $request.Arguments -contains 'exec') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'READY'; Stderr = '' } }; if ($request.FilePath -eq 'node.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'v22.0.0'; Stderr = '' } }; if ([IO.Path]::GetFileName($request.FilePath) -eq 'python.exe') { return [pscustomobject]@{ ExitCode = 0; Stdout = 'Python 3.11.9'; Stderr = '' } }; [pscustomobject]@{ ExitCode = 0; Stdout = '8.0.0'; Stderr = '' } }.GetNewClosure()
        try { Invoke-CodexLocalWorkerSetup -Config ([pscustomobject]@{ repository = 'owner/repo'; repositoryRoot = $repoRoot; runnerRelease = $release; bootstrapPython = $script:BootstrapPython }) -DataRoot $dataRoot -CommandRunner $command -TaskQueryRunner { param($request) [pscustomobject]@{ Exists = $false } } -DownloadRunner { param($request) $seen.Add($request.Destination) | Out-Null; $stagingAclSnapshots.Add((Get-Acl -LiteralPath (Split-Path -Parent $request.Destination))) | Out-Null; [IO.File]::WriteAllText((Join-Path (Split-Path -Parent (Split-Path -Parent $request.Destination)) 'keep.txt'), 'keep'); throw 'stop after ACL check' }.GetNewClosure() -TemporaryGitPath $TestDrive | Out-Null } catch { }
        $parentAcl = Get-Acl -LiteralPath $stagingParent
        $parentAcl.AreAccessRulesProtected | Should Be $true
        @($parentAcl.Access | Where-Object { $_.IdentityReference -match '(?i)Everyone|Users|Authenticated Users' -and $_.FileSystemRights.ToString() -match '(?i)Write|Modify|FullControl' }).Count | Should Be 0
        $seen.Count | Should Be 1
        $stagingAclSnapshots.Count | Should Be 1
        $stagingAclSnapshots[0].AreAccessRulesProtected | Should Be $true
        @($stagingAclSnapshots[0].Access | Where-Object { $_.IsInherited }).Count | Should Be 0
    }

    It 'applies and verifies the real ACL helper under PowerShell 7' {
        if ($PSVersionTable.PSEdition -ne 'Core') { return }
        $path = Join-Path $TestDrive 'pwsh-real-acl'
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        { Ensure-CodexTrustedDirectory -Path $path } | Should Not Throw
        { Assert-CodexTrustedDirectoryAcl -Path $path } | Should Not Throw
    }
}
