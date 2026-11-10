/* static/modules/world.js - the "World & content" page. Backend: modules/world.py. Contract: MODULES.md.
   Owned by the world module: only this file, world.css and modules/world.py change for it.
   Sections (#/world/<section>): overview, loot, medusa, gm, npc, quests, instances, events, gifts, bosses, calendar,
   balances, shops. Every ID is shown with its English name and icon; every ID picker is the shared all-entries grid. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;

  const SECTIONS = [
    ["overview", "Overview", "Pending restart, the start-up check and what each part changes."],
    ["loot", "Monster loot", "What monsters drop: item, chance, quantity, quality, grade, attributes. After a restart."],
    ["medusa", "Medusa Island", "Medusa monsters, their drops, the honour table and titles. After a restart."],
    ["gm", "GM monsters", "Add monsters to a map, move or switch off spawns, change their stats. After a restart."],
    ["npc", "GM NPCs", "Place extra NPCs and write their dialogue windows. After a restart."],
    ["quests", "Quest rewards", "Change what a quest pays: EXP, talent points, Silver, Gold and reward items. After a restart."],
    ["instances", "Dungeon entries", "Daily entries of Atlantis, Wonderland, Harbor Attack and Medusa. At the next entry."],
    ["events", "EXP events", "Double EXP weekends and other EXP boosts with a start and an end."],
    ["gifts", "Event gifts", "Give every player (or a group) an item, Silver or Gold. Online players get it at once with the round-2 server."],
    ["bosses", "World bosses", "The 19 field bosses and the camp that holds each area bonus (read-only)."],
    ["server", "Server settings", "Edit server-list names and view service status."],
    ["calendar", "Calendar", "The realm calendar and reset times; save time-zone changes for the next game restart."],
    ["balances", "Crier & awards", "Faction Crier prices (read-only) and the Online Award items (edit on the Online award page)."],
    ["shops", "Shops & Mall", "Every NPC shop and the Mall with prices. Edit them (items, prices, slot order) on the NPCs page."],
  ];
  const DIFF = { 1: "Normal", 2: "Hard", 3: "Hell" };
  const CAMPS = { 0: "Sparta", 1: "Athens" };
  const CLASSES = ["Warrior", "Champion", "Priest", "Mage"];
  const QN = () => GW.QUALITY_NAMES || [];
  let S = null;                   // page state for the mounted page
  const KEEP = {};                // survives the remount the shell does on every hash change (map, tab, shop)

  // ------------------------------------------------------------ small helpers
  const pct = bp => (Number(bp) / 100).toLocaleString("en-US", { maximumFractionDigits: 2 }) + " %";
  const n2 = v => (v === null || v === undefined || v === "" ? null : Number(v));
  const fmtT = t => { if (!t) return "-"; const d = new Date(t); return isNaN(d) ? String(t) : d.toLocaleString(); };
  function put(el, ...kids) { for (const k of kids.flat()) if (k !== null && k !== undefined && k !== false) el.append(k); return el; }
  function win(title, ...kids) { return h("section", { class: "gw-frame-normalwindow gw-win gw-w-win" }, h("div", { class: "gw-wtitle" }, title), ...kids); }
  function sub(title, ...kids) { return h("div", { class: "gw-frame-simplewindow gw-w-sub" }, title ? h("div", { class: "gw-w-subt" }, title) : null, ...kids); }
  function lab(text, field, help) { return h("span", { class: "gw-label gw-w-lab", "data-field": field || null, "data-help": help || null }, text); }
  function note(...kids) { return h("p", { class: "gw-dim gw-w-note" }, ...kids); }
  function bad(msg) { return h("div", { class: "gw-online warn gw-w-bad", role: "alert" }, msg); }
  function readOnly(why) { return h("div", { class: "gw-w-ro", "data-help": why || "" }, h("span", { class: "gw-chip never" }, "read-only"), " ", why || ""); }
  function btn(text, onclick, o) {
    o = o || {};
    return h("button", { class: "gw-btnx" + (o.small ? " small" : "") + (o.danger ? " danger" : "") + (o.on ? " on" : ""), type: "button",
      disabled: o.disabled ? true : null, "data-help": o.help || null, onclick }, text);
  }
  function sel(options, value, o) {
    o = o || {};
    const s = h("select", { class: "gw-w-in", "aria-label": o.label || "choose", "data-field": o.field || null, "data-help": o.help || null },
      options.map(([v, t]) => h("option", { value: v === null ? "" : String(v) }, t)));
    s.value = value === null || value === undefined ? "" : String(value);
    if (o.onchange) s.addEventListener("change", () => o.onchange(s.value === "" ? null : s.value));
    return s;
  }
  function inp(value, o) {
    o = o || {};
    const i = h("input", { class: "gw-w-in" + (o.wide ? " wide" : ""), type: o.type || "text", value: value === null || value === undefined ? "" : String(value),
      placeholder: o.placeholder || "", maxlength: o.max || null, "aria-label": o.label || "value", "data-field": o.field || null, "data-help": o.help || null,
      step: o.step || null, min: o.min, style: o.width ? "width:" + o.width : null });
    if (o.oninput) i.addEventListener("input", () => o.oninput(i.value));
    return i;
  }
  function check(on, text, o) {
    o = o || {};
    const c = h("input", { type: "checkbox", checked: on ? true : null, "aria-label": text });
    if (o.onchange) c.addEventListener("change", () => o.onchange(c.checked));
    return h("label", { class: "gw-w-check", "data-field": o.field || null, "data-help": o.help || null }, c, " " + text);
  }
  function nf(o) { return GW.numberField(Object.assign({ slider: true }, o)); }
  function chipFor(applies) { return GW.appliesChip(applies); }
  function itemRow(id) { return GW.catalog.loadedRow("item", id); }
  function itemCell(id, inst, onclick, o) {
    o = o || {};
    if (id === null || id === undefined || id === "") return GW.cell({ empty: true, label: o.label || "empty: click to pick an item", onclick, help: o.help || "Click to pick an item from every item in the game" });
    const r = itemRow(id) || {};
    return GW.cell({ sprite: r.sprite, name: r.name || ("#" + id), tipType: "item", tipId: id, tipInst: inst || null, onclick, count: o.count,
      quality: inst && inst.item_quality !== null && inst.item_quality !== undefined ? inst.item_quality : undefined, label: r.name || ("item " + id) });
  }
  function itemName(id, q) {
    const r = itemRow(id);
    return h("span", { class: "gw-w-iname", style: q !== null && q !== undefined ? "color:" + GW.qColor(q) : null }, r ? r.name : "item #" + id,
      r && /noclient/.test(r.flags || "") ? h("span", { class: "tt-bad" }, " (not in your client)") : null);
  }
  function monsterName(key) { const r = GW.catalog.loadedRow("monster", key); return r ? r.name : key; }
  function mapName(id) { const r = GW.catalog.loadedRow("map", id); return r ? r.name : "map " + id; }
  async function api(sub, body) { return GW.api("/api/m/world/" + sub, body); }

  // one save: status line + change log; 409 reloads the section; returns the result or null
  async function save(sub, body, label) {
    try {
      const r = await api(sub, body);
      GW.writeDone(r, label);
      if (r.applies === "after_restart") restartBar(true);
      return r;
    } catch (e) {
      const k = await GW.writeError(e);
      if (k === "conflict") route();
      return null;
    }
  }
  async function confirmSave(title, rows, applies, extra) {
    const t = h("table", { class: "gw-sb-review" }, h("tr", null, h("th", null, "What"), h("th", null, "New")),
      rows.map(([a, b]) => h("tr", null, h("td", null, a), h("td", { class: "gw-value" }, b))));
    return GW.confirm({ title, wide: true, ok: "Save", el: h("div", null, t, h("p", null, "Takes effect: ", chipFor(applies)),
      extra || null, h("p", { class: "gw-dim" }, "One database transaction with the server's own start-up checks; a backup is taken first and the Change log gets an Undo.")) });
  }

  // ------------------------------------------------------------ restart bar + helper
  function restartBar(show) {
    if (!S) return;
    if (show) S.pendingFlag = true;
    GW.clear(S.bar);
    if (!S.pendingFlag) return;
    put(S.bar, h("div", { class: "gw-online warn gw-w-restartbar", role: "status" },
      h("b", null, "Restart needed. "), "Loot, Medusa, GM monsters / NPCs and quest rewards are read when the game server starts. ",
      btn("How to restart", () => restartHelp(), { small: true, help: "Shows the commands. The editor never restarts the server itself." }), " ",
      btn("Start-up check", () => runPreflight(), { small: true, help: "Runs every check the server does at start-up on the rows as they are now." })));
  }
  async function restartHelp() {
    let o;
    try { o = await api("overview"); } catch (e) { GW.alert(e.message); return; }
    const r = o.restart;
    const pre = h("pre", { class: "gw-w-cmd", style: "background:#000;border:1px solid #3d6b86;padding:8px 10px;color:#7dff9a;font:12px Consolas,monospace;white-space:pre-wrap;user-select:all" }, r.commands.join("\n"));
    GW.confirm({ title: "Restart the game server to apply", wide: true, ok: "Close", cancel: false, el: h("div", { class: "gw-w-help" },
      h("p", null, "For ", h("b", null, r.where), ":"), pre,
      btn("Copy the commands", () => { navigator.clipboard && navigator.clipboard.writeText(r.commands.join("\n")); GW.status("commands copied"); }, { small: true }),
      h("p", null, r.note), h("p", { class: "gw-dim" }, o.pending.length + " saved change" + (o.pending.length === 1 ? "" : "s") + " wait for the restart:"),
      h("ul", { class: "gw-w-pend" }, o.pending.slice(0, 30).map(c => h("li", null, c.action || c.id)))) });
  }
  async function runPreflight(target) {
    let p;
    try { p = await api("preflight"); } catch (e) { GW.alert(e.message); return; }
    const el = h("div", { class: "gw-w-pre" },
      h("p", null, p.ok ? h("b", { class: "gw-good" }, "Everything passes: the server will accept these rows at start-up.")
        : h("b", { class: "tt-bad" }, "Problems found: the server would refuse to start with these rows. Fix or undo them before a restart.")),
      p.families.map(f => h("div", { class: "gw-w-prerow", style: "display:flex;gap:6px;align-items:baseline;margin:3px 0" }, h("span", { class: "gw-dot " + (f.ok ? "on" : "off"), style: f.ok ? null : "background:#ff3b30" }), h("b", null, f.label),
        f.ok ? h("span", { class: "gw-dim" }, " ok") : h("ul", null, f.problems.map(x => h("li", { class: "tt-bad" }, x))))),
      p.warnings.length ? h("div", null, h("b", null, "Warnings (the server starts, but skips these):"), h("ul", null, p.warnings.map(w => h("li", null, w)))) : null);
    if (target) { put(GW.clear(target), el); return; }
    GW.confirm({ title: "Start-up check", wide: true, ok: "Close", cancel: false, el });
  }

  // ------------------------------------------------------------ module
  GW.module("world", {
    async mount(el, ctx) {
      S = { el, ctx, bar: h("div", { class: "gw-w-bar" }), body: h("div", { class: "gw-w-body" }), nav: h("nav", { class: "gw-w-nav", "aria-label": "World sections" }), pendingFlag: false };
      for (const [k, t, d] of SECTIONS) {
        put(S.nav, h("a", { class: "gw-w-navi", href: "#/world/" + k, "data-sec": k, "data-help": d }, t));
      }
      put(el, S.bar, h("div", { class: "gw-w-cols" }, S.nav, S.body));
      // the catalogues every section uses (cached by GW.catalog)
      await Promise.all(["item", "monster", "map"].map(t => GW.catalog.load(t).catch(() => null)));
      try { const o = await api("overview"); S.overview = o; S.pendingFlag = (o.pending || []).length > 0; restartBar(false); } catch (e) { /* shown by the section */ }
      route();
    },
    unmount() { if (S && S.timer) clearInterval(S.timer); S = null; },
    route(ctx) { if (S) { S.ctx = ctx; route(); } },
  });

  function route() {
    if (!S) return;
    const r = (location.hash.split("?")[0].split("/")[2]) || "overview";
    const sec = SECTIONS.find(x => x[0] === r) ? r : "overview";
    for (const a of S.nav.querySelectorAll(".gw-w-navi")) a.classList.toggle("on", a.dataset.sec === sec);
    put(GW.clear(S.body), h("p", { class: "gw-dim" }, "loading ..."));
    const fn = { overview: secOverview, loot: secLoot, medusa: secMedusa, gm: secGm, npc: secNpc, quests: secQuests, instances: secInstances,
      events: secEvents, gifts: secGifts, bosses: secBosses, calendar: secCalendar, server: secServer, balances: secBalances, shops: secShops }[sec];
    Promise.resolve(fn(S.body)).catch(e => { put(GW.clear(S.body), bad(e.message)); });
  }

  // ============================================================ overview
  async function secOverview(body) {
    const o = await api("overview");
    S.overview = o;
    const c = o.counts;
    const tile = (sec, title, value, applies) => h("a", { class: "gw-w-tile", href: "#/world/" + sec, "data-help": (SECTIONS.find(x => x[0] === sec) || [])[2] },
      h("b", null, title), h("span", { class: "gw-value" }, value), applies ? chipFor(applies) : null);
    const pre = h("div");
    put(GW.clear(body), 
      win("World & content",
        h("p", null, "Database ", h("b", null, o.database), o.live ? h("span", { class: "gw-chip restart" }, " LIVE") : null, o.writable ? "" : " (read-only)",
          " · game server: ", h("b", null, o.server.state), o.capabilities.available ? " · round-2 live edits on" : ""),
        note("Changes here reach the game at three different moments. The chip on every part says which: ",
          chipFor("now"), " the next time a player uses it, ", chipFor("next_login"), " when the character next logs in, ",
          chipFor("after_restart"), " after the game server restarts (it reads that content once at start-up)."),
        h("div", { class: "gw-w-tiles" },
          tile("loot", "Monster loot", c.loot_tables + " custom tables · browse all monsters", "after_restart"),
          tile("medusa", "Medusa Island", c.medusa_monsters + " monster rules, " + c.medusa_loot + " drops", "after_restart"),
          tile("gm", "GM monsters", c.gm_monster_spawns + " added, " + c.gm_monster_edits + " edited, " + c.gm_monster_attributes + " stat sets", "after_restart"),
          tile("npc", "GM NPCs", c.gm_npc_spawns + " NPCs, " + c.gm_dialogues + " dialogues", "after_restart"),
          tile("quests", "Quest rewards", c.quest_values + " payouts, " + c.quest_slots + " item menus", "after_restart"),
          tile("instances", "Dungeon entries", "Atlantis, Wonderland, Harbor, Medusa", "now"),
          tile("events", "EXP events", c.exp_events + " running", "next_login"),
          tile("gifts", "Event gifts", c.characters + " characters", "live"),
          tile("bosses", "World bosses", c.world_bosses + " bosses", "not_editable"),
          tile("calendar", "Calendar", "time zone and resets", "after_restart"),
          tile("balances", "Crier & awards", "prices and items", "after_restart"),
          tile("shops", "Shops & Mall", "every shop", "hot_reload"))),
      win("Waiting for a restart",
        o.pending.length ? h("ul", { class: "gw-w-pend" }, o.pending.map(x => h("li", null, h("span", { class: "gw-dim" }, fmtT(x.time) + " "), x.action || x.id)))
          : note("No saved change is waiting for a restart."),
        h("div", { class: "gw-w-line" }, btn("How to restart", () => restartHelp()), btn("Run the start-up check", () => runPreflight(pre))),
        pre));
  }

  // ============================================================ monster loot
  async function secLoot(body) {
    const d = await api("loot");
    const list = h("div", { class: "gw-w-list", role: "list" });
    const right = h("div", { class: "gw-w-right" });
    put(GW.clear(body), 
      d.problems.length ? bad("Start-up problems: " + d.problems.join("; ")) : null,
      h("div", { class: "gw-w-split" },
        win("Loot tables", h("div", { class: "gw-w-line" }, btn("+ New loot table", async () => {
          const k = await GW.pick("monster", { title: "Which monster gets a loot table? (every monster of the game)" });
          if (k) openLoot(right, k, d);
        }, { help: "Pick any monster; its loot table opens empty." }), chipFor("after_restart")),
          note("Each monster can drop up to 8 items per kill (the loot window of the client shows 8). Every drop rolls its own chance."), list),
        right));
    const area=h("select",{"aria-label":"World or instance maps"},h("option",{value:"all"},"All maps"),h("option",{value:"world"},"World maps"),h("option",{value:"instance"},"Instance maps"));
    const map=h("select",{"aria-label":"Monster map"});
    const search=h("input",{type:"search",placeholder:"Search monster name or key","aria-label":"Search monsters"});
    const summary=h("p",{class:"gw-dim"});
    list.before(h("div",{class:"gw-w-line"},area,map,search),summary);
    function maps(){
      const previous=map.value;GW.clear(map).append(h("option",{value:"all"},"Every map"),h("option",{value:"none"},"No mapped spawn"));
      for(const m of d.maps||[])if(area.value==="all"||m.kind===area.value)map.append(h("option",{value:String(m.id)},m.name+" (#"+m.id+")"));
      if(Array.from(map.options).some(o=>o.value===previous))map.value=previous;
      render();
    }
    function render(){
      GW.clear(list);const q=search.value.toLowerCase();let visible=0;
      const allowed=new Set((d.maps||[]).filter(m=>area.value==="all"||m.kind===area.value).map(m=>m.id));
      for(const m of d.monsters||[]){
        if(q&&!(m.name+" "+m.template_key).toLowerCase().includes(q))continue;
        if(map.value==="none"&&m.map_ids.length)continue;
        if(map.value!=="all"&&map.value!=="none"&&!m.map_ids.includes(Number(map.value)))continue;
        if(area.value!=="all"&&!m.map_ids.some(id=>allowed.has(id)))continue;
        const t=d.tables.find(t=>t.template_key===m.template_key),n=d.rules.filter(r=>r.template_key===m.template_key).length;
        const names=m.map_ids.map(id=>(d.maps.find(x=>x.id===id)||{}).name||"Map "+id).join(", ");
        put(list,h("button",{class:"gw-w-row",type:"button",role:"listitem",onclick:()=>openLoot(right,m.template_key,d)},
          h("span",{class:"gw-dot "+(t&&t.enabled?"on":"off")}),h("span",{class:"nm"},m.name||m.template_key),
          h("span",{class:"sub"},m.template_key+" · "+(names||"no mapped spawn")+" · "+(t?n+" configured drops":"no custom loot table"))));
        visible++;
      }
      summary.textContent=visible+" monsters shown · "+(d.monsters||[]).length+" total · "+d.tables.length+" configured loot tables";
      if(!visible)put(list,note("No monsters match these filters."));
    }
    area.onchange=maps;map.onchange=render;search.oninput=render;maps();
    const want = (location.hash.split("?")[1] || "").match(/key=([^&]+)/);
    if (want) openLoot(right, decodeURIComponent(want[1]), d);
    else if (d.tables[0]) openLoot(right, d.tables[0].template_key, d);
    else put(right, win("Loot", note("Choose any monster on the left to review or create its loot table.")));
  }

  async function openLoot(right, key, d) {
    put(GW.clear(right), h("p", { class: "gw-dim" }, "loading " + key + " ..."));
    const m = await api("loot/monster/" + encodeURIComponent(key));
    const table = d.tables.find(t => t.template_key === key) || { template_key: key, maximum_drops: 1, enabled: true };
    const rules = d.rules.filter(r => r.template_key === key).map(r => Object.assign({}, r, { attributes: attrsOf(r) }));
    const st = { md: table.maximum_drops, en: table.enabled, rules };
    const rows = h("div", { class: "gw-w-drops" });
    const mdField = nf({ field: "monster_loot_tables.maximum_drops", label: "Drops per kill (at most)", value: st.md, min: 1, max: 8,
      onChange: v => { st.md = v; summary(); } });
    const summ = h("div", { class: "gw-w-sum" });
    function summary() {
      const on = st.rules.filter(r => r.enabled).length;
      const exp = st.rules.filter(r => r.enabled).reduce((a, r) => a + Number(r.chance_basis_points || 0) / 10000, 0);
      put(GW.clear(summ), h("span", null, on + " drops on; on average ", h("b", null, exp.toFixed(2)), " items per kill before the cap of ", h("b", null, st.md)),
        st.en && on < st.md ? h("span", { class: "tt-bad" }, " · the server refuses to start when a monster can drop more items (" + st.md + ") than it has drops on (" + on + ")") : null);
    }
    function draw() {
      GW.clear(rows);
      st.rules.forEach((r, i) => put(rows, dropRow(r, i, { withExtras: true, onRemove: () => { st.rules.splice(i, 1); draw(); }, onChange: summary })));
      if (!st.rules.length) put(rows, note("No drops yet: add one."));
      summary();
    }
    const where = (m.spawns || []).map(s => h("a", { href: "#/world/gm?map=" + s.map, class: "gw-w-link", "data-help": "Open this map in GM monsters" }, mapName(s.map) + " (" + s.count + ")"));
    put(GW.clear(right), win("Loot of " + (m.name || monsterName(key)),
      h("div", { class: "gw-w-head" }, h("span", { class: "gw-w-mon", "data-tip-type": "monster", "data-tip-id": key }, GW.icon(null, { name: m.name || key })),
        h("div", null, h("b", { class: "gw-w-monname" }, m.name || monsterName(key)), h("div", { class: "gw-dim" }, key + " · " + (m.rank || "") + (m.known ? "" : " · NOT a monster of this server")),
          h("div", { class: "gw-dim" }, "Lives on: ", where.length ? where.reduce((a, x, i) => (i ? a.concat([", ", x]) : [x]), []) : "no published spawn (Medusa / event monster or unused)"))),
      h("div", { class: "gw-w-line" }, mdField, check(st.en, "Table on", { field: "monster_loot_tables.enabled", onchange: v => { st.en = v; summary(); } }), chipFor("after_restart")),
      summ, rows,
      h("div", { class: "gw-w-line" },
        btn("+ Add drop", async () => {
          const id = await GW.pick("item", { title: "Item to drop", itemUse: "loot" });
          if (!id) return;
          st.rules.push({ item_id: Number(id), chance_basis_points: 1000, minimum_quantity: 1, maximum_quantity: 1, enabled: true, bound_on_pickup: null,
            item_quality: null, item_grade: null, attributes: [] });
          draw();
        }, { help: "Pick an item from the full item grid" }),
        btn("Save loot", async () => {
          const body = { template_key: key, version: m.version, table: { maximum_drops: st.md, enabled: st.en }, rules: st.rules.map(r => ({
            loot_index: r.loot_index, item_id: r.item_id, chance_basis_points: r.chance_basis_points, minimum_quantity: r.minimum_quantity,
            maximum_quantity: r.maximum_quantity, enabled: r.enabled, bound_on_pickup: r.bound_on_pickup, item_quality: r.item_quality,
            item_grade: r.item_grade, attributes: r.attributes })) };
          if (!await confirmSave("Save the loot of " + (m.name || key) + "?", [["Drops per kill", st.md], ["Table", st.en ? "on" : "off"]].concat(
            st.rules.map(r => [(itemRow(r.item_id) || {}).name || "#" + r.item_id, pct(r.chance_basis_points) + " · " + r.minimum_quantity + "-" + r.maximum_quantity + (r.enabled ? "" : " · off")])), "after_restart")) return;
          if (await save("loot/save", body, "loot of " + (m.name || key))) secLoot(S.body).then(() => {});
        }),
        m.exists ? btn("Delete table", async () => {
          if (!await GW.confirm({ title: "Delete the loot table of " + (m.name || key) + "?", message: "The monster drops nothing extra after the next restart. Undo is in the Change log.", ok: "Delete", danger: true })) return;
          if (await save("loot/delete", { template_key: key, version: m.version }, "loot table deleted")) secLoot(S.body);
        }, { danger: true }) : null)));
    draw();
  }
  function attrsOf(r) {
    const out = [];
    for (let i = 1; i <= 5; i++) if (r["attribute" + i] !== null && r["attribute" + i] !== undefined) out.push({ id: r["attribute" + i], level: r["attribute_level" + i] });
    return out;
  }

  // one drop row (monster loot, Medusa loot): item cell, chance %, quantity, quality / grade / bound / attributes
  function dropRow(r, i, o) {
    o = o || {};
    const inst = () => ({ item_quality: r.item_quality === null || r.item_quality === undefined ? 1 : r.item_quality, item_grade: r.item_grade || null,
      attrs: (r.attributes || []).map(a => ({ id: a.id, level: a.level || 1 })) });
    const cellBox = h("span");
    const nameBox = h("span");
    const paint = () => {
      put(GW.clear(cellBox), itemCell(r.item_id, o.withExtras ? inst() : null, async () => {
        const id = await GW.pick("item", { title: "Item to drop", itemUse: "loot", value: r.item_id });
        if (id) { r.item_id = Number(id); paint(); }
      }));
      put(GW.clear(nameBox), itemName(r.item_id, o.withExtras ? r.item_quality : null));
    };
    paint();
    const chance = nf({ field: (o.table || "monster_loot_rules") + ".chance_basis_points", label: "Chance %", value: Number(r.chance_basis_points) / 100,
      min: 0.01, max: 100, decimals: 2, unit: "%", onChange: (v, ok) => { if (ok && v !== null) { r.chance_basis_points = Math.round(v * 100); per.textContent = "≈ " + (v).toLocaleString("en-US", { maximumFractionDigits: 2 }) + " in 100 kills"; } if (o.onChange) o.onChange(); } });
    const per = h("span", { class: "gw-dim gw-w-per" }, "≈ " + (Number(r.chance_basis_points) / 100) + " in 100 kills");
    const mn = inp(r.minimum_quantity, { type: "number", min: 1, width: "56px", label: "minimum quantity", field: (o.table || "monster_loot_rules") + ".minimum_quantity", oninput: v => { r.minimum_quantity = Number(v); } });
    const mx = inp(r.maximum_quantity, { type: "number", min: 1, width: "56px", label: "maximum quantity", field: (o.table || "monster_loot_rules") + ".maximum_quantity", oninput: v => { r.maximum_quantity = Number(v); } });
    const row = h("div", { class: "gw-w-drop" + (r.enabled ? "" : " off") },
      h("div", { class: "gw-w-dropmain" }, cellBox, h("div", { class: "gw-w-dropname" }, nameBox, h("span", { class: "gw-dim" }, " #" + r.item_id + (r.loot_index !== undefined && r.loot_index !== null ? " · drop " + r.loot_index : ""))),
        chance, per,
        h("span", { class: "gw-w-qty" }, lab("Quantity", (o.table || "monster_loot_rules") + ".minimum_quantity"), mn, "–", mx),
        check(r.enabled, "on", { field: (o.table || "monster_loot_rules") + ".enabled", onchange: v => { r.enabled = v; row.classList.toggle("off", !v); if (o.onChange) o.onChange(); } }),
        btn("Remove", () => o.onRemove && o.onRemove(), { small: true, danger: true })));
    if (o.withExtras) {
      const q = sel([[null, "item default (Q1)"]].concat(Array.from({ length: 21 }, (_, k) => [k, "Q" + k + " " + (QN()[k] || "") + ""])), r.item_quality,
        { field: "monster_loot_rules.item_quality", label: "quality", onchange: v => { r.item_quality = v === null ? null : Number(v); paint(); } });
      const g = sel([[null, "default"]].concat(Array.from({ length: 26 }, (_, k) => [k, "Grade " + k + (k > 25 ? " (above the client's G25)" : "")])), r.item_grade,
        { field: "monster_loot_rules.item_grade", label: "grade", onchange: v => { r.item_grade = v === null ? null : Number(v); paint(); } });
      const b = sel([[null, "as the item says"], ["true", "bound on pickup"], ["false", "tradeable"]], r.bound_on_pickup === null || r.bound_on_pickup === undefined ? null : String(r.bound_on_pickup),
        { field: "monster_loot_rules.bound_on_pickup", label: "binding", onchange: v => { r.bound_on_pickup = v === null ? null : v === "true"; } });
      const attrs = h("span", { class: "gw-w-attrs" });
      const drawAttrs = () => {
        GW.clear(attrs);
        (r.attributes || []).forEach((a, k) => put(attrs, h("span", { class: "gw-w-attr" },
          GW.idField("appendattr", a.id, { title: "Append attribute", field: "monster_loot_rules.attribute" + (k + 1), filter: allowedAttr(r.item_id), onChange: v => { a.id = v; paint(); } }),
          inp(a.level || "", { type: "number", min: 1, width: "48px", label: "attribute level", placeholder: "Lv", field: "monster_loot_rules.attribute_level" + (k + 1), oninput: v => { a.level = v === "" ? null : Number(v); paint(); } }),
          btn("x", () => { r.attributes.splice(k, 1); drawAttrs(); paint(); }, { small: true, help: "Remove this attribute" }))));
        if ((r.attributes || []).length < 5) put(attrs, btn("+ attribute", async () => {
          const id = await GW.pick("appendattr", { title: "Append attribute (green line)", filter: allowedAttr(r.item_id) });
          if (id === null || id === "") return;
          (r.attributes = r.attributes || []).push({ id: Number(id), level: 1 }); drawAttrs(); paint();
        }, { small: true, help: "Append attributes are the green lines on the item. The grid lists the ones this item can have when the client says so." }));
      };
      drawAttrs();
      put(row, h("div", { class: "gw-w-dropx" }, lab("Quality", "monster_loot_rules.item_quality"), q, lab("Grade", "monster_loot_rules.item_grade"), g,
        lab("Binding", "monster_loot_rules.bound_on_pickup"), b, attrs));
    }
    return row;
  }
  function allowedAttr(itemId) {
    // the attributes this item may roll (client ItemBaseAttribute.xml), when the catalogue knows them
    let allow = null;
    GW.catalog.get("item", itemId).then(e => { if (e && e.append_attributes && e.append_attributes.length) allow = new Set(e.append_attributes.map(Number)); }).catch(() => {});
    return row => !allow || allow.has(Number(row.id));
  }

  // ============================================================ Medusa
  async function secMedusa(body) {
    const d = await api("medusa");
    const tabs = h("div", { class: "gw-w-tabs" });
    const page = h("div");
    const cur = { tab: "monsters", dif: 1 };
    const mk = (k, t) => { const b = h("button", { class: "gw-tabx" + (cur.tab === k ? " on" : ""), type: "button", onclick: () => { cur.tab = k; drawTabs(); show(); } }, t); return b; };
    function drawTabs() { put(GW.clear(tabs), mk("monsters", "Monsters"), mk("loot", "Drops"), mk("rewards", "Honour table"), mk("titles", "Titles")); }
    const difSel = () => h("span", { class: "gw-w-line" }, [1, 2, 3].map(x => btn(DIFF[x], () => { cur.dif = x; show(); }, { small: true, on: cur.dif === x })));
    function show() {
      GW.clear(page);
      if (cur.tab === "monsters") {
        const rows = d.monsters.filter(m => m.difficulty === cur.dif);
        const t = h("table", { class: "gw-w-table" }, h("tr", null, ["Monster", "Level", "Max HP", "Score", "Speed %", "Corpse (no loot) ms", "Corpse (loot) ms", "Pet EXP", ""].map((x, i) =>
          h("th", { "data-field": [null, "medusa_monster_rules.monster_level", "medusa_monster_rules.maximum_health", "medusa_monster_rules.score", "medusa_monster_rules.movement_speed_basis_points", "medusa_monster_rules.corpse_without_loot_ms", "medusa_monster_rules.corpse_with_loot_ms", "medusa_monster_rules.pet_experience"][i] || null }, x))));
        for (const m of rows) {
          const v = { monster_level: m.monster_level, maximum_health: m.maximum_health, score: m.score, movement_speed_basis_points: m.movement_speed_basis_points,
            corpse_without_loot_ms: m.corpse_without_loot_ms, corpse_with_loot_ms: m.corpse_with_loot_ms, pet_experience: m.pet_experience };
          const cell = k => h("td", null, inp(k === "movement_speed_basis_points" ? v[k] / 100 : v[k], { type: "number", width: "92px", label: k, field: "medusa_monster_rules." + k,
            oninput: x => { v[k] = x === "" ? null : (k === "movement_speed_basis_points" ? Math.round(Number(x) * 100) : Number(x)); } }));
          put(t, h("tr", null, h("td", null, h("b", null, m.template_alias.replace(/-/g, " ")), h("div", { class: "gw-dim" }, m.template_alias)),
            ...Object.keys(v).map(cell),
            h("td", null, btn("Save", async () => {
              const ch = Object.keys(v).filter(k => v[k] !== m[k]);
              if (!ch.length) { GW.status("nothing changed"); return; }
              if (!await confirmSave("Save Medusa " + DIFF[m.difficulty] + " " + m.template_alias + "?", ch.map(k => [k.replace(/_/g, " "), GW.fmt(v[k])]), "after_restart")) return;
              const vals = {}; ch.forEach(k => { vals[k] = v[k]; });
              if (await save("medusa/monster", { difficulty: m.difficulty, template_alias: m.template_alias, version: m.version, values: vals }, "Medusa " + m.template_alias)) secMedusa(S.body);
            }, { small: true }))));
        }
        put(page, win("Medusa monsters", h("div", { class: "gw-w-line" }, difSel(), chipFor("after_restart")),
          note("Every monster of the Medusa roster on every difficulty must stay in this list (the server checks it at start-up), so monsters are edited, not added or removed."), h("div", { class: "gw-w-scroll" }, t)));
      } else if (cur.tab === "loot") {
        const mons = d.monsters.filter(m => m.difficulty === cur.dif);
        const box = h("div");
        const pick = sel(mons.map(m => [m.template_alias, m.template_alias.replace(/-/g, " ")]), cur.alias || (mons[0] && mons[0].template_alias), { label: "Medusa monster", onchange: v => { cur.alias = v; drawLoot(); } });
        function drawLoot() {
          const alias = pick.value;
          const m = mons.find(x => x.template_alias === alias);
          const st = d.loot.filter(l => l.difficulty === cur.dif && l.template_alias === alias).map(l => Object.assign({}, l));
          const rows = h("div", { class: "gw-w-drops" });
          const draw = () => { GW.clear(rows); st.forEach((r, i) => put(rows, dropRow(r, i, { table: "medusa_monster_loot_rules", onRemove: () => { st.splice(i, 1); draw(); } }))); if (!st.length) put(rows, note("This monster drops nothing on this difficulty.")); };
          draw();
          put(GW.clear(box), rows, h("div", { class: "gw-w-line" },
            btn("+ Add drop", async () => { const id = await GW.pick("item", { title: "Item to drop", itemUse: "loot" }); if (id) { st.push({ item_id: Number(id), chance_basis_points: 1000, minimum_quantity: 1, maximum_quantity: 1, enabled: true }); draw(); } }),
            btn("Save drops", async () => {
              if (!await confirmSave("Save the drops of " + alias + " (" + DIFF[cur.dif] + ")?", st.map(r => [(itemRow(r.item_id) || {}).name || "#" + r.item_id, pct(r.chance_basis_points) + " · " + r.minimum_quantity + "-" + r.maximum_quantity]), "after_restart")) return;
              if (await save("medusa/loot", { difficulty: cur.dif, template_alias: alias, version: m.loot_version, rules: st.map(r => ({ loot_index: r.loot_index, item_id: r.item_id,
                chance_basis_points: r.chance_basis_points, minimum_quantity: r.minimum_quantity, maximum_quantity: r.maximum_quantity, enabled: r.enabled })) }, "Medusa drops")) secMedusa(S.body);
            })));
        }
        put(page, win("Medusa drops", h("div", { class: "gw-w-line" }, difSel(), lab("Monster"), pick, chipFor("after_restart")), box));
        drawLoot();
      } else if (cur.tab === "rewards") {
        const st = d.rewards.filter(r => r.difficulty === cur.dif).map(r => Object.assign({}, r));
        const box = h("div");
        const titles = [[null, "no title"]].concat(d.titles.map(t => [t.title, t.display_name]));
        function draw() {
          GW.clear(box);
          for (const kind of [1, 2]) {
            const t = h("table", { class: "gw-w-table" }, h("tr", null, h("th", { "data-field": "medusa_completion_reward_rules.threshold" }, kind === 1 ? "Score at least" : "Finished within (seconds)"),
              h("th", { "data-field": "medusa_completion_reward_rules.honor_points" }, "Honour"), kind === 2 ? h("th", { "data-field": "medusa_completion_reward_rules.title" }, "Title") : null, h("th", null, "")));
            st.filter(r => r.reward_kind === kind).sort((a, b) => a.threshold - b.threshold).forEach(r => put(t, h("tr", null,
              h("td", null, inp(r.threshold, { type: "number", width: "90px", label: "threshold", oninput: v => { r.threshold = Number(v); } }), kind === 2 ? h("span", { class: "gw-dim" }, " " + Math.floor(r.threshold / 60) + " min") : null),
              h("td", null, inp(r.honor_points, { type: "number", width: "90px", label: "honour", oninput: v => { r.honor_points = Number(v); } })),
              kind === 2 ? h("td", null, sel(titles, r.title, { label: "title", onchange: v => { r.title = v === null ? null : Number(v); } })) : null,
              h("td", null, btn("x", () => { st.splice(st.indexOf(r), 1); draw(); }, { small: true })))));
            put(box, sub(kind === 1 ? "Run not finished: honour by score (must start at score 0; honour never goes down as the score rises)"
              : "Run finished: honour by time (the last row must be 2400 seconds; honour never goes up as the time rises)", t,
              btn("+ row", () => { st.push({ difficulty: cur.dif, reward_kind: kind, threshold: kind === 1 ? 0 : 2400, honor_points: 100, title: null }); draw(); }, { small: true })));
          }
        }
        draw();
        put(page, win("Medusa honour table", h("div", { class: "gw-w-line" }, difSel(), chipFor("after_restart")), box,
          btn("Save honour table", async () => {
            if (!await confirmSave("Save the " + DIFF[cur.dif] + " honour table?", st.map(r => [(r.reward_kind === 1 ? "score ≥ " : "≤ ") + r.threshold + (r.reward_kind === 2 ? " s" : ""), GW.fmt(r.honor_points) + " honour" + (r.title ? " + title " + r.title : "")]), "after_restart")) return;
            if (await save("medusa/rewards", { difficulty: cur.dif, version: d.reward_versions[String(cur.dif)], rules: st }, "Medusa honour table")) secMedusa(S.body);
          })));
      } else {
        const g = h("div", { class: "gw-w-cards" });
        for (const t of d.titles) {
          const v = Object.assign({}, t);
          const f = k => nf({ field: "medusa_reward_title_definitions." + k, label: { physical_attack_basis_points: "Physical attack %", magic_attack_basis_points: "Magic attack %",
            physical_defense_basis_points: "Physical defence %", magic_defense_basis_points: "Magic defence %" }[k], value: t[k] / 100, min: 0.01, max: 100, decimals: 2,
            onChange: (x, ok) => { if (ok && x !== null) v[k] = Math.round(x * 100); } });
          put(g, sub(null, h("div", { class: "gw-w-line" }, h("span", { class: "gw-w-titleicon", "data-tip-type": "title", "data-tip-id": t.client_title_id }, "★"),
            inp(t.display_name, { label: "title name", field: "medusa_reward_title_definitions.display_name", max: 80, oninput: x => { v.display_name = x; } }),
            h("span", { class: "gw-dim" }, "client title #" + t.client_title_id)),
          ["physical_attack_basis_points", "magic_attack_basis_points", "physical_defense_basis_points", "magic_defense_basis_points"].map(f),
          btn("Save title", async () => {
            if (!await confirmSave("Save the title " + v.display_name + "?", [["Name", v.display_name], ["Bonuses", ["physical_attack_basis_points", "magic_attack_basis_points", "physical_defense_basis_points", "magic_defense_basis_points"].map(k => pct(v[k])).join(" / ")]], "after_restart")) return;
            if (await save("medusa/title", Object.assign({ title: t.title, version: t.version }, v), "Medusa title")) secMedusa(S.body);
          }, { small: true })));
        }
        put(page, win("Medusa titles", h("div", { class: "gw-w-line" }, chipFor("after_restart")), note("Players who own a title keep it; the client title id stays the same."), g));
      }
    }
    put(GW.clear(body), d.problems.length ? bad("Start-up problems: " + d.problems.join("; ")) : null, tabs, page,
      note("Medusa entries per day: see ", h("a", { href: "#/world/instances" }, "Dungeon entries"), "."));
    drawTabs(); show();
  }

  // ============================================================ GM monsters (map view)
  async function secGm(body) {
    const maps = await api("gm/maps");
    const want = Number(((location.hash.split("?")[1] || "").match(/map=(\d+)/) || [])[1] ?? (KEEP.gmMap ?? 0));
    const mapSel = sel(maps.filter(m => m.monsters || m.gm_monsters).map(m => [m.map_id, m.name + " (" + m.monsters + " monsters" + (m.gm_monsters ? ", " + m.gm_monsters + " GM" : "") + ")"]), want,
      { label: "map", onchange: v => { KEEP.gmMap = Number(v); drawMap(Number(v)); } });
    const holder = h("div");
    put(GW.clear(body), win("GM monsters", h("div", { class: "gw-w-line" }, lab("Map", null, "Only maps with published monster spawns are listed: a new GM spawn copies the look of a monster that already lives there."), mapSel, chipFor("after_restart")),
      note("Click a spawn on the map to switch it off, move it or change its stats. Pick a monster type on the right to change every one of them on this map, or to add new ones: then click the map where they should stand."), holder));
    drawMap(Number(mapSel.value));

    async function drawMap(mid) {
      put(GW.clear(holder), h("p", { class: "gw-dim" }, "loading the map ..."));
      const d = await api("gm/map/" + mid);
      const editBy = new Map(d.edits.map(e => [Number(e.object_id), e]));
      const attrBy = new Map(d.attributes.map(a => [a.template_key, a]));
      const types = new Map();
      for (const p of d.published.concat(d.gm_spawns)) {
        const e = p.id === undefined ? editBy.get(Number(p.object_id)) : null;
        const hp = e && e.maximum_health != null ? e.maximum_health : p.maximum_health;
        const t = types.get(p.template_key) || { key: p.template_key, name: monsterName(p.template_key) || p.name,
          n: 0, published: 0, level: p.level, hp, hpMin: hp, hpMax: hp };
        t.n++; if (p.id === undefined) t.published++; t.hpMin = Math.min(t.hpMin, hp); t.hpMax = Math.max(t.hpMax, hp); types.set(p.template_key, t);
      }
      const st = { mode: null, sel: null };
      const canvas = h("canvas", { class: "gw-w-map", width: 760, height: 560, "aria-label": "map of " + d.map.name, tabindex: 0 });
      const side = h("div", { class: "gw-w-side" });
      const legend = h("div", { class: "gw-w-legend" }, h("span", { class: "k pub" }, "monster"), h("span", { class: "k ed" }, "edited"), h("span", { class: "k off" }, "switched off"),
        h("span", { class: "k gm" }, "GM-added"), h("span", { class: "k npc" }, "NPC"), h("span", { class: "k reg" }, "place name"));
      const pts = d.published.concat(d.gm_spawns.map(g => ({ object_id: g.object_id, template_key: g.template_key, name: g.display_name || g.name, x: g.pos_x, z: g.pos_z, gm: g })));
      const all = pts.concat(d.npcs).concat(d.map.regions || []);
      let minX = Infinity, maxX = -Infinity, minZ = Infinity, maxZ = -Infinity;
      for (const p of all) { const x = Number(p.x), z = Number(p.z); if (!isFinite(x) || !isFinite(z)) continue; minX = Math.min(minX, x); maxX = Math.max(maxX, x); minZ = Math.min(minZ, z); maxZ = Math.max(maxZ, z); }
      if (!isFinite(minX)) { minX = -100; maxX = 100; minZ = -100; maxZ = 100; }
      const pad = 20, W = canvas.width, H = canvas.height;
      const sc = Math.min((W - 2 * pad) / Math.max(1, maxX - minX), (H - 2 * pad) / Math.max(1, maxZ - minZ));
      const toS = (x, z) => [pad + (x - minX) * sc, H - pad - (z - minZ) * sc];
      const toW = (sx, sy) => [minX + (sx - pad) / sc, minZ + (H - pad - sy) / sc];
      function paint() {
        const c = canvas.getContext("2d");
        c.fillStyle = "#06131b"; c.fillRect(0, 0, W, H);
        c.strokeStyle = "#16303d"; c.lineWidth = 1;
        for (let i = 0; i <= 10; i++) { c.beginPath(); c.moveTo(pad + i * (W - 2 * pad) / 10, pad); c.lineTo(pad + i * (W - 2 * pad) / 10, H - pad); c.stroke(); c.beginPath(); c.moveTo(pad, pad + i * (H - 2 * pad) / 10); c.lineTo(W - pad, pad + i * (H - 2 * pad) / 10); c.stroke(); }
        c.font = "10px Consolas, monospace";
        for (const r of d.map.regions || []) { const [x, y] = toS(r.x, r.z); c.fillStyle = "#6bccfe88"; c.fillText(r.name, x + 3, y - 3); c.fillRect(x - 1, y - 1, 3, 3); }
        for (const n of d.npcs) { const [x, y] = toS(n.x, n.z); c.fillStyle = "#c8c800"; c.fillRect(x - 2, y - 2, 4, 4); }
        for (const p of d.published) {
          const e = editBy.get(Number(p.object_id));
          const x0 = e && e.pos_x !== null ? e.pos_x : p.x, z0 = e && e.pos_z !== null ? e.pos_z : p.z;
          const [x, y] = toS(x0, z0);
          const hot = st.selType === p.template_key;
          c.fillStyle = e && !e.enabled ? "#666" : e ? "#ff9900" : hot ? "#ffe066" : "#f6b596";
          c.beginPath(); c.arc(x, y, hot ? 3.5 : 2.5, 0, 7); c.fill();
          if (e && !e.enabled) { c.strokeStyle = "#ff3b30"; c.beginPath(); c.moveTo(x - 3, y - 3); c.lineTo(x + 3, y + 3); c.moveTo(x + 3, y - 3); c.lineTo(x - 3, y + 3); c.stroke(); }
        }
        for (const g of d.gm_spawns) { const [x, y] = toS(g.pos_x, g.pos_z); c.fillStyle = g.enabled ? "#00ff00" : "#666"; c.beginPath(); c.moveTo(x, y - 5); c.lineTo(x + 5, y); c.lineTo(x, y + 5); c.lineTo(x - 5, y); c.fill(); }
        if (st.sel) { const [x, y] = toS(st.sel.x, st.sel.z); c.strokeStyle = "#ffff00"; c.lineWidth = 2; c.beginPath(); c.arc(x, y, 8, 0, 7); c.stroke(); }
        if (st.place) { const [x, y] = toS(st.place.x, st.place.z); c.strokeStyle = "#00ff00"; c.lineWidth = 2; c.beginPath(); c.arc(x, y, 9, 0, 7); c.stroke(); c.beginPath(); c.moveTo(x - 12, y); c.lineTo(x + 12, y); c.moveTo(x, y - 12); c.lineTo(x, y + 12); c.stroke(); }
        c.fillStyle = "#8aa3b0"; c.fillText("X " + Math.round(minX) + " … " + Math.round(maxX) + "   Z " + Math.round(minZ) + " … " + Math.round(maxZ), pad, H - 5);
      }
      canvas.addEventListener("click", ev => {
        const r = canvas.getBoundingClientRect();
        const sx = (ev.clientX - r.left) * canvas.width / r.width, sy = (ev.clientY - r.top) * canvas.height / r.height;
        const [wx, wz] = toW(sx, sy);
        if (st.mode === "place") { st.place = { x: Math.round(wx * 10) / 10, z: Math.round(wz * 10) / 10 }; if (st.onPlace) st.onPlace(st.place); paint(); return; }
        let best = null, bd = 64;
        for (const p of pts) { const e = p.gm ? null : editBy.get(Number(p.object_id)); const x0 = e && e.pos_x !== null ? e.pos_x : p.x, z0 = e && e.pos_z !== null ? e.pos_z : p.z; const [x, y] = toS(x0, z0); const dd = (x - sx) ** 2 + (y - sy) ** 2; if (dd < bd) { bd = dd; best = Object.assign({}, p, { x: x0, z: z0 }); } }
        if (best) { st.sel = best; paint(); best.gm ? gmSpawnForm(best.gm) : spawnForm(best); }
      });
      canvas.addEventListener("mousemove", ev => {
        const r = canvas.getBoundingClientRect();
        const [wx, wz] = toW((ev.clientX - r.left) * canvas.width / r.width, (ev.clientY - r.top) * canvas.height / r.height);
        canvas.title = "X " + wx.toFixed(1) + "  Z " + wz.toFixed(1) + (st.mode === "place" ? "  (click to place here)" : "");
      });
      const typeList = h("div", { class: "gw-w-types" });
      for (const t of Array.from(types.values()).sort((a, b) => a.level - b.level)) {
        const a = attrBy.get(t.key);
        put(typeList, h("button", { class: "gw-w-row", type: "button", "data-tip-type": "monster", "data-tip-id": t.key, onclick: () => { st.selType = t.key; paint(); typeForm(t); } },
          h("span", { class: "gw-dot " + (a ? "warn" : "on") }), h("span", { class: "nm" }, t.name), h("span", { class: "sub" }, "Lv " + t.level + " · HP " + (t.hpMin === t.hpMax ? GW.fmt(t.hpMin) : GW.fmt(t.hpMin) + "–" + GW.fmt(t.hpMax)) + " · " + t.n + " here" + (a ? " · stats changed" : ""))));
      }
      put(GW.clear(holder), h("div", { class: "gw-w-mapcols" }, h("div", null, canvas, legend), h("div", null, sub("Monster types on " + d.map.name, typeList), side)));
      paint();
      const statFields = (vals, base, table) => h("div", { class: "gw-w-stats" }, ["level", "maximum_health", "current_health", "physical_attack", "magic_attack", "physical_defense", "magic_defense", "hit", "dodge", "critical", "critical_resistance"].filter(k => table !== "gm_monster_attributes" || !["current_health", "maximum_health"].includes(k)).map(k =>
        h("label", { class: "gw-w-stat" }, lab({ level: "Level", maximum_health: "Max HP", current_health: "HP at spawn", physical_attack: "Phys. attack", magic_attack: "Magic attack", physical_defense: "Phys. defence", magic_defense: "Magic defence", hit: "Hit", dodge: "Dodge", critical: "Critical", critical_resistance: "Crit. resistance" }[k], table + "." + k),
          inp(vals[k], { type: "number", width: "100px", label: k, placeholder: base && base[k] !== undefined ? "game: " + GW.fmt(base[k]) : "game value", oninput: x => { vals[k] = x === "" ? null : Number(x); } }))));
      function pickPlace(cb) {
        st.mode = "place"; st.onPlace = cb; canvas.classList.add("placing");
        GW.status("click the map where it should stand");
      }
      function spawnForm(p) {
        const e = editBy.get(Number(p.object_id)) || {};
        const v = { enabled: e.enabled === undefined ? true : e.enabled, pos_x: e.pos_x ?? null, pos_z: e.pos_z ?? null, facing: e.facing ?? null, note: e.note || "" };
        const vals = {}; for (const k of ["level", "current_health", "maximum_health", "physical_attack", "magic_attack", "physical_defense", "magic_defense", "hit", "dodge", "critical", "critical_resistance"]) vals[k] = e[k] ?? null;
        const px = inp(v.pos_x, { type: "number", width: "80px", label: "X", field: "gm_monster_spawn_edits.pos_x", placeholder: String(p.x), oninput: x => { v.pos_x = x === "" ? null : Number(x); } });
        const pz = inp(v.pos_z, { type: "number", width: "80px", label: "Z", field: "gm_monster_spawn_edits.pos_z", placeholder: String(p.z), oninput: x => { v.pos_z = x === "" ? null : Number(x); } });
        put(GW.clear(side), sub("Spawn " + p.object_id + ": " + p.name,
          h("div", { class: "gw-dim" }, monsterName(p.template_key) + " · " + p.template_key + " · Lv " + p.level + " · HP " + GW.fmt(p.maximum_health) + " · at " + p.x + ", " + p.z),
          h("div", { class: "gw-w-line" }, check(v.enabled, "spawns (untick to remove it from the map)", { field: "gm_monster_spawn_edits.enabled", onchange: x => { v.enabled = x; } })),
          h("div", { class: "gw-w-line" }, lab("Move to", "gm_monster_spawn_edits.pos_x"), px, pz, btn("Pick on map", () => pickPlace(pl => { px.value = pl.x; pz.value = pl.z; v.pos_x = pl.x; v.pos_z = pl.z; st.mode = null; canvas.classList.remove("placing"); }), { small: true }), h("span", { class: "gw-dim" }, "empty = where the game puts it")),
          statFields(vals, p, "gm_monster_spawn_edits"),
          h("div", { class: "gw-w-line" }, lab("Note", "gm_monster_spawn_edits.note"), inp(v.note, { wide: true, label: "note", oninput: x => { v.note = x; } })),
          h("div", { class: "gw-w-line" }, chipFor("after_restart"),
            btn("Save spawn", async () => {
              if (!await confirmSave("Save spawn " + p.object_id + "?", [["Spawns", v.enabled ? "yes" : "no (removed)"], ["Position", v.pos_x === null ? "unchanged" : v.pos_x + ", " + v.pos_z]].concat(Object.entries(vals).filter(([, x]) => x !== null).map(([k, x]) => [k.replace(/_/g, " "), GW.fmt(x)])), "after_restart")) return;
              if (await save("gm/edit", Object.assign({ map_id: mid, object_id: p.object_id, version: d.version, values: vals }, v), "spawn " + p.object_id)) drawMap(mid);
            }),
            e.object_id ? btn("Remove edit", async () => { if (await save("gm/edit", { map_id: mid, object_id: p.object_id, version: d.version, delete: true }, "spawn edit removed")) drawMap(mid); }, { danger: true, help: "Back to the game's own spawn" }) : null)));
      }
      function typeForm(t) {
        const a = attrBy.get(t.key) || {};
        const vals = {}; for (const k of ["level", "physical_attack", "magic_attack", "physical_defense", "magic_defense", "hit", "dodge", "critical", "critical_resistance"]) vals[k] = a[k] ?? null;
        let spawnHp = null;
        const hpSave = btn("Set spawn HP", async () => {
          if (!Number.isInteger(spawnHp) || spawnHp < 1 || spawnHp > 2147483647) return;
          if (!await confirmSave("Set spawn HP for every " + t.name + " on " + d.map.name + "?",
            [["Initial and max HP", GW.fmt(spawnHp)], ["Existing spawns (including disabled)", t.n]], "after_restart")) return;
          if (await save("gm/spawn-hp", {map_id: mid, template_key: t.key, version: d.version,
            publication_version: d.publication_version, maximum_health: spawnHp}, t.name + " spawn HP")) drawMap(mid);
        });
        hpSave.disabled = true;
        const hpInput = inp("", {type: "number", min: 1, max: 2147483647, step: 1, label: "Spawn max HP", width: "140px",
          oninput: x => { spawnHp = x === "" ? null : Number(x); hpSave.disabled = !Number.isInteger(spawnHp) || spawnHp < 1 || spawnHp > 2147483647; }});
        const nw = { count: 1, spread: 0, level: null, maximum_health: null, name: "GM-" + t.key, display_name: "", facing: 0 };
        const placeTxt = h("span", { class: "gw-dim" }, "no place chosen");
        put(GW.clear(side), 
          sub("Every " + t.name + " on " + d.map.name + " (" + t.n + ")",
            note("Empty = the game's own value. Applies to every spawn of this monster on this map; a single spawn's own edit wins."),
            statFields(vals, { level: t.level, maximum_health: t.hp }, "gm_monster_attributes"),
            h("div", { class: "gw-w-line" }, chipFor("after_restart"),
              btn("Save stats", async () => {
                const ch = Object.entries(vals).filter(([, x]) => x !== null);
                if (!ch.length) { GW.alert("Type at least one value, or use 'Remove override'."); return; }
                if (!await confirmSave("Change every " + t.name + " on " + d.map.name + "?", ch.map(([k, x]) => [k.replace(/_/g, " "), GW.fmt(x)]), "after_restart")) return;
                if (await save("gm/attributes", { map_id: mid, template_key: t.key, version: d.version, values: vals }, t.name + " stats")) drawMap(mid);
              }),
              a.template_key ? btn("Remove override", async () => { if (await save("gm/attributes", { map_id: mid, template_key: t.key, version: d.version, delete: true }, "override removed")) drawMap(mid); }, { danger: true }) : null)),
          sub("Spawn HP for " + t.name,
            note("Sets initial and maximum HP on every existing spawn of this type on this map, including disabled spawns. Uses the same spawn data as Monster stat curve. Placement, combat stats and enabled state are kept. Future added spawns are unchanged."),
            h("div", {class: "gw-w-line"}, lab("Spawn max HP"), hpInput, hpSave, chipFor("after_restart"))),
          t.published ? sub("Add " + t.name + " to this map",
            h("div", { class: "gw-w-line" }, btn("Pick the place on the map", () => pickPlace(pl => { nw.x = pl.x; nw.z = pl.z; placeTxt.textContent = "at X " + pl.x + ", Z " + pl.z; }), { small: true }), placeTxt),
            h("div", { class: "gw-w-grid" },
              lab("How many", null, "1-50; more than one stand in a circle"), inp(1, { type: "number", min: 1, width: "70px", label: "how many", oninput: x => { nw.count = Number(x); } }),
              lab("Circle radius", null, "distance between them (map units)"), inp(0, { type: "number", min: 0, width: "70px", label: "spread", oninput: x => { nw.spread = Number(x); } }),
              lab("Name", "gm_monster_spawns.name"), inp(nw.name, { label: "name", max: 100, oninput: x => { nw.name = x; } }),
              lab("Level", "gm_monster_spawns.template_key", "empty = the copied monster's level"), inp("", { type: "number", width: "80px", label: "level", placeholder: String(t.level), oninput: x => { nw.level = x === "" ? null : Number(x); } }),
              lab("Max HP", null, "empty = the copied monster's HP"), inp("", { type: "number", width: "110px", label: "max HP", placeholder: GW.fmt(t.hp), oninput: x => { nw.maximum_health = x === "" ? null : Number(x); } })),
            h("div", { class: "gw-w-line" }, chipFor("after_restart"), btn("Add spawn", async () => {
              if (nw.x === undefined) { GW.alert("Pick the place on the map first."); return; }
              if (!await confirmSave("Add " + nw.count + " x " + t.name + "?", [["Where", d.map.name + " X " + nw.x + ", Z " + nw.z], ["Level", nw.level || "as copied (" + t.level + ")"], ["Max HP", nw.maximum_health || "as copied"]], "after_restart")) return;
              st.mode = null; canvas.classList.remove("placing");
              if (await save("gm/spawn/create", Object.assign({ map_id: mid, template_key: t.key, version: d.version }, nw), "added " + t.name)) drawMap(mid);
            }))) : note("Adding another spawn requires a published appearance on this map."));
      }
      function gmSpawnForm(g) {
        const v = { name: g.name, display_name: g.display_name || "", enabled: g.enabled, x: g.pos_x, z: g.pos_z, facing: g.facing || 0, level: g.level, maximum_health: g.maximum_health, note: g.note || "" };
        const px = inp(v.x, { type: "number", width: "80px", label: "X", oninput: x => { v.x = Number(x); } }), pz = inp(v.z, { type: "number", width: "80px", label: "Z", oninput: x => { v.z = Number(x); } });
        put(GW.clear(side), sub("GM spawn " + g.name,
          h("div", { class: "gw-dim" }, monsterName(g.template_key) + " · object " + g.object_id + " · #" + g.id),
          h("div", { class: "gw-w-grid" },
            lab("Name", "gm_monster_spawns.name"), inp(v.name, { label: "name", oninput: x => { v.name = x; } }),
            lab("Shown name", "gm_monster_spawns.display_name"), inp(v.display_name, { label: "display name", oninput: x => { v.display_name = x; } }),
            lab("Position", "gm_monster_spawns.pos_x"), h("span", null, px, pz, btn("Pick", () => pickPlace(pl => { px.value = pl.x; pz.value = pl.z; v.x = pl.x; v.z = pl.z; st.mode = null; canvas.classList.remove("placing"); }), { small: true })),
            lab("Level"), inp(v.level, { type: "number", width: "80px", label: "level", oninput: x => { v.level = Number(x); } }),
            lab("Max HP"), inp(v.maximum_health, { type: "number", width: "110px", label: "max HP", oninput: x => { v.maximum_health = Number(x); } }),
            lab("Spawns", "gm_monster_spawns.enabled"), check(v.enabled, "on", { onchange: x => { v.enabled = x; } })),
          h("div", { class: "gw-w-line" }, chipFor("after_restart"),
            btn("Save", async () => { if (await save("gm/spawn/update", Object.assign({ id: g.id, version: d.version }, v), "GM spawn " + v.name)) drawMap(mid); }),
            btn("Delete", async () => {
              if (!await GW.confirm({ title: "Delete " + g.name + "?", message: "Tip: untick 'on' instead to keep it undoable. The Undo of a delete needs a framework fix (the row id is generated by the database).", ok: "Delete", danger: true })) return;
              if (await save("gm/spawn/delete", { id: g.id, version: d.version }, "GM spawn deleted")) drawMap(mid);
            }, { danger: true }))));
      }
      put(side, note("Click a spawn on the map, or a monster type above."));
    }
  }

  // ============================================================ GM NPCs + dialogue
  async function secNpc(body) {
    const d = await api("npc");
    const tabs = h("div", { class: "gw-w-tabs" });
    const page = h("div");
    KEEP.npcTab = KEEP.npcTab || "spawns";
    const drawTabs = () => put(GW.clear(tabs), ...[["spawns", "NPC placements"], ["dialogues", "Dialogue windows"]].map(([k, t]) =>
      h("button", { class: "gw-tabx" + (KEEP.npcTab === k ? " on" : ""), type: "button", onclick: () => { KEEP.npcTab = k; drawTabs(); show(); } }, t)));
    function show() { GW.clear(page); if (KEEP.npcTab === "spawns") spawnPage(); else dialoguePage(); }
    put(GW.clear(body), d.problems.length ? bad("Start-up problems: " + d.problems.join("; ")) : null, tabs, page);
    drawTabs(); show();

    function spawnPage() {
      const list = h("div", { class: "gw-w-list" });
      const form = h("div");
      for (const s of d.spawns) put(list, h("button", { class: "gw-w-row", type: "button", onclick: () => spawnEdit(s) },
        h("span", { class: "gw-dot " + (s.enabled ? (d.skipped.includes(s.id) ? "warn" : "on") : "off") }), h("span", { class: "nm" }, s.name),
        h("span", { class: "sub" }, s.map_name + " · " + s.pos_x + ", " + s.pos_z + (s.dialogue_key ? " · talks: " + s.dialogue_key : " · no dialogue") + (d.skipped.includes(s.id) ? " · model not on this map: skipped" : ""))));
      if (!d.spawns.length) put(list, note("No GM NPCs yet."));
      put(page, h("div", { class: "gw-w-split" }, win("GM NPCs", h("div", { class: "gw-w-line" }, btn("+ New NPC", () => spawnEdit(null)), chipFor("after_restart")),
        note("The server places a GM NPC only with a model another NPC already uses on that map, and only a dialogue makes it clickable."), list), form));
      async function spawnEdit(s) {
        const v = s ? { id: s.id, name: s.name, map_id: s.map_id, template_key: s.template_key, npc_key: s.npc_key, dialogue_key: s.dialogue_key, x: s.pos_x, z: s.pos_z, facing: s.facing, enabled: s.enabled, note: s.note, appearance_type: s.appearance_type }
          : { id: null, name: "", map_id: 0, template_key: null, npc_key: "", dialogue_key: null, x: null, z: null, facing: 0, enabled: true, note: "", appearance_type: null };
        const models = h("div", { class: "gw-w-models" });
        const posTxt = h("span", { class: "gw-dim" }, v.x === null ? "no place chosen" : "X " + v.x + ", Z " + v.z);
        const mapBox = h("div");
        async function loadModels() {
          put(GW.clear(models), note("loading the NPC models of " + mapName(v.map_id) + " ..."));
          const ms = await api("npc/templates/" + v.map_id);
          GW.clear(models);
          if (!ms.length) put(models, bad("No NPC stands on this map, so a GM NPC cannot be placed here."));
          for (const m of ms) {
            const b = h("button", { class: "gw-w-model" + (m.template_key === v.template_key ? " on" : ""), type: "button", "data-help": m.desc || m.template_key, onclick: () => {
              v.template_key = m.template_key; v.appearance_type = m.appearance_type; for (const x of models.children) x.classList.remove("on"); b.classList.add("on");
              if (!v.name) { v.name = m.name.replace(/[^\x20-\x7e]/g, "").trim() + " (GM)"; nameIn.value = v.name; }
              if (v.x === null) { v.x = Math.round(m.x) + 3; v.z = Math.round(m.z) + 3; posTxt.textContent = "X " + v.x + ", Z " + v.z + " (next to " + m.name + ")"; }
            } }, GW.icon(null, { name: m.name }), h("span", { class: "nm" }, m.name), h("span", { class: "sub" }, m.template_key + " · used " + m.used + "x"));
            put(models, b);
          }
        }
        const nameIn = inp(v.name, { label: "name", field: "gm_npc_spawns.name", max: 120, oninput: x => { v.name = x; } });
        const mapsAll = await api("gm/maps");
        const dlgSel = sel([[null, "none (cannot be clicked)"]].concat(d.dialogues.map(x => [x.dialogue_key, x.dialogue_key + (x.display_name ? " - " + x.display_name : "")])), v.dialogue_key,
          { field: "gm_npc_spawns.dialogue_key", label: "dialogue", onchange: x => { v.dialogue_key = x; } });
        put(GW.clear(form), win(s ? "NPC " + s.name : "New GM NPC",
          h("div", { class: "gw-w-grid" },
            lab("Map", "gm_npc_spawns.map_id"), sel(mapsAll.filter(m => m.npcs).map(m => [m.map_id, m.name + " (" + m.npcs + " NPCs)"]), v.map_id, { onchange: x => { v.map_id = Number(x); v.template_key = null; loadModels(); } }),
            lab("Name", "gm_npc_spawns.name"), nameIn,
            lab("Key", "gm_npc_spawns.npc_key", "unique; empty = made from the name"), inp(v.npc_key, { label: "npc key", max: 96, placeholder: "GM_<name>", oninput: x => { v.npc_key = x; } }),
            lab("Dialogue", "gm_npc_spawns.dialogue_key"), dlgSel,
            lab("Position", "gm_npc_spawns.pos_x"), h("span", null, posTxt, " ", btn("Pick on the map", () => pickOnMap(), { small: true })),
            lab("Facing", "gm_npc_spawns.facing"), inp(v.facing, { type: "number", width: "80px", label: "facing", oninput: x => { v.facing = Number(x); } }),
            lab("Stands", "gm_npc_spawns.enabled"), check(v.enabled, "on", { onchange: x => { v.enabled = x; } }),
            lab("Note", "gm_npc_spawns.note"), inp(v.note, { wide: true, label: "note", oninput: x => { v.note = x; } })),
          sub("Model (the NPCs that stand on this map)", models), mapBox,
          h("div", { class: "gw-w-line" }, chipFor("after_restart"),
            btn(s ? "Save NPC" : "Add NPC", async () => {
              if (!v.template_key) { GW.alert("Pick a model."); return; }
              if (v.x === null) { GW.alert("Pick the place."); return; }
              if (!await confirmSave((s ? "Save " : "Add ") + (v.name || "the NPC") + "?", [["Map", mapName(v.map_id)], ["Model", v.template_key], ["Where", v.x + ", " + v.z], ["Dialogue", v.dialogue_key || "none"]], "after_restart")) return;
              if (await save("npc/spawn/save", Object.assign({}, v, { version: d.spawns_version }), "GM NPC " + v.name)) secNpc(S.body);
            }),
            s ? btn("Delete", async () => {
              if (!await GW.confirm({ title: "Delete " + s.name + "?", message: "Tip: untick 'Stands' to keep it undoable; the Undo of a delete needs a framework fix (generated row id).", ok: "Delete", danger: true })) return;
              if (await save("npc/spawn/delete", { id: s.id, version: d.spawns_version }, "GM NPC deleted")) secNpc(S.body);
            }, { danger: true }) : null)));
        loadModels();
        async function pickOnMap() {
          const mp = await api("gm/map/" + v.map_id);
          const c = h("canvas", { class: "gw-w-map small", width: 520, height: 380 });
          const all = mp.npcs.concat(mp.published).concat(mp.map.regions || []);
          let a = Infinity, b = -Infinity, e = Infinity, f = -Infinity;
          for (const p of all) { a = Math.min(a, p.x); b = Math.max(b, p.x); e = Math.min(e, p.z); f = Math.max(f, p.z); }
          if (!isFinite(a)) { a = -100; b = 100; e = -100; f = 100; }
          const sc2 = Math.min(480 / Math.max(1, b - a), 340 / Math.max(1, f - e));
          const g = c.getContext("2d");
          const P = (x, z) => [20 + (x - a) * sc2, 360 - (z - e) * sc2];
          const paint2 = () => {
            g.fillStyle = "#06131b"; g.fillRect(0, 0, 520, 380); g.font = "10px Consolas";
            for (const p of mp.published) { const [x, y] = P(p.x, p.z); g.fillStyle = "#f6b59666"; g.fillRect(x - 1, y - 1, 2, 2); }
            for (const r of mp.map.regions || []) { const [x, y] = P(r.x, r.z); g.fillStyle = "#6bccfe"; g.fillText(r.name, x + 3, y); }
            for (const n of mp.npcs) { const [x, y] = P(n.x, n.z); g.fillStyle = n.template_key === v.template_key ? "#ffff00" : "#c8c800"; g.fillRect(x - 2, y - 2, 4, 4); }
            if (v.x !== null) { const [x, y] = P(v.x, v.z); g.strokeStyle = "#00ff00"; g.lineWidth = 2; g.beginPath(); g.arc(x, y, 7, 0, 7); g.stroke(); }
          };
          paint2();
          c.addEventListener("click", ev => {
            const r = c.getBoundingClientRect(); const sx = (ev.clientX - r.left) * 520 / r.width, sy = (ev.clientY - r.top) * 380 / r.height;
            v.x = Math.round((a + (sx - 20) / sc2) * 10) / 10; v.z = Math.round((e + (360 - sy) / sc2) * 10) / 10; posTxt.textContent = "X " + v.x + ", Z " + v.z; paint2();
          });
          put(GW.clear(mapBox), sub("Click where the NPC should stand (yellow squares: NPCs, the bright ones use this model)", c));
        }
      }
    }

    function dialoguePage() {
      const list = h("div", { class: "gw-w-list" });
      const form = h("div");
      for (const x of d.dialogues) put(list, h("button", { class: "gw-w-row", type: "button", onclick: () => dlgEdit(x) },
        h("span", { class: "gw-dot " + (x.enabled ? "on" : "off") }), h("span", { class: "nm" }, x.display_name || x.dialogue_key), h("span", { class: "sub" }, x.dialogue_key + " · " + x.pages.length + " pages")));
      if (!d.dialogues.length) put(list, note("No dialogues yet."));
      put(page, h("div", { class: "gw-w-split" }, win("Dialogues", h("div", { class: "gw-w-line" }, btn("+ New dialogue", () => dlgEdit(null)), chipFor("after_restart")),
        note("A dialogue is the window that opens when a player clicks the NPC: pages of text with up to 12 buttons each."), list), form));
      function dlgEdit(x) {
        const v = x ? JSON.parse(JSON.stringify(x)) : { dialogue_key: "", display_name: "", function_flag: d.default_function_flag, entry_page: 1, enabled: true, note: "",
          pages: [{ page_index: 1, body_text: "Hello, traveller!", note: "", buttons: [{ slot: 1, label: "Goodbye", next_page_index: null }] }] };
        const pagesBox = h("div");
        const preview = h("div", { class: "gw-w-npcwin gw-frame-simplewindow" });
        let cur = v.pages[0] ? v.pages[0].page_index : 1;
        function drawPreview() {
          const p = v.pages.find(q => q.page_index === cur) || v.pages[0];
          put(GW.clear(preview), h("div", { class: "gw-w-npct" }, v.display_name || "NPC"), h("div", { class: "gw-w-npcbody" }, p ? GW.text(p.body_text || "") : ""),
            h("div", { class: "gw-w-npcbtns" }, p ? p.buttons.slice().sort((a, b) => a.slot - b.slot).map(b => h("button", { class: "gw-btnx small", type: "button",
              "data-help": "Sends " + ((v.pages.find(q => q.buttons.includes(b)) || {}).page_index * 1000 + b.slot) + (b.next_page_index ? " → page " + b.next_page_index : " → closes"), onclick: () => { if (b.next_page_index) { cur = b.next_page_index; drawPages(); } } }, b.label || "(button " + b.slot + ")")) : null));
        }
        function drawPages() {
          GW.clear(pagesBox);
          const tabs2 = h("div", { class: "gw-w-tabs" }, v.pages.map(p => h("button", { class: "gw-tabx" + (p.page_index === cur ? " on" : ""), type: "button", onclick: () => { cur = p.page_index; drawPages(); } },
            "Page " + p.page_index + (p.page_index === v.entry_page ? " (first)" : ""))),
            btn("+ page", () => { const n = Math.max(0, ...v.pages.map(p => p.page_index)) + 1; v.pages.push({ page_index: n, body_text: "", note: "", buttons: [] }); cur = n; drawPages(); }, { small: true }));
          const p = v.pages.find(q => q.page_index === cur);
          put(pagesBox, tabs2);
          if (p) {
            const ta = h("textarea", { class: "gw-w-ta", rows: 5, "aria-label": "page text", "data-field": "gm_npc_dialogue_pages.body_text" }); ta.value = p.body_text;
            ta.addEventListener("input", () => { p.body_text = ta.value; drawPreview(); });
            const bt = h("table", { class: "gw-w-table" }, h("tr", null, h("th", { "data-field": "gm_npc_dialogue_buttons.slot" }, "Slot"), h("th", { "data-field": "gm_npc_dialogue_buttons.label" }, "Button text"),
              h("th", { "data-field": "gm_npc_dialogue_buttons.result_number" }, "Result no."), h("th", { "data-field": "gm_npc_dialogue_buttons.next_page_index" }, "Then"), h("th", null, "")));
            p.buttons.forEach((b, i) => put(bt, h("tr", null,
              h("td", null, sel(Array.from({ length: 12 }, (_, k) => [k + 1, String(k + 1)]), b.slot, { label: "slot", onchange: y => { b.slot = Number(y); drawPreview(); } })),
              h("td", null, inp(b.label, { label: "button text", max: 120, oninput: y => { b.label = y; drawPreview(); } })),
              h("td", { class: "gw-dim", "data-help": "The number the client sends back when this button is clicked: page x 1000 + slot (set by the game's protocol, not editable)." }, String(p.page_index * 1000 + b.slot)),
              h("td", null, sel([[null, "close the window"]].concat(v.pages.map(q => [q.page_index, "go to page " + q.page_index])), b.next_page_index, { label: "next page", onchange: y => { b.next_page_index = y === null ? null : Number(y); drawPreview(); } })),
              h("td", null, btn("x", () => { p.buttons.splice(i, 1); drawPages(); }, { small: true })))));
            put(pagesBox, h("div", { class: "gw-w-line" }, lab("Text", "gm_npc_dialogue_pages.body_text", "Colour codes like |cFFFFFF00yellow|cFFFFFFFF work as in the game")), ta, bt,
              h("div", { class: "gw-w-line" }, p.buttons.length < 12 ? btn("+ button", () => { const used = new Set(p.buttons.map(b => b.slot)); let s2 = 1; while (used.has(s2)) s2++; p.buttons.push({ slot: s2, label: "", next_page_index: null }); drawPages(); }, { small: true }) : null,
                v.pages.length > 1 ? btn("Delete this page", () => { v.pages = v.pages.filter(q => q !== p); for (const q of v.pages) for (const b of q.buttons) if (b.next_page_index === p.page_index) b.next_page_index = null; if (v.entry_page === p.page_index) v.entry_page = v.pages[0].page_index; cur = v.pages[0].page_index; drawPages(); }, { small: true, danger: true }) : null,
                v.entry_page !== p.page_index ? btn("Make this the first page", () => { v.entry_page = p.page_index; drawPages(); }, { small: true }) : null));
          }
          drawPreview();
        }
        put(GW.clear(form), win(x ? "Dialogue " + x.dialogue_key : "New dialogue",
          h("div", { class: "gw-w-grid" },
            lab("Key", "gm_npc_dialogues.dialogue_key"), x ? h("b", null, v.dialogue_key) : inp("", { label: "dialogue key", max: 120, placeholder: "e.g. festival_envoy", oninput: y => { v.dialogue_key = y; } }),
            lab("Title", "gm_npc_dialogues.display_name"), inp(v.display_name, { label: "title", max: 255, oninput: y => { v.display_name = y; drawPreview(); } }),
            lab("Client slot", "gm_npc_dialogues.function_flag"), inp(v.function_flag, { type: "number", width: "80px", label: "function flag", oninput: y => { v.function_flag = Number(y); } }),
            lab("On", "gm_npc_dialogues.enabled"), check(v.enabled, "on", { onchange: y => { v.enabled = y; } })),
          h("div", { class: "gw-w-dlg" }, h("div", null, pagesBox), h("div", null, h("div", { class: "gw-dim" }, "Preview (click a button to follow it)"), preview)),
          h("div", { class: "gw-w-line" }, chipFor("after_restart"),
            btn("Save dialogue", async () => {
              if (!await confirmSave("Save the dialogue " + v.dialogue_key + "?", [["Pages", v.pages.length], ["Buttons", v.pages.reduce((a, p) => a + p.buttons.length, 0)], ["First page", v.entry_page]], "after_restart")) return;
              if (await save("npc/dialogue/save", Object.assign({}, v, { version: x ? x.version : "new" }), "dialogue " + v.dialogue_key)) { KEEP.npcTab = "dialogues"; secNpc(S.body); }
            }),
            x ? btn("Delete", async () => {
              if (!await GW.confirm({ title: "Delete the dialogue " + x.dialogue_key + "?", message: "NPCs that use it stay but cannot be clicked. Undo is in the Change log.", ok: "Delete", danger: true })) return;
              if (await save("npc/dialogue/delete", { dialogue_key: x.dialogue_key, version: x.version }, "dialogue deleted")) { KEEP.npcTab = "dialogues"; secNpc(S.body); }
            }, { danger: true }) : null)));
        drawPages();
      }
    }
  }

  // ============================================================ quest rewards
  async function secQuests(body) {
    const d = await api("quests");
    const changed = new Set(d.values.map(v => v.quest_id).concat(d.slots.map(s => s.quest_id)).map(String));
    const right = h("div", { class: "gw-w-right" });
    let only = false;
    const grid = GW.grid({ type: "quest", height: "calc(100vh - 330px)", filter: r => !only || changed.has(String(r.id)), onSelect: r => openQuest(right, r.id, changed) });
    put(GW.clear(body), h("div", { class: "gw-w-split wide" },
      win("Quests", h("div", { class: "gw-w-line" }, check(false, "only quests with changed rewards (" + changed.size + ")", { onchange: x => { only = x; grid.setType("quest", true); } }), chipFor("after_restart")),
        note("Every quest of the game. Click one to change what it pays."), grid.el),
      right));
    put(right, win("Quest rewards", note("Pick a quest on the left.")));
  }
  async function openQuest(right, qid, changed) {
    put(GW.clear(right), h("p", { class: "gw-dim" }, "loading quest " + qid + " ..."));
    const d = await api("quest/" + qid);
    const q = d.quest || { id: qid, title: "quest " + qid };
    const def = q.rewards || {};
    const vals = d.values ? { experience: d.values.experience, talent_points: d.values.talent_points, silver: d.values.silver, gold: d.values.gold } : null;
    const st = { over: !!vals, v: vals || { experience: Number(def.Exp || 0), talent_points: Number(def.TP || 0), silver: Number(def.Money || 0), gold: 0 },
      slotsOver: d.slots.length > 0, slots: d.slots.map(s => ({ item_id: s.item_id, item_quality: s.item_quality, item_grade: s.item_grade, attributes: attrsOf(s) })) };
    const valBox = h("div"), slotBox = h("div");
    function drawVals() {
      put(GW.clear(valBox), check(st.over, "use my own payout", { onchange: x => { st.over = x; drawVals(); } }));
      if (!st.over) { put(valBox, note("The quest pays what it shipped with" + (Object.keys(def).length ? ": " + Object.entries(def).map(([k, x]) => k + " " + x).join(", ") : "") + ".")); return; }
      put(valBox, h("div", { class: "gw-w-stats" },
        nf({ field: "quest_reward_values.experience", label: "EXP", value: st.v.experience, min: 0, max: 2147483647, onChange: (x, ok) => { if (ok) st.v.experience = x; } }),
        nf({ field: "quest_reward_values.talent_points", label: "Talent points", value: st.v.talent_points, min: 0, max: 2147483647, onChange: (x, ok) => { if (ok) st.v.talent_points = x; } }),
        nf({ field: "quest_reward_values.silver", label: "Silver", value: st.v.silver, min: 0, max: 2147483647, onChange: (x, ok) => { if (ok) st.v.silver = x; } }),
        nf({ field: "quest_reward_values.gold", label: "Gold", value: st.v.gold, min: 0, max: 2147483647, onChange: (x, ok) => { if (ok) st.v.gold = x; } })));
    }
    function drawSlots() {
      put(GW.clear(slotBox), check(st.slotsOver, "replace the reward items", { onchange: x => { st.slotsOver = x; drawSlots(); } }));
      if (!st.slotsOver) { put(slotBox, note("The quest offers the reward items it shipped with.")); return; }
      const cells = h("div", { class: "gw-w-slots" });
      for (let i = 0; i < 8; i++) {
        const s = st.slots[i];
        const box = h("div", { class: "gw-w-slot" },
          itemCell(s ? s.item_id : null, s ? { item_quality: s.item_quality ?? 1, item_grade: s.item_grade, attrs: (s.attributes || []).map(a => ({ id: a.id, level: a.level || 1 })) } : null, async () => {
            const id = await GW.pick("item", { title: "Reward item " + (i + 1), value: s ? s.item_id : null, allowNone: !!s, noneLabel: "Remove" });
            if (id === null) return;
            if (id === "") st.slots.splice(i, 1); else if (s) s.item_id = Number(id); else st.slots.push({ item_id: Number(id), item_quality: null, item_grade: null, attributes: [] });
            drawSlots();
          }, { label: "reward slot " + (i + 1) }),
          s ? h("div", { class: "gw-w-slotx" },
            sel([[null, "Q default"]].concat(Array.from({ length: 21 }, (_, k) => [k, "Q" + k + " " + (QN()[k] || "")])), s.item_quality, { field: "quest_reward_slots.item_quality", label: "quality", onchange: x => { s.item_quality = x === null ? null : Number(x); drawSlots(); } }),
            sel([[null, "G default"]].concat(Array.from({ length: 26 }, (_, k) => [k, "Grade " + k])), s.item_grade, { field: "quest_reward_slots.item_grade", label: "grade", onchange: x => { s.item_grade = x === null ? null : Number(x); drawSlots(); } }),
            btn((s.attributes || []).length ? (s.attributes.length + " attr.") : "+ attr.", async () => {
              const id = await GW.pick("appendattr", { title: "Append attribute for " + ((itemRow(s.item_id) || {}).name || "the item"), allowNone: (s.attributes || []).length > 0, noneLabel: "Clear all" });
              if (id === null) return;
              if (id === "") s.attributes = []; else if ((s.attributes || []).length < 5) (s.attributes = s.attributes || []).push({ id: Number(id), level: 1 });
              drawSlots();
            }, { small: true, help: "Add a green append attribute (Lv 1); 'Clear all' removes them" })) : null);
        if (i > st.slots.length) box.classList.add("dim");
        put(cells, box);
      }
      put(slotBox, cells, note("Empty cells are not offered. The player picks from these when finishing the quest."));
    }
    drawVals(); drawSlots();
    put(GW.clear(right), win(q.title || "quest " + qid,
      h("div", { class: "gw-dim", "data-tip-type": "quest", "data-tip-id": qid }, "#" + qid + (q.giver_name ? " · from " + q.giver_name : "") + (q.giver_map_name ? " (" + q.giver_map_name + ")" : "") + (q.min_level ? " · Lv " + q.min_level + "-" + q.max_level : "")),
      d.editable === false ? readOnly(d.why) : null,
      sub("Payout", valBox), sub("Reward items (up to 8)", slotBox),
      h("div", { class: "gw-w-line" }, chipFor("after_restart"), d.editable === false ? null : btn("Save quest rewards", async () => {
        const body = { quest_id: qid, version: d.version, values: st.over ? st.v : null, slots: st.slotsOver ? st.slots : [] };
        if (!await confirmSave("Save the rewards of " + (q.title || qid) + "?", [["Payout", st.over ? "EXP " + GW.fmt(st.v.experience) + ", TP " + st.v.talent_points + ", Silver " + GW.fmt(st.v.silver) + ", Gold " + GW.fmt(st.v.gold) : "as shipped"],
          ["Items", st.slotsOver ? st.slots.map(s => (itemRow(s.item_id) || {}).name || "#" + s.item_id).join(", ") || "none" : "as shipped"]], "after_restart")) return;
        if (await save("quest/save", body, "quest " + qid + " rewards")) { changed.add(String(qid)); openQuest(right, qid, changed); }
      }))));
  }

  // ============================================================ dungeon entries
  async function secInstances(body) {
    const d = await api("instances");
    const cards = h("div", { class: "gw-w-cards" });
    const ICON = { 1: "Atlantis", 2: "Wonderland", 3: "Harbor Attack" };
    for (const r of d.legacy) {
      const v = { free: r.free_entry_limit, paid: r.paid_retry_limit, unlimited: r.paid_retry_limit === null };
      const paidF = nf({ field: "legacy_instance_settings.paid_retry_limit", label: "Paid retries (1 Opal each)", value: v.paid === null ? 0 : v.paid, min: 0, max: 99, onChange: (x, ok) => { if (ok) v.paid = x; }, disabled: v.unlimited });
      put(cards, sub(ICON[r.instance_kind] || r.name,
        nf({ field: "legacy_instance_settings.free_entry_limit", label: "Free entries per day", value: v.free, min: 0, max: 99, onChange: (x, ok) => { if (ok) v.free = x; } }),
        paidF, check(v.unlimited, "unlimited paid retries", { field: "legacy_instance_settings.paid_retry_limit", onchange: x => { v.unlimited = x; } }),
        h("div", { class: "gw-w-line" }, chipFor("now"), btn("Save", async () => {
          const paid = v.unlimited ? null : v.paid;
          if (!await confirmSave("Save " + r.name + "?", [["Free entries per day", v.free], ["Paid retries", paid === null ? "unlimited" : paid]], "now",
            h("p", { class: "tt-bad" }, "An 'advanced' table: the next entry of every player uses the new numbers at once."))) return;
          if (await save("instances/save", { which: "legacy", instance_kind: r.instance_kind, free_entry_limit: v.free, paid_retry_limit: paid, version: r.version, advanced: true }, r.name + " entries")) secInstances(S.body);
        }, { small: true }))));
    }
    if (d.medusa) {
      const v = { n: d.medusa.daily_entry_limit };
      put(cards, sub("Medusa Island",
        nf({ field: "medusa_instance_settings.daily_entry_limit", label: "Entries per day (all difficulties)", value: v.n, min: 1, max: 99, onChange: (x, ok) => { if (ok) v.n = x; } }),
        h("div", { class: "gw-w-line" }, chipFor("now"), btn("Save", async () => {
          if (!await confirmSave("Save Medusa Island?", [["Entries per day", v.n]], "now")) return;
          if (await save("instances/save", { which: "medusa", daily_entry_limit: v.n, version: d.medusa.version, advanced: true }, "Medusa entries")) secInstances(S.body);
        }, { small: true }))));
    }
    put(GW.clear(body), win("Dungeon entries", note("Read every time a player enters, so a change works at once (no restart). A realm day starts at midnight in the realm's time zone (see Calendar)."), cards,
      d.rates.length ? sub("Related server rates (edit them on the Server rates page)", h("div", { class: "gw-kv" }, d.rates.map(r => [h("span", { class: "gw-label" }, r.key), h("span", { class: "gw-value" }, String(r.value))]).flat()),
        h("a", { href: "#/rates" }, "Open Server rates")) : null));
  }

  // ============================================================ EXP events
  function localInput(d) { const z = new Date(d.getTime() - d.getTimezoneOffset() * 60000); return z.toISOString().slice(0, 16); }
  function targetPicker(onChange) {
    const t = { camp: null, class: null, minlv: null, maxlv: null };
    const count = h("span", { class: "gw-dim", "aria-live": "polite" });
    let timer = null;
    const refresh = () => { clearTimeout(timer); timer = setTimeout(async () => {
      try { const r = await api("targets", t); count.textContent = r.characters.length + " characters (" + r.offline + " offline, " + r.online + " online)"; if (onChange) onChange(t, r); }
      catch (e) { count.textContent = e.message; } }, 250); };
    const el = h("div", { class: "gw-w-target" },
      lab("Who", null, "Every active character, or a group. Online characters are handled as each part explains."),
      sel([[null, "both camps"], [0, "Sparta"], [1, "Athens"]], null, { label: "camp", onchange: v => { t.camp = v; refresh(); } }),
      sel([[null, "every class"]].concat(CLASSES.map((c, i) => [i, c])), null, { label: "class", onchange: v => { t.class = v; refresh(); } }),
      lab("Level"), inp("", { type: "number", width: "60px", label: "lowest level", placeholder: "1", oninput: v => { t.minlv = v || null; refresh(); } }), "–",
      inp("", { type: "number", width: "60px", label: "highest level", placeholder: "200", oninput: v => { t.maxlv = v || null; refresh(); } }), count);
    refresh();
    return { el, t, refresh };
  }
  async function secEvents(body) {
    const d = await api("events");
    await GW.catalog.load("status").catch(() => null);
    const running = h("div");
    for (const e of d.events) {
      const name = e.source.replace(/^gm-event:/, "");
      const srow = GW.catalog.loadedRow("status", e.status_id) || {};
      put(running, h("div", { class: "gw-w-ev" }, h("span", { class: "gw-w-evicon", "data-tip-type": "status", "data-tip-id": e.status_id }, GW.icon(srow.sprite, { name: srow.name })),
        h("div", null, h("b", null, name), h("div", { class: "gw-dim" }, "+" + pct(e.bonus_basis_points) + " EXP · from " + fmtT(e.activated_at) + " for " + Math.round((new Date(e.expires_at) - new Date(e.activated_at)) / 3600000) + " h of play · " + e.characters + " characters")),
        btn("End event", async () => {
          if (!await GW.confirm({ title: "End " + name + "?", message: "Removes the boost from every offline character now; online ones keep it until you end it again after they log out.", ok: "End it", danger: true })) return;
          try { const r = await api("events/exp/stop", { name, advanced: true }); GW.writeDone(r, "event " + name + " ended for " + r.applied.length); if (r.skipped.length) GW.alert("Still online (end it again later): " + r.skipped.map(x => x.name).join(", ")); secEvents(S.body); }
          catch (e2) { await GW.writeError(e2); }
        }, { small: true, danger: true })));
    }
    if (!d.events.length) put(running, note("No EXP event is running."));
    const f = { name: "", status_id: 511, bonus: 100, start: new Date(), hours: 48 };
    const nameIn = inp("", { label: "event name", max: 50, placeholder: "Double EXP weekend", oninput: v => { f.name = v; } });
    const startIn = inp(localInput(f.start), { type: "datetime-local", label: "start", oninput: v => { f.start = new Date(v); } });
    const endIn = inp(localInput(new Date(f.start.getTime() + f.hours * 3600000)), { type: "datetime-local", label: "end", oninput: v => { f.end = new Date(v); } });
    f.end = new Date(f.start.getTime() + f.hours * 3600000);
    const icons = h("span", { class: "gw-w-icons" });
    const drawIcons = () => { GW.clear(icons); for (const s of d.statuses) { const r = GW.catalog.loadedRow("status", s) || {}; put(icons, h("button", { class: "gw-w-iconbtn" + (f.status_id === s ? " on" : ""), type: "button", "data-tip-type": "status", "data-tip-id": s, "aria-label": r.name || ("status " + s), onclick: () => { f.status_id = s; drawIcons(); } }, GW.icon(r.sprite, { name: r.name }))); } };
    drawIcons();
    const bonusF = nf({ field: "character_experience_modifiers.bonus_basis_points", label: "Extra EXP", value: f.bonus, min: 1, max: 100000, unit: "%", onChange: (v, ok) => { if (ok) f.bonus = v; } });
    const tp = targetPicker();
    const tmpl = h("span", { class: "gw-w-line" }, d.templates.map(t => btn(t.name, () => {
      f.name = t.name; nameIn.value = t.name; f.status_id = t.status_id; drawIcons(); f.bonus = t.bonus_pct; bonusF.value = t.bonus_pct;
      let st = new Date();
      if (t.start === "next_saturday") { st = new Date(); st.setHours(0, 0, 0, 0); st.setDate(st.getDate() + ((6 - st.getDay() + 7) % 7 || 7)); }
      if (t.start === "today_18") { st = new Date(); st.setHours(18, 0, 0, 0); }
      f.start = st; f.end = new Date(st.getTime() + t.hours * 3600000); startIn.value = localInput(f.start); endIn.value = localInput(f.end);
    }, { small: true, help: "Fill the form with this example" })));
    put(GW.clear(body), 
      win("EXP events", h("div", { class: "gw-w-line" }, chipFor("next_login")), running),
      win("New EXP event",
        h("div", { class: "gw-w-line" }, lab("Examples"), tmpl),
        h("div", { class: "gw-w-grid" },
          lab("Name", "character_experience_modifiers.source", "Shown in the Change log; the same name again replaces the event"), nameIn,
          lab("Buff icon", "character_experience_modifiers.status_id", "The icon the client shows next to the character. Its tooltip text comes from the client, so it may name a different percentage."), icons,
          lab("Bonus", "character_experience_modifiers.bonus_basis_points"), bonusF,
          lab("Starts", "character_experience_modifiers.activated_at"), startIn,
          lab("Ends", "character_experience_modifiers.expires_at"), endIn),
        tp.el,
        note("How the game counts it: every EXP boost (potions too) lasts for the time between start and end ", h("b", null, "of play time"), " from the start, so a player who plays less keeps the rest. It adds to fighter EXP from kills and does not replace potions (it has its own slot). ",
          "Online characters are skipped (their client would keep the old buff icon until they relog): start the event again later and it reaches them too."),
        h("div", { class: "gw-w-line" }, chipFor("next_login"), btn("Start event", async () => {
          if (!f.name) { GW.alert("Give the event a name."); return; }
          if (!(f.end > f.start)) { GW.alert("The end must be after the start."); return; }
          if (!await confirmSave("Start " + f.name + "?", [["Bonus", "+" + f.bonus + " % EXP"], ["From", f.start.toLocaleString()], ["Lasts", Math.round((f.end - f.start) / 3600000) + " hours of play"], ["Who", tp.el.querySelector(".gw-dim:last-child").textContent]], "next_login")) return;
          try {
            const r = await api("events/exp/start", { name: f.name, status_id: f.status_id, bonus_pct: f.bonus, start: f.start.toISOString(), end: f.end.toISOString(), target: tp.t, advanced: true });
            GW.writeDone(r, "EXP event " + f.name + ": " + r.applied.length + " characters");
            if (r.skipped.length) GW.alert(r.applied.length + " characters have the event. Skipped because they are online: " + r.skipped.map(x => x.name).join(", ") + ". Start it again after they log out.", "EXP event started");
            secEvents(S.body);
          } catch (e) { await GW.writeError(e); }
        }))),
      win("Other ways to raise EXP for everyone",
        h("div", { class: "gw-kv" },
          h("span", { class: "gw-label", "data-help": "server_rates exp.* (hot-reloaded every 5 s)" }, "Server rates"), h("span", null, chipFor("hot_reload"), " ", d.rates.map(r => r.key + " x" + r.value).join(", ") || "not on this server", " ", h("a", { href: "#/rates" }, "edit on Server rates")),
          h("span", { class: "gw-label", "data-field": "monster_reward_settings.global_experience_multiplier_basis_points" }, "Global kill EXP"), h("span", null, chipFor("after_restart"), " x" + (d.reward_settings ? d.reward_settings.global_experience_multiplier_basis_points / 10000 : "?") + " (1x-5x), level gap " + (d.reward_settings ? d.reward_settings.maximum_lower_level_gap : "?"), " ", h("a", { href: "#/rates" }, "edit on Server rates"))),
        note("The event rows above add a buff icon; the rates change the numbers for everyone with no icon.")));
  }

  // ============================================================ event gifts
  async function secGifts(body) {
    const caps = (S.overview && S.overview.capabilities) || {};
    const g = { what: "item", item_id: null, quantity: 1, item_quality: null, item_grade: null, bound: null, silver: 0, gold: 0, bgold: 0 };
    const tp = targetPicker();
    const itemBox = h("span");
    const drawItem = () => put(GW.clear(itemBox), itemCell(g.item_id, null, async () => { const id = await GW.pick("item", { title: "Gift item (every item of the game)", value: g.item_id }); if (id) { g.item_id = Number(id); drawItem(); } }),
      g.item_id ? itemName(g.item_id) : h("span", { class: "gw-dim" }, " click the cell to pick the item"));
    drawItem();
    const form = h("div");
    const results = h("div");
    const drawForm = () => {
      put(GW.clear(form), h("div", { class: "gw-w-line" }, btn("An item", () => { g.what = "item"; drawForm(); }, { on: g.what === "item", small: true }), btn("Silver / Gold", () => { g.what = "money"; drawForm(); }, { on: g.what === "money", small: true })));
      if (g.what === "item") put(form, h("div", { class: "gw-w-line" }, itemBox),
        h("div", { class: "gw-w-grid" },
          lab("Quantity"), inp(g.quantity, { type: "number", min: 1, width: "80px", label: "quantity", oninput: v => { g.quantity = Number(v); } }),
          lab("Quality", "character_items.item_quality"), sel([[null, "item default"]].concat(Array.from({ length: 21 }, (_, k) => [k, "Q" + k + " " + (QN()[k] || "")])), g.item_quality, { onchange: v => { g.item_quality = v; } }),
          lab("Grade", "character_items.item_grade"), sel([[null, "item default"]].concat(Array.from({ length: 26 }, (_, k) => [k, "Grade " + k])), g.item_grade, { onchange: v => { g.item_grade = v; } }),
          lab("Bound", "character_items.bound"), sel([[null, "item default"], [1, "bound"], [0, "tradeable"]], g.bound, { onchange: v => { g.bound = v; } })));
      else put(form, h("div", { class: "gw-w-stats" },
        nf({ field: "character_base.Money", label: "Silver", value: g.silver, min: 0, max: 2147483647, onChange: (v, ok) => { if (ok) g.silver = v; } }),
        nf({ field: "character_base.Stone", label: "Gold", value: g.gold, min: 0, max: 2147483647, onChange: (v, ok) => { if (ok) g.gold = v; } }),
        nf({ field: "character_base.BindingGold", label: "B-Gold", value: g.bgold, min: 0, max: 2147483647, onChange: (v, ok) => { if (ok) g.bgold = v; } })),
        note("Added to what each character has (capped at 2,147,483,647)."));
    };
    drawForm();
    put(GW.clear(body), win("Event gifts",
      h("div", { class: "gw-online " + (caps.available ? "on" : "warn") }, caps.available
        ? h("span", null, h("b", { class: "gw-good" }, "Live gifts on: "), "players who are online get the gift at once through the game server (they see it in their bag / wallet); offline players get it at their next login.")
        : h("span", null, "Offline players get the gift at their next login. Online players are skipped: live gifts arrive with the round-2 server update (this server does not offer them).")),
      tp.el, form,
      h("div", { class: "gw-w-line" }, chipFor(caps.available ? "live" : "next_login"), btn("Send gift", async () => {
        if (g.what === "item" && !g.item_id) { GW.alert("Pick the item first."); return; }
        const what = g.what === "item" ? ((itemRow(g.item_id) || {}).name || "#" + g.item_id) + " x" + g.quantity : ["Silver " + GW.fmt(g.silver), "Gold " + GW.fmt(g.gold), "B-Gold " + GW.fmt(g.bgold)].join(", ");
        if (!await confirmSave("Send " + what + "?", [["To", tp.el.querySelector(".gw-dim:last-child").textContent], ["Gift", what]], caps.available ? "live" : "next_login",
          h("p", { class: "gw-dim" }, "Each character is one change in the Change log. Database gifts have Undo; a live bag gift has no automatic Undo (remove the item on the character's Inventory page)."))) return;
        put(GW.clear(results), note("sending ..."));
        try {
          const r = await api("events/gift", Object.assign({ target: tp.t }, g));
          GW.status("gift " + r.gift + ": " + r.live + " live, " + r.database + " by database, " + r.skipped + " skipped", r.skipped ? "" : "ok");
          GW.emit("change", null);
          put(GW.clear(results), sub("Result: " + r.gift, h("table", { class: "gw-w-table" }, h("tr", null, h("th", null, "Character"), h("th", null, "How"), h("th", null, "")),
            r.results.map(x => h("tr", null, h("td", null, h("a", { href: "#/character/" + x.id }, x.name)), h("td", null, x.ok ? chipFor(x.mode === "live" ? "live" : "next_login") : h("span", { class: "gw-chip never" }, "skipped")),
              h("td", { class: x.ok ? "gw-dim" : "tt-bad" }, x.ok ? (x.mode === "live" ? "the player sees it now" : "at the next login") : x.why))))));
        } catch (e) { GW.clear(results); await GW.writeError(e); }
      })), results));
  }

  // ============================================================ read-only views
  async function secBosses(body) {
    const d = await api("bosses");
    const t = h("table", { class: "gw-w-table" }, h("tr", null, ["Map", "Boss", "Area bonus", "Respawn", "Held by"].map((x, i) => h("th", { "data-field": [null, "gameplay_world_boss_definitions.template_key", "gameplay_world_boss_definitions.bonus_basis_points", "gameplay_world_boss_definitions.respawn_interval_seconds", "faction_area_experience_control.controlling_camp"][i] || null }, x))));
    for (const b of d.bosses) {
      const c = d.control.find(x => x.map_id === b.map_id && x.boss_template_key === b.template_key && new Date(x.expires_at) > new Date(d.now));
      put(t, h("tr", null, h("td", null, b.map_name), h("td", { "data-tip-type": "monster", "data-tip-id": b.template_key }, h("b", { style: "color:var(--gw-monster-textcolor,#f6b596)" }, b.display_name), h("div", { class: "gw-dim" }, b.template_key)),
        h("td", null, "+" + pct(b.bonus_basis_points) + " EXP"), h("td", null, (b.respawn_interval_seconds / 3600) + " h"),
        h("td", null, c ? h("span", null, h("span", { class: "gw-sprite " + (c.controlling_camp ? "gw-camp-athens" : "gw-camp-sparta") }), " " + CAMPS[c.controlling_camp] + " until " + fmtT(c.expires_at)) : h("span", { class: "gw-dim" }, "nobody"))));
    }
    put(GW.clear(body), win("World bosses", readOnly(d.why || "published gameplay content"), note("Kill a boss and the killer's camp gets the area EXP bonus (status 1504) on that map until the time shown."), h("div", { class: "gw-w-scroll" }, t),
      d.pending.length ? note("Waiting to be published: " + d.pending.map(p => p.map_name || p.map_id).join(", ")) : null));
  }

  async function secServer(body) {
    const d=await api("server");
    const cards=d.realms.map(r=>{
      const name=h("input",{class:"gw-w-input",value:r.name,maxlength:35,"aria-label":"Server name for realm "+r.id,disabled:!d.writable||null});
      const save=h("button",{class:"gw-btnx",type:"button",disabled:!d.writable||null,onclick:async()=>{
        save.disabled=true;
        try { const result=await api("server/name",{realm:r.id,name:name.value,expected:r.name}); GW.writeDone(result,"Server name saved; reconnect to refresh the server list"); await secServer(body); }
        catch(e){await GW.writeError(e);save.disabled=false;}
      }},"Save server name");
      return sub("Realm "+r.id+(r.enabled?"":" (disabled)"),name,save);
    });
    const operation=h("div",{class:"gw-dim",role:"status"});
    const controls=h("div",{class:"gw-w-line"});
    const out=h("pre",{class:"gw-w-logs",hidden:true,"aria-label":"Server output"});
    const playersLine=h("p",{class:"gw-w-players"});
    const showPlayers=(n,det)=>{playersLine.textContent=n+(n===1?" player is":" players are")+" signed in or connected now"+(det?" ("+det.accounts+" signed-in account"+(det.accounts===1?"":"s")+(det.connections===null?"":", "+det.connections+" open connection"+(det.connections===1?"":"s"))+")":"")+"."+(n>0?" Stopping or restarting will disconnect them.":"");playersLine.className="gw-w-players"+(n>0?" tt-bad":"");};
    showPlayers(d.players_online,d.players_detail);
    async function waitJob(label,job){
      while(job.state==="running"){
        await new Promise(resolve=>setTimeout(resolve,1500));
        job=await api("server/job/"+job.job);
        operation.textContent=label+": "+job.state+(job.error?" · "+job.error:"");
      }
      return job;
    }
    // Every action: fresh player count first, a confirm dialog that shows it, and an explicit tick when players are in the world.
    async function run(action,button,extra){
      let players;
      try{const st=await api("server/status");players=st.players_online;showPlayers(players,st.players_detail);}catch(e){await GW.writeError(e);return;}
      const disruptive=action==="stop"||action==="restart";
      let acknowledged=false;
      if(action!=="logs"){
        const tick=h("input",{type:"checkbox","aria-label":"I understand the players will be disconnected"});
        const msg={start:"Start the installed game service.",backup:"Make a database backup now (a fresh dump kept with the nightly ones). Players are not disconnected.",
          stop:"The game shutdown path saves each player's state, then the service stops until someone starts it.",restart:"The game shutdown path saves each player's state, the service starts again and the editor waits until the game is ready (up to 45 seconds)."}[action];
        const warn=disruptive&&players>0;
        const el=h("div",null,h("p",{class:warn?"tt-bad":""},h("b",null,players+(players===1?" player is":" players are")+" online now."),warn?" They will be disconnected. Prefer to wait until the world is empty (say so in chat first).":" Nobody is online."),h("p",null,msg),warn?h("label",null,tick," I understand the players will be disconnected"):null);
        if(!await GW.confirm({title:action[0].toUpperCase()+action.slice(1)+(action==="backup"?" the database?":" the game server?"),el,ok:action==="backup"?"Back up now":action[0].toUpperCase()+action.slice(1),danger:disruptive}))return;
        if(warn&&!tick.checked){GW.status("Tick the box to confirm the players will be disconnected.","err");return;}
        acknowledged=warn;
      }
      button.disabled=true;
      try{
        const result=await api("server/control",Object.assign({action,players_seen:players,acknowledge_players:acknowledged},extra||{}));
        const label="Game service "+action;operation.textContent=label+": "+result.job.state;
        const job=await waitJob(label,result.job);
        if(action==="logs"||action==="backup"){out.hidden=false;out.textContent=job.output||job.error||"(nothing)";}
        GW.status(label+": "+job.state,job.state==="failed"?"err":undefined);
        try{const st=await api("server/status");showPlayers(st.players_online,st.players_detail);}catch(e){}
      }catch(e){await GW.writeError(e);}
      finally{button.disabled=!d.control_available;}
    }
    for(const action of ["restart","start","stop"]){
      const button=h("button",{class:"gw-btnx"+(action==="stop"?" danger":""),type:"button",disabled:!d.control_available||null}, action[0].toUpperCase()+action.slice(1)+" game server");
      button.onclick=()=>run(action,button);controls.append(button);
    }
    const checkBtn=h("button",{class:"gw-btnx",type:"button",onclick:async()=>{
      try{const r=await api("server/status");showPlayers(r.players_online,r.players_detail);
        out.hidden=false;out.textContent="Game service: "+(r.status.service||"unknown")+" · readiness: "+r.status.state+"\n"+r.ports.map(p=>p.name+" port "+p.port+": "+(p.open?"open":"CLOSED")).join("\n")+"\nPlayers in the world: "+r.players_online;}
      catch(e){await GW.writeError(e);}
    }},"Check status");
    const lines=h("select",{"aria-label":"Log lines"},[50,100,200,300].map(n=>h("option",{value:String(n),selected:n===100||null},n+" lines")));
    const logsBtn=h("button",{class:"gw-btnx",type:"button",disabled:!d.control_available||null}, "Show log");
    logsBtn.onclick=()=>run("logs",logsBtn,{lines:Number(lines.value)});
    const backupBtn=h("button",{class:"gw-btnx",type:"button",disabled:!d.control_available||null}, "Back up database");
    backupBtn.onclick=()=>run("backup",backupBtn);
    const tools=h("div",{class:"gw-w-line"},checkBtn,logsBtn,lines,backupBtn);
    const history=h("div",null,(d.jobs||[]).map(j=>h("p",null,fmtT(j.started_at)+" · "+j.action+" · "+j.state+(j.error?" · "+j.error:""))));
    put(GW.clear(body),win("Server settings",note("Names appear on the next server-list login. Up to 35 printable ASCII characters; addresses, ports and realm IDs stay managed by server configuration."),note("Game service: "+d.status.state),h("div",{class:"gw-w-cards"},cards),sub("Game service controls",playersLine,controls,tools,operation,out,note("A restart also activates staged calendar and startup-content edits. The editor stays available while the game restarts. Passwords and keys are hidden from the log view; every use is recorded in the change history with who ran it and how many players were online. Releases still need zero players and a fresh backup."),history)));

  }

  async function secCalendar(body) {
    const d = await api("calendar");
    const cards = d.realms.map(r => {
      const choices = Array.from(new Set(["Australia/Brisbane", "Etc/UTC", r.time_zone].concat(d.zones || [])));
      const select = h("select", {class:"gw-w-input", "aria-label":"Time zone for " + r.name, disabled: !d.writable || null},
        choices.map(z => h("option", {value:z, selected:z === r.time_zone || null}, z + (z === "Australia/Brisbane" ? " · GMT+10, no daylight saving" : ""))));
      const save = h("button", {class:"gw-btnx", type:"button", disabled:!d.writable || null, onclick:async () => {
        if (select.value === r.time_zone) { GW.status("Time zone is unchanged"); return; }
        save.disabled = true;
        try {
          const result = await api("calendar", {realm:r.id, time_zone:select.value, expected_revision:r.time_zone_revision});
          GW.writeDone(result, "Calendar saved; coordinate a game restart to apply it");
          await secCalendar(body);
        } catch (e) { await GW.writeError(e); save.disabled = false; }
      }}, "Save for next restart");
      return sub(r.name + (r.enabled ? "" : " (off)"),
        h("div", {class:"gw-kv"},
          h("span", {class:"gw-label"}, "Configured time zone"), h("span", {class:"gw-value"}, r.time_zone + " (UTC" + (r.utc_offset[0] === "-" ? "" : "+") + r.utc_offset + ")"),
          h("span", {class:"gw-label"}, "Time in this calendar"), h("span", {class:"gw-value"}, r.local_dow + " " + r.local_now),
          h("span", {class:"gw-label"}, "Next reset in this calendar"), h("span", {class:"gw-value"}, fmtT(r.next_reset_utc) + " (your time)"),
          h("span", {class:"gw-label"}, "Calendar revision"), h("span", {class:"gw-value"}, r.time_zone_revision + " · " + fmtT(r.time_zone_updated_at) + " by " + r.time_zone_updated_by)),
        select, save);
    });
    put(GW.clear(body), win("Calendar", note(d.why),
      note("Australia/Brisbane is GMT+10 all year. Changing the calendar moves daily resets and weekend boundaries after restart. The VPS clock remains UTC. The times below describe the saved configuration; they do not confirm that a running worker has reloaded it."),
      h("div", {class:"gw-w-cards"}, cards),
      sub("Time zone history", h("table", {class:"gw-w-table"},
        h("tr", null, h("th", null, "When"), h("th", null, "Realm"), h("th", null, "Zone"), h("th", null, "By")),
        d.audit.map(a => h("tr", null, h("td", null, fmtT(a.changed_at)), h("td", null, a.realm_id), h("td", null, (a.previous_time_zone_id ? a.previous_time_zone_id + " → " : "") + a.time_zone_id), h("td", null, a.changed_by)))))));
  }

  async function secBalances(body) {
    const d = await api("balances");
    const c = d.crier;
    const tiers = h("table", { class: "gw-w-table" }, h("tr", null, ["Levels", "EXP", "Talent points", "3 rewards (Silver)", "All 6 (Silver)"].map(x => h("th", null, x))),
      (c.tiers || []).map(t => h("tr", null, h("td", null, t.minimum_level + "-" + t.maximum_level), h("td", null, GW.fmt(t.base_experience)), h("td", null, t.base_talent_points), h("td", null, GW.fmt(t.triple_silver_cost)), h("td", null, GW.fmt(t.all_six_silver_cost)))));
    const opts = h("table", { class: "gw-w-table" }, h("tr", null, ["Choice", "Pays with", "Cost", "Multiplier", "Gives"].map(x => h("th", null, x))),
      (c.options || []).map(o => h("tr", null, h("td", null, o.sub_id), h("td", null, h("span", { class: "gw-sprite " + (o.currency_code === "silver" ? "gw-coin-silver" : "gw-coin-gold") }), " " + o.currency_code.replace("_", "-")), h("td", null, GW.fmt(o.cost)), h("td", null, "x" + o.multiplier), h("td", null, o.reward_kind.replace("_", " ")))));
    const aw = h("div", { class: "gw-w-slots" }, (d.award.entries || []).map(e => h("div", { class: "gw-w-slot" }, itemCell(e.item_id, { item_quality: e.item_quality, bound: e.bound }, null, { count: e.quantity }),
      h("div", { class: "gw-dim" }, "#" + e.reward_order + " · x" + e.quantity))));
    put(GW.clear(body), win("Faction Crier", readOnly(d.why), note("Balance revision " + c.revision + (c.meta ? " · from level " + c.meta.minimum_level + " · weekly reclaim " + c.meta.weekly_reclaim_gold_cost + " Gold · renewal " + c.meta.renewal_gold_cost + " Gold" : "")),
      sub("Rewards by level", tiers), sub("Payment choices", opts)),
      win("Online Award", h("div", { class: "gw-w-line" }, h("a", { class: "gw-btnx", href: "#/awards" }, "Edit the Online Award"), " ", GW.appliesChip("after_restart")), note("Publication " + (d.award.publication ? d.award.publication.revision + " (" + d.award.publication.updated_by + ")" : "-") + ". Items in the order the server hands them out."), aw));
  }

  async function secShops(body) {
    const d = await api("shops");
    const list = h("div", { class: "gw-w-list" });
    const right = h("div", { class: "gw-w-right" });
    const q = inp("", { type: "search", label: "Search shop items", placeholder: "name, ID or description ...", oninput: () => open(KEEP.shop) });
    function open(cat) {
      KEEP.shop = cat;
      for (const b of list.children) b.classList.toggle("on", b.dataset.k === cat.name);
      const words = q.value.trim().toLowerCase().split(/\s+/).filter(Boolean);
      const ents = cat.entries.filter(e => {
        const row = itemRow(e.item) || {};
        const text = [e.name || "", e.item, row.name || "", row.search_text || ""].join(" ").toLowerCase();
        return words.every(word => text.includes(word));
      });
      put(GW.clear(right), win(cat.label, h("div", { class: "gw-w-line" }, h("a", { class: "gw-btnx", href: "#/npcs?shop=" + encodeURIComponent(cat.name) }, "Edit this shop"), " ", GW.appliesChip("hot_reload"),
        h("span", { class: "gw-dim" }, " Items, prices and slot order are edited on the NPCs page; the list below is the shop as compiled into the server (an edited shop replaces it).")), note(ents.length + " of " + cat.entries.length + " items · pays with " + cat.currency),
        h("div", { class: "gw-w-mall" }, ents.map(e => h("div", { class: "gw-w-mallcell" + (e.in_client === false ? " noclient" : "") }, itemCell(e.item, null, null),
          h("div", { class: "gw-w-mallname" }, e.name || ("#" + e.item)),
          h("div", { class: "gw-w-price" }, h("span", { class: "gw-sprite " + (/gold/.test(e.currency || cat.currency) ? "gw-coin-gold" : "gw-coin-silver") }), " " + GW.fmt(e.price) + (/(bind|binding)/.test(e.currency || "") ? " B-Gold" : /gold/.test(e.currency || cat.currency) ? " Gold" : /silver/.test(e.currency || cat.currency) ? "" : " " + (e.currency || cat.currency))))))));
    }
    for (const c of d.catalogues) put(list, h("button", { class: "gw-w-row", type: "button", "data-k": c.name, onclick: () => open(c) }, h("span", { class: "nm" }, c.label), h("span", { class: "sub" }, c.entries.length + " items · " + c.currency)));
    put(GW.clear(body), h("div", { class: "gw-w-split" }, win("Shops", h("div", { class: "gw-w-line" }, q), note(d.catalogues.length + " server shops; the client lists " + d.client_items + " shop items."), list), right));
    open(KEEP.shop && d.catalogues.find(c => c.name === KEEP.shop.name) || d.catalogues[0]);
  }
})();
