<!--
Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
https://www.progressiverobot.com
SPDX-License-Identifier: AGPL-3.0-or-later
-->

# Packaging the Linux build

Everything here is support material for one binary. `hmailserver/source/Server/CMakeLists.txt`
builds `hmailserver` from `platform/main.cpp` and the core, and drives CPack; these
files are what turns that executable into something a machine can be handed.

Nothing here is compiled or shipped on Windows. The Windows server is still built
from `hMailServer.sln` and installed by the Inno Setup scripts under
`hmailserver/installation`.

## What each file is

| File | What it is |
|---|---|
| `hmailserver.service` | The systemd unit. `Type=simple`, running `/usr/bin/hmailserver --foreground` as the `hmailserver` user, with the hardening this program can actually tolerate. Every option it deliberately does **not** set is listed at the bottom of the file with the reason. |
| `hMailServer.ini` | The packaged default configuration, installed to `/etc/hmailserver/hMailServer.ini`. The six `[Directories]` keys point at the packaged layout; `[Database]` is deliberately empty. |
| `hmailserver.logrotate` | Installed as `/etc/logrotate.d/hmailserver`. Uses `copytruncate`, and the file explains at length why that and not a signal. |
| `debian/postinst` | Creates the user, the directories and the systemd enablement. Does not start the server. |
| `debian/prerm` | Stops the server on removal, and deliberately not on upgrade. |
| `debian/postrm` | Undoes the systemd enablement; on purge removes the configuration directory and says plainly what it is leaving behind. |
| `rpm/post` | The `%post` scriptlet: the same work as `debian/postinst`, in the shape rpm expects. |
| `rpm/preun` | The `%preun` scriptlet: stop and disable, but only on a real uninstall. |
| `PKGBUILD` | The Arch package. Builds from the repository with the same CMakeLists, and uses `sysusers.d` and `tmpfiles.d` instead of an install script - which is what Arch asks for now. |
| `build-appimage.sh` | Wraps an already-built tree as an AppImage. Read the paragraph at the top of it before using the result for anything. |

The `$1` argument means completely different things to dpkg and to rpm - a verb
in one and a count in the other - and each script says so at the top, because
carrying a maintainer script from one to the other without noticing is the
classic way to write one that never fires.

## Building the packages by hand

### Prerequisites

Debian and Ubuntu:

```sh
sudo apt install cmake ninja-build clang \
     libssl-dev zlib1g-dev libpq-dev \
     libboost-thread-dev libboost-filesystem-dev libboost-regex-dev \
     rpm file
```

Fedora and RHEL:

```sh
sudo dnf install cmake ninja-build clang \
     openssl-devel zlib-devel libpq-devel boost-devel \
     rpm-build dpkg
```

`clang` is not a preference. `Common/Util/StdString.h` calls members of its own
dependent base without `this->`, which only MSVC and clang's
`-fdelayed-template-parsing` accept; the CMakeLists warns and the build then fails
in that header under GCC. Making that header conforming is its own roadmap row.

`rpm`/`rpm-build` and `dpkg` are only needed to produce the *other* distribution's
package: CPack builds both generators from one tree.

### Configure and build

```sh
export CC=clang CXX=clang++

cmake -S hmailserver/source/Server -B build/linux -G Ninja \
      -DCMAKE_BUILD_TYPE=Release \
      -DCMAKE_INSTALL_PREFIX=/usr

cmake --build build/linux
```

`-DCMAKE_INSTALL_PREFIX=/usr` matters more than it looks. `GNUInstallDirs` answers
`/etc` and `/var` for `sysconfdir` and `localstatedir` only when the prefix is
`/usr`; with any other prefix the configuration would be installed to
`<prefix>/etc/hmailserver` and the logrotate rule to `<prefix>/etc/logrotate.d`,
where neither is read.

### The .deb and the .rpm

`CPACK_GENERATOR` is already `DEB;RPM`, so one command makes both:

```sh
cmake --build build/linux --target package
```

or, from inside the build directory, either one on its own:

```sh
cd build/linux
cpack -G DEB
cpack -G RPM
```

The files land in the build directory as `hmailserver_6.2.28-1_amd64.deb` and
`hmailserver-6.2.28-1.x86_64.rpm`, or their `arm64`/`aarch64` equivalents - the
CMakeLists picks the architecture name each packager uses from
`CMAKE_SYSTEM_PROCESSOR`.

### Arch

```sh
cp hmailserver/source/Server/platform/packaging/PKGBUILD /tmp/hmailserver-pkg/
cd /tmp/hmailserver-pkg
makepkg -si
```

It fetches the tag `v6.2.28` from the public repository rather than using the tree
it was copied out of, which is what makes the resulting package reproducible by
somebody who does not have your working copy.

### The AppImage

```sh
hmailserver/source/Server/platform/packaging/build-appimage.sh --build-dir build/linux
```

It packages what is already built, downloads `linuxdeploy` and its AppImage plugin
if they are not cached, and writes `hMailServer-6.2.28-x86_64.AppImage`.

## What lands where

From the CMakeLists install rules, with the prefix above:

```
/usr/bin/hmailserver                        the server; one file
/usr/share/hmailserver/DBScripts/*.sql      schema creation and upgrade scripts
/usr/lib/systemd/system/hmailserver.service the unit
/etc/hmailserver/hMailServer.ini            the configuration
/etc/logrotate.d/hmailserver                the log rotation rule
```

Created by the maintainer scripts, not by the package payload, and therefore not
removed with it:

```
/var/lib/hmailserver           0750 hmailserver:hmailserver   the mail store
/var/lib/hmailserver/temp      0750 hmailserver:hmailserver   scratch space
/var/lib/hmailserver/events    0750 hmailserver:hmailserver   event scripts
/var/lib/hmailserver/database  0750 hmailserver:hmailserver   unused on this platform
/var/log/hmailserver           0750 hmailserver:hmailserver   the logs
/etc/hmailserver               0750 root:hmailserver
/etc/hmailserver/hMailServer.ini 0640 root:hmailserver
```

The configuration is owned by root and only *read* by the service. It carries the
administrator password and the database password, and nothing in a Linux build
rewrites it - the code that would is the COM administration API, which is Windows
only.

## Install and get running

The whole sequence, in order. The package installs the server enabled and stopped
on purpose: it has no database yet, and a mail server that cannot reach one writes
an error to the log every second until somebody notices.

**1. Install the package.**

```sh
sudo apt install ./hmailserver_6.2.28-1_amd64.deb      # or
sudo dnf install ./hmailserver-6.2.28-1.x86_64.rpm
```

If you are using MySQL or MariaDB, install its client library too. The server
opens `libmariadb.so.3` with `dlopen` at run time rather than linking it, so no
package manager will pull it in: `libmariadb3` on Debian and Ubuntu,
`mariadb-connector-c` on Fedora and RHEL, `mariadb-libs` on Arch.

**2. Create the database and its user.** PostgreSQL:

```sh
sudo -u postgres createuser --pwprompt hmailserver
sudo -u postgres createdb --owner hmailserver hmailserver
```

**3. Create the schema.** Nothing does this for you on this platform - `DBSetup`
is a Windows tool, and the server has no `--create-database` yet.

```sh
psql -h localhost -U hmailserver -d hmailserver \
     -f /usr/share/hmailserver/DBScripts/CreateTablesPGSQL.sql
```

For MySQL or MariaDB the script is `CreateTablesMySQL.sql` and the command is
`mysql -h localhost -u hmailserver -p hmailserver < ...`.

**4. Edit the configuration.**

```sh
sudoedit /etc/hmailserver/hMailServer.ini
```

Three things have to change, and the file has a paragraph on each:

* `[Database] Type` - `PostgreSQL` or `MySQL`.
* `[Database] Server`, `Database`, `Username`, `Password` and `Port`. `Port` has
  to be written out (`5432`, `3306`); it is put into the connection string as it
  stands and there is no useful default.
* `[Security] AdministratorPassword` - the password itself. Nothing on this
  platform hashes it for you, which is the reason this file is `0640` and owned
  by root.

**5. Check it before starting anything.**

```sh
sudo -u hmailserver hmailserver --check-config
```

It reads the file, prints the configuration path, the three directories and the
database type it made of what you wrote, and exits without opening a listener or
the database. A `Database type: 0` means the `Type` key did not take.

**6. Start it.**

```sh
sudo systemctl start hmailserver
sudo systemctl status hmailserver
journalctl -u hmailserver -f
```

The server's own logs are in `/var/log/hmailserver`; the journal carries the
startup line, anything written to standard error, and the operational events -
database down, a listener that could not bind, a failed backup, the disk floor -
which reach syslog through the `WindowsEventLogEnabled` setting. The name of that
setting is a Windows inheritance; on this platform it means syslog.

**7. Administration.** There is no Control Panel here and no COM. Turn on the REST
API in the configuration (`RestApiPort`, and `RestApiBindAddress=127.0.0.1` unless
you also set a certificate and key), reload with `sudo systemctl reload
hmailserver`, and it authenticates as `administrator` with the password from step
4. Reloading stops and restarts the listeners inside the running process, so do it
when nothing is mid-delivery.

## What these packages do not do yet

Stated here rather than discovered later.

* **The configuration is not marked as a conffile.** CPack's DEB generator does not
  write a `conffiles` control file and its RPM generator does not mark anything
  `%config(noreplace)`, so an **upgrade replaces `/etc/hmailserver/hMailServer.ini`
  and your database credentials with it**. Keep a copy until the CMakeLists names a
  `conffiles` file in `CPACK_DEBIAN_PACKAGE_CONTROL_EXTRA` and a
  `CPACK_RPM_USER_FILELIST` entry for it. The Arch package does not have this
  problem - its `backup=` array is exactly this mechanism.
* **`tlds.txt` and `dh2048.pem` are not installed.** Both are read from the
  directory the executable is in - `/usr/bin` for a package install - and both live
  in `hmailserver/installation/Extras` with no install rule pointing at them. Until
  there is one, `TLD::Initialize` reports error 4335 and `SslContextInitializer`
  reports a critical 5603 for every TLS context. Copy them to `/usr/bin` by hand as
  a stopgap.
* **The RPM has no `%postun`.** `CPACK_RPM_POST_UNINSTALL_SCRIPT_FILE` is not set,
  so after `rpm -e` the unit file is gone without a `systemctl daemon-reload`
  having been run. Harmless, and one `daemon-reload` fixes it.
* **Path separators are still backslashes.** The roadmap row *Paths and case* is
  open: about a hundred path joins in the server are literal `\`, so some files -
  the log files among them - are created with a backslash in the name rather than
  in the directory the configuration names. The layout above is what the packages
  arrange and what the code will produce when that row lands.
* **Nothing here is signed yet.** The roadmap asks for the same Sigstore flow the
  Windows installer gets, and for the update checker to learn these artefact names.
