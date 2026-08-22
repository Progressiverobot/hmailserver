# Interop.hMailServer.dll

Checked-in tlbimp wrapper for the hMailServer COM API. The .NET tools
(DBSetup, DBSetupQuick, DBUpdater, DataDirectorySynchronizer, Shared,
ImportTool) reference this assembly directly so they build with plain
`dotnet build` on any machine — no registered typelib required. The
installer ships it to `{app}\Bin` for use by external .NET scripts.

## Regenerating

Regenerate whenever the COM API surface changes (`hMailServer.idl`), after
building the server in Release:

```powershell
buildegenerate-interop.ps1
```

That runs the TlbImp command below **and** updates this file's SHA-256 and
size in `hmailserver/docs/third-party-binaries.json`, which the
binary-provenance workflow checks on every push. Doing only the first half
by hand is how that check was failed twice on one day. The underlying
command, for reference:

```powershell
& "C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8.1 Tools\x64\TlbImp.exe" `
  hmailserver\source\Server\hMailServer\x64\Release\hMailServer.tlb `
  /out:hmailserver\source\Tools\Interop\Interop.hMailServer.dll `
  /namespace:hMailServer /machine:X64
```

A stale wrapper still builds and runs — the tools use a small, stable
subset of the API — but new COM members are invisible to them until the
wrapper is regenerated.
