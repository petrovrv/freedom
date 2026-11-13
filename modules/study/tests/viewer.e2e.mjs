// SPDX-License-Identifier: AGPL-3.0-or-later
// End-to-end test of the study viewer page as the core will show it: a local HTTP server serves ui/ under the same
// address and with the same CSP as co-agent/web/agent/module.php, a stand-in parent (tests/parent.html) frames it with
// sandbox="allow-scripts allow-forms" and answers the ext/1 protocol with fixtures. Playwright (from co-web/node_modules)
// drives it. Run: node modules/study/tests/viewer.e2e.mjs [--headed]
import { readFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { png, textPdf } from './fixtures.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const mod = path.resolve(here, '..');
const repo = path.resolve(mod, '../..');
const require = createRequire(path.join(repo, 'co-web/node_modules/x.js'));
const { chromium } = require('playwright');

// exactly the header of module.php for module ui files
const CSP = "sandbox allow-scripts allow-forms; default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; "
  + "img-src 'self' data:; connect-src 'none'; frame-ancestors 'self' chrome-extension://eobijkacekfneefkngaejdcdaehfijaf; base-uri 'none'; form-action 'none'";
const MIME = { html: 'text/html; charset=utf-8', js: 'text/javascript; charset=utf-8', css: 'text/css; charset=utf-8', svg: 'image/svg+xml', png: 'image/png', json: 'application/json; charset=utf-8' };

const LECTURE = textPdf([
  ['Lecture 3: Energy', 'E = m c^2 is the rest energy.', 'Kinetic energy grows with speed.'],
  ['Momentum', 'p = m v for slow bodies.', 'Conservation of momentum holds.'],
  ['Summary', 'Energy and momentum are conserved.'],
]);
const CONVERTED = textPdf([['Converted notes', 'This page came from a docx.']]);
const CACHED = textPdf([['Cached copy', 'This page came from the study copy.']]);
const SCAN = png(320, 240);
const fixtures = { '/fixture/lecture.pdf': LECTURE, '/fixture/converted.pdf': CONVERTED, '/fixture/cached.pdf': CACHED, '/fixture/scan.png': SCAN, '/fixture/notes.docx': Buffer.from('PK fake docx') };

const served = [];
const pendingReq = new Set();
const server = createServer((req, res) => {
  const u = new URL(req.url, 'http://x');
  served.push(u.pathname + u.search);
  if (process.env.E2E_DEBUG) { pendingReq.add(req.url); res.on('finish', () => pendingReq.delete(req.url)); console.log('  GET', req.url.slice(0, 80)); }
  if (u.pathname === '/favicon.ico') {
    res.writeHead(204);
    return res.end();
  }
  if (u.pathname === '/parent.html') {
    res.writeHead(200, { 'Content-Type': MIME.html });
    return res.end(readFileSync(path.join(here, 'parent.html')));
  }
  if (u.pathname === '/ext/ext1.js') {
    res.writeHead(200, { 'Content-Type': MIME.js });
    return res.end(readFileSync(path.join(repo, 'co-web/public/ext/ext1.js')));
  }
  if (fixtures[u.pathname]) {
    res.writeHead(200, { 'Content-Type': 'application/octet-stream' });
    return res.end(fixtures[u.pathname]);
  }
  const f = u.searchParams.get('f') || '';
  if (u.pathname === '/agent/module.php' && u.searchParams.get('m') === 'study' && /^ui\/[A-Za-z0-9._/-]+$/.test(f) && !f.includes('..')) {
    const ext = f.split('.').pop();
    try {
      const body = readFileSync(path.join(mod, f));
      if (!MIME[ext]) throw new Error('type');
      res.writeHead(200, { 'Content-Type': MIME[ext], 'X-Content-Type-Options': 'nosniff', 'Content-Security-Policy': CSP, 'Referrer-Policy': 'no-referrer' });
      return res.end(body);
    } catch { /* 404 below */ }
  }
  res.writeHead(404, { 'Content-Type': 'text/plain' });
  res.end('not found');
});
await new Promise((ok) => server.listen(0, '127.0.0.1', ok));
const base = `http://127.0.0.1:${server.address().port}`;

let browser;
try {
  browser = await chromium.launch({ headless: !process.argv.includes('--headed') });
} catch {
  browser = await chromium.launch({ channel: 'msedge', headless: !process.argv.includes('--headed') });
}

const failures = [];
let checks = 0;
const ok = (v, what) => { checks++; if (!v) failures.push(what); };

const P = 'projects/Physics/chats/CHAT-1/scratch';

async function open(cfg) {
  if (process.env.E2E_DEBUG) console.log('OPEN', cfg.path, cfg.sibling && cfg.sibling.appProperties);
  const page = await browser.newPage({ viewport: { width: 1000, height: 800 } });
  if (process.env.E2E_DEBUG) page.on('console', (m) => console.log('  console', m.type(), m.text().slice(0, 200)));
  const problems = [];
  page.on('console', (m) => {
    const t = m.text();
    if (m.type() === 'error' || /Content.Security.Policy|Refused to/i.test(t)) problems.push(`[${m.type()}] ${t}`);
    else if (m.type() === 'warning') console.log(`  (warning) ${t.slice(0, 160)}`);
  });
  page.on('pageerror', (e) => problems.push(`[pageerror] ${e.message}`));
  page.on('requestfailed', (r) => problems.push(`[requestfailed] ${r.url()} ${r.failure()?.errorText}`));
  await page.goto(`${base}/parent.html#${encodeURIComponent(JSON.stringify(cfg))}`, { waitUntil: 'commit' });
  await page.waitForFunction(() => window.__inits === 1, null, { timeout: 15000 });
  const frame = page.frames().find((f) => f.url().includes('viewer.html'));
  return { page, frame, problems };
}

const reqs = (page) => page.evaluate(() => window.__reqs);

// share of non-white pixels of the first page's canvas, once it has been drawn
async function inked(frame, sel = '.study-scroll .study-page canvas') {
  await frame.waitForSelector(sel, { timeout: 20000 });
  const t0 = Date.now();
  let r = 0;
  while (Date.now() - t0 < 15000) {
    r = await frame.evaluate((s) => {
      const c = document.querySelector(s);
      if (!c || !c.width) return 0;
      const d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data;
      let n = 0;
      for (let i = 0; i < d.length; i += 4) if (d[i + 3] > 0 && d[i] + d[i + 1] + d[i + 2] < 600) n++;
      return n / (d.length / 4);
    }, sel);
    if (r > 0.002) break;
    await new Promise((x) => setTimeout(x, 200));
  }
  return r;
}

function report(name, problems) {
  ok(problems.length === 0, `${name}: console errors / CSP violations:\n    ${problems.join('\n    ')}`);
}

// ---------- 1. a PDF: rendering, text layer, markers, area + Ask, text + WTF, search, contents, close ----------
if (!process.env.E2E_SKIP1) {
  const name = 'pdf';
  const tagged = '[doc: Lecture 3.pdf, p. 1, area 0.100 0.100 0.300 0.050]\n\nWhat is c?\n\n[Study viewer: …]';
  const { page, frame, problems } = await open({
    path: `${P}/Lecture 3.pdf`,
    files: { [`${P}/Lecture 3.pdf`]: { name: 'Lecture 3.pdf', mimeType: 'application/pdf', id: 'F1', md5: 'aaa', url: '/fixture/lecture.pdf' } },
    chat: { canAsk: true, events: [
      { eventId: 'E1', type: 'user_message', text: tagged, status: 'completed', createdUtc: '2026-11-12T10:00:00Z', jobId: 'J1' },
      { eventId: 'E2', type: 'assistant_message', text: 'c is the speed of light.', status: 'completed', createdUtc: '2026-11-12T10:00:10Z', jobId: 'J1' },
      { eventId: 'E3', type: 'user_message', text: '[doc: Lecture 3.pdf, p. 2, page] WTF\n\nx', status: 'running', createdUtc: '2026-11-12T10:01:00Z', jobId: 'J2' },
      { eventId: 'E4', type: 'user_message', text: '[doc: Other.pdf, p. 1, page]\n\nnot ours', status: 'completed', createdUtc: '2026-11-12T10:02:00Z', jobId: 'J3' },
    ] },
  });
  const ink = await inked(frame);
  ok(ink > 0.002, `${name}: page 1 canvas is blank (${ink})`);
  ok((await frame.locator('.study-page').count()) === 3, `${name}: three pages`);
  ok((await frame.locator('.study-pages').innerText()).includes('3'), `${name}: page count in the bar`);
  await frame.waitForSelector('.study-page[data-page="1"] .textLayer span', { timeout: 10000 });
  const words = await frame.locator('.study-page[data-page="1"] .textLayer').innerText();
  ok(words.includes('Lecture 3') && words.includes('rest energy'), `${name}: text layer: ${words.slice(0, 80)}`);
  ok((await frame.locator('.study-name').innerText()).includes('Lecture 3.pdf'), `${name}: name in the bar`);

  // markers: E1 answered on page 1, E3 waiting on page 2, E4 is another document
  const m1 = frame.locator('.study-page[data-page="1"] .study-marker');
  ok((await m1.count()) === 1 && (await m1.getAttribute('class')).includes('done') && (await m1.innerText()) === '?', `${name}: answered marker on page 1`);
  const m2 = frame.locator('.study-page[data-page="2"] .study-marker');
  ok((await m2.count()) === 1 && !(await m2.getAttribute('class')).includes('done') && (await m2.innerText()) === '!', `${name}: waiting WTF marker on page 2`);
  ok((await frame.locator('.study-marker').count()) === 2, `${name}: no marker of another document`);
  await m1.click();
  await page.waitForTimeout(200);
  let r = await reqs(page);
  ok(r.some((x) => x.name === 'chat.reveal' && x.args.eventId === 'E1'), `${name}: marker click -> chat.reveal E1`);

  // area selection + Ask
  await frame.getByRole('button', { name: '▭ Area' }).click();
  const box = await frame.locator('.study-page[data-page="1"]').boundingBox();
  await page.mouse.move(box.x + box.width * 0.1, box.y + box.height * 0.05);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width * 0.5, box.y + box.height * 0.12, { steps: 6 });
  await page.mouse.up();
  await frame.waitForSelector('.study-ask textarea', { timeout: 5000 });
  ok((await frame.locator('.study-rect').count()) === 1, `${name}: area rectangle shown`);
  await frame.locator('.study-ask textarea').fill('Why is c squared?');
  await frame.getByRole('button', { name: 'Ask', exact: true }).click();
  await page.waitForFunction(() => window.__reqs.some((x) => x.name === 'chat.ask'), null, { timeout: 15000 });
  r = await reqs(page);
  const ask = r.find((x) => x.name === 'chat.ask');
  const text = ask.args.text;
  ok(/^\[doc: Lecture 3\.pdf, p\. 1, area 0\.\d{3} 0\.\d{3} 0\.\d{3} 0\.\d{3}\]\n\nWhy is c squared\?/.test(text), `${name}: ask text starts with the tag: ${text.slice(0, 90)}`);
  ok(text.includes(`[Study viewer: the question is about page 1 of AI-Workbench/${P}/Lecture 3.pdf.`) && text.includes('attached PNG') && text.includes('text of pages 1-2'), `${name}: ask note`);
  const [pngF, txtF] = ask.args.files || [];
  ok(pngF && pngF.isArrayBuffer && pngF.type === 'image/png' && pngF.head.startsWith('89504e47') && /^Lecture 3 p1 \d{10}\.png$/.test(pngF.name), `${name}: PNG attached: ${JSON.stringify(pngF && { ...pngF, text: undefined })}`);
  ok(pngF && pngF.width > 300 && pngF.nonWhite > 50, `${name}: PNG has content (${pngF && pngF.width}x${pngF && pngF.height}, ${pngF && pngF.nonWhite} dark px)`);
  ok(txtF && txtF.type === 'text/plain' && txtF.text.startsWith('===== page 1 =====\n') && txtF.text.includes('===== page 2 =====') && txtF.text.includes('Conservation of momentum') && /^Lecture 3 p1-2 \d{10}\.txt$/.test(txtF.name), `${name}: page text attached: ${txtF && txtF.name}`);
  await frame.waitForSelector('.study-ask', { state: 'detached', timeout: 5000 });

  // text selection + WTF (area mode off)
  await frame.getByRole('button', { name: '▭ Area' }).click();
  await frame.locator('.study-page[data-page="2"]').scrollIntoViewIfNeeded();
  await frame.locator('.study-page[data-page="2"] .textLayer span', { hasText: 'p = m v' }).waitFor({ timeout: 10000 });
  await frame.evaluate(() => {
    const span = [...document.querySelectorAll('.study-page[data-page="2"] .textLayer span')].find((s) => s.textContent.includes('p = m v'));
    const range = document.createRange();
    range.selectNodeContents(span);
    const sel = window.getSelection();
    sel.removeAllRanges();
    sel.addRange(range);
    span.dispatchEvent(new MouseEvent('mouseup', { bubbles: true }));
  });
  await frame.waitForSelector('.study-ask', { timeout: 5000 });
  await frame.getByRole('button', { name: 'WTF?!' }).click();
  await page.waitForFunction(() => window.__reqs.filter((x) => x.name === 'chat.ask').length === 2, null, { timeout: 15000 });
  r = await reqs(page);
  const wtf = r.filter((x) => x.name === 'chat.ask')[1].args.text;
  ok(/^\[doc: Lecture 3\.pdf, p\. 2, text [\d. ]+\] WTF\n\nI do not understand this at all\./.test(wtf), `${name}: WTF text: ${wtf.slice(0, 80)}`);
  ok(wtf.includes('Selected text (page 2): «p = m v for slow bodies.»') && wtf.includes('text of pages 1-3'), `${name}: selected text in the WTF message`);

  // canAsk false disables the buttons
  await page.evaluate(() => window.__chat([], false));
  await frame.evaluate(() => {
    const span = document.querySelector('.study-page[data-page="1"] .textLayer span');
    const range = document.createRange();
    range.selectNodeContents(span);
    window.getSelection().removeAllRanges();
    window.getSelection().addRange(range);
    span.dispatchEvent(new MouseEvent('mouseup', { bubbles: true }));
  });
  await frame.waitForSelector('.study-ask', { timeout: 5000 });
  ok(await frame.getByRole('button', { name: 'WTF?!' }).isDisabled(), `${name}: WTF disabled while the chat cannot ask`);
  ok((await frame.locator('.study-marker').count()) === 0, `${name}: markers follow the chat events`);
  await frame.locator('.study-ask-row .btn.ghost').click();

  // search (Ctrl+F focuses the box)
  await frame.locator('.study').hover();
  await page.keyboard.press('Control+f');
  ok(await frame.evaluate(() => document.activeElement?.getAttribute('type') === 'search'), `${name}: Ctrl+F focuses search`);
  await page.keyboard.type('momentum');
  await page.keyboard.press('Enter');
  await frame.waitForSelector('.study-search .tiny', { timeout: 5000 });
  const hits = await frame.locator('.study-search .tiny').innerText();
  ok(hits === '1/2 p.', `${name}: search hits "${hits}"`);
  await frame.waitForSelector('.textLayer span.study-hit', { timeout: 5000 });

  // zoom
  const z0 = await frame.locator('.study-bar button[title="Fit width"]').innerText();
  await frame.locator('.study-bar button[title^="Zoom in"]').click();
  const z1 = await frame.locator('.study-bar button[title="Fit width"]').innerText();
  ok(parseInt(z1) > parseInt(z0), `${name}: zoom in ${z0} -> ${z1}`);

  // thumbnails and contents
  await frame.locator('.study-bar button[title="Pages and contents"]').click();
  ok((await frame.locator('.study-thumb').count()) === 3, `${name}: three thumbnails`);
  const thumbInk = await inked(frame, '.study-thumb canvas');
  ok(thumbInk > 0.002, `${name}: thumbnail drawn`);
  await frame.getByRole('button', { name: 'Contents' }).click();
  await frame.waitForSelector('.study-outline .link', { timeout: 5000 });
  ok((await frame.locator('.study-outline .link').allInnerTexts()).join('|') === 'Chapter 1|Chapter 2|Chapter 3', `${name}: outline`);

  await frame.locator('.study-bar button[title="Close the document"]').click();
  await page.waitForTimeout(200);
  ok((await reqs(page)).some((x) => x.name === 'panel.close'), `${name}: close -> panel.close`);
  ok((await reqs(page)).filter((x) => x.name === 'file.get').length === 1 && !(await reqs(page)).some((x) => /file\.(pdf|sibling|putSibling)/.test(x.name)), `${name}: a pdf needs only file.get`);
  report(name, problems);
  await page.close();
}

// ---------- 2. Office file, no copy yet: file.pdf, then file.putSibling with the cache keys ----------
{
  const name = 'docx-new';
  const doc = `${P}/notes.docx`;
  const { page, frame, problems } = await open({
    path: doc,
    files: { [doc]: { name: 'notes.docx', mimeType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', id: 'D1', md5: 'md5-1', url: '/fixture/notes.docx' } },
    pdfUrl: '/fixture/converted.pdf',
    sibling: null,
    chat: { canAsk: true, events: [] },
  });
  ok((await inked(frame)) > 0.002, `${name}: converted page drawn`);
  await frame.waitForSelector('.textLayer span');
  ok((await frame.locator('.textLayer').first().innerText()).includes('came from a docx'), `${name}: converted text`);
  const r = await reqs(page);
  const names = r.map((x) => x.name);
  ok(JSON.stringify(names) === JSON.stringify(['file.get', 'file.sibling', 'file.pdf', 'file.putSibling']), `${name}: request order ${names}`);
  ok(r[1].args.path === doc && r[1].args.name === 'notes.docx.study.pdf' && r[1].args.withBytes === false, `${name}: sibling args ${JSON.stringify(r[1].args)}`);
  const put = r.find((x) => x.name === 'file.putSibling');
  ok(put && put.args.path === doc && put.args.name === 'notes.docx.study.pdf' && put.args.mimeType === 'application/pdf'
    && JSON.stringify(put.args.appProperties) === '{"studyOf":"D1","studySource":"md5-1"}' && put.args.bytes.head === '255044462d', `${name}: putSibling ${JSON.stringify(put && put.args)}`);
  ok((await frame.locator('.study-note').count()) === 0, `${name}: no note`);
  report(name, problems);
  await page.close();
}

// ---------- 3. Office file with an up-to-date copy: reused, no conversion ----------
{
  const name = 'docx-cached';
  const doc = `${P}/notes.docx`;
  const { page, frame, problems } = await open({
    path: doc,
    files: { [doc]: { name: 'notes.docx', mimeType: 'application/octet-stream', id: 'D1', md5: 'md5-1', url: '/fixture/notes.docx' } },
    pdfUrl: '/fixture/converted.pdf',
    sibling: { id: 'C1', name: 'notes.docx.study.pdf', appProperties: { studyOf: 'D1', studySource: 'md5-1' }, url: '/fixture/cached.pdf' },
    chat: { canAsk: true, events: [] },
  });
  ok((await inked(frame)) > 0.002, `${name}: cached page drawn`);
  await frame.waitForSelector('.textLayer span');
  ok((await frame.locator('.textLayer').first().innerText()).includes('study copy'), `${name}: cached text`);
  const names = (await reqs(page)).map((x) => x.name);
  ok(JSON.stringify(names) === JSON.stringify(['file.get', 'file.sibling', 'file.sibling']), `${name}: request order ${names}`);
  const sibs = (await reqs(page)).filter((x) => x.name === 'file.sibling');
  ok(sibs[0].args.withBytes === false && sibs[1].args.withBytes === true, `${name}: metadata first, bytes only for a valid copy`);
  report(name, problems);
  await page.close();
}

// ---------- 4. a stale copy is replaced; a copy of another file is left alone with a note; save failure -> note ----------
for (const [name, sibling, putFails, expectPut, note, pdfNote] of [
  ['docx-stale', { id: 'C1', name: 'notes.docx.study.pdf', appProperties: { studyOf: 'D1', studySource: 'old' }, url: '/fixture/cached.pdf' }, false, true, 'stayed in your My Drive', 'The temporary copy "~ notes.docx" stayed in your My Drive; delete it there.'],
  ['docx-foreign', { id: 'C9', name: 'notes.docx.study.pdf', appProperties: { studyOf: 'OTHER', studySource: 'md5-1' }, url: '/fixture/cached.pdf' }, false, false, 'already exists and is not the viewer\'s copy'],
  ['docx-putfails', null, true, true, 'The PDF copy was not saved: Drive said no'],
]) {
  const doc = `${P}/notes.docx`;
  const { page, frame, problems } = await open({
    path: doc,
    files: { [doc]: { name: 'notes.docx', mimeType: 'application/octet-stream', id: 'D1', md5: 'md5-1', url: '/fixture/notes.docx' } },
    pdfUrl: '/fixture/converted.pdf', pdfNote, sibling, putFails, chat: { canAsk: true, events: [] },
  });
  await frame.waitForSelector('.textLayer span', { timeout: 20000 });
  ok((await frame.locator('.textLayer').first().innerText()).includes('came from a docx'), `${name}: converted, not the copy`);
  const names = (await reqs(page)).map((x) => x.name);
  ok(names.includes('file.pdf') && names.includes('file.putSibling') === expectPut, `${name}: requests ${names}`);
  const shown = (await frame.locator('.study-note').count()) ? await frame.locator('.study-note').innerText() : null;
  ok(note ? shown && shown.includes(note) : shown === null, `${name}: note "${shown}"`);
  report(name, problems);
  await page.close();
}

// ---------- 5. an image becomes a one-page PDF in the page ----------
{
  const name = 'image';
  const doc = `${P}/scan.png`;
  const { page, frame, problems } = await open({
    path: doc,
    files: { [doc]: { name: 'scan.png', mimeType: 'image/png', id: 'I1', md5: 'img-1', url: '/fixture/scan.png' } },
    sibling: null, chat: { canAsk: true, events: [] },
  });
  ok((await inked(frame)) > 0.05, `${name}: image page drawn`);
  const r = await reqs(page);
  const put = r.find((x) => x.name === 'file.putSibling');
  ok(!r.some((x) => x.name === 'file.pdf') && put && put.args.name === 'scan.png.study.pdf' && put.args.appProperties.studyOf === 'I1' && put.args.bytes.head === '255044462d', `${name}: built here and saved: ${r.map((x) => x.name)}`);
  report(name, problems);
  await page.close();
}

// ---------- 6. unsupported type and a missing file: messages, no crash ----------
{
  const { page, frame, problems } = await open({ path: `${P}/a.zip`, files: {}, chat: { canAsk: true, events: [] } });
  await frame.waitForSelector('.error-text', { timeout: 10000 });
  ok((await frame.locator('.error-text').innerText()).includes('does not open in the study viewer'), 'zip: message');
  report('zip', problems);
  await page.close();
  const b = await open({ path: `${P}/gone.pdf`, files: {}, chat: { canAsk: true, events: [] } });
  await b.frame.waitForSelector('.error-text', { timeout: 10000 });
  ok((await b.frame.locator('.error-text').innerText()).includes('File not found on Drive.'), 'missing: message');
  report('missing', b.problems);
  await b.page.close();
}

ok(!served.some((s) => s.startsWith('/agent/module.php') && !/f=ui\/viewer\.(html|js|css)$/.test(s)), `the page loaded only its own files: ${served.filter((s) => s.startsWith('/agent')).join(', ')}`);

// browser.close() of Playwright 1.63 sometimes never returns on Windows: do not wait for it longer than 10 s
await Promise.race([browser.close(), new Promise((x) => setTimeout(x, 10000))]);
server.closeAllConnections();
server.close();
if (failures.length) {
  console.error(`viewer e2e FAILED (${failures.length} of ${checks}):\n- ` + failures.join('\n- '));
  process.exitCode = 1;
} else {
  console.log(`viewer e2e ok (${checks} checks)`);
}
process.exit(process.exitCode || 0);
