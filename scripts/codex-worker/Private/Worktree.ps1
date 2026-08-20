Set-StrictMode -Version Latest

function Invoke-CodexGit {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string[]] $Arguments,
        [scriptblock] $CommandRunner
    )

    $root = [System.IO.Path]::GetFullPath($RepositoryRoot)
    $fullArguments = @('-C', $root) + @($Arguments)
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # Git writes ordinary progress to stderr. GitHub Actions sets Stop, so
        # capture that stream and decide success strictly from Git's exit code.
        $ErrorActionPreference = 'Continue'
        if ($null -ne $CommandRunner) {
            $output = & $CommandRunner ([string[]] $fullArguments) 2>&1
            if ($null -eq $output) { return '' }
            return (($output | ForEach-Object { [string] $_ }) -join [Environment]::NewLine)
        }

        $output = & git.exe @fullArguments 2>&1
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($exitCode -ne 0) {
        $message = ($output | Out-String).Trim()
        if ([string]::IsNullOrWhiteSpace($message)) { $message = "git exited with code $exitCode." }
        throw $message
    }

    if ($null -eq $output) { return '' }
    return (($output | ForEach-Object { [string] $_ }) -join [Environment]::NewLine)
}

function Get-CodexIssueBranchName {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [int] $IssueNumber,
        [Parameter(Mandatory = $true)] [string] $Title
    )

    $slug = $Title.ToLowerInvariant() -replace '[^a-z0-9]+', '-'
    $slug = $slug.Trim('-')
    if ($slug.Length -gt 48) { $slug = $slug.Substring(0, 48).Trim('-') }
    if ([string]::IsNullOrWhiteSpace($slug)) { $slug = 'issue' }
    return "codex/$IssueNumber-$slug"
}

function Get-RegisteredWorktrees {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [scriptblock] $CommandRunner
    )

    $raw = Invoke-CodexGit -RepositoryRoot $RepositoryRoot -Arguments @('worktree', 'list', '--porcelain') -CommandRunner $CommandRunner
    $records = [System.Collections.Generic.List[object]]::new()
    $current = $null
    foreach ($line in @($raw -split "`r?`n")) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            if ($null -ne $current) { $records.Add([pscustomobject]$current) | Out-Null; $current = $null }
            continue
        }
        if ($line.StartsWith('worktree ')) {
            if ($null -ne $current) { $records.Add([pscustomobject]$current) | Out-Null }
            $current = [ordered]@{ Path = [System.IO.Path]::GetFullPath($line.Substring(9)); Head = $null; Branch = $null; Bare = $false; Locked = $false; Prunable = $false; Reason = $null }
            continue
        }
        if ($null -eq $current) { continue }
        if ($line.StartsWith('HEAD ')) { $current.Head = $line.Substring(5); continue }
        if ($line.StartsWith('branch ')) { $current.Branch = $line.Substring(7) -replace '^refs/heads/', ''; continue }
        if ($line -eq 'bare') { $current.Bare = $true; continue }
        if ($line -eq 'locked') { $current.Locked = $true; continue }
        if ($line.StartsWith('locked ')) { $current.Locked = $true; $current.Reason = $line.Substring(7); continue }
        if ($line -eq 'prunable') { $current.Prunable = $true; continue }
        if ($line.StartsWith('prunable ')) { $current.Prunable = $true; $current.Reason = $line.Substring(9); continue }
    }
    if ($null -ne $current) { $records.Add([pscustomobject]$current) | Out-Null }
    return @($records.ToArray())
}

function Assert-PathUnderRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $Root
    )

    try {
        $normalizedPath = [System.IO.Path]::GetFullPath($Path)
        $normalizedRoot = [System.IO.Path]::GetFullPath($Root)
        $separator = [System.IO.Path]::DirectorySeparatorChar.ToString()
        $pathWithSeparator = $normalizedPath.TrimEnd('\', '/') + $separator
        $rootWithSeparator = $normalizedRoot.TrimEnd('\', '/') + $separator
        if (-not $pathWithSeparator.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'outside'
        }
    } catch {
        if ($_.Exception.Message -eq 'outside') { throw 'Path is outside the automation worktree root.' }
        throw 'Path is outside the automation worktree root.'
    }
    return $true
}

function Get-CodexWorktreeTargetPath {
    param([string] $WorktreeRoot, [string] $BranchName, [int] $IssueNumber)
    $shortName = $BranchName -replace '^codex/', ''
    $shortName = $shortName -replace ('^' + [regex]::Escape([string]$IssueNumber) + '-'), ''
    $safeName = "issue-$IssueNumber-$shortName" -replace '[\\/]+', '-'
    return [System.IO.Path]::GetFullPath((Join-Path $WorktreeRoot $safeName))
}

function Get-OrCreateCodexIssueWorktree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [string] $WorktreeRoot,
        [Parameter(Mandatory = $true)] [int] $IssueNumber,
        [Parameter(Mandatory = $true)] [string] $Title,
        [string] $BranchName,
        [string] $DefaultBranch = 'master',
        [scriptblock] $CommandRunner
    )

    $repository = [System.IO.Path]::GetFullPath($RepositoryRoot)
    if ([string]::IsNullOrWhiteSpace($WorktreeRoot)) { $WorktreeRoot = Join-Path $repository '.worktrees' }
    $worktreeRootFull = [System.IO.Path]::GetFullPath($WorktreeRoot)
    if ([string]::IsNullOrWhiteSpace($BranchName)) { $BranchName = Get-CodexIssueBranchName -IssueNumber $IssueNumber -Title $Title }
    if ($BranchName -notmatch '^codex/[a-z0-9][a-z0-9._/-]*$') { throw "Invalid Codex issue branch name '$BranchName'." }
    $target = Get-CodexWorktreeTargetPath -WorktreeRoot $worktreeRootFull -BranchName $BranchName -IssueNumber $IssueNumber
    Assert-PathUnderRoot -Path $target -Root $worktreeRootFull | Out-Null
    if (-not (Test-Path -LiteralPath $worktreeRootFull -PathType Container) -and (Test-Path -LiteralPath $repository -PathType Container)) {
        New-Item -ItemType Directory -Path $worktreeRootFull -Force | Out-Null
    }

    Invoke-CodexGit -RepositoryRoot $repository -Arguments @('fetch', 'origin', '--prune') -CommandRunner $CommandRunner | Out-Null
    $registered = @(Get-RegisteredWorktrees -RepositoryRoot $repository -CommandRunner $CommandRunner)
    $existing = @($registered | Where-Object { $_.Branch -eq $BranchName })
    if (Test-Path -LiteralPath $target) {
        $targetRegistered = @($existing | Where-Object { [System.IO.Path]::GetFullPath([string]$_.Path) -eq $target })
        if ($targetRegistered.Count -eq 0) {
            throw "Target path '$target' exists but is not the registered worktree for branch '$BranchName'."
        }
    }
    if ($existing.Count -gt 0) {
        $existingPath = [System.IO.Path]::GetFullPath([string] $existing[0].Path)
        if ($existingPath -eq $repository -or $existingPath -eq $worktreeRootFull) { throw "Issue branch '$BranchName' is registered outside the automation worktree root." }
        Assert-PathUnderRoot -Path $existingPath -Root $worktreeRootFull | Out-Null
        return [pscustomobject][ordered]@{ Path = $existingPath; BranchName = $BranchName; Reused = $true; Created = $false }
    }

    $remoteReference = "origin/$BranchName"
    $hasRemoteBranch = $false
    try {
        $remoteResult = Invoke-CodexGit -RepositoryRoot $repository -Arguments @('show-ref', '--verify', "refs/remotes/$remoteReference") -CommandRunner $CommandRunner
        $hasRemoteBranch = -not [string]::IsNullOrWhiteSpace($remoteResult)
    } catch { $hasRemoteBranch = $false }

    $hasLocalBranch = $false
    try {
        $localResult = Invoke-CodexGit -RepositoryRoot $repository -Arguments @('show-ref', '--verify', "refs/heads/$BranchName") -CommandRunner $CommandRunner
        $hasLocalBranch = -not [string]::IsNullOrWhiteSpace($localResult)
    } catch { $hasLocalBranch = $false }

    if ($hasLocalBranch) {
        Invoke-CodexGit -RepositoryRoot $repository -Arguments @('worktree', 'add', $target, $BranchName) -CommandRunner $CommandRunner | Out-Null
    } elseif ($hasRemoteBranch) {
        Invoke-CodexGit -RepositoryRoot $repository -Arguments @('worktree', 'add', '-b', $BranchName, $target, $remoteReference) -CommandRunner $CommandRunner | Out-Null
    } else {
        Invoke-CodexGit -RepositoryRoot $repository -Arguments @('worktree', 'add', '-b', $BranchName, $target, "origin/$DefaultBranch") -CommandRunner $CommandRunner | Out-Null
    }
    return [pscustomobject][ordered]@{ Path = $target; BranchName = $BranchName; Reused = $false; Created = $true }
}

function ConvertTo-CodexSafeLogText {
    param([string] $Text)
    if ($null -eq $Text) { return '' }
    return ($Text -replace '(?i)(password|secret|token|authorization)(\s*[:=]\s*)[^\s&]+', '$1$2[REDACTED]' -replace '(?i)Bearer\s+[^\s]+', 'Bearer [REDACTED]' -replace '(?i)(https?://)([^/@\s]+):([^/@\s]+)@', '$1[REDACTED]@')
}

function Write-CodexActivityLog {
    param([string] $Path, [string] $Text)
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    $directory = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($Path))
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    Add-Content -LiteralPath $Path -Value ("[{0}] {1}" -f [DateTime]::UtcNow.ToString('o'), (ConvertTo-CodexSafeLogText $Text))
}

function Invoke-CodexSetupCommand {
    param([string] $FilePath, [string[]] $Arguments, [scriptblock] $ProcessRunner)
    if ($null -ne $ProcessRunner) {
        $result = & $ProcessRunner $FilePath ([string[]] $Arguments)
        if ($result -is [psobject] -and $result.PSObject.Properties.Name -contains 'ExitCode') { return $result }
        return [pscustomobject]@{ ExitCode = 0; Output = (($result | ForEach-Object { [string] $_ }) -join [Environment]::NewLine) }
    }
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # Setup tools can write informational progress to stderr even when they
        # succeed. GitHub Actions sets Stop, so capture both streams and decide
        # success strictly from the native process exit code.
        $ErrorActionPreference = 'Continue'
        $output = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = (($output | ForEach-Object { [string] $_ }) -join [Environment]::NewLine) }
}

function Test-CodexNpmLockMetadata {
    param([string] $StudioPath)
    $checkedIn = Join-Path $StudioPath 'package-lock.json'
    $installed = Join-Path $StudioPath 'node_modules\.package-lock.json'
    if (-not (Test-Path -LiteralPath $checkedIn -PathType Leaf) -or -not (Test-Path -LiteralPath $installed -PathType Leaf)) { return $false }
    try {
        $a = [System.IO.File]::ReadAllText($checkedIn)
        $b = [System.IO.File]::ReadAllText($installed)
        foreach ($name in @('name', 'version', 'lockfileVersion')) {
            $pattern = '"' + $name + '"\s*:\s*(?:"([^"]*)"|([^,}\s]+))'
            $left = [regex]::Match($a, $pattern)
            $right = [regex]::Match($b, $pattern)
            if (-not $left.Success -or -not $right.Success) { return $false }
            $leftValue = if ($left.Groups[1].Success) { $left.Groups[1].Value } else { $left.Groups[2].Value }
            $rightValue = if ($right.Groups[1].Success) { $right.Groups[1].Value } else { $right.Groups[2].Value }
            if ($leftValue -ne $rightValue) { return $false }
        }
        return $true
    } catch { return $false }
}

function Invoke-CodexSetupStep {
    param([string] $FilePath, [string[]] $Arguments, [string] $ActivityLogPath, [scriptblock] $ProcessRunner)
    $result = Invoke-CodexSetupCommand -FilePath $FilePath -Arguments $Arguments -ProcessRunner $ProcessRunner
    Write-CodexActivityLog -Path $ActivityLogPath -Text ("{0} {1}`n{2}" -f $FilePath, ($Arguments -join ' '), $result.Output)
    if ([int]$result.ExitCode -ne 0) { throw "$FilePath exited with code $($result.ExitCode)." }
    return [pscustomobject]@{ FilePath = $FilePath; Arguments = $Arguments; Output = $result.Output }
}

function Initialize-CodexIssueWorktree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [Alias('WorktreePath')] [string] $Worktree,
        [Parameter(Mandatory = $true)] [object] $Config,
        [string] $ActivityLogPath,
        [scriptblock] $ProcessRunner
    )

    $solutionPath = Join-Path $Worktree 'AgentAssistPlcDev.sln'
    $studioPath = Join-Path $Worktree 'studio'
    $agentServicePath = Join-Path $Worktree 'agent-service'
    $venvPath = Join-Path $agentServicePath '.venv'
    if ([string]::IsNullOrWhiteSpace($ActivityLogPath) -and $Config.PSObject.Properties.Name -contains 'activityLogPath') { $ActivityLogPath = [string] $Config.activityLogPath }
    $results = [System.Collections.Generic.List[object]]::new()
    $step = Invoke-CodexSetupStep -FilePath 'dotnet' -Arguments @('restore', $solutionPath) -ActivityLogPath $ActivityLogPath -ProcessRunner $ProcessRunner
    $results.Add($step) | Out-Null
    if (-not (Test-CodexNpmLockMetadata -StudioPath $studioPath)) {
        $step = Invoke-CodexSetupStep -FilePath 'npm.cmd' -Arguments @('ci', '--prefix', $studioPath) -ActivityLogPath $ActivityLogPath -ProcessRunner $ProcessRunner
        $results.Add($step) | Out-Null
    }

    $bootstrapPython = [string] $Config.bootstrapPython
    $venvPython = Join-Path $venvPath 'Scripts\python.exe'
    $venvValid = $false
    if (Test-Path -LiteralPath $venvPython -PathType Leaf) {
        $check = Invoke-CodexSetupCommand -FilePath $venvPython -Arguments @('-c', 'import app_assistant, pytest') -ProcessRunner $ProcessRunner
        Write-CodexActivityLog -Path $ActivityLogPath -Text ("{0} -c import app_assistant, pytest`n{1}" -f $venvPython, $check.Output)
        $venvValid = ([int]$check.ExitCode -eq 0)
    }
    if (-not $venvValid) {
        $step = Invoke-CodexSetupStep -FilePath $bootstrapPython -Arguments @('-m', 'venv', $venvPath) -ActivityLogPath $ActivityLogPath -ProcessRunner $ProcessRunner
        $results.Add($step) | Out-Null
        $step = Invoke-CodexSetupStep -FilePath $venvPython -Arguments @('-m', 'pip', 'install', '-e', "$agentServicePath[test]") -ActivityLogPath $ActivityLogPath -ProcessRunner $ProcessRunner
        $results.Add($step) | Out-Null
    }
    return @($results.ToArray())
}

function Test-CodexWorktreeCleanup {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $WorktreeRoot,
        [Parameter(Mandatory = $true)] [string] $WorktreePath,
        [Parameter(Mandatory = $true)] [string] $BranchName,
        [scriptblock] $CommandRunner,
        [scriptblock] $ProcessProvider
    )

    $blockers = [System.Collections.Generic.List[string]]::new()
    try { Assert-PathUnderRoot -Path $WorktreePath -Root $WorktreeRoot | Out-Null } catch { $blockers.Add($_.Exception.Message) | Out-Null; return @($blockers.ToArray()) }
    $normalizedCleanupPath = [System.IO.Path]::GetFullPath($WorktreePath).TrimEnd([char]'\', [char]'/')
    $normalizedCleanupRoot = [System.IO.Path]::GetFullPath($WorktreeRoot).TrimEnd([char]'\', [char]'/')
    if ($normalizedCleanupPath -eq $normalizedCleanupRoot) {
        $blockers.Add('Worktree path must be below the automation worktree root.') | Out-Null
        return @($blockers.ToArray())
    }
    try {
        $registered = @(Get-RegisteredWorktrees -RepositoryRoot $RepositoryRoot -CommandRunner $CommandRunner)
        $normalized = [System.IO.Path]::GetFullPath($WorktreePath)
        $match = @($registered | Where-Object { [System.IO.Path]::GetFullPath([string]$_.Path) -eq $normalized -and $_.Branch -eq $BranchName })
        if ($match.Count -eq 0) { $blockers.Add('Worktree is not registered for the expected branch.') | Out-Null }
    } catch { $blockers.Add("Unable to verify worktree registration: $($_.Exception.Message)") | Out-Null }

    if ($null -eq $ProcessProvider) { $ProcessProvider = { @(Get-CimInstance Win32_Process -ErrorAction Stop) } }
    try {
        $pathText = [System.IO.Path]::GetFullPath($WorktreePath)
        foreach ($process in @(& $ProcessProvider)) {
            $processProperties = @($process.PSObject.Properties | ForEach-Object { $_.Name })
            if (($processProperties -contains 'Succeeded' -and -not [bool]$process.Succeeded) -or ($processProperties -contains 'Failed' -and [bool]$process.Failed)) {
                $errorText = if ($processProperties -contains 'Error') { [string]$process.Error } else { 'provider reported failure' }
                $blockers.Add("Unable to inspect active processes: $errorText") | Out-Null
                break
            }
            $commandLine = if ($process -is [string]) { [string] $process } else { [string] $process.CommandLine }
            if (-not [string]::IsNullOrWhiteSpace($commandLine) -and $commandLine.IndexOf($pathText, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { $blockers.Add('An active process references the worktree path.') | Out-Null; break }
        }
    } catch { $blockers.Add("Unable to inspect active processes: $($_.Exception.Message)") | Out-Null }

    try {
        $dirty = Invoke-CodexGit -RepositoryRoot $RepositoryRoot -Arguments @('-C', $WorktreePath, 'status', '--porcelain', '--untracked-files=all') -CommandRunner $CommandRunner
        if (-not [string]::IsNullOrWhiteSpace($dirty)) { $blockers.Add('Worktree has uncommitted changes.') | Out-Null }
    } catch { $blockers.Add("Unable to verify worktree status: $($_.Exception.Message)") | Out-Null }
    try {
        $ahead = Invoke-CodexGit -RepositoryRoot $RepositoryRoot -Arguments @('log', "origin/$BranchName..$BranchName", '--oneline') -CommandRunner $CommandRunner
        if (-not [string]::IsNullOrWhiteSpace($ahead)) { $blockers.Add('Issue branch contains commits not present on its remote branch.') | Out-Null }
    } catch { $blockers.Add("Unable to verify remote branch history: $($_.Exception.Message)") | Out-Null }
    return @($blockers.ToArray())
}

function Remove-CodexWorktree {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $WorktreeRoot,
        [Parameter(Mandatory = $true)] [string] $WorktreePath,
        [Parameter(Mandatory = $true)] [string] $BranchName,
        [scriptblock] $CommandRunner,
        [scriptblock] $ProcessProvider
    )
    $blockers = @(Test-CodexWorktreeCleanup -RepositoryRoot $RepositoryRoot -WorktreeRoot $WorktreeRoot -WorktreePath $WorktreePath -BranchName $BranchName -CommandRunner $CommandRunner -ProcessProvider $ProcessProvider)
    if ($blockers.Count -gt 0) { throw ("Cannot remove Codex worktree: " + ($blockers -join ' ')) }
    if ($PSCmdlet.ShouldProcess($WorktreePath, 'Remove Git worktree')) {
        Invoke-CodexGit -RepositoryRoot $RepositoryRoot -Arguments @('worktree', 'remove', $WorktreePath) -CommandRunner $CommandRunner | Out-Null
    }
    return $true
}
