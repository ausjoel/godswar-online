// Headless tests of the maxer page's reward preview (W23). Not visual/browser validation.
const fs = require('fs'), vm = require('vm'), assert = require('node:assert/strict'), path = require('path');
let component;
function h(tag, props, ...kids) { return Object.assign({ tag, children: kids.flat(Infinity).filter(x => x != null) }, props || {}); }
const GW = { h, module: (name, obj) => { assert.equal(name, 'maxer'); component = obj; } };
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../static/modules/maxer.js'), 'utf8'), { window: { GW }, console });
function text(x) { return typeof x === 'string' || typeof x === 'number' ? String(x) : (x.children || []).map(text).join(''); }

assert.equal(component.rewardsView(null), null);
const v = component.rewardsView({
  quests: 3, exp: 1000, exp_paid: 0, tp: 250, tp_points: 2, silver: 5000, gold: 0,
  bag: [{ name: 'Long Staff', qty: 1, quest: 519 }], chest: [{ name: 'Level 3 Crystal', qty: 8, quest: 1813 }],
  titles: [{ name: 'Greek Vanguard', quest: 376 }], skipped: [{ name: 'Short Sword', quest: 519 }], notes: ['a note']
});
const t = text(v);
for (const want of ['From 3 quest hand-ins', '1,000 EXP (0 kept)', '250 Talent EXP = 2 talent points', '5,000 Silver',
  'Into the bag: Long Staff x1 (quest 519)', 'To the Prize Chest (bag full): Level 3 Crystal x8 (quest 1813)',
  'Quest titles: Greek Vanguard (quest 376)', 'Short Sword (quest 519)', 'a note']) {
  assert.ok(t.includes(want), want + ' missing from: ' + t);
}
assert.ok(!t.includes('Gold'), 'no Gold line when the quests pay none');
console.log('PASS maxer UI: reward preview lists money, bag, Prize Chest, titles, skipped choices and notes. No browser rendering tested.');
