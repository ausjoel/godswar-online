/* static/modules/rates.js - the "Server rates" page (v2). Backend: modules/rates.py. Contract: MODULES.md.
   Every row of public.server_rates gets: its icon, the label with hover help (what it does, limits, when it applies),
   a log-scale slider (x0 .. x1,000,000 usable; markers for stock, the first cap and the server's running value),
   an exact number box ("2", "x2", "1,000,000", "1m", "2.5k", "1e6"), a preview in game terms and the cap notes.
   Presets fill the page; the save bar reviews and saves in one transaction; the running server reloads at once.
   Rates never touch a character's rows: nobody has to log out. Text from the DB / client is inserted as text. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const S = { st: null, draft: {}, stockDraft: {}, note: "", cat: "all", q: "", onlyChanged: false, bar: null, el: null, ctx: null, rows: {} };

  // ------------------------------------------------------------ numbers
  const num = v => (v === null || v === undefined || v === "") ? NaN : Number(v);
  const fmtN = v => {
    const n = Number(v);
    if (!isFinite(n)) return "-";
    return n.toLocaleString("en-US", { maximumFractionDigits: Math.abs(n) < 10 ? 4 : 2 });
  };
  const short = v => {
    const n = Number(v), a = Math.abs(n);
    if (a >= 1e12) return +(n / 1e12).toFixed(2) + "T";
    if (a >= 1e9) return +(n / 1e9).toFixed(2) + "B";
    if (a >= 1e6) return +(n / 1e6).toFixed(2) + "M";
    if (a >= 1e4) return +(n / 1e3).toFixed(1) + "k";
    return fmtN(n);
  };
  const isMult = r => r.kind === "multiplier";
  const isSwitch = r => r.unit === "0/1" || (r.kind !== "multiplier" && Number(r.min_value) === 0 && Number(r.max_value) === 1);
  const valText = (r, v) => isMult(r) ? "x" + fmtN(v) : isSwitch(r) ? (Number(v) ? "on" : "off") : fmtN(v) + (r.unit ? " " + r.unit : "");
  // "2", "x2", "2x", "1,000,000", "1 000 000", "1m", "2.5k", "1e6", "3b"
  function parseVal(s) {
    let t = String(s).trim().toLowerCase().replace(/[,_\s]/g, "").replace(/^[x×]/, "").replace(/[x×]$/, "");
    if (t === "") return NaN;
    const m = t.match(/^(-?\d+(?:\.\d+)?(?:e[+-]?\d+)?)([kmbt]?)$/);
    if (!m) return NaN;
    const mul = { "": 1, k: 1e3, m: 1e6, b: 1e9, t: 1e12 }[m[2]];
    return Number(m[1]) * mul;
  }
  function validate(r, v) {
    if (!isFinite(v)) return "not a number";
    if (v < Number(r.min_value)) return "at least " + valText(r, r.min_value) + " on this server";
    if (v > Number(r.max_value)) return "at most " + valText(r, r.max_value) + " on this server";
    if (!isMult(r) && !Number.isInteger(v)) return "whole numbers only";
    if (Math.abs(Math.round(v * 10000) - v * 10000) > 1e-6) return "at most 4 decimals";
    return "";
  }

  // ------------------------------------------------------------ slider notches (log scale for wide ranges)
  function notches(r) {
    const lo = Number(r.min_value), hi = Number(r.max_value), step = Number(r.step) || (isMult(r) ? 0.05 : 1);
    const extra = [lo, hi, Number(r.default_value), Number(r.value)];
    if (r.info && r.info.first_cap) extra.push(Number(r.info.first_cap));
    let list = [];
    if (!isMult(r) && (hi - lo) / step <= 200) {
      for (let x = lo; x <= hi + 1e-9; x += step) list.push(Math.round(x * 10000) / 10000);
    } else {
      const MANT = [1, 1.25, 1.5, 2, 2.5, 3, 4, 5, 6, 7.5];
      const start = Math.max(lo > 0 ? lo : step, isMult(r) ? 0.05 : step);
      for (let d = Math.pow(10, Math.floor(Math.log10(start))); d <= hi * 1.0001; d *= 10)
        for (const m of MANT) {
          let x = m * d;
          if (!isMult(r)) x = Math.round(x / step) * step;
          x = Math.round(x * 10000) / 10000;
          if (x >= lo && x <= hi && (!isMult(r) || x >= Math.min(0.05, hi))) list.push(x);
        }
    }
    list = list.concat(extra.filter(x => isFinite(x) && x >= lo && x <= hi));
    if (isMult(r) && lo === 0) list.push(0);
    if (r.info && r.info.first_cap && !isMult(r)) list = list.map(x => x);   // keep the cap marker exact
    return [...new Set(list.map(x => Math.round(x * 10000) / 10000))].sort((a, b) => a - b);
  }
  const nearest = (list, v) => { let bi = 0; for (let i = 1; i < list.length; i++) if (Math.abs(list[i] - v) < Math.abs(list[bi] - v)) bi = i; return bi; };
  const decadeLabels = (r, list) => {
    if (list.length < 2) return [];
    const out = [];
    const wide = isMult(r) || (Number(r.max_value) - Number(r.min_value)) / (Number(r.step) || 1) > 200;
    if (wide) {
      for (let i = 0; i < list.length; i++) {
        const x = list[i];
        if (x === 0 || (x > 0 && Math.abs(Math.log10(x) - Math.round(Math.log10(x))) < 1e-9)) out.push([i, (isMult(r) ? "x" : "") + short(x)]);
      }
    } else {
      const n = list.length - 1, k = Math.max(1, Math.round(n / 6));
      for (let i = 0; i <= n; i += k) out.push([i, short(list[i])]);
      if (out[out.length - 1][0] !== n) out.push([n, short(list[n])]);
    }
    return out;
  };

  // ------------------------------------------------------------ previews in game terms (stock -> new, caps applied)
  const T = (b, v) => Math.trunc(b * v + 1e-9);
  function previews(r, v) {
    const C = S.st.constants || {}, K = C.kill_cap || 1e8, I32 = C.int32 || 2147483647;
    // cm(limit, x): x, or the limit marked as capped (a Number object with .capped; the table shows "(cap)")
    const cm = (lim, x) => x > lim ? Object.assign(new Number(lim), { capped: true }) : x;
    const capK = x => cm(K, x), pct = (p, m) => Math.min(100, Math.trunc(p * m + 1e-9)) + "%";
    const kills = (need, per) => per > 0 ? Math.ceil(need / per) : "never";
    const loot = S.st.loot || {};
    const P = {
      "exp.monster": () => [["Tier-1 monster", 80, capK(T(80, v)), "EXP"], ["Tier-100 monster", 548, capK(T(548, v)), "EXP"], ["Tier-200 monster", 1116, capK(T(1116, v)), "EXP"],
        ["Same-level kills, level 1 → 200 (about)", kills(C.exp_to_200, 967), kills(C.exp_to_200, capK(967 * v)), "kills"]],
      "tp.monster": () => [["Talent EXP per kill", 10, T(10, v)], ["Kills per Talent Point", 10, kills(100, T(10, v))], ["Talent Points per 1,000 kills", 100, Math.floor(T(10, v) * 1000 / 100)]],
      "exp.quest": () => [["Starter quest", 200, capK(T(200, v)), "EXP"], ["A 50,000 EXP quest", 50000, capK(T(50000, v)), "EXP"]],
      "tp.quest": () => [["A 2-Talent reward", 2, T(2, v)], ["A 20-Talent reward", 20, T(20, v)]],
      "exp.events": () => [["Lucky Gods claim (smallest)", 210000, capK(T(210000, v)), "EXP"], ["Lucky Gods claim (largest)", 13490000, capK(T(13490000, v)), "EXP"], ["Zeus' praying stone", 8000, capK(T(8000, v)), "EXP"]],
      "pet.exp_monster": () => [["Tier-1 kill", 80, capK(T(80, v)), "pet EXP"], ["Tier-200 kill", 1116, capK(T(1116, v)), "pet EXP"],
        ["Tier-100 kills for a pet to reach level 120", Math.ceil(252947820 / 548), kills(252947820, capK(T(548, v))), "kills"]],
      "zodiac.energy": () => [["Per 5-minute tick, first 3 h of the day", 20, T(20, v)], ["Per tick after that", 10, T(10, v)], ["A full day online", 3240, T(720, v) + T(2520, v)],
        ["Online days for zodiac level 1 → 30 (10,023,500)", 3094, kills(10023500, T(720, v) + T(2520, v)), "days"]],
      "instance.atlantis_hardpoints": () => [["Completion (850 points)", 2800, cm(K, T(2800, v)), "Honour"], ["Ended at score 0", 200, cm(K, T(200, v)), "Honour"]],
      "instance.medusa_honor": () => [["Normal, ended at score 0", 300, T(300, v), "Honour"], ["Mythic, cleared in 10 minutes", 3375, T(3375, v), "Honour"]],
      "instance.farm_points": () => [["Normal monster", 1, cm(1e6, T(1, v))], ["Elite", 10, cm(1e6, T(10, v))], ["Cerberus", 1000, cm(1e6, T(1000, v))]],
      "drop.chance": () => (loot.rules || []).length
        ? loot.rules.slice(0, 6).map(x => [x.item_name + " from " + (x.monster_name || x.monster), (x.chance_bp / 100) + "%", Math.min(100, Math.trunc(x.chance_bp * v + 1e-9) / 100) + "%", "", { sprite: x.sprite, item: x.item }])
        : [["A 25% drop", "25%", pct(25, v)], ["A 5% drop", "5%", pct(5, v)]],
      "drop.quantity": () => [["A quantity-1 drop", 1, cm(255, T(1, v))], ["A quantity-3 drop", 3, cm(255, T(3, v))]],
      "drop.max_items": () => {
        const per = {};
        for (const x of loot.rules || []) per[x.monster] = (per[x.monster] || 0) + 1;
        const rows = (loot.tables || []).slice(0, 5).map(t => [(t.monster_name || t.monster) + " (" + (per[t.monster] || 0) + " loot rules)", t.maximum_drops,
          cm(32, Math.min(per[t.monster] || 0, T(t.maximum_drops, v))), "items"]);
        return rows.length ? rows : [["A 1-item monster", 1, cm(32, T(1, v)), "items"]];
      },
      "money.quest_silver": () => [["A 1,000 Silver quest", 1000, cm(K, T(1000, v)), "Silver"], ["A 50,000 Silver quest", 50000, cm(K, T(50000, v)), "Silver"]],
      "money.quest_gold": () => [["A 10 Gold quest", 10, cm(K, T(10, v)), "Gold"]],
      "money.sell_price": () => [["Selling an item worth 8,656 Silver", 1731, Math.trunc(8656 * T(2000, v) / 10000), "Silver"]],
      "forge.success": () => [["A 40% forge step", "40%", pct(40, v)], ["A 10% forge step", "10%", pct(10, v)]],
      "forge.holystone_success": () => [["Holy stone level 1-3", "90%", pct(90, v)], ["Level 4-6", "25%", pct(25, v)], ["Level 7-9", "10%", pct(10, v)]],
      "holybox.daily_limit": () => [["EXP one account may store per day", 2000000000, v, "EXP"], ["Full Holy Box V (400,000,000)", 5, +(v / 4e8).toFixed(2), "boxes"],
        ["The client's counter shows", 214748364, cm(214748364, v)]],
      "holybox.per_store_max": () => [["Largest single Store", 400000000, v, "EXP"]],
      "holybox.capacity": () => [["Holy Box I", 100000, cm(I32, T(100000, v)), "EXP"], ["Holy Box III", 10000000, cm(I32, T(10000000, v)), "EXP"], ["Holy Box V", 400000000, cm(I32, T(400000000, v)), "EXP"]],
      "holybox.min_level": () => [["Level needed at the forger", 70, v]],
      "cost.talent": () => [["Talent rank 10", 10, T(10, v), "TP"], ["Talent rank 50", 587, T(587, v), "TP"], ["One talent, rank 0 → 60", 19810, T(19810, v), "TP"]],
      "cost.zodiac_level": () => [["Zodiac level 2", 720, T(720, v), "energy"], ["Zodiac level 1 → 30 in total", 10023500, cm(I32, T(10023500, v)), "energy"]],
      "cost.zodiac_grid": () => [["One grid 1 → 50: energy", 827921, cm(I32, T(827921, v))], ["One grid 1 → 50: Talent Points", 726024, cm(I32, T(726024, v))]],
      "exp.faction_crier": () => [["Smallest exchange", 23500, capK(T(23500, v)), "EXP"], ["Largest exchange", 250000, capK(T(250000, v)), "EXP"]],
      "tp.faction_crier": () => [["Smallest exchange", 5, cm(1e6, T(5, v)), "TP"], ["Largest exchange", 38, cm(1e6, T(38, v)), "TP"]],
      "exp.pill": () => [["One EXP pill", 1000000, capK(T(1000000, v)), "EXP"]],
      "pet.exp_items": () => [["A Morning Dew worth 50,000", 50000, cm(C.uint32 || 4294967295, T(50000, v)), "pet EXP"]],
      "cap.lucky_gods_daily": () => [["Wishes per character per day", 10, v]],
      "cap.wishing_pool_daily": () => [["Uses per character per day", 3, v]],
      "cap.zeus_gifts_weekday": () => [["Deliveries Monday-Friday", 10, v]],
      "cap.zeus_gifts_weekend": () => [["Deliveries Saturday and Sunday", 12, v]],
      "cap.zeus_exchanges_week": () => [["Exchanges a week (+1 per 5 stones)", 5, v]],
      "cap.quest_daily": () => [["A once-a-day quest", 1, T(1, v), "times"], ["A 3-a-day quest", 3, T(3, v), "times"]],
      "event.pindus_always_open": () => [["Pindus Mountains", "Wed/Fri 21:00, Sat/Sun 08:00 + 15:00 + 21:00", Number(v) ? "any time" : "Wed/Fri 21:00, Sat/Sun 08:00 + 15:00 + 21:00"]],
      "event.nimini_always_open": () => [["Ni Mini Valley", "Fri 19:00, Sat 07:00, Sun 19:00", Number(v) ? "any time" : "Fri 19:00, Sat 07:00, Sun 19:00"]],
      "event.battlefield_minutes": () => [["Each entry window", "45 min", v + " min"]],
      "event.battlefield_shift_hours": () => {
        const hh = x => String(((x % 24) + 24) % 24).padStart(2, "0") + ":00";
        return [["Wednesday Pindus window (realm time, UTC+8)", "21:00", hh(21 + Number(v))], ["Same window in Brisbane (AEST, UTC+10)", "23:00", hh(23 + Number(v))]];
      },
      "event.wonderland_close_hour": () => [["Wonderland entry", "until 23:00", Number(v) >= 24 ? "all day" : "until " + String(v).padStart(2, "0") + ":00"]],
      "event.zeus_stone_any_day": () => [["Praying stones accepted", "Monday-Friday", Number(v) ? "every day" : "Monday-Friday"]],
    };
    const f = P[r.rate_key];
    if (f) return f();
    return isMult(r) ? [["A stock amount of 100", 100, T(100, v)]] : [["Value", r.default_value, v, r.unit]];
  }

  // a value past a hard limit of the server or of the admin's client (Polish/POINTS-AUDIT.md): one plain sentence, or ""
  function capWarning(r, v) {
    const info = r.info || {}, k = r.rate_key;
    if (info.unused) return "The running game server does not read this setting: the row was left by a newer server build (round-2). Changing it does nothing until that build runs again.";
    if (k === "drop.max_items") {
      const md = Number((S.st.loot || {}).max_drops) || 1, n = Math.min(32, Math.trunc(md * v + 1e-9));
      const per = {};
      for (const x of (S.st.loot || {}).rules || []) per[x.monster] = (per[x.monster] || 0) + 1;
      const most = Math.max(0, ...Object.values(per));
      if (n > 8) return "Client limit: this allows up to " + n + " items on one corpse (a monster with " + md + " maximum drops), but the game's loot window has only 8 slots; what the client does with more was never tested. Keep this at x" + fmtN(Math.floor(8 / md)) + " or less. " +
        (most <= 8 ? "Today no monster has more than " + most + " loot rule" + (most === 1 ? "" : "s") + ", so nothing goes past 8 until monster_loot_rules is filled." : "");
      return "";
    }
    if (k === "holybox.daily_limit" && v > 214748364) return "Client display: the Holy Box counter in the game stops at 214,748,364; the server still enforces " + fmtN(v) + ".";
    if (k === "money.sell_price" && v > 5) return "Above x5 an expensive full stack overflows a 32-bit total and the sale fails.";
    if (info.first_cap && v > Number(info.first_cap) * 1.0001) return "Past the cap from about " + valText(r, info.first_cap) + ": " + info.first_cap_why + ". Higher values change little or nothing.";
    return "";
  }

  // ------------------------------------------------------------ draft helpers
  const rowOf = k => (S.st.rows || []).find(r => r.rate_key === k);
  const stockOf = id => (S.st.stock || []).find(s => s.id === id);
  const curVal = r => (r.rate_key in S.draft) ? Number(S.draft[r.rate_key]) : Number(r.value);
  const writable = () => S.st && S.st.writable && S.st.table;
  function setDraft(r, v) {
    const k = r.rate_key;
    if (Number(v) === Number(r.value)) { delete S.draft[k]; S.bar.unset("r:" + k); }
    else {
      S.draft[k] = v;
      S.bar.set("r:" + k, { label: r.label, from: valText(r, r.value), to: valText(r, v), applies: "hot_reload", field: "server_rates.value" });
    }
    const ui = S.rows[k];
    if (ui) ui.sync();
    drawTabs();
  }
  function setStockDraft(s, v) {
    const same = String(v === null || v === undefined ? "" : v) === String(s.value === null || s.value === undefined ? "" : s.value);
    if (same) { delete S.stockDraft[s.id]; S.bar.unset("s:" + s.id); }
    else { S.stockDraft[s.id] = v; S.bar.set("s:" + s.id, { label: s.label, from: s.value === null ? "(empty)" : s.value, to: v === null ? "(empty)" : v, applies: s.applies === "restart" ? "after_restart" : "per_use", field: s.field }); }
  }

  // ------------------------------------------------------------ one rate row
  function rateRow(r) {
    const editable = writable();
    const list = notches(r);
    const srvRates = (S.st.server && S.st.server.rates) || {};
    const srv = srvRates[r.rate_key];
    const info = r.info || {};
    const helpText = "|cFFFFFF00" + r.label + "|cFFFFFFFF\n" + r.description + "\n\n|cFF6BCCFEKey:|cFFFFFFFF " + r.rate_key + (r.unit && r.unit !== "x" ? " (" + r.unit + ")" : "") +
      "\n|cFF6BCCFEStock:|cFFFFFFFF " + valText(r, r.default_value) + "   |cFF6BCCFERange on this server:|cFFFFFFFF " + valText(r, r.min_value) + " – " + valText(r, r.max_value) +
      "\n|cFF6BCCFEApplies:|cFFFFFFFF " + info.applies + " (the running server reloads within seconds; nobody has to log out)" +
      ((info.caps || []).length ? "\n\n|cFFFF9F3FLimits:|cFFFFFFFF\n• " + info.caps.join("\n• ") : "") + "\n\n|cFF8AA3B0Stored in public.server_rates (row " + r.rate_key + "); the code reading it: " + (r.applies_to || "") + ".";

    const big = h("span", { class: "gw-rt-big" });
    const err = h("span", { class: "gw-rt-err tt-bad", role: "alert" });
    const warn = h("div", { class: "gw-rt-capwarn", role: "status", "data-help": "A hard limit in the server or in the admin's game client. Saving is still allowed." });
    const box = h("input", { class: "gw-rt-num", type: "text", inputmode: "decimal", "aria-label": r.label + " exact value", "data-field": "server_rates.value",
      "data-help": "Type the exact value: 2, x2, 1,000,000, 1m, 2.5k or 1e6. Allowed here: " + valText(r, r.min_value) + " – " + valText(r, r.max_value) + ".", disabled: editable ? null : true });
    const slider = h("input", { class: "gw-rt-slider", type: "range", min: 0, max: Math.max(1, list.length - 1), step: 1, "aria-label": r.label + " slider (log scale)", disabled: editable ? null : true,
      "data-help": "Drag to set " + r.label + (list.length > 40 ? " on a log scale (each mark is 10 times the last)" : "") + ". Yellow ▼ = stock, red line = where a hard cap starts, green dot = what the running server uses. Arrow keys step through the marks; type an exact value in the box.",
      "aria-valuetext": valText(r, curVal(r)) });
    const ticks = h("div", { class: "gw-rt-ticks", "aria-hidden": "true" });
    const pos = i => (list.length > 1 ? i / (list.length - 1) * 100 : 0) + "%";
    for (const [i, lab] of decadeLabels(r, list)) ticks.append(h("span", { class: "gw-rt-tick", style: "left:" + pos(i) }, lab));
    const marks = h("div", { class: "gw-rt-marks" });
    marks.append(h("span", { class: "gw-rt-mark stock", style: "left:" + pos(nearest(list, Number(r.default_value))), "data-help": "Stock value: " + valText(r, r.default_value) }));
    if (info.first_cap && info.first_cap >= Number(r.min_value) && info.first_cap <= Number(r.max_value))
      marks.append(h("span", { class: "gw-rt-mark cap", style: "left:" + pos(nearest(list, Number(info.first_cap))), "data-help": "From about " + valText(r, info.first_cap) + " on: " + info.first_cap_why + ". Higher values change less and less." }));
    if (srv !== undefined) marks.append(h("span", { class: "gw-rt-mark srv", style: "left:" + pos(nearest(list, Number(srv))), "data-help": "The running game server uses " + valText(r, srv) + " right now." }));
    const meta = h("div", { class: "gw-rt-meta" });
    const pv = h("table", { class: "gw-rt-preview" });
    const toggle = isSwitch(r) ? h("button", { class: "gw-rt-switch", type: "button", role: "switch", "aria-label": r.label, disabled: editable ? null : true,
      "data-help": "Switch " + r.label + " on or off (stock: " + (Number(r.default_value) ? "on" : "off") + ").",
      onclick: () => setDraft(r, curVal(r) ? 0 : 1) }, h("span", { class: "knob" })) : null;
    const btn = (label, help, fn, cls) => h("button", { class: "gw-btnx small" + (cls ? " " + cls : ""), type: "button", "data-help": help, disabled: editable ? null : true, onclick: fn }, label);
    const stockBtn = btn("Stock", "Back to the stock value " + valText(r, r.default_value) + ".", () => setDraft(r, Number(r.default_value)));
    const maxBtn = btn("Max", "The highest value this server allows: " + valText(r, r.max_value) + ".", () => setDraft(r, Number(r.max_value)));
    const why = h("button", { class: "gw-rt-why", type: "button", "data-help": "Why can't it go higher than " + valText(r, r.max_value) + "?",
      onclick: async () => {
        try { await S.ctx.api("bounds", { key: r.rate_key }); }
        catch (e) { GW.alert((e.status === 403 ? "" : "") + e.message + "\n\nThis setting: " + valText(r, r.min_value) + " – " + valText(r, r.max_value) + ((info.caps || []).length ? "\n\nLimits in the game:\n• " + info.caps.join("\n• ") : ""), "Why " + valText(r, r.max_value) + " is the limit"); }
      } }, "why this limit?");
    const capsEl = (info.caps || []).length ? h("details", { class: "gw-rt-caps" }, h("summary", { "data-help": "Hard limits in the server and the client that cap this setting." }, "Limits (" + info.caps.length + ")"),
      h("ul", null, info.caps.map(c => h("li", null, c)))) : null;
    const cell = GW.cell({ sprite: info.sprite, name: r.label, help: helpText });

    const el = h("div", { class: "gw-rt-row", id: "gw-rt-" + r.rate_key.replace(/\./g, "-") },
      cell,
      h("div", { class: "gw-rt-main" },
        h("div", { class: "gw-rt-head" },
          h("span", { class: "gw-rt-label gw-label", "data-help": helpText, tabindex: "0" }, r.label),
          big,
          GW.appliesChip("hot_reload", info.applies),
          h("span", { class: "gw-rt-key gw-dim" }, r.rate_key)),
        toggle ? h("div", { class: "gw-rt-ctl" }, toggle, h("span", { class: "gw-rt-switchtext" }), stockBtn)
          : h("div", { class: "gw-rt-ctl" },
            h("div", { class: "gw-rt-track" }, slider, marks, ticks),
            h("span", { class: "gw-rt-numwrap" }, isMult(r) ? h("span", { class: "gw-rt-x" }, "x") : null, h("span", { class: "gw-nf-box" }, h("span", { class: "gw-sprite gw-edit-left" }), box, h("span", { class: "gw-sprite gw-edit-right" })),
              !isMult(r) && r.unit ? h("span", { class: "gw-rt-unit" }, r.unit) : null),
            stockBtn, maxBtn),
        err, warn, meta, pv, h("div", { class: "gw-rt-foot" }, capsEl, why)));

    function sync(fromSlider) {
      const v = curVal(r);
      const changed = r.rate_key in S.draft;
      el.classList.toggle("changed", changed);
      el.classList.toggle("nondefault", Number(v) !== Number(r.default_value));
      big.textContent = valText(r, v);
      big.className = "gw-rt-big" + (changed ? " changed" : Number(v) !== Number(r.default_value) ? " nondefault" : "");
      if (document.activeElement !== box) box.value = fmtN(v);
      if (!fromSlider) slider.value = nearest(list, v);
      slider.setAttribute("aria-valuetext", valText(r, v));
      if (toggle) { toggle.classList.toggle("on", !!Number(v)); toggle.setAttribute("aria-checked", Number(v) ? "true" : "false"); el.querySelector(".gw-rt-switchtext").textContent = Number(v) ? "On" : "Off"; }
      stockBtn.disabled = !editable || Number(v) === Number(r.default_value);
      if (maxBtn) maxBtn.disabled = !editable || Number(v) === Number(r.max_value);
      GW.clear(meta).append(...[
        h("span", { "data-help": "The stock reborn value." }, "stock " + valText(r, r.default_value)),
        h("span", { "data-help": "The range comes from the game server binary (this server). The round-2 update raises it." }, "range " + valText(r, r.min_value) + " – " + valText(r, r.max_value)),
        srv !== undefined ? h("span", { class: Number(srv) === Number(r.value) ? "gw-good" : "tt-bad", "data-help": "GET /rates on the game server's management port: the value the running server uses." },
          "server uses " + valText(r, srv) + (Number(srv) === Number(r.value) ? " ✓" : " (database: " + valText(r, r.value) + ")"))
          : h("span", { class: "gw-dim", "data-help": (S.st.server || {}).note || "" }, "server value unknown"),
        changed ? h("span", { class: "gw-rt-was" }, "saved " + valText(r, r.value)) : null].filter(Boolean));
      const cw = capWarning(r, v);
      warn.textContent = cw; warn.hidden = !cw;
      GW.clear(pv).append(h("tr", null, h("th", null, "In game"), h("th", null, "Stock"), h("th", null, changed ? "After save" : "Now")));
      for (const p of previews(r, v)) {
        let [what, stock, now, unit, ex] = p;
        let capped = typeof now === "string" && now === "100%" && stock !== "100%";
        if (now instanceof Number) { capped = !!now.capped; now = now.valueOf(); }
        pv.append(h("tr", null,
          h("td", null, ex && ex.sprite !== undefined && ex.sprite !== null ? h("span", { class: "gw-rt-pvicon", "data-tip-type": "item", "data-tip-id": String(ex.item) }, GW.icon(ex.sprite)) : null, what),
          h("td", { class: "gw-dim" }, typeof stock === "number" ? fmtN(stock) : stock, unit && typeof stock === "number" ? " " + unit : ""),
          h("td", { class: "gw-value" + (capped ? " capped" : ""), "data-help": capped ? "Capped: a hard limit stops this value here." : null },
            typeof now === "number" ? fmtN(now) : now, unit && typeof now === "number" ? " " + unit : "", capped ? " (cap)" : "")));
      }
    }
    box.addEventListener("input", () => {
      const v = parseVal(box.value);
      const msg = validate(r, v);
      err.textContent = box.value.trim() === "" ? "" : msg;
      box.classList.toggle("bad", !!msg);
      if (!msg) { if (!(r.rate_key in S.draft) || Number(S.draft[r.rate_key]) !== v) setDraftQuiet(v); slider.value = nearest(list, v); }
    });
    box.addEventListener("blur", () => { err.textContent = ""; box.classList.remove("bad"); box.value = fmtN(curVal(r)); });
    box.addEventListener("keydown", e => {
      if (e.key === "Enter") box.blur();
      if (e.key === "ArrowUp" || e.key === "ArrowDown") {
        e.preventDefault();
        const i = nearest(list, curVal(r)) + (e.key === "ArrowUp" ? 1 : -1);
        setDraft(r, list[Math.max(0, Math.min(list.length - 1, i))]);
      }
    });
    function setDraftQuiet(v) {          // typing: update the draft and the preview without rewriting the box
      const k = r.rate_key;
      if (Number(v) === Number(r.value)) { delete S.draft[k]; S.bar.unset("r:" + k); }
      else { S.draft[k] = v; S.bar.set("r:" + k, { label: r.label, from: valText(r, r.value), to: valText(r, v), applies: "hot_reload", field: "server_rates.value" }); }
      sync(true); drawTabs();
    }
    slider.addEventListener("input", () => setDraft(r, list[Number(slider.value)]));
    S.rows[r.rate_key] = { el, sync, r };
    sync();
    return el;
  }

  // ------------------------------------------------------------ stock reborn settings
  const DUNGEON_IDS = /^(medusa|atlantis|wonderland|harbor)_/;
  function stockPanel(only) {
    const editable = S.st.writable;
    const panel = only
      ? h("section", { class: "gw-frame-normalwindow gw-win gw-rt-cat", id: "gw-rt-dungeons" }, h("div", { class: "gw-wtitle" }, "Dungeon entries per day: Medusa Island, Atlantis, Wonderland, Harbor Attack"),
        h("p", { class: "gw-dim" }, "Read at every entry, so a change works at once. Wonderland also closes at a set hour (Event times > Wonderland closes at; 24 = open all day)."))
      : h("section", { class: "gw-frame-normalwindow gw-win gw-rt-cat", id: "gw-rt-stock" }, h("div", { class: "gw-wtitle" }, "Stock reborn settings"),
        h("p", { class: "gw-dim" }, "The stock server's own settings. The global EXP multiplier and the level gap are read at start-up (they apply after a gw251 restart); the daily entries are read at every entry."));
    const grid = h("div", { class: "gw-rt-stockgrid" });
    for (const s of (S.st.stock || []).filter(x => !only || DUNGEON_IDS.test(x.id))) {
      if (!s.available) { grid.append(h("div", { class: "gw-dim" }, s.label + ": not in this database")); continue; }
      const cur = s.id in S.stockDraft ? S.stockDraft[s.id] : s.value;
      const extra = h("span", { class: "gw-rt-stockx gw-dim" });
      const nullable = !!s.nullable;
      const nf = GW.numberField({ field: s.field, label: s.label, value: cur, min: s.min, max: s.max, step: s.step || 1, disabled: !editable,
        onChange: (v, ok) => { if (!ok) return; if (v === null && !nullable) return; setStockDraft(s, v); paint(v); } });
      function paint(v) {
        if (s.id === "global_exp") extra.textContent = v ? "= x" + fmtN(v / 10000) + " on every monster kill (Fighter, Talent and pet EXP), on top of the rates above" : "";
        else if (nullable) extra.textContent = v === null || v === undefined ? "empty = unlimited paid retries" : Number(v) === 0 ? "no paid retries" : v + " paid retries (1 Opal each) after the free entries";
        else if (s.id === "level_gap") extra.textContent = "monsters more than " + v + " levels below the player pay no EXP";
        else extra.textContent = v + " a day per character";
      }
      paint(cur);
      grid.append(h("div", { class: "gw-rt-stockrow", "data-help": s.help || null }, nf, nullable ? h("button", { class: "gw-btnx small", type: "button", disabled: editable ? null : true,
        "data-help": "Empty the box: no limit on paid retries (the database's NULL).", onclick: () => { nf.value = null; setStockDraft(s, null); paint(null); } }, "Unlimited") : null, extra));
    }
    panel.append(grid);
    return panel;
  }

  // ------------------------------------------------------------ presets
  function presetBar() {
    const bar = h("div", { class: "gw-rt-presets" });
    for (const p of S.st.presets || []) {
      const res = p.resolved || {};
      const diff = Object.keys(res).filter(k => { const r = rowOf(k); return r && Number(res[k].value) !== curVal(r); });
      bar.append(h("button", { class: "gw-rt-preset", type: "button", disabled: writable() ? null : true,
        "data-help": "|cFFFFFF00" + p.label + "|cFFFFFFFF\n" + p.help + "\n\nKeys the preset does not name go back to stock. Nothing is saved until you press Review & save.",
        onclick: () => openPreset(p) }, GW.icon(p.sprite), h("span", { class: "nm" }, p.label), h("span", { class: "sub" }, diff.length ? diff.length + " changes" : "matches the page")));
    }
    return bar;
  }
  async function openPreset(p) {
    const res = p.resolved || {};
    const rows = (S.st.rows || []).filter(r => res[r.rate_key] && Number(res[r.rate_key].value) !== curVal(r));
    const clamped = (S.st.rows || []).filter(r => res[r.rate_key] && res[r.rate_key].clamped);
    const table = h("table", { class: "gw-sb-review gw-rt-ptable" }, h("tr", null, h("th", null, "Setting"), h("th", null, "On the page"), h("th", null, p.label), h("th", null, "")),
      rows.map(r => h("tr", null, h("td", { "data-help": r.description }, r.label), h("td", null, valText(r, curVal(r))), h("td", { class: "gw-value" }, valText(r, res[r.rate_key].value)),
        h("td", { class: "tt-bad" }, res[r.rate_key].clamped ? "wanted " + valText(r, res[r.rate_key].wanted) + "; this server allows " + valText(r, r.min_value) + " – " + valText(r, r.max_value) : (res[r.rate_key].note || "")))));
    const ok = await GW.confirm({ title: "Preset: " + p.label, wide: true, ok: rows.length ? "Put these on the page" : "OK", cancel: rows.length ? "Cancel" : false,
      el: h("div", null, h("p", null, p.help), rows.length ? table : h("p", { class: "gw-good" }, "The page already has these values."),
        clamped.length ? h("p", { class: "gw-dim" }, clamped.length + " value(s) were limited to what this server allows. The round-2 server update raises the multipliers up to x1,000,000.") : null,
        h("p", { class: "gw-dim" }, "Nothing is saved yet: check the sliders, then press Review & save.")) });
    if (!ok || !rows.length) return;
    for (const r of rows) setDraft(r, Number(res[r.rate_key].value));
    GW.status(p.label + ": " + rows.length + " values put on the page (not saved yet)", "ok");
  }

  // ------------------------------------------------------------ history (this page's saves + the database log)
  function historyPanel() {
    const panel = h("section", { class: "gw-frame-normalwindow gw-win gw-rt-hist" }, h("div", { class: "gw-wtitle" }, "History"));
    const mine = h("div", { class: "gw-rt-histlist" });
    for (const c of S.st.module_changes || []) {
      const undoable = !c.undone && !c.undo_of;
      mine.append(h("div", { class: "gw-rt-hrow" + (c.undone ? " undone" : "") + (c.undo_of ? " undo" : "") },
        h("span", { class: "gw-dim" }, (c.time || "").replace("T", " ")),
        h("span", { class: "gw-rt-hact" }, c.action || "", c.note && c.note !== "gwadmin-web" ? h("i", { class: "gw-dim" }, " — " + c.note) : null),
        c.applies ? GW.appliesChip(c.applies, c.applies_text) : null,
        c.undone ? h("b", { class: "tt-bad" }, "undone") : null,
        undoable ? h("button", { class: "gw-btnx small", type: "button", disabled: S.st.writable ? null : true, "data-help": "Put back the values this save changed (refused if they changed since; then you can overwrite).",
          onclick: () => undo(c) }, "Undo") : null));
    }
    if (!(S.st.module_changes || []).length) mine.append(h("div", { class: "gw-dim" }, "No saves from this editor yet."));
    const keySel = h("select", { class: "gw-rt-sel", "aria-label": "Show the history of" }, h("option", { value: "" }, "every setting"),
      (S.st.rows || []).map(r => h("option", { value: r.rate_key }, r.label)));
    const log = h("table", { class: "gw-rt-log" });
    const drawLog = rows => {
      GW.clear(log).append(h("tr", null, h("th", null, "When (UTC)"), h("th", null, "Setting"), h("th", null, "Old"), h("th", null, "New"), h("th", null, "By"), h("th", null, "Note"), h("th", { "data-help": "server_rates_state.revision after the change" }, "Rev.")));
      for (const l of rows) {
        const r = rowOf(l.rate_key) || { label: l.rate_key, kind: "multiplier" };
        log.append(h("tr", null, h("td", { class: "gw-dim" }, String(l.changed_at || "").replace("T", " ").slice(0, 19)), h("td", { "data-help": l.rate_key }, r.label),
          h("td", null, l.old_value === null ? "-" : valText(r, l.old_value)), h("td", { class: "gw-value" }, valText(r, l.new_value)),
          h("td", { class: "gw-dim" }, l.changed_by), h("td", { class: "gw-dim" }, l.note), h("td", { class: "gw-dim" }, l.revision)));
      }
      if (!rows.length) log.append(h("tr", null, h("td", { colspan: 7, class: "gw-dim" }, "No changes recorded.")));
    };
    drawLog(S.st.log || []);
    keySel.addEventListener("change", async () => {
      try { const r = await S.ctx.api("history?limit=300" + (keySel.value ? "&key=" + encodeURIComponent(keySel.value) : "")); drawLog(r.rows); }
      catch (e) { GW.status(e.message, "err"); }
    });
    panel.append(h("h3", { class: "gw-rt-h3" }, "Saves from this editor"), mine,
      h("h3", { class: "gw-rt-h3", "data-field": "server_rates_log.new_value" }, "Database history (server_rates_log: every change, also by SQL) ", keySel), h("div", { class: "gw-rt-logwrap" }, log));
    return panel;
  }
  async function undo(c) {
    if (!await GW.confirm({ title: "Undo this save?", message: c.action + "\n\nThe values go back to what they were before it. The running server reloads at once." , ok: "Undo" })) return;
    let r;
    try {
      try { r = await S.ctx.api("undo", { change: c.id }); }
      catch (e) {
        if (e.status !== 409) throw e;
        const lines = ((e.data || {}).conflict || []).map(x => x.row + ": " + x.what).join("\n");
        if (!await GW.confirm({ title: "Changed since that save", message: "These values changed after that save:\n\n" + lines + "\n\nPut the old values back anyway?", ok: "Overwrite", danger: true })) return;
        r = await S.ctx.api("undo", { change: c.id, overwrite: true });
      }
      GW.status("undo done - " + (r.output || ""), "ok");
      S.lastChange = r.change && r.change.id;
      GW.emit("change", r.change);
      S.draft = {}; S.stockDraft = {}; S.bar.clear();
      S.st = r.state; render();
    } catch (e) { await GW.writeError(e); }
  }

  // ------------------------------------------------------------ the page
  function header() {
    const st = S.st, srv = st.server || {}, b = st.build || {}, w = st.world || {};
    const same = srv.reachable && srv.revision === st.revision;
    const head = h("section", { class: "gw-frame-normalwindow gw-win gw-rt-headwin" }, h("div", { class: "gw-wtitle" }, "Server rates"));
    head.append(h("div", { class: "gw-rt-status" },
      h("span", { class: "gw-badge", "data-help": "PostgreSQL database this page edits. 'godswar' is the live game; any other name is a copy." }, "DB " + st.database + " · revision " + (st.revision ?? "-")),
      srv.reachable ? h("span", { class: "gw-badge " + (same ? "ok" : "warn"), "data-help": "GET /rates on the game server's management port: the revision it runs. It re-reads the table every " + (srv.pollSeconds || 5) + " s and at once after a save." },
        "game server runs revision " + srv.revision + (same ? " ✓" : " (catching up)")) : h("span", { class: "gw-badge warn", "data-help": srv.note || "" }, "game server not reachable: it still picks changes up within 5 s"),
      h("span", { class: "gw-badge " + (b.round2 ? "ok" : "dim"), "data-help": b.note || "" }, b.round2 ? "round-2 server · " + b.keys + " settings" : "stock RATES server · " + b.keys + " settings"),
      st.writable ? h("span", { class: "gw-badge " + (st.live ? "bad" : "ok") }, st.live ? "LIVE · saving changes the real game" : "copy · writable") : h("span", { class: "gw-badge dim" }, "read-only")));
    head.append(h("div", { class: "gw-online off gw-rt-online", role: "status" },
      h("span", { class: "gw-dot " + (w.count ? "on" : "off") }),
      h("b", null, "Changes apply while the game runs. "),
      "Rates are server-wide: they never touch a character's saved rows, so nobody has to log out. ",
      w.count ? h("span", { "data-help": "Marked in the world (character_base.checkpoint_owner_id set; a crash can leave it set): " + (w.names || []).join(", ") }, w.count + " character" + (w.count === 1 ? " is" : "s are") + " in the world now; they get a saved rate at their next kill, hand-in or tick. ")
        : "Nobody is in the world right now. ",
      h("span", { class: "gw-dim", "data-help": b.live_edits ? "The game server offers live edits of logged-in characters: " + (b.live_operations || []).join(", ") + ". Rates do not need them." : "The game server does not offer live character edits (" + (b.capabilities_error || "stock server") + "). Rates do not need them: the server reloads the rates table by itself." },
        b.live_edits ? "Live character edits: on (round-2)." : "Live character edits: not on this server (not needed here).")));
    if (!b.round2 && st.table) head.append(h("div", { class: "gw-rt-note" }, b.note));
    for (const c of st.checks || []) head.append(h("div", { class: c.severity === "error" ? "tt-bad" : "gw-rt-note" }, c.msg));
    if (!st.writable) head.append(h("div", { class: "gw-rt-note" }, "Read-only: this editor was started without write access to " + st.database + ". You can look and plan, not save."));
    const note = h("input", { class: "gw-rt-input gw-rt-note-in", type: "text", maxlength: 200, placeholder: "note for the history (optional)", value: S.note, "aria-label": "Note for the history",
      oninput: e => { S.note = e.target.value; } });
    head.append(h("div", { class: "gw-rt-tools" },
      h("button", { class: "gw-btnx", type: "button", "data-help": "Ask the running server to re-read the rates table now (POST /rates/reload). It also does that by itself every 5 s.", onclick: reloadServer }, "Reload server now"),
      h("button", { class: "gw-btnx", type: "button", "data-help": "Read the table and the server's values again (keeps your unsaved edits).", onclick: () => load(true) }, "Refresh"),
      h("button", { class: "gw-btnx", type: "button", "data-help": "Open the change log of every page.", onclick: () => GW.changes.open() }, "Change log"),
      note));
    head.append(presetBar());
    return head;
  }
  let tabsEl = null, listEl = null, searchEl = null;
  function drawTabs() {
    if (!tabsEl) return;
    const st = S.st;
    GW.clear(tabsEl);
    const mk = (id, label, sprite, n, nd) => h("button", { class: "gw-rt-tab" + (S.cat === id ? " on" : ""), type: "button", "aria-pressed": S.cat === id ? "true" : "false",
      "data-help": label + ": " + n + " settings" + (nd ? ", " + nd + " not stock" : ", all stock"), onclick: () => { S.cat = id; drawTabs(); drawList(); } },
      sprite !== undefined && sprite !== null ? GW.icon(sprite) : null, h("span", null, label), nd ? h("span", { class: "gw-rt-badge" }, nd) : null);
    const all = st.rows || [];
    const pend = k => k in S.draft;
    tabsEl.append(mk("all", "All", 664, all.length, all.filter(r => curVal(r) !== Number(r.default_value)).length));
    for (const c of st.categories || []) {
      const rs = all.filter(r => r.category === c);
      const t = mk(c, c, ((st.category_item || {})[c] || {}).sprite, rs.length, rs.filter(r => curVal(r) !== Number(r.default_value)).length);
      if (rs.some(r => pend(r.rate_key))) t.classList.add("pending");
      tabsEl.append(t);
    }
    tabsEl.append(mk("stock", "Stock settings", st.stock_sprite, (st.stock || []).length, 0));
  }
  function drawList() {
    GW.clear(listEl);
    const st = S.st, q = S.q.toLowerCase();
    if (!st.table) listEl.append(h("div", { class: "gw-rt-note" }, "public.server_rates does not exist in this database: the server is stock reborn without the RATES patch. The stock settings below still work."));
    if (S.cat === "all" || S.cat !== "stock") {
      for (const c of st.categories || []) {
        if (S.cat !== "all" && S.cat !== c) continue;
        const rows = (st.rows || []).filter(r => r.category === c && (!q || (r.label + " " + r.rate_key + " " + r.description).toLowerCase().includes(q)) && (!S.onlyChanged || curVal(r) !== Number(r.default_value) || r.rate_key in S.draft));
        if (!rows.length) continue;
        listEl.append(h("section", { class: "gw-frame-normalwindow gw-win gw-rt-cat" }, h("div", { class: "gw-wtitle" }, c), rows.map(r => S.rows[r.rate_key] ? S.rows[r.rate_key].el : rateRow(r))));
      }
      if (S.cat === "Instances" && !q && !S.onlyChanged) listEl.append(stockPanel(true));
    }
    if ((S.cat === "all" || S.cat === "stock") && !q && !S.onlyChanged) listEl.append(stockPanel());
    if (!listEl.childNodes.length) listEl.append(h("div", { class: "gw-dim" }, "No setting matches."));
  }
  function render() {
    const el = S.el;
    GW.clear(el);
    S.rows = {};
    tabsEl = h("div", { class: "gw-rt-tabs", role: "toolbar", "aria-label": "Categories" });
    listEl = h("div", { class: "gw-rt-list" });
    searchEl = h("input", { class: "gw-rt-input gw-rt-search", type: "search", placeholder: "Search settings (EXP, honour, drop, Holy Box ...)", value: S.q, "aria-label": "Search settings",
      oninput: e => { S.q = e.target.value; drawList(); } });
    const only = h("label", { class: "gw-rt-only", "data-help": "Show only settings that are not at their stock value (or have an unsaved edit)." },
      h("input", { type: "checkbox", checked: S.onlyChanged ? true : null, onchange: e => { S.onlyChanged = e.target.checked; drawList(); } }), " only not-stock");
    el.append(header(), h("div", { class: "gw-rt-filter" }, searchEl, only), tabsEl, listEl, historyPanel(), S.bar.el);
    // re-apply drafts to the bar (render after a reload keeps unsaved edits)
    for (const k of Object.keys(S.draft)) { const r = rowOf(k); if (!r || Number(S.draft[k]) === Number(r.value)) { delete S.draft[k]; S.bar.unset("r:" + k); } }
    drawTabs(); drawList();
  }
  async function load(keepDraft) {
    let st;
    try { st = await S.ctx.api("state"); }
    catch (e) { GW.clear(S.el).append(h("div", { class: "tt-bad" }, e.message)); return; }
    S.st = st;
    if (!keepDraft) { S.draft = {}; S.stockDraft = {}; S.bar.clear(); }
    render();
  }
  async function reloadServer() {
    try {
      const r = await S.ctx.api("reload", {});
      const rl = r.reload || {};
      GW.status(rl.reachable ? "the server re-read the table: revision " + rl.revision : "reload failed: " + (rl.note || "not reachable"), rl.reachable ? "ok" : "err");
      S.st = r.state; render();
    } catch (e) { GW.status(e.message, "err"); }
  }
  async function saveAll() {
    const values = {}, expected = {}, stock = {}, stockExpected = {};
    for (const k of Object.keys(S.draft)) { const r = rowOf(k); if (!r) continue; values[k] = S.draft[k]; expected[k] = r.value; }
    for (const id of Object.keys(S.stockDraft)) { const s = stockOf(id); if (!s) continue; stock[id] = S.stockDraft[id]; stockExpected[id] = s.value; }
    let r;
    try { r = await S.ctx.api("save", { values, expected, stock, stock_expected: stockExpected, note: S.note }); }
    catch (e) {
      if (e.status === 409) { setTimeout(() => load(false), 50); }
      throw e;
    }
    S.draft = {}; S.stockDraft = {}; S.note = "";
    S.lastChange = r.change && r.change.id;
    S.st = r.state;
    render();
    const ok = Object.values(r.server_applied || {});
    const msg = r.mode === "hot" ? "saved; the running server reloaded revision " + r.server_revision + (ok.length ? (ok.every(Boolean) ? " and uses the new values ✓" : " but does NOT use every new value yet") : "")
      : "saved; " + (r.output || "the server picks it up within 5 s");
    setTimeout(() => GW.status(msg, ok.every(Boolean) ? "ok" : "err"), 30);
    return r;
  }

  GW.module("rates", {
    async mount(el, ctx) {
      S.el = el; S.ctx = ctx; S.draft = {}; S.stockDraft = {};
      S.bar = GW.saveBar({ title: "Save these server rates?", onSave: saveAll, onDiscard: () => { S.draft = {}; S.stockDraft = {}; render(); } });
      el.append(h("div", { class: "gw-dim" }, "loading the rates ..."));
      await load(false);
      this.offChange = GW.on("change", c => { if (c && c.engine === "rates" && c.id !== S.lastChange && S.el && S.el.isConnected && !Object.keys(S.draft).length) load(true); });
      const want = ctx.route && ctx.route[0];
      if (want) { const r = rowOf(want); if (r) { S.cat = r.category; drawTabs(); drawList(); const x = document.getElementById("gw-rt-" + want.replace(/\./g, "-")); if (x) x.scrollIntoView({ block: "center" }); } }
    },
    unmount() { if (this.offChange) this.offChange(); S.el = null; },
  });
})();
