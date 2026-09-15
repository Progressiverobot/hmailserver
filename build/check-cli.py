#!/usr/bin/env python3
# https://www.progressiverobot.com
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
# SPDX-License-Identifier: AGPL-3.0-or-later

"""
Runs the two command-line clients against a recorded server and checks what they
sent and what they printed.

The clients - `hmailserver/source/Tools/Cli/hmctl` (Python, any platform) and
`hmailserver/source/Tools/Cli/HmailServer.psm1` (PowerShell) - are the REST API
with a vocabulary in front of it, so the thing that can go wrong is that the
vocabulary points at the wrong route: `hmctl account list` asking
`/api/v1/accounts?domain=x` when the API lists accounts under their domain. A
real server would answer 404 and a person would find out; a check that needs a
real server is a check nobody runs.

So this stands a small HTTP server in front of both, answers whatever the route
would answer, and asserts on the request that arrived: the method, the path, the
Authorization header and the body. Every case also asserts something the client
printed, because a client that sends the right request and prints nothing useful
is no better.

The PowerShell half is skipped, with a reason, where `pwsh` is not on the path.
Both halves are skipped if the file they test is missing, which cannot happen in
a checkout and can happen in a partial copy.

Usage:
  python build/check-cli.py            # both clients
  python build/check-cli.py --python   # hmctl only
  python build/check-cli.py --verbose  # print every request that arrived
"""

import argparse
import base64
import json
import os
import shutil
import subprocess
import sys
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HMCTL = os.path.join(ROOT, "hmailserver", "source", "Tools", "Cli", "hmctl")
MODULE = os.path.join(ROOT, "hmailserver", "source", "Tools", "Cli", "HmailServer.psm1")

PASSWORD = "bench-password"
EXPECTED_BASIC = "Basic " + base64.b64encode(("Administrator:" + PASSWORD).encode("utf-8")).decode("ascii")

# What the recorded server answers, by (method, path). A path not here is a 404,
# which is what an unknown route deserves and what a wrong one will get.
ANSWERS = {
    ("GET", "/api/v1/status"): {"version": "6.3.3", "database": "PostgreSQL", "uptime_seconds": 4242},
    ("GET", "/api/v1/domains"): {"domains": [
        {"name": "example.com", "active": True, "max_size_mb": 0, "account_count": 2},
        {"name": "example.net", "active": False, "max_size_mb": 100, "account_count": 0},
    ]},
    ("GET", "/api/v1/domains/example.com"): {"name": "example.com", "active": True, "plus_addressing_enabled": True},
    ("POST", "/api/v1/domains"): {"name": "new.example", "active": True},
    ("PUT", "/api/v1/domains/example.com"): {"name": "example.com", "active": False},
    ("DELETE", "/api/v1/domains/example.net"): None,
    ("GET", "/api/v1/domains/example.com/accounts"): {"accounts": [
        {"address": "ann@example.com", "active": True, "max_size_mb": 100, "admin_level": 0},
        {"address": "bob@example.com", "active": True, "max_size_mb": 0, "admin_level": 0},
    ]},
    ("POST", "/api/v1/domains/example.com/accounts"): {"address": "new@example.com", "active": True},
    ("GET", "/api/v1/accounts/ann@example.com"): {"address": "ann@example.com", "active": True, "vacation_message": ""},
    ("PUT", "/api/v1/accounts/ann@example.com"): {"address": "ann@example.com", "active": False},
    ("DELETE", "/api/v1/accounts/bob@example.com"): None,
    ("GET", "/api/v1/domains/example.com/aliases"): {"aliases": [
        {"name": "sales@example.com", "value": "ann@example.com", "active": True}]},
    ("GET", "/api/v1/groups"): {"groups": [{"id": 1, "name": "support"}]},
    ("GET", "/api/v1/groups/1/members"): {"members": [{"id": 7, "account_id": 3, "address": "ann@example.com"}]},
    ("POST", "/api/v1/groups/1/members"): {"id": 8, "account_id": 4, "address": "bob@example.com"},
    ("GET", "/api/v1/queue"): {"messages": [
        {"id": 12, "from_address": "ann@example.com", "to_address": "far@away.test",
         "next_try": "2026-09-15T05:00:00Z", "retry_count": 1, "size": 2048}]},
    ("POST", "/api/v1/queue/12/retry"): {"queued": True},
    ("GET", "/api/v1/quarantine"): {"messages": [
        {"id": 3, "recipient": "ann@example.com", "from_address": "spam@away.test",
         "subject": "Held", "reason": "virus", "received": "2026-09-15T04:00:00Z"}]},
    ("POST", "/api/v1/quarantine/3/release"): {"released": True},
    ("GET", "/api/v1/settings"): {"welcome_smtp": "hMailServer", "max_message_size_kb": 20480},
    ("PUT", "/api/v1/settings"): {"max_message_size_kb": 51200},
    ("GET", "/api/v1/settings/antispam"): {"use_spf": True, "spam_mark_threshold": 5},
    ("PUT", "/api/v1/settings/antispam"): {"use_spf": False},
    ("GET", "/api/v1/settings/ini"): {"settings": [{"name": "DisableAUTHList", "value": "0"}]},
    ("PUT", "/api/v1/settings/ini/DisableAUTHList"): {"name": "DisableAUTHList", "value": "1"},
    ("GET", "/api/v1/logs"): {"logs": [{"name": "hmailserver_2026-09-15.log", "size": 91234}]},
    ("GET", "/api/v1/logs/hmailserver_2026-09-15.log"): {"lines": ["\"SMTPD\" 1 \"04:00:00.001\" \"127.0.0.1\" \"EHLO\""]},
    ("GET", "/api/v1/backup"): {"state": "idle", "last_backup": "2026-09-14T02:00:00Z"},
    ("POST", "/api/v1/backup"): {"state": "running"},
    ("POST", "/api/v1/rules/match"): {"match": True},
    ("GET", "/api/v1/accounts/ann@example.com/app-passwords"): {"app_passwords": [
        {"id": 2, "name": "phone", "created": "2026-09-01T10:00:00Z", "last_used": None}]},
    ("POST", "/api/v1/accounts/ann@example.com/app-passwords"): {"id": 3, "name": "tablet", "password": "abcd-efgh-ijkl"},
    ("GET", "/api/v1/rules"): {"rules": [{"name": "Tag support", "active": True, "sort_order": 1}]},
    ("GET", "/api/v1/routes"): {"routes": [{"domain_name": "partner.test", "target_smtp_host": "mx.partner.test",
                                            "target_smtp_port": 25}]},
    ("GET", "/api/v1/ports"): {"ports": [{"protocol": "SMTP", "address": "0.0.0.0", "port": 25,
                                          "connection_security": 0}]},
    ("GET", "/api/v1/domains/example.com/lists"): {"lists": [{"address": "all@example.com", "active": True,
                                                              "mode": 0}]},
    ("GET", "/api/v1/domains/example.net/accounts"): {"accounts": []},
    ("GET", "/api/v1/domains/example.net/aliases"): {"aliases": []},
    ("GET", "/api/v1/domains/example.net/lists"): {"lists": []},
    ("PUT", "/api/v1/domains/example.net"): {"name": "example.net", "active": True},
    ("GET", "/api/v1/ipranges"): {"ipranges": [
        {"id": 1, "name": "My computer", "lower_ip": "127.0.0.1", "upper_ip": "127.0.0.1", "priority": 15}]},
}


class Recorder(BaseHTTPRequestHandler):
    requests = []
    lock = threading.Lock()

    def log_message(self, *arguments):
        pass

    def _handle(self, method):
        length = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(length) if length else b""
        body = None
        if raw:
            try:
                body = json.loads(raw.decode("utf-8"))
            except ValueError:
                body = raw.decode("utf-8", "replace")

        with Recorder.lock:
            Recorder.requests.append({
                "method": method,
                "path": self.path,
                "authorization": self.headers.get("Authorization"),
                "content_type": self.headers.get("Content-Type"),
                "body": body,
            })

        if self.headers.get("Authorization") is None:
            self.send_error(401, "no credential")
            return

        path = self.path.split("?", 1)[0]
        import urllib.parse as parse
        path = parse.unquote(path)

        if (method, path) not in ANSWERS:
            payload = json.dumps({"error": "no such route: %s %s" % (method, path)}).encode("utf-8")
            self.send_response(404)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)
            return

        answer = ANSWERS[(method, path)]
        if answer is None:
            self.send_response(204)
            self.send_header("Content-Length", "0")
            self.end_headers()
            return

        payload = json.dumps(answer).encode("utf-8")
        self.send_response(201 if method == "POST" else 200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def do_GET(self):
        self._handle("GET")

    def do_POST(self):
        self._handle("POST")

    def do_PUT(self):
        self._handle("PUT")

    def do_DELETE(self):
        self._handle("DELETE")


PROBLEMS = []
CHECKS = [0]


def problem(message):
    PROBLEMS.append(message)


def check(condition, message):
    CHECKS[0] += 1
    if not condition:
        problem(message)
    return condition


# (name, arguments, expected method, expected path, body assertion, output assertion)
CASES = [
    ("status", ["status"], "GET", "/api/v1/status", None, ["6.3.3", "4242"]),
    ("domain list", ["domain", "list"], "GET", "/api/v1/domains", None, ["example.com", "example.net", "NAME"]),
    ("domain list --json", ["domain", "list", "--json"], "GET", "/api/v1/domains", None, ['"name"']),
    ("domain get", ["domain", "get", "example.com"], "GET", "/api/v1/domains/example.com", None, ["plus_addressing_enabled"]),
    ("domain create", ["domain", "create", "--set", "name=new.example", "--set", "active=true"],
     "POST", "/api/v1/domains", {"name": "new.example", "active": True}, ["new.example"]),
    ("domain update", ["domain", "update", "example.com", "--set", "active=false"],
     "PUT", "/api/v1/domains/example.com", {"active": False}, []),
    ("domain delete", ["domain", "delete", "example.net"], "DELETE", "/api/v1/domains/example.net", None, ["deleted"]),
    ("account list under its domain", ["account", "list", "--domain", "example.com"],
     "GET", "/api/v1/domains/example.com/accounts", None, ["ann@example.com", "bob@example.com"]),
    ("account create under its domain",
     ["account", "create", "--domain", "example.com", "--set", "address=new@example.com", "--set", "password=secret"],
     "POST", "/api/v1/domains/example.com/accounts", {"address": "new@example.com", "password": "secret"}, ["new@example.com"]),
    ("account get by address", ["account", "get", "ann@example.com"], "GET", "/api/v1/accounts/ann@example.com", None, ["vacation_message"]),
    ("account update by address", ["account", "update", "ann@example.com", "--set", "active=false"],
     "PUT", "/api/v1/accounts/ann@example.com", {"active": False}, []),
    ("account delete by address", ["account", "delete", "bob@example.com"], "DELETE", "/api/v1/accounts/bob@example.com", None, ["deleted"]),
    ("alias list under its domain", ["alias", "list", "--domain", "example.com"],
     "GET", "/api/v1/domains/example.com/aliases", None, ["sales@example.com"]),
    ("group list", ["group", "list"], "GET", "/api/v1/groups", None, ["support"]),
    ("group members", ["member", "1", "list"], "GET", "/api/v1/groups/1/members", None, ["ann@example.com"]),
    ("group member add by address", ["member", "1", "add", "bob@example.com"],
     "POST", "/api/v1/groups/1/members", {"address": "bob@example.com"}, ["bob@example.com"]),
    ("queue list", ["queue", "list"], "GET", "/api/v1/queue", None, ["far@away.test"]),
    ("queue retry", ["queue", "retry", "12"], "POST", "/api/v1/queue/12/retry", None, ["queued"]),
    ("quarantine list", ["quarantine", "list"], "GET", "/api/v1/quarantine", None, ["spam@away.test"]),
    ("quarantine release", ["quarantine", "release", "3"], "POST", "/api/v1/quarantine/3/release", None, ["released"]),
    ("settings get", ["settings", "get"], "GET", "/api/v1/settings", None, ["max_message_size_kb"]),
    ("settings get one", ["settings", "get", "settings", "max_message_size_kb"], "GET", "/api/v1/settings", None, ["20480"]),
    ("settings get a group", ["settings", "get", "antispam"], "GET", "/api/v1/settings/antispam", None, ["use_spf"]),
    ("settings set", ["settings", "set", "--set", "max_message_size_kb=51200"],
     "PUT", "/api/v1/settings", {"max_message_size_kb": 51200}, []),
    ("settings set a group", ["settings", "set", "antispam", "--set", "use_spf=false"],
     "PUT", "/api/v1/settings/antispam", {"use_spf": False}, []),
    ("ini list", ["ini", "list"], "GET", "/api/v1/settings/ini", None, ["DisableAUTHList"]),
    ("ini set", ["ini", "set", "DisableAUTHList", "1"],
     "PUT", "/api/v1/settings/ini/DisableAUTHList", {"value": "1"}, []),
    ("log list", ["log", "list"], "GET", "/api/v1/logs", None, ["hmailserver_2026-09-15.log"]),
    ("log tail", ["log", "tail", "hmailserver_2026-09-15.log", "--lines", "5"],
     "GET", "/api/v1/logs/hmailserver_2026-09-15.log", None, ["SMTPD"]),
    ("backup status", ["backup", "status"], "GET", "/api/v1/backup", None, ["idle"]),
    ("backup start", ["backup", "start"], "POST", "/api/v1/backup", {}, ["running"]),
    ("rule criterion probe", ["match", "contains", "viagra", "cheap viagra here"],
     "POST", "/api/v1/rules/match",
     {"match_value": "viagra", "match_type": "contains", "test_value": "cheap viagra here"}, ["match"]),
    ("app passwords", ["app-password", "ann@example.com", "list"],
     "GET", "/api/v1/accounts/ann@example.com/app-passwords", None, ["phone"]),
    ("app password create", ["app-password", "ann@example.com", "create", "tablet"],
     "POST", "/api/v1/accounts/ann@example.com/app-passwords", {"name": "tablet"}, ["abcd-efgh-ijkl", "only time"]),
    ("ip ranges", ["iprange", "list"], "GET", "/api/v1/ipranges", None, ["My computer"]),
    ("the escape hatch", ["api", "GET", "/api/v1/status"], "GET", "/api/v1/status", None, ["6.3.3"]),
]


def run_python_cases(url, verbose):
    for name, arguments, method, path, body, expected in CASES:
        with Recorder.lock:
            Recorder.requests = []

        command = [sys.executable, HMCTL, "--url", url, "--password", PASSWORD] + arguments
        completed = subprocess.run(command, capture_output=True, text=True, timeout=60)

        with Recorder.lock:
            requests = list(Recorder.requests)

        if verbose:
            print("  %-34s %s" % (name, requests))

        if not check(requests, "hmctl %s: sent no request at all (%s)" % (name, completed.stderr.strip())):
            continue

        sent = requests[-1]
        sent_path = sent["path"].split("?", 1)[0]
        import urllib.parse as parse
        sent_path = parse.unquote(sent_path)

        check(sent["method"] == method,
              "hmctl %s: sent %s, expected %s" % (name, sent["method"], method))
        check(sent_path == path,
              "hmctl %s: asked for %s, expected %s" % (name, sent_path, path))
        check(sent["authorization"] == EXPECTED_BASIC,
              "hmctl %s: Authorization was %r" % (name, sent["authorization"]))
        if body is not None:
            check(sent["body"] == body,
                  "hmctl %s: body was %r, expected %r" % (name, sent["body"], body))
        check(completed.returncode == 0,
              "hmctl %s: exit %d - %s" % (name, completed.returncode, completed.stderr.strip()))
        for text in expected:
            check(text in completed.stdout,
                  "hmctl %s: %r is not in the output:\n%s" % (name, text, completed.stdout))


def run_python_refusals(url):
    """The refusals, which are as much of the contract as the successes."""
    def run(arguments, expect_code=None):
        return subprocess.run([sys.executable, HMCTL, "--url", url] + arguments,
                              capture_output=True, text=True, timeout=60,
                              env=dict(os.environ, HMAIL_PASSWORD=PASSWORD, HMAIL_API_KEY=""))

    completed = run(["account", "list"])
    check(completed.returncode != 0 and "--domain" in (completed.stderr + completed.stdout),
          "hmctl account list with no domain should say which flag is missing, said: %s%s"
          % (completed.stdout, completed.stderr))

    completed = run(["domain", "create"])
    check(completed.returncode != 0 and "--set" in (completed.stderr + completed.stdout),
          "hmctl domain create with nothing to set should say so, said: %s%s" % (completed.stdout, completed.stderr))

    completed = run(["domain", "get", "nothing.here"])
    check(completed.returncode == 4,
          "hmctl on a 404 should exit 4, exited %d" % completed.returncode)

    completed = subprocess.run(
        [sys.executable, HMCTL, "--url", "https://example.invalid:8045", "--insecure", "--password", "x", "status"],
        capture_output=True, text=True, timeout=60)
    check(completed.returncode != 0 and "loopback" in completed.stderr,
          "--insecure against a remote host must be refused with a reason, said: %s" % completed.stderr)

    completed = subprocess.run([sys.executable, HMCTL, "--url", url, "status"],
                               capture_output=True, text=True, timeout=60,
                               env=dict((k, v) for k, v in os.environ.items()
                                        if k not in ("HMAIL_PASSWORD", "HMAIL_API_KEY", "HMAIL_PASSWORD_FILE")),
                               stdin=subprocess.DEVNULL)
    check(completed.returncode != 0 and "credential" in completed.stderr,
          "with no credential and no terminal, hmctl must say so rather than prompt: %s" % completed.stderr)


POWERSHELL_CASES = [
    ("Get-HmDomain", "Get-HmDomain | Select-Object -ExpandProperty name", "GET", "/api/v1/domains", None, ["example.com"]),
    ("Get-HmDomain -Name", "Get-HmDomain -Name example.com | Select-Object -ExpandProperty name",
     "GET", "/api/v1/domains/example.com", None, ["example.com"]),
    ("New-HmDomain", "New-HmDomain -Name new.example -Active $true | Out-String",
     "POST", "/api/v1/domains", {"name": "new.example", "active": True}, ["new.example"]),
    ("Remove-HmDomain", "Remove-HmDomain -Name example.net -Confirm:$false; 'removed'",
     "DELETE", "/api/v1/domains/example.net", None, ["removed"]),
    ("Get-HmAccount", "Get-HmAccount -Domain example.com | Select-Object -ExpandProperty address",
     "GET", "/api/v1/domains/example.com/accounts", None, ["ann@example.com"]),
    ("New-HmAccount", "New-HmAccount -Domain example.com -Address new@example.com -Password secret | Out-String",
     "POST", "/api/v1/domains/example.com/accounts", {"address": "new@example.com", "password": "secret"}, ["new@example.com"]),
    ("Set-HmAccount", "Set-HmAccount -Address ann@example.com -Property @{active=$false} | Out-String",
     "PUT", "/api/v1/accounts/ann@example.com", {"active": False}, []),
    ("Get-HmSetting", "Get-HmSetting | Select-Object -ExpandProperty max_message_size_kb",
     "GET", "/api/v1/settings", None, ["20480"]),
    ("Set-HmSetting", "Set-HmSetting -Property @{max_message_size_kb=51200} | Out-String",
     "PUT", "/api/v1/settings", {"max_message_size_kb": 51200}, []),
    ("Set-HmSetting -Group", "Set-HmSetting -Group antispam -Property @{use_spf=$false} | Out-String",
     "PUT", "/api/v1/settings/antispam", {"use_spf": False}, []),
    ("Get-HmQueue", "Get-HmQueue | Select-Object -ExpandProperty to_address", "GET", "/api/v1/queue", None, ["far@away.test"]),
    ("Get-HmStatus", "Get-HmStatus | Select-Object -ExpandProperty version", "GET", "/api/v1/status", None, ["6.3.3"]),
    ("Invoke-HmApi", "Invoke-HmApi -Method GET -Path /api/v1/status | Select-Object -ExpandProperty database",
     "GET", "/api/v1/status", None, ["PostgreSQL"]),
]


def run_powershell_cases(url, verbose):
    pwsh = shutil.which("pwsh") or shutil.which("powershell")
    if not pwsh:
        print("  (no pwsh on this machine: the PowerShell module is not checked here)")
        return False
    if not os.path.exists(MODULE):
        print("  (no HmailServer.psm1: skipped)")
        return False

    for name, script, method, path, body, expected in POWERSHELL_CASES:
        with Recorder.lock:
            Recorder.requests = []

        full = ("$ErrorActionPreference='Stop'; Import-Module '%s' -Force; "
                "Connect-HmServer -Url '%s' -Password (ConvertTo-SecureString '%s' -AsPlainText -Force) | Out-Null; %s"
                % (MODULE.replace("'", "''"), url, PASSWORD, script))
        completed = subprocess.run([pwsh, "-NoProfile", "-NonInteractive", "-Command", full],
                                   capture_output=True, text=True, timeout=120)

        with Recorder.lock:
            requests = list(Recorder.requests)

        if verbose:
            print("  %-34s %s" % (name, requests))

        if not check(requests, "%s: sent no request (%s)" % (name, completed.stderr.strip()[:400])):
            continue

        sent = requests[-1]
        import urllib.parse as parse
        sent_path = parse.unquote(sent["path"].split("?", 1)[0])

        check(sent["method"] == method, "%s: sent %s, expected %s" % (name, sent["method"], method))
        check(sent_path == path, "%s: asked for %s, expected %s" % (name, sent_path, path))
        check(sent["authorization"] == EXPECTED_BASIC, "%s: Authorization was %r" % (name, sent["authorization"]))
        if body is not None:
            check(sent["body"] == body, "%s: body was %r, expected %r" % (name, sent["body"], body))
        check(completed.returncode == 0, "%s: exit %d - %s" % (name, completed.returncode, completed.stderr.strip()[:400]))
        for text in expected:
            check(text in completed.stdout, "%s: %r is not in the output:\n%s" % (name, text, completed.stdout))

    return True


def run_config_cases(url, tmp):
    """The whole configuration as one document: exported, compared, applied.

    Three properties are checked rather than described, because they are what
    makes a document usable in a repository: an export of an unchanged server is
    the same bytes twice, a document that matches produces an empty plan and
    exit 0, and `apply` without --force writes nothing at all.
    """
    def run(arguments):
        return subprocess.run(
            [sys.executable, HMCTL, "--url", url, "--password", PASSWORD] + arguments,
            capture_output=True, text=True, timeout=120)

    document = os.path.join(tmp, "config.json")

    with Recorder.lock:
        Recorder.requests = []
    completed = run(["config", "export", document])
    check(completed.returncode == 0, "config export: exit %d - %s" % (completed.returncode, completed.stderr))
    with Recorder.lock:
        exporting = list(Recorder.requests)
    check(all(r["method"] == "GET" for r in exporting),
          "config export made a request that was not a GET: %s" % sorted({r["method"] for r in exporting}))

    if not os.path.exists(document):
        problem("config export wrote no file")
        return

    with open(document, encoding="utf-8") as handle:
        first = handle.read()

    check('"version": 1' in first, "the document carries no version")
    check("example.com" in first and "ann@example.com" in first,
          "the document holds neither the domain nor its accounts")
    check("all@example.com" in first, "the document holds no distribution list")
    check("mx.partner.test" in first, "the document holds no route")

    second_path = os.path.join(tmp, "config-again.json")
    run(["config", "export", second_path])
    if os.path.exists(second_path):
        with open(second_path, encoding="utf-8") as handle:
            second = handle.read()
        check(first == second, "two exports of an unchanged server are not the same bytes")

    completed = run(["config", "diff", document])
    check(completed.returncode == 0,
          "a document that matches must exit 0, exited %d:\n%s" % (completed.returncode, completed.stdout))
    check("already matches" in completed.stdout, "a matching document should say so:\n%s" % completed.stdout)

    changed = json.loads(first)
    changed["sections"]["settings"]["settings"]["max_message_size_kb"] = 51200
    for row in changed["sections"]["domains"]:
        if row.get("name") == "example.net":
            row["active"] = True
    changed_path = os.path.join(tmp, "changed.json")
    with open(changed_path, "w", encoding="utf-8") as handle:
        json.dump(changed, handle, indent=2, sort_keys=True)

    completed = run(["config", "diff", changed_path])
    check(completed.returncode == 1, "a document that differs must exit 1, exited %d" % completed.returncode)
    check("max_message_size_kb" in completed.stdout,
          "the plan does not name the changed setting:\n%s" % completed.stdout)
    check("example.net" in completed.stdout, "the plan does not name the changed domain:\n%s" % completed.stdout)

    with Recorder.lock:
        Recorder.requests = []
    completed = run(["config", "apply", changed_path])
    with Recorder.lock:
        planning = list(Recorder.requests)
    check(completed.returncode == 0, "apply without --force should exit 0, exited %d" % completed.returncode)
    check(all(r["method"] == "GET" for r in planning),
          "apply without --force wrote something: %s" % [r["method"] for r in planning])
    check("Nothing was changed" in completed.stdout, "apply without --force must say so:\n%s" % completed.stdout)

    with Recorder.lock:
        Recorder.requests = []
    run(["config", "apply", changed_path, "--force"])
    with Recorder.lock:
        applying = list(Recorder.requests)
    writes = [(r["method"], r["path"], r["body"]) for r in applying if r["method"] != "GET"]
    check(("PUT", "/api/v1/settings", {"max_message_size_kb": 51200}) in writes,
          "the setting was not written: %s" % writes)
    check(any(method == "PUT" and path == "/api/v1/domains/example.net" for method, path, _ in writes),
          "the domain was not written: %s" % writes)

    fewer = json.loads(first)
    fewer["sections"]["domains"] = [r for r in fewer["sections"]["domains"] if r.get("name") != "example.net"]
    fewer_path = os.path.join(tmp, "fewer.json")
    with open(fewer_path, "w", encoding="utf-8") as handle:
        json.dump(fewer, handle, indent=2, sort_keys=True)

    with Recorder.lock:
        Recorder.requests = []
    completed = run(["config", "apply", fewer_path, "--force"])
    with Recorder.lock:
        applying = list(Recorder.requests)
    deleted = [r["path"] for r in applying if r["method"] == "DELETE"]
    check(not deleted, "apply --force deleted without --allow-delete: %s" % deleted)
    check("left alone" in completed.stdout, "apply must say what it left alone:\n%s" % completed.stdout)

    with Recorder.lock:
        Recorder.requests = []
    run(["config", "apply", fewer_path, "--force", "--allow-delete"])
    with Recorder.lock:
        applying = list(Recorder.requests)
    check(any(r["method"] == "DELETE" and r["path"] == "/api/v1/domains/example.net" for r in applying),
          "apply --allow-delete did not delete the domain the document dropped: %s"
          % [(r["method"], r["path"]) for r in applying])


def run_import_cases(url, tmp):
    """The CSV import, which is the one verb with logic of its own."""
    path = os.path.join(tmp, "accounts.csv")
    with open(path, "w", encoding="utf-8", newline="") as handle:
        handle.write("address,password,active,max_size_mb\r\n")
        handle.write("ann@example.com,secret,true,100\r\n")       # exists: left alone
        handle.write("carol@example.com,secret2,true,50\r\n")     # made
        handle.write("dave@example.com,secret3,false,\r\n")       # made, empty column ignored
    with Recorder.lock:
        Recorder.requests = []

    completed = subprocess.run(
        [sys.executable, HMCTL, "--url", url, "--password", PASSWORD, "accounts", "import", path, "--dry-run"],
        capture_output=True, text=True, timeout=60)

    with Recorder.lock:
        requests = list(Recorder.requests)

    check(completed.returncode == 0, "accounts import --dry-run: exit %d - %s" % (completed.returncode, completed.stderr))
    check(all(r["method"] == "GET" for r in requests),
          "accounts import --dry-run made a request that was not a GET: %s" % [r["method"] for r in requests])
    check("2 would be made" in completed.stdout,
          "accounts import --dry-run should say two would be made:\n%s" % completed.stdout)
    check("ann@example.com (exists; left alone)" in completed.stdout,
          "accounts import --dry-run should leave the existing account alone and say so:\n%s" % completed.stdout)

    with Recorder.lock:
        Recorder.requests = []

    completed = subprocess.run(
        [sys.executable, HMCTL, "--url", url, "--password", PASSWORD, "accounts", "import", path],
        capture_output=True, text=True, timeout=60)

    with Recorder.lock:
        requests = list(Recorder.requests)

    posts = [r for r in requests if r["method"] == "POST"]
    check(len(posts) == 2, "accounts import should have posted two accounts, posted %d" % len(posts))
    if len(posts) == 2:
        check(posts[0]["body"] == {"address": "carol@example.com", "password": "secret2", "active": True, "max_size_mb": 50},
              "accounts import sent %r for carol" % posts[0]["body"])
        check(posts[1]["body"] == {"address": "dave@example.com", "password": "secret3", "active": False},
              "accounts import sent %r for dave (an empty column must not be sent)" % posts[1]["body"])

    # Export.
    out = os.path.join(tmp, "out.csv")
    completed = subprocess.run(
        [sys.executable, HMCTL, "--url", url, "--password", PASSWORD, "accounts", "export", "example.com", out],
        capture_output=True, text=True, timeout=60)
    check(completed.returncode == 0, "accounts export: exit %d - %s" % (completed.returncode, completed.stderr))
    if os.path.exists(out):
        with open(out, encoding="utf-8") as handle:
            text = handle.read()
        check("address" in text.splitlines()[0], "accounts export wrote no header: %r" % text[:80])
        check("ann@example.com" in text, "accounts export wrote no accounts: %r" % text[:200])
        check("password" not in text,
              "accounts export must never write a password column - the server does not have them to give")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--python", action="store_true", help="hmctl only")
    parser.add_argument("--powershell", action="store_true", help="the PowerShell module only")
    parser.add_argument("--verbose", action="store_true")
    arguments = parser.parse_args()

    if not os.path.exists(HMCTL):
        print("No hmctl at %s" % HMCTL, file=sys.stderr)
        return 2

    server = HTTPServer(("127.0.0.1", 0), Recorder)
    port = server.server_address[1]
    url = "http://127.0.0.1:%d" % port
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()

    import tempfile
    tmp = tempfile.mkdtemp(prefix="hmctl-check-")

    powershell_ran = False
    try:
        if not arguments.powershell:
            print("hmctl against a recorded server")
            run_python_cases(url, arguments.verbose)
            run_python_refusals(url)
            run_import_cases(url, tmp)
            run_config_cases(url, tmp)
        if not arguments.python:
            print("the PowerShell module against the same server")
            powershell_ran = run_powershell_cases(url, arguments.verbose)
    finally:
        server.shutdown()
        shutil.rmtree(tmp, ignore_errors=True)

    print("")
    if PROBLEMS:
        for line in PROBLEMS:
            print("  PROBLEM %s" % line)
        print("")
        print("%d of %d checks failed." % (len(PROBLEMS), CHECKS[0]))
        return 1

    print("%d checks passed%s." % (CHECKS[0], "" if powershell_ran or arguments.python else ", PowerShell skipped"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
