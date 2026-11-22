/* static/shared/damage_calc.js - "Damage to a character": what a monster's hit does to one character.
   GW.combat = an exact copy of the server's monster-on-player maths (AuthoredCombatPveCurrent = AuthoredCombatV1:
   hit chance, crit chance, normal / crit damage, skill multiplier). tests/test_damage_calc_ui.cjs checks it against
   200 results printed by the server itself (tests/fixtures/combat-v1-vectors.json); rebuild those with
   tools/build_wonderland_roster.py when the server formula changes.
   GW.damagePanel(opts) = the calculator UI (character picker + editable defences + a monster table). Used by the
   Monster stat curve page; any page can mount it with its own monster rows. */
(function () {
  "use strict";
  const GW = window.GW;
  const BP = 10000, MAX_RED = 8000, MAX_INC = 8000, MAX_CRIT_RED = 8000, MAX_IGNORE = 8000;
  const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
  const tdiv = (a, b) => Math.trunc(a / b);                       // C# long division (truncates toward zero)
  const roundAway = v => Math.sign(v) * Math.round(Math.abs(v));  // decimal.Round(..., MidpointRounding.AwayFromZero)
  const n0 = v => Math.max(0, Number(v) || 0);

  // AuthoredCombatRatingScale.Resolve
  const scale = (al, tl) => 100 + 25 * clamp(Math.max(al | 0, tl | 0), 1, 10000);
  // AuthoredHitChancePolicy.Normalized(4000, 4000) + AuthoredCombatFormula clamps
  function hitChance(a, t) {
    const hit = n0(a.Hit), dodge = n0(t.Dodge), delta = hit - dodge;
    return clamp(9000 + tdiv(4000 * delta, scale(a.Level, t.Level) + hit + dodge), 500, 9800);
  }
  // AuthoredCriticalChancePolicy.Normalized(4500): base 5%, at most 50%
  function critChance(a, t) {
    const c = n0(a.Critical), r = n0(t.CriticalResistance);
    return clamp(500 + tdiv(4500 * (c - r), scale(a.Level, t.Level) + c + r), 0, 5000);
  }
  // AuthoredCombatFormula.ResolveForOutcome. magic: basic attack -> profession 2/3, skill -> property 1.
  function damage(a, t, o) {
    o = o || {};
    const magic = o.skill ? o.property === 1 : (a.Profession === 2 || a.Profession === 3);
    const attack = n0(magic ? a.MagicAttack : a.PhysicalAttack);
    const ignore = clamp(Number(magic ? a.IgnoreMagicDefenseBasisPoints : a.IgnorePhysicalDefenseBasisPoints) || 0, 0, MAX_IGNORE);
    const def = Math.trunc(n0(magic ? t.MagicDefense : t.PhysicalDefense) * (BP - ignore) / BP);
    const afterDef = Math.max(0, attack - def);
    const core = afterDef * Math.max(0, 1 + (o.power || 0)) + (o.flat || 0);
    const typed = core * (1 + n0(magic ? a.MagicDamageBonusBasisPoints : a.PhysicalDamageBonusBasisPoints) / BP);
    let critBonus = 0;
    if (o.critical && typed > 0) {
      critBonus = typed * ((5000 + n0(a.CriticalDamageBasisPoints)) / BP) + n0(a.CriticalDamageFlat);
      critBonus = critBonus * (BP - clamp(Number(t.CriticalDamageReductionBasisPoints) || 0, 0, MAX_CRIT_RED)) / BP;
      critBonus = Math.max(0, critBonus - n0(t.CriticalDamageFlatReduction));
    }
    const withAppend = typed + critBonus + n0(magic ? a.MagicAppendDamage : a.PhysicalAppendDamage);
    const red = clamp(Number(magic ? t.MagicDamageReductionBasisPoints : t.PhysicalDamageReductionBasisPoints) || 0, 0, MAX_RED);
    const afterRed = withAppend > 0 ? withAppend * (BP - red) / BP : withAppend;
    const inc = clamp(Number(magic ? t.MagicDamageTakenIncreaseBasisPoints : t.PhysicalDamageTakenIncreaseBasisPoints) || 0, 0, MAX_INC);
    const afterInc = afterRed > 0 ? afterRed * (1 + inc / BP) : afterRed;
    const left = afterInc - n0(magic ? t.MagicFlatAbsorption : t.PhysicalFlatAbsorption);
    const floorOne = !o.skill || withAppend > 0;
    if (left <= 0) return floorOne ? 1 : 0;
    return Math.min(4294967295, roundAway(left));
  }
  // MonsterCombatProfileCatalog.Resolve: level formula, x1.15 elite, x1.3 boss (same as Monster stat curve)
  function monsterFormula(L, elite, boss) {
    L = clamp(L | 0, 1, 10000);
    const bp = boss ? 13000 : elite ? 11500 : 10000, s = v => clamp(Math.floor(v * bp / 10000), 0, 2147483647);
    return { physical_attack: s(21 + 3 * L + Math.floor(L / 3)), magic_attack: s(23 + 3 * L + Math.floor(L / 2)),
      physical_defense: s(10 + 6 * L + Math.floor(L * L / 10)), magic_defense: s(10 + 5 * L + Math.floor(L * L / 12)),
      hit: s(100 + 20 * L), dodge: s(50 + 12 * L), critical: s(25 + 8 * L), critical_resistance: s(25 + 10 * L) };
  }
  // One monster row -> what it does to one character. m: {level, magic, pa, ma, hit, crit, interval, mult?}
  function against(m, ch) {
    const a = { Level: m.level, Profession: m.magic ? 3 : 0, PhysicalAttack: m.pa, MagicAttack: m.ma, Hit: m.hit, Critical: m.crit };
    const t = { Level: ch.level, PhysicalDefense: ch.pd, MagicDefense: ch.md, Dodge: ch.dodge, CriticalResistance: ch.crit_res,
      PhysicalFlatAbsorption: ch.pabs, MagicFlatAbsorption: ch.mabs, PhysicalDamageReductionBasisPoints: Math.round((ch.pred || 0) * 100),
      MagicDamageReductionBasisPoints: Math.round((ch.mred || 0) * 100), CriticalDamageReductionBasisPoints: Math.round((ch.cred || 0) * 100) };
    const skill = m.mult ? { skill: true, property: m.magic ? 1 : 0, power: m.mult - 1 } : {};
    const hc = hitChance(a, t), cc = critChance(a, t);
    // m.damagePct: a what-if multiplier on the final damage (not a server setting today).
    const k = m.damagePct && m.damagePct !== 100 ? m.damagePct / 100 : 1, sc = d => k === 1 ? d : Math.max(d > 0 ? 1 : 0, Math.round(d * k));
    const normal = sc(damage(a, t, skill)), crit = sc(damage(a, t, Object.assign({ critical: true }, skill)));
    const avg = hc / BP * ((1 - cc / BP) * normal + cc / BP * crit);
    const perSec = m.interval > 0 ? avg / m.interval : null;
    // "to down you" uses the average hit (misses and crits included), so a monster that only hurts on a crit still counts.
    return { hit: hc, crit: cc, normal, critHit: crit, avg, perSec, hitsToKill: avg > 0 ? Math.ceil(ch.hp / avg) : Infinity,
      secondsToKill: perSec ? ch.hp / perSec : null };
  }
  GW.combat = { hitChance, critChance, damage, monsterFormula, against, MAP_ATTACK_INTERVAL: 23 / 12 };

  // ---------------------------------------------------------------- UI
  const FIELDS = [["level", "Level", 1, 200], ["hp", "Max HP", 1, 5000000], ["pd", "Phys. defence", 0, 200000], ["md", "Magic defence", 0, 200000],
    ["pabs", "Phys. absorb", 0, 200000], ["mabs", "Magic absorb", 0, 200000], ["dodge", "Dodge", 0, 100000], ["crit_res", "Crit. resistance", 0, 100000],
    ["pred", "Phys. damage reduction %", 0, 80], ["mred", "Magic damage reduction %", 0, 80], ["cred", "Crit. damage reduction %", 0, 80]];
  const BLANK = { name: "Custom", level: 200, hp: 150000, pd: 5000, md: 5000, pabs: 0, mabs: 0, dodge: 2000, crit_res: 300, pred: 0, mred: 0, cred: 0 };
  const pct = bp => (bp / 100).toFixed(bp % 100 ? 1 : 0) + "%";
  const secs = s => s === null ? "" : !isFinite(s) ? "never" : s < 1 ? "<1 s" : s < 90 ? s.toFixed(s < 10 ? 1 : 0) + " s" : (s / 60).toFixed(1) + " min";

  /* opts: { characters: [{name, level, hp, pd, md, pabs, mabs, dodge, crit_res, source}], note }
     returns el with el.setRows(rows, {title, columns: ["now","after"]?}); rows: [{label, sub, hp, groups: {now: monsterRow, after?: monsterRow}}] */
  GW.damagePanel = function (opts) {
    const h = GW.h, chars = (opts && opts.characters) || [];
    let ch = Object.assign({}, chars[0] || BLANK), rows = [], cols = ["now"], titles = { now: "Damage" };
    const pick = h("select", { "aria-label": "Character", onchange: e => { const c = chars[Number(e.target.value)]; ch = Object.assign({}, c || BLANK); drawFields(); draw(); } },
      chars.map((c, i) => h("option", { value: i }, c.name + " (Lv " + c.level + ")")), h("option", { value: -1 }, "Custom character"));
    const fieldBox = h("div", { class: "gw-dc-fields" }), table = h("div", { class: "gw-dc-table" }), src = h("p", { class: "gw-dim gw-dc-src" });
    function drawFields() {
      GW.clear(fieldBox).append(...FIELDS.map(([k, label, min, max]) => GW.numberField({ label, value: ch[k], min, max, decimals: ["pred", "mred", "cred"].includes(k) ? 1 : 0,
        onChange: (v, ok) => { if (ok && v !== null) { ch[k] = v; draw(); } } })));
      src.textContent = ch.source || "";
    }
    function cell(r, key, hp) {
      if (!r) return [h("td", { class: "gw-dim" }, "–"), h("td"), h("td"), h("td")];
      const x = GW.combat.against(r, Object.assign({}, ch, { hp }));
      const danger = x.normal >= hp ? "bad" : x.normal * 3 >= hp ? "warn" : "";
      const atk = (r.magic ? "magic " + GW.fmt(r.ma) : "phys. " + GW.fmt(r.pa)) + (r.damagePct && r.damagePct !== 100 ? " · ×" + r.damagePct + "%" : "");
      return [h("td", { class: "num grp" }, pct(x.hit), h("div", { class: "gw-dim" }, "crit " + pct(x.crit)), h("div", { class: "gw-dim" }, atk)),
        h("td", { class: "num " + danger }, GW.fmt(x.normal), h("div", { class: "gw-dim" }, "crit " + GW.fmt(x.critHit))),
        h("td", { class: "num" }, x.perSec === null ? "–" : GW.fmt(Math.round(x.perSec)) + "/s"),
        h("td", { class: "num " + danger }, x.hitsToKill === Infinity ? "never" : GW.fmt(x.hitsToKill) + " hits", h("div", { class: "gw-dim" }, secs(x.secondsToKill)))];
    }
    function draw() {
      GW.clear(table);
      if (!rows.length) { table.append(h("p", { class: "gw-dim" }, "No monsters to show yet.")); return; }
      const hp = ch.hp;
      const head1 = h("tr", null, h("th", { rowspan: 2 }, "Monster"), h("th", { rowspan: 2 }, "Lv · HP · type"),
        ...cols.map(c => h("th", { colspan: 4, class: "grp" }, titles[c] || c)));
      const head2 = h("tr", null, ...cols.flatMap(() => ["Hit chance", "Per hit", "Per second", "Downs you in (average)"].map(x => h("th", null, x))));
      const body = rows.map(r => {
        const m = r.groups.now || r.groups[cols[0]];
        return h("tr", { class: r.cls || "" }, h("td", null, r.label, r.sub ? h("div", { class: "gw-dim" }, r.sub) : null),
          h("td", { class: "num" }, "Lv " + m.level + (r.hp === null || r.hp === undefined ? "" : " · HP " + GW.fmt(r.hp)), h("div", { class: "gw-dim" },
            (m.magic ? "magic " + GW.fmt(m.ma) : "phys. " + GW.fmt(m.pa)) + (m.mult ? " ×" + m.mult : "") + " · every " + (m.interval ? m.interval.toFixed(2) + " s" : "–"))),
          ...cols.flatMap(c => cell(r.groups[c], c, hp)));
      });
      table.append(h("div", { class: "gw-dc-scroll" }, h("table", { class: "gw-w-table gw-dc-t" }, h("thead", null, head1, head2), h("tbody", null, body))));
    }
    const el = h("div", { class: "gw-dc" },
      h("div", { class: "gw-dc-who" }, h("label", null, "Character ", pick), src),
      fieldBox,
      opts && opts.note ? h("p", { class: "gw-dim" }, opts.note) : null,
      table);
    el.setRows = (r, o) => { rows = r || []; cols = (o && o.columns) || ["now"]; titles = (o && o.titles) || { now: "Damage" }; draw(); };
    el.character = () => Object.assign({}, ch);
    drawFields(); draw();
    return el;
  };
})();
