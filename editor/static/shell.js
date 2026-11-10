/* shell.js - the reborn editor shell: top bar (database, read-only / LIVE, game server, live-edit capabilities,
   character), the sidebar of game systems (one per module, MODULES.md), Home, the Library,
   and the router that loads static/modules/<name>.js on demand.
   Routes: #/home, #/library[/<type>], #/<module>[/<character id>][/more...]. Old links: #rates, #reborn=<id>. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h, $ = GW.$;
  const S = GW.state;
  let current = null;            // {name, def}
  let modsInfo = {};             // name -> describe() from /api/m
  const LIB_TYPES = [["item", "Items"], ["skill", "Skills"], ["talent", "Talents"], ["petskill", "Pet skills"], ["pet", "Pets"],
    ["status", "Buffs & statuses"], ["appendattr", "Append attributes"], ["holystone", "Holy stones"], ["suit", "Holy suits"],
    ["title", "Titles"], ["quest", "Quests"], ["monster", "Monsters"], ["npc", "NPCs"], ["map", "Maps"], ["zodiac_kind", "Zodiac skills"],
    ["zodiac_sign", "Zodiac signs"], ["ride", "Mount rides"], ["guild_building", "Guild buildings"], ["altar", "Guild altars"],
    ["quality", "Qualities"], ["grade", "Grades"], ["aptitude", "Pet aptitudes"], ["currency", "Currencies"], ["stattype", "Stat types"]];

  // ------------------------------------------------------------ top bar
  function badge(id, text, cls, help) {
    const el = document.getElementById(id);
    el.textContent = text; el.className = "gw-badge " + (cls || ""); el.dataset.help = help || "";
  }
  async function loadStatus() {
    let st;
    try { st = await GW.api("/api/m/status"); }
    catch (e) { badge("gw-db", "editor not answering", "bad", e.message); return; }
    S.status = st;
    const rb = st.reborn || {};
    badge("gw-db", "DB " + (rb.database || "?"), rb.available ? "" : "bad",
      "PostgreSQL database this editor reads and writes: " + (rb.database || "?") + (rb.available ? " (" + GW.fmt(rb.characters) + " characters, " + GW.fmt(rb.templates) + " item templates, PostgreSQL " + rb.version + ")." : ". " + (rb.note || "")) +
      "\nThe live game uses 'godswar'; any other name is a copy.");
    if (!rb.available) badge("gw-mode", "NOT CONNECTED", "bad", rb.note || "");
    else if (rb.live && rb.writable) badge("gw-mode", "LIVE - WRITES ON", "bad", "Every save changes the real server's database. A backup was taken when the editor started. The character you edit must be logged out.");
    else if (rb.writable) badge("gw-mode", "COPY - writable", "ok", "This is a copy of the database: saving is allowed and does not touch the live game.");
    else badge("gw-mode", "READ-ONLY", "dim", "You can look at everything; saving is switched off. " + (rb.note || "") + "\nTo edit the live server, open the editor with OPEN-EDITOR.command and choose Edit.");
    const lb = $("#gw-livebanner");
    if (rb.live && rb.writable) { lb.textContent = "LIVE DATABASE " + rb.database + " - saving changes the real game. The character you edit must be logged out."; lb.classList.remove("hidden"); }
    else lb.classList.add("hidden");
    const sv = st.server || {};
    const svText = { ready: "Server: running", "not ready": "Server: starting / not ready", down: "Server: not answering", "no port": "Server: unknown", unknown: "Server: unknown" }[sv.state] || "Server: " + sv.state;
    badge("gw-server", svText + (sv.service && sv.service !== "active" ? " (" + sv.service + ")" : ""), sv.state === "ready" ? "ok" : sv.state === "no port" ? "dim" : "warn",
      "The game server that uses this database" + (sv.port ? " (management port 127.0.0.1:" + sv.port + ")" : "") + ": " +
      (typeof sv.detail === "string" ? sv.detail : JSON.stringify(sv.detail || "")).slice(0, 300) +
      "\nThe editor works while the server runs; only the character you edit must be logged out.");
    const caps = st.capabilities || {};
    const ops = st.live_operations || [];
    if (caps.available) badge("gw-caps", "Live edits: " + ops.length, "ok", "The game server can apply these edits to a logged-in character right away:\n" + ops.join(", ") +
      "\nEverything else still needs the character logged out.");
    else badge("gw-caps", "Live edits: none", "dim", "The game server does not offer live edits yet" + (caps.error ? " (" + caps.error + ")" : "") +
      ".\nSo: log the character out (back to the login screen) before you edit it. Other players can keep playing.");
  }

  // ------------------------------------------------------------ character chooser
  async function chooseCharacter() {
    let rows;
    try { rows = await GW.api("/api/m/characters"); } catch (e) { GW.alert(e.message); return; }
    const q = h("input", { class: "gw-pk-search", type: "search", placeholder: "search name, account or id", "aria-label": "Search characters" });
    const list = h("div", { class: "gw-cp-list", role: "listbox", "aria-label": "Characters" });
    const draw = () => {
      const t = q.value.toLowerCase();
      GW.clear(list);
      let acct = null;
      for (const r of rows) {
        if (t && !(r.name + " " + r.username + " " + r.id).toLowerCase().includes(t)) continue;
        if (r.username !== acct) { acct = r.username; list.append(h("div", { class: "gw-cp-acct" }, "account " + r.username)); }
        list.append(h("button", { class: "gw-cp-row" + (String(r.id) === String(S.cid) ? " on" : ""), type: "button", role: "option",
          onclick: () => { m.close(); selectCharacter(r.id, r); } },
          h("span", { class: "gw-dot " + (r.online ? "on" : "off"), "data-help": r.online ? "online" : "offline" }),
          h("span", { class: "nm" }, r.name), h("span", { class: "sub" }, "Lv " + r.level + " " + r.class + " · " + r.camp_name + " · " + r.map_name + " · #" + r.id)));
      }
      if (!list.children.length) list.append(h("div", { class: "gw-dim" }, "No character matches."));
    };
    q.addEventListener("input", draw);
    q.addEventListener("keydown", e => { if (e.key === "Enter") { const b = list.querySelector(".gw-cp-row"); if (b) b.click(); } });
    draw();
    const m = GW.modal(h("div", null, q, list), { title: "Choose a character" });
  }
  function drawCharButton() {
    const host = $("#gw-charpick");
    GW.clear(host);
    const c = S.character;
    host.append(h("button", { class: "gw-cp-btn", type: "button", onclick: chooseCharacter, "data-help": "The character the character pages (Character, Inventory, Pets, Skills) edit. Click to choose another." },
      c ? h("span", { class: "gw-dot " + (c.online ? "on" : "off") }) : null,
      h("b", null, c ? c.name : "Choose a character"),
      c ? h("span", { class: "gw-cp-sub" }, "Lv " + c.level + " " + c.class + " #" + c.id) : null));
  }
  async function selectCharacter(cid, row) {
    S.cid = cid === null || cid === undefined ? null : Number(cid);
    if (S.cid !== null && !row) {
      try { const rows = await GW.api("/api/m/characters?q=" + encodeURIComponent(String(S.cid))); row = rows.find(r => r.id === S.cid) || null; } catch (e) { row = null; }
    }
    S.character = row || null;
    try { if (S.cid !== null) localStorage.setItem("gwedit.cid", String(S.cid)); } catch (e) { /* storage blocked: nothing lost but the memory */ }
    drawCharButton();
    GW.emit("character", S.character);
    const r = parseRoute();
    const info = modsInfo[r.page];
    if (info && info.needs_character) location.hash = "#/" + r.page + "/" + S.cid;
  }
  GW.selectCharacter = selectCharacter;
  // the online banner of a page re-checks every 15 s: keep the top-bar dot in step with it
  GW.on("online", e => {
    if (S.character && e && e.cid === S.cid && e.state && S.character.online !== !!e.state.online) { S.character.online = !!e.state.online; drawCharButton(); }
  });

  // ------------------------------------------------------------ sidebar
  function navLink(href, icon, title, sub, subCls, key) {
    return h("a", { class: "gw-nav", href, "data-key": key, "data-help": sub || null },
      icon, h("span", { class: "gw-nav-t" }, title, sub ? h("span", { class: "gw-nav-s " + (subCls || "") }, sub) : null));
  }
  function iconFor(info) {
    const i = info.icon || {};
    if (i.sprite !== undefined) return GW.icon(i.sprite);
    if (i.ui) return h("span", { class: "gw-sprite gw-" + i.ui });
    return GW.icon(null, { name: info.title });
  }
  async function loadModules() {
    let r;
    try { r = await GW.api("/api/m"); } catch (e) { GW.status("module list: " + e.message, "err"); r = { modules: [], load_errors: {} }; }
    S.modules = r.modules;
    modsInfo = {};
    for (const m of r.modules) modsInfo[m.name] = m;
    const side = $("#gw-side");
    GW.clear(side);
    side.append(navLink("#/home", h("span", { class: "gw-sprite gw-camp-athens", style: "transform:scale(.8)" }), "Home", "what you can edit", "", "home"));
    side.append(h("div", { class: "gw-side-h" }, "Game systems"));
    for (const m of r.modules) side.append(navLink("#/" + m.name, iconFor(m), m.title, m.status === "ready" ? "" : m.status, m.status === "ready" ? "" : "coming", m.name));
    for (const [name, err] of Object.entries(r.load_errors || {})) side.append(navLink("#/" + name, GW.icon(null, { name }), name, "failed to load: " + err, "error", name));
    side.append(h("div", { class: "gw-side-h" }, "Library"));
    side.append(navLink("#/library", GW.icon(604), "All items, skills ...", "every entry from your client", "", "library"));
    side.append(h("a", { class: "gw-nav gw-nav-small", href: "#/kit", "data-key": "kit", "data-help": "The shared editor parts every page uses, on one page (nothing here saves)." }, "Editor parts (demo)"));
    side.append(h("a", { class: "gw-nav gw-nav-small", href: "/api/reborn/selftest?format=text", target: "_blank", rel: "noopener", "data-help": "Reads every character and lists what the database does not enforce. Writes nothing." }, "Self-test (text)"));
  }
  function markNav(page) { for (const a of GW.$$(".gw-nav")) a.classList.toggle("on", a.dataset.key === page); }

  // ------------------------------------------------------------ router
  function parseRoute() {
    let hs = location.hash || "";
    if (/^#rates/.test(hs)) hs = "#/rates";
    const legacy = hs.match(/^#reborn=(\d+)/);
    if (legacy) hs = "#/character/" + legacy[1];
    const parts = hs.replace(/^#\/?/, "").split("?")[0].split("/").filter(Boolean).map(decodeURIComponent);
    const params = new URLSearchParams(hs.split("?")[1] || "");
    return { page: parts[0] || "home", rest: parts.slice(1), params };
  }
  function loadScript(src) {
    return new Promise((res, rej) => { const s = h("script", { src }); s.onload = res; s.onerror = () => rej(new Error("cannot load " + src)); document.body.append(s); });
  }
  function loadCss(href) { if (!document.querySelector(`link[href="${href}"]`)) document.head.append(h("link", { rel: "stylesheet", href })); }
  async function ensureModule(name) {
    if (GW.modules[name]) return GW.modules[name];
    const info = modsInfo[name];
    if (!info) throw new Error("no editor called " + name);
    if (info.css) loadCss(info.css + "?v=" + encodeURIComponent(S.status ? S.status.editor.started : ""));
    if (!info.js) throw new Error("the " + info.title + " editor has no page yet (static/modules/" + name + ".js)");
    await loadScript(info.js + "?v=" + encodeURIComponent(S.status ? S.status.editor.started : ""));
    if (!GW.modules[name]) throw new Error("static/modules/" + name + ".js did not call GW.module(\"" + name + "\", ...)");
    return GW.modules[name];
  }
  function pageHead(info, extra) {
    return h("div", { class: "gw-pagehead" }, h("h1", null, info.title),
      info.status !== "ready" ? h("span", { class: "gw-chip restart" }, info.status) : null,
      h("span", { class: "gw-dim" }, info.description || ""), extra || null);
  }
  async function route() {
    const r = parseRoute();
    const page = $("#gw-page");
    if (current && current.def && current.def.unmount) { try { current.def.unmount(); } catch (e) { console.error(e); } }
    current = null;
    GW.hideTip && GW.hideTip();
    GW.clear(page);
    markNav(r.page);
    page.scrollTop = 0;
    if (r.page === "home") return home(page);
    if (r.page === "library") return library(page, r.rest[0] || "item");
    if (r.page === "kit") return kit(page);
    const info = modsInfo[r.page];
    if (!info) { page.append(h("div", { class: "gw-dim" }, "There is no page '" + r.page + "'. ", h("a", { href: "#/home" }, "Home"))); return; }
    // character pages: the character comes from the route, else the one chosen last
    let cid = null;
    if (info.needs_character) {
      cid = r.rest[0] && /^\d+$/.test(r.rest[0]) ? Number(r.rest[0]) : S.cid;
      if (cid !== null && cid !== S.cid) await selectCharacter(cid);
      if (cid === null) { page.append(pageHead(info), charPrompt(info)); return; }
      if (!r.rest[0]) { history.replaceState(null, "", "#/" + r.page + "/" + cid); }
    }
    page.append(pageHead(info));
    const body = h("div", { class: "gw-modpage gw-mod-" + r.page });
    page.append(body);
    try {
      const def = await ensureModule(r.page);
      current = { name: r.page, def };
      const ctx = { name: r.page, info, cid, character: S.character, route: info.needs_character ? r.rest.slice(1) : r.rest, params: r.params,
        api: (sub, b) => GW.api("/api/m/" + r.page + "/" + sub, b), navigate: sub => { location.hash = "#/" + r.page + (cid !== null ? "/" + cid : "") + (sub ? "/" + sub : ""); } };
      await def.mount(body, ctx);
    } catch (e) {
      body.append(h("div", { class: "tt-bad" }, e.message));
      console.error(e);
    }
  }
  function charPrompt(info) {
    const box = h("section", { class: "gw-frame-normalwindow gw-win", style: "max-width:760px" }, h("div", { class: "gw-wtitle" }, "Choose a character"),
      h("p", null, "The " + info.title + " page edits one character. Pick it here (or with the button in the top bar)."));
    const grid = h("div", { class: "gw-charlist" });
    box.append(grid);
    GW.api("/api/m/characters").then(rows => {
      for (const r of rows) grid.append(h("button", { class: "gw-cp-row", type: "button", onclick: () => selectCharacter(r.id, r) },
        h("span", { class: "gw-dot " + (r.online ? "on" : "off") }), h("span", { class: "nm" }, r.name), h("span", { class: "sub" }, "Lv " + r.level + " " + r.class + " · " + r.username)));
      if (!rows.length) grid.append(h("div", { class: "gw-dim" }, "This database has no characters."));
    }).catch(e => grid.append(h("div", { class: "tt-bad" }, e.message)));
    return box;
  }

  // ------------------------------------------------------------ Home
  function home(page) {
    page.append(h("div", { class: "gw-pagehead" }, h("h1", null, "GodsWar Editor"), h("span", { class: "gw-dim" }, "the reborn (2.51) server, seen through your Godswar Origin client")));
    const sys = h("div", { class: "gw-sys" });
    for (const m of S.modules) sys.append(h("a", { class: "gw-sys-tile", href: "#/" + m.name },
      GW.cell({ sprite: m.icon && m.icon.sprite, name: m.title }), h("b", null, m.title, m.status !== "ready" ? h("span", { class: "gw-chip restart", style: "margin-left:6px" }, m.status) : null),
      h("span", { class: "d" }, m.description)));
    const st = S.status || {}, rb = st.reborn || {}, caps = st.capabilities || {};
    const kv = (k, v, help) => [h("span", { class: "gw-label", "data-help": help || null }, k), h("span", { class: "gw-value" }, v)];
    const facts = h("div", { class: "gw-kv" },
      kv("Database", (rb.database || "?") + (rb.live ? " (the live game)" : " (a copy)"), "Which PostgreSQL database the editor uses."),
      kv("Saving", rb.writable ? (rb.live ? "ON - live" : "ON - copy") : "off (read-only)", "Read-only mode shows everything but saves nothing."),
      kv("Characters", GW.fmt(rb.characters)), kv("Pets", GW.fmt(rb.pets)), kv("Owned items", GW.fmt(rb.items)), kv("Item templates", GW.fmt(rb.templates)),
      kv("Game server", (st.server || {}).state || "?", "Answer of the game server's management port."),
      kv("Live edits", caps.available ? (st.live_operations || []).join(", ") : "none yet", "Edits the game server applies to a logged-in character itself (round2)."));
    page.append(h("div", { class: "gw-grid2" },
      h("section", { class: "gw-frame-normalwindow gw-win" }, h("div", { class: "gw-wtitle" }, "Game systems"), sys),
      h("div", null,
        h("section", { class: "gw-frame-normalwindow gw-win" }, h("div", { class: "gw-wtitle" }, "This editor"), facts),
        h("section", { class: "gw-frame-normalwindow gw-win gw-explain" }, h("div", { class: "gw-wtitle" }, "Editing while the game is live"),
          h("p", null, "Yes: the game server keeps running and other players keep playing while you edit."),
          h("p", null, h("b", null, "The character you edit must be logged out"), " (at the login screen or with the game closed, not at character select). It sees the change the next time it enters the world."),
          h("p", null, "Why: the server keeps an online character in memory. Silver, items, level and pets are re-read for every action, but your game window is never told, so it shows old values; quests, position and HP/MP are saved from memory and an edit to them would be lost."),
          h("p", null, caps.available ? "This server also accepts some edits for a logged-in character directly: " + (st.live_operations || []).join(", ") + "." :
            "When the server offers live edits (round2), the badge 'Live edits' in the top bar shows them and those fields work for online characters too."),
          h("p", null, "Every field explains itself: hover a label to see what it is, its range, where it is stored and when the game picks it up."),
          h("p", null, "Every save takes a backup first and appears in the Change log with Undo.")),
        h("section", { class: "gw-frame-normalwindow gw-win" }, h("div", { class: "gw-wtitle" }, "Also here"),
          h("p", null, h("a", { href: "#/library" }, "Library"), ": every item, skill, pet skill, buff, title, quest, monster ... from your client, with icons and the game's tooltips.")),
        pendingBox())));
  }
  function pendingBox() {
    const box = h("section", { class: "gw-frame-normalwindow gw-win hidden" }, h("div", { class: "gw-wtitle" }, "Waiting for a server restart"));
    GW.api("/api/m/pending").then(r => {
      if (!r.pending.length) return;
      box.classList.remove("hidden");
      box.append(h("p", null, "These saved changes take effect after ", h("code", null, "gw251 restart"), ":"),
        h("ul", null, r.pending.map(c => h("li", null, c.time + " · " + (c.module || "") + " · " + c.action))));
    }).catch(() => { /* optional */ });
    return box;
  }

  // ------------------------------------------------------------ Library
  function library(page, type) {
    page.append(h("div", { class: "gw-pagehead" }, h("h1", null, "Library"), h("span", { class: "gw-dim" }, "every entry of every kind, read from your Godswar Origin client; hover for the game's tooltip, click for details")));
    const types = h("div", { class: "gw-lib-types", role: "tablist" });
    const detail = h("section", { class: "gw-lib-detail gw-frame-normalwindow gw-win" }, h("div", { class: "gw-wtitle" }, "Details"), h("div", { class: "gw-dim" }, "Click an entry."));
    const grid = GW.grid({ type, height: "100%", onSelect: async r => {
      GW.clear(detail).append(h("div", { class: "gw-wtitle" }, r.name || "Details"));
      try { detail.append(await GW.render.tooltip(cur, r.id)); } catch (e) { detail.append(GW.render.generic(cur, r)); }
      detail.append(h("div", { class: "gw-dim", style: "margin-top:8px" }, "id " + r.id + (r.key && r.key !== String(r.id) ? " · " + r.key : "")));
    } });
    let cur = type;
    for (const [t, label] of LIB_TYPES) {
      const b = h("button", { class: "gw-tabx" + (t === type ? " on" : ""), type: "button", role: "tab", "aria-selected": t === type ? "true" : "false" }, label);
      b.addEventListener("click", () => { cur = t; history.replaceState(null, "", "#/library/" + t); for (const x of types.children) { x.classList.toggle("on", x === b); x.setAttribute("aria-selected", x === b ? "true" : "false"); } grid.setType(t); });
      types.append(b);
    }
    page.append(h("div", { class: "gw-lib" }, h("div", { class: "gw-lib-main" }, types, grid.el), detail));
  }

  // ------------------------------------------------------------ Editor parts (the shared components, for module authors; saves nothing)
  function kit(page) {
    page.append(h("div", { class: "gw-pagehead" }, h("h1", null, "Editor parts"), h("span", { class: "gw-dim" }, "the shared components of static/shared/ (MODULES.md); nothing on this page saves")));
    const win = (title, ...kids) => h("section", { class: "gw-frame-normalwindow gw-win" }, h("div", { class: "gw-wtitle" }, title), ...kids);
    const bar = GW.saveBar({ title: "Demo: save these?", onSave: async items => ({ change: { action: "demo (nothing saved): " + items.map(i => i.label).join(", ") }, applies_text: "nothing was written" }) });
    const money = GW.numberField({ field: "character_base.Money", value: 10000, onChange: (v, ok) => ok ? bar.set("Money", { label: "Silver", from: 10000, to: v, field: "character_base.Money", applies: "next_login" }) : bar.unset("Money") });
    const lv = GW.numberField({ field: "character_base.fighter_job_lv", value: 1, onChange: (v, ok) => ok ? bar.set("lv", { label: "Level", from: 1, to: v, field: "character_base.fighter_job_lv", applies: "next_login" }) : bar.unset("lv") });
    const store = GW.numberField({ field: "character_base.store_num", value: 10 });
    const rate = GW.numberField({ label: "EXP multiplier (example)", value: 1, min: 0, max: 1000000, decimals: 2, unit: "x" });
    const picked = h("span", { class: "gw-value" });
    const ids = h("div", { class: "gw-kv" },
      h("span", { class: "gw-label" }, "Item"), GW.idField("item", 1000, { title: "Pick an item", onChange: (v, r) => { picked.textContent = "picked " + (r ? r.name : "none"); } }),
      h("span", { class: "gw-label" }, "Weapon only"), GW.idField("item", null, { title: "Pick a weapon", group: "Weapon", filter: r => r.key === "weapon" }),
      h("span", { class: "gw-label" }, "Skill"), GW.idField("skill", 10), h("span", { class: "gw-label" }, "Pet skill"), GW.idField("petskill", 400),
      h("span", { class: "gw-label" }, "Buff"), GW.idField("status", 10), h("span", { class: "gw-label" }, "Holy stone"), GW.idField("holystone", 1),
      h("span", { class: "gw-label" }, "Quest"), GW.idField("quest", 0), h("span", { class: "gw-label" }, "Monster"), GW.idField("monster", "A_Boss_gensbd_002"));
    const inst = { item_quality: 7, item_grade: 9, bound: 1, attrs: [{ id: 0, level: 5 }, { id: 130, level: 3 }], holy_suit_code: 403, holy_socket_count: 3, sockets: [{ effect: 1, level: 2 }] };
    const cells = h("div", { style: "display:flex;gap:6px;flex-wrap:wrap" },
      [1, 3, 5, 7, 9, 10, 12, 20].map(q => GW.cell({ sprite: 107, name: "Short Sword", quality: q, corner: "Q" + q, tipType: "item", tipId: 1000, tipInst: Object.assign({}, inst, { item_quality: q }) })),
      GW.cell({ sprite: 604, count: 99, tipType: "item", tipId: 4002, tipInst: { item_quality: 0, stack: 99, bound: 0 } }), GW.cell({ empty: true, help: "an empty cell" }), GW.cell({ sprite: 248, locked: true, help: "a locked cell" }));
    const helpers = h("div", { class: "gw-kv" }, ["Money", "SkillPoint", "checkpoint_owner_id", "wallet_revision", "store_num", "pet_shed_capacity", "zodiac_energy"].map(c =>
      [h("span", { class: "gw-label", "data-field": "character_base." + c, tabindex: 0 }, c), h("span", { class: "gw-dim" }, "hover the label")]));
    page.append(h("div", { class: "gw-grid2" },
      win("Number fields + save bar", h("div", { style: "display:flex;flex-direction:column;gap:8px" }, money, lv, store, rate), bar.el),
      win("ID fields (every entry, big picker)", ids, picked, h("p", null, h("button", { class: "gw-btnx", type: "button", onclick: async () => { const v = await GW.pick("item", { title: "All items" }); GW.status("picker returned " + v); } }, "Open the item picker"))),
      win("Cells + game tooltips", cells, h("p", { class: "gw-dim" }, "Hover a cell: the item tooltip in the game's order, with this item at Q1-Q20, grade 9, two append attributes, a Platinum holy suit and holy stones.")),
      win("Field help (glossary)", helpers, h("p", null, "Applies chips: ", GW.appliesChip("now"), " ", GW.appliesChip("next_login"), " ", GW.appliesChip("after_restart"), " ", GW.appliesChip("never"))),
      win("Online banner", S.cid !== null ? GW.onlineBanner(S.cid) : h("p", { class: "gw-dim" }, "Choose a character in the top bar to see its banner."))));
  }

  // ------------------------------------------------------------ boot
  async function boot() {
    let z = "1";
    try { z = localStorage.getItem("gwedit.zoom") || "1"; } catch (e) { /* storage blocked */ }
    const zs = $("#gw-zoom");
    zs.value = z; $("#gw-page").style.zoom = z;
    zs.addEventListener("change", () => { $("#gw-page").style.zoom = zs.value; try { localStorage.setItem("gwedit.zoom", zs.value); } catch (e) { /* blocked */ } });
    $("#gw-chg").addEventListener("click", () => GW.changes.toggle());
    GW.on("change", () => { GW.changes.refresh(); loadStatus(); });
    drawCharButton();
    await Promise.all([loadStatus(), loadModules()]);
    let saved = null;
    try { saved = localStorage.getItem("gwedit.cid"); } catch (e) { /* storage blocked */ }
    const r = parseRoute();
    const inRoute = modsInfo[r.page] && modsInfo[r.page].needs_character && /^\d+$/.test(r.rest[0] || "");
    if (!inRoute && saved && /^\d+$/.test(saved)) {
      await selectCharacter(Number(saved)).catch(() => {});
      if (!S.character) { S.cid = null; drawCharButton(); }      // remembered character not in this database
    }
    window.addEventListener("hashchange", route);
    await route();
    GW.changes.refresh();
    setInterval(loadStatus, 30000);
  }
  boot();
})();

// Header link shows whether agents are working (static/agent-status.json, published every minute from the Mac).
(function () {
  async function pill() {
    const el = document.getElementById("gw-agent-pill");
    if (!el) return;
    try {
      const a = await (await fetch("/static/agent-status.json", { cache: "no-store" })).json();
      const stale = (Date.now() - new Date(a.observed_at)) / 1000 > 240;
      const label = stale ? "Mac not reporting" : { working: "Agents working", quiet: "Agent on a long job", usage_limit: "Paused: usage limit", idle: "No agent active" }[a.state] || a.state;
      el.textContent = (a.state === "working" && !stale ? "\u25CF " : "") + label;
    } catch (e) { el.textContent = "Development progress"; }
  }
  pill(); setInterval(pill, 30000);
})();
