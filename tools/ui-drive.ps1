<#
.SYNOPSIS
  Minimal UI Automation driver for PixelBridge smoke tests.
  -Nav <index> selects a sidebar entry (Nav list), -Footer <index> a footer entry, -Click invokes buttons by visible name.
#>
param([int]$Nav = -1, [int]$Footer = -1, [string[]]$Click = @(), [int]$WaitMs = 500)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]
$root = $A::RootElement
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition $A::NameProperty, 'PixelBridge'))
if (-not $win) { throw 'PixelBridge window not found' }
function ById($id) { $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition $A::AutomationIdProperty, $id)) }
function SelectIndex($listId, $i) {
    $list = ById $listId
    if (-not $list) { throw "list $listId not found" }
    $items = $list.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition $A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))
    $items[$i].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 700
}
if ($Nav -ge 0) { SelectIndex 'Nav' $Nav }
if ($Footer -ge 0) { SelectIndex 'FooterNav' $Footer }
foreach ($b in $Click) {
    $e = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition $A::NameProperty, $b))
    if (-not $e) { throw "control '$b' not found" }
    $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds $WaitMs
}
Write-Host 'ok'
