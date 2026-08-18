function Get-CodexValue {
    param([object] $Object, [string] $Name, [object] $Default = $null)
    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) {
        if ($property.Value -is [array]) { return ,$property.Value }
        return $property.Value
    }
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
    foreach ($name in @('changedComponents','decisions','warnings','remainingRisks')) {
        $arrayValue = Get-CodexValue $Summary $name
        if ($arrayValue -isnot [array] -or -not (Test-CodexStringArray $arrayValue)) { return $false }
    }
    $human = Get-CodexValue $Summary 'requiresHumanInput'
    if ($human -isnot [bool]) { return $false }
    $question = Get-CodexValue $Summary 'humanQuestion'
    if ($null -ne $question -and $question -isnot [string]) { return $false }

    $validation = Get-CodexValue $Summary 'validation'
    if ($null -eq $validation -or $validation -isnot [array]) { return $false }
    foreach ($entry in @($validation)) {
        if ($null -eq $entry) { return $false }
        $entryNames = @(Get-CodexMemberNames $entry)
        if (@('command','outcome','details') | Where-Object { $_ -notin $entryNames }) { return $false }
        if ($entryNames | Where-Object { $_ -notin @('command','outcome','details','required') }) { return $false }
        if ((Get-CodexValue $entry 'command') -isnot [string] -or (Get-CodexValue $entry 'details') -isnot [string]) { return $false }
        if ((Get-CodexValue $entry 'outcome') -notin @('passed','failed','skipped')) { return $false }
        if ($entryNames -contains 'required' -and (Get-CodexValue $entry 'required') -isnot [bool]) { return $false }
    }
    return $true
}

function Redact-CodexString {
    param([AllowNull()][string] $Text, [string[]] $SecretValues)
    if ($null -eq $Text) { return '' }
    $redacted = $Text
    foreach ($secret in @($SecretValues | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })) {
        $redacted = $redacted.Replace([string]$secret, '[REDACTED]')
    }
    $redacted = [regex]::Replace($redacted, '(?i)\bgh[pousr]_[A-Za-z0-9_\-]+\b', '[REDACTED]')
    $redacted = [regex]::Replace($redacted, '(?i)\bgithub_pat_[A-Za-z0-9_\-]+\b', '[REDACTED]')
    $redacted = [regex]::Replace($redacted, '(?i)\bsk-(?:proj-)?[A-Za-z0-9_\-]{8,}\b', '[REDACTED]')
    return $redacted
}

function Redact-CodexValue {
    param([object] $Value, [string[]] $SecretValues)
    if ($null -eq $Value) { return $null }
    if ($Value -is [string]) { return (Redact-CodexString -Text ([string]$Value) -SecretValues $SecretValues) }
    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [System.Collections.IDictionary]) {
        $items = @()
        foreach ($item in $Value) { $items += ,(Redact-CodexValue -Value $item -SecretValues $SecretValues) }
        return ,$items
    }
    $properties = @($Value.PSObject.Properties)
    if ($properties.Count -gt 0) {
        $result = [ordered]@{}
        foreach ($property in $properties) { $result[[string]$property.Name] = Redact-CodexValue -Value $property.Value -SecretValues $SecretValues }
        return $result
    }
    return $Value
}

function Redact-CodexText {
    param([AllowNull()][string] $Text, [string[]] $SecretValues)
    if ($null -eq $Text) { return '' }
    try {
        $parsed = $Text | ConvertFrom-Json -ErrorAction Stop
        $sanitized = Redact-CodexValue -Value $parsed -SecretValues $SecretValues
        return ($sanitized | ConvertTo-Json -Compress -Depth 100)
    } catch {
        return (Redact-CodexString -Text $Text -SecretValues $SecretValues)
    }
}

function Write-CodexReadableLine {
    param([string] $Text, [string] $ActivityLogPath, [scriptblock] $ConsoleWriter, [string[]] $SecretValues)
    $line = '[{0}] {1}' -f [DateTime]::UtcNow.ToString('o'), (Redact-CodexText $Text -SecretValues $SecretValues)
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

function Resolve-CodexProcessFilePath {
    param([string] $FilePath)
    if ([string]::IsNullOrWhiteSpace($FilePath) -or [IO.Path]::IsPathRooted($FilePath) -or $FilePath -match '[\\/]') { return $FilePath }
    $application = @(Get-Command -Name $FilePath -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1)[0]
    if ($null -eq $application) { return $FilePath }
    if (-not [string]::IsNullOrWhiteSpace([string]$application.Path)) { return [string]$application.Path }
    return [string]$application.Source
}

function New-CodexProcessStartInfo {
    param([string] $FilePath, [string[]] $Arguments, [string] $WorkingDirectory)
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = Resolve-CodexProcessFilePath -FilePath $FilePath
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

function Get-CodexBlockedSecretValues {
    $values = [System.Collections.Generic.List[string]]::new()
    foreach ($name in @('GITHUB_TOKEN','GH_TOKEN','OPENAI_API_KEY','CODEX_API_KEY','DEEPSEEK_API_KEY')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if (-not [string]::IsNullOrWhiteSpace($value)) { $values.Add([string]$value) }
    }
    return @($values)
}

function Stop-CodexProcessTree {
    param([System.Diagnostics.Process] $Process)
    if ($null -eq $Process) { return }
    $processId = $null
    try { if (-not $Process.HasExited) { $processId = $Process.Id } } catch { return }
    if ($null -eq $processId) { return }
    $killWithTree = $null
    try { $killWithTree = $Process.GetType().GetMethod('Kill', [type[]] @([bool])) } catch {}
    if ($null -ne $killWithTree) {
        try { $Process.Kill($true); return } catch {}
    }
    $killer = $null
    try {
        $killerInfo = New-Object System.Diagnostics.ProcessStartInfo
        $killerInfo.FileName = 'taskkill.exe'
        $killerInfo.Arguments = "/PID $processId /T /F"
        $killerInfo.UseShellExecute = $false
        $killerInfo.CreateNoWindow = $true
        $killerInfo.RedirectStandardOutput = $true
        $killerInfo.RedirectStandardError = $true
        $killer = New-Object System.Diagnostics.Process
        $killer.StartInfo = $killerInfo
        if ($killer.Start()) { $killer.WaitForExit(750) | Out-Null }
    } catch {} finally {
        if ($null -ne $killer) { try { $killer.Dispose() } catch {} }
    }
    try { if (-not $Process.HasExited) { $Process.Kill(); $Process.WaitForExit(750) | Out-Null } } catch {}
}

function Invoke-CodexProcess {
    param([string] $FilePath, [string[]] $Arguments, [string] $WorkingDirectory, [string] $Prompt, [int] $TimeoutMilliseconds, [scriptblock] $OutputLineCallback, [scriptblock] $ErrorLineCallback)
    $startInfo = New-CodexProcessStartInfo -FilePath $FilePath -Arguments $Arguments -WorkingDirectory $WorkingDirectory
    $secretValues = Get-CodexBlockedSecretValues
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    $stdoutLines = [System.Collections.Generic.List[string]]::new()
    $stderrTask = $null
    $inputTask = $null
    $stdoutReader = $null
    try {
        if (-not $process.Start()) { throw 'Codex process did not start.' }
        $started = [Diagnostics.Stopwatch]::StartNew()
        $inputTask = $process.StandardInput.WriteAsync($Prompt)
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $stdoutReader = $process.StandardOutput
        $timedOut = $false; $inputClosed = $false
        $stdoutDone = $false
        while (-not $stdoutDone) {
            $lineTask = $stdoutReader.ReadLineAsync()
            while (-not $lineTask.Wait(20)) {
                if (-not $inputClosed -and $inputTask.IsCompleted) { try { $process.StandardInput.Close() } catch {}; $inputClosed = $true }
                if ($started.ElapsedMilliseconds -ge $TimeoutMilliseconds) { $timedOut = $true; Stop-CodexProcessTree $process; break }
                if ($process.HasExited -and $started.ElapsedMilliseconds -ge 1000) { $stdoutDone = $true; break }
            }
            if ($timedOut -or $stdoutDone) { break }
            $line = $lineTask.Result
            if ($null -eq $line) { $stdoutDone = $true; break }
            $stdoutLines.Add([string]$line)
            if ($null -ne $OutputLineCallback) { & $OutputLineCallback ([string]$line) }
            if (-not $inputClosed -and $inputTask.IsCompleted) { try { $process.StandardInput.Close() } catch {}; $inputClosed = $true }
            if ($started.ElapsedMilliseconds -ge $TimeoutMilliseconds -and -not $process.HasExited) { $timedOut = $true; Stop-CodexProcessTree $process; break }
        }
        while (-not $timedOut -and -not $process.HasExited) {
            if (-not $inputClosed -and $inputTask.IsCompleted) { try { $process.StandardInput.Close() } catch {}; $inputClosed = $true }
            if ($started.ElapsedMilliseconds -ge $TimeoutMilliseconds) { $timedOut = $true; Stop-CodexProcessTree $process; break }
            Start-Sleep -Milliseconds 20
        }
        if (-not $inputClosed) { try { $process.StandardInput.Close() } catch {}; $inputClosed = $true }
        try { if ($null -ne $inputTask) { $inputTask.Wait(250) | Out-Null } } catch {}
        try { $process.StandardInput.Close() } catch {}
        try { $process.WaitForExit(750) | Out-Null } catch {}
        $stderrReady = try { $stderrTask.Wait(250) } catch { $false }
        $stderr = if ($stderrReady) { try { $stderrTask.Result } catch { '' } } else { '' }
        if (-not [string]::IsNullOrWhiteSpace($stderr) -and $null -ne $ErrorLineCallback) { foreach ($errorLine in @($stderr -split "`r?`n")) { if (-not [string]::IsNullOrWhiteSpace($errorLine)) { & $ErrorLineCallback ([string]$errorLine) } } }
        try { $stdoutReader.Close() } catch {}
        try { $process.StandardError.Close() } catch {}
        $exitCode = -1; try { if ($process.HasExited) { $exitCode = $process.ExitCode } } catch {}
        return [pscustomobject]@{ ExitCode = $exitCode; Stdout = ($stdoutLines -join [Environment]::NewLine); Stderr = $stderr; TimedOut = $timedOut; Arguments = $Arguments; SecretValues = $secretValues }
    } finally {
        $hasExited = $true
        try { $hasExited = $process.HasExited } catch {}
        if (-not $hasExited) { Stop-CodexProcessTree $process; try { $process.WaitForExit(750) | Out-Null } catch {} }
        try { $process.StandardInput.Close() } catch {}
        try { if ($null -ne $stdoutReader) { $stdoutReader.Close() } } catch {}
        try { $process.StandardError.Close() } catch {}
        try { $process.Dispose() } catch {}
    }
}

function Save-CodexConfigResumeCapability {
    param([object] $Config, [bool] $Supported)
    Set-CodexValue $Config 'supportsResumeOutputControls' $Supported
    $path = [string](Get-CodexValue $Config 'configPath')
    if ([string]::IsNullOrWhiteSpace($path)) { $path = [string](Get-CodexValue $Config 'ConfigPath') }
    if (-not [string]::IsNullOrWhiteSpace($path)) {
        $parent = Split-Path -Parent ([IO.Path]::GetFullPath($path))
        if (-not (Test-Path -LiteralPath $parent -PathType Container)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        $json = $Config | ConvertTo-Json -Depth 20
        $temporary = "$path.tmp"
        [IO.File]::WriteAllText($temporary, $json, (New-Object Text.UTF8Encoding($false)))
        Move-Item -LiteralPath $temporary -Destination $path -Force
    }
}

function Initialize-CodexResumeCapability {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $IssueWorktree,
        [Parameter(Mandatory = $true)] [object] $Config,
        [string] $ConfigPath,
        [scriptblock] $ProcessRunner
    )
    $command = [string](Get-CodexValue $Config 'codexCommand' 'codex')
    if (-not [string]::IsNullOrWhiteSpace($ConfigPath)) { Set-CodexValue $Config 'configPath' $ConfigPath }
    $probeRequest = [pscustomobject][ordered]@{
        FilePath = $command
        Arguments = [string[]]@('exec','resume','--help')
        WorkingDirectory = [IO.Path]::GetFullPath($IssueWorktree)
        Prompt = ''
        TimeoutMilliseconds = 30000
    }
    $probe = if ($null -ne $ProcessRunner) { & $ProcessRunner $probeRequest } else {
        Invoke-CodexProcess -FilePath $probeRequest.FilePath -Arguments $probeRequest.Arguments -WorkingDirectory $probeRequest.WorkingDirectory -Prompt $probeRequest.Prompt -TimeoutMilliseconds $probeRequest.TimeoutMilliseconds
    }
    $supported = ($probe.ExitCode -eq 0 -and (($probe.Stdout + $probe.Stderr) -notmatch '(?i)unknown|unrecognized|invalid'))
    Save-CodexConfigResumeCapability -Config $Config -Supported ([bool]$supported)
    return [bool]$supported
}

function Update-CodexThreadState {
    param([string] $StatePath, [object] $IssueContext, [string] $ThreadId)
    if ([string]::IsNullOrWhiteSpace($StatePath) -or [string]::IsNullOrWhiteSpace($ThreadId)) { throw 'StatePath and ThreadId are required to persist Codex state.' }
    if (Test-Path -LiteralPath $StatePath -PathType Container) { throw 'StatePath must identify a file.' }
    $state = Read-CodexWorkerState -Path $StatePath
    if ($null -eq $state.issues) { Add-Member -InputObject $state -NotePropertyName issues -NotePropertyValue ([pscustomobject]@{}) -Force }
    $key = [string](Get-CodexValue $IssueContext 'number')
    if ([string]::IsNullOrWhiteSpace($key)) { $key = [string](Get-CodexValue $IssueContext 'IssueNumber') }
    if ([string]::IsNullOrWhiteSpace($key)) { throw 'IssueContext.number is required to persist Codex thread state.' }
    $issue = $state.issues.PSObject.Properties[$key]
    if ($null -eq $issue) { Add-Member -InputObject $state.issues -NotePropertyName $key -NotePropertyValue ([pscustomobject]@{}) -Force; $issue = $state.issues.PSObject.Properties[$key] }
    Add-Member -InputObject $issue.Value -NotePropertyName threadId -NotePropertyValue $ThreadId -Force
    Write-CodexWorkerState -Path $StatePath -State $state
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
        [Parameter(Mandatory = $true)] [string] $StatePath,
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
    $revisionFallback = $false
    $secretValues = Get-CodexBlockedSecretValues
    if ($Revision -and [bool]$supportsResume -and -not [string]::IsNullOrWhiteSpace($ThreadId)) {
        $arguments = @('exec','--json','--sandbox','workspace-write','--output-schema',$schemaPath,'--output-last-message',[IO.Path]::GetFullPath($SummaryPath),'resume',$ThreadId,'-')
    } elseif ($Revision) {
        $revisionFallback = $true
        Write-CodexReadableLine -Text 'resume output controls unavailable; started fresh revision thread' -ActivityLogPath $ActivityLogPath -ConsoleWriter $ConsoleWriter -SecretValues $secretValues
    }
    $promptText = Get-CodexPrompt -IssueContext $IssueContext -Revision ([bool]$Revision) -ReviewComments $ReviewComments -PromptOverride $Prompt
    $timeoutMinutes = [double](Get-CodexValue $Config 'codexTimeoutMinutes' 120)
    $threadHolder = [pscustomobject]@{ Value = $null }
    $localGetValue = { param([object]$object, [string]$name, [object]$default) if ($null -ne $object -and $null -ne $object.PSObject.Properties[$name]) { return $object.PSObject.Properties[$name].Value }; return $default }.GetNewClosure()
    $localSanitizeString = { param([AllowNull()][string]$text) if ($null -eq $text) { return '' }; $result = $text; foreach ($secret in @($secretValues | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })) { $result = $result.Replace([string]$secret, '[REDACTED]') }; $result = [regex]::Replace($result, '(?i)\bgh[pousr]_[A-Za-z0-9_\-]+\b', '[REDACTED]'); $result = [regex]::Replace($result, '(?i)\bgithub_pat_[A-Za-z0-9_\-]+\b', '[REDACTED]'); $result = [regex]::Replace($result, '(?i)\bsk-(?:proj-)?[A-Za-z0-9_\-]{8,}\b', '[REDACTED]'); return $result }.GetNewClosure()
    $localSanitizeValue = $null
    $localSanitizeValue = {
        param([object]$value)
        if ($null -eq $value) { return $null }
        if ($value -is [string]) { return (& $localSanitizeString ([string]$value)) }
        if ($value -is [System.Collections.IEnumerable] -and $value -isnot [System.Collections.IDictionary]) {
            $items = @(); foreach ($item in $value) { $items += ,(& $localSanitizeValue $item) }; return ,$items
        }
        $properties = @($value.PSObject.Properties)
        if ($properties.Count -gt 0) { $result = [ordered]@{}; foreach ($property in $properties) { $result[[string]$property.Name] = & $localSanitizeValue $property.Value }; return $result }
        return $value
    }.GetNewClosure()
    $localRedact = { param([AllowNull()][string]$text) if ($null -eq $text) { return '' }; try { $parsed = $text | ConvertFrom-Json -ErrorAction Stop; $sanitized = & $localSanitizeValue $parsed; return ($sanitized | ConvertTo-Json -Compress -Depth 100) } catch { return (& $localSanitizeString $text) } }.GetNewClosure()
    $localWrite = { param([string]$text) $line = '[{0}] {1}' -f [DateTime]::UtcNow.ToString('o'), (& $localRedact $text); [IO.File]::AppendAllText($ActivityLogPath, $line + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false))); if ($null -ne $ConsoleWriter) { & $ConsoleWriter $line } else { Write-Host $line } }.GetNewClosure()
    $handleOutput = {
        param([string] $line)
        if ([string]::IsNullOrEmpty($line)) { return }
        $safeLine = & $localRedact $line
        [IO.File]::AppendAllText($EventsPath, $safeLine + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))
        try { $event = $safeLine | ConvertFrom-Json } catch { & $localWrite "unparseable Codex output: $safeLine"; return }
        $type = [string](& $localGetValue $event 'type' 'unknown'); $item = & $localGetValue $event 'item' $null; $itemType = [string](& $localGetValue $item 'type' '')
        $effectiveType = if ([string]::IsNullOrWhiteSpace($itemType)) { $type } else { $itemType }
        $eventThread = & $localGetValue $event 'thread_id' $null; if ($null -eq $eventThread) { $eventThread = & $localGetValue $event 'threadId' $null }
        if ($type -eq 'thread.started' -and -not [string]::IsNullOrWhiteSpace([string]$eventThread)) {
            $threadHolder.Value = [string]$eventThread
        }
        $readable = switch -Regex ($effectiveType) {
            '^thread\.started$' { "thread started $eventThread"; break }
            'command' { "command $((& $localGetValue $item 'command' (& $localGetValue $event 'command' (& $localGetValue $event 'cmd' '')))); exit $((& $localGetValue $item 'exit_code' (& $localGetValue $event 'exit_code' (& $localGetValue $event 'exitCode' 'unknown'))))"; break }
            'file' { "file changes $((& $localGetValue $item 'path' (& $localGetValue $event 'path' (& $localGetValue $event 'paths' '')) ))"; break }
            'message|assistant' { "agent message $((& $localGetValue $item 'text' (& $localGetValue $event 'message' (& $localGetValue $event 'text' '')) ))"; break }
            '^error$|error' { "error $((& $localGetValue $event 'message' (& $localGetValue $event 'error' 'unknown')) )"; break }
            'turn\.completed|completed' { 'turn completed'; break }
            default { "event $type"; break }
        }
        & $localWrite $readable
    }.GetNewClosure()
    $handleError = { param([string] $line) if (-not [string]::IsNullOrWhiteSpace($line)) { & $localWrite "stderr $line" } }.GetNewClosure()
    try { $run = Invoke-CodexProcess -FilePath $command -Arguments $arguments -WorkingDirectory ([IO.Path]::GetFullPath($IssueWorktree)) -Prompt $promptText -TimeoutMilliseconds ([int]($timeoutMinutes * 60000) - 1) -OutputLineCallback $handleOutput -ErrorLineCallback $handleError }
    catch {
        $message = $_.Exception.Message
        $classification = if ($message -match '(?i)not found|cannot find|does not exist') { 'missing_executable' } else { 'process_start_failed' }
        Write-CodexReadableLine -Text "Codex process failed to start: $message" -ActivityLogPath $ActivityLogPath -ConsoleWriter $ConsoleWriter -SecretValues $secretValues
        return [pscustomobject]@{ Status = 'failed'; Classification = $classification; ThreadId = $null; Summary = $null; Arguments = $arguments; SummaryPath = $SummaryPath; EventsPath = $EventsPath; ActivityLogPath = $ActivityLogPath }
    }
    $thread = $threadHolder.Value
    if (-not [string]::IsNullOrWhiteSpace([string]$thread)) { Update-CodexThreadState -StatePath $StatePath -IssueContext $IssueContext -ThreadId $thread }
    $summary = $null
    $validSummary = Test-CodexSummary -SummaryPath $SummaryPath
    if ($validSummary) { $summary = [IO.File]::ReadAllText($SummaryPath) | ConvertFrom-Json }
    $classification = $null
    if ($run.TimedOut) { $classification = 'timeout' }
    elseif ($run.ExitCode -ne 0 -and ($run.Stderr -match '(?i)auth|login|api.?key|unauthori')) { $classification = 'authentication' }
    elseif ($run.ExitCode -ne 0 -and ($run.Stderr -match '(?i)network|service unavailable|temporar|connection')) { $classification = 'transient_service_unavailable' }
    elseif ($run.ExitCode -ne 0) { $classification = 'process_failed' }
    elseif (-not $validSummary) { $classification = 'malformed_summary' }
    else { $classification = 'completed' }
    $status = if ($classification -eq 'completed' -and $validSummary) { [string]$summary.status } else { 'failed' }
    [pscustomobject]@{ Status = $status; Classification = $classification; ThreadId = $thread; Summary = $summary; Arguments = $arguments; SummaryPath = $SummaryPath; EventsPath = $EventsPath; ActivityLogPath = $ActivityLogPath; RevisionFallback = $revisionFallback; ProbeError = $probeError }
}
