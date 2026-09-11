[Files]
Source: "License.rtf"; DestDir: "{app}\Bin"; Flags: ignoreversion; Components: server admintools;

; 3'rd party dependencies. None of these is in git: build\get-installer-binaries.ps1
; fetches or gathers each one and verifies it against hmailserver\docs\third-party-binaries.json,
; and the installer smoke test proves the result installs. atl70.dll (ATL 7.0, Visual
; Studio .NET 2003) is no longer shipped: nothing on a v145 build links it, and the
; smoke test on a clean runner is what says so.
Source: ".\Extras\7za.exe"; DestDir: "{app}\Bin"; Flags: ignoreversion; Components: server;

; Database scripts
Source: "..\source\DBScripts\*.sql"; DestDir: "{app}\DBScripts";Flags: ignoreversion recursesubdirs; Components: server;

; onlyifdoesntexist keeps an upgrade from touching user-modified files, and
; uninsneveruninstall keeps the uninstaller from deleting them: without it the
; uninstall log entry recorded on first install removes the administrator's
; customized EventHandlers.vbs (and addon scripts) on uninstall/reinstall.
Source: "..\source\Addons\*.*"; DestDir: "{app}\Addons"; Flags: onlyifdoesntexist recursesubdirs uninsneveruninstall; Excludes: "Events";  Components: server;
Source: "..\source\Addons\Events\*.*"; DestDir: "{app}\Events"; Flags: onlyifdoesntexist uninsneveruninstall; Components: server;

Source: "..\source\Translations\*"; Excludes: "CVS,.cvsignore,.#*"; DestDir: "{app}\Languages"; Components: server admintools;

Source: ".\Extras\tlds.txt"; DestDir: "{app}\Bin";  Flags: ignoreversion; Components: server;
Source: ".\Extras\dh2048.pem"; DestDir: "{app}\Bin";  Flags: ignoreversion; Components: server;

; MySQL/MariaDB client library (x64) - MariaDB Connector/C, loaded at runtime as libmysql.dll
; when the database type is MySQL/MariaDB. Talks to both MySQL and MariaDB servers.
Source: ".\Extras\libmysql.dll"; DestDir: "{app}\Bin"; Flags: ignoreversion; Components: server;
; Client authentication plugins, found via MYSQL_PLUGIN_DIR = {app}\Bin\plugin. These let the
; single bundled client authenticate every common account type: caching_sha2_password (MySQL 8.0+
; default), sha256_password, client_ed25519 / auth_gssapi_client / parsec (MariaDB), dialog, etc.
Source: ".\Extras\plugin\*.dll"; DestDir: "{app}\Bin\plugin"; Flags: ignoreversion; Components: server;
