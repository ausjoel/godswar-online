// Readability pass: the shared hover renderer (static/shared/tooltip.js: GW.helpText / GW.helpNode) and the Server
// rates page (static/modules/rates.js): the row hover in the house style, and the Global monster EXP multiplier shown
// and edited as x1.00 .. x5.00 while the save still sends the stored basis points. Fake DOM; layout is not tested.
const fs = require('fs'), vm = require('vm'), path = require('path'), assert = require('node:assert/strict');
const flat = a => a.flat(Infinity).filter(x => x !== null && x !== undefined && x !== false);
function el(tag, props, ...kids) {
  const cls = new Set(String((props && props.class) || '').split(/\s+/).filter(Boolean));
  const e = { tag, props: props || {}, children: [], dataset: {}, style: {}, hidden: false, value: '', disabled: !!(props && props.disabled),
    classList: { add: c => cls.add(c), remove: c => cls.delete(c), contains: c => cls.has(c), toggle: (c, on) => { (on === undefined ? !cls.has(c) : on) ? cls.add(c) : cls.delete(c); } },
    get className() { return [...cls].join(' '); }, set className(v) { cls.clear(); String(v).split(/\s+/).filter(Boolean).forEach(c => cls.add(c)); },
    get childNodes() { return this.children; }, append(...x) { this.children.push(...flat(x)); }, prepend(...x) { this.children.unshift(...flat(x)); },
    addEventListener(ev, fn) { this['on' + ev] = fn; }, setAttribute(k, v) { this.props[k] = v; }, querySelector(sel) { return walk(this).slice(1).find(n => n.classList && n.classList.contains(sel.replace(/^\./, ''))) || null; }, before() {},
    set textContent(v) { this.children = [String(v)]; }, get textContent() { return text(this); } };
  for (const [k, v] of Object.entries(props || {})) { if (k.startsWith('on') && typeof v === 'function') e[k] = v; if (k === 'value') e.value = v; }
  e.append(...kids);
  return e;
}
const walk = (x, out = []) => { if (x && typeof x === 'object') { out.push(x); (x.children || []).forEach(c => walk(c, out)); } return out; };
const text = x => typeof x === 'string' || typeof x === 'number' ? String(x) : (x.children || []).map(text).join('');
const has = (x, c) => x.classList && x.classList.contains(c);
const document = { addEventListener() {}, body: el('body'), activeElement: null, getElementById: () => null };

// ---------------------------------------------------------------- the shared renderer
const GW = { h: el, text: s => String(s).replace(/\|c[0-9A-Fa-f]{8}|\|r/g, ''), clear: n => { n.children = []; return n; } };
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../static/shared/tooltip.js'), 'utf8'), { window: { GW, innerWidth: 1000, innerHeight: 800 }, document, console, setTimeout, clearTimeout });
const src = GW.helpText({ title: 'Monster kill EXP', text: 'Multiplies the EXP of every kill.', facts: [['Normal value', 'x1'], ['Allowed here', 'x0 – x1,000,000'], ['Skipped', '']],
  listTitle: 'Limits', list: ['One kill gives at most 100,000,000 EXP.', 'Characters stop at level 200.'], note: 'Setting name: exp.monster' });
assert.equal(src, '# Monster kill EXP\nMultiplies the EXP of every kill.\n\nNormal value: x1\nAllowed here: x0 – x1,000,000\n\nLimits:\n• One kill gives at most 100,000,000 EXP.\n• Characters stop at level 200.\n~ Setting name: exp.monster');
const node = GW.helpNode(src), kids = node.children;
assert.ok(has(node, 'gw-help'), 'help block');
assert.ok(has(kids[0], 'tt-name') && text(kids[0]) === 'Monster kill EXP', 'title is a heading, without the "# "');
assert.equal(text(kids[1]), 'Multiplies the EXP of every kill.');
assert.ok(has(kids[2], 'tt-fact') && has(kids[2], 'tt-gap') && has(kids[2].children[0], 'tt-key') && text(kids[2]) === 'Normal value: x1', 'a fact with a highlighted label after a gap');
const ul = kids.find(k => k.tag === 'ul');
assert.ok(ul && ul.children.length === 2 && ul.children.every(li => li.tag === 'li') && text(ul.children[1]) === 'Characters stop at level 200.', 'a real list, bullets removed');
assert.ok(has(kids.at(-1), 'tt-note') && text(kids.at(-1)) === 'Setting name: exp.monster', 'the note is last and small');
// old plain strings and the game's colour codes still work; a sentence with a colon in the middle is not a "fact"
assert.equal(text(GW.helpNode('Just one sentence.')), 'Just one sentence.');
assert.equal(GW.helpNode('|cFFFFFF00Yellow|cFFFFFFFF text\n- a\n- b').children[1].children.length, 2);
assert.ok(!has(GW.helpNode('this is how it works: slowly').children[0], 'tt-fact'));
assert.equal(GW.helpNode(null).children.length, 0);

// ---------------------------------------------------------------- the Server rates page
let component; const fields = [], bar = { set: (k, v) => { bar.items[k] = v; }, unset: k => { delete bar.items[k]; }, clear() { bar.items = {}; }, items: {}, el: el('div') }, posts = [];
Object.assign(GW, { module: (n, p) => { component = p; }, icon: () => el('i'), cell: o => el('span', { 'data-help': o.help }), appliesChip: (a, t) => el('span', { class: 'chip' }, t || a),
  saveBar: o => { bar.onSave = o.onSave; return bar; }, status() {}, on: () => () => {}, emit() {}, changes: { open() {} },
  numberField: o => { const f = el('span', { class: 'gw-nf' }); f.opts = o; fields.push(f); return f; } });
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../static/modules/rates.js'), 'utf8'), { window: { GW }, document, console, setTimeout: fn => fn() });
const state = { database: 'copy', writable: true, live: false, table: true, revision: 7, categories: ['Character EXP & Talent', 'Pets'], constants: {}, presets: [], loot: { rules: [], tables: [] },
  server: { reachable: true, revision: 7, rates: { 'exp.monster': 1 } }, build: { round2: true, keys: 2 }, world: { count: 0, names: [] }, checks: [], log: [], module_changes: [],
  rows: [{ rate_key: 'exp.monster', label: 'Monster kill EXP', category: 'Character EXP & Talent', kind: 'multiplier', unit: 'x', value: 1, default_value: 1, min_value: 0, max_value: 1000000, step: 0.05,
    description: 'Multiplies the character EXP of every monster kill. One kill gives at most 100,000,000 EXP.', applies_to: 'Game/GameClientHandler.Progression.cs',
    info: { applies: 'next kill', caps: ['One kill gives at most 100,000,000 EXP.'], sprite: 1 } },
  { rate_key: 'pet.magic_bell_drop_chance_bp', label: 'Magic Bell: drop chance per kill (50 = 0.5%)', category: 'Pets', kind: 'absolute', unit: 'basis points', value: 50, default_value: 50, min_value: 0, max_value: 10000, step: 1,
    description: 'Chance that a listed boss or elite drops a Magic Bell. 50 = 0.5%, 100 = 1%.', info: { applies: 'next kill', caps: [] } },
  { rate_key: 'party.kill_share', label: 'Party kill sharing', category: 'Pets', kind: 'absolute', unit: '0/1', value: 0, default_value: 0, min_value: 0, max_value: 1, step: 1,
    description: 'Shares kills with the party. On: members nearby share. Off: only the killer gets them. Drops always stay with the killer.', info: { applies: 'next kill', caps: [] } }],
  stock: [{ id: 'global_exp', label: 'Global monster EXP multiplier', table: 'monster_reward_settings', column: 'global_experience_multiplier_basis_points', field: 'monster_reward_settings.global_experience_multiplier_basis_points',
    value: 10000, min: 10000, max: 50000, step: 500, applies: 'restart', available: true, scale: 10000, shown: { prefix: 'x', decimals: 2, value: 1, min: 1, max: 5, step: 0.05, text: 'x1' },
    help: 'Multiplies the EXP of every monster kill for everyone.\n• x1 to x5; x1 is normal.' },
  { id: 'level_gap', label: 'Kill EXP level gap', table: 'monster_reward_settings', column: 'maximum_lower_level_gap', field: 'monster_reward_settings.maximum_lower_level_gap', value: 20, min: 0, max: 199, step: 1, applies: 'restart', available: true, help: 'x' }] };
(async () => {
  const root = el('div'); root.isConnected = true;
  const ctx = { api: async (route, body) => { if (body) posts.push({ route, body }); return route === 'save' ? { state, change: { id: 'c1' }, mode: 'hot', server_applied: {} } : state; } };
  await component.mount.call(component, root, ctx);
  const all = walk(root);
  // row hover: house style, no developer line
  const help = all.find(n => n.props && n.props.class && String(n.props.class).includes('gw-rt-label')).props['data-help'];
  assert.ok(help.startsWith('# Monster kill EXP\nMultiplies the character EXP of every monster kill.'), help);
  for (const want of ['Normal value: x1', 'Allowed here: x0 – x1,000,000', 'Applies: next kill; no restart or logout needed', 'Limits:\n• One kill gives at most 100,000,000 EXP.', '~ Setting name: exp.monster'])
    assert.ok(help.includes(want), 'row hover has ' + JSON.stringify(want));
  assert.ok(!/Stored in|server_rates|\.cs\b|Key:|\|c[0-9A-F]{8}/.test(help), 'no table, file or colour-code text in the row hover');
  // a switch: On / Off start their own line
  const sw = all.filter(n => n.props && String(n.props.class || '').includes('gw-rt-label')).map(n => n.props['data-help']).find(t => t.startsWith('# Party kill sharing'));
  assert.ok(sw.includes('\nOn: members nearby share.\nOff: only the killer gets them.'), sw);
  // basis points are never shown bare
  assert.ok(text(root).includes('50 (0.5%)'), 'a basis-point value also says its percent');
  // the global EXP multiplier: shown x1.00 .. x5.00
  const g = fields.find(f => f.opts.label === 'Global monster EXP multiplier').opts;
  assert.deepEqual([g.value, g.min, g.max, g.step, g.decimals, g.field], [1, 1, 5, 0.05, 2, undefined]);
  assert.ok(text(root).includes('= x1 EXP on every monster kill'), 'the note beside the box speaks multipliers');
  const stockPanel = all.find(n => n.props && n.props.id === 'gw-rt-stock');
  assert.ok(stockPanel && !/10,000|10000|basis/.test(text(stockPanel)), 'no raw 10000 / basis points in the stock settings');
  assert.ok(!/basis points/.test(text(root)), 'the words "basis points" are not shown');
  g.onChange(2.5, true);
  assert.deepEqual([bar.items['s:global_exp'].from, bar.items['s:global_exp'].to], ['x1', 'x2.5'], 'the save bar shows multipliers');
  g.onChange(7, false);                                  // out of range: the field says so, nothing changes
  assert.equal(bar.items['s:global_exp'].to, 'x2.5');
  const gap = fields.find(f => f.opts.label === 'Kill EXP level gap').opts;
  assert.equal(gap.field, 'monster_reward_settings.maximum_lower_level_gap');      // unscaled settings are untouched
  await bar.onSave();
  const save = posts.find(p => p.route === 'save').body;
  assert.equal(JSON.stringify(save.stock), JSON.stringify({ global_exp: 25000 }), 'saved in stored units');
  assert.equal(JSON.stringify(save.stock_expected), JSON.stringify({ global_exp: 10000 }));
  g.onChange(1, true);
  console.log('PASS help renderer (title, facts, list, note, legacy strings) and Server rates page (row hover, On/Off lines, basis points with percent, global EXP as x1-x5 saved as basis points). Layout not tested.');
})().catch(e => { console.error(e); process.exitCode = 1; });
