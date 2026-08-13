@echo off
rem Developer convenience: removes an installed hMailServer from a test machine.
rem
rem This is NOT shipped by the installer and must never be - the second half wipes
rem the installation directory, which is where Data (every message), Database and
rem Logs live. The uninstaller deliberately leaves those behind; this deletes them.
rem
rem It used to do both steps unconditionally, against a hard-coded
rem "C:\Program Files\hMailServer\unins000.exe". Two problems with that: the
rem uninstaller is unins001.exe (002, ...) whenever one was already present, so the
rem /SILENT step could silently do nothing and the RMDIR would then destroy the mail
rem store of a still-installed server; and on a machine installed anywhere else it
rem deleted a directory it had never uninstalled from.
rem
rem Usage:
rem   "Uninstall current version.bat"                 uninstall only
rem   "Uninstall current version.bat" /DELETEMAILDATA uninstall, then delete the tree

setlocal

set "HMKEY=HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\hMailServer_is1"
set "HMDIR="

for /f "tokens=2,*" %%a in ('reg query "%HMKEY%" /v InstallLocation /reg:32 2^>nul ^| findstr /i InstallLocation') do set "HMDIR=%%b"

if not defined HMDIR (
   echo hMailServer does not appear to be installed - no InstallLocation under
   echo %HMKEY%.
   exit /b 1
)

rem Inno writes InstallLocation with a trailing backslash.
if "%HMDIR:~-1%"=="\" set "HMDIR=%HMDIR:~0,-1%"

set "HMUNINST="
for %%f in ("%HMDIR%\unins*.exe") do set "HMUNINST=%%~ff"

if not defined HMUNINST (
   echo No uninstaller found in "%HMDIR%".
   exit /b 1
)

echo Uninstalling using "%HMUNINST%"...
"%HMUNINST%" /SILENT
if errorlevel 1 (
   echo The uninstaller returned %errorlevel%; leaving "%HMDIR%" alone.
   exit /b %errorlevel%
)

if /i not "%~1"=="/DELETEMAILDATA" (
   echo Uninstalled. "%HMDIR%" has been left in place - it still holds Data,
   echo Database and Logs. Pass /DELETEMAILDATA to delete it as well.
   exit /b 0
)

echo Deleting "%HMDIR%" including all mail data...
rmdir "%HMDIR%" /S /Q
exit /b 0
