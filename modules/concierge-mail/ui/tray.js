// SPDX-License-Identifier: AGPL-3.0-or-later
(() => {
'use strict';
const chip = document.getElementById('chip');
let generation = 0;
// 0.14.0: the tray is loaded on every CO page, so it also tells the core which chats have mail threads (the
// Correspondence part of their folders, ext/1 chat.partCounts), at most every 5 minutes; asked end-to-end sealed
const COUNTS_MS = 5 * 60000;
let relay = null, relayEnv = '', countsAt = 0, countsBusy = false;
async function counts(st) {
  if (countsBusy || Date.now() - countsAt < COUNTS_MS || typeof HomeRelay !== 'function') return;
  countsBusy = true; countsAt = Date.now();
  try {
    if (!relay || relayEnv !== st.env) { relay = HomeRelay((op, args, opts) => coExt.call(op, args, opts), {env: st.env, module: 'concierge-mail', op: 'mail_call'}); relayEnv = st.env; }
    const data = await relay.call('chats', {}, st.pub);
    const list = Array.isArray(data.chats) ? data.chats : [];
    await coExt.req('chat.partCounts', {part: 'correspondence', counts: Object.fromEntries(list.map(c => [c.chat, Math.max(0, Number(c.threads) || 0)]))});
  } catch (_) { countsAt = Date.now() - COUNTS_MS + 60000; /* try again in a minute */ }
  finally { countsBusy = false; }
}
function theme(ctx) {
  document.documentElement.dataset.theme = ['light', 'dark'].includes(ctx.theme) ? ctx.theme : '';
}
// the count also goes on the app tile ✉ (ext/1 coExt.badge, CO Web 0.116+; older shells simply lack it)
function badge(v) { if (typeof coExt.badge === 'function') coExt.badge(v); }
function hide() { chip.hidden = true; coExt.size(0, 0, false); badge(null); }
async function refresh() {
  const ticket = ++generation;
  try {
    const st = await coExt.call('mail_status', {});
    if (ticket !== generation) return;
    const n = Math.max(0, Number(st.unread) || 0);
    if (st.paired && st.online) counts(st);
    if (!st.paired || !st.online || !n) { hide(); return; }
    chip.textContent = '✉ ' + (n > 99 ? '99+' : n);
    badge(n > 99 ? '99+' : n);
    chip.title = 'Почта: ' + n + ' непрочитанных · открыть';
    chip.setAttribute('aria-label', chip.title);
    chip.hidden = false;
    coExt.size(Math.ceil(chip.getBoundingClientRect().width) + 2, 32, false);
  } catch (_) { if (ticket === generation) hide(); }
}
coExt.ready.then(ctx => {
  theme(ctx); hide();
  chip.addEventListener('click', () => coExt.navigate('/x/concierge-mail/mail', 'self'));
  coExt.on('context', next => { theme(next); hide(); countsAt = 0; refresh(); });
  refresh();
  const timer = setInterval(refresh, 60000);
  window.addEventListener('pagehide', () => { clearInterval(timer); generation++; });
}).catch(hide);
})();
