/* static/modules/maxer.js - bring one offline character to the top in one change with one Undo. Backend: modules/maxer.py.
   W23: the main-quest finisher also pays every finished quest's rewards (preview first), and the result is checked with
   the Natural check and Item legality rules before Apply is offered. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const n = v => Number(v || 0).toLocaleString("en-AU");

  function rewardsView(r) {
    if (!r) return null;
    const rows = [];
    const money = [];
    if (r.exp) money.push(n(r.exp) + " EXP" + (r.exp_paid < r.exp ? " (" + n(r.exp_paid) + " kept)" : ""));
    if (r.tp) money.push(n(r.tp) + " Talent EXP = " + n(r.tp_points) + " talent points");
    if (r.silver) money.push(n(r.silver) + " Silver");
    if (r.gold) money.push(n(r.gold) + " Gold");
    rows.push(h("li", null, "From " + r.quests + " quest hand-ins: " + (money.length ? money.join(", ") : "no EXP, Talent or money")));
    const list = (label, xs) => xs && xs.length ? h("li", null, label + ": " + xs.map(x => x.name + " x" + x.qty + " (quest " + x.quest + ")").join(", ")) : null;
    rows.push(list("Into the bag", r.bag));
    rows.push(list("To the Prize Chest (bag full)", r.chest));
    if (r.titles && r.titles.length) rows.push(h("li", null, "Quest titles: " + r.titles.map(t => t.name + " (quest " + t.quest + ")").join(", ")));
    if (r.skipped && r.skipped.length) rows.push(h("li", { class: "gw-dim" }, "Not given (the game pays one item per quest, this class's own): " +
      r.skipped.map(x => x.name + " (quest " + x.quest + ")").join(", ")));
    (r.notes || []).forEach(t => rows.push(h("li", { class: "gw-dim" }, t)));
    return h("div", { class: "gw-max-rewards" }, h("h4", null, "Quest rewards"), h("ul", null, rows));
  }

  GW.module("maxer", {
    rewardsView,
    async mount(el, ctx) {
      const cid = ctx.cid;
      const o = { level: 200, skills: true, talent_rank: 60, zodiac: true, gear: true, capacity: true, quests: true, rewards: true };
      const box = (key, label) => h("label", { class: "gw-max-opt" }, h("input", { type: "checkbox", checked: o[key], "aria-label": label, onchange: e => { o[key] = e.target.checked; show(); } }), label);
      const num = (key, label, max) => h("label", { class: "gw-max-opt" }, label + " ", h("input", { type: "number", value: o[key], min: 0, max, "aria-label": label, onchange: e => { o[key] = Number(e.target.value); show(); } }));
      const out = h("div");
      el.append(
        h("p", { class: "gw-dim" }, "One offline character, one change, one Undo in History. It never lowers a level and does not choose gear for empty slots (use Favourites or Worn gear presets) or fill zodiac grids. The warehouse is not changed: the game only allows that through its own expansion. Finished main quests pay what their hand-in pays: items go into the bag, and what does not fit goes to the Prize Chest by the warehouse."),
        h("div", { class: "gw-max-opts" }, num("level", "Level", 200), num("talent_rank", "Talent rank (0 = leave)", 100), box("skills", "Skills to top rank"), box("zodiac", "Zodiac level and energy"), box("gear", "Worn gear to top quality and grade"), box("capacity", "Pet shed"), box("quests", "Main quests up to the level"), box("rewards", "Pay the quests' rewards")),
        out);
      async function show() {
        GW.clear(out);
        let p;
        try { p = await ctx.api("preview/" + cid, { options: o }); }
        catch (e) { await GW.writeError(e); return; }
        const typed = h("input", { type: "text", placeholder: "Type " + p.name + " to confirm", "aria-label": "Type the character's name to confirm" });
        const blocked = p.findings && p.findings.length;
        out.append(h("h3", null, p.name + ", level " + p.level + " " + p["class"]),
          p.online ? h("p", { class: "tt-bad" }, p.name + " is online. Log the character out first.") : null,
          p.changes.length ? h("ul", null, p.changes.map(c => h("li", null, c))) : h("p", { class: "gw-dim" }, "Nothing to change."),
          rewardsView(p.rewards),
          p.warnings && p.warnings.length ? h("div", null, h("h4", null, "Left alone"), h("ul", null, p.warnings.map(w => h("li", { class: "gw-dim" }, w)))) : null,
          blocked ? h("div", null, h("h4", { class: "tt-bad" }, "Would not pass the editor's checks, so Apply is off"),
            h("ul", null, p.findings.map(f => h("li", { class: "tt-bad" }, f.where + ": " + f.message)))) : null,
          p.changes.length && !blocked ? h("div", { class: "gw-max-opts" }, typed, h("button", { class: "gw-btnx", type: "button", onclick: async () => {
            try { GW.writeDone(await ctx.api("apply/" + cid, { options: o, confirm: typed.value }), p.name + " updated"); await show(); }
            catch (e) { await GW.writeError(e); }
          } }, "Apply")) : null);
      }
      await show();
    }
  });
})();
