[UninstallRun]
; Order matters, and it used to be the wrong way round: /Unregister came first and
; called DeleteService on a *running* service, which only marks it for deletion and
; leaves the process running - so the uninstaller then began deleting {app} with
; hMailServer.exe still holding its own files open, and COM was unregistered from
; under a live server. net.exe stop is synchronous, so stopping first means the
; process is gone before anything is removed.
;
; skipifdoesntexist on every {app} entry. Without it Inno raises a "cannot execute"
; error for each missing file, and the entries below for the bundled MySQL and for
; hSMTPServer/hPOP3Server refer to files that have not shipped since hMailServer 4
; and 3 respectively: on a current installation that was three error dialogs on
; every single uninstall, and they are kept only so that an uninstall of a very old
; installation still tidies up after itself.
Filename: "{sys}\net.exe"; Parameters: "STOP hMailServer"; Flags: runhidden;
Filename: "{sys}\net.exe"; Parameters: "STOP hMailServerMySQL"; Flags: runhidden;
Filename: "{app}\Bin\hMailServer.exe"; Parameters: "/Unregister"; Flags: runhidden skipifdoesntexist;
Filename: "{app}\MySQL\Bin\mysqld-nt.exe"; Parameters: "--remove hMailServerMySQL"; Flags: runhidden skipifdoesntexist;
Filename: "{app}\Bin\hSMTPServer.exe"; Parameters: "unregister"; Flags: runhidden skipifdoesntexist;
Filename: "{app}\Bin\hPOP3Server.exe"; Parameters: "unregister"; Flags: runhidden skipifdoesntexist;
