// Live test of the tab rules of 0.15.0 on a SEPARATE Edge profile (never the CO browser of the user):
//   node tests/live_chamberlain.mjs [profile]   (default: a fresh profile batest-<n>, closed and deleted at the end)
// Starts its own daemon (profile %LOCALAPPDATA%\CO\browser-<profile>, minutes shortened to seconds), drives it with
// several fake chats/owners against public pages, prints ok/FAIL per check.
import { spawn, spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, existsSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const fresh = !process.argv[2];
const profile = process.argv[2] || `batest-${Date.now() % 1000000}`;
if (profile === 'default') throw new Error('never the default profile');
const baseDir = mkdtempSync(join(tmpdir(), 'ba-live-'));
const cfg = { session: 't', baseDir, profile, minuteMs: 1000, sweepMs: 1000, idleMinutes: 5, shots: false, allow: ['*'] };
const d = spawn(process.execPath, [join(HERE, '..', 'daemon.mjs')], { env: { ...process.env, BA_CONFIG: JSON.stringify(cfg) }, stdio: 'inherit' });
const sleep = ms => new Promise(r => setTimeout(r, ms));
const stateFile = join(baseDir, 'sessions', 't.json');
for (let i = 0; i < 200 && !existsSync(stateFile); i++) await sleep(200);
const st = JSON.parse(readFileSync(stateFile, 'utf8'));

async function cmd(c, args, owner, chat, browser) {
  const r = await fetch(`http://127.0.0.1:${st.port}/cmd`, { method: 'POST', headers: { 'x-token': st.token, 'content-type': 'application/json' },
    body: JSON.stringify({ cmd: c, args, owner, chat, browser }) });
  return r.json();
}
let pass = 0, fail = 0;
const check = (name, cond, extra) => { if (cond) { pass++; console.log('ok  ', name); } else { fail++; console.log('FAIL', name, extra !== undefined ? JSON.stringify(extra).slice(0, 600) : ''); } };
const all = async () => (await cmd('tabs', { all: true }, 'x', null, {})).tabs.filter(t => t.session === 't');
const ofChat = async c => (await all()).filter(t => t.chat === c);

try {
  // 1. unlimited chat: a new site opens a new tab, the same site stays in its tab
  await cmd('navigate', { url: 'https://example.com/' }, 'A', 'chatA', {});   // defaults: 120 min = 120 s here
  await cmd('navigate', { url: 'https://example.org/' }, 'A', 'chatA', {});
  await cmd('navigate', { url: 'https://example.com/?x=1' }, 'A', 'chatA', {});
  const tA = Date.now();
  check('unlimited: 2 tabs for 2 sites', (await ofChat('chatA')).length === 2, await ofChat('chatA'));

  // 2. tabs=1: the second site reuses the chat's only tab
  await cmd('navigate', { url: 'https://example.com/' }, 'B', 'chatB', { tabs: '1', idleMin: 40 });
  await cmd('navigate', { url: 'https://example.org/' }, 'B', 'chatB', { tabs: '1', idleMin: 40 });
  const b = await ofChat('chatB');
  check('tabs=1: one tab, now on example.org', b.length === 1 && b[0].url.includes('example.org'), b);

  // 3. free tab of a finished job (dead pid) on the same site is adopted by another chat
  await cmd('navigate', { url: 'https://example.net/' }, '999991', 'chatF', {});
  const before = (await all()).length;
  await cmd('navigate', { url: 'https://example.net/' }, 'G', 'chatG', { idleMin: 5 });
  check('free same-site tab adopted', (await all()).length === before && (await ofChat('chatG')).length === 1 && (await ofChat('chatF')).length === 0, await all());

  // 4. typed data: formIdleMin, LRU prefers the clean tab
  const H = { tabs: 2, idleMin: 60, formIdleMin: 300 };
  await cmd('navigate', { url: 'https://httpbin.org/forms/post' }, 'H', 'chatH', H);
  const snap = await cmd('snapshot', {}, 'H', 'chatH', H);
  const ref = (String(snap.snapshot).match(/textbox "Customer name[^\n]*\[ref=(e\d+)\]/) || String(snap.snapshot).match(/textbox[^\n]*\[ref=(e\d+)\]/) || [])[1];
  const ty = await cmd('type', { ref, text: 'Test' }, 'H', 'chatH', H);
  const sH = await cmd('status', {}, 'H', 'chatH', H);
  check('type marks typed data', ty.ok && sH.tab?.typed === true, { ty, tab: sH.tab });
  const expMs = Date.parse(sH.tab?.expiresUtc) - Date.now();
  check('typed tab expires by formIdleMin (~300 s)', expMs > 280000 && expMs <= 301000, sH.tab);
  await cmd('navigate', { url: 'https://example.com/' }, 'H', 'chatH', H);
  await cmd('navigate', { url: 'https://example.org/' }, 'H', 'chatH', H);
  const h = await ofChat('chatH');
  check('limit 2: typed tab kept, clean tab reused', h.length === 2 && h.some(t => t.url.includes('httpbin') && t.typed) && h.some(t => t.url.includes('example.org')), h);

  // 5. captcha: ok:true + challenge, tab pending, kept with captchaKeep, closed without
  const D = { idleMin: 5, captchaKeep: '1' };
  let c1 = await cmd('navigate', { url: 'https://www.google.com/recaptcha/api2/demo' }, 'D', 'chatD', D);
  if (!c1.challenge) { await sleep(2000); c1 = await cmd('reload', {}, 'D', 'chatD', D); }   // widget comes after DOMContentLoaded
  check('captcha: ok:true, challenge, hint', c1.ok === true && c1.challenge === 'captcha' && /do not submit/.test(c1.hint || ''), c1);
  const sD = await cmd('status', {}, 'D', 'chatD', D);
  check('captcha: not paused, pending, never expires', sD.paused === null && sD.tab?.captchaPending === true && sD.tab?.expiresUtc === null, sD);
  const E = { idleMin: 5, captchaKeep: '0' };
  let c2 = await cmd('navigate', { url: 'https://www.google.com/recaptcha/api2/demo' }, 'E', 'chatE', E);
  if (!c2.challenge) { await sleep(2000); c2 = await cmd('reload', {}, 'E', 'chatE', E); }
  check('captchaKeep=0: still ok:true', c2.ok === true && c2.challenge === 'captcha', c2);
  const tq = await cmd('type', { ref: 'e1', text: 'x' }, 'D', 'chatD', D);
  check('agent may keep acting in a captcha tab (no paused_by_user)', tq.code !== 'paused_by_user', tq);

  // captcha by permission still works: request pauses the tab while the user decides, cancel ends the pause
  const rq = await cmd('captcha-request', {}, 'D', 'chatD', D);
  const sP = await cmd('status', {}, 'D', 'chatD', D);
  const tg = await cmd('captcha-tap', { id: rq.id, x: 10, y: 10 }, 'D', 'chatD', D);
  const cc = await cmd('captcha-cancel', { id: rq.id }, 'D', 'chatD', D);
  const sC = await cmd('status', {}, 'D', 'chatD', D);
  check('captcha request: code, paused, tap needs grant, cancel unpauses', rq.ok && /^\d{4}$/.test(rq.code) && sP.paused?.by === 'auto'
    && tg.code === 'captcha_not_granted' && cc.ok && sC.paused === null && sC.tab?.captchaPending === true, { rq, sP: sP.paused, tg, cc, sC: sC.paused });

  // 6. lifetimes: idleMin 5 s / 6 s tabs close, the captchaKeep tab and the typed tab stay
  await sleep(12000);
  const left = await all();
  check('idle tabs closed (chatE, chatG)', !left.some(t => ['chatE', 'chatG'].includes(t.chat)), left);
  check('captcha-pending tab kept (chatD)', left.some(t => t.chat === 'chatD' && t.captchaPending), left);
  check('typed tab kept (chatH)', left.some(t => t.chat === 'chatH' && t.typed), left);
  if (Date.now() - tA < 110000) check('default settings tabs kept (chatA, 120 s)', left.filter(t => t.chat === 'chatA').length === 2, left);
} finally {
  console.log(`${pass}/${pass + fail} passed`);
  if (fail) { try { console.log(readFileSync(join(baseDir, 'sessions', 't.log'), 'utf8').split(String.fromCharCode(10)).slice(-25).join(String.fromCharCode(10))); } catch { /* none */ } }
  await cmd('stop', {}, 'x', null, {}).catch(() => {});
  await sleep(2000);
  if (d.exitCode === null) d.kill();
  if (fresh) {
    // Only the Edge of this test profile: its command line holds the profile folder.
    const dir = join(process.env.LOCALAPPDATA, 'CO', `browser-${profile}`);
    spawnSync('powershell', ['-NoProfile', '-Command', `Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" | Where-Object { $_.CommandLine -like '*${dir.replace(/'/g, "''")}*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }`]);
    await sleep(2000);
    try { rmSync(dir, { recursive: true, force: true, maxRetries: 5 }); } catch { /* locked: delete by hand */ }
  }
  process.exit(fail ? 1 : 0);
}
