#!/usr/bin/env python3
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""The Control Panel's translation catalogues agree with its source, and every
language that claims to be complete is.

The English text is the key (Services/Loc.cs). A caption is marked where it is
used - L("_Save changes"), F("Deleted {0} messages", n) or N("Welcome") in C#,
{loc:L '_Save changes'} in XAML - and Resources/Strings.resx, the English
catalogue, is generated from those marks so that it can never drift from them.
Resources/Strings.<culture>.resx holds one language each, keyed by the same
English text.

What is checked
  * Strings.resx holds exactly the set of marked texts, in sorted order, each
    mapped to itself. Run this script with --write after adding or changing a
    marked text; CI fails when the committed file is behind.
  * Every language catalogue: no key that the English catalogue lacks (an
    orphan is a translation of text that no longer exists); every value keeps
    the same {n} placeholders as its English (a lost placeholder is a sentence
    with no subject, an extra one is a FormatException at run time); a caption
    with an Alt-key mnemonic in English carries one in the translation too,
    because the keyboard cannot lose a key by changing language.
  * A language listed in COMPLETE has a translation for every key. Others are
    reported, so a new language can be brought in a page at a time.
  * A source file listed in LOCALISED carries no untranslated caption: no
    literal text reaching a caption, title, header, tool tip, placeholder,
    message box or accessible name without a marker. Files not yet listed are
    reported with their counts - that list is the work remaining, and a file
    joins LOCALISED when its count reaches zero. A literal that is not for
    translation (a protocol name, a brand glyph, an example address) is either
    in EXEMPT below or on a line ending in "// no-loc".

Usage: python3 build/check-localisation.py [--write] [--inventory]
  --write      regenerate Strings.resx and bring every language catalogue up
               to date (orphans dropped, new keys added empty, values kept)
  --inventory  list every untranslated caption in every file, localised or not
Exit status 0 when every check above passes.
"""
import html
import os
import re
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "hmailserver", "source", "Tools", "ControlPanel"))
RESOURCES = os.path.join(ROOT, "Resources")
NEUTRAL = os.path.join(RESOURCES, "Strings.resx")

# Languages whose catalogue must translate every key.
COMPLETE = {"de", "es", "fr", "it", "nl", "pl", "pt-BR", "ru", "sv"}

# Files (relative to ROOT, forward slashes) in which every caption is marked.
LOCALISED = {
   "App.xaml",
   "App.xaml.cs",
   "MainWindow.xaml",
   "MainWindow.xaml.cs",
   "Services/NavigationMap.cs",
   "Services/Toast.cs",
   "Views/BackupView.xaml",
   "Views/BackupView.xaml.cs",
   "Views/ConnectView.xaml",
   "Views/ConnectView.xaml.cs",
   "Views/DashboardView.xaml",
   "Views/DashboardView.xaml.cs",
   "Views/Dialogs.cs",
   "Views/DomainsView.xaml",
   "Views/DomainsView.xaml.cs",
   "Views/FeatureSettingsView.xaml",
   "Views/FeatureSettingsView.xaml.cs",
   "Views/IPRangesView.xaml",
   "Views/IPRangesView.xaml.cs",
   "Views/LogsView.xaml",
   "Views/LogsView.xaml.cs",
   "Views/QueueView.xaml",
   "Views/QueueView.xaml.cs",
   "Views/RoutesView.xaml",
   "Views/RoutesView.xaml.cs",
   "Views/RulesView.xaml",
   "Views/RulesView.xaml.cs",
   "Views/ServerSettingsView.xaml",
   "Views/ServerSettingsView.xaml.cs",
   "Services/SettingClaims.cs",
   "Views/SslCertificatesView.xaml",
   "Views/SslCertificatesView.xaml.cs",
   "Views/StatusView.xaml",
   "Views/StatusView.xaml.cs",
   "Views/TcpIpPortsView.xaml",
   "Views/TcpIpPortsView.xaml.cs",
   "Views/DnsRecordsView.cs",
   "Views/LdapSettingsView.cs",
   "Views/DirectorySyncView.cs",
   "Views/ApiKeysView.cs",
   "Services/ExternalSetupChecks.cs",
   "Services/TlsPosture.cs",
   "Services/SpamPipeline.cs",
   "Services/VirusPipeline.cs",
   "Services/CertificateInspector.cs",
   "Services/ApiKeyStore.cs",
   "Services/ListenerProbe.cs",
   "Services/PaletteSearch.cs",
   "Services/StatusSemantics.cs",
   "Services/ChartCatalog.cs",
   "Services/ChartDataTable.cs",
   "Services/NumericField.cs",
   "Services/WindowsServiceInfo.cs",
   "Services/ActiveDirectoryService.cs",
   "Services/DnsTxtLookup.cs",
   "Services/PathPicker.cs",
   "Services/PasswordStrength.cs",
   "Services/ServerSession.cs",
   "Services/HostReachability.cs",
   "Services/WelcomeIntents.cs",
   "Services/IntentIndex.cs",
   "Services/Typography.cs",
   "Services/IniFeatureStore.cs",
   "Services/MessageStoreConsistencyReport.cs",
   "Services/DirectorySyncReport.cs",
   "Services/DkimKeyGenerator.cs",
   "Services/LanguageChoice.cs",
   "Services/PasswordGenerator.cs",
   "Services/SettingsSearchIndex.cs",
   "Views/StalledMailView.cs",
   "Views/UtilityViews.cs",
   "Views/TlsOverviewView.cs",
   "Views/VirusOverviewView.cs",
   "Views/SpamOverviewView.cs",
   "Views/MessageTraceView.cs",
   "Views/AboutView.cs",
   "Views/QuarantineView.cs",
   "Views/ScriptsView.cs",
   "Views/AccessibleChartCard.cs",
   "Views/PublicFoldersView.cs",
   "Views/WelcomeView.cs",
   "Views/ExternalSetupView.cs",
   "Views/NavigationPalette.cs",
   "Views/Mnemonic.cs",
   "Views/DomainDialog.cs",
   "Views/AccountDialog.cs",
   "Views/CollectionSpecs.cs",
   "Views/FolderPermissionsDialog.cs",
   "Views/TcpIpPortDialog.cs",
   "Views/RouteDialog.cs",
   "Views/IPRangeDialog.cs",
   "Views/GroupMembersDialog.cs",
   "Views/RuleActionDialog.cs",
   "Views/DistributionListDialog.cs",
   "Views/RuleCriteriaDialog.cs",
   "Views/AppPasswordsPanel.cs",
   "Views/RecipientsDialog.cs",
   "Views/ActiveDirectoryPickerDialog.cs",
   "Views/TotpSetupDialog.cs",
   "Views/AdministratorTwoFactorDialog.cs",
   "Views/CollectionEditorView.cs",
   "Views/AccountTwoFactorPanel.cs",
   "Views/MessageViewerDialog.cs",
   "Views/TotpPromptDialog.cs",
   "Views/FieldDialog.cs",
}

# Literal texts that are the same in every language and are not captions to
# translate: protocol and product names, the brand glyph, keyboard chords.
EXEMPT = {
   "hM", "hMailServer", "hMailServer.ini", "Ctrl+K", "localhost", "0.0.0.0",
   "SMTP", "POP3", "IMAP", "SSL/TLS", "TLS", "STARTTLS", "DNS", "ID", "#", "…", "...",
   "—", "-", "OK", "Light", "Dark", "live",
}

CS_LITERAL = r'"((?:[^"\\\n]|\\.)*)"'
CS_MARK = re.compile(r"(?<![\w.])[LNF]\(\s*" + CS_LITERAL)
XAML_MARK = re.compile(r"\{loc:L\s+'((?:[^'\\]|\\.)*)'\s*\}")
XAML_CAPTION_ATTR = re.compile(
   r'\b(?:Text|Header|Content|ToolTip|Title|PlaceholderText|AutomationProperties\.Name|AutomationProperties\.HelpText)="([^"]*)"')
CS_SINK = re.compile(
   r"(\.Show\(|\.Text\s*=|\bTitle\s*=|\bHeader\s*=|\bContent\s*=|\bToolTip\s*=|\bPlaceholderText\s*="
   r"|\bLabel\(|\bMakeButton\(|\bCard\(|\bLabelled\w*\(|\bLabel\s*=|\bBlurb\s*=|\bNote\s*=|\bHint\s*="
   r"|new TextBlock\b|\bSetName\(|\bSetHelpText\(|\bFail_\(|\bSetBusy_\(|\bToast\.\w+\()")
PLACEHOLDER = re.compile(r"\{(\d+)(?:[:,][^}]*)?\}")


def unescape_cs(literal):
   out, i = [], 0
   while i < len(literal):
      c = literal[i]
      if c == "\\" and i + 1 < len(literal):
         n = literal[i + 1]
         if n == "u" and i + 5 < len(literal):
            out.append(chr(int(literal[i + 2:i + 6], 16)))
            i += 6
            continue
         out.append({"n": "\n", "r": "\r", "t": "\t", "\\": "\\", '"': '"', "'": "'", "0": "\0"}.get(n, n))
         i += 2
         continue
      out.append(c)
      i += 1
   return "".join(out)


def unescape_xaml(literal):
   # inside {loc:L '...'}: XML entities first (the attribute), then the
   # markup-extension escapes (\' and \\)
   text = html.unescape(literal)
   return re.sub(r"\\(.)", r"\1", text)


def source_files():
   for base, dirs, files in os.walk(ROOT):
      dirs[:] = [d for d in dirs if d not in ("bin", "obj", "publish", "Resources")]
      for name in sorted(files):
         if name.endswith((".cs", ".xaml")) and not name.endswith(".g.cs"):
            yield os.path.join(base, name)


def rel(path):
   return os.path.relpath(path, ROOT).replace("\\", "/")


def read(path):
   with open(path, encoding="utf-8-sig") as handle:
      return handle.read()


def marked_texts():
   """{english: [locations]} for every marker in the source."""
   found = {}
   for path in source_files():
      src = read(path)
      if path.endswith(".xaml"):
         hits = ((m.start(), unescape_xaml(m.group(1))) for m in XAML_MARK.finditer(src))
      else:
         hits = ((m.start(), unescape_cs(m.group(1))) for m in CS_MARK.finditer(src))
      for pos, text in hits:
         if text:
            found.setdefault(text, []).append(f"{rel(path)}:{src.count(chr(10), 0, pos) + 1}")
   return found


# --- resx ---------------------------------------------------------------------

RESX_HEAD = """<?xml version="1.0" encoding="utf-8"?>
<!-- Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
     SPDX-License-Identifier: AGPL-3.0-or-later -->
<root>
  <!-- {comment} -->
  <resheader name="resmimetype">
    <value>text/microsoft-resx</value>
  </resheader>
  <resheader name="version">
    <value>2.0</value>
  </resheader>
  <resheader name="reader">
    <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
  <resheader name="writer">
    <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
"""

NEUTRAL_COMMENT = ("GENERATED by build/check-localisation.py from the L(...), F(...), N(...) and {loc:L '...'} "
                   "marks in the Control Panel source. Do not edit by hand: change the source and run the "
                   "script with its write option. The English text is the key; a language catalogue beside this file "
                   "(Strings.<culture>.resx) maps each key to its translation.")


def attr(text):
   return (text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;")
           .replace("\r", "&#13;").replace("\n", "&#10;").replace("\t", "&#9;"))


def elem(text):
   # a carriage return as a character reference: an XML parser normalises a
   # literal CR LF in element text to LF, and a message box that says CR LF
   # would come back one character shorter than its key
   return (text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
           .replace("\r", "&#13;"))


def write_resx(path, items, comment):
   lines = [RESX_HEAD.replace("{comment}", comment)]
   for name, value in items:
      lines.append(f'  <data name="{attr(name)}" xml:space="preserve">\n    <value>{elem(value)}</value>\n  </data>\n')
   lines.append("</root>\n")
   with open(path, "w", encoding="utf-8", newline="\n") as handle:
      handle.write("".join(lines))


def read_resx(path):
   """[(name, value)] in file order; a missing file is an empty catalogue."""
   if not os.path.exists(path):
      return []
   tree = ET.parse(path)
   out = []
   for data in tree.getroot().findall("data"):
      value = data.find("value")
      out.append((data.get("name"), (value.text or "") if value is not None else ""))
   return out


def language_files():
   if not os.path.isdir(RESOURCES):
      return []
   out = []
   for name in sorted(os.listdir(RESOURCES)):
      m = re.fullmatch(r"Strings\.([A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*)\.resx", name)
      if m:
         out.append((m.group(1), os.path.join(RESOURCES, name)))
   return out


# --- untranslated captions ------------------------------------------------------

def is_caption(text):
   return re.search(r"[A-Za-z]{2,}", text) is not None and text.strip() not in EXEMPT \
      and not (("@" in text or "." in text) and " " not in text)


NON_UI_CALL = re.compile(
   r"\b(?:GetMetricHistory|TryGetProperty|GetProperty|ReadString|WriteString|ReadBool|WriteBool|ReadInt|WriteInt"
   r"|GetValue|SetValue|ReadFrom|WriteTo|ReadValue|Read|IniRead_|IniWrite_|IniReadInt_|IniWriteInt_|IniReadBool_|IniWriteBool_|LiveBool_|LiveText_|LiveInt_|SecretConfigured_|OpenSubKey|CreateSubKey|SetResourceReference|SetAutomationId|Contains|StartsWith|EndsWith"
   r"|Split|Replace|IndexOf|TryParse|TryParseExact|ParseExact|GetFiles|Path\.Join|Path\.Combine|nameof|Debug\.Fail"
   r"|Debug\.Assert|LogException|RunUpdateAction_|NavigateTo|Slug|GetString|Equals|Compare|Regex|Match|\w+Exception)\s*\(")
NON_UI_CONTEXT = re.compile(r"(?:\bcase\s|==|!=|\bis\s|\[|\bTag\s*=|\bKey\s*=|\bPath\s*=|\bProp\s*=|\bconst\s)\s*$")
# named arguments whose value is a search term list or an icon name, not a caption
NON_UI_ARGUMENT = re.compile(r"\b(?:aliases|seeAlso|icon):")


def cs_untranslated(src):
   """(line, literal) for every literal that reads as a caption and carries no marker.

   A literal is a caption when it holds two consecutive letters and either
   contains a space or begins with a capital letter and looks nothing like an
   identifier, a path or a key. Lines that call something that takes a key, a
   pattern or a path rather than a caption are left out, as are comparisons,
   case labels, indexers and the Tag/Key/Path properties that carry keys."""
   out = []
   lines = src.split("\n")
   for m in re.finditer(r'\$?"((?:[^"\\\n]|\\.)*)"', src):
      raw = m.group(1)
      interpolated = m.group(0).startswith("$")
      text = unescape_cs(re.sub(r"\{[^}]*\}", "", raw) if interpolated else raw)
      if not re.search(r"[A-Za-z]{2,}", text) or text.strip() in EXEMPT:
         continue
      if " " not in text.strip() and not (text[:1].isupper() and not re.search(r"[_./\\:={}<>\[\]@|*]", text)
                                           and not re.search(r"[a-z][A-Z]|\d$", text)):
         continue
      if not is_caption(text) or text.count("|") >= 2:   # a|b|c is a list of search terms
         continue
      line = src.count("\n", 0, m.start()) + 1
      source_line = lines[line - 1]
      stripped = source_line.strip()
      if "// no-loc" in source_line or stripped.startswith(("//", "///", "*", "[")):
         continue
      column = m.start() - (src.rfind("\n", 0, m.start()) + 1)
      head = source_line[:column]
      if "//" in head and head[:head.index("//")].count('"') % 2 == 0:
         continue   # inside a trailing comment
      # a literal continuing a statement from the lines above belongs to the
      # call on the first of them
      statement = source_line
      back = line - 2
      while back >= 0 and statement.lstrip().startswith(('"', "+", "?", ":")) and line - back <= 5:
         statement = lines[back] + "\n" + statement
         back -= 1
      if NON_UI_CALL.search(statement) or NON_UI_ARGUMENT.search(statement):
         continue
      before = src[max(0, m.start() - 40):m.start()]
      if NON_UI_CONTEXT.search(before):
         continue
      if re.search(r"\b[LNF]\(\s*$", before):
         continue
      out.append((line, ("$" if interpolated else "") + unescape_cs(raw)))
   return out


def xaml_untranslated(src):
   out = []
   lines = src.split("\n")
   for m in XAML_CAPTION_ATTR.finditer(src):
      value = html.unescape(m.group(1))
      if value.startswith("{") or not is_caption(value):
         continue
      line = src.count("\n", 0, m.start()) + 1
      if "<!-- no-loc -->" in lines[line - 1]:
         continue
      out.append((line, value))
   return out


def untranslated_by_file():
   result = {}
   for path in source_files():
      src = read(path)
      hits = xaml_untranslated(src) if path.endswith(".xaml") else cs_untranslated(src)
      if hits:
         result[rel(path)] = hits
   return result


# --- main -------------------------------------------------------------------------

def mnemonic_key(caption):
   """The Alt key a caption carries, by the rule MnemonicText.Parse follows, or None."""
   i, shown = 0, []
   key = None
   while i < len(caption):
      c = caption[i]
      if c != "_":
         shown.append(c)
         i += 1
         continue
      if i + 1 < len(caption) and caption[i + 1] == "_":
         shown.append("_")
         i += 2
         continue
      if key is None and i + 1 < len(caption) and not caption[i + 1].isspace():
         key = caption[i + 1].upper()
      else:
         shown.append("_")
      i += 1
   return key


def main():
   if hasattr(sys.stdout, "reconfigure"):
      sys.stdout.reconfigure(encoding="utf-8")
   write = "--write" in sys.argv
   inventory = "--inventory" in sys.argv
   problems = []

   texts = marked_texts()
   wanted = sorted(texts)
   # MSBuild's resource compiler treats names case-insensitively (MSB3568), so
   # two marked texts that differ only in case would be one resource
   folded = {}
   for t in wanted:
      folded.setdefault(t.lower(), []).append(t)
   for group in folded.values():
      if len(group) > 1:
         problems.append("these marked texts differ only in case and the resource compiler treats them as one; reword one: "
                         + ", ".join(repr(t) + " (" + texts[t][0] + ")" for t in group))
   os.makedirs(RESOURCES, exist_ok=True)

   if write:
      write_resx(NEUTRAL, [(t, t) for t in wanted], NEUTRAL_COMMENT)
      for tag, path in language_files():
         existing = dict(read_resx(path))
         comment = read(path)
         m = re.search(r"<!-- (?!Copyright)(.*?) -->", comment, re.S)
         write_resx(path, [(t, existing.get(t, "")) for t in wanted],
                    (m.group(1).strip() if m else f"The {tag} catalogue. Keys are the English text; values are the translation.").replace("--", "-"))

   neutral = read_resx(NEUTRAL)
   if [n for n, _ in neutral] != wanted or any(n != v for n, v in neutral):
      missing = [t for t in wanted if t not in dict(neutral)]
      stale = [n for n, _ in neutral if n not in texts]
      problems.append(f"Resources/Strings.resx is behind the source ({len(missing)} marked text(s) missing, "
                      f"{len(stale)} stale). Run build/check-localisation.py --write and commit the result.")
      for t in missing[:10]:
         problems.append(f"  missing: {t!r} ({texts[t][0]})")
      for t in stale[:10]:
         problems.append(f"  stale:   {t!r}")

   coverage = {}
   for tag, path in language_files():
      entries = read_resx(path)
      known = set(wanted)
      translated = 0
      for name, value in entries:
         if name not in known:
            problems.append(f"Strings.{tag}.resx: {name!r} is not a text the source marks any more; drop it (--write does)")
            continue
         if not value.strip():
            continue
         translated += 1
         if set(PLACEHOLDER.findall(name)) != set(PLACEHOLDER.findall(value)):
            problems.append(f"Strings.{tag}.resx: the placeholders of {name!r} differ in {value!r}")
         if mnemonic_key(name) is not None and mnemonic_key(value) is None:
            problems.append(f"Strings.{tag}.resx: {name!r} carries an Alt key and its translation {value!r} does not")
      coverage[tag] = (translated, len(wanted))
      if tag in COMPLETE and translated < len(wanted):
         untranslated = [n for n in wanted if not dict(entries).get(n, "").strip()]
         problems.append(f"Strings.{tag}.resx claims to be complete and lacks {len(untranslated)} translation(s):")
         for n in untranslated[:20]:
            problems.append(f"  {n!r}")

   remaining = untranslated_by_file()
   for path in sorted(remaining):
      if path in LOCALISED:
         for line, text in remaining[path]:
            problems.append(f"{path}:{line}: untranslated caption {text!r} in a localised file")
   for path in sorted(LOCALISED):
      if not os.path.exists(os.path.join(ROOT, path)):
         problems.append(f"LOCALISED names {path}, which does not exist")

   left = {p: len(h) for p, h in remaining.items() if p not in LOCALISED}
   print(f"{len(wanted)} texts in the catalogue; " +
         ", ".join(f"{tag}: {done}/{total} translated" for tag, (done, total) in sorted(coverage.items())))
   print(f"{len(LOCALISED)} file(s) fully localised; {sum(left.values())} untranslated caption(s) left in {len(left)} file(s)")
   if inventory:
      for path in sorted(remaining, key=lambda p: (-len(remaining[p]), p)):
         print(f"  {path}: {len(remaining[path])}" + ("  (localised)" if path in LOCALISED else ""))
         for line, text in remaining[path]:
            print(f"    {line}: {text[:110]!r}")
   elif left:
      for path in sorted(left, key=lambda p: (-left[p], p))[:12]:
         print(f"  {left[path]:4d} {path}")

   if problems:
      for p in problems:
         print("  " + p)
      print(f"FAIL: {len(problems)} problem(s)")
      return 1
   print("OK    the catalogues match the source and every localised file is fully marked")
   return 0


if __name__ == "__main__":
   sys.exit(main())
