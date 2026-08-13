[INI]
Filename: "{app}\Bin\hMailServer.INI"; Section: "Directories"; Key: "ProgramFolder"; String: "{app}";
Filename: "{app}\Bin\hMailServer.INI"; Section: "Directories"; Key: "DatabaseFolder"; String: "{app}\Database";  Flags: createkeyifdoesntexist; Components: server;
Filename: "{app}\Bin\hMailServer.INI"; Section: "Directories"; Key: "DataFolder"; String: "{app}\Data";  Flags: createkeyifdoesntexist; Components: server;
Filename: "{app}\Bin\hMailServer.INI"; Section: "Directories"; Key: "LogFolder"; String: "{app}\Logs"; Flags: createkeyifdoesntexist; Components: server;
Filename: "{app}\Bin\hMailServer.INI"; Section: "Directories"; Key: "TempFolder"; String: "{app}\Temp"; Flags: createkeyifdoesntexist; Components: server;
Filename: "{app}\Bin\hMailServer.INI"; Section: "Directories"; Key: "EventFolder"; String: "{app}\Events"; Flags: createkeyifdoesntexist; Components: server;

; Languages
Filename: "{app}\Bin\hMailServer.INI"; Section: "GUILanguages"; Key: "ValidLanguages"; String: "english,swedish";
; Check: HasAdministratorPassword is what keeps an installation that never collected
; a password from writing the MD5 of the empty string here - a value the server
; accepts an empty password against, while looking configured to everything that
; tests for "unset". Silent installs can supply one with /adminpassword=<password>.
; See HasAdministratorPassword in hMailServerInnoExtension.iss.
Filename: "{app}\Bin\hMailServer.INI"; Section: "Security"; Key: "AdministratorPassword"; String: "{code:GetHashedPassword}"; Flags: createkeyifdoesntexist; Components: server; Check: HasAdministratorPassword;
