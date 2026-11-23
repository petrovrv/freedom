// SPDX-License-Identifier: AGPL-3.0-or-later
(() => {
'use strict';
const $ = id => document.getElementById(id);
const names = {INBOX:'Входящие', STARRED:'Помеченные', IMPORTANT:'Важные', SENT:'Отправленные', DRAFT:'Черновики', SPAM:'Спам', TRASH:'Корзина', ALL:'Вся почта'};
const privateNote = 'Письмо с кодом входа или ссылкой для сброса пароля — содержимое не хранится';
let st = {}, relay, queue = Promise.resolve(), timer, statusTicket = 0, listTicket = 0, readTicket = 0;
let label = 'INBOX', query = '', labels = [], threads = [], next = null, selected = -1, listBusy = false, initialized = false;
function el(tag, cls, text) {
  const node = document.createElement(tag);
  if (cls) node.className = cls;
  if (text !== undefined) node.textContent = text;
  return node;
}
function button(text, action) { const b = el('button', '', text); b.type = 'button'; b.addEventListener('click', action); return b; }
function theme(ctx) { document.documentElement.dataset.theme = ['dark','light'].includes(ctx.theme) ? ctx.theme : ''; }
function title(n) { coExt.title('Почта' + (n > 0 ? ' (' + n + ')' : '')); }
function pane(name) { document.body.dataset.pane = name; $('link-bar').hidden = true; }
function error(target, err, retry) {
  const messages = {offline:'Домашний сервер не отвечает', timeout:'Сервер не успел ответить', not_paired:'Почта ещё не подключена', not_served:'Домашний сервер не обслуживает почту', not_bound:'Ящик на домашнем сервере принадлежит другому окружению', bad_request:'Не удалось прочитать запрос', worker_error:'Не удалось получить почту', no_webcrypto:'Шифрование недоступно в этом браузере', busy:'Слишком много запросов, повторите'};
  target.replaceChildren(el('span', '', messages[err.message] || 'Не удалось загрузить почту'), button('Повторить', retry));
}
// Serialize calls: relay.js holds a mutable peer key. Do not rotate it mid-response.
function call(action, args) {
  const env = st.env, active = relay;
  const task = queue.catch(() => {}).then(() => {
    if (env !== st.env || active !== relay) throw new Error('not_paired');
    if (!st.paired) throw new Error('not_paired');
    if (!st.online) throw new Error('offline');
    return active.call(action, args, st.pub);
  });
  queue = task.catch(() => {});
  return task;
}
function reset() {
  listTicket++; readTicket++; initialized = false; listBusy = false;
  threads = []; labels = []; next = null; selected = -1;
  $('labels').replaceChildren(); $('threads').replaceChildren(); $('list-note').replaceChildren(); $('more').hidden = true;
  $('reading').replaceChildren(el('p', 'empty', 'Выберите письмо')); $('link-bar').hidden = true;
}
async function status() {
  clearTimeout(timer);
  const ticket = ++statusTicket;
  try {
    const fresh = await coExt.call('mail_status', {});
    if (ticket !== statusTicket) return;
    if (fresh.error) throw new Error(fresh.error);
    const changed = fresh.env !== st.env || fresh.account !== st.account;
    if (changed || !fresh.paired) { reset(); relay = null; }
    st = fresh; title(st.unread);
    if (!st.paired) $('connection').textContent = 'Почта ещё не подключена: хозяин подключает ваш ящик на домашнем сервере.';
    else if (!st.online) error($('connection'), new Error('offline'), status);
    else {
      if (!relay) relay = HomeRelay((op, args, opts) => coExt.call(op, args, opts), {env: st.env, module: 'concierge-mail', op: 'mail_call'});
      $('connection').textContent = st.account || 'Почта подключена';
      if (st.synced) $('connection').append(document.createTextNode(' · Синхронизация: ' + st.synced));
      if (!initialized) { initialized = true; overview(); loadList(); }
    }
  } catch (err) {
    if (ticket !== statusTicket) return;
    st = {...st, online:false}; error($('connection'), err, status);
  } finally {
    if (ticket === statusTicket) timer = setTimeout(status, st.online || st.paired === false ? 60000 : 15000);
  }
}
async function overview() {
  const env = st.env, account = st.account;
  try {
    const data = await call('overview', {});
    if (env !== st.env || account !== st.account || !st.paired) return;
    labels = data.labels || []; renderLabels(); title(data.unread);
  } catch (err) { if (env === st.env && account === st.account) error($('labels'), err, overview); }
}
function renderLabels() {
  $('labels').replaceChildren();
  const system = Object.keys(names).map(id => ({id, name:names[id], ...labels.find(l => l.id === id)}));
  for (const item of [...system, ...labels.filter(l => !l.system && !names[l.id])]) {
    const b = button('', () => { label = item.id; query = ''; $('search').value = ''; renderLabels(); pane('list'); loadList(); });
    b.append(el('span', '', names[item.id] || item.name));
    if (item.unread) b.append(el('span', 'small', item.unread));
    if (!query && item.id === label) b.setAttribute('aria-current', 'page');
    $('labels').append(b);
  }
}
function date(value, full = false) {
  const d = new Date(Number(value)), today = new Date();
  if (Number.isNaN(d.getTime())) return '';
  if (full) return d.toLocaleString('ru-RU');
  if (d.toDateString() === today.toDateString()) return d.toLocaleTimeString('ru-RU', {hour:'2-digit', minute:'2-digit'});
  return d.toLocaleDateString('ru-RU', {day:'numeric', month:'short', ...(d.getFullYear() !== today.getFullYear() ? {year:'numeric'} : {})});
}
function sender(raw) { return String(raw).replace(/\s*<[^>]*>\s*$/, '').replace(/^"(.*)"$/, '$1').trim() || String(raw); }
function renderThreads() {
  $('threads').replaceChildren();
  threads.forEach((t, index) => {
    const b = button('', () => openThread(index)); b.className = 'thread' + (t.unread ? ' unread' : '');
    b.setAttribute('aria-current', String(index === selected));
    const row = el('span', 'row');
    row.append(el('span', 'sender', (t.from || []).map(sender).join(', ') + (t.count > 1 ? ' (' + t.count + ')' : '')), el('span', 'date', date(t.date)));
    b.append(row, el('span', 'subject', (t.attachments ? '📎 ' : '') + (t.sensitive ? 'Защищённое письмо' : t.subject || '(без темы)')),
      el('span', 'snippet', t.sensitive ? privateNote : t.snippet || ''));
    $('threads').append(b);
  });
}
async function loadList(append = false) {
  if (append && (listBusy || next === null)) return;
  const ticket = ++listTicket;
  if (!append) { readTicket++; threads = []; next = null; selected = -1; renderThreads(); $('reading').replaceChildren(el('p', 'empty', 'Выберите письмо')); }
  listBusy = true; $('more').hidden = true; $('list-note').textContent = 'Загрузка…';
  $('list-title').textContent = query ? 'Поиск: ' + query : names[label] || labels.find(l => l.id === label)?.name || label;
  const args = query ? {q:query, limit:50} : {label, limit:50};
  if (append) args.before = next;
  try {
    const data = await call(query ? 'search' : 'list', args);
    if (ticket !== listTicket) return;
    const known = new Set(threads.map(t => t.thread));
    threads.push(...(data.threads || []).filter(t => !known.has(t.thread))); next = data.next ?? null;
    renderThreads(); $('list-note').textContent = threads.length ? '' : 'Писем нет';
  } catch (err) { if (ticket === listTicket) error($('list-note'), err, () => loadList(append)); }
  finally { if (ticket === listTicket) { listBusy = false; $('more').hidden = next === null; } }
}
function showLink(url) {
  $('link-url').value = url; $('copy-note').textContent = ''; $('link-bar').hidden = false;
  $('link-url').focus(); $('link-url').select();
}
// Rebuild an inert allowlist, dropping all email CSS (including obfuscated @import),
// custom elements, SVG/MathML, active controls and resource-bearing attributes.
function bodyHTML(html, host, images = {}) {
  const doc = new DOMParser().parseFromString(html, 'text/html');
  const allowed = new Set('p div span br hr b strong i em u s del blockquote pre code ul ol li table thead tbody tfoot tr td th h1 h2 h3 h4 h5 h6 a img small sup sub center'.split(' '));
  const drop = new Set('script style iframe frame frameset object embed form link meta base input button textarea select option svg math template noscript video audio source'.split(' '));
  let remote = false;
  function clean(node) {
    if (node.nodeType === Node.TEXT_NODE) return document.createTextNode(node.textContent);
    if (node.nodeType !== Node.ELEMENT_NODE) return document.createDocumentFragment();
    const tag = node.localName.toLowerCase();
    if (drop.has(tag)) return document.createDocumentFragment();
    const dest = allowed.has(tag) ? el(tag) : document.createDocumentFragment();
    if (tag === 'img') {
      const src = (node.getAttribute('src') || '').trim();
      // A remote picture shows only as the data: copy the home server fetched ("Показать картинки").
      const data = /^(https?:)?\/\//i.test(src) ? (Object.hasOwn(images, src) ? images[src] : (remote = true, '')) : src;
      // Only embedded raster images; never relative URLs, srcset or SVG data.
      if (/^data:image\/(png|jpeg|gif|webp);base64,[a-z0-9+/=\s]+$/i.test(data)) dest.setAttribute('src', data);
      dest.setAttribute('alt', node.getAttribute('alt') || 'Изображение');
    }
    if (tag === 'a') {
      const href = (node.getAttribute('href') || '').trim();
      if (/^(https?:\/\/|mailto:|tel:)/i.test(href) && !/[\u0000-\u001f\u007f]/.test(href)) {
        dest.setAttribute('role', 'link'); dest.tabIndex = 0; dest.className = 'link';
        dest.addEventListener('click', e => { e.preventDefault(); showLink(href); });
        dest.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); e.stopPropagation(); showLink(href); } });
      }
    }
    if (tag === 'td' || tag === 'th') for (const attr of ['colspan','rowspan']) {
      const value = node.getAttribute(attr); if (/^[1-9]\d?$/.test(value || '')) dest.setAttribute(attr, value);
    }
    for (const child of node.childNodes) dest.append(clean(child));
    return dest;
  }
  const shadow = host.attachShadow({mode:'closed'}), style = el('style');
  style.textContent = ':host{display:block;color:inherit;font:14px/1.6 system-ui,sans-serif;overflow-wrap:anywhere}img{max-width:100%;height:auto}table{max-width:100%;border-collapse:collapse}td,th{padding:4px;border:1px solid #8886}pre{white-space:pre-wrap}blockquote{border-left:3px solid #8886;margin:12px 0;padding-left:12px}.link{color:var(--accent);text-decoration:underline;cursor:pointer}.link:focus-visible{outline:2px solid var(--accent)}';
  shadow.append(style);
  for (const child of doc.body.childNodes) shadow.append(clean(child));
  return remote;
}
function bodyText(text, host) {
  host.className = 'plain';
  for (const part of String(text || '').split(/(https?:\/\/[^\s<>]+)/gi)) {
    host.append(/^https?:\/\//i.test(part) ? el('span', 'plain-url', part) : document.createTextNode(part));
  }
}
// The home server fetches the pictures (the sender sees the home server, not this device) and answers data: copies.
function imagesBar(msg, body) {
  const bar = el('div', 'images-bar'), note = el('span', 'small', 'Картинки из интернета скрыты');
  const show = button('Показать картинки', async () => {
    const ticket = readTicket;
    show.disabled = true; note.textContent = 'Загрузка картинок…';
    try {
      const data = await call('images', {id: msg.id});
      if (ticket !== readTicket) return;
      const images = data.images || {}, got = Object.keys(images).length, missed = Object.keys(data.skipped || {}).length;
      const fresh = el('div', 'mail-body'); bodyHTML(msg.html, fresh, images); body.replaceWith(fresh); body = fresh;
      note.textContent = missed ? `Показано ${got} из ${got + missed}` : 'Картинки показаны';
      show.textContent = 'Ещё раз'; show.disabled = !missed;
    } catch (err) {
      if (ticket !== readTicket) return;
      note.textContent = 'Картинки не загрузились'; show.disabled = false;
    }
  });
  bar.append(note, show);
  return bar;
}
function size(bytes) { return new Intl.NumberFormat('ru-RU', {maximumFractionDigits:1}).format((Number(bytes) || 0) / 1024) + ' КБ'; }
function message(msg, last) {
  const card = el('details', 'message'); card.open = last || (msg.labels || []).includes('UNREAD');
  const head = el('summary'); head.append(el('span', '', msg.from || 'Неизвестный отправитель'), el('span', 'small', date(msg.date, true)));
  card.append(head);
  const content = el('div', 'message-content'); card.append(content);
  if (msg.sensitive) { content.append(el('p', 'sensitive', privateNote)); return card; }
  head.append(el('span', 'small', 'Кому: ' + (msg.to || '—')));
  if (msg.subject) head.append(el('span', 'small', msg.subject));
  if (msg.tainted) content.append(el('p', 'notice', 'Письмо может содержать попытку управлять ИИ (read-guard): ' + (msg.reasons || []).join('; ')));
  const body = el('div', 'mail-body');
  if (msg.html) {
    if (bodyHTML(msg.html, body)) content.append(imagesBar(msg, body));
  } else bodyText(msg.text, body);
  content.append(body);
  for (const att of msg.attachments || []) {
    const line = el('div', 'attachment', '📎 ' + (att.name || 'Вложение') + ' · ' + size(att.size));
    // The declared CSP sandbox lacks allow-downloads. Browser download blocking is
    // silent, so a speculative Blob click cannot detect success. Do not offer it.
    line.append(el('div', 'small', Number(att.size) > 4 * 1024 * 1024 ? 'Файл больше 4 МБ — откройте его в Gmail' : 'Скачивание недоступно в этой странице — откройте файл в Gmail'));
    content.append(line);
  }
  return card;
}
const consentNote = 'Нужно разрешение Google писать и отмечать письма: хозяин подключает его для вашего ящика (hubctl connect).';
function writeError(err) {
  const m = String(err && err.message || err);
  return {needs_consent: consentNote, 'already sending': 'Письмо уже отправляется', 'no recipients': 'Укажите получателя',
    'too big': 'Вложения больше 20 МБ', 'save first': 'Черновик ещё не сохранён', 'protected message': 'На защищённое письмо так не ответить'}[m]
    || (/^bad address/.test(m) ? 'Неверный адрес: ' + m.replace(/^bad address:\s*/, '') : 'Не получилось: ' + m);
}
function toolbar(thread, messages) {
  const bar = $('toolbar'); bar.replaceChildren(); bar.hidden = false;
  const last = [...messages].reverse().find(m => !m.sensitive && !(m.labels || []).includes('DRAFT'));
  const labels = new Set(messages.flatMap(m => m.labels || []));
  const act = (text, fn, title) => { const b = button(text, fn); if (title) b.title = title; bar.append(b); return b; };
  if (last) {
    act('↩ Ответить', () => compose('reply', last.id));
    act('↩↩ Всем', () => compose('replyall', last.id), 'Ответить всем');
    act('↪ Переслать', () => compose('forward', last.id));
  }
  const mark = (what, done) => async () => {
    try { await call('mark', {thread: thread.thread, mark: what}); done && done(); refreshList(); }
    catch (err) { note(writeError(err)); }
  };
  if (labels.has('INBOX')) act('📥 В архив', mark('archive', () => pane('list')));
  else if (!labels.has('TRASH') && !labels.has('SPAM')) act('📥 Во входящие', mark('inbox'));
  act(labels.has('UNREAD') ? '✉ Прочитано' : '✉ Не прочитано', mark(labels.has('UNREAD') ? 'read' : 'unread', () => openThread(selected, false)));
  act(labels.has('STARRED') ? '★ Снять' : '☆ Пометить', mark(labels.has('STARRED') ? 'unstar' : 'star', () => openThread(selected, false)));
  if (labels.has('TRASH')) act('↺ Восстановить', mark('untrash', () => pane('list')));
  else act('🗑 Удалить', mark('trash', () => pane('list')));
}
function note(text) { const n = el('p', 'notice', text); $('reading').prepend(n); setTimeout(() => n.remove(), 8000); }
function refreshList() { overview(); loadList(); }
async function openThread(index, markRead = true) {
  const thread = threads[index]; if (!thread) return;
  selected = index; renderThreads(); pane('reader');
  const ticket = ++readTicket;
  $('toolbar').hidden = true;
  $('reading').replaceChildren(el('p', 'empty', 'Загрузка письма…')); $('reader').focus();
  try {
    const data = await call('thread', {thread:thread.thread});
    if (ticket !== readTicket) return;
    const messages = data.messages || [];
    $('reading').replaceChildren(el('h2', '', messages.find(m => !m.sensitive && m.subject)?.subject || 'Переписка'));
    messages.forEach((msg, i) => {
      const card = message(msg, i === messages.length - 1);
      if ((msg.labels || []).includes('DRAFT') && !msg.sensitive) card.querySelector('.message-content').prepend(button('✏ Продолжить черновик', () => openDraft(msg.id)));
      $('reading').append(card);
    });
    if (!messages.length) $('reading').append(el('p', 'empty', 'Писем нет'));
    toolbar(thread, messages);
    // opening a letter reads it, as in Gmail (silently skipped until writing is allowed)
    if (markRead && thread.unread) call('mark', {thread: thread.thread, mark: 'read'}).then(() => { thread.unread = 0; renderThreads(); overview(); }).catch(() => {});
  } catch (err) { if (ticket === readTicket) error($('reading'), err, () => openThread(index)); }
}

// ---- writing (module home/compose.py on the home server; everything only on the person's click)
let draft = null, saveTimer = 0, saving = null, undoTimer = 0;
const fieldIds = {to:'c-to', cc:'c-cc', bcc:'c-bcc', subject:'c-subject', text:'c-text'};
function fields() { const f = {}; for (const [k, id] of Object.entries(fieldIds)) f[k] = $(id).value; return f; }
function cstate(text, bad) { $('c-note').textContent = text || ''; $('c-note').className = 'small' + (bad ? ' err' : ''); }
function showComposer(d) {
  draft = d; const f = d.fields || {};
  for (const [k, id] of Object.entries(fieldIds)) $(id).value = f[k] || '';
  for (const n of document.querySelectorAll('.cc')) n.hidden = !(f.cc || f.bcc);
  $('c-more').hidden = !!(f.cc || f.bcc);
  $('composer-title').textContent = {reply:'Ответ', replyall:'Ответ всем', forward:'Пересылка'}[f.kind] || 'Новое письмо';
  renderAttachments(); cstate(''); $('composer-state').textContent = d.draft ? 'Черновик сохранён' : '';
  $('composer').hidden = false;
  (f.to ? $('c-text') : $('c-to')).focus();
  if (f.kind === 'reply' || f.kind === 'replyall') $('c-text').setSelectionRange(0, 0);
}
function renderAttachments() {
  const box = $('c-attachments'); box.replaceChildren();
  for (const a of draft?.attachments || []) {
    const chip = el('span', 'chip', '📎 ' + a.name + ' · ' + size(a.size));
    chip.append(button('×', async () => {
      try { await flush(); const out = await call('compose_detach', {key: draft.key, sha: a.sha}); Object.assign(draft, out); renderAttachments(); }
      catch (err) { cstate(writeError(err), true); }
    }));
    box.append(chip);
  }
}
async function compose(kind, of) {
  if (draft && !$('composer').hidden) await closeComposer();
  try { showComposer(await call('compose_start', {kind, of})); }
  catch (err) { note(writeError(err)); }
}
async function openDraft(id) {
  if (draft && !$('composer').hidden) await closeComposer();
  try { showComposer(await call('compose_open', {id})); }
  catch (err) { note(writeError(err)); }
}
function scheduleSave() { clearTimeout(saveTimer); $('composer-state').textContent = 'Изменено'; saveTimer = setTimeout(flush, 2500); }
async function flush() {
  clearTimeout(saveTimer);
  if (!draft) return;
  if (saving) return saving;
  const d = draft;
  saving = (async () => {
    try {
      const out = await call('compose_save', {key: d.key, fields: {...d.fields, ...fields()}, ...(d.pending?.length && !d.draft ? {pending: d.pending} : {})});
      d.draft = out.draft; d.attachments = out.attachments; d.fields = {...d.fields, ...fields()};
      if (d === draft) { $('composer-state').textContent = 'Черновик сохранён'; renderAttachments(); cstate(''); }
    } catch (err) { if (d === draft) cstate(writeError(err), true); throw err; }
    finally { saving = null; }
  })();
  return saving;
}
async function closeComposer() {
  const empty = !fields().to && !fields().subject && !fields().text.trim() && !(draft?.attachments || []).length;
  try { if (draft && !(empty && !draft.draft)) await flush(); } catch (_) { return; }
  $('composer').hidden = true; draft = null;
}
async function attach(files) {
  if (!draft) return;
  try { if (!draft.draft) await flush(); } catch (_) { return; }
  for (const file of files) {
    const upload = Array.from(crypto.getRandomValues(new Uint8Array(8)), b => b.toString(16).padStart(2, '0')).join('');
    const bytes = new Uint8Array(await file.arrayBuffer()), PIECE = 384 * 1024;
    try {
      for (let offset = 0; offset < bytes.length || offset === 0; offset += PIECE) {
        const piece = bytes.subarray(offset, offset + PIECE), last = offset + PIECE >= bytes.length;
        cstate(`Загрузка ${file.name}: ${Math.round(100 * Math.min(bytes.length, offset + PIECE) / Math.max(1, bytes.length))}%`);
        let bin = ''; for (let i = 0; i < piece.length; i += 8192) bin += String.fromCharCode(...piece.subarray(i, i + 8192));
        const out = await call('compose_upload', {key: draft.key, upload, name: file.name, type: file.type || 'application/octet-stream', offset, last, data: btoa(bin)});
        if (last) { Object.assign(draft, {draft: out.draft, attachments: out.attachments}); renderAttachments(); break; }
      }
      cstate('');
    } catch (err) { cstate(file.name + ': ' + writeError(err), true); }
  }
}
async function send() {
  if (!draft) return;
  clearTimeout(saveTimer);
  const d = draft;
  try { if (saving) await saving; } catch (_) {}
  $('c-send').disabled = true;
  try {
    const out = await call('compose_send', {key: d.key, fields: {...d.fields, ...fields()}, ...(d.pending?.length && !d.draft ? {pending: d.pending} : {})});
    d.fields = {...d.fields, ...fields()};
    $('composer').hidden = true; draft = null;
    undo(d, out.undo || 10);
  } catch (err) { cstate(writeError(err), true); }
  finally { $('c-send').disabled = false; }
}
function undo(d, seconds) {
  clearInterval(undoTimer);
  let left = seconds;
  const bar = $('undo-bar'); bar.hidden = false; $('undo').hidden = false; $('undo').textContent = 'Отменить';
  const tick = () => { $('undo-text').textContent = left > 0 ? `Отправка через ${left} с` : 'Отправляется…'; left--; };
  tick(); undoTimer = setInterval(() => { tick(); if (left < -1) { clearInterval(undoTimer); $('undo').hidden = true; result(d, 0); } }, 1000);
  $('undo').onclick = async () => {
    clearInterval(undoTimer);
    try {
      const out = await call('compose_unsend', {key: d.key});
      if (out.cancelled) { bar.hidden = true; showComposer(d); cstate('Отправка отменена, письмо — снова черновик'); }
      else { $('undo').hidden = true; result(d, 0); }
    } catch (err) { $('undo-text').textContent = writeError(err); }
  };
}
async function result(d, tries) {
  try {
    const items = (await call('compose_outbox', {})).items || [];
    const it = items.find(i => i.key === d.key);
    if (it && (it.state === 'wait' || it.state === 'sending') && tries < 10) { setTimeout(() => result(d, tries + 1), 1500); return; }
    if (it && it.state === 'sent') { $('undo-text').textContent = 'Письмо отправлено'; setTimeout(() => { $('undo-bar').hidden = true; }, 6000); refreshList(); return; }
    $('undo-text').textContent = 'Не отправлено' + (it?.error ? ': ' + writeError(it.error) : '') + ' — письмо осталось в черновиках';
    $('undo').hidden = false; $('undo').textContent = 'Открыть';
    $('undo').onclick = () => { $('undo-bar').hidden = true; $('undo').textContent = 'Отменить'; showComposer(d); };
  } catch (err) { if (tries < 10) setTimeout(() => result(d, tries + 1), 3000); }
}
$('compose-new').addEventListener('click', () => compose('new'));
$('composer-close').addEventListener('click', closeComposer);
$('c-more').addEventListener('click', () => { for (const n of document.querySelectorAll('.cc')) n.hidden = false; $('c-more').hidden = true; $('c-cc').focus(); });
for (const id of Object.values(fieldIds)) $(id).addEventListener('input', scheduleSave);
$('c-file').addEventListener('change', e => { const files = [...e.target.files]; e.target.value = ''; attach(files); });
$('c-send').addEventListener('click', send);
$('c-discard').addEventListener('click', async () => {
  if (!draft) return;
  clearTimeout(saveTimer);
  try { if (saving) await saving; } catch (_) {}
  try { await call('compose_discard', {key: draft.key}); $('composer').hidden = true; draft = null; }
  catch (err) { cstate(writeError(err), true); }
});
$('composer').addEventListener('keydown', e => {
  if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') { e.preventDefault(); send(); }
  else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); closeComposer(); }
});
$('undo-close').addEventListener('click', () => { $('undo-bar').hidden = true; });
function back() {
  if (document.body.dataset.pane === 'reader') { pane('list'); $('threads').children[selected]?.focus(); }
  else { pane(document.body.dataset.pane === 'labels' ? 'list' : 'labels'); }
}
$('back').addEventListener('click', back);
$('search-form').addEventListener('submit', e => { e.preventDefault(); query = $('search').value.trim(); renderLabels(); pane('list'); loadList(); });
$('search').addEventListener('keydown', e => { if (e.key === 'Escape') { e.preventDefault(); query = ''; $('search').value = ''; renderLabels(); pane('list'); loadList(); $('search').blur(); } });
$('refresh').addEventListener('click', () => { overview(); pane('list'); loadList(); });
$('more').addEventListener('click', () => loadList(true));
$('close-link').addEventListener('click', () => { $('link-bar').hidden = true; $('reader').focus(); });
$('copy').addEventListener('click', async () => {
  const url = $('link-url').value;
  try { await navigator.clipboard.writeText(url); $('copy-note').textContent = 'Скопировано'; }
  catch (_) { $('link-url').focus(); $('link-url').select(); $('copy-note').textContent = 'Адрес выделен — скопируйте его вручную'; }
});
document.addEventListener('keydown', e => {
  if (e.ctrlKey || e.metaKey || e.altKey || e.composedPath().some(n => n.matches?.('input,textarea,select,[contenteditable="true"]'))) return;
  if (e.key === '/') { e.preventDefault(); $('search').focus(); }
  else if (e.key === 'u') { e.preventDefault(); back(); }
  else if (e.key === 'j' || e.key === 'k') {
    e.preventDefault(); if (!threads.length) return;
    const index = Math.max(0, Math.min(threads.length - 1, selected < 0 ? 0 : selected + (e.key === 'j' ? 1 : -1)));
    if (document.body.dataset.pane === 'reader') openThread(index);
    else { selected = index; renderThreads(); $('threads').children[index]?.focus(); }
  } else if (e.key === 'Enter' && !e.composedPath().some(n => n.matches?.('button,summary,[role="link"]')) && selected >= 0) { e.preventDefault(); openThread(selected); }
});
coExt.ready.then(ctx => {
  theme(ctx); title(0);
  coExt.on('context', nextCtx => { theme(nextCtx); status(); });
  status();
}).catch(err => error($('connection'), err, () => location.reload()));
window.addEventListener('pagehide', () => { if (draft) flush().catch(() => {}); clearTimeout(timer); statusTicket++; listTicket++; readTicket++; });
})();
