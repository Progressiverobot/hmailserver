[Files]
; Main server
Source: "..\source\server\hMailServer\x64\Release\hMailServer.exe"; DestDir: "{app}\Bin"; Flags: ignoreversion; Components: server admintools;
Source: "..\source\server\hMailServer\x64\Release\hMailServer.tlb"; DestDir: "{app}\Bin"; Flags: ignoreversion; Components: server admintools;
Source: "..\source\server\hMailServer\x64\Release\hMailServer.Minidump.exe"; DestDir: "{app}\Bin"; Flags: ignoreversion; Components: server;
; Visual C++ runtime matching the build toolset (v145). Shipping an older
; app-local msvcp140 than the toolset the binaries were built with crashes
; the service on startup (e.g. constexpr std::mutex changes).
Source: "Microsoft.VC145.CRT\*"; DestDir: "{app}\Bin"; Flags: ignoreversion; Components: server admintools;

; Web administration SPA (served by the REST API listener at GET /)
Source: "WebAdmin\index.html"; DestDir: "{app}\WebAdmin"; Flags: ignoreversion; Components: server;

; hMailServer Control Panel (modern .NET 10 WPF admin app)
Source: "..\source\Tools\ControlPanel\publish\*"; DestDir: "{app}\ControlPanel"; Flags: ignoreversion recursesubdirs; Components: controlpanel;
; .NET {#DOTNET_MAJOR} Desktop Runtime, installed silently when missing. The server
; component needs it too: DBSetup/DBSetupQuick/DBUpdater are .NET {#DOTNET_MAJOR}
; apps that run during installation (from [Code], before [Run] executes - see
; InstallDotNetRuntime in hMailServerInnoExtension.iss).
; (file is downloaded by build\get-dotnet-runtime.ps1; not in the repository)
; The filename comes from DOTNET_RUNTIME_FILE in hMailServer64.iss so that this
; entry, the [Run] entry and the installed-version probe cannot name different
; versions - they already had.
Source: "DotNet\{#DOTNET_RUNTIME_FILE}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Components: server controlpanel;

Source: "SQLCE\SSCERuntime_x64-ENU.msi"; Flags: deleteafterinstall ; Excludes: ".svn"; DestDir: "{tmp}"; Components: server;

; Common tools (.NET 10, framework-dependent dotnet publish output; built by
; build\build-tools.ps1). The three database tools share {app}\Bin - their
; publish folders carry identical copies of Shared.dll and the checked-in
; Interop.hMailServer.dll tlbimp wrapper (source\Tools\Interop), which
; external .NET scripts may also reference from {app}\Bin.
; The classic hMailServer Administrator GUI has been removed - the Control
; Panel is the sole bundled GUI.
Source: "..\source\Tools\DBUpdater\publish\*"; DestDir: "{app}\Bin"; Flags: ignoreversion recursesubdirs; Components: server;
Source: "..\source\Tools\DBSetup\publish\*"; DestDir: "{app}\Bin"; Flags: ignoreversion recursesubdirs; Components: server;
Source: "..\source\Tools\DBSetupQuick\publish\*"; DestDir: "{app}\Bin"; Flags: ignoreversion recursesubdirs; Components: server;
Source: "..\source\Tools\Interop\Interop.hMailServer.dll"; DestDir: "{app}\Bin"; Flags: ignoreversion; Components: server admintools;

; Data directory synchronizer
Source: "..\source\Tools\DataDirectorySynchronizer\publish\*"; DestDir: "{app}\Addons\DataDirectorySynchronizer"; Flags: ignoreversion recursesubdirs; Components: server;

; Import tool (accounts from text files, messages from mbox files)
Source: "..\source\Tools\ImportTool\publish\*"; DestDir: "{app}\Addons\ImportTool"; Flags: ignoreversion recursesubdirs; Components: server;

; OpenSSL
Source: "{#OPENSSL_LIBS_PATH}\libcrypto-4-x64.dll"; DestDir: "{app}\Bin"; Flags: ignoreversion; Components: server admintools;
Source: "{#OPENSSL_LIBS_PATH}\libssl-4-x64.dll"; DestDir: "{app}\Bin"; Flags: ignoreversion; Components: server admintools;

; PQSQL (PostgreSQL client)
Source: "{#POSTGRESQL_LIBPQ_PATH}\*.dll"; DestDir: "{app}\Bin"; Flags: ignoreversion; Components: server admintools;