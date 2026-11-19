const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const path = require('node:path');
const src = fs.readFileSync(path.join(__dirname, '../../src/CO.Core/Browser/AgentPageScripts.cs'), 'utf8');
const match = src.match(/(?:public|internal) const string SyntheticKey\s*=\s*"""([\s\S]*?)""";/);
assert.ok(match, 'SyntheticKey source found');
function run(host, attrs, key = 'Enter') {
  const events = []; let clicks = 0;
  const el = {tagName: 'INPUT', type: 'text', id: '', ...attrs,
    closest() { return this.dropdownContainer || null; },
    getAttribute(n) { return this[n] || null; },
    dispatchEvent(e) { events.push(e.type); return true; }, click() { clicks++; }};
  const script = match[1].replace(/DROPDOWN_ONLY/g, 'true').replace(/KEY/g, JSON.stringify(key)).replace(/CODE/g, JSON.stringify(key)).replace(/VK/g, '13');
  const result = vm.runInNewContext(script, {location: {hostname: host}, document: {activeElement: el}, KeyboardEvent: class {constructor(type, init) {this.type=type; Object.assign(this,init);}}});
  return {ok: result.ok, events, clicks};
}
for (const attrs of [{id:'skills--skills'}, {'data-automation-id':'searchBox'}]) {
  assert.deepEqual(run('dow.wd1.myworkdayjobs.com', attrs), {ok:true, events:['keydown','keyup'], clicks:0});
  for (const host of ['example.org', 'evilmyworkdayjobs.com', 'myworkdayjobs.com.evil.org']) assert.equal(run(host, attrs).ok, false);
  assert.equal(run('dow.wd1.myworkdayjobs.com', attrs, ' ').ok, false);
}
assert.deepEqual(run('dow.wd1.myworkdayjobs.com', {tagName:'BUTTON',type:'submit'}), {ok:false,events:[],clicks:0});
assert.equal(run('dow.wd1.myworkdayjobs.com', {id:'other'}).ok, false);
for (const attrs of [{role:'combobox', 'aria-expanded':'true'}, {'aria-autocomplete':'list','aria-expanded':'true'}, {dropdownContainer:{}}]) {
  assert.deepEqual(run('example.org', attrs), {ok:true, events:['keydown','keyup'], clicks:0});
}
assert.equal(run('example.org', {role:'combobox','aria-expanded':'false'}).ok, false);
console.log('Dropdown Enter regression checks passed');
