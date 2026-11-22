/* static/modules/npcs-extra.js - B21 items 27 and 28 for the NPC page (static/modules/npcs.js loads this file).
   GWNpcExtra.shopGrid(o)   the shop as the game's NPC trade window: tabs, a 10 x 5 grid of item cells, drag to reorder,
                            sort, add, remove, and the selected item's price / tab below the grid.
   GWNpcExtra.mapView(o)    every NPC of a map drawn on the map picture; click a dot to open that NPC.
   GWNpcExtra.pointPicker(o) a small map of the destination map; click it to set a teleporter's X / Z.
   Maps use the world-aligned picture of static/graphics/gw-art.js: x -256..256 left to right, z 256..-256 top to bottom. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const COLS = 10, ROWS = 5, PER_TAB = COLS * ROWS;      // NpcTrade.xml: Bag0..Bag3, 10 x 5 cells of 42 px

  let artP = null;
  function art() {
    if (window.GWArt) return Promise.resolve(window.GWArt);
    if (!artP) artP = new Promise(r => { const s = document.createElement("script"); s.src = "/static/graphics/gw-art.js"; s.onload = () => r(window.GWArt || null); s.onerror = () => r(null); document.head.append(s); });
    return artP;
  }
  async function underlay(mapId) { const A = await art(); return A ? A.underlay(mapId).catch(() => null) : null; }

  // ------------------------------------------------------------------------------------------------ shop grid
  const SORTS = {
    price: (a, b) => a.price - b.price,
    price_desc: (a, b) => b.price - a.price,
    name: (a, b, n) => n(a).localeCompare(n(b)),
    id: (a, b) => a.item - b.item,
  };
  function shopGrid(o) {
    const st = o.state;                        // {tab, sel} kept across redraws by the caller
    const rows = o.rows, tabs = o.tabs.length ? o.tabs : [0];
    if (!tabs.includes(st.tab)) st.tab = tabs[0];
    const nameOf = r => (o.irow(r.item) || {}).name || "item #" + r.item;
    const inTab = t => rows.map((r, i) => [r, i]).filter(([r]) => r.category === t);
    const wrap = h("div", { class: "gw-npc-shop" });
    // tabs (the game window's Bag tabs); dropping an item on a tab moves it to the end of that tab
    const tabBar = h("div", { class: "gw-npc-shop-tabs", role: "tablist" });
    tabs.forEach((t, k) => {
      const n = inTab(t).length;
      const b = h("button", { type: "button", role: "tab", class: "gw-tab" + (t === st.tab ? " on" : ""), "aria-selected": t === st.tab ? "true" : "false",
        title: n + " items" + (o.tabs.length > 1 ? "; drop an item here to move it to this tab" : ""), onclick: () => { st.tab = t; st.sel = null; o.redraw(); } },
        "Tab " + (k + 1) + " ", h("small", null, "(" + n + ")"));
      if (o.tabs.length > 1) {
        b.addEventListener("dragover", e => { if (st.drag !== undefined && st.drag !== null && rows[st.drag] && rows[st.drag].category !== t) { e.preventDefault(); b.classList.add("gw-npc-drop-tab"); } });
        b.addEventListener("dragleave", () => b.classList.remove("gw-npc-drop-tab"));
        b.addEventListener("drop", e => { e.preventDefault(); const from = st.drag; st.drag = null; if (from === null || from === undefined) return; moveToTab(from, t); });
      }
      tabBar.append(b);
    });
    function moveToTab(i, t) {
      const [x] = rows.splice(i, 1); x.category = t;
      let k = rows.length; while (k > 0 && rows[k - 1].category !== t && tabs.indexOf(rows[k - 1].category) > tabs.indexOf(t)) k--;
      // after the last row of tab t (or where tab t would start)
      const last = rows.map(r => r.category).lastIndexOf(t);
      rows.splice(last >= 0 ? last + 1 : k, 0, x);
      st.tab = t; st.sel = rows.indexOf(x); o.dirty();
    }
    const items = inTab(st.tab);
    // toolbar: add, sort, remove
    const sortSel = h("select", { "aria-label": "Sort this tab" }, h("option", { value: "" }, "Sort this tab by ..."),
      h("option", { value: "price" }, "price, low to high"), h("option", { value: "price_desc" }, "price, high to low"),
      h("option", { value: "name" }, "name, A to Z"), h("option", { value: "id" }, "item number"));
    sortSel.onchange = () => {
      const f = SORTS[sortSel.value]; if (!f) return;
      const idx = items.map(([, i]) => i), sorted = items.map(([r]) => r).slice().sort((a, b) => f(a, b, nameOf));
      idx.forEach((ri, k) => { rows[ri] = sorted[k]; }); st.sel = null; o.dirty();
    };
    const removeSel = () => { if (st.sel === null || !rows[st.sel]) return; rows.splice(st.sel, 1); st.sel = null; o.dirty(); };
    wrap.append(tabBar, h("div", { class: "gw-npc-bar" },
      h("button", { class: "gw-btnx", type: "button", onclick: () => o.add(st.tab) }, "+ Add items to this tab"), sortSel,
      h("button", { class: "gw-btnx", type: "button", disabled: st.sel === null, onclick: removeSel, title: "Remove the selected item (Delete key)" }, "Remove selected")));
    // the grid
    const grid = h("div", { class: "gw-npc-grid", role: "grid", "aria-label": "Shop tab, " + items.length + " items", tabindex: 0 });
    grid.addEventListener("keydown", e => { if ((e.key === "Delete" || e.key === "Backspace") && st.sel !== null) { e.preventDefault(); removeSel(); } });
    const pages = Math.max(1, Math.ceil(items.length / PER_TAB));
    for (let c = 0; c < pages * PER_TAB; c++) {
      const it = items[c];
      if (!it) { grid.append(h("div", { class: "gw-npc-cell empty", "aria-hidden": "true" })); continue; }
      const [r, i] = it, info = o.irow(r.item) || {}, cur = o.charge(r);
      const cell = h("div", { class: "gw-npc-cell" + (st.sel === i ? " on" : "") + (c >= PER_TAB ? " over" : ""), role: "gridcell", draggable: "true", tabindex: -1,
        "aria-label": nameOf(r) + ", " + r.price + " " + o.label(cur),
        "data-help": nameOf(r) + " #" + r.item + "\nPrice: " + r.price.toLocaleString("en-US") + " " + o.label(cur) + "\nSlot " + (c + 1) + " of this tab. Drag to another slot to move it.",
        onclick: () => { st.sel = i; o.redraw(); } },
        GW.icon(info.sprite, { name: info.name }), h("span", { class: "gw-npc-price gw-cur-" + cur }, short(r.price)));
      cell.addEventListener("dragstart", e => { st.drag = i; cell.classList.add("gw-npc-dragging"); e.dataTransfer.effectAllowed = "move"; try { e.dataTransfer.setData("text/plain", String(i)); } catch (_) {} });
      cell.addEventListener("dragend", () => { st.drag = null; cell.classList.remove("gw-npc-dragging"); });
      cell.addEventListener("dragover", e => { if (st.drag === null || st.drag === undefined || st.drag === i || !rows[st.drag] || rows[st.drag].category !== r.category) return; e.preventDefault(); const b = cell.getBoundingClientRect(); const after = e.clientX > b.left + b.width / 2; cell.classList.toggle("gw-npc-drop-after", after); cell.classList.toggle("gw-npc-drop-before", !after); });
      cell.addEventListener("dragleave", () => cell.classList.remove("gw-npc-drop-before", "gw-npc-drop-after"));
      cell.addEventListener("drop", e => {
        e.preventDefault(); const from = st.drag; st.drag = null;
        const after = cell.classList.contains("gw-npc-drop-after"); cell.classList.remove("gw-npc-drop-before", "gw-npc-drop-after");
        if (from === null || from === undefined || from === i || !rows[from] || rows[from].category !== r.category) return;
        const [x] = rows.splice(from, 1); const to = rows.indexOf(r) + (after ? 1 : 0); rows.splice(to, 0, x); st.sel = rows.indexOf(x); o.dirty();
      });
      grid.append(cell);
    }
    wrap.append(grid);
    if (items.length > PER_TAB) wrap.append(h("p", { class: "tt-bad" }, "This tab has " + items.length + " items. The game's shop window has " + PER_TAB + " slots per tab; the red ones may not show in game (not checked in game). Move some to another tab."));
    // the selected item
    const sel = st.sel !== null ? rows[st.sel] : null;
    if (sel && sel.category === st.tab) {
      const info = o.irow(sel.item) || {}, cur = o.charge(sel), pos = items.findIndex(([, i]) => i === st.sel);
      const step = d => { const j = pos + d; if (j < 0 || j >= items.length) return; const a = items[pos][1], b = items[j][1]; [rows[a], rows[b]] = [rows[b], rows[a]]; st.sel = b; o.dirty(); };
      const price = h("input", { type: "number", min: 1, value: sel.price, class: "gw-npc-num", "aria-label": "Price of " + nameOf(sel),
        onchange: e => { sel.price = Math.max(1, Number(e.target.value) || 1); o.dirty(); } });
      const tabSel = o.tabs.length > 1 ? h("select", { "aria-label": "Tab of " + nameOf(sel), onchange: e => moveToTab(st.sel, Number(e.target.value)) }, tabs.map((t, k) => h("option", { value: t, selected: t === sel.category }, "Tab " + (k + 1)))) : null;
      wrap.append(h("div", { class: "gw-npc-item gw-npc-sel" }, GW.icon(info.sprite, { name: info.name }),
        h("span", { class: "gw-npc-name" }, h("b", null, nameOf(sel)), h("span", { class: "gw-dim" }, " #" + sel.item + " · slot " + (pos + 1))),
        h("label", null, "Price ", price, h("span", { class: "gw-chip gw-cur gw-cur-" + cur }, o.label(cur))),
        tabSel ? h("label", null, "Tab ", tabSel) : null,
        h("span", { class: "gw-npc-order" },
          h("button", { class: "gw-btnx small", type: "button", disabled: pos <= 0, "aria-label": "Move left", onclick: () => step(-1) }, "←"),
          h("button", { class: "gw-btnx small", type: "button", disabled: pos >= items.length - 1, "aria-label": "Move right", onclick: () => step(1) }, "→")),
        h("button", { class: "gw-btnx small", type: "button", onclick: removeSel }, "Remove")));
    } else wrap.append(h("p", { class: "gw-dim" }, "Click an item to change its price or tab; drag it to another slot to move it."));
    return wrap;
  }
  function short(n) { return n >= 1e6 ? (n / 1e6).toFixed(n % 1e6 ? 1 : 0) + "m" : n >= 1e4 ? Math.round(n / 1e3) + "k" : String(n); }

  // ------------------------------------------------------------------------------------------------ NPCs on a map
  function mapView(o) {
    const st = o.state;                            // {map}
    const onMaps = new Map();
    for (const n of o.npcs) if (n.map >= 0) { if (!onMaps.has(n.map)) onMaps.set(n.map, []); onMaps.get(n.map).push(n); }
    const ids = [...onMaps.keys()].sort((a, b) => a - b);
    if (!ids.length) return h("p", { class: "gw-dim" }, "No NPC positions.");
    if (!onMaps.has(st.map)) st.map = onMaps.has(1) ? 1 : ids[0];
    const mapName = id => (o.maps.find(m => m.map_id === id) || {}).name || "map " + id;
    const sel = h("select", { "aria-label": "Map", onchange: e => { st.map = Number(e.target.value); o.redraw(); } },
      ids.map(id => h("option", { value: id, selected: id === st.map }, mapName(id) + " (" + onMaps.get(id).length + ")")));
    const size = 512, cv = h("canvas", { class: "gw-npc-mapcv", width: size, height: size, tabindex: 0, "aria-label": "NPCs on " + mapName(st.map) + ": click a dot to open that NPC" });
    const tip = h("div", { class: "gw-dim gw-npc-maptip", role: "status" }, "Point at a dot to see the NPC; click to open it.");
    const list = onMaps.get(st.map);
    const toS = (x, z) => [(x + 256) / 512 * size, (256 - z) / 512 * size];
    const colour = n => n.shop ? "#f5c542" : n.transporter ? "#5ee0ff" : "#c9d3d9";
    let under = null, hot = null;
    function paint() {
      const c = cv.getContext("2d");
      c.fillStyle = "#06131b"; c.fillRect(0, 0, size, size);
      if (under && window.GWArt) window.GWArt.drawUnderlay(c, under, toS, 0.75);
      c.font = "11px Consolas, monospace";
      for (const n of list) {
        const [x, y] = toS(n.x, n.z), on = o.current && o.current.key === n.key && o.current.map === n.map;
        c.beginPath(); c.arc(x, y, on || hot === n ? 6 : 4, 0, 7); c.fillStyle = colour(n); c.fill(); c.lineWidth = on ? 3 : 1.5; c.strokeStyle = on ? "#fff" : "#000"; c.stroke();
        if (on || hot === n) { c.lineWidth = 3; c.strokeStyle = "#000"; c.strokeText(n.name, x + 8, y + 4); c.fillStyle = "#fff"; c.fillText(n.name, x + 8, y + 4); }
      }
    }
    function nearest(ev) {
      const b = cv.getBoundingClientRect(), px = (ev.clientX - b.left) / b.width * size, py = (ev.clientY - b.top) / b.height * size;
      let best = null, bd = 12 * 12;
      for (const n of list) { const [x, y] = toS(n.x, n.z), d = (x - px) ** 2 + (y - py) ** 2; if (d < bd) { bd = d; best = n; } }
      return best;
    }
    cv.addEventListener("mousemove", ev => { const n = nearest(ev); if (n !== hot) { hot = n; paint(); } cv.style.cursor = n ? "pointer" : "default";
      tip.textContent = n ? n.name + " · " + n.key + " (" + Math.round(n.x) + ", " + Math.round(n.z) + ")" + (n.shop ? " · shop" : "") + (n.transporter ? " · teleporter" : "") : "Point at a dot to see the NPC; click to open it."; });
    cv.addEventListener("click", ev => { const n = nearest(ev); if (n) o.pick(n); });
    underlay(st.map).then(u => { under = u; paint(); });
    paint();
    const legend = h("div", { class: "gw-npc-legend" }, h("span", { class: "k shop" }, "shop"), h("span", { class: "k tele" }, "teleporter"), h("span", { class: "k other" }, "other NPC"));
    const keys = h("div", { class: "gw-npc-mapnames" }, list.slice().sort((a, b) => a.name.localeCompare(b.name)).map(n =>
      h("button", { type: "button", class: "gw-npc-row" + (o.current && o.current.key === n.key && o.current.map === n.map ? " on" : ""), onclick: () => o.pick(n) }, h("b", null, n.name), " ", h("span", { class: "gw-dim" }, n.key))));
    return h("div", { class: "gw-npc-map" }, h("div", { class: "gw-npc-bar" }, sel, legend), cv, tip, h("details", null, h("summary", null, "NPCs on this map as a list"), keys));
  }

  // ------------------------------------------------------------------------------------------------ teleport point picker
  function pointPicker(o) {
    const size = 256, cv = h("canvas", { class: "gw-npc-pickcv", width: size, height: size, tabindex: 0, "aria-label": "Click to set the destination point" });
    const st = { map: o.map, x: Number(o.x), z: Number(o.z), was: [Number(o.x), Number(o.z)] };
    let under = null;
    const toS = (x, z) => [(x + 256) / 512 * size, (256 - z) / 512 * size];
    function paint() {
      const c = cv.getContext("2d"); c.fillStyle = "#06131b"; c.fillRect(0, 0, size, size);
      if (under && window.GWArt) window.GWArt.drawUnderlay(c, under, toS, 0.85);
      const mark = (x, z, col, r) => { if (!isFinite(x) || !isFinite(z)) return; const [sx, sy] = toS(x, z); c.beginPath(); c.arc(sx, sy, r, 0, 7); c.lineWidth = 2; c.strokeStyle = col; c.stroke(); };
      mark(st.was[0], st.was[1], "#ffffffaa", 7); mark(st.x, st.z, "#f5c542", 4);
    }
    async function setMap(id) { st.map = id; under = await underlay(id); paint(); }
    cv.addEventListener("click", ev => {
      const b = cv.getBoundingClientRect(), x = (ev.clientX - b.left) / b.width * 512 - 256, z = 256 - (ev.clientY - b.top) / b.height * 512;
      st.x = Math.round(x * 10) / 10; st.z = Math.round(z * 10) / 10; paint(); o.onPick(st.x, st.z);
    });
    setMap(st.map);
    return { el: cv, setMap, setPoint(x, z) { st.x = Number(x); st.z = Number(z); paint(); } };
  }

  window.GWNpcExtra = { shopGrid, mapView, pointPicker, PER_TAB };
})();
