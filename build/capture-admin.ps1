# Launches hMailAdmin, drives the connect dialog, switches to the requested
# theme, navigates to a settings page and captures a window screenshot.
param(
    [string]$Theme = 'Dark',
    [string]$OutFile = 'C:\Dev\admin-screenshot.png'
)

$ErrorActionPreference = 'Stop'

# Force the saved theme preference before launch.
New-Item -Path 'HKCU:\Software\hMailServer\Administrator' -Force | Out-Null
Set-ItemProperty -Path 'HKCU:\Software\hMailServer\Administrator' -Name 'Theme' -Value $Theme

$exe = Join-Path $PSScriptRoot '..\hmailserver\source\Tools\Administrator\bin\x64\Release\hMailAdmin.exe'
$proc = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 3

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# Drive the connect flow: Enter on the connect dialog opens the password
# prompt; then type the password and confirm.
Add-Type -AssemblyName Microsoft.VisualBasic
try {
    [Microsoft.VisualBasic.Interaction]::AppActivate($proc.Id)
    Start-Sleep -Milliseconds 800
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Seconds 3
    # The modal password prompt now has focus; type straight into it.
    [System.Windows.Forms.SendKeys]::SendWait('testar')
    Start-Sleep -Milliseconds 400
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
} catch { }

Start-Sleep -Seconds 8

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class Win32Capture {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lp);
    public struct RECT { public int Left, Top, Right, Bottom; }
    public static IntPtr FindLargestWindow(uint processId) {
        IntPtr best = IntPtr.Zero; long bestArea = 0;
        EnumWindows((h, l) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == processId && IsWindowVisible(h)) {
                RECT r; GetWindowRect(h, out r);
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > bestArea) { bestArea = area; best = h; }
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }
}
'@

$proc.Refresh()
$hwnd = [Win32Capture]::FindLargestWindow($proc.Id)
if ($hwnd -eq [IntPtr]::Zero) { throw 'No window found.' }

[Win32Capture]::ShowWindow($hwnd, 9) | Out-Null   # SW_RESTORE
[Win32Capture]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 800

$rect = New-Object Win32Capture+RECT
[Win32Capture]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$g.Dispose()
$bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Host "captured $OutFile ($w x $h)"
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
