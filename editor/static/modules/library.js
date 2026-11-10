/* static/modules/library.js - the Library: a read-only encyclopedia of every game entry from the admin's Godswar Origin client
   (items, mounts, buffs, skills, pet skills, talents, zodiac, monsters, NPCs, maps, quests, titles, pets, holy stones,
   append attributes, holy suits, guild buildings, shops). Backend: modules/library.py. Contract: MODULES.md.

   Each tab is the shared virtualised grid (GW.grid: ALL entries, icons, English names, kind / class / level filters,
   search) or a game-window style view (talent trees, zodiac board, shop windows). Clicking an entry opens the detail
   panel: the game's tooltip, every attribute in English with hover help, cross references you can click, and what the
   server's database holds for it (read-only).

   "Use in editor" never writes from this page's backend. It either hands the entry to the right editor
   (#/<module>/<character>?use=<type>:<id>, also kept in sessionStorage "gwedit.use") or uses the framework's shared
   save path: give an item (POST /api/m/library/give/<cid>: online -> ctx.bag_item, live on the round-2 server; offline
   -> ctx.char_write, a guarded database write with Undo) and GW.saveFields (move a character to a place on a map: live
   teleport or database). */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const CLASSES = ["Warrior", "Champion", "Priest", "Mage"];
  const MOUNT_KINDS = ["mount", "mounthead", "mountarmor", "mountsoul", "mountornament", "mountamulet"];
  const QN = () => GW.QUALITY_NAMES || [];
  let S = null;                      // page state (null when unmounted)

  const fmt = v => (v === null || v === undefined || v === "") ? "" : (typeof v === "number" && !Number.isInteger(v) ? String(Math.round(v * 1000) / 1000) : GW.fmt(v));
  const win = (title, ...kids) => h("section", { class: "gw-frame-normalwindow gw-win lib-win" }, h("div", { class: "gw-wtitle" }, title), ...kids);
  const help = (text, ...kids) => h("span", { class: "lib-help", "data-help": text, tabindex: 0 }, ...kids);
  function chipRef(r) {
    if (r.type === "text") return h("span", { class: "lib-ref text" }, r.name);
    const b = h("button", { class: "lib-ref" + (r.known === false ? " unknown" : ""), type: "button", "data-tip-type": r.type, "data-tip-id": String(r.id),
      onclick: () => jump(r.type, r.id) },
      GW.icon(r.sprite, { name: r.name }), h("span", { class: "nm" }, r.name), r.note ? h("span", { class: "note" }, r.note) : null);
    return b;
  }
  function refList(list, limit) {
    const box = h("div", { class: "lib-refs" });
    const n = limit || 60;
    list.slice(0, n).forEach(r => box.append(chipRef(r)));
    if (list.length > n) box.append(h("button", { class: "gw-btnx small", type: "button", onclick: e => { e.target.remove(); list.slice(n).forEach(r => box.append(chipRef(r))); } }, "show all " + list.length));
    return box;
  }
  function valueText(k, v) {
    if (v === true) return "yes";
    if (v === false) return "no";
    if (Array.isArray(v)) return v.map(x => typeof x === "object" ? JSON.stringify(x) : fmt(x)).join(", ");
    if (v && typeof v === "object") return JSON.stringify(v);
    if (k === "class" && typeof v === "number") return CLASSES[v] || String(v);
    if (k === "camp" && v !== null) return Number(v) ? "Athens" : "Sparta";
    if (k === "sex" && v !== null && v !== "") return Number(v) ? "Male" : "Female";
    if (/_s$/.test(k) && typeof v === "number") return fmt(v) + " s";
    return fmt(v);
  }

  // ---------------------------------------------------------------------------------------------- hand-off
  function tabFor(type, row) {
    if (!S) return null;
    if (type === "item" && row && MOUNT_KINDS.includes(row.key) && S.cur && S.cur.key === "mounts") return "mounts";
    const t = S.tabs.find(x => x.type === type);
    if (t) return t.key;
    if (type === "altar") return "guild";
    return null;
  }
  function jump(type, id) {
    const key = tabFor(type);
    if (type === "altar" || type === "guild_building") S.wantSub = type;
    if (key) { showTab(key, id); return; }
    // types without a tab of their own (rides, statuses of mounts ...): show the detail only
    select(type, id);
  }
  async function chooseCharacter(title, why) {
    let rows;
    try { rows = await GW.api("/api/m/characters"); } catch (e) { GW.alert(e.message); return null; }
    return new Promise(res => {
      let done = false;
      const fin = v => { if (!done) { done = true; res(v); } };
      const q = h("input", { class: "lib-input", type: "search", placeholder: "search name or account", "aria-label": "Search characters" });
      const list = h("div", { class: "lib-charlist", role: "listbox" });
      const draw = () => {
        GW.clear(list);
        const t = q.value.toLowerCase();
        for (const r of rows.filter(r => !t || (r.name + " " + r.username + " " + r.id).toLowerCase().includes(t))) {
          list.append(h("button", { class: "lib-charrow" + (r.id === GW.state.cid ? " on" : ""), type: "button", role: "option",
            onclick: () => { fin(r); m.close(); } },
            h("span", { class: "gw-dot " + (r.online ? "on" : "off"), "data-help": r.online ? "online now" : "offline" }),
            h("b", null, r.name), h("span", { class: "gw-dim" }, "Lv " + r.level + " " + r.class + " · " + r.username + " · " + (r.map_name || ""))));
        }
        if (!list.children.length) list.append(h("div", { class: "gw-dim" }, "No character matches."));
      };
      q.addEventListener("input", draw);
      draw();
      const m = GW.modal(h("div", { class: "gw-mod-library lib-dialog" }, why ? h("p", { class: "gw-dim" }, why) : null, q, list), { title: title || "Choose a character", onclose: () => fin(null) });
      setTimeout(() => q.focus(), 30);
    });
  }
  async function handOff(d) {
    const u = d.use;
    if (!u) return;
    let cid = null;
    if (u.needs_character) {
      const c = GW.state.cid !== null && GW.state.cid !== undefined && GW.state.character ? GW.state.character
        : await chooseCharacter("For which character?", "The " + u.title + " editor works on one character.");
      if (!c) return;
      cid = c.id;
      if (GW.state.cid !== c.id && GW.selectCharacter) await GW.selectCharacter(c.id, c);
    }
    const use = { type: d.type, id: d.id, name: d.row.name, from: "library", at: Date.now() };
    try { sessionStorage.setItem("gwedit.use", JSON.stringify(use)); } catch (e) { /* storage blocked: the URL still carries it */ }
    GW.emit("library-use", use);
    location.hash = "#/" + u.module + (cid !== null ? "/" + cid : "") + "?use=" + encodeURIComponent(d.type + ":" + d.id);
  }

  // ---------------------------------------------------------------------------------------------- give an item
  async function giveDialog(d) {
    const e = d.entry, row = d.row;
    const c = await chooseCharacter("Give " + row.name + " to ...", "Pick the character whose bag receives the item.");
    if (!c) return;
    const wear = Number(e.slot) >= 0;
    const st = { qty: 1, q: wear ? 1 : 0, g: wear ? 1 : 10, bound: 1 };
    const stack = Math.max(1, Number(e.stack || (e.server && e.server.overlap) || 1));
    const maxQ = e.max_quality || 10, maxG = e.max_grade || 12;
    const labels = await GW.catalog.labels().catch(() => ({}));
    const prev = h("div", { class: "lib-give-prev" });
    const qName = h("span", { class: "lib-qname" });
    const warn = h("div", { class: "lib-warn" });
    const drawPrev = () => {
      GW.clear(prev).append(GW.render.item(e, { item_quality: st.q, item_grade: st.g, bound: st.bound, stack: st.qty }, labels));
      qName.textContent = (QN()[st.q] || "") + (st.q ? " (Q" + st.q + ")" : "");
      qName.style.color = GW.qColor(st.q);
      const w = [];
      if (wear && st.q > maxQ) w.push("Q" + st.q + " is above this client's Q" + maxQ + ": the client may not show the item's numbers or may close while loading it.");
      if (wear && st.g > maxG) w.push("Grade " + st.g + " is above this client's G" + maxG + ".");
      if (e.in_client === false) w.push("Your client does not have this item: the bag cell will look empty or broken.");
      if (st.qty > stack) w.push("Stacks of " + stack + ": it takes " + Math.ceil(st.qty / stack) + " bag cells.");
      GW.clear(warn).append(...w.map(x => h("div", null, x)));
    };
    const qty = GW.numberField({ label: "Quantity", value: 1, min: 1, max: 9999, onChange: (v, ok) => { if (ok) { st.qty = v; drawPrev(); } } });
    const qf = GW.numberField({ field: "character_items.item_quality", label: "Quality", value: st.q, min: 0, max: 20, onChange: (v, ok) => { if (ok) { st.q = v; drawPrev(); } } });
    const gf = GW.numberField({ field: "character_items.item_grade", label: "Grade", value: st.g, min: 0, max: 25, onChange: (v, ok) => { if (ok) { st.g = v; drawPrev(); } } });
    const bound = h("input", { type: "checkbox", checked: true, onchange: ev => { st.bound = ev.target.checked ? 1 : 0; drawPrev(); } });
    const banner = GW.onlineBanner(c.id, { onState: s => {
      GW.clear(applies).append(s.online ? ((s.live_bag || []).includes("character.give_item") ? GW.appliesChip("live") : GW.appliesChip("never", "refused while online (log out first)")) : GW.appliesChip("next_login"));
    } });
    const applies = h("span", null, GW.appliesChip("next_login"));
    const out = h("div", { class: "lib-result", "aria-live": "polite" });
    const go = h("button", { class: "gw-btnx", type: "button" }, "Give");
    const body = h("div", { class: "gw-mod-library lib-dialog lib-give" },
      banner,
      h("div", { class: "lib-give-cols" },
        h("div", { class: "lib-give-form" },
          h("div", { class: "lib-kv" }, help("The character that receives it.", "Character"), h("b", null, c.name + " (Lv " + c.level + " " + c.class + ")")),
          h("div", { class: "lib-kv" }, help("The item. Its stack size (Overlap) is " + stack + " per bag cell.", "Item"), h("span", null, GW.icon(row.sprite, { name: row.name }), " ", row.name, " #" + row.id)),
          qty, h("div", { class: "lib-q" }, qf, qName), gf,
          h("label", { class: "lib-kv", "data-field": "character_items.bound" }, bound, " bound (cannot be traded)"),
          h("div", { class: "lib-kv" }, help("When the player sees it. Online on the round-2 server: at once. Offline: the next time the character enters the world.", "Applies"), applies),
          warn,
          h("p", { class: "gw-dim" }, "It goes into the first free cells of the kit bag (bags I-IV). Offline: one guarded database write with a backup and Undo in the Change log. Online with the round-2 server: the game server adds it and the player sees it at once (a live bag change has no automatic Undo; remove the item instead)."),
          h("div", { class: "lib-buttons" }, go, h("button", { class: "gw-btnx", type: "button", onclick: () => m.close() }, "Close")), out),
        prev));
    const m = GW.modal(body, { title: "Give " + row.name, wide: true });
    drawPrev();
    go.addEventListener("click", async () => {
      go.disabled = true;
      GW.clear(out).append("saving ...");
      try {
        // modules/library.py give/<cid>: offline = the shared guarded write (with a working Undo), online = GW.bagItem's path
        const r = await S.ctx.api("give/" + c.id, { item: Number(row.id), quantity: st.qty, quality: st.q, grade: st.g, bound: st.bound });
        GW.writeDone(r, "give " + row.name + " x" + st.qty + " to " + c.name);
        GW.clear(out).append(h("div", { class: "gw-good" }, r.mode === "live" ? "Done - the game server added it; " + c.name + " sees it in the bag now." : "Saved - " + c.name + " gets it at the next login."),
          r.mode === "live" ? h("div", { class: "gw-dim" }, "Live bag changes have no automatic Undo: remove the item from the bag to take it back.")
            : h("button", { class: "gw-btnx small", type: "button", onclick: () => { m.close(); GW.changes.open(); } }, "Open the Change log (Undo)"));
      } catch (err) { GW.clear(out); await GW.writeError(err); banner.refresh && banner.refresh(); }
      finally { go.disabled = false; }
    });
  }

  // ---------------------------------------------------------------------------------------------- move a character to a place
  async function moveDialog(d, place) {
    const c = await chooseCharacter("Move a character to " + d.row.name, "Pick the character to move.");
    if (!c) return;
    let cur;
    try { cur = await S.ctx.api("char/" + c.id); } catch (e) { GW.alert(e.message); return; }
    const places = d.places || [];
    const p0 = place || places[0] || { x: 0, z: 0, name: "custom" };
    const st = { x: Number(p0.x), z: Number(p0.z) };
    const xf = GW.numberField({ field: "character_base.Pos_X", label: "X", value: st.x, min: -2000, max: 2000, onChange: (v, ok) => { if (ok) st.x = v; } });
    const zf = GW.numberField({ field: "character_base.Pos_Z", label: "Z", value: st.z, min: -2000, max: 2000, onChange: (v, ok) => { if (ok) st.z = v; } });
    const sel = h("select", { class: "lib-input", "aria-label": "Place" },
      places.map((p, i) => h("option", { value: i, selected: p === p0 ? true : null }, p.name + "  (" + p.x + ", " + p.z + ")")));
    sel.addEventListener("change", () => { const p = places[Number(sel.value)]; if (p) { xf.value = st.x = Number(p.x); zf.value = st.z = Number(p.z); } });
    const applies = h("span", null, GW.appliesChip("next_login"));
    const banner = GW.onlineBanner(c.id, { onState: s => {
      GW.clear(applies).append(s.online ? ((s.live_fields || []).includes("Map") ? GW.appliesChip("live") : GW.appliesChip("never", "refused while online (log out first)")) : GW.appliesChip("next_login"));
    } });
    const out = h("div", { class: "lib-result", "aria-live": "polite" });
    const nowEl = h("span");
    const drawNow = () => { nowEl.textContent = cur.name + " on " + cur.map_name + " at " + fmt(Number(cur.x)) + ", " + fmt(Number(cur.z)); };
    drawNow();
    const go = h("button", { class: "gw-btnx", type: "button" }, "Move");
    const body = h("div", { class: "gw-mod-library lib-dialog" }, banner,
      h("div", { class: "lib-kv" }, help("Where the character is now (character_base.Map, Pos_X, Pos_Z).", "Now"), nowEl),
      h("div", { class: "lib-kv" }, h("span", { class: "gw-label", "data-field": "character_base.Map" }, "Map"), h("b", null, d.row.name + " #" + d.id)),
      places.length ? h("div", { class: "lib-kv" }, help("Named places of this map from the client (world-map captions and minimap list).", "Place"), sel) : null,
      xf, zf,
      h("div", { class: "lib-kv" }, help("Online on the round-2 server: the game server teleports the player at once. Offline: the character appears there at the next login.", "Applies"), applies),
      h("p", { class: "gw-dim" }, "A position off the walkable ground can leave the character stuck; the named places are safe spots."),
      h("div", { class: "lib-buttons" }, go, h("button", { class: "gw-btnx", type: "button", onclick: () => m.close() }, "Close")), out);
    const m = GW.modal(body, { title: "Move " + c.name });
    go.addEventListener("click", async () => {
      go.disabled = true;
      try {
        const r = await GW.saveFields(c.id, { Map: Number(d.id), Pos_X: st.x, Pos_Z: st.z },
          { expected: { Map: cur.map, Pos_X: Number(cur.x), Pos_Z: Number(cur.z) }, label: "move " + c.name + " to " + d.row.name + " (" + st.x + ", " + st.z + ")" });
        GW.clear(out).append(h("div", { class: "gw-good" }, r.mode === "live" ? "Done - the game server moved " + c.name + " now." : "Saved - " + c.name + " is there at the next login."),
          h("button", { class: "gw-btnx small", type: "button", onclick: () => { m.close(); GW.changes.open(); } }, "Open the Change log (Undo)"));
        cur = await S.ctx.api("char/" + c.id).catch(() => cur);
        drawNow();
      } catch (err) { await GW.writeError(err); if (err.status === 409) { cur = await S.ctx.api("char/" + c.id).catch(() => cur); drawNow(); } }
      finally { go.disabled = false; }
    });
  }

  // ---------------------------------------------------------------------------------------------- detail panel
  async function select(type, id, keepUrl) {
    if (!S) return;
    const my = ++S.seq;
    S.selType = type; S.selId = id;
    if (!keepUrl && S.cur) history.replaceState(null, "", "#/library/" + S.cur.key + "/" + encodeURIComponent(id));
    const det = S.detail;
    GW.clear(det).append(h("div", { class: "gw-dim lib-loading" }, "loading ..."));
    let d;
    try { d = await S.ctx.api("detail/" + type + "/" + encodeURIComponent(id)); }
    catch (e) { if (my === S.seq) GW.clear(det).append(h("div", { class: "tt-bad" }, e.message)); return; }
    if (!S || my !== S.seq) return;
    GW.clear(det);
    try { await renderDetail(det, d); } catch (e) { console.error(e); det.append(h("div", { class: "tt-bad" }, "could not draw this entry: " + e.message)); }
    det.scrollTop = 0;
  }

  async function renderDetail(det, d) {
    const r = d.row, e = d.entry, type = d.type;
    const flags = r.sub || "";
    const ix = await GW.catalog.load(type).catch(() => null);
    const ir = ix ? ix.byId.get(String(d.id)) || ix.byId.get(String(r.id)) : null;
    const fl = (ir && ir.flags) || "";
    // ---- head
    det.append(h("div", { class: "lib-head" },
      GW.cell({ sprite: r.sprite, name: r.name, tipType: type, tipId: r.id, class: "lib-bigcell" }),
      h("div", { class: "lib-head-t" },
        h("div", { class: "lib-title", style: ir && ir.color ? "color:var(--gw-" + ir.color + ")" : null }, r.name),
        h("div", { class: "gw-dim" }, flags),
        h("div", { class: "lib-flags" },
          /\bnoclient\b/.test(fl) || e.in_client === false ? h("span", { class: "gw-chip never", "data-help": "The server has it, your Origin client does not: a character holding it sees an empty or broken cell." }, "not in your client") : null,
          /\bnoserver\b/.test(fl) || e.in_server === false ? h("span", { class: "gw-chip never", "data-help": "Your client has it but the server's templates do not, so the server cannot create it." }, "server cannot create it") : null,
          /\bcn\b/.test(fl) ? h("span", { class: "gw-chip restart", "data-help": "The client's own name for it is Chinese." }, "Chinese name in client") : null,
          h("button", { class: "gw-btnx small", type: "button", "data-help": "Copy the id to the clipboard.", onclick: () => { try { navigator.clipboard.writeText(String(d.id)); GW.status("copied " + d.id, "ok"); } catch (x) { GW.status(String(d.id)); } } }, "Copy id")))));
    // ---- use in editor
    det.append(useBox(d));
    // ---- the game tooltip (items: with quality / grade sliders)
    if (type === "item") det.append(await itemTooltipBox(d));
    else {
      const tt = h("div", { class: "lib-tt" });
      try { tt.append(await GW.render.tooltip(type, d.id)); } catch (x) { tt.append(GW.render.generic(type, Object.assign({}, r, e))); }
      det.append(tt);
    }
    // ---- type-specific windows
    const extra = SPECIAL[type];
    if (extra) { const x = await extra(d); if (x) det.append(x); }
    // ---- every attribute
    if (d.attributes && d.attributes.length) {
      det.append(h("h3", { class: "lib-h" }, "Every attribute"),
        h("table", { class: "lib-attrs" }, d.attributes.map(a => h("tr", null,
          h("th", { "data-help": a.help + "  (" + a.key + ")", tabindex: 0 }, a.label),
          h("td", { class: "gw-value" }, GW.text(valueText(a.key, a.value)))))));
    }
    // ---- cross references
    for (const [title, list] of Object.entries(d.refs || {})) {
      det.append(h("h3", { class: "lib-h" }, title, h("span", { class: "gw-dim" }, " (" + list.length + ")")), refList(list));
    }
    // ---- the server's own rows
    const srv = d.server || {};
    const keys = Object.keys(srv).filter(k => Array.isArray(srv[k]) && srv[k].length);
    if (keys.length || d.server_error) {
      const box = h("details", { class: "lib-srv" }, h("summary", { "data-help": "What the reborn server's database holds for this entry. Read in a read-only transaction; nothing here can be changed from the Library." }, "In the server database (read-only)"));
      if (d.server_error) box.append(h("div", { class: "gw-dim" }, "Some tables could not be read: " + d.server_error));
      for (const k of keys) box.append(h("div", { class: "lib-srv-h" }, SRV_TITLES[k] || k), rowsTable(srv[k]));
      det.append(box);
    }
  }
  const SRV_TITLES = { template: "Template row", loot: "Monster loot rules", medusa_loot: "Medusa loot rules", skill_book: "Skill book", material: "Material definition",
    pet_egg: "Pet species using it", online_award: "Online award entries", owned: "Owned by characters", known_by: "Known by", templates: "Monster templates",
    spawns: "Spawns", boss_area: "World boss area", quests: "Quests", monsters: "Monsters", monster_points: "Monster spawn points", npcs: "NPCs",
    address: "Address points", characters: "Characters here", reward_items: "Reward items", taken_by: "Taken by", medusa: "Medusa title", shown_by: "Shown by",
    species: "Species definition", starter_of: "Starter skill of" };
  function rowsTable(rows) {
    const cols = Array.from(rows.reduce((s, r) => { Object.keys(r).forEach(k => s.add(k)); return s; }, new Set()));
    const show = rows.slice(0, 200);
    return h("div", { class: "lib-srv-wrap" }, h("table", { class: "lib-srv-t" }, h("tr", null, cols.map(c => h("th", null, c))),
      show.map(r => h("tr", null, cols.map(c => h("td", null, typeof r[c] === "object" && r[c] !== null ? JSON.stringify(r[c]).slice(0, 400) : fmt(r[c])))))),
      rows.length > show.length ? h("div", { class: "gw-dim" }, rows.length + " rows; the first 200 are shown (the map plot shows all points).") : null);
  }

  function useBox(d) {
    const box = h("div", { class: "lib-use" }, h("div", { class: "lib-use-h", "data-help": "The Library itself never changes anything. These buttons take the entry to an editor, or use the editor's shared save path (backup, Undo, live when the server allows it)." }, "Use in editor"));
    const btns = h("div", { class: "lib-buttons" });
    const writable = S.info && S.info.writable;
    if (d.type === "item" && d.entry.in_server !== false) {
      btns.append(h("button", { class: "gw-btnx", type: "button", disabled: writable ? null : true,
        "data-help": writable ? "Put this item into a character's bag (quality, grade, bound, quantity). Offline: database write with Undo, applies at the next login. Online with the round-2 server: the player gets it at once." : "This editor is read-only for this database.",
        onclick: () => giveDialog(d) }, "Give to a character ..."));
    }
    if (d.type === "map") {
      btns.append(h("button", { class: "gw-btnx", type: "button", disabled: writable ? null : true,
        "data-help": writable ? "Move a character to a named place on this map. Online with the round-2 server: an instant teleport. Offline: applies at the next login." : "This editor is read-only for this database.",
        onclick: () => moveDialog(d) }, "Move a character here ..."));
    }
    if (d.use) {
      const coming = d.use.status !== "ready";
      btns.append(h("button", { class: "gw-btnx", type: "button", onclick: () => handOff(d),
        "data-help": d.use.what + ". Opens the " + d.use.title + " editor with this entry selected" + (coming ? " (that editor is still being built: its page opens, and it picks the entry up once it is ready)." : ".") },
        "Open in " + d.use.title + (coming ? " (coming)" : "")));
    }
    box.append(btns);
    if (d.type === "item" || d.type === "map") box.append(h("div", { class: "lib-chips" }, "Applies: ", GW.appliesChip("next_login"), " offline; ", GW.appliesChip("live"), " online with the round-2 server"));
    if (!btns.children.length) box.append(h("div", { class: "gw-dim" }, "Nothing edits this directly: it is reference data."));
    return box;
  }

  // item tooltip with Q / grade sliders (what a copy looks like in game at that quality)
  async function itemTooltipBox(d) {
    const e = d.entry;
    const labels = await GW.catalog.labels().catch(() => ({}));
    const wear = Number(e.slot) >= 0 && e.stats;
    const st = { q: wear ? 1 : null, g: wear ? 1 : null };
    const tt = h("div", { class: "lib-tt" });
    const draw = () => { GW.clear(tt).append(GW.render.item(e, wear ? { item_quality: st.q, item_grade: st.g || null } : {}, labels)); if (S.statT) markQ(S.statT, st.q); };
    const box = h("div", { class: "lib-ttbox" });
    if (wear) {
      const maxQ = e.max_quality || 10;
      const qs = h("input", { type: "range", min: 1, max: 20, value: 1, "aria-label": "Quality", class: "lib-range" });
      const gs = h("input", { type: "range", min: 0, max: 25, value: 1, "aria-label": "Grade", class: "lib-range" });
      const ql = h("span", { class: "lib-qname" }), gl = h("span", { class: "lib-qname" });
      const upd = () => {
        st.q = Number(qs.value); st.g = Number(gs.value);
        ql.textContent = "Q" + st.q + " " + (QN()[st.q] || "") + (st.q > maxQ ? " (above this client's Q" + maxQ + ")" : "");
        ql.style.color = GW.qColor(st.q);
        gl.textContent = "Grade " + st.g + (st.g > (e.max_grade || 12) ? " (above this client's G" + (e.max_grade || 12) + ")" : "");
        gl.style.color = st.g ? GW.gColor(st.g) : "";
        draw();
      };
      qs.addEventListener("input", upd); gs.addEventListener("input", upd);
      box.append(h("div", { class: "lib-sliders" },
        h("label", { "data-field": "character_items.item_quality" }, "Quality ", qs, ql),
        h("label", { "data-field": "character_items.item_grade" }, "Grade ", gs, gl)));
      box.append(tt);
      upd();
    } else { box.append(tt); draw(); }
    return box;
  }
  function markQ(t, q) { for (const td of t.querySelectorAll("[data-q]")) td.classList.toggle("on", Number(td.dataset.q) === q); }

  // ---------------------------------------------------------------------------------------------- type windows
  const tiles = list => h("div", { class: "lib-tiles" }, list.filter(x => x && x[1] !== null && x[1] !== undefined && x[1] !== "" && x[1] !== 0 || (x && x[3])).map(([lab, v, hl]) =>
    h("div", { class: "lib-tile", "data-help": hl || lab, tabindex: 0 }, h("span", { class: "k" }, lab), h("b", null, v))));
  const SPECIAL = {
    async item(d) {
      const e = d.entry, out = h("div");
      if (d.stats && d.stats.length) {
        const n = Math.max(...d.stats.map(s => (s.values || []).length));
        const t = h("table", { class: "lib-qtab" }, h("tr", null, h("th", null, "Stat"), Array.from({ length: n }, (_, i) => h("th", { "data-q": i + 1, style: "color:" + GW.qColor(i + 1) }, "Q" + (i + 1)))),
          d.stats.map(s => h("tr", null, h("th", { "data-help": d.stats_help }, s.label), (s.values || []).map((v, i) => h("td", { "data-q": i + 1 }, fmt(v))))));
        S.statT = t;
        out.append(h("h3", { class: "lib-h", "data-help": d.stats_help }, "Stats by quality"), h("div", { class: "lib-scroll" }, t));
        markQ(t, 1);
      } else S.statT = null;
      if (d.mount && d.mount.speed_by_quality) {
        out.append(h("h3", { class: "lib-h" }, "Mount"), tiles([["Riding speed", "+" + Math.round(d.mount.speed_by_quality[0] * 100) + "%", "Extra movement speed while riding (Q1; the same at every quality for most mounts)."],
          ["Slot", d.mount.slot_label, "Where it is equipped."], d.mount.max_hp_by_quality ? ["Max HP (Q1)", fmt(d.mount.max_hp_by_quality[0]), "Mount HP at Q1."] : null]));
      }
      const fg = d.forge;
      if (fg && (fg.cost_quality || fg.cost_grade)) {
        const row = (lab, arr, hl) => arr ? h("tr", null, h("th", { "data-help": hl }, lab), arr.map(v => h("td", null, fmt(v)))) : null;
        out.append(h("h3", { class: "lib-h", "data-help": "The client's forge table for this item (EquipForge.xml): what each quality and grade step costs, and the chance modifier per step." }, "Forging (client table)"),
          h("div", { class: "lib-scroll" }, h("table", { class: "lib-qtab" },
            h("tr", null, h("th", null, "Step"), Array.from({ length: Math.max((fg.cost_quality || []).length, (fg.cost_grade || []).length) }, (_, i) => h("th", null, "+" + (i + 1)))),
            row("Quality cost", fg.cost_quality, "Forge points / materials per quality step (client value)."),
            row("Quality chance +", fg.quality_chance_add, "Chance modifier per quality step (client value)."),
            row("Grade cost", fg.cost_grade, "Cost per grade step (client value)."),
            row("Grade chance +", fg.grade_chance_add, "Chance modifier per grade step (client value)."))),
          fg.cost_silver ? h("div", { class: "gw-dim" }, "Silver per forge: " + fmt(fg.cost_silver)) : null);
      }
      if (d.shops && d.shops.length) {
        out.append(h("h3", { class: "lib-h", "data-help": "Shops the server sells it in (compiled into the server's code; shown read-only)." }, "Sold in (" + d.shops.length + ")"),
          h("table", { class: "lib-attrs" }, d.shops.map(s => h("tr", null, h("th", null, s.title), h("td", { class: "gw-value" }, fmt(s.price) + " " + (S.currencies[s.currency] || s.currency))))));
      }
      return out;
    },
    async skill(d) {
      const e = d.entry;
      return h("div", null,
        tiles([["Class", e.class !== null && e.class !== undefined ? CLASSES[e.class] : (d.server_classes ? (d.server_classes.length > 3 ? "every class" : d.server_classes.join(", ")) : "item / monster"), "Who learns it (the client names the class of zodiac skills only; otherwise the server's skill_templates.class_ids)."], ["Level", e.level, "Rank of the skill."],
          ["MP", fmt(e.mp), "MP cost."], ["Cooldown", e.cooldown_s ? e.cooldown_s + " s" : "", "Seconds before the next cast."], ["Cast", e.cast_s ? e.cast_s + " s" : "", "Casting time."],
          ["Range", e.range, "How far."], ["Area", e.area, "Area radius."], ["Damage type", e.property ? "magical" : "physical", "Property", true]]),
        d.desc_filled ? h("div", { class: "lib-desc" }, GW.text(d.desc_filled)) : null);
    },
    async status(d) {
      const e = d.entry;
      return tiles([["Duration", e.duration_s ? e.duration_s + " s" : "", "How long it lasts."], ["Tick", e.interval_s ? e.interval_s + " s" : "", "Seconds between ticks."],
        ["Value", fmt(e.value), "Strength per tick or in total."], ["Effect", e.effect_label, "Effect code in words."], ["Family", e.family, "Statuses of one family replace each other."]]);
    },
    async petskill(d) {
      return h("div", null, d.family_only ? h("div", { class: "lib-warn" }, "Only for " + d.family_only + ": the editor refuses it for other species unless forced.") : null,
        d.desc_filled ? h("div", { class: "lib-desc" }, GW.text(d.desc_filled)) : null,
        d.ranks && d.ranks.length ? h("table", { class: "lib-qtab" }, h("tr", null, h("th", null, "Rank"), d.ranks.map(x => h("th", null, x.rank))),
          h("tr", null, h("th", { "data-help": "The skill's value at each rank." }, "Value"), d.ranks.map(x => h("td", null, fmt(x.value)))),
          h("tr", null, h("th", { "data-help": "The pet rank needed for that skill rank." }, "Pet rank"), d.ranks.map(x => h("td", null, fmt(x.pet_rank))))) : null);
    },
    async talent(d) {
      const ef = d.effects || [];
      if (!ef.length) return null;
      const n = Math.max(...ef.map(x => (x.values || []).length));
      return h("div", null, h("h3", { class: "lib-h" }, "Effect per rank"), h("table", { class: "lib-qtab" },
        h("tr", null, h("th", null, "Effect"), Array.from({ length: n }, (_, i) => h("th", null, "R" + (i + 1)))),
        ef.map(x => h("tr", null, h("th", null, x.label || x.stat), (x.values || []).map(v => h("td", null, fmt(v) + (x.percent ? "%" : "")))))));
    },
    async appendattr(d) {
      const lv = d.levels || [];
      if (!lv.length) return null;
      const mx = Math.max(...lv.map(x => Number(x.value) || 0)) || 1;
      return h("div", null, h("h3", { class: "lib-h", "data-help": "The value this attribute adds at each level (ItemAppendAttribute.xml)." }, (d.stat_label || "Value") + " per level"),
        h("div", { class: "lib-bars" }, lv.map(x => h("div", { class: "lib-bar", "data-help": "Level " + x.level + ": +" + fmt(x.value) },
          h("span", { class: "lv" }, x.level), h("span", { class: "b", style: "width:" + Math.max(2, Math.round(Number(x.value) / mx * 100)) + "%" }), h("span", { class: "v" }, "+" + fmt(x.value))))));
    },
    async map(d) { return mapPlot(d); },
    async quest(d) {
      const e = d.entry;
      return h("div", null,
        tiles([["Level", e.min_level ? e.min_level + (e.max_level && e.max_level !== e.min_level ? "-" + e.max_level : "") : "", "Level range."],
          ["Camp", e.camp === 0 ? "Sparta" : e.camp === 1 ? "Athens" : "both", "Which camp may take it.", true], ["Class", e.class >= 0 ? CLASSES[e.class] : "every class", "Who may take it.", true]]),
        (d.rewards || []).length ? h("div", { class: "lib-rewards" }, h("b", null, "Rewards: "), d.rewards.map(x => h("span", { class: "lib-reward", "data-help": x.label + " the quest gives." }, x.label + " " + fmt(x.value)))) : null,
        e.objectives ? h("div", { class: "lib-desc" }, h("b", null, "Objectives: "), GW.text(e.objectives)) : null,
        e.details ? h("div", { class: "lib-desc" }, GW.text(e.details)) : null,
        e.completion ? h("div", { class: "lib-desc gw-dim" }, GW.text(e.completion)) : null);
    },
    async pet(d) {
      const e = d.entry, s = d.species_def || {};
      const models = d.models || {};
      return h("div", null,
        h("div", { class: "lib-cells" },
          h("span", null, GW.cell({ sprite: e.sprite_male, name: "male" }), h("span", { class: "gw-dim" }, " male")),
          h("span", null, GW.cell({ sprite: e.sprite_female, name: "female" }), h("span", { class: "gw-dim" }, " female"))),
        tiles([["Food kind", s.food_kind, "Which pet food it eats (server)."], ["Lifetime", (s.lifetime_values || []).join(" / "), "Lifetime values per aptitude band (server)."]]),
        Object.keys(models).length ? h("table", { class: "lib-attrs" }, h("tr", null, h("th", null, "Sex"), h("th", null, "From rebirth"), h("th", null, "Model")),
          Object.entries(models).flatMap(([sx, list]) => (list || []).map(m => h("tr", null, h("td", null, sx), h("td", null, m.rebirths), h("td", { class: "gw-dim" }, m.model))))) : null);
    },
    async monster(d) {
      const st = d.server_stats;
      if (!st) return h("div", { class: "gw-dim" }, "The client has no monster stats; the server database has none for this key.");
      return h("div", null, h("h3", { class: "lib-h", "data-help": "monster_templates.stats on the server (read-only)." }, "Server stats"),
        h("table", { class: "lib-attrs" }, Object.entries(st).slice(0, 80).map(([k, v]) => h("tr", null, h("th", null, k), h("td", { class: "gw-value" }, typeof v === "object" ? JSON.stringify(v) : fmt(v))))));
    },
    async suit(d) {
      return h("div", null, h("h3", { class: "lib-h" }, "Tier stages"), h("div", { class: "lib-stages" }, (d.stages || []).map(s =>
        h("div", { class: "lib-stage" }, GW.cell({ sprite: s.sprite, name: s.name }), h("div", null, h("b", null, s.name), h("div", { class: "gw-dim" }, "from level " + s.from_level), h("div", null, GW.text(s.desc || "")))))));
    },
    async guild_building(d) { return levelTable(d); },
    async altar(d) { return levelTable(d); },
  };
  function levelTable(d) {
    const lv = d.levels || [];
    if (!lv.length) return null;
    const cols = Array.from(lv.reduce((s, r) => { Object.keys(r).forEach(k => s.add(k)); return s; }, new Set()));
    const LAB = { level: "Level", bijou: "Gems", found: "Funds", ConsortiaImpactType: "Effect type", ConsortiaImpactValue: "Effect value" };
    return h("div", null, h("h3", { class: "lib-h" }, "Levels"), h("div", { class: "lib-scroll" }, h("table", { class: "lib-qtab" },
      h("tr", null, cols.map(c => h("th", null, LAB[c] || c))), lv.map(r => h("tr", null, cols.map(c => h("td", null, fmt(r[c]))))))));
  }

  // map: an SVG plot of places, gates, monster and NPC spawn points (x right, z up)
  function mapPlot(d) {
    const pts = [];
    const add = (x, z) => { if (isFinite(Number(x)) && isFinite(Number(z))) pts.push([Number(x), Number(z)]); };
    (d.places || []).forEach(p => add(p.x, p.z)); (d.gates || []).forEach(p => add(p.x, p.z));
    const plot = d.plot || { monsters: [], npcs: [] };
    plot.monsters.forEach(p => add(p.x, p.z)); plot.npcs.forEach(p => add(p.x, p.z));
    const box = h("div", { class: "lib-map" });
    if (!pts.length) { box.append(h("div", { class: "gw-dim" }, "No coordinates for this map.")); return box; }
    let x0 = Math.min(...pts.map(p => p[0])), x1 = Math.max(...pts.map(p => p[0])), z0 = Math.min(...pts.map(p => p[1])), z1 = Math.max(...pts.map(p => p[1]));
    const pad = Math.max(10, (x1 - x0 + z1 - z0) * 0.05); x0 -= pad; x1 += pad; z0 -= pad; z1 += pad;
    const W = 360, H = Math.max(160, Math.min(420, Math.round(W * (z1 - z0) / Math.max(1, x1 - x0))));
    const X = x => (x - x0) / (x1 - x0) * W, Y = z => H - (z - z0) / (z1 - z0) * H;
    const NS = "http://www.w3.org/2000/svg";
    const svg = document.createElementNS(NS, "svg");
    svg.setAttribute("viewBox", "0 0 " + W + " " + H); svg.setAttribute("class", "lib-svg"); svg.setAttribute("role", "img");
    svg.setAttribute("aria-label", "map of " + d.row.name);
    const dot = (x, z, cls, r, tip, click) => {
      const c = document.createElementNS(NS, "circle");
      c.setAttribute("cx", X(x).toFixed(1)); c.setAttribute("cy", Y(z).toFixed(1)); c.setAttribute("r", r); c.setAttribute("class", cls);
      if (tip) c.setAttribute("data-help", tip);
      if (click) { c.style.cursor = "pointer"; c.addEventListener("click", click); }
      svg.append(c);
    };
    plot.monsters.forEach(p => dot(p.x, p.z, "mon", 1.6, null));
    plot.npcs.forEach(p => dot(p.x, p.z, "npc", 2.4, "NPC " + p.npc_key + " (" + p.x + ", " + p.z + ")"));
    (d.gates || []).forEach(g => dot(g.x, g.z, "gate", 5, "Gate to map " + g.to + " (" + g.x + ", " + g.z + ")", () => jump("map", g.to)));
    (d.places || []).forEach(p => {
      dot(p.x, p.z, "place", 4, p.name + " (" + p.x + ", " + p.z + ") - click to move a character here", () => moveDialog(d, p));
      const t = document.createElementNS(NS, "text");
      t.setAttribute("x", (X(p.x) + 6).toFixed(1)); t.setAttribute("y", (Y(p.z) + 3).toFixed(1)); t.setAttribute("class", "lbl"); t.textContent = p.name;
      svg.append(t);
    });
    box.append(h("h3", { class: "lib-h", "data-help": "x to the right, z up. Yellow: named places (click one to move a character there). Blue: gates. Red: monster spawns. Green: NPCs. From the client's region list and the server's spawn tables." }, "Map"),
      svg, h("div", { class: "lib-legend" }, h("span", { class: "k place" }), "places ", h("span", { class: "k gate" }), "gates ", h("span", { class: "k mon" }), "monsters (" + plot.monsters.length + ") ",
        h("span", { class: "k npc" }), "NPCs (" + plot.npcs.length + ")"));
    if ((d.places || []).length) box.append(h("div", { class: "lib-places" }, d.places.map(p => h("button", { class: "lib-place", type: "button", "data-help": "Move a character to " + p.name + " (" + p.x + ", " + p.z + ")", onclick: () => moveDialog(d, p) }, p.name))));
    return box;
  }

  // ---------------------------------------------------------------------------------------------- tab views
  function gridView(tab, id) {
    const filt = tab.filter === "mount" ? (r => MOUNT_KINDS.includes(r.key)) : null;
    const grid = GW.grid({ type: tab.type, value: id, height: "100%", filter: filt, onSelect: r => select(tab.type, r.id), onPick: r => select(tab.type, r.id) });
    S.grid = grid;
    return grid.el;
  }

  async function talentView(tab, id) {
    const wrap = h("div", { class: "lib-view" });
    const mode = h("div", { class: "lib-sub" });
    const body = h("div", { class: "lib-view-body" });
    const mk = (k, label) => h("button", { class: "gw-tabx" + (S.sub === k ? " on" : ""), type: "button", onclick: () => { S.sub = k; showTab("talents", id); } }, label);
    S.sub = S.sub === "grid" ? "grid" : "tree";
    mode.append(mk("tree", "Talent trees"), mk("grid", "All talents (grid)"));
    wrap.append(mode, body);
    if (S.sub === "grid") { body.append(gridView(tab, id)); return wrap; }
    const r = await S.ctx.api("talents");
    const trees = h("div", { class: "lib-trees" });
    for (let c = 0; c < 4; c++) {
      const list = r.talents.filter(t => t.class === c).sort((a, b) => (a.tree_order || 0) - (b.tree_order || 0));
      const byId = new Map(list.map(t => [t.id, t]));
      trees.append(h("div", { class: "lib-tree" }, h("div", { class: "lib-tree-h" }, CLASSES[c]),
        h("div", { class: "lib-tree-cells" }, list.map(t => {
          const need = t.requires_talent !== null && t.requires_talent !== undefined && t.requires_talent !== -1 && t.requires_talent !== t.id ? byId.get(t.requires_talent) : null;
          return h("button", { class: "lib-tal" + (String(t.id) === String(S.selId) ? " on" : ""), type: "button", "data-tip-type": "talent", "data-tip-id": String(t.id), onclick: () => select("talent", t.id) },
            GW.cell({ sprite: t.sprite, name: t.name }), h("span", { class: "nm" }, t.name),
            need ? h("span", { class: "need" }, "needs " + need.name + (t.requires_rank ? " " + t.requires_rank : "")) : h("span", { class: "need" }, "#" + t.id));
        }))));
    }
    body.append(trees);
    return wrap;
  }

  async function zodiacView(tab, id) {
    const wrap = h("div", { class: "lib-view" });
    const subs = [["kinds", "Skill kinds"], ["signs", "Signs"], ["grids", "Training grids"], ["levels", "Zodiac levels"], ["stones", "Energy stones"]];
    if (!subs.some(s => s[0] === S.sub)) S.sub = "kinds";
    wrap.append(h("div", { class: "lib-sub" }, subs.map(([k, l]) => h("button", { class: "gw-tabx" + (S.sub === k ? " on" : ""), type: "button", onclick: () => { S.sub = k; showTab("zodiac", id); } }, l))));
    const body = h("div", { class: "lib-view-body lib-scrollpane" });
    wrap.append(body);
    if (S.sub === "kinds") { body.classList.remove("lib-scrollpane"); body.append(gridView(tab, id)); return wrap; }
    const z = S.zodiac || (S.zodiac = await S.ctx.api("zodiac"));
    if (S.sub === "signs") {
      body.append(h("div", { class: "lib-signs" }, Object.values(z.signs || {}).sort((a, b) => a.id - b.id).map(s =>
        h("div", { class: "lib-sign", "data-help": "Sign " + s.id + " (character_base.zodiac_type). " + (s.text || ""), tabindex: 0 },
          h("img", { src: "/static/ui/zodiac/" + String(s.id).padStart(2, "0") + ".png", alt: "", width: 96, height: 96, loading: "lazy" }),
          h("b", null, s.name), h("span", { class: "gw-dim" }, s.dates), h("span", null, "God: " + s.god), h("span", { class: "gw-dim" }, "Planet: " + s.planet)))));
    } else if (S.sub === "grids") {
      const grids = Object.values(z.grids || {}).sort((a, b) => a.grid - b.grid);
      const det = h("div", { class: "lib-griddet" }, h("div", { class: "gw-dim" }, "Click a grid."));
      const showGrid = g => {
        GW.clear(det).append(h("h3", { class: "lib-h" }, "Grid " + (g.grid + 1) + (g.type_label ? " · " + g.type_label : "")),
          h("div", { class: "gw-dim" }, "Accepts skill kinds " + (g.accepts_kinds || "?")),
          h("div", { class: "lib-scroll" }, h("table", { class: "lib-qtab" },
            h("tr", null, ["Level", "Zodiac level needed", "Energy", "Talent points", "Effect", "Extra MP"].map(x => h("th", null, x))),
            (g.levels || []).map(l => h("tr", null, [l.level, l.zodiac_level, l.energy, l.talent_points, l.effect, l.extra_mp].map(v => h("td", null, fmt(v))))))));
      };
      body.append(h("div", { class: "lib-board" }, grids.map(g => h("button", { class: "lib-cellbtn", type: "button", "data-help": "Grid " + (g.grid + 1) + ": accepts kinds " + (g.accepts_kinds || "?") + (g.type_label ? " (" + g.type_label + ")" : ""), onclick: () => showGrid(g) },
        h("b", null, g.grid + 1), h("span", { class: "gw-dim" }, g.type_label || "")))), det);
    } else if (S.sub === "levels") {
      body.append(h("table", { class: "lib-qtab" }, h("tr", null, ["Zodiac level", "Player level", "Max energy", "Energy to next"].map(x => h("th", null, x))),
        Object.values(z.levels || {}).sort((a, b) => a.level - b.level).map(l => h("tr", null, [l.level, l.player_level, l.max_energy, l.energy_to_next].map(v => h("td", null, fmt(v)))))));
    } else {
      body.append(h("table", { class: "lib-qtab" }, h("tr", null, ["Stone level", "Energy cost", "Gold cost", "EXP bonus", "EXP cap", "Talent bonus", "Talent cap"].map(x => h("th", null, x))),
        Object.values(z.stones || {}).sort((a, b) => a.level - b.level).map(l => h("tr", null, [l.level, l.energy_cost, l.gold_cost, l.exp_bonus, l.exp_cap, l.talent_bonus, l.talent_cap].map(v => h("td", null, fmt(v)))))));
    }
    return wrap;
  }

  function guildView(tab, id) {
    const wrap = h("div", { class: "lib-view" });
    if (S.sub !== "altar") S.sub = "guild_building";
    wrap.append(h("div", { class: "lib-sub" }, [["guild_building", "Buildings"], ["altar", "Altars"]].map(([k, l]) =>
      h("button", { class: "gw-tabx" + (S.sub === k ? " on" : ""), type: "button", onclick: () => { S.sub = k; showTab("guild"); } }, l))));
    const body = h("div", { class: "lib-view-body" });
    body.append(gridView(Object.assign({}, tab, { type: S.sub }), id));
    wrap.append(body);
    return wrap;
  }

  async function shopView(tab) {
    const r = S.shops || (S.shops = await S.ctx.api("shops"));
    const wrap = h("div", { class: "lib-view lib-shops" });
    const list = h("div", { class: "lib-shoplist", role: "list" });
    const right = h("div", { class: "lib-shopbody" });
    const showShop = c => {
      for (const b of list.children) b.classList.toggle("on", b.dataset.key === c.key);
      const ix = GW.catalog.loadedRow ? null : null;
      GW.clear(right).append(h("div", { class: "lib-shophead" }, h("b", null, c.title), " · " + c.kind + " · paid in " + c.currency_label + " · " + c.entries.length + " items"),
        h("div", { class: "lib-shopcells" }, c.entries.map(e => {
          const row = GW.catalog.loadedRow("item", e.item);
          return h("button", { class: "lib-shopitem" + (e.in_client ? "" : " noclient"), type: "button", "data-tip-type": "item", "data-tip-id": String(e.item), onclick: () => select("item", e.item, true) },
            GW.cell({ sprite: row ? row.sprite : null, name: e.name }), h("span", { class: "nm" }, e.name || (row && row.name) || "#" + e.item),
            h("span", { class: "pr" }, fmt(e.price) + " " + (r.currencies[e.currency] || e.currency)));
        })),
        h("div", { class: "gw-dim" }, "Source: " + (c.source || "") + ". This is the compiled list. ", h("a", { href: "#/npcs?shop=" + encodeURIComponent(c.key) }, "Edit this shop"), " (NPCs page: items, prices, slot order; reaches the game within 15 seconds)."));
    };
    await GW.catalog.load("item").catch(() => null);
    for (const c of r.catalogues) list.append(h("button", { class: "lib-shoprow", type: "button", role: "listitem", "data-key": c.key, onclick: () => showShop(c) },
      h("b", null, c.title), h("span", { class: "gw-dim" }, c.kind + " · " + c.currency_label + " · " + c.entries.length)));
    wrap.append(list, right);
    if (r.catalogues.length) showShop(r.catalogues[0]);
    return wrap;
  }

  async function showTab(key, id) {
    if (!S) return;
    const tab = S.tabs.find(t => t.key === key) || S.tabs[0];
    if (S.cur && S.cur.key !== tab.key) S.sub = null;
    if (S.wantSub) { S.sub = S.wantSub; S.wantSub = null; }
    S.cur = tab;
    for (const b of S.rail.children) { b.classList.toggle("on", b.dataset.key === tab.key); b.setAttribute("aria-selected", b.dataset.key === tab.key ? "true" : "false"); }
    history.replaceState(null, "", "#/library/" + tab.key + (id !== undefined && id !== null ? "/" + encodeURIComponent(id) : ""));
    S.grid = null; S.statT = null;
    GW.clear(S.center).append(h("div", { class: "gw-dim" }, "loading ..."));
    let v;
    try {
      if (tab.key === "talents") v = await talentView(tab, id);
      else if (tab.key === "zodiac") v = await zodiacView(tab, id);
      else if (tab.key === "shops") v = await shopView(tab);
      else if (tab.key === "guild") v = guildView(tab, id);
      else v = gridView(tab, id);
    } catch (e) { v = h("div", { class: "tt-bad" }, e.message); }
    if (!S || S.cur !== tab) return;
    GW.clear(S.center).append(h("div", { class: "lib-tabhelp gw-dim" }, tab.help), v);
    if (id !== undefined && id !== null && id !== "") select(tab.key === "guild" ? S.sub : (tab.type || "item"), id, true);
    else if (!S.selId) GW.clear(S.detail).append(h("div", { class: "gw-dim" }, "Click an entry for its details: the game tooltip, every attribute in English, where it is used, and what you can do with it."));
  }

  // ---------------------------------------------------------------------------------------------- whole-library search
  function searchBox() {
    const input = h("input", { class: "lib-input lib-gsearch", type: "search", placeholder: "Search the whole library (name, id, key)", "aria-label": "Search the whole library", autocomplete: "off" });
    const pop = h("div", { class: "lib-pop hidden", role: "listbox" });
    let t = 0, my = 0;
    const run = async () => {
      const q = input.value.trim();
      if (q.length < 2) { pop.classList.add("hidden"); return; }
      const me = ++my;
      let r;
      try { r = await S.ctx.api("search?q=" + encodeURIComponent(q)); } catch (e) { return; }
      if (me !== my || !S) return;
      GW.clear(pop).classList.remove("hidden");
      if (!r.groups.length) pop.append(h("div", { class: "gw-dim" }, "Nothing matches '" + q + "'."));
      for (const g of r.groups) {
        const tabKey = g.tab;
        pop.append(h("div", { class: "lib-pop-h" }, g.title + " (" + g.count + ")",
          h("button", { class: "gw-btnx small", type: "button", "data-help": "Open the " + g.title + " tab filtered by '" + q + "' (every match, in the full grid).",
            onclick: async () => { pop.classList.add("hidden"); await showTab(tabKey); filterGrid(q); } }, "show all in " + g.title)));
        const row = h("div", { class: "lib-pop-row" });
        g.hits.slice(0, 24).forEach(x => row.append(h("button", { class: "lib-ref", type: "button", "data-tip-type": g.type, "data-tip-id": String(x.id),
          onclick: () => { pop.classList.add("hidden"); showTab(tabKey, x.id); } }, GW.icon(x.sprite, { name: x.name }), h("span", { class: "nm" }, x.name), h("span", { class: "note" }, "#" + x.id))));
        if (g.hits.length > 24) row.append(h("span", { class: "gw-dim" }, " +" + (g.hits.length - 24) + " more: 'show all'"));
        pop.append(row);
      }
    };
    input.addEventListener("input", () => { clearTimeout(t); t = setTimeout(run, 220); });
    input.addEventListener("keydown", e => { if (e.key === "Escape") pop.classList.add("hidden"); if (e.key === "Enter") run(); });
    // one document listener per mount, removed again in unmount (re-mounting the page must not pile them up)
    const outside = e => { if (!pop.contains(e.target) && e.target !== input) pop.classList.add("hidden"); };
    document.addEventListener("mousedown", outside);
    S.offOutside = () => document.removeEventListener("mousedown", outside);
    return h("div", { class: "lib-gs" }, input, pop);
  }
  function filterGrid(q) {
    if (!S || !S.grid) return;
    const inp = S.grid.el.querySelector(".gw-pk-search");
    if (!inp) return;
    inp.value = q;
    inp.dispatchEvent(new Event("input"));
  }

  // ---------------------------------------------------------------------------------------------- mount
  GW.module("library", {
    async mount(el, ctx) {
      S = { ctx, seq: 0, tabs: [], cur: null, sub: null, selId: null, grid: null, currencies: {} };
      let info;
      try { info = await ctx.api("tabs"); } catch (e) { el.append(h("div", { class: "tt-bad" }, e.message)); return; }
      S.info = info; S.tabs = info.tabs;
      S.currencies = { silver: "Silver", gold: "Gold", binding_gold: "B-Gold", honor: "Honor", exchange_point: "Exchange points", exchange_medal: "Exchange medals" };
      const rail = h("div", { class: "lib-rail", role: "tablist", "aria-label": "Library sections" });
      for (const t of info.tabs) {
        rail.append(h("button", { class: "lib-tab", type: "button", role: "tab", "data-key": t.key, "data-help": t.help, onclick: () => { S.selId = null; showTab(t.key); } },
          t.sprite !== null && t.sprite !== undefined ? GW.icon(t.sprite) : GW.icon(null, { name: t.title }),
          h("span", { class: "t" }, t.title), h("span", { class: "n" }, t.count !== null ? GW.fmt(t.count) : "")));
      }
      S.rail = rail;
      S.center = h("div", { class: "lib-center" });
      S.detail = h("div", { class: "lib-detail", "aria-live": "polite" });
      const cat = info.catalog || {};
      el.append(h("div", { class: "lib-top" }, searchBox(),
        h("span", { class: "gw-chip now", "data-help": "Everything here is read from the client files (catalog-origin, built by build_origin_catalog.py) and, read-only, from the server database. Nothing on this page changes the game by itself." }, "read-only"),
        h("span", { class: "gw-dim", "data-help": "Client folder the catalogue was built from, and the database snapshot it was checked against." },
          "from " + (cat.client || "the Origin client") + (info.database ? " · database " + info.database : ""))),
        h("div", { class: "lib-cols" }, rail, S.center, h("section", { class: "gw-frame-normalwindow gw-win lib-detailwin" }, h("div", { class: "gw-wtitle" }, "Details"), S.detail)));
      // the item index is used by the shop windows and reference chips: start loading it now
      GW.catalog.load("item").catch(() => null);
      const key = ctx.route[0], id = ctx.route[1];
      await showTab(key && info.tabs.some(t => t.key === key) ? key : "items", id !== undefined ? decodeURIComponent(id) : undefined);
    },
    unmount() { if (S && S.offOutside) S.offOutside(); S = null; },
  });
})();
