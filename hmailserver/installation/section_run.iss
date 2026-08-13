[Run]
; Install the bundled .NET {#DOTNET_MAJOR} Desktop Runtime, but only when it is
; missing. For the server component the runtime is normally already installed by
; InstallDotNetRuntime() at ssPostInstall (before the database tools run); this
; entry is the backstop and covers controlpanel-only installs. Note that this
; double-installed the runtime on every install for as long as DotNetDesktopMissing
; was broken - the Check could never go false.
Filename: "{tmp}\{#DOTNET_RUNTIME_FILE}"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing .NET {#DOTNET_MAJOR} Desktop Runtime..."; Check: DotNetDesktopMissing; Components: server controlpanel;
Filename: "{app}\ControlPanel\hMailCP.exe";  Flags: skipifsilent postinstall nowait; Description: Run hMailServer Control Panel; Components: controlpanel;
