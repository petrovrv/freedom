// SPDX-License-Identifier: AGPL-3.0-or-later
// Calendar tray: time of the next meeting today (e.g. "📅 10:00"), a red dot when the next 7 days have a conflict.
// Reads only the flat cal_status (no titles); empty when the service does not answer or nothing is left today.
(() => {
'use strict';
const chip = document.getElementById('chip');
let generation = 0;
const fmt = new Intl.DateTimeFormat(undefined, {hour: '2-digit', minute: '2-digit'});
function theme(ctx) { document.documentElement.dataset.theme = ['light', 'dark'].includes(ctx.theme) ? ctx.theme : ''; }
function badge(v) { if (typeof coExt.badge === 'function') coExt.badge(v); }
function hide() { chip.hidden = true; coExt.size(0, 0, false); badge(null); }
function sameDay(a, b) { return a.toDateString() === b.toDateString(); }
async function refresh() {
  const ticket = ++generation;
  try {
    const st = await coExt.call('cal_status', {});
    if (ticket !== generation) return;
    if (!st.paired || !st.online) { hide(); return; }
    const now = new Date(), start = st.nextStart ? new Date(st.nextStart) : null, end = st.nextEnd ? new Date(st.nextEnd) : null;
    const today = start && sameDay(start, now);
    const conflicts = Math.max(0, Number(st.conflicts) || 0);
    if (!today && !conflicts) { hide(); return; }
    chip.replaceChildren();
    if (today) chip.append(document.createTextNode('📅 ' + (start <= now && end > now ? 'сейчас' : fmt.format(start))));
    else chip.append(document.createTextNode('📅'));
    if (conflicts) { const dot = document.createElement('span'); dot.className = 'dot'; chip.append(dot); }
    badge(conflicts ? '!' : null);
    chip.title = (today ? 'Следующая встреча: ' + fmt.format(start) : 'Сегодня встреч больше нет')
      + (conflicts ? ' · пересечений за 7 дней: ' + conflicts : '') + ' · открыть календарь';
    chip.setAttribute('aria-label', chip.title);
    chip.hidden = false;
    coExt.size(Math.ceil(chip.getBoundingClientRect().width) + 2, 32, false);
  } catch (_) { if (ticket === generation) hide(); }
}
coExt.ready.then(ctx => {
  theme(ctx); hide();
  chip.addEventListener('click', () => coExt.navigate('/x/calendar/app', 'self'));
  coExt.on('context', next => { theme(next); hide(); refresh(); });
  refresh();
  const timer = setInterval(refresh, 60000);
  window.addEventListener('pagehide', () => { clearInterval(timer); generation++; });
}).catch(hide);
})();
