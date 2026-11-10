/* static/modules/events.js - battlefield opening times and scheduled notices. Backend: modules/events.py. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const DAYS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
  const hhmm = m => String(Math.floor(m / 60)).padStart(2, "0") + ":" + String(m % 60).padStart(2, "0");
  const minutes = s => { const p = String(s).split(":"); return Number(p[0]) * 60 + Number(p[1]); };
  const time = (value, label, onchange) => h("input", { type: "time", value: hhmm(value), "aria-label": label, onchange: e => onchange(minutes(e.target.value)) });
  const num = (value, label, o) => h("input", Object.assign({ type: "number", value, "aria-label": label, class: "gw-ev-num" }, o || {}));

  GW.module("events", {
    async mount(el, ctx) {
      let D = null;
      const root = h("div");
      el.append(root);
      async function load() { D = await ctx.api("overview"); draw(); }
      async function write(route, body, done) {
        try { GW.writeDone(await ctx.api(route, body), done); await load(); }
        catch (e) { if (await GW.writeError(e) === "conflict") await load(); }
      }
      function draw() {
        GW.clear(root).append(
          h("p", { class: "gw-dim" }, "Realm time now: " + (D.realm_time || "?") + " (" + (D.time_zone || "?") + "). Saved changes reach the game within 15 seconds, no restart."),
          h("h2", null, "Battlefield opening times"),
          ...D.activities.map(drawActivity),
          h("h2", null, "Seasonal events"),
          drawSeasons(),
          h("h2", null, "Scheduled notices"),
          drawNotices(),
          h("h2", null, "Other fixed times"),
          h("p", { class: "gw-dim" }, "Compiled into the server, shown here for reference. Not editable."),
          h("table", { class: "gw-ev-fixed" }, h("tbody", null, D.fixed.map(f => h("tr", null, h("td", null, f.name), h("td", null, f.when))))));
      }
      function drawSeasons() {
        const rows = D.seasons.map(s => Object.assign({}, s));
        const day = (value, label, onchange) => h("input", { type: "date", value: value || "", "aria-label": label, onchange: e => onchange(e.target.value || null) });
        return h("section", { class: "gw-ev-card" },
          h("p", { class: "gw-dim" }, "Every season starts switched off. While a season is on (and today is between its first and last day, Brisbane time), its envoys stand in the capitals and its drops run; otherwise they are hidden. Leave both days empty to run it until you switch it off. 'Every year' repeats the same dates each year. Players see envoys appear or vanish the next time anyone enters that map. Drop chances: Server rates tab, 'Seasonal events'."),
          h("table", { class: "gw-ev-fixed" }, h("tbody", null, rows.map(s => h("tr", null,
            h("td", null, h("label", { "data-help": "What this season brings: " + s.what }, h("input", { type: "checkbox", checked: !!s.enabled, "aria-label": s.name + " on", onchange: e => { s.enabled = e.target.checked; } }), " ", s.name)),
            h("td", { class: "gw-dim" }, s.what),
            h("td", null, "from ", day(s.first_day, s.name + " first day", v => { s.first_day = v; }), " to ", day(s.last_day, s.name + " last day", v => { s.last_day = v; })),
            h("td", null, h("label", null, h("input", { type: "checkbox", checked: s.yearly !== false, "aria-label": s.name + " every year", onchange: e => { s.yearly = e.target.checked; } }), " every year")))))),
          h("div", { class: "gw-ev-actions" },
            h("button", { class: "gw-btnx primary", type: "button", onclick: () => write("seasons/save", { rows, version: D.seasons_version }, "Seasons saved") }, "Save seasons")));
      }
      function drawActivity(a) {
        const edited = a.override && a.override.length;
        const closed = edited && a.override.some(w => w.day_of_week === -1);
        let rows = edited && !closed ? a.override.map(w => ({ day: w.day_of_week, start_minute: w.start_minute, duration_minutes: w.duration_minutes })) : a.compiled.map(w => ({ day: w.day, start_minute: w.start_minute, duration_minutes: w.duration_minutes }));
        const body = h("div");
        function drawRows() {
          GW.clear(body);
          rows.forEach((r, i) => body.append(h("div", { class: "gw-ev-row" },
            h("select", { "aria-label": a.name + " day", onchange: e => { r.day = Number(e.target.value); } }, DAYS.map((d, n) => h("option", { value: n, selected: n === r.day }, d))),
            time(r.start_minute, a.name + " opening time", v => { r.start_minute = v; }),
            h("label", null, "for ", num(r.duration_minutes, a.name + " length in minutes", { min: 1, max: 1440, onchange: e => { r.duration_minutes = Number(e.target.value); } }), " min"),
            h("button", { class: "gw-btnx small", type: "button", onclick: () => { rows.splice(i, 1); drawRows(); } }, "Remove"))));
        }
        drawRows();
        const notes = [];
        if (a.always_open) notes.push("The \"always open\" switch on the rates tab is on, so these times have no effect until it is off.");
        if (!edited && (D.window_minutes !== 45 || D.shift_hours !== 0)) notes.push("The rates tab currently changes these to " + D.window_minutes + " minutes, shifted " + D.shift_hours + " hours. Saving here replaces that for this battlefield.");
        return h("section", { class: "gw-ev-card" },
          h("h3", null, a.name, " ", h("span", { class: "gw-chip" }, closed ? "closed" : edited ? "edited" : "original times")),
          ...notes.map(n => h("p", { class: "gw-dim" }, n)),
          body,
          h("div", { class: "gw-ev-actions" },
            h("button", { class: "gw-btnx", type: "button", onclick: () => { rows.push({ day: 6, start_minute: 720, duration_minutes: 45 }); drawRows(); } }, "Add opening"),
            h("button", { class: "gw-btnx primary", type: "button", onclick: () => write("schedule/save", { activity: a.key, windows: rows, version: a.version }, a.name + " saved") }, "Save"),
            h("button", { class: "gw-btnx", type: "button", onclick: () => write("schedule/save", { activity: a.key, closed: true, version: a.version }, a.name + " closed") }, "Close it"),
            edited ? h("button", { class: "gw-btnx", type: "button", onclick: () => write("schedule/reset", { activity: a.key, version: a.version }, a.name + " back to the original times") }, "Original times") : null));
      }
      function drawNotices() {
        const rows = D.notices.map(n => Object.assign({}, n));
        let populationOn = D.population_minutes > 0;
        let population = populationOn ? D.population_minutes : (D.population_saved || 5);
        const welcome = Object.assign({ message: "", enabled: false }, D.welcome || {});
        const body = h("div");
        function drawRows() {
          GW.clear(body);
          if (!rows.length) body.append(h("p", { class: "gw-dim" }, "None. The server sends nothing but the population line until you add one."));
          rows.forEach((n, i) => {
            const label = "Notice " + (i + 1);
            body.append(h("div", { class: "gw-ev-notice" },
              h("input", { type: "text", class: "gw-ev-text", maxlength: 106, value: n.message, placeholder: "Plain English, up to 106 characters", "aria-label": label + " text", oninput: e => { n.message = e.target.value; } }),
              h("div", { class: "gw-ev-row" },
                h("select", { "aria-label": label + " shown as", onchange: e => { n.delivery = e.target.value; } },
                  h("option", { value: "banner", selected: n.delivery === "banner" }, "Red banner"), h("option", { value: "log", selected: n.delivery === "log" }, "Game log line"), h("option", { value: "board", selected: n.delivery === "board" }, "Board above chat (top box)"), h("option", { value: "chat", selected: n.delivery === "chat" }, "Chat box line (e.g. event transporters open)")),
                DAYS.map((d, b) => h("label", { class: "gw-ev-day" }, h("input", { type: "checkbox", checked: !!(n.day_mask & (1 << b)), "aria-label": label + " " + d, onchange: e => { n.day_mask = e.target.checked ? n.day_mask | (1 << b) : n.day_mask & ~(1 << b); } }), d)),
                h("label", null, "at ", time(n.start_minute, label + " time", v => { n.start_minute = v; })),
                h("label", null, "repeat every ", num(n.repeat_minutes, label + " repeat in minutes, 0 for once", { min: 0, max: 1439, onchange: e => { n.repeat_minutes = Number(e.target.value); } }), " min (0 = once)"),
                h("label", null, "until ", time(n.end_minute, label + " last time", v => { n.end_minute = v; })),
                h("label", null, "from ", h("input", { type: "date", value: n.first_day || "", "aria-label": label + " first day", onchange: e => { n.first_day = e.target.value || null; } })),
                h("label", null, "to ", h("input", { type: "date", value: n.last_day || "", "aria-label": label + " last day", onchange: e => { n.last_day = e.target.value || null; } })),
                h("label", null, h("input", { type: "checkbox", checked: n.enabled, "aria-label": label + " on", onchange: e => { n.enabled = e.target.checked; } }), "on"),
                h("button", { class: "gw-btnx small", type: "button", onclick: () => { rows.splice(i, 1); drawRows(); } }, "Remove"))));
          });
        }
        drawRows();
        let boardText = "";
        const postBox = h("div", { class: "gw-ev-row" },
          h("input", { type: "text", class: "gw-ev-text", maxlength: 106, placeholder: "Post to the board above every player's chat box, right now", "aria-label": "Board message", oninput: e => { boardText = e.target.value; } }),
          h("button", { class: "gw-btnx primary", type: "button", "data-help": "Sends the line to every online player now. Client 1.4.3 shows it on the board above the chat box; older clients get a game log line. Nothing is stored.", onclick: async () => {
            try { const r = await ctx.api("board/post", { text: boardText }); GW.status("Posted to the board: " + r.delivered + " player(s) online got it", "ok"); }
            catch (e) { await GW.writeError(e); } } }, "Post now"));
        return h("section", { class: "gw-ev-card" },
          h("h3", null, "Board above the chat box"),
          postBox,
          h("p", { class: "gw-dim" }, "The board is the top box above the chat box: server messages go here, the chat box is for player chat. The online-players notice below, scheduled notices set to 'Board above chat', the server welcome and the switches on the Server rates tab (Message board) post here. Only world boss spawns and notices set to 'Chat box line' go to the chat box."),
          h("div", { class: "gw-ev-row" },
            h("label", { "data-help": "The line 'Online: N | Athenians: A | Spartans: S' shown to every player in the world. Off = it is never sent." }, h("input", { type: "checkbox", checked: populationOn, "aria-label": "Show the online-players notice", onchange: e => { populationOn = e.target.checked; } }), " Show the online-players notice"),
            h("label", null, " every ", num(population, "Online-players notice interval in minutes", { min: 1, max: 1440, onchange: e => { population = Math.max(1, Number(e.target.value) || 1); } }), " min")),
          h("p", { class: "gw-dim" }, "The server checks the schedule every " + (D.tick_seconds || 15) + " seconds (fixed in the server), so the line appears within " + (D.tick_seconds || 15) + " seconds of the interval being reached. Saving applies it within 15 seconds; no restart."),
          D.welcome_available === false ? h("p", { class: "gw-dim" }, "Server welcome: needs a game server from 2026-11-10 or later (it creates the table).") :
          h("div", { class: "gw-ev-row" },
            h("label", { "data-help": "One line on the board (top box) for every player, about 15 seconds after entering the world. Off = no server welcome (the default). The '****** Welcome to Godswar Origin ******' lines in the chat box are the client's own text (Text/Message.dat), not this." },
              h("input", { type: "checkbox", checked: welcome.enabled, "aria-label": "Send the server welcome", onchange: e => { welcome.enabled = e.target.checked; } }), " Server welcome at login"),
            h("input", { type: "text", class: "gw-ev-text", maxlength: 106, value: welcome.message, placeholder: "Welcome text, plain English, up to 106 characters", "aria-label": "Server welcome text", oninput: e => { welcome.message = e.target.value; } })),
          body,
          h("div", { class: "gw-ev-actions" },
            h("button", { class: "gw-btnx", type: "button", onclick: () => { rows.push({ message: "", delivery: "banner", day_mask: 127, start_minute: 720, end_minute: 720, repeat_minutes: 0, first_day: null, last_day: null, enabled: true }); drawRows(); } }, "Add notice"),
            h("button", { class: "gw-btnx primary", type: "button", onclick: () => write("notices/save", Object.assign({ rows, population_enabled: populationOn, population_minutes: population, version: D.notices_version }, D.welcome_available === false ? {} : { welcome }), "Notices saved") }, "Save notices")));
      }
      await load();
    }
  });
})();
