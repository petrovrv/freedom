// SPDX-License-Identifier: AGPL-3.0-or-later
// Install Playwright locally or set PLAYWRIGHT_MODULE to its file: URL.
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE || 'playwright');
import fs from 'node:fs/promises';
import assert from 'node:assert/strict';
const root = new URL('../ui/', import.meta.url);
const html = await fs.readFile(new URL('mail.html', root), 'utf8');
const browser = await chromium.launch({headless:true, ...(process.env.PLAYWRIGHT_CHANNEL ? {channel:process.env.PLAYWRIGHT_CHANNEL} : {})});
const errors = [];
async function pageFor(ctx, width = 300) {
  const page = await browser.newPage({viewport:{width,height:620}});
  page.on('pageerror', e => errors.push(e.message));
  await page.addInitScript(ctx => {
    window.calls = []; window.requests = []; window.events = {};
    window.coExt = {ready:Promise.resolve(ctx), title(){}, size(){}, on(k,f){window.events[k]=f;},
      async req(op,args){window.requests.push({op,args});return {};},
      async call(op){if(op==='mail_agent_place') return {projectId:'p',chatId:'mail-chat'};return {paired:true,online:true,env:'test',account:'owner@example.invalid',pub:'mock'};}};
    window.HomeRelay = () => ({async call(op,args){
      window.calls.push({op,args});
      const t={thread:'abc',subject:'Synthetic letter',from:['Sender <sender@example.invalid>'],count:1,date:Date.now(),labels:[],unread:0};
      if(op==='chat_threads'||op==='list')return {threads:[t,{...t,thread:'xyz',subject:'Other letter'}],next:null};
      if(op==='thread')return {messages:[...(ctx.threadTainted?[{id:'old',tainted:true,text:'Older guarded message',labels:[]}]:[]),{id:args.thread==='xyz'?'m2':'m1',sensitive:!!ctx.sensitive,tainted:!!ctx.tainted,subject:ctx.tainted?'UNTRUSTED SUBJECT':t.subject,from:t.from[0],to:'owner@example.invalid',text:'Synthetic body',date:Date.now(),labels:[]}]};
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
  const ctx={slot:'ui.panel',chat:{chatId:'chat-a',projectId:'project-a'}};
  const page=await pageFor(ctx,1000);
  await page.locator('.thread').first().waitFor();
  await page.locator('.thread').nth(1).click({button:'right'});
  await page.getByRole('menuitem',{name:'AI ▸'}).click();
  await page.getByRole('menuitem',{name:'Prepare draft',exact:true}).click();
  await page.waitForFunction(()=>requests.some(x=>x.op==='chat.ask'));
  let req=await page.evaluate(()=>requests.find(x=>x.op==='chat.ask'));
  assert.match(req.args.text,/#m2/); assert.match(req.args.text,/Подготовь черновик/);
  assert.equal(req.args.send,false);
  assert.equal(await page.evaluate(()=>calls.some(x=>x.op==='mark'||x.op.startsWith('compose_')&&x.op!=='compose_outbox')),false);
  await page.locator('.thread').first().click();
  await page.getByRole('button',{name:'AI ▾',exact:true}).click();
  await page.getByRole('button',{name:'AI ▾',exact:true}).click();
  assert.equal(await page.locator('.ai-menu').count(),0);
  await page.getByRole('button',{name:'AI ▾',exact:true}).click();
  await page.keyboard.press('Escape');
  assert.equal(await page.locator('.ai-menu').count(),0);
  assert.equal(await page.evaluate(()=>document.body.dataset.pane),'reader');
  await page.getByRole('button',{name:'AI ▾',exact:true}).click();
  await page.keyboard.press('End');
  assert.equal(await page.evaluate(()=>document.activeElement.textContent),'Translate / explain');
  await page.keyboard.press('Enter');
  await page.waitForFunction(()=>requests.filter(x=>x.op==='chat.ask').length===2);
  assert.match(await page.evaluate(()=>requests.filter(x=>x.op==='chat.ask')[1].args.text),/#m1/);
  await page.getByRole('button',{name:'AI ▾',exact:true}).click();
  await page.mouse.click(10, 100);
  assert.equal(await page.locator('.ai-menu').count(),0);
  // Native context menu remains for selected text.
  assert.equal(await page.locator('.message').evaluate(n=>{const r=document.createRange();r.selectNodeContents(n.querySelector('summary'));getSelection().removeAllRanges();getSelection().addRange(r);return n.dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true}));}),true);
  await page.close();
  const narrow=await pageFor({slot:'ui.chatPart',chat:ctx.chat},300);
  await narrow.locator('.thread').first().click();
  await narrow.getByRole('button',{name:'AI ▾',exact:true}).click();
  const bounds=await narrow.locator('.ai-menu').boundingBox();
  assert.ok(bounds.x>=0&&bounds.x+bounds.width<=300&&bounds.y+bounds.height<=620);
  if(process.env.MAIL_UI_SHOT)await narrow.screenshot({path:process.env.MAIL_UI_SHOT});
  await narrow.getByRole('menuitem',{name:'Discuss',exact:true}).click();
  await narrow.waitForFunction(()=>requests.some(x=>x.op==='chat.open'));
  assert.equal(await narrow.evaluate(()=>requests.find(x=>x.op==='chat.open').args.chatId),'chat-a');
  await narrow.close();
  const guarded=await pageFor({...ctx,tainted:true},1000);
  await guarded.locator('.thread').first().click({button:'right'});
  await guarded.getByRole('menuitem',{name:'AI ▸'}).click();
  await guarded.getByRole('menuitem',{name:'Prepare draft',exact:true}).click();
  await guarded.waitForFunction(()=>requests.some(x=>x.op==='chat.ask'));
  const text=await guarded.evaluate(()=>requests.find(x=>x.op==='chat.ask').args.text);
  assert.doesNotMatch(text,/UNTRUSTED|Sender/);assert.match(text,/явное согласие/);
  await guarded.close();
  const threadGuard=await pageFor({...ctx,threadTainted:true},1000);
  await threadGuard.locator('.thread').first().click();
  await threadGuard.getByRole('button',{name:'AI ▾',exact:true}).click();
  await threadGuard.getByRole('menuitem',{name:'Prepare draft',exact:true}).click();
  await threadGuard.waitForFunction(()=>requests.some(x=>x.op==='chat.ask'));
  assert.match(await threadGuard.evaluate(()=>requests.find(x=>x.op==='chat.ask').args.text),/явное согласие/);
  await threadGuard.locator('.message').last().click({button:'right'});
  await threadGuard.getByRole('menuitem',{name:'AI ▸'}).click();
  await threadGuard.keyboard.press('Escape');
  assert.equal(await threadGuard.evaluate(()=>document.activeElement.tagName),'SUMMARY');
  await threadGuard.locator('.thread').first().click({button:'right'});
  await threadGuard.getByRole('menuitem',{name:'AI ▸'}).click();
  await threadGuard.getByRole('menuitem',{name:'Prepare draft',exact:true}).click();
  await threadGuard.waitForFunction(()=>requests.filter(x=>x.op==='chat.ask').length===2);
  assert.match(await threadGuard.evaluate(()=>requests.filter(x=>x.op==='chat.ask')[1].args.text),/явное согласие/);
  await threadGuard.close();
  const sensitive=await pageFor({...ctx,sensitive:true},1000);
  await sensitive.locator('.thread').first().click({button:'right'});
  await sensitive.getByRole('menuitem',{name:'AI ▸'}).click();
  await sensitive.getByRole('menuitem',{name:'Discuss',exact:true}).click();
  await sensitive.getByText('Нет доступного письма для AI').waitFor();
  assert.equal(await sensitive.evaluate(()=>requests.some(x=>x.op==='chat.ask')),false);
  await sensitive.close();
  const app=await pageFor({slot:'ui.page'},1000);
  await app.locator('.thread').first().click();
  await app.getByRole('button',{name:'AI ▾',exact:true}).click();
  await app.getByRole('menuitem',{name:'More context',exact:true}).click();
  await app.waitForFunction(()=>requests.some(x=>x.op==='chat.open'));
  assert.equal(await app.evaluate(()=>requests.find(x=>x.op==='chat.open').args.chatId),'mail-chat');
  await app.close();
  assert.deepEqual(errors,[]);
  console.log('PASS: AI row target, no writes, menu keyboard/dismissal, 300px layout, panel/folder/page routing, read-guard, sensitive, native selected text');
} finally {await browser.close();}
