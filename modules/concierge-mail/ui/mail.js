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
function bodyHTML(html, host) {
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
      if (/^(https?:)?\/\//i.test(src)) remote = true;
      // Only embedded raster images; never relative URLs, srcset or SVG data.
      if (/^data:image\/(png|jpeg|gif|webp);base64,[a-z0-9+/=\s]+$/i.test(src)) dest.setAttribute('src', src);
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
    if (bodyHTML(msg.html, body)) head.append(el('span', 'small', 'Картинки из интернета скрыты'));
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
async function openThread(index) {
  const thread = threads[index]; if (!thread) return;
  selected = index; renderThreads(); pane('reader');
  const ticket = ++readTicket;
  $('reading').replaceChildren(el('p', 'empty', 'Загрузка письма…')); $('reader').focus();
  try {
    const data = await call('thread', {thread:thread.thread});
    if (ticket !== readTicket) return;
    const messages = data.messages || [];
    $('reading').replaceChildren(el('h2', '', messages.find(m => !m.sensitive && m.subject)?.subject || 'Переписка'));
    messages.forEach((msg, i) => $('reading').append(message(msg, i === messages.length - 1)));
    if (!messages.length) $('reading').append(el('p', 'empty', 'Писем нет'));
  } catch (err) { if (ticket === readTicket) error($('reading'), err, () => openThread(index)); }
}
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
window.addEventListener('pagehide', () => { clearTimeout(timer); statusTicket++; listTicket++; readTicket++; });
})();
