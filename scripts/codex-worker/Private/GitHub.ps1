$script:CodexStatusLabels = @(
    'codex:queued', 'codex:running', 'codex:pr-ready',
    'codex:blocked', 'codex:retry', 'codex:revise', 'codex:done'
)

function Invoke-GhJson {
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
        return $null
    }

    if ($raw -is [string]) {
        $json = $raw.Trim()
    } else {
        $json = ($raw | Out-String).Trim()
    }

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

    $development = Invoke-GhJson -Arguments @(
        'issue', 'develop', '--list', [string] $IssueNumber,
        '--repo', $Repository
    ) -CommandRunner $CommandRunner
    $pullRequests = Invoke-GhJson -Arguments @(
        'pr', 'list',
        '--repo', $Repository,
        '--state', 'open',
        '--json', 'number,title,body,author,comments,reviews,files,state,url,headRefName,baseRefName'
    ) -CommandRunner $CommandRunner

    return [pscustomobject][ordered]@{
        IssueNumber = $IssueNumber
        Development = @($development)
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

    return Invoke-GhJson -Arguments ([string[]] $arguments.ToArray()) -CommandRunner $CommandRunner
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

    return Invoke-GhJson -Arguments @(
        'issue', 'comment', [string] $IssueNumber,
        '--repo', $Repository,
        '--body', $Body
    ) -CommandRunner $CommandRunner
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
