// SPDX-License-Identifier: AGPL-3.0-or-later
// Tool-level test of the AI Workbench extension: loads it into Chromium, opens panel.html as a tab and drives
// runTool() against a local fake job site with a cookie banner, a 2-step form, a mail editor and a cross-origin
// "captcha" frame. Run through run.py tools.
import os from 'node:os';
import path from 'node:path';
import fs from 'node:fs';
import http from 'node:http';

const [extDir, pwMod] = process.argv.slice(2);
// the CO site the extension was configured for (site.js) is stubbed
const coHost = (() => { try { return new URL((fs.readFileSync(path.join(extDir, 'site.js'), 'utf8').match(/https:\/\/[^'"]+/) || [''])[0]).hostname; } catch { return ''; } })();
const { chromium } = await import(pwMod);
const log = (...a) => console.log(...a);
let failures = 0;
const check = (cond, msg) => { log((cond ? 'PASS ' : 'FAIL ') + msg); if (!cond) failures++; };

const SITE = `<!doctype html><html><head><title>Apply - Example Jobs</title></head><body>
<div id="cookie" style="position:fixed;bottom:0;left:0;right:0;background:#eee;padding:10px">We use cookies <button onclick="this.parentNode.remove();window.cookieOk=1">Accept all</button></div>
<h1>Apply for Process Engineer</h1>
<form id="f1" onsubmit="event.preventDefault()">
 <div id="step1"><label for="nm">Full name</label><input id="nm" name="name" required>
 <label>Email <input name="email" type="email"></label>
 <label>Country <select name="country"><option value="">--</option><option value="be">Belgium</option><option value="nl">Netherlands</option></select></label>
 <label>Password <input type="password" name="pw"></label>
 <button type="button" onclick="document.getElementById('step1').hidden=true;document.getElementById('step2').hidden=false">Next</button></div>
 <div id="step2" hidden><h2>Step 2</h2><label>CV <input type="file" name="cv"></label>
 <label>Motivation <textarea name="mot"></textarea></label>
 <button type="submit" onclick="window.submitted=1;document.body.insertAdjacentHTML('beforeend','<p>Application received</p>')">Submit application</button></div>
</form>
<h2>Mail</h2><div id="ed" contenteditable="true" aria-label="Message Body" style="min-height:40px;border:1px solid #999"></div>
<div role="button" tabindex="0" aria-label="Send" onclick="window.sent=1">Send</div>
<iframe src="http://localhost:PORT/anchor" style="width:300px;height:80px;border:0"></iframe>
<iframe id="same" src="/same" style="margin-left:150px;width:300px;height:60px;border:5px solid #ccc"></iframe>
</body></html>`;
const CAPTCHA = `<!doctype html><html><body><label><input type="checkbox" id="c" onclick="window.parent.postMessage('captcha:'+event.isTrusted,'*')"> I'm not a robot</label></body></html>`;

const srv = http.createServer((req, res) => {
  res.writeHead(200, { 'content-type': 'text/html' });
  if (req.url.startsWith('/same')) return res.end('<button onclick="parent.sameHit=event.isTrusted">Inner button</button>');
  res.end(req.url.startsWith('/anchor') ? CAPTCHA : SITE.replace('PORT', PORT).replace('</body>', '<script>addEventListener("message",e=>{window.captcha=e.data})</script></body>'));
});
await new Promise((r) => srv.listen(0, r));
const PORT = String(srv.address().port);
const SITE_URL = 'http://127.0.0.1:' + PORT + '/apply';
const userDir = fs.mkdtempSync(path.join(os.tmpdir(), 'coext-'));
const ctx = await chromium.launchPersistentContext(userDir, {
  channel: 'chromium', headless: true, executablePath: process.env.PW_CHROME || undefined,
  args: [`--disable-extensions-except=${extDir}`, `--load-extension=${extDir}`],
});
try {
  await ctx.route((u) => u.hostname === coHost, (r) => r.fulfill({ status: 200, contentType: 'text/html', body: '<p>co stub</p>' }));
  let [sw] = ctx.serviceWorkers();
  if (!sw) sw = await ctx.waitForEvent('serviceworker');
  const extId = sw.url().split('/')[2];
  check(extId === 'eobijkacekfneefkngaejdcdaehfijaf', 'extension id ' + extId);

  const panel = await ctx.newPage();
  await panel.goto(`chrome-extension://${extId}/panel.html`);
  await panel.waitForTimeout(500);
  const run = (tool, args = {}) => panel.evaluate(([t, a]) => runTool(t, a, { runId: 'r1' }), [tool, args]);
  // Answers the next confirmation dialog in the panel with the button text
  const answer = async (label) => {
    await panel.waitForSelector('#dialog:not([hidden]) button', { timeout: 5000 });
    const title = await panel.textContent('#dialog h3');
    await panel.click(`#dialog button:has-text("${label}")`);
    return title;
  };

  let r = await run('browser_open', { url: SITE_URL });
  check(r.ok && r.content.includes('Apply for Process Engineer') && r.untrusted, 'open + snapshot ' + JSON.stringify(r.error));
  log(r.content.slice(0, 1800));
  const site = ctx.pages().find((p) => p.url().startsWith(SITE_URL));
  const ref = (label) => { const m = new RegExp('\\[((?:f\\d+:)?n\\d+)\\] [a-z]+ "' + label).exec(r.content); return m && m[1]; };

  check(r.content.includes('captcha') || r.content.includes('frame f'), 'cross-origin frame section present');
  r = await run('browser_click', { ref: ref('Accept all') });
  check(r.ok && await site.evaluate(() => window.cookieOk === 1), 'cookie banner accepted without asking');

  r = await run('browser_snapshot');
  r = await run('browser_type', { ref: ref('Full name'), text: 'Anna Example', snapshot: false });
  check(r.ok && await site.inputValue('#nm') === 'Anna Example', 'type into field');
  r = await run('browser_snapshot');
  const pw = /\[(n\d+)\] textbox "Password"/.exec(r.content)?.[1];
  const pr = await run('browser_type', { ref: pw, text: 'x' });
  check(!pr.ok && pr.error.code === 'sensitive_field', 'password refused: ' + JSON.stringify(pr.error));

  const sel = run('browser_select', { ref: ref('Country'), value: 'Belgium', snapshot: false });
  check((await answer('Allow for this run')) === 'Change a choice?', 'select asks');
  r = await sel;
  check(r.ok && await site.inputValue('select') === 'be', 'select done');

  r = await run('browser_snapshot');
  r = await run('browser_click', { ref: ref('Next') });
  check(r.ok && r.content.includes('Step 2') && !(await panel.isVisible('#dialog')), 'Next runs without asking');

  let pend = run('browser_upload', { ref: /\[(n\d+)\] file "CV"/.exec(r.content)?.[1], name: 'cv.pdf', dataBase64: Buffer.from('%PDF-1.4 test').toString('base64'), snapshot: false });
  check((await answer('Allow')) === 'Upload a file?', 'upload asks');
  r = await pend;
  check(r.ok && await site.evaluate(() => document.querySelector('input[type=file]').files[0]?.name) === 'cv.pdf', 'upload done ' + (r.error ? JSON.stringify(r.error) : ''));

  r = await run('browser_snapshot');
  r = await run('browser_type', { ref: ref('Motivation'), text: 'Line one' + String.fromCharCode(10) + 'Line two', snapshot: false });
  check(r.ok && (await site.inputValue('textarea')) === 'Line one' + String.fromCharCode(10) + 'Line two', 'textarea multi-line');

  r = await run('browser_snapshot');
  pend = run('browser_click', { ref: ref('Submit application') });
  check((await answer('Deny')) === 'Submit the form?', 'final submit asks');
  r = await pend;
  check(!r.ok && r.error.code === 'denied_by_user' && !(await site.evaluate(() => window.submitted)), 'submit denied');
  r = await run('browser_snapshot');
  pend = run('browser_click', { ref: ref('Submit application') });
  await answer('Allow');
  r = await pend;
  check(r.ok && r.content.includes('Application received'), 'submit allowed');

  r = await run('browser_type', { ref: ref('Message Body'), text: 'Hello,' + String.fromCharCode(10) + 'thanks!', snapshot: false });
  check(r.ok && (await site.evaluate(() => document.getElementById('ed').innerText)).includes('thanks!'), 'mail editor typing');
  r = await run('browser_snapshot');
  const tr = await run('browser_type', { ref: ref('Send'), text: 'Go', snapshot: false });
  check(!tr.ok && tr.error.code === 'not_editable', 'typing into a button refused ' + JSON.stringify(tr.error));
  const tt = await run('browser_type', { ref: ref('Send'), text: 'x', trusted: true, submit: true, snapshot: false });
  check(!tt.ok && tt.error.code === 'not_editable' && !(await site.evaluate(() => window.sent)), 'trusted type+submit on a button refused');
  pend = run('browser_type', { ref: ref('Message Body'), text: 'Hi', append: true, submit: true, snapshot: false });
  check((await answer('Deny')) === 'Send / publish?', 'Enter in a message box asks as send');
  r = await pend;
  check(!r.ok && r.error.code === 'send_denied', 'Enter in a message box denied');
  r = await run('browser_snapshot');
  pend = run('browser_click', { ref: ref('Send') });
  check((await answer('Deny')) === 'Send / publish?', 'send asks');
  r = await pend;
  check(!r.ok && r.error.code === 'send_denied' && !(await site.evaluate(() => window.sent)), 'send denied');
  r = await run('browser_snapshot');
  pend = run('browser_click', { ref: ref('Send'), snapshot: false });
  await answer('Send');
  r = await pend;
  check(r.ok && await site.evaluate(() => window.sent === 1), 'send allowed by user');

  r = await run('browser_snapshot');
  const cap = /\[(f\d+:n\d+)\] checkbox/.exec(r.content)?.[1];
  check(!!cap, 'captcha checkbox ref in frame: ' + cap);
  r = await run('browser_click', { ref: cap, trusted: true, snapshot: false });
  await site.waitForTimeout(300);
  check(r.ok && (await site.evaluate(() => window.captcha)) === 'captcha:true', 'trusted click in cross-origin frame ' + JSON.stringify(r.error || '') + ' ' + await site.evaluate(() => window.captcha));

  // Keys: Delete outside a text field asks; Enter with focus inside a cross-origin frame asks
  pend = run('browser_press', { key: 'Delete', snapshot: false });
  check((await answer('Deny')) === 'Delete?', 'Delete key outside a field asks');
  r = await pend;
  check(!r.ok && r.error.code === 'denied_by_user', 'Delete key denied');
  await run('browser_click', { ref: cap, snapshot: false }); // a script click focuses the checkbox inside the frame
  pend = run('browser_press', { key: 'Enter', snapshot: false });
  check((await answer('Deny')) === 'Submit the form?', 'Enter with focus in a frame asks');
  r = await pend;
  check(!r.ok && r.error.code === 'denied_by_user', 'Enter in frame denied');

  r = await run('browser_scroll', { ref: cap, dy: 0 });
  check(r.ok, 'scroll to a ref inside a frame ' + JSON.stringify(r.error || ''));

  r = await run('browser_snapshot');
  const inner = ref('Inner button');
  r = await run('browser_click', { ref: inner, trusted: true, snapshot: false });
  check(r.ok && (await site.evaluate(() => window.sameHit)) === true, 'trusted click inside a same-origin frame ' + JSON.stringify(r.error || ''));
  // An overlay of the parent page over both frames: trusted clicks into them are refused
  await site.evaluate(() => document.body.insertAdjacentHTML('beforeend', '<div id=ov style="position:fixed;inset:0;z-index:9" onclick="window.ovHit=1"></div>'));
  r = await run('browser_click', { ref: inner, trusted: true, snapshot: false });
  check(!r.ok && r.error.code === 'covered' && !(await site.evaluate(() => window.ovHit)), 'overlay over a same-origin frame refused');
  r = await run('browser_click', { ref: cap, trusted: true, snapshot: false });
  check(!r.ok && r.error.code === 'covered' && !(await site.evaluate(() => window.ovHit)), 'overlay over a cross-origin frame refused');
  await site.evaluate(() => document.getElementById('ov').remove());

  await panel.evaluate(() => setPaused(true));
  r = await run('browser_snapshot');
  check(!r.ok && r.error.code === 'paused_by_user', 'paused blocks the agent');
  await panel.evaluate(() => setPaused(false));
  r = await run('browser_status');
  check(r.ok && r.content.includes('This run works in: tab'), 'status');
  log(r.content);
} catch (e) {
  failures++;
  log('ERROR', e?.stack || e);
} finally {
  log(failures ? `${failures} FAILED` : 'ALL PASSED');
  await ctx.close();
  srv.close();
}
process.exitCode = failures ? 1 : 0;
