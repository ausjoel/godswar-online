/* static/modules/gearstats.js - "4 legal stats": choose a piece of the character's gear, pick up to four attributes the
   IGG Enhancement guide allows on that slot ("+" ones with levels 1-5), write them in one change. Backend: modules/gearstats.py */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const LOC = { 0: "Worn", 1: "Bag", 3: "Warehouse" };
  const fmtv = (v, pct) => v === null || v === undefined ? "?" : pct ? (Math.round(v * 10000) / 100) + "%" : GW.fmt(v);

  GW.module("gearstats", {
    async mount(el, ctx) {
      el.append(GW.onlineBanner(ctx.cid));
      const list = h("div", { class: "gw-gs-list" }), pane = h("div", { class: "gw-gs-pane" });
      // B21 item 26: the worn gear, bag and warehouse as in-game slots (hover = the game tooltip, click = edit that piece)
      const slots = h("div", { class: "gw-gs-slots" });
      const loadGearUI = () => GW.gearUI ? Promise.resolve() : new Promise((res, rej) => { const s = document.createElement("script"); s.src = "/static/modules/_gear_ui.js"; s.onload = res; s.onerror = () => rej(new Error("cannot load the item slots")); document.body.append(s); });
      let whPage = 0;
      async function drawSlots() {
        GW.clear(slots);
        try { await loadGearUI(); await GW.catalog.load("item"); } catch (e) { slots.append(h("p", { class: "tt-bad" }, e.message)); return; }
        const at = loc => D.items.filter(i => i.location === loc);
        const byRow = it => Object.assign({ prop_id: it.item, sprite: it.sprite, name: it.name, slot_index: it.slot }, it.row);
        const worn = {}; for (const it of at(0)) if (it.slot < 12) worn[it.slot] = byRow(it);
        const pick = (loc, s) => { const it = D.items.find(x => x.location === loc && x.slot === s); if (it && it.guide_type) { current = it; load(); } else if (it) GW.status(it.why_not || "This item has no legal stats to choose.", "err"); };
        const mark = (loc) => (s, row) => row && current && current.location === loc && current.slot === s ? "gw-gearui-on" : row && !(D.items.find(x => x.location === loc && x.slot === s) || {}).guide_type ? "gw-gearui-off" : "";
        const UI = GW.gearUI;
        slots.append(h("div", { class: "gw-gearui-row" },
          UI.doll({ title: "Worn", rows: worn, mark: mark(0), onClick: s => pick(0, s) }),
          h("div", null, h("div", { class: "gw-gearui-dolltitle" }, "Kit bag (gear only)"), UI.bag({ rows: at(1).map(byRow), mark: mark(1), onClick: s => pick(1, s) })),
          at(3).length ? h("div", null, h("div", { class: "gw-gearui-dolltitle" }, "Warehouse (gear only)"), UI.bag({ rows: at(3).map(byRow), label: "Box", pages: 9, page: whPage, mark: mark(3), onClick: s => pick(3, s) })) : null));
      }
      el.append(h("p", { class: "gw-dim" }, "Pick a piece of gear. The menu offers only the attributes IGG's Enhancement guide (2011, acid=229) lists for that slot. New choices use the maximum supported Quartz strength. Existing lower strength is retained until changed; unlevelled stats have no level control. Every amount uses the current gear grade. Writing replaces all of the item's green attributes (the 5th is cleared). The character must be logged out."),
        slots, h("div", { class: "gw-gs-body" }, h("details", { class: "gw-gs-listbox" }, h("summary", null, "All gear as a list"), list), pane));
      let D = null, current = null;
      async function load() {
        D = await ctx.api("gear/" + ctx.cid);
        GW.clear(list);
        let last = null;
        for (const it of D.items) {
          if (it.location !== last) { last = it.location; list.append(h("h3", null, LOC[it.location] || "Location " + it.location)); }
          const attrs = [1, 2, 3, 4, 5].filter(i => it.row["attribute" + i] !== null && it.row["attribute" + i] !== undefined).length;
          list.append(h("button", { class: "gw-gs-row" + (current && current.location === it.location && current.slot === it.slot ? " on" : ""), type: "button", disabled: !it.guide_type || null,
            "data-help": it.why_not || ("IGG type: " + it.guide_type), onclick: () => { current = it; load(); } },
            GW.cell({ sprite: it.sprite, name: it.name, tipType: "item", tipId: it.item }), h("span", null, it.name, h("span", { class: "gw-dim" }, " · " + (it.guide_type || "not enhanceable") + " · " + attrs + " attr"))));
        }
        if (!D.items.length) list.append(h("p", { class: "gw-dim" }, "This character has no gear."));
        drawSlots();
        if (!current && ctx.params && ctx.params.get && ctx.params.get("slot") !== null) {   // opened from Worn gear presets: that worn piece
          const want = D.items.find(x => x.location === 0 && String(x.slot) === String(ctx.params.get("slot")) && x.guide_type);
          if (want) current = want;
        }
        if (current) { const again = D.items.find(x => x.location === current.location && x.slot === current.slot); if (again) { current = again; await open(again); } }
      }
      async function open(it) {
        const o = await ctx.api("options/" + it.item + "?grade=" + it.row.item_grade);
        GW.clear(pane).append(h("h2", null, it.name), h("p", { class: "gw-dim" }, (LOC[it.location] || "") + " · IGG type: " + o.guide_type + " · source: " + o.source));
        const picks = [];
        const cur = [1, 2, 3, 4].map(i => ({ id: it.row["attribute" + i], level: it.row["attribute_level" + i] }));
        // preselect what the item already has, when it is legal
        for (const c of cur) {
          if (c.id === null || c.id === undefined) continue;
          const opt = o.options.find(x => x.levels.some(l => l.id === c.id));
          if (opt && !picks.some(p => p.key === opt.key)) picks.push({ key: opt.key, level: (opt.levels.find(l => l.id === c.id) || {}).level || 1 });
        }
        const rows = h("div", { class: "gw-gs-picks" }), preview = h("div", { class: "gw-gs-preview" });
        function draw() {
          GW.clear(rows);
          for (let i = 0; i < 4; i++) {
            const p = picks[i];
            const sel = h("select", { "aria-label": "Stat " + (i + 1), onchange: e => { if (e.target.value === "") picks.splice(i, 1); else { const opt = o.options.find(x => x.key === e.target.value); picks[i] = { key: opt.key, level: opt.levels[opt.levels.length - 1].level }; } draw(); } },
              h("option", { value: "" }, "(none)"),
              o.options.filter(x => !picks.some((q, j) => j !== i && q.key === x.key)).map(x => h("option", { value: x.key, selected: p && p.key === x.key || null }, x.name + (x.levels.length > 1 ? "  (max Lv5)" : ""))));
            const opt = p && o.options.find(x => x.key === p.key);
            const max = opt && opt.levels[opt.levels.length-1];
            const lv = opt && opt.levels.length > 1 ? h("span", {class:"gw-gs-levels"},
              p.level !== max.level ? h("span",{class:"gw-dim"},"Current Lv"+p.level+" (retained) ") : null,
              h("button",{class:"gw-btnx small"+(p.level===max.level?" on":""),type:"button",onclick:()=>{p.level=max.level;draw();}},"Max Lv"+max.level)) : null;
            rows.append(h("div", { class: "gw-gs-pick" }, h("b", null, (i + 1) + ". "), sel, lv,
              opt ? h("span", { class: "gw-dim", "data-help": opt.note }, " G" + it.row.item_grade + " +" + fmtv((opt.levels.find(l => l.level === p.level) || {}).value, (opt.levels[0] || {}).percent) + (opt.in_client === false ? "  (this item's client list does not roll it)" : "")) : null));
            if (!p) break;            // fill in order
          }
          const after = Object.assign({}, it.row);
          for (let i = 1; i <= 5; i++) { after["attribute" + i] = null; after["attribute_level" + i] = null; }
          picks.forEach((p, i) => { const x = o.options.find(y => y.key === p.key); const lv = x && (x.levels.find(l => l.level === p.level) || x.levels[x.levels.length - 1]); if (lv) { after["attribute" + (i + 1)] = lv.id; after["attribute_level" + (i + 1)] = x.levels.length > 1 ? lv.level : null; } });
          const cells = GW.gearUI ? h("div", { class: "gw-gs-beforeafter" }, h("span", null, "Now "), GW.gearUI.cell(Object.assign({ sprite: it.sprite, name: it.name, prop_id: it.item }, it.row)),
            h("span", null, " → after writing "), GW.gearUI.cell(Object.assign({ sprite: it.sprite, name: it.name, prop_id: it.item }, after), { mark: "gw-gearui-changed" }),
            h("span", { class: "gw-dim" }, " (hover for the game tooltip)")) : null;
          GW.clear(preview).append(...[cells].filter(Boolean), h("h4", null, "Legal on " + o.guide_type), h("ul", null, o.options.map(x => h("li", { "data-help": x.note }, h("b", null, x.name), " · " + x.label + " · " + (x.levels.length > 1 ? "max Lv5 at G" + it.row.item_grade + " +" + fmtv(x.levels[4].value, x.levels[4].percent) : "G" + it.row.item_grade + " +" + fmtv(x.levels[0].value, x.levels[0].percent))))));
        }
        const write = async () => {
          if (!picks.length) { await GW.alert("Choose at least one stat.", "Nothing chosen"); return; }
          const names = picks.map(p => { const x = o.options.find(y => y.key === p.key); return x.name + (x.levels.length > 1 ? " Lv" + p.level : ""); }).join(", ");
          if (!await GW.confirm({ title: "Write " + picks.length + " stats?", message: it.name + " gets: " + names + ".\n\nIts current green attributes are replaced (the 5th is cleared). Undo is in the change log.", ok: "Write" })) return;
          try {
            const r = await ctx.api("apply/" + ctx.cid, { location: it.location, slot: it.slot, stats: picks, expected: it.row });
            GW.writeDone(r, "Stats written to " + it.name);
            if (r.warnings && r.warnings.length) GW.status("Written. " + r.warnings.join("; "), "ok");
            await load();
          } catch (e) { if (await GW.writeError(e) === "conflict") await load(); }
        };
        const quick = h("button", { class: "gw-btnx", type: "button", onclick: () => { picks.length = 0; for (const x of o.options.filter(x => x.levels.length > 1).concat(o.options.filter(x => x.levels.length === 1))) { if (picks.length < 4) picks.push({ key: x.key, level: x.levels[x.levels.length - 1].level }); } draw(); } }, "Quick: best 4 (levelled ones at Lv5 first)");
        pane.append(rows, h("div", { class: "gw-inv-btns" }, quick, h("button", { class: "gw-btnx", type: "button", disabled: !D.writable || null, onclick: write }, "Write stats")), preview);
        draw();
      }
      try { await load(); } catch (e) { GW.clear(pane).append(h("p", { class: "tt-bad" }, e.message)); }
    }
  });
})();
