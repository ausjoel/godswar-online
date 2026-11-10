/* Exercise the real picker controller with a small DOM surface. Browser proof is separate. */
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
class El {
  constructor(tag, attrs = {}) {
    this.tag = tag; this.children = []; this.style = {}; this.dataset = {}; this.events = {};
    this.textContent = ''; this.value = ''; this.clientWidth = 800; this.clientHeight = 400; this.scrollTop = 0;
    this.classList = { add() {}, remove() {}, toggle() {} };
    for (const [k, v] of Object.entries(attrs)) {
      if (k.startsWith('on')) this.addEventListener(k.slice(2), v);
      else this[k] = v;
    }
  }
  append(...kids) { for (const k of kids.flat(Infinity)) if (k != null && k !== false) this.children.push(k); }
  addEventListener(k, f) { (this.events[k] ||= []).push(f); }
  setAttribute(k, v) { this[k] = v; }
  get firstChild() { return this.children[0]; }
  focus() {}
  fire(k) { for (const f of this.events[k] || []) f({target: this, preventDefault() {}}); }
}
const h = (tag, attrs, ...kids) => { const e = new El(tag, attrs || {}); e.append(...kids); return e; };
const all = e => e instanceof El ? [e, ...e.children.flatMap(all)] : [];
const rows = [
  {id: 1, name: 'Sword', group: 'Equipment', cat: 'Weapon'},
  {id: 2, name: 'Old Event', group: 'Items', cat: 'Event', dead_status: 'defunct'},
  {id: 3, name: 'Profession Token', group: 'Items', cat: 'Event', dead_status: 'no-system'},
  {id: 4, name: 'Edifier Prize', group: 'Items', cat: 'Event', dead_status: 'remove', generated_translation: true},
];
const ix = {type: 'item', title: 'Items', rows, groups: ['Equipment', 'Items'], byId: new Map(rows.map(r => [String(r.id), r]))};
const GW = {h, fmt: String, clear: e => {e.children = []; return e;}, icon: () => h('span'), status() {},
  catalog: {load: async type => type === 'item' ? ix : {...ix, type, rows: [{id: 9, name: 'Skill'}], groups: [], byId: new Map()}}};
const context = {window: {GW, addEventListener() {}}, requestAnimationFrame: () => 1, setTimeout, clearTimeout};
vm.runInNewContext(fs.readFileSync(require('node:path').join(__dirname, '../static/shared/picker.js'), 'utf8'), context);
const settle = async () => {await Promise.resolve(); await Promise.resolve();};
(async () => {
  for (const itemUse of [undefined, 'loot', 'shop']) {
    const grid = GW.grid({type: 'item', itemUse, multi: true}); await settle();
    const filter = all(grid.el).find(e => e['aria-label'] === 'Item status');
    const count = all(grid.el).find(e => e.class === 'gw-pk-count');
    assert.equal(count.textContent, `${itemUse ? 3 : 4} of 4 items`);
    filter.value = 'dead'; filter.fire('change');
    assert.equal(count.textContent, `${itemUse ? 2 : 3} of 4 items`);
    filter.value = 'remove'; filter.fire('change');
    assert.equal(count.textContent, `${itemUse ? 0 : 1} of 4 items`);
    grid.tickShown();
    assert.deepEqual(Array.from(grid.ticked(), r => r.id), itemUse ? [] : [4]);
    grid.clearTicks();
    filter.value = 'no-system'; filter.fire('change');
    assert.equal(count.textContent, '1 of 4 items');
    filter.value = ''; filter.fire('change');
    const search = all(grid.el).find(e => e['aria-label'] === 'Search');
    search.value = 'Edifier'; search.fire('keydown');
    // Enter filtering has its own key handling; invoke with the actual keyboard event.
    for (const f of search.events.keydown) f({key: 'Enter', preventDefault() {}});
    assert.equal(count.textContent, `${itemUse ? 0 : 1} of 4 items`);
  }
  // Existing filters still compose with the lifecycle policy.
  const filtered = GW.grid({type: 'item', itemUse: 'loot', filter: r => r.dead_status === 'defunct'}); await settle();
  assert.equal(all(filtered.el).find(e => e.class === 'gw-pk-count').textContent, '1 of 4 items');
  // Wrapper options must reach the actual grid in both single and multi selectors.
  const realGrid = GW.grid; let passed;
  GW.grid = o => {passed = o; return {el: h('div'), focus() {}, ticked: () => [], selected: () => null};};
  GW.modal = () => ({close() {}});
  GW.pick('item', {itemUse: 'loot'}); assert.equal(passed.itemUse, 'loot');
  GW.pickMany('item', {itemUse: 'shop'}); assert.equal(passed.itemUse, 'shop');
  GW.grid = realGrid;
  console.log('PASS lifecycle picker: default all, three statuses, loot/shop exclusion, search/filter composition, ticks and wrapper forwarding.');
})().catch(e => {console.error(e); process.exitCode = 1;});
