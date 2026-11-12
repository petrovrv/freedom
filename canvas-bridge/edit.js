// SPDX-License-Identifier: AGPL-3.0-or-later
// Canvas editor page served by canvas-bridge at <ts.net>/cb/edit#<ticket> (same origin as ONLYOFFICE, so browsers on
// tailnet devices do not block it as a local-network request from the CO site). CO Web gets the ticket from the site
// (db.php op canvas_ticket) and navigates here; the ticket lives 120 s and is removed from the address bar at once.
(function () {
  'use strict';
  var msg = document.getElementById('msg');
  var ticket = location.hash.slice(1);
  history.replaceState(null, '', location.pathname);
  document.getElementById('close').onclick = function () { window.close(); };

  function show(text, isError) {
    msg.textContent = text;
    msg.className = isError ? 'error' : '';
    msg.style.display = '';
  }

  function loadScript(src) {
    return new Promise(function (resolve, reject) {
      var el = document.createElement('script');
      el.src = src;
      el.onload = resolve;
      el.onerror = function () { reject(new Error('The editor did not load (ONLYOFFICE on the home computer is not answering).')); };
      document.head.appendChild(el);
    });
  }

  function status(text, isError) {
    var el = document.getElementById('status');
    el.textContent = text;
    el.className = isError ? 'error' : '';
  }

  function markdownButton(sid) {
    var btn = document.getElementById('md');
    btn.hidden = false;
    function save(mode) {
      return fetch('notebook/' + encodeURIComponent(sid) + '/markdown', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ mode: mode }), cache: 'no-store'
      }).then(function (res) {
        return res.json().catch(function () { return {}; }).then(function (d) {
          if (!res.ok) throw new Error(d.error || ('error ' + res.status));
          return d;
        });
      });
    }
    btn.onclick = function () {
      btn.disabled = true;
      status('Saving as Markdown…');
      save('').then(function (d) {
        if (!d.exists) return d;
        // An existing .md is never replaced silently.
        if (window.confirm(d.exists + ' already exists and differs. Replace it? (Its old text stays in the Drive version history.)')) return save('replace');
        if (window.confirm('Save as a separate copy instead?')) return save('copy');
        return { cancelled: true };
      }).then(function (d) {
        status(d.cancelled ? '' : d.unchanged ? d.name + ' is up to date' : 'Saved ' + d.name);
      }).catch(function (e) {
        status('Markdown not saved: ' + e.message, true);
      }).then(function () { btn.disabled = false; });
    };
  }

  function post(url, body) {
    return fetch(url, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body || {}), cache: 'no-store'
    }).then(function (res) {
      return res.json().catch(function () { return {}; }).then(function (d) {
        if (!res.ok) throw new Error(d.error || ('error ' + res.status));
        return d;
      });
    });
  }

  // M5: Share (Gmail draft on the CO site, with the user's own Google account) and Clean copy (no agent threads).
  function shareButtons(sid, fileType, edit) {
    var base = 'doc/' + encodeURIComponent(sid) + '/';
    var menu = document.getElementById('shareMenu');
    var btn = document.getElementById('share');
    document.getElementById('shareOrig').textContent = 'Gmail draft · ' + fileType.toUpperCase();
    btn.onclick = function (e) { e.stopPropagation(); menu.hidden = !menu.hidden; };
    document.addEventListener('click', function () { menu.hidden = true; });
    Array.prototype.forEach.call(menu.querySelectorAll('button'), function (item) {
      item.onclick = function () {
        menu.hidden = true;
        // the tab opens now, inside the click (a window opened after the request would be blocked as a popup)
        var w = window.open('', '_blank');
        if (w) {
          w.opener = null;
          w.document.title = 'CO · Gmail draft';
          w.document.body.textContent = 'Preparing the attachment…';
        }
        btn.disabled = true;
        status(item.dataset.format === 'pdf' ? 'Making the PDF…' : 'Preparing the attachment…');
        post(base + 'share', { format: item.dataset.format }).then(function (d) {
          if (d.error === 'too_large') throw new Error('the file is over 25 MB, too large for an e-mail; share it from Drive instead');
          status('Opening the Gmail draft' + (d.removed ? ' (' + d.removed + ' agent comments left out)' : '') + '…');
          if (w) w.location.href = d.url; else window.open(d.url, '_blank', 'noopener');
          setTimeout(function () { status(''); }, 8000);
        }).catch(function (e) {
          if (w) w.close();
          status('Not shared: ' + e.message, true);
        }).then(function () { btn.disabled = false; });
      };
    });
    if (!edit || fileType !== 'docx') return;
    var clean = document.getElementById('clean');
    clean.hidden = false;
    clean.onclick = function () {
      clean.disabled = true;
      status('Making a clean copy…');
      post(base + 'clean').then(function (d) {
        status('Saved ' + d.name + (d.removed ? ' (' + d.removed + ' agent comments left out)' : ' (no agent comments)'));
      }).catch(function (e) {
        status('Clean copy not saved: ' + e.message, true);
      }).then(function () { clean.disabled = false; });
    };
  }

  if (!ticket) {
    show('This link has expired. Open the file again from CO (file menu → Open in canvas).', true);
    return;
  }
  fetch('open', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ ticket: ticket }), cache: 'no-store' })
    .then(function (res) {
      return res.json().catch(function () { return {}; }).then(function (data) {
        if (res.status === 403) throw new Error('This link has expired. Open the file again from CO (file menu → Open in canvas).');
        if (res.status === 404) throw new Error('The file is not on the home computer yet (Drive may still be syncing). Try again in a minute.');
        if (!res.ok || !data.config) throw new Error('The canvas service refused the file (' + (data.error || res.status) + ').');
        return data;
      });
    })
    .then(function (data) {
      var cfg = data.config;
      document.title = cfg.document.title + ' · CO canvas';
      document.getElementById('title').textContent = cfg.document.title;
      document.getElementById('mode').textContent = cfg.editorConfig.mode === 'edit' ? '' : 'view only';
      // M4: a text file opens as a notebook, the docx next to it; "Save as Markdown" writes it back as .md.
      if (data.notebook) document.getElementById('nb').textContent = 'notebook of ' + data.notebook.source;
      if (cfg.editorConfig.mode === 'edit' && cfg.document.fileType === 'docx') {
        markdownButton(cfg.editorConfig.callbackUrl.split('/').pop());
      }
      shareButtons(cfg.editorConfig.callbackUrl.split('/').pop(), cfg.document.fileType, cfg.editorConfig.mode === 'edit');
      return loadScript(data.api).then(function () {
        cfg.width = '100%';
        cfg.height = '100%';
        cfg.type = 'desktop';
        msg.style.display = 'none';
        new window.DocsAPI.DocEditor('editor', cfg);
      });
    })
    .catch(function (e) { show(e.message, true); });
})();
