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
        $on | Should Match '(?ms)^    inputs:\s*\r?\n      issue_number:\s*$'
        $text | Should Match "if:\s*github\.event_name == 'workflow_dispatch'\s*\|\|\s*github\.event\.label\.name == 'codex'\s*\|\|\s*github\.event\.label\.name == 'codex:retry'"
        $text | Should Match '(?m)^\s*CODEX_ISSUE_NUMBER:\s*\$\{\{\s*github\.event\.issue\.number\s*\|\|\s*inputs\.issue_number\s*\}\}\s*$'
        $text | Should Match '(?m)^\s*\$issueNumber\s*=\s*\[int\]\$env:CODEX_ISSUE_NUMBER\s*$'
        $text | Should Match '(?ms)^\s*&\s*\.\\scripts\\codex-worker\\Invoke-Issue\.ps1\s*`?\s+-Repository\s+\(\[string\]\$env:CODEX_REPOSITORY\)\s*`?\s+-IssueNumber\s+\$issueNumber'
        $text | Should Not Match 'github\.event\.issue\.body|inputs\.body'
        Assert-TrustedCheckout $text
        Assert-RunnerAndQueueContract $text
        Assert-ExplicitPermissions $text
    }

    It 'routes revise labels from issues and pull requests to the revision worker' {
        $text = Get-WorkflowText 'codex-revise.yml'
        $on = Get-TopLevelSection -Text $text -Name 'on'
        $on | Should Match '(?ms)^  issues:\s*\r?\n    types:\s*\[labeled\]\s*$'
        $on | Should Match '(?ms)^  pull_request:\s*\r?\n    types:\s*\[labeled\]\s*$'
        $text | Should Match "if:\s*github\.event\.label\.name == 'codex:revise'"
        $text | Should Match '(?m)^\s*CODEX_PULL_REQUEST_NUMBER:\s*\$\{\{\s*github\.event\.pull_request\.number\s*\|\|\s*''''\s*\}\}\s*$'
        $text | Should Match '(?ms)^\s*&\s*\.\\scripts\\codex-worker\\Invoke-Revision\.ps1\s*`?\s+-Repository\s+\(\[string\]\$env:CODEX_REPOSITORY\)\s*`?\s+-IssueNumber\s+\$issueNumber'
        $text | Should Not Match 'github\.event\.(?:issue|pull_request)\.body|inputs\.body'
        Assert-TrustedCheckout $text
        Assert-RunnerAndQueueContract $text
        Assert-ExplicitPermissions $text
    }

    It 'passes typed close metadata and gates deployment handoff on merged state' {
        $text = Get-WorkflowText 'codex-pr-closed.yml'
        $on = Get-TopLevelSection -Text $text -Name 'on'
        $on | Should Match '(?ms)^  pull_request:\s*\r?\n    types:\s*\[closed\]\s*$'
        $text | Should Match '(?m)^\s*CODEX_MERGED:\s*\$\{\{\s*github\.event\.pull_request\.merged\s*\}\}\s*$'
        $text | Should Match '(?m)^\s*CODEX_MERGE_SHA:\s*\$\{\{\s*github\.event\.pull_request\.merge_commit_sha\s*\}\}\s*$'
        $text | Should Match '(?m)^\s*CODEX_HEAD_BRANCH:\s*\$\{\{\s*github\.event\.pull_request\.head\.ref\s*\}\}\s*$'
        $text | Should Match '(?m)^\s*CODEX_PR_NUMBER:\s*\$\{\{\s*github\.event\.pull_request\.number\s*\}\}\s*$'
        $text | Should Match '(?m)^\s*\[bool\]\$merged\s*=\s*\[System\.Convert\]::ToBoolean\(\$env:CODEX_MERGED\)\s*$'
        $text | Should Match '(?m)^\s*\$mergeSha\s*=\s*\[string\]\$env:CODEX_MERGE_SHA\s*$'
        $text | Should Match '(?m)^\s*\$headBranch\s*=\s*\[string\]\$env:CODEX_HEAD_BRANCH\s*$'
        $text | Should Match '(?m)^\s*\$pullRequestNumber\s*=\s*\[int\]\$env:CODEX_PR_NUMBER\s*$'
        $text | Should Match '(?ms)^\s*&\s*\.\\scripts\\codex-worker\\Register-PrClosed\.ps1\s*`?\s+-Repository\s+\(\[string\]\$env:CODEX_REPOSITORY\)'
        $text | Should Match '(?ms)if\s*\(\$merged\)\s*\{.*(?:handoff|deployment).*\}'
        $text | Should Match '(?i)github\.event\.pull_request\.merged'
        $text | Should Not Match 'github\.event\.pull_request\.body'
        Assert-TrustedCheckout $text
        Assert-RunnerAndQueueContract $text
        Assert-ExplicitPermissions $text
    }
}
