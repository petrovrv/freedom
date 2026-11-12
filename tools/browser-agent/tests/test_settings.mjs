// Unit test of the agent browser settings and tab lifetime rules (0.15.0): node tests/test_settings.mjs
import assert from 'node:assert/strict';
import { browserSettings, tabExpiry, lruPick, BROWSER_DEFAULTS } from '../chamberlain.mjs';

let n = 0;
const t = (name, fn) => { fn(); n++; console.log('ok', name); };

t('defaults when missing or broken', () => {
  assert.deepEqual(browserSettings(undefined), { tabs: 0, idleMin: 120, formIdleMin: 360, captchaKeep: true });
  assert.deepEqual(browserSettings('x'), BROWSER_DEFAULTS);
  assert.deepEqual(browserSettings({ tabs: 'abc', idleMin: '', formIdleMin: null, captchaKeep: 'maybe' }), BROWSER_DEFAULTS);
  assert.equal(browserSettings({ tabs: true }).tabs, 0);
});
t('env strings and clamping', () => {
  assert.deepEqual(browserSettings({ tabs: '1', idleMin: '30', formIdleMin: '90', captchaKeep: '0' }),
    { tabs: 1, idleMin: 30, formIdleMin: 90, captchaKeep: false });
  assert.deepEqual(browserSettings({ tabs: 99, idleMin: 1, formIdleMin: 99999, captchaKeep: '1' }),
    { tabs: 20, idleMin: 5, formIdleMin: 2880, captchaKeep: true });
  assert.equal(browserSettings({ tabs: -3 }).tabs, 0);
  assert.equal(browserSettings({ idleMin: 2000 }).idleMin, 1440);
});
t('formIdleMin never below idleMin', () => {
  assert.equal(browserSettings({ idleMin: 600, formIdleMin: 100 }).formIdleMin, 600);
  assert.equal(browserSettings({ idleMin: 600 }).formIdleMin, 600);   // default 360 < idleMin
  assert.equal(browserSettings({ idleMin: 60 }).formIdleMin, 360);
});
t('captchaKeep words', () => {
  for (const v of [true, 'true', 'yes', 'on', 1, '1']) assert.equal(browserSettings({ captchaKeep: v }).captchaKeep, true);
  for (const v of [false, 'false', 'no', 'off', 0, '0']) assert.equal(browserSettings({ captchaKeep: v }).captchaKeep, false);
});
t('tab lifetime', () => {
  const s = browserSettings({ idleMin: 10, formIdleMin: 30 });
  assert.equal(tabExpiry({ lastUsed: 1000 }, s), 1000 + 10 * 60000);
  assert.equal(tabExpiry({ lastUsed: 1000, typed: true }, s), 1000 + 30 * 60000);
  assert.equal(tabExpiry({ lastUsed: 1000, typed: true, captcha: { since: 1 } }, s), null);
  assert.equal(tabExpiry({ lastUsed: 1000, captcha: { since: 1 } }, { ...s, captchaKeep: false }), 1000 + 10 * 60000);
  assert.equal(tabExpiry({ lastUsed: 0 }, undefined), 120 * 60000);
  assert.equal(tabExpiry({ lastUsed: 0, typed: true }, s, 1000), 30 * 1000);
});
t('LRU pick prefers clean tabs, then the oldest', () => {
  const a = { id: 'a', lastUsed: 1, typed: true }, b = { id: 'b', lastUsed: 5 }, c = { id: 'c', lastUsed: 3 };
  const d = { id: 'd', lastUsed: 0, captcha: {} };
  assert.equal(lruPick([a, b, c, d]).id, 'c');
  assert.equal(lruPick([a, d]).id, 'a');
  assert.equal(lruPick([d]).id, 'd');
  assert.equal(lruPick([]), null);
});
console.log(`${n}/${n} passed`);
