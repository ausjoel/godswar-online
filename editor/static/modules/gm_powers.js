/* static/modules/gm_powers.js - "GM powers" (wave 5 B). Backend: modules/gm_powers.py.
   WHO is a GM (the character flag: grant / revoke, applies at the next command) and WHAT a GM may do (one switch per
   command or power in public.gm_power_policy, read by the game within 5 seconds; a switched-off command is refused by
   the server, not just hidden). Defaults = everything on = the behaviour before this page existed. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const plural = (n, w) => n + " " + w + (n === 1 ? "" : "s");
  const when = t => t ? String(t).replace("T", " ").slice(0, 16) : "";

  GW.module("gm_powers", {
    async mount(el, ctx) {
      const body = h("div");
      el.append(body);

      async function load() {
        const d = await ctx.api("overview");
        GW.clear(body);
        const pending = {};                                   // key -> wanted state, not saved yet
        const want = p => (p.key in pending ? pending[p.key] : p.allowed);
        const master = d.powers.find(p => p.key === "master");
        const status = h("p", { role: "status", class: "gw-dim" });
        const saveBtn = h("button", { type: "button", class: "gw-btnx", disabled: true, onclick: save }, "Save switches");
        const undoBtn = h("button", { type: "button", class: "gw-btnx", disabled: true, onclick: () => load() }, "Discard");
        const canSave = d.ready && d.writable;

        // ---- the picture at a glance
        const on = d.powers.filter(p => p.key !== "master" && p.allowed).length, total = d.powers.length - 1;
        body.append(h("div", { class: "gw-frame-simplewindow gmp-panel" }, h("h2", null, "GM powers on this server"),
          h("div", { class: "gmp-cards" },
            card("GM commands", d.commands_enabled === false ? "OFF at start-up" : d.master ? "ON" : "OFF (master switch)",
              d.commands_enabled === false ? "GODSWAR_DEVELOPER_COMMANDS_ENABLED is off: nobody is a GM until the game is restarted with it on" : d.master ? plural(on, "power") + " of " + total + " allowed" : "every GM power is refused", d.master && d.commands_enabled !== false ? "gmp-on" : "gmp-off"),
            card("Who is a GM", d.authority === "character_flag" ? "Character flag" : d.authority === "account_allowlist" ? "Account list" : "?", d.who_rule),
            card("GMs now", String(d.gms.filter(g => g.works).length), d.authority === "character_flag" ? d.gms.filter(g => g.works).map(g => g.name).join(", ") || "nobody" : plural(d.allowlist.length, "account") + " on the allow list: " + (d.allowlist.map(a => a.username).join(", ") || "none"))),
          d.why_not_ready ? h("p", { class: "tt-bad" }, d.why_not_ready) : h("p", { class: "gw-dim" }, "A switch " + d.applies_text + "."),
          d.pending && d.pending.length ? h("p", { class: "gw-dim" }, "Saved but not yet read by the game (it polls every 5 seconds): " + d.pending.join(", ")) : null));

        // ---- master switch
        if (master) {
          const mBox = h("input", { type: "checkbox", checked: master.allowed || null, disabled: !canSave || null, "aria-label": master.label,
            onchange: () => { toggle(master, mBox.checked); } });
          body.append(h("div", { class: "gw-frame-simplewindow gmp-panel" }, h("h2", null, "Master switch"),
            h("label", { class: "gmp-row gmp-master" }, mBox, h("span", null, h("strong", null, "GM powers are " + (master.allowed ? "ON" : "OFF")), h("br"), h("span", { class: "gw-dim" }, master.description)))));
        }

        // ---- per group
        const rowsByKey = {};
        for (const g of d.groups) {
          if (g.key === "master") continue;
          const ps = d.powers.filter(p => p.group === g.key);
          if (!ps.length) continue;
          const count = h("span", { class: "gw-dim" });
          const setAll = v => { for (const p of ps) { toggle(p, v); rowsByKey[p.key].box.checked = v; } };
          const panel = h("div", { class: "gw-frame-simplewindow gmp-panel" },
            h("div", { class: "gmp-head" }, h("h3", null, g.title), count,
              h("button", { type: "button", class: "gw-btnx small", disabled: !canSave || null, onclick: () => setAll(true) }, "All on"),
              h("button", { type: "button", class: "gw-btnx small", disabled: !canSave || null, onclick: () => setAll(false) }, "All off")));
          for (const p of ps) {
            const box = h("input", { type: "checkbox", checked: p.allowed || null, disabled: !canSave || null, "aria-label": p.label, onchange: () => toggle(p, box.checked) });
            const state = h("span", { class: "gmp-state" });
            rowsByKey[p.key] = { box, state, p };
            panel.append(h("label", { class: "gmp-row" + (p.risk === "high" ? " gmp-high" : "") }, box,
              h("span", { class: "gmp-what" }, h("code", null, p.label), p.risk === "high" ? h("span", { class: "gmp-tag" }, "changes the game") : null, h("br"),
                h("span", { class: "gw-dim" }, p.description)), state));
          }
          panel.count = () => { count.textContent = ps.filter(want).length + " of " + ps.length + " on"; };
          panel.count(); rowsByKey["group:" + g.key] = panel;
          body.append(panel);
        }
        function paint() {
          const masterOn = master ? want(master) : true;
          for (const k of Object.keys(rowsByKey)) { const r = rowsByKey[k]; if (k.startsWith("group:")) { r.count(); continue; }
            const w = want(r.p), changed = r.p.key in pending;
            r.state.textContent = (w ? (masterOn ? "allowed" : "allowed, but the master switch is off") : "refused") + (changed ? " (not saved)" : r.p.updated_by && !r.p.allowed ? " · off since " + when(r.p.updated_at) : "");
            r.state.className = "gmp-state " + (w && masterOn ? "gmp-on" : "gmp-off"); }
          const n = Object.keys(pending).length;
          saveBtn.disabled = !n || !canSave; undoBtn.disabled = !n;
          status.textContent = n ? plural(n, "change") + " not saved." : "";
        }
        function toggle(p, v) { if (v === p.allowed) delete pending[p.key]; else pending[p.key] = v; paint(); }
        paint();
        body.append(h("div", { class: "gmp-save" }, saveBtn, undoBtn, status));
        async function save() {
          const off = Object.keys(pending).filter(k => !pending[k]), back = Object.keys(pending).filter(k => pending[k]);
          if (pending.master === false && !await GW.confirm({ title: "Switch every GM power off?", danger: true, ok: "Switch GM powers off",
            message: "Every GM command, the GM Mall and the gate skip are refused for every GM within 5 seconds, until this switch is on again. GM flags are kept. The editor still works." })) return;
          else if (off.length && pending.master !== false && !await GW.confirm({ title: "Switch " + plural(off.length, "GM power") + " off?", ok: "Switch off",
            message: off.join(", ") + "\n\nThe game refuses these for every GM within 5 seconds." + (back.length ? "\n\nSwitched back on: " + back.join(", ") : "") })) return;
          try { const res = await ctx.api("save", { changes: Object.assign({}, pending), version: d.version }); GW.writeDone(res, "GM powers saved"); await load(); }
          catch (e) { await GW.writeError(e); if (e.status === 409) await load(); }
        }

        // ---- who is a GM
        const who = h("div", { class: "gw-frame-simplewindow gmp-panel" }, h("h2", null, "Who is a GM"), h("p", { class: "gw-dim" }, d.who_rule));
        who.append(h("table", { class: "gmp-table" }, h("thead", null, h("tr", null, ["Character", "Level", "Account", "Server", "Now", "GM rights", ""].map(t => h("th", null, t)))),
          h("tbody", null, d.gms.length ? d.gms.map(g => h("tr", null, h("td", null, g.name), h("td", null, String(g.level)), h("td", null, g.username + " (#" + g.account_id + ")"), h("td", null, String(g.realm)),
            h("td", null, g.online ? "online" : "offline"), h("td", { class: g.works ? "gmp-on" : "gmp-off" }, g.works ? (d.authority === "character_flag" ? "yes" : "flag set, but the account list decides") : "no: " + (g.why_not || "")),
            h("td", null, h("button", { type: "button", class: "gw-btnx small danger", disabled: !d.writable || null, onclick: () => setGm(g, false) }, "Revoke")))) : h("tr", null, h("td", { colspan: 7, class: "gw-dim" }, "No character has the GM flag.")))));
        if (d.allowlist.length || d.authority === "account_allowlist") who.append(h("p", { class: "gw-dim" }, "Account allow list (GODSWAR_DEVELOPER_ACCOUNT_IDS, a root-owned start-up setting" + (d.authority === "character_flag" ? "; NOT used while the character flag decides" : "") + "): " + (d.allowlist.map(a => a.username + " (#" + a.id + ")").join(", ") || "empty")));
        const q = h("input", { type: "text", maxlength: 32, placeholder: "Start of a character name", "aria-label": "Character name" }), found = h("div");
        async function search() {
          GW.clear(found);
          try { const r = await ctx.api("find?q=" + encodeURIComponent(q.value.trim()));
            if (!r.characters.length) { found.append(h("p", { class: "gw-dim" }, "No live character starts with that.")); return; }
            found.append(h("table", { class: "gmp-table" }, h("tbody", null, r.characters.map(c => h("tr", null, h("td", null, c.name), h("td", null, "level " + c.level), h("td", null, c.username + " (#" + c.account_id + ")"), h("td", null, c.online ? "online" : "offline"),
              h("td", null, c.gm === 1 ? h("span", { class: "gmp-on" }, "is a GM") : h("button", { type: "button", class: "gw-btnx small", disabled: !d.writable || null, onclick: () => setGm(c, true) }, "Make GM")))))));
          } catch (e) { found.append(h("p", { class: "tt-bad" }, e.message)); }
        }
        who.append(h("h3", null, "Make a character a GM"), h("div", { class: "gmp-line" }, q, h("button", { type: "button", class: "gw-btnx", onclick: search }, "Find")), found);
        body.append(who);
        async function setGm(c, value) {
          const last = !value && d.gms.filter(g => g.works).length === 1 && c.works;
          if (!await GW.confirm({ title: (value ? "Make " + c.name + " a GM?" : "Revoke GM rights of " + c.name + "?"), danger: !value, ok: value ? "Make GM" : "Revoke",
            message: value ? c.name + " (account " + c.username + ") can use every GM power that is switched on, from the next command. No relog needed."
              : c.name + " loses GM rights at the next command; active /god, /speed and the like end at logout." + (last ? "\n\nThis is the last working GM. GM rights can always be granted again here." : "") })) return;
          try { const res = await ctx.api("grant", { character_id: c.id, gm: value, expected: c.gm }); GW.writeDone(res, (value ? "GM granted: " : "GM revoked: ") + c.name); await load(); }
          catch (e) { await GW.writeError(e); if (e.status === 409) await load(); }
        }

        // ---- the rest of the picture + log
        body.append(h("div", { class: "gw-frame-simplewindow gmp-panel" }, h("h2", null, "Also part of what a GM can do (set elsewhere)"),
          h("ul", null, d.not_here.map(n => h("li", null, n.what, " ", n.link ? h("a", { href: n.link }, "(" + n.where + ")") : h("span", { class: "gw-dim" }, "(" + n.where + ")"))))));
        const logBox = h("div");
        body.append(h("div", { class: "gw-frame-simplewindow gmp-panel" }, h("h2", null, "Log"),
          h("button", { type: "button", class: "gw-btnx small", onclick: showLog }, "Show switch changes and refused commands"), logBox));
        async function showLog() {
          GW.clear(logBox);
          try { const l = await ctx.api("log");
            logBox.append(h("h3", null, "Switch changes"), l.changes.length ? h("table", { class: "gmp-table" }, h("tbody", null, l.changes.map(c => h("tr", null, h("td", null, when(c.at)), h("td", null, c.power_key), h("td", null, c.new_allowed === false ? "switched OFF" : "switched ON"), h("td", { class: "gw-dim" }, c.changed_by))))) : h("p", { class: "gw-dim" }, "None yet."),
              h("h3", null, "Commands the game refused"), l.refused.length ? h("table", { class: "gmp-table" }, h("tbody", null, l.refused.map(c => h("tr", null, h("td", null, when(c.at)), h("td", null, c.actor_character), h("td", null, c.command), h("td", { class: "gw-dim" }, c.detail))))) : h("p", { class: "gw-dim" }, "None yet."),
              ...l.notes.map(n => h("p", { class: "gw-dim" }, n)));
          } catch (e) { logBox.append(h("p", { class: "tt-bad" }, e.message)); }
        }
      }
      function card(title, value, sub, cls) { return h("div", { class: "gmp-card" }, h("div", { class: "gmp-card-t" }, title), h("div", { class: "gmp-card-v " + (cls || "") }, value), h("div", { class: "gw-dim gmp-card-s" }, sub || "")); }
      await load().catch(e => body.append(h("p", { class: "tt-bad" }, e.message)));
    },
    unmount() {}
  });
})();
