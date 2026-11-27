// SPDX-License-Identifier: AGPL-3.0-or-later
// 0.17.0 message card (ui.messageCard) of ui/mail.js against a synthetic HomeRelay: no backend, no Gmail.
// Install Playwright locally or set PLAYWRIGHT_MODULE to its file: URL (PLAYWRIGHT_CHANNEL=msedge for the system Edge).
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE || 'playwright');
import fs from 'node:fs/promises';
import assert from 'node:assert/strict';
const root = new URL('../ui/', import.meta.url);
const html = await fs.readFile(new URL('mail.html', root), 'utf8');
const browser = await chromium.launch({headless:true, ...(process.env.PLAYWRIGHT_CHANNEL ? {channel:process.env.PLAYWRIGHT_CHANNEL} : {})});
const errors = [];
async function cardFor(ctx, plan) {
  const page = await browser.newPage({viewport:{width:640,height:900}});
  page.setDefaultTimeout(8000);
  page.on('pageerror', e => errors.push(e.message));
  await page.addInitScript(([ctx, plan]) => {
    window.calls = []; window.sizes = []; window.navs = []; window.plan = plan;
    window.coExt = {ready:Promise.resolve(ctx), title(){}, size(w,h){window.sizes.push(h);}, on(){}, navigate(u,t){window.navs.push([u,t]);},
      async req(){return {};}, async call(){return {paired:true,online:true,env:'test',account:'owner@example.invalid',pub:'mock'};}};
    window.HomeRelay = () => ({async call(op,args){
      window.calls.push({op,args:JSON.parse(JSON.stringify(args))});
      const step = (window.plan[op] || []).shift();
      if (step && step.error) throw new Error(step.error);
      if (step) return step;
      if (op==='compose_save') return {draft:'d1',attachments:[],revision:'r'+(calls.length)};
      if (op==='compose_send') return {draft:'d1',undo:10,state:'wait'};
      if (op==='compose_outbox') return {items:[]};
      if (op==='compose_card') return {state:'draft',draft:'d1',revision:args.rev||'19acde1230a1'};
      return {};
    }});
  }, [ctx, plan]);
  await page.route('**/*', async route => {
    const url = new URL(route.request().url());
    const f = url.searchParams.get('f');
    if (f === 'ui/mail.css' || f === 'ui/mail.js') return route.fulfill({body:await fs.readFile(new URL(f.slice(3), root)),contentType:f.endsWith('css')?'text/css':'text/javascript'});
    if (url.pathname.endsWith('.js') || f?.endsWith('.js')) return route.fulfill({body:'',contentType:'text/javascript'});
    return route.fulfill({body:html,contentType:'text/html'});
  });
  await page.goto('https://mail.test/');
  return page;
}
const ctx = {slot:'ui.messageCard', card:{id:'18c0ffee'}, chat:{chatId:'chat-a',projectId:'project-a'}, eventId:'e1'};
const draft = {state:'draft', key:'a'.repeat(32), draft:'d1', revision:'19acde1230a1', thread:'t1', attachments:[{sha:'s', name:'offer.pdf', type:'application/pdf', size:2048}],
  fields:{kind:'new', to:'sender@example.invalid', cc:'', bcc:'', subject:'Re: Synthetic letter', text:'Synthetic draft body'}};
const ops = page => page.evaluate(() => calls.map(x => x.op));
try {
  // 1. the live draft in the composer, no mailbox, no focus taken, revision on every write
  let page = await cardFor(ctx, {compose_card:[draft], compose_save:[{draft:'d1',attachments:draft.attachments,revision:'19acde1230b2'}, {draft:'d1',attachments:draft.attachments,revision:'19acde1230c3'}],
    thread:[{messages:[{id:'m1',subject:'Synthetic letter',from:'Sender <sender@example.invalid>',to:'owner@example.invalid',text:'Original question',date:Date.now(),labels:[]},
      {id:'m2',subject:'Re',from:'me',to:'x',text:'draft copy',date:Date.now(),labels:['DRAFT']}]}]});
  await page.locator('#composer').waitFor({state:'visible'});
  assert.deepEqual(await ops(page), ['compose_card']);
  assert.equal(await page.evaluate(() => calls[0].args.draft), '18c0ffee');
  assert.equal(await page.locator('#c-text').inputValue(), 'Synthetic draft body');
  assert.equal(await page.locator('#c-to').inputValue(), 'sender@example.invalid');
  assert.match(await page.locator('#c-attachments').textContent(), /offer\.pdf/);
  for (const id of ['#search-form', '#labels', '#threads', '#composer-close']) assert.equal(await page.locator(id).isVisible(), false, id);
  assert.equal(await page.evaluate(() => document.activeElement === document.body), true, 'focus stays with the chat');
  assert.ok(await page.evaluate(() => sizes.length > 0 && sizes.at(-1) > 200), 'asks for its height');
  await page.waitForTimeout(2000);
  assert.deepEqual(await ops(page), ['compose_card'], 'an idle card writes nothing');
  await page.locator('#c-text').fill('Edited in the card');
  await page.waitForFunction(() => calls.some(x => x.op === 'compose_save'));
  await page.waitForTimeout(2500);
  assert.equal(await page.evaluate(() => calls.filter(x => x.op === 'compose_save').length), 1, 'one edit, one save');
  assert.deepEqual(await page.evaluate(() => [calls.find(x => x.op === 'compose_save').args.rev, calls.find(x => x.op === 'compose_save').args.fields.text]), ['19acde1230a1', 'Edited in the card']);
  await page.locator('#c-text').fill('Edited twice');
  await page.waitForFunction(() => calls.filter(x => x.op === 'compose_save').length === 2);
  assert.equal(await page.evaluate(() => calls.filter(x => x.op === 'compose_save')[1].args.rev), '19acde1230b2');
  // the thread is folded and loads on opening; the draft copy itself is not shown in it
  assert.equal(await page.evaluate(() => calls.some(x => x.op === 'thread')), false);
  await page.locator('#card-thread summary').click();
  await page.locator('#card-thread-body .message').first().waitFor();
  assert.equal(await page.locator('#card-thread-body .message').count(), 1);
  assert.equal(await page.evaluate(() => calls.find(x => x.op === 'thread').args.thread), 't1');
  // Open in ✉ names the Gmail draft id, not the message id of the marker
  await page.locator('#card-open').click();
  assert.deepEqual(await page.evaluate(() => navs), [['/x/concierge-mail/mail?draft=d1', 'self']]);
  // Ctrl+Enter never sends from a card; the button does, with the revision and the 10 s Cancel
  await page.locator('#c-text').press('Control+Enter');
  assert.equal(await page.evaluate(() => calls.some(x => x.op === 'compose_send')), false);
  await page.locator('#c-send').click();
  await page.locator('#undo').waitFor({state:'visible'});
  assert.equal(await page.evaluate(() => calls.find(x => x.op === 'compose_send').args.rev), '19acde1230c3');
  assert.match(await page.locator('#undo-text').textContent(), /10/);
  await page.close();

  // 2. changed outside: a save is refused, the card offers the current version (loaded without rev)
  page = await cardFor(ctx, {compose_card:[draft, {...draft, revision:'19acde1230f9', fields:{...draft.fields, text:'Changed in Gmail'}}], compose_save:[{error:'conflict'}]});
  await page.locator('#composer').waitFor({state:'visible'});
  await page.locator('#c-text').fill('Mine');
  await page.locator('#card-reload').waitFor({state:'visible'});
  assert.match(await page.locator('#card-state').textContent(), /изменён/);
  await page.locator('#card-reload').click();
  await page.waitForFunction(() => calls.filter(x => x.op === 'compose_card').length === 2);
  assert.equal(await page.evaluate(() => 'rev' in calls.filter(x => x.op === 'compose_card')[1].args), false);
  await page.waitForFunction(() => document.getElementById('c-text').value === 'Changed in Gmail');
  assert.equal(await page.locator('#card-reload').isVisible(), false);
  await page.close();

  // 3. gone without a send from here: shown as gone, never as sent, no letter text
  page = await cardFor(ctx, {compose_card:[{state:'unavailable', draft:'d1'}]});
  await page.waitForFunction(() => /удалён или отправлен вне CO/.test(document.getElementById('card-state').textContent));
  assert.equal(await page.locator('#composer').isVisible(), false);
  assert.doesNotMatch(await page.locator('body').innerText(), /Отправлено/);
  await page.locator('#card-open').click();
  assert.deepEqual(await page.evaluate(() => navs), [['/x/concierge-mail/mail', 'self']]);
  await page.close();

  // 4. sent from here: the time; a send still waiting: its Cancel bar
  page = await cardFor(ctx, {compose_card:[{state:'sent', draft:'d1', updated:1700000000, thread:'t1', sent:'s1'}]});
  await page.waitForFunction(() => /^Отправлено/.test(document.getElementById('card-state').textContent));
  await page.close();
  page = await cardFor(ctx, {compose_card:[{state:'wait', key:'a'.repeat(32), draft:'d1', due:Date.now() / 1000 + 8}, {...draft, revision:'19acde1230d4'}],
    compose_unsend:[{cancelled:true, state:'cancelled'}]});
  await page.locator('#undo').waitFor({state:'visible'});
  assert.equal(await page.locator('#composer').isVisible(), false);
  // reload during wait -> Cancel -> the letter comes back once, with its current revision, and edits carry it
  await page.locator('#undo').click();
  await page.locator('#composer').waitFor({state:'visible'});
  await page.waitForTimeout(300);
  assert.deepEqual(await ops(page), ['compose_card', 'compose_unsend', 'compose_card']);
  await page.locator('#c-text').fill('After cancel');
  await page.waitForFunction(() => calls.some(x => x.op === 'compose_save'));
  assert.equal(await page.evaluate(() => calls.find(x => x.op === 'compose_save').args.rev), '19acde1230d4');
  await page.close();

  // 5. a bad card id: nothing is asked
  page = await cardFor({...ctx, card:{id:'../x'}}, {});
  await page.waitForFunction(() => /Неверная ссылка/.test(document.getElementById('card-state').textContent));
  assert.deepEqual(await ops(page), []);
  await page.close();

  // 6. ✉ opened from a card: the draft opens in the app's composer
  page = await cardFor({slot:'ui.page', params:{draft:'d1'}}, {compose_open:[draft], list:[{threads:[],next:null}]});
  await page.locator('#composer').waitFor({state:'visible'});
  assert.equal(await page.evaluate(() => calls.find(x => x.op === 'compose_open').args.draft), 'd1');
  await page.close();

  assert.deepEqual(errors, []);
  console.log('PASS: card draft with revision, folded thread, button-only send, ✉ by draft id, conflict reload, wait->cancel->edit, unavailable/sent/wait states, bad id, ✉ ?draft=; no page errors');
} finally { await browser.close(); }
