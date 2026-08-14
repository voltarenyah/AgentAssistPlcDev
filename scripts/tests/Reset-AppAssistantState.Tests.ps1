Describe 'Reset-AppAssistantState' {
    BeforeEach {
        $testDataDirectory = Join-Path $TestDrive 'assistant-data'
        New-Item -ItemType Directory -Force -Path $testDataDirectory | Out-Null
        foreach ($fileName in @(
            'checkpoints.sqlite',
            'checkpoints.sqlite-shm',
            'checkpoints.sqlite-wal',
            'assistant-events.jsonl'
        )) {
            Set-Content -LiteralPath (Join-Path $testDataDirectory $fileName) -Value $fileName
        }
    }

    It 'clears checkpoint state while retaining the diagnostic event log' {
        & (Join-Path $PSScriptRoot '..\Reset-AppAssistantState.ps1') -DataDirectory $testDataDirectory

        foreach ($fileName in @(
            'checkpoints.sqlite',
            'checkpoints.sqlite-shm',
            'checkpoints.sqlite-wal'
        )) {
            Test-Path -LiteralPath (Join-Path $testDataDirectory $fileName) | Should Be $false
        }
        Test-Path -LiteralPath (Join-Path $testDataDirectory 'assistant-events.jsonl') | Should Be $true
    }
}
