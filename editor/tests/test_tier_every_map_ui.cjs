/* Wave 4 D item 6: the tier pages list every map by section with its effective value (DOM contract only). */
const assert = require('node:assert/strict'), fs = require('fs'), path = require('path'), vm = require('vm');
function h(tag, attrs, ...children) {
  const node = { tag, value: '', disabled: false, textContent: '', children: children.flat(Infinity).filter(x => x != null),
    append(...items) { this.children.push(...items.flat(Infinity).filter(x => x != null)); } };
  Object.assign(node, attrs || {}); node.value = String(node.value ?? '');
  if (tag === 'select' && !node.value) node.value = node.children[0]?.value || '';
  return node;
}
const GW = { h, on: () => () => {}, clear(node) { node.children = []; return node; }, writeDone() {}, writeError: async () => null, confirm: async () => true };
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../static/shared/tier_overrides.js'), 'utf8'), { window: { GW }, console });
const nodes = root => { const out = []; (function v(n) { if (!n || typeof n !== 'object') return; out.push(n); (n.children || []).forEach(v); })(root); return out; };
const text = n => (n.children || []).map(c => typeof c === 'object' ? text(c) : String(c)).join('');
(async () => {
  const maps = [
    { id: 21, name: 'Olympus', section: 'regular', scene: 'Olympus_All', monsters: { normal: 40, elite: 3, boss: 1 },
      effective: { normal: { source: 'map', row: { any_drop_basis_points: 1500 } }, elite: { source: 'default', row: null }, boss: { source: 'all_maps', row: { any_drop_basis_points: 5000 } } } },
    { id: 200, name: "Medusa's Island", section: 'instance', monsters: { normal: 0, elite: 0, boss: 0 },
      effective: { normal: { source: 'default' }, elite: { source: 'default' }, boss: { source: 'default' } } }];
  const data = { table: true, writable: true, version: 'a'.repeat(32), tiers: ['normal', 'elite', 'boss'], rows: [], maps, default_text: 'no gate',
    groups: [{ section: 'regular', title: 'Regular maps', maps: [maps[0]] }, { section: 'instance', title: 'Instances', maps: [maps[1]] }] };
  const p = GW.tierEditor('drop'); await p.mount(h('div'), { api: async () => data });
  const all = nodes(p.body);
  const heads = all.filter(n => n.tag === 'h4').map(text);
  assert.deepEqual(heads, ['Regular maps (1)', 'Instances (1)']);
  const body = text(p.body);
  assert.ok(body.includes('15%') && body.includes('this map') && body.includes('50%') && body.includes('all maps row'));
  assert.ok(body.includes('40 / 3 / 1'));
  const groups = all.filter(n => n.tag === 'optgroup').map(n => n.label);
  assert.deepEqual(groups, ['Regular maps', 'Instances']);
  const set = all.filter(n => n.tag === 'button' && n.children.includes('Set'));
  assert.equal(set.length, 2); set[1].onclick();
  assert.equal(p.addMap.value, '200');
  const filter = all.find(n => n['aria-label'] === 'Filter maps'); filter.value = 'medusa'; filter.oninput();
  assert.deepEqual(nodes(p.body).filter(n => n.tag === 'h4').map(text), ['Instances (1)']);
  console.log('PASS tier every-map: sections, effective sources, counts, Set pre-fills the map, filter. Browser rendering not tested.');
})().catch(e => { console.error(e); process.exit(1); });
