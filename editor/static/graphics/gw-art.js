/* static/graphics/gw-art.js - pictures of game things for any editor page (window.GWArt). Package E, wave 4.
   Classic script; load it once with GWArt-less pages calling:
     (window.GWArt ? Promise.resolve(window.GWArt) : GW.loadArt())   -- or simply add a <script> tag.
   Data: static/graphics/models.json (thumbnail sheets, which .glb each monster / NPC / pet / mount uses),
         static/graphics/maps.json (world-aligned map underlays), static/graphics/icons-extra.json.
   API (all safe to call before load(): they return placeholders and fill in when the data arrives):
     GWArt.load()                         -> Promise (fetches the three indexes once)
     GWArt.thumb(kind, key, {size})       -> <span> thumbnail of a monster / npc / pet / ride (empty if none)
     GWArt.has(kind, key)                 -> bool (after load)
     GWArt.entry(kind, key)               -> the models.json record
     GWArt.portrait(kind, key, {height})  -> panel: big picture + "View in 3D" (uses the graphics module's API)
     GWArt.open3d(kind, key, title)       -> a window with the 3D viewer
     GWArt.underlay(mapId)                -> Promise<{img, src, kind, mapId} | null>  (512x512, world x -256..256, z 256..-256)
     GWArt.drawUnderlay(c2d, under, toS, alpha)  draws it on a canvas whose world->screen function is toS(x, z) -> [sx, sy]
     GWArt.mapThumb(mapId, {size})        -> <span> map thumbnail
   Old and new graphics: dressed NPCs exist in two avatar sets (Characters/ = old, Characters_New/ = new). A record's
   own glb / t is what the GAME draws (models.json avatar_set.in_game); rec.alt.new is the same outfit from the other set.
     GWArt.pick(kind, key, set)           -> {glb, t, set, game} for set "game" | "old" | "new" (falls back to the game's)
     GWArt.setPref("game"|"old"|"new")    which set thumb / portrait / open3d show by default (kept in this browser)
     GWArt.pref                           the current choice
*/
(function () {
  "use strict";
  if (window.GWArt) return;
  const S = { models: null, maps: null, icons: null, loading: null, under: new Map(), pref: "game" };
  try { const p = localStorage.getItem("gw.art.set"); if (p === "old" || p === "new") S.pref = p; } catch (e) { /* private window */ }
  function setPref(p) { S.pref = p === "old" || p === "new" ? p : "game"; try { localStorage.setItem("gw.art.set", S.pref); } catch (e) { /* not kept */ } }
  function pick(kind, key, want) {
    const rec = entry(kind, key);
    if (!rec) return null;
    const game = { glb: rec.glb, t: rec.t, set: rec.set || null, game: true };
    want = want || S.pref;
    if (!want || want === "game" || !rec.set || want === rec.set) return game;
    const a = rec.alt && rec.alt[want];
    return a && a.glb ? { glb: a.glb, t: a.t, set: want, game: false } : game;
  }
  const el = (tag, attrs, ...kids) => {
    const e = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs || {})) {
      if (v === null || v === undefined) continue;
      if (k === "style") e.style.cssText = v; else if (k.startsWith("on")) e.addEventListener(k.slice(2), v); else e.setAttribute(k, v);
    }
    for (const k of kids.flat()) if (k !== null && k !== undefined) e.append(k.nodeType ? k : String(k));
    return e;
  };
  const getJSON = u => fetch(u, { credentials: "same-origin" }).then(r => r.ok ? r.json() : null).catch(() => null);

  function load() {
    if (!S.loading) S.loading = Promise.all([getJSON("/static/graphics/models.json"), getJSON("/static/graphics/maps.json"), getJSON("/static/graphics/icons-extra.json")])
      .then(([m, mp, ic]) => { S.models = m || {}; S.maps = mp || {}; S.icons = ic || {}; return api; });
    return S.loading;
  }
  function entry(kind, key) { return S.models && S.models[kind] ? S.models[kind][key] || null : null; }
  function paintTile(span, kind, rec, size) {
    const m = S.models, url = m.sheets && m.sheets[kind];
    span.style.backgroundImage = "";
    if (!rec || rec.t === null || rec.t === undefined || !url) { span.classList.add("gwart-none"); return; }
    const cols = m.columns || 32, scale = size / (m.tile || 48);
    span.style.backgroundImage = "url(" + url + ")";
    span.style.backgroundSize = (cols * size) + "px auto";
    span.style.backgroundPosition = "-" + (rec.t % cols) * size + "px -" + Math.floor(rec.t / cols) * size + "px";
    void scale;
  }
  function thumb(kind, key, o) {
    const size = (o && o.size) || 36;
    const span = el("span", { class: "gwart-thumb", "aria-hidden": "true", style: "display:inline-block;vertical-align:middle;width:" + size + "px;height:" + size + "px;background-repeat:no-repeat" });
    const want = o && o.set;
    if (S.models) paintTile(span, kind, pick(kind, key, want), size);
    else load().then(() => paintTile(span, kind, pick(kind, key, want), size));
    return span;
  }
  function mapThumb(mapId, o) {
    const size = (o && o.size) || 48;
    const span = el("span", { class: "gwart-thumb", "aria-hidden": "true", style: "display:inline-block;vertical-align:middle;width:" + size + "px;height:" + size + "px;background-repeat:no-repeat" });
    const paint = () => {
      const mp = S.maps && S.maps.maps ? S.maps.maps[String(mapId)] : null;
      const real = mp && mp.same_as !== undefined ? S.maps.maps[String(mp.same_as)] : mp;
      const th = S.maps && S.maps.thumbs;
      if (!real || real.thumb === undefined || !th) return;
      const cols = th.columns || 16;
      span.style.backgroundImage = "url(" + th.file + ")";
      span.style.backgroundSize = (cols * size) + "px auto";
      span.style.backgroundPosition = "-" + (real.thumb % cols) * size + "px -" + Math.floor(real.thumb / cols) * size + "px";
    };
    if (S.maps) paint(); else load().then(paint);
    return span;
  }
  async function underlay(mapId) {
    await load();
    const mp = S.maps.maps ? S.maps.maps[String(mapId)] : null;
    if (!mp) return null;
    const real = mp.same_as !== undefined ? mp.same_as : mapId;
    const rec = S.maps.maps[String(real)];
    if (!rec || !rec.underlay) return null;
    if (S.under.has(real)) return S.under.get(real);
    const p = new Promise(res => {
      const img = new Image();
      img.onload = () => res({ img, src: img.src, kind: rec.underlay, mapId: real });
      img.onerror = () => res(null);
      img.src = "/static/graphics/maps/" + real + ".jpg";
    });
    S.under.set(real, p);
    return p;
  }
  // the picture spans world x -256..256 (left..right) and z 256..-256 (top..bottom)
  function drawUnderlay(c, u, toS, alpha) {
    if (!u || !u.img) return;
    const [ax, ay] = toS(-256, 256), [bx, by] = toS(256, -256);
    c.save(); c.globalAlpha = alpha === undefined ? 0.85 : alpha;
    c.drawImage(u.img, Math.min(ax, bx), Math.min(ay, by), Math.abs(bx - ax), Math.abs(by - ay));
    c.restore();
  }
  function b64ToBuffer(b64) { const bin = atob(b64), out = new Uint8Array(bin.length); for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i); return out.buffer; }
  async function apiGet(path) {
    const r = await fetch("/api/m/graphics/" + path, { credentials: "same-origin" });
    const j = await r.json().catch(() => null);
    if (!r.ok || !j || j.error) throw new Error((j && j.error) || "HTTP " + r.status);
    return j;
  }
  async function open3d(kind, key, title) {
    await load();
    const rec = entry(kind, key);
    const GW = window.GW;
    const body = el("div", { style: "min-width:min(860px,90vw)" });
    const note = el("div", { class: "gw-dim", style: "margin:4px 0" }, "loading the model ...");
    body.append(note);
    const win = GW && GW.modal ? GW.modal(body, { title: title || (rec && rec.name) || key, wide: true }) : null;
    if (!rec || !rec.glb) { note.textContent = "No model for " + key + "."; return; }
    try {
      const { createViewer } = await import("/static/graphics/viewer.js");
      const v = await createViewer(body, { height: 460 });
      const ctl = el("div", { style: "display:flex;gap:10px;align-items:center;margin-top:6px;flex-wrap:wrap" });
      body.append(ctl);
      const show = async want => {
        const p = pick(kind, key, want);
        note.textContent = "loading the model ...";
        const d = await apiGet("model?glb=" + encodeURIComponent(p.glb));
        await v.load(b64ToBuffer(d.data));
        note.textContent = p.glb + " · " + Math.round(d.bytes / 1024) + " KB · drag to turn, wheel to zoom, right-drag to move" + (v.textures.failed.length ? " · " + v.textures.failed.length + " picture(s) did not decode" : "");
        const sel = el("select", { "aria-label": "Animation", onchange: e => v.play(e.target.value) }, v.clips.map(c => el("option", { value: c }, c)));
        const stand = v.clips.find(c => /stand|idle|wait/i.test(c)); if (stand) sel.value = stand;
        ctl.replaceChildren(...(v.clips.length ? ["Animation ", sel] : [el("span", { class: "gw-dim" }, "no animations")]));
        if (rec.alt && rec.set) {
          for (const st of [rec.set].concat(Object.keys(rec.alt))) ctl.append(el("button", { class: "gw-btnx small" + (p.set === st ? " on" : ""), type: "button", "aria-pressed": p.set === st ? "true" : "false", onclick: () => show(st).catch(e => { note.textContent = e.message; }) }, (st === "old" ? "Old graphics" : "New graphics") + (st === rec.set ? " (in game)" : "")));
        }
      };
      await show();
      if (win) { const old = win.close; win.close = (x) => { v.dispose(); old(x); }; }
      const obs = new MutationObserver(() => { if (!document.body.contains(body)) { v.dispose(); obs.disconnect(); } });
      obs.observe(document.body, { childList: true, subtree: true });
    } catch (e) {
      note.textContent = "The 3D model is not available here: " + e.message;
    }
  }
  function portrait(kind, key, o) {
    const height = (o && o.height) || 220;
    const box = el("div", { class: "gwart-portrait", style: "display:flex;gap:10px;align-items:flex-end;margin:6px 0" });
    load().then(async () => {
      const rec = entry(kind, key);
      if (!rec || !rec.glb) { box.append(el("span", { class: "gw-dim" }, rec && rec.missing ? "Model file " + rec.missing + " is not in the client." : "No model picture.")); return; }
      const pic = el("div", { style: "width:" + height + "px;height:" + height + "px;display:flex;align-items:center;justify-content:center;background:#0b1a22;border:1px solid #2e5468;border-radius:4px" }, thumb(kind, key, { size: 96 }));
      const shown = pick(kind, key);
      box.append(pic, el("div", null,
        el("button", { class: "gw-btnx small", type: "button", onclick: () => open3d(kind, key, rec.name) }, "View in 3D"),
        el("div", { class: "gw-dim", style: "margin-top:4px;font-size:11px" }, shown.glb.replace(/^.*\//, "") + (shown.game ? "" : " (" + shown.set + " graphics; the game shows the " + rec.set + " set)"))));
      try {
        const d = await apiGet("still?glb=" + encodeURIComponent(shown.glb));
        pic.replaceChildren(el("img", { src: "data:image/png;base64," + d.data, alt: rec.name || key, style: "max-width:100%;max-height:100%" }));
      } catch (e) { /* stills not on this server: the thumbnail stays */ }
    });
    return box;
  }
  const api = { load, thumb, mapThumb, entry, pick, setPref, get pref() { return S.pref; }, has: (k, key) => !!(entry(k, key) || {}).glb, portrait, open3d, underlay, drawUnderlay,
    get models() { return S.models; }, get maps() { return S.maps; }, get icons() { return S.icons; } };
  window.GWArt = api;
})();
