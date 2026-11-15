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

// one answer at a time per contact; a contact's messages that arrive while it answers are joined into the next turn
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
async function handle(contactId) {
  if (busy.get(contactId)) return;
  const items = pending.get(contactId); if (!items?.length) return;
  pending.delete(contactId); busy.set(contactId, true);
  const item = { ...items[items.length - 1], text: items.map((i) => i.text).join('\n') };
  try {
    const r = await runForward(item);
    let ans = null; try { ans = JSON.parse(r.out.trim().split('\n').pop() || '{}'); } catch {}
    if (ans?.reply) {
      await client.sendMessage(contactId, String(ans.reply));
      log({ ev: 'replied', to: contactId, chars: String(ans.reply).length, job: ans.jobId || null });
    } else log({ ev: 'forward_no_reply', to: contactId, code: r.code, out: r.out.slice(-500), err: r.err.slice(-500) });
  } catch (e) { log({ ev: 'forward_error', to: contactId, e: String(e.message || e) }); }
  busy.set(contactId, false);
  if (pending.get(contactId)?.length) handle(contactId);
}

client.on('message', async (msg) => {
  try {
    if (msg.fromMe || msg.isStatus || msg.from.endsWith('@g.us') || msg.from === 'status@broadcast') return;
    const contact = await msg.getContact();
    const item = { t: new Date().toISOString(), id: msg.id?._serialized, from: msg.from, number: contact.number || null,
      name: contact.name || null, pushname: contact.pushname || null, contact: !!contact.isMyContact,
      type: msg.type, text: msg.body || (msg.hasMedia ? `[${msg.type}]` : '') };
    fs.appendFileSync(path.join(STATE, 'inbox.jsonl'), JSON.stringify(item) + '\n');
    if (!item.contact) { log({ ev: 'ignored_not_contact', from: msg.from }); return; }
    if (!FORWARD) return;
    if (!pending.has(msg.from)) pending.set(msg.from, []);
    pending.get(msg.from).push(item);
    handle(msg.from);
  } catch (e) { log({ ev: 'message_error', e: String(e.message || e) }); }
});

async function resolveChatId(to) {
  if (/@(c|g)\.us$/.test(to)) return to;
  const digits = String(to).replace(/\D/g, '');
  if (digits.length >= 8 && /^[+\d\s()-]+$/.test(String(to))) {
    const id = await client.getNumberId(digits);
    if (!id) throw new Error('not_on_whatsapp');
    return id._serialized;
  }
  const want = String(to).toLowerCase();
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
    if (req.url === '/chats') {
      const chats = (await client.getChats()).filter((c) => !c.isGroup).slice(0, q.limit || 30);
      return send(res, 200, { ok: true, chats: chats.map((c) => ({ id: c.id._serialized, name: c.name, unread: c.unreadCount, last: c.lastMessage?.body?.slice(0, 120) || null, t: c.timestamp })) });
    }
    if (req.url === '/read') {
      const chat = await client.getChatById(await resolveChatId(q.chat));
      const msgs = await chat.fetchMessages({ limit: q.limit || 30 });
      return send(res, 200, { ok: true, chat: chat.name, messages: msgs.map((m) => ({ fromMe: m.fromMe, t: m.timestamp, type: m.type, text: m.body })) });
    }
    if (req.url === '/contacts') {
      const cs = (await client.getContacts()).filter((c) => c.isMyContact && !c.isGroup && c.id.server === 'c.us');
      return send(res, 200, { ok: true, contacts: cs.map((c) => ({ name: c.name, number: c.number })) });
    }
    send(res, 404, { ok: false, error: 'unknown' });
  } catch (e) { send(res, 400, { ok: false, error: e.message || String(e), candidates: e.candidates }); }
}).listen(PORT, '127.0.0.1');

client.initialize().catch((e) => { st.state = 'start_failed'; st.error = String(e.message || e); writeStatus(); process.exit(2); });
process.on('unhandledRejection', (e) => log({ ev: 'unhandled', e: String(e?.stack || e).slice(0, 1500) }));
process.on('SIGTERM', async () => { try { await client.destroy(); } catch {} process.exit(0); });
