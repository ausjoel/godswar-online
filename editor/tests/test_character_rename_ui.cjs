// test_character_rename_ui.cjs - the Character page's pending-change logic (static/modules/character.js set()).
//   node tests/test_character_rename_ui.cjs
// Regression (2026-11-02, the admin: "renaming doesn't work"): set() compared String(Number(old)) with String(Number(new)),
// so every text value (name, gender) became "NaN" === "NaN" and the change was dropped: the dialog showed the new
// name but the save bar stayed empty and nothing was saved. No browser, no server: the module runs in a vm sandbox.
const fs = require('fs'), vm = require('vm'), path = require('path'), assert = require('node:assert/strict');
let component;
const h = (tag, props, ...kids) => Object.assign({ tag, children: kids.flat(Infinity).filter(x => x != null), append(...x) { this.children.push(...x); },
  classList: { toggle() {}, add() {} }, dataset: {} }, props || {});
const GW = { h, module: (n, p) => { component = p; }, clear: n => { n.children = []; return n; } };
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../static/modules/character.js'), 'utf8'), { window: { GW }, console });
assert.ok(component && typeof component.set === 'function', 'character.js registered its module');

function page(base) {
  const bar = new Map();
  const p = Object.assign({}, component, {
    d: { base, fields: { name: { label: 'Name', editable: true }, gender: { label: 'Gender', editable: true }, Money: { label: 'Silver', editable: true },
                         fighter_level_sealed: { label: 'Sealed', editable: true } }, writable: true, names: {} },
    online: { online: false }, pending: {}, fields: {},
    bar: { set: (k, c) => bar.set(k, c), unset: k => bar.delete(k) },
  });
  return { p, bar };
}

const base = { name: 'TestChar', gender: 'male', Money: 10000, fighter_level_sealed: false };
let { p, bar } = page(base);
p.set('name', 'Vpsnew');
assert.equal(p.pending.name, 'Vpsnew', 'a new name is pending');
assert.equal(bar.size, 1, 'the save bar shows one change');
assert.equal(bar.get('name').to, 'Vpsnew');
p.set('name', 'TestChar');
assert.equal('name' in p.pending, false, 'back to the stored name: nothing pending');
assert.equal(bar.size, 0);
p.set('name', 'TestChar');
assert.equal(p.pending.name, 'TestChar', 'a case-only rename is a change');
p.set('name', 'TestChar');

p.set('gender', 'female');
assert.equal(p.pending.gender, 'female', 'gender (also text) is pending');
p.set('gender', 'male');
assert.equal('gender' in p.pending, false);

// numbers keep the old numeric de-duplication ("10000" from an input equals the stored 10000)
p.set('Money', '10000');
assert.equal('Money' in p.pending, false, '"10000" equals 10000');
p.set('Money', 20000);
assert.equal(p.pending.Money, 20000);
p.set('Money', 10000);
assert.equal('Money' in p.pending, false);
// booleans
p.set('fighter_level_sealed', true);
assert.equal(p.pending.fighter_level_sealed, true);
p.set('fighter_level_sealed', false);
assert.equal('fighter_level_sealed' in p.pending, false);
// a name made of digits is still compared as text ("007" is not "7")
({ p, bar } = page({ name: '7' }));
p.set('name', '007');
assert.equal(p.pending.name, '007', '"007" differs from "7" for a name');

console.log('PASS character set(): name and gender changes reach the save bar (case-only too), back-to-stored clears them, numbers and booleans de-duplicate as before.');
