// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// The portal's script, executed. build/check-portal-script.py hands the page
// (Portal.html) and the script (Portal.js) here as the files they are; this
// builds a small DOM from the markup, answers fetch out of a table of
// recorded API responses, runs the script in that world and then asserts what
// the reader would see.
//
// The DOM is deliberately small and deliberately hand-written: no dependency,
// so this runs on a bare runner with nothing installed, and nothing in it is
// magic - if the script reaches for something that is not here, the test fails
// with the name of it rather than passing quietly. Two places where it differs
// from a browser, and both are in the direction of determinism: hashchange is
// fired on a microtask rather than a task, and setTimeout is a queue this file
// fires by hand, so the change probe is tested without waiting six seconds.

'use strict';

const fs = require('fs');
const vm = require('vm');

const [pagePath, scriptPath] = process.argv.slice(2);
if (!pagePath || !scriptPath) {
   console.error('usage: node build/portal-script-test.js <portal.html> <portal.js>');
   process.exit(2);
}

/* ------------------------------------------------------------------ the DOM */

const VOID = new Set(['area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input', 'link', 'meta', 'param', 'source', 'track', 'wbr']);
const RAW = new Set(['script', 'style']);

class TextNode {
   constructor(data) { this.data = data; this.parentNode = null; this.childNodes = []; }
   get textContent() { return this.data; }
   set textContent(v) { this.data = String(v); }
}

class Element {
   constructor(tag) {
      this.tagName = String(tag).toUpperCase();
      this.nodeName = this.tagName;
      this.attributes = Object.create(null);
      this.childNodes = [];
      this.parentNode = null;
      this.listeners = Object.create(null);
      this.style = {};
      this.hidden = false;
      this.value = '';
      this.checked = false;
      this.scrollTop = 0;
      this.scrolledIntoView = 0;
      this.focused = 0;
   }
   get id() { return this.attributes.id || ''; }
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
   get textContent() {
      return this.childNodes.map((n) => (n instanceof Element ? n.textContent : n.data)).join('');
   }
   set textContent(v) {
      this.childNodes.forEach((n) => { n.parentNode = null; });
      this.childNodes = [];
      if (v !== '' && v !== null && v !== undefined) { this.appendChild(new TextNode(String(v))); }
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
      if (name === 'value') { this.value = String(value); }
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
         (node.listeners[event.type] || []).slice().forEach((fn) => fn.call(node, event));
         if (event.propagationStopped) { return !event.defaultPrevented; }
         node = node.parentNode;
      }
      return !event.defaultPrevented;
   }
   focus() { this.focused += 1; document.activeElement = this; }
   scrollIntoView() { this.scrolledIntoView += 1; }
   querySelector() { throw new Error('the portal script must not need querySelector'); }
}

function makeEvent(type, extra) {
   const event = Object.assign({
      type,
      defaultPrevented: false,
      propagationStopped: false,
      preventDefault() { this.defaultPrevented = true; },
      stopPropagation() { this.propagationStopped = true; }
   }, extra || {});
   return event;
}

function parseHtml(text) {
   const root = new Element('#document-fragment');
   const stack = [root];
   const byId = Object.create(null);
   let i = 0;
   const top = () => stack[stack.length - 1];
   while (i < text.length) {
      const lt = text.indexOf('<', i);
      if (lt < 0) {
         if (i < text.length) { top().appendChild(new TextNode(text.slice(i))); }
         break;
      }
      if (lt > i) { top().appendChild(new TextNode(text.slice(i, lt))); }
      if (text.startsWith('<!--', lt)) { i = text.indexOf('-->', lt); i = i < 0 ? text.length : i + 3; continue; }
      if (text.startsWith('<!', lt)) { i = text.indexOf('>', lt); i = i < 0 ? text.length : i + 1; continue; }
      const gt = text.indexOf('>', lt);
      if (gt < 0) { break; }
      const inside = text.slice(lt + 1, gt);
      i = gt + 1;
      if (inside.startsWith('/')) {
         const name = inside.slice(1).trim().toLowerCase();
         for (let n = stack.length - 1; n > 0; n -= 1) {
            if (stack[n].tagName === name.toUpperCase()) { stack.length = n; break; }
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
         const value = match[2] !== undefined ? match[2] : match[3] !== undefined ? match[3] : match[4] !== undefined ? match[4] : '';
         element.attributes[match[1]] = value;
      }
      if ('hidden' in element.attributes) { element.hidden = true; }
      if ('value' in element.attributes) { element.value = element.attributes.value; }
      if ('checked' in element.attributes) { element.checked = true; }
      if ('type' in element.attributes) { element.type = element.attributes.type; }
      if (element.attributes.id) { byId[element.attributes.id] = element; }
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
   return { root, byId };
}

const parsed = parseHtml(fs.readFileSync(pagePath, 'utf8'));

const document = new Element('#document');
document.byId = parsed.byId;
document.hidden = false;
document.activeElement = null;
document.getElementById = function (id) {
   const found = parsed.byId[id];
   if (!found) { throw new Error('the script asked for an element the page does not have: #' + id); }
   return found;
};
document.createElement = function (tag) { return new Element(tag); };
document.body = (function find(node) {
   if (node instanceof Element && node.tagName === 'BODY') { return node; }
   for (const child of node.childNodes) { const hit = find(child); if (hit) { return hit; } }
   return null;
})(parsed.root);
if (!document.body) { throw new Error('the page has no <body>'); }
(function () {
   // Everything under <html> bubbles to the document, so a keydown on an input
   // reaches the handler the script puts on the document, as in a browser.
   let node = document.body;
   while (node.parentNode && node.parentNode !== parsed.root) { node = node.parentNode; }
   node.parentNode = document;
})();

/* -------------------------------------------------- window, location, timers */

const window = new Element('#window');
// A confirm box the test answers: what was asked is recorded, the answer is scripted.
const confirms = [];
let confirmAnswer = true;
window.confirm = (text) => { confirms.push(String(text)); return confirmAnswer; };
// A new window is recorded, not opened.
const opened = [];
window.open = (url, name, features) => { opened.push({ url: String(url), name: String(name || ''), features: String(features || '') }); return null; };
const historyStack = [''];
let historyAt = 0;

function fireHashChange() {
   Promise.resolve().then(() => window.dispatchEvent(makeEvent('hashchange')));
}

const location = {
   origin: 'http://portal.test',
   get href() { return this.origin + '/portal' + this.hash; },
   get hash() { return historyStack[historyAt] ? '#' + historyStack[historyAt] : ''; },
   set hash(value) {
      const wanted = String(value).replace(/^#/, '');
      if (wanted === historyStack[historyAt]) { return; }
      historyStack.length = historyAt + 1;
      historyStack.push(wanted);
      historyAt += 1;
      fireHashChange();
   },
   replace(value) { historyStack[historyAt] = String(value).replace(/^#/, ''); fireHashChange(); }
};

const history = {
   get length() { return historyStack.length; },
   replaceState(state, title, url) { historyStack[historyAt] = String(url).replace(/^#/, ''); },
   pushState(state, title, url) {
      historyStack.length = historyAt + 1;
      historyStack.push(String(url).replace(/^#/, ''));
      historyAt += 1;
   },
   back() { if (historyAt > 0) { historyAt -= 1; fireHashChange(); } },
   forward() { if (historyAt < historyStack.length - 1) { historyAt += 1; fireHashChange(); } }
};

const timers = [];
let timerId = 1;
function fireTimers() {
   const due = timers.splice(0, timers.length);
   due.forEach((t) => t.fn());
   return due.length;
}

const store = new Map();
const localStorage = {
   getItem: (k) => (store.has(k) ? store.get(k) : null),
   setItem: (k, v) => store.set(k, String(v)),
   removeItem: (k) => store.delete(k),
   get length() { return store.size; }
};

class DOMParserStub {
   parseFromString(text) {
      const tree = parseHtml(text);
      const body = (function find(node) {
         if (node instanceof Element && node.tagName === 'BODY') { return node; }
         for (const child of node.childNodes) { const hit = find(child); if (hit) { return hit; } }
         return null;
      })(tree.root);
      return { body: body || tree.root };
   }
}

class FileReaderStub {
   readAsDataURL(blob) {
      const self = this;
      Promise.resolve().then(() => {
         if (!blob || blob.broken) { if (self.onerror) { self.onerror(); } return; }
         self.result = 'data:' + (blob.type || 'application/octet-stream') + ';base64,' + (blob.base64 || '');
         if (self.onload) { self.onload(); }
      });
   }
}

/* ------------------------------------------------------- the recorded server */

const requests = [];
let signedIn = false;
let changeStep = 0;
let renameRefusal = null;
// One message arrives while the reader is looking at the folder: the probe
// says so, and from then on the folder tree and the listing both show it.
let arrived = false;
// The preferences the page saves, answered back merged, as the server does.
let prefsStore = {};
// A server that does not answer the change route at all - an older one, or one
// built before that route existed.
let changesMissing = false;

const INBOX = { id: 1, account_id: 7, name: 'INBOX', path: 'INBOX', parent_id: 0, special_use: '', subscribed: true, writable: true, messages: 3, unseen: 2, uidvalidity: 1, subfolders: [] };
const SENT = { id: 2, account_id: 7, name: 'Sent', path: 'Sent', parent_id: 0, special_use: '\\Sent', subscribed: true, writable: true, messages: 1, unseen: 0, uidvalidity: 1, subfolders: [] };
const WORK = { id: 3, account_id: 7, name: 'Work', path: 'INBOX.Work', parent_id: 1, special_use: '', subscribed: true, writable: true, messages: 0, unseen: 0, uidvalidity: 1, subfolders: [] };

function folderTree() {
   const inbox = Object.assign({}, INBOX, { subfolders: [Object.assign({}, WORK)] });
   if (arrived) { inbox.messages = 4; inbox.unseen = 3; }
   return { delimiter: '.', folders: [inbox, Object.assign({}, SENT)], shared: [] };
}

// The flags the page changes, kept per message and answered back in every
// listing, search and message from then on: the star and the keywords. One
// message starts with a follow-up due today, so the reminder has something
// to say at sign-in.
const todayStamp = (() => { const d = new Date(); return d.getFullYear() + '-' + String(d.getMonth() + 1).padStart(2, '0') + '-' + String(d.getDate()).padStart(2, '0'); })();
const flaggedById = { 101: true };
const keywordsById = { 101: ['$FollowUp', '$Due-' + todayStamp] };
const withState = (m) => Object.assign({}, m, { flags: Object.assign({}, m.flags, { flagged: !!flaggedById[m.id], keywords: (keywordsById[m.id] || []).slice() }) });

const LISTING = {
   total: 5,
   messages: [
      { id: 103, uid: 13, subject: 'Third', from: 'c@example.net', to: 'user@example.com', date: '2026-09-09 09:00', size: 900, flags: { seen: false, flagged: false, draft: false } },
      { id: 102, uid: 12, subject: 'Second', from: 'b@example.net', to: 'user@example.com', date: '2026-09-08 09:00', size: 800, flags: { seen: false, flagged: false, draft: false }, in_reply_to: '<one@example.net>' },
      { id: 101, uid: 11, subject: 'First', from: 'a@example.net', to: 'user@example.com', date: '2026-09-07 09:00', size: 700, flags: { seen: true, flagged: false, draft: false } }
   ]
};

// The rest of the folder, two pages deep: what the page asks for with before_uid.
const OLDER = [
   { id: 100, uid: 10, subject: 'Older', from: 'e@example.net', to: 'user@example.com', date: '2026-09-06 09:00', size: 500, flags: { seen: true, flagged: false, draft: false } },
   { id: 99, uid: 9, subject: 'Oldest', from: 'e@example.net', to: 'user@example.com', date: '2026-09-05 09:00', size: 500, flags: { seen: true, flagged: false, draft: false } }
];

const ARRIVAL = { id: 104, uid: 14, subject: 'Just in', from: 'd@example.net', to: 'user@example.com', date: '2026-09-09 12:00', size: 400, flags: { seen: false, flagged: false, draft: false } };

const WITH_IMAGE = {
   id: 102, folder_id: 1, uid: 12, subject: 'Second', from: 'Bee <b@example.net>', to: 'user@example.com', cc: '',
   date: '2026-09-08 09:00', size: 800, message_id: '<two@example.net>', references: '', in_reply_to: '<one@example.net>',
   text: '', html: '<p>Hello</p><img src="cid:logo@example.net" alt="logo"><img src="cid:chart@example.net" alt="chart"><img src="https://tracker.example.org/pixel.gif">',
   html_remote: true,
   flags: { seen: false, flagged: false, draft: false },
   attachments: [
      { index: 0, name: 'notes.txt', size: 12, content_type: 'text/plain', content_id: '' },
      { index: 1, name: 'logo.png', size: 3400, content_type: 'image/png', content_id: '<logo@example.net>' },
      { index: 2, name: 'chart.svg', size: 900, content_type: 'image/svg+xml', content_id: 'chart@example.net' }
   ]
};

function json(status, body, headers) {
   return { status, body: body === undefined ? '' : JSON.stringify(body), headers: headers || {} };
}

function answer(method, path, body) {
   if (method === 'POST' && path === '/api/v1/session') { signedIn = true; return json(201, { address: 'user@example.com' }); }
   if (method === 'DELETE' && path === '/api/v1/session') { signedIn = false; return json(200, { ended: true }); }
   if (!signedIn) { return json(401, { error: 'Not signed in.' }); }
   if (path === '/api/v1/me') {
      return json(200, {
         address: 'user@example.com', quota: { used_bytes: 5242880, limit_mb: 100 },
         vacation: { enabled: false, subject: '', message: '', expires: false, expires_date: '' },
         second_factor: false, directory_linked: false, password_changed: '2026-09-01'
      });
   }
   if (path === '/api/v1/me/folders' && method === 'GET') { return json(200, folderTree()); }
   if (path === '/api/v1/me/quarantine') { return json(200, { enabled: true, messages: [] }); }
   if (path === '/api/v1/me/settings' && method === 'GET') {
      return json(200, { name: { first: 'A', last: 'B' }, forwarding: { enabled: false, address: '', keep_original: true }, signature: { enabled: false, text: '' } });
   }
   if (path.startsWith('/api/v1/me/contacts?') && method === 'GET') {
      // The address book, narrowed the way the server narrows it: names and
      // addresses containing the text.
      const q = decodeURIComponent((path.split('q=')[1] || '').split('&')[0]).toLowerCase();
      const book = [
         { id: 1, name: 'Alice Example', address: 'alice@example.net', source: 'manual' },
         { id: 2, name: 'Alan Hidden', address: 'al@example.org', source: 'collected' }
      ];
      const found = book.filter((c) => c.name.toLowerCase().indexOf(q) >= 0 || c.address.indexOf(q) >= 0);
      return json(200, { contacts: found, count: found.length, total: book.length });
   }
   if (path === '/api/v1/me/filters' && method === 'GET') { return json(200, { active: '' }); }
   if (path === '/api/v1/me/filters' && method === 'PUT') { return json(200, { saved: true }); }
   if (path === '/api/v1/me/preferences' && method === 'GET') { return json(200, { preferences: prefsStore }); }
   if (path === '/api/v1/me/preferences' && method === 'PUT') { Object.assign(prefsStore, JSON.parse(body || '{}')); return json(200, { preferences: prefsStore }); }
   if (path.startsWith('/api/v1/me/folders/1/messages')) {
      if (path.indexOf('before_uid=') >= 0) { return json(200, { total: arrived ? 6 : 5, messages: OLDER.map(withState) }); }
      if (!arrived) { return json(200, { total: LISTING.total, messages: LISTING.messages.map(withState) }); }
      return json(200, { total: 6, messages: [ARRIVAL].concat(LISTING.messages).map(withState) });
   }
   if (path.startsWith('/api/v1/me/search?')) {
      // Every folder, for the two queries the page makes of it: the Starred
      // view's and the follow-up reminder's.
      const q = decodeURIComponent((path.split('q=')[1] || '').split('&')[0]);
      const all = LISTING.messages.concat(arrived ? [ARRIVAL] : [], OLDER).map(withState);
      const hits = q === 'is:flagged' ? all.filter((m) => m.flags.flagged)
         : q === 'label:$FollowUp' ? all.filter((m) => m.flags.keywords.some((k) => k.toLowerCase() === '$followup')) : [];
      return json(200, { query: q, scanned: all.length, complete: true, more: false, messages: hits.map((m) => Object.assign({ folder_id: 1, folder: 'INBOX' }, m)) });
   }
   if (/^\/api\/v1\/me\/folders\/\d+\/messages/.test(path)) { return json(200, { total: 0, messages: [] }); }
   if (/^\/api\/v1\/me\/messages\/102\/attachments\/(\d+)$/.test(path)) {
      const index = Number(path.split('/').pop());
      // The download route's own content type, which is what decides whether
      // the page may show it: an SVG comes back as a file, on purpose.
      const types = { 0: 'text/plain', 1: 'image/png', 2: 'application/octet-stream' };
      return { status: 200, body: 'PNGBYTES', headers: { 'Content-Type': types[index] || 'application/octet-stream' } };
   }
   if (path === '/api/v1/me/messages/102' && method === 'GET') { return json(200, withState(JSON.parse(JSON.stringify(WITH_IMAGE)))); }
   if (/^\/api\/v1\/me\/messages\/\d+$/.test(path) && method === 'GET') {
      return json(200, withState(Object.assign(JSON.parse(JSON.stringify(WITH_IMAGE)), { id: Number(path.split('/').pop()), html: '', text: 'plain' })));
   }
   if (path === '/api/v1/me/messages' && method === 'POST') { return json(201, { id: 300 }); }
   if (path === '/api/v1/me/drafts' && method === 'POST') { return json(201, { id: 55 }); }
   if (/\/messages\/\d+\/flags$/.test(path)) {
      // The star and the keywords are kept, removed first then added, as the server does.
      const id = Number(path.split('/')[5]);
      const change = JSON.parse(body || '{}');
      if (typeof change.flagged === 'boolean') { flaggedById[id] = change.flagged; }
      let keywords = (keywordsById[id] || []).slice();
      (change.keywords_remove || []).forEach((k) => { keywords = keywords.filter((have) => have.toLowerCase() !== k.toLowerCase()); });
      (change.keywords_add || []).forEach((k) => { if (!keywords.some((have) => have.toLowerCase() === k.toLowerCase())) { keywords.push(k); } });
      keywordsById[id] = keywords;
      return json(200, { id, folder_id: 1, flags: { seen: true, flagged: !!flaggedById[id], draft: false, keywords: keywords.slice() } });
   }
   if (path.startsWith('/api/v1/me/changes')) {
      if (changesMissing) { return json(404, { error: 'Not found.' }); }
      changeStep += 1;
      if (changeStep === 1) { return json(200, { token: 't1', folders: [{ id: 1, count: 3, unseen: 2 }, { id: 2, count: 1, unseen: 0 }, { id: 3, count: 0, unseen: 0 }] }); }
      if (changeStep === 2) { arrived = true; return json(200, { token: 't2', changed: true, folders: [{ id: 1, count: 4, unseen: 3 }, { id: 2, count: 1, unseen: 0 }, { id: 3, count: 0, unseen: 0 }] }); }
      return json(200, { token: 't2', changed: false, folders: [{ id: 1, count: 4, unseen: 3 }, { id: 2, count: 1, unseen: 0 }, { id: 3, count: 0, unseen: 0 }] });
   }
   if (path === '/api/v1/me/folders' && method === 'POST') { return json(201, { id: 9, name: 'Bills' }); }
   if (/^\/api\/v1\/me\/folders\/\d+$/.test(path) && method === 'PUT') {
      if (renameRefusal) { return json(400, { error: renameRefusal }); }
      return json(200, { id: 3, name: 'Renamed' });
   }
   if (/^\/api\/v1\/me\/folders\/\d+$/.test(path) && method === 'DELETE') { return json(200, { deleted: true }); }
   // A move answers with the id the message has where it went, and the folder.
   if (/\/messages\/\d+\/move$/.test(path) && method === 'POST') { return json(200, { id: 202, folder_id: 4 }); }
   return json(404, { error: 'No such route: ' + path });
}

function fetchStub(path, options) {
   const method = (options && options.method) || 'GET';
   const headers = (options && options.headers) || {};
   requests.push({ method, path, headers, body: options && options.body });
   const reply = answer(method, path, options && options.body);
   return Promise.resolve({
      status: reply.status,
      headers: { get: (name) => (name in reply.headers ? reply.headers[name] : null) },
      text: () => Promise.resolve(reply.body),
      blob: () => Promise.resolve({ type: reply.headers['Content-Type'] || 'application/octet-stream', base64: 'QUJD' })
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
   return since(from).some((r) => r.method === method && (typeof pattern === 'string' ? r.path === pattern : pattern.test(r.path)));
}
const flush = async () => { for (let i = 0; i < 60; i += 1) { await new Promise((r) => setImmediate(r)); } };

/* ------------------------------------------------------------------ the run */

// Node has globals of its own with these names (navigator, localStorage,
// fetch), some of them getter-only, so each one is defined over rather than
// assigned: the script must see this file's world and nothing of node's.
const world = {
   document, window, location, history, localStorage,
   fetch: fetchStub,
   confirm: window.confirm,
   DOMParser: DOMParserStub,
   FileReader: FileReaderStub,
   setTimeout: (fn, ms) => { const id = timerId++; timers.push({ id, fn, ms }); return id; },
   clearTimeout: (id) => { const at = timers.findIndex((t) => t.id === id); if (at >= 0) { timers.splice(at, 1); } }
};
Object.keys(world).forEach((name) => {
   Object.defineProperty(globalThis, name, { value: world[name], writable: true, configurable: true });
});

async function main() {
   // A reader who was on the settings page when the session lapsed: the page is
   // loaded at that address, signed out.
   location.hash = '#/settings';
   await flush();

   vm.runInThisContext(fs.readFileSync(scriptPath, 'utf8'), { filename: 'portal.js' });
   await flush();

   const gate = document.getElementById('signin');
   const app = document.getElementById('account');
   check('a page opened without a session shows the sign-in form', gate.hidden === false && app.hidden === true,
      'signin.hidden=' + gate.hidden + ' account.hidden=' + app.hidden);
   check('nothing but /api/v1/me was asked for before a session existed',
      requests.every((r) => r.path === '/api/v1/me'), JSON.stringify(requests.map((r) => r.path)));

   // ---- sign in
   const before = requests.length;
   document.getElementById('address').value = 'user@example.com';
   document.getElementById('password').value = 'a-real-password';
   document.getElementById('signin-form').dispatchEvent(makeEvent('submit'));
   await flush();

   const withAuth = requests.filter((r) => r.headers && r.headers.Authorization);
   check('the password is sent exactly once', withAuth.length === 1, withAuth.length + ' requests carried Authorization');
   check('and only to start a session', withAuth.length === 1 && withAuth[0].method === 'POST' && withAuth[0].path === '/api/v1/session',
      withAuth.length ? withAuth[0].method + ' ' + withAuth[0].path : 'none');
   check('the password field is emptied', document.getElementById('password').value === '');
   check('nothing secret is stored in the browser',
      [...store.values()].every((v) => v.indexOf('a-real-password') < 0) && [...store.keys()].every((k) => !/pass|token|secret|auth/i.test(k)),
      JSON.stringify([...store.entries()]));
   check('every write carries the header the server demands',
      requests.filter((r) => r.method !== 'GET' && r.path !== '/api/v1/session').every((r) => r.headers['X-Requested-With'] === 'hMailServer'));
   check('the account is shown', app.hidden === false && gate.hidden === true);
   check('the folder tree was read', called(before, 'GET', '/api/v1/me/folders'));

   // ---- a follow-up due today is announced once at sign-in, and the notice opens the message
   const toastBox = document.getElementById('toasts');
   check('the follow-ups are asked for once at sign-in', since(before).filter((r) => r.method === 'GET' && r.path === '/api/v1/me/search?q=label%3A%24FollowUp&limit=200').length === 1,
      JSON.stringify(since(before).map((r) => r.path)));
   check('and the one due today is announced, with a way to open it', toastBox.textContent.indexOf('Due for follow-up: First') >= 0 && toastBox.textContent.indexOf('Open') >= 0, toastBox.textContent);
   check('which was announced is kept for the day, in this browser', (() => { try { const v = JSON.parse(store.get('hmPortalDueShown')); return v.day === todayStamp && v.ids.length === 1 && v.ids[0] === 101; } catch (e) { return false; } })(),
      String(store.get('hmPortalDueShown')));
   // Dismissed for now; what Open does is checked at the end, on a second sign-in.
   { const buttons = []; (function walk(n) { for (let i = 0; i < n.childNodes.length; i++) { const c = n.childNodes[i]; if (c.tagName === 'BUTTON' && c.getAttribute('aria-label') === 'Dismiss') { buttons.push(c); } if (c.childNodes) { walk(c); } } })(toastBox); buttons.forEach((b) => b.dispatchEvent(makeEvent('click'))); }
   check('and the notice can be dismissed', toastBox.children.length === 0 && location.hash === '#/settings', toastBox.children.length + ' toasts, ' + location.hash);

   // ---- the address the reader arrived at is the one that is restored
   check('a reload lands where the reader was', document.getElementById('settings-section').hidden === false,
      'hash=' + location.hash + ' title=' + document.getElementById('view-title').textContent);
   check('the sign-in did not lose the address', location.hash === '#/settings', location.hash);

   // ---- the app shell: folders in the sidebar, with their unseen counts
   const nav = document.getElementById('folder-nav');
   // The navigation carries the folders, a Starred view and a heading; the folders are the /f/ entries.
   const folderRoutes = [];
   for (let i = 0; i < nav.children.length; i++) {
      const route = nav.children[i].getAttribute && nav.children[i].getAttribute('data-route');
      if (route && route.indexOf('/f/') === 0) { folderRoutes.push(route); }
   }
   check('every folder is in the sidebar', folderRoutes.length === 3, folderRoutes.length + ' entries');
   check('the inbox is first', folderRoutes[0] === '/f/1', folderRoutes[0]);
   check('the inbox\'s subfolder is listed with the folders', folderRoutes.indexOf('/f/3') > 0, folderRoutes.join(' '));
   check('the Starred view is in the sidebar', nav.children[1].getAttribute('data-route') === '/starred', nav.children[1].getAttribute('data-route'));
   check('the unseen count is shown', nav.children[0].children[2].textContent === '2', nav.children[0].children[2].textContent);

   // ---- a folder has an address, and clicking one goes to it
   nav.children[0].dispatchEvent(makeEvent('click'));
   await flush();
   check('a folder is an address', location.hash === '#/f/1', location.hash);
   check('the folder listing renders one row per message', document.getElementById('message-list').children.length === 3,
      document.getElementById('message-list').children.length + ' rows');
   check('an unseen message is marked', document.getElementById('message-list').children[0].className.indexOf('unseen') >= 0,
      document.getElementById('message-list').children[0].className);
   const rowNames = (row) => { const out = []; for (let i = 0; i < row.childNodes.length; i++) { const c = row.childNodes[i]; if (c.className === 'who') { out.push(c.textContent); } } return out; };
   check('a row names its sender', rowNames(document.getElementById('message-list').children[1]).length === 1 && rowNames(document.getElementById('message-list').children[1])[0].length > 0,
      JSON.stringify(rowNames(document.getElementById('message-list').children[1])));
   check('the view is titled by the folder', document.getElementById('view-title').textContent === 'Inbox',
      document.getElementById('view-title').textContent);

   // ---- the keyboard moves a cursor
   const rows = () => document.getElementById('message-list').children;
   const onCursor = () => { const list = rows(); let at = -1; for (let i = 0; i < list.length; i += 1) { if (list[i].className.indexOf('msg-cursor') >= 0) { at = i; } } return at; };
   check('the cursor starts on the first row', onCursor() === 0, 'at ' + onCursor());
   document.dispatchEvent(makeEvent('keydown', { key: 'j', target: document.body }));
   check('j moves the cursor down', onCursor() === 1, 'at ' + onCursor());
   document.dispatchEvent(makeEvent('keydown', { key: 'ArrowDown', target: document.body }));
   check('the arrow keys move it too', onCursor() === 2, 'at ' + onCursor());
   document.dispatchEvent(makeEvent('keydown', { key: 'k', target: document.body }));
   check('k moves it back up', onCursor() === 1, 'at ' + onCursor());
   document.dispatchEvent(makeEvent('keydown', { key: 'x', target: document.body }));
   check('x ticks the box under the cursor', document.getElementById('bulk-bar').hidden === false &&
      document.getElementById('bulk-count').textContent === '1 selected', document.getElementById('bulk-count').textContent);
   document.dispatchEvent(makeEvent('keydown', { key: 'x', target: document.body }));
   check('and unticks it', document.getElementById('bulk-bar').hidden === true);
   const typing = makeEvent('keydown', { key: 'j', target: document.getElementById('compose-text') });
   document.dispatchEvent(typing);
   check('a keystroke in a text box is not a shortcut', onCursor() === 1 && typing.defaultPrevented === false, 'at ' + onCursor());

   // ---- e archives the row under the cursor, and the toast's Undo moves it back
   const beforeArchive = requests.length;
   const timersBefore = timers.length;
   document.dispatchEvent(makeEvent('keydown', { key: 'e', target: document.body }));
   await flush();
   await flush();
   const archiveCall = since(beforeArchive).filter((r) => r.method === 'POST' && /\/messages\/102\/move$/.test(r.path))[0];
   check('e archives the message under the cursor', !!archiveCall && JSON.parse(archiveCall.body).to === 'archive', archiveCall ? archiveCall.body : 'no move');
   const toasts = document.getElementById('toasts');
   const clickUndo = () => { const buttons = []; (function walk(n) { for (let i = 0; i < n.childNodes.length; i++) { const c = n.childNodes[i]; if (c.tagName === 'BUTTON' && c.textContent === 'Undo') { buttons.push(c); } if (c.childNodes) { walk(c); } } })(toasts); if (!buttons.length) { throw new Error('no Undo button in the toast'); } buttons[0].dispatchEvent(makeEvent('click')); };
   const dismissAllToasts = () => { const buttons = []; (function walk(n) { for (let i = 0; i < n.childNodes.length; i++) { const c = n.childNodes[i]; if (c.tagName === 'BUTTON' && c.getAttribute('aria-label') === 'Dismiss') { buttons.push(c); } if (c.childNodes) { walk(c); } } })(toasts); buttons.forEach((b) => b.dispatchEvent(makeEvent('click'))); };
   check('a toast says so and offers Undo', toasts.children.length === 1 && toasts.textContent.indexOf('Archived') >= 0 && toasts.textContent.indexOf('Undo') >= 0,
      toasts.textContent);
   const undoButtons = []; (function walk(n) { for (let i = 0; i < n.childNodes.length; i++) { const c = n.childNodes[i]; if (c.tagName === 'BUTTON' && c.textContent === 'Undo') { undoButtons.push(c); } walk(c); } })(toasts);
   const beforeUndo = requests.length;
   if (undoButtons.length) { undoButtons[0].dispatchEvent(makeEvent('click')); }
   await flush();
   await flush();
   const undoCall = since(beforeUndo).filter((r) => r.method === 'POST' && /\/messages\/202\/move$/.test(r.path))[0];
   check('Undo moves it back where it was, by the id the move gave it', !!undoCall && JSON.parse(undoCall.body).folder_id === 1, undoCall ? undoCall.body : 'no move back');
   check('and the toast is gone with its timer', toasts.children.length === 0 && timers.length === timersBefore, toasts.children.length + ' toasts, ' + timers.length + ' timers (' + timersBefore + ' before)');
   check('the cursor is still on a row', onCursor() >= 0, 'at ' + onCursor());

   // ---- the star toggles the flag in place
   const starOf = (row) => { for (let i = 0; i < row.childNodes.length; i++) { if (row.childNodes[i].className && row.childNodes[i].className.indexOf('star') === 0) { return row.childNodes[i]; } } return null; };
   const beforeStar = requests.length;
   starOf(rows()[0]).dispatchEvent(makeEvent('click'));
   await flush();
   const starCall = since(beforeStar).filter((r) => r.method === 'PUT' && /\/flags$/.test(r.path))[0];
   check('the star sets the flag', !!starCall && JSON.parse(starCall.body).flagged === true, starCall ? starCall.body : 'no flags call');
   check('and shows it', starOf(rows()[0]).className.indexOf('on') >= 0, starOf(rows()[0]).className);

   // ---- the select menu picks by state
   document.getElementById('select-menu-btn').dispatchEvent(makeEvent('click'));
   check('the select menu opens', document.getElementById('select-menu').hidden === false);
   const picks = document.getElementById('select-menu').children;
   let unreadPick = null; for (let i = 0; i < picks.length; i++) { if (picks[i].getAttribute('data-pick') === 'unread') { unreadPick = picks[i]; } }
   unreadPick.dispatchEvent(makeEvent('click', { target: unreadPick }));
   check('Unread ticks the unread rows', document.getElementById('bulk-count').textContent === '2 selected', document.getElementById('bulk-count').textContent);
   document.getElementById('bulk-clear').dispatchEvent(makeEvent('click'));
   check('Clear unticks them', document.getElementById('bulk-bar').hidden === true);
   document.dispatchEvent(makeEvent('keydown', { key: 'j', target: document.body }));
   while (onCursor() < 1) { document.dispatchEvent(makeEvent('keydown', { key: 'j', target: document.body })); }
   while (onCursor() > 1) { document.dispatchEvent(makeEvent('keydown', { key: 'k', target: document.body })); }

   // ---- a right-click menu on a row: pin, and Escape closes it
   rows()[1].dispatchEvent(makeEvent('contextmenu', { clientX: 300, clientY: 200 }));
   check('a right-click opens the row menu', document.getElementById('context-menu').hidden === false);
   const menuItems = document.getElementById('context-menu').children;
   let pinItem = null; for (let i = 0; i < menuItems.length; i++) { if (menuItems[i].getAttribute && menuItems[i].getAttribute('data-act') === 'pin') { pinItem = menuItems[i]; } }
   check('and offers to pin it', !!pinItem && pinItem.textContent === 'Pin', pinItem ? pinItem.textContent : 'no pin item');
   const beforePin = requests.length;
   pinItem.dispatchEvent(makeEvent('click', { target: pinItem }));
   await flush();
   await flush();
   const pinCall = since(beforePin).filter((r) => r.method === 'PUT' && /\/messages\/102\/flags$/.test(r.path))[0];
   check('Pin adds the pin keyword to the message', !!pinCall && JSON.stringify(JSON.parse(pinCall.body).keywords_add) === '["$Pinned"]', pinCall ? pinCall.body : 'no flags call');
   check('and the menu is gone', document.getElementById('context-menu').hidden === true);
   // The pin is honoured: the pinned message now heads the list, and Unpin puts it back.
   const menuItem = (act) => { const menu = document.getElementById('context-menu'); for (let i = 0; i < menu.children.length; i++) { if (menu.children[i].getAttribute('data-act') === act) { return menu.children[i]; } } return null; };
   check('the pinned message heads the list', rows()[0].textContent.indexOf('Second') >= 0 && rows()[0].className.indexOf('pinned') >= 0, rows()[0].className + ' ' + rows()[0].textContent.slice(0, 30));
   rows()[0].dispatchEvent(makeEvent('contextmenu', { clientX: 300, clientY: 200 }));
   check('and its row menu offers Unpin', !!menuItem('pin') && menuItem('pin').textContent === 'Unpin', menuItem('pin') ? menuItem('pin').textContent : 'no pin item');
   const beforeUnpin = requests.length;
   menuItem('pin').dispatchEvent(makeEvent('click', { target: menuItem('pin') }));
   await flush();
   await flush();
   const unpinCall = since(beforeUnpin).filter((r) => r.method === 'PUT' && /\/messages\/102\/flags$/.test(r.path))[0];
   check('Unpin takes the keyword off and the row goes back to its place', !!unpinCall && JSON.stringify(JSON.parse(unpinCall.body).keywords_remove) === '["$Pinned"]' && rows()[1].textContent.indexOf('Second') >= 0 && rows()[0].className.indexOf('pinned') < 0,
      (unpinCall ? unpinCall.body : 'no flags call') + ' ' + rows().map((r) => r.textContent.slice(0, 20)).join(' | '));
   dismissAllToasts();
   rows()[1].dispatchEvent(makeEvent('contextmenu', { clientX: 300, clientY: 200 }));
   document.dispatchEvent(makeEvent('keydown', { key: 'Escape', target: document.body }));
   check('Escape closes the row menu', document.getElementById('context-menu').hidden === true);

   // ---- the inbox in tabs: every message here is primary, so one tab holds them all
   const tabBar = () => document.getElementById('inbox-tabs');
   const contextItems = (attribute, value) => { const out = []; const walk = (n) => { for (let i = 0; i < n.childNodes.length; i++) { const c = n.childNodes[i]; if (c.getAttribute && c.getAttribute(attribute) === value) { out.push(c); } if (c.childNodes) { walk(c); } } }; walk(document.getElementById('context-menu')); return out; };
   check('the inbox shows its tabs', tabBar().hidden === false && tabBar().children.length === 5, tabBar().hidden + ' ' + tabBar().children.length);
   check('Primary is the open tab', tabBar().children[0].className.indexOf('on') >= 0 && tabBar().children[0].textContent.indexOf('Primary') === 0, tabBar().children[0].textContent);
   check('and carries the unread count', tabBar().children[0].textContent === 'Primary2', tabBar().children[0].textContent);
   rows()[1].dispatchEvent(makeEvent('contextmenu', { clientX: 300, clientY: 200 }));
   const tabItems = contextItems('data-act', 'tab');
   const socialItem = tabItems.filter((b) => b.getAttribute('data-tab') === 'social')[0];
   check('the row menu offers the other four tabs', tabItems.length === 4 && !!socialItem, tabItems.length + ' items');
   const beforeTab = requests.length;
   socialItem.dispatchEvent(makeEvent('click', { target: socialItem }));
   await flush();
   await flush();
   const prefCall = since(beforeTab).filter((r) => r.method === 'PUT' && r.path === '/api/v1/me/preferences')[0];
   check('the sender\'s tab is kept with the account\'s preferences', !!prefCall && JSON.parse(JSON.parse(prefCall.body).tabs_by_sender)['b@example.net'] === 'social', prefCall ? prefCall.body : 'no preferences call');
   check('the row leaves Primary', rows().length === 2, rows().length + ' rows');
   check('and Social counts it', tabBar().children[1].textContent === 'Social1', tabBar().children[1].textContent);
   check('the toast offers Undo', toasts.textContent.indexOf('b@example.net') >= 0 && toasts.textContent.indexOf('Undo') >= 0, toasts.textContent);
   tabBar().children[1].dispatchEvent(makeEvent('click'));
   check('the Social tab shows it', rows().length === 1 && rows()[0].textContent.indexOf('Second') >= 0, rows().length + ' rows');
   rows()[0].dispatchEvent(makeEvent('contextmenu', { clientX: 300, clientY: 200 }));
   const primaryItem = contextItems('data-act', 'tab').filter((b) => b.getAttribute('data-tab') === 'primary')[0];
   primaryItem.dispatchEvent(makeEvent('click', { target: primaryItem }));
   await flush();
   await flush();
   check('sent back, the tab is empty and says so', rows().length === 1 && rows()[0].textContent.indexOf('Nothing under Social') >= 0, rows()[0].textContent);
   tabBar().children[0].dispatchEvent(makeEvent('click'));
   check('and Primary has its three rows again', rows().length === 3, rows().length + ' rows');
   dismissAllToasts();
   while (onCursor() < 1) { document.dispatchEvent(makeEvent('keydown', { key: 'j', target: document.body })); }
   while (onCursor() > 1) { document.dispatchEvent(makeEvent('keydown', { key: 'k', target: document.body })); }

   // ---- mute: the conversation is archived, its replies will be filed by a rule, and Undo takes it all back
   rows()[1].dispatchEvent(makeEvent('contextmenu', { clientX: 300, clientY: 200 }));
   const muteItem = contextItems('data-act', 'mute')[0];
   check('the row menu offers Mute', !!muteItem && muteItem.textContent === 'Mute', muteItem ? muteItem.textContent : 'no mute item');
   const beforeMute = requests.length;
   muteItem.dispatchEvent(makeEvent('click', { target: muteItem }));
   await flush();
   await flush();
   await flush();
   const muteFlag = since(beforeMute).filter((r) => r.method === 'PUT' && /\/messages\/102\/flags$/.test(r.path))[0];
   check('Mute marks the message', !!muteFlag && JSON.stringify(JSON.parse(muteFlag.body).keywords_add) === '["$Muted"]', muteFlag ? muteFlag.body : 'no flags call');
   check('after reading the rules the server has', called(beforeMute, 'GET', '/api/v1/me/filters'));
   const muteRule = since(beforeMute).filter((r) => r.method === 'PUT' && r.path === '/api/v1/me/filters')[0];
   const muteScript = muteRule ? JSON.parse(muteRule.body).script : '';
   check('and writes a rule that files its replies into the archive folder',
      muteScript.indexOf('header :contains ["references", "in-reply-to"] "<one@example.net>"') >= 0 && muteScript.indexOf('fileinto "Archive"') >= 0, muteScript.slice(0, 300));
   const muteMove = since(beforeMute).filter((r) => r.method === 'POST' && /\/messages\/102\/move$/.test(r.path))[0];
   check('and archives the conversation', !!muteMove && JSON.parse(muteMove.body).to === 'archive', muteMove ? muteMove.body : 'no move');
   check('the toast says so and offers Undo', toasts.textContent.indexOf('Muted') >= 0 && toasts.textContent.indexOf('Undo') >= 0, toasts.textContent);
   const beforeUnmute = requests.length;
   clickUndo();
   await flush();
   await flush();
   await flush();
   const rulesBack = since(beforeUnmute).filter((r) => r.method === 'PUT' && r.path === '/api/v1/me/filters')[0];
   check('Undo takes the rule away', !!rulesBack && JSON.parse(rulesBack.body).script === '', rulesBack ? rulesBack.body : 'no filters call');
   const moveBack = since(beforeUnmute).filter((r) => r.method === 'POST' && /\/messages\/202\/move$/.test(r.path))[0];
   check('moves the message back by the id the archive gave it', !!moveBack && JSON.parse(moveBack.body).folder_id === 1, moveBack ? moveBack.body : 'no move back');
   const unmark = since(beforeUnmute).filter((r) => r.method === 'PUT' && /\/messages\/202\/flags$/.test(r.path))[0];
   check('and takes the mark off it', !!unmark && JSON.stringify(JSON.parse(unmark.body).keywords_remove) === '["$Muted"]', unmark ? unmark.body : 'no flags call');
   dismissAllToasts();

   // ---- select all: the page first, then the whole folder, read page by page
   document.getElementById('select-all').checked = true;
   document.getElementById('select-all').dispatchEvent(makeEvent('click'));
   const note = document.getElementById('select-note');
   check('ticking every row on the page offers the rest of the folder', note.hidden === false && note.textContent.indexOf('Select every message in Primary') >= 0, note.hidden + ' ' + note.textContent);
   document.getElementById('select-folder').dispatchEvent(makeEvent('click'));
   check('and says the whole folder is selected', document.getElementById('bulk-count').textContent === 'All of Primary selected' && note.textContent.indexOf('Clear selection') >= 0,
      document.getElementById('bulk-count').textContent + ' / ' + note.textContent);
   const beforeSweep = requests.length;
   document.getElementById('bulk-read').dispatchEvent(makeEvent('click'));
   await flush();
   await flush();
   await flush();
   const pagedCalls = since(beforeSweep).filter((r) => r.method === 'GET' && /\/folders\/1\/messages\?limit=200/.test(r.path));
   check('the folder is read page by page, from the uid the first page ended on', pagedCalls.length === 2 && /before_uid=11$/.test(pagedCalls[1].path), pagedCalls.map((r) => r.path).join(' '));
   const flaggedIds = since(beforeSweep).filter((r) => r.method === 'PUT' && /\/flags$/.test(r.path)).map((r) => r.path.match(/messages\/(\d+)\//)[1]);
   check('and every message of it is acted on, not only the page', flaggedIds.join(',') === '103,102,101,100,99', flaggedIds.join(','));
   check('the selection is cleared after', document.getElementById('bulk-bar').hidden === true && note.hidden === true);
   while (onCursor() < 1) { document.dispatchEvent(makeEvent('keydown', { key: 'j', target: document.body })); }
   while (onCursor() > 1) { document.dispatchEvent(makeEvent('keydown', { key: 'k', target: document.body })); }

   // ---- a row dragged onto a folder moves there, with undo
   const sentButton = (() => { const n = document.getElementById('folder-nav').children; for (let i = 0; i < n.length; i++) { if (n[i].getAttribute && n[i].getAttribute('data-route') === '/f/2') { return n[i]; } } return null; })();
   const beforeDrop = requests.length;
   rows()[0].dispatchEvent(makeEvent('dragstart'));
   sentButton.dispatchEvent(makeEvent('dragover'));
   sentButton.dispatchEvent(makeEvent('drop'));
   await flush();
   await flush();
   const dropCall = since(beforeDrop).filter((r) => r.method === 'POST' && /\/messages\/103\/move$/.test(r.path))[0];
   check('a row dropped on a folder is moved there', !!dropCall && JSON.parse(dropCall.body).folder_id === 2, dropCall ? dropCall.body : 'no move');
   check('and the toast offers Undo', document.getElementById('toasts').textContent.indexOf('Undo') >= 0, document.getElementById('toasts').textContent);
   { const toastBox = document.getElementById('toasts'); const buttons = []; (function walk(n) { for (let i = 0; i < n.childNodes.length; i++) { const c = n.childNodes[i]; if (c.tagName === 'BUTTON' && c.textContent === 'Undo') { buttons.push(c); } walk(c); } })(toastBox); if (buttons.length) { buttons[0].dispatchEvent(makeEvent('click')); } }
   await flush();
   await flush();
   while (onCursor() < 1) { document.dispatchEvent(makeEvent('keydown', { key: 'j', target: document.body })); }
   while (onCursor() > 1) { document.dispatchEvent(makeEvent('keydown', { key: 'k', target: document.body })); }

   // ---- Enter opens the message under the cursor, and it has an address
   const beforeOpen = requests.length;
   document.dispatchEvent(makeEvent('keydown', { key: 'Enter', target: document.body }));
   await flush();
   check('Enter opens the message under the cursor', location.hash === '#/m/102', location.hash);
   check('which is read from the server', called(beforeOpen, 'GET', '/api/v1/me/messages/102'));
   check('the message is shown beside the list, in the reading pane', document.getElementById('message-view').hidden === false &&
      document.getElementById('message-list').hidden === false && document.getElementById('mail-section').className.indexOf('has-message') >= 0,
      document.getElementById('mail-section').className);
   check('an unread message is marked read on opening', called(beforeOpen, 'PUT', /\/messages\/102\/flags$/));
   check('the attachment names its type', document.getElementById('message-attachments').textContent.indexOf('image/png') >= 0,
      document.getElementById('message-attachments').textContent);

   // ---- the HTML part is the server's document, in a frame with a policy of its own
   document.getElementById('message-html-toggle').dispatchEvent(makeEvent('click'));
   await flush();
   const frame = document.getElementById('message-html');
   const src = String(frame.getAttribute('src') || '');
   check('the HTML part is shown in the frame', frame.hidden === false && src.length > 0, src);
   check('the frame points at the message\'s own document on this server, without remote content',
      src === '/api/v1/me/messages/102/html', src);
   check('nothing is built here: the frame carries no srcdoc', !frame.srcdoc, String(frame.srcdoc || ''));
   check('the page fetched no attachment for it - the server inlines what the document embeds',
      !called(beforeOpen, 'GET', '/api/v1/me/messages/102/attachments/1'));
   check('a message that names remote content shows the note while the reader has not allowed it',
      document.getElementById('message-remote').hidden === false);
   document.getElementById('remote-once').dispatchEvent(makeEvent('click'));
   await flush();
   check('allowing it for this message points the frame at the document with its remote content',
      String(frame.getAttribute('src') || '') === '/api/v1/me/messages/102/html?remote=1', String(frame.getAttribute('src') || ''));
   check('and the note goes', document.getElementById('message-remote').hidden === true);
   check('the frame is still sandboxed', frame.getAttribute('sandbox') === 'allow-popups allow-popups-to-escape-sandbox',
      frame.getAttribute('sandbox'));

   // ---- Reply is an address of its own
   document.getElementById('message-reply').dispatchEvent(makeEvent('click'));
   await flush();
   check('Reply is an address', location.hash === '#/compose?reply=102', location.hash);
   check('the reply is addressed to the sender', document.getElementById('compose-to').value === 'b@example.net',
      document.getElementById('compose-to').value);
   check('and its subject answers the original', document.getElementById('compose-subject').value === 'Re: Second',
      document.getElementById('compose-subject').value);
   check('the reply is written under the message, over the list', document.getElementById('compose-section').hidden === false &&
      document.getElementById('mail-section').hidden === false && document.getElementById('compose-section').className.indexOf('inline') >= 0,
      document.getElementById('compose-section').className);

   // ---- Back goes back, and lands on the row it left
   history.back();
   await flush();
   check('Back returns to the message', location.hash === '#/m/102', location.hash);
   history.back();
   await flush();
   check('Back again returns to the folder', location.hash === '#/f/1', location.hash);
   check('the listing is shown again', document.getElementById('message-list').hidden === false &&
      document.getElementById('message-view').hidden === true);
   check('and the cursor is back on the row it was on', onCursor() === 1, 'at ' + onCursor());

   // ---- the change probe
   check('the first probe asked without a token', called(0, 'GET', '/api/v1/me/changes'));
   const beforeProbe = requests.length;
   check('the probe is waiting on a timer', timers.length === 1, timers.length + ' timers');
   fireTimers();
   await flush();
   check('the next probe carries the token the last answer gave',
      called(beforeProbe, 'GET', /\/api\/v1\/me\/changes\?since=t1$/), JSON.stringify(since(beforeProbe).map((r) => r.path)));
   check('a change makes the folder list and the listing be read again',
      called(beforeProbe, 'GET', '/api/v1/me/folders') && called(beforeProbe, 'GET', /\/folders\/1\/messages/),
      JSON.stringify(since(beforeProbe).map((r) => r.path)));
   check('the unseen count on screen follows it', nav.children[0].children[2].textContent === '3',
      nav.children[0].children[2].textContent);
   check('the message that arrived is in the listing', rows().length === 4 &&
      rows()[0].textContent.indexOf('Just in') >= 0, rows().length + ' rows');
   check('and the cursor is still on the message it was on, which has moved down',
      onCursor() === 2 && rows()[2].textContent.indexOf('Second') >= 0,
      'at ' + onCursor() + ' which is ' + (rows()[onCursor()] ? rows()[onCursor()].textContent.slice(0, 20) : 'nothing'));

   // ---- and it stops while the tab is in the background
   const beforeHidden = requests.length;
   document.hidden = true;
   document.dispatchEvent(makeEvent('visibilitychange'));
   fireTimers();
   await flush();
   check('nothing is polled while the tab is hidden', !called(beforeHidden, 'GET', /changes/),
      JSON.stringify(since(beforeHidden).map((r) => r.path)));
   check('and the reader is told why', document.getElementById('probe-state').textContent.indexOf('background') >= 0,
      document.getElementById('probe-state').textContent);
   document.hidden = false;
   document.dispatchEvent(makeEvent('visibilitychange'));
   await flush();
   check('it starts again when the tab comes back', called(beforeHidden, 'GET', /changes/));

   // ---- a server with no change route at all is asked once, not for ever
   changesMissing = true;
   const beforeGone = requests.length;
   fireTimers();
   await flush();
   check('a server with no change route is asked once more', called(beforeGone, 'GET', /changes/));
   check('and then not again', timers.length === 0, timers.length + ' timers still armed');
   check('and the reader is told', document.getElementById('probe-state').textContent.indexOf('does not report') >= 0,
      document.getElementById('probe-state').textContent);
   changesMissing = false;

   // ---- folder management
   const viewNav = document.getElementById('view-nav').children;
   const foldersButton = viewNav.filter((b) => b.getAttribute('data-route') === '/folders')[0];
   foldersButton.dispatchEvent(makeEvent('click'));
   await flush();
   check('managing folders is an address', location.hash === '#/folders', location.hash);
   const admin = document.getElementById('folder-admin-list');
   check('every folder is listed to be managed', admin.children.length === 3, admin.children.length + ' rows');

   const beforeCreate = requests.length;
   document.getElementById('folder-new-name').value = 'Bills';
   document.getElementById('folder-new-parent').value = '1';
   document.getElementById('folder-new-form').dispatchEvent(makeEvent('submit'));
   await flush();
   const create = since(beforeCreate).filter((r) => r.method === 'POST' && r.path === '/api/v1/me/folders')[0];
   check('creating a folder inside another posts the whole path, in the delimiter the server named',
      !!create && JSON.parse(create.body).name === 'INBOX.Bills', create ? create.body : 'no POST');
   check('and the folder tree is read again', called(beforeCreate, 'GET', '/api/v1/me/folders'));

   // The inbox and a folder the server keeps for a purpose cannot be deleted,
   // so the button is not there to be pressed.
   const adminRows = document.getElementById('folder-admin-list').children;
   const labels = (row) => row.children.map((c) => c.textContent);
   check('the inbox offers neither rename nor delete',
      labels(adminRows[0]).indexOf('Delete') < 0 && labels(adminRows[0]).indexOf('Rename') < 0, JSON.stringify(labels(adminRows[0])));
   const sentRow = adminRows.filter((r) => r.textContent.indexOf('Sent') >= 0)[0];
   check('a folder the server keeps for a special use may be renamed but not deleted',
      !!sentRow && labels(sentRow).indexOf('Delete') < 0 && labels(sentRow).indexOf('Rename') >= 0,
      sentRow ? JSON.stringify(labels(sentRow)) : 'no Sent row');
   check('and it says what it is kept for', !!sentRow && sentRow.textContent.indexOf('kept for') >= 0,
      sentRow ? sentRow.textContent : '');

   // A rename the server refuses shows the server's own sentence.
   renameRefusal = 'A folder called Work is already there.';
   const workAt = () => { const rows = document.getElementById('folder-admin-list').children; let at = -1; rows.forEach((r, i) => { if (r.textContent.indexOf('Work') >= 0) { at = i; } }); return at; };
   const at = workAt();
   const row = document.getElementById('folder-admin-list').children[at];
   row.children.filter((c) => c.textContent === 'Rename')[0].dispatchEvent(makeEvent('click'));
   // The row is now the editor, so it no longer carries the folder name.
   const editing = document.getElementById('folder-admin-list').children[at];
   const field = editing.children.filter((c) => c.tagName === 'INPUT')[0];
   check('renaming shows the whole path, because a rename may move the folder', field.value === 'INBOX.Work', field.value);
   field.value = 'INBOX.Work.2026';
   const beforeRename = requests.length;
   editing.children.filter((c) => c.textContent === 'Save')[0].dispatchEvent(makeEvent('click'));
   await flush();
   const rename = since(beforeRename).filter((r) => r.method === 'PUT')[0];
   check('renaming a folder puts the whole path', !!rename && /\/api\/v1\/me\/folders\/\d+$/.test(rename.path) &&
      JSON.parse(rename.body).name === 'INBOX.Work.2026', rename ? rename.method + ' ' + rename.path + ' ' + rename.body : 'no PUT');
   check("the server's own refusal is what the reader is shown",
      document.getElementById('folder-admin-status').textContent === renameRefusal,
      document.getElementById('folder-admin-status').textContent);
   renameRefusal = null;

   // Deleting takes two presses, and the second one is the one that deletes.
   const doomed = document.getElementById('folder-admin-list').children[at];
   const remove = doomed.children.filter((c) => c.textContent === 'Delete')[0];
   const beforeDelete = requests.length;
   remove.dispatchEvent(makeEvent('click'));
   await flush();
   check('one press on Delete asks rather than deletes', !called(beforeDelete, 'DELETE', /folders/) &&
      remove.textContent === 'Really delete?', remove.textContent);
   remove.dispatchEvent(makeEvent('click'));
   await flush();
   check('the second press deletes it', called(beforeDelete, 'DELETE', /^\/api\/v1\/me\/folders\/\d+$/));

   // ---- searching has an address of its own
   document.getElementById('mail-search').value = 'invoice';
   document.getElementById('mail-search-everywhere').checked = true;
   document.getElementById('mail-search-form').dispatchEvent(makeEvent('submit'));
   await flush();
   check('a search across every folder is an address', location.hash === '#/search?q=invoice', location.hash);
   check('and it is what was asked of the server', called(requests.length - 3, 'GET', /\/api\/v1\/me\/search\?q=invoice/),
      JSON.stringify(requests.slice(-3).map((r) => r.path)));

   // ---- the attachment reminder: a message that promises an attachment and carries none is questioned once
   const compose = document.getElementById('compose-section');
   // The window opens on the navigation's microtask, and opening blanks the
   // form, so the fields are filled once that has happened.
   const writeMessage = async (text) => {
      document.dispatchEvent(makeEvent('keydown', { key: 'c', target: document.body }));
      await flush();
      document.getElementById('compose-to').value = 'a@example.net';
      document.getElementById('compose-subject').value = 'The report';
      document.getElementById('compose-text').value = text;
   };
   await writeMessage('Please see the attached report.\n> You said you would attach it.');
   check('c opens the compose window', location.hash === '#/compose' && compose.hidden === false, location.hash + ' hidden=' + compose.hidden);
   confirmAnswer = false;
   const beforeAsk = requests.length;
   document.getElementById('compose-form').dispatchEvent(makeEvent('submit'));
   await flush();
   check('a body that says "attached" with nothing attached is questioned', confirms.length === 1 && confirms[0].indexOf('nothing is attached') >= 0, JSON.stringify(confirms));
   check('Cancel sends nothing', !called(beforeAsk, 'POST', '/api/v1/me/messages') && timers.length === 0, timers.length + ' timers');
   check('and leaves the message in the open form', compose.hidden === false && document.getElementById('compose-text').value.indexOf('attached report') >= 0, 'hidden=' + compose.hidden);
   confirmAnswer = true;
   document.getElementById('compose-form').dispatchEvent(makeEvent('submit'));
   await flush();
   check('OK is asked once more and lets it go', confirms.length === 2 && timers.length > 0, confirms.length + ' questions, ' + timers.length + ' timers');
   fireTimers();
   await flush();
   await flush();
   check('and the message is sent after the undo delay', called(beforeAsk, 'POST', '/api/v1/me/messages'), JSON.stringify(since(beforeAsk).map((r) => r.method + ' ' + r.path)));
   check('and the form is closed', compose.hidden === true && document.getElementById('compose-text').value === '', 'hidden=' + compose.hidden);
   dismissAllToasts();
   await writeMessage('Thanks, received.\n> Please find the report attached.\n> It is enclosed as a PDF.');
   const beforeQuoted = requests.length;
   document.getElementById('compose-form').dispatchEvent(makeEvent('submit'));
   await flush();
   fireTimers();
   await flush();
   await flush();
   check('a mention only in the quoted lines is the other person\'s and is not questioned', confirms.length === 2 && called(beforeQuoted, 'POST', '/api/v1/me/messages'), confirms.length + ' questions');
   dismissAllToasts();

   // ---- shift-click and ctrl-click in the list select; a plain click still opens
   nav.children[0].dispatchEvent(makeEvent('click'));
   await flush();
   check('the inbox is listed again, with the message that arrived', location.hash === '#/f/1' && rows().length === 4, location.hash + ' ' + rows().length + ' rows');
   const boxOf = (row) => row.childNodes[0].childNodes[0];
   const ticked = () => rows().filter((r) => r.className.indexOf('selected') >= 0).length;
   const beforeCtrl = requests.length;
   rows()[0].dispatchEvent(makeEvent('click', { ctrlKey: true }));
   check('a ctrl-click ticks the row without opening it', document.getElementById('bulk-count').textContent === '1 selected' && location.hash === '#/f/1' && boxOf(rows()[0]).checked === true,
      document.getElementById('bulk-count').textContent + ' ' + location.hash);
   rows()[2].dispatchEvent(makeEvent('click', { shiftKey: true }));
   check('a shift-click ticks every row from the last one clicked to this one', document.getElementById('bulk-count').textContent === '3 selected' && ticked() === 3 && boxOf(rows()[1]).checked === true,
      document.getElementById('bulk-count').textContent + ', ' + ticked() + ' rows marked');
   rows()[1].dispatchEvent(makeEvent('click', { ctrlKey: true }));
   check('a ctrl-click on a ticked row unticks it', document.getElementById('bulk-count').textContent === '2 selected' && boxOf(rows()[1]).checked === false, document.getElementById('bulk-count').textContent);
   boxOf(rows()[3]).checked = true;
   boxOf(rows()[3]).dispatchEvent(makeEvent('click', { shiftKey: true }));
   check('a shift-click on a box ticks the range from the last row clicked', document.getElementById('bulk-count').textContent === '4 selected' && ticked() === 4, document.getElementById('bulk-count').textContent);
   check('and none of it opened a message or asked the server', location.hash === '#/f/1' && !called(beforeCtrl, 'GET', /\/messages\/\d+$/), JSON.stringify(since(beforeCtrl).map((r) => r.path)));
   rows()[1].dispatchEvent(makeEvent('click'));
   await flush();
   check('a plain click still opens the message', location.hash === '#/m/103' && called(beforeCtrl, 'GET', '/api/v1/me/messages/103'), location.hash);
   document.getElementById('bulk-clear').dispatchEvent(makeEvent('click'));
   check('and the selection can be cleared', document.getElementById('bulk-bar').hidden === true && ticked() === 0, ticked() + ' rows marked');

   // ---- search history: the last ten searches, kept in this browser and offered under the empty box
   const search = document.getElementById('mail-search');
   const historyBox = document.getElementById('search-history');
   const historyList = document.getElementById('search-history-list');
   const searchFor = async (text) => { search.value = text; document.getElementById('mail-search-form').dispatchEvent(makeEvent('submit')); await flush(); };
   check('the search made earlier was kept in this browser', store.get('hmPortalSearches') === '["invoice"]', store.get('hmPortalSearches'));
   await searchFor('from:alice');
   check('a new search goes to the front', store.get('hmPortalSearches') === '["from:alice","invoice"]', store.get('hmPortalSearches'));
   await searchFor('invoice');
   check('a search made again moves to the front rather than doubling', store.get('hmPortalSearches') === '["invoice","from:alice"]', store.get('hmPortalSearches'));
   for (let i = 0; i < 12; i += 1) { await searchFor('word' + i); }
   const kept = JSON.parse(store.get('hmPortalSearches'));
   check('and the list holds the last ten', kept.length === 10 && kept[0] === 'word11' && kept[9] === 'word2', JSON.stringify(kept));
   search.value = 'x';
   search.dispatchEvent(makeEvent('focus'));
   check('nothing is offered under a box with something in it', historyBox.hidden === true);
   const beforeShown = requests.length;
   search.value = '';
   search.dispatchEvent(makeEvent('input'));
   check('emptying the box offers the recent searches, newest first', historyBox.hidden === false && historyList.children.length === 10 && historyList.children[0].textContent === 'word11',
      'hidden=' + historyBox.hidden + ' ' + historyList.children.length + ' entries');
   check('without asking the server', requests.length === beforeShown, JSON.stringify(since(beforeShown).map((r) => r.path)));
   historyList.children[1].dispatchEvent(makeEvent('click'));
   await flush();
   // The folder shown is the scope, as the box's own search would have it: the inbox, since the list before this one opened it.
   check('an entry runs that search again, in the folder shown', location.hash === '#/f/1?q=word10' && called(beforeShown, 'GET', /\/folders\/1\/messages\?.*q=word10/) && historyBox.hidden === true,
      location.hash + ' ' + JSON.stringify(since(beforeShown).map((r) => r.path)));
   search.value = '';
   search.dispatchEvent(makeEvent('focus'));
   check('the box focused and empty offers them', historyBox.hidden === false && historyList.children[0].textContent === 'word10', historyList.children[0].textContent);
   search.dispatchEvent(makeEvent('keydown', { key: 'ArrowDown', target: search }));
   check('the arrow key moves into the list', document.activeElement === historyList.children[0]);
   historyList.children[0].dispatchEvent(makeEvent('keydown', { key: 'ArrowDown', target: historyList.children[0] }));
   check('and down it', document.activeElement === historyList.children[1]);
   historyList.children[1].dispatchEvent(makeEvent('keydown', { key: 'Escape', target: historyList.children[1] }));
   check('Escape closes it and returns to the box', historyBox.hidden === true && document.activeElement === search);
   search.dispatchEvent(makeEvent('focus'));
   document.getElementById('search-history-clear').dispatchEvent(makeEvent('click'));
   check('Clear the list empties it', !store.has('hmPortalSearches') && historyBox.hidden === true, String(store.get('hmPortalSearches')));
   search.dispatchEvent(makeEvent('focus'));
   check('and nothing is offered after', historyBox.hidden === true);
   await searchFor('after:2026-09-01');
   check('a search after that starts the list again', store.get('hmPortalSearches') === '["after:2026-09-01"]', store.get('hmPortalSearches'));

   // ---- search suggestions: a contact's name, typed in the box, completes to from:<address>
   const suggestBox = document.getElementById('search-suggest');
   const suggestList = document.getElementById('search-suggest-list');
   const suggestHead = document.getElementById('search-suggest-head');
   const typeSearch = async (text) => { search.value = text; search.dispatchEvent(makeEvent('input')); fireTimers(); await flush(); };
   const beforeSuggest = requests.length;
   await typeSearch('a');
   check('one letter in the box asks nothing of the address book', !called(beforeSuggest, 'GET', /contacts/) && suggestBox.hidden === true,
      JSON.stringify(since(beforeSuggest).map((r) => r.path)));
   await typeSearch('ali');
   check('a name typed in the box asks the address book for it, as the To field does', called(beforeSuggest, 'GET', '/api/v1/me/contacts?limit=8&q=ali'),
      JSON.stringify(since(beforeSuggest).map((r) => r.path)));
   check('and the contact it matches is offered under the box as from:<address>',
      suggestBox.hidden === false && suggestList.children.length === 1 && suggestList.children[0].textContent === 'Alice Examplefrom:alice@example.net' && suggestHead.textContent === 'Search by sender',
      'hidden=' + suggestBox.hidden + ' ' + suggestList.children.length + ' entries, head=' + suggestHead.textContent);
   check('while the history stays closed', historyBox.hidden === true);
   await typeSearch('invoice to:al');
   check('a name after to: completes to to:<address>, and the head says so',
      suggestList.children.length === 2 && suggestList.children[0].textContent === 'Alice Exampleto:alice@example.net' && suggestHead.textContent === 'Search by recipient',
      suggestList.children.length + ' entries, head=' + suggestHead.textContent);
   const beforeOperator = requests.length;
   await typeSearch('is:unread');
   check('an operator is not a name and asks nothing', !called(beforeOperator, 'GET', /contacts/) && suggestBox.hidden === true,
      JSON.stringify(since(beforeOperator).map((r) => r.path)));
   await typeSearch('invoice ali');
   search.dispatchEvent(makeEvent('keydown', { key: 'ArrowDown', target: search }));
   check('the arrow key moves into the suggestions', document.activeElement === suggestList.children[0]);
   suggestList.children[0].dispatchEvent(makeEvent('keydown', { key: 'Escape', target: suggestList.children[0] }));
   check('Escape closes them and returns to the box', suggestBox.hidden === true && document.activeElement === search);
   await typeSearch('invoice ali');
   const beforeTake = requests.length;
   suggestList.children[0].dispatchEvent(makeEvent('click'));
   await flush();
   check('one taken stands in the box as from:<address> beside the words typed, and the search runs in the folder shown',
      search.value === 'invoice from:alice@example.net' && location.hash === '#/f/1?q=' + encodeURIComponent('invoice from:alice@example.net') && called(beforeTake, 'GET', /\/folders\/1\/messages\?.*q=invoice/) && suggestBox.hidden === true,
      'value=' + search.value + ' ' + location.hash + ' ' + JSON.stringify(since(beforeTake).map((r) => r.path)));
   check('and is kept with the recent searches', JSON.parse(store.get('hmPortalSearches'))[0] === 'invoice from:alice@example.net', store.get('hmPortalSearches'));

   // ---- pop-out: the message, or the message being written, in a window of its own
   location.hash = '#/m/102';
   await flush();
   check('the message is open', location.hash === '#/m/102' && document.getElementById('message-view').hidden === false, location.hash);
   document.getElementById('message-popout').dispatchEvent(makeEvent('click'));
   check('the pop-out opens the message at its own address in a new window of a given size',
      opened.length === 1 && opened[0].url === 'http://portal.test/portal#/m/102' && /width=\d+/.test(opened[0].features) && /height=\d+/.test(opened[0].features), JSON.stringify(opened));
   check('and this window stays where it was', location.hash === '#/m/102' && document.getElementById('message-view').hidden === false, location.hash);
   document.dispatchEvent(makeEvent('keydown', { key: 'c', target: document.body }));
   await flush();
   document.getElementById('compose-popout').dispatchEvent(makeEvent('click'));
   await flush();
   check('an empty compose form pops out at its own address', opened.length === 2 && opened[1].url === 'http://portal.test/portal#/compose', JSON.stringify(opened.slice(1)));
   check('and closes here', compose.hidden === true, 'hidden=' + compose.hidden);
   await writeMessage('Half a thought');
   const beforePop = requests.length;
   document.getElementById('compose-popout').dispatchEvent(makeEvent('click'));
   await flush();
   await flush();
   const draftCall = since(beforePop).filter((r) => r.method === 'POST' && r.path === '/api/v1/me/drafts')[0];
   check('a form with something in it is saved as a draft first', !!draftCall && JSON.parse(draftCall.body).text === 'Half a thought', draftCall ? draftCall.body : 'no draft saved');
   check('and the new window opens on that draft', opened.length === 3 && opened[2].url === 'http://portal.test/portal#/compose?draft=55', JSON.stringify(opened.slice(2)));
   check('and the form here is closed and blank, so one window edits the draft', compose.hidden === true && document.getElementById('compose-text').value === '', 'hidden=' + compose.hidden + ' text=' + document.getElementById('compose-text').value);

   // ---- follow-up flags: a flag with a date, the badge on the row, the Starred view by due date
   location.hash = '#/f/1';
   await flush();
   const dueOfRow = (row) => { let found = null; (function walk(n) { for (let i = 0; i < n.childNodes.length; i++) { const c = n.childNodes[i]; if (c.className && c.className.indexOf('due') === 0) { found = c; } if (c.childNodes) { walk(c); } } })(row); return found; };
   const rowBySubject = (subject) => rows().filter((r) => r.textContent.indexOf(subject) >= 0)[0];
   check('a row due today says so', !!dueOfRow(rowBySubject('First')) && dueOfRow(rowBySubject('First')).textContent === 'Due today' && dueOfRow(rowBySubject('First')).className.indexOf('now') >= 0,
      dueOfRow(rowBySubject('First')) ? dueOfRow(rowBySubject('First')).textContent : 'no badge');
   check('and a row with no follow-up carries no badge', !dueOfRow(rowBySubject('Second')));
   location.hash = '#/m/102';
   await flush();
   const followBtn = document.getElementById('message-followup');
   const followMenu = document.getElementById('followup-menu');
   check('the open message offers Follow up, off', !followBtn.classList.contains('on') && followMenu.hidden === true && document.getElementById('followup-clear').hidden === true,
      followBtn.className + ' menu hidden=' + followMenu.hidden);
   followBtn.dispatchEvent(makeEvent('click'));
   check('the button opens the menu', followMenu.hidden === false);
   const beforeFollow = requests.length;
   document.getElementById('followup-tomorrow').dispatchEvent(makeEvent('click'));
   await flush();
   const tomorrowStamp = (() => { const d = new Date(); d.setDate(d.getDate() + 1); return d.getFullYear() + '-' + String(d.getMonth() + 1).padStart(2, '0') + '-' + String(d.getDate()).padStart(2, '0'); })();
   const followCall = since(beforeFollow).filter((r) => r.method === 'PUT' && r.path === '/api/v1/me/messages/102/flags')[0];
   check('tomorrow stars the message and adds the $FollowUp and $Due- keywords in one change',
      !!followCall && JSON.parse(followCall.body).flagged === true && JSON.stringify(JSON.parse(followCall.body).keywords_add) === JSON.stringify(['$FollowUp', '$Due-' + tomorrowStamp]),
      followCall ? followCall.body : 'no flags call');
   check('the button shows it on, the menu closes and Clear the flag is offered', followBtn.classList.contains('on') && followMenu.hidden === true && document.getElementById('followup-clear').hidden === false, followBtn.className);
   check('and the row carries the day', !!dueOfRow(rowBySubject('Second')) && dueOfRow(rowBySubject('Second')).getAttribute('title') === tomorrowStamp,
      dueOfRow(rowBySubject('Second')) ? dueOfRow(rowBySubject('Second')).textContent : 'no badge');
   dismissAllToasts();
   location.hash = '#/starred';
   await flush();
   // The starred messages, the dated ones first and the soonest at the top, the undated after them.
   check('the Starred view lists the follow-ups by due date, the soonest first', rows().length >= 2 && rows()[0].textContent.indexOf('First') >= 0 && rows()[1].textContent.indexOf('Second') >= 0 && rows().slice(2).every((r) => !dueOfRow(r)),
      rows().map((r) => r.textContent.slice(0, 40)).join(' | '));
   location.hash = '#/m/102';
   await flush();
   const beforeClear = requests.length;
   document.getElementById('followup-clear').dispatchEvent(makeEvent('click'));
   await flush();
   const clearCall = since(beforeClear).filter((r) => r.method === 'PUT' && r.path === '/api/v1/me/messages/102/flags')[0];
   check('Clear the flag unstars the message and takes both keywords off',
      !!clearCall && JSON.parse(clearCall.body).flagged === false && JSON.stringify(JSON.parse(clearCall.body).keywords_remove) === JSON.stringify(['$Due-' + tomorrowStamp, '$FollowUp']),
      clearCall ? clearCall.body : 'no flags call');
   check('and the button is off again', !followBtn.classList.contains('on') && document.getElementById('followup-clear').hidden === true, followBtn.className + ' clear hidden=' + document.getElementById('followup-clear').hidden);
   dismissAllToasts();
   // The reminder's Open: announced again on a fresh sign-in, since what was announced went with the account.
   document.getElementById('signout').dispatchEvent(makeEvent('click'));
   await flush();
   check('sign-out forgets which follow-ups were announced', !store.has('hmPortalDueShown') && gate.hidden === false, String(store.get('hmPortalDueShown')));
   document.getElementById('address').value = 'user@example.com';
   document.getElementById('password').value = 'a-real-password';
   const beforeAgain = requests.length;
   document.getElementById('signin-form').dispatchEvent(makeEvent('submit'));
   await flush();
   check('and the next sign-in announces the one due today again', app.hidden === false && toastBox.textContent.indexOf('Due for follow-up: First') >= 0, toastBox.textContent);
   { const buttons = []; (function walk(n) { for (let i = 0; i < n.childNodes.length; i++) { const c = n.childNodes[i]; if (c.tagName === 'BUTTON' && c.textContent === 'Open') { buttons.push(c); } if (c.childNodes) { walk(c); } } })(toastBox); if (buttons.length) { buttons[0].dispatchEvent(makeEvent('click')); } }
   await flush();
   check('Open opens the message', location.hash === '#/m/101' && called(beforeAgain, 'GET', '/api/v1/me/messages/101') && toastBox.children.length === 0, location.hash + ' ' + toastBox.children.length + ' toasts');
   document.getElementById('message-back').dispatchEvent(makeEvent('click'));
   await flush();

   // ---- the theme is a choice the browser keeps, and it is not a secret
   document.getElementById('theme-btn').dispatchEvent(makeEvent('click'));
   check('the theme can be turned over', document.body.getAttribute('data-theme') === 'light',
      document.body.getAttribute('data-theme'));
   check('and is remembered', store.get('hmPortalTheme') === 'light');

   // ---- signing out ends the session and stops the probe
   const beforeOut = requests.length;
   document.getElementById('signout').dispatchEvent(makeEvent('click'));
   await flush();
   check('signing out ends the session', called(beforeOut, 'DELETE', '/api/v1/session'));
   check('and shows the sign-in form again', gate.hidden === false && app.hidden === true);
   const stillPolling = requests.length;
   fireTimers();
   await flush();
   check('nothing is polled once signed out', !called(stillPolling, 'GET', /changes/),
      JSON.stringify(since(stillPolling).map((r) => r.path)));

   // ---- and it forgets the mailbox, which is the half that matters on a
   // shared browser. The page renders an open message and a listing from
   // memory BEFORE it asks the server, so anything left here is shown to
   // whoever signs in next, without a request that could have been refused.
   check('the message pane is emptied', document.getElementById('message-text').innerHTML === '',
      document.getElementById('message-text').innerHTML.slice(0, 80));
   check('the message list is emptied', document.getElementById('message-list').innerHTML === '',
      document.getElementById('message-list').innerHTML.slice(0, 80));
   check('the folder tree is emptied', document.getElementById('folder-nav').innerHTML === '',
      document.getElementById('folder-nav').innerHTML.slice(0, 80));
   check('the attachment list is emptied',
      document.getElementById('message-attachments').innerHTML === '',
      document.getElementById('message-attachments').innerHTML.slice(0, 80));
   check('the html frame keeps no document', !document.getElementById('message-html').getAttribute('srcdoc'),
      String(document.getElementById('message-html').getAttribute('srcdoc')).slice(0, 80));
   check('the address bar names no message or folder', location.hash === '' || location.hash === '#/',
      location.hash);
   check('the draft is blank again', document.getElementById('compose-to').value === '',
      document.getElementById('compose-to').value);
   check('the recent searches go with the account', !store.has('hmPortalSearches'), String(store.get('hmPortalSearches')));

   if (failures.length) {
      console.error('\nportal script: ' + failures.length + ' of ' + checks + ' checks failed\n');
      failures.forEach((f) => console.error('  FAILED: ' + f));
      console.error('');
      process.exit(1);
   }
   console.log('portal script: ' + checks + ' checks passed');
}

main().catch((why) => {
   console.error('the portal script did not survive being run:');
   console.error(why && why.stack ? why.stack : why);
   process.exit(1);
});
