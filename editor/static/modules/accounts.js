/* static/modules/accounts.js - the "Accounts" page: every account with its characters, "New account", and per account:
   password, e-mail, donator tier, ban / unban. Backend: modules/accounts.py. Contract: MODULES.md. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const TIERS = { 0: "none", 1: "Kijin Patron", 2: "Oni Patron", 3: "Demon Lord Seed", 4: "True Demon Lord", 5: "Octagram Patron" };

  function when(t) {
    if (!t) return "never";
    const d = new Date(t);
    return isNaN(d) ? String(t) : d.toLocaleString();
  }
  function passwordBox(id) {
    const input = h("input", { id, class: "gw-acc-input", type: "password", autocomplete: "new-password", maxlength: 32, spellcheck: "false" });
    const eye = h("button", { class: "gw-btnx small", type: "button", "aria-label": "Show password", "data-help": "Show or hide what you typed.",
      onclick: () => { input.type = input.type === "password" ? "text" : "password"; eye.textContent = input.type === "password" ? "Show" : "Hide"; } }, "Show");
    return { el: h("span", { class: "gw-acc-pw" }, input, eye), input };
  }

  GW.module("accounts", {
    async mount(el, ctx) {
      this.el = el;
      const list = h("div", { class: "gw-acc-list", role: "list" });
      const detail = h("section", { class: "gw-frame-normalwindow gw-win gw-acc-detail" }, h("div", { class: "gw-wtitle" }, "Account"),
        h("p", { class: "gw-dim" }, "Pick an account on the left, or make a new one."));
      const search = h("input", { class: "gw-acc-input", type: "search", placeholder: "Search user name, e-mail or character", "aria-label": "Search accounts" });
      const count = h("span", { class: "gw-dim" });
      const banner = h("div", { class: "gw-acc-banner" });
      el.append(banner,
        h("div", { class: "gw-acc-cols" },
          h("section", { class: "gw-frame-normalwindow gw-win gw-acc-left" }, h("div", { class: "gw-wtitle" }, "Accounts"),
            h("div", { class: "gw-acc-tools" },
              h("button", { class: "gw-btnx", type: "button", "data-help": "Make a new login (user name + password). The player then logs in with it and creates characters at the character screen as usual.", onclick: () => this.create() }, "+ New account"),
              search, count),
            list),
          detail));
      this.list = list; this.detail = detail; this.count = count; this.banner = banner; this.ctx = ctx;
      let t = null;
      search.addEventListener("input", () => { clearTimeout(t); t = setTimeout(() => this.load(search.value), 250); });
      await this.load("");
      const want = ctx.route[0];
      if (want === "new") this.create();
      else if (want && /^\d+$/.test(want)) this.show(Number(want));
    },
    unmount() {},

    async load(q) {
      let r;
      try { r = await this.ctx.api("list?q=" + encodeURIComponent(q || "")); }
      catch (e) { GW.clear(this.list).append(h("div", { class: "tt-bad" }, e.message)); return; }
      this.data = r;
      GW.clear(this.banner);
      if (!r.writable) this.banner.append(h("div", { class: "gw-online warn" }, "This editor is read-only for this database: you can look, not change."));
      GW.clear(this.list);
      this.count.textContent = r.accounts.length + " account" + (r.accounts.length === 1 ? "" : "s");
      for (const a of r.accounts) {
        const row = h("button", { class: "gw-acc-row" + (a.id === this.cur ? " on" : ""), type: "button", role: "listitem", "data-id": a.id, onclick: () => this.show(a.id) },
          h("span", { class: "gw-dot " + (a.online ? "on" : "off"), "data-help": a.online ? "logged in now" : "logged out" }),
          h("span", { class: "nm" }, a.username, a.banned ? h("span", { class: "gw-chip restart" }, "banned") : null,
            a.donator_tier ? h("span", { class: "gw-chip login", "data-help": "Donator tier " + a.donator_tier }, TIERS[a.donator_tier]) : null),
          h("span", { class: "sub" }, "#" + a.id + " · " + (a.characters.length ? a.characters.map(c => c.name).join(", ") : "no characters yet")));
        this.list.append(row);
      }
      if (!r.accounts.length) this.list.append(h("div", { class: "gw-dim" }, q ? "No account matches." : "No accounts yet."));
    },

    async show(id) {
      this.cur = id;
      for (const b of this.list.querySelectorAll(".gw-acc-row")) b.classList.toggle("on", Number(b.dataset.id) === id);
      history.replaceState(null, "", "#/accounts/" + id);
      let a;
      try { a = await this.ctx.api(String(id)); } catch (e) { GW.status(e.message, "err"); return; }
      const d = this.detail;
      GW.clear(d).append(h("div", { class: "gw-wtitle" }, "Account " + a.username));
      const ro = !this.data.writable;
      // ---- identity + state
      d.append(h("table", { class: "gw-acc-kv" },
        h("tr", null, h("th", { "data-field": "accounts.username" }, "User name"), h("td", { class: "gw-value" }, a.username, h("span", { class: "gw-dim" }, "  #" + a.id))),
        h("tr", null, h("th", { "data-help": "Logged in = the account is at the login / character screen or a character is in the world." }, "State"),
          h("td", null, h("span", { class: "gw-dot " + (a.online ? "on" : "off") }), a.online ? "logged in" : "logged out",
            a.banned ? h("span", { class: "gw-chip restart", "data-help": a.ban_effective ? "The login is refused." : "The stock server ignores this; it works with the round-2 update." }, "banned") : null)),
        h("tr", null, h("th", { "data-field": "accounts.last_login_time" }, "Last login"), h("td", null, when(a.last_login_time))),
        h("tr", null, h("th", { "data-field": "accounts.last_logout_time" }, "Last logout"), h("td", null, when(a.last_logout_time))),
        h("tr", null, h("th", { "data-field": "accounts.password" }, "Password"), h("td", null, a.hashed ? "stored as a secure hash (nobody can read it back)" : "stored in an old format; set a new one"))));
      // ---- characters
      const chars = h("div", { class: "gw-acc-chars" });
      for (const c of a.characters) {
        chars.append(h("a", { class: "gw-cp-row", href: "#/character/" + c.id, "data-help": "Open " + c.name + " in the Character editor." },
          h("span", { class: "gw-dot " + (c.online ? "on" : "off") }), h("span", { class: "nm" }, c.name), h("span", { class: "sub" }, "Lv " + c.level + " " + c.class + " · " + c.camp_name)));
      }
      if (!a.characters.length) chars.append(h("div", { class: "gw-dim" }, "No characters yet: the player creates them at the character screen after logging in."));
      d.append(h("h3", null, "Characters"), chars);

      // ---- editor authorization is separate from character GM powers.
      const admin = h("input", {type: "checkbox", checked: a.editor_admin || null, disabled: ro || null});
      d.append(h("h3", null, "Editor administrator"),
        h("p", {class: "gw-dim"}, "Allows this account's game login and password to access every editor function. Revoking access, banning the account or changing its password ends its editor sessions. Character GM powers are separate."),
        h("label", null, admin, " Allow editor access"),
        h("button", {class: "gw-btnx", type: "button", disabled: ro || null, onclick: async () => {
          try { const r = await this.ctx.api(a.id + "/editor-admin", {enabled: admin.checked, expected: a.editor_admin});
            GW.writeDone(r, "editor access updated"); await this.load(""); this.show(a.id); }
          catch (e) { await GW.writeError(e); }
        }}, "Save editor access"));

      // ---- password
      const pw = passwordBox("gw-acc-newpw");
      d.append(h("h3", { "data-field": "accounts.password" }, "New password"),
        h("div", { class: "gw-acc-line" }, pw.el,
          h("button", { class: "gw-btnx", type: "button", disabled: ro || null, onclick: async () => {
            const v = pw.input.value;
            if (!await GW.confirm({ title: "Change the password?", message: "The new password works from the next login of " + a.username + ". A player who is in the game now stays in.", ok: "Change it" })) return;
            try { const r = await this.ctx.api(a.id + "/password", { password: v }); pw.input.value = ""; GW.writeDone(r, "password changed for " + a.username); }
            catch (e) { await GW.writeError(e); }
          } }, "Set password")),
        h("p", { class: "gw-dim" }, "4-32 characters. The editor never stores or shows the password itself."));

      // ---- e-mail + donator
      const email = h("input", { class: "gw-acc-input", type: "email", value: a.email || "", maxlength: 255, "aria-label": "E-mail" });
      const tier = h("select", { class: "gw-acc-input", "aria-label": "Donator tier" }, Object.entries(TIERS).map(([k, v]) => h("option", { value: k, selected: Number(k) === a.donator_tier || null }, k + " - " + v)));
      const exp = h("input", { class: "gw-acc-input", type: "date", value: a.donator_expires_at ? String(a.donator_expires_at).slice(0, 10) : "", "aria-label": "Donator tier ends" });
      d.append(h("h3", null, "Details"),
        h("div", { class: "gw-acc-grid" },
          h("label", { "data-field": "accounts.email" }, "E-mail"), email,
          h("label", { "data-field": "accounts.donator_tier" }, "Donator tier"), tier,
          h("label", { "data-field": "accounts.donator_expires_at" }, "Tier ends"), h("span", null, exp, h("span", { class: "gw-dim" }, " empty = never"))),
        h("div", { class: "gw-acc-line" },
          GW.appliesChip("next_login"),
          h("button", { class: "gw-btnx", type: "button", disabled: ro || null, onclick: async () => {
            const body = { expected: { email: a.email, donator_tier: a.donator_tier } };
            if (email.value !== (a.email || "")) body.email = email.value;
            if (Number(tier.value) !== a.donator_tier) body.donator_tier = Number(tier.value);
            const oldExp = a.donator_expires_at ? String(a.donator_expires_at).slice(0, 10) : "";
            if (exp.value !== oldExp) body.donator_expires_at = exp.value || null;
            if (Object.keys(body).length === 1) { GW.status("nothing changed"); return; }
            try { const r = await this.ctx.api(a.id + "/edit", body); GW.writeDone(r); await this.load(""); this.show(a.id); }
            catch (e) { if (await GW.writeError(e) === "conflict") this.show(a.id); }
          } }, "Save details")));

      // ---- ban
      d.append(h("h3", { "data-field": "accounts.status" }, "Ban"),
        h("div", { class: "gw-acc-line" },
          h("button", { class: "gw-btnx" + (a.banned ? "" : " danger"), type: "button", disabled: ro || null, onclick: async () => {
            const on = !a.banned;
            const msg = on ? ("Nobody can log in as " + a.username + " while banned." + (a.online ? " Online characters are disconnected." : "") +
              (a.ban_effective ? "" : "\n\nNote: the server running now (stock) ignores the ban flag; it works once the round-2 update is installed."))
              : "The account can log in again.";
            if (!await GW.confirm({ title: (on ? "Ban " : "Unban ") + a.username + "?", message: msg, ok: on ? "Ban" : "Unban", danger: on })) return;
            try { const r = await this.ctx.api(a.id + "/ban", { banned: on }); GW.writeDone(r); if (r.note) GW.status(r.note); await this.load(""); this.show(a.id); }
            catch (e) { await GW.writeError(e); }
          } }, a.banned ? "Unban" : "Ban this account"),
          h("span", { class: "gw-dim" }, a.ban_effective ? "The game server refuses a banned login." : "Works with the round-2 server update (the server running now ignores it).")));
    },

    create() {
      const user = h("input", { class: "gw-acc-input", type: "text", maxlength: 32, autocomplete: "off", spellcheck: "false", "aria-label": "User name", pattern: "[A-Za-z0-9_]{3,32}" });
      const pw = passwordBox("gw-acc-pw1"), pw2 = passwordBox("gw-acc-pw2");
      const email = h("input", { class: "gw-acc-input", type: "email", maxlength: 255, "aria-label": "E-mail (optional)" });
      const tier = h("select", { class: "gw-acc-input", "aria-label": "Donator tier" }, Object.entries(TIERS).map(([k, v]) => h("option", { value: k }, k + " - " + v)));
      const err = h("div", { class: "tt-bad", role: "alert" });
      const go = h("button", { class: "gw-btnx", type: "button", disabled: !this.data.writable || null }, "Create account");
      const body = h("div", { class: "gw-acc-new" },
        h("div", { class: "gw-acc-grid" },
          h("label", { "data-field": "accounts.username", for: "gw-acc-user" }, "User name"), Object.assign(user, { id: "gw-acc-user" }),
          h("label", { "data-field": "accounts.password", for: "gw-acc-pw1" }, "Password"), pw.el,
          h("label", { for: "gw-acc-pw2" }, "Password again"), pw2.el,
          h("label", { "data-field": "accounts.email" }, "E-mail"), email,
          h("label", { "data-field": "accounts.donator_tier" }, "Donator tier"), tier),
        h("p", { class: "gw-dim" }, "User name: 3-32 letters, digits or _ (the login is case-sensitive). Password: 4-32 characters. ",
          "The account works at once: log in with it in the game client and create a character as usual."),
        err, h("div", { class: "gw-confirm-buttons" }, go));
      const m = GW.modal(body, { title: "New account", wide: true });
      setTimeout(() => user.focus(), 0);
      go.addEventListener("click", async () => {
        err.textContent = "";
        if (!/^[A-Za-z0-9_]{3,32}$/.test(user.value)) { err.textContent = "The user name must be 3-32 letters, digits or _."; return; }
        if (pw.input.value.length < 4) { err.textContent = "The password must be at least 4 characters."; return; }
        if (pw.input.value !== pw2.input.value) { err.textContent = "The two passwords are not the same."; return; }
        go.disabled = true; go.textContent = "Creating ...";
        try {
          const r = await this.ctx.api("create", { username: user.value, password: pw.input.value, email: email.value, donator_tier: Number(tier.value) });
          m.close();
          GW.writeDone(r, "account " + user.value + " created");
          await this.load("");
          if (r.account) this.show(r.account.id);
          GW.alert("Account " + user.value + " is ready.\n\n" + (r.note || ""), "Account created");
        } catch (e) { err.textContent = e.message; go.disabled = false; go.textContent = "Create account"; }
      });
    },
  });
})();
