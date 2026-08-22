function Get-KimiPrompt {
    param([object] $IssueContext, [bool] $Revision, [string] $ReviewComments, [string] $PromptOverride)
    if (-not [string]::IsNullOrWhiteSpace($PromptOverride)) { return $PromptOverride }
    $file = if ($Revision) { 'kimi-revision.md' } else { 'kimi-issue.md' }
    $template = [IO.File]::ReadAllText((Join-Path (Join-Path $PSScriptRoot '..\prompts') $file))
    $content = $IssueContext | ConvertTo-Json -Depth 20
    $template = $template.Replace('{{ISSUE_CONTENT}}', $content)
    return $template.Replace('{{REVIEW_COMMENTS}}', [string]$ReviewComments)
}

function ConvertFrom-KimiSummaryText {
    param([string] $Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    $fenced = [regex]::Matches($Text, '(?is)```json\s*(\{.*\})\s*```')
    if ($fenced.Count -eq 1) { $candidate = $fenced[0].Groups[1].Value }
    elseif ($fenced.Count -gt 1) { return $null }
    else {
        $candidate = $Text.Trim()
        if (-not ($candidate.StartsWith('{') -and $candidate.EndsWith('}'))) { return $null }
    }
    try { return ($candidate | ConvertFrom-Json -ErrorAction Stop) } catch { return $null }
}

function Invoke-KimiRun {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [Alias('WorktreePath','IssueWorktreePath')] [string] $IssueWorktree,
        [Parameter(Mandatory = $true)] [object] $IssueContext,
        [object] $Config,
        [string] $RunDirectory,
        [switch] $Revision,
        [string] $ReviewComments,
        [string] $SummaryPath,
        [string] $EventsPath,
        [string] $ActivityLogPath,
        [Parameter(Mandatory = $true)] [string] $StatePath,
        [scriptblock] $ConsoleWriter,
        [string] $Prompt,
        [string] $ThreadId
    )
    if ($null -eq $Config) { $Config = [pscustomobject]@{} }
    $command = [string](Get-CodexValue $Config 'kimiCommand' 'kimi')
    if ([string]::IsNullOrWhiteSpace($RunDirectory)) { $RunDirectory = Join-Path $IssueWorktree '.kimi-run' }
    $RunDirectory = [IO.Path]::GetFullPath($RunDirectory)
    if (-not (Test-Path -LiteralPath $RunDirectory -PathType Container)) { New-Item -ItemType Directory -Path $RunDirectory -Force | Out-Null }
    if ([string]::IsNullOrWhiteSpace($SummaryPath)) { $SummaryPath = Join-Path $RunDirectory 'final-summary.json' }
    if ([string]::IsNullOrWhiteSpace($EventsPath)) { $EventsPath = Join-Path $RunDirectory 'events.jsonl' }
    if ([string]::IsNullOrWhiteSpace($ActivityLogPath)) { $ActivityLogPath = Join-Path $RunDirectory 'activity.log' }
    foreach ($path in @($SummaryPath,$EventsPath,$ActivityLogPath)) {
        $parent = Split-Path -Parent $path
        if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    }
    if (-not (Test-Path -LiteralPath $EventsPath)) { [IO.File]::WriteAllText($EventsPath, '', (New-Object Text.UTF8Encoding($false))) }
    if (-not (Test-Path -LiteralPath $ActivityLogPath)) { [IO.File]::WriteAllText($ActivityLogPath, '', (New-Object Text.UTF8Encoding($false))) }

    $arguments = @('--auto', '--prompt', (Get-KimiPrompt -IssueContext $IssueContext -Revision ([bool]$Revision) -ReviewComments $ReviewComments -PromptOverride $Prompt), '--output-format', 'stream-json')
    $model = [string](Get-CodexValue $Config 'kimiModel' '')
    if (-not [string]::IsNullOrWhiteSpace($model)) { $arguments += @('--model', $model) }
    $promptText = [string]$arguments[2]
    $timeoutMinutes = [double](Get-CodexValue $Config 'kimiTimeoutMinutes' 120)
    $secretValues = Get-CodexBlockedSecretValues
    $assistantText = [System.Text.StringBuilder]::new()
    $localGetValue = { param([object]$object, [string]$name, [object]$default) if ($null -ne $object -and $null -ne $object.PSObject.Properties[$name]) { return $object.PSObject.Properties[$name].Value }; return $default }.GetNewClosure()
    $localRedact = {
        param([string]$text)
        $result = if ($null -eq $text) { '' } else { $text }
        foreach ($secret in @($secretValues | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })) { $result = $result.Replace([string]$secret, '[REDACTED]') }
        $result = [regex]::Replace($result, '(?i)\bgh[pousr]_[A-Za-z0-9_\-]+\b', '[REDACTED]')
        $result = [regex]::Replace($result, '(?i)\bgithub_pat_[A-Za-z0-9_\-]+\b', '[REDACTED]')
        return [regex]::Replace($result, '(?i)\bsk-(?:proj-)?[A-Za-z0-9_\-]{8,}\b', '[REDACTED]')
    }.GetNewClosure()
    $localWrite = { param([string]$text) $line = '[{0}] {1}' -f [DateTime]::UtcNow.ToString('o'), (& $localRedact $text); [IO.File]::AppendAllText($ActivityLogPath, $line + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false))); if ($null -ne $ConsoleWriter) { & $ConsoleWriter $line } else { Write-Host $line } }.GetNewClosure()
    $localAssistantText = {
        param([object] $event)
        $message = & $localGetValue $event 'message' $null
        $content = & $localGetValue $message 'content' (& $localGetValue $event 'content' $null)
        if ($null -eq $content) { $content = & $localGetValue $event 'text' $null }
        if ($content -is [array]) {
            $parts = @($content | ForEach-Object {
                    [string](& $localGetValue $_ 'text' (& $localGetValue $_ 'content' ''))
                })
            return ($parts -join '')
        }
        if ($content -is [string]) { return [string]$content }
        return [string](& $localGetValue $message 'text' '')
    }.GetNewClosure()
    $handleOutput = {
        param([string] $line)
        if ([string]::IsNullOrEmpty($line)) { return }
        $safeLine = & $localRedact $line
        [IO.File]::AppendAllText($EventsPath, $safeLine + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))
        try {
            $event = $safeLine | ConvertFrom-Json -ErrorAction Stop
            $type = [string](& $localGetValue $event 'type' '')
            $role = [string](& $localGetValue $event 'role' '')
            if ($type -match '(?i)assistant|message' -or $role -eq 'assistant') {
                $text = & $localAssistantText $event
                if (-not [string]::IsNullOrWhiteSpace($text)) { [void]$assistantText.Append($text); & $localWrite ('agent message ' + $text) }
            } else {
                & $localWrite ('event ' + $type)
            }
        } catch { & $localWrite ('unparseable Kimi output: ' + $safeLine) }
    }.GetNewClosure()
    $handleError = { param([string] $line) if (-not [string]::IsNullOrWhiteSpace($line)) { & $localWrite ('stderr ' + $line) } }.GetNewClosure()
    try {
        $run = Invoke-CodexProcess -FilePath $command -Arguments $arguments -WorkingDirectory ([IO.Path]::GetFullPath($IssueWorktree)) -Prompt $promptText -TimeoutMilliseconds ([int]($timeoutMinutes * 60000) - 1) -OutputLineCallback $handleOutput -ErrorLineCallback $handleError
    } catch {
        $message = $_.Exception.Message
        $classification = if ($message -match '(?i)not found|cannot find|does not exist') { 'missing_executable' } else { 'process_start_failed' }
        & $localWrite "Kimi process failed to start: $message"
        return [pscustomobject]@{ Status = 'failed'; Classification = $classification; ThreadId = $null; Summary = $null; EventsPath = $EventsPath; ActivityLogPath = $ActivityLogPath }
    }
    $summary = ConvertFrom-KimiSummaryText -Text $assistantText.ToString()
    $validSummary = $null -ne $summary -and (Test-CodexSummary -Summary $summary)
    if ($validSummary) { [IO.File]::WriteAllText($SummaryPath, ($summary | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding($false))) }
    $classification = if ($run.TimedOut) { 'timeout' }
        elseif ($run.ExitCode -ne 0 -and ($run.Stderr -match '(?i)auth|login|api.?key|unauthori')) { 'authentication' }
        elseif ($run.ExitCode -ne 0 -and ($run.Stderr -match '(?i)network|service unavailable|temporar|connection')) { 'transient_service_unavailable' }
        elseif ($run.ExitCode -ne 0) { 'process_failed' }
        elseif (-not $validSummary) { 'malformed_summary' }
        else { 'completed' }
    $status = if ($classification -eq 'completed') { [string]$summary.status } else { 'failed' }
    return [pscustomobject]@{ Status = $status; Classification = $classification; ThreadId = $null; Summary = if ($validSummary) { $summary } else { $null }; EventsPath = $EventsPath; ActivityLogPath = $ActivityLogPath }
}
