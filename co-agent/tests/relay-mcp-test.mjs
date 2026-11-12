// SPDX-License-Identifier: AGPL-3.0-or-later
// relay-mcp.mjs against a local stand-in for db.php (relay_call_put / relay_call_get): MCP initialize, tools/list,
// a call answered by the "Bridge", a refused call, and a call nobody picks up.
// Run: node co-agent/tests/relay-mcp-test.mjs
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { gunzipSync } from 'node:zlib';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const dir = mkdtempSync(join(tmpdir(), 'relay-mcp-'));
writeFileSync(join(dir, 'key'), 'k1\n');
writeFileSync(join(dir, 'tools.json'), JSON.stringify({ short: 'homepc', name: 'Home PC', kind: 'desktop', tools: [{ name: 'browser_navigate', inputSchema: { type: 'object' } }, { name: 'corp_chat' }], deny: ['corp_chat'] }));

const calls = new Map();
let n = 0;
const server = createServer((req, res) => {
  let body = '';
  req.on('data', (d) => (body += d));
  req.on('end', () => {
    let j = JSON.parse(body);
    if (j.gz) j = JSON.parse(gunzipSync(Buffer.from(j.gz, 'base64')).toString());
    const send = (o) => res.end(JSON.stringify(o));
    if (req.headers['x-co-push-key'] !== 'k1') return send({ error: 'forbidden' });
    if (j.op === 'relay_call_put') {
      if (j.args.url === 'refuse') return send({ ok: false, error: { code: 'not_allowed', message: 'no' } });
      const id = `rc-${String(++n).padStart(32, '0')}`;
      calls.set(id, { put: j, polls: 0 });
      return send({ ok: true, callId: id });
    }
    if (j.op === 'relay_call_get') {
      const c = calls.get(j.callId);
      c.polls++;
      if (c.put.args.url === 'silent') return send({ state: 'expired' });
      if (c.put.args.url === 'unclaimed') return send({ state: 'unclaimed' });
      if (c.polls < 2) return send({ state: 'claimed' });
      return send({ state: 'done', result: { ok: true, content: `opened ${c.put.args.url} for ${c.put.jobId}/${c.put.env}/${c.put.chatId}`, untrusted: true } });
    }
    send({ error: 'unknown_op' });
  });
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const url = `http://127.0.0.1:${server.address().port}/db.php`;

const child = spawn(process.execPath, [join(here, '..', 'executor', 'relay-mcp.mjs')], {
  env: { ...process.env, RELAY_URL: url, RELAY_KEY_FILE: join(dir, 'key'), RELAY_JOB: 'J-7', RELAY_ENV: 'owner', RELAY_CHAT: 'CHAT-7',
    RELAY_DEVICE: 'd-b903ab04eea11ba2', RELAY_TOOLS_FILE: join(dir, 'tools.json') },
  stdio: ['pipe', 'pipe', 'inherit'],
});
const pending = new Map();
let buf = '';
child.stdout.on('data', (d) => {
  buf += d;
  let i;
  while ((i = buf.indexOf('\n')) >= 0) {
    const m = JSON.parse(buf.slice(0, i));
    buf = buf.slice(i + 1);
    pending.get(m.id)?.(m);
  }
});
let id = 0;
const rpc = (method, params) => new Promise((r) => {
  pending.set(++id, r);
  child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', id, method, params })}\n`);
});

let fail = 0;
const ok = (name, cond) => {
  if (!cond) fail++;
  console.log(`${cond ? 'ok  ' : 'FAIL'} ${name}`);
};
const init = await rpc('initialize', { protocolVersion: '2025-06-18' });
ok('initialize names the server', init.result.serverInfo.name === 'dev_homepc' && /Home PC/.test(init.result.instructions));
const list = await rpc('tools/list', {});
ok('tools/list from the file without denied tools', list.result.tools.length === 1 && list.result.tools[0].name === 'browser_navigate');
const r0 = await rpc('tools/call', { name: 'corp_chat', arguments: {} });
ok('denied tool refused locally', r0.result.isError && /tool_off/.test(r0.result.content[0].text) && n === 0);
const r1 = await rpc('tools/call', { name: 'browser_navigate', arguments: { url: 'https://www.linkedin.com/' } });
ok('call answered', !r1.result.isError && r1.result.content[0].text === 'opened https://www.linkedin.com/ for J-7/owner/CHAT-7');
const r2 = await rpc('tools/call', { name: 'browser_navigate', arguments: { url: 'refuse' } });
ok('refusal is an error', r2.result.isError && /not_allowed/.test(r2.result.content[0].text));
const r3 = await rpc('tools/call', { name: 'browser_navigate', arguments: { url: 'silent' } });
ok('expired call is device_timeout', r3.result.isError && /device_timeout/.test(r3.result.content[0].text));
const r4 = await rpc('tools/call', { name: 'browser_navigate', arguments: { url: 'unclaimed' } });
ok('unclaimed call is home_offline', r4.result.isError && /home_offline/.test(r4.result.content[0].text));
child.kill();
server.close();
console.log(fail ? `FAILED: ${fail}` : 'all ok');
process.exit(fail ? 1 : 0);
