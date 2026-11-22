/* static/modules/npcs.js - every NPC: edit shop lists and prices, and where teleporter buttons go. Backend: modules/npcs.py. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const irow = id => GW.catalog.loadedRow("item", id);
  const num = (value, label, o) => h("input", Object.assign({ type: "number", value, "aria-label": label, class: "gw-npc-num" }, o || {}));

  GW.module("npcs", {
    async mount(el, ctx) {
      let D = null, filter = "useful", q = "", current = null, leftView = "list", menus = null, gates = null;
      const mapState = { map: null }, gridState = { tab: null, sel: null, drag: null };   // B21 items 27/28
      let shopView = "grid";
      try { const v = localStorage.getItem("gw.npcs.shopview"); if (v === "list" || v === "grid") shopView = v; } catch (e) { /* storage blocked */ }
      if (!window.GWNpcExtra) await new Promise(r => { const sc = document.createElement("script"); sc.src = "/static/modules/npcs-extra.js"; sc.onload = r; sc.onerror = r; document.head.append(sc); });
      if (!window.GWTeleMenu) await new Promise(r => { const sc = document.createElement("script"); sc.src = "/static/modules/teleport-menu.js"; sc.onload = r; sc.onerror = r; document.head.append(sc); });   // B22 P1 (d32)
      const X = window.GWNpcExtra;
      const list = h("div", { class: "gw-npc-list" }), pane = h("div", { class: "gw-npc-pane" });
      const search = h("input", { type: "search", placeholder: "search NPC name or key ...", "aria-label": "Search NPCs", oninput: e => { q = e.target.value.toLowerCase(); drawList(); } });
      const kinds = h("select", { "aria-label": "Show", onchange: e => { filter = e.target.value; drawList(); } },
        h("option", { value: "useful" }, "Shops and teleporters"), h("option", { value: "shop" }, "Shops"), h("option", { value: "transporter" }, "Teleporters"), h("option", { value: "all" }, "Every NPC"));
      const viewBtn = h("button", { class: "gw-btnx", type: "button", "aria-pressed": "false", onclick: () => { leftView = leftView === "map" ? "list" : "map"; viewBtn.textContent = leftView === "map" ? "Show as list" : "Pick on the map"; viewBtn.setAttribute("aria-pressed", leftView === "map" ? "true" : "false"); drawList(); } }, "Pick on the map");
      el.append(h("div", { class: "gw-npc-bar" }, search, kinds, X ? viewBtn : null), h("div", { class: "gw-npc-body" }, list, pane));

      async function load() {
        [D] = await Promise.all([ctx.api("overview"), GW.catalog.load("item")]);
        if (!menus) menus = await GW.api("/api/m/npc_dialogue/menus").catch(() => null);   // client option text (teleporter labels)
        gates = await GW.api("/api/m/gates/overview").catch(() => null);                     // per-destination open / closed
        drawList();
        // #/npcs?shop=<service> (World > Shops & Mall "Edit this shop", Library): open that shop
        const want = ctx.params && ctx.params.get && ctx.params.get("shop");
        if (!current && want) { const n = D.npcs.find(x => x.shop && x.service === want); if (n) { current = n; filter = "shop"; kinds.value = "shop"; drawList(); } else GW.status("The server has no editable shop called " + want + " (the Athens merchants captured from retail are compiled and not editable).", "err"); }
        // #/npcs?npc=<key>[&map=<id>] (map views, NPC greetings): open that NPC
        const wantNpc = ctx.params && ctx.params.get && ctx.params.get("npc"), wantMap = ctx.params && ctx.params.get && ctx.params.get("map");
        if (!current && wantNpc) { const n = D.npcs.find(x => x.key === wantNpc && (wantMap === null || wantMap === undefined || String(x.map) === wantMap)); if (n) { current = n; filter = "all"; kinds.value = "all"; mapState.map = n.map; drawList(); } }
        if (current) open(D.npcs.find(n => n.key === current.key && n.map === current.map) || current);
      }
      function drawList() {
        GW.clear(list);
        if (leftView === "map" && X) {
          const shown = D.npcs.filter(n => (filter === "all" || (filter === "useful" && (n.shop || n.transporter)) || (filter === "shop" && n.shop) || (filter === "transporter" && n.transporter)) && (!q || (n.name + " " + n.key).toLowerCase().includes(q)));
          list.append(X.mapView({ npcs: shown, maps: D.maps, state: mapState, current, redraw: drawList, pick: n => { current = n; drawList(); open(n); } }));
          return;
        }
        let map = null;
        for (const n of D.npcs) {
          if (filter === "useful" && !n.shop && !n.transporter && !n.activators) continue;
          if (filter === "shop" && !n.shop) continue;
          if (filter === "transporter" && !n.transporter) continue;
          if (q && !(n.name + " " + n.key).toLowerCase().includes(q)) continue;
          if (n.map !== map) { map = n.map; list.append(h("h3", null, n.map_name)); }
          list.append(h("button", { class: "gw-npc-row" + (current && current.key === n.key && current.map === n.map ? " on" : ""), type: "button", onclick: () => { current = n; drawList(); open(n); } },
            h("b", null, n.name), " ", h("span", { class: "gw-dim" }, n.key), n.shop ? h("span", { class: "gw-chip" }, "shop") : null, n.transporter ? h("span", { class: "gw-chip" }, "teleporter") : null));
        }
      }
      function open(n) {
        GW.clear(pane).append(h("h2", null, n.name), h("p", { class: "gw-dim" }, n.map < 0 ? n.map_name : n.key + " · " + n.map_name + " (" + Math.round(n.x) + ", " + Math.round(n.z) + ")"));
        if (n.activators) return drawActivators();
        if (n.map >= 0) pane.append(h("p", null, h("a", { href: "#/npc_dialogue?npc=" + encodeURIComponent(n.key) }, "Greeting and option lines of this NPC →")));
        if (n.shop) drawShop(n);
        if (n.transporter) drawTeleports(n);
        if (!n.shop && !n.transporter) pane.append(h("p", null, "This NPC has no shop or teleporter in the server. What an NPC offers is decided by the client's own dialogue scripts, so a shop can't be added to it from here."));
      }
      async function drawActivators() {
        const cat = await ctx.api("activators/buffs");
        const buffs = (cat.buffs || []).filter(b => b.beneficial !== false);
        const rows = (D.activators || []).map(r => ({ item_id: r.item_id, skill_id: r.skill_id, cooldown_seconds: r.cooldown_seconds }));
        const body = h("div", { class: "gw-npc-rows" });
        const label = b => (b.name || "Skill " + b.skillId) + " (" + b.skillId + ")";
        function draw() {
          GW.clear(body);
          if (!rows.length) body.append(h("p", { class: "gw-dim" }, "None. Nothing changes for players until you add an item here."));
          rows.forEach((r, i) => {
            const it = irow(r.item_id) || {};
            const pick = h("select", { "aria-label": "Buff cast by " + it.name, onchange: e => { r.skill_id = Number(e.target.value); } },
              buffs.map(b => h("option", { value: b.skillId, selected: b.skillId === r.skill_id }, label(b))));
            body.append(h("div", { class: "gw-npc-item" }, GW.icon(it.sprite, { name: it.name }), h("span", { class: "gw-npc-name" }, it.name || "item " + r.item_id),
              h("label", null, "Buff ", pick),
              h("label", null, "Cooldown (s) ", num(r.cooldown_seconds, "Cooldown of " + it.name, { min: 0, onchange: e => { r.cooldown_seconds = Number(e.target.value); } })),
              h("button", { class: "gw-btnx small", type: "button", onclick: () => { rows.splice(i, 1); draw(); } }, "Remove")));
          });
        }
        const add = async () => {
          const picked = await GW.pickMany("item", { title: "Reusable items", allow: r => !/\b(noclient|noserver)\b/.test(r.flags || ""), max: 20 });
          if (!picked || !buffs.length) return;
          for (const p of picked) if (!rows.some(r => r.item_id === p.id)) rows.push({ item_id: p.id, skill_id: buffs[0].skillId, cooldown_seconds: 60 });
          draw();
        };
        const save = async () => {
          try { GW.writeDone(await ctx.api("activators/save", { rows, version: D.activators_version }), "Reusable items saved"); await load(); }
          catch (e) { if (await GW.writeError(e) === "conflict") await load(); }
        };
        draw();
        pane.append(h("p", { class: "gw-dim" }, "Using one of these items casts the buff and keeps the item. The buff and cooldown last until logout."),
          h("div", { class: "gw-npc-bar" }, h("button", { class: "gw-btnx", type: "button", onclick: add }, "+ Add items"), h("button", { class: "gw-btnx", type: "button", onclick: save }, "Save")), body);
      }
      function drawShop(n) {
        const shop = D.shops[n.service];
        if (!shop) { pane.append(h("p", { class: "tt-bad" }, "This shop's list could not be read from the server.")); return; }
        let changed = false, dragFrom = null;
        const cur = shop.currency || { kind: "fixed", by_category: {}, by_item: {} }, names = D.currencies || {};
        const rows = (shop.override || shop.compiled).map(r => ({ item: r.item || r.item_id, price: r.price, category: r.category, quality: r.quality, grade: r.grade, bound: r.bound, stack_marker: r.stack_marker, was: shop.override ? null : r.charges }));
        const tabs = Array.from(new Set(shop.compiled.map(r => r.category))).sort((a, b) => a - b);
        // Same rule as the server (modules/npcs.py row_currency): Point Exchanger by item id, else the tab's first frame.
        const charge = r => (!changed && r.was) || (cur.kind === "item" && cur.by_item[String(r.item)]) || cur.by_category[String(r.category)] || cur.default || null;
        const fallback = r => cur.kind === "item" && !cur.by_item[String(r.item)];
        const label = c => names[c] || c || "?";
        // In game the server sends each tab's items in list order; moving an item across tabs changes nothing, so order within a tab.
        const byTab = () => { const order = tabs.length ? tabs : [0]; rows.sort((x, y) => (order.indexOf(x.category) + 1 || 999) - (order.indexOf(y.category) + 1 || 999)); };
        byTab();
        if (n.service === "MallAdmin") pane.append(h("p", { class: "gw-dim" }, "Tab 8 of the in-game mall. Only accounts with editor access receive it or can buy from it, and it charges silver. New rows cost 1."));
        const body = h("div", { class: "gw-npc-rows" }), note = h("p", { class: "gw-dim", role: "status" }), summary = h("p", { class: "gw-npc-cur" });
        function drawSummary() {
          GW.clear(summary);
          const count = {};
          rows.forEach(r => { const c = charge(r); count[c] = (count[c] || 0) + 1; });
          summary.append(h("b", null, "Currency: "), ...Object.keys(count).map(c => h("span", { class: "gw-chip gw-cur gw-cur-" + c }, label(c) + (Object.keys(count).length > 1 ? " × " + count[c] : ""))));
          if (cur.kind === "item") summary.append(h("span", { class: "gw-dim" }, " The server picks honour, points or medals by item id (a list in its code). Any other item charges " + label(cur.default) + ". Each page in game shows the balance of its first item."));
          else if (cur.kind === "tab") summary.append(h("span", { class: "gw-dim" }, " Each tab charges its own currency. An edited tab charges the currency of its first original page."));
        }
        function move(i, d) {
          let j = i + d;
          while (j >= 0 && j < rows.length && rows[j].category !== rows[i].category) j += d;
          if (j < 0 || j >= rows.length) return;
          const [r] = rows.splice(i, 1); rows.splice(j, 0, r); dirty();
          const btn = body.querySelector('[data-row="' + j + '"] [data-move="' + d + '"]'); if (btn && !btn.disabled) btn.focus();
        }
        // Edits that change rows redraw (currencies then follow the server's rule for edited lists); a price edit only marks.
        const dirty = (redraw = true) => { changed = true; if (redraw) draw(); else if (!/ · not saved$/.test(note.textContent)) note.textContent += " · not saved"; };
        function draw() {
          GW.clear(body);
          if (shopView === "grid" && X) {
            note.textContent = rows.length + " items" + (shop.override ? " · edited" : " · original list") + ". Laid out like the game's shop window: " + X.PER_TAB + " slots per tab. Also sold by every NPC running this shop. Saved lists reach the game within 15 seconds (no restart)." + (changed ? " · not saved" : "");
            drawSummary();
            body.append(X.shopGrid({ rows, tabs, state: gridState, irow, charge, label, redraw: draw, dirty: () => dirty(), add: t => add(t) }));
            return;
          }
          note.textContent = rows.length + " items" + (shop.override ? " · edited" : " · original list") + ". Drag a row (or use the arrows) to change its slot within its tab. Also sold by every NPC running this shop. Saved lists reach the game within 15 seconds (no restart)." + (changed ? " · not saved" : "");
          drawSummary();
          let tab = null;
          rows.forEach((r, i) => {
            if (tabs.length > 1 && r.category !== tab) {
              tab = r.category;
              const inTab = cur.kind === "tab" ? Array.from(new Set(rows.filter(x => x.category === tab).map(charge))) : [];
              body.append(h("h4", { class: "gw-npc-tab" }, "Tab " + (tabs.indexOf(tab) + 1), ...inTab.map(c => h("span", { class: "gw-chip gw-cur gw-cur-" + c }, label(c)))));
            }
            const it = irow(r.item) || {}, c = charge(r), first = i === 0 || rows[i - 1].category !== r.category, last = i === rows.length - 1 || rows[i + 1].category !== r.category;
            const tabSel = tabs.length > 1 ? (() => { const s = h("select", { "aria-label": "Tab of " + (it.name || r.item), onchange: e => { r.category = Number(e.target.value); const [x] = rows.splice(i, 1); let k = rows.length; while (k > 0 && rows[k - 1].category !== r.category && tabs.indexOf(rows[k - 1].category) > tabs.indexOf(r.category)) k--; rows.splice(k, 0, x); dirty(); } }, tabs.map((t, k) => h("option", { value: t }, String(k + 1)))); s.value = String(r.category); return s; })() : null;
            const rowEl = h("div", { class: "gw-npc-item", "data-row": i, draggable: "true", title: "Drag to change the slot order (within this tab)" },
              h("span", { class: "gw-npc-grip", "aria-hidden": "true" }, "⠿"),
              h("span", { class: "gw-npc-order" },
                h("button", { class: "gw-btnx small", type: "button", "data-move": "-1", disabled: first, title: "Move up", "aria-label": "Move " + (it.name || r.item) + " up", onclick: () => move(i, -1) }, "↑"),
                h("button", { class: "gw-btnx small", type: "button", "data-move": "1", disabled: last, title: "Move down", "aria-label": "Move " + (it.name || r.item) + " down", onclick: () => move(i, 1) }, "↓")),
              GW.icon(it.sprite, { name: it.name }), h("span", { class: "gw-npc-name" }, it.name || "item #" + r.item, h("span", { class: "gw-dim" }, " #" + r.item)),
              h("label", null, "Price ", num(r.price, "Price of " + it.name + " in " + label(c), { min: 1, onchange: e => { r.price = Number(e.target.value); dirty(false); } }),
                h("span", { class: "gw-chip gw-cur gw-cur-" + c + (fallback(r) ? " gw-cur-fallback" : ""), title: fallback(r) ? "Not in the server's honour/points/medals list: charges " + label(c) : "Charged in " + label(c) }, label(c) + (fallback(r) ? " (not in list)" : ""))),
              tabSel ? h("label", null, "Tab ", tabSel) : null,
              h("button", { class: "gw-btnx small", type: "button", onclick: () => { rows.splice(i, 1); dirty(); } }, "Remove"));
            // Drag and drop (2026-11-05, the admin): drop a row on another row of the SAME tab to put it in that slot. In game the
            // server sends each tab's items in list order, so order only means something within a tab (like the arrows).
            rowEl.addEventListener("dragstart", e => { if (e.target.closest && e.target.closest("input,select,button")) { e.preventDefault(); return; } dragFrom = i; rowEl.classList.add("gw-npc-dragging"); e.dataTransfer.effectAllowed = "move"; try { e.dataTransfer.setData("text/plain", String(i)); } catch (_) {} });
            rowEl.addEventListener("dragend", () => { dragFrom = null; body.querySelectorAll(".gw-npc-drop-before,.gw-npc-drop-after,.gw-npc-dragging,.gw-npc-drop-no").forEach(x => x.classList.remove("gw-npc-drop-before", "gw-npc-drop-after", "gw-npc-dragging", "gw-npc-drop-no")); });
            rowEl.addEventListener("dragover", e => {
              if (dragFrom === null) return;
              const same = rows[dragFrom] && rows[dragFrom].category === r.category;
              rowEl.classList.toggle("gw-npc-drop-no", !same);
              if (!same || dragFrom === i) return;
              e.preventDefault(); e.dataTransfer.dropEffect = "move";
              const box = rowEl.getBoundingClientRect(), after = e.clientY > box.top + box.height / 2;
              rowEl.classList.toggle("gw-npc-drop-after", after); rowEl.classList.toggle("gw-npc-drop-before", !after);
            });
            rowEl.addEventListener("dragleave", () => rowEl.classList.remove("gw-npc-drop-before", "gw-npc-drop-after", "gw-npc-drop-no"));
            rowEl.addEventListener("drop", e => {
              e.preventDefault();
              const from = dragFrom; dragFrom = null;
              if (from === null || from === i || !rows[from] || rows[from].category !== r.category) return;
              const after = rowEl.classList.contains("gw-npc-drop-after");
              const [x] = rows.splice(from, 1);
              let to = rows.indexOf(r) + (after ? 1 : 0);
              rows.splice(to, 0, x); dirty();
            });
            body.append(rowEl);
          });
        }
        const add = async (tab) => {
          const picked = await GW.pickMany("item", { title: "Add to " + shop.label, itemUse: "shop", allow: r => !/\b(noclient|noserver)\b/.test(r.flags || ""), max: 200 });
          if (!picked) return;
          const into = tabs.includes(tab) ? tab : (tabs[0] || 0);
          for (const p of picked) rows.push({ item: p.id, price: n.service === "MallAdmin" ? 1 : Math.max(1, Number(p.price) || 1), category: into, quality: 1, grade: 1, bound: 0, stack_marker: 1 });
          byTab(); dirty();
        };
        const save = async () => {
          try {
            const r = await ctx.api("shop/save", { service: n.service, rows, version: shop.version });
            GW.writeDone(r, "Shop saved");
            if (r.warnings && r.warnings.length) GW.status("Shop saved. " + r.warnings.slice(0, 3).join("; ") + (r.warnings.length > 3 ? " (+" + (r.warnings.length - 3) + " more)" : ""), "ok");
            await load();
          } catch (e) { if (await GW.writeError(e) === "conflict") await load(); }
        };
        const reset = async () => {
          if (!await GW.confirm({ title: "Back to the original list?", message: "Removes your edits to this shop. The change log keeps an Undo.", ok: "Reset" })) return;
          try { GW.writeDone(await ctx.api("shop/reset", { service: n.service, version: shop.version }), "Shop reset"); await load(); } catch (e) { await GW.writeError(e); }
        };
        const viewSel = h("select", { "aria-label": "Shop view", onchange: e => { shopView = e.target.value; try { localStorage.setItem("gw.npcs.shopview", shopView); } catch (_) {} draw(); } },
          h("option", { value: "grid", selected: shopView === "grid" }, "View: game window"), h("option", { value: "list", selected: shopView === "list" }, "View: list"));
        gridState.tab = null; gridState.sel = null;
        pane.append(h("h3", null, shop.label), summary, note, h("div", { class: "gw-inv-btns" }, X ? viewSel : null,
          h("button", { class: "gw-btnx", type: "button", onclick: () => add(gridState.tab) }, "+ Add items"), h("button", { class: "gw-btnx", type: "button", onclick: save }, "Save shop"),
          shop.override ? h("button", { class: "gw-btnx", type: "button", onclick: reset }, "Reset to original") : null), body);
        draw();
      }
      function drawTeleports(n) {
        // B21 item 27: the buttons in the order the game lists them, with the client's own words, and a map to click
        // the destination on. Order, words and which buttons exist are fixed by the client and the published menu.
        const route = ((menus && menus.routes && menus.routes[n.key]) || []).find(r => r.behavior === 14 || r.behavior === 15 || r.behavior === 18) || null;
        const order = route && route.page ? route.page.options.map(o => o.sub) : (route ? route.subs : []);   // menu, then server-added
        const opt = sub => route && route.page ? route.page.options.find(o => o.sub === sub) : null;
        const strip = t => String(t || "").replace(/\|c[0-9A-Fa-f]{8}|\|r/g, "").replace(/^\*/, "");
        const mine = D.teleports.filter(x => x.npc === n.key).sort((a, b) => ((order.indexOf(a.sub) + 1) || 999) - ((order.indexOf(b.sub) + 1) || 999));
        pane.append(h("h3", null, "Teleporter destinations"), h("p", { class: "gw-dim" }, "Where each original button goes, with the button words from the client (the menu further down decides which buttons players see, their order and price). Pick a map and click the small map (or type X / Z) to move a destination; points inside walls are refused. Saved buttons reach the game within 15 seconds."),
          h("p", { class: "gw-dim", "data-help": "The words on each button come from the client's scripts: see NPC greetings to change them (client update)." }, "A destination can be closed on the Gates page. Below: the buttons this teleporter shows, their order and price."));
        if (window.GWTeleMenu) pane.append(window.GWTeleMenu.panel({ ctx, npc: n.key, maps: D.maps, pointPicker: X ? X.pointPicker : null, onSaved: () => {} }));   // B22 P1 (d32)
        const listEl = h("div", { class: "gw-npc-teles" });
        mine.forEach((t, k) => {
          const o = t.override, op = opt(t.sub);
          const map = h("select", { "aria-label": "Destination map" }, D.maps.map(m => h("option", { value: m.map_id }, m.name + " (" + m.map_id + ")")));
          map.value = String(o ? o.target_map_id : t.target_map);
          const x = num(o ? o.pos_x : (t.x ?? ""), "X"), z = num(o ? o.pos_z : (t.z ?? ""), "Z"), lv = num(o ? o.minimum_level : t.level, "Minimum level", { min: 1, max: 255 });
          const picker = X ? X.pointPicker({ map: Number(map.value), x: x.value, z: z.value, onPick: (px, pz) => { x.value = px; z.value = pz; } }) : null;
          if (picker) { map.addEventListener("change", () => picker.setMap(Number(map.value))); const upd = () => picker.setPoint(x.value, z.value); x.addEventListener("input", upd); z.addEventListener("input", upd); }
          const gateMode = g => { const m = g.effective && g.effective.schedule_mode; return m && m !== "compiled" ? m : (g.compiled && g.compiled.schedule) || "always"; };
          const gate = ((gates && gates.gates) || []).find(g => String(g.buttons || "").split(/,\s*/).includes(t.npc + " " + t.sub));
          const gateEl = gate ? h("a", { href: "#/gates", class: "gw-chip", "data-help": "Open or close this destination, its opening times and its level range are set on the Gates page." },
            "Gates: " + (gateMode(gate) === "closed" ? "closed" : gateMode(gate) === "windows" ? "set times" : "open") + " →") : null;
          const fee = t.target_map === 6 ? h("span", { class: "gw-chip gw-cur gw-cur-silver", "data-help": "The original fee of the capitals' Mycenae button; GMs skip it. A price set in the menu below replaces it." }, "20,000 Silver unless the menu sets a price") : null;
          listEl.append(h("div", { class: "gw-npc-tele" },
            h("div", { class: "gw-npc-tele-head" }, h("span", { class: "gw-npc-tele-no" }, String(k + 1)),
              h("span", { class: "gw-npc-tele-label" }, op && op.label ? strip(op.label.text) : t.name), fee, gateEl,
              op && op.added ? h("span", { class: "gw-chip", "data-help": "Not in the published menu: the game server adds this button after the menu's own buttons." }, "added by the server") : null,
              route && !order.includes(t.sub) ? h("span", { class: "gw-chip", "data-help": "The server knows this destination, but this NPC's menu does not show a button for it." }, "no button") : null,
              h("span", { class: "gw-dim" }, " → " + (t.target_name || t.target_map) + (o ? " · edited" : "") + " · button " + t.sub)),
            h("div", { class: "gw-npc-tele-body" }, picker ? picker.el : null,
              h("div", { class: "gw-npc-tele-form" }, h("label", null, "Map ", map), h("label", null, "X ", x), h("label", null, "Z ", z), h("label", null, "Minimum level ", lv),
                h("div", { class: "gw-inv-btns" },
                  h("button", { class: "gw-btnx small", type: "button", onclick: async () => {
                    try { const r = await ctx.api("teleport/save", { npc: t.npc, sub: t.sub, map: Number(map.value), x: x.value, z: z.value, level: Number(lv.value), version: t.version }); GW.writeDone(r, "Button saved"); await load(); } catch (e) { await GW.writeError(e); }
                  } }, "Save"),
                  o ? h("button", { class: "gw-btnx small", type: "button", onclick: async () => {
                    try { GW.writeDone(await ctx.api("teleport/reset", { npc: t.npc, sub: t.sub, version: t.version }), "Button reset"); await load(); } catch (e) { await GW.writeError(e); }
                  } }, "Reset to original") : null)))));
        });
        pane.append(listEl);
      }
      try { await load(); } catch (e) { GW.clear(pane).append(h("p", { class: "tt-bad" }, e.message)); }
    }
  });
})();
