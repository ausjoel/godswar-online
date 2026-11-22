/* static/modules/login_awards.js - W4-B: login-window awards, Prize Chest gifts, buffs for everyone online or queued for
   the next login. Backend: modules/login_awards.py. Times are Brisbane (AEST, UTC+10); the database stores UTC. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const MONEY = [["silver", "Silver"], ["gold", "Gold"], ["binding_gold", "B-Gold"]];
  const num = (value, label, o) => h("input", Object.assign({ type: "number", value: value == null ? "" : value, "aria-label": label, class: "gw-la-num" }, o || {}));
  const when = s => (s || "").replace(" ", "T");

  // A small editable list of item rows: [{item_id, quantity, quality, grade, bound}]
  function itemRows(initial, limits) {
    const rows = (initial || []).map(r => Object.assign({ quantity: 1, quality: 1, grade: 1, bound: 1 }, r));
    const box = h("div", { class: "gw-la-items" });
    const draw = () => {
      GW.clear(box);
      rows.forEach((r, i) => {
        const pick = GW.idField("item", r.item_id || null, { title: "Pick an item", onChange: id => { r.item_id = id; } });
        box.append(h("div", { class: "gw-la-item" }, pick,
          h("label", null, "Qty ", num(r.quantity, "Quantity", { min: 1, max: limits.stack, onchange: e => { r.quantity = Number(e.target.value); } })),
          h("label", null, "Quality ", num(r.quality, "Quality", { min: 0, max: 30, onchange: e => { r.quality = Number(e.target.value); } })),
          h("label", null, "Grade ", num(r.grade, "Grade", { min: 0, max: 30, onchange: e => { r.grade = Number(e.target.value); } })),
          h("label", null, h("input", { type: "checkbox", checked: r.bound ? true : null, onchange: e => { r.bound = e.target.checked ? 1 : 0; } }), " bound"),
          h("button", { class: "gw-btnx small", type: "button", onclick: () => { rows.splice(i, 1); draw(); } }, "Remove")));
      });
      box.append(h("button", { class: "gw-btnx small", type: "button", disabled: rows.length >= limits.items ? true : null,
        onclick: () => { rows.push({ item_id: null, quantity: 1, quality: 1, grade: 1, bound: 1 }); draw(); } }, "Add item"),
        h("span", { class: "gw-dim" }, " At most " + limits.items + " rows; at most " + limits.stack + " per row (stacks never go above 99)."));
    };
    draw();
    return { el: box, value: () => rows.filter(r => r.item_id).map(r => ({ item_id: Number(r.item_id), quantity: r.quantity, quality: r.quality, grade: r.grade, bound: r.bound })) };
  }

  // Characters chosen by name search: [{id, name}]
  function characterPicker(label) {
    const chosen = new Map();
    const list = h("div", { class: "gw-la-chips" });
    const input = h("input", { type: "search", placeholder: "Type a character name and press Enter", "aria-label": label });
    const draw = () => {
      GW.clear(list);
      for (const [id, name] of chosen) list.append(h("span", { class: "gw-la-chip" }, name + " #" + id, " ",
        h("button", { class: "gw-btnx small", type: "button", "aria-label": "Remove " + name, onclick: () => { chosen.delete(id); draw(); } }, "x")));
      if (!chosen.size) list.append(h("span", { class: "gw-dim" }, "No character chosen."));
    };
    input.addEventListener("keydown", async e => {
      if (e.key !== "Enter") return;
      e.preventDefault();
      const q = input.value.trim(); if (!q) return;
      let rows = [];
      try { rows = await GW.api("/api/m/characters?q=" + encodeURIComponent(q)); } catch (err) { GW.status(err.message, "err"); return; }
      const exact = rows.find(r => String(r.name).toLowerCase() === q.toLowerCase()) || (rows.length === 1 ? rows[0] : null);
      if (!exact) { GW.status(rows.length ? rows.length + " matches: type the exact name" : "No character called " + q, "err"); return; }
      chosen.set(exact.id, exact.name); input.value = ""; draw();
    });
    draw();
    return { el: h("div", null, input, list), ids: () => Array.from(chosen.keys()), add: (id, name) => { chosen.set(id, name); draw(); } };
  }

  async function buffOptions() {
    try {
      const L = await GW.cached("/api/m/buffs/library");
      return (L.entries || []).filter(e => e.apply_status_id != null)
        .map(e => ({ id: e.apply_status_id, name: e.label || e.name, beneficial: e.beneficial !== false, duration: e.duration_s }));
    } catch (e) { return []; }
  }

  function buffSelect(options, value) {
    return h("select", { "aria-label": "Buff" }, h("option", { value: "" }, "(choose a buff)"),
      options.map(o => h("option", { value: String(o.id), selected: String(o.id) === String(value) ? true : null }, o.name + (o.beneficial ? "" : " (hostile)") + " #" + o.id)));
  }

  GW.module("login_awards", {
    async mount(el, ctx) {
      this.ctx = ctx; this.el = el;
      await this.reload();
    },
    unmount() { this.ctx = null; },
    async reload() {
      const ctx = this.ctx; if (!ctx) return;
      let D;
      try { D = await ctx.api("overview"); } catch (e) { GW.clear(this.el).append(h("p", { class: "tt-bad" }, e.message)); return; }
      if (this.ctx !== ctx) return;
      this.D = D; this.buffs = await buffOptions();
      this.render();
    },
    render() {
      const D = this.D, el = GW.clear(this.el), self = this;
      el.classList.add("gw-mod-login_awards");
      el.append(h("h2", null, "Login awards & Prize Chest"));
      if (!D.ready) { el.append(h("p", { class: "tt-bad" }, D.reason)); return; }
      el.append(h("p", { class: "gw-dim" }, "Times are " + D.time_zone + ". Now: " + D.now + ". The game reads awards at each world entry (no restart). " +
        "Players get one chat line, never a pop-up. Switches on the Rates page: prize.login_awards, prize.queued_buffs, prize.prize_chest."));

      // ---- awards
      const awards = h("section", { class: "gw-la-section" }, h("h3", null, "Awards for logging in between two dates"));
      const table = h("table", { class: "gw-la-table" }, h("thead", null, h("tr", null,
        ["Name", "Window (Brisbane)", "Gives", "Who", "Goes to", "Claimed", ""].map(x => h("th", null, x)))));
      const body = h("tbody");
      if (!D.awards.length) body.append(h("tr", null, h("td", { colspan: 7, class: "gw-dim" }, "No awards yet.")));
      for (const a of D.awards) {
        const gives = [].concat((a.items || []).map(i => i.quantity + " x " + i.name + (i.quality !== 1 ? " q" + i.quality : "")),
          MONEY.filter(([k]) => a[k]).map(([k, n]) => Number(a[k]).toLocaleString() + " " + n),
          (a.buffs || []).map(b => "buff #" + b.status_id), a.title_id ? ["title #" + a.title_id] : []);
        body.append(h("tr", { class: a.enabled ? null : "gw-dim" },
          h("td", null, a.name, a.open ? h("b", null, " (open now)") : null),
          h("td", null, a.starts + " to " + a.ends),
          h("td", null, gives.join(", ")),
          h("td", null, (a.per_scope === "account" ? "once per account" : "once per character") + (a.min_level > 1 ? ", level " + a.min_level + "+" : "")),
          h("td", null, a.delivery === "prize_chest" ? "Prize Chest" : "bag"),
          h("td", null, h("button", { class: "gw-btnx small", type: "button", onclick: () => self.showClaims(a) }, String(a.claims)),
            a.claims_chest ? h("span", { class: "gw-dim" }, " (" + a.claims_chest + " to the chest)") : null),
          h("td", null,
            h("button", { class: "gw-btnx small", type: "button", onclick: () => self.editAward(a) }, "Edit"), " ",
            h("button", { class: "gw-btnx small", type: "button", onclick: async () => {
              try { const r = await self.ctx.api("award/" + a.id + "/enabled", { enabled: !a.enabled, version: D.version }); GW.writeDone(r, a.enabled ? "Award disabled" : "Award enabled"); await self.reload(); }
              catch (e) { await GW.writeError(e); }
            } }, a.enabled ? "Disable" : "Enable"))));
      }
      table.append(body);
      awards.append(table, h("button", { class: "gw-btnx", type: "button", onclick: () => self.editAward(null) }, "New award"));
      el.append(awards);

      // ---- Prize Chest
      const chest = h("section", { class: "gw-la-section" }, h("h3", null, "Send to the Prize Chest"),
        h("p", { class: "gw-dim" }, "The Prize Chest stands next to the warehouse keeper in Athens and Sparta (Athens_123 / Sparta_123). " +
          "Players talk to it to collect: what fits goes into the bag, the rest keeps waiting. Works for online and offline characters. " +
          "Waiting now: " + (D.chest.waiting || 0) + " prize(s) for " + (D.chest.characters || 0) + " character(s)."));
      const who = characterPicker("Characters to send to");
      const items = itemRows([], D.limits);
      const money = MONEY.map(([k, n]) => [k, num(0, n, { min: 0, max: D.limits[k] })]);
      const note = h("input", { type: "text", maxlength: 512, placeholder: "Note (optional, kept in the record)", "aria-label": "Note" });
      chest.append(h("div", null, h("b", null, "Characters "), who.el), items.el,
        h("div", { class: "gw-la-money" }, money.map(([k, input]) => h("label", null, MONEY.find(m => m[0] === k)[1] + " ", input))), note,
        h("div", null,
          h("button", { class: "gw-btnx", type: "button", onclick: async () => {
            const body = { targets: who.ids(), items: items.value(), note: note.value };
            for (const [k, input] of money) body[k] = Number(input.value || 0);
            if (!(await GW.confirm({ title: "Send to the Prize Chest?", message: "To " + body.targets.length + " character(s): " + body.items.length + " item row(s) and the money shown." }))) return;
            try { const r = await self.ctx.api("chest/send", body); GW.writeDone(r, "Sent to " + r.sent_to + " Prize Chest(s)" + (r.online && r.online.note ? " \u00b7 " + r.online.note : "")); await self.reload(); }
            catch (e) { await GW.writeError(e); }
          } }, "Send"), " ",
          h("button", { class: "gw-btnx", type: "button", onclick: () => { const ids = who.ids(); if (ids.length !== 1) { GW.status("Choose exactly one character to view its chest.", "err"); return; } self.showChest(ids[0]); } }, "View one character's chest")));
      el.append(chest);

      // ---- Buffs
      const buffs = h("section", { class: "gw-la-section" }, h("h3", null, "Buffs for many players"));
      const sel = buffSelect(this.buffs);
      const dur = num("", "Duration in seconds", { min: 1, placeholder: "authored" });
      const camp = h("select", { "aria-label": "Faction" }, h("option", { value: "" }, "both factions"), h("option", { value: "1" }, "Athens"), h("option", { value: "0" }, "Sparta"));
      const minL = num(1, "Minimum level", { min: 1, max: 200 }), maxL = num(200, "Maximum level", { min: 1, max: 200 });
      const queue = h("input", { type: "checkbox", "aria-label": "Also queue for offline characters" });
      const hostile = h("input", { type: "checkbox", "aria-label": "Allow a hostile status" });
      const result = h("div", { class: "gw-la-result" });
      buffs.append(
        h("p", { class: "gw-dim" }, "Buffs are session effects: they last their duration or until the player logs out. A queued buff starts when the character next enters the world."),
        h("div", { class: "gw-la-row" }, h("label", null, "Buff ", sel), h("label", null, "Seconds ", dur)),
        h("div", { class: "gw-la-row" }, h("label", null, "Who ", camp), h("label", null, "Level ", minL), h("label", null, " to ", maxL)),
        h("div", { class: "gw-la-row" }, h("label", null, queue, " also queue it for every offline character that matches (applied at their next login)"),
          h("label", null, hostile, " allow a hostile status")),
        h("button", { class: "gw-btnx", type: "button", disabled: D.broadcast.available ? null : true, onclick: async () => {
          const body = { status_id: sel.value, duration_seconds: dur.value, camp: camp.value, min_level: minL.value, max_level: maxL.value, queue_offline: queue.checked, allow_hostile: hostile.checked };
          if (!body.status_id) { GW.status("Choose a buff.", "err"); return; }
          if (!(await GW.confirm({ title: "Buff everyone online?", message: sel.options[sel.selectedIndex].text + " to every online player who matches" + (queue.checked ? ", and queued for every offline one" : "") + "." }))) return;
          try {
            const r = await self.ctx.api("buffs/broadcast", body);
            GW.clear(result).append(h("p", null, "Matched " + r.matched + " online: " + r.applied + " applied, " + r.notApplied + " not applied (a stronger buff, or dead), " + r.busy + " busy." + (r.queuedOffline ? " Queued for " + r.queuedOffline + " offline." : "")));
            GW.status("Server-wide buff sent", "ok");
          } catch (e) { await GW.writeError(e); }
        } }, "Apply to everyone online"),
        D.broadcast.available ? null : h("span", { class: "tt-bad" }, " " + D.broadcast.reason), result);
      const qwho = characterPicker("Characters to queue a buff for");
      const qdiscard = h("input", { type: "datetime-local", "aria-label": "Drop it if not back by (Brisbane)" });
      buffs.append(h("h4", null, "Queue for the next login of chosen characters"), qwho.el,
        h("div", { class: "gw-la-row" }, h("label", null, "Drop it if not back by (Brisbane, optional) ", qdiscard)),
        h("button", { class: "gw-btnx", type: "button", onclick: async () => {
          const body = { targets: qwho.ids(), status_id: sel.value, duration_seconds: dur.value, discard_after: qdiscard.value ? qdiscard.value.replace("T", " ") : "" };
          if (!body.status_id) { GW.status("Choose a buff above.", "err"); return; }
          try { const r = await self.ctx.api("buffs/queue", body); GW.writeDone(r, "Queued for " + r.queued + " character(s)"); await self.reload(); }
          catch (e) { await GW.writeError(e); }
        } }, "Queue buff (uses the buff and seconds above)"),
        h("button", { class: "gw-btnx", type: "button", onclick: () => self.showPending() }, "Waiting queued buffs (" + (D.queued.waiting || 0) + ")"));
      el.append(buffs);
    },

    editAward(a) {
      const D = this.D, self = this, src = a || { name: "", starts: D.now, ends: D.now, per_scope: "character", delivery: "bag", items: [], buffs: [], min_level: 1, enabled: true, note: "" };
      const name = h("input", { type: "text", maxlength: 80, value: src.name, "aria-label": "Name" });
      const starts = h("input", { type: "datetime-local", value: when(src.starts), "aria-label": "Start (Brisbane)" });
      const ends = h("input", { type: "datetime-local", value: when(src.ends), "aria-label": "End (Brisbane)" });
      const scope = h("select", { "aria-label": "Once per" }, h("option", { value: "character", selected: src.per_scope !== "account" || null }, "once per character"), h("option", { value: "account", selected: src.per_scope === "account" || null }, "once per account (the first character to log in)"));
      const delivery = h("select", { "aria-label": "Delivery" }, h("option", { value: "bag", selected: src.delivery !== "prize_chest" || null }, "into the bag (a full bag sends the items to the Prize Chest)"), h("option", { value: "prize_chest", selected: src.delivery === "prize_chest" || null }, "to the Prize Chest by the warehouse"));
      const items = itemRows(src.items, D.limits);
      const money = MONEY.map(([k, n]) => [k, num(src[k] || 0, n, { min: 0, max: D.limits[k] })]);
      const buffRows = (src.buffs || []).slice(0, D.limits.buffs);
      const buffBox = h("div");
      const drawBuffs = () => {
        GW.clear(buffBox);
        buffRows.forEach((b, i) => {
          const s = buffSelect(self.buffs, b.status_id); s.onchange = e => { b.status_id = e.target.value; };
          buffBox.append(h("div", { class: "gw-la-row" }, s, h("label", null, " seconds ", num(b.duration_seconds, "Seconds", { min: 1, placeholder: "authored", onchange: e => { b.duration_seconds = e.target.value; } })),
            h("button", { class: "gw-btnx small", type: "button", onclick: () => { buffRows.splice(i, 1); drawBuffs(); } }, "Remove")));
        });
        if (buffRows.length < D.limits.buffs) buffBox.append(h("button", { class: "gw-btnx small", type: "button", onclick: () => { buffRows.push({ status_id: "", duration_seconds: "" }); drawBuffs(); } }, "Add buff"));
      };
      drawBuffs();
      const title = h("select", { "aria-label": "Title" }, h("option", { value: "" }, "(no title)"), D.titles.map(t => h("option", { value: String(t.id), selected: t.id === src.title_id || null }, t.name + " #" + t.id)));
      const minLevel = num(src.min_level || 1, "Minimum level", { min: 1, max: 200 });
      const enabled = h("input", { type: "checkbox", checked: src.enabled ? true : null, "aria-label": "Enabled" });
      const note = h("input", { type: "text", maxlength: 512, value: src.note || "", "aria-label": "Note" });
      const save = h("button", { class: "gw-btnx", type: "button", onclick: async () => {
        const award = { name: name.value, starts: starts.value.replace("T", " "), ends: ends.value.replace("T", " "), per_scope: scope.value, delivery: delivery.value,
          items: items.value(), buffs: buffRows.filter(b => b.status_id).map(b => ({ status_id: Number(b.status_id), duration_seconds: b.duration_seconds ? Number(b.duration_seconds) : null })),
          title_id: title.value ? Number(title.value) : null, min_level: Number(minLevel.value || 1), enabled: enabled.checked, note: note.value };
        for (const [k, input] of money) award[k] = Number(input.value || 0);
        try { const r = await self.ctx.api("award/save", { id: a ? a.id : null, award, version: D.version }); GW.writeDone(r, "Award saved"); m.close(); await self.reload(); }
        catch (e) { await GW.writeError(e); }
      } }, "Save award");
      const m = GW.modal(h("div", { class: "gw-mod-login_awards gw-la-form" },
        h("label", null, "Name ", name),
        h("div", { class: "gw-la-row" }, h("label", null, "From (Brisbane) ", starts), h("label", null, " until ", ends)),
        h("p", { class: "gw-dim" }, "A character entering the world from the start time up to (not including) the end time gets it once."),
        h("div", { class: "gw-la-row" }, scope, delivery), h("label", null, "Minimum level ", minLevel),
        h("h4", null, "Items"), items.el,
        h("div", { class: "gw-la-money" }, money.map(([k, input]) => h("label", null, MONEY.find(x => x[0] === k)[1] + " ", input))),
        h("h4", null, "Buffs (applied at that login; they end at logout)"), buffBox,
        h("label", null, "Title ", title), h("label", null, enabled, " enabled"), h("label", null, "Note ", note),
        h("div", null, save, " ", h("button", { class: "gw-btnx", type: "button", onclick: () => m.close() }, "Cancel"))),
        { title: a ? "Edit award #" + a.id : "New login award" });
    },

    async showClaims(a) {
      let r; try { r = await this.ctx.api("award/" + a.id + "/claims"); } catch (e) { GW.status(e.message, "err"); return; }
      GW.modal(h("div", { class: "gw-mod-login_awards" }, r.claims.length ? h("table", { class: "gw-la-table" },
        h("tr", null, ["Character", "Account", "Went to", "When (Brisbane)"].map(x => h("th", null, x))),
        r.claims.map(c => h("tr", null, h("td", null, (c.character || "(deleted)") + (c.character_id ? " #" + c.character_id : "")), h("td", null, "#" + c.account_id),
          h("td", null, c.delivered_to === "prize_chest" ? "Prize Chest" : "bag"), h("td", null, c.claimed)))) : h("p", null, "Nobody has claimed it yet.")),
        { title: "Claims of " + a.name });
    },

    async showChest(cid) {
      const self = this;
      let r; try { r = await this.ctx.api("chest/" + cid); } catch (e) { GW.status(e.message, "err"); return; }
      const m = GW.modal(h("div", { class: "gw-mod-login_awards" }, r.entries.length ? h("table", { class: "gw-la-table" },
        h("tr", null, ["What", "Amount", "From", "Added", "Collected", ""].map(x => h("th", null, x))),
        r.entries.map(e => h("tr", { class: e.claimed_at ? "gw-dim" : null }, h("td", null, e.name + (e.kind === "item" && e.item_quality !== 1 ? " q" + e.item_quality : "")),
          h("td", null, Number(e.quantity).toLocaleString()), h("td", null, e.source), h("td", null, e.created), h("td", null, e.claimed || "waiting"),
          h("td", null, e.claimed_at ? null : h("button", { class: "gw-btnx small", type: "button", onclick: async () => {
            try { const w = await self.ctx.api("chest/" + e.id + "/remove", {}); GW.writeDone(w, "Prize taken back"); m.close(); self.showChest(cid); }
            catch (err) { await GW.writeError(err); }
          } }, "Take back"))))) : h("p", null, "This Prize Chest is empty.")), { title: "Prize Chest of character #" + cid });
    },

    async showPending() {
      const self = this;
      let r; try { r = await this.ctx.api("buffs/pending"); } catch (e) { GW.status(e.message, "err"); return; }
      const m = GW.modal(h("div", { class: "gw-mod-login_awards" }, r.rows.length ? h("table", { class: "gw-la-table" },
        h("tr", null, ["Character", "Buff", "Seconds", "Queued", "Drop after", ""].map(x => h("th", null, x))),
        r.rows.map(q => h("tr", null, h("td", null, (q.character || "?") + " #" + q.character_id), h("td", null, q.name), h("td", null, q.duration_seconds || "authored"),
          h("td", null, q.created), h("td", null, q.discard || ""),
          h("td", null, h("button", { class: "gw-btnx small", type: "button", onclick: async () => {
            try { const w = await self.ctx.api("buffs/pending/" + q.id + "/cancel", {}); GW.writeDone(w, "Queued buff cancelled"); m.close(); await self.reload(); }
            catch (err) { await GW.writeError(err); }
          } }, "Cancel"))))) : h("p", null, "No buff is waiting.")), { title: "Buffs waiting for the next login" });
    }
  });
})();
