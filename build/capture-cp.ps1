# Captures the running hMailCP window after optionally navigating via UIA.
# Usage: capture-cp.ps1 -Out C:\Dev\shot.png [-Nav "Anti-spam"] [-Launch]
param(
    [string]$Out = 'C:\Dev\cp-shot.png',
    [string]$Nav = '',
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing @'
using System;using System.Runtime.InteropServices;using System.Drawing;
public static class CpAuto{
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint flags);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f,uint dx,uint dy,uint data,UIntPtr extra);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
public struct RECT{public int Left,Top,Right,Bottom;}
public static void ClickAt(IntPtr win,int relX,int relY){RECT r;GetWindowRect(win,out r);SetForegroundWindow(win);System.Threading.Thread.Sleep(400);SetCursorPos(r.Left+relX,r.Top+relY);System.Threading.Thread.Sleep(150);mouse_event(2,0,0,0,UIntPtr.Zero);mouse_event(4,0,0,0,UIntPtr.Zero);}
public static Bitmap Shoot(IntPtr h){RECT r;GetWindowRect(h,out r);int w=r.Right-r.Left,ht=r.Bottom-r.Top;Bitmap b=new Bitmap(w,ht);using(Graphics g=Graphics.FromImage(b)){IntPtr dc=g.GetHdc();PrintWindow(h,dc,2);g.ReleaseHdc(dc);}return b;}}
'@

if ($Launch) {
    Stop-Process -Name hMailCP -Force -ErrorAction SilentlyContinue
    Start-Sleep 1
    $exe = Join-Path $PSScriptRoot '..\hmailserver\source\Tools\ControlPanel\bin\Release\net8.0-windows\hMailCP.exe'
    Start-Process $exe -ArgumentList '/connect','localhost','Administrator','testar' | Out-Null
    Start-Sleep 10
}

$p = Get-Process hMailCP -ErrorAction Stop | Select-Object -First 1

if ($Nav) {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Nav)
    $item = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if (-not $item) { throw "Nav item '$Nav' not found." }

    $pattern = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pattern.Select()
    Start-Sleep 3
}

$b = [CpAuto]::Shoot($p.MainWindowHandle)
$b.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$b.Dispose()
Write-Host "captured $Out"
