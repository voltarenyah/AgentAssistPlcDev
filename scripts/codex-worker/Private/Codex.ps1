function Get-CodexValue {
    param([object] $Object, [string] $Name, [object] $Default = $null)
    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) { Write-Output -NoEnumerate $property.Value; return }
    return $Default
}

function Set-CodexValue {
    param([object] $Object, [string] $Name, [object] $Value)
    if ($null -eq $Object) { return }
    if ($Object.PSObject.Properties[$Name]) { $Object.$Name = $Value }
    else { Add-Member -InputObject $Object -NotePropertyName $Name -NotePropertyValue $Value -Force }
}

function Get-CodexMemberNames {
    param([object] $Object)
    if ($null -eq $Object) { return @() }
    return @($Object.PSObject.Properties | ForEach-Object { $_.Name })
}

function Test-CodexStringArray {
    param([object] $Value)
    if ($null -eq $Value) { return $false }
    foreach ($item in @($Value)) { if ($item -isnot [string]) { return $false } }
    return $true
}

function Test-CodexSummary {
    [CmdletBinding()]
    param(
        [object] $Summary,
        [string] $SummaryPath
    )

    if ([string]::IsNullOrWhiteSpace($SummaryPath) -eq $false) {
        if (-not (Test-Path -LiteralPath $SummaryPath -PathType Leaf)) { return $false }
        try { $Summary = [IO.File]::ReadAllText([IO.Path]::GetFullPath($SummaryPath)) | ConvertFrom-Json } catch { return $false }
    }
    if ($null -eq $Summary -or $Summary -is [array]) { return $false }

    $required = @('status','rootCauseOrApproach','changedComponents','decisions','validation','warnings','remainingRisks','commitMessage','prTitle','requiresHumanInput','humanQuestion')
    $names = @(Get-CodexMemberNames $Summary)
    foreach ($name in $required) { if ($name -notin $names) { return $false } }
    foreach ($name in $names) { if ($name -notin $required) { return $false } }
    if ((Get-CodexValue $Summary 'status') -notin @('completed','blocked','failed')) { return $false }
    foreach ($name in @('rootCauseOrApproach','commitMessage','prTitle')) { if ((Get-CodexValue $Summary $name) -isnot [string]) { return $false } }
    foreach ($name in @('changedComponents','decisions','warnings','remainingRisks')) { if (-not (Test-CodexStringArray (Get-CodexValue $Summary $name))) { return $false } }
    $human = Get-CodexValue $Summary 'requiresHumanInput'
    if ($human -isnot [bool]) { return $false }
    $question = Get-CodexValue $Summary 'humanQuestion'
    if ($null -ne $question -and $question -isnot [string]) { return $false }

    $validation = Get-CodexValue $Summary 'validation'
    if ($null -eq $validation) { return $false }
    foreach ($entry in @($validation)) {
        if ($null -eq $entry) { return $false }
        $entryNames = @(Get-CodexMemberNames $entry)
        if (@('command','outcome','details') | Where-Object { $_ -notin $entryNames }) { return $false }
        if ($entryNames | Where-Object { $_ -notin @('command','outcome','details') }) { return $false }
        if ((Get-CodexValue $entry 'command') -isnot [string] -or (Get-CodexValue $entry 'details') -isnot [string]) { return $false }
        if ((Get-CodexValue $entry 'outcome') -notin @('passed','failed','skipped')) { return $false }
    }
    return $true
}

function Redact-CodexText {
    param([AllowNull()][string] $Text)
    if ($null -eq $Text) { return '' }
    $redacted = $Text
    $redacted = [regex]::Replace($redacted, '(?i)(github_token|gh_token|openai_api_key|codex_api_key|deepseek_api_key)\s*[=:]\s*[^\s,;]+', '$1=[REDACTED]')
    $redacted = [regex]::Replace($redacted, '(?i)\bgh[pousr]_[A-Za-z0-9_\-]+\b', '[REDACTED]')
    return $redacted
}

function Write-CodexReadableLine {
    param([string] $Text, [string] $ActivityLogPath, [scriptblock] $ConsoleWriter)
    $line = '[{0}] {1}' -f [DateTime]::UtcNow.ToString('o'), (Redact-CodexText $Text)
    [IO.File]::AppendAllText($ActivityLogPath, $line + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))
    if ($null -ne $ConsoleWriter) { & $ConsoleWriter $line }
    else { Write-Host $line }
}

function ConvertTo-CodexArgumentString {
    param([string] $Argument)
    if ($null -eq $Argument -or $Argument.Length -eq 0) { return '""' }
    if ($Argument -notmatch '[\s"]') { return $Argument }
    return '"' + ($Argument -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}

function New-CodexProcessStartInfo {
    param([string] $FilePath, [string[]] $Arguments, [string] $WorkingDirectory)
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $blocked = @('GITHUB_TOKEN','GH_TOKEN','OPENAI_API_KEY','CODEX_API_KEY','DEEPSEEK_API_KEY')
    $startInfo.EnvironmentVariables.Clear()
    foreach ($entry in [Environment]::GetEnvironmentVariables().GetEnumerator()) {
        $startInfo.EnvironmentVariables[[string]$entry.Key] = [string]$entry.Value
    }
    foreach ($name in $blocked) { $startInfo.EnvironmentVariables.Remove($name) }
    $argumentListProperty = $startInfo.PSObject.Properties['ArgumentList']
    if ($null -ne $argumentListProperty) {
        foreach ($argument in $Arguments) { $startInfo.ArgumentList.Add([string]$argument) }
    } else {
        $startInfo.Arguments = (($Arguments | ForEach-Object { ConvertTo-CodexArgumentString ([string]$_) }) -join ' ')
    }
    return $startInfo
}

function Stop-CodexProcessTree {
    param([System.Diagnostics.Process] $Process)
    if ($null -eq $Process) { return }
    try {
        $killWithTree = $Process.GetType().GetMethod('Kill', [type[]] @([bool]))
        if ($null -ne $killWithTree) { $Process.Kill($true) }
        else { $Process.Kill() }
    } catch { try { & taskkill.exe /PID $Process.Id /T /F 2>$null | Out-Null } catch {} }
}

function Invoke-CodexProcess {
    param([string] $FilePath, [string[]] $Arguments, [string] $WorkingDirectory, [string] $Prompt, [int] $TimeoutMilliseconds)
    $startInfo = New-CodexProcessStartInfo -FilePath $FilePath -Arguments $Arguments -WorkingDirectory $WorkingDirectory
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw 'Codex process did not start.' }
        $process.StandardInput.Write($Prompt)
        $process.StandardInput.Close()
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $timedOut = -not $process.WaitForExit($TimeoutMilliseconds)
        if ($timedOut) { Stop-CodexProcessTree $process; $process.WaitForExit(5000) | Out-Null }
        return [pscustomobject]@{ ExitCode = if ($process.HasExited) { $process.ExitCode } else { -1 }; Stdout = $stdoutTask.Result; Stderr = $stderrTask.Result; TimedOut = $timedOut; Arguments = $Arguments }
    } finally { $process.Dispose() }
}

function Save-CodexConfigResumeCapability {
    param([object] $Config, [bool] $Supported)
    Set-CodexValue $Config 'supportsResumeOutputControls' $Supported
    $path = [string](Get-CodexValue $Config 'configPath')
    if ([string]::IsNullOrWhiteSpace($path)) { $path = [string](Get-CodexValue $Config 'ConfigPath') }
    if (-not [string]::IsNullOrWhiteSpace($path)) {
        $json = $Config | ConvertTo-Json -Depth 20
        [IO.File]::WriteAllText([IO.Path]::GetFullPath($path), $json, (New-Object Text.UTF8Encoding($false)))
    }
}

function Update-CodexThreadState {
    param([string] $StatePath, [object] $IssueContext, [string] $ThreadId)
    if ([string]::IsNullOrWhiteSpace($StatePath) -or [string]::IsNullOrWhiteSpace($ThreadId)) { return }
    try {
        $state = if (Test-Path -LiteralPath $StatePath) { [IO.File]::ReadAllText($StatePath) | ConvertFrom-Json } else { [pscustomobject]@{ schemaVersion = 1; issues = [pscustomobject]@{} } }
        if ($null -eq $state.issues) { Add-Member -InputObject $state -NotePropertyName issues -NotePropertyValue ([pscustomobject]@{}) -Force }
        $key = [string](Get-CodexValue $IssueContext 'number')
        if ([string]::IsNullOrWhiteSpace($key)) { $key = [string](Get-CodexValue $IssueContext 'IssueNumber') }
        if ([string]::IsNullOrWhiteSpace($key)) { return }
        $issue = $state.issues.PSObject.Properties[$key]
        if ($null -eq $issue) { Add-Member -InputObject $state.issues -NotePropertyName $key -NotePropertyValue ([pscustomobject]@{}) -Force; $issue = $state.issues.PSObject.Properties[$key] }
        Add-Member -InputObject $issue.Value -NotePropertyName threadId -NotePropertyValue $ThreadId -Force
        [IO.File]::WriteAllText([IO.Path]::GetFullPath($StatePath), ($state | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding($false)))
    } catch { Write-Verbose "Unable to persist Codex thread ID: $($_.Exception.Message)" }
}

function Get-CodexPrompt {
    param([object] $IssueContext, [bool] $Revision, [string] $ReviewComments, [string] $PromptOverride)
    if (-not [string]::IsNullOrWhiteSpace($PromptOverride)) { return $PromptOverride }
    $file = if ($Revision) { 'revision.md' } else { 'issue.md' }
    $template = [IO.File]::ReadAllText((Join-Path (Join-Path $PSScriptRoot '..\prompts') $file))
    $content = $IssueContext | ConvertTo-Json -Depth 20
    $template = $template.Replace('{{ISSUE_CONTENT}}', $content)
    return $template.Replace('{{REVIEW_COMMENTS}}', [string]$ReviewComments)
}

function Invoke-CodexRun {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [Alias('WorktreePath','IssueWorktreePath')] [string] $IssueWorktree,
        [Parameter(Mandatory = $true)] [object] $IssueContext,
        [object] $Config,
        [string] $RunDirectory,
        [switch] $Revision,
        [string] $ThreadId,
        [string] $ReviewComments,
        [string] $SummaryPath,
        [string] $EventsPath,
        [string] $ActivityLogPath,
        [string] $StatePath,
        [scriptblock] $ConsoleWriter,
        [string] $Prompt
    )
    if ($null -eq $Config) { $Config = [pscustomobject]@{} }
    $command = [string](Get-CodexValue $Config 'codexCommand' 'codex')
    if ([string]::IsNullOrWhiteSpace($RunDirectory)) { $RunDirectory = Join-Path $IssueWorktree '.codex-run' }
    $RunDirectory = [IO.Path]::GetFullPath($RunDirectory)
    if (-not (Test-Path -LiteralPath $RunDirectory -PathType Container)) { New-Item -ItemType Directory -Path $RunDirectory -Force | Out-Null }
    if ([string]::IsNullOrWhiteSpace($SummaryPath)) { $SummaryPath = Join-Path $RunDirectory 'final-summary.json' }
    if ([string]::IsNullOrWhiteSpace($EventsPath)) { $EventsPath = Join-Path $RunDirectory 'events.jsonl' }
    if ([string]::IsNullOrWhiteSpace($ActivityLogPath)) { $ActivityLogPath = Join-Path $RunDirectory 'activity.log' }
    foreach ($path in @($SummaryPath,$EventsPath,$ActivityLogPath)) { $parent = Split-Path -Parent $path; if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null } }
    if (-not (Test-Path -LiteralPath $EventsPath)) { [IO.File]::WriteAllText($EventsPath, '', (New-Object Text.UTF8Encoding($false))) }
    if (-not (Test-Path -LiteralPath $ActivityLogPath)) { [IO.File]::WriteAllText($ActivityLogPath, '', (New-Object Text.UTF8Encoding($false))) }

    $schemaPath = [IO.Path]::GetFullPath((Join-Path (Join-Path $PSScriptRoot '..\schemas') 'final-summary.schema.json'))
    $arguments = @('exec','--json','--sandbox','workspace-write','--output-schema',$schemaPath,'--output-last-message',[IO.Path]::GetFullPath($SummaryPath),'-')
    $supportsResume = Get-CodexValue $Config 'supportsResumeOutputControls'
    $probeError = $null
    if ($null -eq $supportsResume -and $Revision) {
        try {
            $probe = Invoke-CodexProcess -FilePath $command -Arguments @('exec','resume','--help') -WorkingDirectory $IssueWorktree -Prompt '' -TimeoutMilliseconds 30000
            $supportsResume = ($probe.ExitCode -eq 0 -and (($probe.Stdout + $probe.Stderr) -notmatch '(?i)unknown|unrecognized|invalid'))
            Save-CodexConfigResumeCapability -Config $Config -Supported ([bool]$supportsResume)
        } catch { $probeError = $_.Exception.Message; $supportsResume = $false; Save-CodexConfigResumeCapability -Config $Config -Supported $false }
    }
    $revisionFallback = $false
    if ($Revision -and [bool]$supportsResume -and -not [string]::IsNullOrWhiteSpace($ThreadId)) {
        $arguments = @('exec','--json','--sandbox','workspace-write','--output-schema',$schemaPath,'--output-last-message',[IO.Path]::GetFullPath($SummaryPath),'resume',$ThreadId,'-')
    } elseif ($Revision) {
        $revisionFallback = $true
        Write-CodexReadableLine -Text 'resume output controls unavailable; started fresh revision thread' -ActivityLogPath $ActivityLogPath -ConsoleWriter $ConsoleWriter
    }
    $promptText = Get-CodexPrompt -IssueContext $IssueContext -Revision ([bool]$Revision) -ReviewComments $ReviewComments -PromptOverride $Prompt
    $timeoutMinutes = [double](Get-CodexValue $Config 'codexTimeoutMinutes' 120)
    try { $run = Invoke-CodexProcess -FilePath $command -Arguments $arguments -WorkingDirectory ([IO.Path]::GetFullPath($IssueWorktree)) -Prompt $promptText -TimeoutMilliseconds ([int]($timeoutMinutes * 60000)) }
    catch {
        $message = $_.Exception.Message
        $classification = if ($message -match '(?i)not found|cannot find|does not exist') { 'missing_executable' } else { 'process_start_failed' }
        Write-CodexReadableLine -Text "Codex process failed to start: $message" -ActivityLogPath $ActivityLogPath -ConsoleWriter $ConsoleWriter
        return [pscustomobject]@{ Status = 'failed'; Classification = $classification; ThreadId = $null; Summary = $null; Arguments = $arguments; SummaryPath = $SummaryPath; EventsPath = $EventsPath; ActivityLogPath = $ActivityLogPath }
    }
    $thread = $null
    foreach ($line in @($run.Stdout -split "`r?`n")) {
        if ([string]::IsNullOrEmpty($line)) { continue }
        [IO.File]::AppendAllText($EventsPath, $line + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))
        try { $event = $line | ConvertFrom-Json } catch { Write-CodexReadableLine -Text "unparseable Codex output: $line" -ActivityLogPath $ActivityLogPath -ConsoleWriter $ConsoleWriter; continue }
        $type = [string](Get-CodexValue $event 'type' 'unknown')
        $item = Get-CodexValue $event 'item'
        $itemType = [string](Get-CodexValue $item 'type' '')
        $effectiveType = if ([string]::IsNullOrWhiteSpace($itemType)) { $type } else { $itemType }
        $eventThread = Get-CodexValue $event 'thread_id'
        if ($null -eq $eventThread) { $eventThread = Get-CodexValue $event 'threadId' }
        if ($type -eq 'thread.started' -and -not [string]::IsNullOrWhiteSpace([string]$eventThread)) { $thread = [string]$eventThread; Update-CodexThreadState -StatePath $StatePath -IssueContext $IssueContext -ThreadId $thread }
        $readable = switch -Regex ($effectiveType) {
            '^thread\.started$' { "thread started $eventThread"; break }
            'command' { "command $((Get-CodexValue $item 'command' (Get-CodexValue $event 'command' (Get-CodexValue $event 'cmd' '')))); exit $((Get-CodexValue $item 'exit_code' (Get-CodexValue $event 'exit_code' (Get-CodexValue $event 'exitCode' 'unknown'))))"; break }
            'file' { "file changes $((Get-CodexValue $item 'path' (Get-CodexValue $event 'path' (Get-CodexValue $event 'paths' '')) ))"; break }
            'message|assistant' { "agent message $((Get-CodexValue $item 'text' (Get-CodexValue $event 'message' (Get-CodexValue $event 'text' '')) ))"; break }
            '^error$|error' { "error $((Get-CodexValue $event 'message' (Get-CodexValue $event 'error' 'unknown')) )"; break }
            'turn\.completed|completed' { 'turn completed'; break }
            default { "event $type"; break }
        }
        Write-CodexReadableLine -Text $readable -ActivityLogPath $ActivityLogPath -ConsoleWriter $ConsoleWriter
    }
    foreach ($errorLine in @($run.Stderr -split "`r?`n")) { if (-not [string]::IsNullOrWhiteSpace($errorLine)) { Write-CodexReadableLine -Text "stderr $errorLine" -ActivityLogPath $ActivityLogPath -ConsoleWriter $ConsoleWriter } }
    $summary = $null
    $validSummary = Test-CodexSummary -SummaryPath $SummaryPath
    if ($validSummary) { $summary = [IO.File]::ReadAllText($SummaryPath) | ConvertFrom-Json }
    $classification = $null
    if ($run.TimedOut) { $classification = 'timeout' }
    elseif ($run.ExitCode -ne 0 -and ($run.Stderr -match '(?i)auth|login|api.?key|unauthori')) { $classification = 'authentication' }
    elseif (-not $validSummary) { $classification = 'malformed_summary' }
    elseif ($run.ExitCode -ne 0 -and ($run.Stderr -match '(?i)network|service unavailable|temporar|connection')) { $classification = 'transient_service_unavailable' }
    elseif ($run.ExitCode -ne 0) { $classification = 'process_failed' }
    else { $classification = 'completed' }
    [pscustomobject]@{ Status = if ($validSummary) { [string]$summary.status } else { 'failed' }; Classification = $classification; ThreadId = $thread; Summary = $summary; Arguments = $arguments; SummaryPath = $SummaryPath; EventsPath = $EventsPath; ActivityLogPath = $ActivityLogPath; RevisionFallback = $revisionFallback; ProbeError = $probeError }
}
