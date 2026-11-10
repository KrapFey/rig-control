# Drives the UI-test build (no admin) through UI Automation and screenshots it.
#   ui.ps1 -Page Fans -Out fans.png       select a nav item, then screenshot
#   ui.ps1 -Click "Import FanControl setup"  invoke a button by name
param([string]$Page, [string]$Click, [string]$Out = "$env:TEMP\rigcontrol-ui.png", [int]$Wait = 1500)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$exe = Join-Path $PSScriptRoot '..\src\RigControl\bin-uitest\Debug\net10.0-windows10.0.26100.0\win-x64\RigControl.exe' | Resolve-Path
$p = Get-Process RigControl -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq "$exe" } | Select-Object -First 1
if (-not $p) { $p = Start-Process $exe -PassThru; Start-Sleep 6; $p.Refresh() }
$deadline = (Get-Date).AddSeconds(15)
while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300; $p.Refresh() }
$root = [Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)

function Find($name) {
    $c = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $name)
    $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $c)
}

if ($Page) {
    $el = Find $Page
    if (-not $el) { throw "nav item '$Page' not found" }
    $sel = $null
    if ($el.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sel)) { $sel.Select() }
    else { $el.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
}
if ($Click) {
    $el = Find $Click
    if (-not $el) { throw "'$Click' not found" }
    $el.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
Start-Sleep -Milliseconds $Wait
& (Join-Path $PSScriptRoot 'shot.ps1') -Id $p.Id -Out $Out
