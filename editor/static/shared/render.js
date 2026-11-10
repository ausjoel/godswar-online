/* shared/render.js - catalogue + glossary clients and the game-style tooltip renderers.
   GW.catalog.load(type) / get(type, id) / row(type, id) / name(type, id)
   GW.glossary.table(t) / field("t.c")
   GW.render.item(entry, inst) and friends -> a .gw-tooltip element (UI-STYLE.md s.4.2 order). */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;

  // ------------------------------------------------------------ catalogue client
  const loaded = {}, entries = {}, resolved = {};
  GW.catalog = {
    // every entry of one ID type: {type, title, fields, groups, rows: [{id, name, group, cat, sub, sprite, classes,
    // minlv, maxlv, color, flags, key}], byId: Map}
    load(type) {
      if (!loaded[type]) {
        loaded[type] = GW.api("/api/m/catalog/" + encodeURIComponent(type)).then(ix => {
          const f = ix.fields;
          ix.rows = ix.rows.map(r => { const o = {}; for (let i = 0; i < f.length; i++) o[f[i]] = r[i]; return o; });
          ix.byId = new Map(ix.rows.map(r => [String(r.id), r]));
          resolved[type] = ix;
          return ix;
        }).catch(e => { delete loaded[type]; throw e; });
      }
      return loaded[type];
    },
    // synchronous lookup once load(type) has finished (undefined before)
    loadedRow(type, id) { const ix = resolved[type]; return ix ? ix.byId.get(String(id)) : undefined; },
    async row(type, id) { const ix = await GW.catalog.load(type); return ix.byId.get(String(id)) || null; },
    async name(type, id) { const r = await GW.catalog.row(type, id); return r ? r.name : ""; },
    // the full catalogue entry (tooltips): items.json / skills.json / ... entry
    get(type, id) {
      const k = type + ":" + id;
      if (!entries[k]) entries[k] = GW.api("/api/m/catalog/" + encodeURIComponent(type) + "/" + encodeURIComponent(id)).catch(e => { delete entries[k]; throw e; });
      return entries[k];
    },
    labels() { return GW.cached("/api/m/catalog/labels"); },
    types() { return GW.cached("/api/m/catalog"); },
  };

  // ------------------------------------------------------------ glossary client (field help)
  GW.glossary = {
    table(t) { return GW.cached("/api/m/glossary/" + encodeURIComponent(t)); },
    async field(name) {
      const [t, c] = String(name).split(".");
      const tab = await GW.glossary.table(t);
      const f = tab.columns && tab.columns[c];
      if (!f) throw new Error("no glossary entry for " + name);
      return f;
    },
    meta() { return GW.cached("/api/m/glossary"); },
  };

  // ------------------------------------------------------------ helpers
  const QNAMES = ["None", "Common", "Enhanced", "Delicate", "Good", "Superior", "Classic", "Eternal", "Epic", "Legendary", "Mystic",
    "Divine", "Celestial", "Mythical", "Astral", "Arcane", "Ethereal", "Transcendent", "Ancient", "Primordial", "Boundless"];
  GW.QUALITY_NAMES = QNAMES;
  GW.qColor = q => `var(--gw-q${Math.max(0, Math.min(20, Number(q) || 0))})`;
  GW.gColor = g => `var(--gw-g${Math.max(1, Math.min(25, Number(g) || 1))})`;
  const NO_CLIENT_COLOR = { 14: 1, 15: 1, 17: 1, 19: 1, 20: 1 };       // font.lua lacks these quality colours (UI-STYLE s.3.2)
  const SUIT_COLORS = ["#ffffff", "var(--gw-bronze-color)", "var(--gw-silver-color)", "var(--gw-gold-color)", "var(--gw-platinum-color)",
    "var(--gw-mithril-color)", "var(--gw-orichalcum-color)", "var(--gw-adamantium-color)"];
  GW.suitColor = tier => SUIT_COLORS[tier] || "#fff";
  const fnum = v => (typeof v === "number" && !Number.isInteger(v)) ? v.toFixed(1) : GW.fmt(v);
  const line = (cls, ...kids) => h("div", { class: "tt-line " + (cls || "") }, ...kids);
  const rule = () => h("div", { class: "tt-rule" });
  const ed = (...kids) => h("div", { class: "tt-ed" }, ...kids);         // editor-only lines (after the game lines)
  function tip(...kids) { return h("div", { class: "gw-tooltip gw-tt" }, ...kids); }
  function add(el, ...kids) { for (const k of kids.flat()) if (k !== null && k !== undefined && k !== false) el.append(k); return el; }

  GW.render = {};

  // ------------------------------------------------------------ item (ItemView, UI-STYLE s.4.2)
  // entry = items.json entry (GW.catalog.get("item", id)); inst = a character_items row (optional):
  // {item_quality, item_grade, bound, stack, attrs: [{id, level}], holy_suit_code, holy_socket_count,
  //  sockets: [{effect, level, value}], item_exp}
  GW.render.item = function (e, inst, labels) {
    inst = inst || {};
    const L = labels || {};
    const stats = (L.stats) || {};
    const q = inst.item_quality !== undefined && inst.item_quality !== null ? Number(inst.item_quality) : null;
    const g = inst.item_grade !== undefined && inst.item_grade !== null ? Number(inst.item_grade) : null;
    const t = tip();
    const aptitude=e.pet_egg && (e.pet_egg.choices||[]).find(a=>a.id===q);
    const titleColor=aptitude ? "var(--gw-"+aptitude.color.toLowerCase().replaceAll("_","-")+")" : q !== null ? GW.qColor(q) : "#fff";
    t.append(h("div", { class: "tt-name", style: "color:" + titleColor }, (aptitude ? aptitude.name+" " : "")+(e.name || ("item " + e.id))));
    if(e.pet_egg)t.append(line("tt-desc",aptitude ? "Pet aptitude: "+aptitude.name+" (#"+q+")" : "Choose a native pet aptitude before hatching this egg."));
    if (e.slot_label || e.category) t.append(line("tt-two", h("span", null, e.slot_label || ""), h("span", null, e.category && e.category !== e.slot_label ? e.category : "")));
    if (e.desc) t.append(line("tt-desc", GW.text(e.desc)));
    if (e.generated_translation) t.append(ed("GENERATED English translation; original text retained in catalogue data."));
    if (e.dead_status) t.append(ed("Dead items: " + ({defunct: "Defunct", "no-system": "No system", remove: "Removed promotion"}[e.dead_status] || e.dead_status)));
    if (inst.bound !== undefined && inst.bound !== null) t.append(line(Number(inst.bound) ? "tt-bound" : "", Number(inst.bound) ? "Can't be traded" : "Can be traded"));
    const lv = e.playlv || e.minlv;
    if (lv) t.append(line("", "Lv. " + lv));
    if (e.classes && e.classes.length && e.classes.length < 4) t.append(line("", "Class Restriction: " + (e.class_labels || e.classes).join(", ")));
    if (e.sex !== null && e.sex !== undefined && e.sex !== "") t.append(line("", Number(e.sex) ? "Male only" : "Female only"));
    if (e.camp !== null && e.camp !== undefined && e.camp !== "") t.append(line("", Number(e.camp) ? "Athens only" : "Sparta only"));
    if (g && !e.pet_egg) t.append(line("", h("span", { style: "color:" + GW.gColor(g) }, "Grade " + "★".repeat(Math.min(g, 25)))));
    // base attributes for the quality (stats arrays go to Q20 in this client)
    if (e.stats) {
      for (const k of Object.keys(e.stats)) {
        const arr = e.stats[k] || [];
        const lab = (stats[k] && stats[k].label) || k;
        if (q !== null && q > 0) {
          const v = arr[Math.min(q, arr.length) - 1];
          t.append(line("", lab + " " + fnum(v) + (q > arr.length ? "  (Q" + q + ": the client has numbers only to Q" + arr.length + ")" : "")));
        } else if (arr.length) {
          const a = arr[0], b = arr[arr.length - 1];
          t.append(line("", lab + " " + fnum(a) + (a !== b ? " … " + fnum(b) + " (Q1–Q" + arr.length + ")" : "")));
        }
      }
    }
    // append attributes (green, with level)
    for (const a of (inst.attrs || [])) {
      const ae = a.entry || {};
      if(inst.item_grade!==undefined) {
        const rank=a.enhancement_rank;
        t.append(line("tt-green",(rank?"(Lv."+rank+") ":"")+(ae.label || ae.key || ("attribute "+a.id))+" "+GW.attributeFamilies.format(ae,inst.item_grade)+" at G"+inst.item_grade));
      } else {
        const lvl=Number(a.level)||1, val=ae.levels ? ae.levels[Math.min(lvl,ae.levels.length)-1] : null;
        t.append(line("tt-green","(Lv."+lvl+")"+(ae.label || ae.key || ("attribute "+a.id))+(val!==null && val!==undefined ? " +"+GW.fmt(val):"")));
      }
    }
    if (e.append_attributes && e.append_attributes.length && !(inst.attrs || []).length && inst.item_quality === undefined) {
      t.append(line("tt-green tt-small", "May roll " + e.append_attributes.length + " append attributes"));
    }
    if (e.use_skill_name) t.append(line("", "Use: " + e.use_skill_name));
    if (e.teaches_skill_name) t.append(line("", "Teaches: " + e.teaches_skill_name));
    if (e.pet_skill_name) t.append(line("", "Teaches pet skill: " + e.pet_skill_name));
    if (e.price) t.append(line("", "Silver " + GW.fmt(e.price)));
    if (inst.holy_suit_code) {
      const s = inst.suit || {};
      t.append(line("", h("span", { style: "color:" + GW.suitColor(Math.floor(inst.holy_suit_code / 100)) }, s.name || ("Holy suit " + inst.holy_suit_code))));
    }
    if (inst.holy_socket_count) {
      for (let i = 0; i < inst.holy_socket_count; i++) {
        const s = (inst.sockets || [])[i];
        t.append(line("tt-stone", s && s.effect ? "Holy Stone Lv." + (s.level || 1) + " " + ((s.entry && s.entry.name) || ("effect " + s.effect)) : "Not enchased"));
      }
    }
    // editor-only lines
    const flags = [];
    if (e.in_client === false) flags.push(h("span", { class: "tt-bad" }, "not in your client (the client shows an empty slot)"));
    if (e.in_server === false) flags.push(h("span", { class: "tt-bad" }, "not in the server's item templates (it cannot be created)"));
    add(t, rule(), ed("#" + e.id + " · " + (e.kind || "?") + (e.tag ? " · " + e.tag : "") + (e.stack > 1 ? " · stack " + e.stack : "")),
      q !== null && !e.pet_egg && NO_CLIENT_COLOR[q] ? ed("the game client has no colour for " + QNAMES[q] + " (Q" + q + ")") : null,
      q !== null && e.max_quality && q > e.max_quality ? ed(h("span", { class: "tt-bad" }, "Q" + q + " is above this client's Q" + e.max_quality)) : null,
      g !== null && e.max_grade && g > e.max_grade ? ed(h("span", { class: "tt-bad" }, "grade " + g + " is above this client's G" + e.max_grade)) : null,
      flags.length ? ed(...flags) : null);
    return t;
  };

  // ------------------------------------------------------------ skills, pets, statuses ...
  GW.render.skill = function (e) {
    const desc = String(e.desc || "").replace(/\{Power1\}/g, e.power1).replace(/\{Power2\}/g, e.power2).replace(/\{Power3\}/g, e.power3);
    return tip(h("div", { class: "tt-name" }, e.name || ("skill " + e.id)),
      line("tt-two", h("span", null, e.class !== null && e.class !== undefined ? ["Warrior", "Champion", "Priest", "Mage"][e.class] || "" : "Item / monster skill"), h("span", null, e.level ? "Lv " + e.level : "")),
      line("", [e.mp ? "MP " + e.mp : "", e.cooldown_s ? "Cooldown " + e.cooldown_s + " s" : "", e.cast_s ? "Cast " + e.cast_s + " s" : "", e.range ? "Range " + e.range : ""].filter(Boolean).join(" · ")),
      desc ? line("tt-desc", GW.text(desc)) : null,
      e.status_name ? line("tt-green", "Adds status: " + e.status_name + (e.status_chance ? " (" + e.status_chance + "%)" : "")) : null,
      rule(), ed("#" + e.id + (e.family_name ? " · " + e.family_name : "")));
  };
  GW.render.talent = function (e) {
    return tip(h("div", { class: "tt-name" }, e.name || ("talent " + e.id)),
      line("", ["Warrior", "Champion", "Priest", "Mage"][e.class] || ""),
      (e.effects || []).map(x => line("tt-green", (x.label || x.stat) + " " + (x.values || []).map(GW.fmt).join(" / ") + (x.percent ? "%" : ""))),
      e.requires_rank ? line("", "Needs rank " + e.requires_rank + " in talent #" + e.requires_talent) : null,
      rule(), ed("#" + e.id));
  };
  GW.render.petskill = function (e) {
    const vals = String(e.values || "").split(",").filter(Boolean);
    const info = String(e.info || "").replace(/%Values%/g, vals.length ? vals.join("/") : "?").replace(/%Restrict%/g, e.restrict ? " " + e.restrict.split(",").join("/") : "");
    return tip(h("div", { class: "tt-name" }, e.name || ("pet skill " + e.id)), info ? line("tt-desc", GW.text(info)) : null, rule(), ed("#" + e.id + " · type " + e.type));
  };
  GW.render.pet = function (e) {
    return tip(h("div", { class: "tt-name" }, e.name || ("species " + e.species)), rule(), ed("species " + e.species));
  };
  GW.render.status = function (e) {
    return tip(h("div", { class: "tt-name" }, e.name || ("status " + e.id)),
      e.note ? line("tt-desc", GW.text(e.note)) : null,
      line("", [e.effect_label, e.duration_s ? e.duration_s + " s" : "", e.value ? "value " + e.value : ""].filter(Boolean).join(" · ")),
      rule(), ed("#" + e.id));
  };
  GW.render.appendattr = function (e) {
    return tip(h("div", { class: "tt-name tt-green" }, e.label || e.key),
      line("", e.text ? GW.text(e.text) : ""),
      e.levels ? line("", "G1: " + GW.attributeFamilies.format(e,1) + " … G" + e.levels.length + ": " + GW.attributeFamilies.format(e,e.levels.length)) : null,
      rule(), ed("#" + e.id + " · " + e.key + " · stat type " + e.stat_type));
  };
  GW.render.holystone = function (e) {
    return tip(h("div", { class: "tt-name" }, e.name), line("tt-desc", GW.text(e.desc || e.effect_text || "")), rule(), ed("#" + e.id + " · " + (e.key || "")));
  };
  GW.render.quest = function (e) {
    return tip(h("div", { class: "tt-name" }, e.title || ("quest " + e.id)),
      line("", [e.min_level ? "Lv " + e.min_level + (e.max_level && e.max_level !== e.min_level ? "-" + e.max_level : "") : "", e.giver_name ? "from " + e.giver_name : ""].filter(Boolean).join(" · ")),
      e.details ? line("tt-desc", GW.text(String(e.details).slice(0, 400))) : null, rule(), ed("#" + e.id));
  };
  GW.render.generic = function (type, e) {
    const name = e.name || e.title || e.label || ("#" + (e.id !== undefined ? e.id : ""));
    const desc = e.desc || e.text || e.note || e.sub || "";
    return tip(h("div", { class: "tt-name", style: e.color ? "color:var(--gw-" + e.color + ")" : null }, name), desc ? line("tt-desc", GW.text(String(desc).slice(0, 500))) : null,
      rule(), ed(type + " " + (e.id !== undefined ? e.id : e.key || "")));
  };

  // tooltip for (type, id[, inst]) -> Promise<element>
  GW.render.tooltip = async function (type, id, inst) {
    if (type === "item") {
      const [e, labels] = await Promise.all([GW.catalog.get("item", id), GW.catalog.labels().catch(() => ({}))]);
      if (inst && inst.attrs) for (const a of inst.attrs) if (!a.entry) a.entry = await GW.catalog.get("appendattr", a.id).catch(() => ({}));
      if (inst && inst.sockets) for (const s of inst.sockets) if (s && s.effect && !s.entry) s.entry = await GW.catalog.get("holystone", s.effect).catch(() => ({}));
      if (inst && inst.holy_suit_code && !inst.suit) inst.suit = await GW.catalog.get("suit", inst.holy_suit_code).catch(() => ({}));
      return GW.render.item(e, inst, labels);
    }
    const e = await GW.catalog.get(type, id);
    const fn = GW.render[type];
    if (fn && type !== "tooltip" && type !== "generic" && type !== "fieldHelp") return fn(e, inst);
    const row = await GW.catalog.row(type, id).catch(() => null);
    return GW.render.generic(type, Object.assign({}, row || {}, e));
  };

  // ------------------------------------------------------------ field help (UIHelper, UI-STYLE s.4.3)
  const APPLY_CLASS = { now: "now", per_use: "now", hot_reload: "now", live: "now", next_login: "login", login: "login",
    after_restart: "restart", startup: "restart", never: "never", not_editable: "never", new_revision_and_restart: "never", unknown: "never" };
  GW.render.fieldHelp = function (f) {
    const range = (f.min !== null && f.min !== undefined) || (f.max !== null && f.max !== undefined)
      ? GW.fmt(f.min) + " – " + GW.fmt(f.max) + (f.unit ? " " + f.unit : "") : "";
    const vals = f.values && typeof f.values === "object" ? Object.entries(f.values).slice(0, 12).map(([k, v]) => k + " = " + v).join(", ") : (Array.isArray(f.values) ? f.values.join(", ") : "");
    return h("div", { class: "gw-tooltip gw-help" },
      h("div", { class: "tt-name tt-left" }, f.label || f.column, h("span", { class: "tt-col" }, " (" + f.column + ")")),
      h("div", { class: "tt-line" }, f.description || ""),
      range ? h("div", { class: "tt-line" }, "Range: " + range + ".") : null,
      vals ? h("div", { class: "tt-line" }, "Values: " + vals) : null,
      h("div", { class: "tt-line tt-small" }, "Stored in " + f.table + "." + f.column + (f.type ? " (" + f.type + ")" : "") + "."),
      f.revision ? h("div", { class: "tt-line tt-small" }, "A change also moves " + f.revision + " forward by one.") : null,
      h("div", { class: "tt-line" }, h("span", { class: "gw-chip " + (APPLY_CLASS[f.applies_rule] || "never") }, f.applies_text || f.applies_rule || "")),
      !f.editable ? h("div", { class: "tt-line tt-bad" }, "Read-only" + (f.why ? ": " + f.why : "")) : (f.why ? h("div", { class: "tt-line tt-small" }, f.why) : null),
      f.advanced ? h("div", { class: "tt-line tt-small" }, "Advanced: shown behind the 'Advanced' switch.") : null,
      f.live_capability ? h("div", { class: "tt-line tt-small" }, "Online edit through the game server: " + f.live_capability + " (when the server offers it).") : null,
      f.caution ? h("div", { class: "tt-line tt-bad" }, f.caution) : null);
  };
  GW.APPLY_CLASS = APPLY_CLASS;
})();
