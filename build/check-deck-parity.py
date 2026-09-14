# https://www.progressiverobot.com
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
# SPDX-License-Identifier: AGPL-3.0-or-later

"""How far the Control Deck and the REST API are from the desktop Control Panel.

The desktop Control Panel drives the server over COM; the Control Deck (the
browser page at /WebAdmin) drives it over the REST API. This measures the gap
between them from the sources alone: every COM property the desktop program
writes, whether a REST route writes the same property, and whether a Deck view
calls that route. It is a measure and not a check - the exit code is 0 whatever
the numbers are, unless --strict is given, in which case a non-empty missing
list exits 1.

What is read:

  hmailserver/source/Server/hMailServer/hMailServer.idl
      every interface with its properties (get/put) and methods, and the
      interface each one returns, so that a chain such as
      Application.Domains.ItemByName[x].Accounts can be walked to a type.
  hmailserver/source/Server/COM/Interface*.cpp
      the C++ setter each put_ property calls (put_MaxNumberOfAccounts calls
      SetMaxNoOfAccounts), which is a second name a REST handler can be matched
      on when the JSON key is spelled differently from the COM property.
  hmailserver/source/Tools/ControlPanel/Views/*.cs (and the .xaml page list)
      three ways the desktop program reaches COM: the data-driven settings
      editors (new ComText { Path = "AntiSpam.SpamMarkThreshold" }, resolved
      against Settings), the collection editors (a CollectionSpec's
      GetCollection chain plus its FieldSpec Props), and dynamic locals
      (dynamic domain = domains.ItemByName[...]; domain.Postmaster = ...).
  hmailserver/source/Server/Common/Util/RestApi*.cpp
      the routes, from the OpenAPI document the server emits as string
      literals (path, method, request-schema properties, the "Body: ..."
      sentence) and from the settings group tables (a Row's key, its access
      and the setter it calls); plus the keys each handler reads from its
      body (ReadString(body, "postmaster"), JsonUtf8Value_(requestBody, "name"))
      and the setters it calls, attached to the route whose keys they share.
  hmailserver/installation/WebAdmin/index.html
      the data-view names, the render function of each, the functions those
      reach (directly, or through the data-act buttons their HTML emits and the
      click dispatcher maps), and the api(...) calls in them.

Name matching is heuristic. A desktop write of Interface.Property counts as
writable over REST when a POST, PUT or PATCH route whose path names that
interface's resource carries a key that matches the property by one of three
channels: the C++ setter the COM put_ calls is one the route's handler calls;
the names fold to the same string (case, underscores and a trailing unit such
as _kb or _mb ignored); or the names split into the same words once filler
words (of, number, ...) are dropped. The channel is reported per property so a
match can be judged. Routes under /api/v1/me and /api/v1/portal act on the
caller's own account and are not counted: they are the self-service surface,
not administration.

Usage:
  python build/check-deck-parity.py            # print the report, write docs/DeckParity.md
  python build/check-deck-parity.py --strict   # exit 1 when anything is missing
  python build/check-deck-parity.py --no-doc   # print only
"""

import os
import re
import sys
from collections import defaultdict, OrderedDict

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
IDL = os.path.join(ROOT, 'hmailserver', 'source', 'Server', 'hMailServer', 'hMailServer.idl')
COM_DIR = os.path.join(ROOT, 'hmailserver', 'source', 'Server', 'COM')
VIEWS_DIR = os.path.join(ROOT, 'hmailserver', 'source', 'Tools', 'ControlPanel', 'Views')
REST_DIR = os.path.join(ROOT, 'hmailserver', 'source', 'Server', 'Common', 'Util')
DECK = os.path.join(ROOT, 'hmailserver', 'installation', 'WebAdmin', 'index.html')
DOC = os.path.join(ROOT, 'hmailserver', 'docs', 'DeckParity.md')

WRITE_METHODS = ('post', 'put', 'patch')

# The resource a path segment names, as IDL interface short names (IInterfaceX
# without the prefix). An empty list is a resource with no COM interface behind
# it (API keys, the INI file); a segment missing from the table leaves the
# route's scope unknown, which matches any interface and is flagged.
PATH_SCOPE = {
    'domains': ['Domain'],
    'domain-aliases': ['DomainAlias'],
    'accounts': ['Account'],
    'aliases': ['Alias'],
    'lists': ['DistributionList'],
    'members': ['DistributionListRecipient'],
    'recipients': ['DistributionListRecipient'],
    'rules': ['Rule', 'RuleCriteria', 'RuleAction'],
    'routes': ['Route', 'RouteAddress'],
    'certificates': ['SSLCertificate'],
    'ports': ['TCPIPPort'],
    'ipranges': ['SecurityRange'],
    'dns-blacklists': ['DNSBlackList'],
    'surbl-servers': ['SURBLServer'],
    'whitelist-addresses': ['WhiteListAddress'],
    'blocked-senders': ['BlockedSender'],
    'incoming-relays': ['IncomingRelay'],
    'fetch-accounts': ['FetchAccount'],
    'settings': ['Settings'],
    'antispam': ['AntiSpam'],
    'antivirus': ['AntiVirus'],
    'logging': ['Logging'],
    'scripting': ['Scripting'],
    'backup': ['BackupSettings', 'Backup'],
    'messages': ['ServerMessage'],
    'dkim': ['Domain'],
    'app-passwords': ['AppPassword'],
    'groups': ['Group'],
    'folders': ['IMAPFolder'],
    'permissions': ['IMAPFolderPermission'],
    'apikeys': [],
    'ini': [],
    'directories': [],
    'support-session': [],
    'session': [],
    'status': [],
    'logs': [],
    'tlsa': [],
    'srv': [],
    'update': [],
    'queue': [],
    'quarantine': [],
    'archive': [],
    'scheduled': [],
    'sieve': [],
    'server': [],
    'metrics': [],
    'logon-failures': [],
    'openapi.json': [],
}

# Trailing path segments that are verbs on the resource before them.
VERB_SEGMENTS = {'reload', 'check', 'clear', 'retry', 'release', 'hold', 'download',
                 'run', 'install', 'reinitialize', 'evaluate', 'history', 'start'}

UNIT_SUFFIXES = ('seconds', 'minutes', 'hours', 'days', 'secs', 'mins', 'kb', 'mb', 'ms', 'sec', 'min')
STOP_WORDS = {'of', 'number', 'no', 'the', 'a', 'an', 'is', 'in', 'to', 'use',
              'kb', 'mb', 'seconds', 'sec', 'secs', 'minutes', 'min', 'mins', 'hours', 'days', 'ms'}

# Words a COM name carries that the API key of the same setting leaves out
# (SSLCertificateID and certificate_id, RequireSMTPAuth and require_auth,
# LowerIP and lower). Dropped from the COM side only, and the match is reported
# as loose.
QUALIFIER_WORDS = {'ssl', 'tls', 'smtp', 'anti', 'spam', 'ip', 'enable'}


# ---------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------

def read(path):
    with open(path, 'r', encoding='utf-8-sig', errors='replace') as f:
        return f.read()


def fold(name):
    return re.sub(r'[^a-z0-9]', '', name.lower())


def fold_variants(name):
    """The folded name, and the folded name without a trailing unit."""
    out = {fold(name)}
    words = word_list(name)
    if len(words) > 1 and words[-1] in UNIT_SUFFIXES:
        out.add(''.join(words[:-1]))
    return out


def word_list(name):
    """CamelCase or snake_case split into lower-case words."""
    if '_' in name:
        return [w for w in name.lower().split('_') if w]
    return [w.lower() for w in re.findall(r'[A-Z]+(?=[A-Z][a-z])|[A-Z]?[a-z]+|[A-Z]+|\d+', name)]


def singular(word):
    if word.endswith('ies') and len(word) > 4:
        return word[:-3] + 'y'
    if word.endswith('es') and word[-3:-2] in ('s', 'x', 'h'):
        return word[:-2]
    if word.endswith('s') and not word.endswith('ss') and len(word) > 3:
        return word[:-1]
    return word


def word_set(name):
    return frozenset(singular(w) for w in word_list(name) if w not in STOP_WORDS)


def balanced(text, start, open_ch='{', close_ch='}'):
    """The text of a balanced block whose opening bracket is at text[start]."""
    depth = 0
    i = start
    n = len(text)
    while i < n:
        c = text[i]
        if c == open_ch:
            depth += 1
        elif c == close_ch:
            depth -= 1
            if depth == 0:
                return text[start + 1:i]
        i += 1
    return text[start + 1:]


def strip_c_comments(text):
    text = re.sub(r'/\*.*?\*/', '', text, flags=re.S)
    text = re.sub(r'//[^\n]*', '', text)
    return text


def blank_strings(text):
    """String literal contents replaced, so that a chain in a string is not read as code."""
    return re.sub(r'"(?:[^"\\\n]|\\.)*"', '""', text)


# ---------------------------------------------------------------------------
# 1. The COM surface
# ---------------------------------------------------------------------------

class Interface:
    def __init__(self, name):
        self.name = name                 # IInterfaceDomain
        self.short = name[len('IInterface'):] if name.startswith('IInterface') else name
        self.props = OrderedDict()       # name -> {'get': bool, 'put': bool, 'type': retval}
        self.methods = OrderedDict()     # name -> retval type

    def member_type(self, member):
        if member in self.props:
            return self.props[member]['type']
        if member in self.methods:
            return self.methods[member]
        return None

    def item_type(self):
        for m in ('Item', 'Add', 'ItemByDBID', 'ItemByName', 'ItemByAddress'):
            t = self.member_type(m)
            if t and t.startswith('IInterface'):
                return t
        return None


IFACE_RE = re.compile(r'^\s*interface\s+(\w+)\s*:\s*IDispatch\s*\{(.*?)^\s*\};', re.S | re.M)
MEMBER_RE = re.compile(r'\[([^\]]*)\]\s*HRESULT\s+(\w+)\s*\(([^)]*)\)')


def read_idl():
    text = read(IDL)
    interfaces = OrderedDict()
    for m in IFACE_RE.finditer(text):
        iface = Interface(m.group(1))
        for attrs, name, params in MEMBER_RE.findall(m.group(2)):
            retval = re.search(r'retval\]\s*(\w+)', params)
            rtype = retval.group(1) if retval else None
            if 'propget' in attrs or 'propput' in attrs:
                p = iface.props.setdefault(name, {'get': False, 'put': False, 'type': None})
                if 'propget' in attrs:
                    p['get'] = True
                    p['type'] = rtype
                if 'propput' in attrs:
                    p['put'] = True
            else:
                iface.methods[name] = rtype
        interfaces[iface.name] = iface
    return interfaces


def read_com_setters(interfaces):
    """(short interface, property) -> folded C++ setter names its put_ calls."""
    setters = defaultdict(set)
    put_re = re.compile(r'STDMETHODIMP\s+Interface(\w+)::put_(\w+)\s*\(')
    for name in sorted(os.listdir(COM_DIR)):
        if not (name.startswith('Interface') and name.endswith('.cpp')):
            continue
        text = strip_c_comments(read(os.path.join(COM_DIR, name)))
        heads = list(put_re.finditer(text))
        for i, h in enumerate(heads):
            end = heads[i + 1].start() if i + 1 < len(heads) else len(text)
            body = text[h.end():end]
            body = body[:body.find('\n}')] if '\n}' in body else body
            for s in re.findall(r'\b(Set[A-Z]\w*)\s*\(', body):
                setters[(h.group(1), h.group(2))].add(fold(s))
    return setters


# ---------------------------------------------------------------------------
# 2. The desktop Control Panel
# ---------------------------------------------------------------------------

class Access:
    """One COM access the desktop program makes."""
    def __init__(self, iface, member, kind, page, how):
        self.iface = iface       # short interface name
        self.member = member
        self.kind = kind         # 'write' | 'read' | 'method'
        self.page = page
        self.how = how           # 'settings editor' | 'collection editor' | 'dynamic' | 'InvokeMember'


class ChainWalker:
    def __init__(self, interfaces):
        self.interfaces = interfaces
        self.app = interfaces.get('IInterfaceApplication')

    @staticmethod
    def top_level_index(expr, ch):
        depth = 0
        for i, c in enumerate(expr):
            if c in '([':
                depth += 1
            elif c in ')]':
                depth -= 1
            elif c == ch and depth == 0:
                return i
        return -1

    def split(self, expr):
        """A member chain split on the dots outside brackets and parentheses."""
        parts, depth, cur = [], 0, ''
        for c in expr:
            if c in '([':
                depth += 1
            elif c in ')]':
                depth -= 1
            if c == '.' and depth == 0:
                parts.append(cur)
                cur = ''
            else:
                cur += c
        parts.append(cur)
        return [p.strip() for p in parts]

    def step(self, iface, token):
        """The interface reached by one member token from iface, or None."""
        if iface is None:
            return None
        name = re.match(r'\s*(\w+)', token)
        if not name:
            return None
        name = name.group(1)
        t = iface.member_type(name)
        if t is None:
            return None
        nxt = self.interfaces.get(t)
        return nxt

    def resolve(self, expr, env, methods):
        """The interface an expression evaluates to, or None."""
        expr = expr.strip().rstrip(';').strip()
        expr = re.sub(r'^\(\s*(?:dynamic|object)\s*\)\s*', '', expr)
        # A conditional (id == 0 ? actions.Add() : actions.ItemByDBID[id]) is
        # typed by whichever branch resolves; both are the same object here.
        q = self.top_level_index(expr, '?')
        if q >= 0:
            rest = expr[q + 1:]
            colon = self.top_level_index(rest, ':')
            branches = [rest[:colon], rest[colon + 1:]] if colon >= 0 else [rest]
            for b in branches:
                t = self.resolve(b, env, methods)
                if t:
                    return t
            return None
        expr = expr.strip('()') if expr.startswith('(') and expr.endswith(')') and expr.count('(') == 1 else expr
        tokens = self.split(expr)
        if not tokens:
            return None
        cur = None
        i = 0
        if len(tokens) >= 3 and tokens[0] == 'ServerSession' and tokens[1] == 'Current' and tokens[2].startswith('Application'):
            cur, i = self.app, 3
        elif len(tokens) >= 2 and tokens[0] in ('session', 'session_') and tokens[1].startswith('Application'):
            cur, i = self.app, 2
        else:
            head = tokens[0]
            m = re.match(r'(\w+)\s*(\(|\[)?', head)
            if not m:
                return None
            ident = m.group(1)
            if m.group(2) == '(' and ident in methods:
                cur = methods[ident]
            elif ident in env:
                cur = env[ident]
                if m.group(2) == '[':
                    cur = self.step(cur, 'Item')
            else:
                return None
            i = 1
        while i < len(tokens) and cur is not None:
            cur = self.step(cur, tokens[i])
            i += 1
        return cur


class DesktopReader:
    def __init__(self, interfaces):
        self.interfaces = interfaces
        self.walker = ChainWalker(interfaces)
        self.settings = interfaces.get('IInterfaceSettings')
        self.accesses = []
        self.pages = []
        self.unresolved_paths = []

    def read_all(self):
        for name in sorted(os.listdir(VIEWS_DIR)):
            if name.endswith('.xaml'):
                self.pages.append(name[:-len('.xaml')])
        for name in sorted(os.listdir(VIEWS_DIR)):
            if not name.endswith('.cs'):
                continue
            page = name[:-len('.xaml.cs')] if name.endswith('.xaml.cs') else name[:-len('.cs')]
            if page not in self.pages:
                self.pages.append(page)
            self.read_file(page, read(os.path.join(VIEWS_DIR, name)))
        return self.accesses

    # -- the settings editors ------------------------------------------------

    def walk_settings_path(self, path):
        """('Interface', 'Member') for a dotted path under Settings, or None."""
        parts = path.split('.')
        cur = self.settings
        for p in parts[:-1]:
            cur = self.walker.step(cur, p)
            if cur is None:
                return None
        return cur, parts[-1]

    def read_editors(self, page, text):
        for m in re.finditer(r'new\s+(Com\w+)\s*\{', text):
            kind = m.group(1)
            body = balanced(text, m.end() - 1)
            path = re.search(r'\bPath\s*=\s*"([^"]*)"', body)
            method = re.search(r'\bMethodName\s*=\s*"(\w+)"', body)
            if kind in ('ComPassword',):
                name = method.group(1) if method else (path.group(1).split('.')[-1] if path else None)
                if name:
                    parent = path.group(1) if path else name
                    owner = self.walk_settings_path(parent)
                    if owner:
                        self.accesses.append(Access(owner[0].short, name, 'method', page, 'settings editor'))
                continue
            if not path or not path.group(1):
                continue
            owner = self.walk_settings_path(path.group(1))
            if not owner:
                self.unresolved_paths.append((page, path.group(1)))
                continue
            iface, member = owner
            if kind in ('ComBool', 'ComText', 'ComCombo'):
                if member in iface.props and iface.props[member]['put']:
                    self.accesses.append(Access(iface.short, member, 'write', page, 'settings editor'))
                elif member in iface.props:
                    self.accesses.append(Access(iface.short, member, 'read', page, 'settings editor'))
                else:
                    self.unresolved_paths.append((page, path.group(1)))
            elif kind in ('ComStat', 'ComInert', 'ComAction'):
                if member in iface.props:
                    self.accesses.append(Access(iface.short, member, 'read', page, 'settings editor'))
                elif member in iface.methods:
                    self.accesses.append(Access(iface.short, member, 'method', page, 'settings editor'))

    # -- the collection editors ---------------------------------------------

    def read_collections(self, page, text, env, methods):
        for m in re.finditer(r'new\s+CollectionSpec\s*\{', text):
            body = balanced(text, m.end() - 1)
            chain = re.search(r'GetCollection\s*=\s*\(\)\s*=>\s*([^,\n]+)', body)
            if not chain:
                continue
            expr = chain.group(1).strip()
            if expr.startswith('{'):
                # A block-bodied lambda: the collection is the local it returns,
                # which the file-level environment has typed already.
                block = balanced(body, chain.start(1) + chain.group(1).index('{'))
                r = re.search(r'\breturn\s+([\w.\[\]()]+)\s*;', block)
                expr = r.group(1) if r else ''
            coll = self.walker.resolve(expr, env, methods) if expr else None
            item = self.interfaces.get(coll.item_type()) if coll and coll.item_type() else None
            if item is None:
                self.unresolved_paths.append((page, 'GetCollection ' + chain.group(1).strip()))
                continue
            for prop in re.findall(r'new\s+FieldSpec\s*\{[^}]*?\bProp\s*=\s*"(\w+)"', body):
                if prop in item.props and item.props[prop]['put']:
                    self.accesses.append(Access(item.short, prop, 'write', page, 'collection editor'))
                elif prop in item.props:
                    self.accesses.append(Access(item.short, prop, 'read', page, 'collection editor'))
                else:
                    self.unresolved_paths.append((page, item.short + '.' + prop))

    # -- dynamic locals ------------------------------------------------------

    def build_env(self, code):
        env = {}
        methods = {}
        # Static accessors: private static dynamic AntiSpam => ...Settings.AntiSpam;
        for name, expr in re.findall(r'\bdynamic\s+(\w+)\s*=>\s*([^;]+);', code):
            t = self.walker.resolve(expr, env, methods)
            if t:
                env[name] = t
        dynamic_names = set(re.findall(r'\bdynamic\s+(\w+)\b', code))
        method_returns = {}
        for m in re.finditer(r'\bdynamic\s+(\w+)\s*\([^)]*\)\s*\{', code):
            body = balanced(code, m.end() - 1)
            r = re.search(r'\breturn\s+([\w.\[\]()]+)\s*;', body)
            if r:
                method_returns[m.group(1)] = r.group(1)
        # Expression-bodied: private dynamic FindRule(dynamic rules) => rules.ItemByDBID[ruleId_];
        for name, expr in re.findall(r'\bdynamic\s+(\w+)\s*\([^)]*\)\s*=>\s*([^;]+);', code):
            method_returns.setdefault(name, expr)
        decl_re = re.compile(r'\b(?:dynamic|var)\s+(\w+)\s*=\s*([^;]+);')
        assign_re = re.compile(r'(?<![\w.])(\w+)\s*=(?!=)\s*([^;=][^;]*);')

        def settle():
            for _ in range(6):
                changed = False
                for name, expr in decl_re.findall(code):
                    if name in env:
                        continue
                    t = self.walker.resolve(expr, env, methods)
                    if t:
                        env[name] = t
                        changed = True
                for name, expr in assign_re.findall(code):
                    if name in env or name not in dynamic_names:
                        continue
                    t = self.walker.resolve(expr, env, methods)
                    if t:
                        env[name] = t
                        changed = True
                for name, expr in method_returns.items():
                    if name in methods:
                        continue
                    t = self.walker.resolve(expr, env, methods)
                    if t:
                        methods[name] = t
                        changed = True
                if not changed:
                    break

        settle()
        # A dynamic parameter or field that never resolved (a delegate's result,
        # a parameter) is typed by its name when the name is an interface's own
        # (domain, account, rules ...); then settle again, since a method such
        # as FindRule(rules) may resolve only through it.
        by_short = {fold(i.short): i for i in self.interfaces.values()}
        for name in dynamic_names:
            if name in env:
                continue
            key = fold(name.rstrip('_'))
            if key in by_short:
                env[name] = by_short[key]
        settle()
        return env, methods

    def read_dynamic(self, page, text):
        code = blank_strings(text)
        env, methods = self.build_env(code)
        chain = r'([A-Za-z_]\w*(?:\s*(?:\[[^\]]*\]|\([^()]*\)))?(?:\s*\.\s*[A-Za-z_]\w*(?:\s*(?:\[[^\]]*\]|\([^()]*\)))?)*)'
        write_re = re.compile(chain + r'\s*\.\s*([A-Za-z_]\w*)\s*=(?!=)')
        read_re = re.compile(chain + r'\s*\.\s*([A-Za-z_]\w*)\b(?!\s*(?:=(?!=)|\(|\[))')
        method_re = re.compile(chain + r'\s*\.\s*([A-Za-z_]\w*)\s*\(')
        seen = set()
        for rx, kind in ((write_re, 'write'), (read_re, 'read'), (method_re, 'method')):
            for m in rx.finditer(code):
                receiver, member = m.group(1), m.group(2)
                if receiver.strip() in ('this', 'base'):
                    continue
                iface = self.walker.resolve(receiver, env, methods)
                if iface is None:
                    continue
                if kind == 'method':
                    if member in iface.methods:
                        key = (iface.short, member, kind)
                        if key not in seen:
                            seen.add(key)
                            self.accesses.append(Access(iface.short, member, 'method', page, 'dynamic'))
                    continue
                if member not in iface.props:
                    continue
                if kind == 'write' and not iface.props[member]['put']:
                    continue
                key = (iface.short, member, kind)
                if key in seen:
                    continue
                seen.add(key)
                self.accesses.append(Access(iface.short, member, kind, page, 'dynamic'))
        # ((object)antispam).GetType().InvokeMember("PerformMaintenance", BindingFlags.InvokeMethod ...
        for ident, name in re.findall(r'\(\(object\)\s*(\w+)\)\s*\.GetType\(\)\s*\.InvokeMember\(\s*"(\w+)"', text):
            iface = env.get(ident)
            if iface and name in iface.methods:
                self.accesses.append(Access(iface.short, name, 'method', page, 'InvokeMember'))
        return env, methods

    def read_file(self, page, text):
        text = strip_c_comments(text)
        self.read_editors(page, text)
        env, methods = self.read_dynamic(page, text)
        self.read_collections(page, text, env, methods)


# ---------------------------------------------------------------------------
# 3. The REST API
# ---------------------------------------------------------------------------

class Route:
    def __init__(self, path, method, source):
        self.path = path
        self.method = method
        self.source = source
        self.keys = set()          # JSON keys the route accepts (raw spelling)
        self.setters = set()       # folded C++ setter names its handler calls
        self.scope = None          # list of short interface names, [] for none, None for unknown
        self.description = ''

    @property
    def norm(self):
        return re.sub(r'\{[^}]*\}', '{}', self.path)

    @property
    def label(self):
        return self.method.upper() + ' ' + self.path

    def writes(self):
        return self.method in WRITE_METHODS


def route_scope(path):
    segments = [s for s in path.split('/')[3:] if s and not s.startswith('{')]
    while segments and segments[-1] in VERB_SEGMENTS:
        segments.pop()
    if not segments:
        return []
    last = segments[-1]
    if last in PATH_SCOPE:
        return list(PATH_SCOPE[last])
    return None


LITERAL_RE = re.compile(r'"((?:[^"\\\n]|\\.)*)"')


def unescape_cpp(s):
    return s.replace('\\"', '"').replace('\\\\', '\\')


def top_level_keys(obj_text):
    """The keys of a JSON object's text (without its outer braces)."""
    keys, depth, i, n = [], 0, 0, len(obj_text)
    while i < n:
        c = obj_text[i]
        if c == '"':
            j = i + 1
            while j < n and obj_text[j] != '"':
                j += 1 if obj_text[j] != '\\' else 2
            if depth == 0 and j + 1 < n and obj_text[j + 1] == ':':
                keys.append(obj_text[i + 1:j])
            i = j + 1
            continue
        if c in '{[':
            depth += 1
        elif c in '}]':
            depth -= 1
        i += 1
    return keys


def read_openapi_routes(files):
    routes = {}
    for path_file in files:
        text = strip_c_comments(read(path_file))
        doc = ''.join(unescape_cpp(m.group(1)) for m in LITERAL_RE.finditer(text))
        for m in re.finditer(r'"(/api/v1/[^"]*)":\{', doc):
            path = m.group(1)
            block = balanced(doc, m.end() - 1)
            for om in re.finditer(r'"(get|post|put|patch|delete)":\{', block):
                op = balanced(block, om.end() - 1)
                route = routes.setdefault((path, om.group(1)), Route(path, om.group(1), os.path.basename(path_file)))
                d = re.search(r'"description":"((?:[^"\\]|\\.)*)"', op)
                if d:
                    route.description = d.group(1)
                rb = op.find('"requestBody"')
                if rb >= 0:
                    p = op.find('"properties":{', rb)
                    if p >= 0:
                        props = balanced(op, p + len('"properties":'))
                        route.keys.update(top_level_keys(props))
                        # one level down: rule criteria/actions, route addresses
                        for sub in re.finditer(r'"properties":\{', props):
                            route.keys.update(top_level_keys(balanced(props, sub.end() - 1)))
    return routes


def read_settings_groups(routes):
    text = strip_c_comments(read(os.path.join(REST_DIR, 'RestApiSettings.cpp')))
    groups = re.findall(r'const\s+Group\s+\w+\s*=\s*\{\s*"(\w+)",\s*"(/api/v1/[^"]+)",\s*(\w+),', text)
    for _name, path, rows_name in groups:
        m = re.search(r'const\s+Row\s+' + re.escape(rows_name) + r'\s*\[\]\s*=\s*\{', text)
        if not m:
            continue
        table = balanced(text, m.end() - 1)
        rows = list(re.finditer(r'\{\s*"([a-z0-9_]+)",\s*Kind\w+,\s*(\w+),', table))
        get_route = routes.setdefault((path, 'get'), Route(path, 'get', 'RestApiSettings.cpp'))
        put_route = Route(path, 'put', 'RestApiSettings.cpp')
        for i, r in enumerate(rows):
            end = rows[i + 1].start() if i + 1 < len(rows) else len(table)
            body = table[r.start():end]
            key, access = r.group(1), r.group(2)
            get_route.keys.add(key)
            if access in ('ReadWrite', 'WriteOnly'):
                put_route.keys.add(key)
                put_route.setters.update(fold(s) for s in re.findall(r'\b(Set[A-Z]\w*)\s*\(', body))
        # A group of read-only rows (the directories) has no PUT.
        if put_route.keys:
            routes.setdefault((path, 'put'), put_route)


KEY_READ_RE = re.compile(
    r'(?:Read\w*|HasMember|Member|Field\w*)\s*\(\s*\w+\s*,\s*"([a-z][a-z0-9_]*)"'
    r'|\b\w+\s*\.\s*Get\s*\(\s*"([a-z][a-z0-9_]*)"'
    r'|\b(?:Get)?Json\w*\s*\(\s*\w+\s*,\s*"([a-z][a-z0-9_]*)"')
FUNC_HEAD_RE = re.compile(r'^[ \t]{0,6}(?:[\w:<>*&,]+[ \t]+)*([\w:]+)[ \t]*\([^;{}]*\)[ \t]*(?:const)?[ \t]*\{?[ \t]*$', re.M)


class Function:
    """A C++ function in a REST source: the JSON keys it reads, the setters it
    calls and the functions of the same file it calls."""
    def __init__(self, name, source):
        self.name = name
        self.source = source
        self.keys = set()
        self.setters = set()
        self.callees = set()


def read_handler_functions(files):
    out = []
    for path_file in files:
        text = strip_c_comments(read(path_file))
        heads = [m for m in FUNC_HEAD_RE.finditer(text)
                 if m.group(1).split('::')[-1] not in ('if', 'for', 'while', 'switch', 'catch', 'return', 'sizeof')]
        names = {h.group(1).split('::')[-1] for h in heads}
        for i, h in enumerate(heads):
            end = heads[i + 1].start() if i + 1 < len(heads) else len(text)
            body = text[h.end():end]
            fn = Function(h.group(1).split('::')[-1], os.path.basename(path_file))
            for m in KEY_READ_RE.finditer(body):
                fn.keys.add(next(g for g in m.groups() if g))
            # A setter called with a bare constant (SetListMode(LMPublic),
            # SetActive(true)) fixes a value the body cannot change; it does
            # not make the property writable.
            for name, arg in re.findall(r'\b(Set[A-Z]\w*)\s*\(([^()]*)\)', body):
                if re.fullmatch(r'\s*(?:true|false|-?\d+|\w+::\w+|"[^"]*"|_T\("[^"]*"\))?\s*', arg):
                    continue
                fn.setters.add(fold(name))
            fn.setters |= {fold(s) for s in re.findall(r'\b(Set[A-Z]\w*)\s*\([^()]*\(', body)}
            fn.callees = {c for c in re.findall(r'\b(\w+)\s*\(', body) if c in names and c != fn.name}
            if fn.keys or fn.setters:
                out.append(fn)
    return out


def inherit_create_keys(routes):
    """PUT /x/{id} takes the body of POST /x; the descriptions say so and the
    document lists the schema once, on the create."""
    for (path, method), route in routes.items():
        if method not in ('put', 'patch') or not re.search(r'/\{[^}]*\}$', path):
            continue
        create = routes.get((path[:path.rfind('/')], 'post'))
        if create:
            route.keys.update(create.keys)


def attach_handler_keys(routes, functions):
    """A handler's setters belong to the write route whose documented keys it
    reads: at least two of them, and no more than half of its own keys outside
    them, so that a domain handler and an account handler sharing "active" and
    "signature_html" are not taken for each other. The handler's keys are not
    added to the route, so one attachment cannot widen the next."""
    by_source = defaultdict(list)
    for fn in functions:
        by_source[fn.source].append(fn)
    for route in routes.values():
        if not route.writes():
            continue
        noun = [s for s in route.path.split('/')[3:] if s and not s.startswith('{')]
        noun = fold(noun[-1]).rstrip('s') if noun else ''
        documented = set(route.keys)
        attached = []
        for fn in functions:
            overlap = documented & fn.keys
            outside = fn.keys - documented
            if (len(overlap) >= 2 and len(outside) * 2 <= len(fn.keys)) or \
                    (fn.keys <= documented and fn.keys and noun and noun in fold(fn.name)):
                attached.append(fn)
        # The setter is often in a helper the handler calls after reading the
        # keys, or the handler reads through one helper and writes through
        # another: take the callers of an attached function and everything
        # those call, one hop each way, within the same file. A caller with a
        # wide fan-out is the request dispatcher and is left out, or every
        # handler in the file would follow.
        grown = set(attached)
        for fn in attached:
            for parent in by_source[fn.source]:
                if fn.name in parent.callees and len(parent.callees) <= 8:
                    grown.add(parent)
        for fn in list(grown):
            for child in by_source[fn.source]:
                if child.name in fn.callees:
                    grown.add(child)
        for fn in grown:
            route.setters.update(fn.setters)


def add_description_keys(routes, corpus):
    for route in routes.values():
        if not route.writes() or not route.description:
            continue
        d = route.description
        route.keys.update(re.findall(r'\b([a-z][a-z0-9]*(?:_[a-z0-9]+)+)\b', d))
        if 'Body:' in d:
            tail = d[d.index('Body:') + 5:]
            for w in re.findall(r'\b([a-z][a-z0-9]*)\b', tail):
                if w in corpus:
                    route.keys.add(w)


def read_rest(interfaces):
    files = sorted(os.path.join(REST_DIR, n) for n in os.listdir(REST_DIR)
                   if n.startswith('RestApi') and n.endswith('.cpp'))
    routes = read_openapi_routes(files)
    read_settings_groups(routes)
    functions = read_handler_functions(files)
    corpus = set()
    for r in routes.values():
        corpus |= r.keys
    for fn in functions:
        corpus |= fn.keys
    corpus -= {'error', 'id', 'ok', 'status'}
    add_description_keys(routes, corpus)
    for route in routes.values():
        # A path parameter is not a body key: PUT /settings/messages/{name}
        # cannot rename the message, and PUT /accounts/{address} cannot move it.
        route.keys -= set(re.findall(r'\{(\w+)\}', route.path))
    inherit_create_keys(routes)
    attach_handler_keys(routes, functions)
    settings = interfaces.get('IInterfaceSettings')
    grouped = {'AntiSpam', 'Logging', 'Scripting', 'Backup'}
    subtree = []
    if settings:
        stack, seen = [settings], set()
        while stack:
            cur = stack.pop()
            if cur.name in seen:
                continue
            seen.add(cur.name)
            subtree.append(cur.short)
            for p in cur.props.values():
                nxt = interfaces.get(p['type'] or '')
                if nxt and nxt.item_type() is None and nxt.name not in seen:
                    stack.append(nxt)
    for route in routes.values():
        route.scope = route_scope(route.path)
        if route.path == '/api/v1/settings':
            route.scope = [s for s in subtree if s not in grouped]
    return routes


# ---------------------------------------------------------------------------
# 4. The Control Deck
# ---------------------------------------------------------------------------

def deck_path(arg, groups):
    """A path expression from an api(...) call as a route template, or a list of them."""
    arg = arg.strip()
    if arg.startswith('`'):
        inner = arg[1:arg.rfind('`')]
        return [re.sub(r'\$\{[^}]*\}', '{}', inner)]
    if arg.startswith('"') or arg.startswith("'"):
        pieces, depth, cur = [], 0, ''
        for c in arg:
            if c in '([':
                depth += 1
            elif c in ')]':
                depth -= 1
            if c == '+' and depth == 0:
                pieces.append(cur)
                cur = ''
            else:
                cur += c
        pieces.append(cur)
        out = ''
        for p in pieces:
            p = p.strip()
            if (p.startswith('"') and p.endswith('"')) or (p.startswith("'") and p.endswith("'")):
                out += p[1:-1]
            else:
                out += '{}'
        return [re.sub(r'(\{\})+', '{}', out).rstrip('?')]
    if re.match(r'^[\w.]+$', arg):
        return list(groups)
    return []


def read_deck():
    text = read(DECK)
    views = re.findall(r'data-view="(\w+)"', text)
    groups = re.findall(r'path\s*:\s*"(/api/v1/[^"]+)"', text)
    script = '\n'.join(re.findall(r'<script[^>]*>(.*?)</script>', text, flags=re.S))
    heads = list(re.finditer(r'^(?:async\s+)?function\s+(\w+)\s*\(', script, flags=re.M))
    functions = {}
    top_level = ''
    prev_end = 0
    for i, h in enumerate(heads):
        top_level += script[prev_end:h.start()]
        end = heads[i + 1].start() if i + 1 < len(heads) else len(script)
        functions[h.group(1)] = script[h.start():end]
        prev_end = end
    top_level += script[prev_end:]
    view_fn = dict(re.findall(r'view\s*===?\s*"(\w+)"\s*\)\s*(?:await\s+)?(\w+)\s*\(', script))
    # The function holding that table is the view dispatcher; a helper that calls
    # it after a save re-renders the current view, it does not reach every view.
    dispatchers = {name for name, body in functions.items() if re.search(r'view\s*===?\s*"\w+"\s*\)', body)}
    act_fns = defaultdict(set)
    for act, branch in re.findall(r'd\.act\s*===?\s*"(\w+)"\s*\)([^\n]*)', script):
        for fn in re.findall(r'\b(\w+)\s*\(', branch):
            if fn in functions:
                act_fns[act].add(fn)
    calls = defaultdict(set)   # function -> {(norm path, METHOD)}
    for name, body in functions.items():
        for m in re.finditer(r'\bapi\s*\(', body):
            if body[max(0, m.start() - 9):m.start()].strip().endswith('function'):
                continue
            args = balanced(body, m.end() - 1, '(', ')')
            depth, first = 0, ''
            for c in args:
                if c in '([{':
                    depth += 1
                elif c in ')]}':
                    depth -= 1
                if c == ',' and depth == 0:
                    break
                first += c
            method = re.search(r'method\s*:\s*"(\w+)"', args)
            method = method.group(1).upper() if method else 'GET'
            for p in deck_path(first, groups):
                calls[name].add((re.sub(r'\{[^}]*\}', '{}', p), method))
    reach = {}
    for view in views:
        start = view_fn.get(view)
        if not start:
            reach[view] = set()
            continue
        seen, stack = set(), [start]
        while stack:
            fn = stack.pop()
            if fn in seen or fn not in functions:
                continue
            seen.add(fn)
            body = functions[fn]
            for callee in re.findall(r'\b(\w+)\s*\(', body):
                if callee in functions and callee not in seen and callee not in dispatchers:
                    stack.append(callee)
            for act in re.findall(r'data-act="(\w+)"', body):
                for callee in act_fns.get(act, ()):
                    if callee not in seen and callee not in dispatchers:
                        stack.append(callee)
        reach[view] = seen
    view_calls = {}
    for view, fns in reach.items():
        s = set()
        for fn in fns:
            s |= calls.get(fn, set())
        view_calls[view] = s
    return views, view_calls


# ---------------------------------------------------------------------------
# 5. Matching
# ---------------------------------------------------------------------------

def match_property(iface, prop, com_setters, routes):
    """[(route, channel)] for the write routes that cover Interface.Property."""
    out = []
    names = fold_variants(prop)
    words = word_set(prop)
    setters = com_setters.get((iface, prop), set())
    for route in routes.values():
        if not route.writes() or route.path.startswith('/api/v1/me') or route.path.startswith('/api/v1/portal'):
            continue
        if route.scope is not None and iface not in route.scope:
            continue
        channel = None
        if setters and setters & route.setters:
            channel = 'setter'
        else:
            # Loose: without the qualifiers, and without the interface's own
            # noun (DomainAlias.AliasName is the domain alias's "name").
            loose = words - QUALIFIER_WORDS - word_set(iface)
            for key in route.keys:
                if names & fold_variants(key):
                    channel = 'name'
                    break
                key_words = word_set(key)
                if words and words == key_words:
                    channel = 'words'
                elif channel is None and loose and loose == key_words:
                    channel = 'words, loose'
            if channel is None:
                continue
        if route.scope is None:
            channel += ', scope unknown'
        out.append((route, channel))
    return out


def match_method(iface, method, routes):
    out = []
    f = fold(method)
    for route in routes.values():
        if route.method == 'get' or route.path.startswith('/api/v1/me') or route.path.startswith('/api/v1/portal'):
            continue
        if route.scope is not None and route.scope and iface not in route.scope:
            continue
        segs = [s for s in route.path.split('/')[3:] if s and not s.startswith('{')]
        last = fold(segs[-1]) if segs else ''
        if last and (last == f or (len(last) >= 4 and last in f)):
            out.append(route)
    return out


# ---------------------------------------------------------------------------
# 6. The report
# ---------------------------------------------------------------------------

def md_cell(s):
    return str(s).replace('|', '\\|')


def main():
    strict = '--strict' in sys.argv
    write_doc = '--no-doc' not in sys.argv

    interfaces = read_idl()
    com_setters = read_com_setters(interfaces)
    desktop = DesktopReader(interfaces)
    accesses = desktop.read_all()
    routes = read_rest(interfaces)
    views, view_calls = read_deck()

    # Fold the desktop's accesses per (interface, member).
    writes = OrderedDict()
    reads = OrderedDict()
    methods = OrderedDict()
    for a in accesses:
        target = {'write': writes, 'read': reads, 'method': methods}[a.kind]
        target.setdefault((a.iface, a.member), set()).add(a.page)
    order = list(interfaces.values())
    by_short = {i.short: n for n, i in enumerate(order)}
    writes = OrderedDict(sorted(writes.items(), key=lambda kv: (by_short.get(kv[0][0], 999), kv[0][1])))

    rows = []
    for (iface, prop), pages in writes.items():
        matched = match_property(iface, prop, com_setters, routes)
        deck_views = set()
        for route, _ch in matched:
            for view, calls in view_calls.items():
                if (route.norm, route.method.upper()) in calls:
                    deck_views.add(view)
        rows.append({'iface': iface, 'prop': prop, 'pages': sorted(pages), 'rest': matched,
                     'deck': sorted(deck_views), 'read_only': iface in reads and (iface, prop) in reads})

    n_total = len(rows)
    n_rest = sum(1 for r in rows if r['rest'])
    n_deck = sum(1 for r in rows if r['deck'])
    missing = [r for r in rows if not r['rest']]
    rest_not_deck = [r for r in rows if r['rest'] and not r['deck']]

    method_rows = []
    for (iface, name), pages in sorted(methods.items(), key=lambda kv: (by_short.get(kv[0][0], 999), kv[0][1])):
        if name in ('Save', 'Delete', 'Refresh'):
            continue
        method_rows.append({'iface': iface, 'name': name, 'pages': sorted(pages), 'rest': match_method(iface, name, routes)})

    write_routes = sorted((r for r in routes.values() if r.writes()), key=lambda r: (r.path, r.method))
    deck_routes = set()
    for calls in view_calls.values():
        deck_routes |= calls

    # ---- console ----------------------------------------------------------
    out = []
    p = out.append
    p('Control Deck / REST parity with the desktop Control Panel')
    p('')
    p('COM interfaces: %d; desktop pages: %d; REST routes: %d (%d write); Deck views: %d'
      % (len(interfaces), len(desktop.pages), len(routes), len(write_routes), len(views)))
    p('')
    for r in rows:
        rest = '; '.join('%s [%s]' % (route.label, ch) for route, ch in r['rest']) or '-'
        deck = ', '.join(r['deck']) or '-'
        p('%-24s %-36s REST: %-60s Deck: %s' % (r['iface'], r['prop'], rest, deck))
    p('')
    p('Summary')
    p('  properties the desktop program writes: %d' % n_total)
    p('  of them writable over REST:            %d' % n_rest)
    p('  of them reachable in the Deck:         %d' % n_deck)
    p('')
    p('Missing over REST, by interface (%d):' % len(missing))
    grouped = OrderedDict()
    for r in missing:
        grouped.setdefault(r['iface'], []).append(r['prop'])
    for iface, props in sorted(grouped.items(), key=lambda kv: -len(kv[1])):
        p('  %-28s %3d  %s' % (iface, len(props), ', '.join(props)))
    p('')
    p('Writable over REST but not reached by any Deck view (%d):' % len(rest_not_deck))
    grouped2 = OrderedDict()
    for r in rest_not_deck:
        grouped2.setdefault(r['iface'], []).append(r['prop'])
    for iface, props in sorted(grouped2.items(), key=lambda kv: -len(kv[1])):
        p('  %-28s %3d  %s' % (iface, len(props), ', '.join(props)))
    if desktop.unresolved_paths:
        p('')
        p('Desktop paths the IDL walk could not resolve (%d):' % len(desktop.unresolved_paths))
        for page, path in desktop.unresolved_paths:
            p('  %s: %s' % (page, path))
    unknown = sorted(set(r.label for r in routes.values() if r.scope is None and r.writes()
                         and not r.path.startswith(('/api/v1/me', '/api/v1/portal'))))
    if unknown:
        p('')
        p('Write routes whose resource is not in the scope table (match any interface): %s' % ', '.join(unknown))
    print('\n'.join(out))

    # ---- Markdown ---------------------------------------------------------
    if write_doc:
        write_markdown(interfaces, desktop, routes, views, view_calls, rows, method_rows,
                       n_total, n_rest, n_deck, missing, rest_not_deck, write_routes, deck_routes)

    return 1 if (strict and missing) else 0


def write_markdown(interfaces, desktop, routes, views, view_calls, rows, method_rows,
                   n_total, n_rest, n_deck, missing, rest_not_deck, write_routes, deck_routes):
    L = []
    a = L.append
    a('# Control Deck parity')
    a('')
    a('Generated by `python build/check-deck-parity.py`; do not edit by hand.')
    a('')
    a('This measures how far the browser administration page (the Control Deck at '
      '`/WebAdmin`) and the REST API are from the desktop Control Panel, from the sources '
      'alone. The desktop program drives the server over COM, so its surface is the set of '
      'COM properties its pages write: the settings editors (`new ComText { Path = '
      '"AntiSpam.SpamMarkThreshold" }`, resolved against `Settings`), the collection editors '
      '(a `CollectionSpec` chain and its `FieldSpec` props) and the dynamic locals in the '
      'dialogs (`domain.Postmaster = ...`), each walked to its IDL interface through '
      '`hMailServer.idl`. The REST surface is the OpenAPI document the server emits as string '
      'literals in `RestApi*.cpp` (path, method, request-schema properties and the '
      '"Body: ..." sentence), the settings group tables (a row\'s key, access and the setter '
      'it calls) and the keys and setters each handler uses. The Deck surface is the '
      '`api(...)` calls each `data-view` reaches through its render function, the functions it '
      'calls and the `data-act` buttons its HTML emits. A property counts as writable over '
      'REST when a POST, PUT or PATCH route whose path names that resource carries a matching '
      'key, on one of three channels reported per row: **setter** (the C++ setter the COM '
      '`put_` calls is one the handler calls), **name** (the names fold to the same string, '
      'ignoring case, underscores and a trailing unit such as `_kb`) or **words** (the same '
      'words once fillers like "of" and "number" are dropped). The matching is heuristic: a '
      'shared name is not proof of the same semantics, and a property with no match may be '
      'covered under a name this cannot see. Routes under `/api/v1/me` and `/api/v1/portal` '
      'act on the caller\'s own account and are not counted; they are the self-service '
      'surface, not administration.')
    a('')
    a('## Summary')
    a('')
    a('| Measure | Count |')
    a('|---|---|')
    a('| COM properties the desktop program writes | %d |' % n_total)
    a('| of them writable over REST | %d |' % n_rest)
    a('| of them reachable from a Deck view | %d |' % n_deck)
    a('| missing over REST | %d |' % len(missing))
    a('| over REST but not reached by any Deck view | %d |' % len(rest_not_deck))
    a('| COM interfaces in the IDL | %d |' % len(interfaces))
    a('| desktop pages read | %d |' % len(desktop.pages))
    a('| REST routes (path and method) | %d, %d of them writes |' % (len(routes), len(write_routes)))
    a('| Deck views | %d |' % len(views))
    a('')
    a('## Missing over REST, by interface')
    a('')
    grouped = OrderedDict()
    for r in missing:
        grouped.setdefault(r['iface'], []).append(r['prop'])
    if grouped:
        a('| Interface | Count | Properties the desktop writes and no route covers |')
        a('|---|---|---|')
        for iface, props in sorted(grouped.items(), key=lambda kv: -len(kv[1])):
            a('| %s | %d | %s |' % (iface, len(props), ', '.join('`%s`' % p for p in props)))
    else:
        a('Nothing is missing.')
    a('')
    a('## Over REST but not reached by any Deck view')
    a('')
    grouped2 = OrderedDict()
    for r in rest_not_deck:
        grouped2.setdefault(r['iface'], []).append(r['prop'])
    if grouped2:
        a('| Interface | Count | Properties |')
        a('|---|---|---|')
        for iface, props in sorted(grouped2.items(), key=lambda kv: -len(kv[1])):
            a('| %s | %d | %s |' % (iface, len(props), ', '.join('`%s`' % p for p in props)))
    else:
        a('Every property writable over REST is reached by a Deck view.')
    a('')
    a('## What the heuristic gets wrong')
    a('')
    a('Read with these in mind. They were found by checking the missing list, and a sample of '
      'the matched rows, against the code by hand on 14 September 2026; the script was '
      'corrected for the ones that could be, and the rest are the shapes it still produces.')
    a('')
    a('- **A write that is not a setting.** `Diagnostics.LocalDomainName` and '
      '`TestDomainName` are the inputs of a diagnostic run; `RouteAddress.RouteID`, '
      '`RuleAction.RuleID`, `RuleCriteria.RuleID` and `GroupMember.AccountID` are the link '
      'from a child row to its parent, set when the row is created. They are assignments in '
      'the desktop program, so they are counted, but none of them is something an '
      'administrator edits, and a REST body carries the parent in its path instead. Read the '
      '"missing" ones as noise and the matched ones as accidents of naming.')
    a('- **A name that is fixed by design.** `ServerMessage.Name` is the key of a server '
      'message, not a value: the desktop program\'s collection editor exposes it as a column, '
      'and `PUT /api/v1/settings/messages/{name}` carries it in the path. The row is missing '
      'because a path parameter is not counted as a body key; it is not a gap.')
    a('- **Covered under another shape.** `DistributionListRecipient.RecipientAddress` is what '
      'the `members` array of `POST /api/v1/domains/{domain}/lists` carries, one address per '
      'entry; `AppPassword.Active` and `Name` are writable at `/api/v1/me/app-passwords`, which '
      'is excluded as self-service. Both read as missing.')
    a('- **A synonym the channels cannot cross.** `use_greylisting` writes '
      '`Domain.AntiSpamEnableGreylisting` and `all_criteria` writes `Rule.UseAND`; the words '
      'channel does not reach across a rewording, and these are matched only because the '
      'handler calls the same C++ setter the COM `put_` calls. A reworded key whose setter '
      'sits in a function the walk does not reach (it follows one hop of callers and one of '
      'callees from the function that reads the keys, within the file) is a false '
      '"missing".')
    a('- **A setter that fixes a value.** The list create calls `SetListMode(LMPublic)` and '
      '`SetActive(true)` whatever the body says; a setter called with a bare constant is not '
      'counted, which is why `DistributionList.Mode` and `Active` are missing rather than '
      'matched. A constant reached through a variable would still count.')
    a('- **A generic key held to its resource.** `name`, `active`, `description`, `port` and '
      '`password` exist on many interfaces; the route\'s path decides which interface it may '
      'match, so `POST /api/v1/ports` cannot claim `Domain.Name`. A write route whose resource '
      'is not in the scope table matches any interface and is flagged "scope unknown" in its '
      'row; there should be none outside `/me` and `/portal`.')
    a('- **Nested bodies.** A rule\'s criteria and actions and a route\'s addresses arrive as '
      'arrays inside the rule or route body; their keys are read one level down and matched '
      'against `RuleCriteria`, `RuleAction` and `RouteAddress`, so a match there says the '
      'route can carry the value, not that a top-level key exists. `RuleAction.AbortSpamFlagged` '
      'is the one action field the rules body has no word for.')
    a('- **Loose word matches.** A COM name may carry a qualifier the key drops (`SSL`, `SMTP`, '
      '`IP`, `Anti`/`Spam`, `Enable`) or its own resource\'s noun (`DomainAlias.AliasName` is '
      'the domain alias\'s `name`); such rows say "words, loose" and are the ones to doubt '
      'first.')
    a('- **Collections without a route are true gaps, and small ones.** The SURBL servers, DNS '
      'blacklists, the anti-spam white list, blocked senders and attachments, the greylisting '
      'white list, incoming relays, groups and their members, and folder permissions are '
      'edited by collection editors in the desktop program and have no REST resource; the '
      'anti-virus, cache and message-indexing settings, the per-account spam thresholds and '
      'vacation dates, and the domain\'s DKIM canonicalisation and secondary key have no '
      'settings row or body key. Each is one route or one row away.')
    a('- **Deck reach is by route, not by field.** The Deck builds its forms from the OpenAPI '
      'document, so a view that PUTs a settings group reaches every key of that group; a '
      'property is "reachable" when a view calls a route that covers it, not when a field '
      'for it is on screen. The largest "over REST but not in the Deck" groups are the domain '
      'and the IP range, which the Deck lists but does not edit.')
    a('- **Reads are not measured.** Properties the desktop only displays are outside the '
      'count; the parity question here is what an administrator can change.')
    a('')
    a('## Per interface')
    a('')
    a('Every COM property the desktop program writes, the page that writes it, the REST route '
      'that covers it with the channel the match was made on, and the Deck views that call '
      'that route.')
    a('')
    by_iface = OrderedDict()
    for r in rows:
        by_iface.setdefault(r['iface'], []).append(r)
    for iface, irows in by_iface.items():
        a('### %s' % iface)
        a('')
        a('| Property | Desktop page | REST route | Deck view |')
        a('|---|---|---|---|')
        for r in irows:
            rest = '<br>'.join('`%s` (%s)' % (md_cell(route.label), ch) for route, ch in r['rest']) or '-'
            a('| `%s` | %s | %s | %s |' % (r['prop'], ', '.join(r['pages']), rest, ', '.join(r['deck']) or '-'))
        a('')
    a('## Methods the desktop program invokes')
    a('')
    a('Secondary and looser still: a COM method counts as covered when a non-GET route\'s last '
      'path segment is the method\'s name or part of it (`Scripting.Reload` and '
      '`POST /api/v1/settings/scripting/reload`). `Save`, `Delete` and `Refresh` are left out; '
      'they are how every object is written, not an action of their own.')
    a('')
    a('| Interface | Method | Desktop page | REST route |')
    a('|---|---|---|---|')
    for r in method_rows:
        rest = '<br>'.join('`%s`' % md_cell(route.label) for route in r['rest']) or '-'
        a('| %s | `%s` | %s | %s |' % (r['iface'], r['name'], ', '.join(r['pages']), rest))
    a('')
    a('## Write routes')
    a('')
    a('Every POST, PUT and PATCH route in the OpenAPI document and the settings tables, the '
      'resource it is taken to act on, the keys it accepts, and whether a Deck view calls it.')
    a('')
    a('| Route | Resource | Keys | Deck |')
    a('|---|---|---|---|')
    for route in write_routes:
        scope = '?' if route.scope is None else (', '.join(route.scope) or '-')
        called = any((route.norm, route.method.upper()) == c for c in deck_routes)
        keys = ', '.join('`%s`' % k for k in sorted(route.keys)) or '-'
        a('| `%s` | %s | %s | %s |' % (md_cell(route.label), scope, keys, 'yes' if called else '-'))
    a('')
    a('## Deck views')
    a('')
    a('| View | Routes called |')
    a('|---|---|')
    for view in views:
        calls = sorted(view_calls.get(view, ()))
        a('| `%s` | %s |' % (view, ', '.join('`%s %s`' % (m, md_cell(p)) for p, m in calls) or '-'))
    a('')
    # The working tree is CRLF throughout (.gitattributes: * text=auto eol=crlf).
    with open(DOC, 'w', encoding='utf-8', newline='\r\n') as f:
        f.write('\n'.join(L) + '\n')


if __name__ == '__main__':
    sys.exit(main())
