@echo off
setlocal

set HMS_LIBS=%~1
set OUT_DIR=%~2
set TARGET=%~3
set INT_DIR=%~4
set SCRIPT_DIR=%~dp0

NET STOP hMailServer

xcopy /F /Y "%HMS_LIBS%\openssl-4.0.2\out64\bin\libcrypto-4-x64.dll" "%OUT_DIR%"
if errorlevel 1 exit /b 1

xcopy /F /Y "%HMS_LIBS%\openssl-4.0.2\out64\bin\libssl-4-x64.dll" "%OUT_DIR%"
if errorlevel 1 exit /b 1

xcopy /F /Y "%HMS_LIBS%\postgresql-18.3\builddir\src\interfaces\libpq\*.dll" "%OUT_DIR%"
if errorlevel 1 exit /b 1

rem MySQL/MariaDB client (MariaDB Connector/C, shipped as libmysql.dll) + its auth
rem plugins, vendored in the repo. Copied here so MySQL/MariaDB regression tests can
rem load the same client the installer ships.
set MARIADB_CONNECTOR=%SCRIPT_DIR%..\..\..\..\libraries\mariadb-connector-c-3.4.9
xcopy /F /Y "%MARIADB_CONNECTOR%\libmysql.dll" "%OUT_DIR%"
if errorlevel 1 exit /b 1

if not exist "%OUT_DIR%plugin\" mkdir "%OUT_DIR%plugin"
xcopy /F /Y "%MARIADB_CONNECTOR%\plugin\*.dll" "%OUT_DIR%plugin\"
if errorlevel 1 exit /b 1

rem MIDL generates the type library into the intermediate directory, but the
rem installer ships it from the output directory. Without this copy the
rem installer cannot be built from a clean checkout.
if not "%INT_DIR%"=="" (
   xcopy /F /Y "%INT_DIR%hMailServer.tlb" "%OUT_DIR%"
   if errorlevel 1 exit /b 1
)

"%TARGET%" /Register
if errorlevel 1 exit /b 1
