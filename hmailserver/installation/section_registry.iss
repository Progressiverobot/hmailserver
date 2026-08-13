[Registry]
; Tells other applications where hMailServer is installed. Utilities::GetBinDirectory
; reads this value and falls back to the directory of the running executable when it
; is absent, so a missing value is not fatal - but the COM tools and external scripts
; use it, and it is what the installer's own OverrideInstallationFolder() relies on
; when switching between x86 and x64 installs.
;
; This used to be written from [Code] with RegWriteStringValue at ssPostInstall. Two
; problems with that, both fixed by declaring it here instead:
;   * a code-written value is not recorded in the uninstall log, so every uninstall
;     left Software\Wow6432Node\hMailServer behind. uninsdeletevalue removes the
;     value and uninsdeletekeyifempty removes the key it created.
;   * [Registry] is processed during ssInstall, which is before the service is
;     started by RunPostInstallTasks - the old code wrote it in the same step that
;     started the server, so the very first start could not see it.
;
; HKLM32 is deliberate and must stay. Registry::GetStringValue opens the key with
; KEY_WOW64_32KEY, so the 64-bit server reads the 32-bit registry view; writing to
; the 64-bit view here would make the lookup fail on every machine.
Root: HKLM32; Subkey: "Software\hMailServer"; ValueType: string; ValueName: "InstallLocation"; ValueData: "{app}"; Flags: uninsdeletevalue uninsdeletekeyifempty
