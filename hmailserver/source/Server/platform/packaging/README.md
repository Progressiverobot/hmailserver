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
     libboost-thread-dev libboost-chrono-dev libboost-filesystem-dev libboost-regex-dev \
     rpm file
```

Fedora and RHEL:

```sh
sudo dnf install cmake ninja-build clang \
     openssl-devel zlib-devel libpq-devel boost-devel \
     rpm-build dpkg
```

`clang` is a preference and not a requirement: GCC 13 and later builds the same
tree (`export CC=gcc CXX=g++`), and the CI matrix builds with both. An earlier
version of this page said GCC could not compile `Common/Util/StdString.h`; that
was believed rather than measured, and measurement said otherwise.

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

The files land in the build directory as `hmailserver_6.2.28_amd64.deb` and
`hmailserver-6.2.28-1.x86_64.rpm`, or their `arm64`/`aarch64` equivalents - the
CMakeLists picks the architecture name each packager uses from
`CMAKE_SYSTEM_PROCESSOR`. The names are the ones the server's update checker
builds for its own platform and matches exactly on the release page, so they are
uploaded as they are and never tidied; RELEASE.md has the step.

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
sudo apt install ./hmailserver_6.2.28_amd64.deb      # or
sudo dnf install ./hmailserver-6.2.28-1.x86_64.rpm
```

If you are using MySQL or MariaDB, install its client library too. The server
opens `libmariadb.so.3` with `dlopen` at run time rather than linking it, so no
package manager will pull it in: `libmariadb3` on Debian and Ubuntu,
`mariadb-connector-c` on Fedora and RHEL, `mariadb-libs` on Arch.

**2. Create the database user, and either let it create the database or create
the database for it.** PostgreSQL:

```sh
sudo -u postgres createuser --pwprompt --createdb hmailserver     # step 4b creates the database, or
sudo -u postgres createuser --pwprompt hmailserver                # the tidier policy: no CREATEDB, and
sudo -u postgres createdb --owner hmailserver hmailserver         # step 4b fills the empty database
```

**3. Edit the configuration.**

```sh
sudoedit /etc/hmailserver/hMailServer.ini
```

Two things have to change, and the file has a paragraph on each:

* `[Database] Type` - `PostgreSQL` or `MySQL`.
* `[Database] Server`, `Database`, `Username`, `Password` and `Port`. `Port` has
  to be written out (`5432`, `3306`); it is put into the connection string as it
  stands and there is no useful default.

Leave `[Security] AdministratorPassword` alone; the next step writes it.

**4. Set the administrator password.** It is read from standard input with echo
off, hashed with PBKDF2 exactly as the Control Panel hashes it on Windows, and
written to the file. The file is `0640` and owned by root, so this runs as root:

```sh
sudo hmailserver --set-admin-password
```

**4b. Create the schema.** `DBSetup` is a Windows tool; on this platform the
server does the same work itself. `--create-database` connects to the server
`[Database]` names with no database selected, creates the database in the
backend's own dialect, and runs the create script for its type:

```sh
sudo -u hmailserver hmailserver --create-database
```

An existing empty database is used as it is; one that already holds an
hMailServer schema is refused with its version, because the create script must
not run over it. If the database already exists and is older than this build,
`--upgrade-database` runs every upgrade script from its version to this one, in
order, and refuses a database from a newer build. The package's post-install
step runs it on upgrade; it is safe to run by hand and reports "nothing to do"
when there is nothing to do. Both commands refuse the two Windows-only backends
by name.

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
hmailserver`, and it authenticates as `Administrator` with the password from step
4. Reloading stops and restarts the listeners inside the running process, so do it
when nothing is mid-delivery. Accounts under a domain are created over
`POST /api/v1/domains/{domain}/accounts`; **no route creates the domain itself
yet** - that is the roadmap's *Administration without COM* row - so the first
domain is an `INSERT INTO hm_domains` for now, and the row says so.

**Proven, 8 September 2026.** Against PostgreSQL 18: `--create-database` built
the schema, the server started and opened its SMTP, POP3, IMAP and REST
listeners, an account was created over the REST API, a message submitted over
SMTP with authentication was delivered, and the same message was read back over
IMAP with its subject intact.

## What the packaging pass found, and fixed

Each of these would have shipped a broken package, and each is fixed in the
CMakeLists rather than worked around here. Kept as a record of what to check
when the packaging changes.

* **The configuration was not a conffile.** An upgrade replaced
  `/etc/hmailserver/hMailServer.ini`, database credentials and all. The
  CMakeLists now generates a `conffiles` control file for the `.deb` and marks
  the file `%config(noreplace)` in the `.rpm`; the Arch package's `backup=`
  array was already this mechanism.
* **`tlds.txt` and `dh2048.pem` were not installed.** Both are read from the
  directory the executable is in, and without the second
  `SslContextInitializer` reports a critical 5603 for every TLS context. Both
  are installed beside the binary from `hmailserver/installation/Extras`.
* **The RPM had no `%postun`.** `rpm/postun` runs `systemctl daemon-reload`.
* **The configuration landed in `/usr/etc`.** CPack prefixes a relative
  `sysconfdir` with the packaging prefix; the install rules write `/etc`
  absolutely.
* **Every maintainer script would have shipped with CRLF** - `.gitattributes`
  forces it on the whole repository - and failed on the user's machine as
  `bad interpreter: /bin/sh^M`. This directory and `build/*.sh` have an
  `eol=lf` exception.
* **Paths were joined with a written backslash** in ninety-four places, so the
  first start wrote its log to a file called `Logs\hmailserver_....log` in the
  parent directory and delivery could not create a mailbox. Every join goes
  through `FileUtilities::Combine` and `PathSeparator` now, which is `/` here.

## What is still owed

* **The AArch64 packages have not been built on real hardware.** The
  cross-compile census reads 496/496 and the workflow has an `ubuntu-24.04-arm`
  job that packages; its first run is what turns that into a proof.
* **The PKGBUILD has not been exercised**; that needs an Arch machine.
* **MySQL and MariaDB are compiled in and not yet proven live** the way
  PostgreSQL is; the roadmap's database row says so.
* **The service does not reopen its log on a signal**, which is why the
  logrotate rule uses `copytruncate`.
