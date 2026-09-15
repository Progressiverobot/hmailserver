// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

'use strict';
var CACHE = 'hm-portal-shell-v1';
var SHELL = ['/portal', '/portal.js', '/portal-smime.js', '/portal.webmanifest'];
self.addEventListener('install', function (e) {
  e.waitUntil(caches.open(CACHE).then(function (c) { return c.addAll(SHELL); }).then(function () { return self.skipWaiting(); }));
});
self.addEventListener('activate', function (e) {
  e.waitUntil(caches.keys().then(function (keys) {
    return Promise.all(keys.filter(function (k) { return k !== CACHE; }).map(function (k) { return caches.delete(k); }));
  }).then(function () { return self.clients.claim(); }));
});
// A share from another app arrives as a POST to the share target. The
// title, text, link and files go into a cache of their own and the page is
// sent to a new message, which reads them once and deletes the cache.
var SHARE = 'hm-portal-share';
self.addEventListener('fetch', function (e) {
  var url = new URL(e.request.url);
  if (e.request.method === 'POST' && url.pathname === '/portal/share') {
    e.respondWith(e.request.formData().then(function (form) {
      var files = form.getAll('files').filter(function (f) { return f && typeof f.name === 'string'; });
      var meta = { title: form.get('title') || '', text: form.get('text') || '', url: form.get('url') || '', files: files.map(function (f) { return f.name; }) };
      return caches.open(SHARE).then(function (c) {
        var puts = [c.put('/portal/share-text', new Response(JSON.stringify(meta), { headers: { 'Content-Type': 'application/json' } }))];
        files.forEach(function (f, i) { puts.push(c.put('/portal/share-file/' + i, new Response(f, { headers: { 'Content-Type': f.type || 'application/octet-stream' } }))); });
        return Promise.all(puts);
      }).then(function () { return Response.redirect('/portal#/compose?share=1', 303); });
    }).catch(function () { return Response.redirect('/portal#/compose', 303); }));
    return;
  }
  if (e.request.method !== 'GET' || SHELL.indexOf(url.pathname) < 0) { return; }
  e.respondWith(fetch(e.request).then(function (r) {
    if (r.ok) { var copy = r.clone(); caches.open(CACHE).then(function (c) { c.put(e.request, copy); }); }
    return r;
  }).catch(function () { return caches.match(e.request); }));
});
