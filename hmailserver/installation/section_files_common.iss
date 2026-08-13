[Files]
Source: isxdl.dll; DestDir: {tmp}; Flags: dontcopy
Source: "License.rtf"; DestDir: "{app}\Bin"; Flags: ignoreversion; Components: server admintools;

; 3'rd party dependencies
; atl70.dll is the ATL 7.0 runtime from Visual Studio .NET 2003 and is shared:
; onlyifdoesntexist stops an hMailServer install from downgrading a copy another
; product put there, and uninsneveruninstall stops an hMailServer *uninstall* from
; deleting a System32 DLL out from under that other product, which is what happened
; before - the file was logged on install like any other and removed on uninstall.
; (It also looks vestigial on a v145 build; see the packaging notes.)
Source: "System files\ATL\atl70.dll"; DestDir: "{sys}"; Flags: onlyifdoesntexist uninsneveruninstall; Components: server;
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

Source: ISC.dll; Flags: dontcopy
Source: ".\Extras\tlds.txt"; DestDir: "{app}\Bin";  Flags: ignoreversion; Components: server;
Source: ".\Extras\dh2048.pem"; DestDir: "{app}\Bin";  Flags: ignoreversion; Components: server;

; MySQL/MariaDB client library (x64) - MariaDB Connector/C, loaded at runtime as libmysql.dll
; when the database type is MySQL/MariaDB. Talks to both MySQL and MariaDB servers.
Source: ".\Extras\libmysql.dll"; DestDir: "{app}\Bin"; Flags: ignoreversion; Components: server;
; Client authentication plugins, found via MYSQL_PLUGIN_DIR = {app}\Bin\plugin. These let the
; single bundled client authenticate every common account type: caching_sha2_password (MySQL 8.0+
; default), sha256_password, client_ed25519 / auth_gssapi_client / parsec (MariaDB), dialog, etc.
Source: ".\Extras\plugin\*.dll"; DestDir: "{app}\Bin\plugin"; Flags: ignoreversion; Components: server;
