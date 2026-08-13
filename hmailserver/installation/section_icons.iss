[Icons]
Name: "{group}\hMailServer Database Setup"; Filename: "{app}\Bin\DBSetup.exe"; Components: server;
Name: "{group}\hMailServer Control Panel"; Filename: "{app}\ControlPanel\hMailCP.exe"; Components: controlpanel;
Name: "{group}\Addons\Data Directory Synchronizer"; Filename: "{app}\Addons\DataDirectorySynchronizer\DataDirectorySynchronizer.exe"; Components: server;
Name: "{group}\Addons\Import Tool"; Filename: "{app}\Addons\ImportTool\ImportTool.exe"; Components: server;
; {uninstallexe} rather than a hard-coded unins000.exe: when an uninstaller is
; already present Inno writes unins001.exe, unins002.exe and so on, and the
; shortcut then pointed at whichever file happened to be there first - or at
; nothing at all.
Name: "{group}\Installation\Uninstall hMailServer"; Filename: "{uninstallexe}"; Components: admintools server;
Name: "{group}\Service\Start service"; Filename: "{sys}\net.exe"; Parameters: "START hMailServer"; Components: server;
Name: "{group}\Service\Stop service"; Filename: "{sys}\net.exe"; Parameters: "STOP hMailServer"; Components: server;
