# Interop.hMailServer.dll

The COM wrapper for the hMailServer API that the .NET tools (DBSetup,
DBSetupQuick, DBUpdater, DataDirectorySynchronizer, Shared, ImportTool)
reference by `HintPath`, so they build with a plain `dotnet build` and no
registered type library. The installer ships it to `{app}\Bin` for external
.NET scripts.

It is **not in git** (since 11 September 2026). It is TlbImp output from this
repository's own `hMailServer.idl`, and `build/generate-com-wrapper.ps1` makes
it:

* from the server build's `hMailServer.tlb`, when there is one for the
  requested configuration and the IDL has not changed since it was built;
* otherwise from the IDL alone: MIDL with the project file's options (inside
  `vcvars64.bat`, because MIDL uses `cl.exe` as its preprocessor), then TlbImp.
  This is what CI does (`-FromIdl`), on a runner that never builds the server.

The two sources produce the same type library byte for byte (checked on 11
September 2026). `build.ps1` runs the script after the server build,
`build-tools.ps1` runs it before publishing anything, and the two CI workflows
and the CodeQL analysis run it before they restore. It writes the wrapper only
when the type library it would be made from differs from the one recorded in
`Interop.hMailServer.dll.source-sha256` beside it; the wrapper's own hash cannot
say whether it is current, since TlbImp stamps a fresh module id on every run.

There is nothing to regenerate by hand after an IDL change and no manifest hash
to update: build the server, then the tools, in that order. The entry for this
file in `hmailserver/docs/third-party-binaries.json` is there so the provenance
check refuses the file if it is ever committed again.

The underlying command, for reference:

```powershell
& "C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8.1 Tools\x64\TlbImp.exe" `
  hmailserver\source\Server\hMailServer\x64\Release\hMailServer.tlb `
  /out:hmailserver\source\Tools\Interop\Interop.hMailServer.dll `
  /namespace:hMailServer /machine:X64
```
