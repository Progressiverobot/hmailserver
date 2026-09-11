// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// The portal's script, executed. build/check-portal-script.py recovers the page
// and the script from the C++ literals in RestApiPortal.cpp and hands them here;
// this builds a small DOM from the markup, answers fetch out of a table of
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

const LISTING = {
   total: 3,
   messages: [
      { id: 103, uid: 13, subject: 'Third', from: 'c@example.net', to: 'user@example.com', date: '2026-09-09 09:00', size: 900, flags: { seen: false, flagged: false, draft: false } },
      { id: 102, uid: 12, subject: 'Second', from: 'b@example.net', to: 'user@example.com', date: '2026-09-08 09:00', size: 800, flags: { seen: false, flagged: false, draft: false }, in_reply_to: '<one@example.net>' },
      { id: 101, uid: 11, subject: 'First', from: 'a@example.net', to: 'user@example.com', date: '2026-09-07 09:00', size: 700, flags: { seen: true, flagged: false, draft: false } }
   ]
};

const ARRIVAL = { id: 104, uid: 14, subject: 'Just in', from: 'd@example.net', to: 'user@example.com', date: '2026-09-09 12:00', size: 400, flags: { seen: false, flagged: false, draft: false } };

const WITH_IMAGE = {
   id: 102, folder_id: 1, uid: 12, subject: 'Second', from: 'Bee <b@example.net>', to: 'user@example.com', cc: '',
   date: '2026-09-08 09:00', size: 800, message_id: '<two@example.net>', references: '', in_reply_to: '<one@example.net>',
   text: '', html: '<p>Hello</p><img src="cid:logo@example.net" alt="logo"><img src="cid:chart@example.net" alt="chart"><img src="https://tracker.example.org/pixel.gif">',
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

function answer(method, path) {
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
   if (path === '/api/v1/me/filters' && method === 'GET') { return json(200, { active: '' }); }
   if (path.startsWith('/api/v1/me/folders/1/messages')) {
      if (!arrived) { return json(200, LISTING); }
      return json(200, { total: 4, messages: [ARRIVAL].concat(LISTING.messages) });
   }
   if (/^\/api\/v1\/me\/folders\/\d+\/messages/.test(path)) { return json(200, { total: 0, messages: [] }); }
   if (/^\/api\/v1\/me\/messages\/102\/attachments\/(\d+)$/.test(path)) {
      const index = Number(path.split('/').pop());
      // The download route's own content type, which is what decides whether
      // the page may show it: an SVG comes back as a file, on purpose.
      const types = { 0: 'text/plain', 1: 'image/png', 2: 'application/octet-stream' };
      return { status: 200, body: 'PNGBYTES', headers: { 'Content-Type': types[index] || 'application/octet-stream' } };
   }
   if (path === '/api/v1/me/messages/102' && method === 'GET') { return json(200, JSON.parse(JSON.stringify(WITH_IMAGE))); }
   if (/^\/api\/v1\/me\/messages\/\d+$/.test(path) && method === 'GET') {
      return json(200, Object.assign(JSON.parse(JSON.stringify(WITH_IMAGE)), { id: Number(path.split('/').pop()), html: '', text: 'plain' }));
   }
   if (/\/flags$/.test(path)) { return json(200, { flags: { seen: true, flagged: false, draft: false } }); }
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
   return json(404, { error: 'No such route: ' + path });
}

function fetchStub(path, options) {
   const method = (options && options.method) || 'GET';
   const headers = (options && options.headers) || {};
   requests.push({ method, path, headers, body: options && options.body });
   const reply = answer(method, path);
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

   // ---- the address the reader arrived at is the one that is restored
   check('a reload lands where the reader was', document.getElementById('settings-section').hidden === false,
      'hash=' + location.hash + ' title=' + document.getElementById('view-title').textContent);
   check('the sign-in did not lose the address', location.hash === '#/settings', location.hash);

   // ---- the app shell: folders in the sidebar, with their unseen counts
   const nav = document.getElementById('folder-nav');
   check('every folder is in the sidebar', nav.children.length === 3, nav.children.length + ' entries');
   check('a subfolder is under its parent', nav.children[1].getAttribute('data-route') === '/f/3', nav.children[1].getAttribute('data-route'));
   check('the unseen count is shown', nav.children[0].children[2].textContent === '2', nav.children[0].children[2].textContent);

   // ---- a folder has an address, and clicking one goes to it
   nav.children[0].dispatchEvent(makeEvent('click'));
   await flush();
   check('a folder is an address', location.hash === '#/f/1', location.hash);
   check('the folder listing renders one row per message', document.getElementById('message-list').children.length === 3,
      document.getElementById('message-list').children.length + ' rows');
   check('an unseen message is marked', document.getElementById('message-list').children[0].className.indexOf('unseen') >= 0,
      document.getElementById('message-list').children[0].className);
   check('a reply is set in from the left', document.getElementById('message-list').children[1].className.indexOf('msg-reply') >= 0,
      document.getElementById('message-list').children[1].className);
   check('the view is titled by the folder', document.getElementById('view-title').textContent === 'INBOX',
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

   // ---- Enter opens the message under the cursor, and it has an address
   const beforeOpen = requests.length;
   document.dispatchEvent(makeEvent('keydown', { key: 'Enter', target: document.body }));
   await flush();
   check('Enter opens the message under the cursor', location.hash === '#/m/102', location.hash);
   check('which is read from the server', called(beforeOpen, 'GET', '/api/v1/me/messages/102'));
   check('the message is shown and the list is not', document.getElementById('message-view').hidden === false &&
      document.getElementById('message-list').hidden === true);
   check('an unread message is marked read on opening', called(beforeOpen, 'PUT', /\/messages\/102\/flags$/));
   check('the attachment names its type', document.getElementById('message-attachments').textContent.indexOf('image/png') >= 0,
      document.getElementById('message-attachments').textContent);

   // ---- a cid: reference resolves to the attachment download route
   document.getElementById('message-html-toggle').dispatchEvent(makeEvent('click'));
   await flush();
   const frame = document.getElementById('message-html');
   const doc = String(frame.srcdoc || '');
   check('the HTML part is shown in the frame', frame.hidden === false && doc.length > 0);
   check('a cid: image becomes the bytes themselves, fetched by the page',
      doc.indexOf('data:image/png;base64,') >= 0, doc);
   check('and no cid: is left for that one', doc.indexOf('cid:logo') < 0, doc);
   check('the page, not the frame, fetched it', called(beforeOpen, 'GET', '/api/v1/me/messages/102/attachments/1'));
   check('the frame is pointed at no URL on this server at all',
      doc.indexOf('/api/v1/me/messages/') < 0, doc);
   check('a remote image is left as it was, and the policy blocks it',
      doc.indexOf('https://tracker.example.org/pixel.gif') >= 0 && /img-src data:"/.test(doc), doc);
   check('an inline part the download route will not serve as an image is left alone',
      doc.indexOf('cid:chart@example.net') >= 0, doc);
   check('and its bytes were not put in the page either', doc.indexOf('data:application/octet-stream') < 0, doc);
   check('the frame allows no script and no other source', doc.indexOf("default-src 'none'") >= 0);
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
   check('the compose form is the view', document.getElementById('compose-section').hidden === false &&
      document.getElementById('mail-section').hidden === true);

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
