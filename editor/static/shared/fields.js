/* shared/fields.js - edit controls every module page uses.
   GW.numberField({field, value, ...})  number box + slider; min / max / help from the glossary
   GW.appliesChip(applies)              "applies at the next login" / "after gw251 restart" / "now"
   GW.onlineBanner(cid)                 is this character online, and what can still be edited
   GW.saveBar({onSave})                 collects pending edits -> Review (confirm) -> Save -> Undo
   GW.writeError(e) / GW.writeDone(r)   the standard reactions to a module write */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const TEXT = {
    now: "applies at the next use", per_use: "applies at the next use", hot_reload: "applies within 5 seconds",
    next_login: "applies at the next login", login: "applies at the next login", after_restart: "applies after gw251 restart",
    startup: "applies after gw251 restart", never: "the server does not read this", not_editable: "not editable",
    new_revision_and_restart: "not editable (published content)", live: "applied now (live)", unknown: "not established" };

  // ------------------------------------------------------------ applies chip
  GW.appliesChip = function (applies, text) {
    const el = h("span", { class: "gw-chip " + (GW.APPLY_CLASS[applies] || "never"), "data-help": "When the game sees this change: " + (text || TEXT[applies] || applies) }, text || TEXT[applies] || applies || "?");
    return el;
  };
  GW.appliesText = a => TEXT[a] || a;

  // ------------------------------------------------------------ number field + slider
  // opts: {field: "table.col" (help, range, applies), value, min, max, step (1), label, unit, slider (true when the
  //        range is known), onChange(value, valid), disabled, decimals (0), width}
  GW.numberField = function (opts) {
    const o = Object.assign({ step: 1, decimals: 0, slider: true }, opts);
    let min = o.min, max = o.max, value = o.value === undefined || o.value === null ? null : Number(o.value);
    const orig = value;
    const label = h("span", { class: "gw-nf-label gw-label", "data-field": o.field || null }, o.label || "");
    const input = h("input", { class: "gw-nf-input", type: "text", inputmode: o.decimals ? "decimal" : "numeric", "aria-label": o.label || o.field || "value", disabled: o.disabled ? true : null });
    const box = h("span", { class: "gw-nf-box" }, h("span", { class: "gw-sprite gw-edit-left" }), input, h("span", { class: "gw-sprite gw-edit-right" }));
    const slider = h("input", { class: "gw-nf-slider hidden", type: "range", min: 0, max: 1000, step: 1, "aria-label": (o.label || "value") + " slider", disabled: o.disabled ? true : null });
    const hint = h("span", { class: "gw-nf-hint" });
    const chip = h("span", { class: "gw-nf-chip" });
    const err = h("span", { class: "gw-nf-err" });
    const el = h("span", { class: "gw-nf" + (o.disabled ? " disabled" : "") }, o.label !== undefined ? label : null, box, o.unit ? h("span", { class: "gw-nf-unit" }, o.unit) : null, slider, hint, chip, err);
    const fmt = v => v === null || isNaN(v) ? "" : (o.decimals ? Number(v).toFixed(o.decimals) : Number(v).toLocaleString("en-US"));
    const parse = s => { const t = String(s).replace(/[, _]/g, "").trim(); if (t === "") return null; const n = Number(t); return isNaN(n) ? NaN : n; };
    const logScale = () => min !== undefined && max !== undefined && min !== null && max !== null && (max - min) > 5000;
    function toPos(v) {
      if (v === null || isNaN(v)) return 0;
      const span = max - min;
      if (logScale()) return Math.round(Math.log1p(Math.max(0, v - min)) / Math.log1p(span) * 1000);
      return Math.round((v - min) / span * 1000);
    }
    function fromPos(p) {
      const span = max - min;
      let v = logScale() ? min + Math.expm1(p / 1000 * Math.log1p(span)) : min + span * p / 1000;
      if (!o.decimals) v = Math.round(v); else v = Number(v.toFixed(o.decimals));
      if (logScale() && v > 100) { const mag = Math.pow(10, Math.floor(Math.log10(v)) - 2); v = Math.round(v / mag) * mag; }
      return Math.max(min, Math.min(max, v));
    }
    function check() {
      let msg = "";
      if (value === null) msg = "";
      else if (isNaN(value)) msg = "not a number";
      else if (!o.decimals && !Number.isInteger(value)) msg = "whole numbers only";
      else if (min !== undefined && min !== null && value < min) msg = "at least " + fmt(min);
      else if (max !== undefined && max !== null && value > max) msg = "at most " + fmt(max);
      err.textContent = msg;
      el.classList.toggle("bad", !!msg);
      el.classList.toggle("changed", value !== orig && !(value === null && orig === null));
      return !msg;
    }
    function emit() { const ok = check(); if (o.onChange) o.onChange(value, ok); }
    function sync() {
      if (document.activeElement !== input) input.value = fmt(value);
      if (min !== undefined && min !== null && max !== undefined && max !== null) slider.value = toPos(value);
    }
    function setRange(a, b) {
      if (a !== undefined && a !== null) min = Number(a);
      if (b !== undefined && b !== null) max = Number(b);
      const known = min !== undefined && min !== null && max !== undefined && max !== null && isFinite(min) && isFinite(max) && max > min;
      slider.classList.toggle("hidden", !(o.slider && known));
      hint.textContent = known ? fmt(min) + " – " + fmt(max) : "";
      sync(); check();
    }
    input.addEventListener("input", () => { value = parse(input.value); if (!isNaN(value) && value !== null && max !== undefined) slider.value = toPos(value); emit(); });
    input.addEventListener("blur", () => { if (value !== null && !isNaN(value)) input.value = fmt(value); });
    input.addEventListener("keydown", e => {
      if (e.key === "ArrowUp" || e.key === "ArrowDown") {
        e.preventDefault();
        const d = (e.key === "ArrowUp" ? 1 : -1) * (e.shiftKey ? 10 : 1) * o.step;
        value = (value === null || isNaN(value) ? (min || 0) : value) + d;
        if (max !== undefined && max !== null) value = Math.min(max, value);
        if (min !== undefined && min !== null) value = Math.max(min, value);
        input.value = fmt(value); slider.value = toPos(value); emit();
      }
    });
    slider.addEventListener("input", () => { value = fromPos(Number(slider.value)); input.value = fmt(value); emit(); });
    input.value = fmt(value);
    setRange(min, max);
    if (o.field) {
      GW.glossary.field(o.field).then(f => {
        if (o.label === undefined) { label.textContent = f.label || f.column; el.prepend(label); }
        else if (!o.label) label.textContent = f.label || f.column;
        setRange(o.min !== undefined ? o.min : f.min, o.max !== undefined ? o.max : f.max);
        GW.clear(chip).append(GW.appliesChip(f.applies_rule, f.applies_text));
        if (!f.editable && !o.allowReadOnly) { input.disabled = true; slider.disabled = true; el.classList.add("disabled"); el.dataset.help = "Read-only: " + (f.why || ""); }
      }).catch(() => { /* field not in the glossary: plain box */ });
    }
    Object.defineProperty(el, "value", { get: () => value, set: v => { value = v === null || v === undefined ? null : Number(v); sync(); check(); } });
    el.valid = () => check();
    el.setRange = setRange;
    el.setSlider = shown => { o.slider = !!shown; setRange(min, max); };
    el.reset = () => { value = orig; sync(); check(); };
    return el;
  };

  // ------------------------------------------------------------ online banner
  // GW.onlineBanner(cid, {onState(state)}) -> element, refreshed every 15 s while on the page; el.refresh()
  GW.onlineBanner = function (cid, opts) {
    const o = opts || {};
    const el = h("div", { class: "gw-online loading", role: "status" }, "checking whether the character is online ...");
    async function refresh() {
      let st;
      try { st = await GW.api("/api/m/online/" + cid); }
      catch (e) { GW.clear(el).append(h("span", { class: "tt-bad" }, "online check failed: " + e.message)); el.className = "gw-online warn"; return null; }
      GW.clear(el);
      el.className = "gw-online " + (st.online ? "on" : "off");
      if (!st.online) {
        el.append(h("span", { class: "gw-dot off" }), h("b", null, st.name + " is offline"), " - safe to edit. Changes apply when the character next enters the world.");
      } else {
        const LABEL = { Money: "Silver", Stone: "Gold", BindingGold: "B-Gold", fighter_job_lv: "level", fighter_job_exp: "EXP",
          SkillPoint: "talent points", zodiac_energy: "zodiac energy", Map: "map", Pos_X: "position", Pos_Z: "position" };
        const ORDER = ["Money", "Stone", "BindingGold", "fighter_job_lv", "fighter_job_exp", "SkillPoint", "zodiac_energy", "Map", "Pos_X", "Pos_Z"];
        const lf = Array.from(new Set((st.live_fields || []).slice().sort((x, y) => ORDER.indexOf(x) - ORDER.indexOf(y)).map(c => LABEL[c] || c)));
        const bag = (st.live_bag || []).length ? ["bag items (add, remove, move)"] : [];
        el.append(h("span", { class: "gw-dot on" }), h("b", null, st.name + " is online"), " ",
          h("span", { class: "gw-online-why", "data-help": st.reasons.join("\n") + (st.stale_hint ? "\n\n" + st.stale_hint : "") }, "Why?"), " - ",
          lf.length || bag.length
            ? h("span", null, h("b", { class: "gw-good" }, "live edits on: "), "the game server applies ", lf.concat(bag).join(", "),
                " at once and the player sees it. Everything else needs the character logged out.")
            : h("span", null, "Live edits arrive with the round-2 server update; this character must be logged out to edit."));
        if (st.kick) {
          el.append(" ", h("button", { class: "gw-btnx small", type: "button", "data-help": "Disconnect the player (the game saves and logs them out), so every field can be edited. They can log straight back in afterwards.",
            onclick: async ev => {
              if (!await GW.confirm({ title: "Kick " + st.name + "?", message: "The player is disconnected (their game saves first). Then every field can be edited; they see the changes when they log back in.", ok: "Kick" })) return;
              ev.target.disabled = true;
              try { const r = await GW.api("/api/m/kick/" + cid, { reason: "an admin edit" }); GW.status(r.result, "ok"); }
              catch (e) { GW.status(e.message, "err"); }
              refresh();
            } }, "Kick to edit everything"));
        }
      }
      if (o.onState) o.onState(st);
      GW.emit("online", { cid, state: st });
      return st;
    }
    el.refresh = refresh;
    refresh();
    const t = setInterval(() => { if (!el.isConnected) { clearInterval(t); return; } refresh(); }, 15000);
    return el;
  };

  // ------------------------------------------------------------ write results
  // GW.writeDone(result, label): status line + an Undo link; emits "change"
  GW.writeDone = function (r, label) {
    const c = r && r.change;
    const live = r && r.mode === "live";
    GW.status((label || (c && c.action) || "saved") + " - " + (live ? "applied now by the running server: the player sees it in game"
      : ((r && r.applies_text) || (c && c.applies_text) || "saved")), "ok");
    GW.emit("change", c);
    return c;
  };
  // GW.writeError(e): explains 423 (online), 409 (changed since), 403 (read-only); returns the error kind
  GW.writeError = async function (e) {
    const d = (e && e.data) || {};
    if (e.status === 423) {
      const st = d.online || {};
      await GW.alert("|cFFFFFF00" + (st.name || "The character") + " is online.|cFFFFFFFF\n\n" + e.message +
        (st.kick ? "\n\nUse 'Kick to edit everything' on the online banner to log the player out first." : ""), "Character online");
      GW.status("not saved: the character is online", "err");
      return "online";
    }
    if (e.status === 409) {
      await GW.alert("The data changed since this page read it (the game server or another edit). Nothing was written.\n\n" +
        (d.conflict || []).slice(0, 20).map(x => (x.table || "") + " " + (x.row || "") + ": " + (x.what || "")).join("\n") + "\n\nThe page will reload the current values.", "Changed since you opened it");
      GW.status("not saved: changed since the page was read", "err");
      return "conflict";
    }
    if (e.status === 403) { await GW.alert("Read-only: " + e.message, "Not editable"); GW.status(e.message, "err"); return "refused"; }
    GW.status(e.message, "err");
    await GW.alert(e.message, "Not saved");
    return "error";
  };

  // ------------------------------------------------------------ the shared save path (live or database)
  // GW.saveFields(cid, {column: value}, {expected: {column: shownValue}, label}) -> result
  //   character_base columns. Online + round2 offers the field -> the running server applies it at once (mode "live");
  //   offline -> one guarded database write (mode "database", applies at the next login). Errors: GW.writeError(e).
  GW.saveFields = async function (cid, values, opts) {
    const o = opts || {};
    const r = await GW.api("/api/m/edit/" + cid + "/fields", { values, expected: o.expected || null, label: o.label || null, advanced: !!o.advanced });
    GW.writeDone(r, o.label);
    return r;
  };
  // GW.bagItem(cid, "add" | "remove" | "move", {item, quantity, quality, grade, bound} | {slot} | {from, to}) -> result
  GW.bagItem = async function (cid, op, p, label) {
    const r = await GW.api("/api/m/edit/" + cid + "/bag", Object.assign({ op }, p || {}));
    GW.writeDone(r, label);
    return r;
  };

  // ------------------------------------------------------------ confirm-and-save bar
  // GW.saveBar({onSave: async (changes) => result, onDiscard, title}) -> {el, set(key, {label, from, to, applies}), unset(key), clear(), size}
  GW.saveBar = function (opts) {
    const o = opts || {};
    const pending = new Map();
    const list = h("span", { class: "gw-sb-list" });
    const save = h("button", { class: "gw-btnx", type: "button" }, "Review & save");
    const discard = h("button", { class: "gw-btnx", type: "button" }, "Discard");
    const el = h("div", { class: "gw-savebar hidden", role: "region", "aria-label": "unsaved changes" }, h("b", { class: "gw-sb-count" }), list, save, discard);
    function draw() {
      el.classList.toggle("hidden", pending.size === 0);
      el.querySelector(".gw-sb-count").textContent = pending.size + " unsaved change" + (pending.size === 1 ? "" : "s") + ":";
      GW.clear(list);
      for (const c of pending.values()) list.append(h("span", { class: "gw-sb-item" }, c.label + " " + fmtv(c.from) + " → " + fmtv(c.to)));
    }
    const fmtv = v => v === null || v === undefined ? "(none)" : (typeof v === "number" ? v.toLocaleString("en-US") : String(v));
    save.addEventListener("click", async () => {
      const items = Array.from(pending.values());
      const table = h("table", { class: "gw-sb-review" }, h("tr", null, h("th", null, "What"), h("th", null, "Now"), h("th", null, "New"), h("th", null, "Takes effect")),
        items.map(c => h("tr", null, h("td", { "data-field": c.field || null }, c.label), h("td", null, fmtv(c.from)), h("td", { class: "gw-value" }, fmtv(c.to)),
          h("td", null, c.applies ? GW.appliesChip(c.applies) : ""))));
      const ok = await GW.confirm({ title: o.title || "Save these changes?", el: h("div", null, table,
        h("p", { class: "gw-dim" }, "One database transaction; a backup is taken first and the change log gets an Undo.")), ok: "Save", wide: true });
      if (!ok) return;
      save.disabled = true;
      try {
        const r = await o.onSave(items);
        pending.clear(); draw();
        if (r) GW.writeDone(r);
      } catch (e) { await GW.writeError(e); }
      finally { save.disabled = false; }
    });
    discard.addEventListener("click", () => { pending.clear(); draw(); if (o.onDiscard) o.onDiscard(); });
    draw();
    return { el, set(key, c) { pending.set(key, c); draw(); }, unset(key) { pending.delete(key); draw(); }, clear() { pending.clear(); draw(); },
      get size() { return pending.size; }, items: () => Array.from(pending.values()) };
  };
})();
