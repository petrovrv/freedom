// SPDX-License-Identifier: AGPL-3.0-or-later
import { chromium } from 'playwright';
const b = await chromium.launch({ executablePath: '/opt/pw-browsers/chromium' });
const p = await b.newPage();
const errs = [];
p.on('console', (m) => { if (m.type() === 'error' || /Content Security Policy/.test(m.text())) errs.push(m.text()); });
p.on('pageerror', (e) => errs.push(String(e)));
// CO_URL: the CO site to check, e.g. https://co.example.org
const site = process.env.CO_URL;
if (!site) throw new Error('set CO_URL');
await p.goto(site + '/', { waitUntil: 'networkidle' });
console.log('title:', await p.title());
console.log('button:', await p.getByRole('button', { name: 'Sign in with Google' }).isVisible());
console.log('gis loaded:', await p.evaluate(() => !!(window.google && google.accounts && google.accounts.oauth2)));
console.log('sw:', await p.evaluate(async () => { await new Promise(r=>setTimeout(r,1500)); return (await navigator.serviceWorker.getRegistrations()).length; }));
console.log('errors:', errs);
await b.close();
