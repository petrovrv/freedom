// SPDX-License-Identifier: AGPL-3.0-or-later
// Voice mode in the CO Web composer (module voice, SPEC §3.8): the page of the ui.composer slot.
//   🗣 Talk: CO Web records until the speaker goes quiet (coExt.audio, silenceMs), the upload 'turn' of this module
//   sends it to the voice hub of the family server: speech to text, then Home Assistant when it is a home command (its
//   answer comes back as audio and is played at once). Anything else goes into this chat as the next message
//   ("🎙 <text>", coExt.send); when the agent's answer arrives (chat.read), op voice_speak turns it into audio.
//   ↻ keeps the conversation going: after an answer it listens again (until a quiet turn or a click).
// Shown only while the hub is configured on the server (op voice_status). Language: auto or fixed, kept in this browser.
(function () {
  'use strict';
  var LANGS = [{ id: '', label: 'auto' }, { id: 'ru', label: 'RU' }, { id: 'uk', label: 'UK' }, { id: 'en', label: 'EN' },
    { id: 'nl', label: 'NL' }];
  var MAX_SECONDS = 30;
  var SILENCE_MS = 1000;
  var AGENT_WAIT_MS = 240000;

  var root = document.getElementById('voice');
  var media = { audio: false };
  var hub = false;
  var lang = '';
  var loop = false;
  var state = 'idle'; // idle | listening | thinking | waiting | speaking
  var note = '';
  var error = '';
  var events = [];
  var waiter = null; // {known: Set of assistant eventIds before the send, lang, timer}

  function el(tag, attrs, kids) {
    var e = document.createElement(tag);
    Object.keys(attrs || {}).forEach(function (k) {
      var v = attrs[k];
      if (v === undefined || v === null || v === false) return;
      if (k === 'text') e.textContent = v;
      else if (k.slice(0, 2) === 'on') e.addEventListener(k.slice(2), v);
      else e.setAttribute(k, v === true ? '' : String(v));
    });
    (kids || []).forEach(function (c) { if (c) e.appendChild(c); });
    return e;
  }

  function set(s, n) {
    state = s;
    if (n !== undefined) note = n;
    render();
  }

  function fail(e) {
    var m = String((e && e.message) || e || 'error');
    error = m === 'not_configured' ? 'The voice hub is not configured on the server.'
      : m === 'needs_user_touch' ? 'Press 🗣 to go on (a click allows 10 messages in 10 minutes).' : m;
    if (waiter) clearTimeout(waiter.timer);
    waiter = null;
    set('idle');
  }

  function b64bytes(b64) {
    var bin = atob(b64);
    var out = new Uint8Array(bin.length);
    for (var i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out.buffer;
  }

  function play(audio) {
    if (!audio || !audio.b64) return Promise.resolve();
    set('speaking');
    return coExt.play(b64bytes(audio.b64), audio.type || 'audio/mpeg');
  }

  function listen() {
    error = '';
    note = '';
    set('listening');
    coExt.audio.start(MAX_SECONDS, { silenceMs: SILENCE_MS }).catch(fail);
  }

  function finishTurn(again) {
    if (again && loop) listen();
    else set('idle');
  }

  function stopListening() {
    set('thinking', '');
    coExt.audio.stop().then(function (rec) {
      if (!rec || !rec.file || rec.file.size < 2000) {
        set('idle', 'Nothing heard.');
        return null;
      }
      return coExt.upload('turn', rec.file, rec.filename, { lang: lang, route: 'client' }).then(function (r) {
        if (!r || r.error) throw new Error((r && r.error) || 'hub_failed');
        if (r.route === 'home' || r.route === 'none') {
          note = (r.text ? r.text + ' → ' : '') + (r.reply || '');
          return play(r.audio).then(function () { finishTurn(r.route === 'home'); });
        }
        // a question for the agent of this chat
        note = r.text;
        waiter = { known: assistantIds(), lang: r.lang || lang, timer: setTimeout(function () { fail('No answer from the agent yet; it will appear in the chat.'); }, AGENT_WAIT_MS) };
        set('waiting');
        return coExt.send('🎙 ' + r.text);
      });
    }).catch(fail);
  }

  function assistantIds() {
    var s = {};
    events.forEach(function (e) { if (e.type === 'assistant_message') s[e.eventId] = true; });
    return s;
  }

  function onChat(evs) {
    events = evs || [];
    if (!waiter) return;
    var fresh = events.filter(function (e) { return e.type === 'assistant_message' && !waiter.known[e.eventId]; });
    if (fresh.some(function (e) { return e.status === 'failed' || e.status === 'cancelled'; })) {
      fail('The agent did not answer (see the chat).');
      return;
    }
    var done = fresh.filter(function (e) { return e.text && (!e.status || e.status === 'completed'); });
    if (!done.length) return;
    var answer = done[done.length - 1];
    var w = waiter;
    clearTimeout(w.timer);
    waiter = null;
    set('thinking');
    coExt.call('voice_speak', { text: answer.text, lang: w.lang }, { timeoutMs: 90000 }).then(function (r) {
      if (!r || r.error) throw new Error((r && r.error) || 'speech failed');
      return play(r.audio).then(function () { finishTurn(true); });
    }).catch(fail);
  }

  function main() {
    if (state === 'idle') listen();
    else if (state === 'listening') stopListening();
    else if (state === 'speaking') {
      coExt.play(null).catch(function () { /* already stopped */ });
      loop = false;
      set('idle');
    } else if (state === 'waiting') {
      if (waiter) clearTimeout(waiter.timer);
      waiter = null;
      set('idle', 'The answer will appear in the chat.');
    }
  }

  coExt.on('audio', function (u) {
    if (!u) return;
    // the recording stopped by itself (quiet after speech, or the limit): send what there is
    if ((u.silence || !u.recording) && state === 'listening') stopListening();
  });
  coExt.on('chat', function (c) { onChat(c && c.events); });

  function label() {
    switch (state) {
      case 'listening': return 'Listening… (stop)';
      case 'thinking': return '…';
      case 'waiting': return '⏳ Agent';
      case 'speaking': return '🔊 Stop';
      default: return '🗣 Talk';
    }
  }

  function render() {
    root.textContent = '';
    if (!hub || !media.audio) {
      coExt.size(0, 0, false);
      return;
    }
    if (state === 'listening') root.appendChild(el('span', { class: 'dot', title: 'The microphone is on' }));
    root.appendChild(el('button', {
      class: 'btn' + (state !== 'idle' ? ' on' : ''), type: 'button', disabled: state === 'thinking',
      title: 'Voice mode: speak, the home or the agent of this chat answers aloud', onclick: main, text: label(),
    }));
    root.appendChild(el('button', {
      class: 'btn loop', type: 'button', 'aria-pressed': loop ? 'true' : 'false', title: 'Keep talking: listen again after each answer',
      onclick: function () { loop = !loop; coExt.storage.set('loop', loop ? '1' : '0').catch(function () {}); render(); }, text: '↻',
    }));
    var sel = el('select', { 'aria-label': 'Voice language', disabled: state !== 'idle', onchange: function (e) {
      lang = e.target.value;
      coExt.storage.set('lang', lang).catch(function () { /* kept for this page only */ });
    } }, LANGS.map(function (l) { return el('option', { value: l.id, text: l.label }); }));
    sel.value = lang;
    root.appendChild(sel);
    if (error) root.appendChild(el('span', { class: 'err', title: error, text: error }));
    else if (note) root.appendChild(el('span', { class: 'note', title: note, text: note }));
    coExt.size(root.scrollWidth + 2, 32, false);
  }

  coExt.on('context', function (c) {
    if (c && c.media) media = c.media;
    render();
  });
  coExt.ready.then(function (c) {
    media = (c && c.media) || media;
    Promise.all([coExt.storage.get('lang'), coExt.storage.get('loop')]).then(function (v) {
      if (v[0] !== null && LANGS.some(function (l) { return l.id === v[0]; })) lang = v[0];
      loop = v[1] === '1';
      render();
    }).catch(function () { /* defaults */ });
    coExt.call('voice_status', {}).then(function (s) {
      hub = !!(s && s.configured === true);
      if (hub && s.online === false) error = 'The voice hub is offline.';
      render();
    }).catch(function () { /* hidden */ });
  });
})();
