// SPDX-License-Identifier: AGPL-3.0-or-later
// Calendar app (📅, calendar 0.1, read only): week / day / agenda of the person's calendar copy on the home server.
// Data: cal_status (flat, through the site) and sealed calls through home-relay (ui/relay.js of module home-relay).
(() => {
'use strict';
const $ = id => document.getElementById(id);
const DAY = 86400000, AGENDA_DAYS = 14;
const narrow = () => matchMedia('(max-width: 899px)').matches;
let st = {}, relay = null, queue = Promise.resolve(), statusTicket = 0, loadTicket = 0, timer = 0, refreshTimer = 0;
let view = narrow() ? 'day' : 'week', anchor = startOfDay(new Date()), items = [], cals = [], loaded = false;

function el(tag, cls, text) { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
function button(text, fn, cls) { const b = el('button', cls, text); b.type = 'button'; b.addEventListener('click', fn); return b; }
function theme(ctx) { document.documentElement.dataset.theme = ['light', 'dark'].includes(ctx.theme) ? ctx.theme : ''; }
function startOfDay(d) { const x = new Date(d); x.setHours(0, 0, 0, 0); return x; }
function addDays(d, n) { const x = new Date(d); x.setDate(x.getDate() + n); return x; }
function monday(d) { const x = startOfDay(d); return addDays(x, -((x.getDay() + 6) % 7)); }
function sameDay(a, b) { return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate(); }
function ymd(d) { return d.getFullYear() + '-' + String(d.getMonth() + 1).padStart(2, '0') + '-' + String(d.getDate()).padStart(2, '0'); }
function localDate(s) { const [y, m, d] = s.split('-').map(Number); return new Date(y, m - 1, d); }
const fmtTime = new Intl.DateTimeFormat(undefined, {hour: '2-digit', minute: '2-digit'});
const fmtDay = new Intl.DateTimeFormat(undefined, {weekday: 'short', day: 'numeric'});
const fmtLong = new Intl.DateTimeFormat(undefined, {weekday: 'long', day: 'numeric', month: 'long'});
const fmtMonth = new Intl.DateTimeFormat(undefined, {month: 'long', year: 'numeric'});
const fmtWhen = new Intl.DateTimeFormat(undefined, {weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit'});

const messages = {offline: 'Домашний сервер не отвечает', timeout: 'Сервер не успел ответить', not_paired: 'Календарь ещё не подключён',
  not_served: 'Домашний сервер не обслуживает календарь', not_bound: 'Календарь на домашнем сервере принадлежит другому окружению',
  bad_request: 'Не удалось прочитать запрос', worker_error: 'Не удалось получить календарь', no_webcrypto: 'Шифрование недоступно в этом браузере',
  busy: 'Слишком много запросов, повторите', bad_range: 'Слишком длинный период', not_found: 'Событие не найдено (могло быть удалено)'};
function showError(target, err, retry) { target.replaceChildren(el('span', '', messages[err.message] || 'Не удалось загрузить календарь'), button('Повторить', retry)); }

// one call at a time: relay.js keeps one session key for the page
function call(action, args) {
  const env = st.env, active = relay;
  const task = queue.catch(() => {}).then(() => {
    if (env !== st.env || active !== relay || !st.paired) throw new Error('not_paired');
    if (!st.online) throw new Error('offline');
    return active.call(action, args, st.pub);
  });
  queue = task.catch(() => {});
  return task;
}

function ago(iso) {
  if (!iso) return '';
  const m = Math.round((Date.now() - Date.parse(iso)) / 60000);
  return m < 1 ? 'только что' : m < 60 ? m + ' мин назад' : fmtWhen.format(new Date(iso));
}
function connectionLine() {
  const c = $('connection');
  if (!st.paired) { c.textContent = 'Календарь ещё не подключён: хозяин подключает его на домашнем сервере (calctl add + connect).'; return; }
  if (!st.online) { showError(c, new Error('offline'), status); return; }
  if (st.auth === 'needs_consent') { c.replaceChildren(el('span', 'notice', 'Google не даёт доступ к календарю: доступ отозван или не выдан. Подключите календарь снова (calctl connect).')); return; }
  if (st.auth === 'never') { c.textContent = (st.account || 'Календарь') + ' · первая синхронизация ещё не прошла'; return; }
  c.textContent = (st.account || 'Календарь') + (st.synced ? ' · обновлено ' + ago(st.synced) : '');
}

async function status() {
  clearTimeout(timer);
  const ticket = ++statusTicket;
  try {
    const fresh = await coExt.call('cal_status', {});
    if (ticket !== statusTicket) return;
    if (fresh.error) throw new Error(fresh.error);
    if (fresh.env !== st.env || fresh.account !== st.account || !fresh.paired) { relay = null; loaded = false; }
    st = fresh;
    if (st.paired && st.online && !relay) relay = HomeRelay((op, args, opts) => coExt.call(op, args, opts), {env: st.env, module: 'calendar', op: 'cal_call'});
    connectionLine();
    if (st.paired && st.online && !loaded) { loaded = true; overview(); load(); }
  } catch (err) {
    if (ticket !== statusTicket) return;
    st = {...st, online: false}; showError($('connection'), err, status);
  } finally {
    if (ticket === statusTicket) timer = setTimeout(status, st.online || st.paired === false ? 60000 : 15000);
  }
}

async function overview() {
  try { const data = await call('overview', {}); cals = data.calendars || []; renderCals(); }
  catch (err) { showError($('cals'), err, overview); }
}
function renderCals() {
  const nav = $('cals'); nav.replaceChildren(el('h2', '', 'Календари'));
  for (const c of cals) {
    const lab = el('label'), box = el('input'), dot = el('span', 'dot');
    box.type = 'checkbox'; box.checked = !!c.show; dot.style.background = safeColor(c.color);
    box.addEventListener('change', async () => {
      box.disabled = true;
      try { const data = await call('calendar_set', {provider: c.provider, id: c.id, show: box.checked}); cals = data.calendars || cals; renderCals(); load(); }
      catch (err) { box.checked = !box.checked; box.disabled = false; showError($('connection'), err, status); }
    });
    lab.append(box, dot, el('span', '', c.title || c.id)); nav.append(lab);
    const note = [c.primary ? 'основной' : '', c.busy ? 'учитывается в пересечениях' : '', c.error ? 'ошибка: ' + c.error : ''].filter(Boolean).join(' · ');
    if (note) nav.append(el('div', 'small', note));
  }
  nav.append(el('p', 'small', 'В версии 0.1 календарь только читает Google: изменения делаются в Google Calendar.'));
}
function safeColor(c) { return /^#[0-9a-f]{3,8}$/i.test(c || '') ? c : '#3367d6'; }

function period() {
  if (view === 'week') { const s = monday(anchor); return [s, addDays(s, 7)]; }
  if (view === 'day') return [anchor, addDays(anchor, 1)];
  return [anchor, addDays(anchor, AGENDA_DAYS)];
}
async function load() {
  const ticket = ++loadTicket, [s, e] = period();
  renderFrame();
  if (!relay) return;
  try {
    const data = await call('range', {from: s.toISOString().replace(/\.\d{3}Z$/, 'Z'), to: e.toISOString().replace(/\.\d{3}Z$/, 'Z')});
    if (ticket !== loadTicket) return;
    items = data.items || []; render();
  } catch (err) { if (ticket === loadTicket) showError($('grid'), err, load); }
}

function renderFrame() {
  const [s, e] = period();
  $('period').textContent = view === 'day' ? fmtLong.format(s) : view === 'week' ? fmtMonth.format(addDays(s, 3)) : fmtLong.format(s) + ' – ' + fmtLong.format(addDays(e, -1));
  document.body.dataset.view = view;
  for (const b of document.querySelectorAll('.views button')) b.setAttribute('aria-selected', String(b.dataset.view === view));
}
function render() { renderFrame(); if (view === 'agenda') renderAgenda(); else renderGrid(); }

function cls(i) {
  return ['ev', i.my === 'declined' ? 'declined' : '', i.my === 'needsAction' || i.my === 'tentative' || i.status === 'tentative' ? 'tentative' : '',
    i.transparency === 'transparent' ? 'free' : '', i.conflict ? 'conflict' : i.softConflict ? 'soft' : ''].filter(Boolean).join(' ');
}
function evButton(i, extra) {
  const b = button('', () => openCard(i), cls(i) + (extra ? ' ' + extra : ''));
  b.style.setProperty('--c', safeColor(i.color));
  const label = (i.summary || '(без названия)') + (i.allDay ? '' : ', ' + fmtTime.format(new Date(i.start)) + '–' + fmtTime.format(new Date(i.end)))
    + (i.conflict ? ', пересечение' : i.softConflict ? ', приходится на занятый день' : '') + (i.my === 'needsAction' ? ', нет ответа' : '');
  b.setAttribute('aria-label', label); b.title = label;
  return b;
}
function days() { const [s, e] = period(); const out = []; for (let d = s; d < e; d = addDays(d, 1)) out.push(d); return out; }

function renderGrid() {
  const ds = days(), cols = '52px repeat(' + ds.length + ', minmax(0,1fr))', today = new Date();
  const g = $('grid'); g.replaceChildren();
  const head = el('div', 'head'); head.style.gridTemplateColumns = cols; head.append(el('div'));
  for (const d of ds) { const c = el('div', 'd' + (sameDay(d, today) ? ' today' : '')); c.append(el('b', '', fmtDay.format(d))); head.append(c); }
  const all = el('div', 'allday'); all.style.gridTemplateColumns = cols; all.append(el('div', 'small', ''));
  const body = el('div', 'body'); body.style.gridTemplateColumns = cols;
  const hours = el('div', 'hours'); for (let h = 0; h < 24; h++) hours.append(el('div', 'h', h ? String(h).padStart(2, '0') + ':00' : ''));
  body.append(hours);
  const hourPx = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--hour')) || 48;
  for (const d of ds) {
    const next = addDays(d, 1), cell = el('div', 'cell'), col = el('div', 'col');
    for (const i of items.filter(i => i.allDay && localDate(i.date) < next && localDate(i.endDate) > d)) { const b = evButton(i, 'chip'); b.textContent = i.summary || '(без названия)'; cell.append(b); }
    const timed = items.filter(i => !i.allDay && new Date(i.start) < next && new Date(i.end) > d)
      .map(i => ({i, s: Math.max(new Date(i.start), d), e: Math.min(new Date(i.end), next)}));
    layout(timed);
    for (const t of timed) {
      // wall-clock minutes of the local day (a DST day has 23 or 25 hours, the hour labels stay 00..23)
      const m0 = wall(t.s, d), m1 = +t.e >= +next ? 1440 : wall(t.e, d);
      const top = m0 / 60 * hourPx, h = Math.max((m1 - m0) / 60 * hourPx - 2, 18);
      const b = evButton(t.i);
      b.style.top = top + 'px'; b.style.height = h + 'px';
      b.style.left = 'calc(' + (100 * t.col / t.cols) + '% + 2px)'; b.style.right = 'auto'; b.style.width = 'calc(' + (100 / t.cols) + '% - 4px)';
      b.append(el('span', '', t.i.summary || '(без названия)'));
      if (h > 30) b.append(el('span', 't', fmtTime.format(new Date(t.i.start)) + '–' + fmtTime.format(new Date(t.i.end))));
      col.append(b);
    }
    if (sameDay(d, today)) { const n = el('div', 'now'); n.style.top = (wall(today, d) / 60 * hourPx) + 'px'; col.append(n); }
    all.append(cell); body.append(col);
  }
  g.append(head, all, body);
  all.style.top = head.getBoundingClientRect().height + 'px';
  if (!g.dataset.scrolled) { g.dataset.scrolled = '1'; g.scrollTop = 7.5 * hourPx; }
}
function wall(t, day) { const x = new Date(t); return +x <= +day ? 0 : x.getHours() * 60 + x.getMinutes(); }
// side-by-side columns for overlapping events of one day
function layout(list) {
  list.sort((a, b) => a.s - b.s || b.e - a.e);
  let group = [], end = 0;
  const flush = () => { const n = Math.max(1, ...group.map(g => g.col + 1)); for (const g of group) g.cols = n; group = []; };
  for (const t of list) {
    if (group.length && t.s >= end) flush();
    const used = new Set(group.filter(g => g.e > t.s).map(g => g.col));
    let c = 0; while (used.has(c)) c++;
    t.col = c; group.push(t); end = Math.max(end, +t.e);
  }
  flush();
}

function renderAgenda() {
  const g = $('grid'), box = el('div', 'agenda'), today = new Date();
  let any = false;
  for (const d of days()) {
    const next = addDays(d, 1);
    const list = items.filter(i => i.allDay ? localDate(i.date) < next && localDate(i.endDate) > d : new Date(i.start) < next && new Date(i.end) > d);
    if (!list.length) continue;
    any = true;
    box.append(el('div', 'day' + (sameDay(d, today) ? ' today' : ''), fmtLong.format(d)));
    for (const i of list) {
      const b = evButton(i);
      b.append(el('span', 't', i.allDay ? 'весь день' : fmtTime.format(new Date(i.start)) + '–' + fmtTime.format(new Date(i.end))), el('span', '', i.summary || '(без названия)'));
      box.append(b);
    }
  }
  if (!any) box.append(el('p', 'none', 'Встреч нет'));
  g.replaceChildren(box);
}

let cardKey = '', opener = null;
async function openCard(i) {
  cardKey = i.key; opener = document.activeElement;
  $('card-title').textContent = i.summary || '(без названия)';
  $('card-body').replaceChildren(el('p', 'small', 'Загрузка…'));
  $('card').hidden = false; $('card-close').focus();
  try {
    const e = await call('event', {key: i.key});
    if (cardKey !== i.key) return;
    const body = $('card-body'); body.replaceChildren();
    if (e.tainted) body.append(el('p', 'notice', 'Текст этого события похож на попытку управлять агентом; показан только как текст.'));
    const when = i.allDay ? fmtLong.format(localDate(i.date)) + (i.endDate && localDate(i.endDate) - localDate(i.date) > DAY ? ' – ' + fmtLong.format(addDays(localDate(i.endDate), -1)) : '') + ', весь день'
      : fmtWhen.format(new Date(i.start)) + ' – ' + fmtTime.format(new Date(i.end));
    body.append(el('p', '', when));
    const myTz = Intl.DateTimeFormat().resolvedOptions().timeZone;
    if (!i.allDay && e.tz && e.tz !== myTz) {
      const there = new Intl.DateTimeFormat(undefined, {hour: '2-digit', minute: '2-digit', timeZone: e.tz}).format(new Date(i.start));
      body.append(el('p', 'small', 'В часовом поясе события (' + e.tz + '): ' + there));
    }
    if (i.conflict) body.append(el('p', 'notice', 'Пересекается с другой встречей'));
    else if (i.softConflict) body.append(el('p', 'notice', 'Приходится на занятый день (отпуск, поездка или другое событие на весь день)'));
    if (e.location) body.append(el('p', '', '📍 ' + e.location));
    if (e.video) body.append(link('🎥 Видеозвонок', e.video));
    if (e.series) body.append(el('p', 'small', 'Повторяющееся событие'));
    const my = {accepted: 'вы приняли', declined: 'вы отклонили', tentative: 'вы ответили «может быть»', needsAction: 'вы ещё не ответили'}[e.my];
    if (e.organizer && !e.organizerSelf) body.append(el('p', 'small', 'Организатор: ' + e.organizer));
    if (my) body.append(el('p', '', my));
    if (e.attendees?.length) {
      const d = el('details'); d.append(el('summary', '', 'Участники: ' + e.attendees.length));
      const word = {accepted: 'да', declined: 'нет', tentative: 'может быть', needsAction: 'нет ответа'};
      for (const a of e.attendees) { const r = el('div', 'att'); r.append(el('span', '', a.name || a.email), el('span', 'small', word[a.response] || '')); d.append(r); }
      body.append(d);
    }
    if (e.description) body.append(el('div', 'desc', e.description));
    for (const u of e.links || []) if (u !== e.video) body.append(link(u, u));
    if (e.htmlLink) body.append(link('Открыть в Google Calendar', e.htmlLink));
  } catch (err) { if (cardKey === i.key) showError($('card-body'), err, () => openCard(i)); }
}
function link(text, url) {
  const p = el('p'), a = el('a', '', text);
  if (!/^(https:|mailto:)/i.test(url)) return el('p', 'small', text);
  a.href = url; a.target = '_blank'; a.rel = 'noopener noreferrer'; p.append(a); return p;
}
function closeCard() { $('card').hidden = true; cardKey = ''; opener?.focus?.(); }

function move(dir) {
  anchor = view === 'week' ? addDays(anchor, 7 * dir) : view === 'day' ? addDays(anchor, dir) : addDays(anchor, AGENDA_DAYS * dir);
  load();
}
$('prev').addEventListener('click', () => move(-1));
$('next').addEventListener('click', () => move(1));
$('today').addEventListener('click', () => { anchor = startOfDay(new Date()); $('grid').dataset.scrolled = ''; load(); });
for (const b of document.querySelectorAll('.views button')) b.addEventListener('click', () => { view = b.dataset.view; $('grid').dataset.scrolled = ''; load(); });
$('card-close').addEventListener('click', closeCard);
$('cals-toggle').addEventListener('click', () => { const open = document.body.classList.toggle('cals-open'); $('cals-toggle').setAttribute('aria-expanded', String(open)); });
document.addEventListener('keydown', e => {
  if (e.composedPath().some(n => n.matches?.('input,textarea,select'))) return;
  if (e.key === 'Escape' && !$('card').hidden) { e.preventDefault(); e.stopPropagation(); closeCard(); return; }
  if (e.ctrlKey || e.metaKey || e.altKey) return;
  if (e.key === 'ArrowLeft' || e.key === 'p') move(-1);
  else if (e.key === 'ArrowRight' || e.key === 'n') move(1);
  else if (e.key === 't') $('today').click();
  else if (e.key === 'd' || e.key === 'w' || e.key === 'a') { view = {d: 'day', w: 'week', a: 'agenda'}[e.key]; load(); }
}, true);
// swipe in the day view (phone)
let touchX = null;
$('grid').addEventListener('touchstart', e => { touchX = e.touches.length === 1 ? e.touches[0].clientX : null; }, {passive: true});
$('grid').addEventListener('touchend', e => {
  if (touchX == null || view === 'week') return;
  const dx = e.changedTouches[0].clientX - touchX; touchX = null;
  if (Math.abs(dx) > 70) move(dx < 0 ? 1 : -1);
}, {passive: true});

coExt.ready.then(ctx => {
  theme(ctx); renderFrame();
  coExt.on('context', next => { theme(next); status(); });
  status();
  // the relay allows few waiting calls: the view is read again at most once a minute, and only while visible
  refreshTimer = setInterval(() => { if (!document.hidden && relay && st.online) load(); }, 60000);
}).catch(err => showError($('connection'), err, () => location.reload()));
window.addEventListener('pagehide', () => { clearTimeout(timer); clearInterval(refreshTimer); statusTicket++; loadTicket++; });
})();
