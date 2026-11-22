/* Wave 4 D item 1: GW.monsterLabel shows the in-game name + icon with the ID secondary (DOM contract only). */
const assert = require('node:assert/strict'), fs = require('fs'), path = require('path'), vm = require('vm');
function h(tag, attrs, ...children) {
  const node = { tag, textContent: '', hidden: false, children: children.flat(Infinity).filter(x => x != null), classList: { add() {} },
    append(...items) { this.children.push(...items.flat(Infinity).filter(x => x != null)); } };
  Object.assign(node, attrs || {});
  if (node.children.length === 1 && typeof node.children[0] === 'string') node.textContent = node.children[0];
  return node;
}
const index = { monsters: [
  { key: 'A_elite_x_001', name: '[E]Grumpy', names_by_map: { 5: 'Grumpy of Nemea' }, thumb: '/static/monster-thumbs/m.png', rank: 'elite', level: 60, level_min: 60, level_max: 62 },
  { key: 'A_plain_002', name: 'Plain', names_by_map: {}, thumb: null, rank: 'normal', level: 5, level_min: 5, level_max: 5 }] };
let calls = 0;
const GW = { h, cached: async p => { calls++; assert.equal(p, '/api/m/monsters/index'); return index; } };
const document = { getElementById: () => null, createElement: () => ({}), head: { append() {} } };
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../static/shared/monster_label.js'), 'utf8'), { window: { GW }, document, console });
(async () => {
  const a = GW.monsterLabel('A_elite_x_001', { map: 5 });
  const b = GW.monsterLabel('A_plain_002', { name: 'Plain' });
  const c = GW.monsterLabel('Unknown_key');
  const [imgA, nameA, , keyA] = a.children;
  assert.equal(nameA.textContent, 'A_elite_x_001');            // the ID shows at once
  await new Promise(r => setTimeout(r, 0)); await new Promise(r => setTimeout(r, 0));
  assert.equal(nameA.textContent, 'Grumpy of Nemea');          // per-map in-game name
  assert.equal(keyA.textContent, 'A_elite_x_001');             // ID secondary
  assert.equal(imgA.src, '/static/monster-thumbs/m.png'); assert.equal(imgA.hidden, false);
  assert.equal(b.children[0].hidden, true);                    // no picture: name only
  assert.equal(c.children[1].textContent, 'Unknown_key');      // unknown stays the ID
  assert.equal(a['data-tip-type'], 'monster');
  assert.equal(calls, 1);                                      // one index request for every label
  console.log('PASS monster label: per-map in-game name, icon, ID secondary, unknown ID kept, one cached index.');
})().catch(e => { console.error(e); process.exit(1); });
