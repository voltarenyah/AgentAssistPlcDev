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
}
