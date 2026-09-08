#!/usr/bin/env python3
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""What a translation can lose that a per-string check cannot see.

check-localisation.py judges one string against its English: the placeholders,
the Alt key, whether it is there at all. Three properties are invisible from
there, because seeing them means reading the string against the rest of the
catalogue, or against what the server does at run time:

  literals  Some English in a caption is not language. The server logs its
            accept-pipeline stages under fixed names - spam-protection,
            script/save - and the stalled-mail page lists them so that an
            administrator can match the page against the log; a translated
            copy matches nothing. The same holds for setting and INI names,
            file names, %TOKEN% and <token>, INBOX, and the syntax a user
            types verbatim. These have to survive translation unchanged.

  pages     Where a caption says "on the Transport security page", the
            translation has to name that page the way that page's own title
            is translated. Naming it anything else sends the reader looking
            for a page that is not in the navigation. Only a reference that
            actually points at a page is checked: "the IP ranges page", not
            the ordinary words "no IP ranges are configured", which a
            translation is right to render as ordinary words.

  numbers   Every RFC number, port, size and count the English states is
            stated by the translation too. Digit-group separators differ
            between languages, so the comparison is on the digits alone -
            210,000 and 210 000 are the same number. A dropped digit is the
            kind of defect that reads perfectly well.

Exits 1 with the language, the catalogue key and what is wrong.
"""
import os
import re
import sys
import xml.etree.ElementTree as ET

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RESOURCES = os.path.join(ROOT, "hmailserver", "source", "Tools", "ControlPanel", "Resources")

# The pages a caption points at. Each is a catalogue key in its own right, so
# the translated title is looked up rather than written here.
PAGE_TITLES = [
   "Anti-spam settings", "Anti-virus settings", "Blocked attachments",
   "Delivery of e-mail", "Transport security", "TCP/IP ports", "IP ranges",
   "SSL certificates", "Live logs", "External setup", "Event scripts",
   "Web services & autoconfiguration", "Auto-ban", "Logging", "Domains",
   "API & monitoring", "Authentication", "App passwords", "Advanced",
   "REST API keys", "Message trace", "Quarantine", "Delivery queue",
   "Public folders", "External accounts", "Routes", "Rules", "Diagnostics",
   "Backup", "Groups",
]

# What the server writes, and what the user types back verbatim.
LITERALS = [
   "spam-protection", "done spam-protection", "script/save", "header-parsing",
   "message-modifications", "AEAD-ONLY", ":hours", "HMBackup",
   "hm_metricsamples", "v=DMARC1", "v=spf1", "v=STSv1", "v=TLSRPTv1",
   "multipart/mixed", "multipart/alternative", "rsa-sha1", "rsa-sha256",
   "objectCategory", "sAMAccountName", "strongAuthRequired", "/api/v1",
   "/metrics", "/livez", "/readyz", "/healthz", "/portal", ".well-known",
   "127.0.0.1", "INBOX", "identd",
]

# An INI or setting name: two or more CamelCase words run together.
SETTING = re.compile(r"\b(?:[A-Z][a-z0-9]+){2,}\b")
FILENAME = re.compile(r"\b[\w.\\-]+\.(?:INI|ini|exe|log|xml|dll|py|ps1|md|resx)\b")
PERCENT = re.compile(r"%[A-Za-z]+%")
ANGLED = re.compile(r"<[a-z][a-z0-9\\_.-]*>")
PLACEHOLDER = re.compile(r"\{\d+(?:[:,][^}]*)?\}")
# Digits, optionally in groups of three. "451. 0" is two numbers, not 4510.
NUMBER = re.compile(r"\d+(?:[ ,. ]\d{3})*")

# CamelCase that is a product or a class rather than a setting a user types,
# and that a translation may reasonably render in its own words.
SETTING_SKIP = {"ControlPanel", "OpenSSL", "OpenTelemetry", "SpamAssassin",
                "ClamWin", "PowerShell", "LiveCharts", "SkiaSharp", "MimeBody",
                "MessageData", "ManageSieve", "LocalSystem", "ChaCha",
                "Postfix", "HAProxy", "AutoDiscover", "AutoConfig",
                "MailServer", "ProgressiveRobot"}


def read_resx(path):
   """{key: value} for one catalogue; a missing file is an empty one."""
   if not os.path.exists(path):
      return {}
   out = {}
   for data in ET.parse(path).getroot().findall("data"):
      value = data.find("value")
      out[data.get("name")] = (value.text or "") if value is not None else ""
   return out


def without_mnemonic(text):
   """The caption as it is read, with the Alt-key underscore taken out.

   The underscore may land inside an identifier - "Timeo_utSeconds" is how a
   collision in that scope was resolved - and the setting name is still on
   screen, so the literal checks accept either form.
   """
   index = 0
   while index < len(text):
      if text[index] != "_":
         index += 1
         continue
      if index + 1 < len(text) and text[index + 1] == "_":
         index += 2
         continue
      return text[:index] + text[index + 1:]
   return text


def digits(text):
   return re.sub(r"[^0-9]", "", text)


def check(tag, catalogue):
   titles = {}
   for title in PAGE_TITLES:
      if title in catalogue and catalogue[title]:
         titles[title] = catalogue[title]

   findings = []
   for source, target in sorted(catalogue.items()):
      if not source or not target:
         continue

      plain = without_mnemonic(target)
      bare = PLACEHOLDER.sub(" ", source)

      wanted = {digits(m.group(0)) for m in NUMBER.finditer(bare)}
      have = {digits(m.group(0)) for m in NUMBER.finditer(PLACEHOLDER.sub(" ", target))}
      for number in sorted(w for w in wanted - have if len(w) >= 2):
         findings.append((source, "the number %s is not in the translation" % number))

      for literal in LITERALS:
         if literal in source and literal not in target and literal not in plain:
            findings.append((source, "%r is what the server writes; the translation changed it" % literal))

      for name in sorted(set(SETTING.findall(source)) - SETTING_SKIP):
         if name not in target and name not in plain:
            findings.append((source, "the setting name %r is not in the translation" % name))

      for pattern in (FILENAME, PERCENT, ANGLED):
         for match in sorted(set(pattern.findall(source))):
            if match not in target and match not in plain:
               findings.append((source, "%r is a literal; the translation changed it" % match))

      if len(source) > 40:
         for title, translated in sorted(titles.items()):
            if source == title or translated in target:
               continue
            pointer = r"(?:the |on the |see the |Open )" + re.escape(title) + r"(?: page| tab)(?![\w-])"
            if re.search(pointer, source):
               findings.append((source, "names the %r page as something other than %r" % (title, translated)))

   return findings


def main():
   if not os.path.isdir(RESOURCES):
      print("No Resources directory at %s" % RESOURCES)
      return 1

   english = read_resx(os.path.join(RESOURCES, "Strings.resx"))
   if not english:
      print("Strings.resx is empty or missing; run check-localisation.py --write first.")
      return 1

   total = 0
   for name in sorted(os.listdir(RESOURCES)):
      match = re.match(r"^Strings\.([A-Za-z-]+)\.resx$", name)
      if not match:
         continue
      tag = match.group(1)
      catalogue = read_resx(os.path.join(RESOURCES, name))
      # Only the keys this catalogue actually translates, keyed by the English.
      catalogue = {key: value for key, value in catalogue.items() if key in english and value}
      findings = check(tag, catalogue)
      total += len(findings)
      for source, what in findings:
         print("  [%s] %s\n        in %r" % (tag, what, source[:110]))

   if total:
      print("FAIL: %d problem(s) across the catalogues" % total)
      return 1

   print("OK    every catalogue keeps the server's own words, the numbers and the page names")
   return 0


if __name__ == "__main__":
   sys.exit(main())
