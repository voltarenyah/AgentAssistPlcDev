Set-StrictMode -Version Latest

function Test-CodexInteractiveSession {
    [CmdletBinding()]
    param()

    try {
        if (-not [Environment]::UserInteractive) { return $false }
        $sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
        if ($sessionId -le 0) { return $false }

        $explorer = @(Get-Process -Name explorer -ErrorAction SilentlyContinue |
            Where-Object { $_.SessionId -eq $sessionId })
        if ($explorer.Count -eq 0) { return $false }

        $logonUi = @(Get-Process -Name LogonUI -ErrorAction SilentlyContinue |
            Where-Object { $_.SessionId -eq $sessionId })
        if ($logonUi.Count -gt 0) { return $false }
        return $true
    } catch {
        return $false
    }
}

function Read-CodexDeploymentNotifierConfig {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $ConfigPath)

    if ([string]::IsNullOrWhiteSpace($ConfigPath) -or -not [IO.Path]::IsPathRooted($ConfigPath)) {
        throw 'ConfigPath must be an absolute path.'
    }
    $resolvedConfigPath = [IO.Path]::GetFullPath($ConfigPath)
    if (-not (Test-Path -LiteralPath $resolvedConfigPath -PathType Leaf)) {
        throw "Notifier config was not found: $resolvedConfigPath"
    }
    try {
        $config = [IO.File]::ReadAllText($resolvedConfigPath) | ConvertFrom-Json
    } catch {
        throw "Notifier config is malformed: $($_.Exception.Message)"
    }
    if ($null -eq $config) { throw 'Notifier config is empty.' }

    $repositoryRoot = [string](Get-CodexNotificationValue -Object $config -Name 'repositoryRoot' -Default '')
    $dataRoot = [string](Get-CodexNotificationValue -Object $config -Name 'dataRoot' -Default '')
    if ([string]::IsNullOrWhiteSpace($repositoryRoot) -or [string]::IsNullOrWhiteSpace($dataRoot)) {
        throw 'Notifier config must specify repositoryRoot and dataRoot.'
    }
    $paths = Resolve-CodexWorkerPaths -RepositoryRoot $repositoryRoot -DataRoot $dataRoot
    $repoPrefix = $paths.RepositoryRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($paths.DataRoot.Equals($paths.RepositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $paths.DataRoot.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Notifier dataRoot must be outside the repository.'
    }
    if (-not $resolvedConfigPath.Equals($paths.ConfigPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Notifier ConfigPath must be the config.json under the configured dataRoot.'
    }
    $declaredConfigPath = [string](Get-CodexNotificationValue -Object $config -Name 'configPath' -Default '')
    if (-not [string]::IsNullOrWhiteSpace($declaredConfigPath)) {
        if (-not [IO.Path]::IsPathRooted($declaredConfigPath) -or
            -not ([IO.Path]::GetFullPath($declaredConfigPath)).Equals($resolvedConfigPath, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Notifier configPath does not match the supplied ConfigPath.'
        }
    }
    return [pscustomobject][ordered]@{ Config = $config; Paths = $paths }
}

function Get-CodexNotificationValue {
    param([object] $Object, [string] $Name, [object] $Default = $null)
    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    return $property.Value
}

function Copy-CodexNotificationObject {
    param([Parameter(Mandatory = $true)][object] $Object)
    return (($Object | ConvertTo-Json -Depth 50) | ConvertFrom-Json)
}

function Test-CodexNotificationDue {
    param([object] $Deployment, [DateTime] $Now)
    if ($null -eq $Deployment) { return $false }
    $status = [string](Get-CodexNotificationValue $Deployment 'status' '')
    if ($status -notin @('pending', 'snoozed')) { return $false }

    $snooze = Get-CodexNotificationValue $Deployment 'snoozeUntil' $null
    if ($null -eq $snooze -or [string]::IsNullOrWhiteSpace([string]$snooze)) {
        if ($status -eq 'snoozed') { throw 'A snoozed deployment must contain snoozeUntil.' }
        return $true
    }

    $snoozeAt = [DateTimeOffset]::MinValue
    if ($snooze -is [DateTimeOffset]) {
        $snoozeAt = $snooze.ToUniversalTime()
    } elseif ($snooze -is [DateTime]) {
        $snoozeAt = ([DateTimeOffset]$snooze).ToUniversalTime()
    } elseif (-not [DateTimeOffset]::TryParse(
            [string]$snooze,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$snoozeAt)) {
        throw 'The persisted deployment snooze is invalid.'
    }
    return [DateTimeOffset]$Now.ToUniversalTime() -ge $snoozeAt
}

function Get-CodexNotificationStateSignature {
    param([object] $Deployment)
    if ($null -eq $Deployment) { return '<null>' }
    $normalizeTimestamp = {
        param([object] $Value)
        if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) { return '' }
        if ($Value -is [DateTimeOffset]) {
            return [string]$Value.ToUniversalTime().Ticks
        }
        if ($Value -is [DateTime]) {
            return [string]$Value.ToUniversalTime().Ticks
        }
        $parsed = [DateTimeOffset]::MinValue
        if ([DateTimeOffset]::TryParse(
                [string]$Value,
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::RoundtripKind,
                [ref]$parsed)) {
            return [string]$parsed.ToUniversalTime().Ticks
        }
        return [string]$Value
    }.GetNewClosure()
    $fields = [ordered]@{
        targetCommit = [string](Get-CodexNotificationValue $Deployment 'targetCommit' '')
        sourcePr = [int](Get-CodexNotificationValue $Deployment 'sourcePr' 0)
        requestedAt = & $normalizeTimestamp (Get-CodexNotificationValue $Deployment 'requestedAt' '')
        snoozeUntil = & $normalizeTimestamp (Get-CodexNotificationValue $Deployment 'snoozeUntil' '')
        status = [string](Get-CodexNotificationValue $Deployment 'status' '')
    }
    return (($fields | ConvertTo-Json -Compress -Depth 10))
}

function Write-CodexNotificationStateVerified {
    param(
        [Parameter(Mandatory = $true)] [string] $StatePath,
        [Parameter(Mandatory = $true)] [object] $DesiredState,
        [object] $ExpectedDeployment,
        [scriptblock] $StateWriter,
        [scriptblock] $StateReader
    )

    # Clone before handing state to an injected writer. This prevents an
    # in-memory alias from making a no-op fake writer look durable.
    $snapshot = Copy-CodexNotificationObject -Object $DesiredState
    if ($null -ne $StateWriter) { & $StateWriter $StatePath $snapshot | Out-Null }
    else { Write-CodexWorkerState -Path $StatePath -State $snapshot }

    $persisted = if ($null -ne $StateReader) { & $StateReader $StatePath } else { Read-CodexWorkerState -Path $StatePath }
    $actualDeployment = Get-CodexNotificationValue $persisted 'deployment' $null
    $expectedSignature = Get-CodexNotificationStateSignature -Deployment $ExpectedDeployment
    $actualSignature = Get-CodexNotificationStateSignature -Deployment $actualDeployment
    if ($expectedSignature -ne $actualSignature) {
        throw 'The notification state was not durably persisted.'
    }
    return $persisted
}

function New-CodexDeploymentDialogController {
    [CmdletBinding()]
    param(
        [int] $Seconds = 10,
        [scriptblock] $StartTimer,
        [scriptblock] $StopTimer,
        [scriptblock] $DisposeTimer,
        [scriptblock] $CloseWindow,
        [scriptblock] $SetMessage
    )

    $seconds = [Math]::Max(1, $Seconds)
    $state = [pscustomobject][ordered]@{
        Remaining = $seconds
        Decision = $null
        TimerStarted = $false
        TimerStopped = $false
        TimerDisposed = $false
        WindowClosed = $false
        Topmost = $true
        Message = "Automation Workbench will rebuild in $seconds seconds."
        LaterLabel = 'Later (5 min)'
        CancelLabel = 'Cancel'
    }

    if ($null -eq $SetMessage) { $SetMessage = { param($message) $state.Message = $message }.GetNewClosure() }
    if ($null -eq $StartTimer) { $StartTimer = { }.GetNewClosure() }
    if ($null -eq $StopTimer) { $StopTimer = { }.GetNewClosure() }
    if ($null -eq $DisposeTimer) { $DisposeTimer = { }.GetNewClosure() }
    if ($null -eq $CloseWindow) { $CloseWindow = { $state.WindowClosed = $true }.GetNewClosure() }

    $cleanup = {
        if (-not $state.TimerStopped) {
            & $StopTimer
            $state.TimerStopped = $true
        }
        if (-not $state.TimerDisposed) {
            & $DisposeTimer
            $state.TimerDisposed = $true
        }
    }.GetNewClosure()
    $finish = {
        param([string] $decision)
        if ($null -eq $state.Decision) { $state.Decision = $decision }
        & $cleanup
        & $CloseWindow
    }.GetNewClosure()
    $contentRendered = {
        if (-not $state.TimerStarted -and $null -eq $state.Decision) {
            & $StartTimer
            $state.TimerStarted = $true
        }
    }.GetNewClosure()
    $tick = {
        if (-not $state.TimerStarted -or $null -ne $state.Decision) { return }
        $state.Remaining--
        $state.Message = "Automation Workbench will rebuild in $($state.Remaining) seconds."
        & $SetMessage $state.Message
        if ($state.Remaining -le 0) { & $finish 'Deploy' }
    }.GetNewClosure()
    $later = { & $finish 'Later' }.GetNewClosure()
    $cancel = { & $finish 'Cancel' }.GetNewClosure()
    $closed = {
        if ($null -eq $state.Decision) { $state.Decision = 'Later' }
        & $cleanup
        $state.WindowClosed = $true
    }.GetNewClosure()

    return [pscustomobject][ordered]@{
        State = $state
        ContentRendered = $contentRendered
        Tick = $tick
        Later = $later
        Cancel = $cancel
        Closed = $closed
    }
}

function Show-CodexDeploymentDialog {
    [CmdletBinding()]
    param([int] $Seconds = 10)

    if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne [Threading.ApartmentState]::STA) {
        throw 'The deployment notification requires an STA PowerShell session.'
    }

    try {
        Add-Type -AssemblyName PresentationCore -ErrorAction Stop
        Add-Type -AssemblyName PresentationFramework -ErrorAction Stop
        Add-Type -AssemblyName WindowsBase -ErrorAction Stop
    } catch {
        throw 'WPF is unavailable for the deployment notification.'
    }

    $window = New-Object System.Windows.Window
    $window.Title = 'Automation Workbench'
    $window.Topmost = $true
    $window.Width = 430
    $window.Height = 185
    $window.ResizeMode = [System.Windows.ResizeMode]::NoResize
    $window.WindowStartupLocation = [System.Windows.WindowStartupLocation]::CenterScreen

    $panel = New-Object System.Windows.Controls.StackPanel
    $panel.Margin = New-Object System.Windows.Thickness(18)
    $message = New-Object System.Windows.Controls.TextBlock
    $message.Text = "Automation Workbench will rebuild in $([Math]::Max(1, $Seconds)) seconds."
    $message.FontSize = 16
    $message.TextWrapping = [System.Windows.TextWrapping]::Wrap
    $panel.Children.Add($message) | Out-Null

    $buttons = New-Object System.Windows.Controls.StackPanel
    $buttons.Orientation = [System.Windows.Controls.Orientation]::Horizontal
    $buttons.HorizontalAlignment = [System.Windows.HorizontalAlignment]::Right
    $buttons.Margin = New-Object System.Windows.Thickness(0, 24, 0, 0)
    $later = New-Object System.Windows.Controls.Button
    $later.Content = 'Later (5 min)'
    $later.Width = 115
    $later.Margin = New-Object System.Windows.Thickness(0, 0, 10, 0)
    $cancel = New-Object System.Windows.Controls.Button
    $cancel.Content = 'Cancel'
    $cancel.Width = 90
    $buttons.Children.Add($later) | Out-Null
    $buttons.Children.Add($cancel) | Out-Null
    $panel.Children.Add($buttons) | Out-Null
    $window.Content = $panel

    $timer = New-Object System.Windows.Threading.DispatcherTimer
    $timer.Interval = [TimeSpan]::FromSeconds(1)
    $controller = New-CodexDeploymentDialogController -Seconds $Seconds `
        -StartTimer { $timer.Start() } `
        -StopTimer { if ($timer.IsEnabled) { $timer.Stop() } } `
        -DisposeTimer { } `
        -CloseWindow { $window.Close() } `
        -SetMessage { param($text) $message.Text = $text }
    $timer.add_Tick({ & $controller.Tick })
    $window.add_ContentRendered({ & $controller.ContentRendered })
    $later.Add_Click({ & $controller.Later })
    $cancel.Add_Click({ & $controller.Cancel })
    $window.add_Closed({ & $controller.Closed })

    $window.ShowDialog() | Out-Null
    return [string]$controller.State.Decision
}

function Invoke-CodexDeploymentNotificationCycle {
    [CmdletBinding()]
    param(
        [string] $RepositoryRoot,
        [string] $DataRoot,
        [string] $StatePath,
        [object] $Config,
        [Alias('Clock')] [scriptblock] $NowProvider,
        [Alias('SessionProvider')] [scriptblock] $SessionProbe,
        [scriptblock] $LockProvider,
        [scriptblock] $UnlockProvider,
        [scriptblock] $DialogProvider,
        [scriptblock] $DeployAction,
        [scriptblock] $StateReader,
        [scriptblock] $StateWriter
    )

    if ($null -eq $NowProvider) { $NowProvider = { [DateTime]::UtcNow } }
    if ($null -eq $SessionProbe) { $SessionProbe = { Test-CodexInteractiveSession } }
    if ($null -eq $Config) { $Config = [pscustomobject]@{} }
    $countdownSeconds = [int](Get-CodexNotificationValue $Config 'notificationSeconds' 10)
    $snoozeMinutes = [int](Get-CodexNotificationValue $Config 'snoozeMinutes' 5)
    if ($snoozeMinutes -le 0) { throw 'snoozeMinutes must be positive.' }

    if ([string]::IsNullOrWhiteSpace($StatePath)) {
        if (-not [string]::IsNullOrWhiteSpace($RepositoryRoot)) {
            $paths = Resolve-CodexWorkerPaths -RepositoryRoot $RepositoryRoot -DataRoot $DataRoot
            $StatePath = $paths.StatePath
        } else {
            $StatePath = Join-Path (Get-Location).Path 'state.json'
        }
    } else {
        $StatePath = [IO.Path]::GetFullPath($StatePath)
    }
    $lockPath = Join-Path (Split-Path -Parent $StatePath) 'worker.lock'
    if (-not [string]::IsNullOrWhiteSpace($RepositoryRoot)) {
        try { $lockPath = (Resolve-CodexWorkerPaths -RepositoryRoot $RepositoryRoot -DataRoot $DataRoot).LockPath } catch { if ($null -eq $LockProvider) { throw } }
    }

    $read = if ($null -ne $StateReader) { $StateReader } else { { param($Path) Read-CodexWorkerState -Path $Path } }
    $state = & $read $StatePath
    $deployment = Get-CodexNotificationValue $state 'deployment' $null
    if ($null -eq $deployment) { return [pscustomobject]@{ Status = 'Idle'; Decision = $null } }
    $now = ([DateTime](& $NowProvider)).ToUniversalTime()
    try { $due = Test-CodexNotificationDue -Deployment $deployment -Now $now } catch { return [pscustomobject]@{ Status = 'InvalidState'; Error = $_.Exception.Message } }
    if (-not $due) { return [pscustomobject]@{ Status = [string](Get-CodexNotificationValue $deployment 'status' 'snoozed'); Decision = $null } }

    if (-not [bool](& $SessionProbe)) { return [pscustomobject]@{ Status = 'SessionUnavailable'; Decision = $null } }

    $lock = $null
    try {
        try {
            if ($null -ne $LockProvider) { $lock = & $LockProvider $lockPath }
            else { $lock = Enter-CodexWorkerLock -Path $lockPath -TimeoutSeconds 0 }
        } catch {
            if ($_.Exception.Message -match '(?i)lock.*busy') {
                return [pscustomobject]@{ Status = 'LockBusy'; Decision = $null }
            }
            throw
        }
        if ($null -eq $lock) { return [pscustomobject]@{ Status = 'LockBusy'; Decision = $null } }

        # Re-read after acquiring the lock so a concurrent close or notifier
        # cannot cause a stale dialog decision to mutate newer state.
        $state = & $read $StatePath
        $deployment = Get-CodexNotificationValue $state 'deployment' $null
        $now = ([DateTime](& $NowProvider)).ToUniversalTime()
        if ($null -eq $deployment) { return [pscustomobject]@{ Status = 'Idle'; Decision = $null } }
        try { $due = Test-CodexNotificationDue -Deployment $deployment -Now $now } catch { return [pscustomobject]@{ Status = 'InvalidState'; Error = $_.Exception.Message } }
        if (-not $due) { return [pscustomobject]@{ Status = [string](Get-CodexNotificationValue $deployment 'status' 'snoozed'); Decision = $null } }
        if (-not [bool](& $SessionProbe)) { return [pscustomobject]@{ Status = 'SessionUnavailable'; Decision = $null } }

        if ($null -eq $DialogProvider) {
            $DialogProvider = { param($Pending, $Seconds, $At) Show-CodexDeploymentDialog -Seconds $Seconds }
        }
        $response = & $DialogProvider (Copy-CodexNotificationObject -Object $deployment) $countdownSeconds $now
        if ($null -eq $response) {
            $decision = 'Deploy'
        } else {
            $decisionProperty = $response.PSObject.Properties['Decision']
            if ($null -ne $decisionProperty) { $decision = [string]$decisionProperty.Value }
            else { $decision = ([string]$response).Trim() }
        }
        if ($decision -in @('Close', 'Closed', 'WindowClosed')) { $decision = 'Later' }
        if ($decision -notin @('Deploy', 'Later', 'Cancel')) { throw "Unknown deployment notification decision '$decision'." }

        if ($decision -eq 'Later') {
            $desired = Copy-CodexNotificationObject -Object $state
            $desired.deployment.snoozeUntil = $now.AddMinutes($snoozeMinutes).ToUniversalTime().ToString('o')
            $desired.deployment.status = 'snoozed'
            Write-CodexNotificationStateVerified -StatePath $StatePath -DesiredState $desired -ExpectedDeployment $desired.deployment -StateWriter $StateWriter -StateReader $StateReader | Out-Null
            return [pscustomobject]@{ Status = 'Snoozed'; Decision = 'Later'; SnoozeUntil = $desired.deployment.snoozeUntil }
        }

        if ($decision -eq 'Cancel') {
            $desired = Copy-CodexNotificationObject -Object $state
            $desired.deployment = $null
            Write-CodexNotificationStateVerified -StatePath $StatePath -DesiredState $desired -ExpectedDeployment $null -StateWriter $StateWriter -StateReader $StateReader | Out-Null
            return [pscustomobject]@{ Status = 'Cancelled'; Decision = 'Cancel' }
        }

        if ($null -eq $DeployAction) { throw 'No deployment action was supplied.' }
        $deployResult = & $DeployAction (Copy-CodexNotificationObject -Object $deployment)
        if ($deployResult -is [bool] -and -not $deployResult) { throw 'The deployment action reported failure.' }
        $desired = Copy-CodexNotificationObject -Object $state
        $desired.deployment = $null
        Write-CodexNotificationStateVerified -StatePath $StatePath -DesiredState $desired -ExpectedDeployment $null -StateWriter $StateWriter -StateReader $StateReader | Out-Null
        return [pscustomobject]@{ Status = 'Deployed'; Decision = 'Deploy' }
    } finally {
        if ($null -ne $lock) {
            if ($null -ne $UnlockProvider) { & $UnlockProvider $lock }
            else { Exit-CodexWorkerLock -Handle $lock }
        }
    }
}

function Invoke-CodexDeploymentNotifier {
    [CmdletBinding()]
    param(
        [switch] $Watch,
        [string] $RepositoryRoot,
        [string] $DataRoot,
        [string] $StatePath,
        [object] $Config,
        [Alias('Clock')] [scriptblock] $NowProvider,
        [Alias('SessionProvider')] [scriptblock] $SessionProbe,
        [scriptblock] $LockProvider,
        [scriptblock] $UnlockProvider,
        [scriptblock] $DialogProvider,
        [scriptblock] $DeployAction,
        [scriptblock] $StateReader,
        [scriptblock] $StateWriter,
        [scriptblock] $SleepProvider,
        [int] $PollSeconds = 5,
        [int] $MaxCycles = 0
    )

    $cycleParameters = @{}
    foreach ($name in @('RepositoryRoot', 'DataRoot', 'StatePath', 'Config', 'NowProvider', 'SessionProbe', 'LockProvider', 'UnlockProvider', 'DialogProvider', 'DeployAction', 'StateReader', 'StateWriter')) {
        if ($PSBoundParameters.ContainsKey($name)) { $cycleParameters[$name] = $PSBoundParameters[$name] }
    }
    if (-not $Watch) {
        return Invoke-CodexDeploymentNotificationCycle @cycleParameters
    }
    if ($PollSeconds -le 0) { throw 'PollSeconds must be positive.' }
    $cycle = 0
    $results = New-Object 'System.Collections.Generic.List[object]'
    while ($true) {
        $cycle++
        try {
            $cycleResult = Invoke-CodexDeploymentNotificationCycle @cycleParameters
        } catch {
            Write-Warning "Deployment notifier cycle failed: $($_.Exception.Message)"
            $cycleResult = [pscustomobject]@{ Status = 'Failed'; Error = $_.Exception.Message; Decision = $null }
        }
        $results.Add($cycleResult) | Out-Null
        if ($MaxCycles -gt 0 -and $cycle -ge $MaxCycles) { break }
        if ($null -ne $SleepProvider) { & $SleepProvider ([TimeSpan]::FromSeconds($PollSeconds)) }
        else { Start-Sleep -Seconds $PollSeconds }
    }
    return $results.ToArray()
}
