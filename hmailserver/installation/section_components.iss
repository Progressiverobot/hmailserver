[Components]
Name: "server"; Description: "Server"; Types: full custom
; Not a bundle of tools - it registers the hMailServer COM type library on a
; machine that is not the server, so scripts and COM clients can administer a
; remote instance. The Control Panel does not need it: it binds late, through
; IDispatch, and connects to a remote host on its own.
Name: "admintools"; Description: "Remote administration support (registers the COM API for scripts)"; Types: full custom
Name: "controlpanel"; Description: "Control Panel (modern admin app, requires .NET 8 Desktop Runtime)"; Types: full custom
