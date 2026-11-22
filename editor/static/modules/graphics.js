/* static/modules/graphics.js - Graphics: 3D models (monsters, NPCs, pets, mounts), maps, every client picture, effects.
   Backend: modules/graphics.py. 3D: static/graphics/viewer.js (three.js r186, vendored). Shared helper: static/graphics/gw-art.js. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const KINDS = [["monster", "Monsters"], ["npc", "NPCs"], ["pet", "Pets"], ["ride", "Mounts"]];
  const TABS = [["models", "Models"], ["maps", "Maps"], ["textures", "Textures & files"], ["effects", "Effects"]];
  const BLEND = ["ZERO", "ONE", "SRCCOLOR", "INVSRCCOLOR", "SRCALPHA", "INVSRCALPHA", "DESTALPHA", "INVDESTALPHA", "DESTCOLOR", "INVDESTCOLOR", "BLENDFACTOR", "INVBLENDFACTOR"];
  const KEEP = { tab: "models", kind: "monster", q: "", dir: "", fx: "" };
  let viewer = null, alive = 0;

  function loadArt() {
    if (window.GWArt) return window.GWArt.load();
    return new Promise((ok, no) => { const s = document.createElement("script"); s.src = "/static/graphics/gw-art.js"; s.onload = () => window.GWArt.load().then(ok); s.onerror = () => no(new Error("gw-art.js missing")); document.head.append(s); });
  }
  const viewerMod = () => import("/static/graphics/viewer.js");
  function dropViewer() { if (viewer) { try { viewer.dispose(); } catch (e) { /* gone */ } viewer = null; } }
  const b64ToBuffer = b64 => { const bin = atob(b64), out = new Uint8Array(bin.length); for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i); return out.buffer; };
  const kb = n => n >= 1048576 ? (n / 1048576).toFixed(1) + " MB" : Math.max(1, Math.round(n / 1024)) + " KB";
  const btn = (label, onclick, o) => h("button", Object.assign({ class: "gw-btnx" + (o && o.small ? " small" : "") + (o && o.on ? " on" : ""), type: "button", onclick }, o && o.attrs || {}), label);
  function download(name, dataUrl) { const a = h("a", { href: dataUrl, download: name }); document.body.append(a); a.click(); a.remove(); }

  GW.module("graphics", {
    async mount(el, ctx) {
      const my = ++alive;
      const tabs = h("div", { class: "gx-tabs", role: "tablist" });
      const body = h("div", { class: "gx-body" });
      const info = h("div", { class: "gx-info gw-dim" });
      el.append(info, tabs, body);
      const p = (ctx.route && ctx.route[0]) || KEEP.tab;
      if (TABS.some(t => t[0] === p)) KEEP.tab = p;
      let ov = null;
      try { [ov] = await Promise.all([ctx.api("overview"), loadArt()]); } catch (e) { info.textContent = "Could not read the graphics index: " + e.message; }
      if (my !== alive) return;
      if (ov) info.replaceChildren(
        h("span", null, (ov.counts.monster || 0) + " monster identities, " + (ov.counts.npc || 0) + " NPCs, " + (ov.counts.pet || 0) + " pet models, " + (ov.counts.ride || 0) + " mount models, " + (ov.counts.maps || 0) + " maps. "),
        h("span", { "data-help": "3D models and full-size pictures live outside git (GW_GRAPHICS_OUT); the client folder (GW_CLIENT) is needed for the texture browser and effects. " + "Output: " + ov.out.path + " · Client: " + ov.client.path },
          ov.out.models ? "3D models: yes" : "3D models: not on this server (thumbnails only)", " · ",
          ov.source === "client" ? "textures and effects: read from the game client on this machine"
            : ov.source === "prebuilt" ? "textures and effects: prebuilt copy" + (ov.web && ov.web.built ? " of " + ov.web.built : "") + (ov.web && ov.web.counts ? " (" + ov.web.counts.pictures + " pictures, " + ov.web.counts.effects + " effects)" : "")
            : "textures and effects: not on this server (upload graphics-out/web)"));
      const draw = () => {
        dropViewer();
        GW.clear(tabs).append(...TABS.map(([k, t]) => h("button", { class: "gw-tabx" + (KEEP.tab === k ? " on" : ""), type: "button", role: "tab", "aria-selected": KEEP.tab === k ? "true" : "false", onclick: () => { KEEP.tab = k; history.replaceState(null, "", "#/graphics/" + k); draw(); } }, t)));
        GW.clear(body);
        ({ models: tabModels, maps: tabMaps, textures: tabTextures, effects: tabEffects })[KEEP.tab](body, ctx, my);
      };
      draw();
    },
    unmount() { alive++; dropViewer(); },
  });

  // ============================================================================================ models
  function tabModels(body, ctx, my) {
    const A = window.GWArt, M = A.models || {};
    const kindSel = h("div", { class: "gx-kinds" });
    const search = h("input", { type: "search", placeholder: "search name or key ...", value: KEEP.q, "aria-label": "Search models", oninput: e => { KEEP.q = e.target.value.toLowerCase(); list(); } });
    const rankSel = h("select", { "aria-label": "Rank", onchange: list }, ["all", "normal", "elite", "boss"].map(r => h("option", { value: r }, r === "all" ? "every rank" : r)));
    const grid = h("div", { class: "gx-grid" });
    const pane = h("div", { class: "gx-pane" }, h("p", { class: "gw-dim" }, "Pick a model on the left."));
    const AS = M.avatar_set || { in_game: "old", evidence: "" };
    const setSel = h("div", { class: "gx-kinds", role: "group", "aria-label": "Graphics set", "data-help": "The client holds two sets of character graphics: the old one (Characters/) and the new one (Characters_New/). Dressed NPCs can be shown from either. The game itself draws the " + AS.in_game + " set (" + AS.evidence + "). Monsters are different: old and redrawn 'new' monster models are both in use, each monster has the one its map names; open a monster to see its family's other models." });
    let current = null;
    function sets() {
      GW.clear(setSel).append(h("span", { class: "gw-dim" }, "NPC graphics: "), ...[["game", "As in the game (" + AS.in_game + ")"], ["old", "Old"], ["new", "New"]].map(([k, t]) =>
        btn(t, () => { A.setPref(k); sets(); list(); if (current) open(current[0], current[1]); }, { small: true, on: A.pref === k, attrs: { "aria-pressed": A.pref === k ? "true" : "false" } })));
      setSel.style.display = KEEP.kind === "npc" ? "" : "none";
    }
    body.append(h("div", { class: "gx-bar" }, kindSel, search, rankSel, setSel), h("div", { class: "gx-cols" }, grid, pane));
    function kinds() { GW.clear(kindSel).append(...KINDS.map(([k, t]) => btn(t + " (" + Object.keys(M[k] || {}).length + ")", () => { KEEP.kind = k; current = null; kinds(); list(); }, { small: true, on: KEEP.kind === k }))); rankSel.style.display = KEEP.kind === "monster" ? "" : "none"; sets(); }
    function list() {
      const recs = M[KEEP.kind] || {};
      const rank = rankSel.value;
      const rows = Object.entries(recs).filter(([k, r]) => (!KEEP.q || (k + " " + (r.name || "")).toLowerCase().includes(KEEP.q)) && (KEEP.kind !== "monster" || rank === "all" || r.rank === rank))
        .sort((a, b) => String(a[1].name || a[0]).localeCompare(String(b[1].name || b[0])));
      GW.clear(grid);
      if (!rows.length) { grid.append(h("p", { class: "gw-dim" }, "Nothing matches.")); return; }
      const frag = document.createDocumentFragment();
      for (const [k, r] of rows.slice(0, 1500)) {
        frag.append(h("button", { class: "gx-card" + (r.glb ? "" : " none"), type: "button", title: k, onclick: () => open(k, r) },
          A.thumb(KEEP.kind, k, { size: 48 }), h("span", { class: "nm" }, r.name || k), h("span", { class: "sub" }, KEEP.kind === "monster" ? (r.rank || "") + " · " + k : KEEP.kind === "npc" ? (r.scene || "") + " · " + k + (r.alt && A.pref !== "game" && A.pref !== r.set ? " · " + A.pref + " set" : "") : k)));
      }
      grid.append(frag);
      if (rows.length > 1500) grid.append(h("p", { class: "gw-dim" }, (rows.length - 1500) + " more: narrow the search."));
    }
    // what is shown for a record: the game's own model, or (dressed NPCs) the same outfit from the other avatar set,
    // or (monsters) another model of the same family picked by hand (other = {glb, model, set})
    async function open(key, rec, other) {
      dropViewer();
      current = [key, rec];
      const shown = other ? { glb: other.glb, set: other.set, game: other.glb === rec.glb } : (A.pick(KEEP.kind, key) || { glb: rec.glb, game: true });
      const same = Object.entries(M[KEEP.kind] || {}).filter(([k, r]) => r.glb && r.glb === rec.glb && k !== key);
      const f = (M.files || {})[shown.glb] || {};
      const head = h("div", { class: "gx-head" }, h("h2", null, rec.name || key), h("div", { class: "gw-dim" }, key + (rec.rank ? " · " + rec.rank : "") + (rec.scale ? " · scale " + rec.scale + " in game" : "") + (rec.kind ? " · " + (rec.kind === "avatar" ? "dressed character model (avatar parts)" : "own model") : "")));
      GW.clear(pane).append(head);
      if (!rec.glb) { pane.append(h("p", { class: "tt-bad" }, rec.missing ? "Its model file " + rec.missing + " is not in the client." : "No model.")); return; }
      pane.append(h("div", { class: "gw-dim" }, shown.glb + " · " + (f.triangles || "?") + " triangles · " + (f.clips || 0) + " animations · " + (f.bytes ? kb(f.bytes) : "")));
      // ---- old / new graphics
      if (rec.kind === "avatar" && rec.alt) {
        pane.append(h("div", { class: "gx-same gx-set" }, h("b", null, "In the game: " + rec.set + " graphics. "),
          [rec.set].concat(Object.keys(rec.alt)).map(st => btn((st === "old" ? "Old graphics (Characters/)" : "New graphics (Characters_New/)") + (st === rec.set ? " · in game" : ""), () => open(key, rec, { glb: st === rec.set ? rec.glb : rec.alt[st].glb, set: st }), { small: true, on: shown.set === st, attrs: { "aria-pressed": shown.set === st ? "true" : "false" } })),
          h("span", { class: "gw-dim", "data-help": AS.evidence }, shown.game ? " This is what players see." : " Not what players see: the client would need config.ini [SYSTEM] Avatar=2 for this set.")));
      } else if (rec.set && rec.family && (M.families || {})[rec.family]) {
        const fam = M.families[rec.family], otherSet = rec.set === "new" ? "old" : "new";
        pane.append(h("div", { class: "gx-same gx-set" }, h("b", null, "In the game: " + (rec.model || rec.glb).replace(/\.jcs$/i, "") + ", one of the " + (rec.set === "new" ? "redrawn (new)" : "original (old)") + " " + rec.family.replace(/^monster_/, "") + " models. "),
          btn("In game", () => open(key, rec), { small: true, on: shown.game }),
          h("span", { class: "gw-dim" }, " " + (otherSet === "new" ? "New" : "Old") + " models of the same family: "),
          fam[otherSet].map(o => h("button", { class: "gx-alt" + (shown.glb === o.glb ? " on" : ""), type: "button", title: o.model, "aria-label": o.model, onclick: () => open(key, rec, { glb: o.glb, set: otherSet }) }, altTile(o.t))),
          h("span", { class: "gw-dim", "data-help": "Old and new monster models are not a global switch: each monster shows the model its map's Monster.ini names. The other set is shown here for comparison only." }, shown.game ? "" : " Showing " + shown.glb.replace(/^.*\//, "").replace(/\.glb$/, "") + " (comparison only).")));
      }
      if (same.length) pane.append(h("div", { class: "gx-same" }, "Same model: ", same.slice(0, 12).map(([k, r]) => h("button", { class: "gw-btnx small", type: "button", onclick: () => open(k, r) }, r.name || k)), same.length > 12 ? " +" + (same.length - 12) : null));
      const stage = h("div", { class: "gx-stage" });
      const ctl = h("div", { class: "gx-ctl" });
      pane.append(stage, ctl);
      stage.append(h("p", { class: "gw-dim" }, "loading ..."));
      let d;
      try { d = await ctx.api("model?glb=" + encodeURIComponent(shown.glb)); }
      catch (e) {
        GW.clear(stage).append(A.portrait(KEEP.kind, key), h("p", { class: "gw-dim" }, "3D is not available here (" + e.message + "). The picture above is the shipped thumbnail."));
        return;
      }
      if (my !== alive || !current || current[0] !== key) return;
      const { createViewer } = await viewerMod();
      GW.clear(stage);
      viewer = await createViewer(stage, { height: 440 });
      const v = viewer;
      const sz = await v.load(b64ToBuffer(d.data));
      if (v.textures.failed.length) pane.insertBefore(h("p", { class: "tt-bad" }, v.textures.failed.length + " of " + v.textures.images + " pictures in this model did not decode; those parts are grey."), stage);
      const clipSel = h("select", { "aria-label": "Animation", onchange: e => v.play(e.target.value) }, v.clips.map(c => h("option", { value: c }, c)));
      const firstStand = v.clips.find(c => /stand|idle|wait/i.test(c)); if (firstStand) clipSel.value = firstStand;
      const speed = h("input", { type: "range", min: "0", max: "3", step: "0.1", value: "1", "aria-label": "Speed", oninput: e => v.setSpeed(e.target.value) });
      const chk = (label, key2, on) => h("label", { class: "gx-chk" }, h("input", { type: "checkbox", checked: on ? "checked" : null, onchange: e => v.set({ [key2]: e.target.checked }) }), " " + label);
      ctl.append(v.clips.length ? h("label", null, "Animation ", clipSel) : h("span", { class: "gw-dim" }, "no animations"),
        h("label", null, "Speed ", speed), chk("grid", "grid", true), chk("wireframe", "wire"), chk("skeleton", "skeleton"),
        btn("Save picture", () => download((rec.name || key).replace(/[^\w-]+/g, "_") + ".png", v.snapshot()), { small: true }),
        btn("Reset view", () => v.reframe(), { small: true }),
        sz ? h("span", { class: "gw-dim" }, "size " + sz.size.map(x => x.toFixed(2)).join(" x ") + " units") : null);
    }
    function altTile(t) {
      const span = h("span", { class: "gwart-thumb", "aria-hidden": "true", style: "display:inline-block;width:40px;height:40px;background-repeat:no-repeat" });
      const cols = M.columns || 32;
      if (t !== null && t !== undefined) { span.style.backgroundImage = "url(" + M.sheets[KEEP.kind === "npc" ? "monster" : KEEP.kind] + ")"; span.style.backgroundSize = (cols * 40) + "px auto"; span.style.backgroundPosition = "-" + (t % cols) * 40 + "px -" + Math.floor(t / cols) * 40 + "px"; }
      return span;
    }
    kinds(); list();
  }

  // ============================================================================================ maps
  function tabMaps(body, ctx, my) {
    const A = window.GWArt, MP = (A.maps && A.maps.maps) || {};
    const left = h("div", { class: "gx-maplist" }), pane = h("div", { class: "gx-pane" }, h("p", { class: "gw-dim" }, "Pick a map."));
    body.append(h("p", { class: "gw-dim" }, "Each map picture is aligned to world coordinates: x -256 (left) to 256 (right), z 256 (top) to -256 (bottom). The minimap art is the client's own picture turned back from its 45° view; the terrain picture is drawn from the map file (ground, water, building outlines)."),
      h("div", { class: "gx-cols" }, left, pane));
    const ids = Object.keys(MP).map(Number).sort((a, b) => a - b);
    for (const id of ids) {
      const m = MP[id];
      left.append(h("button", { class: "gx-card wide", type: "button", onclick: () => open(id) }, A.mapThumb(id, { size: 48 }), h("span", { class: "nm" }, (m.name || m.scene) + " #" + id), h("span", { class: "sub" }, m.same_as !== undefined ? "uses " + (MP[m.same_as] || {}).scene : (m.scene || "") + (m.underlay ? "" : " · no picture"))));
    }
    async function open(id) {
      const m = MP[id] || {};
      const real = m.same_as !== undefined ? m.same_as : id;
      const rm = MP[real] || {};
      const cv = h("canvas", { class: "gx-mapcv", width: 640, height: 640, "aria-label": "map " + (m.name || id) });
      const coords = h("div", { class: "gw-dim" }, "move the mouse over the map");
      const mode = { kind: "minimap", img: null, places: [] };
      const sel = h("select", { "aria-label": "Picture", onchange: e => { mode.kind = e.target.value; load(); } }, h("option", { value: "minimap" }, "minimap art (client)"), h("option", { value: "terrain" }, "terrain (map file)"));
      GW.clear(pane).append(h("h2", null, (m.name || m.scene) + " #" + id), h("div", { class: "gw-dim" }, (rm.scene || "") + (rm.hmp ? " · " + rm.hmp : "") + (rm.minimap ? " · " + rm.minimap : "") + (m.note ? " · " + m.note : "")),
        h("div", { class: "gx-ctl" }, sel, btn("Save picture", () => download("map-" + id + "-" + mode.kind + ".png", cv.toDataURL("image/png")), { small: true })), cv, coords);
      try { const e = await GW.catalog.get("map", id); mode.places = (e && e.regions) || []; } catch (e) { /* no catalogue */ }
      const toS = (x, z) => [(x + 256) / 512 * cv.width, (256 - z) / 512 * cv.height];
      function paint() {
        const c = cv.getContext("2d");
        c.fillStyle = "#06131b"; c.fillRect(0, 0, cv.width, cv.height);
        if (mode.img) c.drawImage(mode.img, 0, 0, cv.width, cv.height);
        c.font = "11px Consolas, monospace";
        for (const r of mode.places) { const [x, y] = toS(r.x, r.z); c.fillStyle = "#ffe066"; c.fillRect(x - 2, y - 2, 5, 5); c.fillStyle = "#fff"; c.strokeStyle = "#000"; c.lineWidth = 3; c.strokeText(r.name, x + 5, y + 4); c.fillText(r.name, x + 5, y + 4); }
      }
      async function load() {
        mode.img = null;
        if (mode.kind === "minimap") { const u = await A.underlay(id); mode.img = u ? u.img : null; }
        else {
          try { const d = await ctx.api("mapimage?id=" + real + "&kind=terrain"); const img = new Image(); await new Promise(r => { img.onload = r; img.onerror = r; img.src = "data:image/png;base64," + d.data; }); mode.img = img; }
          catch (e) { GW.status("terrain picture: " + e.message, "err"); }
        }
        paint();
      }
      cv.addEventListener("mousemove", ev => { const r = cv.getBoundingClientRect(); const x = (ev.clientX - r.left) / r.width * 512 - 256, z = 256 - (ev.clientY - r.top) / r.height * 512; coords.textContent = "x " + x.toFixed(1) + "   z " + z.toFixed(1); });
      load();
    }
  }

  // ============================================================================================ textures & files
  function tabTextures(body, ctx, my) {
    const crumbs = h("div", { class: "gx-crumbs" }), list = h("div", { class: "gx-files" }), pane = h("div", { class: "gx-pane" }, h("p", { class: "gw-dim" }, "Pick a picture or a model. Folders worth a look: Monster, Pet, Ride, Characters, Effect, Map/Texture, Map/Surface, Localization/en_us/UI/Texture (UI atlases, minimaps)."));
    const filter = h("input", { type: "search", placeholder: "filter this folder ...", "aria-label": "Filter files", oninput: () => draw() });
    body.append(h("div", { class: "gx-bar" }, crumbs, filter), h("div", { class: "gx-cols" }, list, pane));
    let cur = null;
    async function go(dir) {
      try { cur = await ctx.api("browse?dir=" + encodeURIComponent(dir)); }
      catch (e) { GW.clear(list).append(h("p", { class: "tt-bad" }, e.message)); return; }
      if (my !== alive) return;
      KEEP.dir = cur.dir; filter.value = "";
      const parts = cur.dir ? cur.dir.split("/") : [];
      GW.clear(crumbs).append(h("button", { class: "gw-link", type: "button", onclick: () => go("") }, "client"), ...parts.map((p, i) => [" / ", h("button", { class: "gw-link", type: "button", onclick: () => go(parts.slice(0, i + 1).join("/")) }, p)]).flat());
      draw();
    }
    function draw() {
      if (!cur) return;
      const q = filter.value.toLowerCase();
      GW.clear(list);
      for (const d of cur.dirs) if (!q || d.name.toLowerCase().includes(q)) list.append(h("button", { class: "gx-file dir", type: "button", onclick: () => go((cur.dir ? cur.dir + "/" : "") + d.name) }, "📁 " + d.name, h("span", { class: "sub" }, d.entries + "")));
      let n = 0;
      for (const f of cur.files) {
        if (q && !f.name.toLowerCase().includes(q)) continue;
        if (++n > 2500) { list.append(h("p", { class: "gw-dim" }, "more files: filter the folder")); break; }
        const rel = (cur.dir ? cur.dir + "/" : "") + f.name;
        const can = (f.kind === "picture" || f.kind === "model") && f.ready !== false;
        list.append(h("button", { class: "gx-file " + f.kind, type: "button", disabled: can ? null : "disabled",
          title: f.ready === false ? (f.kind === "model" ? "Not in the prebuilt copy: character part files are converted only where the game client is (see Models > NPCs for dressed characters)" : "This file could not be converted when the prebuilt copy was made") : null, onclick: () => f.kind === "picture" ? showPicture(rel) : showModel(rel) }, f.name, h("span", { class: "sub" }, kb(f.bytes))));
      }
    }
    async function showPicture(rel, frame) {
      dropViewer();
      GW.clear(pane).append(h("p", { class: "gw-dim" }, "decoding " + rel + " ..."));
      let d;
      try { d = await ctx.api("texture?path=" + encodeURIComponent(rel) + (frame ? "&frame=" + frame : "")); } catch (e) { GW.clear(pane).append(h("p", { class: "tt-bad" }, e.message)); return; }
      const img = new Image(); img.src = "data:" + (d.mime || "image/png") + ";base64," + d.data;
      let bad = false;
      await new Promise(r => { img.onload = r; img.onerror = () => { bad = true; r(); }; });
      if (bad) { GW.clear(pane).append(h("p", { class: "tt-bad" }, "This browser could not decode the picture (" + (d.mime || "image/png") + ").")); return; }
      // always hand out a PNG, whatever the server sent (the prebuilt copy is lossless WebP)
      const asPng = () => { const t = document.createElement("canvas"); t.width = img.width; t.height = img.height; t.getContext("2d").drawImage(img, 0, 0); return t.toDataURL("image/png"); };
      const st = { zoom: Math.min(4, Math.max(0.25, 640 / Math.max(img.width, img.height))), alpha: true, layout: false, bg: "checker" };
      if (st.zoom > 1) st.zoom = Math.floor(st.zoom);
      const cv = h("canvas", { class: "gx-tex" });
      const hover = h("div", { class: "gw-dim gx-hover" }, " ");
      let lay = null;
      const isUi = /localization\/[^/]+\/ui\/texture\//i.test(rel);
      function paint() {
        cv.width = Math.round(img.width * st.zoom); cv.height = Math.round(img.height * st.zoom);
        const c = cv.getContext("2d"); c.imageSmoothingEnabled = st.zoom < 1;
        if (st.bg === "checker") { const s = 8; for (let y = 0; y < cv.height; y += s) for (let x = 0; x < cv.width; x += s) { c.fillStyle = ((x / s + y / s) & 1) ? "#2a3a44" : "#1b262d"; c.fillRect(x, y, s, s); } }
        else { c.fillStyle = st.bg; c.fillRect(0, 0, cv.width, cv.height); }
        if (st.alpha) c.drawImage(img, 0, 0, cv.width, cv.height);
        else { const t = document.createElement("canvas"); t.width = img.width; t.height = img.height; const tc = t.getContext("2d"); tc.drawImage(img, 0, 0); const id = tc.getImageData(0, 0, t.width, t.height); for (let i = 3; i < id.data.length; i += 4) id.data[i] = 255; tc.putImageData(id, 0, 0); c.drawImage(t, 0, 0, cv.width, cv.height); }
        if (st.layout && lay) {
          c.lineWidth = 1;
          for (const r of lay.ui) { c.strokeStyle = "#00e5ff"; c.strokeRect(r.x * st.zoom + 0.5, r.y * st.zoom + 0.5, r.w * st.zoom, r.h * st.zoom); }
          for (const r of lay.items) { c.strokeStyle = "#ffcc00"; c.strokeRect(r.x * st.zoom + 0.5, r.y * st.zoom + 0.5, r.w * st.zoom, r.h * st.zoom); }
        }
      }
      cv.addEventListener("mousemove", ev => {
        const r = cv.getBoundingClientRect(), x = Math.floor((ev.clientX - r.left) / st.zoom), y = Math.floor((ev.clientY - r.top) / st.zoom);
        let what = "";
        if (lay) {
          const hitU = lay.ui.filter(u => x >= u.x && x < u.x + u.w && y >= u.y && y < u.y + u.h).slice(0, 3).map(u => u.file + " " + u.element + (u.id ? " #" + u.id : "") + " (" + u.w + "x" + u.h + ")");
          const hitI = lay.items.filter(u => x >= u.x && x < u.x + u.w && y >= u.y && y < u.y + u.h).slice(0, 3).map(u => "item " + u.id + " " + u.key);
          what = hitU.concat(hitI).join(" · ");
        }
        hover.textContent = "x " + x + ", y " + y + (what ? "  →  " + what : "");
      });
      const zooms = [...new Set([0.25, 0.5, 1, 2, 3, 4, 6, 8, st.zoom])].sort((a, b) => a - b);
      const zoomSel = h("select", { "aria-label": "Zoom", onchange: e => { st.zoom = Number(e.target.value); paint(); } }, zooms.map(z => h("option", { value: z }, Math.round(z * 100) + "%")));
      zoomSel.value = String(st.zoom);
      const bgSel = h("select", { "aria-label": "Background", onchange: e => { st.bg = e.target.value; paint(); } }, [["checker", "checker"], ["#000000", "black"], ["#ffffff", "white"], ["#06131b", "dark blue"]].map(([v, t]) => h("option", { value: v }, t)));
      const ctl = h("div", { class: "gx-ctl" }, h("label", null, "Zoom ", zoomSel), h("label", null, "Background ", bgSel),
        h("label", { class: "gx-chk" }, h("input", { type: "checkbox", checked: "checked", onchange: e => { st.alpha = e.target.checked; paint(); } }), " transparency"),
        btn("Download PNG", () => download(rel.replace(/^.*\//, "").replace(/\.[^.]+$/, "") + (d.frames ? "_" + (d.frame || 0) : "") + ".png", asPng()), { small: true }));
      if (d.frames > 1) ctl.append(h("label", null, "Frame ", h("select", { "aria-label": "Frame", onchange: e => showPicture(rel, Number(e.target.value)) }, Array.from({ length: d.frames }, (_, i) => h("option", { value: i, selected: i === (d.frame || 0) ? "selected" : null }, i + 1 + " / " + d.frames)))));
      GW.clear(pane).append(h("h2", null, rel.replace(/^.*\//, "")), h("div", { class: "gw-dim" }, rel + " · " + d.format + (d.dds ? " " + d.dds : "") + (d.mipmaps ? " · " + d.mipmaps + " mipmaps" : "") + " · " + d.size.join("x") + " " + d.mode + " · alpha " + d.alpha + " · " + kb(d.bytes) + (d.container ? " · " + d.container : "")), ctl);
      if (isUi) {
        const lb = h("label", { class: "gx-chk", "data-help": "Outline every picture the game's windows and item list cut from this sheet. Hover a box to see which window element or item it is." }, h("input", { type: "checkbox", onchange: async e => { st.layout = e.target.checked; if (st.layout && !lay) { try { lay = await ctx.api("atlas?path=" + encodeURIComponent(rel)); lbN.textContent = " (" + lay.ui_total + " UI elements, " + lay.items_total + " item icons)"; } catch (x) { GW.status(x.message, "err"); } } paint(); } }), " show layout");
        const lbN = h("span", { class: "gw-dim" });
        ctl.append(lb, lbN);
      }
      pane.append(hover, h("div", { class: "gx-texwrap" }, cv));
      paint();
    }
    async function showModel(rel) {
      dropViewer();
      GW.clear(pane).append(h("h2", null, rel.replace(/^.*\//, "")), h("p", { class: "gw-dim" }, "converting " + rel + " ..."));
      let d;
      try { d = await ctx.api("convert?path=" + encodeURIComponent(rel)); } catch (e) { pane.append(h("p", { class: "tt-bad" }, e.message)); return; }
      if (my !== alive) return;
      const { createViewer } = await viewerMod();
      const r = d.report || {};
      GW.clear(pane).append(h("h2", null, rel.replace(/^.*\//, "")), h("div", { class: "gw-dim" }, rel + " · " + (r.triangles || "?") + " triangles · " + (r.animations || 0) + " animations" + ((r.textures || []).length ? " · texture " + r.textures.join(", ") : "") + ((r.issues || []).length ? " · " + r.issues.length + " notes" : "")));
      const stage = h("div", { class: "gx-stage" }); pane.append(stage);
      viewer = await createViewer(stage, { height: 440 });
      const v = viewer;
      await v.load(b64ToBuffer(d.data));
      if (v.clips.length) pane.append(h("div", { class: "gx-ctl" }, h("label", null, "Animation ", h("select", { "aria-label": "Animation", onchange: e => v.play(e.target.value) }, v.clips.map(c => h("option", { value: c }, c))))));
    }
    go(KEEP.dir || "");
  }

  // ============================================================================================ effects
  function tabEffects(body, ctx, my) {
    const left = h("div", { class: "gx-files" }), pane = h("div", { class: "gx-pane" }, h("p", { class: "gw-dim" }, "Pick an effect. Skills name their effect files in Magic.ini (EffectFile1..4)."));
    const filter = h("input", { type: "search", placeholder: "effect file or skill name ...", "aria-label": "Filter effects", oninput: () => draw() });
    body.append(h("div", { class: "gx-bar" }, filter, h("span", { class: "gw-dim" }, "Edits are saved as a copy in the graphics output folder; the client is never changed.")), h("div", { class: "gx-cols" }, left, pane));
    let all = null;
    (async () => {
      try { all = await ctx.api("effects"); } catch (e) { left.append(h("p", { class: "tt-bad" }, e.message)); return; }
      if (my === alive) draw();
    })();
    function draw() {
      if (!all) return;
      const q = filter.value.toLowerCase();
      GW.clear(left);
      for (const f of all.effects) {
        if (q && !(f.name + " " + f.used_by.join(" ")).toLowerCase().includes(q)) continue;
        left.append(h("button", { class: "gx-file" + (KEEP.fx === f.path ? " on" : ""), type: "button", onclick: () => { KEEP.fx = f.path; draw(); open(f.path); } }, f.name, h("span", { class: "sub" }, f.used_by.length ? f.used_by.slice(0, 2).join(", ") + (f.used_by.length > 2 ? " +" + (f.used_by.length - 2) : "") : f.path.replace(/\/[^/]*$/, ""))));
      }
      if (all.edited.length) left.append(h("h3", null, "Edited copies"), h("div", { class: "gw-dim" }, all.edited_dir), ...all.edited.map(e => h("div", { class: "gw-dim" }, e)));
    }
    async function open(rel) {
      dropViewer();
      GW.clear(pane).append(h("h2", null, rel.replace(/^.*\//, "")), h("p", { class: "gw-dim" }, "reading ..."));
      let d;
      try { d = await ctx.api("effect?path=" + encodeURIComponent(rel)); } catch (e) { pane.append(h("p", { class: "tt-bad" }, e.message)); return; }
      if (my !== alive) return;
      const { createViewer } = await viewerMod();
      GW.clear(pane).append(h("h2", null, rel.replace(/^.*\//, "")), h("div", { class: "gw-dim" }, rel + " · " + d.elements.length + " element(s)" + (d.used_by.length ? " · used by " + d.used_by.slice(0, 8).join(", ") + (d.used_by.length > 8 ? " +" + (d.used_by.length - 8) : "") : " · no skill names it")));
      const stage = h("div", { class: "gx-stage" }); pane.append(stage);
      viewer = await createViewer(stage, { height: 380, background: 0x000000 });
      const v = viewer;
      await v.load(b64ToBuffer(d.glb));
      v.set({ grid: true });
      const ctl = h("div", { class: "gx-ctl" });
      if (v.clips.length) ctl.append(h("label", null, "Animation ", h("select", { "aria-label": "Animation", onchange: e => v.play(e.target.value) }, v.clips.map(c => h("option", { value: c }, c)))));
      ctl.append(h("label", null, "Speed ", h("input", { type: "range", min: "0", max: "3", step: "0.1", value: "1", "aria-label": "Speed", oninput: e => v.setSpeed(e.target.value) })),
        btn("Save picture", () => download(rel.replace(/^.*\//, "").replace(/\.gwm$/i, "") + ".png", v.snapshot()), { small: true }));
      pane.append(ctl);
      // ---- element editor
      const roots = new Map(v.nodes(n => n.userData && n.userData.gwm_params).map(n => [n.name, n]));
      const edits = d.elements.filter(e => e.textured).map(e => ({ index: e.index, name: e.name, p: JSON.parse(JSON.stringify(e.params)), orig: e.params }));
      const tbl = h("table", { class: "gx-fx" });
      const numIn = (arr, i, el, step) => h("input", { type: "number", step: step || "0.01", value: +arr[i].toFixed(4), "aria-label": el, oninput: e => { arr[i] = Number(e.target.value); live(); } });
      function live() {
        for (const e of edits) {
          const n = roots.get(e.name); if (!n) continue;
          const [x, y, z] = e.p.position, [qx, qy, qz, qw] = e.p.orientation, [sx, sy, sz] = e.p.scale;
          n.position.set(x, y, -z);
          const len = Math.hypot(qx, qy, qz, qw) || 1; n.quaternion.set(-qx / len, -qy / len, qz / len, qw / len);
          n.scale.set(sx, sy, sz);
          n.traverse(m => { if (m.isMesh) for (const mat of [].concat(m.material)) { if (mat.userData && mat.userData.gwm_blend) { mat.userData.gwm_blend = { src: e.p.blend_src, dst: e.p.blend_dst }; } } });
        }
      }
      tbl.append(h("tr", null, ["Element", "Position x / y / z", "Scale x / y / z", "Orientation x / y / z / w", "Blend src → dst", "Picture"].map(t => h("th", null, t))));
      for (const e of edits) {
        const blendSel = (k) => h("select", { "aria-label": k, onchange: x => { e.p[k] = Number(x.target.value); } }, BLEND.map((b, i) => h("option", { value: i, selected: e.p[k] === i ? "selected" : null }, i + " " + b)));
        tbl.append(h("tr", null, h("td", null, e.name),
          h("td", null, [0, 1, 2].map(i => numIn(e.p.position, i, "position " + "xyz"[i]))),
          h("td", null, [0, 1, 2].map(i => numIn(e.p.scale, i, "scale " + "xyz"[i]))),
          h("td", null, [0, 1, 2, 3].map(i => numIn(e.p.orientation, i, "orientation " + "xyzw"[i], "0.001"))),
          h("td", null, blendSel("blend_src"), " → ", blendSel("blend_dst")),
          h("td", null, btn("view", () => pictureOf(rel, e.index), { small: true }))));
      }
      const save = async () => {
        const changed = edits.map(e => {
          const o = { index: e.index };
          for (const k of ["position", "scale", "orientation"]) if (JSON.stringify(e.p[k]) !== JSON.stringify(e.orig[k])) o[k] = e.p[k];
          for (const k of ["blend_src", "blend_dst"]) if (e.p[k] !== e.orig[k]) o[k] = e.p[k];
          return o;
        }).filter(o => Object.keys(o).length > 1);
        if (!changed.length) { GW.status("nothing changed", "err"); return; }
        try { const r = await ctx.api("effect/save", { path: rel, elements: changed }); GW.alert(r.changes.join(", ") + ". Written to " + r.written + ". " + r.note, "Saved a copy"); all = await ctx.api("effects"); draw(); }
        catch (e) { GW.status("save failed: " + e.message, "err"); }
      };
      pane.append(h("h3", null, "Elements"), h("p", { class: "gw-dim" }, "Position, scale and orientation are the element's scene-node placement (read by the client at Origin.exe 0x6851b0: decoded). Blend source / destination are inferred from the values across all effects (ONE → ONE = additive glow). The preview updates position, scale and orientation live; blend changes show after saving and reopening the copy in a game build."),
        h("div", { class: "gx-fxwrap" }, tbl), h("div", { class: "gx-ctl" }, btn("Save as a copy", save), btn("Undo my changes", () => open(rel), { small: true })));
      function pictureOf(rel2, idx) {
        const box = h("div");
        GW.modal(box, { title: "Element picture" });
        ctx.api("texture?path=" + encodeURIComponent(rel2) + "&frame=" + idx).then(t => box.append(h("img", { src: "data:" + (t.mime || "image/png") + ";base64," + t.data, alt: "element picture", style: "max-width:70vw;background:#000" }), h("div", { class: "gw-dim" }, t.format + " " + t.size.join("x")))).catch(x => box.append(h("p", { class: "tt-bad" }, x.message)));
      }
    }
  }
})();
