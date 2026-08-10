[Run]
; Install the bundled .NET 8 Desktop Runtime, but only when it is missing.
; For the server component the runtime is normally already installed by
; InstallDotNetRuntime() at ssPostInstall (before the database tools run);
; this entry is the backstop and covers controlpanel-only installs.
Filename: "{tmp}\windowsdesktop-runtime-8.0-win-x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing .NET 8 Desktop Runtime..."; Check: DotNetDesktopMissing; Components: server controlpanel;
Filename: "{app}\ControlPanel\hMailCP.exe";  Flags: skipifsilent postinstall nowait; Description: Run hMailServer Control Panel; Components: controlpanel;
