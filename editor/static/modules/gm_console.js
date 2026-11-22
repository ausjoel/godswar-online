/* static/modules/gm_console.js - B22 d51: "GM commands" window. Type the game's GM command syntax (/online, /where Bob,
   /announce Server restart at 8pm, /kick Bob afk ...) or click a command to start it. Backend: modules/gm_console.py
   (info, log, run). Kick and ban ask first. Every command is logged with the editor user who ran it. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;

  GW.module("gm_console", {
    async mount(el, ctx) {
      let alive = true, info = null;
      const history = [];
      let hpos = 0;
      this.cleanup = () => { alive = false; };
      const out = h("div", { class: "gw-gc-out", role: "log", "aria-live": "polite" });
      const input = h("input", { class: "gw-gc-input", type: "text", placeholder: "/online", "aria-label": "GM command", autocomplete: "off", spellcheck: "false" });
      const runBtn = h("button", { class: "gw-btnx", type: "button", onclick: () => go() }, "Run");
      const cmds = h("div", { class: "gw-gc-cmds" });
      const logBox = h("div", { class: "gw-gc-log" });
      const state = h("p", { class: "gw-dim" });

      function line(text, cls) { out.append(h("div", { class: "gw-gc-line" + (cls ? " " + cls : "") }, text)); out.scrollTop = out.scrollHeight; }

      async function go(text) {
        const cmd = (text !== undefined ? text : input.value).trim();
        if (!cmd) return;
        const word = (cmd.startsWith("/") ? cmd : "/" + cmd).split(/\s+/)[0].toLowerCase();
        const c = info && info.commands.find(x => x.word === word);
        let confirm = false;
        if (c && c.confirm) {
          if (!await GW.confirm({ title: "Run " + word + "?", ok: "Run it", el: h("div", null, h("p", null, cmd), h("p", { class: "gw-dim" }, c.what)) })) return;
          confirm = true;
        }
        history.push(cmd); hpos = history.length; input.value = "";
        line("> " + cmd, "cmd");
        try {
          const r = await ctx.api("run", { command: cmd, confirm });
          if (!alive) return;
          (r.lines || []).forEach(t => line(t));
        } catch (e) { if (alive) line(e.message, "err"); }
        drawLog();
      }

      input.addEventListener("keydown", e => {
        if (e.key === "Enter") go();
        else if (e.key === "ArrowUp" && hpos > 0) { hpos--; input.value = history[hpos]; e.preventDefault(); }
        else if (e.key === "ArrowDown") { hpos = Math.min(history.length, hpos + 1); input.value = history[hpos] || ""; e.preventDefault(); }
      });

      async function drawLog() {
        try {
          const r = await ctx.api("log?limit=100");
          if (!alive) return;
          GW.clear(logBox).append(r.log.length ? h("table", { class: "gw-gc-table" },
            h("tr", null, ["Time", "Who", "Command", "Result"].map(t => h("th", null, t))),
            r.log.map(x => h("tr", { class: x.ok ? "" : "gw-gc-bad" }, h("td", null, (x.time || "").replace("T", " ").slice(0, 19)), h("td", null, x.user || ""),
              h("td", null, h("code", null, x.command)), h("td", null, (x.ok ? "" : "Refused: ") + x.result)))) : h("p", { class: "gw-dim" }, "Nothing run yet."));
        } catch (e) { if (alive) GW.clear(logBox).append(h("p", { class: "gw-dim" }, e.message)); }
      }

      el.append(
        h("p", { class: "gw-dim" }, "No GM character in the game is needed. Type a command and press Enter, or click one below to start it. Kick and ban ask first."),
        state,
        h("div", { class: "gw-gc-row" }, input, runBtn), out,
        h("h3", null, "Commands"), cmds,
        h("h3", { "data-help": "Every command run here, newest first, with the editor account that ran it. Kept in the editor's backup folder; nothing removes it." }, "Log"), logBox);

      try {
        info = await ctx.api("info");
        if (!alive) return;
        state.textContent = "Game server: " + (info.server.state || "unknown") + (info.writable ? "" : " · this editor is read-only, so only the look-up commands work") + (info.live ? "" : " · no live operations (kick, teleport and live give need the running server)");
        GW.clear(cmds).append(
          h("table", { class: "gw-gc-table" }, info.commands.map(c => h("tr", null,
            h("td", null, h("button", { class: "gw-gc-cmd", type: "button", disabled: (c.writes && !info.writable) || null,
              "data-help": c.what, onclick: () => { input.value = c.usage.replace(/\s*[<\[].*$/, "") + " "; input.focus(); } }, c.usage)),
            h("td", null, c.what + (c.confirm ? " Asks first." : ""))))),
          h("details", null, h("summary", null, "In game only (need a GM character in the world)"),
            h("ul", null, info.unavailable.map(u => h("li", null, h("code", null, u.command), ": " + u.why)))),
          h("p", { class: "gw-dim" }, "Also: ", ...info.see_also.flatMap((s, i) => [i ? " · " : "", h("a", { href: s.link }, s.what)])));
        line("Type /help for the list. Up and down arrows repeat earlier commands.", "dim");
      } catch (e) { state.textContent = e.message; }
      drawLog();
      input.focus();
    },
    unmount() { if (this.cleanup) this.cleanup(); },
  });
})();
