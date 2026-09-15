// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// The Control Deck's script, executed. build/check-deck-script.py lifts the one
// inline script out of hmailserver/installation/WebAdmin/index.html and hands
// the page and the script here; this builds a small DOM from the markup,
// answers fetch out of a recorded server whose state the writes change, runs
// the script in that world and then asserts what the administrator would see:
// each view drawn from its answers, each form sending the JSON its route takes,
// each refusal shown in the server's own words, each change read back after
// the round trip.
//
// The DOM is deliberately small and hand-written, as the portal's is: no
// dependency, so this runs on a bare runner with nothing installed, and nothing
// in it is magic - a call the script makes that is not here fails with its
// name rather than passing quietly. What the Deck needs beyond the portal:
// innerHTML, because every view is drawn by assigning it; a selector engine
// for the shapes the page uses (#id, .class, tag, [attr], [attr=value], the
// descendant and child combinators, a comma); closest; dataset; on<event>
// handler properties beside addEventListener; a select whose value is its
// selected option; and entity decoding, because everything the server says
// goes through escHtml before it is drawn and the assertions want it back.
// Timers are a queue this file fires by hand, so the restart's polling loop is
// tested without waiting a minute.

'use strict';

const fs = require('fs');
const nodePath = require('path');
const vm = require('vm');

const out = console;

// The Deck's catalogues, which the recorded server answers at
// /deck-lang/<code>.json as the real one does from the directory beside the
// page: the files in the repository, so a check here runs against the
// translations that ship.
const CATALOGUES = nodePath.join(__dirname, '..', 'hmailserver', 'installation', 'WebAdmin', 'languages');

const [pagePath, scriptPath] = process.argv.slice(2);
if (!pagePath || !scriptPath) {
   out.error('usage: node build/deck-script-test.js <index.html> <deck.js>');
   process.exit(2);
}

/* ------------------------------------------------------------------ the DOM */

const VOID = new Set(['area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input', 'link', 'meta', 'param', 'source', 'track', 'wbr']);
const RAW = new Set(['script', 'style']);
const NAMED = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ', mdash: '—', ndash: '–', hellip: '…' };

function decodeEntities(text) {
   if (text.indexOf('&') < 0) { return text; }
   return text.replace(/&(#x[0-9a-fA-F]+|#\d+|[a-zA-Z]+);/g, (whole, body) => {
      if (body[0] === '#') {
         const code = body[1] === 'x' || body[1] === 'X' ? parseInt(body.slice(2), 16) : parseInt(body.slice(1), 10);
         return isFinite(code) ? String.fromCodePoint(code) : whole;
      }
      return body in NAMED ? NAMED[body] : whole;
   });
}

function encodeText(text) {
   return String(text).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

class TextNode {
   constructor(data) { this.data = data; this.parentNode = null; this.childNodes = []; this.nodeType = 3; }
   get textContent() { return this.data; }
   set textContent(v) { this.data = String(v); }
   get nodeValue() { return this.data; }
   set nodeValue(v) { this.data = String(v); }
}

/* Selectors: a list of chains, a chain a list of compounds from ancestor to
   target, each compound a tag, an id, classes and attribute tests. */
function parseCompound(text) {
   const c = { tag: null, id: null, classes: [], attrs: [] };
   let rest = text;
   const re = /^(?:([a-zA-Z][\w-]*)|#([\w-]+)|\.([\w-]+)|\[([\w-]+)(?:=(?:"([^"]*)"|'([^']*)'|([^\]]*)))?\]|(\*))/;
   while (rest.length) {
      const m = re.exec(rest);
      if (!m) { throw new Error('a selector this DOM does not understand: ' + text); }
      if (m[1]) { c.tag = m[1].toUpperCase(); }
      else if (m[2]) { c.id = m[2]; }
      else if (m[3]) { c.classes.push(m[3]); }
      else if (m[4]) { c.attrs.push({ name: m[4], value: m[5] !== undefined ? m[5] : m[6] !== undefined ? m[6] : m[7] !== undefined ? m[7] : null }); }
      rest = rest.slice(m[0].length);
   }
   return c;
}

function parseSelector(text) {
   return String(text).split(',').map((part) => {
      const chain = [];
      let child = false;
      part.trim().split(/\s+/).filter(Boolean).forEach((token) => {
         if (token === '>') { child = true; return; }
         chain.push({ compound: parseCompound(token), child });
         child = false;
      });
      if (!chain.length) { throw new Error('an empty selector: ' + text); }
      return chain;
   });
}

function matchesCompound(node, c) {
   if (!(node instanceof Element) || node.tagName[0] === '#') { return false; }
   if (c.tag && node.tagName !== c.tag) { return false; }
   if (c.id && node.attributes.id !== c.id) { return false; }
   const classes = node.className ? node.className.split(/\s+/) : [];
   if (c.classes.some((k) => classes.indexOf(k) < 0)) { return false; }
   return c.attrs.every((a) => (a.value === null ? a.name in node.attributes : node.attributes[a.name] === a.value));
}

function matchesChain(node, chain) {
   let at = chain.length - 1;
   if (!matchesCompound(node, chain[at].compound)) { return false; }
   let here = node;
   while (at > 0) {
      const link = chain[at];
      at -= 1;
      here = here.parentNode;
      if (link.child) {
         if (!matchesCompound(here, chain[at].compound)) { return false; }
      } else {
         while (here && !matchesCompound(here, chain[at].compound)) { here = here.parentNode; }
         if (!here) { return false; }
      }
   }
   return true;
}

function walk(node, visit) {
   node.childNodes.forEach((child) => {
      if (child instanceof Element) { visit(child); walk(child, visit); }
   });
}

function serialize(node) {
   if (node instanceof TextNode) { return encodeText(node.data); }
   const tag = node.tagName.toLowerCase();
   const attrs = Object.keys(node.attributes).map((k) => ' ' + k + '="' + String(node.attributes[k]).replace(/&/g, '&amp;').replace(/"/g, '&quot;') + '"').join('');
   if (VOID.has(tag)) { return '<' + tag + attrs + '>'; }
   return '<' + tag + attrs + '>' + node.childNodes.map(serialize).join('') + '</' + tag + '>';
}

class Element {
   constructor(tag) {
      this.tagName = String(tag).toUpperCase();
      this.nodeName = this.tagName;
      this.nodeType = 1;
      this.attributes = Object.create(null);
      this.childNodes = [];
      this.parentNode = null;
      this.listeners = Object.create(null);
      this.style = {};
      this.hidden = false;
      this.checked = false;
      this.disabled = false;
      this.scrollTop = 0;
      this.scrollHeight = 0;
      this.focused = 0;
      this._value = undefined;
   }
   get id() { return this.attributes.id || ''; }
   set id(v) { this.attributes.id = String(v); }
   get children() { return this.childNodes.filter((n) => n instanceof Element); }
   get firstChild() { return this.childNodes.length ? this.childNodes[0] : null; }
   get className() { return this.attributes.class || ''; }
   set className(v) { this.attributes.class = String(v); }
   get classList() {
      const self = this;
      const parts = () => (self.className ? self.className.split(/\s+/).filter(Boolean) : []);
      const write = (list) => { self.className = list.join(' '); };
      return {
         contains: (c) => parts().indexOf(c) >= 0,
         add: (c) => { const p = parts(); if (p.indexOf(c) < 0) { p.push(c); write(p); } },
         remove: (c) => write(parts().filter((x) => x !== c)),
         toggle: (c, force) => {
            const on = force === undefined ? parts().indexOf(c) < 0 : !!force;
            if (on) { const p = parts(); if (p.indexOf(c) < 0) { p.push(c); write(p); } }
            else { write(parts().filter((x) => x !== c)); }
            return on;
         }
      };
   }
   get dataset() {
      const self = this;
      const name = (key) => 'data-' + String(key).replace(/[A-Z]/g, (c) => '-' + c.toLowerCase());
      return new Proxy({}, {
         get: (_, key) => (typeof key === 'string' ? self.attributes[name(key)] : undefined),
         set: (_, key, v) => { self.attributes[name(key)] = String(v); return true; },
         has: (_, key) => name(key) in self.attributes
      });
   }
   /* A select answers with its selected option, a textarea with its text, and
      anything assigned wins over both - as in a browser. */
   get value() {
      if (this._value !== undefined) { return this._value; }
      if (this.tagName === 'SELECT') {
         const options = [];
         walk(this, (n) => { if (n.tagName === 'OPTION') { options.push(n); } });
         const chosen = options.filter((o) => 'selected' in o.attributes)[0] || options[0];
         return chosen ? ('value' in chosen.attributes ? chosen.attributes.value : chosen.textContent) : '';
      }
      if (this.tagName === 'TEXTAREA') { return this.textContent; }
      return 'value' in this.attributes ? this.attributes.value : '';
   }
   set value(v) { this._value = String(v); }
   get textContent() {
      return this.childNodes.map((n) => (n instanceof Element ? n.textContent : n.data)).join('');
   }
   set textContent(v) {
      this.childNodes.forEach((n) => { n.parentNode = null; });
      this.childNodes = [];
      if (v !== '' && v !== null && v !== undefined) { this.appendChild(new TextNode(String(v))); }
   }
   get innerHTML() { return this.childNodes.map(serialize).join(''); }
   set innerHTML(text) {
      this.childNodes.forEach((n) => { n.parentNode = null; });
      this.childNodes = [];
      parseHtml(String(text)).root.childNodes.slice().forEach((n) => this.appendChild(n));
   }
   appendChild(child) {
      if (child.parentNode) { child.parentNode.removeChild(child); }
      child.parentNode = this;
      this.childNodes.push(child);
      return child;
   }
   removeChild(child) {
      const at = this.childNodes.indexOf(child);
      if (at >= 0) { this.childNodes.splice(at, 1); child.parentNode = null; }
      return child;
   }
   setAttribute(name, value) {
      this.attributes[name] = String(value);
      if (name === 'hidden') { this.hidden = true; }
      if (name === 'value') { this._value = String(value); }
      if (name === 'type') { this.type = String(value); }
   }
   getAttribute(name) { return name in this.attributes ? this.attributes[name] : null; }
   hasAttribute(name) { return name in this.attributes; }
   removeAttribute(name) { delete this.attributes[name]; if (name === 'hidden') { this.hidden = false; } }
   addEventListener(type, fn) { (this.listeners[type] = this.listeners[type] || []).push(fn); }
   removeEventListener(type, fn) {
      const list = this.listeners[type] || [];
      const at = list.indexOf(fn);
      if (at >= 0) { list.splice(at, 1); }
   }
   dispatchEvent(event) {
      event.target = event.target || this;
      let node = this;
      while (node) {
         const own = node['on' + event.type];
         if (typeof own === 'function') { own.call(node, event); }
         (node.listeners[event.type] || []).slice().forEach((fn) => fn.call(node, event));
         if (event.propagationStopped) { return !event.defaultPrevented; }
         node = node.parentNode;
      }
      return !event.defaultPrevented;
   }
   matches(selector) { return parseSelector(selector).some((chain) => matchesChain(this, chain)); }
   closest(selector) {
      let node = this;
      while (node instanceof Element) {
         if (node.matches(selector)) { return node; }
         node = node.parentNode;
      }
      return null;
   }
   querySelectorAll(selector) {
      const chains = parseSelector(selector);
      const found = [];
      walk(this, (n) => { if (chains.some((chain) => matchesChain(n, chain))) { found.push(n); } });
      return found;
   }
   querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
   focus() { this.focused += 1; document.activeElement = this; }
   click() { this.dispatchEvent(makeEvent('click')); }
   blur() { }
   select() { }
   scrollIntoView() { }
}

function makeEvent(type, extra) {
   return Object.assign({
      type,
      defaultPrevented: false,
      propagationStopped: false,
      preventDefault() { this.defaultPrevented = true; },
      stopPropagation() { this.propagationStopped = true; }
   }, extra || {});
}

function parseHtml(text) {
   const root = new Element('#document-fragment');
   const stack = [root];
   let i = 0;
   const top = () => stack[stack.length - 1];
   while (i < text.length) {
      const lt = text.indexOf('<', i);
      if (lt < 0) {
         if (i < text.length) { top().appendChild(new TextNode(decodeEntities(text.slice(i)))); }
         break;
      }
      if (lt > i) { top().appendChild(new TextNode(decodeEntities(text.slice(i, lt)))); }
      if (text.startsWith('<!--', lt)) { i = text.indexOf('-->', lt); i = i < 0 ? text.length : i + 3; continue; }
      if (text.startsWith('<!', lt)) { i = text.indexOf('>', lt); i = i < 0 ? text.length : i + 1; continue; }
      const gt = text.indexOf('>', lt);
      if (gt < 0) { break; }
      const inside = text.slice(lt + 1, gt);
      i = gt + 1;
      if (inside.startsWith('/')) {
         const name = inside.slice(1).trim().toUpperCase();
         for (let n = stack.length - 1; n > 0; n -= 1) {
            if (stack[n].tagName === name) { stack.length = n; break; }
         }
         continue;
      }
      const nameEnd = inside.search(/[\s/]/);
      const name = (nameEnd < 0 ? inside : inside.slice(0, nameEnd)).toLowerCase();
      const element = new Element(name);
      const attrText = nameEnd < 0 ? '' : inside.slice(nameEnd);
      const attr = /([:\w-]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'>]+)))?/g;
      let match;
      while ((match = attr.exec(attrText)) !== null) {
         const raw = match[2] !== undefined ? match[2] : match[3] !== undefined ? match[3] : match[4] !== undefined ? match[4] : '';
         element.attributes[match[1]] = decodeEntities(raw);
      }
      if ('hidden' in element.attributes) { element.hidden = true; }
      if ('checked' in element.attributes) { element.checked = true; }
      if ('disabled' in element.attributes) { element.disabled = true; }
      if ('type' in element.attributes) { element.type = element.attributes.type; }
      if ('style' in element.attributes) {
         element.attributes.style.split(';').forEach((rule) => {
            const colon = rule.indexOf(':');
            if (colon > 0) { element.style[rule.slice(0, colon).trim()] = rule.slice(colon + 1).trim(); }
         });
      }
      top().appendChild(element);
      if (RAW.has(name)) {
         const close = text.toLowerCase().indexOf('</' + name, i);
         const end = close < 0 ? text.length : close;
         if (end > i) { element.appendChild(new TextNode(text.slice(i, end))); }
         i = close < 0 ? text.length : text.indexOf('>', close) + 1;
         continue;
      }
      if (!VOID.has(name) && !inside.trim().endsWith('/')) { stack.push(element); }
   }
   return { root };
}

const parsed = parseHtml(fs.readFileSync(pagePath, 'utf8'));

const document = new Element('#document');
document.hidden = false;
document.activeElement = null;
parsed.root.childNodes.slice().forEach((n) => document.appendChild(n));
document.documentElement = document.querySelector('html');
document.body = document.querySelector('body');
if (!document.body) { throw new Error('the page has no <body>'); }
document.getElementById = function (id) {
   let found = null;
   walk(document, (n) => { if (!found && n.attributes.id === id) { found = n; } });
   return found;
};
document.createElement = function (tag) { return new Element(tag); };

/* ------------------------------------------------ timers, storage, the rest */

const timers = [];
const intervals = [];
let timerId = 1;
function fireTimers() {
   const due = timers.splice(0, timers.length);
   due.forEach((t) => t.fn());
   return due.length;
}
function fireIntervals() { intervals.slice().forEach((t) => t.fn()); }

function storage() {
   const store = new Map();
   return {
      store,
      getItem: (k) => (store.has(k) ? store.get(k) : null),
      setItem: (k, v) => store.set(k, String(v)),
      removeItem: (k) => store.delete(k),
      get length() { return store.size; }
   };
}
const localStorage = storage();
const sessionStorage = storage();

let clock = 0;
const performance = { now: () => { clock += 1; return clock; } };

const confirmations = [];
let confirmAnswer = true;
const consoleErrors = [];
let clipboard = null;

/* ------------------------------------------------------- the recorded server */

const requests = [];
let signedIn = false;
let otpRequired = false;
let nextRefusal = null;
let restartProbes = 0;
let nextId = 100;
// The language route's two failure shapes: a server without the catalogues
// installed, and a catalogue that lacks a key the page has.
let catalogueMissing = false;
let droppedKey = null;

const RULES_POST = 'Body: name (required, at most 100 characters), active (default true), all_criteria (default true: every criterion must match; false: any one), criteria and actions as arrays of objects in the order they run. A criterion: field (from, to, cc, subject, body, message_size, recipient_list, delivery_attempts, or header with the header\'s name in header), match (equals, not_equals, contains, not_contains, less_than, greater_than, regex, wildcard) and value (at most 2000 characters; a regex must compile). An action: type and the parameters that type takes - forward: to and abort_spam_flagged; reply: from_name, from_address (required), subject, body and abort_spam_flagged; move_to_folder: folder; script_function: script_function; set_header: header and value; send_using_route: route_id; bind_to_address: value; delete, stop and copy take none. value is also accepted as the parameter the listing shows under that name. An unknown key, word or type, a parameter the type does not take, or a missing one it needs, is refused naming it. Saved as the Control Panel saves a rule; it applies to the next message delivered. Server-wide; refused for domain-restricted keys.';

const ROUTE_PROPS = {
   domain_name: { type: 'string' }, description: { type: 'string' }, target_smtp_host: { type: 'string' },
   target_smtp_port: { type: 'integer' }, number_of_tries: { type: 'integer' }, minutes_between_try: { type: 'integer' },
   relayer_requires_authentication: { type: 'boolean' }, relayer_auth_username: { type: 'string' },
   relayer_auth_password: { type: 'string', writeOnly: true },
   treat_recipient_as_local_domain: { type: 'boolean' }, treat_security_as_local_domain: { type: 'boolean', deprecated: true },
   treat_sender_as_local_domain: { type: 'boolean' }, all_addresses: { type: 'boolean' },
   addresses: { type: 'array', items: { type: 'string' } },
   connection_security: { type: 'string', enum: ['none', 'starttls_optional', 'starttls_required', 'tls'] }
};
const ROUTES_POST = 'Body: domain_name and target_smtp_host (required); target_smtp_port (default 25), number_of_tries (default 3), minutes_between_try (default 10), relayer_requires_authentication (default false, and then relayer_auth_username is required), relayer_auth_username, relayer_auth_password (write-only), treat_recipient_as_local_domain or treat_security_as_local_domain (default false), treat_sender_as_local_domain (default false), all_addresses (default true), addresses (an array of e-mail addresses) and connection_security (default none). Persisted and put into effect exactly as a route saved in the Control Panel is: the next message to the domain uses it. Server-wide; refused for domain-restricted keys.';

const PORT_PROPS = {
   protocol: { type: 'string', enum: ['smtp', 'pop3', 'imap'] }, address: { type: 'string' }, port: { type: 'integer' },
   connection_security: { type: 'string', enum: ['none', 'tls', 'starttls_optional', 'starttls_required'] },
   certificate_id: { type: 'integer' },
   client_certificate_policy: { type: 'string', enum: ['off', 'request', 'require'] },
   client_certificate_ca_file: { type: 'string' }
};
const PORTS_POST = 'The listeners are created when the server starts, so a port added here takes effect when the server restarts. address defaults to 0.0.0.0, connection_security to none, certificate_id to 0 and client_certificate_policy to off. tls and both starttls values need a certificate_id, and a client certificate policy other than off needs a CA file and a security that runs a handshake - the same refusals the Control Panel meets, in the same words. Server-wide; refused for domain-restricted keys.';
const PORTS_PUT = 'The whole record, with the fields and defaults of POST: a field left out takes its default, so send back what GET returned with the change made. Takes effect when the server restarts. Server-wide; refused for domain-restricted keys.';
const PORTS_DELETE = 'The row is gone at once; the listener stays up until the server restarts. Server-wide; refused for domain-restricted keys.';
const REINITIALIZE = 'What the Control Panel\'s Reinitialize does: every service is stopped, the configuration reloaded and the services started again in the same process, so that a port, a certificate binding or a setting the document marks as taking effect on restart takes effect now. Answers before it happens, because the REST listener itself restarts: poll GET /api/v1/status until it answers again. Server-wide; refused for domain-restricted and read-only keys.';

const CERT_PROPS = {
   name: { type: 'string' }, certificate_file: { type: 'string' }, private_key_file: { type: 'string' },
   private_key_password: { type: 'string', writeOnly: true }
};

// The domain's keys as ApplyDomainBody in RestApiAdministration.cpp reads
// them, and its description, which names three keys in the schema and the
// rest in prose.
const DOMAIN_KEYS = ['name', 'active', 'postmaster', 'max_message_size_kb', 'max_size_mb', 'max_account_size_mb',
   'max_accounts', 'max_aliases', 'max_lists', 'max_accounts_enabled', 'max_aliases_enabled', 'max_lists_enabled',
   'plus_addressing_enabled', 'plus_addressing_character', 'use_greylisting',
   'signature_enabled', 'signature_method', 'signature_plain_text', 'signature_html', 'signature_add_to_replies', 'signature_add_to_local_mail',
   'dkim_enabled', 'dkim_selector', 'dkim_private_key_file', 'dkim_signing_algorithm',
   'message_retention_days', 'relay_host', 'relay_port', 'relay_requires_auth', 'relay_username', 'relay_password', 'relay_connection_security',
   'vacation_enabled', 'vacation_subject', 'vacation_message'];
const DOMAIN_PUT = 'Body: active (required) and any subset of postmaster, name (a new name renames the domain and every address in it, as the Control Panel does), max_message_size_kb, max_size_mb, max_account_size_mb, max_accounts, max_aliases, max_lists and their switches max_accounts_enabled, max_aliases_enabled, max_lists_enabled, plus_addressing_enabled, plus_addressing_character, use_greylisting, signature_enabled, signature_method (set_if_not_specified, overwrite or append), signature_plain_text, signature_html, signature_add_to_replies, signature_add_to_local_mail, dkim_enabled, dkim_selector, dkim_private_key_file, dkim_signing_algorithm (sha1 or sha256), message_retention_days, relay_host, relay_port, relay_requires_auth, relay_username, relay_password (write-only), relay_connection_security, vacation_enabled, vacation_subject, vacation_message. A field left out keeps its value; everything is checked before anything is applied, and an unknown field or a wrong type is a 400 naming it.';
const DOMAINS_POST = 'Body: name (required), active (default true) and postmaster. The name is judged as the Control Panel judges it - a valid domain name, not one a domain alias already has - and every other setting takes the default a new domain gets there. Server-wide; refused for domain-restricted keys.';

// The account's update schema as RestApiRoutes.cpp emits it - every field
// typed, the admin level's three words - and its description; the GET answers
// the same keys less the password.
const ACCOUNT_PROPS = {
   active: { type: 'boolean' }, password: { type: 'string' }, max_size_mb: { type: 'integer' }, first_name: { type: 'string' }, last_name: { type: 'string' },
   forward_enabled: { type: 'boolean' }, forward_address: { type: 'string' }, forward_keep_original: { type: 'boolean' },
   signature_enabled: { type: 'boolean' }, signature_plain_text: { type: 'string' }, signature_html: { type: 'string' },
   admin_level: { type: 'string', enum: ['user', 'domain', 'server'] },
   antispam_enabled: { type: 'boolean' }, spam_mark_threshold: { type: 'integer' }, spam_delete_threshold: { type: 'integer' },
   forward_abort_spam_flagged: { type: 'boolean' }, message_retention_days: { type: 'integer' },
   vacation_enabled: { type: 'boolean' }, vacation_subject: { type: 'string' }, vacation_message: { type: 'string' },
   vacation_expires: { type: 'boolean' }, vacation_expires_date: { type: 'string' }, vacation_begin_date: { type: 'string' }, vacation_abort_spam_flagged: { type: 'boolean' },
   ad_enabled: { type: 'boolean' }, ad_domain: { type: 'string' }, ad_username: { type: 'string' }, sieve_script: { type: 'string' }
};
const ACCOUNT_PUT = 'Any subset of active, password, max_size_mb, first_name, last_name, forward_enabled, forward_address, forward_keep_original, signature_enabled, signature_plain_text, signature_html, admin_level (user, domain or server), antispam_enabled, spam_mark_threshold and spam_delete_threshold (-1 is the server\'s own value), forward_abort_spam_flagged, message_retention_days (-1 keeps forever, 0 follows the domain), vacation_enabled, vacation_subject, vacation_message, vacation_expires, vacation_expires_date and vacation_begin_date (YYYY-MM-DD; empty means today, as the Control Panel takes them), vacation_abort_spam_flagged, ad_enabled, ad_domain, ad_username and sieve_script; a field the body does not name is left as it is, and an unknown field is refused by name. Scoped to the address\'s domain.';
// An account as AccountEntryJson answers it: every field, never the password.
function accountRecord(address, active) {
   return { address, active, max_size_mb: 0, first_name: '', last_name: '', forward_enabled: false, forward_address: '', forward_keep_original: false,
      signature_enabled: false, signature_plain_text: '', signature_html: '', vacation_enabled: false, vacation_subject: '', vacation_message: '',
      vacation_expires: false, vacation_expires_date: '', message_retention_days: 0, admin_level: 'user', antispam_enabled: true,
      spam_mark_threshold: -1, spam_delete_threshold: -1, forward_abort_spam_flagged: false, vacation_begin_date: '', vacation_abort_spam_flagged: false,
      ad_enabled: false, ad_domain: '', ad_username: '', sieve_script: '' };
}

// The distribution list's schemas and descriptions as RestApiServer.cpp emits
// them: the create takes the address and the members, the update neither.
const LIST_PROPS = {
   active: { type: 'boolean' }, require_auth: { type: 'boolean' }, mode: { type: 'string', enum: ['public', 'membership', 'announcement', 'domain_members'] },
   require_sender_address: { type: 'string' }, moderator_address: { type: 'string' }, bounce_address: { type: 'string' }
};
const LIST_CREATE_PROPS = Object.assign({ address: { type: 'string' }, members: { type: 'array', items: { type: 'string' } } }, LIST_PROPS);
const LISTS_POST = 'Body: address (required, in this domain), members (an array of addresses), active (default true), require_auth (default false), mode (public, membership, announcement or domain_members; default public, and any other word is refused as put_Mode refuses it), require_sender_address (the one sender an announcement list accepts), moderator_address and bounce_address. Saved as the Control Panel saves a list, with the same limitation check. Scoped to the domain.';
const LIST_PUT = 'Body: any subset of active, require_auth, mode (public, membership, announcement or domain_members), require_sender_address, moderator_address and bounce_address; a field left out keeps its value, and the members are not changed here. Everything is checked before anything is applied. Scoped to the address\'s domain.';

// The seven small collections as RestApiAntiSpamLists.cpp and
// RestApiBlockedAttachments.cpp describe them: the create's schema, its
// required keys and the three descriptions; the update takes any subset of
// the same keys. The entries the recorded server starts with are below.
const DNS_LIST_PROPS = { dns_host: { type: 'string' }, expected_result: { type: 'string' }, reject_message: { type: 'string' }, score: { type: 'integer' }, active: { type: 'boolean' } };
const COLLECTION_SPECS = {
   '/api/v1/dns-blacklists': { props: DNS_LIST_PROPS, required: ['dns_host'], noun: 'DNS black list',
      get: 'AntiSpam.DNSBlackLists over COM. Each entry: id, active, dns_host, expected_result, reject_message, score. Server-wide; refused for domain-restricted keys.',
      post: 'Body: dns_host (required, the zone queried), expected_result (the answers that mean listed, 127.0.0.2 or a range or wildcard), reject_message, score and active (default true). Saved as a list saved in the Control Panel is, and consulted for the next message. Server-wide; refused for domain-restricted and read-only keys.' },
   '/api/v1/surbl-servers': { props: DNS_LIST_PROPS, required: ['dns_host'], noun: 'SURBL server',
      get: 'AntiSpam.SURBLServers over COM. Each entry: id, active, dns_host, expected_result, reject_message, score. Server-wide; refused for domain-restricted keys.',
      post: 'Body: dns_host (required, the zone the domains in a message are looked up in), expected_result (the answers that mean listed; empty is any answer but a refusal code), reject_message, score and active (default true). Saved as one saved in the Control Panel is, and consulted for the next message. Server-wide; refused for domain-restricted and read-only keys.' },
   '/api/v1/whitelist-addresses': { props: { lower_ip: { type: 'string' }, upper_ip: { type: 'string' }, email_address: { type: 'string' }, description: { type: 'string' } }, required: ['lower_ip', 'upper_ip'], noun: 'white-list address',
      get: 'AntiSpam.WhiteListAddresses over COM: the senders and address ranges the spam tests skip. Each entry: id, lower_ip, upper_ip, email_address, description. Server-wide; refused for domain-restricted keys.',
      post: 'Body: lower_ip and upper_ip (required, the address range), email_address (the sender, with wildcards; empty is any) and description. Saved as one saved in the Control Panel is; the white-list cache is reloaded for the next message. Server-wide; refused for domain-restricted and read-only keys.' },
   '/api/v1/blocked-senders': { props: { address: { type: 'string' }, score: { type: 'integer' }, description: { type: 'string' } }, required: ['address'], noun: 'blocked sender',
      get: 'AntiSpam.BlockedSenders over COM: the envelope senders, by address or by domain, that score. Each entry: id, address, score, description. Server-wide; refused for domain-restricted keys.',
      post: 'Body: address (required; an address, or a domain that covers its subdomains), score and description. Saved as one saved in the Control Panel is; the blocked-sender cache is reloaded for the next message. Server-wide; refused for domain-restricted and read-only keys.' },
   '/api/v1/blocked-attachments': { props: { wildcard: { type: 'string' }, description: { type: 'string' } }, required: ['wildcard'], noun: 'blocked attachment',
      get: 'AntiVirus.BlockedAttachments over COM: the file-name wildcards stripped from incoming messages when attachment_blocking_enabled is on in the anti-virus settings. Each entry: id, wildcard, description. Server-wide; refused for domain-restricted keys.',
      post: 'Body: wildcard (required; a file-name pattern such as *.exe) and description. Saved as one saved in the Control Panel is, and consulted for the next message scanned. Server-wide; refused for domain-restricted and read-only keys.' },
   '/api/v1/greylisting-white-addresses': { props: { ip_address: { type: 'string' }, description: { type: 'string' } }, required: ['ip_address'], noun: 'greylisting white address',
      get: 'AntiSpam.GreyListingWhiteAddresses over COM: the address patterns whose connections are never greylisted. Each entry: id, ip_address (with the wildcards as typed), description. Server-wide; refused for domain-restricted keys.',
      post: 'Body: ip_address (required; an address, or a pattern such as 192.168.* or 2001:db8:*) and description. Saved as one saved in the Control Panel is, and consulted for the next connection. Server-wide; refused for domain-restricted and read-only keys.' },
   '/api/v1/incoming-relays': { props: { name: { type: 'string' }, lower_ip: { type: 'string' }, upper_ip: { type: 'string' } }, required: ['name', 'lower_ip', 'upper_ip'], noun: 'incoming relay',
      get: 'Settings.IncomingRelays over COM: the address ranges whose Received headers are skipped when the real sender of a message is looked for. Each entry: id, name, lower_ip, upper_ip. Server-wide; refused for domain-restricted keys.',
      post: 'Body: name, lower_ip and upper_ip (all required). Saved as one saved in the Control Panel is, and in force for the next session. Server-wide; refused for domain-restricted and read-only keys.' }
};
const COLLECTION_PUT = 'Body: any subset of the fields POST takes; a field left out keeps its value. Nothing changes when the body is refused. Server-wide; refused for domain-restricted and read-only keys.';
function collectionPaths() {
   const out = {};
   Object.keys(COLLECTION_SPECS).forEach((path) => {
      const c = COLLECTION_SPECS[path];
      out[path] = { get: { summary: 'List the ' + c.noun + 's', description: c.get }, post: { summary: 'Add a ' + c.noun, description: c.post, requestBody: body(c.props, c.required) } };
      out[path + '/{id}'] = { put: { summary: 'Change a ' + c.noun, description: COLLECTION_PUT, requestBody: body(c.props) }, delete: { summary: 'Remove a ' + c.noun } };
   });
   return out;
}

// The IP range's schema as the ports and certificates literal emits it, every
// field typed; the update takes any subset of the same keys.
const RANGE_PROPS = {
   name: { type: 'string' }, lower: { type: 'string' }, upper: { type: 'string' }, priority: { type: 'integer' },
   allow_smtp: { type: 'boolean' }, allow_imap: { type: 'boolean' }, allow_pop3: { type: 'boolean' },
   deliver_local_to_local: { type: 'boolean' }, deliver_local_to_remote: { type: 'boolean' }, deliver_remote_to_local: { type: 'boolean' }, deliver_remote_to_remote: { type: 'boolean' },
   require_auth_local_to_local: { type: 'boolean' }, require_auth_local_to_remote: { type: 'boolean' }, require_auth_remote_to_local: { type: 'boolean' }, require_auth_remote_to_remote: { type: 'boolean' },
   require_tls_for_auth: { type: 'boolean' }, spam_protection: { type: 'boolean' }, virus_protection: { type: 'boolean' },
   expires: { type: 'boolean' }, expires_time: { type: 'string' }
};
const RANGE_PUT = 'Body: any subset of the fields POST takes - name, lower, upper, priority, the permission flags, expires and expires_time; a field left out keeps its value, and a body that turns expiry on must carry expires_time in it. The same check as saving the range in the Control Panel; nothing changes when it is refused. Server-wide; refused for domain-restricted and read-only keys.';
// The expiry as the two handlers read it: the time only with the flag, and
// the flag turned on only with a time; a time is YYYY-MM-DD HH:MM:SS.
function expiryProblem(parsed, was) {
   const time = String(parsed.expires_time || '').trim();
   const expires = 'expires' in parsed ? parsed.expires : !!(was && was.expires);
   if (time && !expires) { return 'expires_time is taken only when expires is true'; }
   if (time && !/^\d{4}-\d{2}-\d{2}( \d{2}:\d{2}:\d{2})?$/.test(time)) { return 'expires_time must be a date and time as YYYY-MM-DD HH:MM:SS'; }
   if (!time && expires && !(was && was.expires)) { return was ? 'expires_time is required when a range is made to expire' : 'expires_time is required when a range expires'; }
   return null;
}
// The defaults HandleCreateIpRange_ applies to a flag left out.
const RANGE_CREATE_DEFAULTS = { allow_smtp: true, allow_imap: true, allow_pop3: true,
   deliver_local_to_local: true, deliver_local_to_remote: false, deliver_remote_to_local: true, deliver_remote_to_remote: false,
   require_auth_local_to_local: false, require_auth_local_to_remote: true, require_auth_remote_to_local: false, require_auth_remote_to_remote: true,
   require_tls_for_auth: false, spam_protection: true, virus_protection: true };
const IPV4 = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/;
function ipAddress(text) { return IPV4.test(text) || /^[0-9a-f:]+$/i.test(text) && text.indexOf(':') >= 0; }

// The fetch account's schema and description as RestApiFetchAccounts.cpp
// emits them; the update takes any subset of the same keys.
const FETCH_PROPS = {
   name: { type: 'string' }, server_address: { type: 'string' }, port: { type: 'integer' },
   server_type: { type: 'string', enum: ['pop3', 'imap'] }, username: { type: 'string' }, password: { type: 'string', writeOnly: true },
   enabled: { type: 'boolean' }, minutes_between_fetch: { type: 'integer' }, days_to_keep_messages: { type: 'integer' },
   connection_security: { type: 'string', enum: ['none', 'starttls_optional', 'starttls_required', 'tls'] },
   process_mime_recipients: { type: 'boolean' }, process_mime_date: { type: 'boolean' }, use_antispam: { type: 'boolean' }, use_antivirus: { type: 'boolean' },
   enable_route_recipients: { type: 'boolean' }, mime_recipient_headers: { type: 'string' }, mirror_folders: { type: 'boolean' }
};
const FETCH_GET = 'The remote POP3 or IMAP mailboxes the server collects into this account - Account.FetchAccounts over COM. Each entry: id, name, server_address, port, server_type (pop3 or imap), username, enabled, minutes_between_fetch, days_to_keep_messages, connection_security, process_mime_recipients, process_mime_date, use_antispam, use_antivirus, enable_route_recipients, mime_recipient_headers, mirror_folders, locked (a collection is running now) and next_download_time. The remote password is never emitted. A key restricted to named domains reaches the accounts of those domains only.';
const FETCH_POST = 'Body: name, server_address and port (required); server_type (pop3, the default, or imap), username, password (write-only), enabled (default true), minutes_between_fetch (default 30), days_to_keep_messages (default 0: delete after collecting; -1 keeps every message; for IMAP the remote INBOX is collected once by UID and left intact when this says so), connection_security (none, starttls_optional, starttls_required, tls; default none), process_mime_recipients, process_mime_date, use_antispam, use_antivirus, enable_route_recipients, mime_recipient_headers, mirror_folders. What InterfaceFetchAccounts.Add and Save do; the first collection is scheduled at once. Everything is checked before anything is saved: an unknown field, a value of the wrong type or a value out of range is a 400 naming it.';
const FETCH_PUT = 'Body: any subset of the fields POST takes; a field left out keeps its value, and a password left out is kept. The same checks as POST, and nothing changes when one fails. What the setters and Save do over COM.';
// A fetch account as FetchAccountJson emits it, at the constructor's defaults.
function fetchRecord(id, name) {
   return { id, name, server_address: '', port: 110, server_type: 'pop3', username: '', minutes_between_fetch: 30, days_to_keep_messages: 0,
      connection_security: 'none', mime_recipient_headers: '', enabled: true, process_mime_recipients: false, process_mime_date: false,
      use_antispam: true, use_antivirus: true, enable_route_recipients: false, mirror_folders: false, locked: false, next_download_time: '' };
}
// ApplyBody's checks, the first problem being the answer.
function fetchProblem(parsed, creating) {
   for (const key of Object.keys(parsed)) { if (!(key in FETCH_PROPS)) { return 'unknown field: ' + key; } }
   const range = (key, low, high) => (key in parsed && (typeof parsed[key] !== 'number' || parsed[key] < low || parsed[key] > high)) ? key + ' must be a whole number between ' + low + ' and ' + high : null;
   const problem = range('port', 1, 65535) || range('minutes_between_fetch', 1, 100000) || range('days_to_keep_messages', -1, 100000);
   if (problem) { return problem; }
   if ('server_type' in parsed && FETCH_PROPS.server_type.enum.indexOf(parsed.server_type) < 0) { return 'server_type must be pop3 or imap'; }
   if ('connection_security' in parsed && FETCH_PROPS.connection_security.enum.indexOf(parsed.connection_security) < 0) { return 'connection_security must be one of none, starttls_optional, starttls_required, tls'; }
   if (creating && !String(parsed.name || '').trim()) { return 'name is required'; }
   if (creating && !String(parsed.server_address || '').trim()) { return 'server_address is required'; }
   if (creating && !('port' in parsed)) { return 'port is required'; }
   return null;
}

// A domain as DomainEntryJson_ emits it: every field, the relay password
// never among them.
function domainRecord(name, active, postmaster) {
   return { name, active, postmaster, max_message_size_kb: 0, max_size_mb: 0, max_account_size_mb: 0,
      max_accounts: 0, max_aliases: 0, max_lists: 0, message_retention_days: 0, relay_port: 0,
      max_accounts_enabled: false, max_aliases_enabled: false, max_lists_enabled: false,
      plus_addressing_enabled: false, plus_addressing_character: '+', use_greylisting: false,
      signature_enabled: false, signature_method: 'set_if_not_specified', signature_plain_text: '', signature_html: '',
      signature_add_to_replies: false, signature_add_to_local_mail: false,
      dkim_enabled: false, dkim_selector: '', dkim_private_key_file: '', dkim_signing_algorithm: 'sha256',
      relay_host: '', relay_requires_auth: false, relay_username: '', relay_connection_security: 'none',
      vacation_enabled: false, vacation_subject: '', vacation_message: '' };
}

function body(props, required) {
   return { content: { 'application/json': { schema: Object.assign({ type: 'object', properties: props }, required ? { required } : {}) } } };
}

/* The settings groups, in the shape OpenApiProperties emits them: a type, a
   description, an enum for a word setting, readOnly for a fact, writeOnly for
   a secret, and "Takes effect when the server restarts." for a key the running
   server reads only at start. */
const SETTING_GROUPS = {
   '/api/v1/settings': {
      props: {
         hostname: { type: 'string', description: 'The name the server gives in its SMTP banner.' },
         max_message_size_kb: { type: 'integer', description: 'The largest message SMTP and IMAP APPEND accept, in KB; 0 is no limit.' },
         service_smtp: { type: 'boolean', description: 'Whether the SMTP server runs. Takes effect when the server restarts.' },
         smtp_relayer_host: { type: 'string', description: 'The smart host every outbound message goes through; empty delivers by MX.' },
         smtp_relayer_connection_security: { type: 'string', description: 'How the connection to the smart host is secured.', enum: ['none', 'tls', 'starttls_optional', 'starttls_required'] },
         smtp_relayer_password: { type: 'string', description: 'The password for the smart host. Accepted here and never emitted.', writeOnly: true }
      },
      values: { hostname: 'mail.example.com', max_message_size_kb: 10240, service_smtp: true, smtp_relayer_host: '', smtp_relayer_connection_security: 'none' }
   },
   '/api/v1/settings/antispam': {
      props: {
         spam_mark_threshold: { type: 'integer', description: 'The score at which a message is marked as spam.' },
         spam_delete_threshold: { type: 'integer', description: 'The score at which a message is deleted.' },
         use_spf: { type: 'boolean', description: 'Whether SPF is checked.' }
      },
      values: { spam_mark_threshold: 5, spam_delete_threshold: 20, use_spf: true }
   },
   '/api/v1/settings/logging': {
      props: {
         enabled: { type: 'boolean', description: 'Whether anything is logged at all.' },
         log_smtp: { type: 'boolean', description: 'Whether the SMTP conversations are logged.' },
         log_directory: { type: 'string', description: 'Where the log files are written.', readOnly: true }
      },
      values: { enabled: true, log_smtp: false, log_directory: '/var/log/hmailserver' }
   },
   '/api/v1/settings/antivirus': {
      props: {
         clamav_enabled: { type: 'boolean', description: 'Whether messages are scanned by a ClamAV daemon.' },
         clamav_host: { type: 'string', description: 'The ClamAV daemon\'s host.' },
         clamav_port: { type: 'integer', description: 'The ClamAV daemon\'s port, 1 to 65535.' },
         action: { type: 'string', description: 'What is done with a message a scanner flags.', enum: ['delete_email', 'delete_attachments'] },
         maximum_message_size_kb: { type: 'integer', description: 'Messages larger than this, in KB, are not scanned; 0 scans every size.' }
      },
      values: { clamav_enabled: false, clamav_host: 'localhost', clamav_port: 3310, action: 'delete_attachments', maximum_message_size_kb: 0 }
   },
   // The three groups the Settings view gained on 14 September: the scripting
   // switch and language, the cache's switch, ceilings, lives and counters
   // (the counters read-only, as the table has them), and the indexing switch
   // with its two counts.
   '/api/v1/settings/scripting': {
      props: {
         enabled: { type: 'boolean', description: 'Whether the event handlers run at all.' },
         language: { type: 'string', description: 'VBScript or JScript, the language the event handlers are written in; any other name is refused.' },
         current_script_file: { type: 'string', description: 'The event-handler file the server is running, in the event directory.', readOnly: true }
      },
      values: { enabled: false, language: 'VBScript', current_script_file: '/var/lib/hmailserver/events/EventHandlers.vbs' }
   },
   '/api/v1/settings/cache': {
      props: {
         enabled: { type: 'boolean', description: 'Whether the domain, account, alias and distribution-list caches are used at all.' },
         domain_cache_size_kb: { type: 'integer', description: 'What the domain cache holds now, in kilobytes.', readOnly: true },
         domain_cache_max_size_kb: { type: 'integer', description: 'The domain cache\'s ceiling, in kilobytes. Held in memory only, as over COM: the built-in 10240 returns when the server starts.' },
         domain_cache_ttl: { type: 'integer', description: 'Seconds a domain stays cached.' },
         domain_hit_rate: { type: 'integer', description: 'Of the domain lookups since the cache was last cleared, the percentage the cache answered.', readOnly: true }
      },
      values: { enabled: true, domain_cache_size_kb: 12, domain_cache_max_size_kb: 10240, domain_cache_ttl: 3600, domain_hit_rate: 97 }
   },
   '/api/v1/settings/indexing': {
      props: {
         enabled: { type: 'boolean', description: 'Whether delivered messages are indexed for search.' },
         total_indexed_count: { type: 'integer', description: 'How many messages the index holds.', readOnly: true },
         total_message_count: { type: 'integer', description: 'How many delivered messages there are to index.', readOnly: true }
      },
      values: { enabled: true, total_indexed_count: 40, total_message_count: 45 }
   },
   '/api/v1/settings/backup': {
      props: {
         destination: { type: 'string', description: 'The directory a backup is written to.' },
         backup_domains: { type: 'boolean', description: 'Whether the domains, accounts, aliases and lists are backed up.' },
         backup_messages: { type: 'boolean', description: 'Whether the message files are backed up.' },
         backup_settings: { type: 'boolean', description: 'Whether the server settings are backed up.' },
         compress: { type: 'boolean', description: 'Whether the backup is compressed into one file.' },
         log_file: { type: 'string', description: 'The full path of the backup log, beside the other logs.', readOnly: true }
      },
      values: { destination: '/var/backups/hmailserver', backup_domains: true, backup_messages: true, backup_settings: true, compress: false, log_file: '/var/log/hmailserver/hmailserver_backup.log' }
   }
};
const secrets = {};

function settingsPath(path) {
   return { get: { summary: 'The group', description: 'Every key the PUT lists, as the server holds it now; the write-only ones are left out.' },
      put: { summary: 'Change the group', description: 'Body: any subset of the writable keys below.', requestBody: body(SETTING_GROUPS[path].props) } };
}

const spec = {
   openapi: '3.0.0',
   paths: {
      '/api/v1/settings': settingsPath('/api/v1/settings'),
      '/api/v1/settings/antispam': settingsPath('/api/v1/settings/antispam'),
      '/api/v1/settings/logging': settingsPath('/api/v1/settings/logging'),
      '/api/v1/settings/antivirus': settingsPath('/api/v1/settings/antivirus'),
      '/api/v1/rules': {
         get: { summary: 'List the global rules with their criteria and actions', description: 'Each entry: id, name, active, all_criteria, criteria (field, header, match, value) and actions (type, value). Server-wide; refused for domain-restricted keys.' },
         post: { summary: 'Create a global rule', description: RULES_POST, requestBody: body({ name: { type: 'string' } }, ['name']) }
      },
      '/api/v1/rules/{id}': { put: { summary: 'Replace a global rule' }, delete: { summary: 'Delete a global rule' } },
      '/api/v1/routes': {
         get: { summary: 'List the SMTP routes', description: 'The routes the running server delivers by, as the Control Panel lists them.' },
         post: { summary: 'Create an SMTP route', description: ROUTES_POST, requestBody: body(ROUTE_PROPS, ['domain_name', 'target_smtp_host']) }
      },
      '/api/v1/routes/{id}': {
         put: { summary: 'Replace an SMTP route', description: 'The whole record, with the same fields, defaults and checks as the create.', requestBody: body(ROUTE_PROPS, ['domain_name', 'target_smtp_host']) },
         delete: { summary: 'Delete an SMTP route' }
      },
      '/api/v1/certificates': {
         get: { summary: 'List the SSL certificates', description: 'Names and file paths, never a private key password.' },
         post: { summary: 'Add an SSL certificate', description: 'A name and the paths of a PEM certificate file and its private key file on the server.', requestBody: body(CERT_PROPS, ['name', 'certificate_file', 'private_key_file']) }
      },
      '/api/v1/certificates/{id}': { delete: { summary: 'Delete an SSL certificate' } },
      '/api/v1/ports': {
         get: { summary: 'List the TCP/IP ports', description: 'Every listener the server is configured with.' },
         post: { summary: 'Add a TCP/IP port', description: PORTS_POST, requestBody: body(PORT_PROPS, ['protocol', 'port']) }
      },
      '/api/v1/ports/{id}': {
         put: { summary: 'Replace a TCP/IP port', description: PORTS_PUT, requestBody: body(PORT_PROPS, ['protocol', 'port']) },
         delete: { summary: 'Delete a TCP/IP port', description: PORTS_DELETE }
      },
      '/api/v1/server/reinitialize': { post: { summary: 'Restart the services in place', description: REINITIALIZE } },
      '/api/v1/domains': {
         get: { summary: 'List domains', description: 'A domain-restricted key sees only its own domains. Each entry: name, active, postmaster.' },
         post: { summary: 'Create a domain', description: DOMAINS_POST, requestBody: body({ name: { type: 'string' }, active: { type: 'boolean' }, postmaster: { type: 'string' } }, ['name']) }
      },
      '/api/v1/domains/{domain}': {
         put: { summary: 'Change a domain', description: DOMAIN_PUT, requestBody: body({ active: { type: 'boolean' }, postmaster: { type: 'string' }, name: { type: 'string' } }, ['active']) },
         delete: { summary: 'Delete a domain with everything in it' }
      },
      '/api/v1/domains/{domain}/domain-aliases': {
         get: { summary: 'The domain\'s aliases - other names the domain answers to', description: 'Each entry: id, name.' },
         post: { summary: 'Add a domain alias', description: 'Body: name.', requestBody: body({ name: { type: 'string' } }, ['name']) }
      },
      '/api/v1/domains/{domain}/domain-aliases/{name}': { delete: { summary: 'Remove a domain alias' } },
      '/api/v1/domains/{domain}/aliases': {
         get: { summary: 'List aliases in a domain' },
         post: { summary: 'Create an alias', description: 'Body: name (the alias address, in this domain), value (the e-mail address it delivers to) and active (default true). Judged as the Control Panel judges an alias - a name an account or a distribution list already has, or a domain at its alias limit, is refused with the same sentence - and in effect for the next message to the name. Scoped to the domain.',
            requestBody: body({ name: { type: 'string' }, value: { type: 'string' }, active: { type: 'boolean' } }, ['name', 'value']) }
      },
      '/api/v1/aliases/{address}': { delete: { summary: 'Delete an alias', description: 'Scoped to the address\'s domain.' } },
      '/api/v1/domains/{domain}/lists': {
         get: { summary: 'List the distribution lists in a domain, with their members', description: 'Each entry: address, active, require_auth, mode (public, membership, announcement or domain_members), require_sender_address, moderator_address, bounce_address and members. Scoped to the domain.' },
         post: { summary: 'Create a distribution list', description: LISTS_POST, requestBody: body(LIST_CREATE_PROPS, ['address']) }
      },
      '/api/v1/lists/{address}': {
         put: { summary: 'Change a distribution list', description: LIST_PUT, requestBody: body(LIST_PROPS) },
         delete: { summary: 'Delete a distribution list' }
      },
      '/api/v1/accounts/{address}': {
         get: { summary: 'Read an account', description: 'The account whole, as the update answers it, less the password, the hash and the TOTP secret. Scoped to the address\'s domain.' },
         put: { summary: 'Update an account', description: ACCOUNT_PUT, requestBody: body(ACCOUNT_PROPS) },
         delete: { summary: 'Delete an account' }
      },
      '/api/v1/ipranges': {
         get: { summary: 'List the IP ranges', description: 'Server-wide; refused for domain-restricted keys.' },
         post: { summary: 'Create an IP range', requestBody: body(RANGE_PROPS, ['name', 'lower', 'upper']) }
      },
      '/api/v1/ipranges/{id}': {
         put: { summary: 'Change an IP range', description: RANGE_PUT, requestBody: body({}) },
         delete: { summary: 'Delete an IP range' }
      },
      '/api/v1/accounts/{address}/fetch-accounts': {
         get: { summary: 'The account\'s external (fetch) accounts', description: FETCH_GET },
         post: { summary: 'Create an external (fetch) account', description: FETCH_POST, requestBody: body(FETCH_PROPS, ['name', 'server_address', 'port']) }
      },
      '/api/v1/accounts/{address}/fetch-accounts/{id}': {
         get: { summary: 'One external (fetch) account' },
         put: { summary: 'Change an external (fetch) account', description: FETCH_PUT, requestBody: body({}) },
         delete: { summary: 'Delete an external (fetch) account' }
      },
      '/api/v1/accounts/{address}/fetch-accounts/{id}/download': { post: { summary: 'Collect from the remote mailbox now' } },
      '/api/v1/settings/backup': settingsPath('/api/v1/settings/backup'),
      ...collectionPaths(),
      '/api/v1/settings/messages': { get: { summary: 'The server\'s message texts', description: 'The texts the server puts in the mail it writes itself - Settings.ServerMessages over COM. Each entry: id, name, text. Server-wide; refused for domain-restricted keys.' } },
      '/api/v1/settings/messages/{name}': { put: { summary: 'Change one server message text', description: 'Body: text. The name is the table\'s own, as the listing shows it. In use at once. Server-wide; refused for domain-restricted and read-only keys.', requestBody: body({ text: { type: 'string' } }, ['text']) } },
      '/api/v1/settings/scripting': settingsPath('/api/v1/settings/scripting'),
      '/api/v1/settings/scripting/reload': { post: { summary: 'Load the event-handler script again', description: 'What Scripting.Reload does over COM: the script file is read again and the handlers it defines take over from the next event.' } },
      '/api/v1/settings/scripting/check': { post: { summary: 'Check the event-handler script\'s syntax', description: 'What Scripting.CheckSyntax does over COM: result is empty when the script parses, and the parser\'s message otherwise. Nothing is changed.' } },
      '/api/v1/settings/cache': settingsPath('/api/v1/settings/cache'),
      '/api/v1/settings/cache/clear': { post: { summary: 'Empty the caches', description: 'What Settings.Cache.Clear does over COM: the domain, account, alias and distribution-list caches are emptied.' } },
      '/api/v1/settings/indexing': settingsPath('/api/v1/settings/indexing'),
      '/api/v1/settings/indexing/index': { post: { summary: 'Index the messages not yet indexed, now', description: 'What Settings.MessageIndexing.Index does over COM: the indexer runs at once rather than at its next tick; the answer does not wait for it.' } },
      '/api/v1/settings/indexing/clear': { post: { summary: 'Empty the message index', description: 'What Settings.MessageIndexing.Clear does over COM: every indexed message\'s metadata, the full-text terms and the backfill cursor go.' } },
      '/api/v1/backup': {
         get: { summary: 'The backup manager\'s status text and the last lines of the backup log' },
         post: { summary: 'Start a backup with the configured settings', description: 'Runs on the maintenance queue; poll GET for the outcome.' }
      }
   }
};

const HOSTILE = 'evil<img src=x onerror=alert(1)>.test';

const state = {
   status: { version: '6.3.3', state: 3, processedMessages: 1234, spamMessages: 56, virusesRemoved: 7, sessions: { smtp: 3, imap: 12, pop3: 1 } },
   domains: [
      domainRecord('example.com', true, 'postmaster@example.com'),
      domainRecord(HOSTILE, false, ''),
      domainRecord('second.example', true, '')
   ],
   domainAliases: {
      'example.com': [{ id: 1, name: 'example.net' }],
      [HOSTILE]: [],
      'second.example': []
   },
   accounts: {
      'example.com': [Object.assign(accountRecord('anna@example.com', true), { first_name: 'Anna', admin_level: 'domain', sieve_script: 'require ["fileinto"];\r\nif header :contains "subject" "[SPAM]" { fileinto "Junk"; }' }),
         accountRecord('bob@example.com', false)],
      [HOSTILE]: [accountRecord('x@' + HOSTILE, true)],
      'second.example': []
   },
   lists: {
      'example.com': [{ address: 'all@example.com', active: true, require_auth: false, mode: 'public', require_sender_address: '', moderator_address: '', bounce_address: '',
         members: ['anna@example.com', 'bob@example.com'] }],
      [HOSTILE]: [],
      'second.example': []
   },
   aliases: {
      'example.com': [{ name: 'info@example.com', value: 'anna@example.com', active: true }],
      [HOSTILE]: [],
      'second.example': []
   },
   fetchAccounts: {
      'anna@example.com': [Object.assign(fetchRecord(7, 'ISP POP3'), { server_address: 'pop.isp.example', port: 995, connection_security: 'tls', username: 'anna.isp', next_download_time: '2026-09-14 10:30:00' })]
   },
   fetcherDown: false,
   backup: { status: '', log: ['2026-09-13 02:00:00 Backup started.', '2026-09-13 02:00:09 Backup completed.'] },
   backupRunning: false,
   scriptReloads: 0,
   scriptProblem: '',
   messages: [
      { id: 1, name: 'BOUNCE_MESSAGE', text: 'Your message could not be delivered.' },
      { id: 2, name: 'VIRUS_NOTIFICATION', text: 'A virus was found in a message sent to you.' }
   ],
   collections: {
      '/api/v1/dns-blacklists': [{ id: 11, active: true, dns_host: 'zen.spamhaus.org', expected_result: '127.0.0.2-11', reject_message: 'Listed at Spamhaus', score: 5 }],
      '/api/v1/surbl-servers': [{ id: 12, active: false, dns_host: 'multi.surbl.org', expected_result: '', reject_message: '', score: 5 }],
      '/api/v1/whitelist-addresses': [{ id: 13, lower_ip: '10.0.0.1', upper_ip: '10.0.0.9', email_address: '*@partner.example', description: 'The partner' }],
      '/api/v1/blocked-senders': [{ id: 14, address: 'spam.example', score: 100, description: 'A spam domain' }],
      '/api/v1/blocked-attachments': [{ id: 15, wildcard: '*.exe', description: 'Programs' }],
      '/api/v1/greylisting-white-addresses': [{ id: 16, ip_address: '10.0.0.*', description: 'The office' }],
      '/api/v1/incoming-relays': [{ id: 17, name: 'Front relay', lower_ip: '192.0.2.1', upper_ip: '192.0.2.1' }]
   },
   ranges: [
      Object.assign({ id: 1, name: 'My computer', lower: '127.0.0.1', upper: '127.0.0.1', priority: 15, expires: false, expires_time: '' }, RANGE_CREATE_DEFAULTS,
         { require_auth_local_to_remote: false, require_auth_remote_to_remote: false, deliver_local_to_remote: true, deliver_remote_to_remote: true }),
      Object.assign({ id: 2, name: 'Internet', lower: '0.0.0.0', upper: '255.255.255.255', priority: 10, expires: false, expires_time: '' }, RANGE_CREATE_DEFAULTS),
      Object.assign({ id: 3, name: 'Auto-ban: 203.0.113.9', lower: '203.0.113.9', upper: '203.0.113.9', priority: 20, expires: true, expires_time: '2026-09-14 11:30:00' }, RANGE_CREATE_DEFAULTS,
         { allow_smtp: false, allow_imap: false, allow_pop3: false })
   ],
   queue: [
      { id: 41, from: 'anna@example.com', recipients: 'far@away.example', next_try: '2026-09-14 10:00:00' },
      { id: 42, from: 'bob@example.com', recipients: 'gone@nowhere.example', next_try: '2026-09-14 10:05:00' }
   ],
   tlsa: { records: [{ record: '_25._tcp.mail.example.com. IN TLSA 3 1 1 0123abcd' }, { record: '_465._tcp.mail.example.com. IN TLSA 3 1 1 0123abcd' }] },
   rules: [
      { id: 1, name: 'Spam to junk', active: true, all_criteria: true,
         criteria: [{ field: 'subject', header: '', match: 'contains', value: '[SPAM]' }],
         actions: [{ type: 'move_to_folder', folder: 'Junk', value: 'Junk' }] }
   ],
   routes: [
      { id: 5, domain_name: 'partner.example', description: 'The partner', target_smtp_host: 'smtp.partner.example', target_smtp_port: 587,
         number_of_tries: 3, minutes_between_try: 10, relayer_requires_authentication: true, relayer_auth_username: 'relay',
         treat_security_as_local_domain: false, treat_recipient_as_local_domain: false, treat_sender_as_local_domain: false,
         all_addresses: true, connection_security: 'starttls_required', addresses: [] }
   ],
   certificates: [{ id: 2, name: 'mail.example.com', certificate_file: '/etc/ssl/mail.pem', private_key_file: '/etc/ssl/mail.key' }],
   ports: [
      { id: 1, protocol: 'smtp', address: '0.0.0.0', port: 25, connection_security: 'starttls_optional', certificate_id: 2, client_certificate_policy: 'off', client_certificate_ca_file: '' },
      { id: 2, protocol: 'imap', address: '0.0.0.0', port: 143, connection_security: 'none', certificate_id: 0, client_certificate_policy: 'off', client_certificate_ca_file: '' }
   ],
   logs: [
      { name: 'hmailserver_2026-09-14.log', size: 2048, created: '2026-09-14 00:00:01' },
      { name: 'ERROR_hmailserver_2026-09-14.log', size: 10, created: '2026-09-14 00:00:02' }
   ],
   logLines: ['"SMTPD" 1 "2026-09-14 09:00:00.000" "127.0.0.1" "SENT: 220 mail.example.com"', '"SMTPD" 1 "2026-09-14 09:00:01.000" "127.0.0.1" "RECEIVED: QUIT"'],
   // The reports. The index says what each source is and how far back it can
   // see; the sections are the tables. One of them is deliberately switched
   // off (the trace), because the page has to SAY so rather than draw an empty
   // table that reads as "nothing happened".
   reportIndex: {
      today: '2026-09-15',
      sections: [
         { name: 'traffic', scope: 'domain', source: 'hm_messagetrace', csv: true, summary: 'Messages in and out.' },
         { name: 'storage', scope: 'server', source: 'hm_metricsamples', csv: true, summary: 'The store per day.' }
      ],
      sources: {
         message_trace: { table: 'hm_messagetrace', enabled: true, retention_days: 30, oldest: '2026-09-01 08:00:00' },
         metric_history: { table: 'hm_metricsamples', enabled: true, retention_days: 7, oldest: '2026-09-08 00:00:00' },
         message_store: { table: 'hm_messages', enabled: true, retention_days: 0, oldest: '' }
      },
      not_answerable: ['Spam and virus counts per domain.', 'Storage growth per domain.']
   },
   reportOmitted: [],
   reportSections: {
      traffic: {
         section: 'traffic', source: 'hm_messagetrace', enabled: true, truncated: false, note: 'Counted from the message trace.',
         columns: ['day', 'domain', 'incoming', 'outgoing', 'failed_incoming', 'failed_outgoing'],
         rows: [
            { day: '2026-09-14', domain: 'example.com', incoming: 12, outgoing: 3, failed_incoming: 1, failed_outgoing: 0 },
            { day: '2026-09-14', domain: 'second.example', incoming: 4, outgoing: 0, failed_incoming: 0, failed_outgoing: 2 },
            { day: '2026-09-15', domain: 'example.com', incoming: 9, outgoing: 5, failed_incoming: 0, failed_outgoing: 0 }
         ],
         totals: { incoming: 25, outgoing: 8, failed_incoming: 1, failed_outgoing: 2 }
      },
      failures: {
         section: 'failures', source: 'hm_messagetrace', enabled: true, truncated: false, note: 'The reason is the SMTP status code.',
         columns: ['domain', 'status', 'reason', 'incoming', 'outgoing', 'total'],
         rows: [{ domain: 'example.com', status: 550, reason: 'the mailbox was unavailable, or the message was refused', incoming: 1, outgoing: 0, total: 1 }]
      },
      senders: {
         section: 'senders', source: 'hm_messagetrace', enabled: true, truncated: false, note: 'Local senders only.',
         columns: ['sender', 'domain', 'messages', 'failed'],
         rows: [{ sender: 'anna@example.com', domain: 'example.com', messages: 8, failed: 0 },
            { sender: 'bob@example.com', domain: 'example.com', messages: 3, failed: 1 }]
      },
      recipients: {
         section: 'recipients', source: 'hm_messagetrace', enabled: true, truncated: false, note: 'Local recipients only.',
         columns: ['recipient', 'domain', 'messages', 'failed'],
         rows: [{ recipient: 'anna@example.com', domain: 'example.com', messages: 21, failed: 1 }]
      },
      mailboxes: {
         section: 'mailboxes', source: 'hm_messages', enabled: true, truncated: false, note: 'A size now, not a history.',
         columns: ['address', 'domain', 'messages', 'bytes', 'megabytes'],
         rows: [{ address: 'anna@example.com', domain: 'example.com', messages: 402, bytes: 41943040, megabytes: 40 }],
         domains: [{ domain: 'example.com', mailboxes: 2, messages: 402, bytes: 41943040, megabytes: 40 }]
      },
      volume: {
         section: 'volume', source: 'hm_metricsamples', enabled: false, truncated: false,
         note: 'The metric history is switched off, so nothing is being recorded.',
         columns: ['day', 'processed', 'delivered', 'deferred', 'bounced', 'spam', 'viruses'],
         rows: []
      },
      storage: {
         section: 'storage', source: 'hm_metricsamples', enabled: true, truncated: false, note: 'Sampled hourly.',
         columns: ['day', 'bytes', 'megabytes', 'messages'],
         rows: [{ day: '2026-09-14', bytes: 41943040, megabytes: 40, messages: 400 },
            { day: '2026-09-15', bytes: 44040192, megabytes: 42, messages: 402 }],
         growth_bytes: 2097152
      }
   }
};

function json(status, payload, headers) {
   return { status, body: payload === undefined ? '' : JSON.stringify(payload), headers: headers || {} };
}
function clone(x) { return JSON.parse(JSON.stringify(x)); }
function segment(path, at) { return decodeURIComponent(path.split('?')[0].split('/')[at]); }

function answer(method, path, headers, raw) {
   const parsed = raw ? JSON.parse(raw) : null;
   if (method === 'POST' && path === '/api/v1/session') {
      if (!headers.Authorization) { return json(401, { error: 'A credential is required.' }, { 'WWW-Authenticate': 'Basic realm="hMailServer"' }); }
      if (headers.Authorization !== 'Basic ' + Buffer.from('Administrator:a-real-password').toString('base64')) {
         return json(401, { error: 'The password is wrong.' }, { 'WWW-Authenticate': 'Basic realm="hMailServer"' });
      }
      if (otpRequired && headers['X-hMailServer-OTP'] !== '123456') { return json(401, { error: 'A one-time code is required.', second_factor: 'required' }); }
      signedIn = true;
      return json(201, { session: true });
   }
   if (method === 'DELETE' && path === '/api/v1/session') { signedIn = false; return json(200, { ended: true }); }
   if (path === '/') { restartProbes += 1; return restartProbes < 3 ? { status: 503, body: '', headers: {} } : { status: 200, body: '<!doctype html>', headers: {} }; }
   // A catalogue, unauthenticated as the page is: the file from the
   // repository, or 404 when there is no such language or none is installed.
   if (method === 'GET' && /^\/deck-lang\/[A-Za-z-]+\.json$/.test(path)) {
      const file = nodePath.join(CATALOGUES, path.slice(11, -5) + '.json');
      if (catalogueMissing || !fs.existsSync(file)) { return json(404, { error: 'no such language' }); }
      const data = JSON.parse(fs.readFileSync(file, 'utf8'));
      if (droppedKey) { delete data[droppedKey]; }
      return json(200, data);
   }
   if (!signedIn) { return json(401, { error: 'Not signed in.' }, { 'WWW-Authenticate': 'Basic realm="hMailServer"' }); }
   if (method !== 'GET' && headers['X-Requested-With'] !== 'hMailServer') { return json(403, { error: 'A write from a browser session must carry X-Requested-With.' }); }
   if (method !== 'GET' && nextRefusal) { const sentence = nextRefusal; nextRefusal = null; return json(400, { error: sentence }); }

   if (path === '/api/v1/status') { return json(200, state.status); }
   if (path === '/api/v1/openapi.json') { return json(200, spec); }

   if (path === '/api/v1/domains' && method === 'GET') { return json(200, state.domains); }
   if (path === '/api/v1/domains' && method === 'POST') {
      const name = String(parsed.name || '').trim();
      if (!name) { return json(400, { error: 'name is required' }); }
      if (state.domains.some((d) => d.name === name)) { return json(409, { error: 'domain already exists' }); }
      const record = domainRecord(name, parsed.active !== false, parsed.postmaster || '');
      state.domains.push(record);
      state.accounts[name] = [];
      state.domainAliases[name] = [];
      return json(201, record);
   }
   if (/^\/api\/v1\/domains\/[^/]+$/.test(path)) {
      const name = segment(path, 4);
      const at = state.domains.findIndex((d) => d.name === name);
      if (at < 0) { return json(404, { error: 'domain not found' }); }
      if (method === 'PUT') {
         if (!('active' in parsed) || parsed.active === null) { return json(400, { error: 'active is required' }); }
         for (const key of Object.keys(parsed)) {
            if (DOMAIN_KEYS.indexOf(key) < 0) { return json(400, { error: 'unknown field: ' + key }); }
         }
         const record = state.domains[at];
         Object.keys(parsed).forEach((key) => {
            if (key === 'relay_password') { secrets.relay_password = parsed[key]; return; }
            record[key] = parsed[key];
         });
         if (record.name !== name) {
            state.accounts[record.name] = state.accounts[name] || [];
            delete state.accounts[name];
            state.domainAliases[record.name] = state.domainAliases[name] || [];
            delete state.domainAliases[name];
         }
         return json(200, record);
      }
      if (method === 'DELETE') {
         state.domains.splice(at, 1);
         delete state.accounts[name];
         delete state.domainAliases[name];
         return json(200, { deleted: true });
      }
   }
   if (/^\/api\/v1\/domains\/[^/]+\/domain-aliases$/.test(path)) {
      const domain = segment(path, 4);
      if (!(domain in state.domainAliases)) { return json(404, { error: 'domain not found' }); }
      if (method === 'GET') { return json(200, state.domainAliases[domain]); }
      if (method === 'POST') {
         const alias = { id: nextId++, name: String(parsed.name || '').trim() };
         if (!alias.name) { return json(400, { error: 'name is required' }); }
         state.domainAliases[domain].push(alias);
         return json(201, alias);
      }
   }
   if (/^\/api\/v1\/domains\/[^/]+\/domain-aliases\/[^/]+$/.test(path) && method === 'DELETE') {
      const domain = segment(path, 4);
      const alias = segment(path, 6);
      if (!(domain in state.domainAliases) || !state.domainAliases[domain].some((a) => a.name === alias)) { return json(404, { error: 'domain alias not found' }); }
      state.domainAliases[domain] = state.domainAliases[domain].filter((a) => a.name !== alias);
      return json(200, { deleted: true });
   }
   if (/^\/api\/v1\/domains\/[^/]+\/accounts$/.test(path)) {
      const domain = segment(path, 4);
      if (!(domain in state.accounts)) { return json(404, { error: 'domain not found' }); }
      // The listing carries the two fields HandleListAccounts_ emits.
      if (method === 'GET') { return json(200, state.accounts[domain].map((a) => ({ address: a.address, active: a.active }))); }
      if (method === 'POST') {
         state.accounts[domain].push(accountRecord(parsed.address, true));
         return json(201, { address: parsed.address, active: true });
      }
   }
   if (/^\/api\/v1\/accounts\/[^/]+$/.test(path)) {
      const address = segment(path, 4);
      const domain = Object.keys(state.accounts).filter((d) => state.accounts[d].some((a) => a.address === address))[0];
      if (method === 'DELETE') {
         Object.keys(state.accounts).forEach((d) => { state.accounts[d] = state.accounts[d].filter((a) => a.address !== address); });
         return json(200, { deleted: true });
      }
      if (!domain) { return json(404, { error: 'account not found' }); }
      const record = state.accounts[domain].filter((a) => a.address === address)[0];
      if (method === 'GET') { return json(200, record); }
      if (method === 'PUT') {
         // As HandleUpdateAccount_ reads it: nothing to update refused, an
         // unknown field refused by name, the password kept aside.
         if (!Object.keys(parsed).length) { return json(400, { error: 'nothing to update' }); }
         for (const key of Object.keys(parsed)) {
            if (!(key in ACCOUNT_PROPS)) { return json(400, { error: 'unknown field: ' + key }); }
         }
         Object.keys(parsed).forEach((key) => {
            if (key === 'password') { secrets.account_password = parsed[key]; return; }
            record[key] = parsed[key];
         });
         return json(200, record);
      }
   }

   if (/^\/api\/v1\/domains\/[^/]+\/aliases$/.test(path)) {
      const domain = segment(path, 4);
      if (!(domain in state.aliases)) { return json(404, { error: 'domain not found' }); }
      if (method === 'GET') { return json(200, state.aliases[domain]); }
      if (method === 'POST') {
         for (const key of Object.keys(parsed)) { if (['name', 'value', 'active'].indexOf(key) < 0) { return json(400, { error: 'unknown field: ' + key }); } }
         if (!parsed.name || !parsed.value) { return json(400, { error: 'name and value are required' }); }
         if (state.aliases[domain].some((a) => a.name === parsed.name)) { return json(409, { error: 'an alias with that name exists' }); }
         const alias = { name: parsed.name, value: parsed.value, active: parsed.active !== false };
         state.aliases[domain].push(alias);
         return json(201, alias);
      }
   }
   if (/^\/api\/v1\/aliases\/[^/]+$/.test(path) && method === 'DELETE') {
      const name = segment(path, 4);
      const domain = Object.keys(state.aliases).filter((d) => state.aliases[d].some((a) => a.name === name))[0];
      if (!domain) { return json(404, { error: 'alias not found' }); }
      state.aliases[domain] = state.aliases[domain].filter((a) => a.name !== name);
      return json(200, { deleted: true });
   }
   if (/^\/api\/v1\/domains\/[^/]+\/lists$/.test(path)) {
      const domain = segment(path, 4);
      if (!(domain in state.lists)) { return json(404, { error: 'domain not found' }); }
      if (method === 'GET') { return json(200, state.lists[domain]); }
      if (method === 'POST') {
         for (const key of Object.keys(parsed)) { if (!(key in LIST_CREATE_PROPS)) { return json(400, { error: 'unknown field: ' + key }); } }
         if (!String(parsed.address || '').trim()) { return json(400, { error: 'address is required' }); }
         if ('mode' in parsed && LIST_PROPS.mode.enum.indexOf(parsed.mode) < 0) { return json(400, { error: 'mode must be public, membership, announcement or domain_members' }); }
         if (state.lists[domain].some((l) => l.address === parsed.address)) { return json(409, { error: 'a list with that address exists' }); }
         const list = Object.assign({ active: true, require_auth: false, mode: 'public', require_sender_address: '', moderator_address: '', bounce_address: '', members: [] }, parsed);
         state.lists[domain].push(list);
         return json(201, list);
      }
   }
   if (/^\/api\/v1\/lists\/[^/]+$/.test(path)) {
      const address = segment(path, 4);
      const domain = Object.keys(state.lists).filter((d) => state.lists[d].some((l) => l.address === address))[0];
      if (!domain) { return json(404, { error: 'list not found' }); }
      const at = state.lists[domain].findIndex((l) => l.address === address);
      if (method === 'PUT') {
         for (const key of Object.keys(parsed)) { if (!(key in LIST_PROPS)) { return json(400, { error: 'unknown field: ' + key }); } }
         Object.assign(state.lists[domain][at], parsed);
         return json(200, state.lists[domain][at]);
      }
      if (method === 'DELETE') { state.lists[domain].splice(at, 1); return json(200, { deleted: true }); }
   }

   if (/^\/api\/v1\/accounts\/[^/]+\/fetch-accounts(\/\d+(\/download)?)?$/.test(path)) {
      const address = segment(path, 4);
      if (!Object.keys(state.accounts).some((d) => state.accounts[d].some((a) => a.address === address))) { return json(404, { error: 'account not found' }); }
      const list = state.fetchAccounts[address] = state.fetchAccounts[address] || [];
      const parts = path.split('/');
      if (parts.length === 6) {
         if (method === 'GET') { return json(200, list); }
         if (method === 'POST') {
            const problem = fetchProblem(parsed, true);
            if (problem) { return json(400, { error: problem }); }
            const item = Object.assign(fetchRecord(nextId++, ''), parsed);
            if ('password' in parsed) { secrets.fetch_password = parsed.password; delete item.password; }
            list.push(item);
            return json(201, item);
         }
      }
      const id = Number(parts[6]);
      const at = list.findIndex((x) => x.id === id);
      if (at < 0) { return json(404, { error: 'fetch account not found' }); }
      if (parts.length === 8 && method === 'POST') {
         if (state.fetcherDown) { return json(503, { error: 'the external fetcher is not running' }); }
         list[at].locked = true;
         return json(202, { queued: true });
      }
      if (parts.length === 7 && method === 'GET') { return json(200, list[at]); }
      if (parts.length === 7 && method === 'PUT') {
         const problem = fetchProblem(parsed, false);
         if (problem) { return json(400, { error: problem }); }
         Object.assign(list[at], parsed);
         if ('password' in parsed) { secrets.fetch_password = parsed.password; delete list[at].password; }
         return json(200, list[at]);
      }
      if (parts.length === 7 && method === 'DELETE') { list.splice(at, 1); return json(200, { deleted: true }); }
   }

   if (path === '/api/v1/backup') {
      if (method === 'GET') { return json(200, state.backup); }
      if (method === 'POST') {
         if (state.backupRunning) { return json(409, { error: 'the backup did not start', status: 'A backup is already running.' }); }
         state.backupRunning = true;
         state.backup.log.push('2026-09-14 11:00:00 Backup started.');
         return json(202, { started: true });
      }
   }

   if (path === '/api/v1/ipranges' && method === 'GET') { return json(200, state.ranges); }
   if (path === '/api/v1/ipranges' && method === 'POST') {
      // As HandleCreateIpRange_ reads it: three required, the addresses
      // parsed, a flag left out taking its default, and only the id back.
      if (!parsed.name || !parsed.lower || !parsed.upper) { return json(400, { error: 'name, lower and upper are required' }); }
      if (!ipAddress(parsed.lower) || !ipAddress(parsed.upper)) { return json(400, { error: 'lower and upper must be IP addresses' }); }
      const expiry = expiryProblem(parsed, null);
      if (expiry) { return json(400, { error: expiry }); }
      const range = Object.assign({ id: nextId++, priority: 0, expires: false, expires_time: '' }, RANGE_CREATE_DEFAULTS, parsed);
      if (!range.expires) { range.expires_time = ''; }
      state.ranges.push(range);
      return json(201, { id: range.id });
   }
   if (/^\/api\/v1\/ipranges\/\d+$/.test(path)) {
      const id = Number(segment(path, 4));
      const at = state.ranges.findIndex((r) => r.id === id);
      if (method === 'PUT') {
         for (const key of Object.keys(parsed)) {
            if (!(key in RANGE_PROPS)) { return json(400, { error: 'unknown field: ' + key }); }
         }
         if (at < 0) { return json(404, { error: 'ip range not found' }); }
         if ('lower' in parsed && !ipAddress(parsed.lower) || 'upper' in parsed && !ipAddress(parsed.upper)) { return json(400, { error: 'lower and upper must be IP addresses' }); }
         const expiry = expiryProblem(parsed, state.ranges[at]);
         if (expiry) { return json(400, { error: expiry }); }
         const time = String(parsed.expires_time || '').trim();
         Object.assign(state.ranges[at], parsed);
         if (!state.ranges[at].expires) { state.ranges[at].expires_time = ''; }
         else if (!time) { state.ranges[at].expires_time = state.ranges[at].expires_time || ''; }
         else { state.ranges[at].expires_time = time.length === 10 ? time + ' 00:00:00' : time; }
         return json(200, state.ranges[at]);
      }
      if (at < 0) { return json(404, { error: 'ip range not found' }); }
      if (method === 'DELETE') { state.ranges.splice(at, 1); return json(200, { deleted: true }); }
   }

   // The seven small collections, each in the same shape: the body read
   // whole, an unknown field or a missing required one refused by name.
   for (const base of Object.keys(COLLECTION_SPECS)) {
      const c = COLLECTION_SPECS[base];
      const items = state.collections[base];
      const refused = () => {
         for (const key of Object.keys(parsed)) { if (!(key in c.props)) { return 'unknown field: ' + key; } }
         for (const key of Object.keys(parsed)) { if (c.props[key].type === 'integer' && typeof parsed[key] !== 'number') { return key + ' must be a whole number between -2000000000 and 2000000000'; } }
         return null;
      };
      if (path === base && method === 'GET') { return json(200, items); }
      if (path === base && method === 'POST') {
         const problem = refused() || c.required.filter((k) => !String(parsed[k] || '').trim()).map((k) => k + ' is required')[0];
         if (problem) { return json(400, { error: problem }); }
         const item = Object.assign({ id: nextId++ }, c.props.active ? { active: true } : {}, parsed);
         items.push(item);
         return json(201, item);
      }
      if (path.indexOf(base + '/') === 0) {
         const id = Number(path.slice(base.length + 1));
         const at = items.findIndex((x) => x.id === id);
         if (method === 'PUT') {
            const problem = refused();
            if (problem) { return json(400, { error: problem }); }
            if (at < 0) { return json(404, { error: c.noun + ' not found' }); }
            Object.assign(items[at], parsed);
            return json(200, items[at]);
         }
         if (method === 'DELETE') {
            if (at < 0) { return json(404, { error: c.noun + ' not found' }); }
            items.splice(at, 1);
            return json(200, { deleted: true });
         }
      }
   }

   if (path === '/api/v1/queue' && method === 'GET') { return json(200, { messages: state.queue }); }
   if (/^\/api\/v1\/queue\/\d+\/retry$/.test(path) && method === 'POST') { return json(200, { rescheduled: true }); }
   if (/^\/api\/v1\/queue\/\d+$/.test(path) && method === 'DELETE') {
      const id = Number(segment(path, 4));
      state.queue = state.queue.filter((m) => m.id !== id);
      return json(200, { deleted: true });
   }

   if (path === '/api/v1/tlsa') { return json(200, state.tlsa); }

   if (path === '/api/v1/settings/messages' && method === 'GET') { return json(200, state.messages); }
   if (/^\/api\/v1\/settings\/messages\/[^/]+$/.test(path) && method === 'PUT') {
      const name = segment(path, 5);
      const at = state.messages.findIndex((m) => m.name === name);
      if (at < 0) { return json(404, { error: 'server message not found' }); }
      if (typeof parsed.text !== 'string') { return json(400, { error: 'text must be a string' }); }
      state.messages[at].text = parsed.text;
      return json(200, state.messages[at]);
   }

   // The verbs beside three of the groups.
   if (method === 'POST' && path === '/api/v1/settings/scripting/reload') { state.scriptReloads += 1; return json(200, { reloaded: true }); }
   if (method === 'POST' && path === '/api/v1/settings/scripting/check') { return json(200, { result: state.scriptProblem }); }
   if (method === 'POST' && path === '/api/v1/settings/cache/clear') {
      SETTING_GROUPS['/api/v1/settings/cache'].values.domain_cache_size_kb = 0;
      SETTING_GROUPS['/api/v1/settings/cache'].values.domain_hit_rate = 0;
      return json(200, { cleared: true });
   }
   if (method === 'POST' && path === '/api/v1/settings/indexing/index') { SETTING_GROUPS['/api/v1/settings/indexing'].values.total_indexed_count = 45; return json(200, { started: true }); }
   if (method === 'POST' && path === '/api/v1/settings/indexing/clear') { SETTING_GROUPS['/api/v1/settings/indexing'].values.total_indexed_count = 0; return json(200, { cleared: true }); }

   if (path in SETTING_GROUPS) {
      const group = SETTING_GROUPS[path];
      if (method === 'GET') { return json(200, group.values); }
      if (method === 'PUT') {
         for (const key of Object.keys(parsed)) {
            if (!(key in group.props)) { return json(400, { error: 'unknown key: ' + key }); }
            if (group.props[key].readOnly) { return json(400, { error: key + ' is read-only' }); }
            if (group.props[key].writeOnly) { secrets[key] = parsed[key]; continue; }
            group.values[key] = parsed[key];
         }
         return json(200, group.values);
      }
   }

   if (path === '/api/v1/rules' && method === 'GET') { return json(200, state.rules); }
   if (path === '/api/v1/rules' && method === 'POST') {
      const rule = Object.assign({ id: nextId++ }, parsed);
      state.rules.push(rule);
      return json(201, rule);
   }
   if (/^\/api\/v1\/rules\/\d+$/.test(path)) {
      const id = Number(segment(path, 4));
      const at = state.rules.findIndex((r) => r.id === id);
      if (at < 0) { return json(404, { error: 'No global rule with that id' }); }
      if (method === 'PUT') { state.rules[at] = Object.assign({ id }, parsed); return json(200, state.rules[at]); }
      if (method === 'DELETE') { state.rules.splice(at, 1); return json(200, { deleted: true }); }
   }

   if (path === '/api/v1/routes' && method === 'GET') { return json(200, state.routes); }
   if (path === '/api/v1/routes' && method === 'POST') {
      const route = Object.assign({ id: nextId++, addresses: [] }, parsed);
      delete route.relayer_auth_password;
      state.routes.push(route);
      return json(201, route);
   }
   if (/^\/api\/v1\/routes\/\d+$/.test(path)) {
      const id = Number(segment(path, 4));
      const at = state.routes.findIndex((r) => r.id === id);
      if (at < 0) { return json(404, { error: 'Unknown id' }); }
      if (method === 'PUT') {
         const route = Object.assign({ id, addresses: [] }, parsed);
         delete route.relayer_auth_password;
         state.routes[at] = route;
         return json(200, route);
      }
      if (method === 'DELETE') { state.routes.splice(at, 1); return json(200, { deleted: true }); }
   }

   if (path === '/api/v1/certificates' && method === 'GET') { return json(200, state.certificates); }
   if (path === '/api/v1/certificates' && method === 'POST') {
      const cert = { id: nextId++, name: parsed.name, certificate_file: parsed.certificate_file, private_key_file: parsed.private_key_file };
      if (parsed.private_key_password !== undefined) { secrets.private_key_password = parsed.private_key_password; }
      state.certificates.push(cert);
      return json(201, cert);
   }
   if (/^\/api\/v1\/certificates\/\d+$/.test(path) && method === 'DELETE') {
      const id = Number(segment(path, 4));
      if (state.ports.some((p) => p.certificate_id === id)) { return json(409, { error: 'The certificate is bound by port ' + state.ports.filter((p) => p.certificate_id === id)[0].id + '. Delete or rebind the port first.' }); }
      state.certificates = state.certificates.filter((c) => c.id !== id);
      return json(200, { deleted: true });
   }

   if (path === '/api/v1/ports' && method === 'GET') { return json(200, state.ports); }
   if (path === '/api/v1/ports' && method === 'POST') {
      const port = Object.assign({ id: nextId++, address: '0.0.0.0', connection_security: 'none', certificate_id: 0, client_certificate_policy: 'off', client_certificate_ca_file: '' }, parsed);
      state.ports.push(port);
      return json(201, port);
   }
   if (/^\/api\/v1\/ports\/\d+$/.test(path)) {
      const id = Number(segment(path, 4));
      const at = state.ports.findIndex((p) => p.id === id);
      if (at < 0) { return json(404, { error: 'Unknown id' }); }
      if (method === 'PUT') { state.ports[at] = Object.assign({ id, address: '0.0.0.0', connection_security: 'none', certificate_id: 0, client_certificate_policy: 'off', client_certificate_ca_file: '' }, parsed); return json(200, state.ports[at]); }
      if (method === 'DELETE') { state.ports.splice(at, 1); return json(200, { deleted: true }); }
   }
   if (path === '/api/v1/server/reinitialize' && method === 'POST') { return json(202, { reinitializing: true }); }

   if (path === '/api/v1/logs' && method === 'GET') { return json(200, state.logs); }
   if (/^\/api\/v1\/logs\/[^/]+/.test(path) && method === 'GET') {
      const name = segment(path, 4);
      // A file that was in the listing a moment ago and has been rotated away
      // since: listed, and then not there to read.
      if (name === 'gone.log' || !state.logs.some((l) => l.name === name)) { return json(404, { error: 'No such log file' }); }
      const lines = Number((/lines=(\d+)/.exec(path) || [0, 200])[1]);
      return json(200, { lines: state.logLines.slice(-lines) });
   }

   // The reports. The recorded answers echo the window, the domain and the row
   // count out of the query string, so that what the page ASKED FOR is what the
   // assertions can read back: the failure this catches is a picker that draws
   // a date and sends yesterday's.
   if (path.startsWith('/api/v1/reports') && method === 'GET') {
      const query = path.indexOf('?') < 0 ? '' : path.slice(path.indexOf('?') + 1);
      const parameter = (name) => {
         const hit = query.split('&').filter((p) => p.split('=')[0] === name)[0];
         return hit ? decodeURIComponent(hit.split('=').slice(1).join('=')) : '';
      };
      const section = path.split('?')[0].slice('/api/v1/reports'.length).replace(/^\//, '');
      const from = parameter('from') || '2026-08-17';
      const to = parameter('to') || '2026-09-15';
      const domain = parameter('domain');
      const top = Number(parameter('top') || 10);

      if (!section) { return json(200, Object.assign({ from, to }, state.reportIndex)); }

      const built = (name) => {
         const made = clone(state.reportSections[name]);
         if (domain) { made.rows = made.rows.filter((r) => r.domain === undefined || r.domain === domain); }
         if (name === 'senders' || name === 'recipients' || name === 'mailboxes') { made.rows = made.rows.slice(0, top); }
         return made;
      };

      if (section === 'summary') {
         const sections = {};
         Object.keys(state.reportSections).forEach((name) => { sections[name] = built(name); });
         return json(200, { section: 'summary', from, to, domain, top, sections, omitted: state.reportOmitted });
      }

      if (!state.reportSections[section]) { return json(404, { error: 'no such report section' }); }
      if (parameter('format') === 'csv') {
         const made = built(section);
         const lines = [made.columns.join(',')].concat(made.rows.map((r) => made.columns.map((c) => r[c]).join(',')));
         return { status: 200, body: lines.join('\r\n') + '\r\n', headers: { 'Content-Type': 'text/csv; charset=utf-8' } };
      }
      return json(200, Object.assign({ from, to, domain, top }, built(section)));
   }

   return json(404, { error: 'No such route: ' + method + ' ' + path });
}

function fetchStub(path, options) {
   const method = (options && options.method) || 'GET';
   const headers = (options && options.headers) || {};
   requests.push({ method, path, headers, body: options && options.body, credentials: options && options.credentials });
   const reply = answer(method, path, headers, options && options.body);
   return Promise.resolve({
      status: reply.status,
      ok: reply.status >= 200 && reply.status < 300,
      headers: { get: (name) => (name in reply.headers ? reply.headers[name] : null) },
      text: () => Promise.resolve(reply.body),
      // A browser's Response has both, and the page uses both: text() where it
      // wants the empty body of a 204 as well, json() for a catalogue.
      json: () => new Promise((resolve, reject) => {
         try { resolve(JSON.parse(reply.body)); } catch (why) { reject(why); }
      })
   });
}

/* ------------------------------------------------------------ the assertions */

const failures = [];
let checks = 0;
function check(what, ok, detail) {
   checks += 1;
   if (!ok) { failures.push(what + (detail ? ' -- ' + detail : '')); }
}
function since(n) { return requests.slice(n); }
function called(from, method, pattern) {
   return since(from).filter((r) => r.method === method && (typeof pattern === 'string' ? r.path === pattern : pattern.test(r.path)));
}
function paths(from) { return JSON.stringify(since(from).map((r) => r.method + ' ' + r.path)); }
const flush = async () => { for (let i = 0; i < 60; i += 1) { await new Promise((r) => setImmediate(r)); } };

const $ = (s) => document.querySelector(s);
const $$ = (s) => document.querySelectorAll(s);
const content = () => $('#content');
function click(el) { if (!el) { throw new Error('nothing to click'); } el.dispatchEvent(makeEvent('click')); }
function act(name, attrs) {
   const all = content().querySelectorAll('button[data-act="' + name + '"]');
   const hit = all.filter((b) => !attrs || Object.keys(attrs).every((k) => b.attributes['data-' + k] === String(attrs[k])))[0];
   if (!hit) { throw new Error('the view has no button data-act="' + name + '"' + (attrs ? ' with ' + JSON.stringify(attrs) : '') + '; it has ' + JSON.stringify(all.map((b) => b.attributes))); }
   return hit;
}
async function goTo(view) {
   const button = $('#nav button[data-view="' + view + '"]');
   if (!button) { throw new Error('the sidebar has no view ' + view); }
   click(button);
   await flush();
}
function rows() { return content().querySelectorAll('tbody tr'); }
function setValue(id, value) { const el = document.getElementById(id); if (!el) { throw new Error('no control #' + id); } el.value = value; return el; }
function setChecked(id, on) { const el = document.getElementById(id); if (!el) { throw new Error('no control #' + id); } el.checked = on; return el; }
function lastBody(from, method, pattern) { const list = called(from, method, pattern); return list.length ? JSON.parse(list[list.length - 1].body) : null; }
function toastText() { return $('#toast').textContent; }

/* ------------------------------------------------------------------ the run */

// Node has globals of its own with these names (navigator, localStorage,
// fetch, performance), some of them getter-only, so each one is defined over
// rather than assigned: the script must see this file's world and nothing of
// node's.
const world = {
   document, localStorage, sessionStorage, performance,
   fetch: fetchStub,
   setTimeout: (fn, ms) => { const id = timerId++; timers.push({ id, fn, ms }); return id; },
   clearTimeout: (id) => { const at = timers.findIndex((t) => t.id === id); if (at >= 0) { timers.splice(at, 1); } },
   setInterval: (fn, ms) => { const id = timerId++; intervals.push({ id, fn, ms }); return id; },
   clearInterval: (id) => { const at = intervals.findIndex((t) => t.id === id); if (at >= 0) { intervals.splice(at, 1); } },
   requestAnimationFrame: (fn) => { fn(performance.now() + 5000); return 1; },
   confirm: (question) => { confirmations.push(question); return confirmAnswer; },
   navigator: { clipboard: { writeText: (text) => { clipboard = text; return Promise.resolve(); } } },
   console: { error: (...args) => consoleErrors.push(args), log: () => { }, warn: () => { } }
};
Object.keys(world).forEach((name) => {
   Object.defineProperty(globalThis, name, { value: world[name], writable: true, configurable: true });
});

async function signIn() {
   $('#user').value = 'Administrator';
   $('#pass').value = 'a-real-password';
   $('#loginForm').dispatchEvent(makeEvent('submit'));
   await flush();
}

async function main() {
   vm.runInThisContext(fs.readFileSync(scriptPath, 'utf8'), { filename: 'deck.js' });
   await flush();
   let before = 0;
   let put = null;
   let posted = null;

   // ---- a first visit: the sign-in card, and not one request
   check('a first visit shows the sign-in card', $('#gate').style.display !== 'none' && $('#app').style.display === 'none',
      'gate=' + $('#gate').style.display + ' app=' + $('#app').style.display);
   check('and asks the server nothing before there is a session', requests.length === 0, paths(0));
   check('the theme starts dark and is the body\'s attribute', document.body.getAttribute('data-theme') === 'dark');

   // ---- a wrong password is the server's sentence
   $('#user').value = 'Administrator';
   $('#pass').value = 'wrong';
   $('#loginForm').dispatchEvent(makeEvent('submit'));
   await flush();
   check('a refused sign-in shows the server\'s own sentence', $('#loginErr').textContent === 'The password is wrong.', $('#loginErr').textContent);
   check('and the app stays hidden', $('#app').style.display === 'none');

   // ---- a second factor, when the credential has one
   otpRequired = true;
   $('#pass').value = 'a-real-password';
   $('#loginForm').dispatchEvent(makeEvent('submit'));
   await flush();
   check('a credential with a second factor makes the code field appear', $('#otp').hidden === false && $('#loginErr').textContent.indexOf('one-time code') >= 0,
      'otp.hidden=' + $('#otp').hidden + ' err=' + $('#loginErr').textContent);
   check('the password is still there for the second attempt', $('#pass').value === 'a-real-password');
   $('#otp').value = '123456';
   $('#loginForm').dispatchEvent(makeEvent('submit'));
   await flush();
   check('the code goes in its own header', requests.filter((r) => r.headers['X-hMailServer-OTP'] === '123456').length === 1);
   otpRequired = false;

   // ---- signed in
   const withAuth = requests.filter((r) => r.headers && r.headers.Authorization);
   check('the password went only to the session route', withAuth.every((r) => r.method === 'POST' && r.path === '/api/v1/session'),
      JSON.stringify(withAuth.map((r) => r.method + ' ' + r.path)));
   check('the password field is emptied once a session exists', $('#pass').value === '' && $('#otp').value === '' && $('#otp').hidden === true);
   check('nothing secret is stored in the browser',
      [...localStorage.store.values(), ...sessionStorage.store.values()].every((v) => v.indexOf('a-real-password') < 0 && v.indexOf('123456') < 0) &&
      [...localStorage.store.keys(), ...sessionStorage.store.keys()].every((k) => !/pass|token|secret|auth/i.test(k)),
      JSON.stringify([...sessionStorage.store.entries()]));
   check('a mark says this tab has a session, so a reload asks the server instead of showing the card', sessionStorage.getItem('hmsSession') === '1');
   check('the app is shown and the card is not', $('#app').style.display === 'flex' && $('#gate').style.display === 'none');
   check('the version comes from the status', $('#ver').textContent === '6.3.3', $('#ver').textContent);
   check('every write carries the header the server demands of a browser session',
      requests.filter((r) => r.method !== 'GET' && r.method !== 'HEAD').every((r) => r.headers['X-Requested-With'] === 'hMailServer'));
   check('every request is same-origin, with the cookie and nothing else',
      requests.filter((r) => r.path !== '/').every((r) => r.credentials === 'same-origin'));

   // ---- the dashboard
   const locale = (n) => n.toLocaleString();
   check('the dashboard shows the messages processed', $('#kpiProcessed').textContent === locale(1234), $('#kpiProcessed').textContent);
   check('the spam and virus counts', $('#kpiSpam').textContent === locale(56) && $('#kpiVirus').textContent === locale(7));
   check('the state as a badge', $('#kpiState').textContent === 'Running' && $('#kpiState').querySelector('.badge.good') !== null, $('#kpiState').innerHTML);
   check('the live sessions', $('#sesSmtp').textContent === '3' && $('#sesImap').textContent === '12' && $('#sesPop3').textContent === '1');
   check('the dashboard polls on an interval', intervals.length === 1 && intervals[0].ms === 3000, intervals.length + ' intervals');
   const tile = $('#kpiProcessed');
   const beforePoll = requests.length;
   state.status.processedMessages = 1300;
   state.status.state = 4;
   fireIntervals();
   await flush();
   check('the poll reads the status again', called(beforePoll, 'GET', '/api/v1/status').length === 1, paths(beforePoll));
   check('and updates the tiles in place rather than redrawing the view', $('#kpiProcessed') === tile && tile.textContent === locale(1300), tile.textContent);
   check('a state other than running is a warning', $('#kpiState').querySelector('.badge.warn') !== null && $('#kpiState').textContent === 'Stopping', $('#kpiState').textContent);
   state.status.state = 3;

   // ---- domains
   before = requests.length;
   await goTo('domains');
   check('the domains view reads the domains', called(before, 'GET', '/api/v1/domains').length === 1, paths(before));
   check('the title follows the view', $('#viewTitle').textContent === 'Domains');
   check('the sidebar marks the view', $('#nav button[data-view="domains"]').classList.contains('on') && !$('#nav button[data-view="dash"]').classList.contains('on'));
   check('one row per domain', rows().length === 3, rows().length + ' rows');
   check('an inactive domain says so', rows()[1].textContent.indexOf('Disabled') >= 0 && rows()[0].textContent.indexOf('Active') >= 0);
   check('a hostile domain name is text, never markup', content().querySelectorAll('img').length === 0 && rows()[1].textContent.indexOf(HOSTILE) >= 0,
      rows()[1].innerHTML);
   check('the poll is not running away from the dashboard', (() => { const n = requests.length; fireIntervals(); return requests.length === n; })());

   before = requests.length;
   click(act('accounts', { domain: HOSTILE }));
   await flush();
   check('the accounts button carries the name as data, and the request encodes it',
      called(before, 'GET', '/api/v1/domains/' + encodeURIComponent(HOSTILE) + '/accounts').length === 1, paths(before));
   check('the accounts are listed', rows().length === 1 && rows()[0].textContent.indexOf('x@' + HOSTILE) >= 0, rows().length + ' rows');
   click(act('domains'));
   await flush();
   click(act('accounts', { domain: 'example.com' }));
   await flush();
   check('two accounts in example.com', rows().length === 2 && rows()[1].textContent.indexOf('Disabled') >= 0);

   before = requests.length;
   click(act('add', { domain: 'example.com' }));
   await flush();
   check('an empty form is refused by the page, not the server', called(before, 'POST', /accounts/).length === 0 && $('#err_account').textContent.length > 0, $('#err_account').textContent);
   setValue('newAddr', 'carla@example.com');
   setValue('newPass', 'pw-for-carla');
   before = requests.length;
   click(act('add', { domain: 'example.com' }));
   await flush();
   const created = lastBody(before, 'POST', '/api/v1/domains/example.com/accounts');
   check('creating an account posts the address and the password', !!created && created.address === 'carla@example.com' && created.password === 'pw-for-carla' && Object.keys(created).length === 2,
      JSON.stringify(created));
   check('and the listing is read again', called(before, 'GET', '/api/v1/domains/example.com/accounts').length === 1, paths(before));
   check('so the new account is in it', rows().length === 3 && rows()[2].textContent.indexOf('carla@example.com') >= 0);
   check('and the toast says so', toastText().indexOf('carla@example.com') >= 0, toastText());

   nextRefusal = 'The address is already taken by an alias.';
   setValue('newAddr', 'dupe@example.com');
   setValue('newPass', 'x');
   click(act('add', { domain: 'example.com' }));
   await flush();
   check('a refusal is shown beside the form in the server\'s words', $('#err_account').textContent === 'The address is already taken by an alias.', $('#err_account').textContent);
   check('and went to the console with the whole exchange', consoleErrors.length > 0 && JSON.stringify(consoleErrors[consoleErrors.length - 1]).indexOf('400') >= 0);

   confirmAnswer = false;
   before = requests.length;
   click(act('del', { address: 'bob@example.com' }));
   await flush();
   check('deleting asks first, and no means nothing is sent', confirmations[confirmations.length - 1].indexOf('bob@example.com') >= 0 && called(before, 'DELETE', /accounts/).length === 0,
      paths(before));
   confirmAnswer = true;
   click(act('del', { address: 'bob@example.com' }));
   await flush();
   check('yes deletes by address', called(before, 'DELETE', '/api/v1/accounts/bob%40example.com').length === 1, paths(before));
   check('and the listing is read again without the account', rows().length === 2 && rows().every((r) => r.textContent.indexOf('bob@') < 0));

   // ---- the account editor: every key of the update, as the desktop dialog groups them
   before = requests.length;
   click(act('accountedit', { address: 'anna@example.com' }));
   await flush();
   check('Edit reads the account whole', called(before, 'GET', '/api/v1/accounts/anna%40example.com').length === 1 && content().querySelector('h2').textContent.indexOf('anna@example.com') >= 0, paths(before));
   const accountHeadings = content().querySelectorAll('h2').map((h) => h.textContent.trim());
   check('the editor is grouped as the desktop dialog is', ['General', 'Forwarding', 'Auto-reply', 'Spam', 'Signature', 'Sieve', 'Directory'].every((g) => accountHeadings.indexOf(g) >= 0) && accountHeadings.indexOf('Other') < 0,
      JSON.stringify(accountHeadings));
   const drawnAccount = content().querySelectorAll('input, select, textarea').map((el) => el.id).filter((id) => id.indexOf('acct_') === 0).map((id) => id.slice(5)).sort();
   check('every key of the update has a control, and nothing else', JSON.stringify(drawnAccount) === JSON.stringify(Object.keys(ACCOUNT_PROPS).sort()), JSON.stringify(drawnAccount));
   check('the values are the account\'s own: the name, the level as a select of three words, the script as a text area, the password box empty',
      document.getElementById('acct_first_name').value === 'Anna' && document.getElementById('acct_admin_level').tagName === 'SELECT' &&
      document.getElementById('acct_admin_level').querySelectorAll('option').map((o) => o.attributes.value).join(',') === 'user,domain,server' && document.getElementById('acct_admin_level').value === 'domain' &&
      document.getElementById('acct_sieve_script').tagName === 'TEXTAREA' && document.getElementById('acct_sieve_script').value.indexOf('fileinto') >= 0 &&
      document.getElementById('acct_password').type === 'password' && document.getElementById('acct_password').value === '' && document.getElementById('acct_spam_mark_threshold').value === '-1' &&
      document.getElementById('acct_forward_abort_spam_flagged').closest('.fr').textContent.indexOf('Do not forward messages flagged as spam') >= 0);
   check('the route\'s own description is the note', content().textContent.indexOf('a field the body does not name is left as it is') >= 0);
   before = requests.length;
   click(act('accountsave'));
   await flush();
   check('saving with nothing changed sends nothing', called(before, 'PUT', /accounts/).length === 0 && toastText() === 'Nothing changed', toastText());
   setChecked('acct_forward_enabled', true);
   setValue('acct_forward_address', 'anna@elsewhere.example');
   setValue('acct_message_retention_days', '90');
   before = requests.length;
   click(act('accountsave'));
   await flush();
   put = lastBody(before, 'PUT', '/api/v1/accounts/anna%40example.com');
   check('saving sends only the keys that changed, numbers as numbers', JSON.stringify(put) === '{"forward_enabled":true,"forward_address":"anna@elsewhere.example","message_retention_days":90}', JSON.stringify(put));
   check('then reads the account again and stays in the editor with what the server holds', called(before, 'GET', '/api/v1/accounts/anna%40example.com').length === 1 &&
      document.getElementById('acct_forward_address').value === 'anna@elsewhere.example' && document.getElementById('acct_message_retention_days').value === '90' && toastText() === 'Account saved', toastText());
   setValue('acct_password', 'a-new-one');
   before = requests.length;
   click(act('accountsave'));
   await flush();
   put = lastBody(before, 'PUT', '/api/v1/accounts/anna%40example.com');
   check('a filled password goes alone, and its box is empty after the re-read', JSON.stringify(put) === '{"password":"a-new-one"}' && secrets.account_password === 'a-new-one' && document.getElementById('acct_password').value === '', JSON.stringify(put));
   setValue('acct_max_size_mb', 'lots');
   before = requests.length;
   click(act('accountsave'));
   await flush();
   check('a size that is not a number is refused by the page', called(before, 'PUT', /accounts/).length === 0 && document.getElementById('err_accountedit').textContent.indexOf('whole number') >= 0);
   setValue('acct_max_size_mb', '500');
   nextRefusal = 'The password does not meet the policy.';
   click(act('accountsave'));
   await flush();
   check('a refusal keeps the editor open with the server\'s sentence', document.getElementById('err_accountedit').textContent === 'The password does not meet the policy.' && document.getElementById('acct_max_size_mb').value === '500');
   before = requests.length;
   click(act('accounts', { domain: 'example.com' }));
   await flush();
   check('the way back re-reads the accounts of the domain', called(before, 'GET', '/api/v1/domains/example.com/accounts').length === 1 && rows().length === 2 && act('accountedit', { address: 'carla@example.com' }) !== null);

   // ---- the distribution lists of a domain
   click(act('domains'));
   await flush();
   before = requests.length;
   click(act('lists', { domain: 'example.com' }));
   await flush();
   check('Lists reads the domain\'s lists', called(before, 'GET', '/api/v1/domains/example.com/lists').length === 1, paths(before));
   check('a list row shows its address, state, who may send and its members', rows().length === 1 && rows()[0].textContent.indexOf('all@example.com') >= 0 && rows()[0].textContent.indexOf('Active') >= 0 &&
      rows()[0].textContent.indexOf('Public') >= 0 && rows()[0].textContent.indexOf('2') >= 0 && rows()[0].textContent.indexOf('anna@example.com') >= 0, rows().length ? rows()[0].textContent : 'no rows');
   const drawnListNew = content().querySelectorAll('input, select, textarea').map((el) => el.id).filter((id) => id.indexOf('listnew_') === 0).map((id) => id.slice(8)).sort();
   check('the new-list form has every key of the create and nothing else, the members as a list', JSON.stringify(drawnListNew) === JSON.stringify(Object.keys(LIST_CREATE_PROPS).sort()) &&
      document.getElementById('listnew_members').tagName === 'TEXTAREA', JSON.stringify(drawnListNew));
   check('and starts at the defaults the description states, the mode\'s word picked out of its sentence', document.getElementById('listnew_active').checked === true && document.getElementById('listnew_require_auth').checked === false &&
      document.getElementById('listnew_mode').value === 'public' && document.getElementById('listnew_mode').querySelectorAll('option').map((o) => o.attributes.value).join(',') === 'public,membership,announcement,domain_members' &&
      document.getElementById('listnew_address').closest('.fr').textContent.indexOf('required') >= 0,
      'mode=' + document.getElementById('listnew_mode').value);
   before = requests.length;
   click(act('listnew', { domain: 'example.com' }));
   await flush();
   check('an empty address is refused by the page', called(before, 'POST', /lists/).length === 0 && document.getElementById('err_listnew').textContent.indexOf('required') >= 0);
   setValue('listnew_address', 'team@example.com');
   setValue('listnew_members', 'anna@example.com\n\ncarla@example.com\n');
   setValue('listnew_mode', 'membership');
   setChecked('listnew_require_auth', true);
   before = requests.length;
   click(act('listnew', { domain: 'example.com' }));
   await flush();
   posted = lastBody(before, 'POST', '/api/v1/domains/example.com/lists');
   check('creating a list posts the form with the members as an array and the defaults', !!posted && posted.address === 'team@example.com' && JSON.stringify(posted.members) === '["anna@example.com","carla@example.com"]' &&
      posted.mode === 'membership' && posted.require_auth === true && posted.active === true && posted.moderator_address === '', JSON.stringify(posted));
   check('and the re-read list has it', called(before, 'GET', '/api/v1/domains/example.com/lists').length === 1 && rows().length === 2 && rows()[1].textContent.indexOf('team@example.com') >= 0 && toastText() === 'List created: team@example.com');
   nextRefusal = 'The domain has reached its list limit.';
   setValue('listnew_address', 'more@example.com');
   click(act('listnew', { domain: 'example.com' }));
   await flush();
   check('a refused create is the server\'s sentence beside the form', document.getElementById('err_listnew').textContent === 'The domain has reached its list limit.');
   click(act('listedit', { address: 'all@example.com' }));
   await flush();
   const drawnList = content().querySelectorAll('input, select, textarea').map((el) => el.id).filter((id) => id.indexOf('list_') === 0).map((id) => id.slice(5)).sort();
   check('editing draws the update\'s keys with the list\'s values and the members without a control', JSON.stringify(drawnList) === JSON.stringify(Object.keys(LIST_PROPS).sort()) &&
      document.getElementById('list_mode').value === 'public' && document.getElementById('list_active').checked === true && content().textContent.indexOf('bob@example.com') >= 0 &&
      content().textContent.indexOf('the update does not change them') >= 0, JSON.stringify(drawnList));
   setValue('list_mode', 'announcement');
   setValue('list_require_sender_address', 'news@example.com');
   before = requests.length;
   click(act('listsave'));
   await flush();
   put = lastBody(before, 'PUT', '/api/v1/lists/all%40example.com');
   check('saving sends only what changed, by address', JSON.stringify(put) === '{"mode":"announcement","require_sender_address":"news@example.com"}', JSON.stringify(put));
   check('then reads the lists again and stays in the editor on the server\'s values', called(before, 'GET', '/api/v1/domains/example.com/lists').length === 1 && document.getElementById('list_mode').value === 'announcement' && toastText() === 'List saved');
   nextRefusal = 'An announcement list needs a sender.';
   setValue('list_require_sender_address', '');
   click(act('listsave'));
   await flush();
   check('a refused save keeps the editor open with the server\'s sentence', document.getElementById('err_listedit').textContent === 'An announcement list needs a sender.' && document.getElementById('list_mode') !== null);
   click(act('lists', { domain: 'example.com' }));
   await flush();
   confirmAnswer = false;
   before = requests.length;
   click(act('listdel', { address: 'team@example.com' }));
   await flush();
   check('deleting a list asks, naming it', called(before, 'DELETE', /lists/).length === 0 && confirmations[confirmations.length - 1].indexOf('team@example.com') >= 0);
   confirmAnswer = true;
   click(act('listdel', { address: 'team@example.com' }));
   await flush();
   check('yes deletes by address and the re-read list is without it', called(before, 'DELETE', '/api/v1/lists/team%40example.com').length === 1 && rows().length === 1 && rows()[0].textContent.indexOf('all@example.com') >= 0);

   // ---- the aliases of a domain
   click(act('domains'));
   await flush();
   before = requests.length;
   click(act('aliases', { domain: 'example.com' }));
   await flush();
   check('Aliases reads the domain\'s aliases', called(before, 'GET', '/api/v1/domains/example.com/aliases').length === 1 && rows().length === 1 &&
      rows()[0].textContent.indexOf('info@example.com') >= 0 && rows()[0].textContent.indexOf('anna@example.com') >= 0 && rows()[0].textContent.indexOf('Active') >= 0, paths(before));
   check('the form has the create\'s three keys, the two required ones marked, active on by the description', document.getElementById('aliasnew_name') !== null && document.getElementById('aliasnew_value') !== null &&
      document.getElementById('aliasnew_active').checked === true && document.getElementById('aliasnew_name').closest('.fr').textContent.indexOf('required') >= 0 &&
      document.getElementById('aliasnew_active').closest('.fr').textContent.indexOf('required') < 0 && content().textContent.indexOf('delete it and make it again') >= 0);
   before = requests.length;
   click(act('aliasnew', { domain: 'example.com' }));
   await flush();
   check('an empty form is refused by the page', called(before, 'POST', /aliases/).length === 0 && document.getElementById('err_aliasnew').textContent.indexOf('required') >= 0);
   setValue('aliasnew_name', 'sales@example.com');
   setValue('aliasnew_value', 'carla@example.com');
   setChecked('aliasnew_active', false);
   before = requests.length;
   document.getElementById('aliasnew_value').dispatchEvent(makeEvent('keydown', { key: 'Enter' }));
   await flush();
   posted = lastBody(before, 'POST', '/api/v1/domains/example.com/aliases');
   check('Enter in the form creates the alias with the three keys', JSON.stringify(posted) === '{"name":"sales@example.com","value":"carla@example.com","active":false}', JSON.stringify(posted));
   check('and the re-read list has it, disabled', called(before, 'GET', '/api/v1/domains/example.com/aliases').length === 1 && rows().length === 2 && rows()[1].textContent.indexOf('sales@example.com') >= 0 &&
      rows()[1].textContent.indexOf('Disabled') >= 0 && toastText() === 'Alias created: sales@example.com');
   nextRefusal = 'The name is already an account.';
   setValue('aliasnew_name', 'anna@example.com');
   setValue('aliasnew_value', 'carla@example.com');
   click(act('aliasnew', { domain: 'example.com' }));
   await flush();
   check('a refused alias is the server\'s sentence beside the form', document.getElementById('err_aliasnew').textContent === 'The name is already an account.');
   confirmAnswer = false;
   before = requests.length;
   click(act('aliasdel', { name: 'sales@example.com' }));
   await flush();
   check('deleting an alias asks, naming it', called(before, 'DELETE', /aliases/).length === 0 && confirmations[confirmations.length - 1].indexOf('sales@example.com') >= 0);
   confirmAnswer = true;
   click(act('aliasdel', { name: 'sales@example.com' }));
   await flush();
   check('yes deletes by address and the re-read list is without it', called(before, 'DELETE', '/api/v1/aliases/sales%40example.com').length === 1 && rows().length === 1);
   nextRefusal = 'The alias is named by a rule.';
   click(act('aliasdel', { name: 'info@example.com' }));
   await flush();
   check('a refused delete is shown on its row', document.getElementById('err_alias_info@example.com').textContent === 'The alias is named by a rule.' && rows().length === 1);

   // ---- the domain editor
   click(act('domains'));
   await flush();
   check('the domain list shows the postmaster and offers Edit and Delete', rows()[0].textContent.indexOf('postmaster@example.com') >= 0 &&
      act('domainedit', { name: 'example.com' }) !== null && act('domaindel', { name: HOSTILE }) !== null);
   before = requests.length;
   click(act('domainedit', { name: 'example.com' }));
   await flush();
   check('Edit reads the domain\'s other names', called(before, 'GET', '/api/v1/domains/example.com/domain-aliases').length === 1, paths(before));
   const headings = content().querySelectorAll('h2').map((h) => h.textContent.trim());
   check('the editor is grouped as the desktop dialog is', ['General', 'Names', 'Limits', 'Signature', 'Relay', 'Out of office', 'DKIM'].every((g) => headings.indexOf(g) >= 0), JSON.stringify(headings));
   check('the route\'s own description is the note', content().textContent.indexOf('A field left out keeps its value') >= 0);
   check('General: active, the name and the postmaster', document.getElementById('dom_active').checked === true && document.getElementById('dom_name').value === 'example.com' &&
      document.getElementById('dom_postmaster').value === 'postmaster@example.com');
   check('Limits: numbers as number boxes, switches as checkboxes', document.getElementById('dom_max_size_mb').type === 'number' && document.getElementById('dom_max_size_mb').value === '0' &&
      document.getElementById('dom_max_accounts_enabled').checked === false && document.getElementById('dom_plus_addressing_character').value === '+');
   check('Signature: the method\'s three words and the texts as text areas',
      document.getElementById('dom_signature_method').querySelectorAll('option').map((o) => o.attributes.value).join(',') === 'set_if_not_specified,overwrite,append' &&
      document.getElementById('dom_signature_plain_text').tagName === 'TEXTAREA' && document.getElementById('dom_signature_html').tagName === 'TEXTAREA');
   check('Relay: the four securities and a write-only password box, empty',
      document.getElementById('dom_relay_connection_security').querySelectorAll('option').map((o) => o.attributes.value).join(',') === 'none,starttls_optional,starttls_required,tls' &&
      document.getElementById('dom_relay_password').type === 'password' && document.getElementById('dom_relay_password').value === '' &&
      document.getElementById('dom_relay_password').closest('.fr').textContent.indexOf('write-only') >= 0);
   check('Out of office and DKIM', document.getElementById('dom_vacation_message').tagName === 'TEXTAREA' &&
      document.getElementById('dom_dkim_signing_algorithm').querySelectorAll('option').map((o) => o.attributes.value).join(',') === 'sha1,sha256' && document.getElementById('dom_dkim_signing_algorithm').value === 'sha256');
   const drawn = content().querySelectorAll('input, select, textarea').map((el) => el.id).filter((id) => id.indexOf('dom_') === 0).map((id) => id.slice(4)).sort();
   check('every key the route takes has a control, and no key it does not', JSON.stringify(drawn) === JSON.stringify(DOMAIN_KEYS.slice().sort()), JSON.stringify(drawn));
   check('the other names are listed with a Remove each', content().textContent.indexOf('example.net') >= 0 && act('daliasdel', { name: 'example.net' }) !== null);

   before = requests.length;
   click(act('domainsave'));
   await flush();
   check('saving a domain with nothing changed sends nothing', called(before, 'PUT', /domains/).length === 0 && toastText() === 'Nothing changed', toastText());
   setValue('dom_max_size_mb', '500');
   setChecked('dom_use_greylisting', true);
   before = requests.length;
   click(act('domainsave'));
   await flush();
   put = lastBody(before, 'PUT', '/api/v1/domains/example.com');
   check('saving sends active, which the route requires, and only what changed', JSON.stringify(put) === '{"active":true,"max_size_mb":500,"use_greylisting":true}', JSON.stringify(put));
   check('then reads the domains again and stays in the editor with what the server holds', called(before, 'GET', '/api/v1/domains').length === 1 &&
      document.getElementById('dom_max_size_mb').value === '500' && document.getElementById('dom_use_greylisting').checked === true && toastText() === 'Domain saved', toastText());
   setValue('dom_relay_password', 'relay-secret');
   before = requests.length;
   click(act('domainsave'));
   await flush();
   put = lastBody(before, 'PUT', '/api/v1/domains/example.com');
   check('a filled relay password goes with active and nothing else', JSON.stringify(put) === '{"active":true,"relay_password":"relay-secret"}', JSON.stringify(put));
   check('and its box is empty again after the re-read', document.getElementById('dom_relay_password').value === '');
   setValue('dom_max_accounts', 'many');
   before = requests.length;
   click(act('domainsave'));
   await flush();
   check('a limit that is not a number is refused by the page', called(before, 'PUT', /domains/).length === 0 && document.getElementById('err_domainedit').textContent.indexOf('whole number') >= 0,
      document.getElementById('err_domainedit').textContent);
   setValue('dom_max_accounts', '10');
   nextRefusal = 'The domain name is not valid.';
   click(act('domainsave'));
   await flush();
   check('a refusal keeps the editor open with the server\'s sentence', document.getElementById('err_domainedit').textContent === 'The domain name is not valid.' && document.getElementById('dom_max_accounts').value === '10');
   setValue('dom_name', 'renamed.example');
   before = requests.length;
   click(act('domainsave'));
   await flush();
   put = lastBody(before, 'PUT', '/api/v1/domains/example.com');
   check('a new name is PUT to the old name, with the other change', !!put && put.name === 'renamed.example' && put.max_accounts === 10 && Object.keys(put).length === 3, JSON.stringify(put));
   check('and the editor is redrawn under the new name', content().querySelector('h2').textContent.indexOf('renamed.example') >= 0 && document.getElementById('dom_name').value === 'renamed.example' &&
      toastText() === 'Domain renamed to renamed.example', toastText());

   document.getElementById('dom_postmaster').value = 'keep-me@renamed.example';
   setValue('newDomainAlias', 'alias.example');
   before = requests.length;
   document.getElementById('newDomainAlias').dispatchEvent(makeEvent('keydown', { key: 'Enter' }));
   await flush();
   posted = lastBody(before, 'POST', '/api/v1/domains/renamed.example/domain-aliases');
   check('Enter in the name box adds the name, under the domain\'s new name', JSON.stringify(posted) === '{"name":"alias.example"}', JSON.stringify(posted));
   check('the names are read again and the new one is there', called(before, 'GET', '/api/v1/domains/renamed.example/domain-aliases').length === 1 && act('daliasdel', { name: 'alias.example' }) !== null);
   check('and an edit made in another group survives it', document.getElementById('dom_postmaster').value === 'keep-me@renamed.example');
   before = requests.length;
   click(act('daliasdel', { name: 'example.net' }));
   await flush();
   check('removing a name asks, deletes it by name and re-reads', confirmations[confirmations.length - 1].indexOf('example.net') >= 0 &&
      called(before, 'DELETE', '/api/v1/domains/renamed.example/domain-aliases/example.net').length === 1 && content().querySelectorAll('button[data-act="daliasdel"]').length === 1);
   nextRefusal = 'That name is already a domain.';
   setValue('newDomainAlias', 'second.example');
   click(act('daliasadd'));
   await flush();
   check('a refused name is the server\'s sentence in the names box', document.getElementById('err_dalias').textContent === 'That name is already a domain.');
   setChecked('dom_active', false);
   before = requests.length;
   click(act('domainsave'));
   await flush();
   put = lastBody(before, 'PUT', '/api/v1/domains/renamed.example');
   check('switching the domain off sends active false, with the postmaster edit that was waiting', JSON.stringify(put) === '{"active":false,"postmaster":"keep-me@renamed.example"}', JSON.stringify(put));
   click(act('cancel'));
   await flush();
   check('Cancel returns to the re-read list, which shows the rename and the state', rows().length === 3 && rows()[0].textContent.indexOf('renamed.example') >= 0 && rows()[0].textContent.indexOf('Disabled') >= 0 &&
      rows()[0].textContent.indexOf('keep-me@renamed.example') >= 0, rows()[0].textContent);
   before = requests.length;
   click(act('domainedit', { name: HOSTILE }));
   await flush();
   check('a hostile name is encoded in the path and drawn as text in the editor', called(before, 'GET', '/api/v1/domains/' + encodeURIComponent(HOSTILE) + '/domain-aliases').length === 1 &&
      content().querySelectorAll('img').length === 0 && document.getElementById('dom_name').value === HOSTILE);
   click(act('cancel'));
   await flush();

   setValue('newDomain', 'example.com');
   before = requests.length;
   click(act('domainnew'));
   await flush();
   posted = lastBody(before, 'POST', '/api/v1/domains');
   check('creating a domain posts its name, active and postmaster', JSON.stringify(posted) === '{"name":"example.com","active":true,"postmaster":""}', JSON.stringify(posted));
   check('and opens the editor on the domain as read back', called(before, 'GET', '/api/v1/domains').length === 1 && document.getElementById('dom_name') !== null &&
      document.getElementById('dom_name').value === 'example.com' && toastText() === 'Domain created: example.com', toastText());
   click(act('cancel'));
   await flush();
   setValue('newDomain', 'second.example');
   before = requests.length;
   click(act('domainnew'));
   await flush();
   check('a domain that exists is the server\'s refusal beside the form', document.getElementById('err_domainnew').textContent === 'domain already exists' && rows().length === 4,
      document.getElementById('err_domainnew').textContent);
   setValue('newDomain', '   ');
   click(act('domainnew'));
   await flush();
   check('an empty name is refused by the page', called(before, 'POST', '/api/v1/domains').length === 1 && document.getElementById('err_domainnew').textContent.indexOf('required') >= 0);
   confirmAnswer = false;
   before = requests.length;
   click(act('domaindel', { name: 'example.com' }));
   await flush();
   check('deleting a domain asks, naming it and what goes with it', called(before, 'DELETE', /domains/).length === 0 && confirmations[confirmations.length - 1].indexOf('example.com') >= 0 &&
      confirmations[confirmations.length - 1].indexOf('account') >= 0, confirmations[confirmations.length - 1]);
   confirmAnswer = true;
   click(act('domaindel', { name: 'example.com' }));
   await flush();
   check('yes deletes by name and the re-read list is without it', called(before, 'DELETE', '/api/v1/domains/example.com').length === 1 && rows().length === 3 && rows().every((r) => r.textContent.indexOf('example.com') < 0));
   nextRefusal = 'The domain is named by a route.';
   click(act('domaindel', { name: 'second.example' }));
   await flush();
   check('a refused delete is shown on the domain\'s row', document.getElementById('err_domain_second.example').textContent === 'The domain is named by a route.' && rows().length === 3);

   // ---- IP ranges
   before = requests.length;
   await goTo('ipranges');
   check('the IP ranges view reads the ranges', called(before, 'GET', '/api/v1/ipranges').length === 1 && $('#viewTitle').textContent === 'IP ranges', paths(before));
   check('one row per range with the desktop list\'s columns', rows().length === 3 && rows()[1].textContent.indexOf('Internet') >= 0 && rows()[1].textContent.indexOf('0.0.0.0') >= 0 &&
      rows()[1].textContent.indexOf('255.255.255.255') >= 0 && rows()[1].textContent.indexOf('10') >= 0, rows().length ? rows()[1].textContent : 'no rows');
   check('the connection flags are shown as yes and no', rows()[1].querySelectorAll('.badge.good').length === 3 && rows()[2].querySelectorAll('.badge.warn').length === 3);
   check('a range that expires says so, with its time', rows()[2].textContent.indexOf('expires 2026-09-14 11:30:00') >= 0 && rows()[0].textContent.indexOf('expires') < 0);
   click(act('rangenew'));
   await flush();
   let rangeHeadings = content().querySelectorAll('h2').map((h) => h.textContent.trim());
   check('the editor is grouped as the desktop dialog is, the expiry in a group of its own', ['General', 'Connections', 'Relaying', 'Require auth', 'Protection', 'Expiry'].every((g) => rangeHeadings.indexOf(g) >= 0) && rangeHeadings.indexOf('Other') < 0,
      JSON.stringify(rangeHeadings));
   check('a new range does not expire and has no time', document.getElementById('range_expires').checked === false && document.getElementById('range_expires_time').value === '' &&
      document.getElementById('range_expires_time').closest('.fr').textContent.indexOf("server's clock") >= 0);
   const drawnRange = content().querySelectorAll('input, select, textarea').map((el) => el.id).filter((id) => id.indexOf('range_') === 0).map((id) => id.slice(6)).sort();
   check('every key of the schema has a control, and nothing else', JSON.stringify(drawnRange) === JSON.stringify(Object.keys(RANGE_PROPS).sort()), JSON.stringify(drawnRange));
   check('a new range starts where the create handler puts a key left out, with the desktop\'s priority',
      Object.keys(RANGE_CREATE_DEFAULTS).every((k) => document.getElementById('range_' + k).checked === RANGE_CREATE_DEFAULTS[k]) && document.getElementById('range_priority').value === '15' &&
      document.getElementById('range_name').value === '' && document.getElementById('range_lower').value === '',
      Object.keys(RANGE_CREATE_DEFAULTS).map((k) => k + '=' + document.getElementById('range_' + k).checked).join(' '));
   check('the required keys are marked and the flags carry the desktop\'s wording', document.getElementById('range_lower').closest('.fr').textContent.indexOf('required') >= 0 &&
      document.getElementById('range_priority').closest('.fr').textContent.indexOf('required') < 0 && document.getElementById('range_deliver_remote_to_remote').closest('.fr').textContent.indexOf('open relay') >= 0);
   setValue('range_name', 'Office');
   setValue('range_lower', '10.0.0.0');
   setValue('range_upper', '10.0.0.255');
   setValue('range_priority', 'high');
   before = requests.length;
   click(act('rangesave'));
   await flush();
   check('a priority that is not a number is refused by the page', called(before, 'POST', /ipranges/).length === 0 && document.getElementById('err_rangeedit').textContent.indexOf('whole number') >= 0);
   setValue('range_priority', '25');
   setChecked('range_deliver_local_to_remote', true);
   setValue('range_upper', 'ten.0.0.255');
   click(act('rangesave'));
   await flush();
   check('an address that does not parse is the server\'s refusal, shown in the editor', called(before, 'POST', '/api/v1/ipranges').length === 1 &&
      document.getElementById('err_rangeedit').textContent === 'lower and upper must be IP addresses' && document.getElementById('range_name').value === 'Office');
   setValue('range_upper', '10.0.0.255');
   before = requests.length;
   click(act('rangesave'));
   await flush();
   posted = lastBody(before, 'POST', '/api/v1/ipranges');
   check('saving a new range posts every field, the priority as a number', !!posted && Object.keys(posted).length === Object.keys(RANGE_PROPS).length && posted.name === 'Office' && posted.lower === '10.0.0.0' &&
      posted.priority === 25 && posted.deliver_local_to_remote === true && posted.deliver_remote_to_remote === false, JSON.stringify(posted));
   check('and the re-read list has it', called(before, 'GET', '/api/v1/ipranges').length === 1 && rows().length === 4 && rows()[3].textContent.indexOf('Office') >= 0 && toastText() === 'IP range saved');
   click(act('rangeedit', { id: 2 }));
   await flush();
   check('editing shows the range\'s own values', document.getElementById('range_name').value === 'Internet' && document.getElementById('range_upper').value === '255.255.255.255' &&
      document.getElementById('range_priority').value === '10' && document.getElementById('range_require_auth_local_to_remote').checked === true);
   setChecked('range_allow_pop3', false);
   setChecked('range_require_tls_for_auth', true);
   before = requests.length;
   click(act('rangesave'));
   await flush();
   put = lastBody(before, 'PUT', '/api/v1/ipranges/2');
   check('saving an existing range PUTs the whole record by id', !!put && put.allow_pop3 === false && put.require_tls_for_auth === true && put.name === 'Internet' && Object.keys(put).length === Object.keys(RANGE_PROPS).length,
      JSON.stringify(put));
   check('and the re-read list shows the change', rows()[1].querySelectorAll('.badge.warn').length === 1);
   click(act('rangeedit', { id: 2 }));
   await flush();
   nextRefusal = 'The range overlaps My computer with the same priority.';
   click(act('rangesave'));
   await flush();
   check('a refused save keeps the editor open with the server\'s sentence', document.getElementById('err_rangeedit').textContent === 'The range overlaps My computer with the same priority.' && $('#range_name') !== null);
   setChecked('range_expires', true);
   before = requests.length;
   click(act('rangesave'));
   await flush();
   check('turning expiry on without a time is the server\'s refusal in the editor', called(before, 'PUT', '/api/v1/ipranges/2').length === 1 &&
      document.getElementById('err_rangeedit').textContent === 'expires_time is required when a range is made to expire' && document.getElementById('range_expires').checked === true);
   setValue('range_expires_time', '2030-01-02');
   before = requests.length;
   click(act('rangesave'));
   await flush();
   put = lastBody(before, 'PUT', '/api/v1/ipranges/2');
   check('with a time, the PUT carries the flag and the time and the row shows when', !!put && put.expires === true && put.expires_time === '2030-01-02' && rows()[1].textContent.indexOf('expires 2030-01-02 00:00:00') >= 0, JSON.stringify(put));
   click(act('rangeedit', { id: 3 }));
   await flush();
   check('editing a range the auto-ban placed shows its expiry', document.getElementById('range_expires').checked === true && document.getElementById('range_expires_time').value === '2026-09-14 11:30:00');
   click(act('cancel'));
   await flush();
   confirmAnswer = false;
   before = requests.length;
   click(act('rangedel', { id: 3 }));
   await flush();
   check('deleting a range asks, naming it and its addresses', called(before, 'DELETE', /ipranges/).length === 0 && confirmations[confirmations.length - 1].indexOf('Auto-ban: 203.0.113.9 (203.0.113.9 – 203.0.113.9)') >= 0,
      confirmations[confirmations.length - 1]);
   confirmAnswer = true;
   click(act('rangedel', { id: 3 }));
   await flush();
   check('yes deletes by id and the re-read list is without it', called(before, 'DELETE', '/api/v1/ipranges/3').length === 1 && rows().length === 3 && rows().every((r) => r.textContent.indexOf('Auto-ban') < 0));
   nextRefusal = 'The last range cannot be deleted.';
   click(act('rangedel', { id: 1 }));
   await flush();
   check('a refused delete is shown on its row', document.getElementById('err_range_1').textContent === 'The last range cannot be deleted.' && rows().length === 3);

   // ---- fetch accounts, under the account that owns them
   const fetchList = '/api/v1/accounts/anna%40example.com/fetch-accounts';
   before = requests.length;
   await goTo('fetch');
   check('the fetch view reads the domains, the first domain\'s accounts and the first account\'s external accounts',
      called(before, 'GET', '/api/v1/domains').length === 1 && called(before, 'GET', '/api/v1/domains/renamed.example/accounts').length === 1 && called(before, 'GET', fetchList).length === 1, paths(before));
   check('the pickers hold the domains and the accounts, the first of each chosen', $('#fetchDomain').querySelectorAll('option').length === 3 && $('#fetchDomain').value === 'renamed.example' &&
      $('#fetchAccount').querySelectorAll('option').map((o) => o.attributes.value).join(',') === 'anna@example.com,carla@example.com' && $('#fetchAccount').value === 'anna@example.com');
   check('one row per external account with its server, security, schedule and state', rows().length === 1 && rows()[0].textContent.indexOf('pop.isp.example:995') >= 0 && rows()[0].textContent.indexOf('TLS') >= 0 &&
      rows()[0].textContent.indexOf('30 min') >= 0 && rows()[0].textContent.indexOf('On') >= 0 && rows()[0].textContent.indexOf('2026-09-14 10:30:00') >= 0, rows().length ? rows()[0].textContent : 'no rows');
   before = requests.length;
   $('#fetchAccount').value = 'carla@example.com';
   $('#fetchAccount').dispatchEvent(makeEvent('change'));
   await flush();
   check('choosing an account reads its external accounts', called(before, 'GET', '/api/v1/accounts/carla%40example.com/fetch-accounts').length === 1 && $('#fetchAccount').value === 'carla@example.com' &&
      rows().length === 1 && rows()[0].textContent.indexOf('No external accounts') >= 0, paths(before));
   before = requests.length;
   $('#fetchDomain').value = 'second.example';
   $('#fetchDomain').dispatchEvent(makeEvent('change'));
   await flush();
   check('choosing a domain reads its accounts, and one without any has nothing to add to', called(before, 'GET', '/api/v1/domains/second.example/accounts').length === 1 &&
      $('#fetchAccount').querySelectorAll('option').length === 0 && content().querySelectorAll('button[data-act="fetchnew"]').length === 0 && called(before, 'GET', /fetch-accounts/).length === 0, paths(before));
   $('#fetchDomain').value = 'renamed.example';
   $('#fetchDomain').dispatchEvent(makeEvent('change'));
   await flush();
   check('back on a domain with accounts, its first is chosen again', $('#fetchAccount').value === 'anna@example.com' && rows().length === 1 && rows()[0].textContent.indexOf('ISP POP3') >= 0);
   click(act('fetchnew'));
   await flush();
   const fetchHeadings = content().querySelectorAll('h2').map((h) => h.textContent.trim());
   check('the editor names the account and is grouped', fetchHeadings[0].indexOf('anna@example.com') >= 0 && ['External account', 'Schedule', 'Downloaded mail'].every((g) => fetchHeadings.indexOf(g) >= 0) && fetchHeadings.indexOf('Other') < 0,
      JSON.stringify(fetchHeadings));
   const drawnFetch = content().querySelectorAll('input, select, textarea').map((el) => el.id).filter((id) => id.indexOf('fetch_') === 0).map((id) => id.slice(6)).sort();
   check('every key of the schema has a control, and nothing else', JSON.stringify(drawnFetch) === JSON.stringify(Object.keys(FETCH_PROPS).sort()), JSON.stringify(drawnFetch));
   check('a new one starts at the defaults the description states, read out of its sentence', document.getElementById('fetch_enabled').checked === true && document.getElementById('fetch_minutes_between_fetch').value === '30' &&
      document.getElementById('fetch_days_to_keep_messages').value === '0' && document.getElementById('fetch_server_type').value === 'pop3' && document.getElementById('fetch_connection_security').value === 'none' &&
      document.getElementById('fetch_port').value === '' && document.getElementById('fetch_name').value === '',
      'minutes=' + document.getElementById('fetch_minutes_between_fetch').value + ' days=' + document.getElementById('fetch_days_to_keep_messages').value + ' type=' + document.getElementById('fetch_server_type').value + ' sec=' + document.getElementById('fetch_connection_security').value);
   check('the three required keys are marked, the password box is write-only and empty, the port carries the desktop\'s advice',
      ['name', 'server_address', 'port'].every((k) => document.getElementById('fetch_' + k).closest('.fr').textContent.indexOf('required') >= 0) &&
      document.getElementById('fetch_username').closest('.fr').textContent.indexOf('required') < 0 &&
      document.getElementById('fetch_password').type === 'password' && document.getElementById('fetch_password').value === '' && document.getElementById('fetch_password').closest('.fr').textContent.indexOf('write-only') >= 0 &&
      document.getElementById('fetch_port').closest('.fr').textContent.indexOf('995') >= 0);
   setValue('fetch_name', 'ISP mailbox');
   setValue('fetch_server_address', 'mail.isp.example');
   setValue('fetch_port', '110');
   setValue('fetch_username', 'anna');
   setValue('fetch_password', 'isp-secret');
   setValue('fetch_connection_security', 'starttls_required');
   before = requests.length;
   click(act('fetchsave'));
   await flush();
   posted = lastBody(before, 'POST', fetchList);
   check('saving a new one posts the form under the account, numbers as numbers and the password as typed', !!posted && posted.name === 'ISP mailbox' && posted.server_address === 'mail.isp.example' && posted.port === 110 &&
      posted.minutes_between_fetch === 30 && posted.days_to_keep_messages === 0 && posted.password === 'isp-secret' && posted.connection_security === 'starttls_required' && posted.server_type === 'pop3' && posted.enabled === true,
      JSON.stringify(posted));
   check('and the re-read list has it, for the same account', called(before, 'GET', fetchList).length === 1 && rows().length === 2 && rows()[1].textContent.indexOf('ISP mailbox') >= 0 &&
      rows()[1].textContent.indexOf('mail.isp.example:110') >= 0 && toastText() === 'External account saved', toastText());
   click(act('fetchedit', { id: 7 }));
   await flush();
   check('editing shows its values and an empty password box', document.getElementById('fetch_name').value === 'ISP POP3' && document.getElementById('fetch_port').value === '995' &&
      document.getElementById('fetch_connection_security').value === 'tls' && document.getElementById('fetch_password').value === '');
   setValue('fetch_minutes_between_fetch', '0');
   before = requests.length;
   click(act('fetchsave'));
   await flush();
   check('a schedule the server refuses is its sentence in the editor, with the edits kept', called(before, 'PUT', fetchList + '/7').length === 1 &&
      document.getElementById('err_fetchedit').textContent === 'minutes_between_fetch must be a whole number between 1 and 100000' && document.getElementById('fetch_minutes_between_fetch').value === '0');
   setValue('fetch_minutes_between_fetch', '15');
   before = requests.length;
   click(act('fetchsave'));
   await flush();
   put = lastBody(before, 'PUT', fetchList + '/7');
   check('saving an existing one PUTs the form by id without a password it was not given', !!put && put.minutes_between_fetch === 15 && put.name === 'ISP POP3' && !('password' in put), JSON.stringify(put));
   check('and the list shows the change', rows()[0].textContent.indexOf('15 min') >= 0);
   before = requests.length;
   click(act('fetchnow', { id: 7 }));
   await flush();
   check('Collect now posts to the download route and re-reads, showing the collection under way', called(before, 'POST', fetchList + '/7/download').length === 1 && called(before, 'GET', fetchList).length === 1 &&
      rows()[0].textContent.indexOf('collecting') >= 0 && toastText() === 'Collection queued', toastText());
   state.fetcherDown = true;
   click(act('fetchnow', { id: 7 }));
   await flush();
   check('a fetcher that is not running is the server\'s sentence on the row', document.getElementById('err_fetch_7').textContent === 'the external fetcher is not running');
   state.fetcherDown = false;
   confirmAnswer = false;
   before = requests.length;
   click(act('fetchdel', { id: 7 }));
   await flush();
   check('deleting asks, naming it and what stays', called(before, 'DELETE', /fetch-accounts/).length === 0 && confirmations[confirmations.length - 1].indexOf('ISP POP3') >= 0 &&
      confirmations[confirmations.length - 1].indexOf('already collected stays') >= 0, confirmations[confirmations.length - 1]);
   confirmAnswer = true;
   click(act('fetchdel', { id: 7 }));
   await flush();
   check('yes deletes by id under the account and the re-read list is without it', called(before, 'DELETE', fetchList + '/7').length === 1 && rows().length === 1 && rows()[0].textContent.indexOf('ISP mailbox') >= 0);

   // ---- the seven small collections, one view
   before = requests.length;
   await goTo('lists');
   check('the Lists view reads the seven collections', Object.keys(COLLECTION_SPECS).every((p) => called(before, 'GET', p).length === 1) && $('#viewTitle').textContent === 'Lists', paths(before));
   const tables = content().querySelectorAll('table');
   check('one table per collection, each with its entry\'s own columns and a row per entry', tables.length === 7 && tables.every((t) => t.querySelectorAll('tbody tr').length === 1) &&
      tables[0].querySelectorAll('th').map((h) => h.textContent).join(',') === 'Active,DNS host,Expected result,Reject message,Score,' &&
      tables[6].querySelectorAll('th').map((h) => h.textContent).join(',') === 'Name,Lower IP,Upper IP,', tables.length + ' tables');
   check('a flag is yes or no, and the values are the entries\' own', tables[0].querySelector('.badge.good') !== null && tables[1].querySelector('.badge.warn') !== null &&
      tables[0].textContent.indexOf('zen.spamhaus.org') >= 0 && tables[4].textContent.indexOf('*.exe') >= 0 && tables[5].textContent.indexOf('10.0.0.*') >= 0 && tables[6].textContent.indexOf('Front relay') >= 0);
   check('each section carries its route\'s own sentence and an Add', content().textContent.indexOf('AntiVirus.BlockedAttachments over COM') >= 0 &&
      COLLECTION_SPECS && ['dnsbl', 'surbl', 'whitelist', 'blocked', 'attachments', 'greylist', 'relays'].every((id) => act('collnew', { coll: id }) !== null));
   click(act('collnew', { coll: 'dnsbl' }));
   await flush();
   const drawnColl = content().querySelectorAll('input, select, textarea').map((el) => el.id).filter((id) => id.indexOf('coll_') === 0).map((id) => id.slice(5)).sort();
   check('Add opens an editor with every key of the create and nothing else', JSON.stringify(drawnColl) === JSON.stringify(Object.keys(DNS_LIST_PROPS).sort()), JSON.stringify(drawnColl));
   check('a new entry starts at the description\'s default and the desktop editor\'s', document.getElementById('coll_active').checked === true && document.getElementById('coll_score').value === '5' &&
      document.getElementById('coll_dns_host').value === '' && document.getElementById('coll_dns_host').closest('.fr').textContent.indexOf('required') >= 0 &&
      document.getElementById('coll_score').closest('.fr').textContent.indexOf('required') < 0);
   setValue('coll_score', 'high');
   before = requests.length;
   click(act('collsave'));
   await flush();
   check('a score that is not a number is refused by the page', called(before, 'POST', /dns-blacklists/).length === 0 && document.getElementById('err_colledit').textContent.indexOf('whole number') >= 0);
   setValue('coll_score', '3');
   click(act('collsave'));
   await flush();
   check('a create without its required key is the server\'s refusal in the editor', called(before, 'POST', '/api/v1/dns-blacklists').length === 1 && document.getElementById('err_colledit').textContent === 'dns_host is required');
   setValue('coll_dns_host', 'bl.example.test');
   setValue('coll_reject_message', 'Listed');
   before = requests.length;
   click(act('collsave'));
   await flush();
   posted = lastBody(before, 'POST', '/api/v1/dns-blacklists');
   check('saving a new entry posts the form, the score as a number', JSON.stringify(posted) === '{"dns_host":"bl.example.test","expected_result":"","reject_message":"Listed","score":3,"active":true}', JSON.stringify(posted));
   check('and the re-read view has it', called(before, 'GET', '/api/v1/dns-blacklists').length === 1 && content().querySelectorAll('table')[0].querySelectorAll('tbody tr').length === 2 &&
      content().querySelectorAll('table')[0].textContent.indexOf('bl.example.test') >= 0 && toastText() === 'DNS blacklist saved', toastText());
   click(act('colledit', { coll: 'blocked', id: 14 }));
   await flush();
   check('editing shows the entry\'s own values and the update\'s sentence', document.getElementById('coll_address').value === 'spam.example' && document.getElementById('coll_score').value === '100' &&
      document.getElementById('coll_description').value === 'A spam domain' && content().textContent.indexOf('a field left out keeps its value') >= 0);
   setValue('coll_score', '50');
   before = requests.length;
   click(act('collsave'));
   await flush();
   put = lastBody(before, 'PUT', '/api/v1/blocked-senders/14');
   check('saving an existing entry PUTs the whole record by id', JSON.stringify(put) === '{"address":"spam.example","score":50,"description":"A spam domain"}' && content().querySelectorAll('table')[3].textContent.indexOf('50') >= 0, JSON.stringify(put));
   click(act('collnew', { coll: 'attachments' }));
   await flush();
   check('a new attachment starts at the desktop\'s *.exe, a new relay at nothing', document.getElementById('coll_wildcard').value === '*.exe');
   click(act('cancel'));
   await flush();
   click(act('collnew', { coll: 'greylist' }));
   await flush();
   setValue('coll_ip_address', '203.0.113.*');
   nextRefusal = 'The IP address must not be empty.';
   click(act('collsave'));
   await flush();
   check('a refused save keeps the editor open with the server\'s sentence', document.getElementById('err_colledit').textContent === 'The IP address must not be empty.' && document.getElementById('coll_ip_address').value === '203.0.113.*');
   before = requests.length;
   click(act('collsave'));
   await flush();
   check('and the next save posts to the collection\'s own path', called(before, 'POST', '/api/v1/greylisting-white-addresses').length === 1 && content().querySelectorAll('table')[5].textContent.indexOf('203.0.113.*') >= 0);
   confirmAnswer = false;
   before = requests.length;
   click(act('colldel', { coll: 'relays', id: 17 }));
   await flush();
   check('deleting asks, naming the entry by its first text column', called(before, 'DELETE', /incoming-relays/).length === 0 && confirmations[confirmations.length - 1].indexOf('incoming relay Front relay') >= 0,
      confirmations[confirmations.length - 1]);
   confirmAnswer = true;
   click(act('colldel', { coll: 'relays', id: 17 }));
   await flush();
   check('yes deletes by id and the re-read table is empty', called(before, 'DELETE', '/api/v1/incoming-relays/17').length === 1 && content().querySelectorAll('table')[6].textContent.indexOf('Nothing here') >= 0);
   nextRefusal = 'The white list is in use.';
   click(act('colldel', { coll: 'whitelist', id: 13 }));
   await flush();
   check('a refused delete is shown on its row', document.getElementById('err_coll_whitelist_13').textContent === 'The white list is in use.');

   // ---- the delivery queue
   before = requests.length;
   await goTo('queue');
   check('the queue is read', called(before, 'GET', '/api/v1/queue').length === 1);
   check('one row per queued message with its recipients and next try', rows().length === 2 && rows()[0].textContent.indexOf('far@away.example') >= 0 && rows()[0].textContent.indexOf('2026-09-14 10:00:00') >= 0);
   before = requests.length;
   click(act('retry', { id: 41 }));
   await flush();
   check('Retry now posts to the message\'s retry route and re-reads', called(before, 'POST', '/api/v1/queue/41/retry').length === 1 && called(before, 'GET', '/api/v1/queue').length === 1, paths(before));
   nextRefusal = 'The message is being delivered right now.';
   click(act('retry', { id: 42 }));
   await flush();
   check('a refused retry is shown on its own row', document.getElementById('err_queue_42').textContent === 'The message is being delivered right now.');
   before = requests.length;
   click(act('delmsg', { id: 41 }));
   await flush();
   check('Delete confirms, deletes and re-reads', confirmations[confirmations.length - 1].indexOf('41') >= 0 && called(before, 'DELETE', '/api/v1/queue/41').length === 1 && rows().length === 1);

   // ---- DANE / TLSA
   before = requests.length;
   await goTo('tlsa');
   check('the TLSA view reads the records', called(before, 'GET', '/api/v1/tlsa').length === 1);
   check('and shows them one per line', $('#tlsaPre').textContent === state.tlsa.records.map((r) => r.record).join('\n'), $('#tlsaPre').textContent);
   click(act('copy'));
   await flush();
   check('Copy all puts the block on the clipboard', clipboard === $('#tlsaPre').textContent);

   // ---- settings, drawn from the OpenAPI document
   before = requests.length;
   await goTo('settings');
   check('the settings view reads the seven groups, and the document was read once for the whole session',
      called(0, 'GET', '/api/v1/openapi.json').length === 1 &&
      ['/api/v1/settings', '/api/v1/settings/antispam', '/api/v1/settings/logging', '/api/v1/settings/antivirus', '/api/v1/settings/scripting', '/api/v1/settings/cache', '/api/v1/settings/indexing'].every((p) => called(before, 'GET', p).length === 1),
      paths(before));
   check('a string is a text box with the value', document.getElementById('set_srv_hostname').type === 'text' && document.getElementById('set_srv_hostname').value === 'mail.example.com');
   check('an integer is a number box', document.getElementById('set_srv_max_message_size_kb').type === 'number' && document.getElementById('set_srv_max_message_size_kb').value === '10240');
   check('a boolean is a checkbox in its state', document.getElementById('set_srv_service_smtp').type === 'checkbox' && document.getElementById('set_srv_service_smtp').checked === true &&
      document.getElementById('set_log_log_smtp').checked === false);
   const words = document.getElementById('set_srv_smtp_relayer_connection_security');
   check('an enumeration is a select with the document\'s words', words.tagName === 'SELECT' && words.querySelectorAll('option').map((o) => o.attributes.value).join(',') === 'none,tls,starttls_optional,starttls_required' && words.value === 'none',
      words ? words.innerHTML : 'none');
   const secret = document.getElementById('set_srv_smtp_relayer_password');
   check('a write-only key is a password box, empty, marked write-only', secret.type === 'password' && secret.value === '' && secret.closest('.fr').textContent.indexOf('write-only') >= 0);
   check('a read-only key is shown and has no control', document.getElementById('set_log_log_directory') === null && content().textContent.indexOf('/var/log/hmailserver') >= 0 &&
      $$('.fr').filter((r) => r.textContent.indexOf('log_directory') >= 0)[0].textContent.indexOf('read-only') >= 0);
   check('a key that waits for a restart is marked', document.getElementById('set_srv_service_smtp').closest('.fr').textContent.indexOf('restart') >= 0 &&
      document.getElementById('set_srv_hostname').closest('.fr').querySelector('.badge.warn') === null);
   check('the description is the hint', document.getElementById('set_srv_hostname').closest('.fr').textContent.indexOf('SMTP banner') >= 0);

   before = requests.length;
   click(act('setsave', { group: 'srv' }));
   await flush();
   check('saving with nothing changed sends nothing', called(before, 'PUT', /settings/).length === 0 && toastText() === 'Nothing changed', toastText());
   setValue('set_srv_max_message_size_kb', '20480');
   before = requests.length;
   click(act('setsave', { group: 'srv' }));
   await flush();
   put = lastBody(before, 'PUT', '/api/v1/settings');
   check('saving sends only the key that changed, as a number', !!put && JSON.stringify(put) === '{"max_message_size_kb":20480}', JSON.stringify(put));
   check('then reads the groups again', called(before, 'GET', '/api/v1/settings').length === 1);
   check('and the control shows what the server holds', document.getElementById('set_srv_max_message_size_kb').value === '20480' && toastText() === '1 setting saved', toastText());
   setValue('set_srv_smtp_relayer_password', 'hunter2');
   setChecked('set_srv_service_smtp', false);
   before = requests.length;
   click(act('setsave', { group: 'srv' }));
   await flush();
   put = lastBody(before, 'PUT', '/api/v1/settings');
   check('a filled password goes with the change, and nothing unchanged', !!put && put.smtp_relayer_password === 'hunter2' && put.service_smtp === false && Object.keys(put).length === 2, JSON.stringify(put));
   check('the password box is empty again after the re-read', document.getElementById('set_srv_smtp_relayer_password').value === '' && document.getElementById('set_srv_service_smtp').checked === false);
   setValue('set_spam_spam_mark_threshold', 'five');
   before = requests.length;
   click(act('setsave', { group: 'spam' }));
   await flush();
   check('a number that is not a number is refused by the page', called(before, 'PUT', /antispam/).length === 0 && document.getElementById('err_set_spam').textContent.indexOf('whole number') >= 0,
      document.getElementById('err_set_spam').textContent);
   setValue('set_spam_spam_mark_threshold', '7');
   nextRefusal = 'The delete threshold must be above the mark threshold.';
   click(act('setsave', { group: 'spam' }));
   await flush();
   check('a refusal is the server\'s sentence beside the group', document.getElementById('err_set_spam').textContent === 'The delete threshold must be above the mark threshold.');
   const filter = $('#setFilter');
   filter.value = 'relayer';
   filter.dispatchEvent(makeEvent('input'));
   check('the filter hides the rows that do not match', document.getElementById('set_srv_hostname').closest('.fr').hidden === true && document.getElementById('set_srv_smtp_relayer_host').closest('.fr').hidden === false);
   filter.value = '';
   filter.dispatchEvent(makeEvent('input'));
   check('and clearing it shows them again', document.getElementById('set_srv_hostname').closest('.fr').hidden === false);

   // ---- the three groups with verbs beside them
   check('the cache group draws its counters read-only and its ceiling and life as number boxes', document.getElementById('set_cache_domain_cache_size_kb') === null &&
      document.getElementById('set_cache_domain_hit_rate') === null && content().textContent.indexOf('97') >= 0 &&
      document.getElementById('set_cache_domain_cache_max_size_kb').type === 'number' && document.getElementById('set_cache_domain_cache_max_size_kb').value === '10240' &&
      document.getElementById('set_cache_domain_cache_ttl').value === '3600' && document.getElementById('set_cache_enabled').checked === true);
   check('the scripting and indexing groups are drawn with their switches and the read-only facts', document.getElementById('set_script_enabled').checked === false && document.getElementById('set_script_language').value === 'VBScript' &&
      document.getElementById('set_script_current_script_file') === null && content().textContent.indexOf('EventHandlers.vbs') >= 0 &&
      document.getElementById('set_index_enabled').checked === true && document.getElementById('set_index_total_indexed_count') === null && content().textContent.indexOf('40') >= 0);
   check('each verb is a button worded from the route, with the route\'s sentence beside it', act('setpost', { group: 'script', path: '/api/v1/settings/scripting/reload' }) !== null &&
      act('setpost', { group: 'script', path: '/api/v1/settings/scripting/check' }) !== null && act('setpost', { group: 'cache', path: '/api/v1/settings/cache/clear' }) !== null &&
      act('setpost', { group: 'index', path: '/api/v1/settings/indexing/index' }) !== null && act('setpost', { group: 'index', path: '/api/v1/settings/indexing/clear' }) !== null &&
      content().textContent.indexOf('What Settings.Cache.Clear does over COM') >= 0);
   setValue('set_cache_domain_cache_max_size_kb', '2048');
   before = requests.length;
   click(act('setsave', { group: 'cache' }));
   await flush();
   put = lastBody(before, 'PUT', '/api/v1/settings/cache');
   check('saving the cache group sends only the ceiling that changed, and never a counter', JSON.stringify(put) === '{"domain_cache_max_size_kb":2048}', JSON.stringify(put));
   before = requests.length;
   click(act('setpost', { group: 'cache', path: '/api/v1/settings/cache/clear' }));
   await flush();
   check('Empty the caches posts to the verb and re-reads the group, so the counters follow', called(before, 'POST', '/api/v1/settings/cache/clear').length === 1 && called(before, 'GET', '/api/v1/settings/cache').length === 1 &&
      toastText() === 'Empty the caches: done' && $$('.fr').filter((r) => r.textContent.indexOf('domain_hit_rate') >= 0)[0].textContent.indexOf('97') < 0, toastText());
   state.scriptProblem = 'Line 12: expected end of statement';
   before = requests.length;
   click(act('setpost', { group: 'script', path: '/api/v1/settings/scripting/check' }));
   await flush();
   check('Check the syntax shows the parser\'s message beside the group and changes nothing', called(before, 'POST', '/api/v1/settings/scripting/check').length === 1 &&
      document.getElementById('err_set_script').textContent === 'Line 12: expected end of statement' && called(before, 'GET', /settings/).length === 0, document.getElementById('err_set_script').textContent);
   state.scriptProblem = '';
   click(act('setpost', { group: 'script', path: '/api/v1/settings/scripting/check' }));
   await flush();
   check('and says so when it parses', document.getElementById('err_set_script').textContent === '' && toastText() === 'The script parses.', toastText());
   before = requests.length;
   click(act('setpost', { group: 'script', path: '/api/v1/settings/scripting/reload' }));
   await flush();
   check('Reload the script posts to its route', called(before, 'POST', '/api/v1/settings/scripting/reload').length === 1 && state.scriptReloads === 1);
   before = requests.length;
   click(act('setpost', { group: 'index', path: '/api/v1/settings/indexing/index' }));
   await flush();
   check('Index now posts and the re-read count follows', called(before, 'POST', '/api/v1/settings/indexing/index').length === 1 && $$('.fr').filter((r) => r.textContent.indexOf('total_indexed_count') >= 0)[0].textContent.indexOf('45') >= 0);
   nextRefusal = 'The index is being rebuilt already.';
   click(act('setpost', { group: 'index', path: '/api/v1/settings/indexing/clear' }));
   await flush();
   check('a refused verb is the server\'s sentence beside its group', document.getElementById('err_set_index').textContent === 'The index is being rebuilt already.');

   // ---- backup: a settings group of its own, beside the manager's status
   before = requests.length;
   await goTo('backup');
   check('the backup view reads its group and the status', called(before, 'GET', '/api/v1/settings/backup').length === 1 && called(before, 'GET', '/api/v1/backup').length === 1 && $('#viewTitle').textContent === 'Backup', paths(before));
   check('the settings are drawn from the document: the folder, the switches, the read-only log path', document.getElementById('set_backup_destination').value === '/var/backups/hmailserver' &&
      document.getElementById('set_backup_backup_domains').checked === true && document.getElementById('set_backup_compress').checked === false && document.getElementById('set_backup_log_file') === null &&
      content().textContent.indexOf('/var/log/hmailserver/hmailserver_backup.log') >= 0);
   check('the status card shows the log\'s tail and no failure', $('#backupLog').textContent.indexOf('Backup completed.') >= 0 && content().textContent.indexOf('No failure recorded') >= 0);
   check('and says where the schedule is', content().textContent.indexOf('ScheduledBackupTime') >= 0);
   setValue('set_backup_destination', '/mnt/backup');
   setChecked('set_backup_compress', true);
   before = requests.length;
   click(act('setsave', { group: 'backup' }));
   await flush();
   put = lastBody(before, 'PUT', '/api/v1/settings/backup');
   check('saving the backup settings sends only what changed', JSON.stringify(put) === '{"destination":"/mnt/backup","compress":true}', JSON.stringify(put));
   check('then re-reads the group on this view, not the settings view', called(before, 'GET', '/api/v1/settings/backup').length === 1 && called(before, 'GET', '/api/v1/settings').length === 0 &&
      $('#viewTitle').textContent === 'Backup' && document.getElementById('set_backup_destination').value === '/mnt/backup' && document.getElementById('set_backup_compress').checked === true && toastText() === '2 settings saved', toastText());
   before = requests.length;
   click(act('backupstart'));
   await flush();
   check('Start backup now posts and re-reads the status', called(before, 'POST', '/api/v1/backup').length === 1 && called(before, 'GET', '/api/v1/backup').length === 1 && toastText() === 'Backup started' &&
      $('#backupLog').textContent.indexOf('2026-09-14 11:00:00 Backup started.') >= 0, toastText());
   click(act('backupstart'));
   await flush();
   check('a backup already running is the server\'s refusal beside the button', document.getElementById('err_backupstart').textContent === 'the backup did not start');
   state.backupRunning = false;
   state.backup.status = 'The destination directory could not be written.';
   before = requests.length;
   click(act('backupstatus'));
   await flush();
   check('Re-read status reads it again and shows the manager\'s last failure', called(before, 'GET', '/api/v1/backup').length === 1 && content().textContent.indexOf('The destination directory could not be written.') >= 0);
   state.backup.status = '';

   // ---- the server messages
   before = requests.length;
   await goTo('messages');
   check('the messages view reads the messages', called(before, 'GET', '/api/v1/settings/messages').length === 1 && $('#viewTitle').textContent === 'Server messages', paths(before));
   check('one row per message, the name as the caption and the text in a text area with its own Save', $$('.fr').length === 2 && $$('.fr')[0].textContent.indexOf('BOUNCE_MESSAGE') >= 0 &&
      document.getElementById('msg_0').tagName === 'TEXTAREA' && document.getElementById('msg_0').value === 'Your message could not be delivered.' &&
      act('msgsave', { name: 'VIRUS_NOTIFICATION' }) !== null && content().textContent.indexOf('The name is the table\'s own') >= 0);
   before = requests.length;
   click(act('msgsave', { name: 'BOUNCE_MESSAGE' }));
   await flush();
   check('saving an unchanged text sends nothing', called(before, 'PUT', /messages/).length === 0 && toastText() === 'Nothing changed');
   setValue('msg_1', 'A virus was found in a message sent to you, and it was removed.');
   before = requests.length;
   click(act('msgsave', { name: 'VIRUS_NOTIFICATION' }));
   await flush();
   put = lastBody(before, 'PUT', '/api/v1/settings/messages/VIRUS_NOTIFICATION');
   check('a changed text is PUT by name, as text alone', JSON.stringify(put) === '{"text":"A virus was found in a message sent to you, and it was removed."}', JSON.stringify(put));
   check('and the messages are read again with the change', called(before, 'GET', '/api/v1/settings/messages').length === 1 && document.getElementById('msg_1').value.indexOf('it was removed') >= 0 && toastText() === 'Message saved: VIRUS_NOTIFICATION');
   setValue('msg_0', 'Undeliverable.');
   nextRefusal = 'text is not valid UTF-8';
   click(act('msgsave', { name: 'BOUNCE_MESSAGE' }));
   await flush();
   check('a refusal is the server\'s sentence under the row, with the edit kept', document.getElementById('err_msg_0').textContent === 'text is not valid UTF-8' && document.getElementById('msg_0').value === 'Undeliverable.');
   const messageFilter = $('#msgFilter');
   messageFilter.value = 'virus';
   messageFilter.dispatchEvent(makeEvent('input'));
   check('the filter hides the messages that do not match', $$('.fr')[0].hidden === true && $$('.fr')[1].hidden === false);

   // ---- rules
   before = requests.length;
   await goTo('rules');
   check('the rules view reads the rules and the routes, and the document once only', called(before, 'GET', '/api/v1/rules').length === 1 && called(before, 'GET', '/api/v1/routes').length === 1 &&
      called(0, 'GET', '/api/v1/openapi.json').length === 1, paths(before));
   check('a rule row says when and then', rows().length === 1 && rows()[0].textContent.indexOf('Subject contains') >= 0 && rows()[0].textContent.indexOf('[SPAM]') >= 0 && rows()[0].textContent.indexOf('Junk') >= 0,
      rows()[0].textContent);
   click(act('rulenew'));
   await flush();
   check('New rule opens an empty editor', $('#ruleName') !== null && $('#ruleName').value === '' && $('#ruleActive').checked === true && content().querySelectorAll('[data-r="crit-field"]').length === 0);
   click(act('critadd'));
   await flush();
   const field = content().querySelector('[data-r="crit-field"]');
   check('a criterion\'s fields are the words of the document\'s sentence', field !== null && field.querySelectorAll('option').map((o) => o.attributes.value).join(',') === 'from,to,cc,subject,body,message_size,recipient_list,delivery_attempts,header',
      field ? field.innerHTML : 'none');
   check('and its matches', content().querySelector('[data-r="crit-match"]').querySelectorAll('option').map((o) => o.attributes.value).join(',') === 'equals,not_equals,contains,not_contains,less_than,greater_than,regex,wildcard');
   check('the header name is hidden until the field is header', content().querySelector('[data-r="crit-header"]').hidden === true);
   field.value = 'header';
   field.dispatchEvent(makeEvent('change'));
   await flush();
   check('choosing header shows the header name', content().querySelector('[data-r="crit-header"]').hidden === false);
   content().querySelector('[data-r="crit-header"]').value = 'X-Spam-Flag';
   content().querySelector('[data-r="crit-match"]').value = 'equals';
   content().querySelector('[data-r="crit-value"]').value = 'YES';
   click(act('actadd'));
   await flush();
   const type = content().querySelector('[data-r="act-type"]');
   check('an action\'s types are the document\'s', type !== null && type.querySelectorAll('option').map((o) => o.attributes.value).join(',') === 'forward,reply,move_to_folder,script_function,set_header,send_using_route,bind_to_address,delete,stop,copy',
      type ? type.innerHTML : 'none');
   type.value = 'send_using_route';
   type.dispatchEvent(makeEvent('change'));
   await flush();
   const routeChoice = content().querySelector('[data-r="act-route_id"]');
   check('send_using_route offers the routes by name', routeChoice !== null && routeChoice.tagName === 'SELECT' && routeChoice.textContent.indexOf('partner.example') >= 0, routeChoice ? routeChoice.innerHTML : 'none');
   routeChoice.value = '5';
   click(act('actadd'));
   await flush();
   const secondType = content().querySelector('[data-r="act-type"][data-i="1"]');
   secondType.value = 'forward';
   secondType.dispatchEvent(makeEvent('change'));
   await flush();
   const abortBox = content().querySelector('[data-r="act-abort_spam_flagged"][data-i="1"]');
   check('a forward action offers its abort flag as a checkbox with the desktop\'s caption, off', abortBox !== null && abortBox.type === 'checkbox' && abortBox.checked === false &&
      abortBox.closest('label').textContent.indexOf('Abort on messages marked as spam') >= 0 && content().querySelector('[data-r="act-abort_spam_flagged"][data-i="0"]') === null,
      abortBox ? 'found' : 'no checkbox');
   content().querySelector('[data-r="act-to"][data-i="1"]').value = 'copy@example.test';
   abortBox.checked = true;
   $('#ruleName').value = 'Flagged via partner';
   $('#ruleAll').value = 'any';
   before = requests.length;
   click(act('rulesave'));
   await flush();
   posted = lastBody(before, 'POST', '/api/v1/rules');
   check('saving a new rule posts the shape the route takes, the flag as a boolean', !!posted && posted.name === 'Flagged via partner' && posted.active === true && posted.all_criteria === false &&
      JSON.stringify(posted.criteria) === '[{"field":"header","match":"equals","value":"YES","header":"X-Spam-Flag"}]' &&
      JSON.stringify(posted.actions) === '[{"type":"send_using_route","route_id":5},{"type":"forward","to":"copy@example.test","abort_spam_flagged":true}]', JSON.stringify(posted));
   check('and returns to the re-read list with the new rule', called(before, 'GET', '/api/v1/rules').length === 1 && rows().length === 2 && rows()[1].textContent.indexOf('Flagged via partner') >= 0);
   click(act('ruleedit', { id: 1 }));
   await flush();
   check('editing shows the rule\'s own values', $('#ruleName').value === 'Spam to junk' && content().querySelector('[data-r="crit-value"]').value === '[SPAM]' && content().querySelector('[data-r="act-folder"]').value === 'Junk');
   $('#ruleName').value = 'Spam to Junk folder';
   nextRefusal = 'A rule named that already exists.';
   before = requests.length;
   click(act('rulesave'));
   await flush();
   check('a refused save keeps the editor open with the server\'s sentence', $('#ruleName') !== null && document.getElementById('err_ruleedit').textContent === 'A rule named that already exists.');
   click(act('rulesave'));
   await flush();
   const replaced = lastBody(before, 'PUT', '/api/v1/rules/1');
   check('saving an existing rule PUTs it by id', !!replaced && replaced.name === 'Spam to Junk folder' && JSON.stringify(replaced.actions) === '[{"type":"move_to_folder","folder":"Junk"}]', JSON.stringify(replaced));
   check('the list shows the new name', rows()[0].textContent.indexOf('Spam to Junk folder') >= 0);
   before = requests.length;
   click(act('ruledel', { id: 1 }));
   await flush();
   check('deleting a rule names it in the question and deletes by id', confirmations[confirmations.length - 1].indexOf('Spam to Junk folder') >= 0 && called(before, 'DELETE', '/api/v1/rules/1').length === 1 && rows().length === 1);
   click(act('rulenew'));
   await flush();
   click(act('cancel'));
   await flush();
   check('Cancel returns to the list', $('#ruleName') === null && rows().length === 1);

   // ---- routes
   before = requests.length;
   await goTo('routes');
   check('the routes view reads the routes', called(before, 'GET', '/api/v1/routes').length === 1 && rows().length === 1);
   check('a route row shows the target, its security and its authentication', rows()[0].textContent.indexOf('smtp.partner.example:587') >= 0 && rows()[0].textContent.indexOf('relay') >= 0 && rows()[0].textContent.indexOf('every recipient') >= 0,
      rows()[0].textContent);
   click(act('routenew'));
   await flush();
   check('a new route starts at the defaults the document states', document.getElementById('route_target_smtp_port').value === '25' && document.getElementById('route_number_of_tries').value === '3' &&
      document.getElementById('route_all_addresses').checked === true && document.getElementById('route_relayer_requires_authentication').checked === false && document.getElementById('route_connection_security').value === 'none',
      'port=' + document.getElementById('route_target_smtp_port').value + ' all=' + document.getElementById('route_all_addresses').checked);
   check('the COM spelling of the recipient flag is not drawn twice', document.getElementById('route_treat_security_as_local_domain') === null && document.getElementById('route_treat_recipient_as_local_domain') !== null);
   check('the required keys are marked', document.getElementById('route_domain_name').closest('.fr').textContent.indexOf('required') >= 0 && document.getElementById('route_description').closest('.fr').textContent.indexOf('required') < 0);
   setValue('route_domain_name', 'other.example');
   setValue('route_target_smtp_host', 'mx.other.example');
   setChecked('route_all_addresses', false);
   setValue('route_addresses', 'a@other.example\n\nb@other.example\n');
   before = requests.length;
   click(act('routesave'));
   await flush();
   posted = lastBody(before, 'POST', '/api/v1/routes');
   check('saving a new route posts the form with the defaults, the list as an array, and no password', !!posted && posted.domain_name === 'other.example' && posted.target_smtp_port === 25 && posted.all_addresses === false &&
      JSON.stringify(posted.addresses) === '["a@other.example","b@other.example"]' && !('relayer_auth_password' in posted) && !('treat_security_as_local_domain' in posted), JSON.stringify(posted));
   check('and the re-read list has it', rows().length === 2 && rows()[1].textContent.indexOf('2 addresses') >= 0, rows().map((r) => r.textContent).join(' | '));
   click(act('routeedit', { id: 5 }));
   await flush();
   check('editing a route shows its values and an empty password box', document.getElementById('route_target_smtp_host').value === 'smtp.partner.example' && document.getElementById('route_relayer_auth_password').value === '' &&
      document.getElementById('route_connection_security').value === 'starttls_required');
   setValue('route_relayer_auth_password', 'new-secret');
   before = requests.length;
   click(act('routesave'));
   await flush();
   put = lastBody(before, 'PUT', '/api/v1/routes/5');
   check('saving an existing route PUTs the whole record with the new password', !!put && put.relayer_auth_password === 'new-secret' && put.target_smtp_host === 'smtp.partner.example' && put.relayer_requires_authentication === true, JSON.stringify(put));
   before = requests.length;
   click(act('routedel', { id: 5 }));
   await flush();
   check('deleting a route names its domain and deletes by id', confirmations[confirmations.length - 1].indexOf('partner.example') >= 0 && called(before, 'DELETE', '/api/v1/routes/5').length === 1 && rows().length === 1);

   // ---- certificates
   before = requests.length;
   await goTo('certs');
   check('the certificates view reads the certificates', called(before, 'GET', '/api/v1/certificates').length === 1 && rows().length === 1 && rows()[0].textContent.indexOf('/etc/ssl/mail.pem') >= 0);
   check('the form has the document\'s keys and its password box is empty', document.getElementById('cert_name') !== null && document.getElementById('cert_private_key_password').type === 'password' && document.getElementById('cert_private_key_password').value === '');
   setValue('cert_name', 'second');
   setValue('cert_certificate_file', '/etc/ssl/second.pem');
   setValue('cert_private_key_file', '/etc/ssl/second.key');
   before = requests.length;
   click(act('certadd'));
   await flush();
   posted = lastBody(before, 'POST', '/api/v1/certificates');
   check('adding a certificate posts the paths and no password when none was typed', !!posted && JSON.stringify(posted) === '{"name":"second","certificate_file":"/etc/ssl/second.pem","private_key_file":"/etc/ssl/second.key"}', JSON.stringify(posted));
   check('and the re-read list shows it', rows().length === 2 && rows()[1].textContent.indexOf('second') >= 0);
   before = requests.length;
   click(act('certdel', { id: 2 }));
   await flush();
   check('a certificate a port binds is refused, in the server\'s words, on its row', called(before, 'DELETE', '/api/v1/certificates/2').length === 1 && document.getElementById('err_cert_2').textContent.indexOf('bound by port 1') >= 0,
      document.getElementById('err_cert_2').textContent);
   const newCert = state.certificates[1].id;
   click(act('certdel', { id: newCert }));
   await flush();
   check('an unbound one is deleted and gone from the re-read list', rows().length === 1);

   // ---- ports
   before = requests.length;
   await goTo('ports');
   check('the ports view reads the ports and the certificates', called(before, 'GET', '/api/v1/ports').length === 1 && called(before, 'GET', '/api/v1/certificates').length === 1 && rows().length === 2);
   check('a port row names its certificate rather than its id', rows()[0].textContent.indexOf('mail.example.com') >= 0 && rows()[0].textContent.indexOf('0.0.0.0:25') >= 0 && rows()[1].textContent.indexOf('—') >= 0);
   check('the restart wording is the document\'s', content().textContent.indexOf('takes effect when the server restarts') >= 0 && content().textContent.indexOf(REINITIALIZE.slice(0, 40)) >= 0);
   click(act('portnew'));
   await flush();
   check('a new port starts at the defaults the document states in prose', document.getElementById('port_address').value === '0.0.0.0' && document.getElementById('port_connection_security').value === 'none' &&
      document.getElementById('port_client_certificate_policy').value === 'off' && document.getElementById('port_certificate_id').tagName === 'SELECT' && document.getElementById('port_certificate_id').value === '0',
      'address=' + document.getElementById('port_address').value + ' cert=' + document.getElementById('port_certificate_id').value);
   check('the certificate choice lists the certificates by name', document.getElementById('port_certificate_id').textContent.indexOf('mail.example.com') >= 0 && document.getElementById('port_certificate_id').textContent.indexOf('(none)') >= 0);
   setValue('port_protocol', 'pop3');
   setValue('port_port', '995');
   setValue('port_connection_security', 'tls');
   setValue('port_certificate_id', '2');
   before = requests.length;
   click(act('portsave'));
   await flush();
   posted = lastBody(before, 'POST', '/api/v1/ports');
   check('saving a new port posts the record with the certificate id as a number', !!posted && posted.protocol === 'pop3' && posted.port === 995 && posted.connection_security === 'tls' && posted.certificate_id === 2 && posted.address === '0.0.0.0',
      JSON.stringify(posted));
   check('and the re-read list has three', rows().length === 3 && rows()[2].textContent.indexOf('0.0.0.0:995') >= 0);
   click(act('portedit', { id: 2 }));
   await flush();
   setValue('port_port', '993');
   setValue('port_connection_security', 'tls');
   before = requests.length;
   click(act('portsave'));
   await flush();
   check('a TLS port without a certificate is the server\'s refusal to make, not the page\'s', called(before, 'PUT', '/api/v1/ports/2').length === 1);
   nextRefusal = null;
   click(act('portedit', { id: 2 }));
   await flush();
   nextRefusal = 'A TLS port needs a certificate.';
   click(act('portsave'));
   await flush();
   check('and the refusal is shown in the editor', document.getElementById('err_portedit').textContent === 'A TLS port needs a certificate.' && $('#port_port') !== null);
   click(act('cancel'));
   await flush();
   before = requests.length;
   click(act('portdel', { id: 2 }));
   await flush();
   check('deleting a port names it and deletes by id', confirmations[confirmations.length - 1].indexOf('imap 0.0.0.0:993') >= 0 && called(before, 'DELETE', '/api/v1/ports/2').length === 1 && rows().length === 2,
      confirmations[confirmations.length - 1]);

   // ---- the restart, which ends this very session
   confirmAnswer = false;
   before = requests.length;
   click(act('portapply'));
   await flush();
   check('the restart asks first', called(before, 'POST', /reinitialize/).length === 0 && confirmations[confirmations.length - 1].indexOf('session ends') >= 0, confirmations[confirmations.length - 1]);
   confirmAnswer = true;
   click(act('portapply'));
   await flush();
   check('yes posts the restart', called(before, 'POST', '/api/v1/server/reinitialize').length === 1);
   check('and stops the dashboard poll', intervals.length === 0, intervals.length + ' intervals');
   for (let i = 0; i < 6 && $('#gate').style.display === 'none'; i += 1) { fireTimers(); await flush(); }
   const probes = called(before, 'GET', '/');
   check('then waits for the page to answer again, asked without a credential', probes.length === 3 && probes.every((r) => r.credentials === 'omit'), probes.length + ' probes');
   check('and returns to the sign-in card saying the session ended with the restart', $('#gate').style.display === 'grid' && $('#loginErr').textContent.indexOf('restarted') >= 0, $('#loginErr').textContent);
   check('the session mark is gone with it', sessionStorage.getItem('hmsSession') === null);

   // ---- back in, to the logs
   signedIn = false;
   await signIn();
   check('signing in again works after a restart', $('#app').style.display === 'flex');
   before = requests.length;
   await goTo('logs');
   check('the logs view reads the file list', called(before, 'GET', '/api/v1/logs').length === 1 && rows().length === 2 && rows()[0].textContent.indexOf('2.0 KB') >= 0, rows().length ? rows()[0].textContent : 'no rows');
   before = requests.length;
   click(act('log', { name: 'hmailserver_2026-09-14.log', lines: 200 }));
   await flush();
   check('Last 200 asks for the tail with the count', called(before, 'GET', '/api/v1/logs/hmailserver_2026-09-14.log?lines=200').length === 1, paths(before));
   check('and shows the lines', $('#logPre').hidden === false && $('#logPre').textContent === state.logLines.join('\n'));
   click(act('log', { name: 'hmailserver_2026-09-14.log', lines: 2000 }));
   await flush();
   check('Last 2000 asks for 2000', called(before, 'GET', /lines=2000$/).length === 1);
   nextRefusal = null;
   state.logs.push({ name: 'gone.log', size: 1, created: '' });
   await goTo('logs');
   click(act('log', { name: 'gone.log', lines: 200 }));
   await flush();
   check('a file the server no longer has is an error under the list, naming it', document.getElementById('err_log').textContent.indexOf('gone.log') >= 0 && document.getElementById('err_log').textContent.indexOf('No such log file') >= 0,
      document.getElementById('err_log').textContent);
   state.logs.pop();

   // ---- the reports
   before = requests.length;
   await goTo('reports');
   check('the reports view reads the index and then one summary, not a request per section',
      called(before, 'GET', '/api/v1/reports').length === 1 && called(before, 'GET', /^\/api\/v1\/reports\/summary\?/).length === 1 &&
      called(before, 'GET', /^\/api\/v1\/reports\/traffic/).length === 0, paths(before));
   check('the window comes from the index rather than from this browser\'s clock',
      document.getElementById('repFrom').attributes.value === '2026-08-17' && document.getElementById('repTo').attributes.value === '2026-09-15',
      document.getElementById('repFrom').attributes.value + '..' + document.getElementById('repTo').attributes.value);
   check('the sources are drawn with their retention and how far back they go',
      content().textContent.indexOf('2026-09-01 08:00:00') >= 0 && content().textContent.indexOf('Message trace') >= 0, '');
   check('and what cannot be answered is shown, not hidden',
      content().textContent.indexOf('Spam and virus counts per domain.') >= 0);
   check('a section whose source is off says so in the server\'s own words and is marked',
      content().textContent.indexOf('The metric history is switched off') >= 0 && content().textContent.indexOf('Not recorded') >= 0);
   const trafficTable = content().querySelectorAll('table')[1];
   check('the traffic table is drawn from the server\'s own columns',
      trafficTable && trafficTable.querySelectorAll('th').map((h) => h.textContent).join(',') === 'Day,Domain,Incoming,Outgoing,Failed incoming,Failed outgoing',
      trafficTable ? trafficTable.querySelectorAll('th').map((h) => h.textContent).join(',') : 'no table');
   check('and holds a row per day and domain', trafficTable && trafficTable.querySelectorAll('tbody tr').length === 3,
      trafficTable ? String(trafficTable.querySelectorAll('tbody tr').length) : 'none');
   check('the chart is drawn in the page, one bar group per day, with no library and nothing fetched',
      content().querySelectorAll('svg').length === 2 && content().querySelectorAll('rect').length === 6,
      content().querySelectorAll('svg').length + ' charts, ' + content().querySelectorAll('rect').length + ' bars');
   check('every table offers its own CSV at the same route with the same window',
      $('#csv_traffic') && $('#csv_traffic').attributes.href.indexOf('/api/v1/reports/traffic?from=2026-08-17&to=2026-09-15') === 0 &&
      $('#csv_traffic').attributes.href.indexOf('format=csv') > 0 && 'download' in $('#csv_traffic').attributes,
      $('#csv_traffic') ? $('#csv_traffic').attributes.href : 'no link');
   check('the mailboxes section is there too, with its own CSV', !!$('#csv_mailboxes') && !!$('#csv_storage'));
   before = requests.length;
   setValue('repFrom', '2026-09-01');
   setValue('repTo', '2026-09-15');
   document.getElementById('repDomain').value = 'example.com';
   setValue('repTop', '5');
   click(act('repshow'));
   await flush();
   check('Show sends the window, the domain and the row count',
      called(before, 'GET', /reports\/summary\?from=2026-09-01&to=2026-09-15&domain=example\.com&top=5/).length === 1, paths(before));
   check('and the tables are redrawn for that domain alone',
      content().querySelectorAll('table')[1].querySelectorAll('tbody tr').length === 2,
      String(content().querySelectorAll('table')[1].querySelectorAll('tbody tr').length));
   check('the CSV link follows the window', $('#csv_traffic').attributes.href.indexOf('domain=example.com') > 0);
   before = requests.length;
   click(act('repdays', { days: 7 }));
   await flush();
   check('Last 7 days counts back whole days from the end of the window, in UTC',
      called(before, 'GET', /reports\/summary\?from=2026-09-09&to=2026-09-15/).length === 1, paths(before));

   // ---- a session that ends under the page
   signedIn = false;
   await goTo('queue');
   check('a 401 on any view returns to the sign-in card and says why', $('#gate').style.display === 'grid' && $('#loginErr').textContent.indexOf('session ended') >= 0, $('#loginErr').textContent);
   check('with the session mark removed', sessionStorage.getItem('hmsSession') === null);

   // ---- the theme, and signing out
   await signIn();
   click($('#themeBtn'));
   check('the theme can be turned over', document.body.getAttribute('data-theme') === 'light');
   check('and is remembered under a name that is not a secret', localStorage.getItem('hmsTheme') === 'light');
   before = requests.length;
   click($('#logoutBtn'));
   await flush();
   check('signing out ends the session on the server', called(before, 'DELETE', '/api/v1/session').length === 1);
   check('and shows the card', $('#gate').style.display === 'grid' && $('#app').style.display === 'none' && sessionStorage.getItem('hmsSession') === null);
   check('and stops the poll', intervals.length === 0);
   check('no inline handler was needed anywhere: every button is data-act', $$('button[onclick]').length === 0);

   // ---- the language: the catalogues, the switch, the walk and the fallback.
   // This browser (the world above) names no language, so everything up to
   // here ran in English and asked for no catalogue; what follows chooses one.
   const catalogue = (code) => JSON.parse(fs.readFileSync(nodePath.join(CATALOGUES, code + '.json'), 'utf8'));
   const choose = async (select, code) => { select.value = code; select.dispatchEvent(makeEvent('change')); await flush(); };
   check('a browser that names no language gets English and no catalogue is asked for', called(0, 'GET', /^\/deck-lang\//).length === 0 && $('#nav button[data-view="domains"]').textContent.indexOf('Domains') >= 0);
   const codes = $('#langSel').querySelectorAll('option').map((o) => o.attributes.value);
   check('the header carries the switch: English first, then the twenty languages', codes.length === 21 && codes[0] === 'en' && codes.indexOf('de') > 0 && codes.indexOf('zh-Hans') > 0, JSON.stringify(codes));
   check('and the sign-in card carries the same switch', $('#langSelGate').querySelectorAll('option').map((o) => o.attributes.value).join() === codes.join());
   await signIn();
   await goTo('domains');
   const de = catalogue('de');
   before = requests.length;
   await choose($('#langSel'), 'de');
   const fetched = called(before, 'GET', '/deck-lang/de.json');
   check('choosing German fetches its catalogue once, same-origin', fetched.length === 1 && fetched[0].credentials === 'same-origin', paths(before));
   check('the document says which language it is in', document.documentElement.getAttribute('lang') === 'de');
   check('and the choice is remembered under a name that is not a secret', localStorage.getItem('hmsLang') === 'de');
   check('the sidebar is walked: the Domains button reads the German', $('#nav button[data-view="domains"]').textContent.indexOf(de['Domains']) >= 0, $('#nav button[data-view="domains"]').textContent);
   check('the top bar title as well', $('#viewTitle').textContent === de['Domains'], $('#viewTitle').textContent);
   check('an attribute is walked: the sign-out button\'s title', $('#logoutBtn').getAttribute('title') === de['Sign out'], $('#logoutBtn').getAttribute('title'));
   check('and a placeholder on the sign-in card', $('#pass').getAttribute('placeholder') === de['Password']);
   check('both switches show the choice', $('#langSel').value === 'de' && $('#langSelGate').value === 'de');
   check('the view is drawn again, in German: the table headings', called(before, 'GET', '/api/v1/domains').length === 1 && content().querySelector('th').textContent === de['Domain'], content().querySelector('th').textContent);
   check('and a button in a row', act('domainedit', { name: 'renamed.example' }).textContent === de['Edit'], act('domainedit', { name: 'renamed.example' }).textContent);
   before = requests.length;
   setValue('newDomain', 'sprache.example');
   click(act('domainnew'));
   await flush();
   check('a sentence the script builds carries its value: the toast', toastText() === de['Domain created: {0}'].replace('{0}', 'sprache.example'), toastText());
   check('the editor\'s heading too', content().querySelector('h2').textContent === de['Domain {0}'].replace('{0}', 'sprache.example'), content().querySelector('h2').textContent);
   check('a hint a table holds, marked with K, is looked up when drawn', document.getElementById('dom_active').closest('.fr').querySelector('.hint').textContent === de['Whether the domain receives mail and its accounts can sign in.'],
      document.getElementById('dom_active').closest('.fr').querySelector('.hint').textContent);
   check('and a group title', $$('h2').some((h) => h.textContent === de['General']));
   confirmAnswer = false;
   click(act('domaindel', { name: 'sprache.example' }));
   await flush();
   check('a question the script asks is German, with its value', confirmations[confirmations.length - 1] === de['Delete the domain {0} with every account, alias and list in it? Their messages go with them.'].replace('{0}', 'sprache.example'),
      confirmations[confirmations.length - 1]);
   confirmAnswer = true;
   click(act('cancel'));
   await flush();
   // Every catalogue that ships draws the view in its language.
   for (const code of codes.slice(1)) {
      const cat = catalogue(code);
      await choose($('#langSel'), code);
      check(code + ': the catalogue applied draws the Domains view in that language', document.documentElement.getAttribute('lang') === code && $('#viewTitle').textContent === cat['Domains'] && content().querySelector('th').textContent === cat['Domain'] && act('domainedit', { name: 'renamed.example' }).textContent === cat['Edit'],
         $('#viewTitle').textContent + ' | ' + content().querySelector('th').textContent);
   }
   // A key the catalogue lacks is shown in English; the rest is not.
   const fr = catalogue('fr');
   droppedKey = 'Domains';
   await choose($('#langSel'), 'fr');
   check('a key the catalogue lacks falls back to English while the rest is French', $('#viewTitle').textContent === 'Domains' && $('#nav button[data-view="domains"]').textContent.indexOf('Domains') >= 0 &&
      $('#nav button[data-view="rules"]').textContent.indexOf(fr['Rules']) >= 0 && content().querySelector('th').textContent === fr['Domain'],
      $('#viewTitle').textContent + ' | ' + $('#nav button[data-view="rules"]').textContent);
   droppedKey = null;
   // A server without the catalogues: the page stays as it was and the switch says so.
   catalogueMissing = true;
   await choose($('#langSel'), 'sv');
   check('a catalogue the server does not have leaves the page in its language, and the switch shows that language', document.documentElement.getAttribute('lang') === 'fr' && $('#langSel').value === 'fr' && $('#langSelGate').value === 'fr' &&
      localStorage.getItem('hmsLang') === 'fr' && content().querySelector('th').textContent === fr['Domain'], $('#langSel').value);
   catalogueMissing = false;
   // Back to English, which is the page itself.
   before = requests.length;
   await choose($('#langSel'), 'en');
   check('English asks the server for nothing and walks the markup back', called(before, 'GET', /^\/deck-lang\//).length === 0 && document.documentElement.getAttribute('lang') === 'en' &&
      $('#nav button[data-view="rules"]').textContent.indexOf('Rules') >= 0 && $('#logoutBtn').getAttribute('title') === 'Sign out' && $('#pass').getAttribute('placeholder') === 'Password' &&
      content().querySelector('th').textContent === 'Domain', $('#nav button[data-view="rules"]').textContent);
   check('and forgets the choice', localStorage.getItem('hmsLang') === null);
   // The sign-in card's switch drives the same mechanism.
   const ja = catalogue('ja');
   await choose($('#langSelGate'), 'ja');
   check('the card\'s switch chooses for the whole page: Japanese on the card, in the sidebar and in the header switch', $('#gate p').textContent === ja['Sign in with the hMailServer administrator credential. The password is exchanged once for a session cookie and is never kept by this page.'] &&
      $('#nav button[data-view="rules"]').textContent.indexOf(ja['Rules']) >= 0 && $('#langSel').value === 'ja', $('#gate p').textContent);
   await choose($('#langSelGate'), 'en');
   check('and back', $('#gate p').textContent.indexOf('Sign in with the hMailServer administrator credential') === 0 && $('#langSel').value === 'en');

   if (failures.length) {
      out.error('\ndeck script: ' + failures.length + ' of ' + checks + ' checks failed\n');
      failures.forEach((f) => out.error('  FAILED: ' + f));
      out.error('');
      process.exit(1);
   }
   out.log('deck script: ' + checks + ' checks passed');
}

main().catch((why) => {
   out.error('the deck script did not survive being run:');
   out.error(why && why.stack ? why.stack : why);
   process.exit(1);
});
