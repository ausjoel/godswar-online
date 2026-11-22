/* static/modules/player_health.js - "Player health" (wave 6 A). Backend: modules/player_health.py.
   For every player: can the character log in right now (Login check, with a Fix button where the editor can repair
   it), how the recent logins went (green / yellow / red), what went wrong in plain words and what to do about it.
   Server-wide: logins today, failed share, the most common problems and a hint when failures began after a change. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const plural = (n, w) => n + " " + w + (n === 1 ? "" : "s");
  const LIGHT = { red: "Problem", amber: "Check", green: "Healthy", grey: "No logins" };
  const LEVEL = { bad: "red", warn: "amber", ok: "green", info: "grey" };
  const HELP = {
    light: GW.helpText({ title: "Status", text: "How this player's logins are going.",
      list: ["Red: the last login failed, or the character cannot log in right now",
        "Yellow: a recent login had a problem, or something is hidden from the player at login",
        "Green: the recent logins worked", "Grey: no logins in the days shown"] }),
    check: GW.helpText({ title: "Login check", text: "Tests whether each character would get into the world, without the player.",
      facts: [["What it does", "the game reads the character and builds what it sends at login, then throws it away"],
        ["Changes anything", "no"], ["Runs", "when this page opens, after every character edit, and when you press the button"]] }),
    failed: GW.helpText({ title: "Failed logins", text: "Logins today that ended before the player was in the world because something went wrong.",
      note: "A wrong password counts as a problem (yellow), not as a failed login." }),
    network: GW.helpText({ title: "Network", text: "The first three parts of the player's internet address.",
      facts: [["Why", "two players with the same network are in the same house or on the same provider"], ["Stored", "never the full address"]] }),
    version: GW.helpText({ title: "Client version", text: "The version number the game client sent when it logged in." }),
  };
  const dot = (light, label) => h("span", { class: "ph-dot ph-" + light, role: "img", "aria-label": label || LIGHT[light] || light, title: label || LIGHT[light] || light });
  const card = (title, value, sub, cls, help) => h("div", { class: "ph-card " + (cls || ""), "data-help": help || null },
    h("div", { class: "ph-card-t" }, title), h("div", { class: "ph-card-v" }, value), h("div", { class: "ph-card-s" }, sub || ""));

  GW.module("player_health", {
    async mount(el, ctx) {
      const body = h("div");
      el.append(body);
      const filter = { problems: false, build: "", day: "", text: "" };
      let days = 7;

      async function load() {
        const d = await ctx.api("overview?days=" + days);
        GW.clear(body);
        const s = d.stats, c = d.check;

        // ---- the picture at a glance
        body.append(h("div", { class: "gw-frame-simplewindow ph-panel" }, h("h2", null, "Player health"),
          h("div", { class: "ph-cards" },
            card("Cannot log in", String(c.blocked), c.blocked ? "fix these first" : "every character can log in", c.blocked ? "ph-bad" : "ph-good", HELP.check),
            card("Players with problems", String(d.counts.red + d.counts.amber), d.counts.red + " red, " + d.counts.amber + " yellow, " + d.counts.green + " healthy", d.counts.red ? "ph-bad" : d.counts.amber ? "ph-warn" : "ph-good", HELP.light),
            card("Logins today", d.records_ready ? String(s.logins_today) : "-", d.records_ready ? "since midnight, Brisbane time" : "not recorded yet"),
            card("Failed today", d.records_ready ? s.failed_today + " (" + s.failed_percent + "%)" : "-", d.records_ready ? "ended before the world, by a fault" : "not recorded yet", s.failed_today ? "ph-bad" : "", HELP.failed)),
          d.records_note ? h("p", { class: "ph-note" }, d.records_note) : null,
          d.what_changed ? h("p", { class: "ph-changed" }, h("strong", null, "What changed: "), d.what_changed.text) : null));

        // ---- login check
        const checkBox = h("div");
        const checkStatus = h("span", { class: "gw-dim", role: "status" });
        const checkBtn = h("button", { type: "button", class: "gw-btnx", "data-help": HELP.check, onclick: runCheck }, "Check every character now");
        body.append(h("div", { class: "gw-frame-simplewindow ph-panel" },
          h("div", { class: "ph-head" }, h("h2", null, "Login check"), checkBtn, checkStatus), checkBox));
        function paintCheck(r) {
          GW.clear(checkBox);
          const bad = r.results.filter(x => x.findings.length);
          checkStatus.textContent = plural(r.checked, "character") + " checked: " + r.blocked + " cannot log in, " + r.hidden + " with something hidden, " + (r.checked - r.blocked - r.hidden) + " fine.";
          if (r.why) checkBox.append(h("p", { class: "ph-note" }, r.why));
          if (!bad.length) { checkBox.append(h("p", { class: "ph-good" }, "Nothing would stop any character logging in.")); return; }
          for (const x of bad) {
            const box = h("div", { class: "ph-check ph-check-" + x.light }, h("div", { class: "ph-check-h" }, dot(x.light, x.light === "red" ? "Cannot log in" : "Something hidden"),
              h("strong", null, x.character), h("span", { class: "gw-dim" }, "account " + (x.username || x.account_id)),
              h("span", { class: x.light === "red" ? "ph-bad" : "ph-warn" }, x.light === "red" ? "cannot log in" : "logs in, with something hidden")));
            for (const f of x.findings) {
              const fixBtn = f.fix ? h("button", { type: "button", class: "gw-btnx small", disabled: !d.writable || null, onclick: () => applyFix(x, f) }, f.fix.label) : null;
              box.append(h("div", { class: "ph-finding" }, h("div", null, h("strong", null, f.title), f.where ? h("span", { class: "gw-dim" }, " · " + f.where) : null, h("br"),
                h("span", { class: "gw-dim" }, f.detail || "")), fixBtn || h("span", { class: "gw-dim" }, "no automatic fix: open the character")));
            }
            checkBox.append(box);
          }
        }
        async function runCheck() {
          checkBtn.disabled = true; checkStatus.textContent = "checking ...";
          try { paintCheck(await ctx.api("check", {})); } catch (e) { checkStatus.textContent = e.message; }
          checkBtn.disabled = false;
        }
        async function applyFix(x, f) {
          if (!await GW.confirm({ title: f.fix.label + "?", ok: "Remove it", danger: true,
            message: x.character + ": " + f.title + (f.where ? " (" + f.where + ")" : "") + ".\n\nThe item is removed from the character. It goes into the change log, where Undo puts it back." })) return;
          try {
            const res = await ctx.api("fix", { character_id: x.cid, fix: f.fix });
            GW.writeDone(res, "Removed " + res.removed.name + " from " + x.character);
            await load();
          } catch (e) { await GW.writeError(e); if (e.status === 409) await load(); }
        }
        paintCheck(c);

        // ---- most common problems
        if (d.records_ready) {
          const top = h("div", { class: "gw-frame-simplewindow ph-panel" }, h("h2", null, "Most common problems, last " + plural(d.days, "day")));
          if (!s.top_reasons.length) top.append(h("p", { class: "ph-good" }, "No login problems recorded."));
          else top.append(h("table", { class: "ph-table" }, h("thead", null, h("tr", null, h("th", null, ""), h("th", null, "Problem"), h("th", null, "Times"), h("th", null, "Players"))),
            h("tbody", null, s.top_reasons.map(r => h("tr", null, h("td", null, dot(LEVEL[r.level])), h("td", null, r.title), h("td", null, String(r.count)), h("td", null, String(r.accounts)))))));
          if (s.unknown_name_attempts) top.append(h("p", { class: "gw-dim" }, plural(s.unknown_name_attempts, "login") + " used a user name nobody has (not linked to a player)."));
          body.append(top);
        }

        // ---- players
        const list = h("div");
        const count = h("span", { class: "gw-dim" });
        const problems = h("input", { type: "checkbox", checked: filter.problems || null, "aria-label": "Problems only", onchange: () => { filter.problems = problems.checked; paint(); } });
        const search = h("input", { type: "text", placeholder: "player or account", value: filter.text, "aria-label": "Find a player", oninput: () => { filter.text = search.value; paint(); } });
        const allDays = Array.from(new Set(d.players.flatMap(p => p.days))).sort().reverse();
        const daySel = h("select", { "aria-label": "Day", onchange: () => { filter.day = daySel.value; paint(); } }, h("option", { value: "" }, "Any day"), allDays.map(x => h("option", { value: x, selected: filter.day === x || null }, x)));
        const buildSel = h("select", { "aria-label": "Client version", "data-help": HELP.version + "\n~ " + d.build_note, onchange: () => { filter.build = buildSel.value; paint(); } }, h("option", { value: "" }, "Any client version"),
          d.builds.map(b => h("option", { value: String(b.build), selected: filter.build === String(b.build) || null }, b.text + " - " + plural(b.accounts, "player") + (b.newest ? " (newest)" : ""))));
        const daysSel = h("select", { "aria-label": "Period", onchange: async () => { days = Number(daysSel.value); await load(); } }, [1, 3, 7, 14, 30].map(n => h("option", { value: String(n), selected: n === d.days || null }, "Last " + plural(n, "day"))));
        body.append(h("div", { class: "gw-frame-simplewindow ph-panel" }, h("div", { class: "ph-head" }, h("h2", null, "Players"), count),
          h("div", { class: "ph-filters" }, h("label", null, problems, " Problems only"), daysSel, daySel, buildSel, search), list));
        function paint() {
          GW.clear(list);
          const text = filter.text.trim().toLowerCase();
          const rows = d.players.filter(p => (!filter.problems || p.light === "red" || p.light === "amber") &&
            (!filter.build || String(p.build) === filter.build) && (!filter.day || p.days.includes(filter.day)) &&
            (!text || ((p.username || "") + " " + p.characters.map(x => x.name).join(" ")).toLowerCase().includes(text)));
          count.textContent = rows.length + " of " + plural(d.players.length, "player");
          if (!rows.length) { list.append(h("p", { class: "gw-dim" }, "Nobody matches.")); return; }
          for (const p of rows) {
            const names = p.characters.map(x => x.name + " (level " + x.level + ")" + (x.online ? ", online now" : "")).join(", ") || "no character";
            const timeline = h("div", { class: "ph-timeline", hidden: true });
            const toggle = h("button", { type: "button", class: "gw-btnx small", "aria-expanded": "false", disabled: !p.timeline.length || null,
              onclick: () => { timeline.hidden = !timeline.hidden; toggle["aria-expanded"] = String(!timeline.hidden); toggle.textContent = timeline.hidden ? "Show logins" : "Hide logins"; } }, "Show logins");
            if (p.timeline.length) timeline.append(h("table", { class: "ph-table" },
              h("thead", null, h("tr", null, h("th", null, ""), h("th", null, "When"), h("th", null, "What happened"), h("th", null, "Got as far as"), h("th", null, "Lasted"), h("th", { "data-help": HELP.network }, "Network"))),
              h("tbody", null, p.timeline.map(t => h("tr", null, h("td", null, dot(LEVEL[t.level])), h("td", null, t.at),
                h("td", null, h("strong", null, t.title), t.detail ? h("div", { class: "gw-dim" }, t.detail) : null, t.fix ? h("div", { class: "ph-fix" }, "What to do: " + t.fix) : null),
                h("td", null, t.reached), h("td", null, t.duration), h("td", null, t.network))))));
            list.append(h("div", { class: "ph-player ph-player-" + p.light }, h("div", { class: "ph-player-h" }, dot(p.light), h("strong", null, names),
              h("span", { class: "gw-dim" }, "account " + (p.username || p.account_id)), toggle),
              h("div", { class: "ph-player-b" }, h("span", { class: p.light === "red" ? "ph-bad" : p.light === "amber" ? "ph-warn" : "" }, p.headline),
                p.fix ? h("div", { class: "ph-fix" }, "What to do: " + p.fix) : null,
                h("div", { class: "gw-dim" }, (p.last ? "Last login " + p.last : "No login recorded") + (p.logins ? " · " + plural(p.logins, "login") + ", " + p.problems + " with a problem" : "") +
                  (p.build_text ? " · client " + p.build_text + (p.build_behind ? " (older than the newest seen)" : "") : ""))), timeline));
          }
        }
        paint();
      }
      await load();
    },
    unmount() {},
  });
})();
