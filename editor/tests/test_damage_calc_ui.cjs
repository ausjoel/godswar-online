/* GW.combat (static/shared/damage_calc.js) must equal the server's AuthoredCombatPveCurrent exactly.
   Vectors: tests/fixtures/combat-v1-vectors.json, printed by the server itself (tools/build_wonderland_roster.py).
   Also: every Wonderland monster with captured damage hits the capture's level-143 reference target for exactly that damage. */
const assert = require('node:assert/strict'), fs = require('fs'), path = require('path'), vm = require('vm');
const GW = { h: () => ({}) };
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../static/shared/damage_calc.js'), 'utf8'), { window: { GW }, Math, Number, Object, Infinity });
const C = GW.combat;
const V = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures/combat-v1-vectors.json'), 'utf8')).vectors;
let n = 0;
for (const v of V) {
  const a = v.attacker, t = v.target, tag = JSON.stringify([a.Level, a.Profession, a.PhysicalAttack, t.Level, t.PhysicalDefense]);
  assert.equal(C.hitChance(a, t), v.hit_bp, 'hit ' + tag);
  assert.equal(C.critChance(a, t), v.crit_bp, 'crit ' + tag);
  assert.equal(C.damage(a, t), v.normal, 'normal ' + tag);
  assert.equal(C.damage(a, t, { critical: true }), v.critical, 'critical ' + tag);
  assert.equal(C.damage(a, t, { skill: true, property: a.Profession === 3 ? 1 : 0, power: 1.5 }), v.skill_x2_5, 'skill ' + tag);
  n++;
}
// Captured Wonderland evidence (WonderlandCapturedAttackPolicy): damage on the reference target.
const R = JSON.parse(fs.readFileSync(path.join(__dirname, '../data/wonderland-roster.json'), 'utf8'));
const ref = { level: 143, hp: 1e9, pd: 5379, md: 2920, pabs: 4791, mabs: 4791, dodge: 0, crit_res: 8 };
const captured = { B_bosse_kingofscorpion_01: 22153, B_normalf_wraith_001: 25653, B_bosse_dragon_014: 15153, B_bosse_bull_001: 12353,
  B_boss_xerxer_001: 3953, B_bosse_male_001: 14453, B_bossf_dracoladon_003: 15853, B_bosse_greecewarrior_002: 15993 };
for (const [key, want] of Object.entries(captured)) {
  const m = R.roster.athens.find(r => r.key === key);
  const got = C.against(Object.assign({}, m), ref).normal;
  // Spartan marshal and Alpha Demon carry their own reference mitigation; the rest use PD/MD + absorb 4791.
  assert.equal(got, want, key);
}
assert.equal(R.roster.athens.length, 115);
assert.equal(R.party_size_matters, false);
console.log('damage calc: ' + n + ' server vectors + ' + Object.keys(captured).length + ' captured Wonderland hits match');
