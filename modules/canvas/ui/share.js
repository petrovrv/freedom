// SPDX-License-Identifier: AGPL-3.0-or-later
// Share from the canvas, the ui.page "share" of the module canvas at /canvas-share?id=<id> (Freedom F10; was
// co-web/src/CanvasShare.tsx of CO Web 0.68.0-0.101.0). The editor page of canvas-bridge puts the prepared file (docx
// without agent threads, or PDF) on the site and opens this page. The site makes a Gmail draft with this browser's own
// CO session (op canvas_share_draft, scope gmail.compose; asked once through the sign-in page) and the window moves to
// the draft. Nothing is ever sent.
// This page runs in an opaque-origin frame without sessionStorage: "Google was already asked for this share" is kept
// with coExt.storage (CO Web keeps it in this browser). The sign-in page accepts only back=/canvas-share?id=<id>.
(function () {
  'use strict';
  var card = document.getElementById('card');
  var ASKED = 'askedGmail'; // the share id Google was last asked for (coExt.storage)
  var BUSY_TRIES = 20;   // the same share open in another tab (the site holds a lock while it talks to Gmail)
  var LOST_TRIES = 4;    // the call timed out in CO Web: Gmail may still be working; asking again is safe (busy/draft.json)

  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined) e.textContent = text;
    return e;
  }

  function q(obj) {
    return Object.keys(obj).map(function (k) { return encodeURIComponent(k) + '=' + encodeURIComponent(String(obj[k])); }).join('&');
  }

  function show(phase, id) {
    card.textContent = '';
    card.appendChild(el('div', 'brand', 'CO'));
    if (phase.s === 'loading') card.appendChild(el('p', 'muted', 'Making the Gmail draft…'));
    if (phase.s === 'allow') {
      card.appendChild(el('p', 'muted', 'CO needs your permission to create Gmail drafts. It never sends mail: the draft waits in Gmail for you.'));
      var p = el('p');
      var b = el('button', 'btn primary', 'Allow Gmail drafts');
      b.type = 'button';
      b.addEventListener('click', function () { allow(id, phase.email); });
      p.appendChild(b);
      card.appendChild(p);
      card.appendChild(el('p', 'tiny muted', 'On the Google page, tick “Manage drafts and send emails”: CO uses it only to create the draft.'));
    }
    if (phase.s === 'error') card.appendChild(el('p', 'error-text', phase.message));
  }

  // First use: Google asks once whether CO may create Gmail drafts, then the sign-in page comes back here.
  function allow(id, email) {
    var go = function () {
      coExt.navigate('/auth/login.php?' + q({ add: 'gmail', hint: email, back: '/canvas-share?' + q({ id: id }) }), 'self');
    };
    coExt.storage.set(ASKED, id).then(go, go);
  }

  coExt.ready.then(function (ctx) {
    var params = (ctx && ctx.params) || {};
    var id = typeof params.id === 'string' ? params.id : '';
    var asked = false;
    coExt.title('Gmail draft · CO');
    show({ s: 'loading' }, id);
    var lost = 0;

    function fail(e) {
      show({ s: 'error', message: (e && e.message) || String(e) }, id);
    }

    function run(tries) {
      if (!/^[0-9a-f]{32}$/.test(id)) return fail(new Error('This share link is not valid.'));
      coExt.call('canvas_share_draft', { id: id }, { timeoutMs: 150000 }).then(function (d) {
        d = d || {};
        if (d.error === 'uncertain') {
          // an earlier try may have made the draft already: look in Drafts instead of making a second one
          coExt.navigate('https://mail.google.com/mail/?' + q({ authuser: String(d.email || '') }) + '#drafts', 'self');
          return;
        }
        if (d.error === 'busy' && tries < BUSY_TRIES) {
          setTimeout(function () { run(tries + 1); }, 1500);
          return;
        }
        if (d.error === 'need_gmail') {
          // back here after Google's page: if the box was left unticked, offer the button instead of a loop
          if (!asked) return allow(id, String(d.email || ''));
          show({ s: 'allow', email: String(d.email || '') }, id);
          return;
        }
        if (d.error === 'not_found') throw new Error('This share has expired. Press Share in the canvas again.');
        if (d.error === 'forbidden') throw new Error('This attachment was prepared for another CO account.');
        if (!d.draftId) throw new Error('Gmail did not take the draft (' + (d.error || 'no draft') + (d.status ? ' ' + d.status : '') + ').');
        coExt.storage.set(ASKED, null).catch(function () {});
        coExt.navigate('https://mail.google.com/mail/?' + q({ authuser: String(d.email || '') }) + '#drafts?compose='
          + encodeURIComponent(d.messageId || ''), 'self');
      }).catch(function (e) {
        // a call may time out while a large attachment is still on its way to Gmail; the site
        // answers a second call with busy (still working), the same draft (draft.json) or uncertain (Drafts)
        if (e && e.message === 'timeout' && lost < LOST_TRIES) {
          lost++;
          return run(tries);
        }
        fail(e);
      });
    }
    var start = function (v) { asked = v === id; run(0); };
    coExt.storage.get(ASKED).then(start, function () { start(null); });
  });
})();
