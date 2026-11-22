/* Controller/DOM-contract checks; real browser rendering is a release acceptance step. */
const assert = require('node:assert/strict'), fs = require('fs'), path = require('path'), vm = require('vm');
let confirmResolve, calls, done, listeners = new Set();
function h(tag, attrs, ...children) {
  const node = { tag, value: '', disabled: false, textContent: '', children: children.flat(Infinity).filter(x => x != null),
    append(...items) { this.children.push(...items.flat(Infinity).filter(x => x != null)); } };
  Object.assign(node, attrs || {}); node.value = String(node.value ?? '');
  if (tag === 'select' && !node.value) node.value = node.children[0]?.value || '';
  return node;
}
const GW = { h, on(event, fn) { listeners.add(fn); return () => listeners.delete(fn); }, clear(node) { node.children = []; return node; },
  writeDone(result, label) { done.push(label); }, writeError: async error => error.status === 409 ? 'conflict' : null,
  confirm: () => new Promise(resolve => { confirmResolve = resolve; }) };
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../static/shared/tier_overrides.js'), 'utf8'), { window: { GW }, console });
function nodes(root) { const out = []; function visit(node) { if (!node || typeof node !== 'object') return; out.push(node); (node.children || []).forEach(visit); } visit(root); return out; }
function button(p, text) { return nodes(p.body).find(node => node.tag === 'button' && node.children.includes(text)); }
function input(p, label) { return nodes(p.body).find(node => node['aria-label'] === label); }
async function setup(kind = 'drop', extra = {}) {
  calls = []; done = [];
  const data = Object.assign({ table: true, writable: true, version: 'a'.repeat(32), maps: [{ id: 21, name: 'Olympus' }],
    tiers: kind === 'drop' ? ['normal', 'elite', 'boss'] : ['normal', 'elite'],
    rows: [kind === 'drop' ? { map_id: 21, map_name: 'Olympus', tier: 'normal', any_drop_basis_points: 1500, note: '' } :
      { map_id: 21, map_name: 'Olympus', tier: 'elite', minimum_seconds: 1800, maximum_seconds: 3600, note: '' }] }, extra);
  const ctx = { api: async (route, body) => { calls.push({ route, body }); return route === 'overview' ? data :
    { ok: true, reload: { ok: false, message: 'Saved data is awaiting game reload.' } }; } };
  const p = GW.tierEditor(kind); await p.mount(h('div'), ctx); calls = [];
  return { p, ctx, data };
}
(async () => {
  let { p } = await setup();
  const chance = input(p, 'Drop chance (%)'), save = button(p, 'Save');
  assert.equal(chance.value, '15');                      // 1500 basis points are shown as 15 (%)
  for (const value of ['', '-1', '100.01', '1.555', 'NaN', 'Infinity']) {
    chance.value = value; chance.oninput(); assert.equal(save.disabled, true, value);
    await save.onclick(); assert.equal(calls.length, 0);
  }
  chance.value = '15'; chance.oninput(); assert.equal(save.disabled, false); await save.onclick();
  assert.equal(calls[0].route, 'save'); assert.equal(calls[0].body.any_drop_basis_points, 1500);
  assert.equal(calls[0].body.map_id, 21); assert.equal(calls[0].body.tier, 'normal'); assert.equal(calls[0].body.version, 'a'.repeat(32));
  assert.equal(calls[1].route, 'overview'); assert.ok(done.includes('Saved; game reload pending'));
  assert.ok(nodes(p.body).some(node => node.role === 'status' && node.class === 'gw-tier-warning'));

  ({ p } = await setup('respawn'));
  const lo = input(p, 'Minimum respawn seconds'), hi = input(p, 'Maximum respawn seconds');
  const respawnSave = button(p, 'Save');
  for (const [minimum, maximum] of [['14', '30'], ['15', '86401'], ['3601', '3600'], ['', '30'], ['15.5', '30']]) {
    lo.value = minimum; hi.value = maximum; lo.oninput(); assert.equal(respawnSave.disabled, true);
    await respawnSave.onclick(); assert.equal(calls.length, 0);
  }
  lo.value = '1800'; hi.value = '3600'; lo.oninput(); await respawnSave.onclick();
  assert.equal(calls[0].body.minimum_seconds, 1800); assert.equal(calls[0].body.maximum_seconds, 3600);

  ({ p } = await setup('drop', { writable: false }));
  assert.ok(nodes(p.body).filter(node => node.tag === 'button' || node.tag === 'input' || node.tag === 'select').every(node => node.disabled));
  await button(p, 'Save').onclick(); assert.equal(calls.length, 0);
  ({ p } = await setup('drop', { table: false, why_missing: 'Table missing until the server release.' }));
  assert.equal(button(p, 'Save'), undefined); assert.equal(button(p, 'Add override'), undefined);
  assert.ok(nodes(p.body).some(node => node.role === 'alert'));

  for (const change of [p => p.unmount(), p => { p.ctx = {}; }, p => { p.data = {}; }]) {
    ({ p } = await setup()); const pending = button(p, 'Remove').onclick(); change(p); confirmResolve(true); await pending;
    assert.equal(calls.length, 0, 'confirmation must not act on stale context or view');
  }
  ({ p } = await setup()); let pending = button(p, 'Remove').onclick(); confirmResolve(true); await pending;
  assert.equal(calls[0].route, 'delete'); assert.equal(calls[0].body.version, 'a'.repeat(32)); assert.equal(calls[0].body.map_id, 21);

  const fixture = await setup(); p = fixture.p;
  const oldSave = button(p, 'Save'), completions = [];
  fixture.ctx.api = () => new Promise(resolve => completions.push(resolve));
  const first = p.load(), second = p.load();
  completions[1](Object.assign({}, fixture.data, { version: 'new' })); await second;
  completions[0](Object.assign({}, fixture.data, { version: 'old' })); await first;
  assert.equal(p.data.version, 'new', 'late overview cannot replace a newer view');
  calls = []; await oldSave.onclick(); assert.equal(calls.length, 0, 'detached old save cannot write after refresh');
  const fresh = await setup();
  let refreshes = 0; fresh.p.load = async () => { refreshes++; };
  const listener = Array.from(listeners).at(-1); listener();
  assert.equal(refreshes, 1, 'global Undo/change refreshes the mounted tier view');
  fresh.p.busy = true; listener(); assert.equal(refreshes, 1, 'own pending writes do not start duplicate refreshes');
  fresh.p.unmount(); assert.equal(listeners.has(listener), false, 'unmount removes the change listener');
  console.log('PASS tier UI: numeric bounds/order, exact payloads, refresh, read-only/missing tables, reload warning, stale confirmations, Undo refresh and out-of-order views. Browser rendering not tested.');
})().catch(error => { console.error(error); process.exitCode = 1; });
