[Run]
; Install the bundled .NET 8 Desktop Runtime, but only when it is missing.
Filename: "{tmp}\windowsdesktop-runtime-8.0-win-x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing .NET 8 Desktop Runtime..."; Check: DotNetDesktopMissing; Components: controlpanel;
Filename: "{app}\ControlPanel\hMailCP.exe";  Flags: skipifsilent postinstall nowait; Description: Run hMailServer Control Panel; Components: controlpanel;
