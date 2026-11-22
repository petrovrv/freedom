// SPDX-License-Identifier: AGPL-3.0-or-later
// ext/1 client for module pages (Freedom F10, SPEC §3.6). A module page runs in a sandboxed iframe of CO Web without
// same-origin: it has no session and no network of its own. Everything goes through this bridge to CO Web
// (co-web/src/extBridge.ts), which checks every call against the module's manifest before it calls db.php:
//   coExt.ready             -> Promise of the context {ext, module, version, slot, me, members, owner, vw, theme, person}
//   coExt.call(op, args[, {timeoutMs}]) -> Promise of the result of a user op the module declared (manifest
//                             server.ops.user) or of a core call "core.<point>.<call>" whose permission it declared
//                             (default timeout 30 s, at most 10 min)
//                             or of a device op it declared (provides.deviceOps) from its ui.settings page, answered by
//                             the app the page is shown in (CO Desktop, CO Resident; elsewhere device_op_unavailable)
//   coExt.size(w, h, overlay) - ask for the frame size; overlay = draw over the page (an open popover)
//   coExt.on(type, fn)      - 'context' (theme, viewport width or the person's switch changed), 'blur' (a click outside),
//                             'composer' (ui.hint pages with composer.read: the text being typed, debounced)
//   coExt.hint(h)           - ui.hint: {text, title, picks: [{agent, model, label}]} or null; CO Web shows the first
//                             pick that this host offers next to the model picker and never applies it by itself
//   coExt.routeUrl(route, q) - URL of a route of this module (agent/module.php?m=<id>&r=<route>&...)
// With permissions (manifest), in the ui.composer slot (a page in the action row of the message box):
//   coExt.input(text, {interim, done}), coExt.focusInput()      composer.input - text into the message box
//   coExt.audio.start(maxSeconds) / stop() / cancel()           media.audio - CO Web records the microphone
//   coExt.speech.start(lang) / stop() / platform()              media.speech - browser speech service, events 'speech'
//   coExt.audio.start(maxSeconds, {silenceMs})                  media.audio - stops by itself after silenceMs of quiet
//                                                               (event 'audio' {silence: true}; then stop())
//   coExt.send(text)                                            composer.send - the text goes out as the chat's next
//                                                               message (agent and model of the composer)
//   coExt.play(bytes, type) / coExt.play(null)                  media.play - CO Web plays an audio answer (ArrayBuffer,
//                                                               audio/*); resolves {ended} when it ends or is stopped
// A composer page with chat.read gets 'chat' {events, canAsk} like a viewer panel (voice mode waits for the answer).
// Any slot: coExt.upload(name, blob, filename, fields) (manifest server.uploads), coExt.storage.get/set(key, value).
// ctx.media in the composer: {audio, speech, platform} - what this browser can do. Events: 'audio', 'speech', 'sent'
// (the message went out: speech recognition stopped).
//   coExt.req(name, args, transfer) -> Promise of a core service (file.get, file.pdf, file.sibling, file.putSibling,
//                           chat.ask, chat.reveal, panel.close; modules/EXT1.md); binary data as ArrayBuffer
//   coExt.navigate(url, target) - the whole window ('self') or a new one ('blank') to a target of manifest navigate
//   coExt.title(text)       - window title of a full-window page (ui.page)
//   coExt.badge(v)          - the module's badge in the shell (count on its app tile, dot on ▦); 0/''/null clears
//   coExt.on('chat', fn)    - a viewer panel: {events, canAsk} of its chat (permission chat.read)
(function () {
  'use strict';
  var parentWin = window.parent;
  var port = null; // MessagePort from CO Web's init; everything after the hello goes over it
  var queue = [];
  var seq = 0;
  var pending = {};
  var handlers = {};
  var ctx = null;
  var readyResolve;
  var ready = new Promise(function (res) { readyResolve = res; });

  function post(msg, transfer) {
    msg.ext = 1;
    if (port) port.postMessage(msg, transfer || []);
    else queue.push([msg, transfer || []]);
  }


  function applyTheme(theme) {
    if (!theme) return;
    var root = document.documentElement;
    Object.keys(theme).forEach(function (k) {
      if (/^--[a-z0-9-]+$/.test(k)) root.style.setProperty(k, String(theme[k]));
      else if (k === 'font') root.style.fontFamily = String(theme[k]);
      else if (k === 'scheme') root.style.colorScheme = String(theme[k]);
    });
  }

  function emit(type, data) {
    (handlers[type] || []).forEach(function (fn) {
      try { fn(data); } catch (e) { console.error(e); }
    });
  }

  function onMessage(m) {
    if (!m || m.ext !== 1 || typeof m.t !== 'string') return;
    if (m.t === 'context') {
      ctx = Object.assign(ctx || {}, m.ctx || {});
      applyTheme(ctx.theme);
      emit('context', ctx);
    } else if (m.t === 'result' && pending[m.id]) {
      var p = pending[m.id];
      delete pending[m.id];
      if (m.ok) p.res(m.data);
      else p.rej(new Error(m.error || 'call failed'));
    } else if (m.t === 'blur') {
      emit('blur', null);
    } else if (m.t === 'composer') {
      emit('composer', typeof m.text === 'string' ? m.text : '');
    } else if (m.t === 'event' && /^[a-z]{1,20}$/.test(String(m.type))) {
      emit(m.type, m.data);
    } else if (m.t === 'chat') {
      emit('chat', { events: m.events || [], canAsk: !!m.canAsk });
    }
  }

  // a request with an answer (calls, capabilities): {t, id, ...} -> Promise of the data
  function request(msg, timeoutMs, transfer) {
    return new Promise(function (res, rej) {
      var id = 'r' + (++seq);
      pending[id] = { res: res, rej: rej };
      msg.id = id;
      post(msg, transfer);
      if (timeoutMs) {
        setTimeout(function () {
          if (pending[id]) {
            delete pending[id];
            rej(new Error('timeout'));
          }
        }, timeoutMs);
      }
    });
  }

  // only the init comes by window message: it carries the port of this page's channel
  window.addEventListener('message', function (e) {
    if (port || e.source !== parentWin || !e.ports || !e.ports[0]) return;
    var m = e.data;
    if (!m || m.ext !== 1 || m.t !== 'init') return;
    port = e.ports[0];
    port.onmessage = function (ev) { onMessage(ev.data); };
    ctx = Object.assign({}, m.ctx || {});
    applyTheme(ctx.theme);
    readyResolve(ctx);
    queue.splice(0).forEach(function (q) { port.postMessage(q[0], q[1]); });
  });

  window.coExt = {
    ready: ready,
    call: function (op, args, opts) {
      var ms = Math.min(600000, Math.max(1000, Number(opts && opts.timeoutMs) || 30000));
      // CO Web waits as long for the site (a slow op, e.g. a large upload to another service)
      return request({ t: 'call', op: String(op), args: args || {}, timeoutMs: ms }, ms);
    },
    // a core service (modules/EXT1.md): file.get|pdf|sibling|putSibling, chat.ask|reveal, panel.close; ArrayBuffers
    // in transfer (file conversions by Google Drive take a while)
    req: function (name, args, transfer) {
      return request({ t: 'req', name: String(name), args: args || {} }, 180000, transfer || []);
    },
    // the whole window ('self') or a new one ('blank') to a target of manifest navigate (permission ui.navigate)
    navigate: function (url, target) {
      post({ t: 'navigate', url: String(url), target: target === 'blank' ? 'blank' : 'self' });
    },
    // window title of a full-window page (ui.page)
    title: function (text) {
      post({ t: 'title', text: String(text).slice(0, 200) });
    },
    size: function (w, h, overlay) {
      post({ t: 'size', w: Math.ceil(w), h: Math.ceil(h), overlay: !!overlay });
    },
    // the module's badge in the shell (any slot): a count or up to 3 characters on its app tile and a dot on ▦;
    // 0, '' or null clears it (usually set by the tray page, which stays loaded)
    badge: function (v) {
      post({ t: 'badge', value: v == null ? '' : String(v).slice(0, 3) });
    },
    hint: function (h) {
      post({ t: 'hint', hint: h || null });
    },
    on: function (type, fn) {
      (handlers[type] = handlers[type] || []).push(fn);
    },
    // composer slot (permission composer.input): the text of the current input session, an interim tail, done = end
    input: function (text, opts) {
      opts = opts || {};
      post({ t: 'input', text: String(text || ''), interim: String(opts.interim || ''), done: !!opts.done });
    },
    focusInput: function () { post({ t: 'input', focus: true }); },
    // microphone recorded by CO Web (permission media.audio); stop -> {file: Blob, filename, type} or null; events 'audio'
    audio: {
      start: function (maxSeconds, opts) {
        return request({ t: 'audio', action: 'start', maxSeconds: maxSeconds || 300, silenceMs: Number(opts && opts.silenceMs) || 0 }, 60000);
      },
      stop: function () { return request({ t: 'audio', action: 'stop' }, 60000); },
      cancel: function () { return request({ t: 'audio', action: 'cancel' }, 10000); },
    },
    // speech recognition of the browser in CO Web (permission media.speech); results as events 'speech'
    // {final, interim, active, error?}; platform() = the platform's own voice typing into the message box
    speech: {
      start: function (lang) { return request({ t: 'speech', action: 'start', lang: lang || '' }, 10000); },
      stop: function () { return request({ t: 'speech', action: 'stop' }, 10000); },
      platform: function () { return request({ t: 'speech', action: 'platform' }, 0); },
    },
    // the chat's next message (permission composer.send); refused while the chat is busy
    send: function (text) { return request({ t: 'send', text: String(text || '') }, 120000); },
    // an audio answer played by CO Web (permission media.play); null stops it
    play: function (bytes, type) {
      if (!bytes) return request({ t: 'play', action: 'stop' }, 10000);
      return request({ t: 'play', action: 'play', bytes: bytes, type: String(type || 'audio/mpeg') }, 600000);
    },
    // POST of a file to an upload the module declared (manifest server.uploads); the handler's JSON comes back
    upload: function (name, file, filename, fields) {
      return request({ t: 'upload', name: String(name), file: file, filename: filename || 'file', fields: fields || {} }, 180000);
    },
    // small values of this module kept in this browser
    storage: {
      get: function (key) { return request({ t: 'storage', action: 'get', key: String(key) }, 10000); },
      set: function (key, value) { return request({ t: 'storage', action: 'set', key: String(key), value: value === null ? null : String(value) }, 10000); },
    },
    routeUrl: function (route, q) {
      var s = '/agent/module.php?m=' + encodeURIComponent(ctx ? ctx.module : '') + '&r=' + encodeURIComponent(route);
      Object.keys(q || {}).forEach(function (k) { s += '&' + encodeURIComponent(k) + '=' + encodeURIComponent(String(q[k])); });
      return s;
    },
  };
  // the parent is CO Web; this page has an opaque origin and sends nothing secret by '*'
  parentWin.postMessage({ ext: 1, t: 'hello' }, '*');
})();
