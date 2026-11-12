// SPDX-License-Identifier: AGPL-3.0-or-later
// MCP stdio server dev_<short> of the site agent (S1, schema 22): tools of one home device (CO Desktop, CO Resident Mac,
// or the home Bridge itself) reached through the server. tools/call -> db.php relay_call_put; the home Bridge claims the
// call, runs it over its signed device channel and stores the reply; relay_call_get returns it. Started by agent.mjs per
// job and device with RELAY_* variables; the tool list is the one the Bridge published when the job started.

import { gzipSync } from 'node:zlib';
import { readFileSync } from 'node:fs';
import { createInterface } from 'node:readline';

const URL_DB = process.env.RELAY_URL;
const KEY = readFileSync(process.env.RELAY_KEY_FILE, 'utf8').trim();
const JOB = process.env.RELAY_JOB;
const ENV = process.env.RELAY_ENV;
const CHAT = process.env.RELAY_CHAT || '';
const DEVICE = process.env.RELAY_DEVICE;
let tainted = process.env.RELAY_TAINTED === '1'; // set for good once a device returned untrusted content
const info = JSON.parse(readFileSync(process.env.RELAY_TOOLS_FILE, 'utf8')); // {short, name, kind, tools, deny}
const DENY = Array.isArray(info.deny) ? info.deny : []; // tools the chat turned off (deviceToolsDeny)

const CALL_TIMEOUT_SEC = 2400;   // a person may have to confirm on the device (Bridge ask-user budget 30 min)

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function db(op, body, gz = false, timeoutMs = 40000) {
  let payload = JSON.stringify({ ...body, op });
  if (gz) payload = JSON.stringify({ gz: gzipSync(Buffer.from(payload)).toString('base64') });
  const r = await fetch(URL_DB, {
    method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CO-Push-Key': KEY }, body: payload, signal: AbortSignal.timeout(timeoutMs),
  });
  const text = await r.text();
  if (!r.ok) throw new Error(`HTTP ${r.status} ${text.slice(0, 200)}`);
  return JSON.parse(text);
}

async function call(tool, args) {
  if (DENY.includes(tool)) return { ok: false, error: { code: 'tool_off', message: 'This tool is turned off for this chat (its connector button in the CO composer is off). Ask the user to turn it on.' } };
  const put = await db('relay_call_put', { env: ENV, jobId: JOB, chatId: CHAT, deviceId: DEVICE, tool, args: args || {}, tainted, deny: DENY, timeoutSec: CALL_TIMEOUT_SEC }, true);
  if (!put.ok) return { ok: false, error: put.error || { code: 'relay_refused', message: 'The server refused the call.' } };
  const started = Date.now();
  let failures = 0;
  for (;;) {
    let g;
    try {
      g = await db('relay_call_get', { callId: put.callId, waitSec: 20 });
      failures = 0;
    } catch (e) {
      if (++failures >= 5) {
        // withdraw it if the Bridge has not taken it yet, so it never runs after this failure
        await db('relay_cancel', { callId: put.callId }).catch(() => undefined);
        return { ok: false, error: { code: 'relay_unreachable', message: `The server did not answer: ${e.message}` } };
      }
      await sleep(2000 * failures);
      continue;
    }
    if (g.state === 'done') {
      if (g.result?.untrusted) tainted = true;
      return g.result || { ok: false, error: { code: 'bad_result', message: 'Empty answer from the home Bridge.' } };
    }
    if (g.state === 'expired' || g.state === 'missing')
      return { ok: false, error: { code: 'device_timeout', message: `No answer from ${info.name} within ${Math.round((Date.now() - started) / 1000)} s.` } };
    // the server withdraws a call the Bridge did not claim within 60 s: it will not run later
    if (g.state === 'unclaimed')
      return { ok: false, error: { code: 'home_offline', message: 'The home Bridge did not pick up the call within 60 s (the home PC may be asleep or Bridge restarting); the call was withdrawn and will not run. Do not retry at once; tell the user.' } };
  }
}

function send(obj) {
  process.stdout.write(`${JSON.stringify(obj)}\n`);
}

const what = info.kind === 'home' ? 'the home PC itself (its tool library: browser-agent, linkedin-web, whatsapp-web, ...)'
  : info.kind === 'mac' ? `the Mac "${info.name}"` : `the computer "${info.name}" (CO Desktop, its CO browser and folders)`;
const INSTRUCTIONS = `Tools of ${what}, reached from the site through the home Bridge. They act on that device, `
  + 'with its own rights and confirmations (a person there may have to approve). Sending messages is never allowed.';

const rl = createInterface({ input: process.stdin });
rl.on('line', (line) => {
  let req;
  try {
    req = JSON.parse(line);
  } catch {
    return;
  }
  if (req.id === undefined || req.id === null) return; // notifications
  const p = req.params || {};
  if (req.method === 'initialize') {
    send({ jsonrpc: '2.0', id: req.id, result: { protocolVersion: p.protocolVersion || '2025-06-18', capabilities: { tools: {} },
      serverInfo: { name: `dev_${info.short}`, version: '1.0.0' }, instructions: INSTRUCTIONS } });
  } else if (req.method === 'ping') {
    send({ jsonrpc: '2.0', id: req.id, result: {} });
  } else if (req.method === 'tools/list') {
    send({ jsonrpc: '2.0', id: req.id, result: { tools: info.tools.filter((t) => !DENY.includes(t.name)) } });
  } else if (req.method === 'tools/call') {
    call(String(p.name || ''), p.arguments).then((r) => {
      const ok = !!r?.ok;
      const text = ok ? String(r.content ?? '') : `Error ${r?.error?.code || 'failed'}: ${r?.error?.message || ''}`;
      send({ jsonrpc: '2.0', id: req.id, result: { content: [{ type: 'text', text }], isError: !ok } });
    }, (e) => {
      send({ jsonrpc: '2.0', id: req.id, result: { content: [{ type: 'text', text: `Error relay_failed: ${e.message}` }], isError: true } });
    });
  } else {
    send({ jsonrpc: '2.0', id: req.id, error: { code: -32601, message: `Method not found: ${req.method}` } });
  }
});
