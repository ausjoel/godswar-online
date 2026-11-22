/* static/modules/monsters.js - Monster progression (wave 4 D items 1 and 4). Backend: modules/monsters.py.
   Every map with monsters, grouped by section (regular maps in level order, instances, events, battlefields...), on one
   level axis 1-200: each monster is a mark at its spawn levels (size and colour by rank). Click a monster for its card. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const NS = "http://www.w3.org/2000/svg";
  const svg = (tag, a) => { const e = document.createElementNS(NS, tag); for (const k in a || {}) e.setAttribute(k, a[k]); return e; };
  const RANK = { normal: "Normal", elite: "Elite", boss: "Boss" };
  // the shared label helper is in static/shared/monster_label.js; load it here too until the shell loads it everywhere
  function needLabel() {
    if (GW.monsterLabel) return Promise.resolve();
    return new Promise(res => { const s = document.createElement("script"); s.src = "/static/shared/monster_label.js"; s.onload = res; s.onerror = res; document.head.append(s); });
  }

  GW.module("monsters", {
    async mount(el, ctx) {
      await needLabel();
      const D = await ctx.api("progression");
      let section = "all", q = "", ranks = new Set(["normal", "elite", "boss"]), noLoot = false;
      const card = h("div", { class: "gw-mp-card" });
      const chart = h("div", { class: "gw-mp-chart" });
      const tabs = h("div", { class: "gw-mp-tabs", role: "tablist" });
      const search = h("input", { type: "search", placeholder: "Find a monster (name or ID)", "aria-label": "Find a monster", oninput: e => { q = e.target.value.trim().toLowerCase(); draw(); } });
      const rankBox = h("span", { class: "gw-mp-ranks" }, Object.keys(RANK).map(r => h("label", null,
        h("input", { type: "checkbox", checked: true, onchange: e => { if (e.target.checked) ranks.add(r); else ranks.delete(r); draw(); } }), " " + RANK[r])));
      const lootBox = h("label", { "data-help": "Show only monsters whose kills drop nothing (no loot table for that template or template@map)." },
        h("input", { type: "checkbox", onchange: e => { noLoot = e.target.checked; draw(); } }), " no loot table only");
      el.append(
        h("p", { class: "gw-dim" }, "Every monster that spawns, by section and map, on the level axis. Regular maps are ordered by their median level (the game's progression); instances and events are their own sections. Dot = a spawn level; big = elite, red = boss; hollow = no loot table; dashed = spawned in code (the map's typical level). Click a monster for details. " +
          (D.db ? "" : "The server database is not reachable: only the client catalogue is shown. ") + D.unspawned + " monster templates have no spawn and are not drawn."),
        tabs, h("div", { class: "gw-mp-bar" }, search, rankBox, lootBox), h("div", { class: "gw-mp-cols" }, chart, card));
      const all = [{ section: "all", title: "All sections" }].concat(D.groups);
      for (const g of all) tabs.append(h("button", { type: "button", role: "tab", class: "gw-btnx small", "data-s": g.section, onclick: () => { section = g.section; draw(); } },
        g.title + (g.maps ? " (" + g.maps.length + ")" : "")));
      showCard(null);

      function visible(mon) {
        if (!ranks.has(mon.rank)) return false;
        if (noLoot && mon.loot) return false;
        return !q || (mon.name + " " + mon.key).toLowerCase().includes(q);
      }
      function draw() {
        for (const b of tabs.children) b.classList.toggle("on", b.getAttribute("data-s") === section);
        GW.clear(chart);
        const groups = D.groups.filter(g => section === "all" || g.section === section);
        const rows = [];
        for (const g of groups) {
          const maps = g.maps.map(m => Object.assign({}, m, { monsters: m.monsters.filter(visible) })).filter(m => m.monsters.length);
          if (!maps.length) continue;
          rows.push({ head: g }); for (const m of maps) rows.push({ map: m });
        }
        if (!rows.length) { chart.append(h("p", { class: "gw-dim" }, "No monsters match.")); return; }
        const W = 900, left = 190, right = 16, rowH = 22, headH = 26, top = 26;
        let H = top + 8; for (const r of rows) H += r.head ? headH : rowH;
        const X = L => left + (Math.max(1, Math.min(200, L)) - 1) / 199 * (W - left - right);
        const s = svg("svg", { viewBox: "0 0 " + W + " " + H, class: "gw-mp-svg", role: "img", "aria-label": "Monster levels by map" });
        for (const L of [1, 20, 40, 60, 80, 100, 120, 140, 160, 180, 200]) {
          s.append(svg("line", { x1: X(L), x2: X(L), y1: top - 6, y2: H - 4, class: "grid" }));
          const t = svg("text", { x: X(L) - 8, y: 14, class: "lbl" }); t.textContent = "Lv" + L; s.append(t);
        }
        let y = top;
        for (const r of rows) {
          if (r.head) {
            const t = svg("text", { x: 4, y: y + 17, class: "sec" }); t.textContent = r.head.title; s.append(t);
            s.append(svg("line", { x1: 0, x2: W, y1: y + headH - 2, y2: y + headH - 2, class: "secline" }));
            y += headH; continue;
          }
          const m = r.map, cy = y + rowH / 2;
          const t = svg("text", { x: 8, y: cy + 4, class: "map" }); t.textContent = m.name + (m.level_min !== null ? " (" + m.level_min + (m.level_max !== m.level_min ? "-" + m.level_max : "") + ")" : "");
          const tt = svg("title"); tt.textContent = "Map " + m.id + " · " + m.monsters.length + " monster types" + (m.no_loot ? " · " + m.no_loot + " without a loot table" : ""); t.append(tt); s.append(t);
          if (m.level_min !== null) s.append(svg("line", { x1: X(m.level_min), x2: X(m.level_max), y1: cy, y2: cy, class: "band" }));
          for (const mon of m.monsters) {
            for (const L of mon.levels) {
              const c = svg("circle", { cx: X(L), cy: cy + (mon.rank === "boss" ? 0 : mon.rank === "elite" ? -3 : 3), r: mon.rank === "boss" ? 6 : mon.rank === "elite" ? 4.5 : 3,
                class: "mon " + mon.rank + (mon.loot ? "" : " noloot") + (mon.guessed ? " guessed" : ""), tabindex: 0 });
              const ti = svg("title"); ti.textContent = mon.name + " (" + mon.key + ") Lv" + (mon.guessed ? "~" : "") + L + " · " + RANK[mon.rank] + (mon.loot ? "" : " · no loot table") + (mon.guessed ? " · spawned in code: level is the map's typical level" : ""); c.append(ti);
              c.addEventListener("click", () => showCard(mon, m));
              c.addEventListener("keydown", e => { if (e.key === "Enter") showCard(mon, m); });
              s.append(c);
            }
          }
          y += rowH;
        }
        chart.append(s);
        // the list under the chart: icon + in-game name + ID, per map
        const list = h("div", { class: "gw-mp-list" });
        for (const r of rows) {
          if (r.head) { list.append(h("h3", null, r.head.title)); continue; }
          list.append(h("h4", null, r.map.name + " ", h("span", { class: "gw-dim" }, "map " + r.map.id)),
            h("div", { class: "gw-mp-mons" }, r.map.monsters.map(mon => h("button", { type: "button", class: "gw-mp-mon " + mon.rank + (mon.loot ? "" : " noloot"), onclick: () => showCard(mon, r.map) },
              GW.monsterLabel ? GW.monsterLabel(mon.key, { name: mon.name, map: r.map.id, compact: true }) : mon.name,
              h("span", { class: "gw-dim" }, " Lv " + (mon.guessed ? "~" : "") + (mon.levels.length > 1 ? mon.levels[0] + "-" + mon.levels[mon.levels.length - 1] : mon.levels[0]))))));
        }
        chart.append(list);
      }
      function showCard(mon, map) {
        GW.clear(card);
        if (!mon) { card.append(h("p", { class: "gw-dim" }, "Click a monster.")); return; }
        const img = mon.thumb ? h("img", { src: mon.thumb, alt: "", class: "gw-mp-big" }) : h("div", { class: "gw-mp-big none" }, "no picture yet");
        card.append(...[img, h("h3", null, mon.name), h("div", { class: "gw-dim" }, mon.key),
          h("p", null, RANK[mon.rank] + " · " + map.name + " (" + map.section_title + ") · Lv " + (mon.guessed ? "~" : "") + mon.levels.join(", ") + (mon.guessed ? " (spawned in code; the map's typical level)" : "")),
          mon.tier !== mon.rank ? h("p", { class: "gw-tier-warning" }, "Drop/respawn tier pages treat it as " + mon.tier + " (its ID); the game data ranks it " + mon.rank + ".") : null,
          h("p", null, mon.loot ? "Has a loot table." : h("b", null, "No loot table: kills drop nothing (see server/tools/w4-drops for the generated tables).")),
          h("p", null, h("a", { href: "#/world", class: "gw-btnx small" }, "Loot and GM monsters (World)"))].filter(Boolean));
      }
      draw();
    }
  });
})();
