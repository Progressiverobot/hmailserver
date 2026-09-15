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
  var LANGUAGES = [['en', 'English'], ['cs', '\u010ce\u0161tina'], ['da', 'Dansk'], ['de', 'Deutsch'], ['el', '\u0395\u03bb\u03bb\u03b7\u03bd\u03b9\u03ba\u03ac'], ['es', 'Espa\u00f1ol'], ['fi', 'Suomi'], ['fr', 'Fran\u00e7ais'], ['it', 'Italiano'], ['ja', '\u65e5\u672c\u8a9e'], ['ko', '\ud55c\uad6d\uc5b4'], ['nb', 'Norsk bokm\u00e5l'], ['nl', 'Nederlands'], ['pl', 'Polski'], ['pt-BR', 'Portugu\u00eas (Brasil)'], ['pt-PT', 'Portugu\u00eas (Portugal)'], ['ru', '\u0420\u0443\u0441\u0441\u043a\u0438\u0439'], ['sv', 'Svenska'], ['tr', 'T\u00fcrk\u00e7e'], ['uk', '\u0423\u043a\u0440\u0430\u0457\u043d\u0441\u044c\u043a\u0430'], ['zh-Hans', '\u7b80\u4f53\u4e2d\u6587'], ['ar', '\u0627\u0644\u0639\u0631\u0628\u064a\u0629'], ['he', '\u05e2\u05d1\u05e8\u05d9\u05ea'], ['fa', '\u0641\u0627\u0631\u0633\u06cc']];
  // Written right to left: the document is mirrored for these - dir on the
  // root, which the stylesheet's logical properties follow. A message keeps
  // its own direction: the body panes and the compose fields are dir=auto.
  var RTL = { ar: true, he: true, fa: true };
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
    if (document.documentElement && document.documentElement.setAttribute) {
      document.documentElement.setAttribute('lang', code);
      document.documentElement.setAttribute('dir', RTL[code] ? 'rtl' : 'ltr');
    }
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
  var panels = ['mail-section', 'quarantine-section', 'folders-section', 'settings-section', 'filter-section', 'contacts-section', 'away-section', 'storage-section', 'security-section', 'password-section', 'scheduled-section'];
  var SETTINGS_PANELS = ['settings-section', 'away-section', 'filter-section', 'folders-section', 'storage-section', 'security-section', 'password-section'];
  var showPanel = function (id, title) {
    panels.forEach(function (p) { var e = el(p); if (e) { e.hidden = p !== id; } });
    el('settings-head').hidden = SETTINGS_PANELS.indexOf(id) < 0;
    el('view-title').textContent = title;
    var section = el(id);
    if (section && section.scrollTop) { section.scrollTop = 0; }
    if (id === 'mail-section') { applyPane(); }
    closeMenus();
    el('account').classList.remove('nav-open');
  };
  var markNav = function (hash) {
    [el('folder-nav'), el('view-nav'), el('side-nav')].forEach(function (nav) {
      if (!nav) { return; }
      var children = nav.children;
      for (var i = 0; i < children.length; i++) {
        children[i].classList.toggle('on', !!hash && children[i].getAttribute('data-route') === hash);
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
  var folderName = function (id) { var f = foldersById[id]; return f ? (f.path.toUpperCase() === 'INBOX' ? t('Inbox') : f.path) : 'Mail'; };
  // The folders a message may be moved to, as options; an option given
  // first (such as "leave it") goes before them.
  var fillFolderSelect = function (select, homeId, first) {
    clear(select);
    if (first) { select.appendChild(first); }
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
    var special = function (f, use) { return String(f.special_use || '').indexOf(String.fromCharCode(92) + use) >= 0; };
    // One entry: an icon, the name, the count - the third child, which the
    // change probe updates in place.
    var entry = function (f, iconName, route, label, count, cls) {
      var b = node('button', undefined, cls || '');
      b.type = 'button';
      b.setAttribute('data-route', route);
      var ico = node('span', undefined, 'ico'); ico.appendChild(icon(iconName)); b.appendChild(ico);
      b.appendChild(node('span', label, 'nm'));
      b.appendChild(node('span', count ? String(count) : '', 'ct'));
      if (count && !(f && special(f, 'Drafts'))) { b.classList.add('unread'); }
      b.addEventListener('click', function () { go(route); });
      if (f && route.indexOf('/f/') === 0) { dropTarget(b, f.id); }
      nav.appendChild(b);
      return b;
    };
    // The system folders first, in the order every mail client uses, then
    // the reader's own, then what others share. The Snoozed folder is the
    // server's waiting room, not a place to file things: it stays out.
    var order = { Sent: 2, Drafts: 3, Archive: 4, Junk: 5, Trash: 6 };
    var icons = { inbox: 'inbox', sent: 'send', drafts: 'draft', archive: 'archive', junk: 'junk', trash: 'trash' };
    var system = [], rest = [];
    folders.filter(function (f) { return !f.owner && f.path !== 'Snoozed'; }).forEach(function (f) {
      if (f.id === inboxId) { system.push([f, 'inbox', 0]); return; }
      var found = null;
      Object.keys(order).forEach(function (use) { if (!found && special(f, use)) { found = [f, use.toLowerCase(), order[use]]; } });
      if (found) { system.push(found); } else { rest.push(f); }
    });
    system.sort(function (a, b) { return a[2] - b[2]; });
    system.forEach(function (s) {
      var f = s[0];
      entry(f, icons[s[1]], '/f/' + f.id, s[1] === 'inbox' ? t('Inbox') : (f.name || f.path), s[1] === 'drafts' ? f.count : f.unseen, '');
      if (s[2] === 0) { entry(null, 'star', '/starred', t('Starred'), 0, ''); }
    });
    var saved = savedSearches();
    if (saved.length) {
      nav.appendChild(node('div', t('Saved searches'), 'navhead'));
      saved.forEach(function (s, i) { entry(null, 'search', savedSearchRoute(s, i), s.name, 0, ''); });
    }
    if (rest.length) {
      var head = node('div', t('Folders'), 'navhead');
      head.appendChild(node('span', undefined, 'grow'));
      var add = iconButton('plus', t('New folder'));
      add.addEventListener('click', function () { go('/folders'); });
      head.appendChild(add);
      nav.appendChild(head);
      // A folder under INBOX - the shape some clients make every folder in -
      // is listed as one of the reader's own, not as the inbox's child.
      rest.forEach(function (f) {
        var indent = f.path.toUpperCase().indexOf('INBOX' + delimiter.toUpperCase()) === 0 ? f.depth - 1 : f.depth;
        entry(f, 'folder', '/f/' + f.id, f.name || f.path, f.unseen, indent > 0 ? (indent > 1 ? 'child2' : 'child') : '');
      });
    }
    var lastOwner = '';
    folders.filter(function (f) { return f.owner; }).forEach(function (f) {
      if (f.owner !== lastOwner) {
        nav.appendChild(node('div', f.owner.charAt(0) === '#' ? t('Public folders') : t('Shared by ') + f.owner, 'navhead'));
        lastOwner = f.owner;
      }
      entry(f, 'folder', '/f/' + f.id, f.name || f.path, f.unseen, f.depth ? 'child' : '');
    });
    markNav(currentNavHash());
    updateTitle();
    renderNotifyFolders();
    fillFolderSelect(el('rule-folder'), 0);
    fillFolderSelect(el('folder-export-target'), 0);
    fillFolderSelect(el('folder-import-target'), 0);
    setExportLink();
  };
  var loadFolders = function () {
    return call('GET', '/api/v1/me/folders').then(function (result) {
      if (result.status === 200 && result.data) { renderFolders(result.data); renderQuickSteps(); }
      return result;
    });
  };

  // ---- The listing --------------------------------------------------------
  var listRows = [];
  var cursor = -1;
  var selected = {};
  var wholeFolder = false;
  var lastListing = null;
  // What "where the reader was" means, kept whenever the listing is left:
  // the scroll offset, the message the cursor was on, and the ticked boxes.
  var rememberPlace = function () {
    if (!lastListing || el('mail-section').hidden || el('message-list').hidden) { return; }
    lastListing.scroll = el('message-list').scrollTop;
    lastListing.cursorId = cursor >= 0 && listRows[cursor] ? listRows[cursor].id : 0;
    lastListing.selected = selected;
  };
  // The list and one message share a panel, so what belongs to the list -
  // the mailbox card above it and the keyboard hint - goes with it.
  var showList = function () {
    el('message-list').hidden = false;
    el('list-tools').hidden = false;
    if (!current) {
      el('message-view').hidden = true;
      el('pane-empty').hidden = false;
      el('mail-section').classList.remove('has-message');
    }
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
    fillFolderSelect(el('bulk-move'), state.folderId);
    var all = el('select-all');
    if (all) {
      all.checked = listRows.length > 0 && listRows.every(function (r) { return !!selected[r.id]; });
      all.indeterminate = ids.length > 0 && !all.checked;
    }
    if (!all || !all.checked) { wholeFolder = false; }
    renderSelectNote();
    el('bulk-bar').hidden = ids.length === 0;
    el('bulk-count').textContent = wholeFolder ? tf('All of {0} selected', selectionScope()) : ids.length ? tf('{0} selected', ids.length) : '';
    listRows.forEach(function (r) { r.row.classList.toggle('selected', !!selected[r.id]); });
  };
  var clearSelection = function () { selected = {}; wholeFolder = false; renderBulk(); };
  // Shift-click and Ctrl-click in the list, as the file manager has them: a
  // shift-click ticks every row from the last row clicked to this one, a
  // ctrl-click (Cmd on a Mac) ticks or unticks this one without opening it.
  // The anchor is the row last clicked in any way, or ticked with x; when it
  // is no longer in the listing, the cursor row stands in for it.
  var anchorId = 0;
  var tickEntry = function (entry, on) {
    (entry.ids || [entry.id]).forEach(function (id) { selected[id] = on; });
    if (entry.box) { entry.box.checked = on; }
  };
  var tickRange = function (entry, on) {
    var to = listRows.indexOf(entry);
    if (to < 0) { return; }
    var from = -1;
    listRows.forEach(function (r, i) { if (r.id === anchorId) { from = i; } });
    if (from < 0) { from = cursor >= 0 ? cursor : to; }
    for (var i = Math.min(from, to); i <= Math.max(from, to); i++) { tickEntry(listRows[i], on); }
    renderBulk();
  };
  // True when the click selected rather than opened, and has been dealt with.
  var selectingClick = function (event, entry) {
    if (!event || !(event.shiftKey || event.ctrlKey || event.metaKey)) { anchorId = entry.id; return false; }
    if (event.preventDefault) { event.preventDefault(); }
    if (event.shiftKey) { tickRange(entry, true); return true; }
    tickEntry(entry, !selected[entry.id]);
    anchorId = entry.id;
    renderBulk();
    return true;
  };
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
    if (typeof to === 'number') { return call('POST', '/api/v1/me/messages/' + id + '/move', { folder_id: to }); }
    return call('POST', '/api/v1/me/messages/' + id + '/move', { to: to });
  };
  var afterFiling = function (result, what) {
    if (result.status === 200) { say('mail-status', what, true); lastListing = null; loadFolders(); return true; }
    say('mail-status', describe(result, t('Could not file the message')), false);
    return false;
  };
  // A collapsed conversation is filed whole, one message after another,
  // as its tick box selects it whole.
  // A swipe's action, from the settings: archive, delete, junk, mark as read, or nothing.
  var swipeAction = function (what, entry) {
    if (what === 'archive' || what === 'delete') { fileRow(entry, what); }
    else if (what === 'junk') { fileRow(entry, junkTargetFor(state.folderId)); }
    else if (what === 'read') { markRows([entry], true); }
  };
  var fileRow = function (row, to) {
    if (!row) { return; }
    var entries = (row.ids || [row.id]).map(function (id) { return { id: id, folderId: row.folderId || state.folderId }; });
    var at = cursor;
    fileMany(entries, to).then(function (ok) {
      if (!ok) { return; }
      if (current && entries.some(function (e) { return e.id === current.id; })) { closeMessage(true); }
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
  var chipsFor = function (keywords) { var box = node('span', undefined, 'chips'); (keywords || []).filter(function (k) { return k.charAt(0) !== '$'; }).forEach(function (k) { box.appendChild(chip(k)); }); return box; };
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
    (current.flags.keywords || []).filter(function (k) { return k.charAt(0) !== '$'; }).forEach(function (k) { box.appendChild(chip(k)); });
  };
  var setKeywords = function (add, remove) {
    if (!current) { return Promise.resolve(false); }
    return call('PUT', '/api/v1/me/messages/' + current.id + '/flags', { keywords_add: add, keywords_remove: remove }).then(function (result) {
      if (result.status === 200 && result.data && result.data.flags) {
        current.flags = result.data.flags;
        renderMessageLabels();
        renderLabelMenu();
        lastListing = null;
        if (remove.length && !add.length) {
          // Undo puts the label back on the same message, whatever is open by then.
          var was = current;
          toast(t('Label removed.'), function () {
            call('PUT', '/api/v1/me/messages/' + was.id + '/flags', { keywords_add: remove, keywords_remove: [] }).then(function (back) {
              if (back.status === 200 && back.data && back.data.flags && current && current.id === was.id) { current.flags = back.data.flags; renderMessageLabels(); renderLabelMenu(); }
              lastListing = null;
            });
          });
        }
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
    var row = node('div', undefined, 'msg' + (m.flags.seen ? '' : ' unseen') + (inThread ? ' in-thread' : ''));
    row.setAttribute('role', 'listitem');
    paintRow(row, colourFor(m));
    var folderId = m.folder_id || state.folderId;
    var entry = { id: m.id, row: row, m: m, folderId: folderId };
    if (isPinned(m)) { row.classList.add('pinned'); }
    rowGestures(row, entry);
    var cb = node('span', undefined, 'cb');
    var box = document.createElement('input'); box.type = 'checkbox'; box.checked = !!selected[m.id]; box.setAttribute('aria-label', t('Select'));
    box.addEventListener('click', function (event) { event.stopPropagation(); if (event.shiftKey) { tickRange(entry, box.checked); return; } selected[m.id] = box.checked; anchorId = m.id; renderBulk(); });
    cb.appendChild(box);
    row.appendChild(cb);
    entry.box = box;
    if (box.checked) { row.classList.add('selected'); }
    entry.star = starButton(m);
    row.appendChild(entry.star);
    row.appendChild(avatarFor(m.from));
    var outgoing = folderIs(folderId, 'Sent') || folderIs(folderId, 'Drafts');
    var who = node('div', outgoing ? t('To: ') + (recipientsOf(m.to).slice(0, 2).join(', ') || '?') : nameOf(m.from), 'who');
    who.setAttribute('title', (outgoing ? m.to : m.from) || '');
    row.appendChild(who);
    var what = node('div', undefined, 'what');
    what.appendChild(node('span', m.subject || t('(no subject)'), 'subject'));
    if (m.flags && m.flags.keywords && m.flags.keywords.length) { what.appendChild(chipsFor(m.flags.keywords)); }
    if (m.folder && state.everywhere) { what.appendChild(node('span', m.folder, 'badge')); }
    var nudge = nudgeFor(m, folderId);
    if (nudge) { entry.nudge = node('span', nudge, 'nudge'); what.appendChild(entry.nudge); }
    if (mentionsReader(m)) { var at = node('span', '@', 'mention'); at.setAttribute('title', t('Mentions you')); what.appendChild(at); }
    what.appendChild(node('span', m.snippet || '', 'snip'));
    row.appendChild(what);
    var meta = node('div', undefined, 'meta');
    if (m.has_attachments) { var clip = node('span', undefined, 'clip'); clip.setAttribute('title', t('Has attachments')); clip.appendChild(icon('clip', true)); meta.appendChild(clip); }
    if (isMuted(m)) { meta.appendChild(muteMark()); }
    var due = dueMark(m);
    if (due) { meta.appendChild(due); }
    var when = node('span', whenText(m), 'when'); when.setAttribute('title', fullDate(m)); meta.appendChild(when);
    row.appendChild(meta);
    var acts = node('div', undefined, 'acts');
    var act = function (name, title, fn) { var b = iconButton(name, title); b.addEventListener('click', function (e) { e.stopPropagation(); fn(); }); acts.appendChild(b); };
    act('archive', t('Archive'), function () { fileRow(entry, 'archive'); });
    act('trash', t('Delete'), function () { fileRow(entry, 'delete'); });
    act(m.flags.seen ? 'mail' : 'mail-open', m.flags.seen ? t('Mark as unread') : t('Mark as read'), function () { markRows([entry], !m.flags.seen); });
    row.appendChild(acts);
    row.addEventListener('click', function (event) { if (selectingClick(event, entry)) { return; } go('/m/' + m.id); });
    // On a touch screen: a swipe to the right archives, to the left deletes.
    var touchX = null;
    row.addEventListener('touchstart', function (e) { touchX = e.touches && e.touches.length === 1 ? e.touches[0].clientX : null; }, { passive: true });
    row.addEventListener('touchend', function (e) {
      if (touchX === null || !e.changedTouches || !e.changedTouches.length) { return; }
      var dx = e.changedTouches[0].clientX - touchX;
      touchX = null;
      if (dx > 90) { swipeAction(pref('swipe_right'), entry); } else if (dx < -90) { swipeAction(pref('swipe_left'), entry); }
    }, { passive: true });
    listRows.push(entry);
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
    // The Starred view puts the dated follow-ups first, the soonest at the
    // top; elsewhere the pinned messages come first.
    var ordered = state.everywhere && state.query === 'is:flagged' ? byDue(page.messages) : page.messages.filter(isPinned).concat(page.messages.filter(function (m) { return !isPinned(m); }));
    if (pref('view') !== 'threads' || page.query) {
      ordered.forEach(function (m) { shown[m.id] = true; });
      chunkedAppend(ordered, list, function (m) { return messageRow(m, false); });
      return;
    }
    // A null prototype: a References header spelling 'constructor' is a key, not a property.
    var groups = []; var byKey = Object.create(null);
    ordered.forEach(function (m) {
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
      paintRow(row, g.messages.map(colourFor).filter(function (c) { return c; })[0] || '');
      var folderId = newest.folder_id || state.folderId;
      var entry = { id: newest.id, ids: g.messages.map(function (m) { return m.id; }), ms: g.messages, row: row, m: newest, folderId: folderId };
      if (g.messages.some(isPinned)) { row.classList.add('pinned'); }
      rowGestures(row, entry);
      var cb = node('span', undefined, 'cb');
      var box = document.createElement('input'); box.type = 'checkbox'; box.setAttribute('aria-label', t('Select the conversation'));
      box.checked = g.messages.every(function (m) { return !!selected[m.id]; });
      box.addEventListener('click', function (event) { event.stopPropagation(); if (event.shiftKey) { tickRange(entry, box.checked); return; } g.messages.forEach(function (m) { selected[m.id] = box.checked; }); anchorId = newest.id; renderBulk(); });
      cb.appendChild(box); row.appendChild(cb);
      entry.box = box;
      entry.star = starButton(newest);
      row.appendChild(entry.star);
      row.appendChild(avatarFor(newest.from));
      var senders = [];
      g.messages.forEach(function (m) { var s = nameOf(m.from); if (senders.indexOf(s) < 0) { senders.push(s); } });
      var who = node('div', senders.slice(0, 3).join(', ') + (senders.length > 3 ? ', ...' : ''), 'who');
      var count = node('span', String(g.messages.length), 'tc');
      count.setAttribute('title', t('Show every message of the conversation'));
      who.appendChild(count);
      row.appendChild(who);
      var what = node('div', undefined, 'what');
      what.appendChild(node('span', newest.subject || t('(no subject)'), 'subject'));
      var keywords = [];
      g.messages.forEach(function (m) { (m.flags.keywords || []).forEach(function (k) { if (keywords.indexOf(k) < 0) { keywords.push(k); } }); });
      if (keywords.length) { what.appendChild(chipsFor(keywords)); }
      var nudge = nudgeFor(newest, folderId);
      if (nudge) { entry.nudge = node('span', nudge, 'nudge'); what.appendChild(entry.nudge); }
      what.appendChild(node('span', newest.snippet || '', 'snip'));
      row.appendChild(what);
      var meta = node('div', undefined, 'meta');
      if (g.messages.some(function (m) { return m.has_attachments; })) { var clip = node('span', undefined, 'clip'); clip.appendChild(icon('clip', true)); meta.appendChild(clip); }
      if (g.messages.some(isMuted)) { meta.appendChild(muteMark()); }
      var soonest = soonestDue(g.messages);
      if (soonest) { meta.appendChild(dueMark(soonest)); }
      var when = node('span', whenText(newest), 'when'); when.setAttribute('title', fullDate(newest)); meta.appendChild(when);
      row.appendChild(meta);
      var acts = node('div', undefined, 'acts');
      var act = function (name, title, fn) { var b = iconButton(name, title); b.addEventListener('click', function (e) { e.stopPropagation(); fn(); }); acts.appendChild(b); };
      act('archive', t('Archive'), function () { fileRow(entry, 'archive'); });
      act('trash', t('Delete'), function () { fileRow(entry, 'delete'); });
      act(unseen ? 'mail-open' : 'mail', unseen ? t('Mark as read') : t('Mark as unread'), function () { markRows([entry], unseen); });
      row.appendChild(acts);
      var toggle = function () {
        expandedThreads[g.key] = !expandedThreads[g.key];
        if (!lastListing) { return; }
        renderMessages(lastListing.page);
        listRows.forEach(function (r, i) { if (r.toggle && r.id === newest.id) { setCursor(i, true); } });
      };
      count.addEventListener('click', function (event) { event.stopPropagation(); toggle(); });
      row.addEventListener('click', function (event) { if (selectingClick(event, entry)) { return; } go('/m/' + newest.id); });
      entry.toggle = toggle;
      listRows.push(entry);
      list.appendChild(row);
      if (expandedThreads[g.key]) {
        // Oldest first inside the conversation, newest last, as it is read.
        g.messages.slice().reverse().forEach(function (m) { list.appendChild(messageRow(m, true)); });
      }
    });
  };
  var lastPage = null;
  var renderMessages = function (page) {
    var list = el('message-list');
    clear(list);
    list.classList.remove('skeleton');
    listRows = []; cursor = -1;
    lastPage = page;
    renderTabs(page);
    var tabbed = tabsActive(page);
    var visible = tabbed ? page.messages.filter(function (m) { return tabOf(m) === activeTab; }) : page.messages;
    if (!visible.length) {
      selected = {}; renderBulk(); renderCount(page);
      var empty = node('div', undefined, 'empty');
      empty.appendChild(icon('empty'));
      empty.appendChild(node('div', page.query ? t('Nothing matched.') : tabbed && page.messages.length ? tf('Nothing under {0}.', tabName(activeTab)) : t('This folder is empty.')));
      list.appendChild(empty);
      renderSearchNote(list, page);
      return;
    }
    var shown = {};
    renderRows(tabbed ? { messages: visible, query: page.query } : page, list, shown);
    Object.keys(selected).forEach(function (k) { if (!shown[k]) { delete selected[k]; } });
    setCursor(0, true);
    renderBulk();
    renderCount(page);
    renderSearchNote(list, page);
    if (current) { renderConversation(current, page); }
    if (current) { listRows.forEach(function (r) { r.row.classList.toggle('open', r.id === current.id || (r.ids || []).indexOf(current.id) >= 0); }); }
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
        askReplies(result.data);
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
    var scroll = el('message-list').scrollTop;
    return loadMessages().then(function () {
      selected = {};
      Object.keys(ticked).forEach(function (k) { if (ticked[k] && listRows.filter(function (r) { return String(r.id) === String(k); }).length) { selected[k] = true; } });
      listRows.forEach(function (r) { r.box.checked = !!selected[r.id]; });
      renderBulk();
      var at = -1;
      listRows.forEach(function (r, i) { if (r.id === onId) { at = i; } });
      if (at >= 0) { setCursor(at, true); }
      el('message-list').scrollTop = scroll;
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
  // What the remote-image block kept out, counted from the HTML as sent: the
  // images that point at a host, the hosts, and the ones that look like
  // tracking pixels - a pixel or less in size, hidden, or named as one. The
  // sender's habit is the number of their messages seen carrying pixels, kept
  // in this browser, each message counted once.
  var countRemote = function (html) {
    var out = { images: 0, hosts: {}, pixels: 0 };
    var tags = String(html || '').match(/<img\b[^>]*>/gi) || [];
    tags.forEach(function (tag) {
      var src = (tag.match(/\bsrc\s*=\s*["']?\s*(https?:\/\/[^"'\s>]+)/i) || [])[1];
      if (!src) { return; }
      out.images++;
      var host = (src.match(/^https?:\/\/([^\/?#]+)/i) || [])[1];
      if (host) { out.hosts[host.toLowerCase()] = true; }
      var w = (tag.match(/\bwidth\s*=\s*["']?\s*(\d+)/i) || [])[1];
      var h = (tag.match(/\bheight\s*=\s*["']?\s*(\d+)/i) || [])[1];
      var tiny = (w !== undefined && Number(w) <= 1) || (h !== undefined && Number(h) <= 1) || /width\s*:\s*[01]px|height\s*:\s*[01]px/i.test(tag);
      var hidden = /display\s*:\s*none|visibility\s*:\s*hidden/i.test(tag);
      var named = /pixel|track|beacon|open\.gif|\/o\.gif|\/open\b|\/img\/open|spacer/i.test(src);
      if (tiny || hidden || named) { out.pixels++; }
    });
    (String(html || '').match(/url\(\s*["']?https?:\/\/[^)"']+/gi) || []).forEach(function (u) {
      out.images++;
      var host = (u.match(/https?:\/\/([^\/?#"')]+)/i) || [])[1];
      if (host) { out.hosts[host.toLowerCase()] = true; }
    });
    out.hostCount = Object.keys(out.hosts).length;
    return out;
  };
  var TRACKERS = 'hmPortalTrackers';
  var trackerHabit = function (sender, messageId, pixels) {
    var record = {};
    try { record = JSON.parse(localStorage.getItem(TRACKERS) || '{}') || {}; } catch (e) { record = {}; }
    var entry = record[sender] || { count: 0, seen: [] };
    if (pixels && entry.seen.indexOf(messageId) < 0) {
      entry.count++; entry.seen.push(messageId);
      if (entry.seen.length > 50) { entry.seen = entry.seen.slice(-50); }
      record[sender] = entry;
      try { localStorage.setItem(TRACKERS, JSON.stringify(record)); } catch (e) { /* a browser without storage forgets */ }
    }
    return entry.count;
  };
  var describeRemote = function (showing, sender) {
    var counted = countRemote(showing.html);
    el('message-remote-count').textContent = counted.images ? tf('{0} remote image(s) from {1} host(s) were not loaded; {2} look like tracking pixels.', counted.images, counted.hostCount, counted.pixels) : '';
    var habit = trackerHabit(sender, showing.id, counted.pixels);
    el('message-remote-habit').textContent = habit ? tf('This sender has used tracking pixels in {0} message(s).', habit) : '';
  };
  var renderHtml = function () {
    var frame = el('message-html');
    if (!current || !current.html || !showHtml) { frame.hidden = true; frame.removeAttribute('src'); el('message-text').hidden = false; return Promise.resolve(); }
    var showing = current;
    var sender = addressOf(showing.from).toLowerCase();
    var allowed = remoteAllowed.id === showing.id || remoteSenders().indexOf(sender) >= 0;
    el('message-remote').hidden = !(showing.html_remote && !allowed);
    if (!el('message-remote').hidden) { describeRemote(showing, sender); }
    frame.setAttribute('src', '/api/v1/me/messages/' + showing.id + '/html' + (allowed ? '?remote=1' : ''));
    frame.hidden = false;
    el('message-text').hidden = true;
    return Promise.resolve();
  };
  var renderActions = function () {
    if (!current) { return; }
    var seen = current.flags.seen;
    var unread = el('message-unread');
    unread.setAttribute('title', seen ? t('Mark as unread') : t('Mark as read'));
    unread.setAttribute('aria-label', seen ? t('Mark as unread') : t('Mark as read'));
    setIcon(unread, seen ? 'mail' : 'mail-open');
    var flag = el('message-flag');
    paintStar(flag, starStage(current));
    flag.setAttribute('aria-label', starTitle(starStage(current)));
    var follow = el('message-followup');
    var dueDay = dueOf(current);
    follow.classList.toggle('on', hasFollowUp(current));
    follow.setAttribute('title', dueDay ? tf('Due {0}', dueText(dueDay)) : t('Follow up'));
    follow.setAttribute('aria-label', dueDay ? tf('Due {0}', dueText(dueDay)) : t('Follow up'));
    el('followup-clear').hidden = !hasFollowUp(current);
    el('followup-menu').hidden = true;
    el('label-menu').hidden = true;
    renderMessageLabels();
    el('message-edit').hidden = !current.flags.draft;
    el('message-pin').textContent = isPinned(current) ? t('Unpin') : t('Pin');
    el('message-mute').textContent = isMuted(current) ? t('Unmute') : t('Mute');
    var junk = el('message-junk');
    var isJunk = folderIs(current.folder_id, 'Junk');
    junk.setAttribute('title', isJunk ? t('Not junk') : t('Junk'));
    junk.setAttribute('aria-label', isJunk ? t('Not junk') : t('Junk'));
    fillFolderSelect(el('message-move'), current.folder_id);
    listRows.forEach(function (r) { r.row.classList.toggle('open', r.id === current.id || (r.ids || []).indexOf(current.id) >= 0); });
  };
  var renderMessage = function (m) {
    el('mail-section').classList.add('has-message');
    el('message-view').hidden = false;
    el('pane-empty').hidden = true;
    closeMenus();
    el('message-subject').textContent = m.subject || t('(no subject)');
    var avatar = el('message-avatar');
    avatar.textContent = initialsOf(m.from);
    if (avatar.style) { avatar.style.background = avatarColour(addressOf(m.from)); }
    el('message-from-name').textContent = nameOf(m.from);
    var address = addressOf(m.from);
    el('message-from-address').textContent = address && address !== nameOf(m.from) ? '<' + address + '>' : '';
    var to = recipientsOf(m.to);
    el('message-to-line').textContent = t('to ') + (to.length ? to.slice(0, 3).join(', ') + (to.length > 3 ? ', ...' : '') : t('me'));
    el('message-date').textContent = fullDate(m);
    el('message-meta').textContent = 'From ' + (m.from || '?') + (m.to ? ' to ' + m.to : '') + (m.cc ? ', cc ' + m.cc : '') + ' - ' + (m.date || m.received);
    var details = el('message-details');
    clear(details);
    details.hidden = true;
    el('message-details-toggle').setAttribute('aria-expanded', 'false');
    [['From', m.from], ['To', m.to], ['Cc', m.cc], ['Date', m.date || m.received], ['Message-ID', m.message_id]].forEach(function (pair) {
      if (!pair[1]) { return; }
      details.appendChild(node('span', t(pair[0])));
      details.appendChild(node('span', pair[1]));
    });
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
    el('message-headers-toggle').textContent = t('Headers');
    el('message-headers-toggle').hidden = !m.headers;
    el('message-receipt').hidden = !(m.receipt_requested_by && pref('receipts') !== 'never');
    el('snooze-menu').hidden = true;
    el('followup-menu').hidden = true;
    el('message-unsubscribe').hidden = !m.list_unsubscribe;
    el('message-source').setAttribute('href', '/api/v1/me/messages/' + m.id + '/source');
    el('message-source').setAttribute('download', 'message-' + m.id + '.eml');
    var text = m.text || '';
    if (!text && m.html) {
      // An HTML-only message is read as a document and only its text is
      // shown: nothing in it runs, loads or renders.
      text = new DOMParser().parseFromString(m.html, 'text/html').body.textContent || '';
    }
    if (m.truncated) { text = t('This message is too large to show here; open it in your mail program.') + ((m.attachments || []).length ? t(' Its attachments can be downloaded below.') : ''); }
    el('message-text').textContent = text || t('(no text)');
    var attachments = el('message-attachments');
    clear(attachments);
    (m.attachments || []).forEach(function (a) {
      var link = node('a', undefined, 'att');
      link.href = '/api/v1/me/messages/' + m.id + '/attachments/' + a.index;
      link.setAttribute('download', a.name);
      link.setAttribute('title', a.name + ' (' + (a.content_type ? a.content_type + ', ' : '') + format(a.size) + ')');
      link.appendChild(icon('file'));
      var words = node('div');
      words.appendChild(node('div', a.name, 'n'));
      words.appendChild(node('div', format(a.size) + (a.content_type ? ' \u00b7 ' + a.content_type : '') + (a.content_id ? ' \u00b7 ' + t('in the message') : ''), 's'));
      link.appendChild(words);
      attachments.appendChild(link);
    });
    current = m;
    renderConversation(m);
    renderActions();
    smimeInspect(m);
    el('message-html-toggle').hidden = !m.html || m.truncated;
    renderHtml();
    if (el('message-body').scrollTop) { el('message-body').scrollTop = 0; }
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
  // The attachment reminder: a body that speaks of an attachment - attached,
  // attachment, enclosed - in the reader's own lines. A quoted line is what
  // the other person wrote, and so is everything under a forwarded message's
  // rule, so neither counts.
  var mentionsAttachment = function (text) {
    var own = String(text || '').split('---------- Forwarded message ----------')[0];
    own = own.split('\n').filter(function (line) { return line.replace(/^\s+/, '').charAt(0) !== '>'; }).join('\n');
    return /\b(attached|attachments?|enclosed)\b/i.test(own);
  };
  var anythingAttached = function () {
    var picked = el('compose-files').files;
    return !!((picked && picked.length) || dropped.length || carried.length || linked.length);
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
      if (folder && count) {
        var shown = folderIs(id, 'Drafts') ? folder.count : folder.unseen;
        count.textContent = shown ? String(shown) : '';
        children[i].classList.toggle('unread', !!folder.unseen && !folderIs(id, 'Drafts'));
      }
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
        // A page left open overnight: the follow-ups due today are asked for once the day turns.
        if (followUps.day && followUps.day !== dueToday()) { followUpsCheck(); }
        if (had && (result.data.changed || unknown)) {
          loadFolders().then(function () {
            if (!el('mail-section').hidden && !el('message-list').hidden) { reloadKeepingPlace(); }
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
    dropMessage();
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
      el('message-list').scrollTop = lastListing.scroll || 0;
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
    dropMessage();
    var saved = params.saved !== undefined ? savedSearches()[Number(params.saved)] : null;
    showPanel('mail-section', saved && saved.q === state.query ? saved.name : 'Search - \'' + state.query + '\'');
    markNav(saved && saved.q === state.query ? savedSearchRoute(saved, Number(params.saved)) : '');
    showList();
    renderListTools();
    loadMessages();
  };
  var showMessage = function (id) {
    showPanel('mail-section', folderName(state.folderId));
    markNav(currentNavHash());
    showList();
    if (!lastListing && state.folderId && !state.query) { loadMessages(); }
    openMessage(id).then(function (m) {
      if (!m) { return; }
      if (!state.folderId && m.folder_id) {
        state.folderId = m.folder_id;
        el('view-title').textContent = folderName(m.folder_id);
        markNav('/f/' + m.folder_id);
        loadMessages();
      }
      var index = -1;
      listRows.forEach(function (r, i) { if (r.id === m.id || (r.ids || []).indexOf(m.id) >= 0) { index = i; } });
      if (index >= 0) { setCursor(index, true); }
    });
  };
  // ---- The installed app ------------------------------------------------
  // The unread count on the installed app's icon - the App Badging API, the
  // same total the title carries, which is the folders the reader asked to
  // be told about. Nothing without the API, nothing said when it refuses.
  var updateBadge = function (total) {
    try {
      if (typeof navigator === 'undefined' || typeof navigator.setAppBadge !== 'function') { return; }
      var p = total ? navigator.setAppBadge(total) : navigator.clearAppBadge();
      if (p && p.catch) { p.catch(function () { /* the badge is a courtesy */ }); }
    } catch (why) { /* the badge is a courtesy */ }
  };
  // A mailto: URL (RFC 6068) into the fields of a new message: the addresses
  // before the question mark, then subject, body, cc and bcc as query keys,
  // each percent-decoded; anything else the URL carries is ignored.
  var parseMailto = function (raw) {
    var text = String(raw || '');
    if (!/^mailto:/i.test(text)) { return null; }
    text = text.slice(7);
    var cut = text.indexOf('?');
    var out = { to: decodeURIComponent(cut >= 0 ? text.slice(0, cut) : text) };
    (cut >= 0 ? text.slice(cut + 1) : '').split('&').forEach(function (pair) {
      if (!pair) { return; }
      var eq = pair.indexOf('=');
      var key = decodeURIComponent(eq < 0 ? pair : pair.slice(0, eq)).toLowerCase();
      var value = eq < 0 ? '' : decodeURIComponent(pair.slice(eq + 1).replace(/\+/g, ' '));
      if (key === 'to') { out.to = out.to ? out.to + ', ' + value : value; }
      else if (key === 'subject' || key === 'body' || key === 'cc' || key === 'bcc') { out[key] = value; }
    });
    return out;
  };
  // What another app shared to the installed webmail: the service worker
  // answered the share's POST by putting the title, text, link and files into
  // a cache of their own and sending the page here. Read once, then gone.
  var readShare = function () {
    if (typeof caches === 'undefined' || !caches.open) { return Promise.resolve(null); }
    var name = 'hm-portal-share';
    return caches.open(name).then(function (c) {
      return c.match('/portal/share-text').then(function (r) { return r ? r.json() : null; }).then(function (meta) {
        if (!meta) { return null; }
        var files = [];
        var chain = Promise.resolve();
        (meta.files || []).forEach(function (fileName, i) {
          chain = chain.then(function () {
            return c.match('/portal/share-file/' + i).then(function (r) {
              if (!r) { return null; }
              return r.blob().then(function (b) { files.push(new File([b], fileName, { type: b.type || 'application/octet-stream' })); });
            });
          });
        });
        return chain.then(function () { return caches.delete(name); }).then(function () {
          return { title: meta.title || '', text: meta.text || '', url: meta.url || '', files: files };
        });
      });
    }).catch(function () { return null; });
  };
  var fillCompose = function (fields) {
    if (fields.to) { el('compose-to').value = fields.to; }
    if (fields.cc) { el('compose-cc').value = fields.cc; }
    if (fields.bcc) { el('compose-bcc').value = fields.bcc; }
    if (fields.subject) { el('compose-subject').value = fields.subject; }
    if (fields.body) {
      var text = el('compose-text');
      text.value = fields.body + (text.value ? '\n\n' + text.value : '');
    }
    syncComposeRows();
  };
  // A new message opened with something already in it: a mailto: link the
  // browser handed over, the fields as query keys, or what another app
  // shared. Runs after the form has been blanked and signed.
  var prefillCompose = function (params) {
    var fields = params.mailto !== undefined ? (parseMailto(params.mailto) || {}) : {};
    ['to', 'cc', 'bcc', 'subject', 'body'].forEach(function (key) { if (params[key] !== undefined) { fields[key] = params[key]; } });
    fillCompose(fields);
    if (params.share === undefined) { return Promise.resolve(); }
    return readShare().then(function (shared) {
      if (!shared) { return; }
      var body = [shared.text, shared.url].filter(function (s) { return s; }).join('\n');
      fillCompose({ subject: shared.title, body: body });
      if (shared.files.length) {
        shared.files.forEach(function (f) { dropped.push(f); });
        el('compose-files-note').textContent = 'Attached from the share: ' + shared.files.map(function (f) { return f.name + ' (' + format(f.size) + ')'; }).join(', ');
      }
    });
  };
  var composePrefilled = function (params) {
    return ['mailto', 'share', 'to', 'cc', 'bcc', 'subject', 'body'].some(function (key) { return params[key] !== undefined; });
  };
  var showCompose = function (params) {
    var mode = params.reply !== undefined ? 'reply' : params.replyall !== undefined ? 'replyall' :
      params.forward !== undefined ? 'forward' : params.draft !== undefined ? 'draft' : 'new';
    var id = Number(params[mode] || 0);
    var key = mode + ':' + id;
    if (composeKey === key && !composePrefilled(params)) { openCompose(mode, id); el('compose-to').focus(); return; }
    composeKey = key;
    if (mode === 'new' || !id) { blankCompose(); addSignature(); syncComposeRows(); prefillCompose(params); openCompose('new', 0); el('compose-to').focus(); return; }
    if (current && current.id === id) { var was = current; prime(mode, was).then(function () { afterPrime(mode, was); openCompose(mode, id); }); return; }
    call('GET', '/api/v1/me/messages/' + id).then(function (result) {
      if (result.status === 200 && result.data) { prime(mode, result.data).then(function () { afterPrime(mode, result.data); openCompose(mode, id); }); return; }
      openCompose(mode, id);
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
    if (head === 'starred') { cameFromList = true; showSearch({ q: 'is:flagged' }); el('view-title').textContent = t('Starred'); markNav('/starred'); return; }
    if (head === 'scheduled') { showPanel('scheduled-section', t('Scheduled')); markNav('/scheduled'); loadScheduled(); return; }
    if (head === 'compose') {
      // The window opens over whatever page is shown; on a reload, over the list.
      if (!panels.some(function (p) { return el(p) && !el(p).hidden; })) { showMail(state.folderId || inboxId, {}); }
      showCompose(r.params);
      return;
    }
    if (head === 'held') { showPanel('quarantine-section', 'Held as suspected spam'); markNav('/held'); loadQuarantine(); return; }
    if (head === 'folders') { showPanel('folders-section', 'Manage folders'); markNav('/folders'); renderFolderAdmin(); return; }
    if (head === 'settings') { showPanel('settings-section', 'Settings'); markNav('/settings'); return; }
    if (head === 'filters') { showPanel('filter-section', 'Filters'); markNav('/filters'); renderColourRules(); return; }
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
    searchesWrite([]);
    hideSearchHistory();
    hideSuggestions();
    clear(el('search-suggest-list'));
    dueShownForget();
    followUps.day = '';
    replied = {};
    askedReplies = {};
    clear(el('quick-steps'));
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
    el('account-avatar').textContent = initialsOf(account.address);
    if (el('account-avatar').style) { el('account-avatar').style.background = avatarColour(account.address); }
    el('password-user').value = account.address;
    var used = account.quota.used_bytes, limit = account.quota.limit_mb * 1048576;
    if (limit > 0) {
      el('quota').textContent = format(used) + ' of ' + account.quota.limit_mb + ' MB used';
      el('quota-bar').style.width = Math.min(100, Math.round(100 * used / limit)) + '%';
    } else {
      el('quota').textContent = format(used) + ' used, no limit';
      el('quota-bar').style.width = '0';
    }
    el('password-changed').textContent = account.password_changed ? t('Password last changed ') + account.password_changed : '';
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
    el('signature-html').innerHTML = s.signature.html || '';
    signature = { enabled: !!s.signature.enabled, text: s.signature.text || '', html: s.signature.html || '' };
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
          followUpsCheck();
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
  el('message-back').addEventListener('click', function () { closeMessage(false); });
  el('message-unread').addEventListener('click', function () { if (current) { setFlags({ seen: !current.flags.seen }, false); } });
  el('message-flag').addEventListener('click', function () { if (current) { cycleStar(current, null); } });
  el('message-delete').addEventListener('click', function () { fileCurrent('delete'); });
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
    var was = current;
    fileMany([{ id: was.id, folderId: was.folder_id }], to).then(function (ok) {
      if (!ok) { return; }
      if (current === was) { closeMessage(true); }
      loadMessages();
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
    else if (event.key === 'm') { muteThread(entryOfCurrent(), !isMuted(current)); event.preventDefault(); }
  });
  el('message-move-go').addEventListener('click', function () {
    var target = el('message-move').value;
    if (!current || !target) { return; }
    var was = current;
    var back = was.folder_id;
    call('POST', '/api/v1/me/messages/' + was.id + '/move', { folder_id: Number(target) }).then(function (result) {
      if (result.status === 200) {
        lastListing = null;
        loadFolders();
        if (current === was) { closeMessage(true); }
        loadMessages();
        var moved = result.data && result.data.id;
        toast(t('Moved to ') + folderName(Number(target)), moved && back ? function () {
          call('POST', '/api/v1/me/messages/' + moved + '/move', { folder_id: back }).then(function () { lastListing = null; loadFolders(); loadMessages(); });
        } : null);
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
  el('compose-discard').addEventListener('click', function () { composeKey = newComposeKey(); blankCompose(); closeCompose(); toast(t('Discarded.')); });
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
          if (result.status !== 200) { say('contact-status', describe(result, t('Could not remove the contact')), false); return; }
          contactsCache = null; loadContacts();
          toast(t('Contact removed.'), function () {
            call('POST', '/api/v1/me/contacts', { name: c.name || '', address: c.address }).then(function (back) {
              if (back.status !== 201) { say('contact-status', describe(back, t('Could not add the contact')), false); }
              contactsCache = null; loadContacts();
            });
          });
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

  // ---- Contacts in and out ---------------------------------------------------
  // The address book as a file and back: vCard 3.0, one card per contact, is
  // what every phone and mail program reads; CSV with the header row Google
  // Contacts and Outlook write is what people already have. An import adds
  // what is not there yet, by address, and says how many it added.
  var vcardEscape = function (s) { return String(s || '').replace(/\\/g, '\\\\').replace(/\n/g, '\\n').replace(/,/g, '\\,').replace(/;/g, '\\;'); };
  var vcardUnescape = function (s) { return String(s || '').replace(/\\n/gi, '\n').replace(/\\([\\,;])/g, '$1'); };
  var contactsToVCard = function (list) {
    return list.map(function (c) {
      var name = c.name || c.address;
      var words = String(c.name || '').trim().split(/\s+/).filter(function (w) { return w; });
      var family = words.length > 1 ? words[words.length - 1] : '';
      var given = words.length > 1 ? words.slice(0, -1).join(' ') : (words[0] || '');
      return ['BEGIN:VCARD', 'VERSION:3.0', 'FN:' + vcardEscape(name), 'N:' + vcardEscape(family) + ';' + vcardEscape(given) + ';;;',
        'EMAIL;TYPE=INTERNET:' + vcardEscape(c.address), 'END:VCARD'].join('\r\n') + '\r\n';
    }).join('');
  };
  var csvCell = function (s) { s = String(s || ''); return /[",\r\n]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s; };
  var contactsToCsv = function (list) {
    return 'Name,E-mail Address\r\n' + list.map(function (c) { return csvCell(c.name) + ',' + csvCell(c.address) + '\r\n'; }).join('');
  };
  var parseVCards = function (text) {
    var lines = String(text).replace(/\r\n?/g, '\n').replace(/\n[ \t]/g, '').split('\n');
    var out = [];
    var card = null;
    lines.forEach(function (line) {
      var colon = line.indexOf(':');
      if (colon < 0) { return; }
      var head = line.slice(0, colon); var value = line.slice(colon + 1);
      var name = head.split(';')[0].toUpperCase();
      if (name === 'BEGIN' && value.toUpperCase() === 'VCARD') { card = { name: '', n: '', emails: [] }; return; }
      if (!card) { return; }
      if (name === 'END') {
        var display = card.name || card.n;
        card.emails.forEach(function (address) { if (address.indexOf('@') > 0) { out.push({ name: display, address: address }); } });
        card = null; return;
      }
      if (/ENCODING=QUOTED-PRINTABLE/i.test(head)) { return; }
      if (name === 'FN') { card.name = vcardUnescape(value).trim(); }
      else if (name === 'N') { var parts = value.split(';'); card.n = [vcardUnescape(parts[1] || ''), vcardUnescape(parts[0] || '')].join(' ').trim(); }
      else if (name === 'EMAIL') { card.emails.push(vcardUnescape(value).trim()); }
    });
    return out;
  };
  var parseCsvRows = function (text) {
    var rows = []; var row = []; var cell = ''; var quoted = false;
    var s = String(text).replace(/^\uFEFF/, '');
    for (var i = 0; i < s.length; i++) {
      var ch = s[i];
      if (quoted) {
        if (ch === '"') { if (s[i + 1] === '"') { cell += '"'; i++; } else { quoted = false; } }
        else { cell += ch; }
      } else if (ch === '"') { quoted = true; }
      else if (ch === ',') { row.push(cell); cell = ''; }
      else if (ch === '\n' || ch === '\r') { if (ch === '\r' && s[i + 1] === '\n') { i++; } row.push(cell); rows.push(row); row = []; cell = ''; }
      else { cell += ch; }
    }
    if (cell !== '' || row.length) { row.push(cell); rows.push(row); }
    return rows.filter(function (r) { return r.some(function (c) { return c.trim() !== ''; }); });
  };
  var parseContactsCsv = function (text) {
    var rows = parseCsvRows(text);
    if (rows.length < 2) { return []; }
    var header = rows[0].map(function (h) { return h.trim().toLowerCase(); });
    var emailCols = []; header.forEach(function (h, i) { if (/^e-?mail/.test(h) && !/type|label/.test(h)) { emailCols.push(i); } });
    var col = function (names) { for (var i = 0; i < header.length; i++) { if (names.indexOf(header[i]) >= 0) { return i; } } return -1; };
    var nameCol = col(['name', 'display name', 'full name']);
    var firstCol = col(['first name', 'given name']); var lastCol = col(['last name', 'family name', 'surname']);
    if (!emailCols.length) { return []; }
    var out = [];
    rows.slice(1).forEach(function (r) {
      var name = (nameCol >= 0 ? r[nameCol] : '') || [firstCol >= 0 ? r[firstCol] : '', lastCol >= 0 ? r[lastCol] : ''].join(' ');
      name = String(name || '').trim();
      emailCols.forEach(function (i) { var address = String(r[i] || '').trim(); if (address.indexOf('@') > 0) { out.push({ name: name, address: address }); } });
    });
    return out;
  };
  var downloadText = function (fileName, type, text) {
    // The text is left on the section as well: a page without object URLs
    // (a test harness) can still be asked what it would have sent.
    el('contacts-section').setAttribute('data-last-export', text);
    try {
      var link = document.createElement('a');
      link.href = URL.createObjectURL(new Blob([text], { type: type }));
      link.setAttribute('download', fileName);
      document.body.appendChild(link);
      if (typeof link.click === 'function') { link.click(); }
      document.body.removeChild(link);
    } catch (why) { /* nothing to download with */ }
  };
  var exportContacts = function (asCsv) {
    var list = contactsCache || [];
    if (!list.length) { say('contact-status', t('No contacts yet. Send a message, or add one above.'), false); return; }
    if (asCsv) { downloadText('contacts.csv', 'text/csv', contactsToCsv(list)); } else { downloadText('contacts.vcf', 'text/vcard', contactsToVCard(list)); }
    say('contact-status', tf('Exported {0} contact(s).', list.length), true);
  };
  el('contact-export-vcf').addEventListener('click', function () { exportContacts(false); });
  el('contact-export-csv').addEventListener('click', function () { exportContacts(true); });
  el('contact-import-file').addEventListener('change', function () {
    var input = el('contact-import-file');
    var file = (input.files || [])[0];
    if (!file) { return; }
    say('contact-status', t('Importing...'), true);
    file.text().then(function (text) {
      var list = /BEGIN:VCARD/i.test(text) ? parseVCards(text) : parseContactsCsv(text);
      input.value = '';
      if (!list.length) { say('contact-status', t('Nothing in that file looked like a contact.'), false); return; }
      var known = {};
      (contactsCache || []).forEach(function (c) { known[String(c.address).toLowerCase()] = true; });
      var fresh = [];
      list.forEach(function (c) { var key = c.address.toLowerCase(); if (!known[key]) { known[key] = true; fresh.push(c); } });
      var imported = 0;
      var chain = Promise.resolve();
      fresh.forEach(function (c) {
        chain = chain.then(function () {
          return call('POST', '/api/v1/me/contacts', { name: c.name, address: c.address }).then(function (result) { if (result.status === 201) { imported++; } });
        });
      });
      return chain.then(function () {
        contactsCache = null;
        say('contact-status', tf('{0} contact(s) imported, {1} already there.', imported, list.length - imported), true);
        return loadContacts();
      });
    }, function () { say('contact-status', t('Nothing in that file looked like a contact.'), false); });
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
  // ---- @mentions in the text -------------------------------------------------
  // An @ followed by letters, at the caret, asks the address book as the To
  // field does; the person chosen is written as @Name where the @ was and
  // added to To if not there. Outlook's shape, in the plain text editor.
  var mentionComplete = function () {
    var input = el('compose-text');
    var list = document.createElement('ul'); list.className = 'complete'; list.hidden = true; list.setAttribute('role', 'listbox');
    list.setAttribute('style', 'top:auto;bottom:100%');
    input.parentNode.appendChild(list);
    var items = []; var marked = -1; var timer = null;
    var close = function () { list.hidden = true; list.textContent = ''; items = []; marked = -1; };
    var mention = function () {
      var caret = typeof input.selectionStart === 'number' ? input.selectionStart : input.value.length;
      var head = input.value.slice(0, caret);
      var m = head.match(/(^|\s)@([^\s@]{1,40})$/);
      return m ? { start: caret - m[2].length - 1, q: m[2], caret: caret } : null;
    };
    var take = function (c) {
      var at = mention();
      if (!at) { close(); return; }
      var name = c.name || c.address.split('@')[0];
      input.value = input.value.slice(0, at.start) + '@' + name + ' ' + input.value.slice(at.caret);
      var to = el('compose-to').value;
      if (to.toLowerCase().indexOf(c.address.toLowerCase()) < 0) {
        el('compose-to').value = (to.trim() ? to.replace(/,?\s*$/, ', ') : '') + (c.name ? c.name + ' <' + c.address + '>' : c.address) + ', ';
      }
      close();
      input.focus();
    };
    var mark = function (n) { marked = n; for (var i = 0; i < list.children.length; i++) { list.children[i].classList.toggle('on', i === marked); } };
    var show = function (found) {
      close();
      if (!found.length) { return; }
      items = found;
      found.forEach(function (c) {
        var li = document.createElement('li'); li.setAttribute('role', 'option');
        var nm = document.createElement('span'); nm.className = 'nm'; nm.textContent = '@' + (c.name || c.address);
        var ad = document.createElement('span'); ad.className = 'ad'; ad.textContent = c.name ? c.address : '';
        li.appendChild(nm); li.appendChild(ad);
        li.addEventListener('mousedown', function (e) { e.preventDefault(); take(c); });
        list.appendChild(li);
      });
      list.hidden = false; mark(0);
    };
    input.addEventListener('input', function () {
      var at = mention();
      if (timer) { clearTimeout(timer); }
      if (!at) { close(); return; }
      timer = setTimeout(function () {
        call('GET', '/api/v1/me/contacts?limit=8&q=' + encodeURIComponent(at.q)).then(function (result) {
          var now = mention();
          if (result.status !== 200 || !result.data || !now || now.q !== at.q) { return; }
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
  mentionComplete();
  // A message that mentions the reader by name, or by the mailbox's name,
  // in what the listing shows of it, carries an @ mark in the list.
  var readerNames = function () {
    var names = [];
    var first = el('name-first') ? String(el('name-first').value || '').trim() : '';
    if (first) { names.push(first); }
    if (me && me.indexOf('@') > 0) { names.push(me.split('@')[0]); }
    return names;
  };
  var mentionsReader = function (m) {
    var text = (String(m.subject || '') + ' ' + String(m.snippet || '')).toLowerCase();
    return readerNames().some(function (n) { return n && text.indexOf('@' + n.toLowerCase()) >= 0; });
  };

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
          closeCompose();
        }
        toast(t('Sent.'));
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
    if (el('toasts') && el('toasts').textContent.indexOf(t('Sending in {0} s').split('{0}')[0]) >= 0) { dismissToast(); }
  };
  el('compose-form').addEventListener('submit', function (event) {
    event.preventDefault();
    if (pendingSend) { return; }
    say('compose-status', '', true);
    var body = composeBody();
    // The message promises an attachment and carries none, not even as a
    // link: one question. Cancel leaves the message here, as written.
    if (mentionsAttachment(body.text) && !anythingAttached() && !window.confirm(t('Your message mentions an attachment, but nothing is attached. Send it anyway?'))) { return; }
    readFiles().then(function (files) {
      files = carried.concat(files);
      if (files.length) { body.attachments = files; }
      appendLinks(body);
      var queued = { body: body, key: composeKey, draft: draftId };
      var delay = undoSeconds();
      if (!delay) { return submitMessage(queued); }
      var left = delay;
      el('undo-text').textContent = tf('Sending in {0} s', left);
      el('compose-send').disabled = true;
      pendingSend = queued;
      pendingSend.mode = composeMode; pendingSend.id = composeId;
      hideCompose();
      var countdown = toast(tf('Sending in {0} s', left), function () { el('undo-send').click(); }, true);
      pendingSend.ticker = setInterval(function () { left--; var text = tf('Sending in {0} s', Math.max(left, 0)); el('undo-text').textContent = text; if (countdown && countdown.firstChild) { countdown.firstChild.textContent = text; } }, 1000);
      pendingSend.timer = setTimeout(function () { var q = pendingSend; cancelUndo(); if (q) { submitMessage(q); } }, delay * 1000);
      return null;
    }, function (why) { say('compose-status', why, false); return null; });
  });
  el('undo-send').addEventListener('click', function () { var q = pendingSend; cancelUndo(); dismissToast(); if (q) { openCompose(q.mode || 'new', q.id || 0); } say('compose-status', t('Not sent. The message is still here.'), true); });
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
  // The draft, saved: what the form holds and the files with it, over the
  // draft it came from. Resolves to the draft's id, or 0 when it could not
  // be saved, having said why.
  var saveDraft = function () {
    var body = composeBody();
    if (draftId) { body.replace_id = draftId; }
    return readFiles().then(function (files) {
      files = carried.concat(files);
      if (files.length) { body.attachments = files; }
      appendLinks(body);
      return call('POST', '/api/v1/me/drafts', body);
    }, function (why) { say('compose-status', why, false); return null; }).then(function (result) {
      if (!result) { return 0; }
      if (result.status === 201 && result.data) { draftId = result.data.id; composeKey = 'draft:' + draftId; say('compose-status', t('Draft saved.'), true); loadFolders(); return draftId; }
      say('compose-status', describe(result, t('Could not save the draft')), false);
      return 0;
    });
  };
  el('compose-save').addEventListener('click', function () { saveDraft(); });
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
  // ---- Search history: the last ten searches, kept in this browser --------
  // Offered under the box while it is focused and empty; an entry runs that
  // search again, Clear the list empties it. Nothing of it reaches the
  // server, and it goes with the account at sign-out.
  var SEARCHES = 'hmPortalSearches';
  var searchesRead = function () {
    try {
      var list = JSON.parse(localStorage.getItem(SEARCHES) || '[]');
      return Array.isArray(list) ? list.filter(function (s) { return typeof s === 'string' && s; }).slice(0, 10) : [];
    } catch (e) { return []; }
  };
  var searchesWrite = function (list) {
    try { if (list.length) { localStorage.setItem(SEARCHES, JSON.stringify(list)); } else { localStorage.removeItem(SEARCHES); } } catch (e) { /* a browser that keeps nothing */ }
  };
  var rememberSearch = function (text) {
    var list = searchesRead().filter(function (s) { return s !== text; });
    list.unshift(text);
    searchesWrite(list.slice(0, 10));
  };
  var hideSearchHistory = function () { el('search-history').hidden = true; };
  var historyButtons = function () { return el('search-history-list').children; };
  // The arrow keys through one of the two lists under the box - the history
  // or the suggestions - and Escape back to the box.
  var listKeys = function (buttons, hide) {
    return function (e) {
      var items = buttons(); var at = -1;
      for (var i = 0; i < items.length; i++) { if (items[i] === e.target) { at = i; } }
      if (e.key === 'ArrowDown') { e.preventDefault(); if (at + 1 < items.length) { items[at + 1].focus(); } }
      else if (e.key === 'ArrowUp') { e.preventDefault(); if (at > 0) { items[at - 1].focus(); } else { el('mail-search').focus(); } }
      else if (e.key === 'Escape') { e.preventDefault(); hide(); el('mail-search').focus(); }
    };
  };
  var historyKeys = listKeys(historyButtons, hideSearchHistory);
  // Focus leaving the box or an entry closes the lists, unless it went to
  // the other of the two: a moment later, so a click on an entry lands first.
  var leaveHistory = function () {
    setTimeout(function () {
      var focus = document.activeElement;
      if (focus === el('mail-search') || within(focus, el('search-history')) || within(focus, el('search-suggest'))) { return; }
      hideSearchHistory();
      hideSuggestions();
    }, 150);
  };
  // ---- Search suggestions: a contact's name completing to from:<address> --
  // The token being typed - the text after the last space, with the from:
  // or to: it may already carry - is asked of the address book the way the
  // To field asks it, and the contacts it matches are offered under the box
  // in place of the history. One taken stands in the box as from:<address>
  // (to: when the token began with to:) in place of the name, beside
  // whatever else was typed, and the search runs. An operator's value
  // (is:unread, subject:x) and a quoted phrase are not names, and one
  // letter asks nothing.
  var suggestTimer = null;
  var suggestToken = function () {
    var v = el('mail-search').value;
    var cut = Math.max(v.lastIndexOf(' '), v.lastIndexOf('\t')) + 1;
    var tail = v.slice(cut);
    var m = /^(from|to):(.*)$/i.exec(tail);
    return { head: v.slice(0, cut), key: m ? m[1].toLowerCase() : 'from', q: m ? m[2] : tail };
  };
  var suggestButtons = function () { return el('search-suggest-list').children; };
  var hideSuggestions = function () {
    el('search-suggest').hidden = true;
    if (suggestTimer) { clearTimeout(suggestTimer); suggestTimer = null; }
  };
  var suggestKeys = listKeys(suggestButtons, hideSuggestions);
  var showSuggestions = function (found, token) {
    var list = el('search-suggest-list');
    clear(list);
    if (!found.length) { hideSuggestions(); return; }
    el('search-suggest-head').textContent = token.key === 'to' ? t('Search by recipient') : t('Search by sender');
    found.forEach(function (c) {
      var b = node('button', undefined, 'q'); b.type = 'button';
      b.appendChild(icon('person', true));
      b.appendChild(node('span', c.name || c.address, 'nm'));
      b.appendChild(node('span', token.key + ':' + c.address, 'ad'));
      b.addEventListener('mousedown', function (e) { e.preventDefault(); });
      b.addEventListener('click', function () {
        var text = token.head + token.key + ':' + c.address;
        el('mail-search').value = text;
        hideSuggestions();
        runSearch(text);
      });
      b.addEventListener('keydown', suggestKeys);
      b.addEventListener('blur', leaveHistory);
      list.appendChild(b);
    });
    el('search-suggest').hidden = false;
  };
  var suggestSearch = function () {
    var token = suggestToken();
    if (suggestTimer) { clearTimeout(suggestTimer); suggestTimer = null; }
    if (token.q.length < 2 || /[:"]/.test(token.q)) { hideSuggestions(); return; }
    suggestTimer = setTimeout(function () {
      suggestTimer = null;
      call('GET', '/api/v1/me/contacts?limit=8&q=' + encodeURIComponent(token.q)).then(function (result) {
        // The answer to what is still being typed, and no other.
        var now = suggestToken();
        if (result.status !== 200 || !result.data || now.q !== token.q || now.key !== token.key) { return; }
        showSuggestions(result.data.contacts || [], token);
      });
    }, 120);
  };
  var showSearchHistory = function () {
    var list = el('search-history-list');
    clear(list);
    var items = searchesRead();
    if (!items.length || el('mail-search').value) { hideSearchHistory(); return; }
    items.forEach(function (q) {
      var b = node('button', undefined, 'q'); b.type = 'button';
      b.appendChild(icon('clock', true));
      b.appendChild(node('span', q));
      b.addEventListener('mousedown', function (e) { e.preventDefault(); });
      b.addEventListener('click', function () { el('mail-search').value = q; hideSearchHistory(); runSearch(q); });
      b.addEventListener('keydown', historyKeys);
      b.addEventListener('blur', leaveHistory);
      list.appendChild(b);
    });
    el('search-history').hidden = false;
  };
  // The one way a search is run, from the box, its options panel or the
  // history: an empty search is the folder itself, and a real one is kept.
  var runSearch = function (text) {
    text = String(text || '').trim();
    state.before = 0;
    lastListing = null;
    if (!text) { go('/f/' + (state.folderId || inboxId)); return; }
    rememberSearch(text);
    if (el('mail-search-everywhere').checked) { go('/search?q=' + encodeURIComponent(text)); return; }
    go('/f/' + (state.folderId || inboxId) + '?q=' + encodeURIComponent(text));
  };
  el('mail-search').addEventListener('focus', showSearchHistory);
  el('mail-search').addEventListener('blur', leaveHistory);
  el('mail-search').addEventListener('input', function () {
    if (el('mail-search').value) { hideSearchHistory(); suggestSearch(); } else { hideSuggestions(); showSearchHistory(); }
  });
  el('mail-search').addEventListener('keydown', function (e) {
    if (e.key === 'Escape') { hideSearchHistory(); hideSuggestions(); return; }
    if (e.key === 'ArrowDown') {
      var items = !el('search-history').hidden ? historyButtons() : !el('search-suggest').hidden ? suggestButtons() : [];
      if (items.length) { e.preventDefault(); items[0].focus(); }
    }
  });
  el('search-history-clear').addEventListener('mousedown', function (e) { e.preventDefault(); });
  el('search-history-clear').addEventListener('click', function () { searchesWrite([]); hideSearchHistory(); el('mail-search').focus(); });
  el('mail-search-form').addEventListener('submit', function (event) {
    event.preventDefault();
    hideSearchHistory();
    hideSuggestions();
    runSearch(el('mail-search').value);
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
  // ---- The formatted signature ---------------------------------------------
  // Beside the plain signature, one with layout: bold, italic, underline, a
  // link, an image from a file kept as a data URI (the cleaner keeps those and
  // nothing remote). Saved as signature.html, which the server has stored all
  // along; put on formatted messages, the plain one on plain messages.
  (function () {
    var editor = el('signature-html');
    var tools = el('signature-tools').children;
    for (var i = 0; i < tools.length; i++) {
      (function (b) {
        if (!b.getAttribute('data-cmd')) { return; }
        b.addEventListener('mousedown', function (event) { event.preventDefault(); });
        b.addEventListener('click', function () {
          var cmd = b.getAttribute('data-cmd');
          var arg = null;
          if (cmd === 'createLink') { arg = window.prompt ? window.prompt(t('Link address')) : ''; if (!arg) { return; } }
          try { document.execCommand(cmd, false, arg); } catch (e) { /* a browser without the command keeps the text */ }
        });
      })(tools[i]);
    }
    el('signature-image').addEventListener('change', function () {
      var file = (el('signature-image').files || [])[0];
      if (!file) { return; }
      if (file.size > 200 * 1024 || !/^image\/(png|jpeg|gif|webp)$/i.test(file.type || '')) { say('settings-status', t('An image for the signature: PNG, JPEG, GIF or WebP, 200 KB at most.'), false); el('signature-image').value = ''; return; }
      var reader = new FileReader();
      reader.onload = function () {
        var img = document.createElement('img');
        img.setAttribute('src', String(reader.result));
        img.setAttribute('alt', '');
        editor.appendChild(img);
        el('signature-image').value = '';
      };
      reader.readAsDataURL(file);
    });
  })();
  el('settings-form').addEventListener('submit', function (event) {
    event.preventDefault();
    var body = {
      name: { first: el('name-first').value, last: el('name-last').value },
      signature: { enabled: el('signature-enabled').checked, text: el('signature-text').value, html: cleanHtml(el('signature-html')) }
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
    if (r.field === 'thread') {
      var ids = Array.isArray(r.text) ? r.text : [r.text];
      return 'anyof (' + ids.map(function (id) { return 'header :contains ["references", "in-reply-to"] ' + sieveString(id); }).join(', ') + ')';
    }
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
    if (r.field === 'thread') { return { when: tf('A reply in the conversation "{0}"', r.subject || ''), then: 'move to ' + r.folder }; }
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
  // ---- Rows coloured by a rule ----------------------------------------------
  // Outlook's conditional formatting: a field, a text and a colour, kept with
  // the account's preferences, edited on the Filters page beside the rules.
  // The first rule that matches paints the row's edge; the labels keep their
  // own colours, this is a rule on the row.
  var rowColours = function () {
    try {
      var list = JSON.parse(pref('row_colours') || '[]');
      return Array.isArray(list) ? list.filter(function (r) { return r && r.text && /^#[0-9a-f]{6}$/i.test(r.colour || ''); }) : [];
    } catch (why) { return []; }
  };
  var colourFor = function (m) {
    var rules = rowColours();
    if (!rules.length || !m) { return ''; }
    var from = String(m.from || '').toLowerCase();
    var to = (String(m.to || '') + ' ' + String(m.cc || '')).toLowerCase();
    var subject = String(m.subject || '').toLowerCase();
    for (var i = 0; i < rules.length; i++) {
      var needle = String(rules[i].text).toLowerCase();
      var field = rules[i].field;
      var hay = field === 'from' ? from : field === 'to' ? to : field === 'subject' ? subject : from + ' ' + to + ' ' + subject;
      if (hay.indexOf(needle) >= 0) { return rules[i].colour; }
    }
    return '';
  };
  var paintRow = function (row, colour) {
    // On the leading edge: --lead is 1, or -1 when the page is written right to left.
    if (colour) { row.setAttribute('style', 'box-shadow:inset calc(4px * var(--lead, 1)) 0 0 ' + colour); }
  };
  var writeRowColours = function (list) {
    return savePrefs({ row_colours: JSON.stringify(list.slice(0, 20)) }).then(function (ok) { if (ok) { renderColourRules(); lastListing = null; } return ok; });
  };
  var renderColourRules = function () {
    var box = el('colour-rows');
    clear(box);
    rowColours().forEach(function (r, i) {
      var line = node('div', undefined, 'row');
      var swatch = node('span', undefined, 'swatch'); swatch.setAttribute('style', 'display:inline-block;width:14px;height:14px;border-radius:3px;background:' + r.colour);
      line.appendChild(swatch);
      var fieldWord = r.field === 'from' ? t('From') : r.field === 'to' ? t('To or Cc') : r.field === 'subject' ? t('Subject') : t('From, To or Subject');
      line.appendChild(node('span', fieldWord + ' ' + t('contains') + ' ' + r.text));
      var remove = button(t('Remove'), 'btn ghost sm');
      remove.addEventListener('click', function () { writeRowColours(rowColours().filter(function (x, j) { return j !== i; })); });
      line.appendChild(remove);
      box.appendChild(line);
    });
  };
  el('colour-form').addEventListener('submit', function (event) {
    event.preventDefault();
    var text = el('colour-text').value.trim();
    var colour = String(el('colour-value').value || '').toLowerCase();
    if (!text || !/^#[0-9a-f]{6}$/.test(colour)) { return; }
    writeRowColours(rowColours().concat([{ field: el('colour-field').value, text: text, colour: colour }])).then(function (ok) { if (ok) { el('colour-text').value = ''; } });
  });
  var saveRules = function (rules) {
    var script = scriptOf(rules);
    var before = el('filter-script').value;
    return call('PUT', '/api/v1/me/filters', { script: script }).then(function (result) {
      if (result.status === 200) {
        el('filter-script').value = script; renderRules(script); say('rule-status', t('Saved.'), true);
        if (before !== script) {
          toast(t('Rules saved.'), function () {
            call('PUT', '/api/v1/me/filters', { script: before }).then(function (back) {
              if (back.status === 200) { el('filter-script').value = before; renderRules(before); say('rule-status', t('Saved.'), true); }
              else { say('rule-status', describe(back, t('Could not save the rules')), false); }
            });
          });
        }
        return true;
      }
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
    if (on) {
      var text = box.value;
      if (signature.enabled && signature.html && signature.text) {
        var plainBlock = '\n\n-- \n' + signature.text;
        var at = text.lastIndexOf(plainBlock);
        if (at >= 0 && text.slice(at + plainBlock.length).trim() === '') { text = text.slice(0, at); }
      }
      textToEditor(text);
      if (signature.enabled && signature.html && editor.innerHTML.indexOf('data-sig="1"') < 0) {
        var sig = document.createElement('div'); sig.setAttribute('data-sig', '1'); sig.innerHTML = '<br>-- <br>' + signature.html;
        editor.appendChild(sig);
      }
      editor.hidden = false; box.hidden = true; el('rich-tools').hidden = false; el('rich-toggle').textContent = 'Plain text';
    }
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
  var snoozeHours = function () { var n = Number(pref('snooze_hours')); return n > 0 && n <= 72 ? n : 3; };
  var snoozeHour = function () { var n = Number(pref('snooze_hour')); return n >= 0 && n <= 23 ? n : 9; };
  var renderSnoozePresets = function () {
    el('snooze-3h').textContent = tf('in {0} hours', snoozeHours());
    el('snooze-tomorrow').textContent = tf('tomorrow at {0}', snoozeHour());
    el('snooze-week').textContent = tf('next week at {0}', snoozeHour());
  };
  el('snooze-3h').addEventListener('click', function () { snoozeUntil(stampOf(new Date(Date.now() + snoozeHours() * 3600 * 1000))); });
  el('snooze-tomorrow').addEventListener('click', function () { var d = new Date(); d.setDate(d.getDate() + 1); d.setHours(snoozeHour(), 0, 0, 0); snoozeUntil(stampOf(d)); });
  el('snooze-week').addEventListener('click', function () { var d = new Date(); d.setDate(d.getDate() + 7); d.setHours(snoozeHour(), 0, 0, 0); snoozeUntil(stampOf(d)); });
  el('snooze-go').addEventListener('click', function () { var at = fromPicker(el('snooze-at').value); if (at.length === 16) { snoozeUntil(at); } else { say('mail-status', t('Choose when.'), false); } });
  // ---- Clean up conversation, as Outlook has it -----------------------------
  // The messages of a conversation whose whole text a later message of it
  // quotes are redundant: each is read, oldest first, and one whose every
  // line of three characters or more is found in a later one - the quote
  // markers and the spacing taken out of both, so a quote re-wrapped or
  // nested still counts - goes to Trash with the others, with Undo. The
  // newest message stays, as does one unread, starred or carrying an
  // attachment, since a reader may not have seen it and a quote carries no
  // file. Fifty messages at most are read for it. From a conversation row's
  // menu, or the open message's, whose conversation is the listing's.
  var cleanLines = function (text) {
    return String(text || '').split(/\r?\n/).map(function (line) { return line.replace(/^[\s>]+/, '').replace(/\s+/g, ' ').trim(); }).filter(function (line) { return line.length >= 3; });
  };
  var quotedIn = function (earlier, later) {
    var lines = cleanLines(earlier);
    if (!lines.length) { return false; }
    var haystack = cleanLines(later).join('\n');
    return lines.every(function (line) { return haystack.indexOf(line) >= 0; });
  };
  var hasAttachments = function (m) { return !!(m.has_attachments || (m.attachments && m.attachments.length)); };
  var cleanUpConversation = function (members) {
    var list = (members || []).filter(function (m) { return m && m.id; }).slice(0, 50);
    var nothing = function () { toast(t('Nothing to clean up: every message says something the later ones do not.')); };
    if (list.length < 2) { nothing(); return; }
    list.sort(function (a, b) { var da = dateOf(a), db = dateOf(b); return (da ? da.getTime() : 0) - (db ? db.getTime() : 0); });
    Promise.all(list.map(function (m) {
      return call('GET', '/api/v1/me/messages/' + m.id).then(function (result) { return result.status === 200 && result.data ? textOf(result.data) : null; });
    })).then(function (texts) {
      var redundant = [];
      list.forEach(function (m, i) {
        if (i === list.length - 1 || texts[i] === null || !m.flags || !m.flags.seen || m.flags.flagged || hasAttachments(m)) { return; }
        for (var j = i + 1; j < list.length; j++) {
          if (texts[j] !== null && quotedIn(texts[i], texts[j])) { redundant.push({ id: m.id, folderId: m.folder_id || state.folderId }); break; }
        }
      });
      if (!redundant.length) { nothing(); return; }
      var ids = redundant.map(function (e) { return e.id; });
      fileMany(redundant, 'delete').then(function (ok) {
        if (!ok) { return; }
        if (current && ids.indexOf(current.id) >= 0) { closeMessage(true); }
        lastListing = null;
        loadMessages();
      });
    });
  };
  // The open message's conversation: the listing's messages with its key, and itself.
  var conversationOfCurrent = function () {
    if (!current) { return []; }
    var page = lastListing && lastListing.page;
    var key = threadKey(current);
    var members = page ? page.messages.filter(function (x) { return threadKey(x) === key; }) : [];
    if (!members.some(function (x) { return x.id === current.id; })) { members.push(current); }
    return members;
  };
  el('message-cleanup').addEventListener('click', function () { closeMenus(); cleanUpConversation(conversationOfCurrent()); });
  // ---- Nudges: a reply or a follow-up that seems owed, as Gmail suggests ----
  // Computed from what a row already carries. A message received three to
  // thirty days ago, not answered, not from the reader, not muted, that
  // asked a question - a question mark in its subject or its first line -
  // is nudged to be replied to. A message in the Sent folder that old is
  // nudged to be followed up when nothing answered it, which the listing
  // cannot say: the page asks it of the server once, in_reply_to: for each
  // such message on the page joined by OR, and keeps the answer - a reply
  // once seen stays seen, the rest are asked about again after a while. A
  // preference turns nudges off. Nothing is nudged in a search.
  var NUDGE_DAYS_MIN = 3;
  var NUDGE_DAYS_MAX = 30;
  var nudgesOn = function () { return pref('nudges') !== '0'; };
  var daysOld = function (m) { var d = dateOf(m); return d ? Math.floor((Date.now() - d.getTime()) / 86400000) : -1; };
  var oldEnough = function (m) { var days = daysOld(m); return days >= NUDGE_DAYS_MIN && days <= NUDGE_DAYS_MAX; };
  var fromMe = function (m) { return !!me && addressOf(m.from || '').toLowerCase() === me; };
  var asksAQuestion = function (m) { return /\?/.test(m.subject || '') || /\?/.test(m.snippet || ''); };
  var messageIdOf = function (s) { return String(s || '').replace(/[<>]/g, '').trim().toLowerCase(); };
  var replied = {};
  var askedReplies = {};
  var nudgeFor = function (m, folderId) {
    if (!nudgesOn() || state.query || state.everywhere || !m.flags || m.flags.draft || !oldEnough(m)) { return ''; }
    if (folderIs(folderId, 'Sent')) {
      var id = messageIdOf(m.message_id);
      return id && !replied[id] ? tf('Sent {0} days ago. Follow up?', daysOld(m)) : '';
    }
    if (folderIs(folderId, 'Drafts') || folderIs(folderId, 'Junk') || folderIs(folderId, 'Trash')) { return ''; }
    if (m.flags.answered || fromMe(m) || isMuted(m) || !asksAQuestion(m)) { return ''; }
    return tf('Received {0} days ago. Reply?', daysOld(m));
  };
  var askReplies = function (page) {
    if (!nudgesOn() || state.query || state.everywhere || !folderIs(state.folderId, 'Sent')) { return; }
    var wanted = [];
    page.messages.forEach(function (m) {
      var id = messageIdOf(m.message_id);
      if (id && !replied[id] && oldEnough(m) && wanted.length < 16 && !(askedReplies[id] && Date.now() - askedReplies[id] < 5 * 60 * 1000)) { wanted.push(m.message_id); askedReplies[id] = Date.now(); }
    });
    if (!wanted.length) { return; }
    call('GET', '/api/v1/me/search?q=' + encodeURIComponent(wanted.map(function (id) { return 'in_reply_to:' + id; }).join(' OR ')) + '&limit=200').then(function (result) {
      if (result.status !== 200 || !result.data) { return; }
      var learned = false;
      (result.data.messages || []).forEach(function (r) {
        String((r.in_reply_to || '') + ' ' + (r.references || '')).split(/\s+/).forEach(function (ref) { var id = messageIdOf(ref); if (id && !replied[id]) { replied[id] = true; learned = true; } });
      });
      // The rows nudged a moment ago that turn out to have been answered lose the nudge, in place.
      if (!learned) { return; }
      listRows.forEach(function (r) { if (r.nudge && !nudgeFor(r.m, r.folderId)) { r.nudge.parentNode.removeChild(r.nudge); r.nudge = null; } });
    });
  };
  // ---- Follow-up flags: a flag with a date, as Outlook has them -------------
  // A follow-up is the star, the $FollowUp keyword and a $Due-YYYY-MM-DD
  // keyword beside it, all on the message: it travels with the message
  // wherever it is filed and whatever client moves it, a mail program that
  // does not know the two keywords shows the star and leaves them be, and
  // label:$FollowUp finds them. The Starred view lists the dated ones
  // first, the soonest at the top; a row says when one is due; and what is
  // due today or before is announced once a day.
  var FOLLOWUP = '$FollowUp';
  var DUE = '$Due-';
  var isFollowUpKeyword = function (k) { return String(k).toLowerCase() === FOLLOWUP.toLowerCase(); };
  var isDueKeyword = function (k) { return String(k).slice(0, DUE.length).toLowerCase() === DUE.toLowerCase() && /^\d{4}-\d{2}-\d{2}$/.test(String(k).slice(DUE.length)); };
  var keywordsOf = function (m) { return (m && m.flags && m.flags.keywords) || []; };
  var hasFollowUp = function (m) { return keywordsOf(m).some(isFollowUpKeyword); };
  var dueOf = function (m) { var found = keywordsOf(m).filter(isDueKeyword); return found.length ? found[0].slice(DUE.length) : ''; };
  var dueDayOf = function (d) { return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate()); };
  var dueToday = function () { return dueDayOf(new Date()); };
  var dueText = function (day) {
    var d = new Date(day + 'T00:00:00');
    if (isNaN(d.getTime())) { return day; }
    try { return d.toLocaleDateString(languageActive, d.getFullYear() === new Date().getFullYear() ? { day: 'numeric', month: 'short' } : { year: 'numeric', month: 'short', day: 'numeric' }); } catch (e) { return day; }
  };
  // The badge a row carries: overdue, due today, or the day.
  var dueMark = function (m) {
    var day = dueOf(m);
    if (!day) { return null; }
    var now = dueToday();
    var s = node('span', day < now ? t('Overdue') : day === now ? t('Due today') : tf('Due {0}', dueText(day)), 'due' + (day < now ? ' late' : day === now ? ' now' : ''));
    s.setAttribute('title', day);
    return s;
  };
  var soonestDue = function (messages) {
    var best = null;
    messages.forEach(function (m) { var d = dueOf(m); if (d && (!best || d < dueOf(best))) { best = m; } });
    return best;
  };
  var byDue = function (messages) {
    var dated = messages.filter(function (m) { return !!dueOf(m); });
    dated.sort(function (a, b) { var x = dueOf(a), y = dueOf(b); return x < y ? -1 : x > y ? 1 : 0; });
    return dated.concat(messages.filter(function (m) { return !dueOf(m); }));
  };
  // The star, the keyword and the day in one change; a day already there
  // is taken off first. An empty day clears the flag, star and all.
  var setFollowUp = function (m, day) {
    var stale = keywordsOf(m).filter(isDueKeyword);
    var body = day ? { flagged: true, keywords_add: [FOLLOWUP, DUE + day], keywords_remove: stale } : { flagged: false, keywords_remove: stale.concat([FOLLOWUP]) };
    return call('PUT', '/api/v1/me/messages/' + m.id + '/flags', body).then(function (result) {
      if (result.status !== 200 || !result.data || !result.data.flags) { say('mail-status', describe(result, t('Could not change the flags')), false); return false; }
      m.flags = result.data.flags;
      if (current && current.id === m.id) { current.flags = result.data.flags; renderActions(); }
      toast(day ? tf('Follow up by {0}.', dueText(day)) : t('Follow-up flag cleared.'));
      lastListing = null;
      loadFolders();
      if (!el('mail-section').hidden) { reloadKeepingPlace(); }
      return true;
    });
  };
  var followUpIn = function (days) { var d = new Date(); d.setDate(d.getDate() + days); return dueDayOf(d); };
  el('message-followup').addEventListener('click', function () { var menu = el('followup-menu'); menu.hidden = !menu.hidden; el('snooze-menu').hidden = true; });
  el('followup-today').addEventListener('click', function () { if (current) { setFollowUp(current, followUpIn(0)); } });
  el('followup-tomorrow').addEventListener('click', function () { if (current) { setFollowUp(current, followUpIn(1)); } });
  el('followup-week').addEventListener('click', function () { if (current) { setFollowUp(current, followUpIn(7)); } });
  el('followup-go').addEventListener('click', function () {
    var day = String(el('followup-at').value || '').slice(0, 10);
    if (current && /^\d{4}-\d{2}-\d{2}$/.test(day)) { setFollowUp(current, day); } else { say('mail-status', t('Choose when.'), false); }
  });
  el('followup-clear').addEventListener('click', function () { if (current) { setFollowUp(current, ''); } });
  // What is due today or before, asked of the server once a day - at
  // sign-in, and again when the probe sees the day turn - and announced
  // once per message: a toast that opens it, or the Starred view when there
  // are several, and a browser notification where those are on. Which were
  // announced is kept in this browser for the day, and forgotten at
  // sign-out.
  var DUE_SHOWN = 'hmPortalDueShown';
  var followUps = { day: '' };
  var dueShownRead = function () {
    try { var v = JSON.parse(localStorage.getItem(DUE_SHOWN) || 'null'); return v && v.day === dueToday() && Array.isArray(v.ids) ? v.ids : []; } catch (e) { return []; }
  };
  var dueShownWrite = function (ids) { try { localStorage.setItem(DUE_SHOWN, JSON.stringify({ day: dueToday(), ids: ids })); } catch (e) { /* a browser that keeps nothing */ } };
  var dueShownForget = function () { try { localStorage.removeItem(DUE_SHOWN); } catch (e) { /* nothing to forget */ } };
  var followUpsCheck = function () {
    followUps.day = dueToday();
    return call('GET', '/api/v1/me/search?q=' + encodeURIComponent('label:' + FOLLOWUP) + '&limit=200').then(function (result) {
      if (result.status !== 200 || !result.data || el('account').hidden) { return; }
      var shown = dueShownRead();
      var due = (result.data.messages || []).filter(function (m) { var d = dueOf(m); return d && d <= dueToday() && shown.indexOf(m.id) < 0; });
      if (!due.length) { return; }
      dueShownWrite(shown.concat(due.map(function (m) { return m.id; })));
      var text = due.length === 1 ? tf('Due for follow-up: {0}', due[0].subject || t('(no subject)')) : tf('{0} messages are due for follow-up', due.length);
      if (due.length === 1) { toast(text, function () { go('/m/' + due[0].id); }, true, t('Open')); }
      else { toast(text, function () { go('/starred'); }, true, t('Show them')); }
      if (pref('notify') === '1' && 'Notification' in window && Notification.permission === 'granted') {
        try { new Notification(text, { tag: 'hm-followup' }); } catch (e) { /* a browser without notifications is still a browser */ }
      }
    });
  };
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
  el('bulk-archive').addEventListener('click', function () { fileSelected('archive'); });
  el('bulk-junk').addEventListener('click', function () { fileSelected(junkTargetFor(state.folderId)); });
  el('bulk-delete').addEventListener('click', function () { fileSelected('delete'); });
  el('bulk-move-go').addEventListener('click', function () {
    var target = Number(el('bulk-move').value);
    if (!target) { return; }
    eachSelected(function (id) { return call('POST', '/api/v1/me/messages/' + id + '/move', { folder_id: target }); });
  });
  el('bulk-clear').addEventListener('click', function () { clearSelection(); listRows.forEach(function (r) { r.box.checked = false; }); });
  // One call per message, in order, then one reload.
  var eachSelected = function (act) {
    return selectedEntries().then(function (entries) {
      var chain = Promise.resolve();
      entries.forEach(function (e) { chain = chain.then(function () { return act(e.id); }); });
      return chain.then(function () { clearSelection(); lastListing = null; loadFolders(); return loadMessages(); });
    }, function (r) { say('mail-status', describe(r, t('Could not read the folder')), false); });
  };
  document.addEventListener('keydown', function (event) {
    var tag = (event.target && event.target.tagName) || '';
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || event.ctrlKey || event.metaKey || event.altKey) { return; }
    if (el('account').hidden || el('mail-section').hidden || el('message-list').hidden) { return; }
    if (current && !el('message-view').hidden && (event.key === 'e' || event.key === '!' || event.key === '#' || event.key === 'm')) { return; }
    if (event.key === 'ArrowDown' || event.key === 'j') { setCursor(cursor + 1); event.preventDefault(); }
    else if (event.key === 'ArrowUp' || event.key === 'k') { setCursor(cursor - 1); event.preventDefault(); }
    else if (event.key === 'Enter' && cursor >= 0) { var hit = listRows[cursor]; if (hit.toggle) { hit.toggle(); } else { go('/m/' + hit.id); } event.preventDefault(); }
    else if (event.key === 'e' && cursor >= 0) { fileRow(listRows[cursor], 'archive'); event.preventDefault(); }
    else if (event.key === '!' && cursor >= 0) { fileRow(listRows[cursor], junkTargetFor(state.folderId)); event.preventDefault(); }
    else if (event.key === '#' && cursor >= 0) { fileRow(listRows[cursor], 'delete'); event.preventDefault(); }
    else if (event.key === 'm' && cursor >= 0) { muteThread(listRows[cursor], !isMuted(listRows[cursor].m)); event.preventDefault(); }
    else if (event.key === 'x' && cursor >= 0) { var r = listRows[cursor]; r.box.checked = !r.box.checked; (r.ids || [r.id]).forEach(function (id) { selected[id] = r.box.checked; }); anchorId = r.id; renderBulk(); event.preventDefault(); }
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
  var basePrefs = { theme: 'system', density: 'comfortable', undo_seconds: '5', notify: '0', notify_folders: '', view: 'threads', pane: 'right', inbox: 'tabs', tabs_by_sender: '', nudges: '1', timezone: '', swipe_right: 'archive', swipe_left: 'delete', snooze_hours: '3', snooze_hour: '9' };
  var renderListTools = function () {
    el('view-threads').textContent = pref('view') === 'threads' ? t('Show messages one by one') : t('Show conversations');
    var emptyable = !state.everywhere && (folderIs(state.folderId, 'Junk') || folderIs(state.folderId, 'Trash'));
    el('folder-empty').hidden = !emptyable;
    el('folder-empty').textContent = t('Empty this folder');
    ['right', 'bottom', 'off'].forEach(function (mode) { el('pane-' + mode).classList.toggle('on', paneMode() === mode); });
    el('mail-search-clear').hidden = !state.query;
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
    updateBadge(total);
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
    el('pref-pane').value = paneMode();
    applyPane();
    el('pref-undo').value = String(undoSeconds());
    if (!el('pref-undo').value) { el('pref-undo').value = '5'; }
    el('pref-notify').checked = pref('notify') === '1';
    el('pref-nudges').checked = nudgesOn();
    el('pref-inbox').value = inboxMode();
    el('pref-language').value = knownLanguage(pref('language') || '') || languageActive;
    fillZones();
    el('pref-timezone').value = pref('timezone') || '';
    el('pref-swipe-right').value = pref('swipe_right');
    el('pref-swipe-left').value = pref('swipe_left');
    el('pref-snooze-hours').value = String(snoozeHours());
    el('pref-snooze-hour').value = String(snoozeHour());
    renderSnoozePresets();
    renderNotifyFolders();
    renderTemplates();
    renderQuickSteps();
    renderSupport();
    refreshLabelColours();
    applyLanguagePref();
    updateTitle();
  };
  // ---- Quick steps: a button of the reader's own, as Outlook has them -------
  // Kept in the preferences as qs.<slug> -> {name, key, read, move, label,
  // forward}, so they follow the account between browsers. One runs on the
  // ticked messages, failing those on the open message, failing that on the
  // row under the cursor: mark as read, the label, the forward - the compose
  // window primed from the message, attachments and all, before anything
  // moves it - and then the move, whose toast has Undo as any filing's has.
  // A key from 1 to 9 runs it from the list.
  var quickSteps = function () {
    var list = [];
    Object.keys(prefs).forEach(function (k) {
      if (k.indexOf('qs.') !== 0) { return; }
      try {
        var v = JSON.parse(prefs[k]);
        if (v && v.name) {
          list.push({ key: k, name: String(v.name), shortcut: /^[1-9]$/.test(String(v.key || '')) ? String(v.key) : '', read: !!v.read, move: Number(v.move) || 0, label: String(v.label || ''), forward: String(v.forward || '') });
        }
      } catch (e) { /* a value that is not a quick step is left alone */ }
    });
    return list.sort(function (a, b) { return a.name.localeCompare(b.name); });
  };
  var quickStepByKey = function (digit) { return quickSteps().filter(function (s) { return s.shortcut === digit; })[0] || null; };
  var describeQuickStep = function (s) {
    var parts = [];
    if (s.read) { parts.push(t('Mark as read')); }
    if (s.label) { parts.push(t('Label') + ' ' + s.label); }
    if (s.forward) { parts.push(t('Forward to') + ' ' + s.forward); }
    if (s.move) { parts.push(t('Move to') + ' ' + folderName(s.move)); }
    return parts.join(', ');
  };
  var quickStepTargets = function () {
    if (selectedIds().length) { return selectedEntries(); }
    if (current && !el('message-view').hidden) { return Promise.resolve([{ id: current.id, folderId: current.folder_id }]); }
    var r = cursor >= 0 ? listRows[cursor] : null;
    if (r) { return Promise.resolve((r.ids || [r.id]).map(function (id) { return { id: id, folderId: r.folderId || state.folderId }; })); }
    return Promise.resolve([]);
  };
  // The compose window primed as Forward would prime it, addressed, and the
  // address bar told; the route then finds the form already holds it.
  var forwardTo = function (id, address) {
    composeKey = 'forward:' + id;
    return call('GET', '/api/v1/me/messages/' + id).then(function (result) {
      if (result.status !== 200 || !result.data) { say('mail-status', describe(result, t('Could not read the message being answered')), false); return null; }
      return prime('forward', result.data).then(function () {
        afterPrime('forward', result.data);
        el('compose-to').value = address;
        openCompose('forward', id);
        go('/compose?forward=' + id);
      });
    });
  };
  var runQuickStep = function (step) {
    quickStepTargets().then(function (entries) {
      if (!entries.length) { say('mail-status', t('Nothing to run it on.'), false); return; }
      var ids = entries.map(function (e) { return e.id; });
      var chain = Promise.resolve();
      if (step.read) { ids.forEach(function (id) { chain = chain.then(function () { return call('PUT', '/api/v1/me/messages/' + id + '/flags', { seen: true }); }); }); }
      if (step.label) { chain = chain.then(function () { return setKeywordOn(ids, step.label, true); }); }
      if (step.forward) { chain = chain.then(function () { return forwardTo(entries[0].id, step.forward); }); }
      chain.then(function () {
        if (step.move) {
          return fileMany(entries, step.move).then(function (ok) { if (ok && current && ids.indexOf(current.id) >= 0) { closeMessage(true); } });
        }
        toast(tf('Quick step {0} done.', step.name));
        return null;
      }).then(function () {
        clearSelection();
        lastListing = null;
        loadFolders();
        if (!el('mail-section').hidden) { reloadKeepingPlace(); }
      });
    }, function (r) { say('mail-status', describe(r, t('Could not read the folder')), false); });
  };
  var renderQuickSteps = function () {
    var list = quickSteps();
    var bar = el('quick-steps');
    clear(bar);
    list.forEach(function (s) {
      var b = node('button', s.name, 'textbtn'); b.type = 'button';
      b.setAttribute('title', describeQuickStep(s) + (s.shortcut ? ' (' + s.shortcut + ')' : ''));
      b.addEventListener('click', function () { runQuickStep(s); });
      bar.appendChild(b);
    });
    var rows = el('quickstep-rows');
    clear(rows);
    list.forEach(function (s) {
      var tr = document.createElement('tr');
      tr.appendChild(node('td', s.name));
      tr.appendChild(node('td', s.shortcut || t('None')));
      tr.appendChild(node('td', describeQuickStep(s)));
      var actions = document.createElement('td');
      var remove = button(t('Remove'));
      remove.addEventListener('click', function () { var change = {}; change[s.key] = null; savePrefs(change).then(function (ok) { if (ok) { say('quickstep-status', t('Removed.'), true); } }); });
      actions.appendChild(remove);
      tr.appendChild(actions);
      rows.appendChild(tr);
    });
    el('quickstep-empty').hidden = list.length > 0;
    el('quickstep-table').hidden = list.length === 0;
    // The folders a step may move to, the choice kept while they are redrawn.
    var move = el('quickstep-move');
    var chosen = move.value;
    var leave = node('option', t('Leave it where it is')); leave.value = '';
    fillFolderSelect(move, 0, leave);
    move.value = chosen || '';
  };
  el('quickstep-form').addEventListener('submit', function (event) {
    event.preventDefault();
    var name = el('quickstep-name').value.trim().slice(0, 40);
    var label = el('quickstep-label').value.trim();
    var forward = el('quickstep-forward').value.trim();
    var move = Number(el('quickstep-move').value) || 0;
    var read = el('quickstep-read').checked;
    if (!name) { say('quickstep-status', t('Give the quick step a name.'), false); return; }
    if (label && !validLabel(label)) { say('quickstep-status', t('Give the label: one word, no spaces, quotes or brackets.'), false); return; }
    if (forward && forward.indexOf('@') < 1) { say('quickstep-status', t('Give an address to forward to.'), false); return; }
    if (!read && !label && !forward && !move) { say('quickstep-status', t('Give the quick step something to do.'), false); return; }
    var slug = name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '').slice(0, 40) || 'step';
    var change = {};
    change['qs.' + slug] = JSON.stringify({ name: name, key: el('quickstep-key').value, read: read, move: move, label: label, forward: forward });
    savePrefs(change).then(function (ok) {
      if (!ok) { return; }
      say('quickstep-status', t('Saved.'), true);
      el('quickstep-name').value = ''; el('quickstep-key').value = ''; el('quickstep-read').checked = false; el('quickstep-move').value = ''; el('quickstep-label').value = ''; el('quickstep-forward').value = '';
    });
  });
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

  // The browser is asked to send mailto: links here: registerProtocolHandler,
  // from a click as browsers require, the %s the link the browser hands
  // over, which the compose route reads. The browser asks the reader once.
  el('pref-mailto').addEventListener('click', function () {
    try {
      navigator.registerProtocolHandler('mailto', (location.origin || '') + '/portal#/compose?mailto=%s');
      say('prefs-status', t('This webmail will open mailto: links from now on.'), true);
    } catch (why) {
      say('prefs-status', t('This browser did not take the mailto: handler.'), false);
    }
  });
  el('prefs-form').addEventListener('submit', function (event) {
    event.preventDefault();
    var ticked = [];
    var labels = el('notify-folders').children;
    for (var i = 0; i < labels.length; i++) { var tick = labels[i].children[0]; if (tick && tick.checked) { ticked.push(tick.value); } }
    savePrefs({ language: el('pref-language').value, timezone: el('pref-timezone').value, swipe_right: el('pref-swipe-right').value, swipe_left: el('pref-swipe-left').value, snooze_hours: el('pref-snooze-hours').value, snooze_hour: el('pref-snooze-hour').value, theme: el('pref-theme').value, density: el('pref-density').value, pane: el('pref-pane').value, undo_seconds: el('pref-undo').value,
                notify: el('pref-notify').checked ? '1' : '0', notify_folders: ticked.join(','), inbox: el('pref-inbox').value, nudges: el('pref-nudges').checked ? '1' : '0' }).then(function (ok) {
      if (ok) { say('prefs-status', t('Saved.'), true); if (lastPage) { renderMessages(lastPage); } }
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
      select.hidden = one; el('compose-from-label').hidden = one; el('compose-from-row').hidden = one;
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
    syncComposeRows();
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
  el('message-pin').addEventListener('click', function () { closeMenus(); if (current) { pinMessages([current.id], !isPinned(current)); } });
  el('message-mute').addEventListener('click', function () { closeMenus(); if (current) { muteThread(entryOfCurrent(), !isMuted(current)); } });
  el('message-block').addEventListener('click', function () { closeMenus(); if (current) { blockSender(addressOf(current.from)); } });
  el('message-sweep').addEventListener('click', function () { closeMenus(); if (current) { sweepSender(addressOf(current.from), current.folder_id || state.folderId); } });

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


  // ---- The look: icons, avatars, names and times ---------------------------
  var SVG_NS = 'http://www.w3.org/2000/svg';
  var svgNode = function (tag) { return document.createElementNS ? document.createElementNS(SVG_NS, tag) : document.createElement(tag); };
  var icon = function (name, small) {
    var svg = svgNode('svg');
    svg.setAttribute('class', 'i' + (small ? ' sm' : ''));
    svg.setAttribute('aria-hidden', 'true');
    var use = svgNode('use');
    use.setAttribute('href', '#i-' + name);
    svg.appendChild(use);
    return svg;
  };
  var iconButton = function (name, title, className) {
    var b = node('button', undefined, 'iconbtn sm' + (className ? ' ' + className : ''));
    b.type = 'button';
    b.setAttribute('title', title);
    b.setAttribute('aria-label', title);
    b.appendChild(icon(name));
    return b;
  };
  // The <use> inside a button, by walking: the page's test harness has no
  // querySelector, on purpose.
  var findUse = function (n) {
    if (!n || !n.childNodes) { return null; }
    for (var i = 0; i < n.childNodes.length; i++) {
      var c = n.childNodes[i];
      if (String(c.tagName || '').toLowerCase() === 'use') { return c; }
      var deeper = findUse(c);
      if (deeper) { return deeper; }
    }
    return null;
  };
  var setIcon = function (holder, name) {
    var use = findUse(holder);
    if (use) { use.setAttribute('href', '#i-' + name); }
  };
  var nameOf = function (header) {
    var name = displayNameOf(header);
    if (name) { return name.replace(/^"|"$/g, ''); }
    var address = addressOf(header);
    return address || String(header || '').trim() || '?';
  };
  var initialsOf = function (header) {
    var name = nameOf(header).replace(/[<>"]/g, '').trim();
    if (!name || name === '?') { return '?'; }
    if (name.indexOf('@') >= 0) { name = name.split('@')[0]; }
    var parts = name.split(/[\s._-]+/).filter(Boolean);
    if (!parts.length) { return '?'; }
    return ((parts[0].charAt(0)) + (parts.length > 1 ? parts[parts.length - 1].charAt(0) : '')).toUpperCase();
  };
  var avatarColour = function (address) {
    var h = 0, s = String(address || '').toLowerCase();
    for (var i = 0; i < s.length; i++) { h = (h * 31 + s.charCodeAt(i)) >>> 0; }
    return 'var(--a' + ((h % 10) + 1) + ')';
  };
  var avatarFor = function (header, large) {
    var a = node('span', initialsOf(header), 'avatar' + (large ? ' lg' : ''));
    if (a.style) { a.style.background = avatarColour(addressOf(header)); }
    a.setAttribute('aria-hidden', 'true');
    return a;
  };
  var recipientsOf = function (header) {
    return String(header || '').split(',').map(function (s) { return s.trim() ? nameOf(s.trim()) : ''; }).filter(Boolean);
  };
  var dateOf = function (m) {
    var d = new Date(m.date || '');
    if (isNaN(d.getTime())) { d = new Date(String(m.received || '').replace(' ', 'T')); }
    return isNaN(d.getTime()) ? null : d;
  };
  // The reader's own zone, when one is chosen in Settings: every date and
  // time the page shows is rendered in it, and today and this year are
  // decided in it, so a reader away from home, or reading a server on
  // another continent, sees their own clock. Empty means the browser's.
  var zoneOptions = function (options) {
    var zone = pref('timezone');
    if (zone) { options.timeZone = zone; }
    return options;
  };
  var dayKey = function (d) { try { return d.toLocaleDateString('en-CA', zoneOptions({ year: 'numeric', month: '2-digit', day: '2-digit' })); } catch (e) { return d.toDateString(); } };
  // The time today, the day this year, the date otherwise - what a list shows.
  var whenText = function (m) {
    var d = dateOf(m);
    if (!d) { return m.date || m.received || ''; }
    var now = new Date();
    try {
      var day = dayKey(d), today = dayKey(now);
      if (day === today) { return d.toLocaleTimeString(languageActive, zoneOptions({ hour: '2-digit', minute: '2-digit', hour12: false })); }
      if (day.slice(0, 4) === today.slice(0, 4)) { return d.toLocaleDateString(languageActive, zoneOptions({ day: 'numeric', month: 'short' })); }
      return d.toLocaleDateString(languageActive, zoneOptions({ year: 'numeric', month: 'short', day: 'numeric' }));
    } catch (e) { return d.toDateString(); }
  };
  var fullDate = function (m) {
    var d = dateOf(m);
    if (!d) { return m.date || m.received || ''; }
    try { return d.toLocaleString(languageActive, zoneOptions({ weekday: 'short', year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })); } catch (e) { return d.toString(); }
  };
  // The zones a browser knows, or a short list when it cannot say.
  var fillZones = function () {
    var select = el('pref-timezone');
    if (select.children.length > 1) { return; }
    var zones = [];
    try { zones = (typeof Intl !== 'undefined' && Intl.supportedValuesOf) ? Intl.supportedValuesOf('timeZone') : []; } catch (e) { zones = []; }
    if (!zones.length) { zones = ['UTC', 'Europe/London', 'Europe/Paris', 'Europe/Berlin', 'Europe/Madrid', 'Europe/Rome', 'Europe/Amsterdam', 'Europe/Stockholm', 'Europe/Warsaw', 'Europe/Athens', 'Europe/Kyiv', 'Europe/Moscow', 'America/New_York', 'America/Chicago', 'America/Denver', 'America/Los_Angeles', 'America/Sao_Paulo', 'Asia/Tokyo', 'Asia/Seoul', 'Asia/Shanghai', 'Asia/Kolkata', 'Australia/Sydney']; }
    var chosen = pref('timezone');
    if (chosen && zones.indexOf(chosen) < 0) { zones.push(chosen); }
    zones.forEach(function (z) { var o = document.createElement('option'); o.value = z; o.textContent = z; select.appendChild(o); });
  };

  // ---- Toasts, with undo ----------------------------------------------------
  var toastTimer = 0;
  var dismissToast = function () {
    var box = el('toasts');
    if (box) { clear(box); }
    if (toastTimer && typeof clearTimeout === 'function') { clearTimeout(toastTimer); }
    toastTimer = 0;
  };
  var toast = function (text, undo, sticky, label) {
    var box = el('toasts');
    if (!box) { say('mail-status', text, true); return null; }
    clear(box);
    var one = node('div', undefined, 'toast');
    one.setAttribute('role', 'status');
    one.appendChild(node('span', text));
    one.appendChild(node('span', undefined, 'sp'));
    if (undo) {
      var u = node('button', label || t('Undo')); u.type = 'button';
      u.addEventListener('click', function () { dismissToast(); undo(); });
      one.appendChild(u);
    }
    var x = node('button', undefined, 'x'); x.type = 'button'; x.setAttribute('aria-label', t('Dismiss'));
    x.appendChild(icon('close', true));
    x.addEventListener('click', dismissToast);
    one.appendChild(x);
    box.appendChild(one);
    if (toastTimer && typeof clearTimeout === 'function') { clearTimeout(toastTimer); }
    toastTimer = sticky ? 0 : setTimeout(function () { clear(box); toastTimer = 0; }, 8000);
    return one;
  };
  var filingText = function (to, result, count) {
    var what;
    if (typeof to === 'number') { what = t('moved to ') + folderName(to); }
    else if (to === 'delete') { what = result && result.data && result.data.deleted ? t('deleted') : t('moved to Trash'); }
    else { what = to === 'archive' ? t('archived') : to === 'junk' ? t('moved to Junk') : to === 'inbox' ? t('moved to Inbox') : to === 'trash' ? t('moved to Trash') : t('deleted'); }
    if (count > 1) { return tf('{0} {1}.', count, what); }
    return what.charAt(0).toUpperCase() + what.slice(1) + '.';
  };
  // One call per message, in order; then a toast whose Undo moves each one
  // back where it was, by the id the move gave it.
  var fileMany = function (entries, to) {
    var moved = [];
    var last = null;
    var chain = Promise.resolve();
    entries.forEach(function (e) {
      chain = chain.then(function () {
        return fileMessage(e.id, to).then(function (r) {
          last = r;
          if (r.status === 200 && r.data && r.data.id && e.folderId) { moved.push({ id: r.data.id, back: e.folderId }); }
        });
      });
    });
    return chain.then(function () {
      if (!last || last.status !== 200) { say('mail-status', describe(last || { status: 0 }, t('Could not file the message')), false); return false; }
      say('mail-status', '', true);
      lastListing = null;
      loadFolders();
      var undo = moved.length ? function () {
        var c = Promise.resolve();
        moved.forEach(function (x) { c = c.then(function () { return call('POST', '/api/v1/me/messages/' + x.id + '/move', { folder_id: x.back }); }); });
        c.then(function () { lastListing = null; loadFolders(); if (!el('mail-section').hidden) { loadMessages(); } });
      } : null;
      toast(filingText(to, last, entries.length), undo);
      return true;
    });
  };
  var fileSelected = function (to) {
    selectedEntries().then(function (entries) {
      var ids = entries.map(function (e) { return e.id; });
      return fileMany(entries, to).then(function (ok) {
        clearSelection();
        if (ok && current && ids.indexOf(current.id) >= 0) { closeMessage(true); }
        loadMessages();
      });
    }, function (r) { say('mail-status', describe(r, t('Could not read the folder')), false); });
  };
  // ---- Every message of the folder, not only the page ----------------------
  // Ticking the page's box offers the rest of the folder, as both webmails
  // do; taken up, the bulk actions read the folder page by page - at most
  // five thousand messages - and act on all of it, or on the open tab of it.
  var selectionScope = function () {
    return lastPage && tabsActive(lastPage) ? tabName(activeTab) : folderName(state.folderId);
  };
  var renderSelectNote = function () {
    var note = el('select-note');
    var page = lastListing && lastListing.key === listingKey() ? lastListing.page : null;
    var all = el('select-all');
    if (!page || page.query || state.everywhere || !all || !all.checked || !listRows.length || !(page.total > page.messages.length)) { wholeFolder = false; note.hidden = true; return; }
    if (wholeFolder) {
      el('select-note-text').textContent = tf('Every message in {0} is selected.', selectionScope());
      el('select-folder').textContent = t('Clear selection');
    } else {
      el('select-note-text').textContent = tf('All {0} messages on this page are selected.', selectedIds().length);
      el('select-folder').textContent = tf('Select every message in {0}', selectionScope());
    }
    note.hidden = false;
  };
  el('select-folder').addEventListener('click', function () {
    if (wholeFolder) { clearSelection(); listRows.forEach(function (r) { r.box.checked = false; }); return; }
    wholeFolder = true;
    renderBulk();
  });
  var selectedEntries = function () {
    if (!wholeFolder) {
      return Promise.resolve(selectedIds().map(function (id) {
        var folderId = state.folderId;
        listRows.forEach(function (r) { if (r.id === id || (r.ids || []).indexOf(id) >= 0) { folderId = r.folderId || folderId; } });
        return { id: id, folderId: folderId };
      }));
    }
    var folderId = state.folderId;
    var tab = lastPage && tabsActive(lastPage) ? activeTab : '';
    var entries = [], seen = 0, before = 0;
    var step = function () {
      return call('GET', '/api/v1/me/folders/' + folderId + '/messages?limit=200' + (before ? '&before_uid=' + before : '')).then(function (r) {
        if (r.status !== 200 || !r.data) { return Promise.reject(r); }
        var ms = r.data.messages || [];
        seen += ms.length;
        ms.forEach(function (m) { if (!tab || tabOf(m) === tab) { entries.push({ id: m.id, folderId: folderId }); } });
        if (!ms.length || seen >= (r.data.total || 0) || entries.length >= 5000 || !ms[ms.length - 1].uid) { return entries; }
        before = ms[ms.length - 1].uid;
        return step();
      });
    };
    return step();
  };
  var markRows = function (entries, seen) {
    var chain = Promise.resolve();
    entries.forEach(function (e) {
      (e.ids || [e.id]).forEach(function (id) {
        chain = chain.then(function () { return call('PUT', '/api/v1/me/messages/' + id + '/flags', { seen: seen }); });
      });
    });
    chain.then(function () { lastListing = null; loadFolders(); reloadKeepingPlace(); });
  };
  // Three stars, as Gmail has them: the star, then amber, then red, then
  // none again. The first is the IMAP flag every client shows; the second
  // and third are the keywords $Star2 and $Star3 on top of it, which other
  // clients ignore and the Starred view includes all the same.
  var STAR_KEYWORDS = ['$Star2', '$Star3'];
  var starStage = function (m) {
    if (!m.flags.flagged) { return 0; }
    var keys = (m.flags.keywords || []).map(function (k) { return k.toLowerCase(); });
    return keys.indexOf('$star3') >= 0 ? 3 : keys.indexOf('$star2') >= 0 ? 2 : 1;
  };
  var starTitle = function (stage) { return stage === 0 ? t('Star') : stage === 1 ? t('Second star') : stage === 2 ? t('Third star') : t('Unstar'); };
  var paintStar = function (button, stage) {
    button.classList.toggle('on', stage > 0);
    button.classList.toggle('s2', stage === 2);
    button.classList.toggle('s3', stage === 3);
    button.setAttribute('title', starTitle(stage));
  };
  var applyStarStage = function (m, stage) {
    m.flags.flagged = stage > 0;
    m.flags.keywords = (m.flags.keywords || []).filter(function (k) { return STAR_KEYWORDS.indexOf(k) < 0 && k.toLowerCase() !== '$star2' && k.toLowerCase() !== '$star3'; });
    if (stage >= 2) { m.flags.keywords.push(STAR_KEYWORDS[stage - 2]); }
  };
  var cycleStar = function (m, star) {
    var stage = (starStage(m) + 1) % 4;
    var body = { flagged: stage > 0 };
    if (stage >= 2) { body.keywords_add = [STAR_KEYWORDS[stage - 2]]; }
    body.keywords_remove = STAR_KEYWORDS.filter(function (k) { return !(stage >= 2 && k === STAR_KEYWORDS[stage - 2]); });
    return call('PUT', '/api/v1/me/messages/' + m.id + '/flags', body).then(function (result) {
      if (result.status !== 200) { say('mail-status', describe(result, t('Could not change the flags')), false); return; }
      // The star and the keywords from the server's answer; the rest of the
      // flags - seen, answered - stay as the row had them.
      if (result.data && result.data.flags) { m.flags.flagged = !!result.data.flags.flagged; m.flags.keywords = result.data.flags.keywords || []; } else { applyStarStage(m, stage); }
      if (star) { paintStar(star, starStage(m)); }
      if (current && current.id === m.id) { if (current !== m) { current.flags = m.flags; } renderActions(); }
      lastListing = null;
    });
  };
  var toggleStar = function (m, star) { return cycleStar(m, star); };
  var starButton = function (m) {
    var stage = starStage(m);
    var star = node('button', undefined, 'star' + (stage > 0 ? ' on' : '') + (stage === 2 ? ' s2' : '') + (stage === 3 ? ' s3' : ''));
    star.type = 'button';
    star.setAttribute('title', starTitle(stage));
    star.setAttribute('aria-label', t('Star'));
    star.appendChild(icon('star'));
    star.addEventListener('click', function (event) { event.stopPropagation(); toggleStar(m, star); });
    return star;
  };

  // ---- The reading pane -----------------------------------------------------
  var paneMode = function () { var p = pref('pane'); return p === 'bottom' || p === 'off' ? p : 'right'; };
  var applyPane = function () {
    var s = el('mail-section');
    var mode = paneMode();
    s.classList.toggle('has-pane', mode !== 'off');
    s.classList.toggle('pane-bottom', mode === 'bottom');
    s.classList.toggle('no-pane', mode === 'off');
  };
  // The message leaves the pane; quietly, the address bar is put back to the
  // list without a history entry (the message is gone), otherwise it goes
  // back the way the reader came.
  var dropMessage = function () {
    if (!el('compose-section').hidden && el('compose-section').classList.contains('inline')) { placeCompose('dock'); }
    current = null;
    el('message-view').hidden = true;
    el('pane-empty').hidden = false;
    el('mail-section').classList.remove('has-message');
  };
  var closeMessage = function (quiet) {
    current = null;
    el('message-view').hidden = true;
    el('pane-empty').hidden = false;
    el('mail-section').classList.remove('has-message');
    listRows.forEach(function (r) { r.row.classList.remove('open'); });
    if (parseHash().parts[0] !== 'm') { return; }
    if (!quiet && cameFromList && history.length > 1) { history.back(); return; }
    replaceWith(listHash());
  };
  var currentNavHash = function () {
    if (state.everywhere) { return state.query === 'is:flagged' ? '/starred' : ''; }
    return state.folderId ? '/f/' + state.folderId : '';
  };
  var renderCount = function (page) {
    var count = el('list-count');
    var older = el('page-older');
    var newer = el('page-newer');
    var n = page.messages.length;
    if (page.query) {
      count.textContent = n ? tf('{0} results', n) : '';
      older.hidden = true; newer.hidden = true;
      return;
    }
    count.textContent = n ? (state.before ? tf('{0} older, of {1}', n, page.total) : tf('1-{0} of {1}', n, page.total)) : '';
    older.hidden = !(n && page.total > n);
    newer.hidden = !state.before;
    pagerOlderUid = n ? page.messages[n - 1].uid : 0;
  };
  var pagerOlderUid = 0;
  el('page-older').addEventListener('click', function () { if (pagerOlderUid) { state.before = pagerOlderUid; go(listHash()); } });
  el('page-newer').addEventListener('click', function () { state.before = 0; go(listHash()); });
  el('list-refresh').addEventListener('click', function () { lastListing = null; loadFolders(); loadMessages(); });
  el('list-mark-all-read').addEventListener('click', function () { closeMenus(); markRows(listRows.filter(function (r) { return !r.m || !r.m.flags.seen || r.ids; }), true); });
  el('message-details-toggle').addEventListener('click', function () {
    var d = el('message-details');
    d.hidden = !d.hidden;
    el('message-details-toggle').setAttribute('aria-expanded', d.hidden ? 'false' : 'true');
  });
  ['right', 'bottom', 'off'].forEach(function (mode) {
    el('pane-' + mode).addEventListener('click', function () {
      closeMenus();
      savePrefs({ pane: mode }).then(function () { applyPane(); renderListTools(); if (current) { el('message-view').hidden = false; el('pane-empty').hidden = true; } });
    });
  });

  // ---- Menus that open under a button and close on a click elsewhere --------
  var within = function (target, ancestor) { var n = target; while (n) { if (n === ancestor) { return true; } n = n.parentNode; } return false; };
  var menus = [['list-more-btn', 'list-more'], ['message-more-btn', 'message-more'], ['select-menu-btn', 'select-menu'], ['account-btn', 'account-menu'], ['search-tune', 'search-advanced']];
  var closeMenus = function () {
    menus.forEach(function (pair) { var m = el(pair[1]); if (m && !m.hidden) { m.hidden = true; el(pair[0]).setAttribute('aria-expanded', 'false'); } });
  };
  menus.forEach(function (pair) {
    el(pair[0]).addEventListener('click', function (event) {
      event.stopPropagation();
      var menu = el(pair[1]);
      var open = menu.hidden;
      closeMenus();
      menu.hidden = !open;
      el(pair[0]).setAttribute('aria-expanded', open ? 'true' : 'false');
      if (open && pair[1] === 'search-advanced') { renderSavedSearches(); el('adv-from').focus(); }
    });
    el(pair[1]).addEventListener('click', function (event) { event.stopPropagation(); });
  });
  document.addEventListener('click', function (event) {
    var open = menus.filter(function (pair) { return !el(pair[1]).hidden; });
    if (!open.length) { return; }
    var inside = open.some(function (pair) { return within(event.target, el(pair[1])) || within(event.target, el(pair[0])); });
    if (!inside) { closeMenus(); }
  });
  el('select-menu').addEventListener('click', function (event) {
    var pick = event.target && event.target.getAttribute ? event.target.getAttribute('data-pick') : '';
    if (!pick) { return; }
    closeMenus();
    listRows.forEach(function (r) {
      var messages = r.ms || [r.m];
      var take = pick === 'all' ? true : pick === 'none' ? false :
        pick === 'read' ? messages.every(function (m) { return m.flags.seen; }) :
        pick === 'unread' ? messages.some(function (m) { return !m.flags.seen; }) :
        pick === 'starred' ? messages.some(function (m) { return m.flags.flagged; }) :
        messages.every(function (m) { return !m.flags.flagged; });
      (r.ids || [r.id]).forEach(function (id) { selected[id] = take; });
      r.box.checked = take;
    });
    renderBulk();
  });
  el('select-all').addEventListener('click', function () {
    var take = el('select-all').checked;
    listRows.forEach(function (r) { (r.ids || [r.id]).forEach(function (id) { selected[id] = take; }); r.box.checked = take; });
    renderBulk();
  });
  el('menu-settings').addEventListener('click', function () { closeMenus(); go('/settings'); });
  el('menu-security').addEventListener('click', function () { closeMenus(); go('/security'); });
  el('settings-btn').addEventListener('click', function () { go('/settings'); });
  el('keys-btn').addEventListener('click', function () { openKeys(); });
  el('storage-link').addEventListener('click', function () { go('/storage'); });
  (function () {
    var nav = el('side-nav').children;
    for (var i = 0; i < nav.length; i++) {
      (function (b) { b.addEventListener('click', function () { go(b.getAttribute('data-route')); }); })(nav[i]);
    }
  })();
  el('nav-toggle').addEventListener('click', function () { el('account').classList.toggle('nav-open'); });
  el('nav-scrim').addEventListener('click', function () { el('account').classList.remove('nav-open'); });
  el('side').addEventListener('click', function (event) {
    var n = event.target;
    while (n && n !== el('side')) { if (n.tagName === 'BUTTON') { el('account').classList.remove('nav-open'); return; } n = n.parentNode; }
  });

  // ---- The search box's options ---------------------------------------------
  var quoteTerm = function (value) { value = value.trim(); return /\s/.test(value) ? '"' + value.replace(/"/g, '') + '"' : value; };
  var advancedQuery = function () {
    var parts = [];
    var words = el('adv-words').value.trim();
    if (words) { parts.push(words); }
    if (el('adv-from').value.trim()) { parts.push('from:' + quoteTerm(el('adv-from').value)); }
    if (el('adv-to').value.trim()) { parts.push('to:' + quoteTerm(el('adv-to').value)); }
    if (el('adv-subject').value.trim()) { parts.push('subject:' + quoteTerm(el('adv-subject').value)); }
    if (el('adv-label').value.trim()) { parts.push('label:' + el('adv-label').value.trim()); }
    if (el('adv-after').value) { parts.push('after:' + el('adv-after').value); }
    if (el('adv-before').value) { parts.push('before:' + el('adv-before').value); }
    var size = el('adv-size').value.replace(/\s+/g, '');
    if (size) { parts.push((el('adv-size-kind').value === 'smaller' ? 'smaller:' : 'larger:') + size); }
    if (el('adv-age').value) { parts.push('newer_than:' + el('adv-age').value); }
    if (el('adv-attachment').checked) { parts.push('has:attachment'); }
    if (el('adv-unread').checked) { parts.push('is:unread'); }
    if (el('adv-flagged').checked) { parts.push('is:flagged'); }
    return parts.join(' ');
  };
  // ---- Saved searches -------------------------------------------------------
  // A search kept under a name, in the account's preferences so it follows
  // the reader between browsers, and listed among the folders: opening one
  // runs the search again, so it is always current, which is what Outlook's
  // search folders are. Twenty at most, as many as a navigation can carry.
  var savedSearches = function () {
    try {
      var list = JSON.parse(pref('saved_searches') || '[]');
      return Array.isArray(list) ? list.filter(function (s) { return s && s.name && s.q; }) : [];
    } catch (why) { return []; }
  };
  var savedSearchRoute = function (s, i) { return '/search?q=' + encodeURIComponent(s.q) + '&saved=' + i; };
  var writeSavedSearches = function (list) {
    return savePrefs({ saved_searches: JSON.stringify(list.slice(0, 20)) }).then(function (ok) { if (ok) { return loadFolders(); } });
  };
  var renderSavedSearches = function () {
    var box = el('adv-saved');
    clear(box);
    var list = savedSearches();
    box.hidden = !list.length;
    list.forEach(function (s, i) {
      var open = node('button', s.name, 'btn ghost sm'); open.type = 'button';
      open.addEventListener('click', function () { closeMenus(); go(savedSearchRoute(s, i)); });
      var remove = iconButton('close', t('Remove') + ': ' + s.name);
      remove.addEventListener('click', function () { writeSavedSearches(list.filter(function (x, j) { return j !== i; })).then(renderSavedSearches); });
      var row = node('span', undefined, 'saved-search'); row.appendChild(open); row.appendChild(remove);
      box.appendChild(row);
    });
  };
  el('adv-save').addEventListener('click', function () {
    var q = advancedQuery() || el('mail-search').value.trim();
    var name = el('adv-save-name').value.trim();
    if (!q || !name) { el('adv-save-name').focus(); return; }
    var list = savedSearches().filter(function (s) { return s.name !== name; });
    list.push({ name: name, q: q });
    el('adv-save-name').value = '';
    writeSavedSearches(list).then(renderSavedSearches);
  });
  el('adv-search').addEventListener('click', function () {
    var q = advancedQuery();
    el('mail-search').value = q;
    el('mail-search-everywhere').checked = el('adv-where').value === 'all';
    closeMenus();
    if (!q) { return; }
    runSearch(q);
  });
  el('adv-reset').addEventListener('click', function () {
    ['adv-from', 'adv-to', 'adv-subject', 'adv-words', 'adv-label', 'adv-after', 'adv-before', 'adv-size', 'adv-age'].forEach(function (id) { el(id).value = ''; });
    ['adv-attachment', 'adv-unread', 'adv-flagged'].forEach(function (id) { el(id).checked = false; });
    el('adv-where').value = 'folder';
    el('adv-size-kind').value = 'larger';
  });
  el('mail-search').addEventListener('input', function () { el('mail-search-clear').hidden = !el('mail-search').value && !state.query; });
  window.addEventListener('hashchange', function () { el('mail-search-clear').hidden = !state.query && !el('mail-search').value; });

  // ---- Compose: Cc and Bcc appear when asked for, or when they hold something
  var syncComposeRows = function () {
    el('compose-cc-row').hidden = !el('compose-cc').value;
    el('compose-bcc-row').hidden = !el('compose-bcc').value;
    el('show-cc').hidden = !el('compose-cc-row').hidden;
    el('show-bcc').hidden = !el('compose-bcc-row').hidden;
  };
  el('show-cc').addEventListener('click', function () { el('compose-cc-row').hidden = false; el('show-cc').hidden = true; el('compose-cc').focus(); });
  el('show-bcc').addEventListener('click', function () { el('compose-bcc-row').hidden = false; el('show-bcc').hidden = true; el('compose-bcc').focus(); });

  // ---- The keys a mail client has, on top of the list's own ----------------
  document.addEventListener('keydown', function (event) {
    var tag = (event.target && event.target.tagName) || '';
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || event.ctrlKey || event.metaKey || event.altKey) { return; }
    if (el('account').hidden || !el('keys-overlay').hidden || !el('palette').hidden) { return; }
    if (event.key === '/') { event.preventDefault(); el('mail-search').focus(); return; }
    if (event.key === 'c') { event.preventDefault(); go('/compose'); return; }
    if (el('mail-section').hidden) { return; }
    // A digit runs the quick step given that key.
    if (/^[1-9]$/.test(event.key)) { var step = quickStepByKey(event.key); if (step) { event.preventDefault(); runQuickStep(step); } return; }
    var open = current && !el('message-view').hidden;
    if (event.key === 'u' || (event.key === 'Escape' && open)) { if (open) { event.preventDefault(); closeMessage(false); } return; }
    if (open && event.key === 'r') { event.preventDefault(); go('/compose?reply=' + current.id); return; }
    if (open && event.key === 'a') { event.preventDefault(); go('/compose?replyall=' + current.id); return; }
    if (open && event.key === 'f') { event.preventDefault(); go('/compose?forward=' + current.id); return; }
    if (event.key === 's') {
      event.preventDefault();
      if (open) { setFlags({ flagged: !current.flags.flagged }, false); return; }
      if (cursor >= 0 && listRows[cursor] && listRows[cursor].star) { toggleStar(listRows[cursor].m, listRows[cursor].star); }
      return;
    }
    if (event.key === 'o' && cursor >= 0 && listRows[cursor] && !el('message-list').hidden) { event.preventDefault(); go('/m/' + listRows[cursor].id); return; }
    // With a reading pane, moving the cursor reads the message it lands on.
    if ((event.key === 'j' || event.key === 'k' || event.key === 'ArrowDown' || event.key === 'ArrowUp') && paneMode() !== 'off' && open && cursor >= 0 && listRows[cursor] && listRows[cursor].id !== current.id) {
      replaceWith('/m/' + listRows[cursor].id);
    }
  });


  // ---- The compose window: docked over the mail, or under the message it answers
  var composeMode = 'new';
  var composeId = 0;
  var placeCompose = function (where) {
    var dock = el('compose-section');
    if (where === 'inline') {
      if (dock.parentNode !== el('reply-slot')) { el('reply-slot').appendChild(dock); }
      dock.classList.add('inline');
      dock.classList.remove('min'); dock.classList.remove('full');
    } else {
      if (dock.parentNode !== document.body) { document.body.appendChild(dock); }
      dock.classList.remove('inline');
    }
  };
  var composeTitle = function (mode) {
    if (mode === 'reply') { return t('Reply'); }
    if (mode === 'replyall') { return t('Reply all'); }
    if (mode === 'forward') { return t('Forward'); }
    if (mode === 'draft') { return t('Draft'); }
    return t('New message');
  };
  var openCompose = function (mode, id) {
    composeMode = mode; composeId = id;
    var answering = (mode === 'reply' || mode === 'replyall' || mode === 'forward') && current && current.id === id && !el('message-view').hidden;
    placeCompose(answering ? 'inline' : 'dock');
    el('compose-title').textContent = composeTitle(mode) + (answering && current && current.subject ? ': ' + current.subject : '');
    el('compose-section').hidden = false;
    el('compose-section').classList.remove('min');
  };
  var hideCompose = function () { el('compose-section').hidden = true; };
  // Closing keeps what was written: a draft is saved when there is anything
  // to save, and the address bar goes back to the list when it named the window.
  var closeCompose = function () {
    hideCompose();
    el('compose-section').classList.remove('min'); el('compose-section').classList.remove('full');
    if (parseHash().parts[0] === 'compose') { replaceWith(listHash()); }
  };
  el('compose-close').addEventListener('click', function () {
    var written = el('compose-to').value.trim() || el('compose-subject').value.trim() || el('compose-text').value.trim();
    if (written && !pendingSend) { el('compose-save').click(); }
    closeCompose();
  });
  el('compose-attach').addEventListener('click', function () { el('compose-files').click(); });
  el('compose-min').addEventListener('click', function () { el('compose-section').classList.toggle('min'); el('compose-section').classList.remove('full'); });
  el('compose-expand').addEventListener('click', function () { el('compose-section').classList.toggle('full'); el('compose-section').classList.remove('min'); });
  el('compose-title').addEventListener('click', function () { if (el('compose-section').classList.contains('min')) { el('compose-section').classList.remove('min'); } });

  // ---- Pop-out: the message, or the message being written, in a window of its own
  // The page at that address, in a new window the size of a reading pane, so
  // a person reads one message while writing another. Nothing is handed to
  // the new window but the address: it loads as a reload of this page would.
  var popOut = function (hash) {
    var url = location.href.split('#')[0] + '#' + hash;
    try { window.open(url, '_blank', 'popup,width=900,height=720,noopener'); } catch (e) { /* a browser that refuses windows */ }
  };
  el('message-popout').addEventListener('click', function () { if (current) { popOut('/m/' + current.id); } });
  // The message being written is saved as a draft first, so the new window
  // opens on what was typed, and the form here closes: two windows editing
  // one draft would each save over the other. A form with nothing in it
  // opens its own address in the new window.
  el('compose-popout').addEventListener('click', function () {
    var written = el('compose-to').value.trim() || el('compose-subject').value.trim() || el('compose-text').value.trim();
    if (!written) { popOut('/compose' + (composeMode !== 'new' && composeId ? '?' + composeMode + '=' + composeId : '')); closeCompose(); return; }
    saveDraft().then(function (id) {
      if (!id) { return; }
      popOut('/compose?draft=' + id);
      blankCompose();
      composeKey = newComposeKey();
      closeCompose();
    });
  });

  // ---- The rest of a conversation, above the message opened ---------------
  var conversationOpen = {};
  var renderConversation = function (m, page) {
    var box = el('conversation');
    clear(box);
    page = page || (lastListing && lastListing.page);
    if (!page || pref('view') !== 'threads' || page.query) { return; }
    var key = threadKey(m);
    var others = page.messages.filter(function (x) { return x.id !== m.id && threadKey(x) === key; });
    if (!others.length) { return; }
    others.sort(function (a, b) { var da = dateOf(a), db = dateOf(b); return (da ? da.getTime() : 0) - (db ? db.getTime() : 0); });
    box.appendChild(node('div', tf('{0} earlier messages in this conversation', others.length), 'count'));
    others.forEach(function (x) {
      var card = node('div', undefined, 'msg-card collapsed');
      var head = node('div', undefined, 'msg-head');
      head.appendChild(avatarFor(x.from, true));
      var from = node('div', undefined, 'from');
      var line = node('div'); line.appendChild(node('b', nameOf(x.from))); from.appendChild(line);
      from.appendChild(node('div', x.snippet || '', 'snip'));
      head.appendChild(from);
      var when = node('div', whenText(x), 'date'); when.setAttribute('title', fullDate(x)); head.appendChild(when);
      card.appendChild(head);
      var body = node('div', undefined, 'body'); body.hidden = true; body.setAttribute('dir', 'auto'); card.appendChild(body);
      var atts = node('div', undefined, 'atts'); atts.hidden = true; card.appendChild(atts);
      var loaded = null;
      card.addEventListener('click', function () {
        var open = card.classList.contains('collapsed');
        card.classList.toggle('collapsed', !open);
        body.hidden = !open; atts.hidden = !open;
        from.childNodes[1].hidden = open;
        if (!open || loaded) { return; }
        loaded = true;
        body.textContent = t('Loading...');
        call('GET', '/api/v1/me/messages/' + x.id).then(function (result) {
          if (result.status !== 200 || !result.data) { body.textContent = t('Could not open the message'); return; }
          var d = result.data;
          var text = d.text || '';
          if (!text && d.html) { text = new DOMParser().parseFromString(d.html, 'text/html').body.textContent || ''; }
          body.textContent = text || t('(no text)');
          clear(atts);
          (d.attachments || []).forEach(function (a) {
            var link = node('a', undefined, 'att');
            link.href = '/api/v1/me/messages/' + d.id + '/attachments/' + a.index;
            link.setAttribute('download', a.name);
            link.appendChild(icon('file'));
            var words = node('div'); words.appendChild(node('div', a.name, 'n')); words.appendChild(node('div', format(a.size), 's'));
            link.appendChild(words);
            atts.appendChild(link);
          });
          if (!d.flags.seen) { call('PUT', '/api/v1/me/messages/' + d.id + '/flags', { seen: true }).then(function () { x.flags.seen = true; loadFolders(); }); }
        });
      });
      box.appendChild(card);
    });
  };


  // ---- Pin, block, sweep; drag to a folder; the right-click menu -----------
  // ---- The inbox in tabs ----------------------------------------------------
  // Primary, Social, Promotions, Updates and Forums, the way Gmail sorts an
  // inbox, or Focused and Other, the way Outlook does; or one list. The
  // server says which tab a message is from its header (category in the
  // listing); the reader's own word for a sender, kept with the account's
  // preferences, overrules it. A tab is a filter over the page loaded, so
  // switching costs no request.
  var TABS = ['primary', 'social', 'promotions', 'updates', 'forums'];
  var TAB_ICONS = { primary: 'person', social: 'people', promotions: 'tag', updates: 'info', forums: 'chat', focused: 'person', other: 'inbox' };
  var tabName = function (tab) {
    return tab === 'primary' ? t('Primary') : tab === 'social' ? t('Social') : tab === 'promotions' ? t('Promotions') : tab === 'updates' ? t('Updates') :
      tab === 'forums' ? t('Forums') : tab === 'focused' ? t('Focused') : t('Other');
  };
  var inboxMode = function () { var m = pref('inbox'); return m === 'focused' || m === 'all' ? m : 'tabs'; };
  var tabList = function () { return inboxMode() === 'focused' ? ['focused', 'other'] : TABS; };
  var activeTab = 'primary';
  var senderTabs = function () { try { var v = JSON.parse(pref('tabs_by_sender') || '{}'); return v && typeof v === 'object' ? v : {}; } catch (e) { return {}; } };
  var tabOf = function (m) {
    var own = senderTabs()[addressOf(m.from || '').toLowerCase()] || '';
    var focused = inboxMode() === 'focused';
    if (focused && (own === 'focused' || own === 'other')) { return own; }
    var category = TABS.indexOf(own) >= 0 ? own : TABS.indexOf(m.category) >= 0 ? m.category : 'primary';
    if (focused) { return category === 'primary' ? 'focused' : 'other'; }
    return category;
  };
  var tabsActive = function (page) { return inboxMode() !== 'all' && !state.everywhere && !page.query && !!inboxId && state.folderId === inboxId; };
  var renderTabs = function (page) {
    var bar = el('inbox-tabs');
    clear(bar);
    if (!tabsActive(page)) { bar.hidden = true; return; }
    var tabs = tabList();
    if (tabs.indexOf(activeTab) < 0) { activeTab = tabs[0]; }
    var unseen = {};
    page.messages.forEach(function (m) { if (!m.flags.seen) { var tab = tabOf(m); unseen[tab] = (unseen[tab] || 0) + 1; } });
    tabs.forEach(function (tab) {
      var b = document.createElement('button');
      b.type = 'button';
      b.setAttribute('role', 'tab');
      b.setAttribute('data-tab', tab);
      b.setAttribute('aria-selected', tab === activeTab ? 'true' : 'false');
      if (tab === activeTab) { b.classList.add('on'); }
      b.appendChild(icon(TAB_ICONS[tab]));
      b.setAttribute('title', tabName(tab));
      b.appendChild(node('span', tabName(tab), 'lbl'));
      if (unseen[tab]) { var n = node('span', String(unseen[tab]), 'n'); n.setAttribute('title', tf('{0} unread', unseen[tab])); b.appendChild(n); }
      b.addEventListener('click', function () { activeTab = tab; if (lastPage) { renderMessages(lastPage); } });
      bar.appendChild(b);
    });
    bar.hidden = false;
  };
  // The row's menu offers the other tabs for the sender; the choice is kept
  // in the preferences, so it holds on every device and for every message
  // from that sender, and Undo takes it back.
  var renderContextTabs = function (m) {
    var box = el('context-tabs');
    clear(box);
    if (lastPage && tabsActive(lastPage) && m) {
      var mine = tabOf(m);
      box.appendChild(node('div', undefined, 'sep'));
      tabList().forEach(function (tab) {
        if (tab === mine) { return; }
        var b = document.createElement('button');
        b.type = 'button';
        b.setAttribute('data-act', 'tab');
        b.setAttribute('data-tab', tab);
        b.textContent = tf('Show under {0}', tabName(tab));
        box.appendChild(b);
      });
    }
    box.hidden = !box.children.length;
  };
  var moveSenderToTab = function (address, tab) {
    address = String(address || '').trim().toLowerCase();
    if (!address || !tab) { return; }
    var map = senderTabs();
    var before = JSON.stringify(map);
    map[address] = tab;
    savePrefs({ tabs_by_sender: JSON.stringify(map) }).then(function (ok) {
      if (!ok) { return; }
      if (lastPage) { renderMessages(lastPage); }
      toast(tf('Messages from {0} will show under {1}.', address, tabName(tab)), function () {
        savePrefs({ tabs_by_sender: before }).then(function () { if (lastPage) { renderMessages(lastPage); } });
      });
    });
  };

  // ---- Mute -----------------------------------------------------------------
  // The conversation leaves the inbox, and its replies will too: every
  // message of it carries $Muted (so a row shows it, and Unmute knows), the
  // messages are archived, and a rule files whatever answers any of them
  // into the archive folder. Unmute takes the keyword and the rule away.
  var MUTE = '$Muted';
  var isMuted = function (m) { return !!(m && m.flags && (m.flags.keywords || []).indexOf(MUTE) >= 0); };
  var muteMark = function () { var s = node('span', undefined, 'mute'); s.setAttribute('title', t('Muted')); s.appendChild(icon('bell-off', true)); return s; };
  var entryOfCurrent = function () {
    var hit = null;
    listRows.forEach(function (r) { if (r.id === current.id || (r.ids || []).indexOf(current.id) >= 0) { hit = r; } });
    return hit || { id: current.id, m: current, folderId: current.folder_id || state.folderId };
  };
  var threadIdsOf = function (messages) {
    var ids = [];
    messages.forEach(function (m) {
      [m.message_id, m.in_reply_to].concat(String(m.references || '').split(/\s+/)).forEach(function (id) {
        id = String(id || '').trim();
        if (id && ids.indexOf(id) < 0) { ids.push(id); }
      });
    });
    return ids;
  };
  var setKeywordOn = function (ids, keyword, on) {
    var chain = Promise.resolve();
    ids.forEach(function (id) { chain = chain.then(function () { return call('PUT', '/api/v1/me/messages/' + id + '/flags', on ? { keywords_add: [keyword] } : { keywords_remove: [keyword] }); }); });
    return chain;
  };
  var muteThread = function (entry, on) {
    if (!entry || !entry.m) { return; }
    var messages = entry.ms || [entry.m];
    var ids = messages.map(function (m) { return m.id; });
    var key = threadKey(entry.m);
    withRules(function (rules) {
      var before = rules.slice();
      var kept = rules.filter(function (r) { return !(r.field === 'thread' && r.key === key); });
      var next = kept;
      if (on) {
        var archive = allFolders.filter(function (f) { return !f.owner && folderIs(f.id, 'Archive'); })[0];
        next = kept.concat([{ field: 'thread', key: key, text: threadIdsOf(messages), subject: entry.m.subject || '', action: 'move', folder: archive ? archive.path : 'Archive' }]);
      }
      return setKeywordOn(ids, MUTE, on).then(function () { return saveRules(next); }).then(function (ok) {
        if (!ok) { return; }
        var moved = [];
        var moves = Promise.resolve();
        if (on && !folderIs(entry.folderId, 'Archive')) {
          ids.forEach(function (id) {
            moves = moves.then(function () {
              return fileMessage(id, 'archive').then(function (r) { if (r.status === 200 && r.data && r.data.id) { moved.push({ id: r.data.id, back: entry.folderId }); } });
            });
          });
        }
        return moves.then(function () {
          if (current && ids.indexOf(current.id) >= 0) {
            if (moved.length) { closeMessage(true); }
            else { current.flags.keywords = (current.flags.keywords || []).filter(function (k) { return k !== MUTE; }).concat(on ? [MUTE] : []); renderActions(); }
          }
          lastListing = null;
          loadFolders();
          if (moved.length) { loadMessages(); } else { reloadKeepingPlace(); }
          toast(on ? t('Muted. Replies will skip the inbox.') : t('Unmuted.'), on ? function () {
            var back = [];
            var undo = saveRules(before);
            moved.forEach(function (x) {
              undo = undo.then(function () {
                return call('POST', '/api/v1/me/messages/' + x.id + '/move', { folder_id: x.back }).then(function (r) { if (r.status === 200 && r.data && r.data.id) { back.push(r.data.id); } });
              });
            });
            undo.then(function () { return setKeywordOn(moved.length ? back : ids, MUTE, false); }).then(function () {
              lastListing = null;
              loadFolders();
              if (!el('mail-section').hidden) { loadMessages(); }
            });
          } : null);
        });
      });
    });
  };
  var PIN = '$Pinned';
  var isPinned = function (m) { return !!(m && m.flags && (m.flags.keywords || []).indexOf(PIN) >= 0); };
  var pinMessages = function (ids, on) {
    var chain = Promise.resolve();
    ids.forEach(function (id) {
      chain = chain.then(function () { return call('PUT', '/api/v1/me/messages/' + id + '/flags', on ? { keywords_add: [PIN] } : { keywords_remove: [PIN] }); });
    });
    chain.then(function () {
      if (current && ids.indexOf(current.id) >= 0) {
        var list = (current.flags.keywords || []).filter(function (k) { return k !== PIN; });
        if (on) { list.push(PIN); }
        current.flags.keywords = list;
        renderActions();
      }
      lastListing = null;
      reloadKeepingPlace();
      toast(on ? t('Pinned to the top of the list.') : t('Unpinned.'));
    });
  };
  // A rule that files what the sender sends into Junk - or discards it where
  // the account has no Junk folder - added to the account's own rules.
  // ---- Report phishing ------------------------------------------------------
  // Beside Junk: the message's source goes to the domain's postmaster as an
  // attachment (with what the page knows of it in the text when the source
  // cannot be fetched), the message is filed as junk, which teaches the
  // filter, and the sender is blocked - the block's toast carries its Undo.
  var reportPhishing = function (m) {
    if (!m) { return Promise.resolve(); }
    var domain = me && me.indexOf('@') > 0 ? me.split('@')[1] : '';
    if (!domain) { say('mail-status', t('Could not report the message'), false); return Promise.resolve(); }
    var sender = addressOf(m.from);
    var report = {
      to: 'postmaster@' + domain,
      subject: t('Phishing report: ') + String(m.subject || ''),
      text: tf('{0} reported this message as phishing. From: {1}. Subject: {2}. Received: {3}. Its source is attached.', me, m.from || '', m.subject || '', m.date || m.received || '')
    };
    var source = fetch('/api/v1/me/messages/' + m.id + '/source', { cache: 'no-store', credentials: 'same-origin' }).then(function (response) {
      if (response.status !== 200) { return null; }
      return response.blob().then(function (blob) {
        return new Promise(function (resolve) {
          var reader = new FileReader();
          reader.onload = function () { resolve({ name: 'reported-' + m.id + '.eml', type: 'message/rfc822', data: String(reader.result).split(',')[1] || '' }); };
          reader.onerror = function () { resolve(null); };
          reader.readAsDataURL(blob);
        });
      });
    }).catch(function () { return null; });
    return source.then(function (attachment) {
      if (attachment) { report.attachments = [attachment]; }
      return call('POST', '/api/v1/me/messages', report);
    }).then(function (result) {
      if (result.status !== 201 && result.status !== 200) { say('mail-status', describe(result, t('Could not report the message')), false); return; }
      var entry = { id: m.id, folderId: m.folder_id || state.folderId, m: m };
      if (!folderIs(entry.folderId, 'Junk')) { fileRow(entry, 'junk'); }
      if (sender) { blockSender(sender); }
      say('mail-status', t('Reported to the postmaster, filed as junk and the sender blocked.'), true);
    });
  };
  el('message-phish').addEventListener('click', function () { if (current) { closeMenus(); reportPhishing(current); } });
  var blockSender = function (address) {
    address = String(address || '').trim();
    if (!address) { return; }
    withRules(function (rules) {
      var junk = allFolders.filter(function (f) { return !f.owner && folderIs(f.id, 'Junk'); })[0];
      var rule = junk ? { field: 'from', text: address, action: 'move', folder: junk.path } : { field: 'from', text: address, action: 'discard' };
      var before = rules.slice();
      return saveRules(rules.concat([rule])).then(function (ok) {
        if (!ok) { return; }
        toast(tf('Messages from {0} will go to {1}.', address, junk ? junk.path : t('nowhere')), function () { saveRules(before); });
      });
    });
  };
  // The account's rules as the server has them now - the page's copy is only
  // as fresh as the last visit to the Rules page - or a word when the script
  // was written by hand, in which case nothing is written over it.
  var withRules = function (fn) {
    return call('GET', '/api/v1/me/filters').then(function (result) {
      if (result.status === 200 && result.data) { el('filter-script').value = result.data.active || ''; }
      var rules = rulesOf(el('filter-script').value);
      if (rules === null) { toast(t('The filter script was written by hand: add the rule there.')); return null; }
      return fn(rules);
    });
  };
  // Every message from the sender in the folder, deleted in one go, with undo.
  var sweepSender = function (address, folderId) {
    address = String(address || '').trim();
    if (!address || !folderId) { return; }
    call('GET', '/api/v1/me/folders/' + folderId + '/messages?q=' + encodeURIComponent('from:' + address) + '&limit=200').then(function (result) {
      if (result.status !== 200 || !result.data) { say('mail-status', describe(result, t('Could not read the folder')), false); return; }
      var hits = (result.data.messages || []).filter(function (m) { return addressOf(m.from).toLowerCase() === address.toLowerCase(); });
      if (!hits.length) { toast(tf('Nothing from {0} in this folder.', address)); return; }
      fileMany(hits.map(function (m) { return { id: m.id, folderId: folderId }; }), 'delete').then(function (ok) {
        if (ok && current && hits.some(function (m) { return m.id === current.id; })) { closeMessage(true); }
        loadMessages();
      });
    });
  };
  var dragged = null;
  var rowGestures = function (row, entry) {
    row.setAttribute('draggable', 'true');
    row.addEventListener('dragstart', function (event) {
      var ids = selected[entry.id] ? selectedIds() : (entry.ids || [entry.id]);
      dragged = { ids: ids, folderId: entry.folderId };
      row.classList.add('dragging');
      if (event.dataTransfer) { try { event.dataTransfer.setData('text/plain', ids.join(',')); event.dataTransfer.effectAllowed = 'move'; } catch (e) { /* the harness's event has none */ } }
    });
    row.addEventListener('dragend', function () { row.classList.remove('dragging'); dragged = null; });
    row.addEventListener('contextmenu', function (event) {
      event.preventDefault();
      openContextMenu(entry, event.clientX || 0, event.clientY || 0);
    });
  };
  var dropTarget = function (button, folderId) {
    button.addEventListener('dragover', function (event) { if (!dragged || dragged.folderId === folderId) { return; } event.preventDefault(); button.classList.add('drop'); });
    button.addEventListener('dragleave', function () { button.classList.remove('drop'); });
    button.addEventListener('drop', function (event) {
      event.preventDefault();
      button.classList.remove('drop');
      if (!dragged) { return; }
      var moving = dragged;
      dragged = null;
      fileMany(moving.ids.map(function (id) { return { id: id, folderId: moving.folderId }; }), folderId).then(function (ok) {
        if (ok && current && moving.ids.indexOf(current.id) >= 0) { closeMessage(true); }
        clearSelection();
        loadMessages();
      });
    });
  };
  var contextRow = null;
  var openContextMenu = function (entry, x, y) {
    closeMenus();
    contextRow = entry;
    var menu = el('context-menu');
    var m = entry.m;
    var items = menu.children;
    for (var i = 0; i < items.length; i++) {
      var act = items[i].getAttribute ? items[i].getAttribute('data-act') : '';
      if (act === 'read') { items[i].textContent = m.flags.seen ? t('Mark as unread') : t('Mark as read'); }
      if (act === 'star') { items[i].textContent = m.flags.flagged ? t('Unstar') : t('Star'); }
      if (act === 'pin') { items[i].textContent = isPinned(m) ? t('Unpin') : t('Pin'); }
      if (act === 'mute') { items[i].textContent = isMuted(m) ? t('Unmute') : t('Mute'); }
      // Only a conversation has anything to clean up.
      if (act === 'cleanup') { items[i].hidden = !(entry.ms && entry.ms.length > 1); }
      if (act === 'junk') { items[i].textContent = folderIs(entry.folderId, 'Junk') ? t('Not junk') : t('Junk'); }
    }
    renderContextTabs(m);
    menu.hidden = false;
    if (menu.style) {
      var w = window.innerWidth || 1200, h = window.innerHeight || 800;
      // At the pointer, opening away from it: to the right of it normally, to
      // the left of it when the page is written right to left.
      var rtl = document.documentElement && document.documentElement.getAttribute && document.documentElement.getAttribute('dir') === 'rtl';
      menu.style.left = Math.max(4, Math.min(rtl ? x - 240 : x, w - 240)) + 'px';
      menu.style.top = Math.max(4, Math.min(y, h - 320)) + 'px';
    }
  };
  var closeContextMenu = function () { el('context-menu').hidden = true; contextRow = null; };
  el('context-menu').addEventListener('click', function (event) {
    var act = event.target && event.target.getAttribute ? event.target.getAttribute('data-act') : '';
    var entry = contextRow;
    closeContextMenu();
    if (!act || !entry) { return; }
    var m = entry.m;
    if (act === 'open') { go('/m/' + entry.id); }
    else if (act === 'archive') { fileRow(entry, 'archive'); }
    else if (act === 'junk') { fileRow(entry, junkTargetFor(entry.folderId)); }
    else if (act === 'delete') { fileRow(entry, 'delete'); }
    else if (act === 'read') { markRows([entry], !m.flags.seen); }
    else if (act === 'star' && entry.star) { toggleStar(m, entry.star); }
    else if (act === 'pin') { pinMessages(entry.ids || [entry.id], !isPinned(m)); }
    else if (act === 'block') { blockSender(addressOf(m.from)); }
    else if (act === 'sweep') { sweepSender(addressOf(m.from), entry.folderId); }
    else if (act === 'mute') { muteThread(entry, !isMuted(m)); }
    else if (act === 'cleanup') { cleanUpConversation(entry.ms || [m]); }
    else if (act === 'tab') { moveSenderToTab(addressOf(m.from), event.target.getAttribute('data-tab')); }
  });
  document.addEventListener('click', function (event) {
    if (el('context-menu').hidden) { return; }
    if (!within(event.target, el('context-menu'))) { closeContextMenu(); }
  });
  document.addEventListener('keydown', function (event) { if (event.key === 'Escape' && !el('context-menu').hidden) { closeContextMenu(); } });

  // The theme the last visit chose, for the sign-in page; the account's own
  // choice replaces it once there is an account.
  applyTheme(storedTheme());
  pending = parseHash().hash;
  load(true);
})();
