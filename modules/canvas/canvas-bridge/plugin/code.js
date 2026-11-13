// SPDX-License-Identifier: AGPL-3.0-or-later
// CO Agent: background ONLYOFFICE plugin of the CO canvas (stage M3, SPEC.md in chat CHAT-20261028152233-8P34).
// A comment thread whose first message starts with @agent (or made with the "Ask agent" button) goes to the chat's agent
// 20 s after its last change ("Send now" and closing the editor send at once). canvas-bridge runs the job; the answer
// comes back here: the ops are applied as tracked changes and the reply is added to the thread as "CO Agent".
(function (window) {
  'use strict';
  var P = window.Asc.plugin;
  var AGENT = 'CO Agent';
  var QUIET_MS = 20000;
  var TICK_MS = 2000;
  var POLL_MS = 4000;
  var MARK = /^\s*@agent\b/i;

  var base = '';           // <ts.net>/cb/agent/<sid>
  var threads = {};        // comment id -> {data, changed}
  var sent = {};           // tkey -> sig asked already
  var ids = {};            // tkey -> comment id in this editor session
  var status = {};         // tkey -> queued | running | done | error (from canvas-bridge)
  var busy = false;        // applying an answer (no polling meanwhile)
  var asking = false;

  function log() { try { console.log.apply(console, ['COAGENT'].concat([].slice.call(arguments))); } catch (e) { /* ignore */ } }

  // ---------- helpers ----------

  function hash(s) {
    // FNV-1a 32 bit twice (different seeds) -> 16 hex chars: stable across sessions, comment ids are not
    var h1 = 0x811c9dc5, h2 = 0x01000193 ^ 0x5bd1e995;
    for (var i = 0; i < s.length; i++) {
      var c = s.charCodeAt(i);
      h1 = Math.imul(h1 ^ c, 0x01000193) >>> 0;
      h2 = Math.imul(h2 ^ c, 0x5bd1e995) >>> 0;
    }
    return ('0000000' + h1.toString(16)).slice(-8) + ('0000000' + h2.toString(16)).slice(-8);
  }

  function messages(d) {
    var list = [{ author: d.UserName || '', text: d.Text || '' }];
    (d.Replies || []).forEach(function (r) { list.push({ author: r.UserName || '', text: r.Text || '' }); });
    return list;
  }

  // The thread key survives a reload and edits of the anchored text (the agent's own ops change QuoteText): author and
  // text of the first message, and of the first reply when the first message is only the @agent mark ("Ask agent").
  // The creation second tells apart two comments with the same text (the docx keeps it to the second).
  function tkeyOf(d) {
    var k = (d.UserName || '') + '\u0001' + (d.Text || '') + '\u0001' + Math.floor((+d.Time || 0) / 1000);
    var r = (d.Replies || [])[0];
    if (r && !(d.Text || '').replace(MARK, '').trim()) k += '\u0001' + (r.UserName || '') + '\u0001' + (r.Text || '');
    return hash(k);
  }
  function sigOf(d) {
    var m = messages(d);
    return hash(m.length + '\u0001' + m[m.length - 1].author + '\u0001' + m[m.length - 1].text);
  }

  function isAgentThread(d) { return !!d && MARK.test(d.Text || ''); }

  // Waits for the agent: not resolved, the last word is not the agent's, and there is a question to answer.
  function needsAnswer(d) {
    if (!isAgentThread(d) || d.Solved) return false;
    var m = messages(d);
    var last = m[m.length - 1];
    if (last.author === AGENT) return false;
    return m.some(function (x) { return x.author !== AGENT && x.text.replace(MARK, '').trim() !== ''; });
  }

  function exec(name, args) {
    return new Promise(function (resolve) { P.executeMethod(name, args, function (r) { resolve(r); }); });
  }

  function cmd(fn, scope, recalc) {
    return new Promise(function (resolve) {
      window.Asc.scope = scope || {};
      P.callCommand(fn, false, recalc !== false, function (r) { resolve(r); });
    });
  }

  function http(method, action, body) {
    return fetch(base + '/' + action, {
      method: method, cache: 'no-store',
      headers: body ? { 'Content-Type': 'application/json' } : {},
      body: body ? JSON.stringify(body) : undefined,
    }).then(function (r) { return r.json().then(function (j) { if (!r.ok) throw new Error(j.error || r.status); return j; }); });
  }

  // ---------- document text for the agent ----------

  function docText() {
    return cmd(function () {
      var out = [];
      function para(p) {
        var st = p.GetStyle && p.GetStyle();
        var name = st ? st.GetName() : '';
        var m = /^heading (\d)$/i.exec(name);
        var t = (p.GetText() || '').replace(/[\r\n]+$/, '');
        return (m ? new Array(+m[1] + 1).join('#') + ' ' : '') + t;
      }
      function content(c) {
        for (var i = 0; i < c.GetElementsCount(); i++) {
          var e = c.GetElement(i), type = e.GetClassType();
          if (type === 'paragraph') out.push(para(e));
          else if (type === 'table') {
            for (var r = 0; r < e.GetRowsCount(); r++) {
              var row = e.GetRow(r), cells = [];
              for (var k = 0; k < row.GetCellsCount(); k++) {
                var cc = row.GetCell(k).GetContent(), parts = [];
                for (var j = 0; j < cc.GetElementsCount(); j++) {
                  var x = cc.GetElement(j);
                  if (x.GetClassType() === 'paragraph') parts.push((x.GetText() || '').replace(/[\r\n]+$/, ''));
                }
                cells.push(parts.join(' / '));
              }
              out.push('| ' + cells.join(' | ') + ' |');
            }
          }
        }
      }
      content(Api.GetDocument());
      return out.join('\n');
    }, {}, false);
  }

  // ---------- asking ----------

  function due(force) {
    var now = Date.now(), list = [];
    Object.keys(threads).forEach(function (id) {
      var t = threads[id], d = t.data;
      if (!needsAnswer(d)) return;
      var key = tkeyOf(d), sig = sigOf(d);
      if (sent[key] === sig) return;
      if (!force && now - t.changed < QUIET_MS) return;
      list.push({ id: id, tkey: key, sig: sig, quote: d.QuoteText || '', messages: messages(d) });
    });
    return list;
  }

  function send(force, beacon) {
    if (!base || asking) return;
    var list = due(force);
    if (!list.length) return;
    list.forEach(function (t) { sent[t.tkey] = t.sig; ids[t.tkey] = t.id; });
    var payload = function (doc) {
      return { threads: list.map(function (t) { return { tkey: t.tkey, sig: t.sig, quote: t.quote, messages: t.messages }; }), doc: doc };
    };
    if (beacon) {
      // the editor is closing: no time for the document text, the agent reads the file (saved on close)
      navigator.sendBeacon(base + '/ask', new Blob([JSON.stringify(payload(''))], { type: 'application/json' }));
      return;
    }
    asking = true;
    docText()
      .then(function (doc) { return http('POST', 'ask', payload(doc || '')); })
      .then(function (r) { log('asked', JSON.stringify(r)); })
      .catch(function (e) {
        log('ask failed', e && e.message);
        list.forEach(function (t) { delete sent[t.tkey]; });
      })
      .then(function () { asking = false; });
  }

  // ---------- answers ----------

  function applyOps(ops) {
    if (!ops || !ops.length) return Promise.resolve([]);
    var results = [];
    var wasTrack = null;
    var chain = cmd(function () {
      var d = Api.GetDocument();
      var was = d.IsTrackRevisions();
      d.SetTrackRevisions(true);
      return was;
    }, {}, false).then(function (w) { wasTrack = w; });
    ops.forEach(function (op) {
      chain = chain.then(function () {
        if (op.op === 'replace' || op.op === 'delete') {
          // delete = replace with nothing: ApiRange.Delete drops comments anchored in the range, even when tracked
          return cmd(function () {
            var rs = Api.GetDocument().Search(Asc.scope.find, true);
            var r = rs[(Asc.scope.n || 1) - 1];
            if (!r) return 'not_found';
            r.Select();
            return 'ok';
          }, { find: op.find, n: op.n }, false).then(function (s) {
            if (s !== 'ok') return s;
            return cmd(function () { return Api.ReplaceTextSmart([Asc.scope.text]) ? 'ok' : 'failed'; }, { text: op.op === 'delete' || op['with'] == null ? '' : String(op['with']) });
          });
        }
        if (op.op === 'insert_after') {
          return cmd(function () {
            var d = Api.GetDocument();
            var rs = d.Search(Asc.scope.find, true);
            var r = rs[(Asc.scope.n || 1) - 1];
            if (!r) return 'not_found';
            var anchor = r.GetAllParagraphs()[0];
            var st = Asc.scope.style ? d.GetStyle(Asc.scope.style) : null;
            var list = Asc.scope.paragraphs;
            for (var i = list.length - 1; i >= 0; i--) {
              var p = Api.CreateParagraph();
              p.AddText(String(list[i]));
              if (st) p.SetStyle(st);
              anchor.InsertParagraph(p, 'after', true);
            }
            return 'ok';
          }, { find: op.find, n: op.n, style: op.style || null, paragraphs: Array.isArray(op.paragraphs) ? op.paragraphs : [String(op.text || '')] });
        }
        return 'unknown_op';
      }).then(function (s) { results.push(s); }, function (e) { results.push('error: ' + (e && e.message)); });
    });
    return chain.then(function () {
      return cmd(function () { Api.GetDocument().SetTrackRevisions(Asc.scope.was); return 1; }, { was: !!wasTrack }, false);
    }).then(function () { return results; });
  }

  function addReply(tkey, text, question) {
    return exec('GetAllComments', null).then(function (all) {
      all = all || [];
      var c = all.filter(function (x) { return x.Id === ids[tkey]; })[0] || all.filter(function (x) { return tkeyOf(x.Data) === tkey; })[0];
      if (!c) {
        // the thread is gone (deleted, or its anchor removed): the answer becomes a comment on the first paragraph
        return cmd(function () { Api.GetDocument().GetElement(0).GetRange().Select(); return 1; }, {}, false).then(function () {
          return exec('AddComment', [{ UserName: AGENT, Text: 'Re: «' + String(question || '').slice(0, 200) + '»\n' + text, Time: String(Date.now()), Solved: false, Replies: [] }]);
        }).then(function () { return false; });
      }
      var reply = { UserName: AGENT, UserId: 'co-agent', Text: text, Time: String(Date.now()), Solved: false };
      var data = Object.assign({}, c.Data, { Replies: (c.Data.Replies || []).concat([reply]) });
      return exec('ChangeComment', [c.Id, data]).then(function () {
        threads[c.Id] = { data: data, changed: Date.now() };
        sent[tkey] = sigOf(data);
        return true;
      });
    });
  }

  // Applied here already (only the confirmation failed), or by another editor that crashed before confirming: the
  // thread then already has this reply of the agent, so the ops are not applied twice.
  var done = {};           // aid/tkey -> result sent with /applied
  function alreadyApplied(ans) {
    if (done[ans.aid + '/' + ans.tkey]) return Promise.resolve(done[ans.aid + '/' + ans.tkey]);
    if (!ans.reply) return Promise.resolve(null);
    return exec('GetAllComments', null).then(function (all) {
      var c = (all || []).filter(function (x) { return x.Id === ids[ans.tkey] || tkeyOf(x.Data) === ans.tkey; })[0];
      var hit = c && (c.Data.Replies || []).some(function (r) { return r.UserName === AGENT && (r.Text || '').indexOf(ans.reply) === 0; });
      return hit ? ['already_applied'] : null;
    });
  }

  function handle(ans) {
    return alreadyApplied(ans).then(function (prev) {
      if (prev) return http('POST', 'applied', { aid: ans.aid, tkey: ans.tkey, result: prev });
      return applyAnswer(ans);
    });
  }

  function applyAnswer(ans) {
    var notes = [];
    return applyOps(ans.ops).then(function (res) {
      res.forEach(function (s, i) {
        if (s !== 'ok') notes.push('⚠ ' + ans.ops[i].op + ' not applied (' + s + '): «' + String(ans.ops[i].find).slice(0, 80) + '»');
      });
      var text = (ans.reply || '') + (notes.length ? '\n' + notes.join('\n') : '');
      return addReply(ans.tkey, text || '(no reply)', ans.question).then(function (ok) {
        var result = res.concat(ok ? [] : ['thread_not_found']);
        done[ans.aid + '/' + ans.tkey] = result;
        return http('POST', 'applied', { aid: ans.aid, tkey: ans.tkey, result: result });
      });
    });
  }

  function poll() {
    if (!base || busy) return;
    http('GET', 'poll').then(function (r) {
      status = r.status || {};
      var list = r.answers || [];
      if (!list.length) return;
      busy = true;
      var chain = Promise.resolve();
      list.forEach(function (a) { chain = chain.then(function () { return handle(a); }).catch(function (e) { log('apply failed', e && e.message); }); });
      return chain.then(function () { busy = false; });
    }).catch(function (e) { busy = false; log('poll failed', e && e.message); });
  }

  // ---------- UI: toolbar tab and context menu ----------

  function askAgent() {
    // a thread "@agent" on the selection; the user writes the request as its reply (or edits the comment)
    exec('AddComment', [{ UserName: P.info.userName, Text: '@agent ', Time: String(Date.now()), Solved: false, Replies: [] }]);
  }

  function addUi() {
    try {
      P.executeMethod('AddToolbarMenuItem', [{
        guid: P.info.guid,
        tabs: [{
          id: 'co-agent-tab', type: 'tab', text: 'CO Agent',
          items: [
            { id: 'co-agent-ask', type: 'button', text: 'Ask agent', hint: 'Comment on the selection for the chat agent (@agent); write the request in it', icons: 'icon.png', lockInViewMode: true },
            { id: 'co-agent-send', type: 'button', text: 'Send now', hint: 'Send threads for the agent without waiting 20 s', icons: 'icon.png', lockInViewMode: true },
          ],
        }],
      }]);
      P.attachToolbarMenuClickEvent('co-agent-ask', askAgent);
      P.attachToolbarMenuClickEvent('co-agent-send', function () { send(true); });
    } catch (e) { log('toolbar', e && e.message); }
  }

  P.event_onContextMenuShow = function (opt) {
    if (!opt || (opt.type !== 'Selection' && opt.type !== 'Target')) return;
    P.executeMethod('AddContextMenuItem', [{ guid: P.info.guid, items: [{ id: 'co-agent-ctx', text: 'Ask CO Agent' }] }]);
  };
  P.attachContextMenuClickEvent && P.attachContextMenuClickEvent('co-agent-ctx', askAgent);

  // ---------- comment events ----------

  function track(c) {
    if (!c || !c.Id || !c.Data) return;
    threads[c.Id] = { data: c.Data, changed: Date.now() };
  }
  P.event_onAddComment = track;
  P.event_onChangeCommentData = track;
  P.event_onRemoveComment = function (c) { if (c && c.Id) delete threads[c.Id]; };

  P.init = function () {
    // info.documentCallbackUrl is not in the plugin docs, but ONLYOFFICE 10 passes editorConfig.callbackUrl in it
    // (checked live 31.10: info keys include documentCallbackUrl, documentId, userId); its last part is our session id.
    var m = /\/save\/([A-Za-z0-9_-]+)$/.exec(P.info.documentCallbackUrl || '');
    if (!m || P.info.isViewMode) { log('inactive (view mode or no session)'); return; }
    base = P.info.documentCallbackUrl.replace(/\/save\/[A-Za-z0-9_-]+$/, '/agent/' + m[1]);
    addUi();
    // threads left from earlier sessions: due at once (their quiet time has passed)
    exec('GetAllComments', null).then(function (all) {
      (all || []).forEach(function (c) { threads[c.Id] = { data: c.Data, changed: 0 }; });
    });
    setInterval(function () { send(false); }, TICK_MS);
    setInterval(poll, POLL_MS);
    poll();
    window.addEventListener('pagehide', function () { send(true, true); });
    window.__coReady = true;
    log('ready');
  };
  P.button = function () {};
})(window);
