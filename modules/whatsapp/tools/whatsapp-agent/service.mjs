// whatsapp-agent service: the agent's own WhatsApp account as a linked device (whatsapp-web.js in headless Edge, own
// profile, no window). Local HTTP API on 127.0.0.1 guarded by a token from the environment (the CLI takes it from
// keyring). Incoming direct messages from saved contacts of the account go to inbox.jsonl and, with --forward, to a
// command that answers them (the CO module); everyone else is logged as ignored and never answered.
import { createRequire } from 'node:module';
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';

const args = Object.fromEntries(process.argv.slice(2).map((a, i, all) => a.startsWith('--') ? [a.slice(2), all[i + 1] && !all[i + 1].startsWith('--') ? all[i + 1] : true] : null).filter(Boolean));
const STATE = args.state;
const RUNTIME = args.runtime;
const PORT = Number(args.port || 8789);
const TOKEN = process.env.WA_AGENT_TOKEN || '';
const EDGE = args.edge || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
const FORWARD = typeof args.forward === 'string' ? args.forward : '';
const PUSHES = typeof args.pushes === 'string' ? args.pushes : '';
// a runner started before 0.5.0 passes no --transcribe: the same CLI as --pushes then does it
const TRANSCRIBE = typeof args.transcribe === 'string' ? args.transcribe
  : / pushes$/.test(PUSHES) ? PUSHES.replace(/ pushes$/, ' transcribe --file') : '';
if (!STATE || !RUNTIME || TOKEN.length < 16) { console.error('need --state, --runtime and WA_AGENT_TOKEN'); process.exit(2); }

const require = createRequire(path.join(RUNTIME, 'package.json'));
const { Client, LocalAuth } = require('whatsapp-web.js');
const QRCode = require('qrcode');

fs.mkdirSync(STATE, { recursive: true });
const st = { state: 'starting', me: null, qrAt: null, pairingCode: null, error: null, startedAt: new Date().toISOString() };
const writeStatus = () => fs.writeFileSync(path.join(STATE, 'status.json'), JSON.stringify({ ...st, pid: process.pid, port: PORT }, null, 1));
const log = (o) => fs.appendFileSync(path.join(STATE, 'events.jsonl'), JSON.stringify({ t: new Date().toISOString(), ...o }) + '\n');
writeStatus();

const client = new Client({
  authStrategy: new LocalAuth({ dataPath: path.join(STATE, 'auth') }),
  // WhatsApp Web stops making new QR codes after a few minutes; the service then exits and `run` starts a fresh one
  qrMaxRetries: 4,
  // WhatsApp refused to link a headless browser ("couldn't link device"); a real window kept off the screen links fine
  puppeteer: { executablePath: EDGE, headless: args.headless === true,
    args: ['--no-first-run', '--disable-extensions', '--window-position=-3000,-3000', '--window-size=1000,800',
      // an off-screen window counts as occluded: without these the page is throttled and never gets to "ready"
      '--disable-backgrounding-occluded-windows', '--disable-renderer-backgrounding', '--disable-background-timer-throttling'] },
});

let pairPhone = typeof args['pair-phone'] === 'string' ? args['pair-phone'].replace(/\D/g, '') : '';
client.on('qr', async (qr) => {
  st.state = 'login_required'; st.qrAt = new Date().toISOString();
  try {
    if (pairPhone) {
      st.pairingCode = await client.requestPairingCode(pairPhone);
      pairPhone = ''; // one code per start; the next qr events only refresh the picture
    }
    await QRCode.toFile(path.join(STATE, 'qr.png'), qr, { width: 360, margin: 2 });
    if (typeof args['qr-out'] === 'string') fs.copyFileSync(path.join(STATE, 'qr.png'), args['qr-out']);
  } catch (e) { st.error = String(e.message || e); }
  writeStatus();
});
client.on('authenticated', () => { st.state = 'authenticated'; st.pairingCode = null; writeStatus(); log({ ev: 'authenticated' }); });
// whatsapp-web.js 1.34.8 with WhatsApp Web 2.3000.105x: the page syncs ("authenticated") but the library's own ready
// step never finishes. After 45 s without "ready" the same steps run here: utils, client info, event listeners.
let readyFallback = null;
async function finishReady() {
  if (st.state === 'ready') return;
  const page = client.pupPage;
  const step = { s: 'utils' };
  try {
    const { LoadUtils } = require('whatsapp-web.js/src/util/Injected/Utils');
    if (!(await page.evaluate(() => typeof window.WWebJS !== 'undefined'))) await page.evaluate(LoadUtils);
    step.s = 'info';
    const ClientInfo = require('whatsapp-web.js/src/structures/ClientInfo');
    const InterfaceController = require('whatsapp-web.js/src/util/InterfaceController');
    client.info = new ClientInfo(client, await page.evaluate(() => ({ ...window.require('WAWebConnModel').Conn.serialize(),
      wid: window.require('WAWebUserPrefsMeUser').getMaybeMePnUser() || window.require('WAWebUserPrefsMeUser').getMaybeMeLidUser() })));
    client.interface = new InterfaceController(client);
    step.s = 'listeners';
    await client.attachEventListeners();
    log({ ev: 'ready_fallback' });
    client.emit('ready');
  } catch (e) { log({ ev: 'ready_fallback_failed', step: step.s, e: String(e?.stack || e).slice(0, 1500) }); }
}
client.on('authenticated', () => { clearTimeout(readyFallback); readyFallback = setTimeout(finishReady, 45000); });
client.on('auth_failure', (m) => { st.state = 'auth_failure'; st.error = String(m); writeStatus(); log({ ev: 'auth_failure', m: String(m) }); });
client.on('ready', () => {
  st.state = 'ready'; st.me = client.info?.wid?.user || null; st.error = null; writeStatus(); log({ ev: 'ready', me: st.me });
  try { fs.rmSync(path.join(STATE, 'qr.png'), { force: true }); } catch {}
});
client.on('disconnected', (r) => { try { fs.rmSync(path.join(STATE, 'qr.png'), { force: true }); } catch {} st.state = 'disconnected'; st.error = String(r); writeStatus(); log({ ev: 'disconnected', r: String(r) }); setTimeout(() => process.exit(3), 1000); });

// Policy (config.json next to state/, re-read on every message):
//   family {digits: env}   - household members: direct messages become normal jobs in their own chat
//   names [..]             - words that call the agent in a group (besides a mention or a reply to it)
//   limits {hour, day}     - turns per contact/group outside the family (a flood costs the owner's subscription)
// Direct messages from other saved contacts and calls in groups become isolated jobs; the rest is only logged.
const CONFIG = path.join(STATE, '..', 'config.json');
const readConfig = () => { try { return JSON.parse(fs.readFileSync(CONFIG, 'utf8')); } catch { return {}; } };
const digits = (id) => String(id || '').split('@')[0].replace(/\D/g, '');

const phoneCache = new Map();
async function phoneOf(id) {
  if (!id) return '';
  if (id.endsWith('@c.us')) return digits(id);
  if (phoneCache.has(id)) return phoneCache.get(id);
  let pn = '';
  try { pn = digits((await client.getContactLidAndPhone([id]))[0]?.pn); } catch {}
  phoneCache.set(id, pn);
  return pn;
}

const turns = new Map(); // key -> [ms...]
function overLimit(key, cfg) {
  const now = Date.now(), lim = { hour: 20, day: 80, ...(cfg.limits || {}) };
  const list = (turns.get(key) || []).filter((t) => now - t < 86400000);
  turns.set(key, list);
  if (list.filter((t) => now - t < 3600000).length >= lim.hour || list.length >= lim.day) return true;
  list.push(now);
  return false;
}

// one answer at a time per chat; messages that arrive meanwhile are joined into the next turn
const busy = new Map();
const pending = new Map();
function runForward(item) {
  return new Promise((resolve) => {
    const p = spawn(FORWARD, { shell: true, windowsHide: true, env: process.env });
    let out = '', err = '';
    p.stdout.on('data', (d) => out += d); p.stderr.on('data', (d) => err += d);
    p.on('close', (code) => resolve({ code, out, err }));
    p.stdin.end(JSON.stringify(item));
  });
}
async function handle(chatId) {
  if (busy.get(chatId)) return;
  const items = pending.get(chatId); if (!items?.length) return;
  pending.delete(chatId); busy.set(chatId, true);
  const item = { ...items[items.length - 1], text: items.map((i) => (items.length > 1 && i.kind === 'group' ? `${i.name || i.number}: ` : '') + i.text).join('\n') };
  try {
    const r = await runForward(item);
    let ans = null; try { ans = JSON.parse(r.out.trim().split('\n').pop() || '{}'); } catch {}
    let reply = ans?.reply ? String(ans.reply) : '';
    // [[owner: ...]] lines are for the owner, never for the conversation
    const forOwner = [];
    reply = reply.replace(/^\s*\[\[owner:\s*([\s\S]*?)\]\]\s*$/gim, (_, t) => { forOwner.push(t.trim()); return ''; }).trim();
    if (reply) {
      await client.sendMessage(chatId, reply, item.kind === 'group' && item.msgId ? { quotedMessageId: item.msgId } : {});
      log({ ev: 'replied', to: chatId, kind: item.kind, chars: reply.length, job: ans.jobId || null });
    } else log({ ev: 'forward_no_reply', to: chatId, code: r.code, out: r.out.slice(-500), err: r.err.slice(-500) });
    const owner = Object.entries(readConfig().family || {}).find(([, env]) => env === readConfig().env)?.[0];
    if (forOwner.length && owner) {
      const where = item.kind === 'group' ? `группа «${item.groupName}», ${item.name || item.number}` : `${item.name || ''} +${item.number}`;
      await client.sendMessage(`${owner}@c.us`, `🔔 ${where}: ${forOwner.join(' ')}\nРазрешить? Ответьте мне здесь, например: «можно продолжать с ${item.name || item.number} о …».`);
      log({ ev: 'asked_owner', about: chatId });
    }
  } catch (e) { log({ ev: 'forward_error', to: chatId, e: String(e.message || e) }); }
  busy.set(chatId, false);
  if (pending.get(chatId)?.length) handle(chatId);
}

// voice notes (ptt) and audio files -> text through the CLI's `transcribe` (voice hub at home); the file is deleted after
const VOICE_MAX_S = 600;
async function voiceText(msg) {
  if (!TRANSCRIBE || !['ptt', 'audio'].includes(msg.type)) return null;
  if (Number(msg.duration) > VOICE_MAX_S) return { error: 'too_long', seconds: Number(msg.duration) };
  const media = await msg.downloadMedia().catch(() => null);
  if (!media?.data) return { error: 'download_failed' };
  const dir = path.join(STATE, 'voice'); fs.mkdirSync(dir, { recursive: true });
  const ext = /mpeg/.test(media.mimetype) ? '.mp3' : /mp4|aac/.test(media.mimetype) ? '.m4a' : /wav/.test(media.mimetype) ? '.wav' : '.ogg';
  const file = path.join(dir, String(msg.id?.id || Date.now()).replace(/\W/g, '') + ext);
  fs.writeFileSync(file, Buffer.from(media.data, 'base64'));
  try {
    const r = await new Promise((resolve) => {
      const p = spawn(`${TRANSCRIBE} "${file}"`, { shell: true, windowsHide: true, env: process.env });
      let out = ''; p.stdout.on('data', (d) => out += d); p.on('close', (code) => resolve({ code, out }));
    });
    let d = {}; try { d = JSON.parse(r.out.trim().split('\n').pop() || '{}'); } catch {}
    log({ ev: 'transcribed', msg: msg.id?._serialized, ok: !!d.ok, chars: (d.text || '').length, s: d.seconds, error: d.error });
    return d.ok ? { text: d.text || '', lang: d.lang } : { error: d.error || 'transcribe_failed' };
  } finally { fs.rmSync(file, { force: true }); }
}
const voiceLine = (v) => v?.text ? `🎤 (голосовое сообщение, расшифровка) ${v.text}`
  : `[голосовое сообщение${v?.error === 'too_long' ? ' длиннее 10 мин' : ''}, расшифровать не удалось]`;

async function groupHistory(chat, cfg, limit = 15) {
  const msgs = await chat.fetchMessages({ limit });
  const lines = [];
  for (const m of msgs) {
    const body = m.body || (['ptt', 'audio'].includes(m.type) ? '[голосовое сообщение]' : '');
    if (!body) continue;
    let who = 'me (the assistant)';
    if (!m.fromMe) { const c = await m.getContact().catch(() => null); who = c?.name || c?.pushname || '+' + (await phoneOf(m.author)); }
    lines.push(`${who}: ${body.slice(0, 500)}`);
  }
  return lines.join('\n');
}

// one chat's messages are taken in order: a voice note being transcribed is not overtaken by the text after it
const order = new Map();
client.on('message', (msg) => {
  const prev = order.get(msg.from) || Promise.resolve();
  const next = prev.then(() => onMessage(msg));
  order.set(msg.from, next);
  next.then(() => { if (order.get(msg.from) === next) order.delete(msg.from); });
});
async function onMessage(msg) {
  try {
    if (msg.fromMe || msg.isStatus || msg.from === 'status@broadcast') return;
    const cfg = readConfig();
    const group = msg.from.endsWith('@g.us');
    const sender = group ? msg.author : msg.from;
    const contact = await msg.getContact();
    const number = await phoneOf(sender) || contact.number || '';
    const familyEnv = (cfg.family || {})[number] || null;
    const item = { t: new Date().toISOString(), id: msg.id?._serialized, msgId: msg.id?._serialized, from: msg.from, sender, number,
      name: contact.name || contact.pushname || null, contact: !!contact.isMyContact, family: familyEnv, group,
      type: msg.type, text: msg.body || (msg.hasMedia ? `[${msg.type}]` : '') };
    // voice of a non-contact is never downloaded: it is only logged, like their text
    if (['ptt', 'audio'].includes(msg.type) && (familyEnv || contact.isMyContact) && TRANSCRIBE) {
      const v = await voiceText(msg);
      item.voice = true; item.text = voiceLine(v) + (msg.body ? `\n${msg.body}` : '');
    }
    fs.appendFileSync(path.join(STATE, 'inbox.jsonl'), JSON.stringify(item) + '\n');
    if (!FORWARD || !item.text) return;
    if (group) {
      const me = client.info?.wid?._serialized, meLid = client.info?.lid?._serialized;
      const mentioned = (msg.mentionedIds || []).some((m) => [me, meLid].includes(m?._serialized || m));
      const quoted = msg.hasQuotedMsg ? await msg.getQuotedMessage().catch(() => null) : null;
      const low = item.text.toLowerCase();
      const named = (cfg.names || []).some((n) => {
        if (!n) return false;
        const i = low.indexOf(n.toLowerCase()), j = i + n.length;
        return i >= 0 && !/\p{L}/u.test(low[i - 1] || '') && !/\p{L}/u.test(low[j] || ''); // a whole word
      });
      if (!mentioned && !(quoted && quoted.fromMe) && !named) return; // not called: stays out of the conversation
      if (overLimit(msg.from, cfg)) { log({ ev: 'limit', chat: msg.from }); return; }
      const chat = await msg.getChat();
      const ownerNumber = Object.entries(cfg.family || {}).find(([, env]) => env === cfg.env)?.[0];
      let ownerPresent = false;
      for (const p of chat.participants || []) if (ownerNumber && (await phoneOf(p.id?._serialized)) === ownerNumber) ownerPresent = true;
      Object.assign(item, { kind: 'group', groupName: chat.name, ownerPresent, history: await groupHistory(chat, cfg) });
    } else if (familyEnv) {
      item.kind = 'family';
    } else if (item.contact) {
      if (overLimit(msg.from, cfg)) { log({ ev: 'limit', chat: msg.from }); return; }
      item.kind = 'contact';
    } else { log({ ev: 'ignored_not_contact', from: msg.from }); return; }
    if (!pending.has(msg.from)) pending.set(msg.from, []);
    pending.get(msg.from).push(item);
    handle(msg.from);
  } catch (e) { log({ ev: 'message_error', e: String(e.stack || e).slice(0, 800) }); }
}

async function resolveChatId(to) {
  if (/@(c|g)\.us$/.test(to)) return to;
  const digits = String(to).replace(/\D/g, '');
  if (digits.length >= 8 && /^[+\d\s()-]+$/.test(String(to))) {
    const id = await client.getNumberId(digits);
    if (!id) throw new Error('not_on_whatsapp');
    return id._serialized;
  }
  const want = String(to).toLowerCase();
  const groups = (await client.getChats()).filter((c) => c.isGroup && (c.name || '').toLowerCase() === want);
  if (groups.length === 1) return groups[0].id._serialized;
  const contacts = (await client.getContacts()).filter((c) => c.isMyContact && !c.isGroup);
  const hits = contacts.filter((c) => (c.name || '').toLowerCase() === want);
  const near = hits.length ? hits : contacts.filter((c) => (c.name || '').toLowerCase().includes(want));
  if (near.length === 1) return near[0].id._serialized;
  const e = new Error(near.length ? 'ambiguous' : 'contact_not_found');
  e.candidates = near.slice(0, 10).map((c) => ({ name: c.name, number: c.number }));
  throw e;
}

const send = (res, code, o) => { res.writeHead(code, { 'content-type': 'application/json; charset=utf-8' }); res.end(JSON.stringify(o)); };
http.createServer(async (req, res) => {
  if (req.headers['x-token'] !== TOKEN) return send(res, 403, { ok: false, error: 'forbidden' });
  let body = ''; for await (const c of req) body += c;
  const q = body ? JSON.parse(body) : {};
  try {
    if (req.url === '/status') return send(res, 200, { ok: true, ...st });
    if (req.url === '/debug') {
      const page = client.pupPage; if (!page) return send(res, 409, { ok: false, error: 'no_page' });
      const d = await page.evaluate(() => ({ store: typeof window.Store, wwebjs: typeof window.WWebJS, debug: typeof window.Debug,
        version: window.Debug?.VERSION || null, req: typeof window.require, mreq: typeof window.__d }));
      return send(res, 200, { ok: true, ...d, state: await client.getState().catch((e) => String(e)) });
    }
    if (req.url === '/screenshot') {
      const page = client.pupPage; if (!page) return send(res, 409, { ok: false, error: 'no_page' });
      await page.screenshot({ path: path.join(STATE, 'screen.png') });
      return send(res, 200, { ok: true, file: path.join(STATE, 'screen.png'), title: await page.title() });
    }
    if (st.state !== 'ready') return send(res, 409, { ok: false, error: 'not_ready', state: st.state });
    if (req.url === '/send') {
      const id = await resolveChatId(q.to);
      const m = await client.sendMessage(id, String(q.text));
      log({ ev: 'sent', to: id, chars: String(q.text).length, by: q.by || null });
      return send(res, 200, { ok: true, sent: true, to: id, id: m.id?._serialized });
    }
    if (req.url === '/resolve') return send(res, 200, { ok: true, id: await resolveChatId(q.to) });
    if (req.url === '/chats') {
      const chats = (await client.getChats()).filter((c) => !c.isGroup).slice(0, q.limit || 30);
      return send(res, 200, { ok: true, chats: chats.map((c) => ({ id: c.id._serialized, name: c.name, unread: c.unreadCount, last: c.lastMessage?.body?.slice(0, 120) || null, t: c.timestamp })) });
    }
    if (req.url === '/read') {
      const chat = await client.getChatById(await resolveChatId(q.chat));
      const msgs = await chat.fetchMessages({ limit: q.limit || 30 });
      return send(res, 200, { ok: true, chat: chat.name, messages: msgs.map((m) => ({ fromMe: m.fromMe, t: m.timestamp, type: m.type, text: m.body })) });
    }
    if (req.url === '/voice') { // transcript of one voice note by message id (nothing is sent)
      const m = await client.getMessageById(String(q.id));
      if (!m) return send(res, 404, { ok: false, error: 'message_not_found' });
      const v = await voiceText(m);
      return send(res, 200, { ok: !!v?.text || v?.text === '', type: m.type, ...(v || { error: 'not_voice' }) });
    }
    if (req.url === '/contacts') {
      const cs = (await client.getContacts()).filter((c) => c.isMyContact && !c.isGroup && c.id.server === 'c.us');
      return send(res, 200, { ok: true, contacts: cs.map((c) => ({ name: c.name, number: c.number })) });
    }
    send(res, 404, { ok: false, error: 'unknown' });
  } catch (e) { send(res, 400, { ok: false, error: e.message || String(e), candidates: e.candidates }); }
}).listen(PORT, '127.0.0.1');

// CO pushes to WhatsApp (config pushes [env]): the CLI's `pushes` fetches new copies from the server and sends them
// through this service; every 20 s while ready, one run at a time
let pushRun = false;
if (PUSHES) setInterval(() => {
  if (pushRun || st.state !== 'ready' || !(readConfig().pushes || []).length) return;
  pushRun = true;
  const p = spawn(PUSHES, { shell: true, windowsHide: true, env: process.env });
  let out = ''; p.stdout.on('data', (d) => out += d); p.stderr.on('data', (d) => out += d);
  p.on('close', (code) => {
    pushRun = false;
    let r = null; try { r = JSON.parse(out.trim().split('\n').pop() || '{}'); } catch {}
    if (code !== 0 || (r && r.sent)) log({ ev: 'pushes', code, sent: r?.sent ?? null, out: code ? out.slice(-400) : undefined });
  });
}, 20000);

client.initialize().catch((e) => { st.state = 'start_failed'; st.error = String(e.message || e); writeStatus(); process.exit(2); });
process.on('unhandledRejection', (e) => log({ ev: 'unhandled', e: String(e?.stack || e).slice(0, 1500) }));
process.on('SIGTERM', async () => { try { await client.destroy(); } catch {} process.exit(0); });
