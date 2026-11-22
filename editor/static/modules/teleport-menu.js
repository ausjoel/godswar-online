/* static/modules/teleport-menu.js - B22 P1 (d32) teleporter menu editor for the NPC page (npcs.js loads this file).
   GWTeleMenu.panel({ ctx, npc, maps, pointPicker, onSaved }) -> element
   The buttons of Sparta's, Athens' and Mycenae's teleporters: order (arrows), show / hide, a price and currency per
   button, and new destinations (a free button number, a map and a point). Backend: modules/_teleport_menu.py. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const CUR = { silver: "Silver", gold: "Gold", bgold: "B-Gold" };
  const strip = t => String(t || "").replace(/\|c[0-9A-Fa-f]{8}|\|r/g, "").replace(/^\*/, "");

  function panel(o) {
    const box = h("div", { class: "gw-npc-tmenu" }, h("p", { class: "gw-dim" }, "Loading the teleporter menu ..."));
    let M = null, rows = [];

    async function load() {
      try { M = await o.ctx.api("teleport/menu/" + o.npc); }
      catch (e) { GW.clear(box).append(h("p", { class: "gw-dim" }, "Menu editing: " + e.message)); return; }
      const byPos = M.rows.slice().sort((a, b) => a.position - b.position);
      rows = byPos.length ? byPos.map(r => ({ sub: r.sub_id, hidden: r.hidden, price: r.price, currency: r.currency, map: r.target_map_id,
        x: r.pos_x, z: r.pos_z, level: r.minimum_level, name: r.display_name }))
        : M.compiled.map(c => ({ sub: c.sub, hidden: false, price: null, currency: "silver", map: null, x: null, z: null, level: null, name: null }));
      draw();
    }
    const info = sub => (M.choices || []).find(c => c.sub === sub) || (M.compiled || []).find(c => c.sub === sub) || { sub };

    function draw() {
      GW.clear(box);
      const shown = rows.filter(r => !r.hidden).length;
      box.append(h("h4", null, "Menu: buttons, order and price"),
        h("p", { class: "gw-dim" }, M.rows.length ? "Edited menu. " : "Original menu (nothing edited yet). ",
          shown + " of " + M.maximum_buttons + " buttons shown. Use the arrows to reorder, untick to hide a button, and set a price. " +
          "An empty price keeps the original fee (20,000 Silver for Mycenae from a capital, otherwise free); 0 makes it free."));
      const list = h("div", { class: "gw-npc-tmenu-list" });
      rows.forEach((r, i) => {
        const c = info(r.sub);
        const label = c.label ? strip(c.label) : (r.name || c.name || "button " + r.sub);
        const price = h("input", { type: "number", min: 0, value: r.price ?? "", placeholder: c.fee ? String(c.fee) : "free", class: "gw-npc-num", "aria-label": "Price", oninput: e => { r.price = e.target.value === "" ? null : Number(e.target.value); } });
        const cur = h("select", { "aria-label": "Currency", onchange: e => { r.currency = e.target.value; } }, Object.entries(CUR).map(([k, v]) => h("option", { value: k }, v)));
        cur.value = r.currency || "silver";
        const lv = h("input", { type: "number", min: 1, max: 255, value: r.level ?? "", placeholder: c.level ? String(c.level) : "1", class: "gw-npc-num", "aria-label": "Minimum level", oninput: e => { r.level = e.target.value === "" ? null : Number(e.target.value); } });
        const show = h("input", { type: "checkbox", "aria-label": "Show this button", onchange: e => { r.hidden = !e.target.checked; draw(); } });
        show.checked = !r.hidden;
        const move = d => { const j = i + d; if (j < 0 || j >= rows.length) return; [rows[i], rows[j]] = [rows[j], rows[i]]; draw(); };
        const isNew = !c.compiled_here && !c.borrowed;
        const chips = [
          c.borrowed ? h("span", { class: "gw-chip", "data-help": "Another teleporter has this destination; this one copies it." }, "copied from another teleporter") : null,
          isNew ? h("span", { class: "gw-chip", "data-help": "A destination of your own: it goes to the map and point below." }, "new destination") : null,
          !c.label ? h("span", { class: "gw-chip tt-bad", "data-help": "The current client has no words for this button number, so players do not see it until a client update adds them." }, "no words in the client yet") : null];
        const item = h("div", { class: "gw-npc-tele" + (r.hidden ? " gw-dim" : "") },
          h("div", { class: "gw-npc-tele-head" },
            h("button", { class: "gw-btnx small", type: "button", "aria-label": "Move up", onclick: () => move(-1) }, "↑"),
            h("button", { class: "gw-btnx small", type: "button", "aria-label": "Move down", onclick: () => move(1) }, "↓"),
            h("label", null, show, " show"),
            h("span", { class: "gw-npc-tele-label" }, label), ...chips,
            h("span", { class: "gw-dim" }, " button " + r.sub + (c.name ? " → " + c.name : ""))),
          h("div", { class: "gw-npc-tele-form gw-npc-tmenu-form" },
            h("label", null, "Price ", price, " ", cur), h("label", null, "Minimum level ", lv),
            isNew || r.map !== null ? targetForm(r) : null,
            isNew ? h("button", { class: "gw-btnx small", type: "button", onclick: () => { rows.splice(i, 1); draw(); } }, "Remove") : null));
        list.append(item);
      });
      const free = [];
      for (let s = M.slot_first; s <= M.slot_last; s++) if (!rows.some(r => r.sub === s)) free.push(s);
      const others = (M.choices || []).filter(c => !c.compiled_here && c.borrowed && !rows.some(r => r.sub === c.sub));
      const addSel = h("select", { "aria-label": "Add a button" }, h("option", { value: "" }, "+ Add a button ..."),
        others.map(c => h("option", { value: c.sub }, (c.label ? strip(c.label) : c.name) + " (button " + c.sub + ", from another teleporter)")),
        free.map(s => { const c = info(s); return h("option", { value: s }, "New destination (button " + s + (c.label ? ": " + strip(c.label) : ", words come with a client update") + ")"); }));
      addSel.addEventListener("change", () => {
        const s = Number(addSel.value); if (!s) return;
        rows.push({ sub: s, hidden: false, price: null, currency: "silver", map: info(s).borrowed ? null : M.source_map, x: null, z: null, level: null, name: null });
        draw();
      });
      box.append(list, h("div", { class: "gw-inv-btns" }, addSel,
        h("button", { class: "gw-btnx", type: "button", onclick: save }, "Save menu"),
        M.rows.length ? h("button", { class: "gw-btnx", type: "button", onclick: reset }, "Reset to original menu") : null));
    }

    function targetForm(r) {
      const map = h("select", { "aria-label": "Destination map" }, (o.maps || []).map(m => h("option", { value: m.map_id }, m.name + " (" + m.map_id + ")")));
      map.value = String(r.map ?? M.source_map);
      if (r.map === null) r.map = Number(map.value);
      const x = h("input", { type: "number", value: r.x ?? "", class: "gw-npc-num", "aria-label": "X", oninput: e => { r.x = e.target.value; } });
      const z = h("input", { type: "number", value: r.z ?? "", class: "gw-npc-num", "aria-label": "Z", oninput: e => { r.z = e.target.value; } });
      const name = h("input", { type: "text", maxlength: 48, value: r.name ?? "", placeholder: "name in the editor and in messages", "aria-label": "Name", oninput: e => { r.name = e.target.value; } });
      const picker = o.pointPicker ? o.pointPicker({ map: Number(map.value), x: x.value, z: z.value, onPick: (px, pz) => { x.value = px; z.value = pz; r.x = px; r.z = pz; } }) : null;
      map.addEventListener("change", () => { r.map = Number(map.value); if (picker) picker.setMap(r.map); });
      return h("div", { class: "gw-npc-tele-body" }, picker ? picker.el : null,
        h("div", { class: "gw-npc-tele-form" }, h("label", null, "Map ", map), h("label", null, "X ", x), h("label", null, "Z ", z), h("label", null, "Name ", name)));
    }

    async function save() {
      try {
        const r = await o.ctx.api("teleport/menu/save", { npc: o.npc, version: M.version, rows });
        GW.writeDone(r, "Teleporter menu saved");
        (r.warnings || []).forEach(w => GW.status(w, "warn"));
        await load(); if (o.onSaved) o.onSaved();
      } catch (e) { await GW.writeError(e); }
    }
    async function reset() {
      try { GW.writeDone(await o.ctx.api("teleport/menu/reset", { npc: o.npc, version: M.version }), "Teleporter menu reset"); await load(); if (o.onSaved) o.onSaved(); }
      catch (e) { await GW.writeError(e); }
    }
    load();
    return box;
  }

  window.GWTeleMenu = { panel };
})();
