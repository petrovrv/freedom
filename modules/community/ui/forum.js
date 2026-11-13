// SPDX-License-Identifier: AGPL-3.0-or-later
// Forum page of the module community (ui.page "forum", Freedom F10): what the home PC put on the server with
// `python -m community page --push` - drafts waiting for the owner's yes, the inbox of the last tick, the digest and
// the social budget. Read only: the owner approves drafts and answers the digest in a chat with the agent
// (`python -m community approve ID`, digest quick replies), never from this frame. Foreign text is plain text here.
(function () {
  'use strict';
  var root = document.getElementById('forum');

  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined && text !== null) e.textContent = String(text);
    return e;
  }
  function when(ts) {
    if (!ts) return '';
    var d = new Date(ts * 1000);
    return d.toLocaleDateString() + ' ' + d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }
  function section(title, items, empty, row) {
    var frag = document.createDocumentFragment();
    frag.appendChild(el('h2', '', title + (items.length ? ' (' + items.length + ')' : '')));
    if (!items.length) { frag.appendChild(el('p', 'muted', empty)); return frag; }
    var ul = el('ul');
    items.forEach(function (it) { ul.appendChild(row(it)); });
    frag.appendChild(ul);
    return frag;
  }
  function render(v) {
    root.textContent = '';
    root.appendChild(el('h1', '', 'Freedom forum'));
    if (!v) {
      root.appendChild(el('p', 'muted', 'Nothing from the home PC yet. It puts this page after a tick: python -m community page --push.'));
      return;
    }
    var b = v.budget || {};
    root.appendChild(el('p', 'muted', 'Updated ' + when(v.updated) +
      (b.allowed ? ' · social budget: room for a tick' : (b.reason || b.error) ? ' · social budget: ' + (b.reason || b.error) : '')));
    root.appendChild(section('Drafts waiting for your yes', v.drafts || [], 'No drafts.', function (d) {
      var li = el('li');
      li.appendChild(el('span', 'kind', d.kind));
      li.appendChild(el('span', 'title', '#' + d.id + (d.note ? ' · ' + d.note : '')));
      li.appendChild(el('div', 'muted', when(d.created) + ' · approve in a chat: '));
      li.lastChild.appendChild(el('code', '', 'python -m community approve ' + d.id));
      return li;
    }));
    root.appendChild(section('Digest', v.digest || [], 'No new ideas this week.', function (i) {
      var li = el('li');
      li.appendChild(el('div', 'title', i.title));
      if (i.problem) li.appendChild(el('div', '', 'Problem: ' + i.problem));
      if (i.approach) li.appendChild(el('div', '', 'How: ' + i.approach));
      li.appendChild(el('div', 'muted', 'reputation ' + (i.reputation || 0) + ' · confirmed ' + (i.confirmations || 0) + (i.why ? ' · ' + i.why : '')));
      li.appendChild(el('div', 'card', i.card));
      return li;
    }));
    root.appendChild(section('Inbox of the last tick', v.inbox || [], 'Nothing new.', function (i) {
      var li = el('li');
      li.appendChild(el('span', 'kind', i.kind));
      li.appendChild(el('span', 'title', i.title || i.todo));
      if (i.title) li.appendChild(el('div', 'muted', i.todo));
      return li;
    }));
  }
  function load() {
    coExt.call('community_page', {}).then(function (j) { render(j && j.view); }, function (e) {
      root.textContent = '';
      root.appendChild(el('p', 'muted', 'Cannot read the forum page: ' + (e && e.message ? e.message : e)));
    });
  }
  coExt.ready.then(function () { load(); setInterval(load, 5 * 60000); });
})();
