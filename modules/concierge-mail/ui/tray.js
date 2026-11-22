// SPDX-License-Identifier: AGPL-3.0-or-later
(() => {
'use strict';
const chip = document.getElementById('chip');
let generation = 0;
function theme(ctx) {
  document.documentElement.dataset.theme = ['light', 'dark'].includes(ctx.theme) ? ctx.theme : '';
}
function hide() { chip.hidden = true; coExt.size(0, 0, false); }
async function refresh() {
  const ticket = ++generation;
  try {
    const st = await coExt.call('mail_status', {});
    if (ticket !== generation) return;
    const n = Math.max(0, Number(st.unread) || 0);
    if (!st.paired || !st.online || !n) { hide(); return; }
    chip.textContent = '✉ ' + (n > 99 ? '99+' : n);
    chip.title = 'Почта: ' + n + ' непрочитанных · открыть';
    chip.setAttribute('aria-label', chip.title);
    chip.hidden = false;
    coExt.size(Math.ceil(chip.getBoundingClientRect().width) + 2, 32, false);
  } catch (_) { if (ticket === generation) hide(); }
}
coExt.ready.then(ctx => {
  theme(ctx); hide();
  chip.addEventListener('click', () => coExt.navigate('/x/concierge-mail/mail', 'self'));
  coExt.on('context', next => { theme(next); hide(); refresh(); });
  refresh();
  const timer = setInterval(refresh, 60000);
  window.addEventListener('pagehide', () => { clearInterval(timer); generation++; });
}).catch(hide);
})();
