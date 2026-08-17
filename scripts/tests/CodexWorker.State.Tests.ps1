Describe 'Codex worker paths' {
    BeforeEach {
        Import-Module (Join-Path $PSScriptRoot '..\codex-worker\CodexWorker.psd1') -Force
    }

    It 'keeps durable state outside the repository' {
        $paths = Resolve-CodexWorkerPaths -RepositoryRoot 'C:\repo' -DataRoot (Join-Path $TestDrive 'worker')
        $paths.RepositoryRoot | Should Be 'C:\repo'
        $paths.WorktreeRoot | Should Be 'C:\repo\.worktrees'
        $paths.StatePath | Should Be (Join-Path $TestDrive 'worker\state.json')
        $paths.RunRoot | Should Be (Join-Path $TestDrive 'worker\runs')
    }

    It 'rejects a relative repository root' {
        { Resolve-CodexWorkerPaths -RepositoryRoot '.\repo' -DataRoot (Join-Path $TestDrive 'worker') } |
            Should Throw 'RepositoryRoot must be absolute.'
    }

    It 'returns a schema-version-1 default for missing state' {
        $path = Join-Path $TestDrive 'missing-state.json'

        $state = Read-CodexWorkerState -Path $path

        $state.schemaVersion | Should Be 1
        $state.issues.Count | Should Be 0
        $state.deployment | Should Be $null
    }

    It 'round trips durable state atomically' {
        $path = Join-Path $TestDrive 'state.json'
        Write-CodexWorkerState -Path $path -State ([pscustomobject]@{
            schemaVersion = 1
            issues = @{}
            deployment = $null
        })

        (Read-CodexWorkerState -Path $path).schemaVersion | Should Be 1
        Test-Path "$path.tmp" | Should Be $false
    }

    It 'quarantines corrupt state without overwriting evidence' {
        $path = Join-Path $TestDrive 'state.json'
        [System.IO.File]::WriteAllText($path, '{not-json')

        $state = Read-CodexWorkerState -Path $path

        $state.schemaVersion | Should Be 1
        Test-Path $path | Should Be $false
        $quarantine = Get-ChildItem $TestDrive -Filter 'state.corrupt.*.json'
        $quarantine.Count | Should Be 1
        $quarantine[0].Name | Should Match '^state\.corrupt\.\d{8}T\d{6}Z(?:-\d+)?\.json$'
        (Get-Content -Raw $quarantine[0].FullName) | Should Be '{not-json'
    }

    It 'allows only one lock holder' {
        $lockPath = Join-Path $TestDrive 'worker.lock'
        $first = Enter-CodexWorkerLock -Path $lockPath -TimeoutSeconds 1
        try {
            { Enter-CodexWorkerLock -Path $lockPath -TimeoutSeconds 0 } | Should Throw 'Worker lock is busy.'
        } finally {
            Exit-CodexWorkerLock -Handle $first
        }
    }

    It 'exports the state and lock APIs' {
        $module = Get-Module CodexWorker
        foreach ($name in @(
                'Resolve-CodexWorkerPaths',
                'Read-CodexWorkerState',
                'Write-CodexWorkerState',
                'Enter-CodexWorkerLock',
                'Exit-CodexWorkerLock')) {
            $module.ExportedFunctions.ContainsKey($name) | Should Be $true
        }
    }
}
