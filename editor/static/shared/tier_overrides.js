/* Shared E1 page controller. Text is inserted with GW.h; the normal API supplies CSRF and Undo. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  // seconds as people say them: 45 s, 30 min, 1.5 h
  const dur = s => { const n = Number(s); return n < 120 ? n + " s" : n < 7200 ? +(n / 60).toFixed(1) + " min" : +(n / 3600).toFixed(1) + " h"; };
  GW.tierEditor = function (kind) {
    const drop = kind === "drop";
    return {
      async mount(el, ctx) {
        this.ctx = ctx; this.active = true; this.epoch = (this.epoch || 0) + 1;
        this.body = h("div"); el.append(this.body); this.busy = false; this.notice = null;
        this.offChange = GW.on && GW.on("change", () => {
          if (this.active && !this.busy) this.load().catch(error => {
            if (this.active) this.body.append(h("p", { class: "tt-bad", role: "alert" }, error.message));
          });
        });
        try { await this.load(); } catch (error) { if (this.active) this.body.append(h("p", { class: "tt-bad", role: "alert" }, error.message)); }
      },
      unmount() { this.active = false; this.epoch++; this.ctx = null; if (this.offChange) this.offChange(); },
      current(ctx, epoch) { return this.active && this.ctx === ctx && this.epoch === epoch; },
      async load() {
        const ctx = this.ctx, epoch = this.epoch, request = this.request = (this.request || 0) + 1;
        const data = await ctx.api("overview");
        if (!this.current(ctx, epoch) || request !== this.request) return;
        this.data = data; this.render();
      },
      updateControls() {
        const blocked = this.busy || !this.data.table || !this.data.writable;
        this.inputs.forEach(input => { input.disabled = blocked; });
        this.buttons.forEach(button => { button.disabled = blocked; });
        this.checks.forEach(check => check());
      },
      async write(route, payload) {
        if (!this.active || this.busy || !this.data.table || !this.data.writable) return;
        const ctx = this.ctx, epoch = this.epoch;
        this.busy = true; this.updateControls();
        try {
          const result = await ctx.api(route, payload);
          if (!this.current(ctx, epoch)) return;
          const reload = route === "reload" ? result : result.reload;
          this.notice = reload || null;
          if (route !== "reload") GW.writeDone(result, reload && !reload.ok ? "Saved; game reload pending" : "Saved; game reload requested");
          await this.load();
        } catch (error) {
          if (!this.current(ctx, epoch)) return;
          const action = await GW.writeError(error);
          if (this.current(ctx, epoch) && action === "conflict") await this.load();
        } finally {
          if (this.current(ctx, epoch)) { this.busy = false; this.updateControls(); }
        }
      },
      async remove(row, snapshot) {
        if (this.busy || !this.active || this.data !== snapshot || !snapshot.writable) return;
        const ctx = this.ctx, epoch = this.epoch;
        const yes = await GW.confirm({ title: "Remove override", message: "Remove the " + row.tier + " override for " + row.map_name + "? The game will use its default for this scope after reload.", ok: "Remove", danger: true });
        if (!yes || !this.current(ctx, epoch) || this.data !== snapshot) return;
        await this.write("delete", { map_id: row.map_id, tier: row.tier, version: snapshot.version });
      },
      render() {
        const data = this.data, body = this.body;
        GW.clear(body); this.inputs = []; this.buttons = []; this.checks = [];
        body.append(h("p", null, drop ? "Chance that a kill drops any loot, in percent (15 = 15%)." : "How long a monster takes to come back: a random time between the minimum and the maximum, in seconds (1800 = 30 minutes)."));
        body.append(h("p", { class: "gw-dim" }, "Use All maps (-1) or a specific map. Saved settings need a compatible game-data reload. Removing a row restores the game's default for that scope. After Undo, reload game data too."));
        if (this.notice) body.append(h("p", { role: "status", class: this.notice.ok ? "gw-dim" : "gw-tier-warning" }, this.notice.message));
        if (!data.table) { body.append(h("p", { role: "alert", class: "tt-bad" }, data.why_missing)); return; }
        if (!data.writable) body.append(h("p", { role: "status", class: "gw-tier-warning" }, "This editor connection is read-only."));
        const reload = h("button", { type: "button", class: "gw-btnx", onclick: () => this.write("reload", {}) }, "Reload game data");
        this.buttons.push(reload); body.append(reload);
        const columns = drop ? ["Map", "Tier", "Chance (%)", "Note", ""] : ["Map", "Tier", "Minimum seconds", "Maximum seconds", "Note", ""];
        const table = h("table", { class: "gw-w-table gw-tier-table" }, h("tr", null, columns.map(text => h("th", null, text))));
        for (const row of data.rows) this.form(table, row, data);
        body.append(table);
        if (!data.rows.length) body.append(h("p", { class: "gw-dim" }, "No overrides saved. The game uses its defaults."));
        body.append(h("h3", null, "Add override")); this.form(body, null, data);
        if (data.groups) this.everyMap(body, data);
        this.updateControls();
      },
      // Wave 4 D item 6: every real map, by section, with its monsters per tier and what each tier really uses.
      everyMap(body, data) {
        const label = { map: "this map", all_maps: "all maps row", default: "default" };
        const show = (tier, eff) => {
          if (!eff || eff.source === "default") return h("span", { class: "gw-dim", title: data.default_text || "" }, "default");
          const r = eff.row || {};
          const value = drop ? (Number(r.any_drop_basis_points) / 100) + "%" : dur(r.minimum_seconds) + " to " + dur(r.maximum_seconds);
          return h("span", null, h("b", null, value), h("div", { class: "gw-dim" }, label[eff.source]));
        };
        const filter = h("input", { type: "search", placeholder: "Filter maps", "aria-label": "Filter maps", class: "gw-tier-note" });
        const box = h("div");
        const draw = () => {
          GW.clear(box); const q = (filter.value || "").trim().toLowerCase();
          for (const g of data.groups) {
            const maps = g.maps.filter(m => !q || (m.name + " " + m.id + " " + (m.scene || "")).toLowerCase().includes(q));
            if (!maps.length) continue;
            const table = h("table", { class: "gw-w-table gw-tier-table" }, h("tr", null, ["Map", "Monsters (normal / elite / boss)", ...data.tiers.map(t => t[0].toUpperCase() + t.slice(1)), ""].map(t => h("th", null, t))));
            for (const m of maps) {
              const mc = m.monsters || {};
              const edit = h("button", { type: "button", class: "gw-btnx small", onclick: () => this.pick(m.id) }, "Set");
              this.buttons.push(edit);
              table.append(h("tr", null, h("td", null, m.name, h("div", { class: "gw-dim" }, "Map " + m.id + (m.scene ? " · " + m.scene : "") + (m.town ? " · town" : ""))),
                h("td", null, (mc.normal || 0) + " / " + (mc.elite || 0) + " / " + (mc.boss || 0)),
                ...data.tiers.map(t => h("td", null, show(t, (m.effective || {})[t]))), h("td", null, edit)));
            }
            box.append(h("h4", { title: g.why || "" }, g.title + " (" + maps.length + ")"), table);
          }
        };
        filter.oninput = draw;
        body.append(h("h3", null, "Every map"), h("p", { class: "gw-dim" }, "What each map uses now: its own row, else the All maps row, else the default (" + (data.default_text || "stock behaviour") + "). Monster counts are published and GM spawns, by the tier the server uses (key contains _boss / _elite)."), filter, box);
        draw();
      },
      pick(mapId) {
        if (!this.addMap) return;
        this.addMap.value = String(mapId);
        if (this.addMap.oninput) this.addMap.oninput();
        if (this.addMap.focus) this.addMap.focus();
      },
      form(parent, row, snapshot) {
        const adding = !row, value = row || {};
        const opt = item => h("option", { value: String(item.id) }, item.name + " (" + item.id + ")");
        const map = adding ? h("select", { "aria-label": "Map" }, [h("option", { value: "-1" }, "All maps (-1)"),
          ...(snapshot.groups ? snapshot.groups.map(g => h("optgroup", { label: g.title }, g.maps.map(opt))) : snapshot.maps.map(opt))]) : null;
        if (adding) this.addMap = map;
        const tier = adding ? h("select", { "aria-label": "Monster tier" }, snapshot.tiers.map(name => h("option", { value: name }, name))) : null;
        const numeric = (name, label, min, max) => h("input", { type: "number", step: "1", min, max, required: true, value: value[name] === undefined ? "" : String(value[name]), class: "gw-tier-number", "aria-label": label });
        // the chance is stored in basis points (1/100 %): shown and typed as a percent, converted when saved
        const chance = drop ? h("input", { type: "number", step: "0.01", min: 0, max: 100, required: true, class: "gw-tier-number", "aria-label": "Drop chance (%)",
          value: value.any_drop_basis_points === undefined ? "" : String(Number(value.any_drop_basis_points) / 100) }) : null;
        const percent = input => { const t = input.value.trim(), n = Number(t); return t !== "" && isFinite(n) && n >= 0 && n <= 100 && Math.abs(n * 100 - Math.round(n * 100)) < 1e-6; };
        const minimum = drop ? null : numeric("minimum_seconds", "Minimum respawn seconds", 15, 86400);
        const maximum = drop ? null : numeric("maximum_seconds", "Maximum respawn seconds", 15, 86400);
        const note = h("input", { type: "text", maxlength: 200, value: value.note || "", class: "gw-tier-note", "aria-label": "Note" });
        const error = h("div", { class: "tt-bad gw-tier-error", role: "alert" });
        const preview = h("div", { class: "gw-dim" });
        const number = (input, lo, hi) => input.value.trim() !== "" && Number.isInteger(Number(input.value)) && Number(input.value) >= lo && Number(input.value) <= hi;
        const valid = () => drop ? percent(chance) : number(minimum, 15, 86400) && number(maximum, 15, 86400) && Number(minimum.value) <= Number(maximum.value);
        const save = h("button", { type: "button", class: "gw-btnx small", onclick: async () => {
          if (!valid() || this.data !== snapshot) return;
          const payload = { map_id: adding ? Number(map.value) : row.map_id, tier: adding ? tier.value : row.tier, note: note.value, version: snapshot.version };
          if (drop) payload.any_drop_basis_points = Math.round(Number(chance.value) * 100);
          else { payload.minimum_seconds = Number(minimum.value); payload.maximum_seconds = Number(maximum.value); }
          await this.write("save", payload);
        } }, adding ? "Add override" : "Save");
        let touched = false;
        const check = () => {
          error.textContent = valid() || !touched ? "" : drop ? "Enter a percent from 0 to 100, with at most 2 decimals." : "Use whole seconds from 15 to 86400 (24 hours), with the minimum no greater than the maximum.";
          preview.textContent = !valid() ? "" : drop ? Number(chance.value) + "% chance of any loot" : "= " + dur(minimum.value) + " to " + dur(maximum.value);
          save.disabled = this.busy || !snapshot.writable || !valid();
        };
        const inputs = [map, tier, chance, minimum, maximum, note].filter(Boolean);
        this.inputs.push(...inputs); this.buttons.push(save); this.checks.push(check);
        inputs.forEach(input => { input.oninput = () => { touched = true; check(); }; });
        let remove = null;
        if (!adding) { remove = h("button", { type: "button", class: "gw-btnx small", onclick: () => this.remove(row, snapshot) }, "Remove"); this.buttons.push(remove); }
        if (adding) {
          const label = (text, input) => h("label", null, h("span", null, text), input);
          parent.append(h("div", { class: "gw-tier-new" }, label("Map", map), label("Tier", tier),
            ...(drop ? [label("Chance (%)", chance)] : [label("Minimum seconds", minimum), label("Maximum seconds", maximum)]), label("Note", note), save, error, preview));
        } else parent.append(h("tr", null, h("td", null, row.map_name, h("div", { class: "gw-dim" }, "Map " + row.map_id)), h("td", null, row.tier),
          ...(drop ? [h("td", null, chance, preview)] : [h("td", null, minimum), h("td", null, maximum, preview)]), h("td", null, note, error), h("td", null, save, remove)));
      }
    };
  };
})();
