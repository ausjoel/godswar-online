(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const TABS = [["server", "Game server (gw251)"], ["monitor", "Resource monitor"], ["realms", "Servers (realms)"], ["changes", "Change log"]];
  const MB = n => n === null || n === undefined ? "?" : n >= 1e9 ? (n / 1e9).toFixed(2) + " GB" : (n / 1e6).toFixed(0) + " MB";
  const RATE = n => n === null || n === undefined ? "?" : n >= 1e6 ? (n / 1e6).toFixed(1) + " MB/s" : (n / 1e3).toFixed(1) + " kB/s";
  const DUR = s => { if (s === null || s === undefined) return "?"; s = Math.floor(s); const d = Math.floor(s / 86400), hh = Math.floor(s % 86400 / 3600), m = Math.floor(s % 3600 / 60); return (d ? d + "d " : "") + hh + "h " + m + "m"; };
  const plural = (n, w) => n + " " + w + (n === 1 ? "" : "s");

  // A small sparkline: values (numbers or null) -> inline SVG, scaled 0..max.
  function spark(values, max, cls) {
    const W = 220, H = 40, n = values.length;
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("viewBox", "0 0 " + W + " " + H); svg.setAttribute("class", "ops-spark " + (cls || ""));
    svg.setAttribute("preserveAspectRatio", "none"); svg.setAttribute("aria-hidden", "true");
    const top = Math.max(max || 0, ...values.filter(v => v !== null && v !== undefined), 1e-9);
    let d = "";
    values.forEach((v, i) => { if (v === null || v === undefined) return; const x = n < 2 ? W : i * W / (n - 1), y = H - 2 - (H - 4) * v / top; d += (d ? "L" : "M") + x.toFixed(1) + " " + y.toFixed(1); });
    const p = document.createElementNS("http://www.w3.org/2000/svg", "path");
    p.setAttribute("d", d || "M0 " + (H - 2) + "L" + W + " " + (H - 2)); svg.append(p);
    return svg;
  }

  GW.module("operations", {
    async mount(el, ctx) {
      const self = this;
      self.timers = [];
      self.alive = true;
      const tabbar = h("div", { class: "ops-tabs", role: "tablist" }), body = h("div", { class: "ops-body" });
      el.append(tabbar, body);
      let tab = (ctx.route && ctx.route[0]) || "server";
      function draw() {
        GW.clear(tabbar);
        for (const [k, label] of TABS) tabbar.append(h("button", { type: "button", role: "tab", class: "gw-btnx" + (k === tab ? " on" : ""), "aria-selected": String(k === tab), onclick: () => { tab = k; ctx.navigate(k); } }, label));
        self.stopTimers(); GW.clear(body);
        ({ server, monitor, realms, changes })[tab](body).catch(e => body.append(h("p", { class: "tt-bad" }, e.message)));
      }
      draw();

      // ------------------------------------------------------------ Game server (gw251)
      async function server(box) {
        const d = await ctx.api("overview");
        const out = h("pre", { class: "ops-out", hidden: true }), op = h("p", { class: "gw-dim", role: "status" });
        const statusBox = h("div", { class: "ops-cards" });
        const players = h("p", { class: "ops-players" });
        function showStatus(o) {
          const s = o.service || {};
          GW.clear(statusBox).append(
            card("Service", (s.ActiveState || "?") + " / " + (s.SubState || "?"), s.error || ("since " + (s.ActiveEnterTimestamp || "?"))),
            card("Main PID", s.MainPID || "-", "restarts by systemd: " + (s.NRestarts || "0")),
            card("Players online", o.players_online === null ? "?" : String(o.players_online), o.players_detail && o.players_detail.accounts !== undefined ? plural(o.players_detail.accounts, "signed-in account") + (o.players_detail.connections === null || o.players_detail.connections === undefined ? "" : ", " + plural(o.players_detail.connections, "open connection")) : ""),
            card("Database", o.database || "?", o.writable ? "writable" : "read-only"));
          players.textContent = o.players_online > 0 ? o.players_online + (o.players_online === 1 ? " player is" : " players are") + " online. Stop or restart disconnects them; a restore is refused until nobody is online." : "Nobody is online.";
          players.className = "ops-players" + (o.players_online > 0 ? " tt-bad" : "");
        }
        showStatus(d);
        async function refresh() { try { const o = await ctx.api("overview"); Object.assign(d, o); showStatus(o); } catch (e) { GW.writeError(e); } }
        async function waitJob(label, job) {
          while (job && job.state === "running" && self.alive) {
            await new Promise(r => setTimeout(r, 1500));
            job = await ctx.api("job/" + job.job);
            op.textContent = label + ": " + job.state + (job.error ? " · " + job.error : "");
          }
          return job;
        }
        async function run(action, button, extra) {
          await refresh();
          const n = d.players_online || 0, disruptive = action === "stop" || action === "restart";
          let ack = false;
          if (action !== "logs") {
            const tick = h("input", { type: "checkbox" });
            const warn = disruptive && n > 0;
            const text = { start: "Start godswar251.service and wait until the game is ready.", backup: "Dump the database now (kept with the nightly dumps, 14 days). Players are not disconnected.",
              stop: "Players' state is saved by the game's shutdown path; the server stays down until someone starts it.", restart: "Players' state is saved, the service starts again and the editor waits up to 45 seconds for readiness." }[action];
            const box2 = h("div", null, h("p", { class: warn ? "tt-bad" : "" }, h("b", null, n + (n === 1 ? " player is" : " players are") + " online now."), warn ? " They will be disconnected. Prefer to warn them in chat and wait for an empty world." : ""), h("p", null, text), warn ? h("label", null, tick, " I understand " + plural(n, "player") + " will be disconnected") : null);
            if (!await GW.confirm({ title: action[0].toUpperCase() + action.slice(1) + (action === "backup" ? " the database?" : " the game server?"), el: box2, ok: action[0].toUpperCase() + action.slice(1), danger: disruptive })) return;
            if (warn && !tick.checked) { GW.status("Tick the box to confirm the players will be disconnected.", "err"); return; }
            ack = warn;
          }
          button.disabled = true;
          try {
            const r = await ctx.api("control", Object.assign({ action, players_seen: n, acknowledge_players: ack }, extra || {}));
            const label = "gw251 " + action; op.textContent = label + ": " + r.job.state;
            const job = await waitJob(label, r.job);
            if (job && (job.output || job.error)) { out.hidden = false; out.textContent = job.output || job.error; }
            GW.status(label + ": " + (job ? job.state : "?"), job && job.state === "failed" ? "err" : undefined);
            if (action !== "logs") GW.emit("change");
          } catch (e) { await GW.writeError(e); }
          finally { button.disabled = !d.control_available; await refresh(); }
        }
        const btn = (label, action, cls, extra) => { const b = h("button", { type: "button", class: "gw-btnx" + (cls ? " " + cls : ""), disabled: !d.control_available || null }, label); b.onclick = () => run(action, b, extra && extra()); return b; };
        const lines = h("select", { "aria-label": "Log lines" }, [50, 100, 200, 300].map(n => h("option", { value: String(n), selected: n === 100 || null }, n + " lines")));
        const checkOut = h("div", { class: "ops-check" });
        const checkBtn = h("button", { type: "button", class: "gw-btnx", onclick: async () => {
          checkBtn.disabled = true;
          try { const r = await ctx.api("check"); GW.clear(checkOut).append(h("table", { class: "ops-table" }, h("tbody", null, r.checks.map(c => h("tr", null, h("td", { class: c.ok ? "ops-ok" : "ops-bad" }, c.ok ? "OK" : "FAIL"), h("td", null, c.name), h("td", { class: "gw-dim" }, c.detail)))))); }
          catch (e) { GW.writeError(e); } finally { checkBtn.disabled = false; }
        } }, "Check (gw251 check)");
        const logsBtn = h("button", { type: "button", class: "gw-btnx", disabled: !d.control_available || null }, "Logs");
        logsBtn.onclick = () => run("logs", logsBtn, { lines: Number(lines.value) });
        box.append(
          h("div", { class: "gw-frame-simplewindow ops-panel" },
            h("h2", null, "gw251 status"), statusBox, players,
            h("div", { class: "ops-line" }, h("button", { type: "button", class: "gw-btnx", onclick: refresh }, "Status (gw251 status)"), checkBtn),
            checkOut,
            h("h2", null, "Commands"),
            d.control_available ? null : h("p", { class: "tt-bad" }, "Server commands are off: this editor is not the live editor, or the root helper socket (/run/gweditor-control.sock) is missing."),
            h("div", { class: "ops-line" }, btn("Start", "start"), btn("Restart", "restart"), btn("Stop", "stop", "danger"), btn("Back up database", "backup"), logsBtn, lines),
            op, out),
          restorePanel(),
          featuresPanel(),
          jobsPanel(d.jobs || []));

        function featuresPanel() {
          const inner = h("div");
          const det = h("details", { class: "ops-panel" }, h("summary", null, "Feature switches (read-only)"), inner);
          det.addEventListener("toggle", async () => {
            if (!det.open || inner.children.length) return;
            try {
              const f = await ctx.api("features");
              inner.append(h("p", { class: "gw-dim" }, f.error || f.note + " Drop-ins: " + (f.dropins || []).join(", ")),
                h("table", { class: "ops-table" }, h("tbody", null, (f.switches || []).map(x => h("tr", null, h("td", null, h("code", null, x.name)), h("td", null, x.value))))));
            } catch (e) { inner.append(h("p", { class: "tt-bad" }, e.message)); }
          });
          return det;
        }

        function restorePanel() {
          const list = h("div", { class: "ops-backups" });
          const panel = h("div", { class: "gw-frame-simplewindow ops-panel" }, h("h2", null, "Restore a backup (gw251 restore)"),
            h("p", { class: "gw-dim" }, d.restore_rule),
            h("button", { type: "button", class: "gw-btnx", disabled: !d.control_available || null, onclick: listBackups }, "List backups"), list);
          async function listBackups() {
            GW.clear(list).append(h("p", { class: "gw-dim" }, "Asking the helper…"));
            try {
              const r = await ctx.api("control", { action: "backups" });
              const job = await waitJob("backups", r.job);
              GW.clear(list);
              if (!job || job.state !== "succeeded") { list.append(h("p", { class: "tt-bad" }, (job && job.error) || "failed")); return; }
              if (!(job.backups || []).length) { list.append(h("p", null, "No backup files found.")); return; }
              list.append(h("table", { class: "ops-table" }, h("tbody", null, job.backups.map(b => h("tr", null,
                h("td", null, h("code", null, b.file)), h("td", { class: "gw-dim" }, b.folder), h("td", null, MB(b.bytes)), h("td", { class: "gw-dim" }, new Date(b.time).toLocaleString("en-AU")),
                h("td", null, h("button", { type: "button", class: "gw-btnx small danger", onclick: () => restore(b) }, "Restore…")))))));
            } catch (e) { GW.clear(list); GW.writeError(e); }
          }
          async function restore(b) {
            await refresh();
            if (d.players_online > 0) { GW.alert(plural(d.players_online, "player") + " online. A restore is allowed only when nobody is online.", "Restore refused"); return; }
            const typed = h("input", { type: "text", autocomplete: "off", spellcheck: "false", "aria-label": "Type the backup file name" });
            const ok = await GW.confirm({ title: "Restore the whole database?", danger: true, ok: "Restore", el: h("div", null,
              h("p", { class: "tt-bad" }, h("b", null, "Every account, character and setting goes back to " + new Date(b.time).toLocaleString("en-AU") + ".")),
              h("p", null, "The game server is stopped, the current database is dumped to pre-restore/ first, then " + b.file + " is loaded and the server is started again. If loading fails the pre-restore dump is put back. The editor and the sign-up site lose their database connection for a moment."),
              h("p", null, "Type the file name to confirm:"), h("code", null, b.file), typed) });
            if (!ok) return;
            if (typed.value.trim() !== b.file) { GW.status("The typed name does not match; nothing was restored.", "err"); return; }
            try {
              const r = await ctx.api("control", { action: "restore", file: b.file, confirm: typed.value.trim() });
              const job = await waitJob("restore " + b.file, r.job);
              out.hidden = false; out.textContent = (job && (job.output || job.error)) || "";
              GW.status("restore: " + (job ? job.state : "?"), job && job.state === "failed" ? "err" : undefined);
              GW.emit("change");
            } catch (e) { GW.writeError(e); }
          }
          return panel;
        }
        function jobsPanel(jobs) {
          return h("details", { class: "ops-panel" }, h("summary", null, "Recent server operations (" + jobs.length + ")"),
            jobs.map(j => h("p", { class: "gw-dim" }, (j.started_at || "").replace("T", " ").slice(0, 19) + " · " + j.action + " · " + j.state + (j.error ? " · " + j.error : ""))));
        }
      }
      function card(title, value, sub, graph) { return h("div", { class: "ops-card" }, h("div", { class: "ops-card-t" }, title), h("div", { class: "ops-card-v" }, value), h("div", { class: "ops-card-s gw-dim" }, sub || ""), graph || null); }

      // ------------------------------------------------------------ Resource monitor
      async function monitor(box) {
        const tiles = h("div", { class: "ops-cards" }), svcBox = h("div"), note = h("p", { class: "gw-dim" });
        const auto = h("input", { type: "checkbox", checked: true });
        box.append(h("div", { class: "gw-frame-simplewindow ops-panel" }, h("h2", null, "Server resources"),
          h("label", { class: "ops-auto" }, auto, " refresh every 5 seconds"), tiles, h("h3", null, "Services"), svcBox, note));
        async function load() {
          let m;
          try { m = await ctx.api("monitor"); } catch (e) { note.textContent = "Monitor unavailable: " + e.message; return; }
          const hist = m.history || [], last = m.now || {};
          const col = k => hist.map(r => r[k]);
          const mem = m.memory || {};
          GW.clear(tiles).append(
            card("CPU", last.cpu_pct === null || last.cpu_pct === undefined ? "…" : last.cpu_pct + " %", plural(m.cores, "core"), spark(col("cpu_pct"), 100, "cpu")),
            card("Memory", mem.total ? mem.used_pct + " %" : "?", mem.total ? MB(mem.used) + " of " + MB(mem.total) + (mem.swap_total ? " · swap " + MB(mem.swap_used) : "") : "", spark(col("mem_pct"), 100, "mem")),
            card("Load", m.load ? m.load.map(x => x.toFixed(2)).join(" ") : "?", "1 / 5 / 15 min", spark(col("load1"), m.cores, "load")),
            card("Network", RATE(last.net_rx) + " in", RATE(last.net_tx) + " out", spark(col("net_rx"), 0, "net")),
            card("Disk", (m.disks || []).map(x => x.used_pct + " %").join(" · ") || "?", (m.disks || []).map(x => x.mount + " " + (x.free / 1e9).toFixed(1) + " GB free").join(" · ")),
            card("Uptime", DUR(m.uptime), "machine · game server " + DUR(m.game_uptime)),
            card("Players online", m.players_online === null ? "?" : String(m.players_online), "last hour", spark(col("players"), 0, "players")));
          const rows = (m.services || []).map(name => {
            const s = (last.services || {})[name];
            return h("tr", null, h("td", null, name), s ? h("td", { class: s.running ? "ops-ok" : "ops-bad" }, s.running ? "running" : "stopped") : h("td", { class: "gw-dim" }, "no cgroup"),
              h("td", null, s ? MB(s.memory) : "-"), h("td", null, s && s.cpu_pct !== null ? s.cpu_pct + " %" : "-"), h("td", null, s && s.pids !== null ? String(s.pids) : "-"),
              h("td", null, spark(hist.map(r => r.services && r.services[name] ? r.services[name].memory : null), 0, "mem")),
              h("td", null, spark(hist.map(r => r.services && r.services[name] ? r.services[name].cpu_pct : null), 0, "cpu")));
          });
          GW.clear(svcBox).append(h("table", { class: "ops-table" }, h("thead", null, h("tr", null, ["Service", "State", "Memory", "CPU (of one core)", "Tasks", "Memory, last hour", "CPU, last hour"].map(t => h("th", null, t)))), h("tbody", null, rows)));
          note.textContent = "Sampled every " + m.interval + " s by the editor (from /proc and the systemd cgroups; nothing is stored, the history restarts with the editor). " + hist.length + " samples. Graphs show the last hour.";
        }
        await load();
        self.timers.push(setInterval(() => { if (auto.checked && self.alive) load(); }, 5000));
      }

      // ------------------------------------------------------------ Servers (realms)
      async function realms(box) {
        const d = await ctx.api("realms");
        const list = h("div"), form = h("div", { class: "ops-realm-form" });
        const sleep = ms => new Promise(r => setTimeout(r, ms));
        const redraw = async () => { GW.clear(box); await realms(box); };
        box.append(h("div", { class: "gw-frame-simplewindow ops-panel" }, h("h2", null, "Server list (realms)"),
          h("ul", { class: "gw-dim" }, d.how.map(t => h("li", null, t))), list,
          h("div", { class: "ops-line" },
            h("button", { type: "button", class: "gw-btnx", disabled: !d.writable || null, onclick: () => guide(null) }, "Add a server"),
            h("button", { type: "button", class: "gw-btnx small", disabled: !d.writable || null, onclick: () => edit(null) }, "Add a row by hand (advanced)")),
          h("p", { class: "gw-dim" }, d.character_rule || ""), form));
        const procText = r => { const p = r.process; if (!p || !p.state) return r.id === 1 ? "main game process" : "not set up";
          if (p.problem) return p.problem; if (p.state === "running") return "set-up running…";
          if (p.set_up) return "running on port " + p.port + (p.outside && p.outside.result === "seen" ? ", seen from outside" : p.outside && p.outside.result === "not_seen" ? ", NOT seen from outside" : "");
          return p.state === "removed" ? "taken down" : "set-up failed: " + (p.error || p.state); };
        GW.clear(list).append(h("table", { class: "ops-table" },
          h("thead", null, h("tr", null, ["#", "Name", "Address", "Port", "Limit", "Order", "Enabled", "Recommended", "Game answers", "Game process", "Characters", "Used by", ""].map(t => h("th", null, t)))),
          h("tbody", null, d.realms.map(r => { const setUp = !!(r.process && r.process.set_up);
            return h("tr", null, h("td", null, String(r.id)), h("td", { title: r.placeholder || null }, r.placeholder ? r.name + " (placeholder)" : r.name), h("td", null, r.ip_address), h("td", null, String(r.game_port)),
            h("td", null, String(r.server_limit)), h("td", null, String(r.display_order)), h("td", { class: r.enabled ? "ops-ok" : "gw-dim" }, r.enabled ? "yes" : "no"),
            h("td", null, r.recommended ? "yes" : ""), h("td", { class: r.reachable ? "ops-ok" : "ops-bad" }, r.reachable ? "yes" : "no"),
            h("td", { class: r.process && (r.process.problem || r.process.state === "failed") ? "ops-bad" : "gw-dim" }, procText(r)),
            h("td", null, r.characters === undefined || r.characters === null ? "?" : String(r.characters)),
            h("td", { class: "gw-dim" }, (r.references || []).map(x => x.table.replace("public.", "") + " " + (x.rows === null ? "?" : x.rows)).join(", ") || "nothing"),
            h("td", null, h("button", { type: "button", class: "gw-btnx small", disabled: !d.writable || null, onclick: () => edit(r) }, "Edit"),
              r.id >= 2 && r.id <= 255 && !r.enabled ? h("button", { type: "button", class: "gw-btnx small", disabled: !d.writable || null, onclick: () => guide(r.id) }, setUp ? "Test again" : "Set up") : null,
              r.id !== 1 && (r.enabled || setUp) ? h("button", { type: "button", class: "gw-btnx small danger", disabled: !d.writable || null, onclick: () => closeServer(r, false) }, "Close") : null,
              r.protected ? null : h("button", { type: "button", class: "gw-btnx small danger", disabled: !d.writable || null, onclick: () => closeServer(r, true) }, "Delete"))); }))));

        // ---- guided "Add a server": address detected, port suggested and checked, one click sets everything up
        async function guide(preferId) {
          let s;
          try { s = await ctx.api("realm/setup"); } catch (e) { GW.writeError(e); return; }
          const options = s.reuse.map(r => ({ mode: "existing", id: r.id, name: r.name, port: r.suggested_port,
            label: "Use " + r.name + " (server #" + r.id + ": already in the list, disabled, " + plural(r.characters, "character") + ")" }));
          if (s.next_id) options.push({ mode: "new", id: s.next_id, name: "", port: s.suggested_port, label: "A new server (#" + s.next_id + ")" });
          const fromRow = d.realms.find(r => r.id === preferId);
          if (fromRow && !options.some(o => o.id === preferId)) options.unshift({ mode: "existing", id: fromRow.id, name: fromRow.name, port: fromRow.game_port, label: "Set up " + fromRow.name + " (server #" + fromRow.id + ") again" });
          if (!options.length) { GW.clear(form).append(h("p", { class: "tt-bad" }, "No server number is free.")); return; }
          let pick = options.find(o => o.id === preferId) || options[0];
          const which = h("select", { "aria-label": "Which server", onchange: () => { pick = options[Number(which.value)]; name.value = pick.name; port.value = String(pick.port || ""); } },
            options.map((o, i) => h("option", { value: String(i), selected: o === pick || null }, o.label)));
          which.value = String(options.indexOf(pick));
          const name = h("input", { type: "text", maxlength: d.limits.name, value: pick.name });
          const port = h("input", { type: "number", min: s.port_range[0], max: s.port_range[1], value: String(pick.port || "") });
          const limit = h("input", { type: "number", min: 1, max: 100000, value: String(s.default_limit) });
          const steps = h("ol", { class: "ops-steps" }), status = h("p", { role: "status" }), actions = h("div", { class: "ops-line" });
          const go = h("button", { type: "button", class: "gw-btnx", disabled: !(s.writable && s.control_available && s.address.address) || null, onclick: () => run() }, "Set up server");
          GW.clear(form).append(...[h("h3", null, "Add a server"),
            h("label", null, "Which server", which),
            h("label", null, "Name in the server list (1-35 ASCII)", name),
            h("p", null, "Address players connect to: ", h("strong", null, s.address.address || "not detected"), s.address.source ? " (read from " + s.address.source + ", not typed)" : ""),
            s.address.note ? h("p", { class: "gw-dim" }, s.address.note) : null,
            h("label", null, "Game port (" + s.port_range[0] + "-" + s.port_range[1] + "; the suggestion is free on this machine)", port),
            h("label", null, "Player limit", limit),
            h("p", null, "What 'Set up server' does:"), h("ol", { class: "gw-dim" }, s.what_happens.map(t => h("li", null, t))),
            h("p", { class: s.handoff.shared ? "gw-dim" : "tt-bad" }, s.handoff.text),
            h("p", { class: "gw-dim" }, s.provider_rule), h("p", { class: "gw-dim" }, s.template_note),
            s.control_available ? null : h("p", { class: "tt-bad" }, "The root helper is not available here: set-up works only on the live game server."),
            h("div", { class: "ops-line" }, go, h("button", { type: "button", class: "gw-btnx", onclick: () => GW.clear(form) }, "Cancel")),
            status, steps, actions].filter(Boolean));          // a DOM append() would print a null as text
          function showSteps(p) { GW.clear(steps); for (const st of (p && p.steps) || []) steps.append(h("li", { class: st.ok ? "ops-ok" : "ops-bad" }, st.step + ": " + st.detail)); }
          function autoKnock(k) { if (typeof WebSocket === "undefined") return;      // works only if the site's CSP allows it; the link below always works
            try { const ws = new WebSocket("wss://" + k.host + ":" + k.port + "/"); ws.onerror = () => {}; setTimeout(() => { try { ws.close(); } catch (e) { /* closed */ } }, 3000); } catch (e) { /* blocked */ } }
          async function run() {
            const body = { mode: pick.mode, id: pick.id, name: name.value, port: Number(port.value), server_limit: Number(limit.value) };
            go.disabled = true; GW.clear(actions); GW.clear(steps); status.className = ""; status.textContent = "Setting up…";
            let res, job, knocking = false;
            try { res = await ctx.api("realm/provision", body); }
            catch (e) { go.disabled = false; status.textContent = ""; GW.writeError(e); return; }
            if (pick.mode === "new") { pick = { mode: "existing", id: res.realm_id, name: res.name, port: res.port, label: res.name }; }   // a second try reuses the row
            job = res.job;
            try {
              while (job && job.state === "running" && self.alive) {
                await sleep(1000);
                const p = await ctx.api("realm/process/" + res.realm_id); showSteps(p);
                if (p.outside && p.outside.result === "listening") { autoKnock(res.knock);
                  if (!knocking) { knocking = true; GW.clear(actions).append(
                    h("a", { class: "gw-btnx", href: res.knock.url, target: "_blank", rel: "noopener noreferrer" }, "Test from this computer"),
                    h("span", null, " Press now (about " + Math.round(p.outside.seconds) + " seconds). A new tab opens and shows an error page: that is expected, close it.")); } }
                else if (knocking && p.outside) { knocking = false; GW.clear(actions); }
                job = await ctx.api("job/" + job.job);
              }
            } catch (e) { go.disabled = false; GW.writeError(e); return; }
            GW.clear(actions); go.disabled = false; showSteps(job.realm);
            const rec = job.realm || {};
            if (job.state !== "succeeded") {
              status.className = "tt-bad"; status.textContent = "Not set up: " + (job.error || job.state);
              if (rec.code === "needs_restart") actions.append(h("button", { type: "button", class: "gw-btnx", onclick: () => ctx.navigate("server") }, "Go to Game server (restart at zero players)"), h("span", null, " Then come back and press Set up server again."));
              return;
            }
            await finish(res, job, false);
          }
          async function finish(res, job, ack) {
            try {
              const out = await ctx.api("realm/provision/finish", Object.assign({ id: res.realm_id, job: job.job }, ack ? { acknowledge_unreachable: true } : {}));
              GW.writeDone(out, "server #" + res.realm_id + " is in the server list");
              await redraw();
            } catch (e) {
              if (e.status !== 409 || ack) { status.className = "tt-bad"; status.textContent = e.message; return; }
              status.className = "tt-bad"; status.textContent = e.message;
              GW.clear(actions).append(h("button", { type: "button", class: "gw-btnx", onclick: () => run() }, "Test again"),
                h("button", { type: "button", class: "gw-btnx danger", onclick: async () => {
                  if (await GW.confirm({ title: "List the server without a passed test?", danger: true, ok: "List it anyway", message: "Only if you opened inbound TCP " + res.port + " in the Hosting by Design panel. If it is still blocked, everyone who picks this server is disconnected." })) await finish(res, job, true); } }, "I opened it: list the server anyway"));
            }
          }
        }

        // ---- "Close server" (process down, port closed, row hidden) and "Delete server" (also removes the row)
        async function closeServer(r, del) {
          const typed = h("input", { type: "text", "aria-label": "Server name" });
          const what = del ? "Delete server #" + r.id + " " + r.name + ": its game process is stopped and removed, its port is closed and its row is removed from the list. Its number is never reused."
            : "Close server #" + r.id + " " + r.name + ": it disappears from the server list, its game process is stopped and removed and its port is closed. The row stays (disabled), so it can be set up again.";
          const chars = r.characters ? plural(r.characters, "character") + " live on it. " : "No character lives on it. ";
          if (!await GW.confirm({ title: (del ? "Delete" : "Close") + " server #" + r.id + "?", danger: true, ok: del ? "Delete server" : "Close server",
            el: h("div", null, h("p", null, what), h("p", null, chars + (d.character_rule || "")), h("p", null, "Refused while anyone is in it. Type the server name to confirm:"), h("code", null, r.name), typed) })) return;
          try {
            const res = await ctx.api("realm/close", { id: r.id, confirm: typed.value, delete: del });
            let job = res.job;
            while (job && job.state === "running" && self.alive) { await sleep(1500); job = await ctx.api("job/" + job.job); }
            if (job && job.state !== "succeeded") throw new Error("The game process was not taken down: " + (job.error || job.state) + ". The row is disabled; nothing was deleted.");
            if (del) GW.writeDone(await ctx.api("realm/delete", { id: r.id, confirm: typed.value }), "server #" + r.id + " deleted");
            else GW.writeDone(res.row || res, "server #" + r.id + " closed");
            await redraw();
          } catch (e) { GW.writeError(e); }
        }
        function edit(r) {
          const isNew = !r, v = r ? Object.assign({}, r) : { id: d.next_id, name: "", identifier: d.new_identifier, ip_address: "", game_port: 7000, server_limit: 250, display_order: d.realms.length + 1, enabled: false, recommended: false };
          const f = {};
          const text = (k, label, attrs) => { f[k] = h("input", Object.assign({ type: "text", value: String(v[k]) }, attrs || {})); return h("label", null, label, f[k]); };
          const num = (k, label, lo, hi) => { f[k] = h("input", { type: "number", min: lo, max: hi, value: String(v[k]) }); return h("label", null, label, f[k]); };
          const chk = (k, label, attrs) => { f[k] = h("input", Object.assign({ type: "checkbox", checked: !!v[k] || null }, attrs || {})); return h("label", { class: "ops-chk" }, f[k], " " + label); };
          GW.clear(form).append(h("h3", null, isNew ? "New realm #" + v.id : "Realm #" + v.id),
            text("name", "Name in the server list (1-35 ASCII)", { maxlength: d.limits.name }),
            text("identifier", "Identifier (exactly 25 characters; fixed once added)", { maxlength: 25, readonly: isNew ? null : true }),
            text("ip_address", "Game server address the client connects to (IPv4 or DNS, at most 23)", { maxlength: d.limits.host }),
            num("game_port", "Game port", 1, 65535), num("server_limit", "Player limit", 1, 2147483647), num("display_order", "Order in the list", 0, 2147483647),
            chk("enabled", isNew ? "Enabled (add it disabled; enable once its game process answers)" : "Enabled (shown in the server list)", isNew ? { disabled: true } : null),
            chk("recommended", "Recommended (at most one enabled realm)"),
            h("div", { class: "ops-line" }, h("button", { type: "button", class: "gw-btnx", onclick: save }, isNew ? "Add realm" : "Save"), h("button", { type: "button", class: "gw-btnx", onclick: () => GW.clear(form) }, "Cancel")));
          async function save() {
            const body = { id: v.id, name: f.name.value, identifier: f.identifier.value, ip_address: f.ip_address.value.trim(), game_port: Number(f.game_port.value),
              server_limit: Number(f.server_limit.value), display_order: Number(f.display_order.value), enabled: f.enabled.checked, recommended: f.recommended.checked,
              expected: isNew ? null : r };
            if (!isNew && r.enabled && !body.enabled && !await GW.confirm({ title: "Disable realm " + r.name + "?", message: "It disappears from the server list at the next login. Characters on it stay in the database.", ok: "Disable", danger: true })) return;
            if (!isNew && body.enabled && r.enabled && (body.ip_address !== r.ip_address || body.game_port !== r.game_port) && !await GW.confirm({ title: "Move an enabled realm?", message: "Players who pick " + r.name + " at their next login go to " + body.ip_address + ":" + body.game_port + ". A typo here locks everyone out of this realm.", ok: "Save", danger: true })) return;
            const send = async ack => ctx.api("realm/save", Object.assign({}, body, ack ? { acknowledge_unreachable: true } : {}));
            try { let res;
              try { res = await send(false); }
              catch (e) {
                if (e.status !== 409 || !/Nothing answers/.test(e.message || "")) throw e;
                if (!await GW.confirm({ title: "Nothing answers there", message: e.message, ok: "Save anyway", danger: true })) return;
                res = await send(true);
              }
              GW.writeDone(res, "realm #" + v.id + " saved"); await redraw(); }
            catch (e) { GW.writeError(e); }
          }
        }
      }

      // ------------------------------------------------------------ Change log
      async function changes(box) {
        const s = await ctx.api("changes/summary");
        const out = h("p", { role: "status" });
        const exportBtn = h("button", { type: "button", class: "gw-btnx", onclick: download }, "Export (download JSON)");
        const clearBtn = h("button", { type: "button", class: "gw-btnx danger", onclick: clear }, "Clear change log…");
        box.append(h("div", { class: "gw-frame-simplewindow ops-panel" }, h("h2", null, "Change log housekeeping"),
          h("p", null, plural(s.records, "record") + " on disk" + (s.oldest ? " (" + s.oldest.slice(0, 10) + " to " + s.newest.slice(0, 10) + ")" : "") + "."),
          s.keep ? h("p", { class: "gw-dim" }, plural(s.keep, "change") + " still wait for a game restart; clearing keeps them (Home lists them): " + s.keep_list.map(x => x.action).join("; ")) : null,
          h("p", { class: "gw-dim" }, "Clearing first writes one export file with every record, then moves the records into web-changes/cleared-<time>/ on the editor's disk. Nothing is deleted, but Undo is no longer offered for archived changes."),
          s.archives.length ? h("p", { class: "gw-dim" }, "Earlier archives: " + s.archives.join(", ")) : null,
          h("div", { class: "ops-line" }, exportBtn, clearBtn), out));
        async function download() {
          try {
            const data = await ctx.api("changes/export");
            const blob = new Blob([JSON.stringify(data, null, 1)], { type: "application/json" });
            const a = h("a", { href: URL.createObjectURL(blob), download: "gw-editor-changes-" + data.exported_at.slice(0, 19).replace(/[:T]/g, "-") + ".json" });
            document.body.append(a); a.click(); a.remove(); setTimeout(() => URL.revokeObjectURL(a.href), 5000);
            out.textContent = "Downloaded " + plural(data.changes.length, "record") + ".";
            return true;
          } catch (e) { GW.writeError(e); return false; }
        }
        async function clear() {
          const typed = h("input", { type: "text", "aria-label": "Type CLEAR" }), dl = h("input", { type: "checkbox", checked: true });
          if (!await GW.confirm({ title: "Clear the change log?", danger: true, ok: "Clear", el: h("div", null,
            h("p", null, plural(s.records - s.keep, "record") + " will be archived; Undo will no longer be offered for them."),
            h("label", null, dl, " Download the export to this computer first"), h("p", null, "Type CLEAR to confirm:"), typed) })) return;
          if (typed.value.trim() !== "CLEAR") { GW.status("Type CLEAR to confirm; nothing was changed.", "err"); return; }
          if (dl.checked && !await download()) return;
          try {
            const r = await ctx.api("changes/clear", { confirm: "CLEAR", records_seen: s.records });
            GW.status("Change log cleared: " + plural(r.archived, "record") + " archived to " + r.folder + (r.kept ? "; " + r.kept + " kept" : ""), "ok");
            GW.emit("change"); GW.clear(box); await changes(box);
          } catch (e) { GW.writeError(e); }
        }
      }
    },
    stopTimers() { for (const t of this.timers || []) clearInterval(t); this.timers = []; },
    unmount() { this.alive = false; this.stopTimers(); },
  });
})();
