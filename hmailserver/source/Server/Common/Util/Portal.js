// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

(function () {
  'use strict';
  var el = function (id) { return document.getElementById(id); };
  // ---- Language. English is the key; a catalogue maps it to the reader's
  // language. The one the server chose from Accept-Language rides in the
  // page (lang-data, parsed below); a choice kept in this browser or in the
  // account's preferences fetches another (/portal-lang/<code>.json). The
  // script's own texts go through t() and tf(); the markup is walked once
  // the catalogue is known, and again on a change, each text node and
  // attribute keeping its English so it can be walked back.
  var LANGUAGES = [['en', 'English'], ['cs', '\u010ce\u0161tina'], ['da', 'Dansk'], ['de', 'Deutsch'], ['el', '\u0395\u03bb\u03bb\u03b7\u03bd\u03b9\u03ba\u03ac'], ['es', 'Espa\u00f1ol'], ['fi', 'Suomi'], ['fr', 'Fran\u00e7ais'], ['it', 'Italiano'], ['ja', '\u65e5\u672c\u8a9e'], ['ko', '\ud55c\uad6d\uc5b4'], ['nb', 'Norsk bokm\u00e5l'], ['nl', 'Nederlands'], ['pl', 'Polski'], ['pt-BR', 'Portugu\u00eas (Brasil)'], ['pt-PT', 'Portugu\u00eas (Portugal)'], ['ru', '\u0420\u0443\u0441\u0441\u043a\u0438\u0439'], ['sv', 'Svenska'], ['tr', 'T\u00fcrk\u00e7e'], ['uk', '\u0423\u043a\u0440\u0430\u0457\u043d\u0441\u044c\u043a\u0430'], ['zh-Hans', '\u7b80\u4f53\u4e2d\u6587']];
  var catalogue = {};
  var languageActive = 'en';
  var catalogueHas = function (key) { return Object.prototype.hasOwnProperty.call(catalogue, key) && typeof catalogue[key] === 'string' && catalogue[key] !== ''; };
  var t = function (key) { return catalogueHas(key) ? catalogue[key] : key; };
  var tf = function (key) { var s = t(key); for (var i = 1; i < arguments.length; i++) { s = s.split('{' + (i - 1) + '}').join(String(arguments[i])); } return s; };
  var LOCALISED_ATTRIBUTES = ['placeholder', 'aria-label', 'title', 'alt'];
  var localiseTree = function (root) {
    if (!root || !root.childNodes) { return; }
    var kids = root.childNodes;
    for (var i = 0; i < kids.length; i++) {
      var n = kids[i];
      if (n.nodeType === 3) {
        var original = n.__en !== undefined ? n.__en : String(n.nodeValue || '');
        var trimmed = original.trim();
        if (trimmed && (catalogueHas(trimmed) || n.__en !== undefined)) {
          if (n.__en === undefined) { n.__en = original; }
          n.nodeValue = original.replace(trimmed, function () { return t(trimmed); });
        }
      } else if (n.nodeType === 1) {
        if (n.tagName === 'SCRIPT' || n.tagName === 'STYLE') { continue; }
        for (var a = 0; a < LOCALISED_ATTRIBUTES.length; a++) {
          var name = LOCALISED_ATTRIBUTES[a];
          if (!n.hasAttribute || !n.hasAttribute(name)) { continue; }
          var key = '__en_' + name;
          var given = n[key] !== undefined ? n[key] : n.getAttribute(name);
          if (catalogueHas(given) || n[key] !== undefined) { n[key] = given; n.setAttribute(name, t(given)); }
        }
        localiseTree(n);
      }
    }
  };
  var knownLanguage = function (code) {
    var lower = String(code || '').toLowerCase();
    for (var i = 0; i < LANGUAGES.length; i++) { if (LANGUAGES[i][0].toLowerCase() === lower) { return LANGUAGES[i][0]; } }
    return null;
  };
  var applyCatalogue = function (code, data) {
    catalogue = data || {};
    languageActive = code;
    try { if (code === 'en') { localStorage.removeItem('hmPortalLang'); } else { localStorage.setItem('hmPortalLang', code); } } catch (e) { /* no storage */ }
    if (document.documentElement && document.documentElement.setAttribute) { document.documentElement.setAttribute('lang', code); }
    if (document.body) { localiseTree(document.body); }
    var select = el('pref-language');
    if (select) { select.value = code; }
  };
  var loadLanguage = function (code) {
    if (!code || code === languageActive) { return Promise.resolve(true); }
    if (code === 'en') { applyCatalogue('en', {}); return Promise.resolve(true); }
    return fetch('/portal-lang/' + encodeURIComponent(code) + '.json', { credentials: 'same-origin' }).then(function (response) {
      return response.ok ? response.json() : null;
    }).then(function (data) { if (data) { applyCatalogue(code, data); return true; } return false; }, function () { return false; });
  };
  var browserLanguage = function () {
    if (typeof navigator === 'undefined') { return 'en'; }
    var wanted = navigator.languages && navigator.languages.length ? navigator.languages : [navigator.language || 'en'];
    for (var i = 0; i < wanted.length; i++) {
      var w = String(wanted[i] || '');
      var exact = knownLanguage(w);
      if (exact) { return exact; }
      var primary = w.split('-')[0].toLowerCase();
      if (primary === 'pt') { return 'pt-BR'; }
      if (primary === 'zh') { return 'zh-Hans'; }
      if (primary === 'no' || primary === 'nn') { return 'nb'; }
      var byPrimary = knownLanguage(primary);
      if (byPrimary) { return byPrimary; }
    }
    return 'en';
  };
  (function () {
    var select = el('pref-language');
    if (select) {
      LANGUAGES.forEach(function (pair) { var o = document.createElement('option'); o.value = pair[0]; o.textContent = pair[1]; select.appendChild(o); });
    }
    var data = null;
    try { data = el('lang-data'); } catch (e) { data = null; }
    var code = null;
    var parsed = null;
    if (data) { try { parsed = JSON.parse(data.textContent || '{}'); code = knownLanguage(data.getAttribute('data-lang')); } catch (e) { parsed = null; } }
    var remembered = null;
    try { remembered = knownLanguage(localStorage.getItem('hmPortalLang')); } catch (e) { remembered = null; }
    if (remembered && remembered !== code) { loadLanguage(remembered); }
    else if (parsed && code && code !== 'en') { applyCatalogue(code, parsed); }
    else if (!remembered && !code) { var guess = browserLanguage(); if (guess !== 'en') { loadLanguage(guess); } }
  })();
  var applyLanguagePref = function () { var code = knownLanguage(pref('language') || ''); if (code) { loadLanguage(code); } };
  var say = function (id, text, ok) { var s = el(id); s.textContent = text || ''; s.className = 'status' + (text ? (ok ? ' ok' : ' error') : ''); };
  // The password is sent exactly once, to start the session; from then on
  // the browser's cookie is the credential and nothing is kept in memory.
  var call = function (method, path, body, extra) {
    var h = {};
    if (extra) { for (var k in extra) { if (extra[k]) { h[k] = extra[k]; } } }
    if (method !== 'GET') { h['X-Requested-With'] = 'hMailServer'; }
    var options = { method: method, headers: h, cache: 'no-store', credentials: 'same-origin' };
    if (body !== undefined) { h['Content-Type'] = 'application/json'; options.body = JSON.stringify(body); }
    return fetch(path, options).then(function (response) {
      return response.text().then(function (text) {
        var data = null;
        try { data = text ? JSON.parse(text) : null; } catch (e) { data = null; }
        // A 401 that asks for the one-time code is not the session ending:
        // the password was right, and the code is what the form asks for next.
        if (response.status === 401 && path !== '/api/v1/me' && path !== '/api/v1/session' && !el('account').hidden && response.headers.get('X-hMailServer-OTP') !== 'required') {
          showSignIn();
          say('signin-status', t('Your session has ended. Sign in again.'), false);
        }
        return { status: response.status, data: data, otp: response.headers.get('X-hMailServer-OTP') };
      });
    }, function () {
      // The request never reached the server: the network went, or the
      // listener did. Without this arm the rejection is unhandled and the
      // caller's .then never runs, so the button that was pressed does
      // nothing at all and says nothing - and the probe, which schedules
      // its next tick inside its own .then, stops for good while the page
      // goes on saying it is watching. Status 0 is not a status any
      // server sends, and every check here is written as 'is it the one
      // I wanted', so it reports as the failure it is.
      return { status: 0, data: null, otp: null };
    });
  };
  var describe = function (result, fallback) {
    if (result.data && result.data.error) { return result.data.error; }
    if (result.status === 401) { return 'The address or password is not right, or the session has ended.'; }
    if (result.status === 429) { return 'Too many attempts; wait a minute and try again.'; }
    return fallback + ' (' + result.status + ')';
  };
  var format = function (bytes) {
    if (bytes >= 1073741824) { return (bytes / 1073741824).toFixed(1) + ' GB'; }
    if (bytes >= 1048576) { return (bytes / 1048576).toFixed(1) + ' MB'; }
    if (bytes >= 1024) { return (bytes / 1024).toFixed(0) + ' KB'; }
    return bytes + ' bytes';
  };
  // Every value from the server is written as text, never as markup.
  var node = function (tag, text, className) {
    var n = document.createElement(tag);
    if (text !== undefined) { n.textContent = text; }
    if (className) { n.className = className; }
    return n;
  };
  var clear = function (n) { while (n.firstChild) { n.removeChild(n.firstChild); } };
  var button = function (text, className) { var b = node('button', text, className || 'btn ghost sm'); b.type = 'button'; return b; };

  // ---- Where the reader is: the address bar -------------------------------
  //
  // Routes live in the fragment, not the path. The page is served by the
  // server at exactly one address - RestApiServer answers GET /portal and
  // nothing below it - so a path route would be a 404 on the first reload,
  // and making the server serve one page under a whole subtree is a change
  // to the routing table for the sake of a cosmetic URL. The fragment needs
  // no server route at all, is never sent to the server (so no folder name
  // or search term reaches a log), and gives the history API everything it
  // needs: assigning location.hash pushes an entry and fires hashchange, so
  // Back and Forward work, and a reload lands where the reader was.
  //
  //   #/f/<id>[?q=text][&before=<uid>]   a folder, or a search inside it
  //   #/search?q=text                    a search across every folder
  //   #/m/<id>                           one message
  //   #/compose[?reply|replyall|forward|draft=<id>]
  //   #/held  #/folders  #/settings  #/filters  #/password
  var parseHash = function (raw) {
    var text = String(raw === undefined ? (location.hash || '') : raw).replace(/^#/, '');
    var cut = text.indexOf('?');
    var query = cut >= 0 ? text.slice(cut + 1) : '';
    var path = (cut >= 0 ? text.slice(0, cut) : text).replace(/^\/+/, '').replace(/\/+$/, '');
    var params = {};
    query.split('&').forEach(function (pair) {
      if (!pair) { return; }
      var eq = pair.indexOf('=');
      var key = decodeURIComponent(eq < 0 ? pair : pair.slice(0, eq));
      params[key] = eq < 0 ? '' : decodeURIComponent(pair.slice(eq + 1).replace(/\+/g, ' '));
    });
    return { parts: path ? path.split('/') : [], params: params, hash: text };
  };
  var go = function (hash) {
    rememberPlace();
    if (('#' + hash) === location.hash) { route(); return; }
    location.hash = hash;
  };
  var replaceWith = function (hash) {
    rememberPlace();
    if (history.replaceState) { history.replaceState(null, '', '#' + hash); route(); return; }
    location.replace('#' + hash);
  };
  var listHash = function () {
    if (state.everywhere && state.query) { return '/search?q=' + encodeURIComponent(state.query); }
    return '/f/' + (state.folderId || inboxId) + (state.query ? '?q=' + encodeURIComponent(state.query) : '') +
      (state.before ? (state.query ? '&' : '?') + 'before=' + state.before : '');
  };

  var state = { folderId: 0, query: '', everywhere: false, before: 0 };
  var me = '';
  var directoryLinked = false;
  var quarantineOn = true;

  // ---- The app shell ------------------------------------------------------
  var panels = ['mail-section', 'compose-section', 'quarantine-section', 'folders-section', 'settings-section', 'filter-section', 'contacts-section', 'away-section', 'storage-section', 'security-section', 'password-section'];
  var showPanel = function (id, title) {
    panels.forEach(function (p) { el(p).hidden = p !== id; });
    el('view-title').textContent = title;
    el('mail-search-form').hidden = id !== 'mail-section';
    el('content').scrollTop = 0;
  };
  var markNav = function (hash) {
    [el('folder-nav'), el('view-nav')].forEach(function (nav) {
      var children = nav.children;
      for (var i = 0; i < children.length; i++) {
        children[i].classList.toggle('on', children[i].getAttribute('data-route') === hash);
      }
    });
  };

  // ---- Folders ------------------------------------------------------------
  var allFolders = [];
  var foldersById = {};
  var inboxId = 0;
  // The hierarchy delimiter is the server's, and it is in the folder listing;
  // a folder's name is a path in it, so making or moving a folder means
  // sending the whole path and letting the server make what is missing.
  var delimiter = '.';
  var flatten = function (list, into, depth) {
    list.forEach(function (f) { f.depth = depth; into.push(f); flatten(f.subfolders || [], into, depth + 1); });
    return into;
  };
  var folderName = function (id) { var f = foldersById[id]; return f ? f.path : 'Mail'; };
  var fillFolderSelect = function (select, homeId) {
    clear(select);
    var home = foldersById[homeId];
    allFolders.forEach(function (f) {
      if (f.id === homeId || !f.writable || (home && f.account_id !== home.account_id)) { return; }
      var option = node('option', f.path); option.value = f.id; select.appendChild(option);
    });
  };
  var renderFolders = function (tree) {
    delimiter = tree.delimiter || delimiter;
    var folders = flatten(tree.folders, [], 0);
    (tree.shared || []).forEach(function (share) {
      var start = folders.length;
      flatten(share.folders, folders, 0);
      for (var s = start; s < folders.length; s++) { folders[s].owner = share.owner || ''; }
    });
    allFolders = folders;
    foldersById = {};
    folders.forEach(function (f) { foldersById[f.id] = f; });
    var inbox = folders.filter(function (f) { return f.path.toUpperCase() === 'INBOX'; })[0];
    inboxId = inbox ? inbox.id : (folders.length ? folders[0].id : 0);
    var nav = el('folder-nav');
    clear(nav);
    var lastOwner = '';
    folders.forEach(function (f) {
      // The Snoozed folder is the server's waiting room, not a place to file
      // things: it stays out of the list, and comes back in Storage.
      if (!f.owner && f.path === 'Snoozed') { return; }
      // Another account's folders, and the public ones, under a heading of
      // their own, so a reader knows whose mail they are looking at.
      if (f.owner && f.owner !== lastOwner) {
        nav.appendChild(node('div', f.owner.charAt(0) === '#' ? t('Public folders') : t('Shared by ') + f.owner, 'navhead'));
        lastOwner = f.owner;
      }
      var b = button('', '');
      b.setAttribute('data-route', '/f/' + f.id);
      var icon = node('span', f.depth ? '↳' : '▢', 'ico');
      var name = node('span', f.name || f.path, 'nm');
      if (f.depth) { name.style.paddingLeft = (f.depth * 10) + 'px'; }
      b.appendChild(icon); b.appendChild(name);
      b.appendChild(node('span', f.unseen ? String(f.unseen) : '', 'ct'));
      b.addEventListener('click', function () { go('/f/' + f.id); });
      nav.appendChild(b);
    });
    markNav(state.folderId ? '/f/' + state.folderId : '');
    updateTitle();
    renderNotifyFolders();
    fillFolderSelect(el('rule-folder'), 0);
    fillFolderSelect(el('folder-export-target'), 0);
    fillFolderSelect(el('folder-import-target'), 0);
    setExportLink();
  };
  var loadFolders = function () {
    return call('GET', '/api/v1/me/folders').then(function (result) {
      if (result.status === 200 && result.data) { renderFolders(result.data); }
      return result;
    });
  };

  // ---- The listing --------------------------------------------------------
  var listRows = [];
  var cursor = -1;
  var selected = {};
  var lastListing = null;
  // What "where the reader was" means, kept whenever the listing is left:
  // the scroll offset, the message the cursor was on, and the ticked boxes.
  var rememberPlace = function () {
    if (!lastListing || el('mail-section').hidden || el('message-list').hidden) { return; }
    lastListing.scroll = el('content').scrollTop;
    lastListing.cursorId = cursor >= 0 && listRows[cursor] ? listRows[cursor].id : 0;
    lastListing.selected = selected;
  };
  // The list and one message share a panel, so what belongs to the list -
  // the mailbox card above it and the keyboard hint - goes with it.
  var showList = function () {
    el('message-view').hidden = true;
    el('message-list').hidden = false;
    el('list-tools').hidden = false;
    el('quota-card').hidden = false;
    el('list-hint').hidden = false;
  };
  var setCursor = function (index, silent) {
    if (!listRows.length) { cursor = -1; return; }
    if (typeof prefetchNext === 'function') { setTimeout0(prefetchNext); }
    cursor = Math.max(0, Math.min(listRows.length - 1, index));
    listRows.forEach(function (r, i) { r.row.classList.toggle('msg-cursor', i === cursor); });
    if (!silent) { listRows[cursor].row.scrollIntoView({ block: 'nearest' }); }
  };
  var selectedIds = function () { return Object.keys(selected).filter(function (k) { return selected[k]; }).map(Number); };
  var renderBulk = function () {
    var ids = selectedIds();
    el('bulk-bar').hidden = ids.length === 0;
    el('bulk-count').textContent = ids.length + ' selected';
    fillFolderSelect(el('bulk-move'), state.folderId);
  };
  var clearSelection = function () { selected = {}; renderBulk(); };
  var renderSearchNote = function (list, page) {
    if (!page.query) { return; }
    var note = 'Searched ' + page.scanned + ' message' + (page.scanned === 1 ? '' : 's') + ' for \'' + page.query + '\'.';
    if (page.more) { note += ' Only the newest are shown.'; }
    var row = node('div', note, 'listfoot');
    if (!page.complete && page.next_before_uid) {
      var older = button(t('Search older messages'));
      older.addEventListener('click', function () { state.before = page.next_before_uid; go(listHash()); });
      row.appendChild(older);
    }
    list.appendChild(row);
    list.appendChild(node('div', t('Narrow a search with from:, to:, subject:, has:attachment, before:2026-01-31, after:, in:folder, is:unread, is:read, is:flagged, is:unflagged, is:answered, label:name, and "quoted phrases".'), 'note'));
  };
  // ---- Archive, Junk and Trash, and conversations ---------------------------
  var folderIs = function (folderId, use) {
    var f = foldersById[folderId];
    return !!(f && f.special_use && f.special_use.indexOf(String.fromCharCode(92) + use) >= 0);
  };
  var junkTargetFor = function (folderId) { return folderIs(folderId, 'Junk') ? 'inbox' : 'junk'; };
  // What a filing did: a delete says where the message went, since the
  // route moves it to Trash when the account has one.
  var filingLabel = function (to, result) {
    if (to === 'delete') { return result && result.data && result.data.deleted ? 'Deleted.' : 'Moved to the trash folder.'; }
    return to === 'archive' ? 'Archived.' : to === 'junk' ? 'Filed as junk.' : to === 'inbox' ? 'Moved to the inbox.' : to === 'trash' ? 'Moved to the trash folder.' : 'Deleted.';
  };
  var fileMessage = function (id, to) {
    if (to === 'delete') { return call('DELETE', '/api/v1/me/messages/' + id); }
    return call('POST', '/api/v1/me/messages/' + id + '/move', { to: to });
  };
  var afterFiling = function (result, what) {
    if (result.status === 200) { say('mail-status', what, true); lastListing = null; loadFolders(); return true; }
    say('mail-status', describe(result, t('Could not file the message')), false);
    return false;
  };
  // A collapsed conversation is filed whole, one message after another,
  // as its tick box selects it whole.
  var fileRow = function (row, to) {
    if (!row) { return; }
    var ids = row.ids || [row.id];
    var last = null;
    var chain = Promise.resolve();
    ids.forEach(function (id) { chain = chain.then(function () { return fileMessage(id, to).then(function (r) { last = r; }); }); });
    chain.then(function () {
      var result = last;
      if (!afterFiling(result, filingLabel(to, result))) { return; }
      var at = cursor;
      var reloaded = loadMessages();
      if (reloaded && reloaded.then) { reloaded.then(function () { setCursor(Math.min(at, listRows.length - 1), true); }); }
    });
  };
  var expandedThreads = {};
  // The conversation a message belongs to: the first reference names the
  // root, then what it answers, then itself.
  var threadKey = function (m) {
    var refs = (m.references || '').trim().split(/\s+/).filter(Boolean);
    return refs.length ? refs[0] : ((m.in_reply_to || '').trim() || m.message_id || ('id:' + m.id));
  };
  // ---- Labels: IMAP keywords on the message, a colour of the reader's own.
  var labelColours = {};
  var labelNames = function () { return Object.keys(labelColours).sort(function (a, b) { return a.toLowerCase() < b.toLowerCase() ? -1 : a.toLowerCase() > b.toLowerCase() ? 1 : 0; }); };
  var colourKeyOf = function (name) { var lower = name.toLowerCase(); for (var k in labelColours) { if (k.toLowerCase() === lower) { return k; } } return null; };
  var colourOf = function (name) { var k = colourKeyOf(name); return k ? labelColours[k] : '#5b6875'; };
  var chip = function (name) { var c = node('span', name, 'chip'); if (c.style) { c.style.backgroundColor = colourOf(name); } return c; };
  var chipsFor = function (keywords) { var box = node('span', undefined, 'chips'); (keywords || []).forEach(function (k) { box.appendChild(chip(k)); }); return box; };
  var validLabel = function (name) { return /^[\x21-\x7e]+$/.test(name) && !/[()\{\}\[\]%*"\\]/.test(name) && name.length <= 60; };
  var saveLabelColours = function () { return savePrefs({ labels: JSON.stringify(labelColours) }); };
  var renderLabelNav = function () {
    var nav = el('label-nav');
    clear(nav);
    var names = labelNames();
    if (!names.length) { return; }
    nav.appendChild(node('div', t('Labels'), 'nav-heading'));
    names.forEach(function (name) {
      var b = document.createElement('button');
      b.type = 'button';
      b.appendChild(chip(name));
      b.addEventListener('click', function () { go('/search?q=' + encodeURIComponent('label:' + name)); });
      nav.appendChild(b);
    });
  };
  var renderLabelSettings = function () {
    var rows = el('label-rows');
    clear(rows);
    var names = labelNames();
    names.forEach(function (name) {
      var row = node('div', undefined, 'frow');
      var left = node('div', undefined, 'fname');
      left.appendChild(chip(name));
      row.appendChild(left);
      var colour = document.createElement('input');
      colour.type = 'color';
      colour.value = labelColours[name];
      colour.setAttribute('aria-label', t('Colour of ') + name);
      colour.addEventListener('change', function () {
        labelColours[name] = colour.value;
        saveLabelColours().then(function (ok) { if (ok) { say('label-status', t('Colour saved.'), true); } });
      });
      var remove = button(t('Remove'), 'btn ghost sm danger');
      remove.addEventListener('click', function () {
        delete labelColours[name];
        saveLabelColours().then(function (ok) { if (ok) { say('label-status', t('Taken off the list; messages keep the keyword until it is taken off them.'), true); } });
      });
      row.appendChild(colour);
      row.appendChild(remove);
      rows.appendChild(row);
    });
    el('label-empty').hidden = names.length > 0;
  };
  var refreshLabelColours = function () {
    labelColours = {};
    try { var parsed = JSON.parse(pref('labels') || '{}'); if (parsed && typeof parsed === 'object') { labelColours = parsed; } } catch (e) { labelColours = {}; }
    renderLabelNav();
    renderLabelSettings();
  };
  el('label-form').addEventListener('submit', function (event) {
    event.preventDefault();
    var name = el('label-name').value.trim();
    if (!validLabel(name)) { say('label-status', t('A label is one word: letters, digits and punctuation, no spaces, quotes or brackets.'), false); return; }
    labelColours[colourKeyOf(name) || name] = el('label-colour').value || '#36c2ff';
    saveLabelColours().then(function (ok) { if (ok) { el('label-name').value = ''; say('label-status', t('Added.'), true); } });
  });
  var renderMessageLabels = function () {
    var box = el('message-labels');
    clear(box);
    if (!current) { return; }
    (current.flags.keywords || []).forEach(function (k) { box.appendChild(chip(k)); });
  };
  var setKeywords = function (add, remove) {
    if (!current) { return Promise.resolve(false); }
    return call('PUT', '/api/v1/me/messages/' + current.id + '/flags', { keywords_add: add, keywords_remove: remove }).then(function (result) {
      if (result.status === 200 && result.data && result.data.flags) {
        current.flags = result.data.flags;
        renderMessageLabels();
        renderLabelMenu();
        lastListing = null;
        return true;
      }
      say('mail-status', describe(result, t('Could not change the labels')), false);
      return false;
    });
  };
  var renderLabelMenu = function () {
    var menu = el('label-menu');
    clear(menu);
    if (menu.hidden || !current) { return; }
    var have = {};
    (current.flags.keywords || []).forEach(function (k) { have[k.toLowerCase()] = true; });
    var names = labelNames();
    (current.flags.keywords || []).forEach(function (k) { if (!colourKeyOf(k)) { names.push(k); } });
    if (!names.length) { menu.appendChild(node('span', t('No labels yet - name one:'), 'muted')); }
    names.forEach(function (name) {
      var label = document.createElement('label');
      label.className = 'inline';
      var box = document.createElement('input');
      box.type = 'checkbox';
      box.checked = !!have[name.toLowerCase()];
      box.setAttribute('aria-label', name);
      box.addEventListener('change', function () { if (box.checked) { setKeywords([name], []); } else { setKeywords([], [name]); } });
      label.appendChild(box);
      label.appendChild(chip(name));
      menu.appendChild(label);
    });
    var input = document.createElement('input');
    input.type = 'text';
    input.id = 'label-new';
    input.setAttribute('maxlength', '60');
    input.setAttribute('placeholder', t('New label'));
    input.setAttribute('aria-label', t('New label'));
    var add = button(t('Add'), 'btn ghost sm');
    add.id = 'label-new-add';
    add.addEventListener('click', function () {
      var name = input.value.trim();
      if (!validLabel(name)) { say('mail-status', t('A label is one word: letters, digits and punctuation, no spaces, quotes or brackets.'), false); return; }
      if (!colourKeyOf(name)) { labelColours[name] = '#36c2ff'; saveLabelColours(); }
      setKeywords([name], []);
    });
    menu.appendChild(input);
    menu.appendChild(add);
  };
  var toggleLabelMenu = function () { var menu = el('label-menu'); menu.hidden = !menu.hidden; renderLabelMenu(); };
  el('message-label').addEventListener('click', toggleLabelMenu);

  var messageRow = function (m, inThread) {
    var row = node('div', undefined, (m.flags.seen ? 'msg' : 'msg unseen') + (inThread ? ' msg-reply' : ''));
    row.setAttribute('role', 'listitem');
    var box = document.createElement('input'); box.type = 'checkbox'; box.checked = !!selected[m.id]; box.setAttribute('aria-label', t('Select'));
    box.addEventListener('click', function (event) { event.stopPropagation(); selected[m.id] = box.checked; renderBulk(); });
    var body = node('div', undefined, 'msg-body');
    var subjectLine = node('div', m.subject || t('(no subject)'), 'msg-subject');
    if (m.flags && m.flags.keywords && m.flags.keywords.length) { subjectLine.appendChild(chipsFor(m.flags.keywords)); }
    body.appendChild(subjectLine);
    body.appendChild(node('div', (m.from || '?') + ' - ' + (m.date || m.received) + ' - ' + format(m.size) + (m.folder ? t(' - in ') + m.folder : ''), 'msg-detail'));
    row.appendChild(box); row.appendChild(body);
    body.addEventListener('click', function () { go('/m/' + m.id); });
    // On a touch screen: a swipe to the right archives, to the left deletes.
    var touchX = null;
    row.addEventListener('touchstart', function (e) { touchX = e.touches && e.touches.length === 1 ? e.touches[0].clientX : null; }, { passive: true });
    row.addEventListener('touchend', function (e) {
      if (touchX === null || !e.changedTouches || !e.changedTouches.length) { return; }
      var dx = e.changedTouches[0].clientX - touchX;
      touchX = null;
      var entry = null;
      listRows.forEach(function (r) { if (r.id === m.id) { entry = r; } });
      if (dx > 90) { fileRow(entry, 'archive'); } else if (dx < -90) { fileRow(entry, 'delete'); }
    }, { passive: true });
    listRows.push({ id: m.id, row: row, box: box });
    return row;
  };
  // A folder of thousands: the first chunk is on the screen at once, the rest
  // arrives frame by frame while the reader is already reading.
  var CHUNK = 150;
  var chunkedAppend = function (items, list, make) {
    var at = 0;
    var frame = typeof window.requestAnimationFrame === 'function' ? window.requestAnimationFrame : null;
    var step = function () {
      var end = frame ? Math.min(items.length, at + CHUNK) : items.length;
      for (; at < end; at++) { list.appendChild(make(items[at])); }
      if (at < items.length) { frame(step); }
    };
    step();
  };
  var renderRows = function (page, list, shown) {
    if (pref('view') !== 'threads' || page.query) {
      page.messages.forEach(function (m) { shown[m.id] = true; });
      // A reply is indented under what it answers. The list is newest
      // first, so 'the root was seen already' would indent the original.
      chunkedAppend(page.messages, list, function (m) { return messageRow(m, !!(m.in_reply_to || (m.references || '').trim())); });
      return;
    }
    // A null prototype: a References header spelling 'constructor' is a key, not a property.
    var groups = []; var byKey = Object.create(null);
    page.messages.forEach(function (m) {
      shown[m.id] = true;
      var key = threadKey(m);
      if (!byKey[key]) { byKey[key] = { key: key, messages: [] }; groups.push(byKey[key]); }
      byKey[key].messages.push(m);
    });
    groups.forEach(function (g) {
      if (g.messages.length === 1) { list.appendChild(messageRow(g.messages[0], false)); return; }
      var newest = g.messages[0];
      var unseen = g.messages.some(function (m) { return !m.flags.seen; });
      var row = node('div', undefined, 'msg thread' + (unseen ? ' unseen' : ''));
      row.setAttribute('role', 'listitem');
      var box = document.createElement('input'); box.type = 'checkbox'; box.setAttribute('aria-label', t('Select the conversation'));
      box.checked = g.messages.every(function (m) { return !!selected[m.id]; });
      box.addEventListener('click', function (event) { event.stopPropagation(); g.messages.forEach(function (m) { selected[m.id] = box.checked; }); renderBulk(); });
      var body = node('div', undefined, 'msg-body');
      var subject = node('div', undefined, 'msg-subject');
      subject.appendChild(node('span', String(g.messages.length), 'thread-count'));
      subject.appendChild(node('span', newest.subject || t('(no subject)')));
      body.appendChild(subject);
      var senders = [];
      g.messages.forEach(function (m) { var s = (m.from || '?').replace(/<.*$/, '').trim() || m.from; if (senders.indexOf(s) < 0) { senders.push(s); } });
      body.appendChild(node('div', senders.slice(0, 3).join(', ') + (senders.length > 3 ? ', ...' : '') + ' - ' + (newest.date || newest.received), 'msg-detail'));
      row.appendChild(box); row.appendChild(body);
      var toggle = function () {
        expandedThreads[g.key] = !expandedThreads[g.key];
        if (!lastListing) { return; }
        renderMessages(lastListing.page);
        listRows.forEach(function (r, i) { if (r.toggle && r.id === newest.id) { setCursor(i, true); } });
      };
      body.addEventListener('click', toggle);
      listRows.push({ id: newest.id, ids: g.messages.map(function (m) { return m.id; }), row: row, box: box, toggle: toggle });
      list.appendChild(row);
      if (expandedThreads[g.key]) {
        // Oldest first inside the conversation, newest last, as it is read.
        g.messages.slice().reverse().forEach(function (m) { var sub = messageRow(m, false); sub.className += ' in-thread'; list.appendChild(sub); });
      }
    });
  };
  var renderMessages = function (page) {
    var list = el('message-list');
    clear(list);
    listRows = []; cursor = -1;
    if (!page.messages.length) {
      selected = {}; renderBulk();
      list.appendChild(node('div', page.query ? t('Nothing matched.') : t('This folder is empty.'), 'empty'));
      renderSearchNote(list, page);
      return;
    }
    var shown = {};
    renderRows(page, list, shown);
    Object.keys(selected).forEach(function (k) { if (!shown[k]) { delete selected[k]; } });
    setCursor(0, true);
    renderBulk();
    if (!page.query && page.total > page.messages.length && page.messages.length) {
      var last = page.messages[page.messages.length - 1];
      var older = button(t('Older messages'));
      older.addEventListener('click', function () { state.before = last.uid; go(listHash()); });
      var note = node('div', (state.before ? t('Older than message ') + state.before + ': ' : t('The newest ')) + page.messages.length + t(' of ') + page.total + t(' are shown. '), 'listfoot');
      note.appendChild(older);
      list.appendChild(note);
    }
    renderSearchNote(list, page);
  };
  var listingKey = function () { return state.everywhere + '|' + state.folderId + '|' + state.query + '|' + state.before; };
  var loadMessagesOffline = function (result) {
    if (result.status !== 0 || state.everywhere || state.query || state.before || state.folderId !== inboxId) { return false; }
    var saved = offlineRead('list');
    if (!saved) { return false; }
    offlineNote(true);
    renderMessages(saved);
    return true;
  };
  var loadMessages = function () {
    var url;
    if (state.query && state.everywhere) {
      url = '/api/v1/me/search?q=' + encodeURIComponent(state.query) + '&limit=200';
    } else {
      if (!state.folderId) { renderMessages({ total: 0, messages: [] }); return Promise.resolve(); }
      url = '/api/v1/me/folders/' + state.folderId + '/messages';
      if (state.query) { url += '?q=' + encodeURIComponent(state.query) + (state.before ? '&before_uid=' + state.before : ''); }
      else if (state.before) { url += '?before_uid=' + state.before; }
    }
    return call('GET', url).then(function (result) {
      if (result.status === 200 && result.data) {
        renderMessages(result.data);
        lastListing = { key: listingKey(), page: result.data };
        if (!state.everywhere && !state.query && !state.before && state.folderId === inboxId) { offlineKeep('list', result.data); }
        return;
      }
      if (loadMessagesOffline(result)) { return; }
      say('mail-status', describe(result, t('Could not read the folder')), false);
    });
  };
  // A change the probe saw, folded into what is on screen without moving the
  // reader: the same listing is fetched again, then the cursor is put back on
  // the message it was on (by id, not by index - rows may have arrived above
  // it), the ticked boxes are put back, and the scroll position is restored.
  var reloadKeepingPlace = function () {
    var onId = cursor >= 0 && listRows[cursor] ? listRows[cursor].id : 0;
    var ticked = selected;
    var scroll = el('content').scrollTop;
    return loadMessages().then(function () {
      selected = {};
      Object.keys(ticked).forEach(function (k) { if (ticked[k] && listRows.filter(function (r) { return String(r.id) === String(k); }).length) { selected[k] = true; } });
      listRows.forEach(function (r) { r.box.checked = !!selected[r.id]; });
      renderBulk();
      var at = -1;
      listRows.forEach(function (r, i) { if (r.id === onId) { at = i; } });
      if (at >= 0) { setCursor(at, true); }
      el('content').scrollTop = scroll;
    });
  };

  // ---- One message --------------------------------------------------------
  var current = null;
  var showHtml = false;
  var cameFromList = false;
  // An image the sender embedded: the HTML part points at it as cid:<id>, and
  // the attachment carrying that Content-ID is somewhere in the same message.
  //
  // It cannot be pointed at the attachment's download route, which is the
  // obvious answer and the wrong one. The frame is sandboxed without
  // allow-same-origin, so its document has an origin of its own; a browser
  // therefore counts a request it makes as cross-site, and the session cookie
  // is SameSite=Strict, so the cookie does not go and the route answers 401.
  // Measured in Chrome on 9 September 2026 through a logging proxy: the same
  // frame without the sandbox attribute sent the cookie and got 200 image/png,
  // and with it sent no cookie and got 401. Weakening the sandbox to make the
  // URL work would give the sender's markup this account's own origin, which
  // is the whole thing the frame exists to prevent.
  //
  // So the page fetches the attachment itself, with its own credentials, and
  // hands the frame the bytes as a data: URL - which its policy already allows
  // and which travels no further. The type is the one the SERVER declared on
  // the download, not the one the message claimed: the download route serves
  // HTML, SVG, XML and script as application/octet-stream on purpose, and
  // anything that does not come back as an image is left alone, so the
  // reference stays a cid: that renders as nothing and the attachment is still
  // in the list below, named. Remote images are untouched and the policy still
  // refuses them, so opening a message still tells its sender nothing.
  var RENDERS = { 'image/png': 1, 'image/jpeg': 1, 'image/gif': 1, 'image/webp': 1, 'image/bmp': 1, 'image/x-icon': 1, 'image/vnd.microsoft.icon': 1 };
  var INLINE_EACH = 4 * 1024 * 1024;
  var INLINE_TOGETHER = 12 * 1024 * 1024;
  var INLINE_MOST = 12;
  var inlineFor = { id: 0, images: null };
  var contentIdOf = function (a) { return String(a.content_id || '').replace(/^</, '').replace(/>$/, '').trim(); };
  var fetchInline = function (message) {
    if (inlineFor.id === message.id && inlineFor.images) { return Promise.resolve(inlineFor.images); }
    var budget = INLINE_TOGETHER;
    var wanted = (message.attachments || []).filter(function (a) {
      var type = String(a.content_type || '').toLowerCase();
      if (!contentIdOf(a) || (type && !RENDERS[type]) || a.size > INLINE_EACH || budget < a.size) { return false; }
      budget -= a.size;
      return true;
    }).slice(0, INLINE_MOST);
    if (!wanted.length) { inlineFor = { id: message.id, images: {} }; return Promise.resolve(inlineFor.images); }
    return Promise.all(wanted.map(function (a) {
      return fetch('/api/v1/me/messages/' + message.id + '/attachments/' + a.index, { cache: 'no-store', credentials: 'same-origin' }).then(function (response) {
        if (response.status !== 200) { return null; }
        return response.blob();
      }).then(function (blob) {
        if (!blob || String(blob.type || '').indexOf('image/') !== 0) { return null; }
        return new Promise(function (resolve) {
          var reader = new FileReader();
          reader.onload = function () { resolve({ id: contentIdOf(a).toLowerCase(), url: String(reader.result) }); };
          reader.onerror = function () { resolve(null); };
          reader.readAsDataURL(blob);
        });
      }, function () { return null; });
    })).then(function (found) {
      var images = {};
      found.forEach(function (one) { if (one && one.url.indexOf('data:image/') === 0) { images[one.id] = one.url; } });
      inlineFor = { id: message.id, images: images };
      return images;
    });
  };
  var resolveInline = function (html, images) {
    if (!Object.keys(images).length) { return html; }
    return html.replace(/(["'(=])\s*cid:([^"'()<>\s]+)/gi, function (whole, lead, reference) {
      var key = '';
      try { key = decodeURIComponent(reference).toLowerCase(); } catch (e) { key = reference.toLowerCase(); }
      if (!(key in images)) { key = reference.toLowerCase(); }
      if (!(key in images)) { return whole; }
      return lead + images[key];
    });
  };
  // HTML as it was sent: in a frame with an origin of its own, where
  // nothing runs, no form is submitted and no address is rewritten - a
  // document the server serves under a policy of its own (a srcdoc frame
  // would inherit this page's, which allows no remote image at all).
  // Remote images, fonts and styles stay blocked unless the reader asked
  // for this message, or for this sender, to show them; the server says
  // whether the part names any (html_remote), so the note is right.
  var renderHtml = function () {
    var frame = el('message-html');
    if (!current || !current.html || !showHtml) { frame.hidden = true; frame.removeAttribute('src'); el('message-text').hidden = false; return Promise.resolve(); }
    var showing = current;
    var sender = addressOf(showing.from).toLowerCase();
    var allowed = remoteAllowed.id === showing.id || remoteSenders().indexOf(sender) >= 0;
    el('message-remote').hidden = !(showing.html_remote && !allowed);
    frame.setAttribute('src', '/api/v1/me/messages/' + showing.id + '/html' + (allowed ? '?remote=1' : ''));
    frame.hidden = false;
    el('message-text').hidden = true;
    return Promise.resolve();
  };
  var renderActions = function () {
    if (!current) { return; }
    el('message-unread').textContent = current.flags.seen ? 'Mark as unread' : 'Mark as read';
    el('message-flag').textContent = current.flags.flagged ? 'Remove flag' : 'Flag';
    el('label-menu').hidden = true;
    renderMessageLabels();
    el('message-edit').hidden = !current.flags.draft;
    el('message-junk').textContent = folderIs(current.folder_id, 'Junk') ? 'Not junk' : 'Junk';
    fillFolderSelect(el('message-move'), current.folder_id);
  };
  var renderMessage = function (m) {
    el('message-list').hidden = true;
    el('list-tools').hidden = true;
    el('message-view').hidden = false;
    el('quota-card').hidden = true;
    el('list-hint').hidden = true;
    el('message-subject').textContent = m.subject || '(no subject)';
    el('message-meta').textContent = 'From ' + (m.from || '?') + (m.to ? ' to ' + m.to : '') + (m.cc ? ', cc ' + m.cc : '') + ' - ' + (m.date || m.received);
    // The verdicts a reader learns to look at, the sender's domain against
    // the account's, the headers on request, and the file itself.
    var badges = el('message-badges');
    clear(badges);
    var auth = m.authentication || {};
    ['spf', 'dkim', 'dmarc'].forEach(function (k) {
      var v = auth[k] || '';
      if (!v) { return; }
      var cls = v === 'pass' ? 'good' : (v === 'none' || v === 'neutral' || v === 'temperror') ? 'muted' : 'bad';
      badges.appendChild(node('span', k.toUpperCase() + ' ' + v, 'badge ' + cls));
    });
    if (m.external) { badges.appendChild(node('span', t('External sender'), 'badge warn')); }
    el('message-remote').hidden = true;
    warnAbout(m);
    previewAttachments(m);
    el('message-headers').textContent = m.headers || '';
    el('message-headers').hidden = true;
    el('message-headers-toggle').textContent = 'Headers';
    el('message-headers-toggle').hidden = !m.headers;
    el('message-receipt').hidden = !(m.receipt_requested_by && pref('receipts') !== 'never');
    el('snooze-menu').hidden = true;
    el('message-unsubscribe').hidden = !m.list_unsubscribe;
    el('message-source').setAttribute('href', '/api/v1/me/messages/' + m.id + '/source');
    el('message-source').setAttribute('download', 'message-' + m.id + '.eml');
    var text = m.text || '';
    if (!text && m.html) {
      // An HTML-only message is read as a document and only its text is
      // shown: nothing in it runs, loads or renders.
      text = new DOMParser().parseFromString(m.html, 'text/html').body.textContent || '';
    }
    if (m.truncated) { text = 'This message is too large to show here; open it in your mail program.' + ((m.attachments || []).length ? ' Its attachments can be downloaded below.' : ''); }
    el('message-text').textContent = text || '(no text)';
    var attachments = el('message-attachments');
    clear(attachments);
    if ((m.attachments || []).length) {
      attachments.appendChild(node('span', t('Attachments: ')));
      m.attachments.forEach(function (a, i) {
        var what = a.content_type ? a.content_type + ', ' : '';
        var link = node('a', a.name + ' (' + what + format(a.size) + ')');
        link.href = '/api/v1/me/messages/' + m.id + '/attachments/' + a.index;
        link.setAttribute('download', a.name);
        if (i > 0) { attachments.appendChild(node('span', ', ')); }
        attachments.appendChild(link);
        if (a.content_id) { attachments.appendChild(node('span', ' ')); attachments.appendChild(node('span', t('in the message'), 'badge info')); }
      });
    }
    current = m;
    renderActions();
    smimeInspect(m);
    el('message-html-toggle').hidden = !m.html || m.truncated;
    renderHtml();
  };
  // The message after the cursor is fetched while the reader is still on
  // this one, when the browser has an idle moment for it; opening it then
  // costs nothing. Never in the small DOM the page is tested in, which has
  // no idle moments to offer.
  var prefetched = {};
  var prefetchNext = function () {
    var idle = window.requestIdleCallback || window.requestAnimationFrame;
    if (typeof idle !== 'function' || cursor < 0 || !listRows[cursor + 1]) { return; }
    var id = listRows[cursor + 1].id;
    if (prefetched[id] || (current && current.id === id)) { return; }
    idle(function () {
      call('GET', '/api/v1/me/messages/' + id).then(function (result) {
        if (result.status === 200 && result.data) {
          prefetched[id] = result.data;
          var keys = Object.keys(prefetched);
          if (keys.length > 6) { delete prefetched[keys[0]]; }
        }
      });
    });
  };
  var openMessage = function (id) {
    say('mail-status', '', true);
    if (current && current.id === id) { renderMessage(current); return Promise.resolve(current); }
    var show = function (data) {
      showHtml = false;
      el('message-html-toggle').textContent = 'Show as sent';
      renderMessage(data);
      if (!data.flags.seen) { setFlags({ seen: true }, true); }
      offlineKeep('m' + data.id, data);
      return data;
    };
    if (prefetched[id]) { var ready = prefetched[id]; delete prefetched[id]; return Promise.resolve(show(ready)); }
    return call('GET', '/api/v1/me/messages/' + id).then(function (result) {
      if (result.status === 200 && result.data) { return show(result.data); }
      var saved = result.status === 0 ? offlineRead('m' + id) : null;
      if (saved) { offlineNote(true); return show(saved); }
      say('mail-status', describe(result, t('Could not open the message')), false);
      return null;
    });
  };
  var setFlags = function (flags, quiet) {
    if (!current) { return; }
    var id = current.id;
    return call('PUT', '/api/v1/me/messages/' + id + '/flags', flags).then(function (result) {
      if (result.status === 200 && result.data) {
        if (current && current.id === id) { current.flags = result.data.flags; renderActions(); }
        loadFolders();
        return;
      }
      if (!quiet) { say('mail-status', describe(result, t('Could not change the flags')), false); }
    });
  };

  // ---- Compose ------------------------------------------------------------
  var replyTo = null;
  var draftId = 0;
  var carried = [];
  var composeKey = '';
  var identities = [];
  var signature = { enabled: false, text: '' };
  var addressOf = function (header) {
    var m = /<([^>]+)>/.exec(header || '');
    return m ? m[1] : (header || '').trim();
  };
  var textOf = function (message) {
    var text = message.text || '';
    if (!text && message.html) { text = new DOMParser().parseFromString(message.html, 'text/html').body.textContent || ''; }
    return text;
  };
  var quoted = function (message) {
    return textOf(message).split('\n').map(function (line) { return '> ' + line; }).join('\n');
  };
  // A new message's key is its own, so a send queued from one form is
  // never mistaken for the next message written in it.
  var newComposeKey = function () { return 'new:' + Date.now(); };
  var blankCompose = function () {
    // A send waiting on the undo delay goes now rather than being lost
    // with the form it was written in.
    flushPending();
    ['compose-to', 'compose-cc', 'compose-bcc', 'compose-subject', 'compose-text'].forEach(function (id) { el(id).value = ''; });
    carried = []; replyTo = null; draftId = 0; dropped = [];
    el('compose-receipt').checked = false;
    el('compose-sign').checked = false; el('compose-encrypt').checked = false; smimeComposeHints();
    if (richOn) { setRich(false); }
    selectIdentity('');
    el('compose-files').value = ''; el('compose-files-note').textContent = '';
    resetLinked();
    say('compose-status', '', true);
  };
  var carryAttachments = function (message) {
    carried = [];
    var list = message.attachments || [];
    if (!list.length) { return Promise.resolve(); }
    el('compose-files-note').textContent = 'Fetching ' + list.length + ' attachment(s)...';
    return Promise.all(list.map(function (a) {
      return fetch('/api/v1/me/messages/' + message.id + '/attachments/' + a.index, { cache: 'no-store', credentials: 'same-origin' }).then(function (response) {
        if (response.status !== 200) { throw new Error(a.name); }
        return response.blob();
      }).then(function (blob) {
        return new Promise(function (resolve, reject) {
          var reader = new FileReader();
          reader.onload = function () { resolve({ name: a.name, type: a.content_type || blob.type || 'application/octet-stream', data: String(reader.result).split(',')[1] || '' }); };
          reader.onerror = function () { reject(new Error(a.name)); };
          reader.readAsDataURL(blob);
        });
      });
    })).then(function (files) {
      carried = files;
      el('compose-files-note').textContent = 'Attached from the original: ' + files.map(function (f) { return f.name; }).join(', ');
    }, function (why) {
      carried = [];
      el('compose-files-note').textContent = 'The attachment ' + why.message + ' could not be fetched, so it is not attached.';
    });
  };
  // Reply, Reply all, Forward and Edit draft are addresses of their own, so
  // that a reload keeps the half-written reply's subject and quoting, and Back
  // out of the form and in again does not silently start a different message.
  var prime = function (mode, message) {
    blankCompose();
    var subject = message.subject || '';
    if (mode === 'draft') {
      draftId = message.id;
      el('compose-to').value = message.to || '';
      el('compose-cc').value = message.cc || '';
      el('compose-subject').value = subject;
      el('compose-text').value = message.text || '';
      return carryAttachments(message);
    }
    if (mode === 'forward') {
      if (!/^fwd?:/i.test(subject)) { subject = 'Fwd: ' + subject; }
      el('compose-subject').value = subject;
      el('compose-text').value = '\n\n---------- Forwarded message ----------\nFrom: ' + (message.from || '') + '\nDate: ' + (message.date || message.received) + '\nSubject: ' + (message.subject || '') + '\nTo: ' + (message.to || '') + '\n\n' + textOf(message);
      return carryAttachments(message);
    }
    if (!/^re:/i.test(subject)) { subject = 'Re: ' + subject; }
    replyTo = message.message_id ? { message_id: message.message_id, references: ((message.references || '') + ' ' + message.message_id).trim(), id: message.id } : null;
    el('compose-to').value = addressOf(message.from);
    el('compose-subject').value = subject;
    el('compose-text').value = '\n\nOn ' + (message.date || message.received) + ', ' + (message.from || '') + ' wrote:\n' + quoted(message);
    if (mode === 'replyall') {
      // Everyone on the message but this account, once each, sender first.
      var seen = {}; var others = [];
      ((message.to || '') + ',' + (message.cc || '')).split(/[,;]/).forEach(function (part) {
        var a = addressOf(part); var key = a.toLowerCase();
        if (!a || key === me || key === addressOf(message.from).toLowerCase() || seen[key]) { return; }
        seen[key] = true; others.push(a);
      });
      el('compose-cc').value = others.join(', ');
    }
    return Promise.resolve();
  };
  var composeBody = function () {
    var body = { to: el('compose-to').value, cc: el('compose-cc').value, bcc: el('compose-bcc').value, subject: el('compose-subject').value, text: el('compose-text').value };
    if (!el('compose-from').hidden) { body.from = el('compose-from').value; }
    if (el('compose-receipt').checked) { body.receipt = true; }
    if (el('compose-sign').checked) { body.sign = true; }
    if (el('compose-encrypt').checked) { body.encrypt = true; }
    if (richOn) { body.html = cleanHtml(el('compose-editor')); body.text = plainOf(el('compose-editor')).replace(/\n+$/, ''); }
    if (replyTo) { body.in_reply_to = replyTo.message_id; body.references = replyTo.references; body.answered_id = replyTo.id; }
    return body;
  };
  // ---- Files sent as links: above the domain's threshold a file goes to the
  // server's store in chunks and the message carries a link to it.
  var filesPolicy = { link_above_kb: 8192, days: 14, max_mb: 100, quota_mb: 1024 };
  var loadFilesPolicy = function () {
    return call('GET', '/api/v1/me/files').then(function (result) {
      if (result.status === 200 && result.data && result.data.policy) { filesPolicy = result.data.policy; }
    });
  };
  var linked = [];
  var linkedByKey = {};
  var fileKey = function (f) { return f.name + ':' + f.size + ':' + (f.lastModified || 0); };
  var htmlText = function (s) { return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;'); };
  var whenExpires = function (seconds) { return new Date(seconds * 1000).toLocaleDateString(); };
  var renderLinked = function () {
    var box = el('compose-links');
    clear(box);
    box.hidden = !linked.length;
    if (!linked.length) { return; }
    box.appendChild(node('span', t('Sent as links, until ') + whenExpires(linked[0].expires) + ': '));
    linked.forEach(function (f, i) {
      if (i > 0) { box.appendChild(node('span', ', ')); }
      box.appendChild(node('span', f.name + ' (' + format(f.size) + ')'));
    });
  };
  var CHUNK = 4 * 1024 * 1024;
  var uploadLinked = function (file) {
    var record = null;
    var offset = 0;
    var next = function () {
      if (offset >= file.size) { return record; }
      el('compose-files-note').textContent = 'Sending ' + file.name + ' as a link: ' + Math.round(100 * offset / file.size) + '%';
      var slice = file.slice(offset, Math.min(file.size, offset + CHUNK));
      return fetch('/api/v1/me/files/' + record.id + '/content?offset=' + offset, { method: 'PUT', body: slice, headers: { 'X-Requested-With': 'hMailServer', 'Content-Type': 'application/octet-stream' }, cache: 'no-store', credentials: 'same-origin' }).then(function (response) {
        return response.text().then(function (text) {
          var data = null;
          try { data = text ? JSON.parse(text) : null; } catch (e) { data = null; }
          if (response.status === 409 && data && typeof data.stored === 'number' && data.stored !== offset) { offset = data.stored; return next(); }
          if (response.status !== 200 || !data) { throw (data && data.error) || (t('Could not send ') + file.name + ' (' + response.status + ')'); }
          offset = data.stored;
          return next();
        });
      });
    };
    return call('POST', '/api/v1/me/files', { name: file.name, type: file.type || 'application/octet-stream', size: file.size }).then(function (result) {
      if (result.status !== 201 || !result.data) { throw describe(result, t('Could not send ') + file.name + t(' as a link')); }
      record = result.data;
      return next();
    }).then(function (done) {
      var entry = { name: file.name, size: file.size, link: done.link || record.link, expires: record.expires };
      linked.push(entry);
      linkedByKey[fileKey(file)] = entry;
      renderLinked();
      return entry;
    });
  };
  // The links go at the end of what is sent - the text, and the HTML when
  // there is one - never into the form, so a draft saved and then sent
  // carries them once.
  var appendLinks = function (body) {
    if (!linked.length) { return; }
    var until = whenExpires(linked[0].expires);
    var lines = linked.map(function (f) { return '- ' + f.name + ' (' + format(f.size) + '): ' + location.origin + f.link; });
    body.text = (body.text || '') + '\n\nFiles sent as links (until ' + until + '):\n' + lines.join('\n') + '\n';
    if (body.html) {
      var items = linked.map(function (f) { var url = location.origin + f.link; return '<li><a href="' + htmlText(url) + '">' + htmlText(f.name) + '</a> (' + htmlText(format(f.size)) + ')</li>'; });
      body.html += '<p>Files sent as links (until ' + htmlText(until) + '):</p><ul>' + items.join('') + '</ul>';
    }
  };
  var resetLinked = function () { linked = []; linkedByKey = {}; renderLinked(); };

  // Files: those under the domain's threshold are read in the browser and
  // sent as base64 in the same call, twelve megabytes together at most;
  // those above it go to the store first, one after another, and become
  // links.
  var dropped = [];
  var readFiles = function () {
    var files = Array.prototype.slice.call(el('compose-files').files || []).concat(dropped);
    var threshold = Number(filesPolicy.link_above_kb) * 1024;
    var asLink = function (f) { return threshold > 0 && f.size > threshold; };
    var small = files.filter(function (f) { return !asLink(f); });
    var large = files.filter(asLink).filter(function (f) { return !linkedByKey[fileKey(f)]; });
    var total = small.reduce(function (n, f) { return n + f.size; }, 0);
    if (small.length > 20) { return Promise.reject(t('At most 20 files.')); }
    if (total > 12 * 1024 * 1024) { return Promise.reject(t('The files are larger than 12 MB together.')); }
    var uploads = large.reduce(function (chain, f) { return chain.then(function () { return uploadLinked(f); }); }, Promise.resolve());
    return uploads.then(function () {
      if (large.length) { el('compose-files-note').textContent = large.length + ' file' + (large.length === 1 ? '' : 's') + ' sent as ' + (large.length === 1 ? 'a link' : 'links') + '.'; }
      return Promise.all(small.map(function (file) {
        return new Promise(function (resolve, reject) {
          var reader = new FileReader();
          reader.onload = function () { resolve({ name: file.name, type: file.type || 'application/octet-stream', data: String(reader.result).split(',')[1] || '' }); };
          reader.onerror = function () { reject('Could not read ' + file.name); };
          reader.readAsDataURL(file);
        });
      }));
    }, function (why) { return Promise.reject(String(why)); });
  };

  // ---- Held mail ----------------------------------------------------------
  var renderQuarantine = function (held) {
    var list = el('quarantine-list');
    clear(list);
    quarantineOn = !!held.enabled;
    el('held-count').textContent = held.enabled && held.messages.length ? String(held.messages.length) : '';
    if (!held.enabled) { el('quarantine-note').textContent = 'The server does not hold suspected spam for review; it refuses it.'; return; }
    if (!held.messages.length) { el('quarantine-note').textContent = 'Nothing is being held for you.'; return; }
    el('quarantine-note').textContent = held.messages.length + ' message' + (held.messages.length === 1 ? '' : 's') + ' held as suspected spam. Releasing one delivers it to you; deleting one is final.';
    held.messages.forEach(function (m) {
      var row = node('div', undefined, 'frow');
      var body = node('div', undefined, 'fname');
      body.appendChild(node('div', m.subject || t('(no subject)')));
      body.appendChild(node('div', t('From ') + m.sender + ' - ' + m.created + ' - ' + m.reason + t(' (score ') + m.score + ')', 'fmeta'));
      row.appendChild(body);
      var release = button(t('Release to my inbox'), 'btn sm');
      var discard = button(t('Delete'), 'btn ghost sm danger');
      release.addEventListener('click', function () {
        call('POST', '/api/v1/me/quarantine/' + m.id + '/release').then(function (result) {
          say('quarantine-status', result.status === 200 ? t('Released. It will arrive in your inbox shortly.') : describe(result, t('Could not release')), result.status === 200);
          loadQuarantine();
        });
      });
      discard.addEventListener('click', function () {
        call('DELETE', '/api/v1/me/quarantine/' + m.id).then(function (result) {
          say('quarantine-status', result.status === 200 ? t('Deleted.') : describe(result, t('Could not delete')), result.status === 200);
          loadQuarantine();
        });
      });
      row.appendChild(release); row.appendChild(discard);
      list.appendChild(row);
    });
  };
  var loadQuarantine = function () {
    return call('GET', '/api/v1/me/quarantine').then(function (result) {
      if (result.status === 200 && result.data) { renderQuarantine(result.data); }
    });
  };

  // ---- Making, renaming and deleting a folder -----------------------------
  //
  // The server decides what a folder may be called and what may be deleted;
  // this shows the sentence it answers with rather than inventing one.
  var renderFolderAdmin = function () {
    var list = el('folder-admin-list');
    clear(list);
    var parent = el('folder-new-parent');
    clear(parent);
    var top = node('option', t('At the top level')); top.value = ''; parent.appendChild(top);
    allFolders.forEach(function (f) {
      // Compared, not tested for truth. A public folder's account_id is 0,
      // and a falsy account_id walked straight through the truthiness form
      // of this guard - so the card drew Rename and Delete for folders the
      // routes always refuse. The folder picker above compares directly.
      if (foldersById[inboxId] && f.account_id !== foldersById[inboxId].account_id) { return; }
      var option = node('option', f.path); option.value = f.id; parent.appendChild(option);
      var row = node('div', undefined, 'frow');
      var name = node('div', f.path, 'fname');
      row.appendChild(name);
      var held = f.messages === undefined ? (f.count || 0) : f.messages;
      row.appendChild(node('span', held + ' message' + (held === 1 ? '' : 's'), 'fmeta'));
      if (f.writable === false) { row.appendChild(node('span', 'read-only', 'badge info')); list.appendChild(row); return; }
      var isInbox = f.path.toUpperCase() === 'INBOX';
      if (f.special_use) { row.appendChild(node('span', t('kept for ') + f.special_use, 'badge info')); }
      if (isInbox) { list.appendChild(row); return; }
      var rename = button(t('Rename'));
      rename.addEventListener('click', function () {
        clear(row);
        // The whole path, because that is what the server renames by: editing
        // the part before the delimiter moves the folder, and its subfolders
        // go with it. Showing only the leaf would make a move look like a
        // rename, and a rename to the top level look like nothing.
        var input = document.createElement('input'); input.type = 'text'; input.value = f.path; input.setAttribute('maxlength', '255');
        input.setAttribute('aria-label', t('The full name of this folder, parts separated by ') + delimiter);
        var save = button(t('Save'), 'btn sm');
        var cancel = button(t('Cancel'));
        save.addEventListener('click', function () {
          call('PUT', '/api/v1/me/folders/' + f.id, { name: input.value.trim() }).then(function (result) {
            if (result.status === 200) { say('folder-admin-status', t('Renamed.'), true); loadFolders().then(renderFolderAdmin); return; }
            say('folder-admin-status', describe(result, t('Could not rename the folder')), false);
            renderFolderAdmin();
          });
        });
        cancel.addEventListener('click', renderFolderAdmin);
        row.appendChild(input); row.appendChild(save); row.appendChild(cancel);
      });
      row.appendChild(rename);
      // The server refuses to delete a folder it has designated for a special
      // use, and the inbox, and it is right to: there is no undo here. Not
      // offering it is better than a refusal the reader did not ask for.
      if (f.special_use) { list.appendChild(row); return; }
      var remove = button(t('Delete'), 'btn ghost sm danger');
      remove.addEventListener('click', function () {
        if (remove.getAttribute('data-armed') !== 'yes') {
          remove.setAttribute('data-armed', 'yes');
          remove.textContent = t('Really delete?');
          return;
        }
        call('DELETE', '/api/v1/me/folders/' + f.id).then(function (result) {
          if (result.status === 200 || result.status === 204) {
            say('folder-admin-status', t('Deleted.'), true);
            if (state.folderId === f.id) { state.folderId = inboxId; }
            loadFolders().then(renderFolderAdmin);
            return;
          }
          say('folder-admin-status', describe(result, t('Could not delete the folder')), false);
          renderFolderAdmin();
        });
      });
      row.appendChild(remove);
      list.appendChild(row);
    });
  };

  // ---- The change probe ---------------------------------------------------
  //
  // One small GET every few seconds, carrying the token the last answer gave.
  // The server says whether anything changed and what each folder now holds;
  // when something did, the folder list and the open listing are read again
  // and the reader is put back where they were. Nothing is polled while the
  // tab is in the background - the browser tells us, and we stop until it
  // tells us otherwise, so a portal left open all day costs nothing.
  var probe = { token: '', timer: 0, paused: false, off: false };
  var probeEvery = 6000;
  var probeSlowly = 30000;
  var probeIn = probeEvery;
  var setProbeState = function (text, stale) {
    el('probe-state').textContent = text;
    el('probe-dot').classList.toggle('stale', !!stale);
  };
  var applyCounts = function (folders) {
    var fresh = false;
    var grew = [];
    folders.forEach(function (f) {
      var known = foldersById[f.id];
      if (!known) { fresh = true; return; }
      if (f.unseen > known.unseen && notifyWanted(f.id)) { grew.push({ folder: known, added: f.unseen - known.unseen, unseen: f.unseen }); }
      known.unseen = f.unseen;
      known.count = f.count;
    });
    notifyNewMail(grew);
    updateTitle();
    if (fresh) { return true; }
    var nav = el('folder-nav');
    var children = nav.children;
    for (var i = 0; i < children.length; i++) {
      var route = children[i].getAttribute('data-route') || '';
      var id = Number(route.replace('/f/', ''));
      var folder = foldersById[id];
      var count = children[i].children[2];
      if (folder && count) { count.textContent = folder.unseen ? String(folder.unseen) : ''; }
    }
    return false;
  };
  var probeStop = function () { if (probe.timer) { clearTimeout(probe.timer); probe.timer = 0; } };
  var probeLater = function () {
    probeStop();
    if (el('account').hidden || probe.off) { return; }
    probe.timer = setTimeout(probeNow, probeIn);
  };
  var probeNow = function () {
    probeStop();
    if (el('account').hidden || probe.off) { return Promise.resolve(); }
    // A background tab stops asking - unless notifications are on, in
    // which case it keeps asking, slowly, since that is what they are for.
    if (document.hidden && pref('notify') !== '1') { probe.paused = true; setProbeState('Paused - this tab is in the background', true); return Promise.resolve(); }
    probe.paused = false;
    return call('GET', '/api/v1/me/changes' + (probe.token ? '?since=' + encodeURIComponent(probe.token) : '')).then(function (result) {
      if (result.status === 200 && result.data) {
        var had = !!probe.token;
        probe.token = result.data.token || probe.token;
        var unknown = applyCounts(result.data.folders || []);
        probeIn = document.hidden ? probeSlowly : probeEvery;
        setProbeState('Watching for new mail', false);
        if (had && (result.data.changed || unknown)) {
          loadFolders().then(function () {
            if (!el('mail-section').hidden && el('message-view').hidden) { reloadKeepingPlace(); }
          });
        }
      } else if (result.status === 404 || result.status === 403) {
        // An older server, serving this page with no change route behind it -
        // 404 if it does not know the path, 403 if it does not know it is one
        // of the account's own. Asking it again every six seconds for the rest
        // of the day would be a defect of this page, so it is asked once.
        probe.off = true;
        setProbeState('This server does not report changes', true);
        return;
      } else if (result.status !== 401) {
        // Something transient - a restart, a rate limit. Keep watching, but
        // stop leaning on it.
        probeIn = probeSlowly;
        setProbeState('Not watching (' + result.status + ')', true);
      }
      probeLater();
    });
  };

  // ---- Routing ------------------------------------------------------------
  var pending = '';
  var showMail = function (folderId, params) {
    state.folderId = folderId;
    state.query = params.q || '';
    state.everywhere = false;
    state.before = Number(params.before || 0);
    el('mail-search').value = state.query;
    el('mail-search-everywhere').checked = false;
    showPanel('mail-section', folderName(folderId) + (state.query ? ' - \'' + state.query + '\'' : ''));
    markNav('/f/' + folderId);
    showList();
    renderListTools();
    if (lastListing && lastListing.key === listingKey()) {
      selected = lastListing.selected || {};
      renderMessages(lastListing.page);
      var back = -1;
      listRows.forEach(function (r, i) { if (r.id === lastListing.cursorId) { back = i; } });
      if (back >= 0) { setCursor(back, true); }
      el('content').scrollTop = lastListing.scroll || 0;
      reloadKeepingPlace();
      return;
    }
    loadMessages();
  };
  var showSearch = function (params) {
    state.query = params.q || '';
    state.everywhere = true;
    state.before = 0;
    el('mail-search').value = state.query;
    el('mail-search-everywhere').checked = true;
    showPanel('mail-section', 'Search - \'' + state.query + '\'');
    markNav('');
    showList();
    renderListTools();
    loadMessages();
  };
  var showMessage = function (id) {
    showPanel('mail-section', folderName(state.folderId));
    markNav(state.everywhere ? '' : '/f/' + state.folderId);
    openMessage(id).then(function (m) {
      if (m && m.folder_id) { markNav(state.everywhere ? '' : '/f/' + state.folderId); }
    });
  };
  var showCompose = function (params) {
    showPanel('compose-section', 'New message');
    markNav('');
    var mode = params.reply !== undefined ? 'reply' : params.replyall !== undefined ? 'replyall' :
      params.forward !== undefined ? 'forward' : params.draft !== undefined ? 'draft' : 'new';
    var id = Number(params[mode] || 0);
    var key = mode + ':' + id;
    if (composeKey === key) { el('compose-to').focus(); return; }
    composeKey = key;
    if (mode === 'new' || !id) { blankCompose(); addSignature(); el('compose-to').focus(); return; }
    if (current && current.id === id) { var was = current; prime(mode, was).then(function () { afterPrime(mode, was); }); return; }
    call('GET', '/api/v1/me/messages/' + id).then(function (result) {
      if (result.status === 200 && result.data) { prime(mode, result.data).then(function () { afterPrime(mode, result.data); }); return; }
      say('compose-status', describe(result, t('Could not read the message being answered')), false);
    });
  };
  // The one-time secret is for the moment it is made: it leaves the page
  // with the view that showed it, and with the account.
  var forgetSecret = function () { el('apppassword-new').hidden = true; el('apppassword-secret').textContent = ''; };
  var route = function () {
    if (el('account').hidden) { return; }
    var r = parseHash();
    var head = r.parts[0] || '';
    if (head !== 'security') { forgetSecret(); }
    if (head === 'f' && r.parts[1]) { cameFromList = true; showMail(Number(r.parts[1]), r.params); return; }
    if (head === 'search') { cameFromList = true; showSearch(r.params); return; }
    if (head === 'm' && r.parts[1]) { showMessage(Number(r.parts[1])); return; }
    if (head === 'compose') { showCompose(r.params); return; }
    if (head === 'held') { showPanel('quarantine-section', 'Held as suspected spam'); markNav('/held'); loadQuarantine(); return; }
    if (head === 'folders') { showPanel('folders-section', 'Manage folders'); markNav('/folders'); renderFolderAdmin(); return; }
    if (head === 'settings') { showPanel('settings-section', 'Settings'); markNav('/settings'); return; }
    if (head === 'filters') { showPanel('filter-section', 'Filters'); markNav('/filters'); return; }
    if (head === 'contacts') { showPanel('contacts-section', 'Contacts'); markNav('/contacts'); loadContacts(); return; }
    if (head === 'away') { showPanel('away-section', 'Away and forwarding'); markNav('/away'); renderAwayAddresses(); loadScheduled(); return; }
    if (head === 'storage') { showPanel('storage-section', 'Storage'); markNav('/storage'); loadStorage(); loadFiles(); return; }
    if (head === 'security') { showPanel('security-section', 'Security'); markNav('/security'); loadSecurity(); return; }
    if (head === 'password') {
      if (directoryLinked) { replaceWith('/f/' + inboxId); return; }
      showPanel('password-section', 'Change password'); markNav('/password'); return;
    }
    replaceWith('/f/' + (state.folderId || inboxId));
  };

  // ---- Sign in and sign out -----------------------------------------------
  // Everything the signed-in account put in this page. Called when the
  // gate goes back up, because the next person to use this browser must
  // not be shown any of it: openMessage and showMail both render from
  // these caches before they ask the server, and the fragment in the
  // address bar survives a sign-out, so a stale #/m/<id> would repaint
  // the previous account's message to the new one without a request.
  var forgetAccount = function () {
    // A send still waiting cannot go on a session that has ended.
    cancelUndo();
    me = '';
    identities = [];
    signature = { enabled: false, text: '' };
    prefs = {};
    document.title = baseTitle;
    offlineForget();
    prefetched = {};
    current = null;
    lastListing = null;
    listRows = [];
    selected = {};
    allFolders = [];
    foldersById = {};
    inboxId = 0;
    inlineFor = { id: 0, images: null };
    showHtml = false;
    composeKey = '';
    blankCompose();
    forgetSecret();
    var frame = el('message-html');
    if (frame) { frame.hidden = true; frame.removeAttribute('src'); }
    ['message-list', 'folder-nav', 'message-text', 'message-attachments'].forEach(function (id) {
      var node = el(id);
      if (node) { node.innerHTML = ''; }
    });
    // The address bar too, so the router has nothing to re-open. Put to
    // the root route rather than rebuilt from the location: replaceState
    // leaves no history entry and fires no hashchange, and '#/' is the one
    // address that names nothing - the gate is up now, and a later sign-in
    // lands on the inbox from it.
    if (location.hash && location.hash !== '#/') {
      try { history.replaceState(null, '', '#/'); } catch (e) { location.hash = '#/'; }
    }
  };
  var showSignIn = function () {
    probeStop();
    forgetAccount();
    el('account').hidden = true;
    el('signin').hidden = false;
    el('address').focus();
  };
  var render = function (account) {
    el('who').textContent = account.address;
    el('password-user').value = account.address;
    var used = account.quota.used_bytes, limit = account.quota.limit_mb * 1048576;
    if (limit > 0) {
      el('quota').textContent = format(used) + ' of ' + account.quota.limit_mb + ' MB used';
      el('quota-bar').style.width = Math.min(100, Math.round(100 * used / limit)) + '%';
    } else {
      el('quota').textContent = format(used) + ' used, no limit';
      el('quota-bar').style.width = '0';
    }
    el('password-changed').textContent = account.password_changed ? 'Password last changed ' + account.password_changed : '';
    el('vacation-enabled').checked = !!account.vacation.enabled;
    el('vacation-subject').value = account.vacation.subject || '';
    el('vacation-message').value = account.vacation.message || '';
    el('vacation-expires').checked = !!account.vacation.expires;
    el('vacation-expires-date').value = account.vacation.expires_date || '';
    el('otp-row').hidden = !account.second_factor;
    directoryLinked = !!account.directory_linked;
    var nav = el('view-nav').children;
    for (var i = 0; i < nav.length; i++) {
      if (nav[i].getAttribute('data-route') === '/password') { nav[i].hidden = directoryLinked; }
    }
    el('signin').hidden = true;
    el('account').hidden = false;
  };
  var renderSettings = function (s) {
    el('name-first').value = s.name.first || '';
    el('name-last').value = s.name.last || '';
    el('forward-enabled').checked = !!s.forwarding.enabled;
    el('forward-address').value = s.forwarding.address || '';
    el('forward-keep').checked = !!s.forwarding.keep_original;
    el('signature-enabled').checked = !!s.signature.enabled;
    el('signature-text').value = s.signature.text || '';
    signature = { enabled: !!s.signature.enabled, text: s.signature.text || '' };
  };
  var loadSettings = function () {
    call('GET', '/api/v1/me/settings').then(function (result) {
      if (result.status === 200 && result.data) { renderSettings(result.data); }
    });
    call('GET', '/api/v1/me/filters').then(function (result) {
      if (result.status === 200 && result.data) { el('filter-script').value = result.data.active || ''; renderRules(result.data.active || ''); }
    });
  };
  var load = function (quiet) {
    return call('GET', '/api/v1/me').then(function (result) {
      if (result.status === 200 && result.data) {
        me = String(result.data.address || '').toLowerCase();
        render(result.data);
        loadQuarantine();
        loadSettings();
        loadPrefs();
        loadIdentities();
        loadBranding();
        loadFilesPolicy();
        loadSmime();
        return loadFolders().then(function () {
          if (pending) { var wanted = pending; pending = ''; replaceWith(wanted); }
          else { route(); }
          probeNow();
          return true;
        });
      }
      if (!quiet) { say('signin-status', describe(result, t('Could not sign in')), false); }
      pending = parseHash().hash;
      showSignIn();
      return false;
    });
  };

  // ---- Wiring -------------------------------------------------------------
  el('signin-form').addEventListener('submit', function (event) {
    event.preventDefault();
    say('signin-status', '', true);
    var address = el('address').value.trim(), password = el('password').value;
    smimePassword = password;
    el('password').value = '';
    var basic = 'Basic ' + btoa(unescape(encodeURIComponent(address + ':' + password)));
    // A new session is a new server as far as this page knows: whatever the
    // last one answered about the change route, ask again.
    probe.token = ''; probe.off = false; probeIn = probeEvery;
    call('POST', '/api/v1/session', undefined, { 'Authorization': basic }).then(function (result) {
      if (result.status === 201) { load(false); return; }
      say('signin-status', describe(result, t('Could not sign in')), false);
    });
  });
  el('signout').addEventListener('click', function () {
    flushPending().then(function () { return call('DELETE', '/api/v1/session'); }).then(function () { probe.token = ''; probe.off = false; showSignIn(); });
  });
  el('theme-btn').addEventListener('click', function () {
    var next = document.body.getAttribute('data-theme') === 'light' ? 'dark' : 'light';
    applyTheme(next);
    if (!el('account').hidden) { savePrefs({ theme: next }); }
  });
  el('nav-compose').addEventListener('click', function () { go('/compose'); });
  (function () {
    var nav = el('view-nav').children;
    for (var i = 0; i < nav.length; i++) {
      (function (b) { b.addEventListener('click', function () { go(b.getAttribute('data-route')); }); })(nav[i]);
    }
  })();
  el('message-back').addEventListener('click', function () {
    if (cameFromList && history.length > 1) { history.back(); return; }
    go(listHash());
  });
  el('message-unread').addEventListener('click', function () { if (current) { setFlags({ seen: !current.flags.seen }, false); } });
  el('message-flag').addEventListener('click', function () { if (current) { setFlags({ flagged: !current.flags.flagged }, false); } });
  el('message-delete').addEventListener('click', function () {
    if (!current) { return; }
    call('DELETE', '/api/v1/me/messages/' + current.id).then(function (result) {
      if (result.status === 200) {
        say('mail-status', result.data && result.data.deleted ? t('Deleted.') : t('Moved to the trash folder.'), true);
        current = null; lastListing = null;
        loadFolders(); go(listHash());
        return;
      }
      say('mail-status', describe(result, t('Could not delete')), false);
    });
  });
  var remoteAllowed = { id: 0 };
  var remoteSenders = function () { return String(pref('remote_senders') || '').toLowerCase().split(',').filter(Boolean); };
  el('remote-once').addEventListener('click', function () { if (current) { remoteAllowed = { id: current.id }; renderHtml(); } });
  el('remote-always').addEventListener('click', function () {
    if (!current) { return; }
    var sender = addressOf(current.from).toLowerCase();
    var list = remoteSenders();
    if (sender && list.indexOf(sender) < 0) { list.push(sender); }
    savePrefs({ remote_senders: list.join(',') }).then(function (ok) {
      if (!ok) { say('mail-status', 'Could not remember this sender: the list is full.', false); return; }
      renderHtml();
    });
  });
  // Links whose text names one site and go to another, and a sender whose
  // name is a contact's but whose address is not: the two shapes of a
  // forgery a reader can be shown without being asked to read headers.
  var hostOf = function (url) { try { return new URL(url, location.origin).hostname.toLowerCase().replace(/^www\./, ''); } catch (e) { return ''; } };
  var displayNameOf = function (header) { var m = /^\s*"?([^"<]*?)"?\s*</.exec(header || ''); return m ? m[1].trim() : ''; };
  // Read with the parser, which is inert and linear: a regex over a
  // megabyte of HTML a sender wrote could freeze the tab.
  var linkWarnings = function (html) {
    var out = [];
    var doc;
    try { doc = new DOMParser().parseFromString(html, 'text/html'); } catch (e) { return out; }
    if (!doc || typeof doc.getElementsByTagName !== 'function') { return out; }
    var anchors = doc.getElementsByTagName('a');
    for (var i = 0; i < anchors.length && out.length < 5; i++) {
      var text = (anchors[i].textContent || '').trim();
      if (!/^(https?:\/\/)?(www\.)?[a-z0-9-]+(\.[a-z0-9-]+)+(\/\S*)?$/i.test(text)) { continue; }
      var said = hostOf(/^https?:/i.test(text) ? text : 'https://' + text);
      var goes = hostOf(anchors[i].getAttribute('href') || '');
      if (said && goes && said !== goes) { out.push('A link that says ' + text + ' goes to ' + goes + '.'); }
    }
    return out;
  };
  var showWarnings = function (list) {
    var box = el('message-warnings');
    clear(box);
    list.forEach(function (w) { box.appendChild(node('div', w)); });
    box.hidden = !list.length;
  };
  var ensureContacts = function () {
    if (contactsCache) { return Promise.resolve(contactsCache); }
    return loadContacts();
  };
  var warnAbout = function (m) {
    var warnings = linkWarnings(m.html || '');
    showWarnings(warnings);
    ensureContacts().then(function (list) {
      if (current !== m) { return; }
      var name = displayNameOf(m.from);
      var address = addressOf(m.from).toLowerCase();
      if (!name) { return; }
      var same = (list || []).filter(function (c) { return (c.name || '').toLowerCase() === name.toLowerCase() && c.address.toLowerCase() !== address; })[0];
      if (same) { warnings.push('The sender is named like your contact ' + same.name + ' <' + same.address + '>, but this came from ' + address + '.'); showWarnings(warnings); }
    });
  };
  // Images among the attachments are shown; a PDF is previewed on request,
  // in a frame of its own, from bytes the page fetched itself.
  var fetchAsDataUrl = function (id, index) {
    return fetch('/api/v1/me/messages/' + id + '/attachments/' + index, { cache: 'no-store', credentials: 'same-origin' }).then(function (response) {
      if (response.status !== 200) { return null; }
      return response.blob();
    }).then(function (blob) {
      if (!blob || String(blob.type || '').indexOf('image/') !== 0) { return null; }
      return new Promise(function (resolve) {
        var reader = new FileReader();
        reader.onload = function () { resolve(String(reader.result)); };
        reader.onerror = function () { resolve(null); };
        reader.readAsDataURL(blob);
      });
    }, function () { return null; });
  };
  // Previews keep to the inline budget - twelve parts, twelve megabytes -
  // and a part the HTML already shows in place is not fetched again.
  var previewAttachments = function (m) {
    var previews = el('message-previews');
    clear(previews);
    var budget = INLINE_TOGETHER, count = 0;
    (m.attachments || []).forEach(function (a) {
      var type = String(a.content_type || '').toLowerCase();
      var cid = contentIdOf(a);
      if (cid && m.html && m.html.toLowerCase().indexOf('cid:' + cid.toLowerCase()) >= 0) { return; }
      if (count >= INLINE_MOST || a.size > budget) { return; }
      if (RENDERS[type] && a.size <= INLINE_EACH) {
        count++; budget -= a.size;
        fetchAsDataUrl(m.id, a.index).then(function (url) {
          if (!url || current !== m) { return; }
          var img = document.createElement('img'); img.setAttribute('loading', 'lazy'); img.setAttribute('src', url); img.setAttribute('alt', a.name); img.setAttribute('title', a.name);
          previews.appendChild(img);
        });
      } else if (type === 'application/pdf' && a.size <= INLINE_TOGETHER) {
        var show = button(t('Preview ') + a.name);
        show.addEventListener('click', function () {
          fetch('/api/v1/me/messages/' + m.id + '/attachments/' + a.index, { cache: 'no-store', credentials: 'same-origin' }).then(function (response) { return response.status === 200 ? response.blob() : null; }).then(function (blob) {
            if (!blob || current !== m) { return; }
            var frame = document.createElement('iframe');
            frame.setAttribute('referrerpolicy', 'no-referrer'); frame.setAttribute('title', a.name);
            frame.setAttribute('src', URL.createObjectURL(new Blob([blob], { type: 'application/pdf' })));
            previews.appendChild(frame);
            show.hidden = true;
          });
        });
        previews.appendChild(show);
      }
    });
  };
  var fileCurrent = function (to) {
    if (!current) { return; }
    fileMessage(current.id, to).then(function (result) {
      if (afterFiling(result, filingLabel(to, result))) { current = null; go(listHash()); }
    });
  };
  el('message-archive').addEventListener('click', function () { fileCurrent('archive'); });
  el('message-junk').addEventListener('click', function () { if (current) { fileCurrent(junkTargetFor(current.folder_id)); } });
  el('message-headers-toggle').addEventListener('click', function () {
    var pane = el('message-headers');
    pane.hidden = !pane.hidden;
    el('message-headers-toggle').textContent = pane.hidden ? 'Headers' : 'Hide headers';
  });
  document.addEventListener('keydown', function (event) {
    var tag = (event.target && event.target.tagName) || '';
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || event.ctrlKey || event.metaKey || event.altKey) { return; }
    if (el('account').hidden || el('mail-section').hidden || el('message-view').hidden || !current) { return; }
    if (event.key === 'e') { fileCurrent('archive'); event.preventDefault(); }
    else if (event.key === '!') { fileCurrent(junkTargetFor(current.folder_id)); event.preventDefault(); }
    else if (event.key === '#') { fileCurrent('delete'); event.preventDefault(); }
    else if (event.key === 'l') { toggleLabelMenu(); event.preventDefault(); }
  });
  el('message-move-go').addEventListener('click', function () {
    var target = el('message-move').value;
    if (!current || !target) { return; }
    call('POST', '/api/v1/me/messages/' + current.id + '/move', { folder_id: Number(target) }).then(function (result) {
      if (result.status === 200) {
        say('mail-status', t('Moved.'), true);
        current = null; lastListing = null;
        loadFolders(); go(listHash());
        return;
      }
      say('mail-status', describe(result, t('Could not move')), false);
    });
  });
  el('message-html-toggle').addEventListener('click', function () {
    showHtml = !showHtml;
    el('message-html-toggle').textContent = showHtml ? 'Show as text' : 'Show as sent';
    renderHtml();
  });
  el('message-reply').addEventListener('click', function () { if (current) { go('/compose?reply=' + current.id); } });
  el('message-reply-all').addEventListener('click', function () { if (current) { go('/compose?replyall=' + current.id); } });
  el('message-forward').addEventListener('click', function () { if (current) { go('/compose?forward=' + current.id); } });
  el('message-edit').addEventListener('click', function () { if (current) { go('/compose?draft=' + current.id); } });
  el('compose-discard').addEventListener('click', function () { composeKey = newComposeKey(); blankCompose(); go('/compose'); });
  // ---- Contacts ------------------------------------------------------------
  var contactsCache = null;
  var renderContacts = function (list) {
    var rows = el('contact-rows');
    rows.textContent = '';
    list.forEach(function (c) {
      var tr = document.createElement('tr');
      var name = document.createElement('td'); name.textContent = c.name || '';
      var address = document.createElement('td'); address.textContent = c.address;
      var from = document.createElement('td'); from.textContent = c.source === 'collected' ? t('a message you sent') : 'you';
      var actions = document.createElement('td');
      var write = document.createElement('button'); write.type = 'button'; write.className = 'btn ghost'; write.textContent = t('Write');
      write.addEventListener('click', function () { composeTo(c.name ? c.name + ' <' + c.address + '>' : c.address); });
      var remove = document.createElement('button'); remove.type = 'button'; remove.className = 'btn ghost'; remove.textContent = t('Remove');
      remove.addEventListener('click', function () {
        call('DELETE', '/api/v1/me/contacts/' + c.id).then(function (result) {
          if (result.status === 200) { contactsCache = null; loadContacts(); } else { say('contact-status', describe(result, t('Could not remove the contact')), false); }
        });
      });
      actions.appendChild(write); actions.appendChild(remove);
      tr.appendChild(name); tr.appendChild(address); tr.appendChild(from); tr.appendChild(actions);
      rows.appendChild(tr);
    });
    el('contact-empty').hidden = list.length > 0;
    el('contact-table').hidden = list.length === 0;
  };
  var loadContacts = function () {
    return call('GET', '/api/v1/me/contacts?limit=1000').then(function (result) {
      if (result.status !== 200 || !result.data) { say('contact-status', describe(result, t('Could not read the contacts')), false); return []; }
      contactsCache = result.data.contacts || [];
      renderContacts(contactsCache);
      return contactsCache;
    });
  };
  el('contact-form').addEventListener('submit', function (event) {
    event.preventDefault();
    say('contact-status', '', true);
    call('POST', '/api/v1/me/contacts', { name: el('contact-name').value, address: el('contact-address').value }).then(function (result) {
      if (result.status === 201) { el('contact-name').value = ''; el('contact-address').value = ''; contactsCache = null; loadContacts(); return; }
      say('contact-status', describe(result, t('Could not add the contact')), false);
    });
  });

  // ---- To-field completion ---------------------------------------------------
  // The token being typed is the text after the last comma; on each keystroke the
  // address book is asked for it (small, so a fetch per key is fine) and a popup
  // under the field offers up to eight matches. Enter or Tab takes the marked one,
  // Escape closes, arrows move, a click takes.
  var complete = function (inputId) {
    var input = el(inputId);
    var list = document.createElement('ul'); list.className = 'complete'; list.hidden = true; list.setAttribute('role', 'listbox');
    input.parentNode.appendChild(list);
    var items = []; var marked = -1; var timer = null;
    var close = function () { list.hidden = true; list.textContent = ''; items = []; marked = -1; };
    var token = function () { var v = input.value; var i = v.lastIndexOf(','); return { head: i >= 0 ? v.slice(0, i + 1) : '', tail: (i >= 0 ? v.slice(i + 1) : v).replace(/^\s+/, '') }; };
    var take = function (c) { var t = token(); input.value = (t.head ? t.head + ' ' : '') + (c.name ? c.name + ' <' + c.address + '>' : c.address) + ', '; close(); input.focus(); };
    var mark = function (n) { marked = n; for (var i = 0; i < list.children.length; i++) { list.children[i].classList.toggle('on', i === marked); } };
    var show = function (found) {
      close();
      if (!found.length) { return; }
      items = found;
      found.forEach(function (c, i) {
        var li = document.createElement('li'); li.setAttribute('role', 'option');
        var nm = document.createElement('span'); nm.className = 'nm'; nm.textContent = c.name || c.address;
        var ad = document.createElement('span'); ad.className = 'ad'; ad.textContent = c.name ? c.address : '';
        li.appendChild(nm); li.appendChild(ad);
        li.addEventListener('mousedown', function (e) { e.preventDefault(); take(c); });
        list.appendChild(li);
      });
      list.hidden = false; mark(0);
    };
    input.setAttribute('autocomplete', 'off');
    input.addEventListener('input', function () {
      var q = token().tail;
      if (timer) { clearTimeout(timer); }
      if (!q) { close(); return; }
      timer = setTimeout(function () {
        call('GET', '/api/v1/me/contacts?limit=8&q=' + encodeURIComponent(q)).then(function (result) {
          if (result.status !== 200 || !result.data || token().tail !== q) { return; }
          show(result.data.contacts || []);
        });
      }, 120);
    });
    input.addEventListener('keydown', function (e) {
      if (list.hidden) { return; }
      if (e.key === 'ArrowDown') { e.preventDefault(); mark((marked + 1) % items.length); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); mark((marked - 1 + items.length) % items.length); }
      else if (e.key === 'Enter' || e.key === 'Tab') { if (marked >= 0) { e.preventDefault(); take(items[marked]); } }
      else if (e.key === 'Escape') { close(); }
    });
    input.addEventListener('blur', function () { setTimeout(close, 150); });
  };
  complete('compose-to'); complete('compose-cc'); complete('compose-bcc');

  // The request itself, once the undo delay has passed (or at once when
  // there is none). The form is emptied only when it still holds the
  // message that was sent: during the delay the reader may have gone on to
  // write another.
  var submitMessage = function (queued) {
    // A signed or an encrypted message is built here first (smimePrepare);
    // a plain one goes as it is.
    return smimePrepare(queued.body).then(function (prepared) {
      queued.body = prepared;
      return call('POST', '/api/v1/me/messages', prepared);
    }, function (why) {
      say('compose-status', String(why && why.message ? why.message : why), false);
      el('compose-send').disabled = false;
      return { status: -1 };
    }).then(function (result) {
      if (result.status === -1) { return; }
      if (result.status === 0) {
        // No connection: the message waits in this browser and goes when
        // the connection is back. The form is left as it is.
        outboxAdd(queued.body);
        say('compose-status', t('No connection. The message waits here and will be sent when the connection is back.'), false);
        return;
      }
      if (result.status === 201) {
        if (composeKey === queued.key) {
          blankCompose();
          composeKey = newComposeKey();
          if (!el('compose-section').hidden) { replaceWith('/compose'); }
        }
        say('compose-status', t('Sent.'), true);
        if (queued.draft) { call('DELETE', '/api/v1/me/messages/' + queued.draft + '?permanent=1').then(function () { loadFolders(); }); return; }
        loadFolders();
        return;
      }
      say('compose-status', describe(result, t('Could not send')), false);
    });
  };
  // Undo send: for the delay the account chose, the message waits here, in
  // the page, and Undo keeps it in the form untouched. Nothing has reached
  // the server until the bar runs out.
  var pendingSend = null;
  var cancelUndo = function () {
    if (!pendingSend) { return; }
    clearInterval(pendingSend.ticker); clearTimeout(pendingSend.timer);
    pendingSend = null;
    el('undo-bar').hidden = true;
    el('compose-send').disabled = false;
  };
  el('compose-form').addEventListener('submit', function (event) {
    event.preventDefault();
    if (pendingSend) { return; }
    say('compose-status', '', true);
    var body = composeBody();
    readFiles().then(function (files) {
      files = carried.concat(files);
      if (files.length) { body.attachments = files; }
      appendLinks(body);
      var queued = { body: body, key: composeKey, draft: draftId };
      var delay = undoSeconds();
      if (!delay) { return submitMessage(queued); }
      var left = delay;
      el('undo-text').textContent = 'Sending in ' + left + ' s';
      el('undo-bar').hidden = false;
      el('compose-send').disabled = true;
      pendingSend = queued;
      pendingSend.ticker = setInterval(function () { left--; el('undo-text').textContent = 'Sending in ' + Math.max(left, 0) + ' s'; }, 1000);
      pendingSend.timer = setTimeout(function () { var q = pendingSend; cancelUndo(); if (q) { submitMessage(q); } }, delay * 1000);
      return null;
    }, function (why) { say('compose-status', why, false); return null; });
  });
  el('undo-send').addEventListener('click', function () { cancelUndo(); say('compose-status', t('Not sent. The message is still here.'), true); });
  // The send that is waiting goes now: before the form is cleared or
  // written to, and before the account signs out.
  var flushPending = function () {
    if (!pendingSend) { return Promise.resolve(); }
    var q = pendingSend;
    cancelUndo();
    return submitMessage(q);
  };
  // The page closing with a send waiting: the message goes in a request
  // the browser keeps alive after the page, when it fits in one (64 KB);
  // a larger one - attachments - is worth a warning instead.
  window.addEventListener('beforeunload', function (event) {
    if (!pendingSend) { return; }
    var body = JSON.stringify(pendingSend.body);
    if (body.length < 60000) {
      cancelUndo();
      fetch('/api/v1/me/messages', { method: 'POST', keepalive: true, credentials: 'same-origin', headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'hMailServer' }, body: body });
      return;
    }
    event.preventDefault();
    event.returnValue = '';
  });
  el('compose-save').addEventListener('click', function () {
    var body = composeBody();
    if (draftId) { body.replace_id = draftId; }
    readFiles().then(function (files) {
      files = carried.concat(files);
      if (files.length) { body.attachments = files; }
      appendLinks(body);
      return call('POST', '/api/v1/me/drafts', body);
    }, function (why) { say('compose-status', why, false); return null; }).then(function (result) {
      if (!result) { return; }
      if (result.status === 201 && result.data) { draftId = result.data.id; composeKey = 'draft:' + draftId; say('compose-status', t('Draft saved.'), true); loadFolders(); return; }
      say('compose-status', describe(result, t('Could not save the draft')), false);
    });
  });
  // Files dropped anywhere on the form join the ones picked.
  el('compose-form').addEventListener('dragover', function (event) { event.preventDefault(); el('compose-form').classList.add('dropping'); });
  el('compose-form').addEventListener('dragleave', function () { el('compose-form').classList.remove('dropping'); });
  el('compose-form').addEventListener('drop', function (event) {
    el('compose-form').classList.remove('dropping');
    var files = Array.prototype.slice.call((event.dataTransfer && event.dataTransfer.files) || []);
    // Text dropped into a field is the field's; only files are taken here.
    if (!files.length) { return; }
    event.preventDefault();
    files.forEach(function (f) { dropped.push(f); });
    if (files.length) { el('compose-files-note').textContent = 'Dropped: ' + dropped.map(function (f) { return f.name + ' (' + format(f.size) + ')'; }).join(', '); }
  });
  el('compose-files').addEventListener('change', function () {
    var files = Array.prototype.slice.call(el('compose-files').files || []);
    el('compose-files-note').textContent = files.length ? files.map(function (f) { return f.name + ' (' + format(f.size) + ')'; }).join(', ') : '';
  });
  el('mail-search-form').addEventListener('submit', function (event) {
    event.preventDefault();
    var text = el('mail-search').value.trim();
    state.before = 0;
    lastListing = null;
    if (!text) { go('/f/' + (state.folderId || inboxId)); return; }
    if (el('mail-search-everywhere').checked) { go('/search?q=' + encodeURIComponent(text)); return; }
    go('/f/' + (state.folderId || inboxId) + '?q=' + encodeURIComponent(text));
  });
  el('mail-search-clear').addEventListener('click', function () {
    el('mail-search').value = '';
    el('mail-search-everywhere').checked = false;
    state.before = 0;
    lastListing = null;
    go('/f/' + (state.folderId || inboxId));
  });
  el('folder-new-form').addEventListener('submit', function (event) {
    event.preventDefault();
    var name = el('folder-new-name').value.trim();
    var parent = el('folder-new-parent').value;
    var inside = parent && foldersById[Number(parent)] ? foldersById[Number(parent)].path : '';
    // One field, and it is a path: the server makes every part of it that is
    // not there yet, the way IMAP CREATE does.
    var body = { name: inside ? inside + delimiter + name : name };
    call('POST', '/api/v1/me/folders', body).then(function (result) {
      if (result.status === 201 || result.status === 200) {
        el('folder-new-name').value = '';
        say('folder-admin-status', t('Created.'), true);
        loadFolders().then(renderFolderAdmin);
        return;
      }
      say('folder-admin-status', describe(result, t('Could not create the folder')), false);
    });
  });
  el('vacation-form').addEventListener('submit', function (event) {
    event.preventDefault();
    var body = {
      enabled: el('vacation-enabled').checked,
      subject: el('vacation-subject').value,
      message: el('vacation-message').value,
      expires: el('vacation-expires').checked,
      expires_date: el('vacation-expires-date').value
    };
    call('PUT', '/api/v1/me/vacation', body).then(function (result) {
      say('vacation-status', result.status === 200 ? t('Saved.') : describe(result, t('Could not save')), result.status === 200);
    });
  });
  el('settings-form').addEventListener('submit', function (event) {
    event.preventDefault();
    var body = {
      name: { first: el('name-first').value, last: el('name-last').value },
      signature: { enabled: el('signature-enabled').checked, text: el('signature-text').value, html: '' }
    };
    call('PUT', '/api/v1/me/settings', body).then(function (result) {
      if (result.status === 200 && result.data) { renderSettings(result.data); say('settings-status', t('Saved.'), true); return; }
      say('settings-status', describe(result, t('Could not save')), false);
    });
  });
  el('forward-form').addEventListener('submit', function (event) {
    event.preventDefault();
    var body = { forwarding: { enabled: el('forward-enabled').checked, address: el('forward-address').value, keep_original: el('forward-keep').checked } };
    call('PUT', '/api/v1/me/settings', body).then(function (result) {
      if (result.status === 200 && result.data) { renderSettings(result.data); say('forward-status', t('Saved.'), true); return; }
      say('forward-status', describe(result, t('Could not save')), false);
    });
  });

  // ---- Storage ----------------------------------------------------------------
  var loadFiles = function () {
    return call('GET', '/api/v1/me/files').then(function (result) {
      if (result.status !== 200 || !result.data) { say('files-status', describe(result, t('Could not read the files')), false); return; }
      if (result.data.policy) { filesPolicy = result.data.policy; }
      var files = result.data.files || [];
      var rows = el('files-rows');
      clear(rows);
      el('files-empty').hidden = files.length > 0;
      el('files-note').textContent = (Number(filesPolicy.link_above_kb) > 0
        ? 'A file above ' + format(Number(filesPolicy.link_above_kb) * 1024) + ' on a message goes here and the message carries a link that lives ' + filesPolicy.days + ' days. '
        : 'Files are attached to messages here, never sent as links. ')
        + format(result.data.used_bytes || 0) + ' of ' + filesPolicy.quota_mb + ' MB used.';
      files.forEach(function (f) {
        var tr = document.createElement('tr');
        var name = document.createElement('td');
        name.appendChild(node('span', f.name));
        if (f.protected) { name.appendChild(node('span', ' ')); name.appendChild(node('span', t('Password'), 'badge info')); }
        if (!f.complete) { name.appendChild(node('span', ' ')); name.appendChild(node('span', t('Unfinished'), 'badge warn')); }
        tr.appendChild(name);
        tr.appendChild(node('td', format(f.size)));
        tr.appendChild(node('td', String(f.downloads || 0)));
        tr.appendChild(node('td', f.expired ? t('Expired') : whenExpires(f.expires)));
        var actions = document.createElement('td');
        var copy = button(t('Copy link'), 'btn ghost sm');
        copy.addEventListener('click', function () {
          var url = location.origin + f.link;
          if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(url).then(function () { say('files-status', t('Link copied: ') + url, true); }, function () { say('files-status', url, true); });
          } else { say('files-status', url, true); }
        });
        var protect = button(f.protected ? t('Change password') : t('Set password'), 'btn ghost sm');
        protect.addEventListener('click', function () {
          var given = window.prompt('A password for ' + f.name + ' (empty removes it):', '');
          if (given === null) { return; }
          call('PUT', '/api/v1/me/files/' + f.id, { password: given }).then(function (r) {
            say('files-status', r.status === 200 ? (given ? t('Password set.') : t('Password removed.')) : describe(r, t('Could not change it')), r.status === 200);
            loadFiles();
          });
        });
        var extend = button(t('Extend'), 'btn ghost sm');
        extend.addEventListener('click', function () {
          call('PUT', '/api/v1/me/files/' + f.id, { days: filesPolicy.days }).then(function (r) {
            say('files-status', r.status === 200 ? t('Kept for another ') + filesPolicy.days + t(' days.') : describe(r, t('Could not extend it')), r.status === 200);
            loadFiles();
          });
        });
        var remove = button(t('Remove'), 'btn ghost sm danger');
        remove.addEventListener('click', function () {
          call('DELETE', '/api/v1/me/files/' + f.id).then(function (r) {
            say('files-status', r.status === 200 ? t('Removed; the link is dead.') : describe(r, t('Could not remove it')), r.status === 200);
            loadFiles();
          });
        });
        actions.appendChild(copy); actions.appendChild(node('span', ' ')); actions.appendChild(protect); actions.appendChild(node('span', ' ')); actions.appendChild(extend); actions.appendChild(node('span', ' ')); actions.appendChild(remove);
        tr.appendChild(actions);
        rows.appendChild(tr);
      });
    });
  };
  var loadStorage = function () {
    el('storage-clean').textContent = t('Delete'); el('storage-clean').removeAttribute('data-armed');
    return call('GET', '/api/v1/me/storage').then(function (result) {
      if (result.status !== 200 || !result.data) { say('storage-status', describe(result, t('Could not read the storage')), false); return; }
      var s = result.data;
      el('storage-quota').textContent = s.limit_mb > 0
        ? format(s.used_bytes) + ' of ' + s.limit_mb + ' MB used (' + Math.min(100, Math.round(100 * s.used_bytes / (s.limit_mb * 1048576))) + '%)'
        : format(s.used_bytes) + ' used, no limit';
      var rows = el('storage-folder-rows');
      clear(rows);
      (s.folders || []).slice().sort(function (a, b) { return b.bytes - a.bytes; }).forEach(function (f) {
        var tr = document.createElement('tr');
        tr.appendChild(node('td', f.path));
        tr.appendChild(node('td', String(f.messages), 'num'));
        tr.appendChild(node('td', format(f.bytes), 'num'));
        tr.addEventListener('click', function () { go('/f/' + f.id); });
        tr.className = 'clickable';
        rows.appendChild(tr);
      });
      var largest = el('storage-largest-rows');
      clear(largest);
      (s.largest || []).forEach(function (m) {
        var tr = document.createElement('tr');
        tr.appendChild(node('td', m.subject || t('(no subject)')));
        tr.appendChild(node('td', m.from || ''));
        tr.appendChild(node('td', m.folder || ''));
        tr.appendChild(node('td', format(m.size), 'num'));
        tr.className = 'clickable';
        tr.addEventListener('click', function () { state.folderId = m.folder_id; go('/m/' + m.id); });
        largest.appendChild(tr);
      });
      el('storage-largest-empty').hidden = (s.largest || []).length > 0;
      el('storage-largest').hidden = (s.largest || []).length === 0;
    });
  };
  // Asked twice, as Empty this folder is: the first press arms the button,
  // the second deletes. A folder that could not be emptied is named, and
  // the count is of what actually went.
  el('storage-clean-form').addEventListener('submit', function (event) {
    event.preventDefault();
    var b = el('storage-clean');
    if (b.getAttribute('data-armed') !== '1') { b.setAttribute('data-armed', '1'); b.textContent = t('Really delete?'); say('storage-status', t('Press again to delete these for good.'), true); return; }
    b.removeAttribute('data-armed'); b.textContent = t('Delete');
    var days = el('storage-days').value;
    var targets = allFolders.filter(function (f) { return folderIs(f.id, 'Junk') || folderIs(f.id, 'Trash'); });
    if (!targets.length) { say('storage-status', t('There is no Junk or Trash folder to clean.'), false); return; }
    var total = 0, failed = [];
    var chain = Promise.resolve();
    targets.forEach(function (f) {
      chain = chain.then(function () {
        return emptyFolder(f.id, days).then(function (r) { total += r.deleted; if (r.failed) { failed.push(folderName(f.id)); } });
      });
    });
    chain.then(function () {
      if (failed.length) { say('storage-status', tf('{0} deleted; {1} could not be emptied.', total, failed.join(', ')), false); }
      else { say('storage-status', total + t(' deleted.'), true); }
      loadFolders(); loadStorage();
    });
  });

  // ---- Security: app passwords and sessions ----------------------------------
  var ago = function (seconds) {
    if (seconds < 60) { return 'just now'; }
    if (seconds < 3600) { return Math.round(seconds / 60) + ' min ago'; }
    if (seconds < 86400) { return Math.round(seconds / 3600) + ' h ago'; }
    return Math.round(seconds / 86400) + ' d ago';
  };
  var loadAppPasswords = function () {
    return call('GET', '/api/v1/me/app-passwords').then(function (result) {
      if (result.status !== 200 || !result.data) { say('apppassword-status', describe(result, t('Could not read the app passwords')), false); return; }
      var rows = el('apppassword-rows');
      clear(rows);
      var list = result.data.app_passwords || [];
      list.forEach(function (a) {
        var tr = document.createElement('tr');
        tr.appendChild(node('td', a.name));
        tr.appendChild(node('td', a.created || ''));
        tr.appendChild(node('td', a.last_used || 'never'));
        var actions = document.createElement('td');
        var remove = button(t('Remove'));
        remove.addEventListener('click', function () {
          call('DELETE', '/api/v1/me/app-passwords/' + a.id).then(function (r) {
            if (r.status === 200) { say('apppassword-status', t('Removed. Anything still using it will stop signing in.'), true); loadAppPasswords(); }
            else { say('apppassword-status', describe(r, t('Could not remove it')), false); }
          });
        });
        actions.appendChild(remove);
        tr.appendChild(actions);
        rows.appendChild(tr);
      });
      el('apppassword-empty').hidden = list.length > 0;
      el('apppassword-table').hidden = list.length === 0;
    });
  };
  // The account's own password goes with the request, and the one-time
  // code when the server asks for it: minting a credential is proven by
  // the person, not by the session.
  el('apppassword-form').addEventListener('submit', function (event) {
    event.preventDefault();
    forgetSecret();
    var body = { name: el('apppassword-name').value, password: el('apppassword-password').value };
    call('POST', '/api/v1/me/app-passwords', body, { 'X-hMailServer-OTP': el('apppassword-otp').value.trim() }).then(function (result) {
      if (result.status === 201 && result.data) {
        el('apppassword-secret').textContent = result.data.password || '';
        el('apppassword-new').hidden = false;
        el('apppassword-name').value = ''; el('apppassword-password').value = ''; el('apppassword-otp').value = '';
        say('apppassword-status', '', true);
        loadAppPasswords();
        return;
      }
      if (result.otp === 'required') { el('apppassword-otp').hidden = false; el('apppassword-otp').focus(); say('apppassword-status', t('Enter the code from your authenticator app.'), false); return; }
      say('apppassword-status', describe(result, t('Could not make the password')), false);
    });
  });
  var loadSessions = function () {
    return call('GET', '/api/v1/me/sessions').then(function (result) {
      if (result.status !== 200 || !result.data) { say('session-status', describe(result, t('Could not read the sessions')), false); return; }
      var rows = el('session-rows');
      clear(rows);
      (result.data.sessions || []).forEach(function (s) {
        var tr = document.createElement('tr');
        tr.appendChild(node('td', ago(s.created_seconds_ago)));
        tr.appendChild(node('td', ago(s.idle_seconds)));
        var which = document.createElement('td');
        if (s.current) { which.appendChild(node('span', t('This browser'), 'badge info')); }
        if (s.support) { which.appendChild(node('span', t('Support (an administrator)'), 'badge warn')); }
        tr.appendChild(which);
        var actions = document.createElement('td');
        if (!s.current) {
          var end = button(t('End'));
          end.addEventListener('click', function () {
            call('DELETE', '/api/v1/me/sessions/' + s.id).then(function (r) {
              if (r.status === 200) { say('session-status', t('Ended.'), true); loadSessions(); } else { say('session-status', describe(r, t('Could not end it')), false); }
            });
          });
          actions.appendChild(end);
        }
        tr.appendChild(actions);
        rows.appendChild(tr);
      });
    });
  };
  el('sessions-end-others').addEventListener('click', function () {
    call('DELETE', '/api/v1/me/sessions').then(function (r) {
      if (r.status === 200 && r.data) { say('session-status', (r.data.ended || 0) + t(' ended.'), true); loadSessions(); } else { say('session-status', describe(r, t('Could not end them')), false); }
    });
  });
  var loadSecurity = function () { loadAppPasswords(); loadSessions(); loadSmime(); };

  // Focus follows a change of view to its title, unless the view put the
  // focus somewhere itself, so that a screen reader announces where it is.
  // ---- Rules: a form that writes Sieve, and reads its own rules back from the
  // script's first line, so a script written by hand is never touched.
  var RULES_MARK = '# hmailserver-webmail-rules v1 ';
  var rulesOf = function (script) {
    var text = String(script || '').trim();
    if (!text) { return []; }
    if (text.indexOf(RULES_MARK) !== 0) { return null; }
    try { var list = JSON.parse(text.split('\n')[0].slice(RULES_MARK.length)); return Array.isArray(list) ? list : null; } catch (e) { return null; }
  };
  var sieveString = function (s) { return '"' + String(s).replace(/\\/g, '\\\\').replace(/"/g, '\\"') + '"'; };
  var conditionOf = function (r) {
    var text = sieveString(r.text);
    if (r.field === 'any') { return 'anyof (header :contains "from" ' + text + ', header :contains ["to", "cc"] ' + text + ', header :contains "subject" ' + text + ')'; }
    if (r.field === 'to') { return 'header :contains ["to", "cc"] ' + text; }
    return 'header :contains ' + sieveString(r.field) + ' ' + text;
  };
  var actionOf = function (r) {
    if (r.action === 'move') { return 'fileinto ' + sieveString(r.folder) + '; stop;'; }
    if (r.action === 'flag') { return 'addflag "\\\\Flagged";'; }
    if (r.action === 'read') { return 'addflag "\\\\Seen";'; }
    if (r.action === 'label') { return 'addflag ' + sieveString(r.label) + ';'; }
    return 'discard; stop;';
  };
  var scriptOf = function (rules) {
    if (!rules.length) { return ''; }
    return RULES_MARK + JSON.stringify(rules) + '\n' +
      'require ["fileinto", "imap4flags"];\n' +
      rules.map(function (r) { return 'if ' + conditionOf(r) + ' {\n  ' + actionOf(r) + '\n}'; }).join('\n') + '\n';
  };
  var describeRule = function (r) {
    var when = (r.field === 'any' ? 'From, To or Subject' : r.field === 'to' ? 'To or Cc' : r.field.charAt(0).toUpperCase() + r.field.slice(1)) + ' contains "' + r.text + '"';
    var then = r.action === 'move' ? 'move to ' + r.folder : r.action === 'label' ? 'label ' + r.label : r.action === 'flag' ? 'flag' : r.action === 'read' ? 'mark as read' : 'delete';
    return { when: when, then: then };
  };
  var currentRules = [];
  var renderRules = function (script) {
    var rules = rulesOf(script);
    el('rules-handwritten').hidden = rules !== null;
    el('rule-form').hidden = rules === null;
    currentRules = rules || [];
    var rows = el('rule-rows');
    clear(rows);
    currentRules.forEach(function (r, i) {
      var d = describeRule(r);
      var tr = document.createElement('tr');
      tr.appendChild(node('td', d.when));
      tr.appendChild(node('td', d.then));
      var actions = document.createElement('td');
      var remove = button(t('Remove'));
      remove.addEventListener('click', function () { var next = currentRules.slice(); next.splice(i, 1); saveRules(next); });
      actions.appendChild(remove);
      tr.appendChild(actions);
      rows.appendChild(tr);
    });
    el('rule-empty').hidden = rules === null || currentRules.length > 0;
    el('rule-table').hidden = rules === null || currentRules.length === 0;
    fillFolderSelect(el('rule-folder'), 0);
  };
  var saveRules = function (rules) {
    var script = scriptOf(rules);
    return call('PUT', '/api/v1/me/filters', { script: script }).then(function (result) {
      if (result.status === 200) { el('filter-script').value = script; renderRules(script); say('rule-status', t('Saved.'), true); return true; }
      say('rule-status', describe(result, t('Could not save the rules')), false);
      return false;
    });
  };
  el('rule-form').addEventListener('submit', function (event) {
    event.preventDefault();
    var rule = { field: el('rule-field').value, text: el('rule-text').value.trim(), action: el('rule-action').value };
    if (!rule.text) { return; }
    if (rule.action === 'label') {
      rule.label = el('rule-label').value.trim();
      if (!validLabel(rule.label)) { say('rule-status', t('Give the label: one word, no spaces, quotes or brackets.'), false); return; }
      if (!colourKeyOf(rule.label)) { labelColours[rule.label] = '#36c2ff'; saveLabelColours(); }
    }
    if (rule.action === 'move') {
      var folderId = Number(el('rule-folder').value);
      var folder = foldersById[folderId];
      if (!folder) { say('rule-status', t('Choose a folder to move to.'), false); return; }
      rule.folder = folder.path;
    }
    saveRules(currentRules.concat([rule])).then(function (ok) { if (ok) { el('rule-text').value = ''; } });
  });
  el('rule-action').addEventListener('change', function () { el('rule-folder').hidden = el('rule-action').value !== 'move'; el('rule-label').hidden = el('rule-action').value !== 'label'; });

  // ---- A receipt when asked, an unsubscribe when offered
  el('receipt-send').addEventListener('click', function () {
    if (!current) { return; }
    call('POST', '/api/v1/me/messages/' + current.id + '/receipt').then(function (result) {
      el('message-receipt').hidden = true;
      say('mail-status', result.status === 201 ? t('A read receipt was sent.') : describe(result, t('The receipt could not be sent')), result.status === 201);
    });
  });
  el('receipt-never').addEventListener('click', function () { el('message-receipt').hidden = true; savePrefs({ receipts: 'never' }); });
  // ---- S/MIME ---------------------------------------------------------------
  // The cryptography is /portal-smime.js (window.hmSmime): DER, X.509, CMS and
  // the MIME around them, on the Web Crypto API. This block keeps the account's
  // key store in step with the server (GET/PUT/DELETE /api/v1/me/smime...),
  // unlocks the private keys with the password - which never leaves the
  // browser - and puts what the module finds on the page.
  var smime = function () { return window.hmSmime; };
  var smimeState = { own: [], recipients: [], unlocked: {}, loaded: false, limits: {} };
  var smimePassword = '';
  var smimeCerts = {};
  var smimeAddress = function (text) { var m = String(text || '').match(/<([^>]+)>/); return (m ? m[1] : String(text || '')).trim().toLowerCase(); };
  var smimeAddresses = function (text) { return String(text || '').split(/[,;]/).map(smimeAddress).filter(function (a) { return a.indexOf('@') > 0; }); };
  var smimeFromAddress = function () { var f = el('compose-from'); return (!f.hidden && f.value) ? smimeAddress(f.value) : me; };
  var smimeOwnFor = function (address) { return smimeState.own.filter(function (o) { return o.address === address; })[0] || null; };
  var smimeRecipientsFor = function (address) { return smimeState.recipients.filter(function (r) { return r.address === address; }); };
  var smimeParse = function (entry) {
    if (!smimeCerts[entry.fingerprint]) { try { smimeCerts[entry.fingerprint] = smime().parseCertificate(smime().b64decode(entry.certificate)); } catch (e) { return null; } }
    return smimeCerts[entry.fingerprint];
  };
  var smimeDate = function (seconds) { return seconds ? new Date(seconds * 1000).toLocaleDateString() : ''; };
  var smimeSeconds = function (date) { return Math.floor(date.getTime() / 1000); };
  var smimeRenderRows = function (tbody, list, kind) {
    clear(tbody);
    list.forEach(function (entry) {
      var tr = document.createElement('tr');
      tr.appendChild(node('td', entry.address));
      tr.appendChild(node('td', (entry.name ? entry.name + ' - ' : '') + entry.fingerprint.slice(0, 16)));
      tr.appendChild(node('td', smimeDate(entry.not_after)));
      var td = document.createElement('td');
      var remove = node('button', t('Remove'), 'btn ghost sm');
      remove.type = 'button';
      remove.addEventListener('click', function () {
        call('DELETE', '/api/v1/me/smime/' + kind + '/' + entry.fingerprint).then(function (result) {
          if (result.status === 200) { delete smimeState.unlocked[entry.fingerprint]; loadSmime(); return; }
          say('smime-status', describe(result, t('Could not remove')), false);
        });
      });
      td.appendChild(remove);
      tr.appendChild(td);
      tbody.appendChild(tr);
    });
  };
  // The compose form: Sign is offered when the From address has a certificate
  // of the account's own; Encrypt when there is any certificate to encrypt for;
  // and a note names what is missing for what is ticked.
  var smimeComposeHints = function () {
    if (!smimeState.loaded || !smime()) { return; }
    var own = smimeOwnFor(smimeFromAddress());
    el('compose-sign-label').hidden = !own;
    el('compose-encrypt-label').hidden = !own && !smimeState.recipients.length;
    var hints = [];
    var missing = [];
    smimeAddresses(el('compose-to').value + ',' + el('compose-cc').value + ',' + el('compose-bcc').value).forEach(function (a) {
      if (!smimeRecipientsFor(a).length && !smimeOwnFor(a) && missing.indexOf(a) < 0) { missing.push(a); }
    });
    if (el('compose-encrypt').checked && missing.length) { hints.push(tf('Encrypt: no certificate for {0}.', missing.join(', '))); }
    if (el('compose-sign').checked && !own) { hints.push(t('Sign: no certificate of yours for this From address.')); }
    el('compose-smime-note').textContent = hints.join(' ');
    el('compose-smime-note').hidden = !hints.length;
  };
  ['compose-to', 'compose-cc', 'compose-bcc'].forEach(function (id) { el(id).addEventListener('input', smimeComposeHints); });
  ['compose-from', 'compose-sign', 'compose-encrypt'].forEach(function (id) { el(id).addEventListener('change', smimeComposeHints); });
  var loadSmime = function () {
    if (!smime()) { return Promise.resolve(); }
    return call('GET', '/api/v1/me/smime').then(function (result) {
      if (result.status !== 200 || !result.data) { return; }
      smimeState.own = result.data.own || [];
      smimeState.recipients = result.data.recipients || [];
      smimeState.limits = result.data.limits || {};
      smimeState.loaded = true;
      smimeRenderRows(el('smime-own-rows'), smimeState.own, 'own');
      el('smime-own-table').hidden = !smimeState.own.length;
      el('smime-own-empty').hidden = !!smimeState.own.length;
      smimeRenderRows(el('smime-recipient-rows'), smimeState.recipients, 'recipients');
      el('smime-recipient-table').hidden = !smimeState.recipients.length;
      el('smime-recipient-empty').hidden = !!smimeState.recipients.length;
      smimeComposeHints();
      // The password typed to sign in opens the keys once, then is forgotten.
      var typed = smimePassword;
      smimePassword = '';
      if (typed && smimeState.own.length) { smimeUnlockWith(typed).then(null, function () {}); }
    });
  };
  // Unwraps every own key that is still wrapped, with the password given;
  // rejects with the reason when one would not open.
  var smimeUnlockWith = function (password) {
    var chain = Promise.resolve();
    var failed = '';
    smimeState.own.forEach(function (entry) {
      chain = chain.then(function () {
        if (smimeState.unlocked[entry.fingerprint] || !entry.key) { return null; }
        return smime().unwrapPrivateKey(entry.key, password).then(function (pkcs8) {
          return smime().importPrivateKey(pkcs8).then(function (key) {
            var chainCerts = (entry.chain || []).map(function (c) { try { return smime().parseCertificate(smime().b64decode(c)); } catch (e) { return null; } }).filter(Boolean);
            smimeState.unlocked[entry.fingerprint] = { key: key, certificate: smimeParse(entry), chain: chainCerts, address: entry.address, entry: entry };
          });
        }, function (e) { failed = e && e.message ? e.message : String(e); });
      });
    });
    return chain.then(function () { if (failed) { throw new Error(failed); } });
  };
  var smimeUnlockedList = function () { return Object.keys(smimeState.unlocked).map(function (k) { return smimeState.unlocked[k]; }); };
  var smimeAllUnlocked = function () { return smimeState.own.every(function (e) { return !!smimeState.unlocked[e.fingerprint]; }); };
  // The dialog: in 'unlock' mode it unwraps the keys with what is typed and
  // resolves; in 'password' mode it just hands the text back. Cancel rejects.
  var smimeWaiters = [];
  var smimeAsk = function (title, note, mode) {
    el('smime-unlock-title').textContent = title;
    el('smime-unlock-note').textContent = note;
    el('smime-unlock-form').setAttribute('data-ask', mode);
    say('smime-unlock-status', '', true);
    el('smime-unlock-password').value = '';
    el('smime-unlock').hidden = false;
    el('smime-unlock-password').focus();
    return new Promise(function (resolve, reject) { smimeWaiters.push({ resolve: resolve, reject: reject }); });
  };
  var smimeSettle = function (ok, value) { el('smime-unlock').hidden = true; smimeWaiters.splice(0).forEach(function (w) { if (ok) { w.resolve(value); } else { w.reject(value); } }); };
  var smimeUnlock = function () {
    if (smimeAllUnlocked()) { return Promise.resolve(''); }
    return smimeAsk(t('Unlock your keys'), t('Your password unwraps the private key in this browser; nothing is sent to the server.'), 'unlock');
  };
  el('smime-unlock-form').addEventListener('submit', function (event) {
    event.preventDefault();
    var password = el('smime-unlock-password').value;
    if (!password) { say('smime-unlock-status', t('Enter your password.'), false); return; }
    if (el('smime-unlock-form').getAttribute('data-ask') === 'password') { smimeSettle(true, password); return; }
    smimeUnlockWith(password).then(function () { smimeSettle(true, password); }, function (e) {
      say('smime-unlock-status', (e && e.message === 'wrong password') ? t('Wrong password. If an administrator changed your password, enter the one you had before; the keys are then re-wrapped with the current one.') : String(e && e.message || e), false);
    });
  });
  el('smime-unlock-cancel').addEventListener('click', function () { smimeSettle(false, t('Not sent. The message is still here.')); });
  // Every unlocked key wrapped again under the password given and stored.
  var smimeRewrapUnlocked = function (password) {
    var chain = Promise.resolve();
    var count = 0;
    smimeUnlockedList().forEach(function (u) {
      if (!u.entry) { return; }
      chain = chain.then(function () {
        return smime().wrapPrivateKey(u.key.pkcs8, password).then(function (wrapped) {
          var e = u.entry;
          return call('PUT', '/api/v1/me/smime/own', { address: e.address, name: e.name, fingerprint: e.fingerprint, certificate: e.certificate, chain: e.chain || [], key: wrapped, not_after: e.not_after }).then(function (result) { if (result.status === 200 || result.status === 201) { count++; } });
        });
      });
    });
    return chain.then(function () { return count; });
  };
  // A password change on this page: the keys open with the old password and
  // are wrapped again with the new. Answers a note for the status line.
  var smimeRewrap = function (oldPassword, newPassword) {
    if (!smime() || !smimeState.own.length) { return Promise.resolve(''); }
    return smimeUnlockWith(oldPassword).then(null, function () {}).then(function () { return smimeRewrapUnlocked(newPassword); }).then(function (count) {
      return count ? t('Your keys were re-wrapped with the new password.') : '';
    }, function () { return ''; });
  };
  // ---- reading a message ----
  var smimeContentType = function (headers) {
    var m = String(headers || '').match(/^content-type:\s*([^\r\n]*(?:\r?\n[ \t][^\r\n]*)*)/im);
    return m ? m[1].replace(/\r?\n[ \t]+/g, ' ').trim().toLowerCase() : '';
  };
  var smimeRun = 0;
  var smimeInspect = function (m) {
    el('message-smime').hidden = true;
    el('message-smime-unlock').hidden = true;
    if (!smime()) { return; }
    var type = smimeContentType(m.headers);
    if (type.indexOf('multipart/signed') !== 0 && type.indexOf('application/pkcs7-mime') !== 0 && type.indexOf('application/x-pkcs7-mime') !== 0) { return; }
    var run = ++smimeRun;
    el('message-smime-text').textContent = t('Checking the signature...');
    el('message-smime').hidden = false;
    var ready = smimeState.loaded ? Promise.resolve() : loadSmime();
    ready.then(function () {
      return fetch('/api/v1/me/messages/' + m.id + '/source', { cache: 'no-store', credentials: 'same-origin' }).then(function (r) { return r.ok ? r.arrayBuffer() : Promise.reject(new Error('HTTP ' + r.status)); });
    }).then(function (buffer) {
      return smime().readMessage(new Uint8Array(buffer), smimeUnlockedList());
    }).then(function (result) {
      if (run !== smimeRun || !current || current.id !== m.id) { return; }
      smimeShow(m, result);
    }, function (e) {
      if (run !== smimeRun) { return; }
      el('message-smime-text').textContent = tf('Could not check: {0}', e && e.message ? e.message : String(e));
    });
  };
  var smimeBadge = function (text, cls) { el('message-badges').appendChild(node('span', text, 'badge ' + cls)); };
  // A verified signer's certificate is kept, so a reply can be encrypted.
  var smimeRemember = function (signer, who) {
    smime().fingerprint(signer).then(function (fp) {
      if (smimeState.recipients.some(function (r) { return r.fingerprint === fp; }) || smimeState.own.some(function (o) { return o.fingerprint === fp; })) { return; }
      return call('PUT', '/api/v1/me/smime/recipients', { address: who, name: signer.name, fingerprint: fp, certificate: smime().b64encode(signer.raw), not_after: smimeSeconds(signer.notAfter) }).then(function (result) {
        if (result.status !== 201) { return; }
        smimeCerts[fp] = signer;
        el('message-smime-text').textContent = (el('message-smime-text').textContent + ' ' + tf('The certificate of {0} was kept for encrypting replies.', who)).trim();
        el('message-smime').hidden = false;
        loadSmime();
      });
    }, function () {});
  };
  var smimeShow = function (m, result) {
    var lines = [];
    if (result.encrypted) {
      if (result.encrypted.ok) { lines.push(t('Encrypted; decrypted in this browser. It can be searched by its headers only.')); smimeBadge(t('Encrypted'), 'good'); }
      else if (smimeState.own.length && !smimeAllUnlocked()) { lines.push(t('Encrypted. Unlock your keys to read it.')); el('message-smime-unlock').hidden = false; smimeBadge(t('Encrypted'), 'muted'); }
      else { lines.push(tf('Encrypted, and cannot be read here: {0}', result.encrypted.error)); smimeBadge(t('Encrypted'), 'muted'); }
    }
    if (result.signed) {
      var s = result.signed;
      var who = s.signer ? (s.signer.emails[0] || s.signer.name) : '';
      if (!s.valid) { smimeBadge(tf('Signature invalid: {0}', s.error), 'bad'); }
      else if (s.signer.emails.indexOf(smimeAddress(m.from)) < 0) { smimeBadge(tf('Signed by {0}, not the sender', who), 'warn'); }
      else {
        var certs = [s.signer].concat(s.certificates.filter(function (c) { return c !== s.signer; })).map(function (c) { return smime().b64encode(c.raw); });
        call('POST', '/api/v1/me/smime/chain', { certificates: certs, purpose: 'sign' }).then(function (verdict) {
          if (!current || current.id !== m.id) { return; }
          var d = verdict.data || {};
          if (verdict.status === 200 && d.trusted) { smimeBadge(tf('Signed by {0}', who), 'good'); }
          else { smimeBadge(tf('Signed by {0} (certificate not trusted: {1})', who, d.error || ('HTTP ' + verdict.status)), 'warn'); }
          smimeRemember(s.signer, who);
        });
      }
    }
    if (result.content && result.encrypted && result.encrypted.ok) {
      var c = result.content;
      var text = c.text || (c.html ? (new DOMParser().parseFromString(c.html, 'text/html').body.textContent || '') : '');
      el('message-text').textContent = text || '(no text)';
      var attachments = el('message-attachments');
      clear(attachments);
      if (c.attachments.length) {
        attachments.appendChild(node('span', t('Attachments: ')));
        c.attachments.forEach(function (a, i) {
          var link = node('a', a.name + ' (' + a.type + ', ' + format(a.bytes.length) + ')');
          link.href = URL.createObjectURL(new Blob([a.bytes], { type: a.type }));
          link.setAttribute('download', a.name);
          if (i > 0) { attachments.appendChild(node('span', ', ')); }
          attachments.appendChild(link);
        });
      }
    }
    el('message-smime-text').textContent = lines.join(' ');
    el('message-smime').hidden = !lines.length;
  };
  el('message-smime-unlock').addEventListener('click', function () { smimeUnlock().then(function () { if (current) { smimeInspect(current); } }, function () {}); });
  // ---- writing a message ----
  // The body the form made, turned into the MIME entity the server sends as
  // it is: signed with the From address's key (unlocked first), encrypted
  // for every recipient's certificate and the sender's own.
  var smimePrepare = function (body) {
    if (!body.sign && !body.encrypt) { return Promise.resolve(body); }
    if (!smime()) { return Promise.reject(t('The S/MIME module did not load.')); }
    var own = smimeOwnFor(body.from ? smimeAddress(body.from) : me);
    if (body.sign && !own) { return Promise.reject(t('Sign: no certificate of yours for this From address.')); }
    var recipients = [];
    if (body.encrypt) {
      var missing = [];
      smimeAddresses(body.to + ',' + body.cc + ',' + body.bcc).forEach(function (a) {
        var found = smimeRecipientsFor(a).map(smimeParse).filter(Boolean);
        var mine = smimeOwnFor(a);
        if (mine) { var pm = smimeParse(mine); if (pm) { found.push(pm); } }
        if (!found.length) { if (missing.indexOf(a) < 0) { missing.push(a); } return; }
        found.forEach(function (c) { if (recipients.indexOf(c) < 0) { recipients.push(c); } });
      });
      if (missing.length) { return Promise.reject(tf('Encrypt: no certificate for {0}.', missing.join(', '))); }
      if (own) { var mineCert = smimeParse(own); if (mineCert && recipients.indexOf(mineCert) < 0) { recipients.push(mineCert); } }
    }
    return (body.sign ? smimeUnlock() : Promise.resolve('')).then(function () {
      var signer = body.sign ? smimeState.unlocked[own.fingerprint] : null;
      if (body.sign && !signer) { throw new Error(t('Sign: the key could not be unlocked.')); }
      return smime().composeMessage({ headers: {}, text: body.text || '', html: body.html || '', attachments: body.attachments || [], sign: signer, encrypt: body.encrypt ? recipients : null });
    }).then(function (message) {
      var out = {};
      ['to', 'cc', 'bcc', 'from', 'subject', 'receipt', 'in_reply_to', 'references', 'answered_id'].forEach(function (k) { if (body[k] !== undefined && body[k] !== '') { out[k] = body[k]; } });
      // The module writes only MIME-Version above the entity; the server puts its own on.
      out.mime = message.replace(/^MIME-Version: 1\.0\r\n/, '');
      return out;
    });
  };
  // ---- the Security page ----
  el('smime-import-form').addEventListener('submit', function (event) {
    event.preventDefault();
    var file = (el('smime-file').files || [])[0];
    if (!file) { say('smime-status', t('Choose a file first.'), false); return; }
    if (!smime()) { say('smime-status', t('The S/MIME module did not load.'), false); return; }
    var passphrase = el('smime-passphrase').value;
    say('smime-status', t('Importing...'), true);
    file.arrayBuffer().then(function (buffer) {
      return smime().importAny(new Uint8Array(buffer), passphrase);
    }).then(function (found) {
      var keyed = found.keys.filter(function (k) { return k.certificate; });
      var others = found.certificates.filter(function (c) { return !keyed.some(function (k) { return k.certificate === c; }) && !c.isCA && c.emails.length; });
      if (!keyed.length && !others.length) { throw new Error(t('The file holds no certificate.')); }
      var ask = keyed.length ? smimeAsk(t('Your password protects the key'), t('It is wrapped with your password in this browser before it is stored; the server cannot open it.'), 'password') : Promise.resolve('');
      return ask.then(function (password) {
        var chain = Promise.resolve();
        var imported = [];
        keyed.forEach(function (k) {
          chain = chain.then(function () {
            var cert = k.certificate;
            var chainCerts = found.certificates.filter(function (c) { return c !== cert; });
            return smime().fingerprint(cert).then(function (fp) {
              return smime().wrapPrivateKey(k.key.pkcs8, password).then(function (wrapped) {
                var address = cert.emails[0] || me;
                var entry = { address: address, name: cert.name, fingerprint: fp, certificate: smime().b64encode(cert.raw), chain: chainCerts.map(function (c) { return smime().b64encode(c.raw); }), key: wrapped, not_after: smimeSeconds(cert.notAfter) };
                return call('PUT', '/api/v1/me/smime/own', entry).then(function (result) {
                  if (result.status !== 200 && result.status !== 201) { throw new Error(describe(result, t('Could not store the key'))); }
                  smimeCerts[fp] = cert;
                  smimeState.unlocked[fp] = { key: k.key, certificate: cert, chain: chainCerts, address: address, entry: entry };
                  imported.push(address);
                });
              });
            });
          });
        });
        others.forEach(function (cert) {
          chain = chain.then(function () {
            return smime().fingerprint(cert).then(function (fp) {
              return call('PUT', '/api/v1/me/smime/recipients', { address: cert.emails[0], name: cert.name, fingerprint: fp, certificate: smime().b64encode(cert.raw), not_after: smimeSeconds(cert.notAfter) }).then(function (result) {
                if (result.status === 200 || result.status === 201) { smimeCerts[fp] = cert; imported.push(cert.emails[0]); }
              });
            });
          });
        });
        return chain.then(function () { return imported; });
      });
    }).then(function (imported) {
      el('smime-file').value = ''; el('smime-passphrase').value = '';
      say('smime-status', tf('Imported {0}.', imported.join(', ')), true);
      loadSmime();
    }, function (e) {
      say('smime-status', tf('Could not import: {0}', e && e.message ? e.message : String(e)), false);
    });
  });
  el('message-unsubscribe').addEventListener('click', function () {
    if (!current) { return; }
    say('mail-status', t('Asking the list...'), true);
    call('POST', '/api/v1/me/messages/' + current.id + '/unsubscribe').then(function (result) {
      var d = result.data || {};
      if (result.status === 200 && d.method === 'one-click') { say('mail-status', d.ok ? t('Unsubscribed: the list answered ') + d.status + '.' : t('The list answered ') + d.status + t('; it may not have taken.'), !!d.ok); return; }
      if (result.status === 201 && d.method === 'mail') { say('mail-status', t('An unsubscribe request was sent to ') + d.to + '.', true); return; }
      say('mail-status', describe(result, t('Could not unsubscribe')), false);
    });
  });

  // ---- Rich text: a formatting bar over an editor whose HTML is rebuilt from
  // an allowed subset before it is sent, and a plain text made from the same
  // content, so the message goes as multipart/alternative.
  var richOn = false;
  var ALLOWED = { B: 'b', STRONG: 'b', I: 'i', EM: 'i', U: 'u', BR: 'br', P: 'p', DIV: 'p', UL: 'ul', OL: 'ol', LI: 'li', BLOCKQUOTE: 'blockquote', A: 'a', IMG: 'img', H1: 'b', H2: 'b', H3: 'b' };
  var escapeHtml = function (s) { return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;'); };
  var cleanNode = function (node) {
    if (node.nodeType === 3) { return escapeHtml(node.nodeValue); }
    if (node.nodeType !== 1) { return ''; }
    var tag = ALLOWED[node.tagName];
    var inner = '';
    for (var i = 0; i < node.childNodes.length; i++) { inner += cleanNode(node.childNodes[i]); }
    if (!tag) { return inner; }
    if (tag === 'br') { return '<br>'; }
    if (tag === 'img') {
      var src = String(node.getAttribute('src') || '');
      return /^data:image\/(png|jpeg|gif|webp);base64,/i.test(src) && src.length < 2000000 ? '<img src="' + src + '" alt="' + escapeHtml(node.getAttribute('alt') || '') + '">' : '';
    }
    if (tag === 'a') {
      var href = String(node.getAttribute('href') || '');
      return /^(https?:|mailto:)/i.test(href) ? '<a href="' + escapeHtml(href) + '">' + inner + '</a>' : inner;
    }
    if (tag === 'p' && !inner) { return '<br>'; }
    return '<' + tag + '>' + inner + '</' + tag + '>';
  };
  var cleanHtml = function (root) { var out = ''; for (var i = 0; i < root.childNodes.length; i++) { out += cleanNode(root.childNodes[i]); } return out; };
  var plainOf = function (node) {
    if (node.nodeType === 3) { return node.nodeValue; }
    if (node.nodeType !== 1) { return ''; }
    if (node.tagName === 'BR') { return '\n'; }
    var text = '';
    for (var i = 0; i < node.childNodes.length; i++) { text += plainOf(node.childNodes[i]); }
    if (/^(P|DIV|LI|BLOCKQUOTE|H1|H2|H3|UL|OL)$/.test(node.tagName)) { text = (node.tagName === 'BLOCKQUOTE' ? text.split('\n').map(function (l) { return '> ' + l; }).join('\n') : text) + '\n'; }
    return text;
  };
  var textToEditor = function (text) {
    var editor = el('compose-editor');
    while (editor.firstChild) { editor.removeChild(editor.firstChild); }
    String(text || '').split('\n').forEach(function (line, i) {
      if (i > 0) { editor.appendChild(document.createElement('br')); }
      editor.appendChild(node('span', line));
    });
  };
  var setRich = function (on) {
    richOn = on;
    var editor = el('compose-editor'), box = el('compose-text');
    if (on) { textToEditor(box.value); editor.hidden = false; box.hidden = true; el('rich-tools').hidden = false; el('rich-toggle').textContent = 'Plain text'; }
    else { box.value = plainOf(editor).replace(/\n+$/, ''); box.hidden = false; editor.hidden = true; el('rich-tools').hidden = true; el('rich-toggle').textContent = 'Formatting'; }
  };
  el('rich-toggle').addEventListener('click', function () { setRich(!richOn); if (richOn) { el('compose-editor').focus(); } });
  (function () {
    var tools = el('rich-tools').children;
    for (var i = 0; i < tools.length; i++) {
      (function (b) {
        // The button must not take the focus: Firefox collapses the editor's
        // selection when it does, and the command then has nothing to apply to.
        b.addEventListener('mousedown', function (event) { event.preventDefault(); });
        b.addEventListener('click', function () {
          var cmd = b.getAttribute('data-cmd'), arg = b.getAttribute('data-arg') || null;
          if (cmd === 'createLink') { arg = window.prompt ? window.prompt('The address to link to') : ''; if (!arg) { return; } }
          el('compose-editor').focus();
          try { document.execCommand(cmd, false, arg); } catch (e) { /* a browser without the command keeps the text */ }
        });
      })(tools[i]);
    }
  })();

  // ---- Offline: the inbox and opened messages kept in this browser, sends
  // queued, all of it forgotten at sign-out.
  var offlineData = function () { try { return JSON.parse(localStorage.getItem('hmPortalOffline') || '{}') || {}; } catch (e) { return {}; } };
  var offlineWrite = function (d) { try { localStorage.setItem('hmPortalOffline', JSON.stringify(d)); } catch (e) { /* storage full or refused: nothing kept */ } };
  var offlineKeep = function (key, value) {
    if (key !== 'list' && value && value.html && value.html.length > 100000) { return; }
    var d = offlineData();
    d.items = d.items || {};
    d.order = d.order || [];
    d.items[key] = value;
    var at = d.order.indexOf(key);
    if (at >= 0) { d.order.splice(at, 1); }
    d.order.push(key);
    while (d.order.length > 31) { var old = d.order.shift(); if (old !== 'list') { delete d.items[old]; } else { d.order.push(old); } }
    offlineWrite(d);
  };
  var offlineRead = function (key) { var d = offlineData(); return d.items && d.items[key] ? d.items[key] : null; };
  var offlineForget = function () { try { localStorage.removeItem('hmPortalOffline'); } catch (e) { /* nothing to forget */ } };
  var offlineNote = function (on) { el('offline-note').hidden = !on; };
  var outboxAdd = function (body) { var d = offlineData(); d.outbox = d.outbox || []; d.outbox.push(body); offlineWrite(d); };
  var outboxFlush = function () {
    var d = offlineData();
    if (!d.outbox || !d.outbox.length || el('account').hidden) { return; }
    var queued = d.outbox.slice();
    d.outbox = [];
    offlineWrite(d);
    var chain = Promise.resolve();
    queued.forEach(function (body) {
      chain = chain.then(function () {
        return call('POST', '/api/v1/me/messages', body).then(function (result) {
          if (result.status === 0) { outboxAdd(body); }
          else if (result.status === 201) { say('mail-status', t('A message written without a connection has been sent.'), true); loadFolders(); }
          else { say('mail-status', describe(result, t('A queued message could not be sent')), false); }
        });
      });
    });
  };
  window.addEventListener('online', function () { offlineNote(false); outboxFlush(); });
  window.addEventListener('offline', function () { offlineNote(true); });
  if (typeof navigator !== 'undefined' && navigator.serviceWorker && navigator.serviceWorker.register) {
    navigator.serviceWorker.register('/portal-sw.js').catch(function () { /* the page works without it */ });
  }
  var setTimeout0 = function (fn) { if (typeof window.requestAnimationFrame === 'function') { window.requestAnimationFrame(fn); } };

  // ---- Later: send later, snooze, what is scheduled ----------------------------
  var pad2 = function (n) { return (n < 10 ? '0' : '') + n; };
  var stampOf = function (d) { return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate()) + ' ' + pad2(d.getHours()) + ':' + pad2(d.getMinutes()); };
  var fromPicker = function (value) { return String(value || '').replace('T', ' ').slice(0, 16); };
  var setExportLink = function () {
    var id = Number(el('folder-export-target').value);
    el('folder-export-link').setAttribute('href', id ? '/api/v1/me/folders/' + id + '/export' : '#');
    el('folder-export-link').setAttribute('download', 'folder-' + (id || 0) + '.mbox');
  };
  el('folder-export-target').addEventListener('change', setExportLink);
  el('folder-import-go').addEventListener('click', function () {
    var id = Number(el('folder-import-target').value);
    var file = (el('folder-import-file').files || [])[0];
    if (!id || !file) { say('folder-import-status', t('Choose a folder and a .eml file.'), false); return; }
    var reader = new FileReader();
    reader.onload = function () {
      fetch('/api/v1/me/folders/' + id + '/messages', { method: 'POST', credentials: 'same-origin', cache: 'no-store',
        headers: { 'Content-Type': 'message/rfc822', 'X-Requested-With': 'hMailServer' }, body: String(reader.result) }).then(function (response) {
        if (response.status === 201) { say('folder-import-status', t('Imported into ') + folderName(id) + '.', true); el('folder-import-file').value = ''; lastListing = null; loadFolders(); return; }
        return response.text().then(function (text) { var why = ''; try { why = JSON.parse(text).error || ''; } catch (e) { why = ''; } say('folder-import-status', why || (t('Could not import (') + response.status + ')'), false); });
      });
    };
    reader.onerror = function () { say('folder-import-status', t('The file could not be read.'), false); };
    reader.readAsText(file);
  });
  el('compose-send-later').addEventListener('click', function () {
    var at = fromPicker(el('compose-later').value);
    if (at.length !== 16) { say('compose-status', t('Choose when to send it.'), false); return; }
    if (!el('compose-to').value.trim()) { say('compose-status', t('Say who it is to.'), false); return; }
    var body = composeBody();
    if (draftId) { body.replace_id = draftId; }
    readFiles().then(function (files) {
      files = carried.concat(files);
      if (files.length) { body.attachments = files; }
      appendLinks(body);
      return call('POST', '/api/v1/me/drafts', body);
    }, function (why) { say('compose-status', why, false); return null; }).then(function (result) {
      if (!result) { return; }
      if (result.status !== 201 || !result.data) { say('compose-status', describe(result, t('Could not save the draft')), false); return; }
      var id = result.data.id;
      return call('POST', '/api/v1/me/drafts/' + id + '/schedule', { send_at: at }).then(function (scheduled) {
        if (scheduled.status === 201) {
          blankCompose(); composeKey = 'new:0';
          say('compose-status', t('Saved as a draft; it will be sent at ') + at + '.', true);
          loadFolders();
          return;
        }
        draftId = id; composeKey = 'draft:' + id;
        say('compose-status', describe(scheduled, t('Saved as a draft, but it could not be scheduled')), false);
      });
    });
  });
  var snoozeUntil = function (at) {
    if (!current) { return; }
    call('POST', '/api/v1/me/messages/' + current.id + '/snooze', { until: at }).then(function (result) {
      if (result.status === 200) { say('mail-status', t('Snoozed until ') + at + '.', true); current = null; lastListing = null; loadFolders(); go(listHash()); return; }
      say('mail-status', describe(result, t('Could not snooze')), false);
    });
  };
  el('message-snooze').addEventListener('click', function () { var m = el('snooze-menu'); m.hidden = !m.hidden; });
  el('snooze-3h').addEventListener('click', function () { snoozeUntil(stampOf(new Date(Date.now() + 3 * 3600 * 1000))); });
  el('snooze-tomorrow').addEventListener('click', function () { var d = new Date(); d.setDate(d.getDate() + 1); d.setHours(9, 0, 0, 0); snoozeUntil(stampOf(d)); });
  el('snooze-week').addEventListener('click', function () { var d = new Date(); d.setDate(d.getDate() + 7); d.setHours(9, 0, 0, 0); snoozeUntil(stampOf(d)); });
  el('snooze-go').addEventListener('click', function () { var at = fromPicker(el('snooze-at').value); if (at.length === 16) { snoozeUntil(at); } else { say('mail-status', t('Choose when.'), false); } });
  var loadScheduled = function () {
    return call('GET', '/api/v1/me/scheduled').then(function (result) {
      if (result.status !== 200 || !result.data) { say('scheduled-status', describe(result, t('Could not read what is scheduled')), false); return; }
      var rows = el('scheduled-rows');
      clear(rows);
      var list = result.data.scheduled || [];
      list.forEach(function (s) {
        var tr = document.createElement('tr');
        tr.appendChild(node('td', s.action === 'send' ? t('Send') : t('Bring back')));
        tr.appendChild(node('td', s.subject || t('(no subject)')));
        tr.appendChild(node('td', s.at));
        var actions = document.createElement('td');
        var cancel = button(t('Cancel'));
        cancel.addEventListener('click', function () {
          call('DELETE', '/api/v1/me/scheduled/' + s.id).then(function (r) {
            if (r.status === 200) { say('scheduled-status', s.action === 'send' ? t('Cancelled; the draft stays.') : t('Brought back.'), true); loadScheduled(); lastListing = null; loadFolders(); }
            else { say('scheduled-status', describe(r, t('Could not cancel')), false); }
          });
        });
        actions.appendChild(cancel);
        tr.appendChild(actions);
        rows.appendChild(tr);
      });
      el('scheduled-empty').hidden = list.length > 0;
      el('scheduled-table').hidden = list.length === 0;
    });
  };

  el('filter-form').addEventListener('submit', function (event) {
    event.preventDefault();
    call('PUT', '/api/v1/me/filters', { script: el('filter-script').value }).then(function (result) {
      if (result.status === 200) { say('filter-status', el('filter-script').value.trim() ? t('Saved. The filter runs on every message that arrives from now on.') : t('Removed.'), true); return; }
      say('filter-status', describe(result, t('Could not save the filter')), false);
    });
  });
  el('password-form').addEventListener('submit', function (event) {
    event.preventDefault();
    var now = el('current').value, fresh = el('new').value, again = el('confirm').value;
    if (fresh !== again) { say('password-status', t('The two new passwords differ.'), false); return; }
    call('POST', '/api/v1/me/password', { current: now, 'new': fresh }, { 'X-hMailServer-OTP': el('otp').value.trim() }).then(function (result) {
      if (result.status === 200) {
        el('current').value = ''; el('new').value = ''; el('confirm').value = ''; el('otp').value = '';
        smimeRewrap(now, fresh).then(function (note) {
          say('password-status', t('Password changed. Other browsers signed in to this account have been signed out.') + (note ? ' ' + note : ''), true);
          load(true);
        });
        return;
      }
      if (result.otp === 'required') { el('otp-row').hidden = false; say('password-status', t('Enter the code from your authenticator app.'), false); return; }
      say('password-status', describe(result, t('Could not change the password')), false);
    });
  });
  el('bulk-read').addEventListener('click', function () { eachSelected(function (id) { return call('PUT', '/api/v1/me/messages/' + id + '/flags', { seen: true }); }); });
  el('bulk-unread').addEventListener('click', function () { eachSelected(function (id) { return call('PUT', '/api/v1/me/messages/' + id + '/flags', { seen: false }); }); });
  el('bulk-archive').addEventListener('click', function () { eachSelected(function (id) { return fileMessage(id, 'archive'); }); });
  el('bulk-junk').addEventListener('click', function () { var to = junkTargetFor(state.folderId); eachSelected(function (id) { return fileMessage(id, to); }); });
  el('bulk-delete').addEventListener('click', function () { eachSelected(function (id) { return call('DELETE', '/api/v1/me/messages/' + id); }); });
  el('bulk-move-go').addEventListener('click', function () {
    var target = Number(el('bulk-move').value);
    if (!target) { return; }
    eachSelected(function (id) { return call('POST', '/api/v1/me/messages/' + id + '/move', { folder_id: target }); });
  });
  el('bulk-clear').addEventListener('click', function () { clearSelection(); listRows.forEach(function (r) { r.box.checked = false; }); });
  // One call per message, in order, then one reload.
  var eachSelected = function (act) {
    var ids = selectedIds();
    var chain = Promise.resolve();
    ids.forEach(function (id) { chain = chain.then(function () { return act(id); }); });
    return chain.then(function () { clearSelection(); lastListing = null; loadFolders(); return loadMessages(); });
  };
  document.addEventListener('keydown', function (event) {
    var tag = (event.target && event.target.tagName) || '';
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || event.ctrlKey || event.metaKey || event.altKey) { return; }
    if (el('account').hidden || el('mail-section').hidden || el('message-list').hidden) { return; }
    if (event.key === 'ArrowDown' || event.key === 'j') { setCursor(cursor + 1); event.preventDefault(); }
    else if (event.key === 'ArrowUp' || event.key === 'k') { setCursor(cursor - 1); event.preventDefault(); }
    else if (event.key === 'Enter' && cursor >= 0) { var hit = listRows[cursor]; if (hit.toggle) { hit.toggle(); } else { go('/m/' + hit.id); } event.preventDefault(); }
    else if (event.key === 'e' && cursor >= 0) { fileRow(listRows[cursor], 'archive'); event.preventDefault(); }
    else if (event.key === '!' && cursor >= 0) { fileRow(listRows[cursor], junkTargetFor(state.folderId)); event.preventDefault(); }
    else if (event.key === '#' && cursor >= 0) { fileRow(listRows[cursor], 'delete'); event.preventDefault(); }
    else if (event.key === 'x' && cursor >= 0) { var r = listRows[cursor]; r.box.checked = !r.box.checked; (r.ids || [r.id]).forEach(function (id) { selected[id] = r.box.checked; }); renderBulk(); event.preventDefault(); }
  });
  window.addEventListener('hashchange', route);
  // Registered after the router's own listener - listeners run in the
  // order they were added - so it runs once the view has been shown and
  // any field the view focused has the focus; otherwise the title takes it.
  window.addEventListener('hashchange', function () {
    var title = el('view-title');
    var active = document.activeElement;
    if (title && !el('account').hidden && (!active || active === document.body || active.tagName === 'BUTTON')) { title.focus(); }
  });
  // The skip link goes to the content itself, not through the router:
  // #content is no route, and the router would send it to the folder.
  el('skip').addEventListener('click', function (event) {
    event.preventDefault();
    var content = el('content');
    content.focus();
    if (content.scrollIntoView) { content.scrollIntoView(); }
  });
  document.addEventListener('visibilitychange', function () {
    if (!document.hidden) { probeNow(); return; }
    if (document.hidden && pref('notify') !== '1') { probeStop(); probe.paused = true; setProbeState('Paused - this tab is in the background', true); }
  });

  // A session from an earlier visit is still good until it has been idle
  // too long: try it first, and only ask for the password when it is not.
  // ---- Preferences ----------------------------------------------------------
  // Kept with the account (GET/PUT /api/v1/me/preferences); the browser's
  // storage holds only the theme, for the sign-in page before there is one.
  var prefs = {};
  var basePrefs = { theme: 'system', density: 'comfortable', undo_seconds: '5', notify: '0', notify_folders: '', view: 'messages' };
  var renderListTools = function () {
    el('view-threads').textContent = pref('view') === 'threads' ? 'Messages' : 'Threads';
    var emptyable = !state.everywhere && (folderIs(state.folderId, 'Junk') || folderIs(state.folderId, 'Trash'));
    el('folder-empty').hidden = !emptyable;
    el('folder-empty').textContent = 'Empty this folder';
  };
  el('view-threads').addEventListener('click', function () {
    savePrefs({ view: pref('view') === 'threads' ? 'messages' : 'threads' }).then(function () {
      renderListTools();
      if (lastListing && !el('mail-section').hidden && el('message-view').hidden) { renderMessages(lastListing.page); }
    });
  });
  // Two presses, as deleting a folder takes two: the second one empties.
  // Emptying goes in batches the server sizes: its answer's remaining says
  // whether to ask again. Bounded, so a folder that fills as fast as it
  // empties cannot keep this going for ever. Resolves to what was deleted
  // and whether a call failed, with the failing answer.
  var emptyFolder = function (folderId, days) {
    var deleted = 0;
    var path = '/api/v1/me/folders/' + folderId + '/empty' + (days === undefined ? '' : '?older_than_days=' + encodeURIComponent(days));
    var round = function (left) {
      return call('POST', path).then(function (result) {
        if (result.status !== 200 || !result.data) { return { deleted: deleted, failed: true, result: result }; }
        deleted += result.data.deleted || 0;
        if (result.data.remaining > 0 && left > 0) { return round(left - 1); }
        return { deleted: deleted, failed: false, result: result };
      });
    };
    return round(200);
  };
  el('folder-empty').addEventListener('click', function () {
    var b = el('folder-empty');
    if (b.getAttribute('data-armed') !== '1') { b.setAttribute('data-armed', '1'); b.textContent = t('Really empty?'); return; }
    b.removeAttribute('data-armed');
    emptyFolder(state.folderId).then(function (r) {
      if (!r.failed) { say('mail-status', r.deleted + t(' deleted.'), true); lastListing = null; loadFolders(); loadMessages(); }
      else { say('mail-status', describe(r.result, t('Could not empty the folder')) + (r.deleted ? ' ' + tf('{0} had gone first.', r.deleted) : ''), false); }
      b.textContent = t('Empty this folder');
    });
  });
  var pref = function (name) { return prefs[name] !== undefined ? prefs[name] : basePrefs[name]; };
  var undoSeconds = function () { var n = Number(pref('undo_seconds')); return isNaN(n) ? 5 : Math.min(30, Math.max(0, Math.round(n))); };
  var systemDark = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;
  var applyTheme = function (choice) {
    var actual = choice === 'light' || choice === 'dark' ? choice : (systemDark && !systemDark.matches ? 'light' : 'dark');
    document.body.setAttribute('data-theme', actual);
    try { localStorage.setItem('hmPortalTheme', choice); } catch (e) { /* a browser that keeps nothing is still a browser */ }
  };
  var storedTheme = function () { try { return localStorage.getItem('hmPortalTheme') || 'system'; } catch (e) { return 'system'; } };
  if (systemDark && systemDark.addEventListener) { systemDark.addEventListener('change', function () { if (pref('theme') === 'system') { applyTheme('system'); } }); }
  var notifyFolderIds = function () {
    var raw = pref('notify_folders');
    if (!raw) { return inboxId ? [inboxId] : []; }
    return raw.split(',').map(Number).filter(function (n) { return n > 0; });
  };
  var notifyWanted = function (folderId) { return pref('notify') === '1' && notifyFolderIds().indexOf(folderId) >= 0; };
  var notifyNewMail = function (grew) {
    if (!grew.length || !('Notification' in window) || Notification.permission !== 'granted') { return; }
    grew.forEach(function (g) {
      try { new Notification('New mail in ' + (g.folder.name || g.folder.path), { body: g.added + ' new, ' + g.unseen + ' unread', tag: 'hm-folder-' + g.folder.id }); } catch (e) { /* a browser without notifications is still a browser */ }
    });
  };
  var baseTitle = document.title || 'hMailServer - my account';
  var updateTitle = function () {
    var total = 0;
    notifyFolderIds().forEach(function (id) { var f = foldersById[id]; if (f) { total += f.unseen || 0; } });
    document.title = (total ? '(' + total + ') ' : '') + baseTitle;
  };
  var renderNotifyFolders = function () {
    var box = el('notify-folders');
    clear(box);
    var chosen = notifyFolderIds();
    allFolders.forEach(function (f) {
      var label = document.createElement('label'); label.className = 'inline';
      var tick = document.createElement('input'); tick.type = 'checkbox'; tick.value = String(f.id); tick.checked = chosen.indexOf(f.id) >= 0;
      label.appendChild(tick); label.appendChild(node('span', ' ' + f.path));
      box.appendChild(label);
    });
  };
  var applyPrefs = function () {
    applyTheme(pref('theme'));
    document.body.setAttribute('data-density', pref('density') === 'compact' ? 'compact' : 'comfortable');
    el('pref-theme').value = pref('theme') === 'light' || pref('theme') === 'dark' ? pref('theme') : 'system';
    el('pref-density').value = pref('density') === 'compact' ? 'compact' : 'comfortable';
    el('pref-undo').value = String(undoSeconds());
    if (!el('pref-undo').value) { el('pref-undo').value = '5'; }
    el('pref-notify').checked = pref('notify') === '1';
    el('pref-language').value = knownLanguage(pref('language') || '') || languageActive;
    renderNotifyFolders();
    renderTemplates();
    renderSupport();
    refreshLabelColours();
    applyLanguagePref();
    updateTitle();
  };
  // ---- Templates: kept in the preferences as tpl.<slug> -> {name, subject, text}
  var templates = function () {
    var list = [];
    Object.keys(prefs).forEach(function (k) {
      if (k.indexOf('tpl.') !== 0) { return; }
      try { var v = JSON.parse(prefs[k]); if (v && v.name) { list.push({ key: k, name: v.name, subject: v.subject || '', text: v.text || '' }); } } catch (e) { /* a value that is not a template is left alone */ }
    });
    return list.sort(function (a, b) { return a.name.localeCompare(b.name); });
  };
  var renderTemplates = function () {
    var list = templates();
    var pick = el('template-pick');
    clear(pick);
    var none = node('option', t('Templates')); none.value = ''; pick.appendChild(none);
    list.forEach(function (tp) { var o = node('option', tp.name); o.value = tp.key; pick.appendChild(o); });
    var rows = el('template-rows');
    clear(rows);
    list.forEach(function (tp) {
      var tr = document.createElement('tr');
      tr.appendChild(node('td', tp.name));
      tr.appendChild(node('td', tp.subject));
      var actions = document.createElement('td');
      var remove = button(t('Remove'));
      remove.addEventListener('click', function () { var change = {}; change[tp.key] = null; savePrefs(change).then(function (ok) { if (ok) { say('template-status', t('Removed.'), true); } }); });
      actions.appendChild(remove);
      tr.appendChild(actions);
      rows.appendChild(tr);
    });
    el('template-empty').hidden = list.length > 0;
    el('template-table').hidden = list.length === 0;
  };
  var fillTemplate = function (text) {
    var first = (el('compose-to').value.split(',')[0] || '').replace(/<.*$/, '').replace(/"/g, '').trim().split(/\s+/)[0] || '';
    return text.replace(/\{first_name\}/g, first).replace(/\{subject\}/g, el('compose-subject').value).replace(/\{date\}/g, new Date().toLocaleDateString());
  };
  el('template-use').addEventListener('click', function () {
    var key = el('template-pick').value;
    var tp = templates().filter(function (x) { return x.key === key; })[0];
    if (!tp) { return; }
    if (!el('compose-subject').value && tp.subject) { el('compose-subject').value = fillTemplate(tp.subject); }
    var box = el('compose-text');
    box.value = fillTemplate(tp.text) + (box.value ? '\n' + box.value : '');
    box.focus();
  });
  el('template-save').addEventListener('click', function () {
    var name = window.prompt ? window.prompt('A name for this template') : '';
    if (!name) { return; }
    name = name.trim().slice(0, 60);
    var slug = name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '').slice(0, 40) || 'template';
    var value = JSON.stringify({ name: name, subject: el('compose-subject').value.slice(0, 500), text: el('compose-text').value.slice(0, 3000) });
    var change = {};
    change['tpl.' + slug] = value;
    savePrefs(change).then(function (ok) { say('compose-status', ok ? t('Saved as a template.') : t('The template could not be saved.'), ok); });
  });
  var loadPrefs = function () {
    return call('GET', '/api/v1/me/preferences').then(function (result) {
      if (result.status === 200 && result.data) { prefs = result.data.preferences || {}; }
      applyPrefs();
    });
  };
  var savePrefs = function (changes) {
    return call('PUT', '/api/v1/me/preferences', changes).then(function (result) {
      if (result.status === 200 && result.data) { prefs = result.data.preferences || {}; applyPrefs(); return true; }
      say('prefs-status', describe(result, t('Could not save the preferences')), false);
      return false;
    });
  };
  // ---- Branding: the server's from the page itself, the domain's own after
  // sign-in; the announcement above everything; the title takes the name.
  var branding = { name: '', logo: '', announcement: '' };
  var applyBranding = function (b) {
    branding = { name: (b && b.name) || '', logo: (b && b.logo) || '', announcement: (b && b.announcement) || '' };
    if (branding.name) {
      el('signin-title').textContent = branding.name;
      document.title = branding.name + t(' - my account');
      baseTitle = document.title;
      updateTitle();
    }
    var logo = el('signin-logo');
    clear(logo);
    if (branding.logo && branding.logo.indexOf('data:image/') === 0) {
      var img = document.createElement('img'); img.setAttribute('src', branding.logo); img.setAttribute('alt', branding.name || 'Logo');
      logo.appendChild(img);
    } else {
      logo.appendChild(node('span', 'hM'));
    }
    el('signin-announcement').textContent = branding.announcement;
    el('signin-announcement').hidden = !branding.announcement;
    el('announcement').textContent = branding.announcement;
    el('announcement').hidden = !branding.announcement;
  };
  (function () {
    var data = null;
    try { data = el('branding-data'); } catch (e) { data = null; }
    if (data) { try { applyBranding(JSON.parse(data.textContent || '{}')); } catch (e) { /* not JSON: the defaults stay */ } }
  })();
  var loadBranding = function () {
    var domain = me.indexOf('@') > 0 ? me.slice(me.indexOf('@') + 1) : '';
    if (!domain) { return; }
    call('GET', '/api/v1/portal/branding?domain=' + encodeURIComponent(domain)).then(function (result) {
      if (result.status === 200 && result.data) { applyBranding(result.data); }
    });
  };

  // ---- Support access: the consent switch, the record of the last access.
  el('support-allowed').addEventListener('change', function () {
    savePrefs({ support_allowed: el('support-allowed').checked ? '1' : null }).then(function (ok) {
      if (ok) { say('support-status', el('support-allowed').checked ? t('An administrator may now open your mailbox for support.') : t('Support access is off.'), true); }
    });
  });
  el('support-note-dismiss').addEventListener('click', function () { savePrefs({ support_last: null }); });
  var renderSupport = function () {
    el('support-allowed').checked = pref('support_allowed') === '1';
    var last = pref('support_last') || '';
    el('support-last').textContent = last ? 'An administrator last opened this mailbox for support on ' + last + '.' : '';
    el('support-last').hidden = !last;
    el('support-note-text').textContent = last ? 'An administrator opened this mailbox for support on ' + last + '.' : '';
    el('support-note').hidden = !last;
  };

  el('prefs-form').addEventListener('submit', function (event) {
    event.preventDefault();
    var ticked = [];
    var labels = el('notify-folders').children;
    for (var i = 0; i < labels.length; i++) { var tick = labels[i].children[0]; if (tick && tick.checked) { ticked.push(tick.value); } }
    savePrefs({ language: el('pref-language').value, theme: el('pref-theme').value, density: el('pref-density').value, undo_seconds: el('pref-undo').value,
                notify: el('pref-notify').checked ? '1' : '0', notify_folders: ticked.join(',') }).then(function (ok) {
      if (ok) { say('prefs-status', t('Saved.'), true); }
    });
  });
  el('pref-notify').addEventListener('change', function () {
    if (el('pref-notify').checked && 'Notification' in window && Notification.permission === 'default') { Notification.requestPermission(); }
  });

  // ---- Identities and the signature -----------------------------------------
  var loadIdentities = function () {
    return call('GET', '/api/v1/me/identities').then(function (result) {
      identities = (result.status === 200 && result.data && result.data.identities) || [];
      var select = el('compose-from');
      clear(select);
      identities.forEach(function (i) { var o = document.createElement('option'); o.value = i.address; o.textContent = i.header || i.address; select.appendChild(o); });
      var one = identities.length < 2;
      select.hidden = one; el('compose-from-label').hidden = one;
      renderAwayAddresses();
    });
  };
  var renderAwayAddresses = function () {
    var list = el('away-addresses');
    clear(list);
    var kinds = { account: 'your address', alias: 'an alias that points here', granted: 'a mailbox that lets you write as it' };
    identities.forEach(function (i) {
      var li = document.createElement('li');
      li.appendChild(node('span', i.address));
      li.appendChild(node('span', kinds[i.kind] || i.kind, 'kind'));
      list.appendChild(li);
    });
    if (!identities.length) { list.appendChild(node('li', t('Not known yet.'), 'muted')); }
  };
  var selectIdentity = function (address) {
    var wanted = (address || '').toLowerCase();
    var found = identities.filter(function (i) { return i.address === wanted; })[0];
    el('compose-from').value = found ? found.address : (identities[0] ? identities[0].address : '');
  };
  // A reply goes out from the address the message came to, when that is one
  // of the account's identities; otherwise from the account itself.
  // The identity a message was addressed to: whole addresses, compared
  // whole, and the account's own wins when it is among them.
  var identityAddressedIn = function (message) {
    var addresses = ((message.to || '') + ',' + (message.cc || '')).split(/[,;]/).map(function (entry) {
      var m = /<([^>]+)>/.exec(entry);
      return (m ? m[1] : entry).trim().toLowerCase();
    }).filter(Boolean);
    if (addresses.indexOf(me) >= 0) { return ''; }
    var hit = identities.filter(function (i) { return i.kind !== 'account' && addresses.indexOf(i.address) >= 0; })[0];
    return hit ? hit.address : '';
  };
  var addSignature = function () {
    if (!signature.enabled || !signature.text) { return; }
    var box = el('compose-text');
    box.value = '\n\n-- \n' + signature.text + (box.value ? '\n' + box.value : '');
    if (box.setSelectionRange) { box.setSelectionRange(0, 0); }
  };
  var afterPrime = function (mode, message) {
    if (mode === 'draft') { selectIdentity(addressOf(message.from)); }
    else { selectIdentity(identityAddressedIn(message)); addSignature(); }
    el('compose-text').focus();
  };
  var composeTo = function (entry) {
    blankCompose();
    composeKey = newComposeKey();
    addSignature();
    el('compose-to').value = entry;
    go('/compose');
    el('compose-subject').focus();
  };

  // ---- Print ----------------------------------------------------------------
  el('message-print').addEventListener('click', function () { window.print(); });

  // ---- The shortcut list and the palette -------------------------------------
  var closeOverlays = function () { el('keys-overlay').hidden = true; el('palette').hidden = true; };
  var openKeys = function () { el('palette').hidden = true; el('keys-overlay').hidden = false; el('keys-close').focus(); };
  el('keys-close').addEventListener('click', closeOverlays);
  ['keys-overlay', 'palette'].forEach(function (id) { el(id).addEventListener('click', function (e) { if (e.target === el(id)) { closeOverlays(); } }); });
  // Every entry is something the page already has a button for; the palette
  // is the fast path to it, not a second feature set.
  var paletteItems = [];
  var paletteMarked = 0;
  var paletteActions = function () {
    var items = [];
    var add = function (label, kind, run) { items.push({ label: label, kind: kind, run: run }); };
    add('Write a message', 'action', function () { go('/compose'); });
    add('Held mail', 'page', function () { go('/held'); });
    add('Manage folders', 'page', function () { go('/folders'); });
    add('Settings', 'page', function () { go('/settings'); });
    add('Filters', 'page', function () { go('/filters'); });
    add('Contacts', 'page', function () { go('/contacts'); });
    add('Away and forwarding', 'page', function () { go('/away'); });
    add('Storage', 'page', function () { go('/storage'); });
    add('Security', 'page', function () { go('/security'); });
    if (!directoryLinked) { add('Change password', 'page', function () { go('/password'); }); }
    add('Light or dark', 'action', function () { el('theme-btn').click(); });
    add('Keyboard shortcuts', 'action', openKeys);
    add('Sign out', 'action', function () { el('signout').click(); });
    var open = current && !el('mail-section').hidden && !el('message-view').hidden;
    if (open) {
      add('Reply', 'message', function () { el('message-reply').click(); });
      add('Reply all', 'message', function () { el('message-reply-all').click(); });
      add('Forward', 'message', function () { el('message-forward').click(); });
      add(current.flags.seen ? 'Mark as unread' : 'Mark as read', 'message', function () { el('message-unread').click(); });
      add(current.flags.flagged ? 'Remove flag' : 'Flag', 'message', function () { el('message-flag').click(); });
      add('Delete', 'message', function () { el('message-delete').click(); });
      add('Print', 'message', function () { window.print(); });
      allFolders.forEach(function (f) {
        if (f.id !== current.folder_id) { add('Move to ' + f.path, 'message', function () { el('message-move').value = String(f.id); el('message-move-go').click(); }); }
      });
    }
    allFolders.forEach(function (f) { add('Go to ' + f.path, 'folder', function () { go('/f/' + f.id); }); });
    return items;
  };
  var runPalette = function (item) { closeOverlays(); item.run(); };
  var markPalette = function (n) {
    if (!paletteItems.length) { return; }
    paletteMarked = (n + paletteItems.length) % paletteItems.length;
    var kids = el('palette-list').children;
    for (var i = 0; i < kids.length; i++) { kids[i].classList.toggle('on', i === paletteMarked); }
  };
  var renderPalette = function (items) {
    var list = el('palette-list');
    clear(list);
    paletteItems = items.slice(0, 12);
    paletteMarked = 0;
    paletteItems.forEach(function (item, i) {
      var li = document.createElement('li'); li.setAttribute('role', 'option');
      li.appendChild(node('span', item.label)); li.appendChild(node('span', item.kind, 'kind'));
      li.classList.toggle('on', i === 0);
      li.addEventListener('mousedown', function (e) { e.preventDefault(); runPalette(item); });
      list.appendChild(li);
    });
  };
  var paletteTimer = null;
  var filterPalette = function () {
    var q = el('palette-input').value.trim().toLowerCase();
    var words = q ? q.split(/\s+/) : [];
    var matches = function (label) { var l = label.toLowerCase(); return words.every(function (w) { return l.indexOf(w) >= 0; }); };
    var items = paletteActions().filter(function (i) { return matches(i.label); });
    renderPalette(items);
    if (paletteTimer) { clearTimeout(paletteTimer); paletteTimer = null; }
    if (q.length < 2) { return; }
    // "write to anna": the address book, as the To field completes from it.
    paletteTimer = setTimeout(function () {
      var wanted = q.replace(/^(write|compose|mail|to)\s+(to\s+)?/, '');
      call('GET', '/api/v1/me/contacts?limit=6&q=' + encodeURIComponent(wanted)).then(function (result) {
        if (el('palette').hidden || el('palette-input').value.trim().toLowerCase() !== q) { return; }
        var found = (result.status === 200 && result.data && result.data.contacts) || [];
        if (!found.length) { return; }
        var extra = found.map(function (c) {
          var entry = c.name ? c.name + ' <' + c.address + '>' : c.address;
          return { label: 'Write to ' + entry, kind: 'contact', run: function () { composeTo(entry + ', '); } };
        });
        renderPalette(items.concat(extra));
      });
    }, 150);
  };
  var openPalette = function () {
    el('keys-overlay').hidden = true;
    el('palette').hidden = false;
    el('palette-input').value = '';
    filterPalette();
    el('palette-input').focus();
  };
  el('palette-input').addEventListener('input', filterPalette);
  el('palette-input').addEventListener('keydown', function (e) {
    if (e.key === 'ArrowDown') { e.preventDefault(); markPalette(paletteMarked + 1); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); markPalette(paletteMarked - 1); }
    else if (e.key === 'Enter') { e.preventDefault(); if (paletteItems[paletteMarked]) { runPalette(paletteItems[paletteMarked]); } }
    else if (e.key === 'Escape') { e.preventDefault(); closeOverlays(); }
  });
  document.addEventListener('keydown', function (event) {
    if (el('account').hidden) { return; }
    if ((event.ctrlKey || event.metaKey) && !event.altKey && (event.key === 'k' || event.key === 'K')) { event.preventDefault(); openPalette(); return; }
    if (event.key === 'Escape' && (!el('keys-overlay').hidden || !el('palette').hidden)) { closeOverlays(); return; }
    var tag = (event.target && event.target.tagName) || '';
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || event.ctrlKey || event.metaKey || event.altKey) { return; }
    if (event.key === '?') { event.preventDefault(); if (el('keys-overlay').hidden) { openKeys(); } else { closeOverlays(); } }
  });

  // The theme the last visit chose, for the sign-in page; the account's own
  // choice replaces it once there is an account.
  applyTheme(storedTheme());
  pending = parseHash().hash;
  load(true);
})();
