$script:CodexStatusLabels = @(
    'codex:queued', 'codex:running', 'codex:pr-ready',
    'codex:blocked', 'codex:retry', 'codex:revise', 'codex:done'
)
$script:CodexMilestoneKeys = @{}

function Get-CodexWorkflowRunUrl {
    [CmdletBinding()]
    param(
        [string] $ServerUrl = $env:GITHUB_SERVER_URL,
        [string] $Repository = $env:GITHUB_REPOSITORY,
        [string] $RunId = $env:GITHUB_RUN_ID
    )

    if ([string]::IsNullOrWhiteSpace($ServerUrl) -or
        [string]::IsNullOrWhiteSpace($Repository) -or
        [string]::IsNullOrWhiteSpace($RunId)) {
        return $null
    }
    return ('{0}/{1}/actions/runs/{2}' -f $ServerUrl.TrimEnd('/'), $Repository.Trim('/'), $RunId)
}

function Add-CodexIssueMilestone {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $Repository,
        [Parameter(Mandatory = $true)] [int] $IssueNumber,
        [Parameter(Mandatory = $true)] [ValidateSet('claimed', 'approach', 'validation', 'blocked', 'pr-ready')] [string] $Milestone,
        [string] $Details,
        [scriptblock] $CommandRunner
    )

    $key = '{0}#{1}:{2}' -f $Repository, $IssueNumber, $Milestone
    if ($script:CodexMilestoneKeys.ContainsKey($key)) { return $null }

    $heading = switch ($Milestone) {
        'claimed' { 'Codex work claimed.' }
        'approach' { 'Codex approach established.' }
        'validation' { 'Codex validation result.' }
        'blocked' { 'Codex work is blocked.' }
        'pr-ready' { 'Codex implementation is ready for publication.' }
    }
    $parts = [System.Collections.Generic.List[string]]::new()
    $parts.Add($heading) | Out-Null
    if (-not [string]::IsNullOrWhiteSpace($Details)) { $parts.Add($Details.Trim()) | Out-Null }
    $workflowUrl = Get-CodexWorkflowRunUrl
    if (-not [string]::IsNullOrWhiteSpace($workflowUrl)) { $parts.Add("Workflow run: $workflowUrl") | Out-Null }
    $body = $parts -join "`n`n"
    $result = Add-CodexIssueComment -Repository $Repository -IssueNumber $IssueNumber -Body $body -CommandRunner $CommandRunner
    $script:CodexMilestoneKeys[$key] = $true
    return $result
}

function Invoke-GhCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments,

        [scriptblock] $CommandRunner
    )

    if ($null -ne $CommandRunner) {
        $raw = & $CommandRunner $Arguments
    } else {
        $raw = & gh.exe @Arguments 2>&1
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) {
            $message = ($raw | Out-String).Trim()
            if ([string]::IsNullOrWhiteSpace($message)) {
                $message = "gh.exe exited with code $exitCode."
            }

            throw $message
        }
    }

    if ($null -eq $raw) {
        return ''
    }

    if ($raw -is [string]) {
        return $raw
    }

    return (($raw | ForEach-Object { [string] $_ }) -join [Environment]::NewLine)
}

function Invoke-GhJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments,

        [scriptblock] $CommandRunner
    )

    $raw = Invoke-GhCommand -Arguments $Arguments -CommandRunner $CommandRunner
    $json = $raw.Trim()

    if ([string]::IsNullOrWhiteSpace($json)) {
        return $null
    }

    try {
        return $json | ConvertFrom-Json
    } catch {
        throw "gh.exe returned malformed JSON: $($_.Exception.Message)"
    }
}

function Assert-TrustedGitHubActor {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Repository,

        [Parameter(Mandatory = $true)]
        [string] $Actor,

        [scriptblock] $CommandRunner
    )

    $failure = "Actor $Actor does not have write permission."
    try {
        $permissionContext = Invoke-GhJson -Arguments @(
            'api',
            "repos/$Repository/collaborators/$Actor/permission"
        ) -CommandRunner $CommandRunner
    } catch {
        throw $failure
    }

    $permission = $null
    $propertyNames = @()
    if ($null -ne $permissionContext) {
        $propertyNames = @($permissionContext.PSObject.Properties | ForEach-Object { $_.Name })
    }

    if ($propertyNames -contains 'permission') {
        $permission = [string] $permissionContext.permission
    }

    if ($permission -notin @('admin', 'maintain', 'write')) {
        throw $failure
    }

    return $true
}

function Get-CodexIssueContext {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [Alias('Repo')]
        [string] $Repository,

        [Parameter(Mandatory = $true)]
        [Alias('Issue', 'Number')]
        [int] $IssueNumber,

        [scriptblock] $CommandRunner
    )

    return Invoke-GhJson -Arguments @(
        'issue', 'view', [string] $IssueNumber,
        '--repo', $Repository,
        '--comments',
        '--json', 'number,title,body,author,comments,labels,state,url'
    ) -CommandRunner $CommandRunner
}

function Get-CodexIssueDevelopment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [Alias('Repo')]
        [string] $Repository,

        [Parameter(Mandatory = $true)]
        [Alias('Issue', 'Number')]
        [int] $IssueNumber,

        [scriptblock] $CommandRunner
    )

    $developmentRaw = Invoke-GhCommand -Arguments @(
        'issue', 'develop', '--list', [string] $IssueNumber,
        '--repo', $Repository
    ) -CommandRunner $CommandRunner
    $pullRequests = Invoke-GhJson -Arguments @(
        'pr', 'list',
        '--repo', $Repository,
        '--state', 'open',
        '--json', 'number,title,body,author,comments,reviews,files,state,url,headRefName,baseRefName'
    ) -CommandRunner $CommandRunner

    $developmentLines = @()
    if (-not [string]::IsNullOrWhiteSpace($developmentRaw)) {
        $developmentLines = @(
            $developmentRaw -split "`r?`n" |
                ForEach-Object { $_.Trim() } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        )
    }

    return [pscustomobject][ordered]@{
        IssueNumber = $IssueNumber
        Development = [pscustomobject][ordered]@{
            Raw = $developmentRaw
            Lines = $developmentLines
        }
        PullRequests = @($pullRequests)
    }
}

function Get-CodexLabelName {
    param([object] $Label)

    if ($Label -is [string]) {
        return $Label
    }

    if ($null -ne $Label) {
        if ($Label -is [System.Collections.IDictionary] -and $Label.Contains('name')) {
            return [string] $Label['name']
        }

        $propertyNames = @($Label.PSObject.Properties | ForEach-Object { $_.Name })
        if ($propertyNames -contains 'name') {
            return [string] $Label.name
        }
    }

    return $null
}

function Set-CodexIssueStatus {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [Alias('Repo')]
        [string] $Repository,

        [Parameter(Mandatory = $true)]
        [Alias('Issue', 'Number')]
        [int] $IssueNumber,

        [Parameter(Mandatory = $true)]
        [string] $Status,

        [Alias('Labels')]
        [object[]] $CurrentLabels,

        [string] $Actor,

        [scriptblock] $CommandRunner
    )

    $normalizedStatus = if ($Status.StartsWith('codex:')) { $Status } else { "codex:$Status" }
    if ($normalizedStatus -notin $script:CodexStatusLabels) {
        throw "Unknown Codex issue status '$Status'."
    }

    if (-not [string]::IsNullOrWhiteSpace($Actor)) {
        Assert-TrustedGitHubActor -Repository $Repository -Actor $Actor -CommandRunner $CommandRunner |
            Out-Null
    }

    if ($null -eq $CurrentLabels) {
        $issue = Get-CodexIssueContext -Repository $Repository -IssueNumber $IssueNumber -CommandRunner $CommandRunner
        $CurrentLabels = @($issue.labels)
    }

    $arguments = [System.Collections.Generic.List[string]]::new()
    $arguments.Add('issue')
    $arguments.Add('edit')
    $arguments.Add([string] $IssueNumber)
    $arguments.Add('--repo')
    $arguments.Add($Repository)

    foreach ($label in @($CurrentLabels)) {
        $name = Get-CodexLabelName -Label $label
        if ($name -in $script:CodexStatusLabels -and $name -ne $normalizedStatus) {
            $arguments.Add('--remove-label')
            $arguments.Add($name)
        }
    }

    $arguments.Add('--add-label')
    $arguments.Add($normalizedStatus)

    return (Invoke-GhCommand -Arguments ([string[]] $arguments.ToArray()) -CommandRunner $CommandRunner).Trim()
}

function Add-CodexIssueComment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [Alias('Repo')]
        [string] $Repository,

        [Parameter(Mandatory = $true)]
        [Alias('Issue', 'Number')]
        [int] $IssueNumber,

        [Parameter(Mandatory = $true)]
        [string] $Body,

        [scriptblock] $CommandRunner
    )

    return (Invoke-GhCommand -Arguments @(
        'issue', 'comment', [string] $IssueNumber,
        '--repo', $Repository,
        '--body', $Body
    ) -CommandRunner $CommandRunner).Trim()
}

function Get-CodexPullRequestContext {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [Alias('Repo')]
        [string] $Repository,

        [Parameter(Mandatory = $true)]
        [Alias('PullRequest', 'Number')]
        [int] $PullRequestNumber,

        [scriptblock] $CommandRunner
    )

    return Invoke-GhJson -Arguments @(
        'pr', 'view', [string] $PullRequestNumber,
        '--repo', $Repository,
        '--comments',
        '--json', 'number,title,body,author,comments,reviews,files,state,url,headRefName,baseRefName'
    ) -CommandRunner $CommandRunner
}

function Get-CodexPullRequestForBranch {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Repository,
        [Parameter(Mandatory = $true)][string] $BranchName,
        [scriptblock] $CommandRunner
    )
    $items = @(Invoke-GhJson -Arguments @('pr', 'list', '--repo', $Repository, '--head', $BranchName, '--state', 'open', '--json', 'number,url,state,isDraft,headRefName,baseRefName,body') -CommandRunner $CommandRunner)
    if ($items.Count -eq 0) { return $null }
    return $items[0]
}

function New-CodexDraftPullRequest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Repository,
        [Parameter(Mandatory = $true)][string] $BaseBranch,
        [Parameter(Mandatory = $true)][string] $HeadBranch,
        [Parameter(Mandatory = $true)][string] $BodyPath,
        [scriptblock] $CommandRunner
    )
    $result = Invoke-GhCommand -Arguments @('pr', 'create', '--repo', $Repository, '--draft', '--base', $BaseBranch, '--head', $HeadBranch, '--body-file', ([IO.Path]::GetFullPath($BodyPath))) -CommandRunner $CommandRunner
    return $result.Trim()
}

function Set-CodexPullRequestBody {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Repository,
        [Parameter(Mandatory = $true)][int] $PullRequestNumber,
        [Parameter(Mandatory = $true)][string] $BodyPath,
        [scriptblock] $CommandRunner
    )
    return (Invoke-GhCommand -Arguments @('pr', 'edit', [string]$PullRequestNumber, '--repo', $Repository, '--body-file', ([IO.Path]::GetFullPath($BodyPath))) -CommandRunner $CommandRunner).Trim()
}

function Add-CodexPullRequestComment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Repository,
        [Parameter(Mandatory = $true)][int] $PullRequestNumber,
        [Parameter(Mandatory = $true)][string] $Body,
        [scriptblock] $CommandRunner
    )
    return (Invoke-GhCommand -Arguments @('pr', 'comment', [string]$PullRequestNumber, '--repo', $Repository, '--body', $Body) -CommandRunner $CommandRunner).Trim()
}
