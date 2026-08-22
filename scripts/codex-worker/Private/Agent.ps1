$script:CodexWorkerProviders = [ordered]@{
    Codex = [pscustomobject]@{
        Name = 'Codex'; TriggerLabel = 'codex'; RevisionLabel = 'codex:revise'
        StatusLabels = @{ queued='codex:queued'; running='codex:running'; retry='codex:retry'; 'pr-ready'='codex:pr-ready'; blocked='codex:blocked'; done='codex:done' }
    }
    Kimi = [pscustomobject]@{
        Name = 'Kimi'; TriggerLabel = 'kimi'; RevisionLabel = 'kimi-revise'
        StatusLabels = @{ queued='kimi-queued'; running='kimi-running'; retry='kimi-retry'; 'pr-ready'='kimi-ready'; blocked='kimi-blocked'; done='kimi-done' }
    }
}

function Resolve-CodexWorkerProvider {
    param([string] $Provider, [Parameter(Mandatory=$true)][string] $EventName)
    if ([string]::IsNullOrWhiteSpace($Provider)) {
        if ($EventName -match '^codex(?::|$)') { $Provider = 'Codex' }
        else { throw "A provider is required for event '$EventName'." }
    }
    $selected = $script:CodexWorkerProviders[$Provider]
    if ($null -eq $selected) { throw "Unsupported worker provider '$Provider'." }
    if ($EventName -notmatch ('^' + [regex]::Escape($selected.TriggerLabel) + '($|[-:])')) {
        throw "Event '$EventName' is not valid for provider '$Provider'."
    }
    return $selected
}

function Invoke-CodexWorkerAgentRun {
    param([object]$Provider, [hashtable]$RunParameters)
    if ($Provider.Name -eq 'Codex') { return Invoke-CodexRun @RunParameters }
    if ($Provider.Name -eq 'Kimi') { return Invoke-KimiRun @RunParameters }
    throw "Unsupported worker provider '$($Provider.Name)'."
}
