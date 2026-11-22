// B21 items 27/28: the shop grid and NPC map helpers (static/modules/npcs-extra.js) on a tiny fake DOM.
// Run: node tests/test_npcs_shop_grid_ui.cjs   (drawing and real drag events in a browser: checked by hand, see E3 README)
const fs = require('fs'), vm = require('vm'), path = require('path'), assert = require('node:assert/strict');
class El {
  constructor(tag, attrs) { this.tag = tag; this.attrs = attrs || {}; this.children = []; this.on = {}; this.cls = new Set(String(this.attrs.class || '').split(/\s+/).filter(Boolean));
    this.classList = { add: (...c) => c.forEach(x => this.cls.add(x)), remove: (...c) => c.forEach(x => this.cls.delete(x)), toggle: (c, v) => (v === undefined ? !this.cls.has(c) : v) ? this.cls.add(c) : this.cls.delete(c), contains: c => this.cls.has(c) };
    for (const [k, v] of Object.entries(this.attrs)) if (k.startsWith('on') && typeof v === 'function') this.on[k.slice(2)] = v; this.style = {}; }
  append(...c) { for (const x of c.flat()) if (x !== null && x !== undefined && x !== false) this.children.push(x); }
  addEventListener(t, f) { this.on[t] = f; }
  set onchange(f) { this.on.change = f; } get onchange() { return this.on.change; }
  getBoundingClientRect() { return { left: 0, top: 0, width: 42, height: 42 }; }
  getContext() { return new Proxy({}, { get: () => () => {} , set: () => true }); }
  all(pred, out = []) { if (pred(this)) out.push(this); for (const c of this.children) if (c instanceof El) c.all(pred, out); return out; }
}
const h = (tag, attrs, ...kids) => { const e = new El(tag, attrs); e.append(...kids); return e; };
const GW = { h, icon: () => h('span', { class: 'gw-icon' }) };
const ctx = { window: { GW }, console, document: { createElement: () => new El('script'), head: new El('head') }, Promise };
ctx.window.document = ctx.document;
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../static/modules/npcs-extra.js'), 'utf8'), ctx);
const X = ctx.window.GWNpcExtra;
assert.equal(X.PER_TAB, 50);

const mk = () => [
  { item: 1, price: 30, category: 0 }, { item: 2, price: 10, category: 0 }, { item: 3, price: 20, category: 0 },
  { item: 4, price: 5, category: 1 }];
let rows = mk(), dirty = 0;
const st = { tab: null, sel: null, drag: null };
const opts = () => ({ rows, tabs: [0, 1], state: st, irow: id => ({ name: 'Item ' + id }), charge: () => 'silver', label: c => c,
  redraw() {}, dirty() { dirty++; }, add() {} });
let el = X.shopGrid(opts());
const cells = el.all(e => e.cls.has('gw-npc-cell'));
assert.equal(cells.length, 50, 'one page of 50 cells');
assert.equal(cells.filter(c => !c.cls.has('empty')).length, 3, 'tab 1 shows its 3 items');
// drag the first item onto the right half of the third: order 2, 3, 1
const ev = (x) => ({ preventDefault() {}, clientX: x, dataTransfer: { setData() {} } });
cells[0].on.dragstart(ev(0)); cells[2].on.dragover(ev(40)); cells[2].on.drop(ev(40));
assert.deepEqual(rows.map(r => r.item), [2, 3, 1, 4]); assert.equal(dirty, 1);
// a drop onto another tab's item is refused (order only means something within a tab)
st.drag = 3; el = X.shopGrid(opts()); el.all(e => e.cls.has('gw-npc-cell'))[0].on.drop(ev(0));
assert.deepEqual(rows.map(r => r.item), [2, 3, 1, 4]);
// sort tab 1 by price
el = X.shopGrid(opts()); const sort = el.all(e => e.tag === 'select')[0]; sort.value = 'price'; sort.on.change();
assert.deepEqual(rows.map(r => r.item), [2, 3, 1, 4]);
sort.value = 'price_desc'; sort.on.change(); assert.deepEqual(rows.map(r => r.item), [1, 3, 2, 4]);
// drop on the Tab 2 button: item moves to the end of tab 2
el = X.shopGrid(opts()); st.drag = 0; el.all(e => e.attrs.role === 'tab')[1].on.drop(ev(0));
assert.deepEqual(rows.map(r => [r.item, r.category]), [[3, 0], [2, 0], [4, 1], [1, 1]]); assert.equal(st.tab, 1);
// remove the selected item (Delete key)
st.sel = 3; el = X.shopGrid(opts()); el.all(e => e.attrs.role === 'grid')[0].on.keydown({ key: 'Delete', preventDefault() {} });
assert.deepEqual(rows.map(r => r.item), [3, 2, 4]);
// more than 50 in a tab: a second page, cells past 50 marked
rows = Array.from({ length: 53 }, (_, i) => ({ item: 100 + i, price: 1, category: 0 })); st.tab = 0; st.sel = null;
el = X.shopGrid(opts());
assert.equal(el.all(e => e.cls.has('gw-npc-cell')).length, 100); assert.equal(el.all(e => e.cls.has('over')).length, 3);
// map view: only maps with NPCs, clicking near a dot picks it
let picked = null;
const mv = X.mapView({ npcs: [{ key: 'A', name: 'A', map: 1, x: 0, z: 0 }, { key: 'B', name: 'B', map: 2, x: 10, z: 10 }, { key: 'M', name: 'Mall', map: -1, x: 0, z: 0 }],
  maps: [{ map_id: 1, name: 'Athens' }, { map_id: 2, name: 'Suburb' }], state: { map: null }, redraw() {}, pick: n => { picked = n; } });
const cv = mv.all(e => e.tag === 'canvas')[0];
assert.equal(mv.all(e => e.tag === 'option').length, 2, 'the mall (no map) is left out');
cv.getBoundingClientRect = () => ({ left: 0, top: 0, width: 512, height: 512 });
cv.on.click({ clientX: 258, clientY: 255 }); assert.equal(picked && picked.key, 'A');
picked = null; cv.on.click({ clientX: 100, clientY: 100 }); assert.equal(picked, null);
console.log('PASS shop grid: 50 cells per tab, drag within a tab, refused across tabs, sort, move to tab, Delete, overflow page; map view picks the nearest dot. Real browser drag and drawing checked by hand.');
