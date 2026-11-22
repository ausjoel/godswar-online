/* static/modules/_gear_ui.js - B21 item 26: in-game-looking item slots for the worn-gear, gear-stats and mount-gear pages.
   Not a module of its own (the leading _ keeps it out of the sidebar); a page loads it with GW.gearUI_load().
   GW.gearUI.doll({tab, rows, after, onClick, mark})  the 12 worn slots (tab "equip") or the mount slots (tab "mount") at the
                                                       inventory paperdoll positions; each cell has the game tooltip
   GW.gearUI.bag({rows, onClick, mark, pages})        the 96-cell kit bag in four pages of 24 (and the warehouse if asked)
   GW.gearUI.inst(row)                                a character_items row -> the tooltip instance (stats, class stone,
                                                       sockets, holy suit), so a proposed row shows its after-state
   GW.gearUI.state(cid)                               the character's items (inventory state) with the item catalogue loaded */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  // equipment slot -> [name, x, y] (modules/inventory.py SLOTS, the bag window's paperdoll)
  const EQUIP = { 0: ["Head", 123, 8], 1: ["Amulet", 204, 8], 2: ["Gloves", 42, 92], 3: ["Armor", 123, 50], 4: ["Cuffs", 42, 50],
    5: ["Girdle", 123, 92], 6: ["Shoes", 123, 176], 7: ["Leggings", 123, 134], 8: ["Ring", 204, 50], 9: ["Ring", 204, 92],
    10: ["Weapon", 42, 166], 11: ["Shield", 205, 166] };
  const MOUNT = { 20: ["Mount", 123, 30], 15: ["Coronet", 19, 140], 16: ["Armor", 71, 140], 17: ["Soul", 123, 140], 18: ["Saddle", 175, 140],
    19: ["Tassels", 227, 140] };
  const LOC = { EQUIP: 0, BAG: 1, WH: 3 };

  function inst(row) {
    if (!row) return null;
    const attrs = [];
    for (let i = 1; i <= 5; i++) if (row["attribute" + i] !== null && row["attribute" + i] !== undefined) attrs.push({ id: row["attribute" + i], level: row["attribute_level" + i] });
    // the class stone stat is its own row in the database; the game lists it with the green stats
    if (row.class_attribute1 !== null && row.class_attribute1 !== undefined) attrs.push({ id: row.class_attribute1, level: null });
    const sockets = [];
    for (let i = 1; i <= 6; i++) sockets.push({ effect: row["holy_socket" + i + "_effect_id"], level: row["holy_socket" + i + "_level"], value: i <= 4 ? row["holy_socket" + i + "_value"] : null });
    return { item_quality: row.item_quality, item_grade: row.item_grade, bound: row.bound, stack: row.stack, attrs,
      holy_suit_code: row.holy_suit_code, holy_socket_count: Math.min(4, row.holy_socket_count || 0), sockets, item_exp: row.item_exp };
  }
  function sprite(id) { const r = GW.catalog.loadedRow("item", id); return r ? r.sprite : null; }
  function itemName(id) { const r = GW.catalog.loadedRow("item", id); return r ? r.name : "item #" + id; }

  function cell(row, o) {
    o = o || {};
    if (!row) return GW.cell({ empty: true, label: (o.caption || "") + ": empty", help: o.help || ((o.caption || "This slot") + " is empty." + (o.onClick ? " Click to choose." : "")),
      onclick: o.onClick || null, class: "gw-gearui-cell" + (o.mark ? " " + o.mark : "") });
    const c = GW.cell({ sprite: row.sprite !== undefined ? row.sprite : sprite(row.prop_id), name: row.name || itemName(row.prop_id), quality: row.item_quality,
      count: row.stack, tipType: "item", tipId: row.prop_id, tipInst: inst(row), label: (o.caption ? o.caption + ": " : "") + (row.name || itemName(row.prop_id)),
      onclick: o.onClick || null, class: "gw-gearui-cell" + (o.mark ? " " + o.mark : "") });
    if (row.item_grade) c.append(h("span", { class: "gw-gearui-grade gw-outline" }, "G" + row.item_grade));
    return c;
  }

  function doll(o) {
    const layout = o.tab === "mount" ? MOUNT : EQUIP;
    const box = h("div", { class: "gw-gearui-doll", "data-tab": o.tab || "equip" });
    if ((o.tab || "equip") === "equip") box.append(h("span", { class: "gw-sprite gw-paperdoll gw-gearui-figure", "aria-hidden": "true" }));
    for (const [slot, [name, x, y]] of Object.entries(layout)) {
      const s = Number(slot), row = (o.rows || {})[s];
      const c = cell(row, { caption: name, mark: o.mark ? o.mark(s, row) : "", onClick: o.onClick ? () => o.onClick(s, row) : null,
        help: o.help ? o.help(s, row) : null });
      c.classList.add("gw-gearui-abs");
      c.style.left = x + "px"; c.style.top = y + "px";
      if (!row) c.append(h("span", { class: "gw-gearui-cap" }, name));
      box.append(c);
    }
    if (o.title) return h("div", { class: "gw-gearui-dollwrap" }, h("div", { class: "gw-gearui-dolltitle" }, o.title), box);
    return box;
  }

  function bag(o) {
    const rows = new Map((o.rows || []).map(r => [r.slot_index, r]));
    const per = 24, pages = o.pages || 4;
    let page = o.page || 0;
    const grid = h("div", { class: "gw-gearui-bag" }), tabs = h("div", { class: "gw-gearui-bagtabs", role: "tablist" });
    function draw() {
      GW.clear(grid); GW.clear(tabs);
      for (let p = 0; p < pages; p++) {
        const n = [...rows.keys()].filter(s => s >= p * per && s < (p + 1) * per).length;
        tabs.append(h("button", { class: "gw-tabx" + (p === page ? " on" : ""), type: "button", role: "tab", "aria-selected": p === page ? "true" : "false",
          onclick: () => { page = p; draw(); } }, (o.label || "Bag") + " " + (p + 1) + " (" + n + ")"));
      }
      for (let s = page * per; s < (page + 1) * per; s++) {
        const row = rows.get(s);
        grid.append(cell(row, { caption: (o.label || "Bag") + " cell " + (s + 1), mark: o.mark ? o.mark(s, row) : "",
          onClick: row && o.onClick ? () => o.onClick(s, row) : null, help: row ? null : "Empty cell " + (s + 1) + "." }));
      }
    }
    draw();
    return h("div", { class: "gw-gearui-bagwrap" }, tabs, grid);
  }

  async function state(cid) {
    const [st] = await Promise.all([GW.api("/api/m/inventory/state/" + cid), GW.catalog.load("item")]);
    const at = loc => (st.items || []).filter(r => r.item_location === loc);
    const worn = {};
    for (const r of at(LOC.EQUIP)) worn[r.slot_index] = r;
    return { raw: st, worn, bag: at(LOC.BAG).filter(r => r.slot_index >= 0 && r.slot_index < 96), warehouse: at(LOC.WH) };
  }

  GW.gearUI = { inst, cell, doll, bag, state, EQUIP, MOUNT, LOC };
  if (!document.querySelector('link[href^="/static/modules/_gear_ui.css"]'))
    document.head.append(h("link", { rel: "stylesheet", href: "/static/modules/_gear_ui.css" }));
})();
