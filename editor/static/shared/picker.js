/* shared/picker.js - the "show me ALL of them" ID picker (UI-STYLE.md s.5.1, Mall layout).
   GW.grid({type, ...})      -> an embedded, virtualised grid of every entry of one ID type (Library page)
   GW.pick(type, opts)       -> Promise<id | null>: the same grid in a big window; click selects, double-click picks
   GW.pickMany(type, opts)   -> Promise<rows | null>: the same grid; click ticks and unticks, ticks survive searching
   GW.idField(type, value, opts) -> an inline field: icon + English name + "Pick..." button
   Every entry is listed (no "top N"): the rows come from /api/m/catalog/<type> once, filtering is in memory and
   only the visible tiles are in the DOM, so thousands of items stay fast. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const TW = 200, TH = 52;                  // tile size (cell 42 + two text lines)
  const CLASSES = ["Warrior", "Champion", "Priest", "Mage"];
  const TIP_TYPES = { item: 1, skill: 1, petskill: 1, pet: 1, status: 1, talent: 1, appendattr: 1, holystone: 1, quest: 1, title: 1,
    monster: 1, npc: 1, map: 1, suit: 1, suit_tier: 1, guild_building: 1, altar: 1, zodiac_kind: 1, zodiac_sign: 1, ride: 1 };

  function searchKey(r) {
    if (r._s === undefined) r._s = (r.name + " " + r.id + " " + (r.key || "") + " " + (r.sub || "") + " " + (r.cat || "") + " " + (r.group || "") + " " + (r.search_text || "")).toLowerCase();
    return r._s;
  }

  // Compact only explicitly reviewed families, and only after search/level/availability filtering.
  // The original index and its numeric IDs stay intact for tooltips, saved items and API calls.
  GW.groupVariantRows = function (rows, families, selected) {
    const byId = new Map(rows.map(r => [String(r.id), r])), membership = new Map();
    for (const family of families || []) {
      const variants = family.variants.map(v => byId.get(String(v.id))).filter(r => r && r.key === "mount");
      if (variants.length < 2) continue;
      const chosen = variants.find(r => String(r.id) === String(selected)) || variants[0];
      const grouped = Object.assign({}, chosen, { name: family.name, _familyName: family.name, _variants: variants,
        _tiers: Object.fromEntries(family.variants.map(v => [String(v.id), v.tier])),
        sub: variants.length + " level/variant choices · select to choose" });
      for (const row of variants) membership.set(String(row.id), grouped);
    }
    const out = [], seen = new Set();
    for (const row of rows) {
      const group = membership.get(String(row.id));
      if (!group) out.push(row);
      else if (!seen.has(group)) { seen.add(group); out.push(group); }
    }
    return out;
  };

  // opts: {type, value, onSelect(row), onPick(row), group, filter(row)->bool, height, pickMode, allowNone}
  GW.grid = function (opts) {
    const o = Object.assign({}, opts);
    const S = { ix: null, rows: [], shown: [], group: o.group || "", cat: "", classes: new Set(), lvmin: "", lvmax: "", q: "",
      deadStatus: "", hideNoClient: false, hideNoServer: false, groupVariants: true, sel: o.value !== undefined && o.value !== null ? String(o.value) : null, pool: [] };
    const tabs = h("div", { class: "gw-pk-tabs", role: "tablist" });
    const search = h("input", { class: "gw-pk-search", type: "search", placeholder: "search name, description, effect, id ...", "aria-label": "Search", autocomplete: "off" });
    const catSel = h("select", { class: "gw-pk-cat hidden", "aria-label": "Category" });
    const deadSel = h("select", { class: "gw-pk-cat hidden", "aria-label": "Item status",
      "data-help": "Reviewed Dead items remain available in the library and GM tools. Removed promotions are excluded only from loot/shop pickers." },
      h("option", { value: "" }, "All items"), h("option", { value: "dead" }, "Dead items (all)"),
      h("option", { value: "defunct" }, "Dead items: Defunct"), h("option", { value: "no-system" }, "Dead items: No system"),
      h("option", { value: "remove" }, "Dead items: Removed promotions"));
    deadSel.addEventListener("change", () => { S.deadStatus = deadSel.value; applyFilter(); });
    const classBox = h("span", { class: "gw-pk-classes hidden" });
    const lvA = h("input", { class: "gw-pk-lv", type: "number", min: 0, max: 300, placeholder: "Lv from", "aria-label": "Level from" });
    const lvB = h("input", { class: "gw-pk-lv", type: "number", min: 0, max: 300, placeholder: "to", "aria-label": "Level to" });
    const lvBox = h("span", { class: "gw-pk-lvbox hidden" }, lvA, "–", lvB);
    const flagBox = h("span", { class: "gw-pk-flags hidden" });
    const count = h("span", { class: "gw-pk-count", "aria-live": "polite" }, "loading ...");
    const grouping = h("label", { class: "gw-pk-flag hidden" }, h("input", { type: "checkbox", checked: true,
      onchange: e => { S.groupVariants = e.target.checked; applyFilter(); } }), " group mount levels");
    const variantSelect = h("select", { "aria-label": "Mount level or variant" });
    const variantText = h("span");
    const variantBox = h("div", { class: "gw-pk-variant hidden", style: "padding:8px" }, variantText, " Level / variant: ", variantSelect);
    let activeGroup = null;
    const multi = o.multi ? new Map((o.initial || []).map(r => [String(r.id), r])) : null;   // id -> row
    function setTicked(r, on) {
      const id = String(r.id);
      if (!on) multi.delete(id);
      else if (o.allow && !o.allow(r)) { GW.status((r.name || "#" + r.id) + " cannot be added: it is missing from the client or the server.", "err"); return; }
      else if (o.max && multi.size >= o.max && !multi.has(id)) { GW.status("At most " + o.max + " items at once.", "err"); return; }
      else multi.set(id, r);
    }
    const variantTick = h("button", { class: "gw-btnx small", type: "button", onclick: () => {
      const r = activeGroup && activeGroup._variants.find(v => String(v.id) === variantSelect.value);
      if (r) { setTicked(r, !multi.has(String(r.id))); syncSelection(r, false); }
    } });
    const spacer = h("div", { class: "gw-pk-spacer" });
    if (multi) variantBox.append(" ", variantTick);
    const view = h("div", { class: "gw-pk-view", tabindex: 0, role: "listbox", "aria-label": "entries", "aria-multiselectable": multi ? "true" : null }, spacer);
    if (o.height) view.style.height = o.height;
    const el = h("div", { class: "gw-pk gw-text" },
      tabs,
      h("div", { class: "gw-pk-bar" }, search, catSel, deadSel, classBox, lvBox, flagBox, grouping, count),
      variantBox, view);

    let raf = 0;
    const schedule = () => { if (!raf) raf = requestAnimationFrame(() => { raf = 0; draw(); }); };

    function applyFilter() {
      const toks = S.q.toLowerCase().split(/\s+/).filter(Boolean);
      const a = S.lvmin === "" ? null : Number(S.lvmin), b = S.lvmax === "" ? null : Number(S.lvmax);
      const out = [];
      for (const r of S.rows) {
        if (S.group && r.group !== S.group) continue;
        if (S.cat && r.cat !== S.cat) continue;
        if (o.filter && !o.filter(r)) continue;
        if (S.ix.type === "item") {
          if ((o.itemUse === "loot" || o.itemUse === "shop") && r.dead_status === "remove") continue;
          if (S.deadStatus === "dead" && !r.dead_status) continue;
          if (S.deadStatus && S.deadStatus !== "dead" && r.dead_status !== S.deadStatus) continue;
        }
        if (S.hideNoClient && /\bnoclient\b/.test(r.flags || "")) continue;
        if (S.hideNoServer && /\bnoserver\b/.test(r.flags || "")) continue;
        if (S.classes.size && r.classes && r.classes.length && !r.classes.some(c => S.classes.has(c))) continue;
        if (a !== null && (r.minlv === null || r.minlv === undefined || r.minlv < a)) continue;
        if (b !== null && (r.minlv === null || r.minlv === undefined || r.minlv > b)) continue;
        if (toks.length) { const s = searchKey(r); if (!toks.every(t => s.includes(t))) continue; }
        out.push(r);
      }
      if (toks.length === 1 && !/^\d+$/.test(toks[0])) {
        const t = toks[0];
        out.sort((x, y) => (x.name.toLowerCase().startsWith(t) ? 0 : 1) - (y.name.toLowerCase().startsWith(t) ? 0 : 1));
      }
      if (S.sel !== null && !out.some(r => String(r.id) === S.sel)) { S.sel = null; if (o.onClear) o.onClear(); }
      S.shown = S.groupVariants && S.ix && S.ix.type === "item" ? GW.groupVariantRows(out, S.ix.families, S.sel) : out;
      activeGroup = null; variantBox.classList.add("hidden");
      count.textContent = (S.shown.length !== out.length ? GW.fmt(S.shown.length) + " entries · " : "") + GW.fmt(out.length) + " of " + GW.fmt(S.rows.length) + " " + (S.ix ? S.ix.title.toLowerCase() : "");
      view.scrollTop = 0;
      draw();
    }

    function tile() {
      const t = h("button", { class: "gw-pk-tile", type: "button", role: "option" },
        h("span", { class: "gw-sprite gw-cell gw-cellx gw-pk-cell" }), h("span", { class: "gw-pk-name" }), h("span", { class: "gw-pk-sub" }));
      t.addEventListener("click", () => select(t._row, false));
      t.addEventListener("dblclick", () => { if (!multi) select(t._row, true); });
      return t;
    }
    function isSelected(r) {
      if (multi) return multi.has(String(r.id)) || (r._variants || []).some(v => multi.has(String(v.id)));
      return S.sel !== null && (String(r.id) === S.sel || (r._variants || []).some(v => String(v.id) === S.sel));
    }
    function fill(t, r) {
      t._row = r;
      const cell = t.firstChild;
      GW.clear(cell);
      if (r.sprite !== null && r.sprite !== undefined) cell.append(GW.icon(r.sprite));
      else cell.append(GW.icon(null, { name: r.name }));
      const nm = t.children[1];
      nm.textContent = r.name || "(no name)";
      nm.style.color = r.color ? (/^suit(\d)/.test(r.color) ? GW.suitColor(Number(r.color.slice(4))) : r.color === "green" ? "var(--gw-green-textcolor)" : "var(--gw-" + r.color + ")") : "";
      t.children[2].textContent = r.sub || ("#" + r.id);
      const f = r.flags || "";
      t.classList.toggle("noclient", /\bnoclient\b/.test(f));
      t.classList.toggle("noserver", /\bnoserver\b/.test(f));
      t.classList.toggle("warnflag", /\b(overclient|overserver|cn)\b/.test(f));
      t.classList.toggle("sel", isSelected(r));
      t.classList.toggle("multi", !!multi);
      t.setAttribute("aria-selected", isSelected(r) ? "true" : "false");
      t.setAttribute("aria-label", (r.name || "") + " #" + r.id);
      if (TIP_TYPES[S.ix.type]) { t.dataset.tipType = S.ix.type; t.dataset.tipId = String(r.id); }
      else { delete t.dataset.tipType; t.dataset.help = (r.name || "") + " · " + (r.sub || ""); }
      t.title = "";
    }
    function draw() {
      if (!S.ix) return;
      const vw = view.clientWidth || 800, vh = view.clientHeight || 400;
      const cols = Math.max(1, Math.floor((vw - 6) / TW));
      const n = S.shown.length, nrows = Math.ceil(n / cols);
      spacer.style.height = (nrows * TH) + "px";
      const r0 = Math.max(0, Math.floor(view.scrollTop / TH) - 2), r1 = Math.min(nrows, Math.ceil((view.scrollTop + vh) / TH) + 2);
      const need = Math.max(0, (r1 - r0) * cols);
      while (S.pool.length < need) { const t = tile(); S.pool.push(t); spacer.append(t); }
      let k = 0;
      for (let row = r0; row < r1; row++) for (let c = 0; c < cols; c++) {
        const i = row * cols + c;
        if (i >= n) break;
        const t = S.pool[k++];
        if (t._row !== S.shown[i] || t._selv !== S.sel) { fill(t, S.shown[i]); t._selv = S.sel; }
        t.style.transform = `translate(${c * TW}px, ${row * TH}px)`;
        t.style.display = "";
      }
      for (; k < S.pool.length; k++) { S.pool[k].style.display = "none"; S.pool[k]._row = null; }
    }
    function syncSelection(r, dbl) {
      S.sel = String(r.id);
      for (const t of S.pool) if (t._row) { t.classList.toggle("sel", isSelected(t._row)); t.setAttribute("aria-selected", isSelected(t._row) ? "true" : "false"); t._selv = null; }
      draw();
      if (multi) {
        if (activeGroup) variantTick.textContent = multi.has(String(r.id)) ? "Untick this level" : "Tick this level";
        if (o.onTicks) o.onTicks(Array.from(multi.values()));
      }
      if (o.onSelect) o.onSelect(r);
      if (dbl && o.onPick) o.onPick(r);
    }
    function select(r, dbl) {
      if (!r) return;
      activeGroup = r._variants ? r : null;
      variantBox.classList.toggle("hidden", !activeGroup);
      if (activeGroup) {
        const chosen = r._variants.find(v => String(v.id) === S.sel) || r._variants.find(v => v.id === r.id) || r._variants[0];
        variantText.textContent = r._familyName + " ·";
        GW.clear(variantSelect);
        for (const v of r._variants) {
          const tier = r._tiers[String(v.id)];
          variantSelect.append(h("option", { value: v.id }, "Lv " + v.minlv + (tier === "special" ? " (special)" : tier === "max" ? " (max)" : "") + " · #" + v.id));
        }
        variantSelect.value = String(chosen.id);
        Object.assign(r, chosen, { name: r._familyName });
        syncSelection(chosen, dbl);
      } else {
        if (multi) setTicked(r, !multi.has(String(r.id)));
        syncSelection(r, dbl);
      }
    }
    variantSelect.addEventListener("change", () => {
      if (!activeGroup) return;
      const chosen = activeGroup._variants.find(v => String(v.id) === variantSelect.value);
      if (chosen) { Object.assign(activeGroup, chosen, { name: activeGroup._familyName }); syncSelection(chosen, false); }
    });
    function scrollToSel() {
      if (S.sel === null) return;
      const i = S.shown.findIndex(r => isSelected(r));
      if (i < 0) return;
      const cols = Math.max(1, Math.floor(((view.clientWidth || 800) - 6) / TW));
      view.scrollTop = Math.max(0, Math.floor(i / cols) * TH - 2 * TH);
      draw();
    }

    function buildTabs() {
      GW.clear(tabs);
      const groups = S.ix.groups || [];
      if (!groups.length) { tabs.classList.add("hidden"); return; }
      tabs.classList.remove("hidden");
      const mk = (g, label) => {
        const b = h("button", { class: "gw-tabx gw-pk-tab" + (S.group === g ? " on" : ""), type: "button", role: "tab", "aria-selected": S.group === g ? "true" : "false" }, label);
        b.addEventListener("click", () => { S.group = g; S.cat = ""; buildTabs(); buildCats(); applyFilter(); });
        return b;
      };
      tabs.append(mk("", "All"));
      for (const g of groups) tabs.append(mk(g, g));
    }
    function buildCats() {
      const cats = new Map();
      for (const r of S.rows) if (r.cat && (!S.group || r.group === S.group)) cats.set(r.cat, (cats.get(r.cat) || 0) + 1);
      GW.clear(catSel);
      if (cats.size < 2 || cats.size > 400) { catSel.classList.add("hidden"); return; }
      catSel.classList.remove("hidden");
      catSel.append(h("option", { value: "" }, "every kind (" + cats.size + ")"));
      for (const [c, n] of Array.from(cats.entries()).sort((x, y) => x[0].localeCompare(y[0]))) catSel.append(h("option", { value: c }, c + " (" + n + ")"));
      catSel.value = S.cat;
    }
    function buildExtras() {
      GW.clear(classBox);
      const anyCls = S.rows.some(r => r.classes && r.classes.length && r.classes.length < 4);
      classBox.classList.toggle("hidden", !anyCls);
      if (anyCls) CLASSES.forEach((n, i) => {
        const b = h("button", { class: "gw-chipbtn", type: "button", "aria-pressed": "false", "data-help": "Show only what a " + n + " can use (items for every class always show)" }, n);
        b.addEventListener("click", () => { if (S.classes.has(i)) S.classes.delete(i); else S.classes.add(i); b.classList.toggle("on", S.classes.has(i)); b.setAttribute("aria-pressed", S.classes.has(i) ? "true" : "false"); applyFilter(); });
        classBox.append(b);
      });
      lvBox.classList.toggle("hidden", !S.rows.some(r => r.minlv));
      GW.clear(flagBox);
      const nc = S.rows.filter(r => /\bnoclient\b/.test(r.flags || "")).length, ns = S.rows.filter(r => /\bnoserver\b/.test(r.flags || "")).length;
      flagBox.classList.toggle("hidden", !(nc || ns));
      if (nc) flagBox.append(h("label", { class: "gw-pk-flag", "data-help": "Entries the server knows but your Origin client does not: giving one shows an empty or broken slot in game." },
        h("input", { type: "checkbox", onchange: e => { S.hideNoClient = e.target.checked; applyFilter(); } }), " hide " + nc + " not in your client"));
      if (ns) flagBox.append(h("label", { class: "gw-pk-flag", "data-help": "Entries your client has but the server's templates do not: the server cannot create them." },
        h("input", { type: "checkbox", onchange: e => { S.hideNoServer = e.target.checked; applyFilter(); } }), " hide " + ns + " the server cannot create"));
    }

    let st = 0;
    search.addEventListener("input", () => { clearTimeout(st); st = setTimeout(() => { S.q = search.value; applyFilter(); }, 80); });
    search.addEventListener("keydown", e => {
      if (e.key === "Enter") { e.preventDefault(); S.q = search.value; applyFilter(); const r = S.shown.find(x => String(x.id) === S.sel) || S.shown[0]; if (r) select(r, true); }
      if (e.key === "ArrowDown") { e.preventDefault(); const i = S.shown.findIndex(x => String(x.id) === S.sel); const r = S.shown[Math.min(S.shown.length - 1, i + 1)]; if (r) { select(r, false); scrollToSel(); } }
      if (e.key === "ArrowUp") { e.preventDefault(); const i = S.shown.findIndex(x => String(x.id) === S.sel); const r = S.shown[Math.max(0, i - 1)]; if (r) { select(r, false); scrollToSel(); } }
    });
    catSel.addEventListener("change", () => { S.cat = catSel.value; applyFilter(); });
    lvA.addEventListener("input", () => { S.lvmin = lvA.value; applyFilter(); });
    lvB.addEventListener("input", () => { S.lvmax = lvB.value; applyFilter(); });
    view.addEventListener("scroll", schedule, { passive: true });
    if (window.ResizeObserver) new ResizeObserver(schedule).observe(view); else window.addEventListener("resize", schedule);

    async function setType(type, keep) {
      S.ix = null; S.rows = []; S.shown = []; count.textContent = "loading ...";
      for (const t of S.pool) { t.style.display = "none"; t._row = null; }
      try {
        const ix = await GW.catalog.load(type);
        S.ix = ix; S.rows = ix.rows;
        deadSel.classList.toggle("hidden", type !== "item");
        if (!keep) { S.deadStatus = ""; deadSel.value = ""; }
        grouping.classList.toggle("hidden", type !== "item" || !(ix.families || []).length);
        if (!keep) { S.group = (o.group && (ix.groups || []).includes(o.group)) ? o.group : ""; S.cat = ""; S.classes.clear(); }
        buildTabs(); buildCats(); buildExtras(); applyFilter(); scrollToSel();
      } catch (e) { count.textContent = e.message; }
    }
    setType(o.type);
    function refreshTicks() { for (const t of S.pool) t._selv = null; for (const t of S.pool) if (t._row) t.classList.toggle("sel", isSelected(t._row)); draw(); if (o.onTicks) o.onTicks(Array.from(multi.values())); }
    return { el, setType, focus: () => search.focus(), selected: () => S.ix && S.sel !== null ? S.ix.byId.get(S.sel) : null, redraw: schedule,
      ticked: () => Array.from(multi ? multi.values() : []),
      clearTicks: () => { multi.clear(); refreshTicks(); },
      tickShown: () => {
        for (const r of S.shown) {
          const row = r._variants ? (r._variants.find(v => v.id === r.id) || r._variants[0]) : r;
          if ((!o.allow || o.allow(row)) && (!o.max || multi.size < o.max)) multi.set(String(row.id), row);
        }
        refreshTicks();
      } };
  };

  // GW.pick(type, {title, value, group, filter, allowNone}) -> Promise<id | null> (null = cancelled; "" = "none" chosen)
  GW.pick = function (type, opts) {
    const o = opts || {};
    return new Promise(res => {
      let done = false;
      const fin = v => { if (!done) { done = true; res(v); } };
      const preview = h("div", { class: "gw-pk-preview" }, "Click an entry to select it, double-click to pick it.");
      const okBtn = h("button", { class: "gw-btnx", type: "button", disabled: true }, "Pick");
      const grid = GW.grid({ type, value: o.value, group: o.group, filter: o.filter, itemUse: o.itemUse, height: "calc(100vh - 290px)",
        onClear: () => { okBtn.disabled = true; preview.textContent = "Choose an entry from the filtered list."; },
        onSelect: r => { okBtn.disabled = false; okBtn.classList.remove("disabled"); GW.clear(preview).append(GW.icon(r.sprite, { name: r.name }), h("b", null, " " + r.name), " ", h("span", { class: "gw-dim" }, r.sub || "#" + r.id)); },
        onPick: r => { fin(r.id); m.close(); } });
      okBtn.addEventListener("click", () => { const r = grid.selected(); if (r) { fin(r.id); m.close(); } });
      const foot = h("div", { class: "gw-pk-foot" }, preview,
        o.allowNone ? h("button", { class: "gw-btnx", type: "button", onclick: () => { fin(""); m.close(); } }, o.noneLabel || "None") : null,
        okBtn, h("button", { class: "gw-btnx", type: "button", onclick: () => { fin(null); m.close(); } }, "Cancel"));
      const m = GW.modal(h("div", { class: "gw-pk-modal" }, grid.el, foot), { title: o.title || "Pick", full: true, onclose: () => fin(null) });
      setTimeout(() => grid.focus(), 30);
    });
  };

  // GW.pickMany(type, {title, group, filter, initial: rows, allow(row)->bool, max, okLabel}) -> Promise<rows | null> (null = cancelled)
  GW.pickMany = function (type, opts) {
    const o = opts || {};
    return new Promise(res => {
      let done = false;
      const fin = v => { if (!done) { done = true; res(v); } };
      const preview = h("div", { class: "gw-pk-preview", "aria-live": "polite" });
      const okBtn = h("button", { class: "gw-btnx", type: "button" }, o.okLabel || "Next");
      const show = rows => {
        const last = rows.slice(-3).map(r => r.name).join(", ");
        preview.textContent = rows.length ? rows.length + " ticked · " + (rows.length > 3 ? "... " : "") + last : "Click entries to tick them. Ticks stay while you search and filter.";
        okBtn.disabled = !rows.length;
      };
      const grid = GW.grid({ type, multi: true, initial: o.initial, allow: o.allow, max: o.max, group: o.group, filter: o.filter, itemUse: o.itemUse, height: "calc(100vh - 290px)", onTicks: show });
      show(grid.ticked());
      okBtn.addEventListener("click", () => { fin(grid.ticked()); m.close(); });
      const foot = h("div", { class: "gw-pk-foot" }, preview,
        h("button", { class: "gw-btnx", type: "button", "data-help": "Tick every entry the current search and filters show." + (o.max ? " Stops at " + o.max + "." : ""), onclick: () => grid.tickShown() }, "Tick all shown"),
        h("button", { class: "gw-btnx", type: "button", onclick: () => grid.clearTicks() }, "Clear"),
        okBtn, h("button", { class: "gw-btnx", type: "button", onclick: () => { fin(null); m.close(); } }, "Cancel"));
      const m = GW.modal(h("div", { class: "gw-pk-modal" }, grid.el, foot), { title: o.title || "Pick several", full: true, onclose: () => fin(null) });
      setTimeout(() => grid.focus(), 30);
    });
  };

  // GW.idField(type, value, {onChange(id, row), title, group, filter, allowNone, disabled, field}) -> element (.value)
  GW.idField = function (type, value, opts) {
    const o = opts || {};
    let cur = value === undefined ? null : value;
    const cell = h("span", { class: "gw-sprite gw-cell gw-cellx gw-idf-cell" });
    const name = h("span", { class: "gw-idf-name" });
    const btn = h("button", { class: "gw-btnx small gw-idf-btn", type: "button", disabled: o.disabled ? true : null }, "Pick");
    const el = h("span", { class: "gw-idf", "data-field": o.field || null }, cell, name, btn);
    async function show() {
      GW.clear(cell); name.textContent = "";
      if (cur === null || cur === "" || cur === undefined) { name.textContent = o.placeholder || "(none)"; name.classList.add("gw-dim"); delete cell.dataset.tipType; return; }
      name.classList.remove("gw-dim");
      const r = await GW.catalog.row(type, cur).catch(() => null);
      if (r) { cell.append(GW.icon(r.sprite, { name: r.name })); name.textContent = r.name + " #" + r.id; if (TIP_TYPES[type]) { cell.dataset.tipType = type; cell.dataset.tipId = String(r.id); } }
      else { cell.append(GW.icon(null, { name: "?" })); name.textContent = "unknown #" + cur; name.classList.add("tt-bad"); }
    }
    btn.addEventListener("click", async () => {
      const v = await GW.pick(type, { title: o.title || "Pick", value: cur, group: o.group, filter: o.filter, itemUse: o.itemUse, allowNone: o.allowNone });
      if (v === null) return;
      cur = v === "" ? null : v;
      await show();
      if (o.onChange) o.onChange(cur, cur === null ? null : await GW.catalog.row(type, cur).catch(() => null));
    });
    Object.defineProperty(el, "value", { get: () => cur, set: v => { cur = v; show(); } });
    show();
    return el;
  };
})();
