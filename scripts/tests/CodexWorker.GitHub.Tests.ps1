Describe 'Codex worker GitHub adapter' {
    BeforeEach {
        Import-Module (Join-Path $PSScriptRoot '..\codex-worker\CodexWorker.psd1') -Force
    }

    It 'exports the trusted GitHub lifecycle APIs but keeps the gh boundary internal' {
        $module = Get-Module CodexWorker
        foreach ($name in @(
                'Assert-TrustedGitHubActor',
                'Get-CodexIssueContext',
                'Get-CodexIssueDevelopment',
                'Set-CodexIssueStatus',
                'Add-CodexIssueComment',
                'Get-CodexPullRequestContext',
                'Resolve-CodexPullRequestIssueNumber',
                'Resolve-CodexRevisionIssueNumber')) {
            $module.ExportedFunctions.ContainsKey($name) | Should Be $true
        }

        $module.ExportedFunctions.ContainsKey('Invoke-GhJson') | Should Be $false
    }

    It 'accepts only actors with write-capable repository permissions' {
        foreach ($permission in @('admin', 'maintain', 'write')) {
            $runner = { param($Arguments) @{ permission = $permission } | ConvertTo-Json }

            Assert-TrustedGitHubActor -Repository 'owner/repo' -Actor 'trusted-user' -CommandRunner $runner |
                Should Be $true
        }
    }

    It 'rejects a triage-only trigger actor' {
        $runner = { param($Arguments) '{"permission":"triage"}' }

        { Assert-TrustedGitHubActor -Repository 'owner/repo' -Actor 'reporter' -CommandRunner $runner } |
            Should Throw 'Actor reporter does not have write permission.'
    }

    It 'rejects read, missing, and malformed permission responses' {
        foreach ($response in @(
                '{"permission":"read"}',
                '{}',
                '{"permission":null}',
                'not-json')) {
            $runner = { param($Arguments) $response }

            { Assert-TrustedGitHubActor -Repository 'owner/repo' -Actor 'reporter' -CommandRunner $runner } |
                Should Throw 'Actor reporter does not have write permission.'
        }
    }

    It 'does not attempt a mutation when trust validation fails' {
        $calls = New-Object 'System.Collections.Generic.List[object]'
        $runner = {
            param($Arguments)
            $calls.Add(@($Arguments))
            if ($Arguments -contains 'permission') {
                return '{"permission":"read"}'
            }

            throw 'Unexpected mutation command.'
        }

        { Assert-TrustedGitHubActor -Repository 'owner/repo' -Actor 'reporter' -CommandRunner $runner } |
            Should Throw 'Actor reporter does not have write permission.'
        $calls.Count | Should Be 1
    }

    It 'does not change lifecycle labels before a supplied actor passes trust validation' {
        $calls = New-Object 'System.Collections.Generic.List[object]'
        $runner = {
            param($Arguments)
            $calls.Add(@($Arguments))
            if ($Arguments -contains 'permission') {
                return '{"permission":"triage"}'
            }

            throw 'Unexpected lifecycle mutation.'
        }

        {
            Set-CodexIssueStatus -Repository 'owner/repo' -IssueNumber 42 -Status 'running' -Actor 'reporter' `
                -CurrentLabels @('codex', 'codex:queued') -CommandRunner $runner
        } | Should Throw 'Actor reporter does not have write permission.'
        $calls.Count | Should Be 1
    }

    It 'gets the complete issue context through separate gh arguments' {
        $payload = [ordered]@{
            number = 42
            title = 'Fix the station'
            body = 'Issue body is data, not a command.'
            author = @{ login = 'reporter' }
            comments = @(@{ author = @{ login = 'maintainer' }; body = 'Reviewed.' })
            labels = @(@{ name = 'codex' }, @{ name = 'customer-impact' })
            state = 'OPEN'
            url = 'https://github.com/owner/repo/issues/42'
        }
        $calls = New-Object 'System.Collections.Generic.List[object]'
        $runner = {
            param($Arguments)
            $calls.Add(@($Arguments))
            $payload | ConvertTo-Json -Depth 10
        }

        $context = Get-CodexIssueContext -Repository 'owner/repo' -IssueNumber 42 -CommandRunner $runner

        $context.title | Should Be 'Fix the station'
        $context.comments.Count | Should Be 1
        $calls.Count | Should Be 1
        (@($calls[0]) -contains 'issue') | Should Be $true
        (@($calls[0]) -contains 'view') | Should Be $true
        (@($calls[0]) -contains '42') | Should Be $true
        (@($calls[0]) -contains '--comments') | Should Be $true
        (@($calls[0]) -contains 'number,title,body,author,comments,labels,state,url') | Should Be $true
        (@($calls[0]) -contains 'Issue body is data, not a command.') | Should Be $false
    }

    It 'gets issue development and open pull requests' {
        $calls = New-Object 'System.Collections.Generic.List[object]'
        $runner = {
            param($Arguments)
            $calls.Add(@($Arguments))
            if (@($Arguments) -contains 'develop') {
                return "BRANCH`tTITLE`tSTATUS`r`ncodex/42-fix-the-station`tFix the station`tOPEN"
            }

            return '[{"number":101,"title":"Fix the station","state":"OPEN","url":"https://github.com/owner/repo/pull/101"}]'
        }

        $development = Get-CodexIssueDevelopment -Repository 'owner/repo' -IssueNumber 42 -CommandRunner $runner

        $development.Development.Lines.Count | Should Be 2
        $development.Development.Lines[1] | Should Be "codex/42-fix-the-station`tFix the station`tOPEN"
        $development.Development.Raw | Should Match 'BRANCH'
        $development.PullRequests.Count | Should Be 1
        $calls.Count | Should Be 2
        (@($calls[0]) -contains 'issue') | Should Be $true
        (@($calls[0]) -contains 'develop') | Should Be $true
        (@($calls[0]) -contains '--list') | Should Be $true
        (@($calls[0]) -contains '42') | Should Be $true
        (@($calls[1]) -contains 'pr') | Should Be $true
        (@($calls[1]) -contains 'list') | Should Be $true
        (@($calls[1]) -contains '--state') | Should Be $true
        (@($calls[1]) -contains 'open') | Should Be $true
    }

    It 'changes only Codex lifecycle labels and preserves codex and unrelated labels' {
        $calls = New-Object 'System.Collections.Generic.List[object]'
        $runner = {
            param($Arguments)
            $calls.Add(@($Arguments))
            'https://github.com/owner/repo/issues/42'
        }
        $labels = @('codex', 'codex:queued', 'codex:retry', 'customer-impact')

        $result = Set-CodexIssueStatus -Repository 'owner/repo' -IssueNumber 42 -Status 'running' -CurrentLabels $labels -CommandRunner $runner
        $result | Should Be 'https://github.com/owner/repo/issues/42'

        $arguments = @($calls[0])
        ($arguments -contains '--remove-label') | Should Be $true
        ($arguments -contains 'codex:queued') | Should Be $true
        ($arguments -contains 'codex:retry') | Should Be $true
        ($arguments -contains 'codex') | Should Be $false
        ($arguments -contains 'customer-impact') | Should Be $false
        ($arguments -contains '--add-label') | Should Be $true
        ($arguments -contains 'codex:running') | Should Be $true
    }

    It 'adds an issue comment without interpolating the body into a command' {
        $calls = New-Object 'System.Collections.Generic.List[object]'
        $runner = {
            param($Arguments)
            $calls.Add(@($Arguments))
            'https://github.com/owner/repo/issues/42#issuecomment-123'
        }
        $body = 'Status: blocked; $(Remove-Item -Recurse)'

        $result = Add-CodexIssueComment -Repository 'owner/repo' -IssueNumber 42 -Body $body -CommandRunner $runner
        $result | Should Be 'https://github.com/owner/repo/issues/42#issuecomment-123'

        $arguments = @($calls[0])
        ($arguments -contains '--body') | Should Be $true
        $arguments[$arguments.IndexOf('--body') + 1] | Should Be $body
    }

    It 'gets pull request context including reviews and changed files' {
        $payload = [ordered]@{
            number = 101
            title = 'Fix the station'
            body = 'PR body'
            author = @{ login = 'trusted-user' }
            comments = @(@{ author = @{ login = 'reviewer' }; body = 'Please revise.' })
            reviews = @(@{ author = @{ login = 'reviewer' }; state = 'CHANGES_REQUESTED'; body = 'Please revise.' })
            files = @(@{ path = 'src/Station.cs'; additions = 2; deletions = 1 })
            state = 'OPEN'
            url = 'https://github.com/owner/repo/pull/101'
            headRefName = 'codex/101-fix'
            baseRefName = 'master'
            headRepository = @{ nameWithOwner = 'owner/repo' }
            mergedAt = $null
            mergeCommit = $null
            closingIssuesReferences = @()
        }
        $calls = New-Object 'System.Collections.Generic.List[object]'
        $runner = {
            param($Arguments)
            $calls.Add(@($Arguments))
            $payload | ConvertTo-Json -Depth 10
        }

        $context = Get-CodexPullRequestContext -Repository 'owner/repo' -PullRequestNumber 101 -CommandRunner $runner

        $context.reviews.Count | Should Be 1
        $context.files.Count | Should Be 1
        $context.headRepository.nameWithOwner | Should Be 'owner/repo'
        $context.baseRepository.nameWithOwner | Should Be 'owner/repo'
        $context.baseRefName | Should Be 'master'
        $context.mergedAt | Should BeNullOrEmpty
        $calls.Count | Should Be 1
        (@($calls[0]) -contains 'pr') | Should Be $true
        (@($calls[0]) -contains 'view') | Should Be $true
        (@($calls[0]) -contains '101') | Should Be $true
        $jsonFields = $calls[0][($calls[0].IndexOf('--json') + 1)]
        $jsonFields | Should Match 'reviews'
        $jsonFields | Should Match 'files'
        $jsonFields | Should Not Match 'baseRepository'
    }

    It 'keeps an issue-labeled revision on its supplied issue without querying a PR' {
        $calls = New-Object 'System.Collections.Generic.List[object]'
        $runner = { param($Arguments) $calls.Add(@($Arguments)) | Out-Null; throw 'PR lookup must not run for an issue event.' }.GetNewClosure()

        (Resolve-CodexRevisionIssueNumber -Repository 'owner/repo' -IssueNumber 42 -PullRequestNumber '' -CommandRunner $runner) | Should Be 42
        $calls.Count | Should Be 0
    }

    It 'resolves one same-repository closing issue for a PR revision' {
        $calls = New-Object 'System.Collections.Generic.List[object]'
        $payload = [ordered]@{
            number = 101
            closingIssuesReferences = @([ordered]@{ number = 42; repository = [ordered]@{ name = 'repo'; owner = [ordered]@{ login = 'owner' } } })
        }
        $runner = { param($Arguments) $calls.Add(@($Arguments)) | Out-Null; $payload | ConvertTo-Json -Depth 10 }.GetNewClosure()

        (Resolve-CodexRevisionIssueNumber -Repository 'owner/repo' -IssueNumber 0 -PullRequestNumber 101 -CommandRunner $runner) | Should Be 42
        $calls.Count | Should Be 1
        (@($calls[0]) -join ' ') | Should Match '(?i)pr view 101 --repo owner/repo'
        (@($calls[0]) -join ' ') | Should Match 'closingIssuesReferences'
    }

    It 'fails closed for zero, multiple, and cross-repository closing issue references' {
        foreach ($references in @(
                @(),
                @([ordered]@{ number = 42; repository = [ordered]@{ nameWithOwner = 'owner/repo' } }, [ordered]@{ number = 43; repository = [ordered]@{ nameWithOwner = 'owner/repo' } }),
                @([ordered]@{ number = 42; repository = [ordered]@{ nameWithOwner = 'other/repo' } }))) {
            $payload = [ordered]@{ number = 101; closingIssuesReferences = $references }
            $runner = { param($Arguments) $payload | ConvertTo-Json -Depth 10 }.GetNewClosure()
            $threw = $false; try { Resolve-CodexRevisionIssueNumber -Repository 'owner/repo' -IssueNumber 0 -PullRequestNumber 101 -CommandRunner $runner } catch { $threw = $true }
            $threw | Should Be $true
        }
    }

    It 'rejects a supplied issue that does not match the PR closing reference' {
        $payload = [ordered]@{
            number = 101
            closingIssuesReferences = @([ordered]@{ number = 42; repository = [ordered]@{ nameWithOwner = 'owner/repo' } })
        }
        $runner = { param($Arguments) $payload | ConvertTo-Json -Depth 10 }.GetNewClosure()

        $threw = $false; try { Resolve-CodexRevisionIssueNumber -Repository 'owner/repo' -IssueNumber 99 -PullRequestNumber 101 -CommandRunner $runner } catch { $threw = $true }
        $threw | Should Be $true
    }

    It 'builds a workflow run URL only when all workflow coordinates are present' {
        $oldServer = $env:GITHUB_SERVER_URL; $oldRepo = $env:GITHUB_REPOSITORY; $oldRun = $env:GITHUB_RUN_ID
        try {
            $env:GITHUB_SERVER_URL = 'https://github.example.test/'
            $env:GITHUB_REPOSITORY = 'owner/repo'
            $env:GITHUB_RUN_ID = '1234'
            (Get-CodexWorkflowRunUrl) | Should Be 'https://github.example.test/owner/repo/actions/runs/1234'
            Remove-Item Env:GITHUB_RUN_ID
            (Get-CodexWorkflowRunUrl) | Should Be $null
        } finally {
            if ($null -eq $oldServer) { Remove-Item Env:GITHUB_SERVER_URL -ErrorAction SilentlyContinue } else { $env:GITHUB_SERVER_URL = $oldServer }
            if ($null -eq $oldRepo) { Remove-Item Env:GITHUB_REPOSITORY -ErrorAction SilentlyContinue } else { $env:GITHUB_REPOSITORY = $oldRepo }
            if ($null -eq $oldRun) { Remove-Item Env:GITHUB_RUN_ID -ErrorAction SilentlyContinue } else { $env:GITHUB_RUN_ID = $oldRun }
        }
    }

    It 'does not flood bounded milestone comments' {
        $calls = New-Object 'System.Collections.Generic.List[object]'
        $runner = { param([string[]] $Arguments) $calls.Add(@($Arguments)) | Out-Null; '' }.GetNewClosure()
        Add-CodexIssueMilestone -Repository 'owner/repo' -IssueNumber 42 -Milestone 'claimed' -Details 'branch codex/42-fix' -CommandRunner $runner | Out-Null
        Add-CodexIssueMilestone -Repository 'owner/repo' -IssueNumber 42 -Milestone 'claimed' -Details 'branch codex/42-fix' -CommandRunner $runner | Out-Null
        $calls.Count | Should Be 1
    }

    It 'includes the Actions workflow URL in the posted milestone body' {
        $oldServer = $env:GITHUB_SERVER_URL; $oldRepo = $env:GITHUB_REPOSITORY; $oldRun = $env:GITHUB_RUN_ID
        $body = New-Object 'System.Collections.Generic.List[string]'
        try {
            $env:GITHUB_SERVER_URL = 'https://github.example.test'
            $env:GITHUB_REPOSITORY = 'owner/repo'
            $env:GITHUB_RUN_ID = '777'
            $runner = { param([string[]] $Arguments) $body.Add($Arguments[$Arguments.IndexOf('--body') + 1]) | Out-Null; '' }.GetNewClosure()
            Add-CodexIssueMilestone -Repository 'owner/repo' -IssueNumber 777 -Milestone 'validation' -Details 'test passed' -CommandRunner $runner | Out-Null
            $body.Count | Should Be 1
            $body[0] | Should Match 'https://github.example.test/owner/repo/actions/runs/777'
        } finally {
            if ($null -eq $oldServer) { Remove-Item Env:GITHUB_SERVER_URL -ErrorAction SilentlyContinue } else { $env:GITHUB_SERVER_URL = $oldServer }
            if ($null -eq $oldRepo) { Remove-Item Env:GITHUB_REPOSITORY -ErrorAction SilentlyContinue } else { $env:GITHUB_REPOSITORY = $oldRepo }
            if ($null -eq $oldRun) { Remove-Item Env:GITHUB_RUN_ID -ErrorAction SilentlyContinue } else { $env:GITHUB_RUN_ID = $oldRun }
        }
    }
}
