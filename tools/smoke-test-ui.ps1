<#
    Drives the built application end to end through UI Automation: open the picker, pick a
    process that is playing audio, start isolation, and verify from outside the app that
    Live Captions really did get pinned to the isolation endpoint.

    The picker is an owned modal window, which UI Automation does not always expose as a child
    of the desktop, so it is located by one of its own controls rather than by window title.
#>

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$root = Split-Path $PSScriptRoot -Parent
$binDir = Join-Path $root 'bin\Release\net8.0-windows'
$exe = Join-Path $binDir 'LiveCaptionsTranslator.exe'
$probe = Join-Path $root 'tools\AudioRouteProbe\bin\Release\net8.0-windows\AudioRouteProbe.exe'
if (-not (Test-Path $exe)) { throw "Not built: $exe" }

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

function Wait-ForAutomationId($scopeRoot, $id, $timeoutSec = 45) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $found = $scopeRoot.FindFirst($TS::Descendants, $condition)
        if ($found) { return $found }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function Get-MainWindow($processId, $timeoutSec = 40) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $processId)
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        foreach ($w in $AE::RootElement.FindAll($TS::Children, $condition)) {
            if ($w.Current.Name -eq 'LiveCaptions Translator') { return $w }
        }
        Start-Sleep -Milliseconds 400
    }
    return $null
}

Remove-Item (Join-Path $binDir 'crash.log') -ErrorAction SilentlyContinue

$app = Start-Process $exe -WorkingDirectory $binDir -PassThru
Write-Output "launched pid $($app.Id)"
$closedGracefully = $false

try {
    $main = Get-MainWindow $app.Id
    if (-not $main) { throw 'Main window never appeared.' }
    Write-Output "PASS  main window is up"

    $button = Wait-ForAutomationId $main 'WindowIsolationButton' 20
    if (-not $button) { throw 'Toolbar button not found.' }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Output 'PASS  clicked the toolbar button'

    # Locate the picker by a control that only it has. Searching from the desktop root also
    # covers the case where the modal is not parented under the main window.
    $combo = Wait-ForAutomationId $AE::RootElement 'EndpointComboBox' 45
    if (-not $combo) { throw 'Picker did not open (EndpointComboBox never appeared).' }
    Write-Output 'PASS  picker is open'

    # Searching from the desktop root is what reliably reaches this owned modal; walking up to
    # its window element is not dependable across UIA providers.
    $picker = $AE::RootElement

    # Enumeration runs on a worker thread, so the controls exist before they are populated.
    $selection = $combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)
    $selected = @()
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and $selected.Count -eq 0) {
        $selected = $selection.Current.GetSelection()
        if ($selected.Count -eq 0) { Start-Sleep -Milliseconds 500 }
    }
    if ($selected.Count -eq 0) { throw 'No isolation device was preselected within 60s.' }
    Write-Output "PASS  isolation device preselected: '$($selected[0].Current.Name)'"

    # The "Playing audio" tab has to be selected first: WPF does not realise a TabItem's
    # content until the tab is shown, so SessionList does not exist in the tree before this.
    $tabs = Wait-ForAutomationId $picker 'SourceTabs' 15
    if (-not $tabs) { throw 'SourceTabs not found.' }
    $tabItems = $tabs.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)
    if ($tabItems.Count -lt 2) { throw "Expected 2 tabs, found $($tabItems.Count)." }
    $tabItems[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Write-Output "PASS  switched to the '$($tabItems[1].Current.Name)' tab"

    $sessionList = Wait-ForAutomationId $picker 'SessionList' 20
    if (-not $sessionList) { throw 'SessionList not found.' }

    $items = @()
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline -and $items.Count -eq 0) {
        $items = $sessionList.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)
        if ($items.Count -eq 0) { Start-Sleep -Milliseconds 500 }
    }
    Write-Output "PASS  session list populated with $($items.Count) entries"

    if ($items.Count -eq 0) {
        Write-Output 'SKIP  nothing is playing audio, cannot exercise Start'
    }
    else {
        for ($i = 0; $i -lt [Math]::Min(5, $items.Count); $i++) {
            Write-Output "        - $($items[$i].Current.Name)"
        }

        $items[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Write-Output "selected: $($items[0].Current.Name)"

        $start = Wait-ForAutomationId $picker 'StartButton' 10
        if (-not $start) { throw 'StartButton not found.' }
        $start.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Write-Output 'clicked Start; waiting for Live Captions to be repinned...'

        # Verified from outside the app: the override is real system state, not a UI claim.
        $pinned = $null
        $deadline = (Get-Date).AddSeconds(45)
        while ((Get-Date) -lt $deadline -and -not $pinned) {
            if (-not (Get-Process -Id $app.Id -ErrorAction SilentlyContinue)) { throw 'App died during Start.' }
            $lc = Get-Process LiveCaptions -ErrorAction SilentlyContinue
            if ($lc) {
                $out = & $probe get $lc[0].Id 2>&1 | Out-String
                if ($out -match 'pinned to (.+)') { $pinned = $Matches[1].Trim() }
            }
            Start-Sleep -Milliseconds 1000
        }
        if ($pinned) {
            Write-Output "PASS  Live Captions is pinned to: $pinned"
        } else {
            Write-Output 'FAIL  Live Captions was never pinned'
        }

        # A picker still on screen is a modal child, and its owner cannot close underneath it,
        # which would look identical to the app refusing to shut down.
        Start-Sleep -Seconds 2
        $stillOpen = $AE::RootElement.FindFirst($TS::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'EndpointComboBox')))
        if ($stillOpen) {
            Write-Output 'FAIL  picker did not close itself after starting isolation'
        } else {
            Write-Output 'PASS  picker closed itself'
        }
    }
}
catch {
    Write-Output "FAIL  $($_.Exception.Message)"
}
finally {
    # Close gracefully so the app's exit handler releases the override.
    try {
        $main = Get-MainWindow $app.Id 5
        if ($main) {
            $main.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
            $closedGracefully = $true
        }
    } catch { }

    # Generous: shutdown stops the capture thread and undoes the endpoint override, and a
    # premature kill here would look exactly like the app failing to clean up after itself.
    $exitWatch = [Diagnostics.Stopwatch]::StartNew()
    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline -and (Get-Process -Id $app.Id -ErrorAction SilentlyContinue)) {
        Start-Sleep -Milliseconds 500
    }
    $stillRunning = [bool](Get-Process -Id $app.Id -ErrorAction SilentlyContinue)
    Write-Output "exit took $([int]$exitWatch.Elapsed.TotalSeconds)s; force-killed: $stillRunning"
    Get-Process -Id $app.Id -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 1500

    $lc = Get-Process LiveCaptions -ErrorAction SilentlyContinue
    if ($lc) {
        Write-Output "FAIL  after exit: $($lc.Count) orphaned LiveCaptions process(es) left running"
        Write-Output "      $(& $probe get $lc[0].Id 2>&1 | Out-String)".Trim()
        foreach ($p in $lc) { & $probe clear $p.Id | Out-Null }
        $lc | Stop-Process -Force -ErrorAction SilentlyContinue
    } else {
        Write-Output 'PASS  after exit: no orphaned LiveCaptions process'
    }

    $crash = Join-Path $binDir 'crash.log'
    if (Test-Path $crash) {
        Write-Output '=== crash.log ==='
        Get-Content $crash | Select-Object -First 40
    } else {
        Write-Output 'crash.log: none'
    }
    Write-Output "graceful close: $closedGracefully"
    Write-Output 'cleaned up'
}
