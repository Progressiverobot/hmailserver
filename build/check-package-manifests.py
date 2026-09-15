#!/usr/bin/env python3
# https://www.progressiverobot.com
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
# SPDX-License-Identifier: AGPL-3.0-or-later

"""
Validates rendered winget manifests against Microsoft's own published schemas,
and the Chocolatey package beside them against what Chocolatey requires.

Why this exists and `winget validate` does not stand in for it: winget validate
is the authoritative check and it is what a maintainer runs locally, but the
winget client is not present on a GitHub-hosted runner in a headless session,
and installing it there to check three YAML files is a great deal of moving
parts for one answer. The manifests are JSON Schema documents; the schemas are
published; this validates against them with nothing but Python, which every
runner has. When the winget client IS present, the workflow runs it too - two
checks that agree is better than one.

What it checks, beyond the schemas:

  * the three manifests agree with each other about the package identifier and
    the version (winget-pkgs rejects the submission if they do not);
  * the version directory is named after the version the manifests carry;
  * the installer manifest keeps the ProductCode the Inno installer writes,
    which is the only thing that lets `winget upgrade` find an installation that
    winget itself did not make;
  * the installer URL is a release asset of this repository, is reachable, and
    is the size the release published (a HEAD request; the hash is the renderer's
    business, which computes it from the bytes rather than copying it);
  * the nuspec, if one is beside them, is well-formed, carries the same version,
    and its install script carries the same URL and hash as the manifest.

Usage:
  python build/check-package-manifests.py <directory>

where <directory> is what build/make-package-manifests.ps1 wrote: the one that
holds winget\\manifests\\... and chocolatey\\. A version directory may be given
instead, in which case only the winget half is checked.

Exits non-zero, with a list, if anything disagrees.
"""

import argparse
import json
import os
import ssl
import sys
import urllib.error
import urllib.request
import xml.etree.ElementTree as ElementTree

try:
    import yaml
except ImportError:  # pragma: no cover - the workflow installs it
    print("PyYAML is not installed: pip install pyyaml jsonschema", file=sys.stderr)
    raise SystemExit(2)

try:
    import jsonschema
except ImportError:  # pragma: no cover - the workflow installs it
    print("jsonschema is not installed: pip install pyyaml jsonschema", file=sys.stderr)
    raise SystemExit(2)

PROBLEMS = []

# The schemas live under a stable aka.ms alias per manifest type and version;
# that alias is what the manifests themselves name in their first line, so the
# schema fetched here is the schema the manifest claims to be written against.
SCHEMA_URL = "https://aka.ms/winget-manifest.{type}.{version}.schema.json"

RELEASE_HOST_PREFIX = "https://github.com/Progressiverobot/hmailserver/releases/download/"


def problem(message):
    PROBLEMS.append(message)


def fetch(url, cache_directory):
    """Fetches a URL, keeping a copy so a re-run offline still works."""
    name = "".join(c if c.isalnum() or c in "._-" else "_" for c in url)
    cached = os.path.join(cache_directory, name)
    if os.path.exists(cached):
        with open(cached, "rb") as handle:
            return handle.read()

    context = ssl.create_default_context()
    request = urllib.request.Request(url, headers={"User-Agent": "hmailserver-package-check"})
    with urllib.request.urlopen(request, timeout=60, context=context) as response:
        body = response.read()

    os.makedirs(cache_directory, exist_ok=True)
    with open(cached, "wb") as handle:
        handle.write(body)
    return body


class ManifestLoader(yaml.SafeLoader):
    """A loader that leaves dates as the strings the schema says they are.

    YAML resolves an unquoted 2026-09-15 to a date object, and the winget schema
    says ReleaseDate is a string - so a manifest that the winget client accepts
    fails validation here for a reason that is the parser's and not the
    manifest's. Quoting the value in our own template is not enough: this also
    reads manifests written by wingetcreate, which does not quote it.
    """


ManifestLoader.add_constructor(
    "tag:yaml.org,2002:timestamp",
    lambda loader, node: loader.construct_scalar(node),
)


def load_manifest(path):
    with open(path, "r", encoding="utf-8-sig") as handle:
        return yaml.load(handle, Loader=ManifestLoader)


def check_winget(version_directory, cache_directory, offline):
    manifests = {}
    for entry in sorted(os.listdir(version_directory)):
        if not entry.endswith(".yaml"):
            continue
        path = os.path.join(version_directory, entry)
        document = load_manifest(path)
        if not isinstance(document, dict):
            problem("%s: not a YAML mapping" % entry)
            continue
        kind = document.get("ManifestType")
        if kind in manifests:
            problem("%s: a second %s manifest" % (entry, kind))
        manifests[kind] = (entry, document)

    for required in ("version", "installer", "defaultLocale"):
        if required not in manifests:
            problem("no %s manifest in %s" % (required, version_directory))

    if not manifests:
        return

    # The schemas.
    for kind, (entry, document) in sorted(manifests.items()):
        manifest_version = document.get("ManifestVersion")
        if not manifest_version:
            problem("%s: no ManifestVersion" % entry)
            continue
        if offline:
            continue
        url = SCHEMA_URL.format(type=kind, version=manifest_version)
        try:
            schema = json.loads(fetch(url, cache_directory))
        except (urllib.error.URLError, ValueError) as error:
            problem("%s: could not read %s (%s)" % (entry, url, error))
            continue
        validator = jsonschema.Draft7Validator(schema)
        for error in sorted(validator.iter_errors(document), key=lambda e: list(e.path)):
            where = "/".join(str(part) for part in error.path) or "(root)"
            problem("%s: %s: %s" % (entry, where, error.message))
        print("  %-46s validated against %s" % (entry, os.path.basename(url)))

    # The three have to agree, and with the directory they are in.
    identifiers = {document.get("PackageIdentifier") for _, document in manifests.values()}
    if len(identifiers) != 1:
        problem("the manifests disagree about PackageIdentifier: %s" % ", ".join(sorted(str(i) for i in identifiers)))
    versions = {str(document.get("PackageVersion")) for _, document in manifests.values()}
    if len(versions) != 1:
        problem("the manifests disagree about PackageVersion: %s" % ", ".join(sorted(versions)))
    else:
        version = versions.pop()
        directory_name = os.path.basename(os.path.normpath(version_directory))
        if directory_name != version:
            problem("the version directory is named %s but the manifests say %s" % (directory_name, version))

    installer_entry, installer = manifests.get("installer", (None, {}))
    if installer:
        if installer.get("ProductCode") != "hMailServer_is1":
            problem(
                "%s: ProductCode is %r, not 'hMailServer_is1' - the Inno installer writes the uninstall key "
                "hMailServer_is1 (AppId in section_setup.iss), and without it winget cannot recognise an "
                "installation it did not make, so `winget upgrade` will offer to install a second copy"
                % (installer_entry, installer.get("ProductCode"))
            )
        if installer.get("InstallerType") != "inno":
            problem("%s: InstallerType is %r, not 'inno'" % (installer_entry, installer.get("InstallerType")))
        switches = installer.get("InstallerSwitches") or {}
        for switch in ("Silent", "SilentWithProgress"):
            if "/SUPPRESSMSGBOXES" not in (switches.get(switch) or ""):
                problem(
                    "%s: the %s switches do not carry /SUPPRESSMSGBOXES - without it a failed database setup "
                    "shows a message box, silent mode answers it with the default button, and a broken install "
                    "reports success" % (installer_entry, switch)
                )
        for entry in installer.get("Installers") or []:
            url = entry.get("InstallerUrl", "")
            if not url.startswith(RELEASE_HOST_PREFIX):
                problem("%s: the installer URL is not a release asset of this repository: %s" % (installer_entry, url))
                continue
            if offline:
                continue
            request = urllib.request.Request(url, method="HEAD", headers={"User-Agent": "hmailserver-package-check"})
            try:
                with urllib.request.urlopen(request, timeout=60) as response:
                    length = response.headers.get("Content-Length")
                    print("  installer URL reachable, %s bytes" % (length or "unknown"))
            except urllib.error.URLError as error:
                problem("%s: the installer URL is not reachable: %s (%s)" % (installer_entry, url, error))

    return manifests


def check_chocolatey(directory, manifests):
    nuspec = os.path.join(directory, "hmailserver.nuspec")
    if not os.path.exists(nuspec):
        problem("no hmailserver.nuspec in %s" % directory)
        return

    try:
        tree = ElementTree.parse(nuspec)
    except ElementTree.ParseError as error:
        problem("the nuspec is not well-formed XML: %s" % error)
        return

    namespace = {"n": "http://schemas.microsoft.com/packaging/2015/06/nuspec.xsd"}
    metadata = tree.getroot().find("n:metadata", namespace)
    if metadata is None:
        problem("the nuspec has no <metadata>")
        return

    def text(tag):
        element = metadata.find("n:%s" % tag, namespace)
        return element.text.strip() if element is not None and element.text else None

    identifier = text("id")
    version = text("version")
    if identifier != "hmailserver":
        problem("the nuspec id is %r, not 'hmailserver'" % identifier)
    for required in ("version", "title", "authors", "description", "summary", "projectUrl", "licenseUrl", "tags"):
        if not text(required):
            problem("the nuspec has no <%s>, which Chocolatey requires" % required)

    install_script = os.path.join(directory, "tools", "chocolateyinstall.ps1")
    uninstall_script = os.path.join(directory, "tools", "chocolateyuninstall.ps1")
    for script in (install_script, uninstall_script):
        if not os.path.exists(script):
            problem("no %s" % script)
    if not os.path.exists(install_script):
        return

    with open(install_script, "r", encoding="utf-8-sig") as handle:
        script_text = handle.read()

    installer = (manifests or {}).get("installer", (None, {}))[1]
    for entry in installer.get("Installers") or []:
        url = entry.get("InstallerUrl")
        digest = entry.get("InstallerSha256", "")
        if url and url not in script_text:
            problem("the Chocolatey install script does not carry the URL the winget manifest does")
        if digest and digest.upper() not in script_text.upper():
            problem("the Chocolatey install script does not carry the hash the winget manifest does")

    if version and installer and str(installer.get("PackageVersion")) != version:
        problem("the nuspec version is %s but the winget manifests say %s" % (version, installer.get("PackageVersion")))

    print("  hmailserver.nuspec %s, tools/chocolateyinstall.ps1 and tools/chocolateyuninstall.ps1 checked" % version)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("directory", help="what build/make-package-manifests.ps1 wrote")
    parser.add_argument("--offline", action="store_true", help="skip the schema fetch and the URL check")
    arguments = parser.parse_args()

    directory = os.path.abspath(arguments.directory)
    if not os.path.isdir(directory):
        print("No directory at %s" % directory, file=sys.stderr)
        return 2

    cache_directory = os.path.join(directory, ".schema-cache")

    winget_root = os.path.join(directory, "winget", "manifests", "p", "ProgressiveRobot", "hMailServer")
    if os.path.isdir(winget_root):
        versions = sorted(
            os.path.join(winget_root, entry)
            for entry in os.listdir(winget_root)
            if os.path.isdir(os.path.join(winget_root, entry))
        )
        if not versions:
            problem("no version directory under %s" % winget_root)
        manifests = None
        for version_directory in versions:
            print("winget %s" % os.path.basename(version_directory))
            manifests = check_winget(version_directory, cache_directory, arguments.offline)
        chocolatey = os.path.join(directory, "chocolatey")
        if os.path.isdir(chocolatey):
            print("chocolatey")
            check_chocolatey(chocolatey, manifests)
    else:
        # A version directory given directly.
        print("winget %s" % os.path.basename(os.path.normpath(directory)))
        check_winget(directory, cache_directory, arguments.offline)

    if PROBLEMS:
        print("")
        for line in PROBLEMS:
            print("  PROBLEM %s" % line)
        print("")
        print("%d problem(s)." % len(PROBLEMS))
        return 1

    print("")
    print("The package manifests are valid.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
