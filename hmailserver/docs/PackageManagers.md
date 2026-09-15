Package managers
================

Installing hMailServer with `winget` or `choco`, and — for the maintainer — how
the manifests behind those two commands are made, checked and submitted.

Where this stands today
-----------------------

The manifests and the Chocolatey package are **built and validated on every
release**, from the release's own signed installer. They are **not yet in the
public repositories**: `winget install hMailServer` and `choco install
hmailserver` do not work until the first submission has been reviewed and
merged by the people who keep those repositories, which is a manual step for a
package identifier nobody has seen before. This document is the walk for that
first submission and for everything after it.

Neither identifier is taken. As of 15 September 2026 `winget search hmailserver`
finds nothing, and the Chocolatey community feed has no `hmailserver` package —
so the fork takes `ProgressiveRobot.hMailServer` on winget and `hmailserver` on
Chocolatey.

What a user will type
---------------------

```powershell
winget install ProgressiveRobot.hMailServer      # or: winget install hMailServer
winget upgrade ProgressiveRobot.hMailServer
```

```powershell
choco install hmailserver
choco upgrade hmailserver
```

Both run the same Authenticode-signed installer the release page offers, with
the same switches the installer smoke test uses. Both install the server, the
Control Panel and the setup tools, and configure the embedded SQL Server Compact
database.

**An unattended install leaves the administrator password unset.** The wizard
page that asks for one is skipped, and neither package invents a password. Set
one before the server is reachable from anywhere but the machine itself:

```powershell
& "C:\Program Files\hMailServer\Bin\hMailServer.exe" --set-admin-password
```

or open the Control Panel from the Start menu.

Uninstalling (`winget uninstall`, `choco uninstall`) runs the installer's own
uninstaller. It does not delete the message store, the database or
`hMailServer.ini`: an administrator removing a package should not lose the mail.

What is in the repository
-------------------------

| Path | What it is |
|---|---|
| `hmailserver/installation/winget/*.yaml.in` | The three winget manifests — version, installer, `en-US` locale — as templates. Lines opening with `#~` are the template's own commentary and are dropped when rendered. |
| `hmailserver/installation/chocolatey/hmailserver.nuspec.in` | The Chocolatey package metadata, as a template. |
| `hmailserver/installation/chocolatey/tools/chocolateyinstall.ps1.in` | Downloads the release installer, checks it against the SHA-256, runs it unattended. |
| `hmailserver/installation/chocolatey/tools/chocolateyuninstall.ps1` | Runs the Inno uninstaller found through the `hMailServer_is1` registry key. Not a template: nothing in it changes between versions. |
| `build/make-package-manifests.ps1` | Renders all of the above for a version, computing the hash from the actual installer. |
| `build/check-package-manifests.py` | Validates the rendered manifests against Microsoft's published JSON schemas and cross-checks them against the Chocolatey package. |
| `.github/workflows/package-managers.yml` | Runs the two on every release, keeps the result as an artifact, and submits when armed. |

Where the values come from
--------------------------

Nothing in the manifests is a guess; every identity field is the installer's
own, from `hmailserver/installation/section_setup.iss` and `section_setup_64.iss`:

| Manifest field | Installer directive | Why it matters |
|---|---|---|
| `MinimumOSVersion: 10.0.14393.0` | `MinVersion=10.0.14393` | Windows 10 1607, the floor of the .NET 10 Desktop Runtime the Control Panel needs. |
| `ProductCode: hMailServer_is1` | `AppId=hMailServer` | Inno's uninstall key is `<AppId>_is1`. This is the only thing that lets winget recognise an installation it did not make — without it `winget upgrade` offers to install a second copy beside the first. |
| `DisplayVersion` | `AppVersion=<version>` | What `winget upgrade` compares against `PackageVersion`. |
| `DisplayName: hMailServer <version>-x64` | `AppVerName` | The name in Apps and Features. |
| `ElevationRequirement: elevatesSelf` | `PrivilegesRequired=admin` | The installer asks for elevation itself. |
| `InstallerType: inno` | Inno Setup | Gives winget the right default switches; ours are declared anyway. |

The silent switches are `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`.
`/SUPPRESSMSGBOXES` is not optional: without it a database-setup failure shows a
message box, silent mode answers it with the default button, and a broken
install reports success. `/TYPE=full` is deliberately **not** passed, unlike the
smoke test: on a first install Inno's default type is the full one anyway, and
on an upgrade Inno preselects the components already installed — forcing the
full type would quietly put the Control Panel back on a server where the
administrator had left it out.

Making the files for a release
------------------------------

The workflow does this on `release: published`, and this is the same thing by
hand:

```powershell
# From the release that has been published (downloads the asset and hashes it):
build\make-package-manifests.ps1 -Version 6.3.3 -ReleaseDate 2026-09-15

# Or from a local copy of the SIGNED installer:
build\make-package-manifests.ps1 -Version 6.3.3 -ReleaseDate 2026-09-15 `
    -InstallerPath C:\...\hMailServer-6.3.3-x64.exe -RequireSignedInstaller

python build\check-package-manifests.py logs\package-manifests
winget validate --manifest logs\package-manifests\winget\manifests\p\ProgressiveRobot\hMailServer\6.3.3
choco pack logs\package-manifests\chocolatey\hmailserver.nuspec --outputdirectory logs\package-manifests\chocolatey
```

**Use the installer the release attached, not the one the build produced.** They
are different files: the release is Authenticode-signed after the build, and
signing changes the bytes and therefore the hash. For 6.3.3 the built installer
hashes to `45A9D7F1…` and the published one to `3188BE2F…`; a manifest carrying
the first would fail for every user with a hash mismatch.

The first submission, by hand
-----------------------------

winget:

1. Fork `microsoft/winget-pkgs` under the account that will submit.
2. Make a classic personal access token with `public_repo` scope.
3. Run the render above, then:
   ```powershell
   wingetcreate submit --token <token> `
       logs\package-manifests\winget\manifests\p\ProgressiveRobot\hMailServer\6.3.3
   ```
   `wingetcreate` is a single executable from <https://aka.ms/wingetcreate/latest>.
   It opens a pull request against `winget-pkgs` from the fork.
4. Their pipeline installs the package on a sandbox VM and a moderator reviews a
   new identifier. Expect questions the first time; answer them on the pull
   request.

Chocolatey:

1. Make an account on <https://community.chocolatey.org> and take the API key
   from the account page.
2. `choco push hmailserver.6.3.3.nupkg --source https://push.chocolatey.org/ --api-key <key>`
3. A new package goes through moderation — automated checks first (the install
   and uninstall are run on a clean machine), then a human. The install script
   downloads a 76 MB installer, so allow for the verifier taking its time.

Arming the automatic submission
-------------------------------

Once the first version of each is accepted, every release can submit itself.
Set, in the repository's settings:

| Kind | Name | Value |
|---|---|---|
| Variable | `PACKAGE_MANAGER_SUBMIT` | `true` |
| Secret | `WINGET_TOKEN` | The classic PAT with `public_repo` on the fork of `winget-pkgs` |
| Secret | `CHOCO_API_KEY` | The Chocolatey community feed key |

With the variable unset, or a secret missing, the workflow still renders,
validates and keeps the files — it just says in the log that it submitted
nothing. That is the deliberate default: no release should open a pull request
against somebody else's repository by surprise.

What this does not do
---------------------

* **No Linux package manager.** The `.deb`, `.rpm` and AppImage are attached to
  each release and are documented in the README; an apt or dnf repository, and
  the Arch AUR, are separate roadmap rows.
* **No `winget install` of a pre-release.** The workflow refuses a tag that is
  not three numbers, because package managers should not hand an alpha to
  somebody typing `winget install hmailserver`.
* **No unattended configuration.** Neither package sets an administrator
  password, adds a domain, or opens a firewall port. Installing a mail server is
  one command; configuring one is not, and a package that pretended otherwise
  would be a security defect rather than a convenience.
