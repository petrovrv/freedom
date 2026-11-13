// SPDX-License-Identifier: AGPL-3.0-or-later
// Canvas launcher, the ui.page "editor" of the module canvas at /canvas?path=<file relative to AI-Workbench> (Freedom
// F10; was co-web/src/Canvas.tsx of CO Web 0.63.0-0.101.0, behaviour unchanged). The site checks the role and signs a
// 120 s ticket (op canvas_ticket), then the window moves to the editor page of canvas-bridge on the home PC
// (<ts.net>/cb/edit#<ticket>), where ONLYOFFICE opens the file and saves edits back to Drive. The editor is not
// embedded: on tailnet devices ts.net is a local address and Chrome blocks the CO site from reaching it (Local Network
// Access); a top-level navigation is allowed (coExt.navigate 'self', manifest navigate {config: publicUrl}).
(function () {
  'use strict';
  var card = document.getElementById('card');

  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined) e.textContent = text;
    return e;
  }

  // A legacy artifact copy (projects/<p>/chats/<c>/[branches/<b>/]artifacts/<job>/x) opens its live scratch file
  // (canvasPath of co-web/src/FileMenu.tsx).
  function canvasPath(p) {
    var m = /^(projects\/[^/]+\/chats\/[^/]+)\/(?:branches\/[^/]+\/)?artifacts\/[^/]+\/(.+)$/.exec(p);
    return m ? m[1] + '/scratch/' + m[2] : p;
  }

  function show(path, name, phase) {
    card.textContent = '';
    card.appendChild(el('div', 'brand', 'CO'));
    var n = el('p', 'muted name', name);
    n.title = path;
    card.appendChild(n);
    if (phase.s === 'loading') {
      card.appendChild(el('p', 'muted', 'Opening the canvas…'));
      return;
    }
    card.appendChild(el('p', phase.offline ? 'muted' : 'error-text', phase.message));
    if (phase.offline) {
      var p = el('p');
      var b = el('button', 'btn', 'Try anyway');
      b.type = 'button';
      b.addEventListener('click', function () { coExt.navigate(phase.offline, 'self'); });
      p.appendChild(b);
      card.appendChild(p);
      card.appendChild(el('p', 'tiny muted', 'Or use “Open in Google Docs” from the file menu.'));
    }
  }

  coExt.ready.then(function (ctx) {
    var params = (ctx && ctx.params) || {};
    var path = canvasPath(typeof params.path === 'string' ? params.path : '');
    var name = path.split('/').pop() || 'Canvas';
    coExt.title(name + ' · CO canvas');
    show(path, name, { s: 'loading' });
    if (!path) return show(path, name, { s: 'error', message: 'No file given.' });
    coExt.call('canvas_ticket', { path: path }).then(function (d) {
      d = d || {};
      // a module op answers 200; the old HTTP statuses of db.php are the error codes of the body
      if (d.error === 'forbidden') throw new Error('You have no access to this file.');
      if (d.error === 'not_found') throw new Error('File not found.');
      if (!d.ticket) throw new Error('The site refused the canvas (' + (d.error || 'no ticket') + ').');
      var target = d.editUrl + '#' + d.ticket;
      if (!d.online) {
        // The site's probe may fail while this device still reaches the PC (tailnet): offer to try anyway.
        show(path, name, { s: 'error', offline: target, message: 'Canvas works when the home computer is on. The site cannot reach it now.' });
        return;
      }
      coExt.navigate(target, 'self');
    }).catch(function (e) {
      show(path, name, { s: 'error', message: (e && e.message) || String(e) });
    });
  });
})();
