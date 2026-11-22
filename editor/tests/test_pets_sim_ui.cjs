// test_pets_sim_ui.cjs - the Pets page's skill picker (one row per family, click for the level), the Merge & rebirth
// simulator tab (sliders, presets, the request bodies, Apply) and legal pet mode, against a fake editor API.
//   node tests/test_pets_sim_ui.cjs
// No browser, no server: static/modules/pets.js runs in a vm sandbox with a small fake DOM. Real rendering is a
// release acceptance step (open the Pets page on a test copy).
const fs = require('fs'), vm = require('vm'), path = require('path'), assert = require('node:assert/strict');

// ---------------------------------------------------------------- a tiny DOM
class El {
  constructor(tag) { this.tag = tag; this.attrs = {}; this.children = []; this.handlers = {}; this.style = {}; this.parent = null; this._text = '';
    const self = this; this.classList = { add: c => self._cls(c, true), remove: c => self._cls(c, false), toggle: (c, on) => self._cls(c, on === undefined ? !self.classList.contains(c) : on), contains: c => (self.className || '').split(/\s+/).includes(c) }; }
  _cls(c, on) { const s = new Set((this.className || '').split(/\s+/).filter(Boolean)); if (on) s.add(c); else s.delete(c); this.className = [...s].join(' '); return on; }
  setAttribute(k, v) { this.attrs[k] = String(v); if (k === 'value') this.value = String(v); if (k === 'disabled') this.disabled = true; }
  getAttribute(k) { return this.attrs[k]; }
  addEventListener(ev, fn) { (this.handlers[ev] = this.handlers[ev] || []).push(fn); }
  async fire(ev, extra) { for (const fn of this.handlers[ev] || []) await fn(Object.assign({ target: this, preventDefault() {} }, extra || {})); }
  append(...kids) { for (const k of kids.flat(Infinity)) { if (k == null || k === false) continue; const n = typeof k === 'object' ? k : new Text(String(k)); n.parent = this; this.children.push(n); } }
  removeChild(k) { this.children = this.children.filter(x => x !== k); }
  get firstChild() { return this.children[0] || null; }
  get textContent() { return this._text + this.children.map(c => c.textContent).join(''); }
  set textContent(v) { this.children = []; this._text = String(v); }
  querySelector(sel) { return all(this).find(n => n !== this && matches(n, sel)) || null; }
  querySelectorAll(sel) { return all(this).filter(n => n !== this && matches(n, sel)); }
  focus() {} setPointerCapture() {} getBoundingClientRect() { return { left: 0, width: 100 }; }
}
class Text { constructor(t) { this.t = t; this.children = []; } get textContent() { return this.t; } }
const all = n => [n, ...(n.children || []).flatMap(all)];
const matches = (n, sel) => sel.startsWith('.') ? !!(n.classList && n.classList.contains(sel.slice(1))) : n.tag === sel;
const text = n => n.textContent;
const find = (root, pred) => all(root).filter(n => n instanceof El && pred(n));
const byText = (root, tag, t) => find(root, n => n.tag === tag && text(n).includes(t))[0];

function h(tag, attrs, ...kids) {
  const el = new El(tag);
  if (attrs) for (const k in attrs) {
    const v = attrs[k];
    if (v === null || v === undefined || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2), v);
    else el.setAttribute(k, v === true ? '' : v);
  }
  el.append(...kids);
  return el;
}

// ---------------------------------------------------------------- the fake GW
let component, modals = [], calls = [], confirms = [];
const stub = () => h('span');
const GW = {
  h, module: (n, p) => { component = p; }, clear: n => { n.children = []; n._text = ''; return n; },
  fmt: n => String(n), on: () => () => {}, onlineBanner: () => h('div'), appliesChip: stub, icon: stub, status() {},
  saveBar: o => { const m = new Map(); return { el: h('div'), set: (k, v) => m.set(k, v), unset: k => m.delete(k), clear: () => m.clear(), get size() { return m.size; }, map: m }; },
  numberField: o => { const e = h('input', { 'aria-label': o.label }); e.value = o.value; return e; },
  cell: o => h('button', { class: 'gw-cellx ' + (o.class || ''), 'aria-label': o.label || o.name || '', onclick: o.onclick || null }),
  render: { tooltip: () => Promise.resolve(h('div', { class: 'gw-tooltip' })) },
  modal: (content, opts) => { const m = { content, opts, closed: false, close() { this.closed = true; } }; modals.push(m); return m; },
  confirm: o => { confirms.push(o); return Promise.resolve(true); }, alert: () => Promise.resolve(),
  writeDone() {}, writeError: async () => null, grid: () => ({ el: h('div'), focus() {}, setType() {} }), qColor: () => '#fff', QUALITY_NAMES: [],
};
const store = {};
const sandbox = { window: { GW }, console, setTimeout, clearTimeout, localStorage: { getItem: k => store[k] ?? null, setItem: (k, v) => { store[k] = v; } } };
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../static/modules/pets.js'), 'utf8'), sandbox);
assert.ok(component, 'pets.js registered its module');

// ---------------------------------------------------------------- fixture data
const stats = [1, 2, 3, 4, 5, 6].map(c => ({ stat_code: c, stat_name: ['Agility', 'Strength', 'Accuracy', 'Technique', 'Wisdom', 'Luck'][c - 1], initial_savvy: '20.00',
  added_savvy: '300', base_growth_rate: '2.5', growth_acceleration: '0', total: c === 5 ? '5' : '320' }));
const PET = { id: 7, name: 'Bao', species_id: 4, species_name: 'Panda', level: 120, rank: '2.70', aptitude: 1, aptitude_name: 'Weak', sex: 1, activity_state: 'owned',
  completed_rebirths: 0, rebirths_remaining: 2, completed_pet_merges: 0, soul_contract_stage: 1, maximum_energy: 100, current_energy: 100, satiety: 100, amity: 100,
  remaining_lifetime: 1000, max_lifetime: 1000, opened_skill_slots: 2, available_skill_slots: 2, revision: 9, talents: [], stats,
  skills: [{ slot_index: 0, skill_id: 400, name: 'Vital Boost I', type: 0, tier: 1, is_active: true, skill_rank: 1, skill_experience: 0, species_ok: true, published: true }],
  skill_problems: [], growth_bracket: ['12', '16'], soul_contract_stage_bonus: 3 };
const SHEET = { character: { id: 5, name: 'Pethero', level: 100, class: 'Warrior', pet_shed_capacity: 2, owned_pets: 1 }, database: 'godswar_test', writable: true, allow_force: false,
  online: { online: false }, live: { available: false, why_pets: 'no live pet operation' }, settings: { maximum_level: 120, maximum_rebirth_count: 100 },
  aptitudes: [{ id: 1, name: 'Weak', savvy: [60, 80], growth: ['12', '16'], talent_mask: 0, cells: 1 }], species: [], exp_steps: {}, rebirth_steps: [],
  soul_bonus: [0, 3, 4, 5, 6, 7, 8], stat_names: {}, wire: { remaining_lifetime: [0, 65535, ''] }, pets: [PET], mounts: { items: [], gear: {}, slot_names: {}, kinds: {} }, findings: [] };
const SKILLS = { skills: [
  { id: 400, name: 'Vital Boost I', type: 0, tier: 1, head: 400, is_head: true, family: 'Vital Boost', published: true, trait: [0, 0, 0, 0, 10, 0], restrict: [0, 5, 11], values: [600, 900, 1125] },
  { id: 405, name: 'Vital Boost II', type: 0, tier: 2, head: 405, is_head: true, family: 'Vital Boost', published: true, trait: [0, 0, 0, 0, 20, 0], restrict: [0, 2, 3], values: [900, 1200, 1500] },
  { id: 808, name: 'Dark Vengeance I', type: 62, tier: 1, head: 808, is_head: true, family: 'Dark Vengeance', published: true, only: 'Ghost', species: [9], trait: null, restrict: [0], values: [40] }],
  families: [{ type: 0, name: 'Vital Boost', only: null, species: null, tiers: { 1: 400, 2: 405 }, books: { 1: [10200], 2: [] } },
             { type: 62, name: 'Dark Vengeance', only: 'Ghost', species: [9], tiers: { 1: 808 }, books: { 1: [10210] } }],
  species: [{ id: 4, name: 'Panda', starter: 405 }] };
const CONTENT = { source: 'client snapshot', species: [{ id: 4, name: 'Panda', factor: '2.60' }, { id: 2, name: 'Flower Pixie', factor: '0.80' }], spirits: {}, rebirth_spirits: {},
  rebirth_levels: { 1: 30 }, waters: [], max_level: 120, max_rebirths: 100, max_rank: '655.35', ceiling: { savvy: '7.80', rank: '7.80' }, merge_min_level: 30, model_changes: [8, 20],
  items: { merge_spirit: 10103, merge_spirit_bound: 10097, rebirth_spirit: 10104, rebirth_spirit_bound: 10098 } };
const six = v => [v, v, v, v, v, v];
async function api(sub, body) {
  calls.push({ sub, body: body && JSON.parse(JSON.stringify(body)) });
  if (sub.startsWith('sheet/')) return JSON.parse(JSON.stringify(SHEET));
  if (sub === 'skills') return SKILLS;
  if (sub === 'sim/content') return CONTENT;
  if (sub === 'sim/merge') return { basic: six('27.80'), rank: '10.50', start_basic: six('20.00'), start_rank: '2.70', partner_added: six('360.00'), notes: [], stalled_at: null,
    ceiling: {}, gain: six('7.80'), rank_gain: '7.80', steps: [{ n: 1, bounds: six(['0.01', '7.80']), gains: six('7.80'), need: six('0.00'), rank_bounds: ['7.80', '7.80'], rank_gain: '7.80', basic: six('27.80'), rank: '10.50' }] };
  if (sub === 'sim/rebirth') return { accel: six('0.20'), range: ['0.01', '0.20'], completed: 1, level: 120, added_level1: six('2.70'), added_at_level: six('324.00'), phoenix: ['0.10', '0.20'], notes: [],
    steps: [{ n: 1, level_needed: 30, water: { name: 'Spring Water' }, increase: six('0.20'), accel: six('0.20'), model_change: false }] };
  if (sub.startsWith('legal/')) return { checks: [{ level: body.fields.maximum_energy ? 'warn' : 'ok', what: 'energy', msg: body.fields.maximum_energy ? 'max energy 500: every pet has 100' : 'nothing here is out of reach' }],
    limits: { basic_total_max: '70.00', basic_hatch: '70.00', rank_max: '3.00', hatch_ranks: ['2.00', '2.70', '3.00'], accel: ['0.00', '0.00'], phoenix: ['0.00', '0.00'], level_max: 120, energy: 100, per_merge: { savvy: '7.80', rank: '7.80' } } };
  if (sub.includes('/apply-sim') || sub.includes('/skill')) return { ok: true };
  throw new Error('unexpected ' + sub);
}
const tick = (ms = 0) => new Promise(r => setTimeout(r, ms));

(async () => {
  const el = h('div');
  await component.mount(el, { cid: 5, route: [], api });
  await tick();
  const root = () => el.children[1];

  // ---- legal pet mode: off by default, the toggle turns it on, checks follow pending edits
  let tog = byText(root(), 'button', 'Legal pet mode');
  assert.ok(tog && text(tog).includes('off'), 'legal pet mode starts off');
  await tog.fire('click'); await tick(10);
  assert.equal(store['gw.pets.legal'], '1', 'the toggle is remembered per viewer');
  assert.ok(calls.some(c => c.sub === 'legal/5' && c.body.pet === 7), 'legal checks are asked for the selected pet');
  assert.ok(byText(root(), 'div', 'Legal: nothing here'), 'a clean pet shows the ok line');
  const maxE = find(root(), n => n.tag === 'input' && n.attrs['aria-label'] === 'Max energy')[0];
  assert.ok(maxE, 'max energy field');
  assert.ok(byText(root(), 'span', '100 for every pet'), 'the max energy note says 100 is normal');

  // ---- the skill picker: one row per family, then the level
  calls = []; modals = [];
  await byText(root(), 'button', '+ Learn a skill').fire('click'); await tick();
  assert.ok(calls.some(c => c.sub === 'skills'), 'the skill library is loaded');
  const dlg = modals.at(-1).content;
  const rows = find(dlg, n => n.classList.contains('pt-famrow'));
  assert.equal(rows.length, 1, 'only the families a Panda can learn (Dark Vengeance is Ghost-only)');
  assert.ok(text(rows[0]).includes('Vital Boost') && !text(rows[0]).includes('Vital Boost I '), 'the combined family name, no tier numeral');
  assert.equal(find(rows[0], n => n.classList.contains('pt-tierpip')).length, 2, 'one pip per level');
  assert.ok(find(rows[0], n => n.classList.contains('nobook')).length === 1, 'tier II has no skill book: pip struck out');
  await byText(dlg, 'button', 'Only skills a Panda').fire('click');
  assert.equal(find(dlg, n => n.classList.contains('pt-famrow')).length, 2, 'the filter off shows every family');
  await find(dlg, n => n.classList.contains('pt-famrow'))[0].fire('click'); await tick();
  const detail = find(dlg, n => n.classList.contains('pt-libdetail'))[0];
  assert.ok(text(detail).includes('Effect now'), 'the level panel shows the effect at the pet rank');
  assert.ok(text(detail).includes('600 (step 1 of 3, from rank 0)'), 'the pet has tier I: at rank 2.70 it resolves the rank-0 step, 600');
  const tierII = find(detail, n => n.tag === 'button' && text(n) === 'II')[0];
  await tierII.fire('click'); await tick();
  assert.ok(text(detail).includes('1200 (step 2 of 3, from rank 2)'), 'tier II at rank 2.70 resolves the rank-2 step, 1200');
  const setBtn = byText(detail, 'button', 'Set to II');
  assert.ok(setBtn, 'the pet has the family: the button sets the level');
  assert.ok(text(detail).includes('Skill book:') && text(detail).includes('none'), 'no skill book for tier II is shown');
  calls = [];
  await setBtn.fire('click'); await tick(10);
  const sk = calls.find(c => c.sub === 'pet/5/skill');
  assert.ok(sk && sk.body.action === 'set' && sk.body.skill === 405 && sk.body.pet === 7 && sk.body.revision === 9 && !('cell' in sk.body), 'set the tier II id on the same family');

  // ---- the simulator tab
  calls = [];
  await byText(root(), 'button', 'Merge & rebirth simulator').fire('click'); await tick(400);
  const m1 = calls.filter(c => c.sub === 'sim/merge').at(-1), r1 = calls.filter(c => c.sub === 'sim/rebirth').at(-1);
  assert.ok(m1 && r1, 'the simulator asks for merges and rebirths');
  assert.deepEqual(m1.body.primary.basic, six('20'), 'the pet\'s Basic prefills the primary');
  assert.equal(m1.body.partner.level, 120, 'the fake partner starts at level 120');
  assert.deepEqual(m1.body.savvy_pos, six(500), 'sliders start at the average');
  assert.equal(r1.body.pet.soul_contract_stage, 1, 'the rebirth sim gets the contract stage');
  assert.ok(byText(root(), 'td', '+7.80'), 'the merge result is drawn');
  // presets: Max on the merge card
  calls = [];
  const maxBtn = find(root(), n => n.tag === 'button' && text(n) === 'Max')[0];
  await maxBtn.fire('click'); await tick(400);
  const m2 = calls.filter(c => c.sub === 'sim/merge').at(-1);
  assert.deepEqual(m2.body.savvy_pos, six(1000), 'Max puts every stat slider at 100%');
  assert.equal(m2.body.rank_pos, 1000, 'and the rank slider');
  // one slider
  calls = [];
  const sl = find(root(), n => n.tag === 'input' && n.attrs['aria-label'] === 'Luck merge roll')[0];
  sl.value = '250'; await sl.fire('input'); await tick(400);
  assert.equal(calls.filter(c => c.sub === 'sim/merge').at(-1).body.savvy_pos[5], 250, 'a stat slider moves its own roll');
  assert.ok(find(root(), n => n.tag === 'input' && n.attrs['aria-label'] === 'rank merge roll')[0].disabled, 'no spirit: the rank roll is fixed (slider off)');
  // partner species and a number
  calls = [];
  const spSel = find(root(), n => n.tag === 'select' && n.attrs['aria-label'] === 'Fake partner (merged away) species')[0];
  spSel.value = '2'; await spSel.fire('change');
  const nIn = find(root(), n => n.tag === 'input' && n.attrs['aria-label'] === 'number of merges')[0];
  nIn.value = '25'; await nIn.fire('input'); await tick(400);
  const m3 = calls.filter(c => c.sub === 'sim/merge').at(-1);
  assert.equal(m3.body.partner.species, 2, 'partner species is sent');
  assert.equal(m3.body.merges, 25, 'the merge count is sent');
  // Apply both
  calls = []; confirms = [];
  await byText(root(), 'button', 'Apply both').fire('click'); await tick(10);
  const ap = calls.find(c => c.sub === 'pet/5/apply-sim');
  assert.ok(confirms.length === 1 && ap, 'Apply asks first, then writes');
  assert.equal(ap.body.pet, 7); assert.equal(ap.body.revision, 9);
  assert.equal(ap.body.merge.merges, 25); assert.equal(ap.body.merge.partner.species, 2);
  assert.deepEqual(ap.body.merge.savvy_pos, [1000, 1000, 1000, 1000, 1000, 250]);
  assert.equal(ap.body.rebirth.rebirths, 1); assert.equal(ap.body.rebirth.level, 120);
  calls = [];
  await byText(root(), 'button', 'Apply the merges').fire('click'); await tick(10);
  const ap2 = calls.find(c => c.sub === 'pet/5/apply-sim');
  assert.ok(ap2.body.merge && !ap2.body.rebirth, 'Apply the merges sends only the merges');
  component.unmount();
  console.log('PASS pets: family skill picker (combined names, levels, books, set tier), legal pet mode (toggle, checks), simulator (prefill, presets, sliders, partner, Apply bodies).');
})().catch(e => { console.error('FAIL', e); process.exit(1); });
