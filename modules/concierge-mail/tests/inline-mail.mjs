// SPDX-License-Identifier: AGPL-3.0-or-later
// Install Playwright locally or set PLAYWRIGHT_MODULE to its file: URL.
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE || 'playwright');
import fs from 'node:fs/promises';
import assert from 'node:assert/strict';
const root = new URL('../ui/', import.meta.url);
const html = await fs.readFile(new URL('mail.html', root), 'utf8');
const browser = await chromium.launch({headless:true});
const errors = [];
async function pageFor(ctx, width = 300) {
  const page = await browser.newPage({viewport:{width,height:620}});
  page.on('pageerror', e => errors.push(e.message));
  await page.addInitScript(ctx => {
    window.calls = []; window.requests = []; window.events = {};
    window.coExt = {ready:Promise.resolve(ctx), title(){}, size(){}, on(k,f){window.events[k]=f;},
      async req(op,args){window.requests.push({op,args});return {};},
      async call(){return {paired:true,online:true,env:'test',account:'owner@example.invalid',pub:'mock'};}};
    window.HomeRelay = () => ({async call(op,args){
      window.calls.push({op,args});
      const t={thread:'abc',subject:'Synthetic letter',from:['Sender <sender@example.invalid>'],count:1,date:Date.now(),labels:[],unread:0};
      if(op==='chat_threads'||op==='list')return {threads:[t],next:null};
      if(op==='thread')return {messages:[{id:'m1',subject:t.subject,from:t.from[0],to:'owner@example.invalid',text:'Synthetic body',date:Date.now(),labels:[]}]};
      if(op==='overview')return {labels:[],unread:0};
      if(op==='chats')return {chats:[]};
      if(op==='links')return {links:[]};
      if(op==='compose_start')return {key:'k1',fields:{kind:args.kind,to:'sender@example.invalid',subject:'Re: Synthetic letter',text:''},attachments:[]};
      if(op==='compose_save')return {draft:'d1',attachments:[]};
      if(op==='compose_send')return {draft:'d1',undo:10};
      if(op==='compose_outbox')return {items:ctx.pending ? [{key:'k1',state:'wait',due:Date.now()/1000+10}] : []};
      if(op==='compose_unsend'){ctx.pending=false;return {cancelled:true};}
      if(op==='compose_drafts')return {drafts:[{key:'k1',draft:'d1',fields:{kind:'reply',to:'sender@example.invalid',text:'Test reply'},attachments:[]}]};
      return {};
    }});
  },ctx);
  await page.route('**/*', async route => {
    const url = new URL(route.request().url());
    const f = url.searchParams.get('f');
    if(f === 'ui/mail.css' || f === 'ui/mail.js')return route.fulfill({body:await fs.readFile(new URL(f.slice(3), root)),contentType:f.endsWith('css')?'text/css':'text/javascript'});
    if(url.pathname.endsWith('.js') || f?.endsWith('.js'))return route.fulfill({body:'',contentType:'text/javascript'});
    return route.fulfill({body:html,contentType:'text/html'});
  });
  await page.goto('https://mail.test/');
  return page;
}
try {
  const page = await pageFor({slot:'ui.chatPart',chat:{chatId:'chat-a',projectId:'project-a'}});
  await page.locator('.thread').waitFor();
  assert.equal(await page.locator('#search-form').isVisible(),false);
  assert.equal(await page.locator('#compose-new').isVisible(),false);
  assert.deepEqual(await page.evaluate(()=>calls.map(x=>x.op)),['chat_threads','compose_outbox']);
  assert.equal(await page.evaluate(()=>calls[0].args.chat),'chat-a');
  assert.equal(await page.evaluate(()=>calls[1].args.chat),'chat-a');
  await page.evaluate(()=>events.context({slot:'ui.chatPart',chat:{chatId:'chat-a',projectId:'project-a'},count:2}));
  await page.waitForFunction(()=>calls.filter(x=>x.op==='chat_threads').length===2);
  await page.locator('.thread').click();
  await page.evaluate(()=>events.context({slot:'ui.chatPart',chat:{chatId:'chat-a',projectId:'project-a'},count:3}));
  assert.equal(await page.evaluate(()=>calls.filter(x=>x.op==='chat_threads').length),2);
  await page.getByRole('button',{name:'↩ Ответить',exact:true}).click();
  await page.locator('#c-text').fill('Test reply');
  await page.waitForFunction(()=>calls.some(x=>x.op==='compose_save'));
  assert.equal(await page.evaluate(()=>calls.find(x=>x.op==='compose_save').args.fields.text),'Test reply');
  assert.equal(await page.evaluate(()=>calls.some(x=>x.op==='compose_send')),false);
  const bounds = await page.locator('#c-send').boundingBox();
  assert.ok(bounds && bounds.x>=0 && bounds.x+bounds.width<=301 && bounds.y+bounds.height<=621);
  assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
  if (process.env.MAIL_UI_SHOT) await page.screenshot({path:process.env.MAIL_UI_SHOT});
  // All send/cancel calls below are intercepted by the synthetic HomeRelay, no network backend exists.
  await page.locator('#c-send').click();
  await page.locator('#undo').waitFor({state:'visible'});
  assert.match(await page.locator('#undo-text').textContent(),/10/);
  await page.locator('#undo').click();
  await page.locator('#composer').waitFor({state:'visible'});
  assert.ok(await page.evaluate(()=>calls.some(x=>x.op==='compose_unsend')));
  await page.locator('#composer-close').click();
  await page.locator('#back').click();
  await page.waitForFunction(()=>calls.filter(x=>x.op==='chat_threads').length===3);
  assert.equal(await page.locator('#list-pane').isVisible(),true);
  await page.close();
  const pending = await pageFor({slot:'ui.chatPart',chat:{chatId:'chat-a',projectId:'project-a'},pending:true});
  await pending.locator('#undo').waitFor({state:'visible'});
  assert.equal(await pending.evaluate(()=>calls.find(x=>x.op==='compose_outbox').args.chat),'chat-a');
  await pending.locator('#undo').click();
  await pending.locator('#composer').waitFor({state:'visible'});
  await pending.close();
  const missing = await pageFor({slot:'ui.chatPart'});
  await missing.getByText('Нет чата для переписки').waitFor();
  assert.deepEqual(await missing.evaluate(()=>calls),[]);
  await missing.close();
  const app = await pageFor({slot:'ui.page'},1024);
  await app.locator('.thread').waitFor();
  assert.equal(await app.locator('#search-form').isVisible(),true);
  assert.ok(await app.evaluate(()=>calls.some(x=>x.op==='list'&&x.args.label==='INBOX')));
  await app.close();
  assert.deepEqual(errors,[]);
  console.log('PASS: scoped folder, missing context, 300px composer, autosave, mock send/cancel, back, regular app; no page errors');
} finally {await browser.close();}
