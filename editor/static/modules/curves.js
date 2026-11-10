/* static/modules/curves.js - Monster stat curve: choose maps, see level vs HP / attack / defence / ratings, drag a multiplier
   curve's control points, preview, write. Backend: modules/curves.py (same formula and curve maths, authoritative). */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const NS = "http://www.w3.org/2000/svg";
  const svg = (tag, a) => { const e = document.createElementNS(NS, tag); for (const k in a || {}) e.setAttribute(k, a[k]); return e; };
  const GROUPS = { hp: "HP", attack: "Attack (physical + magic)", defence: "Defence (physical + magic)", rating: "Hit / dodge / crit / crit resistance" };
  const STAT_OF = { hp: "hp", attack: "physical_attack", defence: "physical_defense", rating: "hit" };
  const INT_MAX = 2147483647;

  // MonsterCombatProfileCatalog.Resolve (same as modules/curves.py formula)
  function formula(L, elite, boss) {
    L = Math.max(1, Math.min(10000, L | 0));
    const bp = boss ? 13000 : elite ? 11500 : 10000, s = v => Math.max(0, Math.min(INT_MAX, Math.floor(v * bp / 10000)));
    return { physical_attack: s(21 + 3 * L + Math.floor(L / 3)), magic_attack: s(23 + 3 * L + Math.floor(L / 2)),
      physical_defense: s(10 + 6 * L + Math.floor(L * L / 10)), magic_defense: s(10 + 5 * L + Math.floor(L * L / 12)),
      hit: s(100 + 20 * L), dodge: s(50 + 12 * L), critical: s(25 + 8 * L), critical_resistance: s(25 + 10 * L) };
  }
  function cv(points, L) {
    const p = points.slice().sort((a, b) => a[0] - b[0]);
    if (!p.length) return 1;
    if (L <= p[0][0]) return p[0][1];
    for (let i = 1; i < p.length; i++) if (L <= p[i][0]) { const [x0, y0] = p[i - 1], [x1, y1] = p[i]; return x1 === x0 ? y1 : y0 + (y1 - y0) * (L - x0) / (x1 - x0); }
    return p[p.length - 1][1];
  }

  GW.module("curves", {
    async mount(el, ctx) {
      const M = await ctx.api("maps");
      let sel = new Set(), D = null, group = "hp", anchor = "middle";
      let curves = { hp: [[1, 1], [200, 1]], attack: [[1, 1], [200, 1]], defence: [[1, 1], [200, 1]], rating: [[1, 1], [200, 1]] };
      const mapBox = h("div", { class: "gw-cv-maps" }), presetBox = h("div", { class: "gw-cv-presets" });
      const curveEl = h("div", { class: "gw-cv-curve" }), plotEl = h("div", { class: "gw-cv-plot" }), info = h("div", { class: "gw-cv-info" });
      const groupSel = h("select", { "aria-label": "Curve", onchange: e => { group = e.target.value; drawCurve(); drawPlot(); } }, Object.entries(GROUPS).map(([k, v]) => h("option", { value: k }, v)));
      const anchorSel = h("select", { "aria-label": "Level used for a template with several levels", onchange: e => { anchor = e.target.value; } },
        h("option", { value: "lowest" }, "lowest level"), h("option", { value: "middle", selected: true }, "middle level"), h("option", { value: "highest" }, "highest level"));
      el.append(
        h("p", { class: "gw-dim" }, "1. Tick maps (or load a preset). 2. Pick a curve and drag its points (double-click the curve to add a point, double-click a point to remove it). 3. Preview, then Write. HP is written per spawn; attack / defence / ratings per monster type and map (the server ignores template HP). Level is never changed. Everything applies after a gw251 restart. ", GW.appliesChip("after_restart")),
        h("h3", null, "Presets"), presetBox, h("h3", null, "Maps"), mapBox,
        h("div", { class: "gw-cv-bar" }, h("label", null, "Curve ", groupSel), h("label", { "data-help": "One monster type can spawn at several levels on one map; its attack/defence row is computed at this level." }, "Several levels: use the ", anchorSel),
          h("button", { class: "gw-btnx", type: "button", onclick: previewNow }, "Preview"), h("button", { class: "gw-btnx", type: "button", onclick: write }, "Write")),
        h("div", { class: "gw-cv-graphs" }, curveEl, plotEl), info);

      for (const p of M.presets) presetBox.append(h("div", { class: "gw-cv-preset" + (p.generated ? " gen" : "") }, h("b", null, p.title), p.generated ? h("span", { class: "gw-chip never" }, "GENERATED") : null,
        h("p", { class: "gw-dim" }, p.why), h("button", { class: "gw-btnx small", type: "button", onclick: async () => {
          curves = JSON.parse(JSON.stringify(p.curves)); if (p.maps.length) sel = new Set(p.maps); drawMaps(); await reload(); } }, "Load")));
      function drawMaps() {
        GW.clear(mapBox);
        for (const m of M.maps) mapBox.append(h("label", { class: "gw-cv-map" }, h("input", { type: "checkbox", checked: sel.has(m.map_id) || null, onchange: async e => { if (e.target.checked) sel.add(m.map_id); else sel.delete(m.map_id); await reload(); } }),
          " " + m.name + " ", h("span", { class: "gw-dim" }, "(" + m.map_id + ", " + (m.published + m.gm) + ")")));
      }
      async function reload() {
        if (!sel.size) { D = null; drawCurve(); drawPlot(); return; }
        D = await ctx.api("load", { maps: Array.from(sel) });
        drawCurve(); drawPlot();
      }
      // ---- the multiplier curve (x: level 1-200, y: x0.5 - x5)
      function drawCurve() {
        const W = 460, H = 260, pad = 36, X = L => pad + (L - 1) / 199 * (W - 2 * pad), Y = m => H - pad - (Math.log(m) - Math.log(0.5)) / (Math.log(5) - Math.log(0.5)) * (H - 2 * pad);
        const iX = x => Math.round(1 + (x - pad) / (W - 2 * pad) * 199), iY = y => Math.exp(Math.log(0.5) + (H - pad - y) / (H - 2 * pad) * (Math.log(5) - Math.log(0.5)));
        const s = svg("svg", { viewBox: "0 0 " + W + " " + H, class: "gw-cv-svg", role: "img", "aria-label": GROUPS[group] + " multiplier curve" });
        for (const m of [0.5, 1, 2, 3, 4, 5]) { s.append(svg("line", { x1: pad, x2: W - pad, y1: Y(m), y2: Y(m), class: m === 1 ? "axis1" : "grid" })); const t = svg("text", { x: 4, y: Y(m) + 4, class: "lbl" }); t.textContent = "x" + m; s.append(t); }
        for (const L of [1, 50, 100, 150, 200]) { s.append(svg("line", { x1: X(L), x2: X(L), y1: pad, y2: H - pad, class: "grid" })); const t = svg("text", { x: X(L) - 8, y: H - 12, class: "lbl" }); t.textContent = "Lv" + L; s.append(t); }
        const pts = curves[group].slice().sort((a, b) => a[0] - b[0]);
        let d = ""; for (let L = 1; L <= 200; L += 1) d += (L === 1 ? "M" : "L") + X(L).toFixed(1) + "," + Y(Math.max(0.5, Math.min(5, cv(pts, L)))).toFixed(1);
        const path = svg("path", { d, class: "curve" }); s.append(path);
        s.addEventListener("dblclick", e => { if (e.target.tagName === "circle") return; const r = s.getBoundingClientRect(), x = (e.clientX - r.left) * W / r.width, y = (e.clientY - r.top) * H / r.height; curves[group].push([Math.max(1, Math.min(200, iX(x))), Math.round(iY(y) * 100) / 100]); drawCurve(); drawPlot(); });
        curves[group].forEach((p, i) => {
          const c = svg("circle", { cx: X(p[0]), cy: Y(Math.max(0.5, Math.min(5, p[1]))), r: 7, class: "pt", tabindex: 0 });
          const tip = svg("title"); tip.textContent = "Lv" + p[0] + " x" + p[1].toFixed(2) + " (drag; double-click removes; arrow keys move)"; c.append(tip);
          c.addEventListener("pointerdown", ev => {
            ev.preventDefault(); c.setPointerCapture(ev.pointerId);
            const move = e2 => { const r = s.getBoundingClientRect(); p[0] = Math.max(1, Math.min(200, iX((e2.clientX - r.left) * W / r.width))); p[1] = Math.round(Math.max(0.5, Math.min(5, iY((e2.clientY - r.top) * H / r.height))) * 100) / 100;
              c.setAttribute("cx", X(p[0])); c.setAttribute("cy", Y(p[1])); tip.textContent = "Lv" + p[0] + " x" + p[1].toFixed(2); legend(); };
            const up = () => { c.removeEventListener("pointermove", move); c.removeEventListener("pointerup", up); drawCurve(); drawPlot(); };
            c.addEventListener("pointermove", move); c.addEventListener("pointerup", up);
          });
          c.addEventListener("dblclick", () => { if (curves[group].length > 1) { curves[group].splice(i, 1); drawCurve(); drawPlot(); } });
          c.addEventListener("keydown", e => { const k = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, .05], ArrowDown: [0, -.05] }[e.key]; if (!k) return; e.preventDefault(); p[0] = Math.max(1, Math.min(200, p[0] + k[0])); p[1] = Math.round(Math.max(0.5, Math.min(5, p[1] + k[1])) * 100) / 100; drawCurve(); drawPlot(); });
          s.append(c);
        });
        const leg = h("div", { class: "gw-cv-leg" });
        function legend() { GW.clear(leg).append(...[100, 130, 140, 150, 160, 170, 180, 190, 200].map(L => h("span", { class: "gw-chip" }, "Lv" + L + " x" + cv(curves[group], L).toFixed(2)))); }
        legend();
        const ptsTxt = h("input", { class: "gw-cv-pts", value: JSON.stringify(curves[group]), "aria-label": "Control points as JSON", onchange: e => { try { const v = JSON.parse(e.target.value); if (Array.isArray(v) && v.length) { curves[group] = v.map(x => [Number(x[0]), Number(x[1])]); drawCurve(); drawPlot(); } } catch (_) { GW.status("points must be [[level, multiplier], ...]", "err"); } } });
        GW.clear(curveEl).append(h("h4", null, GROUPS[group] + ": multiplier"), s, leg, h("label", { class: "gw-dim" }, "Points ", ptsTxt));
      }
      // ---- level vs stat: grey = now (base), gold = after the curve
      function drawPlot() {
        GW.clear(plotEl);
        if (!D) { plotEl.append(h("p", { class: "gw-dim" }, "Tick a map to plot its monsters.")); return; }
        const W = 520, H = 300, pad = 46;
        const pts = [];
        const tmpl = {}; for (const t of D.templates) tmpl[t.map + "|" + t.template] = t;
        for (const s of D.spawns) {
          let before, after;
          if (group === "hp") { before = s.base_hp; after = Math.round(s.base_hp * cv(curves.hp, s.level)); }
          else { const f = formula(s.level, s.elite, s.boss)[STAT_OF[group]]; before = f; after = Math.round(f * cv(curves[group], s.level)); }
          pts.push({ L: s.level, before, after, s });
        }
        const lvls = pts.map(p => p.L), vals = pts.flatMap(p => [p.before, p.after]).filter(v => v > 0);
        const lmin = Math.min(...lvls) - 2, lmax = Math.max(...lvls) + 2, vmin = Math.max(1, Math.min(...vals) * 0.8), vmax = Math.max(...vals) * 1.15;
        const X = L => pad + (L - lmin) / Math.max(1, lmax - lmin) * (W - 2 * pad), Y = v => H - pad - (Math.log(v) - Math.log(vmin)) / (Math.log(vmax) - Math.log(vmin)) * (H - 2 * pad);
        const s = svg("svg", { viewBox: "0 0 " + W + " " + H, class: "gw-cv-svg", role: "img", "aria-label": "Level against " + GROUPS[group] });
        for (let i = 0; i <= 4; i++) { const v = Math.exp(Math.log(vmin) + i / 4 * (Math.log(vmax) - Math.log(vmin))); s.append(svg("line", { x1: pad, x2: W - pad, y1: Y(v), y2: Y(v), class: "grid" })); const t = svg("text", { x: 2, y: Y(v) + 4, class: "lbl" }); t.textContent = GW.fmt(Math.round(v)); s.append(t); }
        for (let i = 0; i <= 4; i++) { const L = Math.round(lmin + i / 4 * (lmax - lmin)); const t = svg("text", { x: X(L) - 10, y: H - 10, class: "lbl" }); t.textContent = "Lv" + L; s.append(t); }
        for (const p of pts) {
          const cls = p.s.boss ? " boss" : p.s.elite ? " elite" : "";
          const a = svg("circle", { cx: X(p.L), cy: Y(Math.max(vmin, p.before)), r: 3, class: "before" + cls });
          const b = svg("circle", { cx: X(p.L) + 2, cy: Y(Math.max(vmin, p.after)), r: 3.5, class: "after" + cls });
          const t = svg("title"); t.textContent = p.s.name + " (" + p.s.template + ") Lv" + p.L + (p.s.boss ? " boss" : p.s.elite ? " elite" : "") + ": " + GW.fmt(p.before) + " -> " + GW.fmt(p.after); b.append(t);
          s.append(a, b);
        }
        plotEl.append(h("h4", null, "Level vs " + GROUPS[group] + " (" + D.spawns.length + " spawns; grey = now, gold = with the curve; big = elite, red = boss)"), s);
        const multi = D.templates.filter(t => t.level_list.length > 1);
        GW.clear(info).append(...[multi.length ? h("details", null, h("summary", null, multi.length + " monster types spawn at several levels on one map: their attack / defence row uses the " + anchor + " level"),
          h("ul", null, multi.slice(0, 60).map(t => h("li", null, t.name + " on map " + t.map + ": levels " + t.level_list.join(", "))))) : null,
          D.templates.some(t => t.override && t.override_by !== "gwadmin-curve") ? h("p", { class: "tt-bad" }, "Some monster types already have stat rows written by someone else (World > GM monsters); the curve keeps those and lists them in the preview.") : null].filter(Boolean));
      }
      async function previewNow() {
        if (!sel.size) { GW.alert("Tick at least one map.", "Preview"); return null; }
        try {
          const r = await ctx.api("preview", { maps: Array.from(sel), curves, anchor });
          GW.clear(info).append(...[h("p", null, h("b", null, r.hp_rows + " spawn HP values and " + r.attr_rows + " combat-stat rows would be written.")),
            r.kept_foreign.length ? h("p", { class: "tt-bad" }, "Kept (written by someone else): " + r.kept_foreign.map(k => k.name + " map " + k.map + " (" + k.by + ")").join(", ")) : null,
            h("table", { class: "gw-w-table" }, h("tr", null, ["Map", "Monster", "Level used", "Phys att", "Mag att", "Phys def", "Mag def"].map(x => h("th", null, x))),
              r.sample.map(x => h("tr", null, h("td", null, x.map), h("td", null, x.name + (x.boss ? " (boss)" : x.elite ? " (elite)" : "")), h("td", null, x.level + (x.levels.length > 1 ? " of " + x.levels.join("/") : "")),
                ...["physical_attack", "magic_attack", "physical_defense", "magic_defense"].map(c => h("td", null, GW.fmt(x.base[c]) + (x.values && x.values[c] !== null ? " → " + GW.fmt(x.values[c]) : ""))))))].filter(Boolean));
          return r;
        } catch (e) { await GW.writeError(e); return null; }
      }
      async function write() {
        const r = await previewNow(); if (!r) return;
        if (!await GW.confirm({ title: "Write the curve?", message: r.hp_rows + " HP values and " + r.attr_rows + " combat-stat rows on " + sel.size + " map(s). Monster levels do not change. It applies after the next gw251 restart; Undo is in the change log.", ok: "Write" })) return;
        try { GW.writeDone(await ctx.api("apply", { maps: Array.from(sel), curves, anchor, version: D.version }), "Monster curve written (after restart)"); await reload(); }
        catch (e) { if (await GW.writeError(e) === "conflict") await reload(); }
      }
      drawMaps(); drawCurve(); drawPlot();
    }
  });
})();
