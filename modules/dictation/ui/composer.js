// SPDX-License-Identifier: AGPL-3.0-or-later
// Voice input button in the CO Web composer: the page of the module dictation in the ui.composer slot (Freedom F10; was
// co-web/src/dictation.ts + voice.ts and the 🎤 button of ChatView up to CO Web 0.102, behaviour unchanged).
//   CO Desktop: Windows voice typing (Win+H) types into the message box itself (coExt.speech.platform).
//   A speech-to-text key on the server (op dictation_status): CO Web records (coExt.audio), the upload 'transcribe' of
//   this module sends it to Whisper, the text goes after the box text (coExt.input ... done). When every service of the
//   server fails, the page falls back to the browser's recognition.
//   Otherwise the browser's speech recognition in CO Web (coExt.speech): the box shows the text as it is spoken.
// The language is kept in this browser (coExt.storage 'lang').
(function () {
  'use strict';
  var LANGS = [
    { id: 'ru-RU', label: 'RU' }, { id: 'uk-UA', label: 'UK' }, { id: 'en-US', label: 'EN' },
    { id: 'nl-BE', label: 'NL' }, { id: 'fr-BE', label: 'FR' }, { id: 'de-DE', label: 'DE' },
  ];
  var MAX_SECONDS = 300;

  var root = document.getElementById('dict');
  var media = { audio: false, speech: false, platform: false };
  var whisper = false;
  var lang = 'ru-RU';
  var state = 'idle'; // idle | listening | recording | busy
  var seconds = 0;
  var error = '';

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

  function useWhisper() { return whisper && media.audio; }
  function available() { return media.platform || media.speech || useWhisper(); }

  function fail(e) {
    error = String((e && e.message) || e || 'error');
    state = 'idle';
    render();
  }

  function toggle() {
    error = '';
    if (media.platform) {
      coExt.speech.platform().catch(fail);
      return;
    }
    if (useWhisper()) {
      if (state === 'recording') stopRecording();
      else if (state === 'idle') {
        state = 'recording';
        seconds = 0;
        render();
        coExt.audio.start(MAX_SECONDS).catch(fail);
      }
      return;
    }
    if (state === 'listening') {
      coExt.speech.stop().catch(fail);
      return;
    }
    state = 'listening';
    render();
    coExt.speech.start(lang).catch(fail);
  }

  function stopRecording() {
    state = 'busy';
    render();
    coExt.audio.stop().then(function (rec) {
      if (!rec || !rec.file) {
        fail('No audio was recorded.');
        return null;
      }
      return coExt.upload('transcribe', rec.file, rec.filename, { lang: lang }).then(function (r) {
        var text = r && typeof r.text === 'string' ? r.text.trim() : '';
        if (text) coExt.input(text, { done: true });
        state = 'idle';
        render();
      });
    }).catch(function (e) {
      var m = String((e && e.message) || e);
      if (m !== 'not_configured' && media.speech) {
        // every speech-to-text service of the server failed (home server off, no fallback key): the browser's own
        // recognition for the rest of this page's life; this recording is lost, so say it
        whisper = false;
        fail('Server transcription failed (' + m + '); switched to browser recognition, press 🎤 again.');
        return;
      }
      fail(m === 'not_configured' ? 'Voice input is not configured on the server.' : 'Transcription failed: ' + m);
    });
  }

  coExt.on('audio', function (u) {
    if (!u) return;
    seconds = u.seconds || 0;
    // the recording stopped by itself at the limit: transcribe what there is
    if (!u.recording && state === 'recording') stopRecording();
    else render();
  });
  coExt.on('speech', function (u) {
    if (!u) return;
    if (state === 'listening' || !u.active) coExt.input(u.final || '', { interim: u.interim || '', done: !u.active });
    if (u.error) error = u.error;
    if (!u.active && state === 'listening') state = 'idle';
    render();
  });
  coExt.on('sent', function () {
    if (state === 'listening') state = 'idle';
    render();
  });

  function label() {
    if (state === 'recording') return '● Stop ' + seconds + 's';
    if (state === 'busy') return '… Transcribing';
    if (state === 'listening') return '● Stop';
    return '🎤 Dictate';
  }

  function render() {
    root.textContent = '';
    if (!available()) {
      coExt.size(0, 0, false);
      return;
    }
    var active = state === 'recording' || state === 'listening';
    root.appendChild(el('button', {
      class: 'btn' + (active ? ' rec' : ''), type: 'button', 'aria-pressed': active ? 'true' : 'false', disabled: state === 'busy',
      title: useWhisper() ? (state === 'recording' ? 'Stop and transcribe' : 'Record voice (Whisper)') : state === 'listening' ? 'Stop dictation' : 'Dictate',
      onclick: toggle, text: label(),
    }));
    if (!media.platform) {
      var sel = el('select', { 'aria-label': 'Dictation language', disabled: state === 'listening', onchange: function (e) {
        lang = e.target.value;
        coExt.storage.set('lang', lang).catch(function () { /* kept for this page only */ });
      } }, LANGS.map(function (l) { return el('option', { value: l.id, text: l.label }); }));
      sel.value = lang;
      root.appendChild(sel);
    }
    if (error) root.appendChild(el('span', { class: 'err', title: error, text: error }));
    coExt.size(root.scrollWidth + 2, 32, false);
  }

  coExt.on('context', function (c) {
    if (c && c.media) media = c.media;
    render();
  });
  coExt.ready.then(function (c) {
    media = (c && c.media) || media;
    render();
    coExt.storage.get('lang').then(function (v) {
      if (v && LANGS.some(function (l) { return l.id === v; })) lang = v;
      render();
    }).catch(function () { /* default language */ });
    // CO Desktop types by itself; elsewhere a configured speech-to-text service wins over the browser's recognition
    if (!media.platform) {
      coExt.call('dictation_status', {}).then(function (s) {
        whisper = !!(s && s.configured === true);
        render();
      }).catch(function () { /* browser recognition */ });
    }
  });
})();
