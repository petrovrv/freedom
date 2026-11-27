// SPDX-License-Identifier: AGPL-3.0-or-later
// Isolated real-browser launcher regression; no backend, auth, or production requests.
// Run: node tests/launcher.test.mjs
import { createServer } from 'vite';
import { chromium } from 'playwright';
import assert from 'node:assert/strict';

const fixture = { name: 'launcher-test', configureServer(server) {
server.middlewares.use('/launcher-test', async (_req, res) => {
  res.setHeader('Content-Type', 'text/html');
  res.end(await server.transformIndexHtml('/launcher-test', `
    <div id="root"></div><script type="module">
    import React from 'react';
    import { createRoot } from 'react-dom/client';
    import { MemoryRouter } from 'react-router-dom';
    import Launcher from '/src/Launcher.tsx';
    const module = (id, moduleChat) => ({ id, moduleChat, enabled: true, version: '1', title: id,
      description: '', permissions: [], uiOps: [], personal: null,
      ui: [{ id: 'main', slot: 'ui.page', entry: 'ui/main.html', title: id, app: true }] });
    window.opened = [];
    const modules = [module('planner', { mode: 'planning', panel: 'view' }), module('ordinary')];
    createRoot(document.getElementById('root')).render(React.createElement(MemoryRouter, {},
      React.createElement(Launcher, { modules, onOpen: a => window.opened.push(a.m.id) })));
    </script>`));
});
} };
const server = await createServer({ plugins: [fixture], server: { host: '127.0.0.1', port: 0 } });
let browser;
try {
  await server.listen();
  const { port } = server.httpServer.address();
  browser = await chromium.launch({ channel: 'msedge', headless: true });
  for (const width of [1280, 390]) {
    const page = await browser.newPage({ viewport: { width, height: 800 } });
    const errors = [];
    page.on('pageerror', e => errors.push(e.message));
    await page.goto(`http://127.0.0.1:${port}/launcher-test`);
    for (const id of ['planner', 'ordinary']) {
      await page.getByRole('button', { name: 'Apps', exact: true }).click();
      await page.getByRole('button', { name: id, exact: true }).click();
      assert.equal(await page.getByRole('dialog').count(), 0, 'tile closes the launcher');
    }
    assert.deepEqual(await page.evaluate(() => window.opened), ['planner', 'ordinary'],
      'both module-chat and ordinary tiles reach the shell dispatcher');
    assert.deepEqual(errors, []);
    await page.close();
    console.log('launcher dispatcher OK at width ' + width);
  }
} finally {
  await browser?.close();
  await server.close();
}
