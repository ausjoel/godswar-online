/* static/modules/gates.js - level ranges AND opening times of every transporter, battlefield, instance and event
   (finalise2 P1: public.level_band_overrides + event_schedule_overrides, described by the server's GET /gate-content).
   Backend: modules/gates.py. Times are realm time (AEST on live). */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const DAYS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
  const MODES = [["compiled", "Default"], ["always", "Always open"], ["closed", "Closed"], ["windows", "Custom times"]];
  const hm = m => String(Math.floor(m / 60)).padStart(2, "0") + ":" + String(m % 60).padStart(2, "0");
  const toMin = s => { const m = /^(\d{1,2}):(\d{2})$/.exec(String(s).trim()); return m ? Number(m[1]) * 60 + Number(m[2]) : NaN; };
  const lv = v => v >= 200 ? "200" : String(v);
  const win = w => (w.day < 0 ? "closed" : DAYS[w.day] + " " + hm(w.start_minute) + "-" + hm((w.start_minute + w.duration_minutes) % 1440));
  // Manila (UTC+8) and the IGG guide's GMT-5, read-only, for comparing with old texts.
  const shifted = (w, hours) => { const t = (w.day * 1440 + w.start_minute + hours * 60 + 10080) % 10080; return DAYS[Math.floor(t / 1440)] + " " + hm(t % 1440); };

  GW.module("gates", {
    async mount(el, ctx) {
      const body = h("div");
      el.append(body);

      function windowEditor(initial) {
        const box = h("div", { class: "gw-gt-windows" });
        const rows = [];
        const add = w => {
          const day = h("select", { "aria-label": "Day" }, DAYS.map((d, i) => h("option", { value: i, selected: w && w.day === i ? "" : null }, d)));
          const start = h("input", { type: "text", value: w ? hm(w.start_minute) : "20:00", size: 5, "aria-label": "Start (AEST)" });
          const len = h("input", { type: "number", min: 1, max: 1440, value: w ? w.duration_minutes : 60, class: "gw-gt-num", "aria-label": "Length in minutes" });
          const r = { day, start, len, el: null };
          r.el = h("div", { class: "gw-gt-win" }, day, " ", start, " AEST for ", len, " min ",
            h("button", { class: "gw-btnx small", type: "button", onclick: () => { r.el.remove(); rows.splice(rows.indexOf(r), 1); } }, "Remove"));
          rows.push(r); list.append(r.el);
        };
        const list = h("div");
        box.append(list, h("button", { class: "gw-btnx small", type: "button", onclick: () => add(null) }, "Add an opening"));
        (initial || []).filter(w => w.day >= 0).forEach(add);
        box.read = () => rows.map(r => ({ day: Number(r.day.value), start_minute: toMin(r.start.value), duration_minutes: Number(r.len.value) }));
        return box;
      }

      async function load() {
        const D = await ctx.api("overview");
        GW.clear(body);
        body.append(h("p", null, "Who may go in and when: every transporter destination, battlefield, instance and event. Empty fields use the server's default (the admin's answers of 2026-11-05). ",
          "Level 200 means no cap. Times are server time, ", h("b", null, D.time_zone || "AEST"), ". Saved values reach the game within 15 seconds (no restart). ", GW.appliesChip("hot_reload")));
        if (!D.ready) { body.append(h("p", { class: "tt-bad", role: "alert" }, D.why_missing)); if (!D.gates.length) return; }
        body.append(h("p", { class: "gw-dim" }, "Server time now: ", D.realm_time, ". GMs: ",
          String(D.gm_obey_gates).startsWith("1") ? "gm.obey_gates = 1, GMs obey every gate." : "gm.obey_gates = 0, GMs skip the gates marked 'GM skips: yes'.",
          " Change the global switch on ", h("a", { href: "#/rates" }, "Server rates"), ". Mycenae also costs ", String(D.mycenae_silver), " Silver (compiled)."));
        body.append(h("p", { class: "gw-dim" }, "Pindus, Ni Mini and Lelantine Farm times saved here replace the ones on ", h("a", { href: "#/events" }, "Events & notices"),
          " (same rows). Players' client texts do not change: when a value disagrees with them the row says so."));
        const groups = {};
        for (const g of D.gates) (groups[g.group] = groups[g.group] || []).push(g);
        for (const [group, list] of Object.entries(groups)) {
          body.append(h("h3", null, group));
          const tbl = h("table", { class: "gw-w-table gw-gt-table" }, h("tr", null,
            ["Destination", "Level min", "Level max", "Opening (AEST)", "GM skips", "Now", "", "Players are told"].map(x => h("th", null, x))));
          for (const g of list) tbl.append(row(g, D));
          body.append(tbl);
        }
      }

      function row(g, D) {
        const r = g.row || {};
        const dis = !D.ready || null;
        const mn = h("input", { type: "number", min: 1, max: 200, value: r.minimum ?? "", placeholder: lv(g.compiled.minimum) + " (default)", class: "gw-gt-num", "aria-label": "Minimum level of " + g.name, disabled: dis || !g.has_level_band || null });
        const mx = h("input", { type: "number", min: 1, max: 200, value: r.maximum ?? "", placeholder: lv(g.compiled.maximum) + " (default)", class: "gw-gt-num", "aria-label": "Maximum level of " + g.name, disabled: dis || !g.has_level_band || null });
        const startMode = r.schedule_mode || (g.rows_alone_switch_windows && g.window_rows.length ? "windows" : "compiled");
        const mode = h("select", { "aria-label": "Opening of " + g.name, disabled: dis }, MODES.map(([v, t]) => h("option", { value: v, selected: v === startMode ? "" : null }, t)));
        const editor = windowEditor(g.window_rows);
        const showEd = () => { editor.style.display = mode.value === "windows" ? "" : "none"; };
        mode.onchange = showEd; showEd();
        const gm = h("select", { "aria-label": "GM skips " + g.name, disabled: dis || !g.supports_gm_bypass || null },
          [["", "Default (" + (g.compiled.gm_bypass ? "yes" : "no") + ")"], ["true", "Yes"], ["false", "No"]].map(([v, t]) =>
            h("option", { value: v, selected: String(r.gm_bypass ?? "") === v ? "" : null }, t)));
        const note = h("input", { type: "text", maxlength: 200, value: g.note || "", class: "gw-gt-note", placeholder: "note", "aria-label": "Note", disabled: dis });
        const warn = h("div", { class: "tt-bad gw-gt-warn" });
        const check = () => {
          const lo = mn.value === "" ? g.compiled.minimum : Number(mn.value), hi = mx.value === "" ? g.compiled.maximum : Number(mx.value);
          warn.textContent = lo > hi ? "Minimum above maximum: nobody could enter." : (lo < 1 || hi > 200) ? "Levels are 1-200." : "";
        };
        mn.oninput = mx.oninput = check;
        const save = async reset => {
          const windows = mode.value === "windows" ? editor.read() : [];
          if (windows.some(w => Number.isNaN(w.start_minute))) { warn.textContent = "Times are HH:MM."; return; }
          const req = reset ? { gate: g.key, reset: true, version: g.version } : {
            gate: g.key, version: g.version, note: note.value,
            minimum_level: mn.value === "" ? null : Number(mn.value), maximum_level: mx.value === "" ? null : Number(mx.value),
            schedule_mode: mode.value, gm_bypass: gm.value === "" ? null : gm.value === "true", windows
          };
          try { GW.writeDone(await ctx.api("save", req), reset ? g.name + " back to defaults" : g.name + " saved (the game reads it within 15 s)"); await load(); }
          catch (e) { if (await GW.writeError(e) === "conflict") await load(); }
        };
        const e = g.effective;
        const comp = g.compiled.windows.length ? g.compiled.windows.map(win).join(", ") : g.compiled.schedule;
        const edited = !!g.row || g.window_rows.length > 0;
        const told = [h("div", null, g.client_text)];
        if (edited && (e.minimum !== g.compiled.minimum || e.maximum !== g.compiled.maximum || e.schedule_mode !== "compiled"))
          told.push(h("div", { class: "tt-bad" }, "Edited: players still see the client text above until a client release."));
        const times = g.window_rows.filter(w => w.day >= 0);
        return h("tr", { class: edited ? "gw-gt-edited" : null },
          h("td", null, h("b", null, g.name), h("div", { class: "gw-dim" }, g.key + " · " + g.buttons)),
          h("td", null, mn), h("td", null, mx),
          h("td", null, mode, editor, h("div", { class: "gw-dim" }, "Default: " + comp),
            times.length ? h("div", { class: "gw-dim" }, "Manila: " + times.map(w => shifted(w, -2)).join(", ") + " · IGG GMT-5: " + times.map(w => shifted(w, -15)).join(", ")) : null),
          h("td", null, gm),
          h("td", null, (g.has_level_band ? e.minimum + "-" + lv(e.maximum) : "-"), h("div", null, h("span", { class: "gw-chip " + (g.open_now ? "now" : "") }, g.open_now ? "open" : "closed")),
            g.next_opening ? h("div", { class: "gw-dim" }, "next " + g.next_opening) : null,
            edited ? h("div", { class: "gw-dim" }, "level from " + e.minimum_source + "/" + e.maximum_source) : null),
          h("td", null, note, warn,
            h("button", { class: "gw-btnx small", type: "button", disabled: dis, onclick: () => save(false) }, "Save"),
            edited ? h("button", { class: "gw-btnx small", type: "button", disabled: dis, onclick: () => save(true) }, "Reset") : null),
          h("td", { class: "gw-dim gw-gt-told" }, told, h("div", null, g.evidence)));
      }

      try { await load(); } catch (e) { GW.clear(body).append(h("p", { class: "tt-bad" }, e.message)); }
    }
  });
})();
