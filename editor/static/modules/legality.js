/* static/modules/legality.js - B21 item 30: items normal play cannot make, with the attribute names, the server's
   verdict (stops the login / hidden / impossible but harmless) and Fix or Remove for one item, one character or all.
   Backend: modules/legality.py. Database and client text is inserted as text, never as HTML. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const CLS_HELP = {
    breaks_login: "The game server's own login check fails on this character: it cannot enter the world until this is fixed.",
    hidden: "The login works, but the server leaves this item out of what the player sees until it is fixed or removed.",
    harmless: "Normal play cannot make this, but the game loads it fine: the login works and the player sees the item.",
  };
  const FIX_TEXT = { clamp: "Lower to the legal value", clamp_sockets: "Keep the first 4 sockets", clear_attribute: "Remove this attribute", clear_class_stone: "Remove the class stone", replace_attribute: "Swap for a legal attribute", remove: "Delete the item" };

  GW.module("legality", {
    async mount(el, ctx) {
      this.el = el; this.ctx = ctx; this.filter = { cls: "", text: "" };
      await this.load();
    },
    async load() {
      const el = this.el;
      GW.clear(el).append(h("p", { class: "gw-dim" }, "Checking every character (and asking the game server to dry-run each login) ..."));
      try { [this.D] = await Promise.all([this.ctx.api("scan"), GW.catalog.load("item").catch(() => null)]); } catch (e) { GW.clear(el).append(h("p", { class: "tt-bad" }, e.message)); return; }
      this.draw();
    },
    counts() {
      const n = { breaks_login: 0, hidden: 0, harmless: 0 };
      for (const c of this.D.characters) for (const f of c.findings) n[f.cls]++;
      return n;
    },
    draw() {
      const D = this.D, el = this.el, n = this.counts();
      const chip = (cls, extra) => h("span", { class: "gw-leg-chip gw-leg-" + cls, "data-help": CLS_HELP[cls] }, D.classes[cls] + (extra !== undefined ? ": " + extra : ""));
      const search = h("input", { type: "search", class: "gw-leg-search", placeholder: "Filter: character, item or attribute", value: this.filter.text, "aria-label": "Filter" });
      search.addEventListener("input", () => { this.filter.text = search.value; this.drawList(); });
      const sel = h("select", { class: "gw-leg-sel", "aria-label": "Show" }, h("option", { value: "" }, "Every finding"),
        Object.keys(D.classes).map(k => h("option", { value: k, selected: this.filter.cls === k || null }, D.classes[k] + " only")));
      sel.addEventListener("change", () => { this.filter.cls = sel.value; this.drawList(); });
      this.list = h("div", { class: "gw-leg-list" });
      GW.clear(el).append(
        h("div", { class: "gw-leg-head" },
          h("p", null, "Items that normal play cannot make. Each one is marked with what the game server does with it: ",
            chip("breaks_login"), " ", chip("hidden"), " ", chip("harmless"), "."),
          D.server_checked ? h("p", { class: "gw-dim" }, "The game server dry-ran every login for this list (it writes nothing). Items it did not object to load fine.")
            : h("p", { class: "tt-bad" }, D.server_note),
          h("div", { class: "gw-leg-totals" }, chip("breaks_login", n.breaks_login), chip("hidden", n.hidden), chip("harmless", n.harmless),
            h("button", { class: "gw-btnx small", type: "button", onclick: () => this.load() }, "Check again"))),
        h("div", { class: "gw-leg-tools" }, search, sel,
          h("button", { class: "gw-btnx", type: "button", "data-help": "Make every listed item legal and keep it: lower a level, quality, grade or stack, or remove an attribute it cannot roll. One save per character; Undo in the Change log.", onclick: () => this.all("fix") }, "Fix all ..."),
          h("button", { class: "gw-btnx danger", type: "button", "data-help": "Delete every listed item. One save per character; Undo in the Change log.", onclick: () => this.all("remove") }, "Remove all ...")),
        this.list);
      this.drawList();
    },
    match(c, f) {
      if (this.filter.cls && f.cls !== this.filter.cls) return false;
      const t = this.filter.text.trim().toLowerCase();
      if (!t) return true;
      return [c.character, f.item, f.attribute_name || "", f.message, f.where].join(" ").toLowerCase().includes(t);
    },
    drawList() {
      const list = GW.clear(this.list);
      let shown = 0;
      for (const c of this.D.characters) {
        const fs = c.findings.filter(f => this.match(c, f));
        if (!fs.length && !(c.can_enter === false && !this.filter.cls)) continue;
        shown++;
        const worst = fs.some(f => f.cls === "breaks_login") || c.can_enter === false ? "breaks_login" : fs.some(f => f.cls === "hidden") ? "hidden" : "harmless";
        const body = h("tbody");
        for (const f of fs) body.append(this.row(c, f));
        list.append(h("details", { class: "gw-leg-char", open: shown <= 3 || null },
          h("summary", null, h("b", null, c.character), " level " + c.level + " " + c["class"] + (c.gm ? " (GM)" : ""), " ",
            h("span", { class: "gw-dot " + (c.online ? "on" : "off"), "data-help": c.online ? "Online: fixes to worn and bag gear need a logout (a bag item can still be removed)." : "Offline" }), " ",
            h("span", { class: "gw-leg-chip gw-leg-" + worst }, fs.length + " item finding" + (fs.length === 1 ? "" : "s")),
            c.can_enter === false ? h("span", { class: "gw-leg-chip gw-leg-breaks_login" }, "cannot log in") : null,
            h("button", { class: "gw-btnx small", type: "button", disabled: !fs.length || null, onclick: ev => { ev.preventDefault(); this.fixChar(c, fs, "fix"); } }, "Fix these")),
          c.server_findings.length ? h("ul", { class: "tt-bad" }, c.server_findings.map(s => h("li", null, (s.severity === "blocks" ? "Stops the login: " : "Hidden: ") + s.message))) : null,
          fs.length ? h("table", { class: "gw-leg-table" }, h("thead", null, h("tr", null, h("th", null, "Effect"), h("th", null, "Where"), h("th", null, "Item"), h("th", null, "Problem"), h("th", null, "Fix"))), body)
            : h("p", { class: "gw-dim" }, "No item problem; the server named the problem above.")));
      }
      if (!shown) list.append(h("p", { class: "gw-dim" }, this.D.characters.length ? "Nothing matches." : "No character found."));
    },
    row(c, f) {
      const it = GW.catalog.loadedRow ? (GW.catalog.loadedRow("item", f.item_id) || {}) : {};
      const pick = h("select", { class: "gw-leg-sel", "aria-label": "Fix for " + f.item }, f.fixes.map(k => h("option", { value: k }, FIX_TEXT[k] || k)));
      const choice = h("select", { class: "gw-leg-sel", hidden: true, "aria-label": "Legal attribute" },
        (f.choices || []).map(x => h("option", { value: x.id }, x.name + " (id " + x.id + ", max level " + x.max + ")")));
      pick.addEventListener("change", () => { choice.hidden = pick.value !== "replace_attribute"; });
      const go = h("button", { class: "gw-btnx small" + (f.fixes[0] === "remove" ? " danger" : ""), type: "button", onclick: async () => {
        const kind = pick.value;
        const p = { row: f.row, rule: f.rule, index: f.index, kind };
        if (kind === "replace_attribute") p.choice = Number(choice.value);
        const label = kind === "remove" ? "Delete " + f.item + " (" + f.where + ") from " + c.character + "?" : FIX_TEXT[kind] + " on " + f.item + " (" + c.character + ")?";
        if (!await GW.confirm({ title: label, message: f.message + "\n\nUndo in the Change log puts the item back.", ok: kind === "remove" ? "Delete" : "Fix", danger: kind === "remove" })) return;
        await this.send(c, [p], kind === "remove" ? f.item + " deleted" : f.item + " fixed");
      } }, "Apply");
      return h("tr", { class: "gw-leg-r-" + f.cls },
        h("td", null, h("span", { class: "gw-leg-chip gw-leg-" + f.cls, "data-help": CLS_HELP[f.cls] + (f.server_message ? "\nServer: " + f.server_message : "") }, f.cls_text)),
        h("td", null, f.where),
        h("td", null, GW.cell ? GW.cell({ sprite: it.sprite, name: f.item, tipType: "item", tipId: f.item_id, small: true }) : h("span", null, f.item)),
        h("td", null, f.attribute_name ? h("b", null, f.attribute_name + " ") : null, h("span", null, f.attribute_name ? f.message.replace(f.attribute_name, "").trim() : f.message),
          h("span", { class: "gw-dim" }, f.severity === "impossible" ? " (the game cannot make this)" : " (above normal play)")),
        h("td", { class: "gw-leg-fix" }, pick, choice, go));
    },
    async send(c, picks, label) {
      GW.status("saving ...");
      try {
        const r = await this.ctx.api("fix/" + c.cid, { picks });
        GW.writeDone(r.change ? r : (r.results && r.results[0]) || r, label);
        await this.load();
      } catch (e) {
        if (e.status === 423) { await GW.alert(e.message + (c.kick ? "\n\nUse Kick on the character's page to log them out, then try again." : ""), c.character + " is online"); return; }
        const kind = await GW.writeError(e);
        if (kind === "conflict") await this.load();
      }
    },
    async fixChar(c, fs, action) {
      if (!await GW.confirm({ title: (action === "remove" ? "Delete " : "Fix ") + fs.length + " finding(s) on " + c.character + "?",
        message: "Each item is made legal and kept (a level, quality, grade, stack or socket count is lowered; an attribute it cannot roll is removed). Nothing is deleted: problems only Remove can solve are left for you. One save; Undo in the Change log.", ok: "Fix" })) return;
      const keep = fs.filter(f => f.fixes.some(k => k !== "remove" && k !== "replace_attribute"));
      if (!keep.length) { await GW.alert("These can only be removed: use Delete the item on each row.", "Nothing to fix"); return; }
      await this.send(c, keep.map(f => ({ row: f.row, rule: f.rule, index: f.index })), c.character + ": " + keep.length + " fixed" + (keep.length < fs.length ? " (" + (fs.length - keep.length) + " need Remove)" : ""));
    },
    async all(action) {
      const boxes = {};
      const pickCls = h("div", null, Object.keys(this.D.classes).map(k => { boxes[k] = h("input", { type: "checkbox", checked: k !== "harmless" || this.filter.cls === "harmless" || !this.filter.cls || null });
        return h("label", { class: "gw-leg-line" }, boxes[k], " " + this.D.classes[k] + " (" + this.counts()[k] + ")"); }));
      if (!await GW.confirm({ title: action === "remove" ? "Delete every such item?" : "Fix every such item?", ok: action === "remove" ? "Delete them" : "Fix them", danger: action === "remove",
        el: h("div", null, h("p", null, action === "remove" ? "Every item with a finding of these kinds is deleted:" : "Every finding of these kinds is made legal and the item kept (nothing is deleted; problems only Remove can solve are listed afterwards):"), pickCls,
          h("p", { class: "gw-dim" }, "One save per character. Online characters that need a logout are skipped and listed. Undo each character's save in the Change log.")) })) return;
      const classes = Object.keys(boxes).filter(k => boxes[k].checked);
      if (!classes.length) return;
      GW.status("saving ...");
      let r;
      try { r = await this.ctx.api("fix-all", { action, classes }); } catch (e) { await GW.writeError(e); return; }
      const lines = r.done.map(x => "OK  " + x.character + ": " + x.fixed + " (" + x.mode + ")").concat(r.skipped.map(x => "SKIPPED  " + x.character + ": " + x.reason),
        (r.needs_remove || []).map(x => "NEEDS REMOVE  " + x.character + ": " + x.items.join("; ")));
      await GW.alert(lines.join("\n") || "Nothing to do.", action === "remove" ? "Remove all" : "Fix all");
      GW.status(r.done.length + " character(s) saved, " + r.skipped.length + " skipped", r.skipped.length ? "err" : "ok");
      await this.load();
    },
  });
})();
