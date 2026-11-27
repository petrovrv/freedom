// SPDX-License-Identifier: AGPL-3.0-or-later
// Local browser + real worker crypto regression. All handlers are fakes; no credentials or public network.
import { pathToFileURL, fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import assert from 'node:assert/strict';
const root = fileURLToPath(new URL('../', import.meta.url)).replace(/\\/g, '/').replace(/\/$/, '');
const { createServer } = await import(pathToFileURL(root + '/node_modules/vite/dist/node/index.js'));
const { chromium } = await import(pathToFileURL(root + '/node_modules/playwright/index.mjs'));
const peer = spawn('python', ['-u', fileURLToPath(new URL('./approval-ui-worker.py', import.meta.url)), root + '/../modules'], { windowsHide: true, stdio: ['pipe', 'pipe', 'inherit'] });
const waiting = [];
createInterface({ input: peer.stdout }).on('line', line => waiting.shift()?.(JSON.parse(line)));
const ask = value => new Promise(resolve => { waiting.push(resolve); peer.stdin.write(JSON.stringify(value) + '\n'); });
const harness = `import React from 'react'; import {createRoot} from 'react-dom/client'; import {AppCard} from '/src/Inbox.tsx';
const root=createRoot(document.getElementById('root'));
window.renderCard = status => root.render(React.createElement(AppCard,{key:status, app:{privateRelay:true,status,requestId:'bbbbbbbbbbbbbbbb', app:'sample-app',action:'send',title:'Test action',icon:'',description:'Local test only',args:[],etag:'test-etag',expiresUtc:'2026-11-27T18:00:00Z'},onChanged:async()=>{}})); window.renderCard('pending');`;
const server = await createServer({ root, configFile: root + '/vite.config.ts', plugins: [{name:'approval-harness', resolveId(id){if(id==='virtual:approval-harness')return '\0approval-harness';},load(id){if(id==='\0approval-harness')return harness;}}], server:{host:'127.0.0.1',port:0}, logLevel:'error' });
await server.listen();
const origin = `http://127.0.0.1:${server.httpServer.address().port}`;
const browser = await chromium.launch({headless:true, ...(process.platform === 'win32' ? {channel:'msedge'} : {})});
const page = await browser.newPage();
const errors=[], wire=[]; let loseRun=false;
page.on('pageerror', e=>errors.push(e.message));
await page.route('**/*', async route=>{
  const req=route.request(), url=new URL(req.url());
  if(url.origin!==origin) return route.abort();
  if(url.pathname==='/')return route.fulfill({contentType:'text/html',body:await server.transformIndexHtml('/', '<div id="root"></div><script type="module" src="/@id/virtual:approval-harness"></script>')});
  if(url.pathname==='/agent/db.php'){
    const data=req.postDataJSON(); wire.push(JSON.stringify(data));
    const result=await ask(data); wire.push(JSON.stringify(result));
    if(data.phase==='run' && loseRun) return route.abort();
    return route.fulfill({json:result});
  }
  return route.continue();
});
try {
  await page.goto(origin);
  await page.getByRole('button',{name:'Run',exact:true}).waitFor();
  assert(await page.getByRole('button',{name:'Run',exact:true}).isDisabled());
  await page.getByRole('button',{name:'Review privately'}).click();
  await page.getByText('PRIVATE <img src=x onerror=alert(1)>',{exact:true}).waitFor();
  assert.equal(await page.locator('img').count(),0);
  assert.equal((await ask({phase:'count'})).count,0);
  loseRun=true;
  await page.getByRole('button',{name:'Run',exact:true}).click();
  await page.getByText('Outcome unknown. Check the result; do not repeat the action.').waitFor();
  assert.equal(await page.getByRole('button',{name:'Run',exact:true}).count(),0);
  await page.getByRole('button',{name:'Check result'}).click();
  await page.getByText('Done.',{exact:true}).waitFor();
  assert.equal((await ask({phase:'count'})).count,1);
  await page.evaluate(()=>window.renderCard('running'));
  await page.getByRole('button',{name:'Check result'}).click();
  await page.getByText('Done.',{exact:true}).waitFor();
  assert.equal((await ask({phase:'count'})).count,1);
  assert(!wire.join('\n').includes('PRIVATE'));
  assert.deepEqual(errors,[]);
  if (process.env.AIWB_TEST_SHOT) await page.screenshot({path:process.env.AIWB_TEST_SHOT});
  console.log(JSON.stringify({ok:true,checks:['Run disabled before preview','private text escaped','no execution during preview','lost Run response recoverable','fresh-key result after remount','exactly one fake call','no plaintext in site wire'],pageErrors:errors}));
} catch(e) { console.log(JSON.stringify({errors,body:await page.locator('body').innerText()}));throw e;
} finally {await browser.close();await server.close();peer.stdin.end();}
