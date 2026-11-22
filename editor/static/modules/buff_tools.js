/* static/modules/buff_tools.js - B21 item 22: buffs for many players. A picker sorted by effect (one entry per effect,
   a level picker for its strengths), who gets it (everyone online / every character / ticked names), for how long
   (a duration or until a fixed Brisbane time = an event buff), remove from the ticked characters, clear server-wide,
   and the running event buffs. Backend: modules/buff_tools.py. Text is inserted as text, never as HTML. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const fmtSecs = s => { s = Number(s) || 0; if (s < 0) return "permanent"; const d = Math.floor(s / 86400), hh = Math.floor(s % 86400 / 3600), m = Math.floor(s % 3600 / 60);
    return (d ? d + " d " : "") + (hh ? hh + " h " : "") + (m || (!d && !hh) ? m + " min" : "").trim(); };

  GW.module("buff_tools", {
    async mount(el, ctx) {
      this.el = el; this.ctx = ctx; this.sel = null; this.level = 0; this.ticked = new Set(); this.cat = ""; this.q = ""; this.onlyApplicable = true;
      GW.clear(el).append(h("p", { class: "gw-dim" }, "Loading the buff list ..."));
      try { [this.L, this.C, this.E] = await Promise.all([ctx.api("library"), ctx.api("characters"), ctx.api("events")]); }
      catch (e) { GW.clear(el).append(h("p", { class: "tt-bad" }, e.message)); return; }
      this.draw();
      this.timer = setInterval(() => this.refreshEvents(), 30000);
    },
    unmount() { clearInterval(this.timer); },
    draw() {
      const el = GW.clear(this.el);
      this.pickerEl = h("div", { class: "gw-bt-groups", role: "listbox", "aria-label": "Buffs by effect" });
      this.detailEl = h("div", { class: "gw-bt-detail" });
      this.whoEl = h("div", { class: "gw-bt-who" });
      this.eventsEl = h("div", { class: "gw-bt-events" });
      const search = h("input", { type: "search", placeholder: "Search buffs and effects", "aria-label": "Search buffs", value: this.q });
      search.addEventListener("input", () => { this.q = search.value.toLowerCase(); this.drawGroups(); });
      const cat = h("select", { "aria-label": "Category" }, h("option", { value: "" }, "Every kind"), this.L.categories.map(c => h("option", { value: c, selected: c === this.cat || null }, c)));
      cat.addEventListener("change", () => { this.cat = cat.value; this.drawGroups(); });
      const only = h("input", { type: "checkbox", checked: this.onlyApplicable || null, "aria-label": "Only buffs the server can give" });
      only.addEventListener("change", () => { this.onlyApplicable = only.checked; this.drawGroups(); });
      el.append(...[
        this.L.server_supported ? null : h("p", { class: "tt-bad" }, "The running game server cannot give buffs (it needs the live buff tools update); the list is for reading only."),
        h("div", { class: "gw-bt-cols" },
          h("section", { class: "gw-frame-simplewindow gw-bt-col" }, h("h3", null, "1. Buff (sorted by effect)"),
            h("div", { class: "gw-bt-tools" }, search, cat, h("label", { "data-help": "Hide effects the running server cannot give to a player (mount rides, server-only rows)." }, only, " only ones the server can give")),
            this.pickerEl),
          h("section", { class: "gw-frame-simplewindow gw-bt-col" }, h("h3", null, "2. Level and how long"), this.detailEl),
          h("section", { class: "gw-frame-simplewindow gw-bt-col" }, h("h3", null, "3. Who"), this.whoEl)),
        h("section", { class: "gw-frame-simplewindow gw-bt-wide" }, h("h3", null, "Event buffs (until a fixed time)"), this.eventsEl),
        h("section", { class: "gw-bt-danger" },
          h("button", { class: "gw-btnx danger", type: "button", "data-help": "Remove every buff the editor can remove from everyone online, cancel every queued buff and stop every event buff. Mount rides and permanent effects stay (the game owns them).", onclick: () => this.clearAll() }, "Clear buffs server-wide ..."),
          h("span", { class: "gw-dim" }, " Buffs live in the game session: they also end when a player logs out."))].filter(Boolean));
      this.drawGroups(); this.drawDetail(); this.drawWho(); this.drawEvents();
    },
    groups() {
      return this.L.groups.filter(g => (!this.cat || g.category === this.cat) && (!this.onlyApplicable || g.applicable) &&
        (!this.q || (g.name + " " + g.effect + " " + g.levels.map(l => l.label + " " + l.effects.join(" ")).join(" ")).toLowerCase().includes(this.q)));
    },
    drawGroups() {
      const box = GW.clear(this.pickerEl); let last = null;
      const gs = this.groups();
      for (const g of gs) {
        if (g.category !== last) { box.append(h("div", { class: "gw-bt-cat" }, g.category)); last = g.category; }
        const on = this.sel && this.sel.key === g.key;
        box.append(h("button", { type: "button", class: "gw-bt-group" + (on ? " on" : "") + (g.beneficial ? "" : " harm"), role: "option", "aria-selected": on ? "true" : "false",
          onclick: () => { this.sel = g; this.level = Math.max(0, g.levels.findIndex(l => l.applicable)); this.drawGroups(); this.drawDetail(); } },
          GW.cell ? GW.cell({ sprite: g.icon, name: g.name, small: true }) : null,
          h("span", { class: "gw-bt-gname" }, h("b", null, g.name), h("span", { class: "gw-dim" }, " " + g.effect)),
          g.levels.length > 1 ? h("span", { class: "gw-chip", "data-help": "The same effect at " + g.levels.length + " strengths: pick the level on the right." }, g.levels.length + " levels") : null));
      }
      if (!gs.length) box.append(h("p", { class: "gw-dim" }, "Nothing matches."));
    },
    drawDetail() {
      const box = GW.clear(this.detailEl), g = this.sel;
      if (!g) { box.append(h("p", { class: "gw-dim" }, "Pick a buff on the left.")); return; }
      const lv = g.levels[this.level] || g.levels[0];
      const pick = h("select", { "aria-label": "Level" }, g.levels.map((l, i) => h("option", { value: i, selected: i === this.level || null, disabled: !l.applicable || null },
        "Level " + l.level + ": " + l.label + (l.applicable ? "" : " (the server cannot give it)"))));
      pick.addEventListener("change", () => { this.level = Number(pick.value); this.drawDetail(); });
      this.mode = this.mode || "for";
      const modeFor = h("input", { type: "radio", name: "gw-bt-mode", checked: this.mode === "for" || null, "aria-label": "For a time" });
      const modeUntil = h("input", { type: "radio", name: "gw-bt-mode", checked: this.mode === "until" || null, "aria-label": "Until a fixed time" });
      const def = Math.max(60, Number(lv.duration_s) > 0 ? Number(lv.duration_s) : 3600);
      this.hours = h("input", { type: "number", min: 0, max: 1193046, value: this.hoursV !== undefined ? this.hoursV : Math.floor(def / 3600), class: "gw-bt-num", "aria-label": "Hours" });
      this.minutes = h("input", { type: "number", min: 0, max: 59, value: this.minutesV !== undefined ? this.minutesV : Math.floor(def % 3600 / 60), class: "gw-bt-num", "aria-label": "Minutes" });
      const soon = new Date(Date.now() + 12 * 3600e3);   // UTC + 10 h (Brisbane, no daylight saving) + 2 h, read as a wall clock below
      this.until = h("input", { type: "datetime-local", value: this.untilV || soon.toISOString().slice(0, 16), "aria-label": "End time (Brisbane)" });
      for (const x of [this.hours, this.minutes, this.until]) x.addEventListener("input", () => { this.hoursV = this.hours.value; this.minutesV = this.minutes.value; this.untilV = this.until.value; });
      modeFor.addEventListener("change", () => { this.mode = "for"; });
      modeUntil.addEventListener("change", () => { this.mode = "until"; });
      box.append(...[
        h("div", { class: "gw-bt-head" }, GW.cell ? GW.cell({ sprite: lv.icon || g.icon, name: g.name }) : null, h("div", null, h("b", null, g.name), h("div", { class: "gw-dim" }, g.category))),
        h("label", { class: "gw-bt-line" }, "Level ", pick),
        h("ul", { class: "gw-bt-effects" }, lv.effects.map(e => h("li", null, e))),
        lv.note ? h("p", { class: "gw-dim" }, lv.note) : null,
        h("p", { class: "gw-dim" }, "Normal length: " + (Number(lv.duration_s) > 0 ? fmtSecs(lv.duration_s) : "permanent") + "."),
        h("label", { class: "gw-bt-line", "data-help": "Online players get it now for this long. Offline ones get it at their next login (the time starts then)." }, modeFor, " For ", this.hours, " h ", this.minutes, " min"),
        h("label", { class: "gw-bt-line", "data-help": "An event buff: it ends at this time for everyone. Players who log in before then (or log in again) get it with the time that is left. The editor gives it every 30 seconds while it runs." }, modeUntil, " Until ", this.until, " Brisbane"),
        h("div", { class: "gw-bt-go" },
          h("button", { class: "gw-btnx", type: "button", disabled: !this.L.server_supported || !lv.applicable || null, onclick: () => this.give() }, "Give buff"),
          h("button", { class: "gw-btnx", type: "button", "data-help": "Remove this buff (every level of it) from the ticked characters.", onclick: () => this.removeTicked(g.levels.map(l => l.status_id)) }, "Remove it from ticked"))].filter(Boolean));
    },
    drawWho() {
      const box = GW.clear(this.whoEl);
      this.who = this.who || "online";
      const radio = (v, label, help) => { const r = h("input", { type: "radio", name: "gw-bt-who", checked: this.who === v || null, "aria-label": label });
        r.addEventListener("change", () => { this.who = v; this.drawWho(); }); return h("label", { class: "gw-bt-line", "data-help": help }, r, " " + label); };
      const online = this.C.characters.filter(c => c.online).length;
      box.append(radio("online", "Everyone online (" + online + ")", "Only the characters in the world now."),
        radio("all", "Every character (" + this.C.characters.length + ")", "Online now, and the others at their next login."),
        radio("selected", "Ticked characters", "Tick names below."));
      if (this.who !== "selected") {
        this.camp = h("select", { "aria-label": "Faction" }, h("option", { value: "all" }, "Both factions"), h("option", { value: "1" }, "Athens"), h("option", { value: "0" }, "Sparta"));
        this.minL = h("input", { type: "number", min: 1, max: 200, value: 1, class: "gw-bt-num", "aria-label": "Lowest level" });
        this.maxL = h("input", { type: "number", min: 1, max: 200, value: 200, class: "gw-bt-num", "aria-label": "Highest level" });
        box.append(h("div", { class: "gw-bt-line" }, this.camp, " level ", this.minL, " to ", this.maxL));
      }
      const q = h("input", { type: "search", placeholder: "Find a name", "aria-label": "Find a character" });
      const list = h("div", { class: "gw-bt-names" });
      const drawList = () => { GW.clear(list); const t = q.value.toLowerCase();
        for (const c of this.C.characters.filter(c => !t || c.name.toLowerCase().includes(t))) {
          const cb = h("input", { type: "checkbox", checked: this.ticked.has(c.id) || null, "aria-label": c.name });
          cb.addEventListener("change", () => { cb.checked ? this.ticked.add(c.id) : this.ticked.delete(c.id); countEl.textContent = this.ticked.size + " ticked"; });
          list.append(h("label", { class: "gw-bt-name" }, cb, " ", h("span", { class: "gw-dot " + (c.online ? "on" : "off") }), " " + c.name, h("span", { class: "gw-dim" }, " Lv " + c.level + " " + c.class)));
        } };
      q.addEventListener("input", drawList);
      const countEl = h("span", { class: "gw-dim" }, this.ticked.size + " ticked");
      box.append(h("div", { class: "gw-bt-tools" }, q,
        h("button", { class: "gw-btnx small", type: "button", onclick: () => { this.C.characters.filter(c => c.online).forEach(c => this.ticked.add(c.id)); this.drawWho(); } }, "Tick online"),
        h("button", { class: "gw-btnx small", type: "button", onclick: () => { this.ticked.clear(); this.drawWho(); } }, "Untick all"), countEl), list,
        h("button", { class: "gw-btnx", type: "button", disabled: !this.ticked.size || null, "data-help": "Remove every buff the editor can remove from the ticked characters that are online, and cancel their queued buffs.", onclick: () => this.removeTicked(null) }, "Remove all buffs from ticked"));
      drawList();
    },
    body() {
      const g = this.sel, lv = g.levels[this.level];
      const b = { status_id: lv.status_id, target: this.who };
      if (this.who === "selected") b.ids = [...this.ticked];
      else { b.camp = this.camp.value; b.min_level = Number(this.minL.value); b.max_level = Number(this.maxL.value); }
      if (this.mode === "until") b.until = this.until.value.replace("T", " ");
      else b.duration_seconds = Number(this.hours.value || 0) * 3600 + Number(this.minutes.value || 0) * 60;
      return b;
    },
    async give() {
      const g = this.sel, lv = g.levels[this.level];
      if (this.who === "selected" && !this.ticked.size) { GW.alert("Tick at least one character.", "Give buff"); return; }
      const b = this.body();
      if (b.duration_seconds !== undefined && b.duration_seconds < 1) { GW.alert("Set how long (hours and minutes).", "Give buff"); return; }
      const who = this.who === "online" ? "everyone online" : this.who === "all" ? "every character (online now, the rest at their next login)" : this.ticked.size + " ticked character(s)";
      const how = b.until ? "until " + b.until + " Brisbane (an event buff)" : "for " + fmtSecs(b.duration_seconds);
      if (!await GW.confirm({ title: "Give " + lv.label + "?", message: lv.label + " to " + who + ", " + how + ".\n\nA buff cannot be undone from the Change log; remove it here.", ok: "Give" })) return;
      GW.status("giving ...");
      try {
        const r = await this.ctx.api("apply", b);
        const bad = r.live.filter(x => x.result !== "applied");
        await GW.alert(r.applied + " online character(s) got it now" + (r.queued ? ", " + r.queued + " get it at their next login" : "") +
          (r.event ? ". Event buff #" + r.event.id + " runs until " + r.event.ends + " Brisbane." : ".") +
          (bad.length ? "\n\nNot given:\n" + bad.map(x => x.name + ": " + x.note).join("\n") : ""), "Given");
        GW.status(r.applied + " given", "ok");
        this.refreshEvents();
      } catch (e) { await GW.writeError(e); }
    },
    async removeTicked(statusIds) {
      if (!this.ticked.size) { GW.alert("Tick the characters first (3. Who: Ticked characters).", "Remove buffs"); return; }
      if (!await GW.confirm({ title: "Remove buffs from " + this.ticked.size + " character(s)?", message: (statusIds ? "This buff" : "Every buff the editor can remove") + " is taken off the ticked characters that are online, and their queued buffs are cancelled. Offline characters have no buffs (they end at logout).", ok: "Remove", danger: true })) return;
      try {
        const r = await this.ctx.api("remove", { ids: [...this.ticked], status_ids: statusIds });
        await GW.alert(r.removed + " buff(s) removed, " + r.queued_cancelled + " queued buff(s) cancelled.\n\n" +
          r.characters.map(c => c.name + ": " + (c.removed.length ? c.removed.join(", ") : "nothing") + (c.skipped.length ? " (kept: " + c.skipped.join(", ") + ")" : "") + (c.note ? " - " + c.note : "")).join("\n"), "Removed");
      } catch (e) { await GW.writeError(e); }
    },
    async clearAll() {
      const typed = h("input", { type: "text", "aria-label": "Type CLEAR" });
      if (!await GW.confirm({ title: "Clear buffs server-wide?", danger: true, ok: "Clear", el: h("div", null,
        h("p", null, "Every buff the editor can remove is taken off everyone online, every queued buff is cancelled and every event buff stops. Mount rides and permanent effects stay."),
        h("p", null, "Type CLEAR to confirm: ", typed)) })) return;
      try {
        const r = await this.ctx.api("clear", { confirm: typed.value.trim() });
        await GW.alert(r.removed + " buff(s) removed from " + r.online + " online character(s), " + r.queued_cancelled + " queued buff(s) cancelled, " + r.events_stopped + " event buff(s) stopped." +
          (r.skipped.length ? "\nKept (the game owns them): " + r.skipped.join(", ") : ""), "Cleared");
        this.refreshEvents();
      } catch (e) { await GW.writeError(e); }
    },
    async refreshEvents() { try { this.E = await this.ctx.api("events"); this.drawEvents(); } catch (e) { /* page gone */ } },
    drawEvents() {
      const box = GW.clear(this.eventsEl), E = this.E;
      box.append(h("p", { class: "gw-dim" }, E.job_running ? "The editor checks every " + E.every_seconds + " s" + (E.job.at_text ? " (last " + E.job.at_text + ")" : "") + " and gives each running event buff to players who entered the world since." : "The event job is not running: start the editor again.",
        E.job.error ? h("span", { class: "tt-bad" }, " Last error: " + E.job.error) : null, E.job_idle ? h("span", { class: "tt-bad" }, " Idle: " + E.job_idle + ".") : null));
      if (!E.events.length) { box.append(h("p", { class: "gw-dim" }, "No event buffs yet. Pick 'Until' in step 2 to start one.")); return; }
      box.append(h("table", { class: "gw-bt-table" }, h("thead", null, h("tr", null, h("th", null, "#"), h("th", null, "Buff"), h("th", null, "Who"), h("th", null, "Ends (Brisbane)"), h("th", null, "State"), h("th", null, "Given"), h("th", null, ""))),
        h("tbody", null, E.events.map(e => h("tr", null, h("td", null, String(e.id)), h("td", null, e.name), h("td", null, e.who), h("td", null, e.ends_text),
          h("td", null, h("span", { class: "gw-chip " + (e.state === "running" ? "now" : "never") }, e.state === "running" ? "running, " + fmtSecs(e.left_seconds) + " left" : e.state)),
          h("td", null, String(e.given)),
          h("td", null, e.state === "running" ? [
            h("button", { class: "gw-btnx small", type: "button", "data-help": "Stop giving it to players who log in. Those who have it keep it until the end time.", onclick: () => this.stopEvent(e, false) }, "Stop"),
            h("button", { class: "gw-btnx small danger", type: "button", "data-help": "Stop it and take it off everyone online now.", onclick: () => this.stopEvent(e, true) }, "Stop and remove")] : null))))));
    },
    async stopEvent(e, remove) {
      if (!await GW.confirm({ title: "Stop event buff #" + e.id + "?", message: e.name + (remove ? ": it stops and is taken off everyone online now." : ": nobody new gets it; those who have it keep it until " + e.ends_text + "."), ok: "Stop", danger: remove })) return;
      try { const r = await this.ctx.api("events/" + e.id + "/stop", { remove }); GW.status("event buff stopped" + (remove ? ", " + (r.removed || 0) + " removed" : ""), "ok"); this.refreshEvents(); }
      catch (err) { await GW.writeError(err); }
    },
  });
})();
