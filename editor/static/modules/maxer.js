/* static/modules/maxer.js - bring one offline character to the top in one change with one Undo. Backend: modules/maxer.py. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;

  GW.module("maxer", {
    async mount(el, ctx) {
      const cid = ctx.cid;
      const o = { level: 200, skills: true, talent_rank: 60, zodiac: true, gear: true, capacity: true, quests: true };
      const box = (key, label) => h("label", { class: "gw-max-opt" }, h("input", { type: "checkbox", checked: o[key], "aria-label": label, onchange: e => { o[key] = e.target.checked; show(); } }), label);
      const num = (key, label, max) => h("label", { class: "gw-max-opt" }, label + " ", h("input", { type: "number", value: o[key], min: 0, max, "aria-label": label, onchange: e => { o[key] = Number(e.target.value); show(); } }));
      const out = h("div");
      el.append(
        h("p", { class: "gw-dim" }, "One offline character, one change, one Undo in History. It does not choose gear for empty slots (use Favourites) or fill zodiac grids. The warehouse is not changed: the game only allows that through its own expansion."),
        h("div", { class: "gw-max-opts" }, num("level", "Level", 200), num("talent_rank", "Talent rank (0 = leave)", 100), box("skills", "Skills to top rank"), box("zodiac", "Zodiac level and energy"), box("gear", "Worn gear to top quality and grade"), box("capacity", "Pet shed"), box("quests", "Main quests up to the level")),
        out);
      async function show() {
        GW.clear(out);
        let p;
        try { p = await ctx.api("preview/" + cid, { options: o }); }
        catch (e) { await GW.writeError(e); return; }
        const typed = h("input", { type: "text", placeholder: "Type " + p.name + " to confirm", "aria-label": "Type the character's name to confirm" });
        out.append(h("h3", null, p.name + ", level " + p.level + " " + p["class"]),
          p.online ? h("p", { class: "tt-bad" }, p.name + " is online. Log the character out first.") : null,
          p.changes.length ? h("ul", null, p.changes.map(c => h("li", null, c))) : h("p", { class: "gw-dim" }, "Nothing to change."),
          p.changes.length ? h("div", { class: "gw-max-opts" }, typed, h("button", { class: "gw-btnx", type: "button", onclick: async () => {
            try { GW.writeDone(await ctx.api("apply/" + cid, { options: o, confirm: typed.value }), p.name + " updated"); await show(); }
            catch (e) { await GW.writeError(e); }
          } }, "Apply")) : null);
      }
      await show();
    }
  });
})();
