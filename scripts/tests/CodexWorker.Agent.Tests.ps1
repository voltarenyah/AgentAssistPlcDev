Describe 'worker provider selection' {
    BeforeEach { Import-Module (Join-Path $PSScriptRoot '..\codex-worker\CodexWorker.psd1') -Force }

    It 'maps the explicit kimi trigger to Kimi lifecycle labels' {
        $provider = Resolve-CodexWorkerProvider -Provider 'Kimi' -EventName 'kimi'
        $provider.Name | Should Be 'Kimi'
        $provider.TriggerLabel | Should Be 'kimi'
        $provider.StatusLabels.'pr-ready' | Should Be 'kimi-ready'
    }

    It 'refuses an implicit Kimi selection' {
        $threw = $false
        try { Resolve-CodexWorkerProvider -Provider '' -EventName 'kimi' } catch { $threw = $true }
        $threw | Should Be $true
    }

    It 'refuses a mismatched Kimi event' {
        $threw = $false
        try { Resolve-CodexWorkerProvider -Provider 'Kimi' -EventName 'codex:retry' } catch { $threw = $true }
        $threw | Should Be $true
    }

    It 'keeps the explicit Codex lifecycle labels' {
        $provider = Resolve-CodexWorkerProvider -Provider 'Codex' -EventName 'codex'
        $provider.Name | Should Be 'Codex'
        $provider.TriggerLabel | Should Be 'codex'
        $provider.RevisionLabel | Should Be 'codex:revise'
        $provider.StatusLabels.'pr-ready' | Should Be 'codex:pr-ready'
    }

    It 'implicitly resolves Codex events to the Codex provider' {
        $provider = Resolve-CodexWorkerProvider -Provider '' -EventName 'codex:retry'
        $provider.Name | Should Be 'Codex'
        $provider.StatusLabels.retry | Should Be 'codex:retry'
    }
}
