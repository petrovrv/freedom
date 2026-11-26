// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.14.0 (ext/1 ui.chatPart, CO Web 0.130.0): the Correspondence part of a chat folder - the mail threads the person put
// into this chat (✉ "🗂 В чат…"). The mail stays on the home server; this page asks it through home-relay (end-to-end
// sealed) for the threads of ctx.chat only. A click opens the thread in ✉ left of this chat (its viewer), where the
// person reads and answers it; nothing here changes Gmail.
(() => {
'use strict';
const $ = id => document.getElementById(id);
let ctx = null, relay = null, st = {}, ticket = 0;
function el(tag, cls, text) { const n = document.createElement(tag); if (cls) n.className = cls; if (text !== undefined) n.textContent = text; return n; }
function theme(c) { document.documentElement.dataset.theme = ['dark', 'light'].includes(c.theme) ? c.theme : ''; }
function fit() { requestAnimationFrame(() => coExt.size(0, Math.min(900, document.body.scrollHeight + 2), false)); }
function date(v) {
  const d = new Date(Number(v)), now = new Date();
  if (Number.isNaN(d.getTime())) return '';
  return d.toDateString() === now.toDateString() ? d.toLocaleTimeString('ru', {hour: '2-digit', minute: '2-digit'})
    : d.toLocaleDateString('ru', d.getFullYear() === now.getFullYear() ? {day: 'numeric', month: 'short'} : {day: 'numeric', month: 'short', year: 'numeric'});
}
function chat() { const c = ctx && ctx.chat; return c && typeof c.chatId === 'string' && typeof c.projectId === 'string' ? c : null; }
// the chat with ✉ as its viewer: "Почта t-<thread>" opens that thread among the chat's threads, "Почта c" their list
function open(doc) {
  const c = chat(); if (!c) return;
  coExt.req('chat.open', {projectId: c.projectId, chatId: c.chatId, viewer: doc, mode: 'mail'}).catch(() => say('Не удалось открыть почту'));
}
function say(text, retry) {
  const n = $('note'); n.replaceChildren(document.createTextNode(text));
  if (retry) { const b = el('button', 'link', 'Повторить'); b.type = 'button'; b.addEventListener('click', retry); n.append(b); }
  fit();
}
const messages = {offline: 'Домашний сервер не отвечает', timeout: 'Сервер не успел ответить', not_paired: 'Почта не подключена', not_served: 'Домашний сервер не обслуживает почту', busy: 'Слишком много запросов'};
async function load() {
  const my = ++ticket, c = chat();
  if (!c) { say('Нет чата'); return; }
  try {
    st = await coExt.call('mail_status', {});
    if (my !== ticket) return;
    if (!st.paired) throw new Error('not_paired');
    if (!st.online) throw new Error('offline');
    if (!relay) relay = HomeRelay((op, args, opts) => coExt.call(op, args, opts), {env: st.env, module: 'concierge-mail', op: 'mail_call'});
    const data = await relay.call('chat_threads', {chat: c.chatId, limit: 20}, st.pub);
    if (my !== ticket) return;
    render(data.threads || [], data.next != null);
  } catch (err) { if (my === ticket) say(messages[err.message] || 'Не удалось загрузить переписку', load); }
}
function render(list, more) {
  $('note').textContent = list.length ? '' : 'В этом чате писем нет';
  $('threads').replaceChildren(...list.map(t => {
    const b = el('button', 't' + (t.unread ? ' unread' : '')); b.type = 'button';
    const who = (t.from || []).map(s => String(s).replace(/\s*<[^>]*>\s*$/, '').replace(/^"|"$/g, '')).filter(Boolean).slice(0, 2).join(', ') || '—';
    const row = el('span', 'row'); row.append(el('span', 'who', who + (t.count > 1 ? ' (' + t.count + ')' : '')), el('span', 'date', date(t.date)));
    const subj = el('span', 'subj', t.sensitive ? 'Письмо с кодом входа — содержимое не хранится' : (t.subject || '(без темы)'));
    if (t.tainted) subj.prepend(el('span', 'flag', '⚠ '));
    b.append(row, subj);
    b.title = 'Открыть в ✉ рядом с чатом: прочитать и ответить';
    b.addEventListener('click', () => open('Почта t-' + t.thread));
    return b;
  }));
  $('foot').hidden = !list.length && !more;
  fit();
}
$('all').addEventListener('click', () => open('Почта c'));
coExt.ready.then(c => {
  ctx = c; theme(c); load();
  // a new count of this chat (a thread put into it or taken out, CO Web passes ctx.count) or another chat: load again
  coExt.on('context', next => {
    const chatChanged = JSON.stringify(next.chat) !== JSON.stringify(ctx && ctx.chat), countChanged = next.count !== (ctx && ctx.count);
    ctx = next; theme(next);
    if (chatChanged) relay = null;
    if (chatChanged || countChanged) load();
  });
}).catch(() => say('Не удалось загрузить переписку'));
window.addEventListener('pagehide', () => { ticket++; });
})();
