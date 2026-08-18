Describe 'Codex worker GitHub Actions workflows' {
    $workflowRoot = Join-Path $PSScriptRoot '..\..\.github\workflows'

    function Get-WorkflowText {
        param([string]$Name)

        $path = Join-Path $workflowRoot $Name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Expected workflow '$Name' was not found."
        }
        return [IO.File]::ReadAllText($path)
    }

    function Get-TopLevelSection {
        param([string]$Text, [string]$Name)

        $lines = $Text -split "`r?`n"
        $start = -1
        for ($index = 0; $index -lt $lines.Count; $index++) {
            if ($lines[$index] -match ('^' + [regex]::Escape($Name) + ':\s*(?:#.*)?$')) {
                $start = $index + 1
                break
            }
        }
        if ($start -lt 0) { return $null }

        $end = $lines.Count
        for ($index = $start; $index -lt $lines.Count; $index++) {
            if ($lines[$index] -match '^[A-Za-z0-9_-]+:\s*') {
                $end = $index
                break
            }
        }
        return ($lines[$start..($end - 1)] -join "`n")
    }

    function Assert-TrustedCheckout {
        param([string]$Text)

        $lines = $Text -split "`r?`n"
        $checkoutStart = -1
        $stepIndent = $null
        for ($index = 0; $index -lt $lines.Count; $index++) {
            $match = [regex]::Match($lines[$index], '^(?<indent>\s*)-\s+name:\s*Checkout trusted worker revision\s*$')
            if ($match.Success) {
                $checkoutStart = $index
                $stepIndent = $match.Groups['indent'].Value
                break
            }
        }
        $checkoutStart | Should Not Be -1
        $checkoutEnd = $lines.Count
        for ($index = $checkoutStart + 1; $index -lt $lines.Count; $index++) {
            if ($lines[$index] -match ('^' + [regex]::Escape($stepIndent) + '-\s+name:')) {
                $checkoutEnd = $index
                break
            }
        }
        $checkout = $lines[$checkoutStart..($checkoutEnd - 1)] -join "`n"
        $checkout | Should Match '(?m)^\s*uses:\s*actions/checkout@v4\s*$'
        $checkout | Should Match '(?m)^\s*ref:\s*refs/heads/\$\{\{\s*github\.event\.repository\.default_branch\s*\}\}\s*$'
        $checkout | Should Match '(?m)^\s*persist-credentials:\s*false\s*$'
    }

    function Get-StepSection {
        param([string]$Text, [string]$Name)

        $lines = $Text -split "`r?`n"
        $start = -1
        $stepIndent = $null
        for ($index = 0; $index -lt $lines.Count; $index++) {
            $match = [regex]::Match($lines[$index], ('^(?<indent>\s*)-\s+name:\s*' + [regex]::Escape($Name) + '\s*$'))
            if ($match.Success) {
                $start = $index
                $stepIndent = $match.Groups['indent'].Value
                break
            }
        }
        $start | Should Not Be -1 | Out-Null
        $end = $lines.Count
        for ($index = $start + 1; $index -lt $lines.Count; $index++) {
            if ($lines[$index] -match ('^' + [regex]::Escape($stepIndent) + '-\s+name:')) {
                $end = $index
                break
            }
        }
        return ($lines[$start..($end - 1)] -join "`n")
    }

    function Assert-RunnerAndQueueContract {
        param([string]$Text)

        ([regex]::Matches($Text, '(?m)^\s*runs-on:\s*\[self-hosted, Windows, X64, agentassist-local\]\s*$')).Count | Should Be 1
        $Text | Should Not Match '(?m)^concurrency\s*:'
    }

    function Assert-ExplicitPermissions {
        param([string]$Text)

        $permissions = Get-TopLevelSection -Text $Text -Name 'permissions'
        $permissions | Should Not BeNullOrEmpty
        $permissions | Should Match '(?m)^  contents:\s*write\s*$'
        $permissions | Should Match '(?m)^  issues:\s*write\s*$'
        $permissions | Should Match '(?m)^  pull-requests:\s*write\s*$'
        $permissions | Should Not Match '(?m)^  (?:actions|administration|deployments|packages|statuses):'
    }

    It 'has exactly the three trusted workflows and no replacement-prone concurrency group' {
        $workflowNames = @('codex-issue.yml', 'codex-revise.yml', 'codex-pr-closed.yml')
        (Get-ChildItem -LiteralPath $workflowRoot -Filter 'codex-*.yml' -File | Select-Object -ExpandProperty Name) |
            Should Be $workflowNames

        $allText = ($workflowNames | ForEach-Object { Get-WorkflowText $_ }) -join "`n"
        ([regex]::Matches($allText, '(?m)^\s*runs-on:\s*\[self-hosted, Windows, X64, agentassist-local\]\s*$')).Count | Should Be 3
        $allText | Should Not Match '(?m)^concurrency\s*:'
    }

    It 'routes codex and retry labels through a trusted issue worker with typed environment inputs' {
        $text = Get-WorkflowText 'codex-issue.yml'
        $on = Get-TopLevelSection -Text $text -Name 'on'
        $on | Should Match '(?ms)^  issues:\s*\r?\n    types:\s*\[labeled\]\s*$'
        $on | Should Match '(?ms)^  workflow_dispatch:\s*$'
        $on | Should Match '(?ms)^    inputs:\s*\r?\n      issue_number:\s*\r?\n        required:\s*true\s*\r?\n        type:\s*number\s*\r?\n      dry_run:\s*\r?\n        required:\s*false\s*\r?\n        type:\s*boolean\s*\r?\n        default:\s*false\s*$'
        $jobs = Get-TopLevelSection -Text $text -Name 'jobs'
        $jobs | Should Match "(?m)^    if:\s*github\.event_name == 'workflow_dispatch' \|\| github\.event\.label\.name == 'codex' \|\| github\.event\.label\.name == 'codex:retry'\s*$"
        $dispatch = Get-StepSection -Text $text -Name 'Dispatch issue to local worker'
        $dispatch | Should Match '(?m)^          CODEX_ISSUE_NUMBER:\s*\$\{\{\s*github\.event\.issue\.number\s*\|\|\s*inputs\.issue_number\s*\}\}\s*$'
        $dispatch | Should Match '(?m)^          \$issueNumber\s*=\s*\[int\]\$env:CODEX_ISSUE_NUMBER\s*$'
        $dispatch | Should Match '(?m)^          CODEX_EVENT_NAME:\s*\$\{\{\s*github\.event\.label\.name\s*\|\|\s*github\.event_name\s*\}\}\s*$'
        $dispatch | Should Match '(?ms)^          &\s+\.\\scripts\\codex-worker\\Invoke-Issue\.ps1\s*`?\s+-Repository\s+\(\[string\]\$env:CODEX_REPOSITORY\)\s*`?\s+-IssueNumber\s+\$issueNumber\s*`?\s+-Actor\s+\(\[string\]\$env:CODEX_ACTOR\)\s*`?\s+-EventName\s+\(\[string\]\$env:CODEX_EVENT_NAME\)\s*`?\s+-DryRun:\$dryRun'
        $dispatch | Should Not Match 'github\.event\.issue\.body|inputs\.body'
        Assert-TrustedCheckout $text
        Assert-RunnerAndQueueContract $text
        Assert-ExplicitPermissions $text
    }

    It 'routes revise labels from issues and pull requests to the revision worker' {
        $text = Get-WorkflowText 'codex-revise.yml'
        $on = Get-TopLevelSection -Text $text -Name 'on'
        $on | Should Match '(?ms)^  issues:\s*\r?\n    types:\s*\[labeled\]\s*$'
        $on | Should Match '(?ms)^  pull_request:\s*\r?\n    types:\s*\[labeled\]\s*$'
        $jobs = Get-TopLevelSection -Text $text -Name 'jobs'
        $jobs | Should Match "(?m)^    if:\s*github\.event\.label\.name == 'codex:revise'\s*$"
        $issueDispatch = Get-StepSection -Text $text -Name 'Dispatch issue revision'
        $issueDispatch | Should Match '(?m)^        if:\s*github\.event_name == ''issues'' && github\.event\.label\.name == ''codex:revise''\s*$'
        $issueDispatch | Should Match '(?m)^          CODEX_ISSUE_NUMBER:\s*\$\{\{\s*github\.event\.issue\.number\s*\}\}\s*$'
        $issueDispatch | Should Match '(?m)^          \$issueNumber\s*=\s*\[int\]\$env:CODEX_ISSUE_NUMBER\s*$'
        $issueDispatch | Should Match '(?ms)^          &\s+\.\\scripts\\codex-worker\\Invoke-Revision\.ps1\s*`?\s+-Repository\s+\(\[string\]\$env:CODEX_REPOSITORY\)\s*`?\s+-IssueNumber\s+\$issueNumber\s*`?\s+-Actor\s+\(\[string\]\$env:CODEX_ACTOR\)\s*`?\s+-EventName\s+\(\[string\]\$env:CODEX_EVENT_NAME\)'
        $issueDispatch | Should Not Match 'PullRequestNumber'
        $prDispatch = Get-StepSection -Text $text -Name 'Dispatch pull request revision'
        $prDispatch | Should Match '(?m)^        if:\s*github\.event_name == ''pull_request'' && github\.event\.label\.name == ''codex:revise''\s*$'
        $prDispatch | Should Match '(?m)^          CODEX_PULL_REQUEST_NUMBER:\s*\$\{\{\s*github\.event\.pull_request\.number\s*\}\}\s*$'
        $prDispatch | Should Match '(?m)^          \$pullRequestNumber\s*=\s*\[int\]\$env:CODEX_PULL_REQUEST_NUMBER\s*$'
        $prDispatch | Should Match '(?ms)^          &\s+\.\\scripts\\codex-worker\\Invoke-Revision\.ps1\s*`?\s+-Repository\s+\(\[string\]\$env:CODEX_REPOSITORY\)\s*`?\s+-PullRequestNumber\s+\$pullRequestNumber\s*`?\s+-Actor\s+\(\[string\]\$env:CODEX_ACTOR\)\s*`?\s+-EventName\s+\(\[string\]\$env:CODEX_EVENT_NAME\)'
        $prDispatch | Should Not Match 'IssueNumber|github\.event\.issue\.number|github\.event\.(?:issue|pull_request)\.body|inputs\.body'
        Assert-TrustedCheckout $text
        Assert-RunnerAndQueueContract $text
        Assert-ExplicitPermissions $text
    }

    It 'passes typed close metadata and gates deployment handoff on merged state' {
        $text = Get-WorkflowText 'codex-pr-closed.yml'
        $on = Get-TopLevelSection -Text $text -Name 'on'
        $on | Should Match '(?ms)^  pull_request:\s*\r?\n    types:\s*\[closed\]\s*$'
        $text | Should Match '(?m)^\s*CODEX_MERGED:\s*\$\{\{\s*github\.event\.pull_request\.merged\s*\}\}\s*$'
        $text | Should Match '(?m)^\s*CODEX_MERGE_COMMIT_SHA:\s*\$\{\{\s*github\.event\.pull_request\.merge_commit_sha\s*\}\}\s*$'
        $text | Should Match '(?m)^\s*CODEX_HEAD_BRANCH:\s*\$\{\{\s*github\.event\.pull_request\.head\.ref\s*\}\}\s*$'
        $text | Should Match '(?m)^\s*CODEX_PR_NUMBER:\s*\$\{\{\s*github\.event\.pull_request\.number\s*\}\}\s*$'
        $text | Should Not Match '(?m)^\s*CODEX_ISSUE_NUMBER:'
        $text | Should Match '(?m)^\s*\[bool\]\$merged\s*=\s*\[System\.Convert\]::ToBoolean\(\$env:CODEX_MERGED\)\s*$'
        $text | Should Match '(?m)^\s*\$mergeCommitSha\s*=\s*\[string\]\$env:CODEX_MERGE_COMMIT_SHA\s*$'
        $text | Should Match '(?m)^\s*\$headBranch\s*=\s*\[string\]\$env:CODEX_HEAD_BRANCH\s*$'
        $text | Should Match '(?m)^\s*\$pullRequestNumber\s*=\s*\[int\]\$env:CODEX_PR_NUMBER\s*$'
        $handoff = Get-StepSection -Text $text -Name 'Register merged pull request handoff'
        $handoff | Should Not Match '(?m)^        if:\s*github\.event\.pull_request\.merged == true\s*$'
        $handoff | Should Match '(?ms)^          &\s+\.\\scripts\\codex-worker\\Register-PrClosed\.ps1\s*`?\s+-Repository\s+\(\[string\]\$env:CODEX_REPOSITORY\)\s*`?\s+-PullRequestNumber\s+\$pullRequestNumber\s*`?\s+-Merged\s+\$merged\s*`?\s+-MergeCommitSha\s+\$mergeCommitSha\s*`?\s+-HeadBranch\s+\$headBranch'
        $handoff | Should Not Match 'IssueNumber|github\.event\.pull_request\.issue\.number|github\.event\.pull_request\.body'
        Assert-TrustedCheckout $text
        Assert-RunnerAndQueueContract $text
        Assert-ExplicitPermissions $text
        $checkout = Get-StepSection -Text $text -Name 'Checkout trusted worker revision'
        $checkout | Should Match '(?m)^          fetch-depth:\s*0\s*$'
        $handler = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\codex-worker\Register-PrClosed.ps1'))
        $handler | Should Match '(?m)Register-CodexPullRequestClosed'
        $deployment = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\codex-worker\Private\Deployment.ps1'))
        $deployment | Should Match '(?m)if \(\$Merged\)'
    }
}
