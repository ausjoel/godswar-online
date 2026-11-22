/* static/shared/monster_label.js - wave 4 D item 1: show a monster as "icon + in-game name" with its ID secondary.
   GW.monsterLabel(key, {name, map, compact}) returns a <span> at once (the ID, or the name the caller already has) and fills in
   the icon and the client name when the shared index (/api/m/monsters/index, cached) arrives. Icons are
   static/monster-thumbs/<template_key>.png or <model stem>.png (package E writes more there); no icon = name only.
   GW.monsterIndex() -> Promise of {byKey: {key: entry}, raw}. Pages that use this must work without it (old shell). */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  let pending = null;
  GW.monsterIndex = function () {
    if (!pending) pending = GW.cached("/api/m/monsters/index").then(raw => {
      const byKey = {};
      for (const m of raw.monsters || []) byKey[m.key] = m;
      return { byKey, raw };
    }).catch(e => { pending = null; throw e; });
    return pending;
  };
  GW.monsterName = function (entry, map) {
    if (!entry) return "";
    return (map !== undefined && map !== null && entry.names_by_map && entry.names_by_map[String(map)]) || entry.name || entry.key;
  };
  GW.monsterLabel = function (key, opts) {
    const o = opts || {};
    const img = h("img", { class: "gw-mon-thumb", alt: "", width: 24, height: 24, loading: "lazy", hidden: true });
    const nm = h("span", { class: "gw-mon-name" }, o.name || key || "");
    const id = h("span", { class: "gw-mon-key gw-dim" }, o.name && o.name !== key ? key : "");
    const el = h("span", { class: "gw-mon-label" + (o.compact ? " compact" : ""), "data-tip-type": "monster", "data-tip-id": key }, img, nm, " ", id);
    if (!key) return el;
    GW.monsterIndex().then(ix => {
      const m = ix.byKey[key];
      if (!m) return;
      const name = GW.monsterName(m, o.map);
      if (name && name !== key) { nm.textContent = name; id.textContent = key; }
      if (m.thumb) { img.src = m.thumb; img.hidden = false; img.onerror = () => { img.hidden = true; }; }
      el.title = name + " (" + key + ")" + (m.level ? " · Lv " + (m.level_min === m.level_max ? m.level : m.level_min + "-" + m.level_max) : "") + " · " + m.rank;
    }).catch(() => { /* no index (old server): the ID stays */ });
    return el;
  };
  // Small style block so the helper works on every page without a shared.css change.
  if (!document.getElementById("gw-mon-label-css")) {
    const st = document.createElement("style"); st.id = "gw-mon-label-css";
    st.textContent = ".gw-mon-label{display:inline-flex;align-items:center;gap:6px;vertical-align:middle}" +
      ".gw-mon-thumb{width:24px;height:24px;border-radius:4px;object-fit:cover;background:#2f343e;flex:none}" +
      ".gw-mon-label.compact .gw-mon-thumb{width:18px;height:18px}.gw-mon-key{font-size:.85em}";
    document.head.append(st);
  }
})();
