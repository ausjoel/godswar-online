/* static/modules/inventory.js - the "Inventory" page: the game's bag window (Equip / Fashion / Mount tabs, bags I-IV),
   the warehouse (SB-1..SB-9), the all-items library, drag and drop between any cells, and the GEAR EDITOR (big item
   view with its holy-stone sockets drawn on it, quality / grade steppers in the game's colours, append / class /
   elemental attribute lines, holy suit, item EXP, the live in-game tooltip).
   Backend: modules/inventory.py (routes meta, state/<cid>, set/<cid>, delete/<cid>, move/<cid>). Contract: MODULES.md.
   Client and database text is inserted as text (GW.h), never as HTML. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const LOC = { EQUIP: 0, BAG: 1, TEMP: 2, WH: 3 };
  const BAG_NAMES = ["I", "II", "III", "IV"];
  const ITEM_FIELDS = ["prop_id", "stack", "bound", "item_quality", "item_grade", "item_exp", "holy_suit_code", "holy_socket_count",
    "attribute1", "attribute2", "attribute3", "attribute4", "attribute5",
    "attribute_level1", "attribute_level2", "attribute_level3", "attribute_level4", "attribute_level5",
    "class_attribute1", "elemental_attribute1", "elemental_attribute2",
    "holy_socket1_effect_id", "holy_socket1_level", "holy_socket2_effect_id", "holy_socket2_level", "holy_socket3_effect_id", "holy_socket3_level",
    "holy_socket4_effect_id", "holy_socket4_level", "holy_socket5_effect_id", "holy_socket5_level", "holy_socket6_effect_id", "holy_socket6_level",
    "holy_socket1_value", "holy_socket2_value", "holy_socket3_value", "holy_socket4_value"];
  const DT_CELL = "application/x-gw-cell", DT_NEW = "application/x-gw-newitem";
  const key = (l, s) => l + ":" + s;
  let P = null;                       // page state (one page at a time)

  // ------------------------------------------------------------ small helpers
  function where(loc, slot) {
    loc = Number(loc); slot = Number(slot);
    if (loc === LOC.EQUIP) return (P.meta.slots[slot] || { name: "slot " + slot }).name + " slot";
    if (loc === LOC.BAG && slot >= 96) return "swap parking cell (bag slot " + slot + ")";
    if (loc === LOC.BAG) return "bag " + BAG_NAMES[Math.floor(slot / 24)] + " cell " + (slot % 24 + 1);
    if (loc === LOC.WH) return "warehouse SB-" + (Math.floor(slot / 40) + 1) + " cell " + (slot % 40 + 1);
    return "server temp slot " + slot;
  }
  const irow = id => GW.catalog.loadedRow("item", id);
  const iname = id => { const r = irow(id); return r ? r.name : "item #" + id; };
  const isGearKind = kind => kind !== undefined && kind !== null && P.meta.kind_slot[kind] !== undefined;
  const fmtPct = v => (Number(v) / 100).toFixed(2).replace(/\.?0+$/, "") + "%";
  function shown(row) { if (!row) return null; const o = { id: row.id }; for (const f of ITEM_FIELDS) o[f] = row[f] === undefined ? null : row[f]; return o; }
  function inst(row) {
    const attrs = [];
    for (let i = 1; i <= 5; i++) if (row["attribute" + i] !== null && row["attribute" + i] !== undefined) attrs.push({ id: row["attribute" + i], level: row["attribute_level" + i], enhancement_rank: ((P.meta.attributes[String(row["attribute"+i])] || {}).family_policy || {}).rank });
    const sockets = [];
    for (let i = 1; i <= 6; i++) sockets.push({ effect: row["holy_socket" + i + "_effect_id"], level: row["holy_socket" + i + "_level"], value: i <= 4 ? row["holy_socket" + i + "_value"] : null });
    return { item_quality: row.item_quality, item_grade: row.item_grade, bound: row.bound, stack: row.stack, attrs, holy_suit_code: row.holy_suit_code,
      holy_socket_count: Math.min(4, row.holy_socket_count || 0), sockets, item_exp: row.item_exp };
  }
  function attrValue(id, grade) { return GW.attributeFamilies.format(P.meta.attributes[String(id)], grade); }
  function attrLabel(id) { const a = P.meta.attributes[String(id)]; const r = GW.catalog.loadedRow("appendattr", id); return (r && r.name) || (a && a.label) || ("attribute " + id); }
  function firstFree(loc) {
    if (loc === LOC.BAG) { for (let s = 0; s < 96; s++) if (!P.items.has(key(LOC.BAG, s))) return s; return null; }
    const cap = P.st.character.warehouse_capacity;
    for (let s = 0; s < cap; s++) if (!P.items.has(key(LOC.WH, s))) return s;
    return null;
  }
  function slotFor(kind) {
    const s = P.meta.kind_slot[kind];
    if (s === undefined) return null;
    if (s === 8) return P.items.has(key(LOC.EQUIP, 8)) && !P.items.has(key(LOC.EQUIP, 9)) ? 9 : 8;
    return s;
  }
  function kindOk(slot, kind) { const sl = P.meta.slots[slot]; return !sl || sl.kinds.includes(kind); }
  function onlineState() { return P.online || (P.st && P.st.online) || { online: false }; }
  function liveBag(op) { const o = onlineState(); return !!(o.online && (o.live_bag || []).includes(op)); }

  // ------------------------------------------------------------ write reactions
  async function done(r, label) {
    P.busy = true;
    try { GW.writeDone(r, label); } finally { P.busy = false; }
    const w = (r && r.warnings) || [];
    if (w.length) GW.status(((r.change && r.change.action) || label || "saved") + " - saved, with notes: " + w.join("; "), "ok");
    if (r && r.mode === "live") {      // the game server saves its own change a moment later: read twice
      await reload(1500);
      setTimeout(() => reload(0), 5000);
    } else await reload(0);
  }
  async function fail(e) {
    const k = await GW.writeError(e);
    if (k === "conflict" || k === "online") await reload(0);
    return k;
  }
  async function reload(delay) {
    if (delay) await new Promise(res => setTimeout(res, delay));
    if (!P) return;
    try { P.st = await P.ctx.api("state/" + P.cid); } catch (e) { GW.status(e.message, "err"); return; }
    P.items = new Map(P.st.items.map(r => [key(r.item_location, r.slot_index), r]));
    if (!P.online) P.online = P.st.online;
    drawAll();
  }

  // ------------------------------------------------------------ cells, drag and drop
  function makeCell(loc, slot, opts) {
    const o = opts || {};
    const row = P.items.get(key(loc, slot));
    let el;
    if (row && !o.locked) {
      const r = irow(row.prop_id), m = P.st.metas[String(row.prop_id)] || {};
      el = GW.cell({ sprite: r ? r.sprite : null, name: r ? r.name : "?", count: row.stack, quality: row.item_quality,
        label: iname(row.prop_id) + " in " + where(loc, slot) + ". Click to edit, drag to move.",
        tipType: "item", tipId: row.prop_id, tipInst: inst(row), onclick: () => openEditor(loc, slot) });
      if((P.meta.pet_eggs||{})[String(row.prop_id)]){
        const a=(P.meta.pet_aptitudes||{})[String(row.item_quality)];
        if(a){el.style.borderColor="var(--gw-"+a.color.toLowerCase().replaceAll("_","-")+")";el.setAttribute("aria-label",a.name+" "+iname(row.prop_id)+" in "+where(loc,slot));}
      }
      if (isGearKind(m.kind) && row.item_grade) el.append(h("span", { class: "gw-inv-gr gw-outline", style: "color:" + GW.gColor(row.item_grade) }, "+" + row.item_grade));
      if (row.holy_socket_count) el.append(h("span", { class: "gw-inv-sk" }, "◆".repeat(Math.min(6, row.holy_socket_count))));
      if (row.sealed) el.classList.add("sealed");
      if (!r) el.classList.add("noclient");
      el.draggable = !row.sealed;
      el.addEventListener("dragstart", e => { e.dataTransfer.setData(DT_CELL, JSON.stringify({ location: loc, slot })); e.dataTransfer.effectAllowed = "move"; GW.hideTip(); el.classList.add("dragging"); });
      el.addEventListener("dragend", () => el.classList.remove("dragging"));
      el.addEventListener("keydown", e => { if (e.key === "Delete" || e.key === "Backspace") { e.preventDefault(); deleteItem(loc, slot); } });
    } else if (o.locked) {
      el = GW.cell({ locked: true, label: o.lockedHelp || "locked", help: o.lockedHelp });
      return el;
    } else {
      el = GW.cell({ empty: true, label: "empty " + where(loc, slot), help: (o.help ? o.help + "\n" : "") + "Empty " + where(loc, slot) + ". Click to put any item here, or drop one from the library or another cell.",
        onclick: () => addInto(loc, slot) });
      if (o.caption) el.append(h("span", { class: "gw-inv-cap" }, o.caption));
    }
    el.dataset.loc = loc; el.dataset.slot = slot;
    el.addEventListener("dragover", e => {
      const t = e.dataTransfer.types;
      if (t.includes(DT_CELL) || t.includes(DT_NEW)) { e.preventDefault(); e.dataTransfer.dropEffect = t.includes(DT_NEW) ? "copy" : "move"; el.classList.add("drop"); }
    });
    el.addEventListener("dragleave", () => el.classList.remove("drop"));
    el.addEventListener("drop", e => {
      e.preventDefault(); el.classList.remove("drop");
      const nid = e.dataTransfer.getData(DT_NEW);
      if (nid) { dropNew(loc, slot, Number(nid)); return; }
      const c = e.dataTransfer.getData(DT_CELL);
      if (c) { const f = JSON.parse(c); moveItem(f.location, f.slot, loc, slot); }
    });
    return el;
  }

  async function moveItem(fl, fs, tl, ts) {
    if (fl === tl && fs === ts) return;
    const src = P.items.get(key(fl, fs)), dst = P.items.get(key(tl, ts));
    if (!src) return;
    const sm = P.st.metas[String(src.prop_id)] || {};
    if (tl === LOC.EQUIP && !kindOk(ts, sm.kind)) {
      await GW.alert(iname(src.prop_id) + " is a '" + (sm.kind || "?") + "' item; it cannot go in the " + where(tl, ts) + ".", "Wrong slot");
      return;
    }
    if (dst && fl === LOC.EQUIP && !kindOk(fs, (P.st.metas[String(dst.prop_id)] || {}).kind)) {
      await GW.alert(iname(dst.prop_id) + " cannot go in the " + where(fl, fs) + ", so the two cannot swap.", "Wrong slot");
      return;
    }
    if (dst && !await GW.confirm({ title: "Swap the two items?", message: iname(src.prop_id) + " (" + where(fl, fs) + ") and " + iname(dst.prop_id) + " (" + where(tl, ts) + ") change places. Both keep their item ids, attributes and sockets.", ok: "Swap" })) return;
    try {
      const r = await P.ctx.api("move/" + P.cid, { from: { location: fl, slot: fs }, to: { location: tl, slot: ts }, expected_from: shown(src), expected_to: shown(dst) });
      await done(r);
    } catch (e) { await fail(e); }
  }

  async function deleteItem(loc, slot, closeFn) {
    const row = P.items.get(key(loc, slot));
    if (!row) return;
    if (!await GW.confirm({ title: "Delete " + iname(row.prop_id) + "?", message: "Removes " + iname(row.prop_id) + (row.stack > 1 ? " x" + row.stack : "") + " from the " + where(loc, slot) + (loc === LOC.BAG && liveBag("character.item_delete") ? ".\nThe player is logged in: the game server removes it at once. A live removal has no automatic Undo (add the item again to put it back)." : ".\nThe change log keeps an Undo."), ok: "Delete", danger: true })) return;
    try {
      const r = await P.ctx.api("delete/" + P.cid, { location: loc, slot, expected: shown(row) });
      if (closeFn) closeFn();
      await done(r);
    } catch (e) { await fail(e); }
  }

  async function addInto(loc, slot) {
    const sl = loc === LOC.EQUIP ? P.meta.slots[slot] : null;
    const id = await GW.pick("item", { title: sl ? "Put an item in the " + sl.name + " slot - every item that fits" : "Put an item in the " + where(loc, slot) + " - every item in the game",
      filter: sl ? (r => sl.kinds.includes(r.key)) : null });
    if (id === null || id === "") return;
    openEditor(loc, slot, Number(id));
  }
  async function dropNew(loc, slot, id) {
    if (P.items.has(key(loc, slot))) { GW.status("that cell is taken: drop new items on an empty cell", "err"); return; }
    if (loc === LOC.EQUIP) {
      const r = irow(id);
      if (r && !kindOk(slot, r.key)) { await GW.alert((r.name || id) + " is a '" + r.key + "' item; it cannot go in the " + where(loc, slot) + ".", "Wrong slot"); return; }
    }
    openEditor(loc, slot, id);
  }
  async function addAnywhere(id) {
    const s = firstFree(LOC.BAG);
    if (s === null) { GW.alert("The bag is full (96 cells). Free a cell first.", "Bag full"); return; }
    openEditor(LOC.BAG, s, Number(id));
  }

  // ------------------------------------------------------------ add many items at once
  const canBatch = r => !/\b(noclient|noserver)\b/.test(r.flags || "");
  async function addMany(initial) {
    const rows = await GW.pickMany("item", { title: "Add many items - tick every item you want", initial, allow: canBatch, max: 96 });
    if (rows) openBatch(rows);
  }
  async function openBatch(picked) {
    const context = P, cid = context.cid;
    const current = () => P === context && context.root.isConnected;
    let online;
    try { online = await GW.api("/api/m/online/" + cid); } catch (e) { GW.status(e.message, "err"); return; }
    const entries = await Promise.all(picked.map(r => GW.catalog.get("item", r.id).catch(() => ({}))));
    if (!current()) return;
    const rows = picked.map((r, i) => {
      const e = entries[i], srv = e.server || {};
      return { row: r, count: 1, overlap: Math.max(1, Number(srv.overlap || e.overlap || 1)), gear: isGearKind(r.key), egg: !!(P.meta.pet_eggs || {})[String(r.id)] };
    });
    const c = P.meta.client;
    const number = (label, value, lo, hi, help) => { const f = h("input", { type: "number", min: lo, max: hi, value, "aria-label": label }); return { f, el: h("label", { class: "gw-inv-batch-field", "data-help": help }, label + " ", f) }; };
    const q = number("Quality", 1, 1, c.quality, "Applies to the gear and mounts in this batch. Up to the client's limit; use the single-item editor to go above it.");
    const g = number("Grade", 1, 1, c.grade, "Applies to the gear and mounts in this batch. Up to the client's limit; use the single-item editor to go above it.");
    const all = number("Set all counts", 1, 1, 999, "Sets every row's count, held to what that item can stack to.");
    const bound = h("input", { type: "checkbox", "aria-label": "Bound" });
    const dest = h("select", { "aria-label": "Destination", disabled: online.online ? true : null }, h("option", { value: LOC.BAG }, "Bags"), h("option", { value: LOC.WH }, "Warehouse"));
    const list = h("div", { class: "gw-inv-batch-list" }), review = h("div", { class: "gw-inv-batch-review" });
    const go = h("button", { class: "gw-btnx", type: "button" }, online.online ? "Give items" : "Review");
    function draw() {
      GW.clear(list);
      for (const x of rows) {
        const count = h("input", { type: "number", min: 1, max: x.overlap, value: x.count, "aria-label": "Count of " + x.row.name, onchange: ev => { x.count = Math.max(1, Math.min(x.overlap, Number(ev.target.value) || 1)); ev.target.value = x.count; } });
        list.append(h("div", { class: "gw-inv-batch-row" }, GW.icon(x.row.sprite, { name: x.row.name }), h("span", { class: "gw-inv-batch-name" }, x.row.name, h("span", { class: "gw-dim" }, " #" + x.row.id + (x.overlap > 1 ? " · stacks to " + x.overlap : ""))),
          count, h("button", { class: "gw-btnx small", type: "button", "aria-label": "Remove " + x.row.name, onclick: () => { rows.splice(rows.indexOf(x), 1); draw(); clearReview(); } }, "Remove")));
      }
      go.disabled = !rows.length;
    }
    let token = null;
    function clearReview() { token = null; GW.clear(review); go.textContent = online.online ? "Give items" : "Review"; }
    function items() {
      const clamp = (f, hi) => Math.max(1, Math.min(hi, Number(f.value) || 1));
      return rows.map(x => {
        const special = x.gear || x.egg;
        return { prop_id: x.row.id, stack: x.count, bound: bound.checked ? 1 : 0, item_quality: x.gear ? clamp(q.f, c.quality) : special ? 1 : 0, item_grade: x.gear ? clamp(g.f, c.grade) : special ? 1 : 10 };
      });
    }
    all.f.addEventListener("change", () => { for (const x of rows) x.count = Math.max(1, Math.min(x.overlap, Number(all.f.value) || 1)); draw(); clearReview(); });
    for (const f of [q.f, g.f, bound, dest]) f.addEventListener("change", clearReview);
    go.addEventListener("click", async () => {
      go.disabled = true;
      try {
        if (online.online) {
          if (!await GW.confirm({ title: "Give " + rows.length + " items now?", message: online.name + " is online, so the game server gives them one at a time. Live gives have no automatic Undo, and if one is refused the rest are not given." , ok: "Give items" })) return;
          if (!current()) return;
          const r = await GW.api("/api/m/favourites/give", { target: cid, items: items() });
          if (!current()) return;
          m.close();
          const lines = ["Given: " + r.given.length + " of " + rows.length + "."];
          if (r.failed) lines.push("Refused: " + r.failed.name + " - " + r.failed.error);
          if (r.not_attempted.length) lines.push("Not attempted: " + r.not_attempted.join(", "));
          if (r.failed) await GW.alert(lines.join("\n"), "Some items were not given");
          await done({ mode: "live", warnings: r.warnings }, lines[0]);
        } else if (!token) {
          const p = await GW.api("/api/m/favourites/preview", { target: cid, items: items(), copies: 1, location: Number(dest.value) });
          if (!current()) return;
          token = p.preview;
          GW.clear(review).append(h("p", null, p.items.length + " new items for " + p.character + ", in one save with one Undo. Cells: " + p.items.map(x => x.slot + 1).join(", ") + "."));
          for (const w of p.warnings) review.append(h("p", { class: "tt-bad" }, w));
          go.textContent = "Add " + p.items.length + " items";
        } else {
          const r = await GW.api("/api/m/favourites/apply", { preview: token });
          if (!current()) return;
          m.close();
          if (r.already_applied) GW.status("This batch was already added; no duplicates.", "ok");
          await done(r, "Added " + rows.length + " items");
        }
      } catch (err) { const k = await fail(err); if (k === "online") m.close(); clearReview(); }
      finally { go.disabled = !rows.length; }
    });
    const m = GW.modal(h("div", { class: "gw-inv-batch" },
      h("div", { class: "gw-inv-batch-bar" }, q.el, g.el, all.el, h("label", { class: "gw-inv-batch-field" }, bound, " Bound"), h("label", { class: "gw-inv-batch-field" }, "To ", dest)),
      online.online ? h("p", { class: "gw-dim" }, online.name + " is online: items go to the first free bag cells, one at a time.") : null,
      list, review,
      h("div", { class: "gw-inv-btns" }, go,
        h("button", { class: "gw-btnx", type: "button", onclick: () => { const back = rows.map(x => x.row); m.close(); addMany(back); } }, "Back to picker"),
        h("button", { class: "gw-btnx", type: "button", onclick: () => m.close() }, "Cancel"))), { title: "Add many items", wide: true });
    draw();
  }

  // ------------------------------------------------------------ page layout
  function drawAll() {
    if (!P || !P.root.isConnected) return;
    drawToolbar(); drawEquip(); drawBags(); drawWarehouse(); drawTemp(); applyOnline();
  }

  function drawToolbar() {
    const c = P.st.character;
    const t = GW.clear(P.toolbar);
    t.append(
      h("span", { class: "gw-inv-who" }, h("b", { class: "gw-value" }, c.name), " Lv " + c.level + " " + c.class, h("span", { class: "gw-dim" }, " · #" + c.id)),
      h("button", { class: "gw-btnx", type: "button", "data-help": "Pick any of the " + GW.fmt((GW.catalog.loadedRow("item", 1001) && P.itemCount) || 3488) + " items (icons, English names, filters by kind, class and level) and put it in the first free bag cell.", onclick: async () => {
        const id = await GW.pick("item", { title: "Add an item - every item in the game" });
        if (id !== null && id !== "") addAnywhere(id);
      } }, "+ Add item"),
      h("button", { class: "gw-btnx", type: "button", "data-help": "Tick many items at once, set their quantity, quality and grade, then add them together.", onclick: () => addMany([]) }, "+ Add many"),
      h("button", { class: "gw-btnx", type: "button", "data-help": "Holy Boxes I-V (items 9020-9024) store EXP for the Master Vestment Forger. Pick one; you can set its stored EXP in the next window.", onclick: async () => {
        const id = await GW.pick("item", { title: "Holy Boxes", filter: r => r.id >= 9020 && r.id <= 9024 });
        if (id !== null && id !== "") addAnywhere(id);
      } }, "+ Holy Box"),
      h("a", { class: "gw-btnx", href: "#/favourites/" + P.cid }, "Favourites / copy items"),
      h("span", { class: "gw-inv-keys", "data-help": "Storage Box Keys (item 4102) in the bag. The Warehouse Manager uses 1 / 2 / 3 keys to open SB-2 / SB-3 / SB-4 (docs/warehouse.md). The editor cannot raise the warehouse capacity itself (it needs the game's settlement record), but it can give keys." },
        GW.icon((irow(P.meta.storage_key) || {}).sprite), " Storage Box Keys: ", h("b", { class: "gw-value" }, GW.fmt(P.st.storage_keys))),
      h("button", { class: "gw-btnx small", type: "button", "data-help": "Put Storage Box Keys in the bag (you choose how many in the next window).", onclick: () => addAnywhere(P.meta.storage_key) }, "+ keys"),
      h("span", { class: "gw-inv-limits", "data-help": "What the Godswar Origin client can draw: quality 1-20, grade 1-25, attribute level 25 and 4 holy sockets. The database also allows 6 sockets; going above the client's limits needs the 'Allow above the client limits' tick in the gear editor (the item then looks wrong or plain in game)." },
        "client limits: Q", P.meta.client.quality, " · G", P.meta.client.grade, " · L", P.meta.client.attribute_level, " · ", P.meta.client.sockets, " sockets"),
      P.modeChip = h("span", { class: "gw-inv-mode" }));
    if (!P.st.writable) t.append(h("span", { class: "gw-chip never", "data-help": "This editor was started read-only for this database: you can look, not change." }, "read-only"));
  }

  function tabBtn(id, label, help) {
    return h("button", { class: "gw-tabx" + (P.tab === id ? " on" : ""), type: "button", role: "tab", "aria-selected": P.tab === id ? "true" : "false", "data-help": help,
      onclick: () => { P.tab = id; drawEquip(); applyOnline(); } }, label);
  }
  function drawEquip() {
    const w = GW.clear(P.equipWin);
    w.append(h("div", { class: "gw-wtitle" }, "Equipment"),
      h("div", { class: "gw-inv-tabs", role: "tablist" },
        tabBtn("equip", "Equip", "Equipment slots 0-11 (the paperdoll of the bag window's Equip tab)."),
        tabBtn("fashion", "Fashion", "The costume slot (equipment slot 12)."),
        tabBtn("mount", "Mount", "The riding slot (the active mount, slot 20) and the five mount-gear slots 15-19. Gear needs a mount, and player level >= mount level >= gear level.")));
    const doll = h("div", { class: "gw-inv-doll", "data-tab": P.tab });
    if (P.tab === "equip") doll.append(h("span", { class: "gw-sprite gw-paperdoll gw-inv-figure", "aria-hidden": "true" }));
    for (const s of Object.values(P.meta.slots)) {
      if (s.tab !== P.tab) continue;
      const c = makeCell(LOC.EQUIP, s.slot, { caption: s.name.replace("Mount ", ""), help: s.name + " (equipment slot " + s.slot + "). Takes: " + s.kinds.join(", ") + "." });
      c.classList.add("gw-inv-abs");
      c.style.left = s.x + "px"; c.style.top = s.y + "px";
      doll.append(c);
    }
    if (P.tab === "fashion") {
      for (const [y, nm, why] of [[50, "Tool", "The production-tool cell of the Fashion tab: the reborn server does not use it (no life skills)."], [92, "Pet", "The pet cell of the Fashion tab: pets live in the pet list, not in an item slot. See the Pets page."]]) {
        const c = GW.cell({ locked: true, label: nm, help: why });
        c.classList.add("gw-inv-abs"); c.style.left = "123px"; c.style.top = y + "px";
        c.append(h("span", { class: "gw-inv-cap" }, nm));
        doll.append(c);
      }
    }
    if (P.tab === "mount") doll.append(h("div", { class: "gw-inv-mountnote gw-dim" }, "Riding slot"), h("div", { class: "gw-inv-gearnote gw-dim" }, "Coronet · Armor · Soul · Saddle · Tassels"));
    const c = P.st.character;
    w.append(doll, h("div", { class: "gw-inv-money" },
      moneyLine("Silver", "gw-coin-silver", c.Money, "character_base.Money"),
      moneyLine("Gold", "gw-coin-gold", c.Stone, "character_base.Stone"),
      moneyLine("B-Gold", "gw-coin-gold", c.BindingGold, "character_base.BindingGold")),
      h("p", { class: "gw-dim gw-inv-small" }, "Money is edited on the Character page (live for a logged-in player)."));
  }
  function moneyLine(label, coin, v, field) {
    return h("div", { class: "gw-inv-mline" }, h("span", { class: "gw-label", "data-field": field }, label), h("span", { class: "gw-value" }, GW.fmt(v)), h("span", { class: "gw-sprite " + coin }));
  }

  function drawBags() {
    const w = GW.clear(P.bagWin);
    w.append(h("div", { class: "gw-wtitle" }, "Bags I - IV"));
    const wrap = h("div", { class: "gw-inv-bags" });
    for (let p = 0; p < 4; p++) {
      const g = h("div", { class: "gw-inv-grid six", role: "grid", "aria-label": "bag " + BAG_NAMES[p] });
      for (let i = 0; i < 24; i++) g.append(makeCell(LOC.BAG, p * 24 + i));
      wrap.append(h("div", { class: "gw-inv-bag" }, h("div", { class: "gw-inv-bagname", "data-help": "Bag " + BAG_NAMES[p] + " = kit-bag slots " + (p * 24) + "-" + (p * 24 + 23) + " (item_location 1)." }, "Bag " + BAG_NAMES[p]), g));
    }
    const used = P.st.items.filter(r => r.item_location === LOC.BAG).length;
    w.append(wrap, h("div", { class: "gw-dim gw-inv-small" }, used + " of 96 cells used. Drag to move (dropping on a full cell swaps); Delete key removes the focused item."));
  }

  function drawWarehouse() {
    const w = GW.clear(P.whWin);
    const cap = P.st.character.warehouse_capacity, boxes = Math.floor(cap / 40);
    w.append(h("div", { class: "gw-wtitle" }, "Warehouse"));
    const tabs = h("div", { class: "gw-inv-tabs", role: "tablist" });
    for (let b = 0; b < 9; b++) {
      const lockedBox = b >= boxes;
      const n = P.st.items.filter(r => r.item_location === LOC.WH && Math.floor(r.slot_index / 40) === b).length;
      tabs.append(h("button", { class: "gw-tabx" + (P.wbox === b ? " on" : "") + (lockedBox ? " gw-inv-lockedtab" : ""), type: "button", role: "tab",
        "data-help": lockedBox ? "SB-" + (b + 1) + " is locked: this character has " + boxes + " box" + (boxes === 1 ? "" : "es") + " (" + cap + " cells). The capacity is read-only here - in game the Warehouse Manager opens SB-2..SB-4 with 1 / 2 / 3 Storage Box Keys and records a settlement; SB-5..SB-9 are reserved." : "Storage box " + (b + 1) + ": warehouse slots " + (b * 40) + "-" + (b * 40 + 39) + " (" + n + " items).",
        onclick: () => { P.wbox = b; drawWarehouse(); applyOnline(); } }, "SB-" + (b + 1) + (lockedBox ? " 🔒" : "")));
    }
    const g = h("div", { class: "gw-inv-grid ten", role: "grid", "aria-label": "warehouse box " + (P.wbox + 1) });
    const lockedBox = P.wbox >= boxes;
    for (let i = 0; i < 40; i++) {
      const s = P.wbox * 40 + i;
      g.append(lockedBox ? makeCell(LOC.WH, s, { locked: true, lockedHelp: "SB-" + (P.wbox + 1) + " is locked (warehouse_capacity " + cap + "): read-only, see the tab's help." }) : makeCell(LOC.WH, s));
    }
    w.append(tabs, g, h("div", { class: "gw-inv-whfoot" },
      h("span", { "data-field": "character_base.warehouse_capacity" }, "Capacity: ", h("b", { class: "gw-value" }, cap), " cells (" + boxes + " of 9 boxes)"),
      h("span", { class: "gw-dim" }, " · items move between bag, equipment and warehouse by drag and drop, keeping their attributes and sockets.")));
  }

  function drawTemp() {
    const rows = P.st.items.filter(r => r.item_location === LOC.TEMP);
    const parked = P.st.items.filter(r => r.item_location === LOC.BAG && (r.slot_index < 0 || r.slot_index >= 96));
    GW.clear(P.tempBox);
    if (parked.length) {
      const box = h("div", { class: "gw-inv-temp", "data-help": "A drag-and-drop swap moves one item to a parking cell outside the bag for a moment. If a swap is interrupted (or only some of its three Undo steps were undone) the item stays there, invisible in game. Drag it onto an EMPTY bag, warehouse or equipment cell to put it back." },
        h("b", null, "Parked by an interrupted swap (drag it to an empty cell): "));
      for (const r of parked) { const c = makeCell(LOC.BAG, r.slot_index); c.onclick = null; box.append(c); }
      P.tempBox.append(box);
    }
    if (!rows.length) return;
    P.tempBox.append(h("div", { class: "gw-inv-temp", "data-help": "Server temp slots (location 2) hold items the game server is moving. They are read-only: the editor never writes them." },
      h("b", null, "Server temp slots (read-only): "), rows.map(r => makeCell(LOC.TEMP, r.slot_index, { locked: false }))));
    for (const c of P.tempBox.querySelectorAll(".gw-cellx")) { c.draggable = false; c.onclick = null; }
  }

  // online: grey out what cannot be edited live (UI-STYLE s.8 rule 6)
  function applyOnline() {
    if (!P || !P.modeChip) return;
    const o = onlineState();
    const off = !o.online;
    const anyBag = liveBag("character.give_item") || liveBag("character.item_move") || liveBag("character.item_delete");
    GW.clear(P.modeChip).append(off ? GW.appliesChip("next_login", "offline: changes apply at the next login")
      : anyBag ? GW.appliesChip("live", "online: bag items change live, the rest needs a logout")
        : h("span", { class: "gw-chip restart", "data-help": "Live edits arrive with the round-2 server update; this character must be logged out to edit its items." }, "online: log out to edit"));
    const why = o.online ? (anyBag ? "The player is logged in: the game server can add, move (to an empty bag cell) and remove BAG items live. Equipment and bag item details need the character logged out (or use 'Kick to edit everything'). Tip: have the player put the item in the warehouse; warehouse items can be edited while they are online (they see it the next time they open storage)."
      : "The player is logged in. Live edits arrive with the round-2 server update; this character must be logged out to edit its items.") : "";
    for (const w of [P.equipWin]) { w.classList.toggle("gw-inv-off", !off); if (!off) w.dataset.help = why; else delete w.dataset.help; }
    // the admin F-87: the warehouse stays editable while the player is online (written to the database, seen on next open).
    P.whWin.classList.remove("gw-inv-off");
    if (!off) P.whWin.dataset.help = "Player online: warehouse edits are saved now and show the next time they open storage."; else delete P.whWin.dataset.help;
    P.bagWin.classList.toggle("gw-inv-off", !off && !anyBag);
    if (!off && !anyBag) P.bagWin.dataset.help = why; else delete P.bagWin.dataset.help;
  }

  // ------------------------------------------------------------ the gear / item editor
  function newDraft(pid, e) {
    const gear = isGearKind(e.kind), egg = (P.meta.pet_eggs || {})[String(pid)];
    const d = { prop_id: pid, stack: 1, bound: 0, item_quality: gear || egg ? 1 : 0, item_grade: gear || egg ? 1 : 10, item_exp: 0, holy_suit_code: 0, holy_socket_count: 0 };
    for (const f of ITEM_FIELDS) if (d[f] === undefined) d[f] = null;
    return d;
  }

  async function openEditor(loc, slot, newPid) {
    const context = P, targetCid = context.cid;
    const current = () => P === context && context.root.isConnected;
    const old = P.items.get(key(loc, slot)) || null;
    const pid = newPid || (old && old.prop_id);
    if (!pid) return;
    let e;
    try { e = await GW.catalog.get("item", pid); } catch (err) { e = { id: pid, name: "item #" + pid, kind: "", in_client: false }; }
    if (!current()) return;
    const srv = (e.server || {});
    const kind = e.kind || srv.kind || "";
    const gear = isGearKind(kind), egg=(P.meta.pet_eggs || {})[String(pid)];
    const apt=q=>(P.meta.pet_aptitudes||{})[String(q)]||{name:"Unsupported aptitude "+q,color:"DEFAULT_TEXTCOLOR"};
    const aptColor=q=>"var(--gw-"+apt(q).color.toLowerCase().replaceAll("_","-")+")";
    const D = old && !newPid ? Object.assign({}, shown(old)) : newDraft(pid, Object.assign({}, e, { kind }));
    if (old && newPid) { Object.assign(D, shown(old)); D.prop_id = pid; }
    const overlap = Math.max(1, Number(srv.overlap || e.overlap || e.stack || 1));
    const isBox = pid >= 9020 && pid <= 9024;
    const minlv = Number(e.minlv || e.playlv || srv.minlv || 0);
    const allowed = new Set((e.append_attributes || []).map(Number));
    const suitItem = P.meta.class_suit_items.includes(Number(pid));
    const o = onlineState();
    const isNew = !old || !!newPid;
    let mode = "offline";
    if (o.online) mode = loc === LOC.BAG && isNew && liveBag("character.give_item") ? "live-add" : (loc === LOC.BAG && !isNew && liveBag("character.item_delete") ? "live-remove" : "locked");
    if (!P.st.writable) mode = "readonly";
    let override = false, force = false;

    const root = h("div", { class: "gw-mod-inventory gw-inv-ed" });
    const left = h("div", { class: "gw-inv-col gw-inv-gear" });
    const mid = h("div", { class: "gw-inv-col" });
    const right = h("div", { class: "gw-inv-col gw-inv-prev" });
    root.append(left, mid, right);

    // --- mode banner
    const banner = {
      offline: null,
      "live-add": "Configure the complete item before creating it, including attributes and sockets. Basic items can be given live. Giving a complete item with extra fields offers to log the player out first, then creates it in one saved edit.",
      "live-remove": "The player is logged in: this bag item can be removed live (Delete). Changing it needs the character logged out.",
      locked: o.online ? (liveBag("character.give_item") ? "The player is logged in: the game server only changes BAG items live (add, move, remove). This needs the character logged out - use 'Kick to edit everything' on the online banner." : "The player is logged in. Live edits arrive with the round-2 server update; this character must be logged out.") : null,
      readonly: "This editor is read-only for this database.",
    }[mode];
    if (banner) root.prepend(h("div", { class: "gw-online " + (mode === "live-add" || mode === "live-remove" ? "on" : "warn") + " gw-inv-edbanner" }, banner));
    const lockAll = mode === "locked" || mode === "readonly" || mode === "live-remove";
    const lockExtras = lockAll;

    // ---- left: big item view with the sockets drawn on it
    const big = h("div", { class: "gw-inv-big", "data-tip-type": "item", "data-tip-id": String(pid) });
    const r0 = irow(pid);
    if (r0 && r0.sprite !== null && r0.sprite !== undefined) { const n = Number(r0.sprite); big.style.backgroundPosition = `-${(n % 64) * 108}px -${Math.floor(n / 64) * 108}px`; }
    else { big.classList.add("none"); big.textContent = (e.name || "?").slice(0, 3); }
    const sockBox = h("div", { class: "gw-inv-sockbox" + (gear ? "" : " nosock") }, big);
    const sockEls = [];
    for (let i = 1; i <= 6; i++) {
      const b = h("button", { class: "gw-inv-sock s" + i, type: "button", "aria-label": "Holy socket " + i });
      b.addEventListener("click", () => socketDialog(i));
      sockEls.push(b); sockBox.append(b);
    }
    const nameEl = h("div", { class: "gw-inv-iname" });
    const sub = h("div", { class: "gw-dim gw-inv-small" }, "#" + pid + " · " + (e.category || kind || "?") + (minlv ? " · Lv " + minlv : "") + (overlap > 1 ? " · stacks to " + overlap : ""),
      e.in_client === false ? h("span", { class: "tt-bad" }, " · not in your client") : null, e.in_server === false ? h("span", { class: "tt-bad" }, " · not a server item") : null);
    left.append(sockBox, nameEl, sub);
    if (!gear) left.append(h("p", { class: "gw-dim gw-inv-small" }, "Holy sockets, append attributes and the holy suit are for equipment."));

    // quality / grade steppers
    let qBox;
    if(egg){
      const select=h("select",{"aria-label":"Pet egg aptitude"},(egg.aptitudes||[]).map(a=>h("option",{value:a},apt(a).name+" (#"+a+")")));
      select.onchange=()=>{D.item_quality=Number(select.value);changed();};
      qBox={el:h("div",{class:"gw-inv-line"},h("span",{class:"gw-label"},"Pet aptitude"),select),draw(){select.value=String(D.item_quality);select.style.color=aptColor(D.item_quality);}};
      left.append(h("p",{class:"gw-dim gw-inv-small"},"Pet eggs use native aptitude IDs, not equipment colour. Brave is aptitude 10 (green) in this client."));
    }else{
      qBox = stepper("Quality", "character_items.item_quality", 0, 20, () => D.item_quality, v => { D.item_quality = v; }, v => h("span", { style: "color:" + GW.qColor(v) }, "Q" + v + " " + (GW.QUALITY_NAMES[v] || "")), P.meta.client.quality, gear, () => changed());
    }

    const gBox = stepper("Grade", "character_items.item_grade", 0, 25, () => D.item_grade, v => { D.item_grade = v; }, v => h("span", { style: "color:" + (v ? GW.gColor(v) : "#aaa") }, v ? "★".repeat(Math.min(v, 12)) + (v > 12 ? " +" + (v - 12) : "") + "  G" + v : "G0 (none)"), P.meta.client.grade, gear, () => changed());
    left.append(qBox.el, gBox.el);
    if (!gear && !egg) left.append(h("p", { class: "gw-dim gw-inv-small" }, "Consumables and materials are normally Q0 / grade 10 (the game's own value for them)."));

    // count, bound
    const stackF = GW.numberField({ field: "character_items.stack", label: "Count", value: D.stack, min: 1, max: force ? 32767 : overlap, onChange: v => { D.stack = v; changed(); } });
    if (overlap === 1 && !force) { stackF.classList.add("gw-inv-dis"); stackF.dataset.help = "This item does not stack (Overlap 1)."; }
    const boundBtn = h("button", { class: "gw-btnx small", type: "button", "data-field": "character_items.bound", onclick: () => { D.bound = D.bound ? 0 : 1; drawBound(); changed(); } });
    const drawBound = () => { boundBtn.textContent = D.bound ? "Bound: can't be traded" : "Tradeable"; boundBtn.classList.toggle("on", !!D.bound); };
    drawBound();
    left.append(h("div", { class: "gw-inv-line" }, stackF), h("div", { class: "gw-inv-line" }, boundBtn));

    // item EXP (Holy Box stored EXP / gear EXP)
    const expMax = isBox ? Math.max(P.meta.holy_boxes[String(pid)] || 0, 2147483647) : 2147483647;
    const expF = GW.numberField({ field: "character_items.item_exp", label: isBox ? "Stored EXP" : gear ? "Gear EXP" : "Item EXP", value: D.item_exp, min: 0, max: expMax, onChange: v => { D.item_exp = v; drawExpBar(); changed(); } });
    const expBar = h("div", { class: "gw-inv-expbar" }, h("span"), h("em"));
    function drawExpBar() {
      const cap = isBox ? P.meta.holy_boxes[String(pid)] : (D.holy_suit_code ? suitNext() : null);
      expBar.classList.toggle("hidden", !cap);
      if (!cap) return;
      expBar.firstChild.style.width = Math.min(100, (Number(D.item_exp) || 0) / cap * 100) + "%";
      expBar.lastChild.textContent = GW.fmt(D.item_exp || 0) + " / " + GW.fmt(cap) + (isBox ? " (Holy Box capacity at rate x1)" : " EXP to the next holy-suit level");
    }
    const expWrap = h("div", { class: "gw-inv-line", "data-help": isBox ? "A Holy Box stores player EXP for the Master Vestment Forger (Holy Box I-V hold 100,000 / 1,000,000 / 10,000,000 / 100,000,000 / 400,000,000 at capacity rate x1). The game shows it as 'Current accumulated EXP'." : gear ? "EXP stored in this piece of gear by the Vestment Forger (Transferring EXP); it is spent on holy-suit upgrades. At most 2,000,000,000." : "Item EXP." }, expF, expBar);
    left.append(expWrap);

    // ---- middle: attribute lines (tooltip order), class / elemental rows, holy suit, sockets list
    const attrBox = h("div", { class: "gw-inv-attrs" });
    const classBox = h("div", { class: "gw-inv-attrs" });
    const suitBox = h("div", { class: "gw-inv-line" });
    const sockList = h("div", { class: "gw-inv-socklist" });
    mid.append(h("h3", { "data-help": "Green attribute amounts use the selected gear grade. Quartz enhancement rank is a separate strength family. Click a line to replace it with the maximum supported family." }, "Append attributes"), attrBox);
    if (suitItem) mid.append(h("h3", { "data-help": "Class-suit pieces (tier III / IV) may carry one class attribute and up to two elemental attributes of different elements (database CHECK). Elemental values grow with the grade: +0.40% per grade (+0.20% for 'chance' attributes)." }, "Class and elemental attributes"), classBox);
    if (gear) mid.append(h("h3", { "data-help": "Holy suit tier and level of this gear (tier x 100 + level, e.g. 403 = Level 3 Platinum). Equipped gear in slots 0-11 adds holy-suit points; the server recomputes them when the equipment changes." }, "Holy suit (Vestment)"), suitBox,
      h("h3", { "data-help": "Holy stones in the sockets. Click a socket on the item picture (or a line here) to add, change or remove a stone. The client shows 4 sockets." }, "Holy sockets"), sockList);
    mid.append(h("div", { class: "gw-inv-line gw-dim gw-inv-small", "data-help": "The reborn server stores no durability and no time limit for items (character_items has no such columns): items never wear out or expire, so there is nothing to edit." }, "Durability / time limit: not stored by this server (items never wear out or expire)."));

    function drawAttrs() {
      GW.clear(attrBox);
      for (let i = 1; i <= 5; i++) {
        const id = D["attribute" + i], lv = D["attribute_level" + i];
        const b = h("button", { class: "gw-inv-attr" + (id === null || id === undefined ? " empty" : ""), type: "button", "data-help": "Append attribute row " + i + " (attribute" + i + " / attribute_level" + i + "). Click to " + (id ? "change or remove it." : "add one.") });
        if (id !== null && id !== undefined) {
          b.append(h("span", { class: "tt-green" }, ((((P.meta.attributes[String(id)] || {}).family_policy || {}).rank) ? "(Lv." + P.meta.attributes[String(id)].family_policy.rank + ") " : "") + attrLabel(id) + " " + attrValue(id, D.item_grade) + " at G" + D.item_grade));
          if (allowed.size && !GW.attributeFamilies.canRoll(P.meta.attributes[String(id)],allowed)) b.append(h("span", { class: "gw-inv-warnmark", "data-help": "This item cannot roll this attribute in game." }, " !"));
        } else b.append(h("span", { class: "gw-dim" }, "+ add append attribute"));
        b.addEventListener("click", () => attrDialog(i));
        attrBox.append(b);
      }
      GW.clear(classBox);
      if (suitItem) {
        const cs = h("select", { class: "gw-inv-sel", "aria-label": "Class attribute", "data-field": "character_items.class_attribute1" }, h("option", { value: "" }, "(no class attribute)"),
          P.meta.class_attributes.filter(a => a.professions.includes(P.st.character.profession) || Number(D.class_attribute1) === a.id).map(a => h("option", { value: a.id, disabled: !a.professions.includes(P.st.character.profession) || null }, attrLabel(a.id) + " #" + a.id + " · G" + D.item_grade + " " + attrValue(a.id, D.item_grade) + " · " + P.meta.attributes[String(a.id)].family_policy.stone_name)));
        cs.value = D.class_attribute1 === null || D.class_attribute1 === undefined ? "" : String(D.class_attribute1);
        cs.addEventListener("change", () => { D.class_attribute1 = cs.value === "" ? null : Number(cs.value); changed(); });
        classBox.append(cs);
        for (const n of [1, 2]) {
          const es = h("select", { class: "gw-inv-sel", "aria-label": "Elemental attribute " + n, "data-field": "character_items.elemental_attribute" + n }, h("option", { value: "" }, "(no elemental attribute " + n + ")"));
          let grp = null, cur = "";
          for (const x of P.meta.elementals) {
            if (x.element !== cur) { cur = x.element; grp = h("optgroup", { label: x.element + " (" + x.effect + ")" }); es.append(grp); }
            grp.append(h("option", { value: x.id }, x.name + " · " + x.family + " · +" + (x.per_grade_bp / 100 * Math.max(1, D.item_grade || 1)).toFixed(2) + "% at G" + Math.max(1, D.item_grade || 1)));
          }
          es.value = D["elemental_attribute" + n] === null || D["elemental_attribute" + n] === undefined ? "" : String(D["elemental_attribute" + n]);
          es.addEventListener("change", () => { D["elemental_attribute" + n] = es.value === "" ? null : Number(es.value); changed(); });
          classBox.append(es);
        }
      }
    }
    const suitField = gear ? GW.idField("suit", D.holy_suit_code || null, { title: "Holy suit tier and level", allowNone: true, field: "character_items.holy_suit_code",
      onChange: v => { D.holy_suit_code = v === null ? 0 : Number(v); drawExpBar(); changed(); } }) : null;
    if (suitField) suitBox.append(suitField);
    function suitNext() {
      const c = D.holy_suit_code;
      if (!c) return null;
      P.suitCache = P.suitCache || {};
      if (P.suitCache[c] === undefined) { P.suitCache[c] = null; GW.catalog.get("suit", c).then(x => { if (P) { P.suitCache[c] = x; drawExpBar(); } }).catch(() => {}); }
      return (P.suitCache[c] && P.suitCache[c].exp_to_next) || null;
    }

    function sockInfo(i) {
      return { eff: D["holy_socket" + i + "_effect_id"], lv: D["holy_socket" + i + "_level"], val: i <= 4 ? D["holy_socket" + i + "_value"] : null };
    }
    function stoneValueText(eff, lv, val) {
      const s = P.meta.spirits[String(eff)];
      if (!s) return "";
      const v = val !== null && val !== undefined ? Number(val) : (s.legacy ? s.legacy[Math.max(1, Math.min(10, lv || 1)) - 1] : null);
      if (v === null) return "";
      return (s.kind === "percent" ? fmtPct(v) : "+" + GW.fmt(v)) + (val === null || val === undefined ? " (default)" : "");
    }
    function drawSockets() {
      const n = Number(D.holy_socket_count) || 0;
      GW.clear(sockList);
      for (let i = 1; i <= 6; i++) {
        const { eff, lv, val } = sockInfo(i);
        const b = sockEls[i - 1];
        GW.clear(b);
        b.className = "gw-inv-sock s" + i + (i > n ? " closed" : eff ? " full" : " open") + (i > 4 ? " beyond" : "");
        const s = eff ? P.meta.spirits[String(eff)] : null;
        if (i <= n && eff) {
          b.append(GW.icon(s && s.sprite !== null ? s.sprite : null, { name: String(eff) }), h("span", { class: "gw-inv-socklv gw-outline" }, lv || "?"));
          b.dataset.help = "Socket " + i + ": Holy Stone Lv." + (lv || "?") + " " + (s ? s.name : "effect " + eff) + " " + stoneValueText(eff, lv, val) + ". Click to change or remove it.";
        } else if (i <= n) {
          b.append(h("span", { class: "gw-inv-sockplus" }, "+"));
          b.dataset.help = "Socket " + i + ": open, no stone (\"Not enchased\"). Click to put a holy stone in it.";
        } else {
          b.append(h("span", { class: "gw-inv-sockplus" }, "+"));
          b.dataset.help = "Socket " + i + ": not drilled. Click to open it" + (i > 4 ? " (sockets 5-6 are stored but the client cannot show them)" : "") + ". In game: socket 1 needs item level 100, socket 2 level 120, sockets 3-4 level 140 and a Socket Spell.";
        }
        if (i <= Math.max(n, 4) || i <= n) {
          sockList.append(h("button", { class: "gw-inv-attr" + (i > n ? " empty" : ""), type: "button", onclick: () => socketDialog(i), "data-help": b.dataset.help },
            i > n ? h("span", { class: "gw-dim" }, "socket " + i + ": not drilled" + (i > 4 ? " (invisible in the client)" : "")) :
              eff ? h("span", { class: "tt-stone" }, "Holy Stone Lv." + (lv || "?") + " " + (s ? s.name : "effect " + eff) + " " + stoneValueText(eff, lv, val)) : h("span", null, "socket " + i + ": Not enchased")));
        }
      }
      sockList.append(h("div", { class: "gw-dim gw-inv-small" }, n + " socket" + (n === 1 ? "" : "s") + " open" + (n > 4 ? " - the client shows only 4" : "")));
    }

    // ---- right: live tooltip + warnings + buttons
    const prev = h("div", { class: "gw-inv-tip" });
    const warnBox = h("div", { class: "gw-inv-warns" });
    const ovr = h("label", { class: "gw-inv-ovr hidden", "data-help": "The database accepts these values but the Godswar Origin client cannot show them (quality above 10, grade above 12, more than 4 sockets, stone values the game never rolls). Tick to save anyway." },
      h("input", { type: "checkbox", onchange: ev => { override = ev.target.checked; } }), " Allow above the client limits");
    const frc = P.meta.allow_force ? h("label", { class: "gw-inv-ovr", "data-help": "Force: skip the slot / kind / stack / mount rules (the editor was started with --allow-force). Only for repairs." },
      h("input", { type: "checkbox", onchange: ev => { force = ev.target.checked; stackF.setRange(1, force ? 32767 : overlap); } }), " force (skip slot and stack rules)") : null;
    const saveBtn = h("button", { class: "gw-btnx", type: "button" }, mode === "live-add" ? "Give item" : "Save");
    const delBtn = !isNew ? h("button", { class: "gw-btnx danger", type: "button", onclick: () => deleteItem(loc, slot, () => m.close()) }, mode === "live-remove" ? "Delete live" : "Delete") : null;
    const quick = h("div", { class: "gw-inv-quick" });
    if (!isNew && mode === "offline") {
      if (gear && loc !== LOC.EQUIP) {
        const ts = slotFor(kind);
        if (ts !== null) quick.append(h("button", { class: "gw-btnx small", type: "button", "data-help": "Move it into its equipment slot (" + P.meta.slots[ts].name + "); an item already there swaps into this cell.", onclick: () => { m.close(); moveItem(loc, slot, LOC.EQUIP, ts); } }, "Equip"));
      }
      if (loc !== LOC.BAG) quick.append(h("button", { class: "gw-btnx small", type: "button", onclick: () => { const s = firstFree(LOC.BAG); if (s === null) return GW.alert("The bag is full."); m.close(); moveItem(loc, slot, LOC.BAG, s); } }, "To bag"));
      if (loc !== LOC.WH) quick.append(h("button", { class: "gw-btnx small", type: "button", onclick: () => { const s = firstFree(LOC.WH); if (s === null) return GW.alert("The warehouse is full (or locked)."); m.close(); moveItem(loc, slot, LOC.WH, s); } }, "To warehouse"));
    }
    right.append(h("h3", { "data-help": "What the game client shows when you hover this item, recomputed as you edit." }, "In-game tooltip (live preview)"), prev, warnBox, ovr, frc || "",
      h("div", { class: "gw-inv-applies" }, GW.appliesChip(mode === "live-add" || mode === "live-remove" ? "live" : "next_login",
        mode === "live-add" ? "basic items live; complete items after logout" : mode === "live-remove" ? "applied now (live)" : "applies at the next login")),
      h("div", { class: "gw-inv-btns" }, saveBtn, delBtn, h("button", { class: "gw-btnx", type: "button", onclick: () => m.close() }, "Cancel")), quick);
    const title = (isNew ? "New item in the " : "Edit item in the ") + where(loc, slot);
    const m = GW.modal(root, { title, wide: true });
    m.el.classList.add("gw-inv-modal");
    if (lockExtras) { for (const x of [attrBox, classBox, suitBox, sockList, expWrap, sockBox]) { x.classList.add("gw-inv-dis"); x.dataset.help = banner || ""; } }
    if (lockAll) { for (const x of [qBox.el, gBox.el, stackF, boundBtn]) { x.classList.add("gw-inv-dis"); } saveBtn.disabled = true; }
    if (mode === "readonly" && delBtn) delBtn.disabled = true;

    let pt = 0, tipSeq = 0;
    function changed() {
      nameEl.textContent = (egg ? apt(D.item_quality).name+" " : "")+(e.name || ("item #" + pid));
      nameEl.style.color = egg ? aptColor(D.item_quality) : GW.qColor(D.item_quality);
      qBox.draw(); gBox.draw(); drawAttrs(); drawSockets(); drawWarnings();
      clearTimeout(pt);
      pt = setTimeout(async () => {
        const my = ++tipSeq;          // a slower, older render must not overwrite a newer one
        try { const t = await GW.render.tooltip("item", pid, inst(D)); if (my === tipSeq) GW.clear(prev).append(t); } catch (err) { if (my === tipSeq) GW.clear(prev).append(h("span", { class: "tt-bad" }, err.message)); }
      }, 60);
    }
    function drawWarnings() {
      const w = [], over = [];
      const cq = Math.max(P.meta.client.quality, Number(e.max_quality) || 0), cg = Math.max(P.meta.client.grade, Number(e.max_grade) || 0);
      if (gear && D.item_quality > cq) over.push("Quality Q" + D.item_quality + " is above what your client shows (Q" + cq + "): the tooltip shows the Q" + cq + " stats" + ([14, 15, 17, 19, 20].includes(Number(D.item_quality)) ? " and the name has no quality colour" : "") + ".");
      if (gear && D.item_grade > cg) over.push("Grade " + D.item_grade + " is above the client's G" + cg + ".");
      for (let i = 1; i <= 5; i++) { const lv = Number(D["attribute_level" + i]); if (D["attribute" + i] !== null && D["attribute" + i] !== undefined && lv > P.meta.client.attribute_level) over.push(attrLabel(D["attribute" + i]) + " level " + lv + " is above the client's L" + P.meta.client.attribute_level + "."); }
      if (D.holy_socket_count > 4) over.push(D.holy_socket_count + " sockets: the client shows 4; sockets 5-6 are invisible in game.");
      if (D.stack > 255) over.push("Count " + D.stack + ": the client shows stack counts only up to 255.");
      for (let i = 1; i <= 4; i++) {
        const { eff, lv, val } = sockInfo(i);
        const s = eff ? P.meta.spirits[String(eff)] : null;
        if (s && s.ranges && val !== null && val !== undefined && lv >= 1 && lv <= 10) {
          const [lo, hi] = s.ranges[lv - 1];
          if (val < lo || val > hi) over.push("Socket " + i + ": value " + val + " is outside what the game rolls at stone level " + lv + " (" + lo + "-" + hi + ").");
        }
      }
      for (let i = 1; i <= Math.min(4, D.holy_socket_count || 0); i++) {
        const need = P.meta.socket_levels[String(i)] || 140;
        if (minlv < need) { w.push("In game, socket " + i + " can only be drilled on items of level " + need + "+ (this one is level " + minlv + ")."); break; }
      }
      for (let i = 1; i <= 5; i++) { const id = D["attribute" + i]; if (id !== null && id !== undefined && allowed.size && !GW.attributeFamilies.canRoll(P.meta.attributes[String(id)],allowed)) { w.push(attrLabel(id) + " is not one of the " + allowed.size + " attributes this item can roll in game."); } }
      if (isBox && D.item_exp > (P.meta.holy_boxes[String(pid)] || Infinity)) w.push("More stored EXP than this Holy Box holds at capacity rate x1.");
      if (!isBox && gear && D.item_exp > P.meta.gear_exp_max) w.push("Gear EXP above 2,000,000,000 is refused by the Vestment Forger's transfer.");
      if (loc === LOC.EQUIP) {
        const ch = P.st.character, cls = (e.classes || []).map(Number);
        if (cls.length && !cls.includes(Number(ch.profession))) w.push((e.name || "This item") + " is for " + (e.class_labels || cls).join(" / ") + "; " + ch.name + " is a " + ch.class + ". The game would not let it be equipped (the server still loads it).");
        if (minlv > Number(ch.level)) w.push((e.name || "This item") + " needs character level " + minlv + "; " + ch.name + " is level " + ch.level + ". The game would not let it be equipped (the server still loads it).");
        if (Number(e.maxlv) && Number(e.maxlv) < Number(ch.level)) w.push((e.name || "This item") + " supports characters only up to level " + e.maxlv + ".");
      }
      if (e.in_client === false) w.push("This item is not in your client: it shows as an empty or broken cell in game.");
      if (D.holy_suit_code && GW.catalog.loadedRow("suit", D.holy_suit_code) === undefined && P.suitsLoaded) over.push("Holy suit code " + D.holy_suit_code + " is not one the client knows.");
      GW.clear(warnBox);
      for (const x of over) warnBox.append(h("div", { class: "gw-inv-warn over" }, "▲ " + x));
      for (const x of w) warnBox.append(h("div", { class: "gw-inv-warn" }, "• " + x));
      ovr.classList.toggle("hidden", !over.length);
    }

    saveBtn.addEventListener("click", async () => {
      if (!current()) { GW.status("The selected character changed. Reopen the item editor.", "err"); return; }
      if (!stackF.valid() || !expF.valid()) { GW.status("fix the red fields first", "err"); return; }
      const item = {};
      for (const f of ITEM_FIELDS) item[f] = D[f] === undefined ? null : D[f];
      saveBtn.disabled = true;
      try {
        const extraFields = ITEM_FIELDS.filter(f => !["prop_id", "stack", "bound", "item_quality", "item_grade"].includes(f));
        const needsFullCreation = mode === "live-add" && (force || extraFields.some(f => ![null, undefined, "", 0].includes(item[f])));
        if (needsFullCreation) {
          const latest = await GW.api("/api/m/online/" + targetCid);
          if (!current()) return;
          context.online = latest;
          if (latest.online) {
            if (!latest.kick) throw new Error("This complete item needs an offline write. Log the player out, then save this draft; no item has been created.");
            if (!await GW.confirm({ title: "Give the complete item?", message: "The player must be logged out to create attributes, sockets, holy suit or item EXP safely. Disconnect them now (the game saves first), then create this fully configured item? No basic placeholder item will be added.", ok: "Log out and give item" })) return;
            if (!current()) return;
            await GW.api("/api/m/kick/" + targetCid, { reason: "create fully configured item" });
            let offline = false;
            for (let attempt = 0; attempt < 30; attempt++) {
              if (!current()) return;
              const state = await GW.api("/api/m/online/" + targetCid);
              if (!current()) return;
              context.online = state;
              if (!state.online) { offline = true; break; }
              await new Promise(resolve => setTimeout(resolve, 500));
            }
            if (!offline) throw new Error("Still waiting for the game to save and log the player out. Your draft is preserved; try Save again when offline.");
          }
        }
        if (!current()) return;
        const r = await context.ctx.api("set/" + targetCid, { location: loc, slot, item, expected: isNew ? (old ? shown(old) : null) : shown(old), override, force });
        if (!current()) return;
        m.close();
        await done(r);
      } catch (err) { const k = await fail(err); if (k === "conflict") m.close(); }
      finally { saveBtn.disabled = false; }
    });

    // One maximum-strength choice per native stone family; existing IDs remain until explicitly changed.
    function attrDialog(i) {
      if (lockExtras) return;
      const original=D["attribute"+i], originalLevel=D["attribute_level"+i];
      let sel=original===null || original===undefined ? null : Number(original);
      let only=allowed.size>0 && (sel===null || GW.attributeFamilies.canRoll(P.meta.attributes[String(sel)],allowed));
      const info=h("div",{class:"gw-inv-attrinfo"}), gridHost=h("div");
      const onlyBox=h("label",{class:"gw-pk-flag"},h("input",{type:"checkbox",checked:only || null,disabled:!allowed.size || null,
        onchange:ev=>{only=ev.target.checked;mkGrid();}})," only what this item can roll");
      function mkGrid() {
        GW.clear(gridHost);
        const select=h("select",{"aria-label":"Attribute family",class:"gw-inv-sel",onchange:ev=>{sel=ev.target.value==="" ? (original===null || original===undefined ? null : Number(original)) : Number(ev.target.value);drawInfo();}},
          h("option",{value:""},original===null || original===undefined ? "Choose a stat family" : "Keep current #"+original));
        let group=null,last=null;
        for(const a of GW.attributeFamilies.choices(P.meta.attributes,allowed,only)) {
          const f=a.family_policy;
          if(f.group!==last){last=f.group;group=h("optgroup",{label:({ordinary:"Attribute stones",mount:"Mount attribute stones",unknown:"Unmapped attributes"})[last] || last});select.append(group);}
          group.append(h("option",{value:a.id},attrLabel(a.id)+" "+attrValue(a.id,D.item_grade)+" · "+(f.stone_name || "unmapped")+(f.max_rank?" · max Lv"+f.rank:f.group==="mount"?" · Epic":"")+" #"+a.id));
        }
        select.value=sel!==null && Number(sel)!==Number(original) && [...select.options].some(o=>o.value===String(sel)) ? String(sel) : "";
        gridHost.append(h("p",{class:"gw-dim"},"One maximum supported strength per stone family. Existing lower-strength IDs are kept unless you choose a replacement."),select);
      }
      function drawInfo() {
        GW.clear(info);
        if(sel===null){info.append(h("p",null,"Choose a stat family."));return;}
        const a=P.meta.attributes[String(sel)] || {}, f=a.family_policy || {};
        info.append(h("div",{class:"tt-green"},attrLabel(sel)+" "+attrValue(sel,D.item_grade)+" at G"+D.item_grade),
          h("p",{class:"gw-dim"},"#"+sel+" · "+(f.stone_name || "Unmapped family; no level inferred")+(f.rank?" · Quartz enhancement Lv"+f.rank+"/"+f.max_rank:" · no enhancement level")),
          h("p",null,"Amounts scale with gear grade. Class-suit stones have their own separate row below the ordinary attributes."));
        if(allowed.size && !GW.attributeFamilies.canRoll(P.meta.attributes[String(sel)],allowed))info.append(h("p",{class:"gw-inv-warn"},"This item cannot roll this attribute in game."));
      }
      function apply(){D["attribute"+i]=sel;D["attribute_level"+i]=sel===null?null:GW.attributeFamilies.levelFor(P.meta.attributes[String(sel)] || {id:sel},original,originalLevel);dm.close();changed();}
      const dm=GW.modal(h("div",{class:"gw-mod-inventory gw-inv-attrdlg"},onlyBox,gridHost,info,h("div",{class:"gw-inv-btns"},
        h("button",{class:"gw-btnx",type:"button",onclick:()=>{if(sel!==null)apply();}},"Apply"),
        h("button",{class:"gw-btnx danger",type:"button",onclick:()=>{sel=null;apply();}},"Remove"),
        h("button",{class:"gw-btnx",type:"button",onclick:()=>dm.close()},"Cancel"))),{title:"Append attribute "+i+" — stone families",wide:true});
      mkGrid();drawInfo();
    }

    // ---- the socket editor (stone picker with icons, level 1-10, rolled value)
    function socketDialog(i) {
      if (lockExtras) return;
      if (!gear) { GW.alert("Holy sockets are for equipment.", "No sockets"); return; }
      const n = Number(D.holy_socket_count) || 0;
      let { eff, lv, val } = sockInfo(i);
      eff = eff || null; lv = lv || 1;
      let useDefault = val === null || val === undefined;
      const hasDefault = x => { const s0 = P.meta.spirits[String(x)]; return !!(s0 && s0.legacy); };
      if (eff && !hasDefault(eff)) useDefault = false;
      const info = h("div", { class: "gw-inv-attrinfo" });
      const lvIn = h("input", { type: "range", min: 1, max: 10, value: lv, class: "gw-inv-range", "aria-label": "Stone level" });
      const valTxt = h("b", { class: "gw-value" }), valWarn = h("div", { class: "gw-inv-warn" });
      const valF = GW.numberField({ label: "Rolled value", value: val, min: 1, max: 32767, onChange: v => { val = v; showVal(); } });
      function showVal() {
        const s = P.meta.spirits[String(eff)] || {};
        const rg = s.ranges ? s.ranges[lv - 1] : null;
        valTxt.textContent = " = " + (s.kind === "percent" ? fmtPct(val || 0) : "+" + GW.fmt(val || 0));
        valWarn.textContent = rg && val !== null && (val < rg[0] || val > rg[1]) ? "Outside the game's roll range: saving needs 'Allow above the client limits'." : "";
      }
      const gridHost = h("div", { class: "gw-inv-gridhost" });
      const g = GW.grid({ type: "holystone", value: eff, height: "260px", filter: r => Number(r.id) > 0, onSelect: r => { eff = Number(r.id); drawInfo(true); }, onPick: r => { eff = Number(r.id); apply(); } });
      gridHost.append(g.el);
      function drawInfo(resetVal) {
        GW.clear(info);
        if (i > n) info.append(h("div", { class: "gw-inv-warn" }, "Socket " + i + " is not drilled yet: applying opens sockets 1-" + i + "." + (i > 4 ? " Sockets 5-6 are invisible in the client." : "")));
        if (!eff) { info.append(h("span", { class: "gw-dim" }, "Pick a holy stone on the left.")); return; }
        const s = P.meta.spirits[String(eff)] || {};
        const rg = s.ranges ? s.ranges[lv - 1] : null;
        if (!s.legacy) useDefault = false;          // no fixed value for this stone: the client would show 0
        if (resetVal && rg && (val === null || val === undefined || val < rg[0] || val > rg[1])) val = rg[1];
        info.append(h("div", { class: "tt-stone" }, GW.icon(s.sprite, { name: String(eff) }), " ", s.name || ("effect " + eff)),
          h("div", { class: "gw-inv-small" }, s.desc || ""),
          h("div", { class: "gw-dim gw-inv-small" }, (s.family || "") + " stone · " + (s.kind === "percent" ? "value in hundredths of a percent (320 = 3.20%)" : "flat value")),
          h("div", { class: "gw-inv-line" }, h("span", { class: "gw-label", "data-field": "character_items.holy_socket" + i + "_level" }, "Stone level "), lvIn, " ", h("b", { class: "gw-value" }, "Lv " + lv)));
        if (i <= 4) {
          const def = s.legacy ? s.legacy[lv - 1] : null;
          const r1 = h("label", { class: "gw-pk-flag" }, h("input", { type: "radio", name: "gwinvval", checked: useDefault ? true : null, disabled: def === null ? true : null, onchange: () => { useDefault = true; drawInfo(false); } }),
            " game default: " + (def === null ? "none for this stone" : (s.kind === "percent" ? fmtPct(def) : "+" + GW.fmt(def))));
          const r2 = h("label", { class: "gw-pk-flag" }, h("input", { type: "radio", name: "gwinvval", checked: useDefault ? null : true, onchange: () => { useDefault = false; if (val === null && rg) val = rg[1]; drawInfo(false); } }),
            " rolled value" + (rg ? " (the game rolls " + (s.kind === "percent" ? fmtPct(rg[0]) + " - " + fmtPct(rg[1]) : rg[0] + " - " + rg[1]) + " at Lv " + lv + ")" : ""));
          info.append(h("div", { class: "gw-inv-line", "data-field": "character_items.holy_socket" + i + "_value" }, r1, r2));
          if (!useDefault) {
            valF.value = val; if (rg) valF.setRange(rg[0], rg[1]);
            info.append(h("div", { class: "gw-inv-line" }, valF, valTxt), valWarn);
            showVal();
          }
        } else info.append(h("div", { class: "gw-dim gw-inv-small" }, "Sockets 5-6 store no value."));
      }
      lvIn.addEventListener("input", () => { lv = Number(lvIn.value); drawInfo(true); lvIn.focus(); });
      function apply() {
        if (!eff) return;
        if (i > n) D.holy_socket_count = i;
        D["holy_socket" + i + "_effect_id"] = eff; D["holy_socket" + i + "_level"] = lv;
        if (i <= 4) D["holy_socket" + i + "_value"] = useDefault ? null : (val === null ? null : Number(val));
        dm.close(); changed();
      }
      function clearStone() {
        D["holy_socket" + i + "_effect_id"] = null; D["holy_socket" + i + "_level"] = null;
        if (i <= 4) D["holy_socket" + i + "_value"] = null;
      }
      const btns = h("div", { class: "gw-inv-btns" }, h("button", { class: "gw-btnx", type: "button", onclick: apply }, i > n ? "Open + set stone" : "Apply"));
      if (i <= n) btns.append(h("button", { class: "gw-btnx danger", type: "button", onclick: () => { clearStone(); dm.close(); changed(); } }, "Remove stone"));
      if (i === n) btns.append(h("button", { class: "gw-btnx", type: "button", "data-help": "Close this socket (the socket count goes down by one; its stone is removed).", onclick: () => { clearStone(); D.holy_socket_count = n - 1; dm.close(); changed(); } }, "Close socket"));
      if (i > n) btns.append(h("button", { class: "gw-btnx", type: "button", "data-help": "Drill sockets up to this one without a stone (\"Not enchased\").", onclick: () => { D.holy_socket_count = i; dm.close(); changed(); } }, "Open empty"));
      btns.append(h("button", { class: "gw-btnx", type: "button", onclick: () => dm.close() }, "Cancel"));
      const dm = GW.modal(h("div", { class: "gw-mod-inventory gw-inv-attrdlg" }, h("div", { class: "gw-inv-dlgcols" }, gridHost, h("div", null, info, btns))),
        { title: "Holy socket " + i + " of " + (e.name || "item"), wide: true });
      drawInfo(false);
    }

    drawAttrs(); drawExpBar(); changed();
  }

  // stepper: [<] value [>] + a range slider marking the client limit
  function stepper(label, field, lo, hi, get, set, render, clientMax, gear, onChange) {
    const val = h("span", { class: "gw-inv-stepval" });
    const range = h("input", { type: "range", min: lo, max: hi, class: "gw-inv-range", "aria-label": label });
    const mark = h("span", { class: "gw-inv-capmark", style: "left:" + ((clientMax - lo) / (hi - lo) * 100) + "%", "data-help": "The client draws up to " + label.toLowerCase() + " " + clientMax + "." });
    const el = h("div", { class: "gw-inv-stepper" },
      h("span", { class: "gw-label", "data-field": field }, label),
      h("button", { class: "gw-btnx small", type: "button", "aria-label": label + " down", onclick: () => { set(Math.max(lo, get() - 1)); fire(); } }, "◀"),
      val,
      h("button", { class: "gw-btnx small", type: "button", "aria-label": label + " up", onclick: () => { set(Math.min(hi, get() + 1)); fire(); } }, "▶"),
      h("span", { class: "gw-inv-rangewrap" }, range, gear ? mark : null));
    function fire() { onChange(); }
    range.addEventListener("input", () => { set(Number(range.value)); fire(); });
    return { el, draw() { const v = get(); GW.clear(val).append(render(v)); range.value = v; el.classList.toggle("over", gear && v > clientMax); } };
  }

  // ------------------------------------------------------------ module
  GW.module("inventory", {
    async mount(el, ctx) {
      P = { ctx, cid: ctx.cid, root: el, tab: "equip", wbox: 0, online: null };
      el.append(GW.onlineBanner(ctx.cid, { onState: st => { if (!P) return; const was = P.online && P.online.online; P.online = st; applyOnline(); if (was !== undefined && was !== null && was !== st.online) reload(0); } }));
      P.toolbar = h("div", { class: "gw-inv-toolbar" });
      P.equipWin = h("section", { class: "gw-frame-normalwindow gw-text gw-inv-win gw-inv-equipwin" });
      P.bagWin = h("section", { class: "gw-frame-normalwindow gw-text gw-inv-win gw-inv-bagwin" });
      P.whWin = h("section", { class: "gw-frame-normalwindow gw-text gw-inv-win gw-inv-whwin" });
      P.libWin = h("section", { class: "gw-frame-normalwindow gw-text gw-inv-win gw-inv-libwin" });
      P.tempBox = h("div");
      el.append(P.toolbar, h("div", { class: "gw-inv-main" }, h("div", { class: "gw-inv-colL" }, P.equipWin), h("div", { class: "gw-inv-colM" }, P.bagWin, P.whWin, P.tempBox), h("div", { class: "gw-inv-colR" }, P.libWin)));
      el.append(h("p", { class: "gw-dim" }, "Loading the character's items ..."));
      try {
        [P.meta, P.st] = await Promise.all([ctx.api("meta"), ctx.api("state/" + ctx.cid), GW.catalog.load("item"), GW.catalog.load("appendattr")]);
      } catch (e) { el.append(h("p", { class: "tt-bad" }, e.message)); return; }
      el.lastChild.remove();
      GW.catalog.load("suit").then(() => { if (P) P.suitsLoaded = true; }).catch(() => {});
      GW.catalog.load("holystone").catch(() => {});
      P.items = new Map(P.st.items.map(r => [key(r.item_location, r.slot_index), r]));
      P.online = P.st.online;
      // the library: every item, draggable onto any cell
      P.libWin.append(h("div", { class: "gw-wtitle" }, "Item library"),
        h("p", { class: "gw-dim gw-inv-small" }, "Every item of the game. Drag one onto a cell, or double-click it to put it in the first free bag cell. Hover for the in-game tooltip."));
      const lib = GW.grid({ type: "item", height: "calc(100vh - 330px)", onPick: r => addAnywhere(r.id) });
      for (const evn of ["mouseover", "pointerdown", "focusin"]) lib.el.addEventListener(evn, ev => { const t = ev.target.closest && ev.target.closest(".gw-pk-tile"); if (t && !t.draggable) t.draggable = true; });
      lib.el.addEventListener("dragstart", ev => { const t = ev.target.closest(".gw-pk-tile"); if (!t || !t._row) return; ev.dataTransfer.setData(DT_NEW, String(t._row.id)); ev.dataTransfer.effectAllowed = "copy"; GW.hideTip(); });
      P.libWin.append(lib.el);
      P.lib = lib;
      // any change (an Undo from the Change log, another page) re-reads the bag; debounced, own saves already reload
      P.offChange = GW.on("change", () => { if (!P || P.busy) return; clearTimeout(P.chT); P.chT = setTimeout(() => reload(0), 400); });
      drawAll();
    },
    unmount() {
      if (P && P.offChange) P.offChange();
      P = null;
    },
  });
})();
