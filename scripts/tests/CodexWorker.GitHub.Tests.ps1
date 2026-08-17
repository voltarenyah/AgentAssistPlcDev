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
                'Get-CodexPullRequestContext')) {
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
                return '[{"number":42,"branch":"codex/42-fix-the-station"}]'
            }

            return '[{"number":101,"title":"Fix the station","state":"OPEN","url":"https://github.com/owner/repo/pull/101"}]'
        }

        $development = Get-CodexIssueDevelopment -Repository 'owner/repo' -IssueNumber 42 -CommandRunner $runner

        $development.Development.Count | Should Be 1
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
            '{}'
        }
        $labels = @('codex', 'codex:queued', 'codex:retry', 'customer-impact')

        Set-CodexIssueStatus -Repository 'owner/repo' -IssueNumber 42 -Status 'running' -CurrentLabels $labels -CommandRunner $runner |
            Out-Null

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
            '{}'
        }
        $body = 'Status: blocked; $(Remove-Item -Recurse)'

        Add-CodexIssueComment -Repository 'owner/repo' -IssueNumber 42 -Body $body -CommandRunner $runner |
            Out-Null

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
        $calls.Count | Should Be 1
        (@($calls[0]) -contains 'pr') | Should Be $true
        (@($calls[0]) -contains 'view') | Should Be $true
        (@($calls[0]) -contains '101') | Should Be $true
        $jsonFields = $calls[0][($calls[0].IndexOf('--json') + 1)]
        $jsonFields | Should Match 'reviews'
        $jsonFields | Should Match 'files'
    }
}
