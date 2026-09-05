#!/usr/bin/env python3
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""Every static caption in the Control Panel carries an Alt-key mnemonic, and no
two captions that can be on screen together share one.

Half a set of access keys is worse than none - a keyboard user learns that Alt
does something on one page and then finds it does nothing on the next - so this
is checked rather than hoped. The rule is WPF's own: a single underscore in a
caption marks the key ("_Save changes" is Alt+S), a doubled underscore is a
literal one. Buttons and check boxes read that through their templates; the
captions above editors go through Views/Mnemonic.cs, which underlines the
letter and registers the key for the editor.

What is checked
  * XAML views: every Button, CheckBox and RadioButton with a literal Content,
    outside a DataTemplate (a caption repeated per row cannot have a unique
    key, and the rows are reached with the arrow keys). The scope for
    uniqueness is the file: a view is one page, all of it on screen at once.
  * Code-built views and dialogs: every Button/CheckBox/RadioButton initialised
    with a literal Content, every MakeButton("..."), every Label("...", editor)
    and Labelled*_("...", editor) caption, and every "<something>Button.Content =
    "..."" assignment. The scope is the method that places the control, because
    a dialog's tabs are built one per method and only one tab is visible at a
    time; a field is placed by the method that first mentions it. Keys in the
    constructor (the Save button under every tab) must not collide with any
    method's keys.
  * Exempt: Cancel, OK and Close (Escape and Enter are their keys, by platform
    convention), the "…" browse buttons, and captions that come from data.

Usage: python3 build/check-mnemonics.py [--list]
Exit status 0 when every caption is covered and every scope is collision-free.
"""
import os
import re
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "hmailserver", "source", "Tools", "ControlPanel")
EXEMPT = {"Cancel", "OK", "Close", "…", "..."}


def parse(caption):
    """The display text and the key index, by the rule MnemonicText.Parse follows."""
    text, key = [], -1
    i = 0
    while i < len(caption):
        c = caption[i]
        if c != "_":
            text.append(c)
            i += 1
            continue
        if i + 1 < len(caption) and caption[i + 1] == "_":
            text.append("_")
            i += 2
            continue
        if key < 0 and i + 1 < len(caption) and not caption[i + 1].isspace():
            key = len(text)
        else:
            text.append("_")
        i += 1
    shown = "".join(text)
    return shown, (shown[key].upper() if key >= 0 else None)


def unescape_cs(literal):
    return literal.replace("\\u2026", "…").replace('\\"', '"')


class Caption:
    def __init__(self, file, line, scope, raw, class_name=None):
        self.file, self.line, self.scope, self.raw = file, line, scope, raw
        self.class_name = class_name
        self.text, self.key = parse(raw)


def line_of(text, pos):
    return text.count("\n", 0, pos) + 1


def xaml_captions(path):
    src = open(path, encoding="utf-8").read()
    # DataTemplate ranges
    inside = []
    depth = 0
    for m in re.finditer(r"<(/?)DataTemplate\b", src):
        if m.group(1):
            depth -= 1
            if depth == 0:
                inside[-1] = (inside[-1][0], m.end())
        else:
            if depth == 0:
                inside.append((m.start(), None))
            depth += 1

    def templated(pos):
        return any(a <= pos < (b or len(src)) for a, b in inside)

    out = []
    for m in re.finditer(r'<(?:ui:)?(?:Button|CheckBox|RadioButton|ToggleButton)\b[^>]*?\bContent="([^"]*)"', src, re.S):
        if templated(m.start()):
            continue
        out.append(Caption(path, line_of(src, m.start()), os.path.basename(path), m.group(1)))
    return out


def body_ranges(src, header):
    """(name, start, end) for every body introduced by a header match, by brace counting."""
    ranges = []
    for m in re.finditer(header, src, re.M):
        name = m.group(1)
        depth, i = 0, m.end() - 1
        while i < len(src):
            c = src[i]
            if c == '"':  # skip string literals
                i += 1
                while i < len(src) and src[i] != '"':
                    i += 2 if src[i] == "\\" else 1
            elif c == "'":
                i += 1
                while i < len(src) and src[i] != "'":
                    i += 2 if src[i] == "\\" else 1
            elif src.startswith("//", i):
                i = src.find("\n", i)
                if i < 0:
                    i = len(src)
            elif c == "{":
                depth += 1
            elif c == "}":
                depth -= 1
                if depth == 0:
                    ranges.append((name, m.start(), i))
                    break
            i += 1
    return ranges


METHOD = r"^\s+(?:public|private|protected|internal)[^\n;=]*?\b(\w+)\s*\([^;{]*?\)\s*(?:where[^{]*)?\n\s*\{"
CLASS = r"^\s*(?:public|private|protected|internal|static|sealed|partial|abstract|\s)*class\s+(\w+)[^\n{]*\n\s*\{"
LITERAL = r"\"((?:[^\"\\]|\\.)*)\""


def cs_captions(path):
    src = open(path, encoding="utf-8").read()
    classes = body_ranges(src, CLASS)
    methods = body_ranges(src, METHOD)
    lines = src.split("\n")

    def enclosing(ranges, pos, innermost=True):
        hits = [(name, a, b) for name, a, b in ranges if a <= pos <= b]
        if not hits:
            return None
        return min(hits, key=lambda r: r[2] - r[1]) if innermost else max(hits, key=lambda r: r[2] - r[1])

    def scope_for(pos, field=None):
        """A class shown as a whole is one scope; a class with tabs is one scope per
        method, its constructor global to all of them. A field belongs to the method
        that first mentions it."""
        cls = enclosing(classes, pos)
        class_name = cls[0] if cls else ""
        tabbed = bool(cls) and "new TabItem" in src[cls[1]:cls[2]]
        if not tabbed:
            return class_name, class_name
        method = enclosing(methods, pos)
        if method is None and field:
            users = [(name, a, b) for name, a, b in methods
                     if cls[1] <= a <= cls[2] and re.search(r"\b" + re.escape(field) + r"\b", src[a:b])]
            # the method that builds the tab, when one does; otherwise the first that mentions it
            builders = [u for u in users if u[0].startswith("Build")]
            method = (builders or users or [None])[0]
        return (method[0] if method else class_name), class_name

    def add(out, pos, literal, field=None, end=None):
        line = line_of(src, pos)
        # a caption repeated per row, card, record or setting cannot have a unique key, and says so
        # in a comment on the initialiser or on the literal; the rows are reached with the arrow keys
        span = "\n".join(lines[line - 1:line_of(src, end if end is not None else pos)])
        if re.search(r"//.*\bper (row|card|record|setting)\b", span):
            return
        scope, class_name = scope_for(pos, field)
        out.append(Caption(path, line, scope, unescape_cs(literal), class_name))

    out = []
    kinds = r"(?:Button|CheckBox|RadioButton|ToggleButton)"
    # new Type { Content = "..." }
    for m in re.finditer(r"new\s+(?:Wpf\.Ui\.Controls\.)?" + kinds + r"\b(?:\(\))?\s*\{(?:[^{}]|\{[^{}]*\})*?\bContent\s*=\s*" + LITERAL, src, re.S):
        add(out, m.start(), m.group(1), end=m.end())
    # Type name = new() { Content = "..." }
    for m in re.finditer(r"\b" + kinds + r"\s+(\w+)\s*=\s*new\s*\(\)\s*\{(?:[^{}]|\{[^{}]*\})*?\bContent\s*=\s*" + LITERAL, src, re.S):
        add(out, m.start(), m.group(2), field=m.group(1), end=m.end())
    for m in re.finditer(r"\bMakeButton\(\s*" + LITERAL, src):
        add(out, m.start(), m.group(1))
    for m in re.finditer(r"\bLabel\(\s*" + LITERAL + r"\s*,\s*\w+\s*\)", src):
        add(out, m.start(), m.group(1))
    for m in re.finditer(r"\bLabelled(?:Box|Combo)_\(\s*" + LITERAL, src):
        add(out, m.start(), m.group(1))
    for m in re.finditer(r"\bLabelledCheck_\(\s*\w+\s*,\s*" + LITERAL, src):
        add(out, m.start(), m.group(1))
    # something.Content = "..." (a button or check box whose caption changes with state)
    for m in re.finditer(r"\b\w+\.Content\s*=\s*(?:[^;\"]*\?\s*)?" + LITERAL + r"(?:\s*:\s*" + LITERAL + r")?", src):
        for g in (m.group(1), m.group(2)):
            if g is not None:
                add(out, m.start(), g)
    return out


def main():
    listing = "--list" in sys.argv
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    problems = []
    all_captions = []
    views = os.path.join(ROOT, "Views")
    files = sorted(os.path.join(views, f) for f in os.listdir(views)) + [os.path.join(ROOT, "MainWindow.xaml")]
    for path in files:
        if path.endswith(".xaml"):
            captions = xaml_captions(path)
        elif path.endswith(".cs"):
            captions = cs_captions(path)
        else:
            continue
        all_captions += captions
        rel = os.path.relpath(path, ROOT)
        # coverage
        for c in captions:
            if c.text in EXEMPT:
                continue
            if c.key is None:
                problems.append(f"{rel}:{c.line}: no mnemonic in \"{c.raw}\"")
        # uniqueness per scope; a tabbed class's constructor scope is global to its methods
        by_scope = {}
        for c in captions:
            if c.key:
                by_scope.setdefault(c.scope, []).append(c)
        for scope, items in by_scope.items():
            ctor = by_scope.get(items[0].class_name, []) if items[0].class_name and items[0].class_name != scope else []
            seen = {}
            for c in items + ctor:
                if c.key in seen:
                    other = seen[c.key]
                    if c in items:
                        problems.append(f"{rel}:{c.line}: Alt+{c.key} in \"{c.raw}\" collides with \"{other.raw}\" (line {other.line}, scope {scope})")
                else:
                    seen[c.key] = c
    if listing:
        for c in all_captions:
            print(f"{os.path.relpath(c.file, ROOT)}:{c.line}: [{c.scope}] {'Alt+' + c.key if c.key else '   -'}  {c.text}")
    covered = sum(1 for c in all_captions if c.key)
    exempt = sum(1 for c in all_captions if c.text in EXEMPT)
    print(f"{len(all_captions)} static captions: {covered} with a mnemonic, {exempt} exempt, {len(all_captions) - covered - exempt} missing")
    if problems:
        # one line per problem, deduplicated, in file order
        for p in sorted(set(problems), key=lambda s: (s.split(":")[0], int(s.split(":")[1]))):
            print("  " + p)
        print(f"FAIL: {len(set(problems))} problem(s)")
        return 1
    print("OK    every static caption carries a mnemonic and no scope has a collision")
    return 0


if __name__ == "__main__":
    sys.exit(main())
