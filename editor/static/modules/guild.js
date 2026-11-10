/* static/modules/guild.js - the "Guild" page: every guild, the guild window (Info / Members / Buildings / Altars /
   Boost) as the game draws it, faction areas, what the server does not implement, and consistency checks.
   Backend: modules/guild.py. Contract: MODULES.md. Database and client text is inserted as text, never as HTML. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const CAMP = { 0: "Sparta", 1: "Athens" };
  const STATE = { implemented: ["now", "works"], partial: ["login", "partly"], missing: ["restart", "missing"], client: ["never", "client side"] };
  const fmt = GW.fmt;
  const when = t => { if (!t) return "never"; const d = new Date(t); return isNaN(d) ? String(t) : d.toLocaleString(); };
  const campIcon = c => h("span", { class: "gw-sprite " + (c === 1 ? "gw-camp-athens" : "gw-camp-sparta") + " gwg-camp", "data-help": "Camp: " + (CAMP[c] || "?") + " (the leader's)" });

  // ------------------------------------------------------------------------------------------------ write helper
  // every guild write: confirm (optional) -> POST -> status line + Undo hint; 423 -> who is online (+ Kick); others: shared dialogs
  async function write(mod, sub, body, label, opts) {
    const o = opts || {};
    if (o.confirm && !await GW.confirm(o.confirm)) return null;
    GW.status("saving ...");
    try {
      const r = await mod.ctx.api(sub, body);
      GW.writeDone(r, label);
      if (r && r.change) mod.lastChange = r.change;
      return r;
    } catch (e) {
      if (e.status === 423) { await onlineDialog(mod, e, () => write(mod, sub, body, label, Object.assign({}, o, { confirm: null }))); return null; }
      const kind = await GW.writeError(e);
      if (kind === "conflict" && o.reload) o.reload();
      return null;
    }
  }

  // 423: the members who are online, with Kick (round-2 server) and "Try again"
  function onlineDialog(mod, e, retry) {
    const st = (e.data && e.data.online) || {};
    const list = h("div", { class: "gwg-onl" });
    const m = GW.modal(h("div", { class: "gwg-onlbox" },
      h("p", null, GW.text("|cFFFFFF00" + (st.name || "A member") + (st.members && st.members.length > 1 ? " are" : " is") + " online.")),
      h("p", null, e.message), list,
      h("p", { class: "gw-dim" }, st.kick ? "Kick disconnects the player through the game server (the game saves first). They can log straight back in and see the change."
        : "The server running now has no kick; ask the player to log out (back to the login screen), then try again."),
      h("div", { class: "gw-confirm-buttons" },
        h("button", { class: "gw-btnx", type: "button", onclick: async () => { m.close(); await retry(); } }, "Try again"),
        h("button", { class: "gw-btnx", type: "button", onclick: () => m.close() }, "Close"))), { title: "Guild members online", wide: true });
    for (const x of st.members || []) {
      const row = h("div", { class: "gwg-onlrow" }, h("span", { class: "gw-dot on" }), h("b", null, x.name),
        h("span", { class: "gw-dim", "data-help": (x.reasons || []).join("\n") }, " why?"));
      if (st.kick) row.append(h("button", { class: "gw-btnx small", type: "button", onclick: async ev => {
        ev.target.disabled = true; ev.target.textContent = "kicking ...";
        try { const r = await GW.api("/api/m/kick/" + x.cid, { reason: "a guild edit" }); ev.target.textContent = "kicked"; GW.status(r.result, "ok"); }
        catch (err) { ev.target.textContent = "Kick"; ev.target.disabled = false; GW.status(err.message, "err"); GW.alert(err.message, "Kick"); }
      } }, "Kick"));
      list.append(row);
    }
    GW.status("not saved: a guild member is online", "err");
    return new Promise(res => { const t = setInterval(() => { if (!m.el.isConnected) { clearInterval(t); res(); } }, 300); });
  }

  // ------------------------------------------------------------------------------------------------ pickers
  // every character, filterable (name / account / guild / class / camp / "no guild only"); scales to thousands (windowed)
  async function pickCharacter(mod, opts) {
    const o = opts || {};
    const data = await mod.ctx.api("characters");
    return new Promise(res => {
      let done = false;
      const fin = v => { if (!done) { done = true; res(v); } };
      const S = { q: "", free: !!o.freeOnly, cls: "", camp: "", sel: null };
      const search = h("input", { class: "gwg-input", type: "search", placeholder: "search name, account, guild ...", "aria-label": "Search characters" });
      const free = h("input", { type: "checkbox", checked: S.free || null });
      const cls = h("select", { class: "gwg-input", "aria-label": "Class" }, h("option", { value: "" }, "every class"), ["Warrior", "Champion", "Priest", "Mage"].map((c, i) => h("option", { value: i }, c)));
      const camp = h("select", { class: "gwg-input", "aria-label": "Camp" }, h("option", { value: "" }, "both camps"), h("option", { value: 0 }, "Sparta"), h("option", { value: 1 }, "Athens"));
      const count = h("span", { class: "gw-dim" });
      const RH = 30;
      const spacer = h("div", { class: "gwg-pc-spacer" });
      const view = h("div", { class: "gwg-pc-view", role: "listbox", tabindex: 0 }, spacer);
      const okb = h("button", { class: "gw-btnx", type: "button", disabled: true }, o.ok || "Pick");
      let shown = [];
      const pool = [];
      function filter() {
        const t = S.q.toLowerCase().split(/\s+/).filter(Boolean);
        shown = data.characters.filter(c => (!S.free || !c.guild_id) && (S.cls === "" || String(c.profession) === S.cls) && (S.camp === "" || String(c.camp) === S.camp)
          && (!o.filter || o.filter(c)) && t.every(x => (c.name + " " + c.username + " " + (c.guild_name || "") + " #" + c.id).toLowerCase().includes(x)));
        count.textContent = fmt(shown.length) + " of " + fmt(data.characters.length) + " characters";
        view.scrollTop = 0; draw();
      }
      function draw() {
        spacer.style.height = shown.length * RH + "px";
        const a = Math.max(0, Math.floor(view.scrollTop / RH) - 3), b = Math.min(shown.length, Math.ceil((view.scrollTop + (view.clientHeight || 400)) / RH) + 3);
        while (pool.length < b - a) {
          const r = h("button", { class: "gwg-pc-row", type: "button", role: "option" });
          r.addEventListener("click", () => { S.sel = r._c; okb.disabled = false; draw(); });
          r.addEventListener("dblclick", () => { fin(r._c); mm.close(); });
          pool.push(r); spacer.append(r);
        }
        let k = 0;
        for (let i = a; i < b; i++, k++) {
          const c = shown[i], r = pool[k];
          r._c = c; r.style.transform = "translateY(" + i * RH + "px)"; r.style.display = "";
          r.classList.toggle("sel", S.sel === c);
          GW.clear(r).append(h("span", { class: "gw-dot " + (c.online ? "on" : "off"), "data-help": c.online ? "online now" : "offline" }),
            h("span", { class: "nm" }, c.name), h("span", { class: "sub" }, "Lv " + c.level + " " + c.class + " · " + c.camp_name + " · " + c.username),
            h("span", { class: "g" + (c.guild_id ? "" : " gw-dim") }, c.guild_id ? c.guild_name + " (" + (mod.dutyName(c.duty)) + ")" : "no guild"));
        }
        for (; k < pool.length; k++) { pool[k].style.display = "none"; }
      }
      view.addEventListener("scroll", () => requestAnimationFrame(draw), { passive: true });
      search.addEventListener("input", () => { S.q = search.value; filter(); });
      free.addEventListener("change", () => { S.free = free.checked; filter(); });
      cls.addEventListener("change", () => { S.cls = cls.value; filter(); });
      camp.addEventListener("change", () => { S.camp = camp.value; filter(); });
      okb.addEventListener("click", () => { if (S.sel) { fin(S.sel); mm.close(); } });
      const mm = GW.modal(h("div", { class: "gwg-pc" },
        h("div", { class: "gwg-pc-bar" }, search, h("label", { "data-help": "Only characters that are in no guild (a character can be in one guild)." }, free, " not in a guild"), cls, camp, count),
        view, h("div", { class: "gw-confirm-buttons" }, okb, h("button", { class: "gw-btnx", type: "button", onclick: () => { fin(null); mm.close(); } }, "Cancel"))),
        { title: o.title || "Pick a character", wide: true, onclose: () => fin(null) });
      setTimeout(() => { search.focus(); filter(); }, 20);
    });
  }

  // ------------------------------------------------------------------------------------------------ the module
  GW.module("guild", {
    async mount(el, ctx) {
      this.el = el; this.ctx = ctx; this.timers = [];
      const route = ctx.route || [];
      this.tabs = h("div", { class: "gwg-top", role: "tablist" });
      this.body = h("div", { class: "gwg-body" });
      el.append(this.tabs, this.body);
      try { this.ov = await ctx.api("overview"); }
      catch (e) { el.append(h("div", { class: "tt-bad" }, e.message)); return; }
      this.offChange = GW.on("change", () => { clearTimeout(this._rt); this._rt = setTimeout(() => this.refresh(), 400); });
      const tab = route[0] === "factions" ? "factions" : route[0] === "systems" ? "systems" : route[0] === "checks" ? "checks" : "guilds";
      this.gid = route[0] === "g" && /^\d+$/.test(route[1] || "") ? Number(route[1]) : null;
      this.sub = route[2] || "info";
      // "#/guild/<character id>" (the Character page's Guild link): open that character's guild
      if (/^\d+$/.test(route[0] || "")) {
        try { const cs = await ctx.api("characters"); const c = cs.characters.find(x => x.id === Number(route[0])); if (c && c.guild_id) { this.gid = c.guild_id; this.sub = "members"; } }
        catch (e) { /* the list still opens */ }
      }
      this.show(tab);
    },
    unmount() { for (const t of this.timers || []) clearInterval(t); if (this.offChange) this.offChange(); },
    dutyName(d) { const x = (this.ov.duties || []).find(y => y.id === d); return x ? x.name : d === 0 ? "None" : "duty " + d; },

    drawTabs() {
      GW.clear(this.tabs);
      const n = (this.ov.issues || []).length;
      const T = [["guilds", "Guilds (" + this.ov.guilds.length + ")", "Every guild: its window, members, buildings, altars and boost."],
        ["factions", "Faction areas", "Which camp holds each world-boss area's EXP bonus."],
        ["systems", "What the server does", "Guild and social systems: what reborn implements, what it does not, and what the editor can still do."],
        ["checks", "Checks" + (n ? " (" + n + ")" : ""), "Guild rows that break the rules the game keeps, with one-click repairs."]];
      for (const [k, label, help] of T) this.tabs.append(h("button", { class: "gw-tabx" + (this.tab === k ? " on" : "") + (k === "checks" && n ? " gwg-warn" : ""), type: "button", role: "tab",
        "aria-selected": this.tab === k ? "true" : "false", "data-help": help, onclick: () => this.show(k) }, label));
      this.tabs.append(h("span", { class: "gwg-topinfo" }, this.ov.round2 ? (this.ov.kick ? h("span", { class: "gw-chip now", "data-help": "The game server answers /admin/capabilities: it can kick a player, so a guild edit never has to wait for a logout." }, "round-2 server: Kick available")
        : h("span", { class: "gw-chip login", "data-help": "The game server answers /admin/capabilities but offers no kick. Guild edits need every member of the guild logged out." }, "round-2 server: members must log out"))
        : h("span", { class: "gw-chip login", "data-help": "The server running now has no management endpoints. Guild edits need every member of the guild logged out." }, "stock server: members must log out"),
        " ", h("button", { class: "gw-btnx small", type: "button", "data-help": "Every change made in this editor, with Undo.", onclick: () => GW.changes.open() }, "Change log")));
    },
    async refresh() {
      try { this.ov = await this.ctx.api("overview"); } catch (e) { return; }
      if (this.tab === "guilds") { this.drawList(); if (this.gid) this.openGuild(this.gid, this.sub, true); }
      else this.show(this.tab);
      this.drawTabs();
    },
    show(tab) {
      this.tab = tab;
      for (const t of this.timers) clearInterval(t);
      this.timers = [];
      this.drawTabs();
      GW.clear(this.body);
      if (tab === "guilds") return this.guildsTab();
      if (tab === "factions") return this.factionsTab();
      if (tab === "systems") return this.systemsTab();
      if (tab === "checks") return this.checksTab();
    },
    hash(s) { history.replaceState(null, "", "#/guild" + (s ? "/" + s : "")); },

    // ============================================================================================ Guilds tab
    guildsTab() {
      this.hash(this.gid ? "g/" + this.gid + "/" + this.sub : "");
      this.list = h("div", { class: "gwg-list", role: "list" });
      this.search = h("input", { class: "gwg-input", type: "search", placeholder: "search guild or leader", "aria-label": "Search guilds" });
      this.search.addEventListener("input", () => this.drawList());
      this.win = h("section", { class: "gw-frame-normalwindow gw-win gwg-window" }, h("div", { class: "gw-wtitle" }, "Guild"),
        h("p", { class: "gw-dim gwg-pad" }, "Pick a guild on the left, or make a new one."));
      this.body.append(h("div", { class: "gwg-cols" },
        h("section", { class: "gw-frame-normalwindow gw-win gwg-left" }, h("div", { class: "gw-wtitle" }, "Guilds"),
          h("div", { class: "gwg-tools" },
            h("button", { class: "gw-btnx", type: "button", disabled: !this.ov.writable || null, "data-help": "Found a new guild with a leader of your choice. The game asks for a Guild Stone (item 4100); the editor does not.", onclick: () => this.create() }, "+ New guild"),
            this.search),
          this.list),
        this.win));
      this.drawList();
      if (this.gid) this.openGuild(this.gid, this.sub);
    },
    drawList() {
      if (!this.list) return;
      const q = (this.search && this.search.value || "").toLowerCase();
      GW.clear(this.list);
      const rows = this.ov.guilds.filter(g => !q || (g.name + " " + (g.lord_name || "") + " #" + g.id).toLowerCase().includes(q));
      for (const g of rows) {
        this.list.append(h("button", { class: "gwg-card" + (g.id === this.gid ? " on" : ""), type: "button", role: "listitem", onclick: () => this.openGuild(g.id, this.sub) },
          campIcon(g.camp),
          h("span", { class: "nm" }, g.name, h("span", { class: "gw-dim" }, "  Lv " + g.level)),
          h("span", { class: "sub" }, "Leader " + (g.lord_name || "?") + " · " + g.members + "/" + g.member_limit + " members",
            g.online_now ? h("span", { class: "gwg-onlcount", "data-help": g.online_now + " member(s) online now: edits wait until they log out (or Kick)." }, " · " + g.online_now + " online") : null),
          h("span", { class: "gwg-money" }, h("span", { class: "gw-sprite gw-coin-silver", "data-help": "Guild Silver" }), fmt(g.silver), " ",
            h("span", { class: "gw-sprite gw-coin-gold", "data-help": "Guild Gold" }), fmt(g.gold))));
      }
      if (!rows.length) this.list.append(h("div", { class: "gw-dim gwg-pad" }, q ? "No guild matches." : "No guilds yet. Players found one at the Guild Registrar with a Guild Stone, or use + New guild."));
    },

    async create() {
      const name = h("input", { class: "gwg-input", type: "text", maxlength: this.ov.limits.name, "aria-label": "Guild name", autocomplete: "off" });
      let lord = null;
      const lordBox = h("span", { class: "gwg-lordpick gw-dim" }, "(nobody yet)");
      const pickBtn = h("button", { class: "gw-btnx small", type: "button", onclick: async () => {
        const c = await pickCharacter(this, { title: "Pick the leader (not in a guild)", freeOnly: true, ok: "Make leader", filter: c => !c.guild_id });
        if (c) { lord = c; GW.clear(lordBox).append(h("span", { class: "gw-dot " + (c.online ? "on" : "off") }), h("b", null, c.name), " Lv " + c.level + " " + c.class + " · " + c.camp_name); lordBox.classList.remove("gw-dim"); }
      } }, "Pick");
      const level = GW.numberField({ field: "guilds.level", label: "Level", value: 1, min: 1, max: 12 });
      const limit = GW.numberField({ field: "guilds.member_limit", label: "Member limit", value: 100, min: 1, max: this.ov.limits.member_limit });
      const silver = GW.numberField({ field: "guilds.silver", label: "Silver", value: 0, min: 0, max: this.ov.limits.money });
      const gold = GW.numberField({ field: "guilds.gold", label: "Gold", value: 0, min: 0, max: this.ov.limits.money });
      const gems = GW.numberField({ field: "guilds.bijou", label: "Gems", value: 0, min: 0, max: this.ov.limits.money });
      const slogan = h("textarea", { class: "gwg-slogan", maxlength: this.ov.limits.proclamation, rows: 3, "aria-label": "Proclamation" });
      const err = h("div", { class: "tt-bad", role: "alert" });
      const go = h("button", { class: "gw-btnx", type: "button" }, "Found the guild");
      const m = GW.modal(h("div", { class: "gwg-form" },
        h("div", { class: "gwg-grid" },
          h("label", { "data-field": "guilds.name" }, "Name"), h("span", null, name, h("span", { class: "gw-dim" }, " up to " + this.ov.limits.name + " characters (the client's name box)")),
          h("label", { "data-field": "guilds.lord_character_id" }, "Leader"), h("span", null, lordBox, " ", pickBtn)),
        level, limit, silver, gold, gems,
        h("div", { class: "gw-label", "data-field": "guilds.proclamation" }, "Slogan (optional; the guild window shows the first " + this.ov.limits.proclamation_wire + " characters at login):"), slogan,
        h("p", { class: "gw-dim" }, "Writes what the Guild Registrar writes: the guild, the leader's membership (Leader, duty 6) and the leader's guild columns, in one transaction. The leader must be logged out. ",
          GW.appliesChip("next_login")),
        err, h("div", { class: "gw-confirm-buttons" }, go)), { title: "New guild", wide: true });
      setTimeout(() => name.focus(), 0);
      go.addEventListener("click", async () => {
        err.textContent = "";
        if (!name.value.trim()) { err.textContent = "Give the guild a name."; return; }
        if (!lord) { err.textContent = "Pick the leader."; return; }
        if (![level, limit, silver, gold, gems].every(f => f.valid())) { err.textContent = "Fix the numbers in red first."; return; }
        if (!/^[\x20-\x7e]*$/.test(slogan.value)) { err.textContent = "Use plain ASCII text in the slogan (the server sends it as ASCII)."; return; }
        go.disabled = true;
        const r = await write(this, "create", { name: name.value.trim(), lord: lord.id, level: level.value, member_limit: limit.value, silver: silver.value, gold: gold.value,
          bijou: gems.value, proclamation: slogan.value },
          "guild " + name.value.trim() + " founded");
        go.disabled = false;
        if (r) { m.close(); this.ov = await this.ctx.api("overview"); this.gid = r.guild.id; this.sub = "info"; this.show("guilds"); }
      });
    },

    // ---------------------------------------------------------------------------------------- the guild window
    async openGuild(gid, sub, quiet) {
      this.gid = gid; this.sub = sub || "info";
      for (const b of this.list ? this.list.querySelectorAll(".gwg-card") : []) b.classList.remove("on");
      this.drawList();
      if (!quiet && this.win) { const t = this.win.querySelector(".gwg-page"); if (t) t.classList.add("gwg-loading"); else GW.clear(this.win).append(h("div", { class: "gw-wtitle" }, "Guild"), h("p", { class: "gw-dim gwg-pad" }, "loading ...")); }
      let d;
      try { d = await this.ctx.api("g/" + gid); }
      catch (e) { if (e.status === 404) { this.gid = null; this.hash(""); GW.clear(this.win).append(h("div", { class: "gw-wtitle" }, "Guild"), h("p", { class: "gw-dim gwg-pad" }, e.message)); return; } GW.status(e.message, "err"); return; }
      this.d = d;
      this.hash("g/" + gid + "/" + this.sub);
      const w = this.win;
      GW.clear(w).append(h("div", { class: "gw-wtitle" }, d.guild.name));
      w.append(this.banner(d));
      const subs = [["info", "Info"], ["members", "Guild (" + d.members.length + ")"], ["buildings", "Building"], ["altars", "Altars"], ["boost", "Boost"]];
      const tabs = h("div", { class: "gwg-wtabs", role: "tablist" });
      for (const [k, label] of subs) tabs.append(h("button", { class: "gw-tabx" + (this.sub === k ? " on" : ""), type: "button", role: "tab", "aria-selected": this.sub === k ? "true" : "false",
        onclick: () => this.openGuild(gid, k) }, label));
      const page = h("div", { class: "gwg-page" });
      w.append(tabs, page);
      if (d.issues.length) page.append(h("div", { class: "gw-online warn" }, h("b", null, d.issues.length + " problem(s): "), d.issues.map(i => i.who + ": " + i.what).join(" · "), " ",
        h("button", { class: "gw-btnx small", type: "button", onclick: () => this.show("checks") }, "Open Checks")));
      this["page_" + this.sub](page, d);
    },
    banner(d) {
      const on = d.members.filter(m => m.online);
      const el = h("div", { class: "gw-online " + (on.length ? "on" : "off"), role: "status" });
      if (!on.length) el.append(h("span", { class: "gw-dot off" }), h("b", null, "All " + d.members.length + " member(s) offline"), " - safe to edit. Members see the changes when they next enter the world. ", GW.appliesChip("next_login"));
      else {
        el.append(h("span", { class: "gw-dot on" }), h("b", null, on.map(m => m.name).join(", ") + (on.length > 1 ? " are" : " is") + " online"), " - ",
          this.ov.round2 ? "the game server has no live guild edit (each online member's guild window and altar bonus are held by the session). Kick them, then edit: "
            : "Live edits arrive with the round-2 server update; every member of this guild must be logged out to edit. ");
        if (this.ov.kick) for (const m of on) el.append(h("button", { class: "gw-btnx small", type: "button", "data-help": "Disconnect " + m.name + " (the game saves first); they can log straight back in.", onclick: async ev => {
          if (!await GW.confirm({ title: "Kick " + m.name + "?", message: "The player is disconnected through the game server and the game saves first. Then the guild can be edited.", ok: "Kick" })) return;
          ev.target.disabled = true;
          try { const r = await GW.api("/api/m/kick/" + m.character_id, { reason: "a guild edit" }); GW.status(r.result, "ok"); } catch (e) { GW.status(e.message, "err"); }
          setTimeout(() => this.openGuild(this.gid, this.sub, true), 800);
        } }, "Kick " + m.name));
      }
      // keep it current while the page is open
      const t = setInterval(async () => {
        if (!el.isConnected) { clearInterval(t); return; }
        try { const d2 = await this.ctx.api("g/" + d.guild.id); const now = d2.members.filter(m => m.online).map(m => m.name).join(","); if (now !== on.map(m => m.name).join(",")) this.openGuild(this.gid, this.sub, true); } catch (e) { /* gone */ }
      }, 15000);
      this.timers.push(t);
      return el;
    },

    // ---- Info: Lord / Deputy, Level / Max Mem / Silver / Gold, slogan
    page_info(page, d) {
      const g = d.guild, lim = this.ov.limits;
      const lords = d.members.filter(m => m.duty === 6), vices = d.members.filter(m => m.duty === 5);
      const pend = {};
      const bar = GW.saveBar({ title: "Save the guild?", onSave: async () => {
        const values = {}; const expected = {};
        for (const k in pend) { values[k] = pend[k]; expected[k] = g[k]; }
        const items = bar.items();
        const r = await write(this, "g/" + g.id + "/edit", { values, expected }, "guild " + g.name + " saved", { reload: () => this.openGuild(g.id, "info") });
        if (r) { for (const k in pend) delete pend[k]; this.refresh(); }
        else setTimeout(() => { for (const c of items) bar.set(c.key, c); }, 0);      // not saved: keep the pending edits
        return null;
      } });
      const set = (k, label, v, field) => { if (v === g[k]) { delete pend[k]; bar.unset(k); } else { pend[k] = v; bar.set(k, { key: k, label, from: g[k], to: v, field, applies: "next_login" }); } };
      // the client's own caps (Settings/Sys/Consortia.xml CorsortiaDefaultValue): above them the game's donate / spend
      // checks stop working as designed, so the page warns (the server and the database accept up to 2,147,483,647)
      const dv = this.ov.defaults || {};
      const CAP = { silver: dv.MaxMoney || 900000000, gold: dv.MaxMoney || 900000000, bijou: dv.MaxBijou || 90000000 };
      const capWarn = h("div", { class: "tt-bad gwg-note", role: "status" });
      const capNow = {};
      const capDraw = () => { const over = Object.keys(capNow).filter(k => capNow[k] > CAP[k]);
        capWarn.textContent = over.length ? "Above the client's own limit (Consortia.xml: funds " + fmt(CAP.silver) + ", gems " + fmt(CAP.bijou) + "): " + over.map(k => ({ silver: "Silver", gold: "Gold", bijou: "Gems" })[k]).join(", ") + ". The server stores it; the game client was made for at most these numbers, so check the guild window in game." : ""; };
      for (const k in CAP) capNow[k] = g[k];
      capDraw();
      const nf = (k, label, max, unit) => GW.numberField({ field: "guilds." + k, label, value: g[k], min: k === "level" || k === "member_limit" ? 1 : 0, max, unit,
        onChange: (v, ok) => { if (ok && v !== null) { set(k, label, v, "guilds." + k); if (k in CAP) { capNow[k] = v; capDraw(); } } }, disabled: !this.ov.writable });
      const name = h("input", { class: "gwg-input", type: "text", value: g.name, maxlength: lim.name, "aria-label": "Guild name", disabled: !this.ov.writable || null });
      name.addEventListener("input", () => set("name", "Name", name.value.trim(), "guilds.name"));
      const slogan = h("textarea", { class: "gwg-slogan", maxlength: lim.proclamation, rows: 4, "aria-label": "Proclamation", disabled: !this.ov.writable || null });
      slogan.value = g.proclamation || "";
      const cnt = h("span", { class: "gw-dim" });
      const sc = () => { const n = slogan.value.length; cnt.textContent = n + " / " + lim.proclamation + (n > lim.proclamation_wire ? " - the guild window shows the first " + lim.proclamation_wire + " at login (the board shows it all after an edit in game)" : ""); cnt.className = n > lim.proclamation_wire ? "tt-bad" : "gw-dim"; };
      slogan.addEventListener("input", () => { sc(); set("proclamation", "Proclamation", slogan.value, "guilds.proclamation"); });
      sc();
      const refuse = h("input", { type: "checkbox", checked: g.refuse_applications || null, disabled: !this.ov.writable || null, "aria-label": "Refuse applications" });
      refuse.addEventListener("change", () => set("refuse_applications", "Refuse applications", refuse.checked, "guilds.refuse_applications"));
      const memberLine = m => m ? h("span", { class: "gwg-who" }, h("span", { class: "gw-dot " + (m.online ? "on" : "off") }), h("span", { class: "gw-value" }, m.name), h("span", { class: "gw-dim" }, " Lv " + m.level + " " + m.class)) : h("span", { class: "gw-dim" }, "(none)");
      const lv = this.ov.levels || {};
      const next = lv[String(g.level + 1)];
      const limWarn = h("div", { class: "tt-bad gwg-note", role: "status" }, g.member_limit < d.members.length ? "More members (" + d.members.length + ") than the limit: nobody can join until members leave." : "");
      page.append(h("div", { class: "gwg-info" },
        h("div", { class: "gw-frame-simplewindow gwg-panel" },
          h("div", { class: "gwg-kv" }, h("span", { class: "gw-label", "data-field": "guilds.lord_character_id" }, "Lord:"),
            h("span", null, lords.length ? memberLine(lords[0]) : h("span", { class: "tt-bad" }, (g.lord_name || "#" + g.lord_character_id) + " (not a member: pick a new leader)"),
              " ", h("button", { class: "gw-btnx small", type: "button", disabled: !this.ov.writable || null, "data-help": "Hand the leadership to another member. The game itself cannot do this. The old leader becomes Vice-Leader.", onclick: () => this.makeLeader(d) }, "Make leader"))),
          h("div", { class: "gwg-kv" }, h("span", { class: "gw-label", "data-help": "Vice-Leaders (duty 5). The official game allowed 4." }, "Deputy:"),
            h("span", null, vices.length ? vices.map((m, i) => [i ? ", " : "", memberLine(m)]) : h("span", { class: "gw-dim" }, "(none)"), vices.length > 4 ? h("span", { class: "gw-chip restart", "data-help": "The official game allowed 4 Vice-Leaders; reborn does not limit them." }, " more than 4") : null)),
          h("div", { class: "gwg-kv" }, h("span", { class: "gw-label", "data-field": "guilds.name" }, "Name:"), name),
          h("div", { class: "gwg-kv" }, h("span", { class: "gw-label", "data-field": "guilds.refuse_applications" }, "Refuse applications:"), refuse),
          h("div", { class: "gwg-kv" }, h("span", { class: "gw-label", "data-field": "guilds.created_at" }, "Founded:"), h("span", { class: "gw-value" }, when(g.created_at))),
          h("div", { class: "gwg-kv" }, h("span", { class: "gw-label", "data-field": "guilds.online_count" }, "Online last time:"), h("span", { class: "gw-value" }, g.online_count + " (the window's own count)"))),
        h("div", { class: "gw-frame-simplewindow gwg-panel" },
          nf("level", "Level", lim.level),
          h("div", { class: "gw-dim gwg-note", "data-help": "In the official game the guild level followed the Guild Footstone; reborn never raises it (it stays 1). Consortia.xml lists what each level cost." },
            next ? "Next level in the official game: " + fmt(next.funds) + " guild Silver + " + fmt(next.gems) + " gems" : "Top level."),
          GW.numberField({ field: "guilds.member_limit", label: "Max Mem", value: g.member_limit, min: 1, max: lim.member_limit, disabled: !this.ov.writable,
            onChange: (v, ok) => { if (ok && v !== null) set("member_limit", "Max Mem", v, "guilds.member_limit");
              limWarn.textContent = ok && v !== null && v < d.members.length ? "Below the " + d.members.length + " members the guild has: nobody is removed, but nobody can join until members leave." : ""; } }),
          h("div", { class: "gw-dim gwg-note" }, d.members.length + " members now. The in-game list shows the first " + lim.roster + "."),
          limWarn,
          nf("silver", "Silver", lim.money), nf("gold", "Gold", lim.money), nf("bijou", "Gems", lim.money), capWarn)),
        h("div", { class: "gw-frame-simplewindow gwg-panel gwg-wide" }, h("div", { class: "gw-label", "data-field": "guilds.proclamation" }, "Slogan:"), slogan, cnt),
        bar.el,
        h("div", { class: "gwg-danger" },
          h("button", { class: "gw-btnx danger", type: "button", disabled: !this.ov.writable || null, "data-help": "Delete the guild, its members' memberships, buildings and altar offerings; each member's guild duty and contribution go to 0 (as leaving does). Undo in the Change log brings everything back.",
            onclick: () => this.disband(d) }, "Disband guild"),
          h("span", { class: "gw-dim" }, "The game has no disband; the editor can. Undo restores it.")));
    },
    async makeLeader(d) {
      const g = d.guild;
      const c = await pickCharacter(this, { title: "New leader of " + g.name + " (members only)", ok: "Make leader", filter: c => c.guild_id === g.id && c.id !== g.lord_character_id });
      if (!c) return;
      const old = h("select", { class: "gwg-input", "aria-label": "Old leader's new duty" }, this.ov.duties.filter(x => x.id < 6).map(x => h("option", { value: x.id, selected: x.id === 5 || null }, x.name)));
      if (!await GW.confirm({ title: "Make " + c.name + " the leader?", el: h("div", null, h("p", null, c.name + " becomes Leader (duty 6) and the guild's lord."),
        h("p", null, (g.lord_name || "The old leader") + " becomes ", old)), ok: "Make leader" })) return;
      const r = await write(this, "g/" + g.id + "/leader", { cid: c.id, old_duty: Number(old.value) }, c.name + " leads " + g.name);
      if (r) this.openGuild(g.id, this.sub);
    },
    async disband(d) {
      const g = d.guild;
      const r = await write(this, "g/" + g.id + "/disband", {}, "guild " + g.name + " disbanded", { confirm: { title: "Disband " + g.name + "?", danger: true, ok: "Disband",
        message: "The guild, its " + d.members.length + " membership(s), " + d.buildings.filter(b => b.level).length + " building(s) and " + d.worship.length + " altar offering(s) and the members' guild EXP boost are deleted. Every member's guild duty and contribution go to 0.\n\nUndo in the Change log puts all of it back." } });
      if (r) { this.gid = null; this.ov = await this.ctx.api("overview"); this.show("guilds"); }
    },

    // ---- Members (the game's "Guild" tab): Name, Lv., Class, Position, SilvrCtrb, GoldCtrb + contribution points
    page_members(page, d) {
      const g = d.guild;
      const tbl = h("table", { class: "gwg-members" },
        h("thead", null, h("tr", null,
          h("th", { "data-help": "Online now?" }, ""), h("th", { "data-field": "character_base.name" }, "Name"), h("th", { "data-field": "character_base.fighter_job_lv" }, "Lv."),
          h("th", { "data-field": "character_base.profession" }, "Class"), h("th", { "data-field": "guild_members.duty" }, "Position"),
          h("th", { "data-field": "guild_members.contribution_silver" }, "SilvrCtrb"), h("th", { "data-field": "guild_members.contribution_gold" }, "GoldCtrb"),
          h("th", { "data-field": "character_base.consortia_contribute" }, "Points"), h("th", { "data-field": "guild_members.joined_at" }, "Joined"), h("th", null, ""))));
      const tb = h("tbody");
      for (const m of d.members) {
        const duty = h("select", { class: "gwg-input gwg-duty", "aria-label": "Duty of " + m.name, disabled: !this.ov.writable || m.duty === 6 || null,
          "data-help": m.duty === 6 ? "The leader's duty changes with 'Make leader'." : "Pick a duty; it saves at once (the game keeps the duty on the membership AND on the character, the editor writes both)." },
          this.ov.duties.filter(x => x.id < 6 || m.duty === 6).map(x => h("option", { value: x.id, selected: x.id === m.duty || null }, x.name)));
        duty.addEventListener("change", async () => {
          const r = await write(this, "g/" + g.id + "/member/" + m.character_id + "/edit", { duty: Number(duty.value), expected: { duty: m.duty } }, m.name + " is now " + this.dutyName(Number(duty.value)));
          this.openGuild(g.id, "members");
          if (!r) return;
        });
        tb.append(h("tr", { class: (m.in_step ? "" : "gwg-bad") + (m.shown_in_window ? "" : " gwg-hidden-row") },
          h("td", null, h("span", { class: "gw-dot " + (m.online ? "on" : "off"), "data-help": m.online ? "online: " + m.reasons.join("; ") : "offline" })),
          h("td", { class: "gw-value" }, m.duty === 6 ? h("span", { class: "gwg-crown", "data-help": "Leader (duty 6)" }, "♛ ") : null, m.name, m.in_step ? null : h("span", { class: "tt-bad", "data-help": "This character's own guild columns disagree with the membership. Checks -> Repair." }, " !"),
            m.shown_in_window ? null : h("span", { class: "gw-dim", "data-help": "The in-game guild window lists only the first " + this.ov.limits.roster + " members (highest duty first)." }, " (not in the window)")),
          h("td", null, m.level), h("td", null, m.class), h("td", null, duty),
          h("td", { class: "num" }, fmt(m.contribution_silver)), h("td", { class: "num" }, fmt(m.contribution_gold)), h("td", { class: "num" }, fmt(m.consortia_contribute)),
          h("td", { class: "gw-dim" }, when(m.joined_at).split(",")[0]),
          h("td", { class: "gwg-acts" },
            h("button", { class: "gw-btnx small", type: "button", disabled: !this.ov.writable || null, "data-help": "Edit the contributions and contribution points.", onclick: () => this.editMember(d, m) }, "Edit"),
            m.duty === 6 ? null : h("button", { class: "gw-btnx small danger", type: "button", disabled: !this.ov.writable || null, "data-help": "Remove from the guild (as leaving does: duty and contribution points go to 0).",
              onclick: async () => { const r = await write(this, "g/" + g.id + "/member/" + m.character_id + "/remove", {}, m.name + " removed", { confirm: { title: "Remove " + m.name + "?", danger: true, ok: "Remove", message: m.name + " leaves " + g.name + ". Their guild duty and contribution points go to 0, as when a player leaves. Undo puts them back." } }); if (r) this.openGuild(g.id, "members"); } }, "Remove"))));
      }
      tbl.append(tb);
      page.append(h("div", { class: "gwg-memtools" },
        h("button", { class: "gw-btnx", type: "button", disabled: !this.ov.writable || null, "data-help": "Seat any character that is in no guild (pick from every character).", onclick: () => this.addMember(d) }, "+ Add member"),
        h("span", { class: "gw-dim" }, d.members.length + " / " + g.member_limit + " members"), GW.appliesChip("next_login")), h("div", { class: "gwg-scroll" }, tbl),
        h("p", { class: "gw-dim" }, "Points = guild contribution points the member still holds (donating 1 Silver gives 1, 1 Gold gives 100; offering at an altar spends them). SilvrCtrb / GoldCtrb = what they donated."));
    },
    async addMember(d) {
      const g = d.guild;
      if (d.members.length >= g.member_limit) { GW.alert("The guild is full (" + d.members.length + " of " + g.member_limit + "). Raise Max Mem on the Info tab first.", "Guild full"); return; }
      const c = await pickCharacter(this, { title: "Add a member to " + g.name, freeOnly: true, ok: "Add", filter: c => !c.guild_id });
      if (!c) return;
      const duty = h("select", { class: "gwg-input", "aria-label": "Duty" }, this.ov.duties.filter(x => x.id < 6).map(x => h("option", { value: x.id, "data-help": x.help }, x.name)));
      if (!await GW.confirm({ title: "Add " + c.name + "?", el: h("div", null, h("p", null, c.name + " joins " + g.name + " as ", duty), h("p", { class: "gw-dim" }, "A player who applies in game starts as Member (duty 1). The names are the English client's own (Text/Message.dat G1-G6).")), ok: "Add" })) return;
      const r = await write(this, "g/" + g.id + "/member/add", { cid: c.id, duty: Number(duty.value) }, c.name + " joined " + g.name);
      if (r) this.openGuild(g.id, "members");
    },
    editMember(d, m) {
      const g = d.guild, lim = this.ov.limits;
      const s = GW.numberField({ field: "guild_members.contribution_silver", label: "Silver donated", value: m.contribution_silver, min: 0, max: lim.money });
      const go = GW.numberField({ field: "guild_members.contribution_gold", label: "Gold donated", value: m.contribution_gold, min: 0, max: lim.money });
      const p = GW.numberField({ field: "character_base.consortia_contribute", label: "Contribution points", value: m.consortia_contribute, min: 0, max: lim.money, allowReadOnly: true });
      const btn = h("button", { class: "gw-btnx", type: "button" }, "Save");
      const mm = GW.modal(h("div", { class: "gwg-form" }, s, go, p,
        h("p", { class: "gw-dim" }, "The official game capped contribution points at " + fmt((this.ov.defaults || {}).MaxContribute || 900000) + "; reborn allows up to 2,147,483,647. ", GW.appliesChip("next_login")),
        h("div", { class: "gw-confirm-buttons" }, btn)), { title: m.name + " - contributions", wide: true });
      btn.addEventListener("click", async () => {
        if (![s, go, p].every(f => f.valid())) return;
        const r = await write(this, "g/" + g.id + "/member/" + m.character_id + "/edit", { contribution_silver: s.value, contribution_gold: go.value, consortia_contribute: p.value,
          expected: { contribution_silver: m.contribution_silver, contribution_gold: m.contribution_gold, consortia_contribute: m.consortia_contribute } }, m.name + " contributions saved",
          { reload: () => { mm.close(); this.openGuild(g.id, "members"); } });
        if (r) { mm.close(); this.openGuild(g.id, "members"); }
      });
    },

    // ---- Buildings: every type as a card, level stars, the client's cost table
    page_buildings(page, d) {
      const g = d.guild;
      const grid = h("div", { class: "gwg-bgrid" });
      const desc = h("div", { class: "gw-frame-simplewindow gwg-bdesc" }, h("div", { class: "gw-label" }, "Building Description"), h("p", { class: "gw-dim" }, "Click a building."));
      const pick = b => {
        for (const x of grid.children) x.classList.toggle("on", x._b === b);
        const lvl = GW.numberField({ field: "guild_buildings.level", label: "Level", value: b.level, min: 0, max: b.max, disabled: !this.ov.writable,
          onChange: (v, ok) => { save.disabled = !ok || v === b.level; } });
        const save = h("button", { class: "gw-btnx", type: "button", disabled: true }, "Save level");
        save.addEventListener("click", async () => {
          const v = lvl.value;
          const r = await write(this, "g/" + g.id + "/building", { type: b.type, level: v, expected_level: b.level }, b.name + (v ? " level " + v : " removed"),
            { confirm: v === 0 ? { title: "Remove " + b.name + "?", message: "The building is deleted (the game's Delete button). Undo puts it back.", ok: "Remove", danger: true } : null, reload: () => this.openGuild(g.id, "buildings") });
          if (r) this.openGuild(g.id, "buildings");
        });
        const impact = Object.entries(b.impact || {}).map(([k, v]) => k + " " + v).join(", ");
        GW.clear(desc).append(...[h("div", { class: "gwg-bhead" }, GW.cell({ sprite: b.sprite, name: b.name, tipType: "guild_building", tipId: b.type }),
          h("div", null, h("div", { class: "gw-value" }, b.name), h("div", { class: "gw-dim" }, "type " + b.type + " · levels 1-" + b.max))),
          h("p", null, b.desc || ""),
          b.altar ? h("p", { class: "gwg-note" }, "Altar: pays ", h("b", { class: "gw-good" }, b.attribute), b.level ? " - base value " + fmt(b.base_value) + " at level " + b.level + " (offering points buy 0-150% of it)." : " once built.") : null,
          impact ? h("p", { class: "gw-dim", "data-help": "The client's own Consortia.xml effect numbers for this level (warehouse capacity, member cap, ...). Reborn does not apply building effects other than the altars." }, "Client effect at this level: " + impact) : null,
          lvl, h("p", { class: "gw-dim" }, "0 = not built. ", b.next_cost ? "Official cost of the next level: " + fmt(b.next_cost.silver) + " guild Silver + " + fmt(b.next_cost.gems) + " gems (reborn builds for free)." : "", " "), save, " ", GW.appliesChip("next_login")].filter(x => x !== null && x !== undefined));
      };
      for (const b of d.buildings) {
        const stars = h("span", { class: "gwg-stars", "aria-label": "level " + b.level + " of " + b.max });
        for (let i = 1; i <= b.max; i++) stars.append(h("span", { class: "gwg-pip" + (i <= b.level ? " on" : "") }));
        const card = h("button", { class: "gwg-bcard" + (b.level ? "" : " off"), type: "button", onclick: () => pick(b), "data-help": b.name + ": " + (b.level ? "level " + b.level + " of " + b.max : "not built") },
          GW.cell({ sprite: b.sprite, name: b.name }), h("span", { class: "nm" }, b.name), h("span", { class: "lv" }, b.level ? "Lv " + b.level + "/" + b.max : "not built"), stars);
        card._b = b;
        grid.append(card);
      }
      page.append(h("div", { class: "gwg-bcols" }, h("div", { class: "gwg-scroll gwg-bleft" }, grid), desc));
      pick(d.buildings[0]);
    },

    // ---- Altars: members x the ten altars, offering points, the bonus they buy and the drain
    page_altars(page, d) {
      const g = d.guild;
      const altars = d.buildings.filter(b => b.altar);
      const bal = {};
      for (const w of d.worship) bal[w.character_id + ":" + w.building_type] = w;
      const head = h("tr", null, h("th", null, "Member"), altars.map(a => h("th", { class: "gwg-alt", "data-tip-type": "guild_building", "data-tip-id": String(a.type) },
        GW.icon(a.sprite, { name: a.name }), h("div", null, a.name), h("div", { class: "gw-good" }, a.attribute), h("div", { class: a.level ? "gw-dim" : "tt-bad" }, a.level ? "Lv " + a.level : "not built"))));
      const tb = h("tbody");
      for (const m of d.members) {
        tb.append(h("tr", null, h("td", { class: "gw-value" }, m.name, h("div", { class: "gw-dim" }, fmt(m.consortia_contribute) + " pts")),
          altars.map(a => {
            const w = bal[m.character_id + ":" + a.type];
            return h("td", { class: "gwg-acell" + (w ? " has" : "") }, h("button", { class: "gwg-abtn", type: "button", disabled: !this.ov.writable || null,
              "data-help": w ? fmt(w.points) + " points stored, about " + fmt(w.points_now) + " after the hourly drain (" + w.hours_owed + " h owed). Pays " + w.bonus_percent + "% of " + fmt(w.base_value) + " = +" + fmt(w.bonus) + " " + a.attribute + "." : "No offering. Click to set points.",
              onclick: () => this.editWorship(d, m, a, w) }, w ? [h("b", null, fmt(w.points)), h("div", { class: "gw-good" }, "+" + fmt(w.bonus) + " (" + w.bonus_percent + "%)")] : "-"));
          })));
      }
      page.append(h("div", { class: "gwg-scroll" }, h("table", { class: "gwg-altars" }, h("thead", null, head), tb)),
        h("p", { class: "gw-dim" }, "Each 50,000 points pay 7.5% of the altar's base value (from its built level), up to 150% at 1,000,000. The server drains 600-8,000 points an hour (more for big balances). These are reborn's own numbers; the client's Consortia.xml has different ones (see 'What the server does')."),
        h("p", { class: "gw-dim" }, "An altar pays only once it is built (Building tab) and only to members holding points on it. ", GW.appliesChip("next_login")));
    },
    editWorship(d, m, a, w) {
      const g = d.guild;
      const out = h("div", { class: "gwg-note" });
      const base = a.base_value || 0;
      const calc = v => { const pct = v > 0 ? Math.min(Math.floor(v / 50000), 20) * 7.5 : 0; const val = Math.round(base * pct / 100);
        GW.clear(out).append("Pays ", h("b", null, pct + "%"), " of " + fmt(base) + " = ", h("b", { class: "gw-good" }, "+" + fmt(val) + " " + a.attribute),
          base ? "" : h("span", { class: "tt-bad" }, " (the altar is not built: it pays nothing)"), h("div", { class: "gw-dim" }, "Drain: " + fmt(v > 800000 ? 8000 : v > 600000 ? 5000 : v > 300000 ? 1500 : 600) + " points an hour at this balance.")); };
      const nf = GW.numberField({ field: "guild_altar_worship.points", label: "Offering points", value: w ? w.points : 0, min: 0, max: this.ov.limits.money, onChange: (v, ok) => { if (ok && v !== null) calc(v); } });
      calc(w ? w.points : 0);
      const btn = h("button", { class: "gw-btnx", type: "button" }, "Save");
      const mm = GW.modal(h("div", { class: "gwg-form" }, h("div", { class: "gwg-bhead" }, GW.icon(a.sprite, { name: a.name }), h("b", null, " " + a.name + " · " + m.name)),
        nf, out, h("p", { class: "gw-dim" }, "0 clears the balance. The hourly drain counts from this save. Up to 1,000,000 points buy the full 150%; more is kept but pays no more."),
        h("div", { class: "gw-confirm-buttons" }, btn)), { title: "Altar offering", wide: true });
      btn.addEventListener("click", async () => {
        if (!nf.valid()) return;
        const r = await write(this, "g/" + g.id + "/worship", { cid: m.character_id, type: a.type, points: nf.value, expected: w ? w.points : 0 }, m.name + ": " + a.name + " offering saved",
          { reload: () => { mm.close(); this.openGuild(g.id, "altars"); } });
        if (r) { mm.close(); this.openGuild(g.id, "altars"); }
      });
    },

    // ---- Boost: the [Guild] Double EXP status for every member
    page_boost(page, d) {
      const g = d.guild;
      const hours = GW.numberField({ label: "Online hours", value: 16, min: 1, max: 24 * 365, field: "character_experience_modifiers.expires_at", allowReadOnly: true });
      const pct = GW.numberField({ label: "EXP bonus", unit: "%", value: 100, min: 1, max: 1000000, field: "character_experience_modifiers.bonus_basis_points", allowReadOnly: true });
      const rows = h("tbody");
      for (const m of d.members) {
        const b = d.boosts.find(x => x.character_id === m.character_id);
        rows.append(h("tr", null, h("td", { class: "gw-value" }, m.name), h("td", null, b ? "+" + fmt(b.bonus_basis_points / 100) + "%" : h("span", { class: "gw-dim" }, "none")),
          h("td", null, b ? Math.round((b.remaining_online_ticks || 0) / 36e9 * 10) / 10 + " h online left" : ""), h("td", { class: "gw-dim" }, b ? b.source : "")));
      }
      page.append(h("div", { class: "gw-frame-simplewindow gwg-panel gwg-wide" },
        h("div", { class: "gwg-bhead" }, GW.cell({ sprite: 29, name: "Guild Double EXP", tipType: "status", tipId: 1007 }),
          h("div", null, h("div", { class: "gw-value" }, "[Guild] Double EXP"), h("div", { class: "gw-dim" }, "Status 1007, the client's own guild benefit. Reborn never grants it; the server's EXP code honours it when it is there."))),
        hours, pct,
        h("p", { class: "gw-dim" }, "Every member gets one boost (character_experience_modifiers, kind 100). Its time counts only while the player is online. It adds to potions and events; a kill still gives at most what the server's caps allow."),
        h("div", { class: "gw-confirm-buttons" },
          h("button", { class: "gw-btnx", type: "button", disabled: !this.ov.writable || null, onclick: async () => {
            if (!hours.valid() || !pct.valid()) return;
            const r = await write(this, "g/" + g.id + "/boost", { hours: hours.value, percent: pct.value }, "guild boost given", { confirm: { title: "Give the boost?", message: "+" + fmt(pct.value) + "% EXP for " + hours.value + " online hours to all " + d.members.length + " member(s) of " + g.name + "." } });
            if (r) this.openGuild(g.id, "boost");
          } }, "Give to every member"),
          h("button", { class: "gw-btnx danger", type: "button", disabled: !this.ov.writable || !d.boosts.length || null, onclick: async () => {
            const r = await write(this, "g/" + g.id + "/boost", { remove: true }, "guild boost removed");
            if (r) this.openGuild(g.id, "boost");
          } }, "Remove"), " ", GW.appliesChip("next_login"))),
        h("table", { class: "gwg-members" }, h("thead", null, h("tr", null, h("th", null, "Member"), h("th", { "data-field": "character_experience_modifiers.bonus_basis_points" }, "Bonus"),
          h("th", { "data-field": "character_experience_modifiers.remaining_online_ticks" }, "Time left"), h("th", { "data-field": "character_experience_modifiers.source" }, "Given by"))), rows));
    },

    // ============================================================================================ Faction areas
    async factionsTab() {
      this.hash("factions");
      let f;
      try { f = await this.ctx.api("factions"); } catch (e) { this.body.append(h("div", { class: "tt-bad" }, e.message)); return; }
      const realm = h("select", { class: "gwg-input", "aria-label": "Realm" }, f.realms.map(r => h("option", { value: r.id }, r.name + " (#" + r.id + ")")));
      const tb = h("tbody");
      const draw = () => {
        GW.clear(tb);
        const rid = Number(realm.value);
        for (const a of f.areas) {
          const c = f.control.find(x => x.map_id === a.map_id && x.realm_id === rid);
          const hours = h("input", { class: "gwg-input gwg-small", type: "number", min: 1, max: 8760, value: 12, "aria-label": "hours" });
          const pct = h("input", { class: "gwg-input gwg-small", type: "number", min: 1, max: 1000000, value: Math.round(a.bonus_basis_points / 100), "aria-label": "EXP bonus %" });
          const go = camp => async () => {
            const r = await write(this, "faction/control", { realm: rid, map: a.map_id, camp, hours: Number(hours.value), percent: Number(pct.value) }, "faction area " + a.map_name);
            if (r) this.show("factions");
          };
          tb.append(h("tr", { class: a.published ? "" : "gwg-bad" },
            h("td", { class: "gw-value", "data-tip-type": "map", "data-tip-id": String(a.map_id) }, a.map_name, h("div", { class: "gw-dim" }, "map " + a.map_id)),
            h("td", { "data-help": a.boss_template_key + (a.published ? "" : " - not in the published world-boss content: the bonus would not apply") }, a.boss_display_name),
            h("td", null, c ? [campIcon(c.controlling_camp), " ", CAMP[c.controlling_camp], c.active ? "" : h("span", { class: "gw-dim" }, " (expired)")] : h("span", { class: "gw-dim" }, "nobody")),
            h("td", null, c ? "+" + fmt(c.bonus_basis_points / 100) + "%" : ""), h("td", { class: "gw-dim" }, c ? when(c.expires_at) : ""),
            h("td", { class: "gwg-acts" }, hours, " h ", pct, " % ",
              h("button", { class: "gw-btnx small", type: "button", disabled: !f.writable || null, onclick: go(1) }, "Athens"),
              h("button", { class: "gw-btnx small", type: "button", disabled: !f.writable || null, onclick: go(0) }, "Sparta"),
              c ? h("button", { class: "gw-btnx small danger", type: "button", disabled: !f.writable || null, onclick: go(null) }, "Clear") : null)));
        }
      };
      realm.addEventListener("change", draw);
      draw();
      this.body.append(h("section", { class: "gw-frame-normalwindow gw-win" }, h("div", { class: "gw-wtitle" }, "Faction areas (world bosses)"),
        h("p", { class: "gwg-pad" }, "When a world boss dies, the killer's camp gets an EXP bonus on that map until the boss respawns (12 hours in the server's content). Here you can hand an area to a camp, change the bonus and the time, or clear it. ",
          "The server reads it at every reward, so it works at once, also for players who are online. ", GW.appliesChip("now")),
        h("div", { class: "gwg-pad" }, h("label", { "data-field": "faction_area_experience_control.realm_id" }, "Realm "), realm,
          h("span", { class: "gw-dim" }, "  Camps now: ", f.camps.map(c => c.name + " " + c.characters + " characters (" + c.in_guilds + " in guilds)").join(" · "))),
        h("div", { class: "gwg-scroll" }, h("table", { class: "gwg-members" }, h("thead", null, h("tr", null,
          h("th", null, "Map"), h("th", { "data-field": "world_boss_areas.boss_display_name" }, "Boss"), h("th", { "data-field": "faction_area_experience_control.controlling_camp" }, "Held by"),
          h("th", { "data-field": "faction_area_experience_control.bonus_basis_points" }, "Bonus"), h("th", { "data-field": "faction_area_experience_control.expires_at" }, "Until"), h("th", null, "Set"))), tb))));
    },

    // ============================================================================================ systems
    systemsTab() {
      this.hash("systems");
      const tb = h("tbody");
      for (const s of this.ov.systems) {
        const [cls, word] = STATE[s.state] || ["never", s.state];
        tb.append(h("tr", null, h("td", { class: "gw-value" }, s.name), h("td", null, h("span", { class: "gw-chip " + cls }, word)), h("td", null, s.server), h("td", { class: "gw-dim" }, s.official), h("td", null, s.editor)));
      }
      this.body.append(h("section", { class: "gw-frame-normalwindow gw-win" }, h("div", { class: "gw-wtitle" }, "Guild and social systems: what the server does"),
        h("p", { class: "gwg-pad" }, "From the reborn server's source and the official-game survey (Official-Site/SCOPE.md). Friends, mail, marriage and master/apprentice have no table in the database, and parties live only in the game server's memory, so there is nothing for the editor to change there."),
        h("div", { class: "gwg-scroll" }, h("table", { class: "gwg-members gwg-sys" }, h("thead", null, h("tr", null, h("th", null, "System"), h("th", null, "Reborn"), h("th", null, "What the server does"), h("th", null, "Official game"), h("th", null, "In this editor"))), tb))));
    },

    // ============================================================================================ checks
    checksTab() {
      this.hash("checks");
      const KIND = { desync: "Character columns out of step", orphan: "Marked as in a guild without a membership", lord: "Leader problem", building: "Building outside the rules", full: "More members than the limit", roster: "Window lists only 20" };
      const box = h("section", { class: "gw-frame-normalwindow gw-win" }, h("div", { class: "gw-wtitle" }, "Checks"));
      const iss = this.ov.issues || [];
      if (!iss.length) box.append(h("p", { class: "gwg-pad gw-good" }, "Every guild keeps the rules: memberships and the characters' own guild columns agree, every guild has exactly its leader at duty 6, buildings are in range."));
      const tb = h("tbody");
      for (const i of iss) {
        tb.append(h("tr", null, h("td", null, KIND[i.kind] || i.kind), h("td", { class: "gw-value" }, i.who), h("td", null, i.what),
          h("td", { class: "gwg-acts" },
            (i.kind === "desync" || i.kind === "orphan") ? h("button", { class: "gw-btnx small", type: "button", disabled: !this.ov.writable || null,
              "data-help": "Write the character's consortia / consortia_job / guild_duty from its membership (or 0 when it has none). The character must be logged out.",
              onclick: async () => { const r = await write(this, "repair/" + i.cid, {}, "repaired " + i.who); if (r) this.refresh(); } }, "Repair") : null,
            i.guild && this.ov.guilds.find(g => g.id === i.guild) ? h("button", { class: "gw-btnx small", type: "button", onclick: () => { this.gid = i.guild; this.sub = i.kind === "building" ? "buildings" : i.kind === "lord" ? "info" : "members"; this.show("guilds"); } }, "Open guild") : null)));
      }
      if (iss.length) box.append(h("div", { class: "gwg-scroll" }, h("table", { class: "gwg-members" }, h("thead", null, h("tr", null, h("th", null, "Problem"), h("th", null, "Who"), h("th", null, "Details"), h("th", null, ""))), tb)));
      box.append(h("p", { class: "gwg-pad gw-dim" }, "The game writes a member's duty twice: on the membership (the guild window) and on the character (the character panel and the name tag). Every guild edit here writes both and refuses to save when they would disagree."));
      this.body.append(box);
    },
  });
})();
