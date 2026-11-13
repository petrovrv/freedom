// SPDX-License-Identifier: AGPL-3.0-or-later
// Family presence chips in the CO Web header: the page of the module geo-presence in the ui.header slot (Freedom F10;
// was co-web/src/GeoStatus.tsx + GeoMap.tsx of CO Web 0.91.0-0.101.0, behaviour unchanged). One chip per member:
// red ↑ - left home recently and still on the way, green ↓ - heading home, 🏠 - at home, 🐕 - a known routine such as a
// dog walk, otherwise the place name or its icon. A "?" is a question of the home PC ("remember this place?", "pick an
// icon"); the member (and the owner) answer it in the chip's popover. The own chip has the privacy modes (only the
// member sets them). Nothing here shows coordinates. Data: coExt.call geo_state / geo_answer / geo_privacy (ext/1).
(function () {
  'use strict';
  var POLL_MS = 60000;
  var PC_STALE_S = 20 * 60;
  var DEFAULT_CENTER = [50.85, 4.35];
  var MODES = [
    { id: 'full', label: 'Where I am', hint: 'place, arrows and icons as they are' },
    { id: 'home', label: 'Only heading home', hint: 'others see 🏠, ↓ heading home, otherwise "out"' },
    { id: 'saved', label: 'A saved place', hint: 'others see the place you choose while you are out' },
    { id: 'map', label: 'A place on the map', hint: 'others see the area of the point you pick' },
    { id: 'off', label: 'Off', hint: 'only "at home" and "heading home", automatic or by the buttons below' },
  ];
  var UNTIL = [
    { id: 'ever', label: 'until I change it', at: function () { return undefined; } },
    { id: '4h', label: 'for 4 hours', at: function () { return Math.floor(Date.now() / 1000) + 4 * 3600; } },
    { id: 'day', label: 'until tonight', at: function () { var d = new Date(); d.setHours(23, 59, 0, 0); return Math.floor(d.getTime() / 1000); } },
  ];

  var root = document.getElementById('geo');
  var ctx = null;
  var st = null;
  var open = null;       // user whose popover is open
  var names = {};        // answers being typed, by suggestion id
  var busy = false;
  var pv = null;         // privacy form state of the own chip {mode, place, pt, until, err}

  function el(tag, attrs, kids) {
    var e = document.createElement(tag);
    Object.keys(attrs || {}).forEach(function (k) {
      var v = attrs[k];
      if (v === undefined || v === null || v === false) return;
      if (k === 'text') e.textContent = v;
      else if (k.slice(0, 2) === 'on') e.addEventListener(k.slice(2), v);
      else if (k === 'style') Object.assign(e.style, v);
      else e.setAttribute(k, v === true ? '' : String(v));
    });
    (kids || []).forEach(function (c) { if (c) e.appendChild(typeof c === 'string' ? document.createTextNode(c) : c); });
    return e;
  }

  function hm(ts) {
    if (!ts) return '';
    var d = new Date(ts * 1000);
    var t = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    return new Date().toDateString() === d.toDateString() ? t : d.toLocaleDateString([], { day: '2-digit', month: '2-digit' }) + ' ' + t;
  }
  function km(m) { return m.km === undefined ? '' : m.km.toFixed(m.km < 10 ? 1 : 0) + ' km from home'; }
  function describe(m) {
    var place = m.place || 'unknown place';
    switch (m.state) {
      case 'home': return 'At home' + (m.since ? ' since ' + hm(m.since) : '');
      case 'left': return 'Left home at ' + hm(m.since) + ', on the way · now near ' + place;
      case 'coming': return 'Heading home' + (m.etaMin ? ', ~' + m.etaMin + ' min' : '') + (m.place ? ' · now near ' + m.place : '');
      case 'walk': return (m.activity || 'Walk') + ' since ' + hm(m.since) + ' · now near ' + place;
      case 'there': return 'At ' + place + (m.since ? ' since ' + hm(m.since) : '');
      case 'moving': return 'On the move near ' + place;
      case 'stale': return 'Last seen ' + (m.place ? 'at ' + m.place + ' ' : '') + 'at ' + hm(m.updated);
      case 'away': return 'Out';
      default: return 'No location yet (OwnTracks is not connected on the phone)';
    }
  }
  function arrow(state) {
    if (state === 'left') return el('span', { class: 'geo-arrow up', 'aria-label': 'left home', text: '↑' });
    if (state === 'coming') return el('span', { class: 'geo-arrow down', 'aria-label': 'heading home', text: '↓' });
    if (state === 'home') return el('span', { class: 'geo-arrow', 'aria-label': 'at home', text: '🏠' });
    if (state === 'walk') return el('span', { class: 'geo-arrow', 'aria-label': 'walk', text: '🐕' });
    return null;
  }

  function normalize(j) {
    var p = j.privacy && typeof j.privacy === 'object' ? j.privacy : { mode: 'full' };
    return {
      updated: Number(j.updated) || 0, now: Number(j.now) || Math.floor(Date.now() / 1000),
      members: Array.isArray(j.members) ? j.members : [], suggestions: Array.isArray(j.suggestions) ? j.suggestions : [],
      places: Array.isArray(j.places) ? j.places : [], privacy: p,
      tileToken: typeof j.tileToken === 'string' ? j.tileToken : '',
      center: Array.isArray(j.center) && j.center.length === 2 ? [Number(j.center[0]), Number(j.center[1])] : null,
    };
  }

  function poll() {
    return coExt.call('geo_state', {}).then(function (j) { st = normalize(j); render(); }, function () { /* keep the last state */ });
  }

  // ---- map for the "place on the map" mode (was GeoMap.tsx) ----
  var T = 256, MW = 276, MH = 200;
  function toPx(lat, lon, z) {
    var n = T * Math.pow(2, z), s = Math.sin(lat * Math.PI / 180);
    return [(lon + 180) / 360 * n, (0.5 - Math.log((1 + s) / (1 - s)) / (4 * Math.PI)) * n];
  }
  function toLatLon(x, y, z) {
    var n = T * Math.pow(2, z);
    return [Math.atan(Math.sinh(Math.PI * (1 - 2 * y / n))) * 180 / Math.PI, x / n * 360 - 180];
  }
  var map = { z: 12, c: null };
  function mapView(center, token, onPick) {
    if (!map.c) map.c = pv.pt || center;
    var box = el('div', { class: 'geo-map', style: { width: MW + 'px', height: MH + 'px' } });
    function draw() {
      box.textContent = '';
      var z = map.z, cxy = toPx(map.c[0], map.c[1], z), left = cxy[0] - MW / 2, top = cxy[1] - MH / 2, max = Math.pow(2, z);
      for (var ty = Math.floor(top / T); ty <= Math.floor((top + MH) / T); ty++) {
        for (var tx = Math.floor(left / T); tx <= Math.floor((left + MW) / T); tx++) {
          if (ty < 0 || ty >= max) continue;
          var wx = ((tx % max) + max) % max;
          box.appendChild(el('img', { alt: '', draggable: 'false', src: coExt.routeUrl('tile', { z: z, x: wx, y: ty, t: token }),
            style: { left: (tx * T - left) + 'px', top: (ty * T - top) + 'px' } }));
        }
      }
      if (pv.pt) {
        var p = toPx(pv.pt[0], pv.pt[1], z);
        box.appendChild(el('span', { class: 'geo-map-pin', text: '📍', style: { left: (p[0] - left) + 'px', top: (p[1] - top) + 'px' } }));
      }
      var zoom = el('div', { class: 'geo-map-zoom' }, [
        el('button', { type: 'button', text: '+', onclick: function () { map.z = Math.min(17, map.z + 1); draw(); } }),
        el('button', { type: 'button', text: '−', onclick: function () { map.z = Math.max(3, map.z - 1); draw(); } }),
      ]);
      zoom.addEventListener('pointerdown', function (e) { e.stopPropagation(); });
      box.appendChild(zoom);
      box.appendChild(el('span', { class: 'geo-map-attr', text: '© OpenStreetMap' }));
      box._origin = [left, top];
    }
    var drag = null;
    box.addEventListener('pointerdown', function (e) {
      var c = toPx(map.c[0], map.c[1], map.z);
      drag = { x: e.clientX, y: e.clientY, cx: c[0], cy: c[1], moved: false };
      if (box.setPointerCapture) box.setPointerCapture(e.pointerId);
    });
    box.addEventListener('pointermove', function (e) {
      if (!drag) return;
      var dx = e.clientX - drag.x, dy = e.clientY - drag.y;
      if (Math.abs(dx) + Math.abs(dy) > 4) drag.moved = true;
      if (drag.moved) { map.c = toLatLon(drag.cx - dx, drag.cy - dy, map.z); draw(); }
    });
    box.addEventListener('pointerup', function (e) {
      var d = drag;
      drag = null;
      if (!d || d.moved) return;
      var r = box.getBoundingClientRect(), o = box._origin;
      var ll = toLatLon(o[0] + e.clientX - r.left, o[1] + e.clientY - r.top, map.z);
      onPick(Math.round(ll[0] * 1e5) / 1e5, Math.round((((ll[1] + 540) % 360) - 180) * 1e5) / 1e5);
    });
    draw();
    return box;
  }

  // ---- privacy form of the own chip ----
  function privacyView() {
    var p = st.privacy;
    if (!pv) pv = { mode: p.mode, place: p.place || (st.places[0] && st.places[0].name) || '', pt: p.lat !== undefined && p.lon !== undefined ? [p.lat, p.lon] : null, until: 'ever', err: '' };
    function run(args) {
      busy = true;
      pv.err = '';
      render();
      coExt.call('geo_privacy', args).then(function (j) {
        if (!j || !j.ok) throw new Error((j && j.error) || 'geo_privacy failed');
        pv = null;
        map.c = null;
        return poll();
      }).catch(function (e) { if (pv) pv.err = e.message || String(e); }).then(function () { busy = false; render(); });
    }
    var ready = (pv.mode !== 'saved' || !!pv.place) && (pv.mode !== 'map' || !!pv.pt);
    var box = el('div', { class: 'geo-privacy' }, [
      el('div', {}, [el('b', { text: 'Privacy' }), ' ', el('span', { class: 'small muted', text: '— only you see and change this' })]),
    ]);
    MODES.forEach(function (x) {
      box.appendChild(el('label', { class: 'geo-privacy-mode', title: x.hint }, [
        el('input', { type: 'radio', name: 'geo-mode', checked: pv.mode === x.id, onchange: function () { pv.mode = x.id; render(); } }),
        el('span', {}, [x.label, el('span', { class: 'small muted', text: ' · ' + x.hint })]),
      ]));
    });
    if (pv.mode === 'saved') {
      if (st.places.length) {
        var sel = el('select', { onchange: function (e) { pv.place = e.target.value; } });
        st.places.forEach(function (pl) { sel.appendChild(el('option', { value: pl.name, selected: pl.name === pv.place, text: (pl.icon ? pl.icon + ' ' : '') + pl.name })); });
        box.appendChild(sel);
      } else box.appendChild(el('div', { class: 'small muted', text: 'No saved places yet.' }));
    }
    if (pv.mode === 'map') {
      box.appendChild(mapView(st.center || DEFAULT_CENTER, st.tileToken, function (la, lo) { pv.pt = [la, lo]; render(); }));
      box.appendChild(el('div', { class: 'small muted', text: pv.pt ? 'Point picked; others see the name of its area.' : 'Click the map to pick a point.' }));
    }
    if (pv.mode !== 'full') {
      var us = el('select', { 'aria-label': 'how long', onchange: function (e) { pv.until = e.target.value; } });
      UNTIL.forEach(function (u) { us.appendChild(el('option', { value: u.id, selected: u.id === pv.until, text: u.label })); });
      box.appendChild(us);
    }
    box.appendChild(el('div', { class: 'geo-sugg-btns' }, [el('button', { class: 'btn', type: 'button', disabled: busy || !ready, text: 'Apply', onclick: function () {
      var args = { mode: pv.mode };
      var u = UNTIL.filter(function (x) { return x.id === pv.until; })[0];
      var at = u && u.at();
      if (at) args.until = at;
      if (pv.mode === 'saved') args.place = pv.place;
      if (pv.mode === 'map' && pv.pt) { args.lat = pv.pt[0]; args.lon = pv.pt[1]; }
      run(args);
    } })]));
    if (p.mode !== 'full') {
      var row = el('div', { class: 'geo-sugg-btns' }, [
        el('button', { class: 'btn ghost', type: 'button', disabled: busy, text: "🏠 I'm home", onclick: function () { run({ manual: 'home' }); } }),
        el('button', { class: 'btn ghost', type: 'button', disabled: busy, text: '↓ Heading home', onclick: function () { run({ manual: 'coming' }); } }),
      ]);
      if (p.manual) row.appendChild(el('button', { class: 'btn ghost', type: 'button', disabled: busy, text: 'Auto', onclick: function () { run({ manual: '' }); } }));
      box.appendChild(row);
    }
    if (p.until) box.appendChild(el('div', { class: 'small muted', text: 'Mode ends ' + hm(p.until) }));
    if (pv.err) box.appendChild(el('div', { class: 'small geo-err', text: pv.err }));
    return box;
  }

  function answer(id, name) {
    busy = true;
    render();
    coExt.call('geo_answer', { id: id, name: name }).then(function () { names[id] = ''; return poll(); }, function () {})
      .then(function () { busy = false; render(); });
  }

  function suggestionView(m, s) {
    var icon = s.kind === 'icon';
    var input = el('input', { value: names[s.id] || '', placeholder: icon ? 'e.g. 🏋️ 📚 🛒' : 'e.g. Gym, Library, Friends', maxlength: icon ? 8 : 60 });
    var submit = el('button', { class: 'btn', type: 'submit', disabled: busy || !(names[s.id] || '').trim(), text: icon ? 'Set icon' : 'Remember' });
    input.addEventListener('input', function () { names[s.id] = input.value; submit.disabled = busy || !input.value.trim(); });
    var form = el('form', { class: 'geo-sugg' }, [
      el('div', { text: icon ? (s.place || 'This place') + ' has no icon yet. Pick an emoji for it'
        : m.name + ' often stays here' + (s.area ? ' (' + s.area + ')' : '') + (s.reason ? ': ' + s.reason : '') + '. Remember the place as' }),
      input,
      el('div', { class: 'geo-sugg-btns' }, [submit, el('button', { class: 'btn ghost', type: 'button', disabled: busy, text: "Don't ask", onclick: function () { answer(s.id, ''); } })]),
    ]);
    form.addEventListener('submit', function (e) { e.preventDefault(); var n = (names[s.id] || '').trim(); if (n) answer(s.id, n); });
    return form;
  }

  function render() {
    root.textContent = '';
    var vw = (ctx && ctx.vw) || 1400;
    document.documentElement.className = vw <= 760 ? 'vw-m vw-s' : vw <= 1100 ? 'vw-m' : '';
    if (!st || !st.members.length) { coExt.size(0, 0, false); return; }
    var pcStale = st.now - st.updated > PC_STALE_S;
    var hidden = st.privacy.mode !== 'full';
    var me = ctx && ctx.me;
    var bar = el('div', { class: 'geo-bar' + (pcStale ? ' pc-stale' : ''), title: pcStale ? 'Home PC has not updated locations since ' + hm(st.updated) : undefined });
    var pop = null;
    st.members.forEach(function (m) {
      var sugg = st.suggestions.filter(function (s) { return s.user === m.user; });
      var mine = m.user === me;
      var label = m.state === 'home' ? '' : m.icon || (m.state === 'away' ? 'out' : m.place);
      bar.appendChild(el('button', {
        type: 'button', class: 'geo-chip ' + m.state + (open === m.user ? ' open' : ''), title: m.name + ': ' + describe(m), 'aria-expanded': open === m.user ? 'true' : 'false',
        onclick: function () { open = open === m.user ? null : m.user; pv = null; map.c = null; render(); },
      }, [
        el('span', { class: 'geo-name', text: m.name }),
        el('span', { class: 'geo-initial', text: m.name.slice(0, 1) }),
        arrow(m.state),
        label ? el('span', { class: m.icon && m.state !== 'home' ? 'geo-arrow' : 'geo-place', 'aria-label': m.icon ? m.place : undefined, text: label }) : null,
        mine && hidden ? el('span', { class: 'geo-lock', 'aria-label': 'privacy on', text: '🔒' }) : null,
        sugg.length ? el('span', { class: 'geo-ask', 'aria-label': 'question', text: '?' }) : null,
      ]));
      if (open === m.user) {
        // width from the CO Web window (ctx.vw): inside this frame vw is the frame itself
        pop = el('div', { class: 'geo-pop', role: 'dialog', 'aria-label': m.name, style: { width: Math.min(300, Math.floor(vw * 0.86)) + 'px' } }, [
          el('div', {}, [el('b', { text: m.name })]),
          el('div', { text: describe(m) }),
          el('div', { class: 'small muted', text: [km(m), m.batt !== undefined ? 'battery ' + m.batt + '%' : '', m.updated ? 'updated ' + hm(m.updated) : ''].filter(Boolean).join(' · ') }),
          mine && hidden ? el('div', { class: 'small muted', text: 'This is what the others see now.' }) : null,
        ]);
        sugg.forEach(function (s) { pop.appendChild(suggestionView(m, s)); });
        if (mine) pop.appendChild(privacyView());
      }
    });
    root.appendChild(bar);
    if (pop) root.appendChild(pop);
    // the closed bar is as small as its chips; an open popover draws over the page
    requestAnimationFrame(function () {
      coExt.size(Math.max(bar.scrollWidth + 2, pop ? pop.offsetWidth + 12 : 0), root.scrollHeight + (pop ? 4 : 0), !!pop);
    });
  }

  coExt.on('blur', function () { if (open) { open = null; pv = null; map.c = null; render(); } });
  coExt.on('context', function (c) { ctx = c; render(); });
  coExt.ready.then(function (c) {
    ctx = c;
    render();
    poll();
    // no refresh while a popover is open: it would rebuild the form being typed in
    setInterval(function () { if (!open) poll(); }, POLL_MS);
  });
  document.addEventListener('keydown', function (e) { if (e.key === 'Escape' && open) { open = null; pv = null; render(); } });
})();
