# Opens the Domain Properties dialog in the running hMailCP and screenshots it.
param(
    [string]$Out = 'C:\Dev\cp-domain-dialog.png',
    [string]$ButtonName = 'Properties',
    [string]$DialogTitlePart = 'Domain -',
    [string]$SelectTab = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -ReferencedAssemblies System.Drawing @'
using System;using System.Runtime.InteropServices;using System.Drawing;
public static class CpShot{
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint flags);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
public struct RECT{public int Left,Top,Right,Bottom;}
public static Bitmap Shoot(IntPtr h){SetForegroundWindow(h);System.Threading.Thread.Sleep(300);RECT r;GetWindowRect(h,out r);int w=r.Right-r.Left,ht=r.Bottom-r.Top;Bitmap b=new Bitmap(w,ht);using(Graphics g=Graphics.FromImage(b)){IntPtr dc=g.GetHdc();PrintWindow(h,dc,2);g.ReleaseHdc(dc);}return b;}}
'@

$p = Get-Process hMailCP -ErrorAction Stop | Select-Object -First 1
$auto = [System.Windows.Automation.AutomationElement]
$scope = [System.Windows.Automation.TreeScope]::Descendants
$root = $auto::FromHandle($p.MainWindowHandle)

# Find and invoke the button
$cond = New-Object System.Windows.Automation.PropertyCondition($auto::NameProperty, $ButtonName)
$btn = $root.FindFirst($scope, $cond)
if (-not $btn) { throw "Button '$ButtonName' not found." }
$inv = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
$inv.Invoke()
Start-Sleep 2

# Find the dialog window by title substring
$desktop = $auto::RootElement
$wins = $desktop.FindAll($scope,
    (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Window)))
$dlg = $null
foreach ($w in $wins) {
    if ($w.Current.Name -like "*$DialogTitlePart*") { $dlg = $w; break }
}
if (-not $dlg) { throw "Dialog '$DialogTitlePart' not found." }

if ($SelectTab) {
    $tab = $dlg.FindFirst($scope,
        (New-Object System.Windows.Automation.PropertyCondition($auto::NameProperty, $SelectTab)))
    if ($tab) {
        $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 700
    }
}

$handle = [IntPtr]$dlg.Current.NativeWindowHandle
$b = [CpShot]::Shoot($handle)
$b.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$b.Dispose()
Write-Host "captured $Out"
