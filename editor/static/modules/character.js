/* static/modules/character.js - the "Character" page: the in-game character window (C) with every stored value
   editable, the wallet, talents, zodiac, a map + minimap position picker, and the character's ACCOUNT (new accounts,
   rename, password, e-mail, donator tier, ban, battle pass, characters, delete).
   Backend: modules/character.py. Contract: MODULES.md. Database and client text is inserted as text, never HTML. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const F = c => "character_base." + c;
  const CAMP_CLASS = { 0: "gw-camp-sparta", 1: "gw-camp-athens" };
  const LIVE_LABEL = "applied now by the running server";
  const fmt = n => (n === null || n === undefined || n === "") ? "" : Number(n).toLocaleString("en-US");
  const num = v => (v === null || v === undefined || v === "") ? null : Number(v);
  // Columns compared as text. Number("TestChar") is NaN, and String(NaN) === String(NaN) made every rename (and gender
  // change) look like "no change": set() dropped it and nothing was saved (the admin 2026-11-02, "renaming doesn't work").
  const TEXT_COLS = new Set(["name", "gender"]);
  function sameValue(col, orig, v) {
    if (v === null || v === undefined) return orig === null || orig === undefined;
    if (orig === null || orig === undefined) return false;
    if (orig === v) return true;
    if (TEXT_COLS.has(col)) return String(orig) === String(v);
    const a = num(orig), b = num(v);
    if (Number.isFinite(a) && Number.isFinite(b)) return a === b;   // "10000" from an input equals the stored 10000
    return String(orig) === String(v);
  }
  function when(t) { if (!t) return "never"; const d = new Date(t); return isNaN(d) ? String(t) : d.toLocaleString(); }

  // ---------------------------------------------------------------- minimap projection (MAP-FORMATS.md s.8, 512 px space)
  function scales(xi, zi) { return zi >= 0 ? [0.57, 0.54] : (xi <= 0 ? [0.57, 0.57] : [0.58, 0.57]); }
  function w2m(x, z) {
    const xi = Math.trunc(x), zi = Math.trunc(z), s = scales(xi, zi), X = xi * s[0], Z = zi * s[1];
    return [256 + (X + Z) / Math.SQRT2, 260 + (X - Z) / Math.SQRT2];
  }
  function m2w(px, py) {
    const a = px - 256, b = 260 - py, X = (a - b) / Math.SQRT2, Z = (a + b) / Math.SQRT2;
    const s = scales(X, Z);
    return [Math.max(-256, Math.min(256, X / s[0])), Math.max(-256, Math.min(256, Z / s[1]))];
  }

  GW.module("character", {
    async mount(el, ctx) {
      this.el = el; this.ctx = ctx; this.cid = ctx.cid;
      this.pending = {};            // column -> new value
      this.fields = {};             // column -> {el, live, editable}
      let saved = null;
      try { saved = sessionStorage.getItem("gw-ch-tab"); } catch (e) { /* storage blocked */ }
      this.tab = ["sheet", "position", "account", "diagnostics"].includes(ctx.route[0]) ? ctx.route[0] : (["sheet", "position", "account", "diagnostics"].includes(saved) ? saved : "sheet");
      this.banner = GW.onlineBanner(ctx.cid, { onState: st => { this.online = st; this.applyLock(); } });
      this.tabs = h("div", { class: "gw-ch-tabs", role: "tablist" });
      this.body = h("div", { class: "gw-ch-body" });
      this.bar = GW.saveBar({ title: "Save these changes?", onSave: () => this.save(), onDiscard: () => this.reload() });
      this.last = h("div", { class: "gw-ch-last" });
      el.append(this.banner, this.tabs, this.last, this.body, this.bar.el);
      this.offChange = GW.on("change", () => { if (this.tab === "account" && this.acct) this.showAccount(this.acct.id, true); });
      await this.reload();
    },
    unmount() { if (this.offChange) this.offChange(); },

    // ---------------------------------------------------------------- data
    async reload() {
      try { this.d = await this.ctx.api("sheet/" + this.cid); }
      catch (e) { GW.clear(this.body).append(h("div", { class: "tt-bad" }, e.message)); return; }
      this.online = this.d.online;
      this.pending = {};
      this.bar.clear();
      this.drawTabs();
      this.draw();
    },
    drawTabs() {
      const T = [["diagnostics", "Login health", "Check saved character data and recent session endings."], ["sheet", "Character", "The character window (C): faith, title, honour, level, EXP, HP / MP, wallet, talents, zodiac, looks."],
        ["position", "Position", "Where the character is: the map and the X / Z point, picked on the client's minimap."],
        ["account", "Account", "The login this character belongs to, every other account, new accounts, password, donator tier, battle pass, ban and delete."]];
      GW.clear(this.tabs).append(...T.map(([k, label, help]) => h("button", { class: "gw-tabx" + (this.tab === k ? " on" : ""), type: "button", role: "tab",
        "aria-selected": this.tab === k ? "true" : "false", "data-help": help, onclick: () => { if (this.tab === k) return; this.tab = k; try { sessionStorage.setItem("gw-ch-tab", k); } catch (e) { /* storage blocked */ } history.replaceState(null, "", "#/character/" + this.cid + "/" + k); this.drawTabs(); this.draw(); } }, label)));
    },
    draw() {
      this.fields = {};
      GW.clear(this.body);
      if (!this.d.writable) this.body.append(h("div", { class: "gw-online warn" }, "This editor is read-only for database " + this.d.database + ": you can look, not change."));
      if (this.tab === "diagnostics") this.drawDiagnostics();
      else if (this.tab === "position") this.drawPosition();
      else if (this.tab === "account") this.drawAccountTab();
      else this.drawSheet();
      this.applyLock();
    },

    async drawDiagnostics() {
      const box = h("div", { class: "gw-ch-panel" }, "Checking character data…");
      this.body.append(box);
      try {
        const d = await this.ctx.api("diagnostics/" + this.cid);
        if (this.tab !== "diagnostics" || !box.isConnected) return;
        GW.clear(box).append(h("h3", {}, "Login health"),
          h("button", { class: "gw-btnx", type: "button", onclick: () => this.draw() }, "Refresh"),
          h("p", {}, d.note), h("p", {}, d.recovery), h("h4", {}, "Saved data checks"));
        if (!d.findings.length) box.append(h("p", {}, "No structural problems found. This does not guarantee native-client compatibility."));
        for (const f of d.findings) box.append(h("p", { class: f.severity === "error" ? "tt-bad" : "gw-dim" }, (f.severity || "warning") + " · " + (f.where || "") + ": " + f.msg));
        box.append(h("h4", {}, "Recent editor changes"));
        if (!d.recent_edits.length) box.append(h("p", {}, "No recent editor changes found for this character."));
        for (const change of d.recent_edits) {
          const undo = change.can_undo ? h("button", { class: "gw-btnx small", type: "button", onclick: async () => {
            if (!await GW.confirm({ title: "Undo this specific edit?", message: change.action + "\n\nA backup is saved first. The server refuses Undo if the character is online or related data changed. This does not restore an entire character.", ok: "Undo edit" })) return;
            undo.disabled = true;
            try { const result = await GW.api("/api/undo", { change: change.id }); this.done(result); await this.reload(); }
            catch (e) { GW.status(e.message, "err"); }
            finally { undo.disabled = false; }
          } }, "Undo edit") : null;
          box.append(h("div", { class: "gw-frame-simplewindow gw-ch-panel" },
            h("b", {}, change.action), h("p", { class: "gw-dim" }, change.time + " UTC · " + change.id + (change.undone ? " · undone" : "")), undo));
        }
        box.append(h("h4", {}, "Recent session endings (newest first)"));
        if (!d.sessions.length) box.append(h("p", {}, "No session-end records found in the available log tail."));
        for (const r of d.sessions) box.append(h("div", { class: "gw-frame-simplewindow gw-ch-panel" },
          h("p", {}, new Date(r["utc-seconds"] * 1000).toLocaleString()),
          h("b", {}, r.ended_before_ready ? "Ended before world-ready" : "Reached world-ready"),
          h("p", {}, "Reason: " + r.reason + "; error: " + r.error + "; transport: " + r["transport-error"] + "; duration: " + (r["duration-ms"] / 1000).toFixed(1) + " s"),
          h("p", {}, "Last received opcode " + r["last-opcode"] + "; last completed send " + r["last-send-opcode"] + " (" + r["last-send-bytes"] + " bytes). Client ready: " + r["client-ready"] + "; UI ready: " + r["ui-ready"] + ".")));
      } catch (e) { GW.clear(box).append(h("p", { class: "tt-bad" }, e.message)); }
    },

    // ---------------------------------------------------------------- edit plumbing
    // which columns can be edited right now: offline -> all editable ones; online -> only the ones the server changes live
    canEdit(col) {
      col = { points: "Pos_X", fighter_job_lv_quick: "fighter_job_lv" }[col] || col;     // helper controls of a column
      const f = this.d.fields[col];
      if (!this.d.writable || !f || !f.editable) return { ok: false, why: f && f.why ? "Read-only: " + f.why : "read-only" };
      const st = this.online || {};
      if (!st.online) return { ok: true };
      if ((st.live_fields || []).includes(col)) return { ok: true, live: true };
      return { ok: false, why: st.capabilities ? "The game server cannot change this live: log the character out to edit it (Kick on the banner)."
        : "Live edits arrive with the round-2 server update; this character must be logged out to edit this." };
    },
    applyLock() {
      if (!this.d) return;
      for (const [col, f] of Object.entries(this.fields)) {
        const c = this.canEdit(col);
        const el = f.el;
        el.classList.toggle("gw-ch-locked", !c.ok);
        for (const x of el.querySelectorAll("input, select, button.gw-ch-edit")) x.disabled = !c.ok;
        if (!c.ok) el.dataset.help = c.why; else delete el.dataset.help;
        if (f.liveChip) f.liveChip.classList.toggle("hidden", !c.live);
      }
    },
    reg(col, el) {
      const liveChip = h("span", { class: "gw-chip now hidden", "data-help": "The character is online and the running game server applies this field at once: the player sees it in game." }, "live");
      el.append(liveChip);
      this.fields[col] = { el, liveChip };
      return el;
    },
    set(col, v) {
      const orig = this.d.base[col];
      if (sameValue(col, orig, v)) { delete this.pending[col]; this.bar.unset(col); }
      else {
        this.pending[col] = v;
        const f = this.d.fields[col] || {};
        const c = this.canEdit(col);
        this.bar.set(col, { label: f.label || col, from: this.show(col, orig), to: this.show(col, v), field: F(col), applies: c.live ? "live" : "next_login" });
      }
    },
    show(col, v) {
      if (v === null || v === undefined) return "(none)";
      const n = this.d.names;
      if (col === "Map") return (GW.catalog.loadedRow("map", v) || {}).name ? GW.catalog.loadedRow("map", v).name + " #" + v : "#" + v;
      if (col === "belief") return ((GW.catalog.loadedRow("faith", v) || {}).name || "faith") + " #" + v;
      if (col === "zodiac_type") return ((GW.catalog.loadedRow("zodiac_sign", v) || {}).name || "sign") + " #" + v;
      if (col === "selected_title_id") return Number(v) ? ((GW.catalog.loadedRow("title", v) || {}).name || "title") + " #" + v : "none";
      if (col === "fighter_level_sealed") return v ? "sealed" : "not sealed";
      if (typeof v === "boolean" || typeof v === "string" && isNaN(Number(v))) return String(v);
      void n;
      return Number(v);
    },
    // a number field bound to a character_base column (glossary range, help, applies chip, live chip)
    nf(col, opts) {
      const o = Object.assign({ field: F(col), value: num(this.d.base[col]), onChange: (v, ok) => { if (ok && v !== null) this.set(col, v); else if (v === null) this.set(col, this.d.base[col]); if (o.after) o.after(v, ok); } }, opts || {});
      const preciseCounter = ["fighter_job_exp", "Money", "Stone", "BindingGold", "medusa_honor_points", "exchange_point", "exchange_medal", "SkillPoint", "zodiac_energy", "consortia_contribute"].includes(col);
      if (preciseCounter) o.slider = false;
      const el = GW.numberField(o);
      if (preciseCounter) {
        let expanded = false;
        const toggle = h("button", { class: "gw-btnx small", type: "button", "data-help": "Enter exact values in the number box. Full-range sliders can make large changes quickly. Displayed limits describe supported storage/wire values, not recommended progression targets.", onclick: () => { expanded = !expanded; el.setSlider(expanded); toggle.textContent = expanded ? "Hide slider" : "Show slider"; } }, "Show slider");
        el.append(toggle);
      }
      el.classList.add("gw-ch-nf");
      return this.reg(col, el);
    },
    async save() {
      const vals = Object.assign({}, this.pending);
      if (!Object.keys(vals).length) return null;
      const expected = {};
      for (const c of Object.keys(vals)) expected[c] = this.d.base[c];
      const adv = Object.keys(vals).some(c => (this.d.fields[c] || {}).advanced);
      const r = await this.ctx.api("save/" + this.cid, { values: vals, expected, advanced: adv });
      this.done(r);
      await this.reload();
      return null;
    },
    done(r, label) {
      if (!r || r.mode === "none") { GW.status((r && r.note) || "nothing changed"); return; }
      GW.writeDone(r, label);
      const c = r.change;
      GW.clear(this.last);
      if (!c) return;
      const live = r.mode === "live" || c.engine === "reborn-live";
      this.last.append(h("span", { class: "gw-chip " + (live ? "now" : "login") }, live ? "applied live" : (r.applies_text || "saved")), " ",
        h("span", { class: "gw-ch-last-what" }, c.action || ""), " ",
        c.no_undo ? h("span", { class: "gw-dim", "data-help": c.no_undo }, "(no automatic Undo: make the opposite edit)")
          : h("button", { class: "gw-btnx small", type: "button", "data-help": "Put back what this save changed (the change log keeps every change, newest first).",
            onclick: async () => { await GW.changes.undo(c, false); GW.clear(this.last); if (this.tab !== "account") this.reload(); else this.showAccount(this.acct.id, true); } }, "Undo"),
        " ", h("button", { class: "gw-btnx small", type: "button", onclick: () => GW.changes.open() }, "Change log"));
    },

    appliesLine() {
      return h("div", { class: "gw-ch-applies" }, GW.appliesChip("next_login", "changes apply at the next login"), " ",
        h("span", { class: "gw-dim", "data-help": "Fields marked 'live' are changed by the running game server while the player is online (round-2 server). Everything else is written to the database and the game reads it when the character next enters the world." }, "(or live where marked)"));
    },
    // ---------------------------------------------------------------- the character window
    drawSheet() {
      const d = this.d, b = d.base, self = this;
      const row = (label, field, ...kids) => h("div", { class: "gw-ch-row" }, h("span", { class: "gw-label", "data-field": field ? F(field) : null }, label), h("span", { class: "gw-value" }, ...kids));
      const panel = (title, ...kids) => h("div", { class: "gw-frame-simplewindow gw-ch-panel" }, title ? h("div", { class: "gw-ch-ptitle" }, title) : null, ...kids);

      // ---- name (title of the window) + rename
      const nameBtn = h("button", { class: "gw-btnx small gw-ch-edit", type: "button", "data-help": "Rename the character (1-31 ASCII letters, digits, _ - . or inner spaces; unique without regard to case). Needs the character logged out. Then press Review & save." }, "Rename");
      nameBtn.addEventListener("click", async () => {
        // 2026-11-05 (the admin: "the editor can't rename a character"): the rename itself works end to end (tests/test_rename_e2e.py:
        // database, Undo and a headless login that enters the world with the new name). What failed for people was silent:
        // an online character, an invalid name or a forgotten Review & save. Each of those is now said in red, up front.
        if (this.online && this.online.online) {
          await GW.alert("|cFFFF4040" + b.name + " is online.|cFFFFFFFF\n\nA name can only be changed while the character is logged out (also not at the character screen). " +
            "Use 'Kick to edit everything' on the online banner, wait until it says offline, then rename.", "Cannot rename now");
          return;
        }
        const RX = /^[A-Za-z0-9_\-.](?:[A-Za-z0-9_\-. ]{0,29}[A-Za-z0-9_\-.])?$/;   // = modules/character.py CHAR_NAME_RX
        const inp = h("input", { class: "gw-ch-input", value: this.pending.name || b.name, maxlength: 31, "aria-label": "New name" });
        const warn = h("p", { class: "gw-dim" }), bad = h("p", { class: "tt-bad", role: "alert" });
        const check = () => {
          const s = inp.value.trim();
          warn.textContent = s.length > 8 ? "Longer than the game's own create box allows (8): the server accepts it, but it may be cut off in narrow windows." : "";
          bad.textContent = !s ? "Type a name." : !RX.test(s) ? "Not allowed: use 1-31 ASCII letters, digits, _ - . and spaces between words (no other symbols, no accents)." :
            s === b.name ? "That is the current name." : "";
          return !bad.textContent;
        };
        inp.addEventListener("input", check); check();
        const ok = await GW.confirm({ title: "Rename " + b.name, el: h("div", { class: "gw-mod-character" }, h("p", null, "New name:"), inp, bad, warn,
          h("p", { class: "gw-dim" }, "Next step: press |Review & save| at the bottom of the page. Names are unique without regard to case; a taken name is refused there with the holder's name. Other players see the new name after the character next enters the world.".replace(/\|/g, ""))), ok: "Use this name" });
        if (!ok) return;
        if (!check()) { await GW.alert(bad.textContent + "\n\nNothing was changed.", "Name not accepted"); return; }
        this.set("name", inp.value.trim()); nameTxt.textContent = inp.value.trim() + "  (not saved yet)";
        GW.status("Rename to " + inp.value.trim() + " is pending: press Review & save to write it.", "err");
      });
      const nameTxt = h("span", null, b.name);
      const nameRow = this.reg("name", h("div", { class: "gw-ch-name", "data-field": F("name") }, nameTxt, " ", nameBtn));

      // ---- status panel: faith / class / repu / title | honour / points / medals
      const faith = GW.idField("faith", num(b.belief), { field: F("belief"), title: "Faith", onChange: id => this.set("belief", id === null || id === "" ? 255 : Number(id)) });
      const titleBtn = h("button", { class: "gw-sprite gw-btn2-small gw-ch-edit gw-ch-titlebtn", type: "button", "data-help": "Pick the title shown above the character's head. Only titles the character owns can be shown (Medusa, Atlantis and Wonderland rewards)." }, "Title");
      const titleTxt = h("span", null, d.names.title || "none");
      titleBtn.addEventListener("click", async () => {
        const owned = new Set((d.titles_owned || []).map(String));
        if (!owned.size) { await GW.alert(b.name + " owns no titles yet.\n\nTitles are earned in Medusa, Atlantis and Wonderland runs; the server only shows a title the character owns, so there is nothing to pick.", "Titles"); return; }
        const id = await GW.pick("title", { title: "Titles " + b.name + " owns", value: num(b.selected_title_id) || null, filter: r => owned.has(String(r.id)), allowNone: true, noneLabel: "No title" });
        if (id === null) return;
        const v = id === "" ? 0 : Number(id);
        this.set("selected_title_id", v);
        titleTxt.textContent = v ? ((await GW.catalog.row("title", v)) || {}).name || ("#" + v) : "none";
      });
      const status = panel(null,
        h("div", { class: "gw-ch-split" },
          h("div", null,
            this.reg("belief", h("div", { class: "gw-ch-row" }, h("span", { class: "gw-label", "data-field": F("belief") }, "Faith"), faith)),
            row("Class", "profession", h("span", { class: "gw-ch-ro", "data-help": "Read-only: " + (d.fields.profession.why || "") }, d.names.class), " ",
              h("span", { class: "gw-ch-ro", "data-field": F("camp") }, "· " + d.names.camp)),
            row("Repu", "prestige", h("span", { class: "gw-ch-ro", "data-help": "Prestige " + fmt(b.prestige) + ". Read-only: " + (d.fields.prestige.why || "not read by this server") }, fmt(b.prestige))),
            this.reg("selected_title_id", h("div", { class: "gw-ch-row" }, titleBtn, titleTxt))),
          h("div", { class: "gw-ch-camp" }, h("span", { class: "gw-sprite " + (CAMP_CLASS[b.camp] || ""), "data-field": F("camp"), role: "img", "aria-label": d.names.camp }))),
        h("div", { class: "gw-ch-cols3" },
          this.nf("medusa_honor_points", { label: "Honor" }),
          this.nf("exchange_point", { label: "Point" }),
          this.nf("exchange_medal", { label: "Medal" })));

      // ---- guild panel (read-only here: the Guild editor writes guild membership with its rows)
      const g = d.guild;
      const guild = panel(null,
        row("Guild", "consortia", g ? h("a", { href: "#/guild/" + this.cid, "data-help": "Guild membership is written with the guild's own rows: change it in the Guild editor." }, g.name + " (Lv " + g.level + ")") : h("span", { class: "gw-dim" }, "no guild")),
        row("Position", "guild_duty", h("span", { class: "gw-ch-ro" }, d.names.guild_duty || "None")),
        this.nf("consortia_contribute", { label: "Contribution" }));

      // ---- level panel
      const L = d.level;
      const expBarFill = h("span", { class: "gw-ch-expfill" });
      const expBar = h("span", { class: "gw-ch-expbar", "data-help": "EXP towards the next level, as the game's EXP bar shows it." }, expBarFill);
      const nextTxt = h("span", null);
      let curLv = num(b.fighter_job_lv), curSealed = !!b.fighter_level_sealed, curExp = num(b.fighter_job_exp);
      const nextOf = () => (curSealed || curLv >= L.cap) ? L.uint32 : d.exp_table[Math.max(1, curLv) - 1];
      let expF = null;
      // the running server's live level change resets EXP to 0 unless EXP is sent too: show that before the save
      const liveLvExp = () => { if (this.canEdit("fighter_job_lv").live && !("fighter_job_exp" in this.pending) && curExp) { curExp = 0; expF.value = 0; this.set("fighter_job_exp", 0); } };
      const upd = () => {
        const nx = nextOf();
        nextTxt.textContent = (curSealed ? "sealed: no more levels; EXP stores up to " + fmt(nx) : curLv >= L.cap ? "level cap " + L.cap + ": EXP stores up to " + fmt(nx) : fmt(nx) + " EXP to reach level " + (curLv + 1));
        expBarFill.style.width = Math.max(0, Math.min(100, (curExp || 0) / nx * 100)) + "%";
        if (expF) expF.setRange(0, (curSealed || curLv >= L.cap) ? nx : nx - 1);
      };
      const lvF = this.nf("fighter_job_lv", { label: "Level", min: 1, max: L.cap, after: (v, ok) => {
        if (!ok || v === null) return;
        curLv = v;
        const nx = nextOf();
        if (!(curSealed || curLv >= L.cap) && curExp >= nx) { curExp = nx - 1; expF.value = curExp; this.set("fighter_job_exp", curExp); }
        liveLvExp();
        upd();
      } });
      expF = this.nf("fighter_job_exp", { label: "Exp", min: 0, max: nextOf() - ((curSealed || curLv >= L.cap) ? 0 : 1), after: (v, ok) => { if (ok && v !== null) { curExp = v; upd(); } } });
      const sealBox = h("button", { class: "gw-sprite gw-check gw-ch-edit" + (curSealed ? " on" : ""), type: "button", role: "checkbox", "aria-checked": curSealed ? "true" : "false", "aria-label": "Level sealed" });
      sealBox.addEventListener("click", () => { curSealed = !curSealed; sealBox.classList.toggle("on", curSealed); sealBox.setAttribute("aria-checked", curSealed ? "true" : "false"); this.set("fighter_level_sealed", curSealed); upd(); });
      const seal = this.reg("fighter_level_sealed", h("div", { class: "gw-ch-row" }, sealBox, h("span", { class: "gw-label", "data-field": F("fighter_level_sealed") }, "Level seal"), h("span", { class: "gw-dim" }, " the character stops gaining levels")));
      const lvQuick = h("span", { class: "gw-ch-quick" }, [1, 30, 50, 80, 100, 120, 150, 200].map(n => h("button", { class: "gw-chipbtn gw-ch-edit", type: "button", "data-help": "Set level " + n, onclick: () => { lvF.value = n; curLv = n; this.set("fighter_job_lv", n); const nx = nextOf(); if (!(curSealed || curLv >= L.cap) && curExp >= nx) { curExp = 0; expF.value = 0; this.set("fighter_job_exp", 0); } liveLvExp(); upd(); } }, String(n))));
      this.fields["fighter_job_lv_quick"] = { el: lvQuick };
      const level = panel(null, lvF, lvQuick, expF, h("div", { class: "gw-ch-row" }, h("span", { class: "gw-label", "data-help": "EXP the character needs for the next level (reborn PlayerExperienceCatalog: level 1 needs 200, level 199 needs 584,435,250; level cap " + L.cap + ")." }, "Next"), h("span", { class: "gw-value" }, nextTxt)), expBar, seal);
      upd();

      // ---- vitals: HP / MP bars + values
      const bar = (cls, cur, max) => h("span", { class: "gw-ch-vbar" }, h("span", { class: "gw-sprite " + cls, style: "width:" + Math.max(2, Math.min(116, Math.round(116 * (Number(cur) || 0) / Math.max(1, Number(max) || 1)))) + "px" }),
        h("span", { class: "gw-ch-vtxt gw-outline" }, fmt(cur) + " / " + fmt(max)));
      const vitals = panel(null,
        h("div", { class: "gw-ch-row" }, h("span", { class: "gw-label", "data-field": F("curHP") }, "HP"), bar("gw-bar-hp", b.curHP, b.MaxHP)),
        h("div", { class: "gw-ch-row" }, h("span", { class: "gw-label", "data-field": F("curMP") }, "MP"), bar("gw-bar-mp", b.curMP, b.MaxMP)),
        this.nf("curHP", { label: "HP now" }), this.nf("curMP", { label: "MP now" }),
        this.nf("MaxHP", { label: "Base max HP" }), this.nf("MaxMP", { label: "Base max MP" }),
        h("p", { class: "gw-dim gw-ch-note" }, "ATK, DEF, Hit, Dodge, Crit and the other combat numbers of the game window are not stored: the server works them out from level, gear, talents and buffs."));

      const charWin = h("section", { class: "gw-frame-normalwindow gw-win gw-ch-charwin" }, h("div", { class: "gw-wtitle" }, "Character"), this.appliesLine(),
        nameRow, status, guild, level, vitals);

      // ---- wallet
      const quick = (col, label) => h("span", { class: "gw-ch-quick" }, [["+10k", 1e4], ["+1M", 1e6], ["+100M", 1e8], ["Max", "max"], ["0", 0]].map(([t, v]) =>
        h("button", { class: "gw-chipbtn gw-ch-edit", type: "button", "data-help": v === "max" ? "the most the database holds (2,147,483,647)" : (v === 0 ? "empty" : "add " + t.slice(1)),
          onclick: () => { const f = this.fields[col].el; const cur = Number(f.value || 0); let n = v === "max" ? 2147483647 : v === 0 ? 0 : Math.min(2147483647, cur + v); f.value = n; this.set(col, n); } }, t)));
      const money = (col, coin, label) => { const f = this.nf(col, { label, unit: null }); f.prepend(h("span", { class: "gw-sprite " + coin, "aria-hidden": "true" })); return h("div", { class: "gw-ch-money" }, f, quick(col, label)); };
      const wallet = h("section", { class: "gw-frame-normalwindow gw-win gw-ch-wallet" }, h("div", { class: "gw-wtitle" }, "Wallet"), this.appliesLine(),
        money("Money", "gw-coin-silver", "Silver"), money("Stone", "gw-coin-gold", "Gold"), money("BindingGold", "gw-coin-gold", "B-Gold"),
        h("p", { class: "gw-dim gw-ch-note" }, "The database holds at most 2,147,483,647 of each. Online with the round-2 server, Silver / Gold / B-Gold change live in the player's bag window."));

      // ---- talents
      const tExp = num(b.SkillExp) || 0;
      const talents = h("section", { class: "gw-frame-normalwindow gw-win gw-ch-talents" }, h("div", { class: "gw-wtitle" }, "Talents"), this.appliesLine(),
        this.nf("SkillPoint", { label: "Talent points" }),
        h("p", { class: "gw-dim gw-ch-note" }, "The client's talent window shows at most 60 points per tree; spare points stay stored."),
        this.nf("SkillExp", { label: "Talent EXP", min: 0, max: d.talent_exp_per_point - 1 }),
        h("span", { class: "gw-ch-expbar small", "data-help": "Talent EXP: 100 becomes one talent point." }, h("span", { class: "gw-ch-expfill", style: "width:" + tExp + "%" })));

      // ---- zodiac
      const Z = d.zodiac;
      let zSign = num(b.zodiac_type) || 0, zLv = num(b.zodiac_level) || 1;
      const pic = h("img", { class: "gw-ch-zpic", alt: "", src: "/static/ui/zodiac/" + String(zSign).padStart(2, "0") + ".png" });
      const signName = h("div", { class: "gw-ch-zname" }, d.names.zodiac);
      const signs = h("div", { class: "gw-ch-zsigns" });
      GW.catalog.load("zodiac_sign").then(ix => {
        for (const r of ix.rows || Array.from(ix.byId.values())) {
          const id = Number(r.id);
          signs.append(h("button", { class: "gw-ch-zsign gw-ch-edit" + (id === zSign ? " on" : ""), type: "button", "data-tip-type": "zodiac_sign", "data-tip-id": String(id), "aria-label": r.name,
            onclick: ev => { zSign = id; for (const x of signs.children) x.classList.remove("on"); ev.currentTarget.classList.add("on"); pic.src = "/static/ui/zodiac/" + String(id).padStart(2, "0") + ".png"; signName.textContent = r.name; this.set("zodiac_type", id); } },
            h("img", { src: "/static/ui/zodiac/" + String(id).padStart(2, "0") + ".png", alt: "" }), h("span", null, r.name)));
        }
        this.applyLock();
      }).catch(() => {});
      const stars = h("div", { class: "gw-ch-stars", role: "radiogroup", "aria-label": "Zodiac level" });
      const capTxt = h("span", { class: "gw-dim" });
      let enF = null;
      const drawStars = () => {
        GW.clear(stars);
        for (let i = 1; i <= Z.levels; i++) stars.append(h("button", { class: "gw-sprite gw-ch-edit " + (i <= zLv ? "gw-zodiac-star-on" : "gw-zodiac-star-off"), type: "button", role: "radio", "aria-checked": i === zLv ? "true" : "false",
          "aria-label": "Zodiac level " + i, "data-help": "Zodiac level " + i + ": holds up to " + fmt(Z.caps[i - 1]) + " zodiac energy",
          onclick: () => { zLv = i; this.set("zodiac_level", i); drawStars(); } }));
        capTxt.textContent = "Level " + zLv + " holds up to " + fmt(Z.caps[zLv - 1]) + " energy.";
        if (enF) { enF.setRange(0, Z.caps[zLv - 1]); }
        this.applyLock();
      };
      enF = this.nf("zodiac_energy", { label: "Zodiac Energy", min: 0, max: Z.caps[zLv - 1] });
      const zodiac = h("section", { class: "gw-frame-normalwindow gw-win gw-ch-zodiac" }, h("div", { class: "gw-wtitle" }, "Zodiac"), this.appliesLine(),
        h("div", { class: "gw-ch-zleft" }, signName, pic),
        this.reg("zodiac_type", h("div", { class: "gw-ch-zsignwrap", "data-field": F("zodiac_type") }, signs)),
        this.reg("zodiac_level", h("div", { class: "gw-ch-zlv" }, h("span", { class: "gw-label", "data-field": F("zodiac_level") }, "Zodiac level"), stars, capTxt)),
        enF);
      drawStars();

      // ---- looks + pet shed + facts
      const gBtns = h("span", { class: "gw-ch-quick" });
      let gen = b.gender;
      const drawG = () => { GW.clear(gBtns).append(...["male", "female"].map(x => h("button", { class: "gw-btnx small gw-ch-edit" + (gen === x ? " on" : ""), type: "button", onclick: () => { gen = x; this.set("gender", x); drawG(); this.applyLock(); } }, x === "male" ? "Male" : "Female"))); };
      drawG();
      const shed = h("div", { class: "gw-ch-shed" });
      let cap = num(b.pet_shed_capacity) || 2;
      const drawShed = () => {
        GW.clear(shed);
        for (let i = 1; i <= 8; i++) shed.append(h("button", { class: "gw-sprite gw-cell gw-cellx gw-ch-edit gw-ch-shedcell" + (i <= cap ? " open" : " locked") + (i <= d.pets_owned ? " used" : ""), type: "button",
          "aria-label": "Pet shed cell " + i, "data-help": i <= 2 ? "Every character has 2 pet cells." : ("Open " + i + " pet shed cells" + (i < d.pets_owned ? " (not possible: the character owns " + d.pets_owned + " pets)" : "")),
          onclick: () => { if (i < 2 || i < d.pets_owned) return; cap = i; this.set("pet_shed_capacity", i); drawShed(); } }, i <= d.pets_owned ? h("span", { class: "gw-ch-pawn" }, "pet") : null));
        this.applyLock();
      };
      drawShed();
      const facts = [["Created", "Register_time", when(b.Register_time)], ["Last entered the world", "LastLogin_time", when(b.LastLogin_time)],
        ["Warehouse cells", "warehouse_capacity", fmt(b.warehouse_capacity)], ["Holy suit points", "holy_suit_points", fmt(b.holy_suit_points)],
        ["Earl rank", "earl_rank", fmt(b.earl_rank)], ["GM flag", "GM", String(b.GM)], ["Kit bags", "bag_num", fmt(b.bag_num)]];
      const looks = h("section", { class: "gw-frame-normalwindow gw-win gw-ch-looks" }, h("div", { class: "gw-wtitle" }, "Looks, pets and facts"), this.appliesLine(),
        this.reg("gender", h("div", { class: "gw-ch-row" }, h("span", { class: "gw-label", "data-field": F("gender") }, "Gender"), gBtns)),
        this.nf("hair_style", { label: "Hair style" }), this.nf("face_shap", { label: "Face" }),
        h("a", { href: "/static/avatars.html", target: "_blank", rel: "noopener", class: "gw-ch-row" }, "Pictures of hair and faces"),
        this.reg("pet_shed_capacity", h("div", { class: "gw-ch-row" }, h("span", { class: "gw-label", "data-field": F("pet_shed_capacity") }, "Pet shed"), shed)),
        h("table", { class: "gw-ch-facts" }, facts.map(([l, c, v]) => h("tr", null, h("th", { "data-field": F(c) }, l), h("td", { class: "gw-ch-ro", "data-help": (d.fields[c] && d.fields[c].why) ? "Read-only: " + d.fields[c].why : null }, v)))));

      const gm = d.gm || {}, gmAllowed = !!gm.supported && d.writable && !this.online.online;
      const gmSwitch = h("input", {type:"checkbox", checked:gm.enabled || null, disabled:!gmAllowed || null});
      looks.append(h("h3", null, "GM powers"), h("label", null, gmSwitch, " Enable GM abilities"),
        h("button", {class:"gw-btnx", type:"button", disabled:!gmAllowed || null, onclick:async () => {
          try { const result = await this.ctx.api("gm/"+this.cid, {enabled:gmSwitch.checked, expected:Number(b.GM)}); GW.writeDone(result,"GM flag saved"); await this.reload(); }
          catch(e) { await GW.writeError(e); }
        }}, "Save GM flag"), h("p", {class:"gw-dim"}, !gm.supported ? "Requires the server's character-GM authority update." : this.online.online ? "Log this character out before changing GM powers." : "Controls all GM commands for this character. Separate from editor administrator access. Data-format and save-integrity checks still apply."));

      this.body.append(h("div", { class: "gw-ch-grid" }, h("div", { class: "gw-ch-colA" }, charWin), h("div", { class: "gw-ch-colB" }, wallet, talents, zodiac, looks)));
      void self;
    },

    // ---------------------------------------------------------------- position: map + minimap
    drawPosition() {
      const d = this.d, b = d.base;
      let mapId = num(b.Map), x = num(b.Pos_X), z = num(b.Pos_Z);
      const info = h("div", { class: "gw-ch-mapinfo" });
      const pts = h("div", { class: "gw-ch-points" });
      const img = h("img", { class: "gw-ch-mmimg", alt: "" });
      const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
      svg.setAttribute("viewBox", "0 0 512 512"); svg.setAttribute("class", "gw-ch-mmsvg");
      const mm = h("div", { class: "gw-ch-minimap", tabindex: 0, role: "application", "aria-label": "Minimap: click to set the X / Z position" }, img, svg);
      const coord = h("div", { class: "gw-ch-coord gw-dim" });
      const xF = this.nf("Pos_X", { label: "X", min: -d.world, max: d.world, decimals: 2, after: (v, ok) => { if (ok && v !== null) { x = v; mark(); } } });
      const zF = this.nf("Pos_Z", { label: "Z", min: -d.world, max: d.world, decimals: 2, after: (v, ok) => { if (ok && v !== null) { z = v; mark(); } } });
      const mapF = GW.idField("map", mapId, { field: F("Map"), title: "Map", onChange: id => { if (id === null || id === "") return; mapId = Number(id); this.set("Map", mapId); loadMap(true); } });
      const mapRow = this.reg("Map", h("div", { class: "gw-ch-row" }, h("span", { class: "gw-label", "data-field": F("Map") }, "Map"), mapF));
      const S = "http://www.w3.org/2000/svg";
      const dot = (cx, cy, r, cls, title) => { const c = document.createElementNS(S, "circle"); c.setAttribute("cx", cx); c.setAttribute("cy", cy); c.setAttribute("r", r); c.setAttribute("class", cls); if (title) { const t = document.createElementNS(S, "title"); t.textContent = title; c.append(t); } return c; };
      let layer = document.createElementNS(S, "g"), here = document.createElementNS(S, "g");
      svg.append(layer, here);
      const setXZ = (nx, nz) => {
        x = Math.round(nx * 100) / 100; z = Math.round(nz * 100) / 100;
        xF.value = x; zF.value = z; this.set("Pos_X", x); this.set("Pos_Z", z); mark();
      };
      const mark = () => {
        while (here.firstChild) here.firstChild.remove();
        if (num(b.Map) === mapId) { const p = w2m(num(b.Pos_X), num(b.Pos_Z)); here.append(dot(p[0], p[1], 7, "gw-ch-mm-old", "stored position " + b.Pos_X + ", " + b.Pos_Z)); }
        if (x !== null && z !== null) { const p = w2m(x, z); here.append(dot(p[0], p[1], 8, "gw-ch-mm-new", "new position " + x + ", " + z)); }
        coord.textContent = "Position: " + (x === null ? "?" : x) + ", " + (z === null ? "?" : z) + " (the game's X / Z, -" + d.world + " .. " + d.world + ")";
      };
      svg.addEventListener("click", ev => {
        const c = this.canEdit("Pos_X"); if (!c.ok) { GW.status(c.why, "err"); return; }
        const r = svg.getBoundingClientRect();
        const p = m2w((ev.clientX - r.left) / r.width * 512, (ev.clientY - r.top) / r.height * 512);
        setXZ(p[0], p[1]);
      });
      svg.addEventListener("mousemove", ev => { const r = svg.getBoundingClientRect(); const p = m2w((ev.clientX - r.left) / r.width * 512, (ev.clientY - r.top) / r.height * 512); mm.dataset.help = "Click to move here: X " + p[0].toFixed(1) + ", Z " + p[1].toFixed(1); });
      const loadMap = async moved => {
        let m;
        try { m = await this.ctx.api("map/" + mapId); } catch (e) { GW.clear(info).append(h("span", { class: "tt-bad" }, e.message)); return; }
        GW.clear(info).append(h("b", null, m.name), " #" + m.id, m.in_server ? "" : h("span", { class: "tt-bad" }, " (not in map_templates: the server cannot load a character here)"),
          m.dungeon ? h("span", { class: "tt-bad", "data-help": m.dungeon }, " (dungeon map: the server sends a character saved here back to its home town; enter it in game)") : "");
        if (m.minimap) { img.src = m.minimap; img.classList.remove("hidden"); mm.classList.remove("nomap"); }
        else { img.removeAttribute("src"); img.classList.add("hidden"); mm.classList.add("nomap"); }
        while (layer.firstChild) layer.firstChild.remove();
        for (const s of m.safe || []) {
          const a = w2m(s.x1, s.z1), bq = w2m(s.x2, s.z1), c = w2m(s.x2, s.z2), dd = w2m(s.x1, s.z2);
          const poly = document.createElementNS(S, "polygon"); poly.setAttribute("points", [a, bq, c, dd].map(p => p.join(",")).join(" ")); poly.setAttribute("class", "gw-ch-mm-safe");
          const t = document.createElementNS(S, "title"); t.textContent = "safe area"; poly.append(t); layer.append(poly);
        }
        GW.clear(pts);
        const list = (m.points || []).concat((m.links || []).map(l => ({ group: "Exits", name: "to " + (l.to_name || ("map " + l.to)), x: l.x, z: l.z })));
        for (const p of list) {
          const q = w2m(p.x, p.z);
          const c = dot(q[0], q[1], 5, p.group === "Exits" ? "gw-ch-mm-exit" : "gw-ch-mm-pt", p.name + " (" + p.x + ", " + p.z + ")");
          c.addEventListener("click", ev => { ev.stopPropagation(); const k = this.canEdit("Pos_X"); if (!k.ok) { GW.status(k.why, "err"); return; } setXZ(p.x, p.z); });
          layer.append(c);
          pts.append(h("button", { class: "gw-ch-pt gw-ch-edit", type: "button", "data-help": "Put the character at " + p.name + " (" + p.x + ", " + p.z + ")", onclick: () => setXZ(p.x, p.z) },
            h("span", { class: "gw-ch-ptg" }, p.group || ""), " ", h("b", null, p.name), " ", h("span", { class: "gw-dim" }, p.x + ", " + p.z)));
        }
        if (!list.length) pts.append(h("div", { class: "gw-dim" }, "The client names no points on this map: click the minimap or type X / Z."));
        if (moved && list.length) setXZ(list[0].x, list[0].z);
        else if (moved) setXZ(0, 0);
        mark();
        this.fields.points = { el: pts };
        this.applyLock();
      };
      this.body.append(h("div", { class: "gw-ch-posgrid" },
        h("section", { class: "gw-frame-normalwindow gw-win gw-ch-poswin" }, h("div", { class: "gw-wtitle" }, "World map"), info, mm, coord,
          h("p", { class: "gw-dim gw-ch-note" }, "The client's own minimap, with its projection (45 degrees, MAP-FORMATS.md). Blue dots: named places; orange: exits to other maps; green: safe areas; white ring: the stored position; yellow: the new one.")),
        h("section", { class: "gw-frame-normalwindow gw-win gw-ch-posside" }, h("div", { class: "gw-wtitle" }, "Position"), this.appliesLine(), mapRow, xF, zF,
          h("p", { class: "gw-dim gw-ch-note" }, "Online with the round-2 server, the map and X / Z teleport the player at once. Otherwise the character appears here at the next login."),
          h("h3", null, "Places on this map"), pts)));
      loadMap(false);
      GW.catalog.load("map").catch(() => {});
    },

    // ---------------------------------------------------------------- account
    drawAccountTab() {
      const list = h("div", { class: "gw-ch-acclist", role: "list" });
      const search = h("input", { class: "gw-ch-input", type: "search", placeholder: "Search user name, e-mail or character", "aria-label": "Search accounts" });
      const count = h("span", { class: "gw-dim" });
      this.accDetail = h("section", { class: "gw-frame-normalwindow gw-win gw-ch-accdetail" }, h("div", { class: "gw-wtitle" }, "Account"));
      const newBtn = h("button", { class: "gw-btnx", type: "button", disabled: !this.d.writable || null, "data-help": "Make a new login (user name + password), written exactly like the game server's own first-login registration." }, "+ New account");
      newBtn.addEventListener("click", () => this.newAccount());
      this.body.append(h("div", { class: "gw-ch-accgrid" },
        h("section", { class: "gw-frame-normalwindow gw-win gw-ch-accleft" }, h("div", { class: "gw-wtitle" }, "Accounts"),
          h("div", { class: "gw-ch-acctools" }, newBtn, search, count), list),
        this.accDetail));
      this.accList = list; this.accCount = count;
      let t = null;
      search.addEventListener("input", () => { clearTimeout(t); t = setTimeout(() => this.loadAccounts(search.value), 250); });
      this.loadAccounts("");
      this.showAccount(this.d.base.account_id);
    },
    async loadAccounts(q) {
      let r;
      try { r = await this.ctx.api("accounts?q=" + encodeURIComponent(q || "")); } catch (e) { GW.clear(this.accList).append(h("div", { class: "tt-bad" }, e.message)); return; }
      this.userRule = r.user_rule;
      this.accCount.textContent = r.accounts.length + " account" + (r.accounts.length === 1 ? "" : "s");
      GW.clear(this.accList);
      for (const a of r.accounts) {
        this.accList.append(h("button", { class: "gw-ch-accrow" + (this.acct && this.acct.id === a.id ? " on" : ""), type: "button", role: "listitem", "data-id": a.id, onclick: () => this.showAccount(a.id) },
          h("span", { class: "gw-dot " + (a.online ? "on" : "off"), "data-help": a.online ? "logged in now" : "logged out" }),
          h("span", { class: "nm" }, a.username, a.banned ? h("span", { class: "gw-chip restart" }, "banned") : null,
            a.donator_tier ? h("span", { class: "gw-chip login" }, a.tier_name) : null,
            a.game_safe_name ? null : h("span", { class: "gw-chip never", "data-help": "This user name has capital letters or symbols: the 2.51 game client cannot log in with it. Rename it (" + r.user_rule + ")." }, "client cannot log in")),
          h("span", { class: "sub" }, "#" + a.id + " · " + (a.characters.length ? a.characters.map(c => c.name).join(", ") : "no characters yet"))));
      }
      if (!r.accounts.length) this.accList.append(h("div", { class: "gw-dim" }, q ? "No account matches." : "No accounts yet."));
    },
    async showAccount(id, keepScroll) {
      let a;
      try { a = await this.ctx.api("account/" + id); } catch (e) { GW.status(e.message, "err"); return; }
      this.acct = a;
      for (const x of (this.accList ? this.accList.querySelectorAll(".gw-ch-accrow") : [])) x.classList.toggle("on", Number(x.dataset.id) === a.id);
      const D = this.accDetail;
      if (!D) return;
      void keepScroll;
      const ro = !this.d.writable;
      const mine = a.id === this.d.base.account_id;
      GW.clear(D).append(h("div", { class: "gw-wtitle" }, "Account " + a.username + (mine ? " (this character's)" : "")));
      const kv = (label, field, ...v) => h("tr", null, h("th", { "data-field": field ? "accounts." + field : null }, label), h("td", null, ...v));
      D.append(h("table", { class: "gw-ch-kv" },
        kv("User name", "username", h("b", { class: "gw-value" }, a.username), h("span", { class: "gw-dim" }, "  #" + a.id),
          a.game_safe_name ? null : h("span", { class: "tt-bad", "data-help": "The 2.51 client cannot log in with capital letters or symbols. Rename it." }, "  the game client cannot log in with this name")),
        kv("State", null, h("span", { class: "gw-dot " + (a.online ? "on" : "off") }), a.online ? "logged in" : "logged out",
          a.banned ? h("span", { class: "gw-chip restart", "data-help": a.ban_effective ? "The login is refused." : "The stock server ignores the ban; it works with the round-2 update." }, "banned") : null),
        kv("Password", "password", a.hashed ? "stored as a secure hash (nobody can read it back)" : "stored in an old format: set a new one"),
        kv("Last login", "last_login_time", when(a.last_login_time)), kv("Last logout", "last_logout_time", when(a.last_logout_time)),
        kv("Time online", "total_online_time", fmt(a.total_online_time) + " s"),
        kv("Realm", null, (a.realms || []).map(r => h("span", { class: "gw-chip never", "data-help": "account_realm: the server adds it at the first character; character_lifecycle_version " + r.character_lifecycle_version + ", slot limit " + r.character_slot_limit }, "realm " + r.realm_id)))));

      // ---- characters (delete / restore the game's way)
      const chars = h("div", { class: "gw-ch-acchars" });
      for (const c of a.characters) {
        const deleted = c.state === "deleted";
        chars.append(h("div", { class: "gw-ch-acchar" + (deleted ? " deleted" : "") },
          h("span", { class: "gw-dot " + (c.online ? "on" : "off") }),
          h("a", { href: "#/character/" + c.id + "/sheet", "data-help": "Open " + c.name + " in the Character editor" }, h("b", null, c.name)),
          h("span", { class: "gw-dim" }, " Lv " + c.level + " " + c.class + " · " + c.camp_name),
          deleted ? h("span", { class: "gw-chip restart", "data-help": "Deleted " + when(c.deleted_at) + "; restorable until " + when(c.restore_until) + "; the server purges it after " + when(c.purge_after) + "." }, c.restorable ? "deleted, restorable" : "deleted") : null,
          !deleted ? h("button", { class: "gw-btnx small danger", type: "button", disabled: ro || null, "data-help": "Delete this character the game's way: it can be restored for 30 days, then the server removes it for good.",
            onclick: () => this.lifecycle(c, "delete") }, "Delete") : null,
          deleted && c.restorable ? h("button", { class: "gw-btnx small", type: "button", disabled: ro || null, "data-help": "Bring the character back (only while the account has no other active character).", onclick: () => this.lifecycle(c, "restore") }, "Restore") : null));
      }
      if (!a.characters.length) chars.append(h("div", { class: "gw-dim" }, "No characters yet: the player makes one at the character screen after logging in."));
      D.append(h("h3", null, "Characters"), chars, h("p", { class: "gw-dim gw-ch-note" }, "One active character per account (the server's rule)."));

      // ---- user name + password
      const userIn = h("input", { class: "gw-ch-input", value: a.username, maxlength: 16, spellcheck: "false", autocomplete: "off", "aria-label": "New user name" });
      const pw = this.pwBox("gw-ch-newpw");
      D.append(h("h3", { "data-field": "accounts.username" }, "Login"),
        h("div", { class: "gw-ch-fgrid" },
          h("label", { "data-field": "accounts.username" }, "User name"),
          h("span", { class: "gw-ch-line" }, userIn, h("button", { class: "gw-btnx small", type: "button", disabled: ro || null, onclick: () => this.rename(a, userIn.value) }, "Rename")),
          h("label", { "data-field": "accounts.password" }, "New password"),
          h("span", { class: "gw-ch-line" }, pw.el, h("button", { class: "gw-btnx small", type: "button", disabled: ro || null, onclick: () => this.password(a, pw) }, "Set password"))),
        h("p", { class: "gw-dim gw-ch-note" }, "User name: " + (a.user_rule || "") + ". Password: 4-32 characters; stored the way the server stores it (PBKDF2 of the MD5 the client sends), never kept by the editor."));

      // ---- e-mail, donator, ban
      const email = h("input", { class: "gw-ch-input", type: "email", value: a.email || "", maxlength: 255, "aria-label": "E-mail" });
      const tier = h("select", { class: "gw-ch-input", "aria-label": "Donator tier" }, Object.entries(a.tiers).map(([k, v]) => h("option", { value: k, selected: Number(k) === a.donator_tier || null }, k + " - " + v)));
      const exp = h("input", { class: "gw-ch-input", type: "date", value: a.donator_expires_at ? String(a.donator_expires_at).slice(0, 10) : "", "aria-label": "Donator tier ends" });
      D.append(h("h3", null, "Details"),
        h("div", { class: "gw-ch-fgrid" },
          h("label", { "data-field": "accounts.email" }, "E-mail"), email,
          h("label", { "data-field": "accounts.donator_tier" }, "Donator tier"), tier,
          h("label", { "data-field": "accounts.donator_expires_at" }, "Tier ends"), h("span", null, exp, h("span", { class: "gw-dim" }, " empty = never"))),
        h("div", { class: "gw-ch-line" }, GW.appliesChip("next_login"),
          h("button", { class: "gw-btnx", type: "button", disabled: ro || null, onclick: async () => {
            const body = { expected: { email: a.email, donator_tier: a.donator_tier } };
            if (email.value !== (a.email || "")) body.email = email.value;
            if (Number(tier.value) !== a.donator_tier) body.donator_tier = Number(tier.value);
            const old = a.donator_expires_at ? String(a.donator_expires_at).slice(0, 10) : "";
            if (exp.value !== old) body.donator_expires_at = exp.value || null;
            if (Object.keys(body).length === 1) { GW.status("nothing changed"); return; }
            try { const r = await this.ctx.api("account/" + a.id + "/edit", body); this.done(r); this.afterAccount(r); }
            catch (e) { if (await GW.writeError(e) === "conflict") this.showAccount(a.id); }
          } }, "Save details")),
        h("h3", { "data-field": "accounts.status" }, "Status (ban)"),
        h("div", { class: "gw-ch-line" },
          h("button", { class: "gw-btnx" + (a.banned ? "" : " danger"), type: "button", disabled: ro || null, onclick: () => this.ban(a) }, a.banned ? "Unban" : "Ban this account"),
          h("span", { class: "gw-dim" }, a.ban_effective ? "The running server refuses a banned login and kicks an online player." : "The server running now (stock) ignores this flag: a ban works once the round-2 server update is installed.")));

      // ---- entitlements
      const ents = h("div", { class: "gw-ch-ents" });
      for (const e of a.entitlements || []) {
        const active = !e.revoked_at && (!e.expires_at || new Date(e.expires_at) > new Date());
        const endIn = h("input", { class: "gw-ch-input", type: "date", value: e.expires_at ? String(e.expires_at).slice(0, 10) : "", "aria-label": "Ends" });
        ents.append(h("div", { class: "gw-ch-ent" + (active ? "" : " off") },
          h("b", { "data-help": (a.entitlement_keys || {})[e.entitlement_key] || "" }, e.entitlement_key.replace("_", " ")),
          h("span", { class: "gw-chip " + (active ? "now" : "never") }, e.revoked_at ? "revoked" : active ? "active" : "ended"),
          h("span", { class: "gw-dim" }, " from " + when(e.starts_at) + " · source " + e.source),
          !e.revoked_at ? h("span", { class: "gw-ch-line" }, endIn,
            h("button", { class: "gw-btnx small", type: "button", disabled: ro || null, onclick: () => this.entitlement(a, { op: "expiry", id: e.id, expires_at: endIn.value || null }) }, "Set end"),
            h("button", { class: "gw-btnx small danger", type: "button", disabled: ro || null, onclick: () => this.entitlement(a, { op: "revoke", id: e.id }) }, "Revoke")) : null));
      }
      if (!(a.entitlements || []).length) ents.append(h("div", { class: "gw-dim" }, "No perks."));
      const bpEnd = h("input", { class: "gw-ch-input", type: "date", "aria-label": "Battle pass ends" });
      D.append(h("h3", { "data-help": "account_entitlements: perks the server checks at each use. " + ((a.entitlement_keys || {}).battle_pass || "") }, "Perks (entitlements)"), ents,
        h("div", { class: "gw-ch-line" }, h("span", { class: "gw-label" }, "Battle pass ends"), bpEnd, h("span", { class: "gw-dim" }, "empty = never"),
          h("button", { class: "gw-btnx small", type: "button", disabled: ro || null, "data-help": (a.entitlement_keys || {}).battle_pass, onclick: () => this.entitlement(a, { op: "grant", key: "battle_pass", expires_at: bpEnd.value || null }) }, "Grant battle pass"),
          GW.appliesChip("now", "applies at the next use")));

      // ---- delete (the plan is read separately: it walks every foreign key of the database)
      const delBox = h("div", { class: "gw-ch-delbox" }, h("p", { class: "gw-dim" }, "Checking what a delete would remove ..."));
      D.append(h("h3", { class: "tt-bad" }, "Delete"), delBox);
      this.ctx.api("account/" + a.id + "/delete-plan").then(P => {
        if (!this.acct || this.acct.id !== a.id) return;
        a._notRestorable = P.not_restorable || [];
        a._wipeNotRestorable = ((P.wipe || {}).not_restorable || []).concat(P.not_restorable || []);
        const tbl = h("table", { class: "gw-ch-deltbl" }, h("tr", null, h("th", null, "Table"), h("th", null, "Rows"), h("th", null, "Undo")),
          (P.tables || []).concat(((P.wipe || {}).tables || []).map(t => Object.assign({}, t, { label: (t.label || t.table) + " (removed by Wipe only)" }))).map(t => h("tr", null, h("td", { "data-help": t.label }, t.table), h("td", null, fmt(t.rows)), h("td", null, t.undo ? "yes" : "no (kept in the backup file)"))));
        GW.clear(delBox).append(
          P.can_delete ? h("p", null, "A permanent delete removes the account and " + fmt(P.total_rows - 1) + " rows that belong to it (the same cascade the server uses when it purges a character). A backup is taken first; Undo in the change log puts it back" +
            ((P.not_restorable || []).length ? ", except " + P.not_restorable.join(", ") + " (the database numbers those rows itself, so Undo cannot re-insert them; they stay in the backup file)." : "."))
            : h("div", null, h("p", { class: "tt-bad" }, "A plain delete is blocked: the game keeps records that point at this account (kills, loot and claims it has settled):"),
              h("ul", null, Object.values((P.blockers || []).reduce((m, x) => { if (!m[x.table] || m[x.table].rows < x.rows) m[x.table] = x; return m; }, {})).map(x => h("li", null, h("b", null, x.table), " " + fmt(x.rows) + " row" + (x.rows === 1 ? "" : "s") + " - " + (x.what || "")))),
              (P.wipe || {}).can_wipe ? h("p", null, "Wipe account removes the account and these records together.")
                : (P.wipe || {}).function_installed === false && !((P.wipe || {}).hard_blockers || []).length
                  ? h("p", { class: "tt-bad" }, "Wipe needs one small database function that is not installed yet. Run deploy-editor-changes.sh once; nothing else changes.")
                  : h("p", { class: "tt-bad" }, "A wipe is blocked too, by something that other players depend on: " + ((P.wipe || {}).hard_blockers || []).map(x => x.table + (x.what ? " (" + x.what + ")" : "")).join("; ") + ". Remove that first (a guild: transfer or disband it in the Guild editor).")),
          h("details", null, h("summary", null, "What a delete or wipe would remove"), tbl),
          h("div", { class: "gw-ch-line" },
            h("button", { class: "gw-btnx danger", type: "button", disabled: ro || !P.can_delete || null, "data-help": P.can_delete ? "Remove the account and everything that belongs to it." : "Blocked by evidence rows; use Retire.", onclick: () => this.deleteAccount(a, "delete") }, "Delete account for good"),
            h("button", { class: "gw-btnx danger", type: "button", disabled: ro || !(P.wipe || {}).can_wipe || null, "data-help": (P.wipe || {}).can_wipe ? "Remove the account, all its characters and its kill, loot and claim records (" + fmt((P.wipe || {}).total_rows) + " rows in all). Works even when a plain delete is blocked." : "Blocked by something outside the account's own records (see above).", onclick: () => this.deleteAccount(a, "wipe") }, "Wipe account"),
            h("button", { class: "gw-btnx danger", type: "button", disabled: ro || null, "data-help": "What the game itself allows: delete the characters the game's way (restorable for 30 days) and ban the account.", onclick: () => this.deleteAccount(a, "retire") }, "Retire (delete characters + ban)")));
      }).catch(e => GW.clear(delBox).append(h("p", { class: "tt-bad" }, e.message)));
    },
    pwBox(id) {
      const input = h("input", { id, class: "gw-ch-input", type: "password", autocomplete: "new-password", maxlength: 32, spellcheck: "false", "aria-label": "Password" });
      const eye = h("button", { class: "gw-btnx small", type: "button", "aria-label": "Show password", onclick: () => { input.type = input.type === "password" ? "text" : "password"; eye.textContent = input.type === "password" ? "Show" : "Hide"; } }, "Show");
      return { el: h("span", { class: "gw-ch-pw" }, input, eye), input };
    },
    afterAccount(r) { if (r && r.account) { this.showAccount(r.account.id); this.loadAccounts(""); } },
    async rename(a, name) {
      name = (name || "").trim();
      if (!/^[a-z0-9]{4,16}$/.test(name)) { GW.alert("The user name must be " + (a.user_rule || "4-16 lower-case letters and digits") + ".", "Rename"); return; }
      if (!await GW.confirm({ title: "Rename " + a.username + "?", message: "The player logs in as |cFFFFFF00" + name + "|cFFFFFFFF from now on. The password stays the same.", ok: "Rename" })) return;
      try { const r = await this.ctx.api("account/" + a.id + "/rename", { username: name, expected: a.username }); this.done(r); if (r.note) GW.status(r.note, "ok"); this.afterAccount(r); }
      catch (e) { await GW.writeError(e); }
    },
    async password(a, pw) {
      const v = pw.input.value;
      if (v.length < 4) { GW.alert("The password must be at least 4 characters.", "Password"); return; }
      if (!await GW.confirm({ title: "Change the password?", message: "The new password works from the next login of " + a.username + ". A player who is in the game now stays in.", ok: "Change it" })) return;
      try { const r = await this.ctx.api("account/" + a.id + "/password", { password: v }); pw.input.value = ""; this.done(r, "password changed for " + a.username); this.afterAccount(r); }
      catch (e) { await GW.writeError(e); }
    },
    async ban(a) {
      const on = !a.banned;
      const msg = on ? ("Nobody can log in as " + a.username + " while banned." + (a.online ? " Online characters are disconnected." : "") +
        (a.ban_effective ? "" : "\n\nNote: the server running now (stock) ignores the ban flag; it works once the round-2 update is installed.")) : "The account can log in again.";
      if (!await GW.confirm({ title: (on ? "Ban " : "Unban ") + a.username + "?", message: msg, ok: on ? "Ban" : "Unban", danger: on })) return;
      try { const r = await this.ctx.api("account/" + a.id + "/ban", { banned: on }); this.done(r); if (r.note) GW.status(r.note); this.afterAccount(r); }
      catch (e) { await GW.writeError(e); }
    },
    async entitlement(a, body) {
      try { const r = await this.ctx.api("account/" + a.id + "/entitlement", body); this.done(r); this.afterAccount(r); }
      catch (e) { await GW.writeError(e); }
    },
    async lifecycle(c, op) {
      let body = {};
      if (op === "delete") {
        const inp = h("input", { class: "gw-ch-input", "aria-label": "Type the character name", autocomplete: "off" });
        const ok = await GW.confirm({ title: "Delete " + c.name + "?", danger: true, ok: "Delete", el: h("div", { class: "gw-mod-character" },
          h("p", null, "The character is deleted the game's way: it can be restored here for 30 days, then the server removes it for good."),
          h("p", null, "Type ", h("b", null, c.name), " to confirm:"), inp) });
        if (!ok) return;
        body = { confirm: inp.value };
      } else if (!await GW.confirm({ title: "Restore " + c.name + "?", message: "The character comes back as it was when it was deleted.", ok: "Restore" })) return;
      try { const r = await this.ctx.api("lifecycle/" + c.id + "/" + op, body); this.done(r); this.afterAccount(r); }
      catch (e) { await GW.writeError(e); }
    },
    async deleteAccount(a, mode) {
      const inp = h("input", { class: "gw-ch-input", "aria-label": "Type the user name", autocomplete: "off" });
      const text = mode === "wipe"
        ? "Account " + a.username + " is wiped: the account, all its characters (" + (a.characters.map(c => c.name).join(", ") || "none") + ") and every kill, loot and claim record it has are removed for good. A backup is taken first; Undo in the change log puts it all back" + ((a._wipeNotRestorable || a._notRestorable || []).length ? ", except " + (a._wipeNotRestorable || a._notRestorable).join(", ") + " (kept in the backup file only)." : ".")
        : mode === "delete"
        ? "Account " + a.username + " and everything that belongs to it (" + a.characters.map(c => c.name).join(", ") + ") is removed for good. A backup is taken first; Undo in the change log puts it back" + ((a._notRestorable || []).length ? ", except " + a._notRestorable.join(", ") + " (kept in the backup file only)." : ".")
        : "Every active character of " + a.username + " is deleted the game's way (restorable for 30 days) and the account is banned." + (a.ban_effective ? "" : " The server running now ignores the ban until the round-2 update.");
      const ok = await GW.confirm({ title: (mode === "wipe" ? "Wipe " : mode === "delete" ? "Delete " : "Retire ") + a.username + "?", danger: true, ok: mode === "wipe" ? "Wipe for good" : mode === "delete" ? "Delete for good" : "Retire",
        el: h("div", { class: "gw-mod-character" }, h("p", null, text), h("p", null, "Type ", h("b", null, a.username), " to confirm:"), inp) });
      if (!ok) return;
      try {
        const r = await this.ctx.api("account/" + a.id + "/delete", { confirm: inp.value, mode });
        this.done(r);
        if (r.note) GW.alert(r.note, mode === "wipe" ? "Account wiped" : mode === "delete" ? "Account deleted" : "Account retired");
        if (mode === "delete" || mode === "wipe") { await this.loadAccounts(""); GW.clear(this.accDetail).append(h("div", { class: "gw-wtitle" }, "Account"), h("p", { class: "gw-dim" }, "Account " + a.username + " was deleted. Undo is in the change log (and on the bar above).")); }
        else this.afterAccount(r);
      } catch (e) { await GW.writeError(e); }
    },
    newAccount() {
      const user = h("input", { class: "gw-ch-input", type: "text", maxlength: 16, autocomplete: "off", spellcheck: "false", "aria-label": "User name", id: "gw-ch-user" });
      const pw = this.pwBox("gw-ch-pw1"), pw2 = this.pwBox("gw-ch-pw2");
      const email = h("input", { class: "gw-ch-input", type: "email", maxlength: 255, "aria-label": "E-mail (optional)" });
      const tier = h("select", { class: "gw-ch-input", "aria-label": "Donator tier" }, Object.entries((this.acct && this.acct.tiers) || this.d.tiers).map(([k, v]) => h("option", { value: k }, k + " - " + v)));
      const err = h("div", { class: "tt-bad", role: "alert" });
      const go = h("button", { class: "gw-btnx", type: "button" }, "Create account");
      const m = GW.modal(h("div", { class: "gw-mod-character gw-ch-new" },
        h("div", { class: "gw-ch-fgrid" },
          h("label", { "data-field": "accounts.username", for: "gw-ch-user" }, "User name"), user,
          h("label", { "data-field": "accounts.password" }, "Password"), pw.el,
          h("label", null, "Password again"), pw2.el,
          h("label", { "data-field": "accounts.email" }, "E-mail"), email,
          h("label", { "data-field": "accounts.donator_tier" }, "Donator tier"), tier),
        h("p", { class: "gw-dim" }, "User name: " + (this.userRule || "4-16 lower-case letters a-z and digits 0-9") + ". Password: 4-32 characters. The account works at once: log in with it in the game client and create a character as usual."),
        err, h("div", { class: "gw-confirm-buttons" }, go)), { title: "New account", wide: true });
      setTimeout(() => user.focus(), 0);
      user.addEventListener("input", () => { if (/[A-Z]/.test(user.value)) err.textContent = "Use lower-case letters: the game client cannot log in with capitals."; else err.textContent = ""; });
      go.addEventListener("click", async () => {
        err.textContent = "";
        if (!/^[a-z0-9]{4,16}$/.test(user.value)) { err.textContent = "The user name must be 4-16 lower-case letters a-z or digits 0-9."; return; }
        if (pw.input.value.length < 4) { err.textContent = "The password must be at least 4 characters."; return; }
        if (pw.input.value !== pw2.input.value) { err.textContent = "The two passwords are not the same."; return; }
        go.disabled = true; go.textContent = "Creating ...";
        try {
          const r = await this.ctx.api("account/create", { username: user.value, password: pw.input.value, email: email.value, donator_tier: Number(tier.value) });
          m.close();
          this.done(r, "account " + user.value + " created");
          await this.loadAccounts("");
          if (r.account) this.showAccount(r.account.id);
          GW.alert("Account " + user.value + " is ready.\n\n" + (r.note || ""), "Account created");
        } catch (e) { err.textContent = e.message; go.disabled = false; go.textContent = "Create account"; }
      });
    },
  });
})();
