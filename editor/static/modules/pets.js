/* static/modules/pets.js - the "Pets & mounts" page: the in-game Pet window (shed), Pet details window (one pet's
   card) and the bag's Mount tab. Backend: modules/pets.py. Contract: MODULES.md. Client and database text is always
   inserted as text (GW.h), never as HTML. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const ROMAN = ["", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X"];
  const F = c => "character_pets." + c;
  const SV = c => "character_pet_stat_values." + c;
  const U32 = 4294967295;
  const TALENT_ABBR = { 1: "RE", 2: "QD", 4: "WK", 8: "HL", 16: "MG" };
  let S = null;

  const num = v => (v === null || v === undefined || v === "") ? null : Number(v);
  const fmt2 = v => Number(v).toFixed(2);
  const fmtg = v => { const n = Number(v); return Number.isInteger(n) ? String(n) : n.toFixed(6).replace(/0+$/, "").replace(/\.$/, ""); };
  const lab = (field, text, help) => h("span", { class: "gw-label", "data-field": field || null, "data-help": help || null }, text);
  const val = (text, cls) => h("span", { class: "gw-value " + (cls || "") }, text);
  const aptCls = a => "gw-apt" + Math.max(1, Math.min(20, Number(a) || 1));
  // the name is the in-game word (text.lua PETAPTITUDE<n>); the reborn server renamed natures 6-10 and added 15/16
  const aptAlso = a => !a ? "" : (a.client_name && a.client_name !== a.name ? "in game: " + a.client_name + "; " : !a.client_name ? "no in-game text; " : "") +
    (a.server_name && a.server_name !== a.name ? "server: " + a.server_name : "").replace(/; $/, "");
  // modals live under <body>: wrap them so this page's CSS (every rule starts with .gw-mod-pets) applies
  const modal = (content, opts) => GW.modal(h("div", { class: "gw-mod-pets" }, content), opts);

  // ------------------------------------------------------------------ data
  async function load(keepSel) {
    const d = await S.ctx.api("sheet/" + S.ctx.cid);
    S.data = d;
    if (!keepSel || !d.pets.some(p => p.id === S.sel)) S.sel = d.pets.length ? d.pets[0].id : null;
    if (S.msel && !d.mounts.items.some(m => m.row === S.msel)) S.msel = null;
    resetPending();
    render();
  }
  async function library() {
    if (!S.lib) {
      const r = await S.ctx.api("skills");
      S.lib = { byId: new Map(r.skills.map(s => [s.id, s])), fam: new Map(r.families.map(f => [f.type, f])), species: r.species };
    }
    return S.lib;
  }
  const pet = () => S.data && S.data.pets.find(p => p.id === S.sel);
  const online = () => !!(S.data && S.data.online && S.data.online.online);
  function blocked() {
    if (!S.data.writable) return "This editor is read-only for database " + S.data.database + ".";
    if (online()) return S.data.character.name + " is online. " + S.data.live.why_pets.replace(/^./, c => c.toUpperCase()) + "." +
      (S.data.live.kick ? " Use 'Kick to edit everything' on the banner first." : " Live edits of other fields arrive with the round-2 server update.");
    return "";
  }
  async function guard() {
    const b = blocked();
    if (b) { await GW.alert(b, "Cannot edit now"); return false; }
    return true;
  }

  // one write with the standard reactions; reloads afterwards (also after 409, so the page shows the current rows)
  async function write(sub, body, label) {
    try {
      const r = await S.ctx.api(sub, body);
      GW.writeDone(r, label);
      if (r.note) GW.alert(r.note, "Saved");
      await load(true);
      return r;
    } catch (e) {
      const kind = await GW.writeError(e);
      if (kind === "conflict") await load(true);
      return null;
    }
  }

  // ------------------------------------------------------------------ pending edits of the selected pet (save bar)
  function resetPending() {
    S.pend = { fields: {}, savvy: {}, growth: {}, presence: null };
    if (S.bar) S.bar.clear();
  }
  function setPend(kind, key, from, to, label, field) {
    const same = String(from) === String(to) || (from !== null && to !== null && !isNaN(Number(from)) && !isNaN(Number(to)) && Number(from) === Number(to));
    const bk = kind + ":" + key;
    if (kind === "f") { if (same) delete S.pend.fields[key]; else S.pend.fields[key] = to; }
    else if (kind === "s") { if (same) delete S.pend.savvy[key]; else S.pend.savvy[key] = to; }
    else if (kind === "g") { if (same) delete S.pend.growth[key]; else S.pend.growth[key] = to; }
    else if (kind === "p") { S.pend.presence = same ? null : to; }
    if (same) S.bar.unset(bk); else S.bar.set(bk, { label, from: kind === "p" ? presText(from) : from, to: kind === "p" ? presText(to) : to, field, applies: "next_login" });
    if (kind === "f" && (key === "level" || key === "aptitude")) drawSavvy();
    if (kind === "s" || kind === "g") drawSavvy();
    scheduleLegal();
  }
  function pendingFields() {
    const fields = Object.assign({}, S.pend.fields);
    if (Object.keys(S.pend.savvy).length) fields.savvy = Object.assign({}, S.pend.savvy);
    if (Object.keys(S.pend.growth).length) fields.growth = Object.assign({}, S.pend.growth);
    return fields;
  }

  // ------------------------------------------------------------------ "legal pet" mode: what real merges / rebirths could reach
  const LEGAL_KEY = "gw.pets.legal";
  function legalOn() { if (S.legal === undefined) { try { S.legal = localStorage.getItem(LEGAL_KEY) === "1"; } catch (e) { S.legal = false; } } return S.legal; }
  function setLegal(on) { S.legal = !!on; try { localStorage.setItem(LEGAL_KEY, on ? "1" : "0"); } catch (e) { /* private window */ } }
  function scheduleLegal() {
    if (!S || !legalOn() || !S.legalBox) return;
    clearTimeout(S.legalT);
    S.legalT = setTimeout(refreshLegal, 300);
  }
  async function refreshLegal() {
    const p = pet(), box = S.legalBox;
    if (!p || !box) return;
    const seq = (S.legalSeq = (S.legalSeq || 0) + 1);
    let r;
    try { r = await S.ctx.api("legal/" + S.ctx.cid, { pet: p.id, fields: pendingFields() }); }
    catch (e) { if (seq === S.legalSeq) GW.clear(box).append(h("div", { class: "tt-bad" }, e.message)); return; }
    if (seq !== S.legalSeq || box !== S.legalBox) return;
    const L = r.limits;
    GW.clear(box).append(
      ...r.checks.map(x => h("div", { class: x.level === "error" ? "tt-bad" : x.level === "warn" ? "pt-legalwarn" : "gw-good" },
        (x.level === "error" ? "Server refuses: " : x.level === "warn" ? "Not reachable in game: " : "Legal: ") + x.msg)),
      h("div", { class: "pt-legallim gw-dim" },
        "Reachable with " + p.completed_pet_merges + " merge(s) and " + p.completed_rebirths + " rebirth(s): Basic total up to " + L.basic_total_max +
        " (hatch " + L.basic_hatch + " + " + L.per_merge.savvy + " per stat per merge) · rank up to " + L.rank_max + " (hatch " + L.hatch_ranks.join(" / ") +
        " + " + L.per_merge.rank + " per merge) · growth acceleration " + L.accel[0] + " - " + L.accel[1] + " per stat (Phoenix " + L.phoenix.join(" - ") +
        ") · level 1 - " + L.level_max + " · max energy " + L.energy + ". Use the Simulator tab to build the merges and rebirths."));
  }
  function legalSection() {
    const on = legalOn();
    const tog = h("button", { class: "gw-chipbtn" + (on ? " on" : ""), type: "button", role: "switch", "aria-checked": on ? "true" : "false",
      "data-help": "Legal pet mode: checks the pet against what real Pet Manager merges, rebirths and items can produce from its hatch (Basic, rank, growth acceleration, energy, skills with no skill book, Trait needs). Warnings only: saving still works.",
      onclick: () => { setLegal(!legalOn()); render(); } }, on ? "Legal pet mode: on" : "Legal pet mode: off");
    S.legalBox = on ? h("div", { class: "pt-legal", role: "status", "aria-live": "polite" }, h("span", { class: "gw-dim" }, "checking ...")) : null;
    if (on) setTimeout(refreshLegal, 0);
    return h("div", { class: "pt-sec" }, h("div", { class: "pt-row" }, tog), S.legalBox);
  }
  const presText = p => p ? [p.carried ? "carried" : "", p.summoned ? "summoned" : "", p.contributes ? "merged into owner" : ""].filter(Boolean).join(", ") || "in the shed" : "";
  async function savePending() {
    const p = pet();
    if (!p) return null;
    const fields = pendingFields();
    if (S.pend.presence) fields.presence = S.pend.presence;
    try {
      const r = await S.ctx.api("pet/" + S.ctx.cid + "/edit", { pet: p.id, revision: p.revision, fields });
      if (r.note) setTimeout(() => GW.alert(r.note, "Saved"), 50);
      await load(true);
      return r;
    } catch (e) { if (e.status === 409) setTimeout(() => load(true), 100); throw e; }
  }

  // ------------------------------------------------------------------ page
  function render() {
    const d = S.data;
    GW.clear(S.root);
    const ch = d.character;
    const head = h("div", { class: "pt-head" },
      h("span", { class: "gw-chip " + (d.writable ? "login" : "never"), "data-help": "The database this editor writes. A copy is safe to experiment on." }, "database " + d.database),
      h("span", { class: "gw-chip " + (d.live.available ? "now" : "never"), "data-help": d.live.available
        ? "The game server can change characters while they are online. For this page: mounts given to or removed from the bag reach an online player at once. Pets have no live operation in the game server."
        : "The game server does not offer live edits (stock server). Everything here is a database edit for a logged-out character; it shows at the next login." },
        d.live.available ? "live edits: bag items" : "no live edits"),
      h("span", { class: "gw-dim" }, ch.name + " · Lv " + ch.level + " " + (ch.class || "") + " · " + ch.owned_pets + " pet" + (ch.owned_pets === 1 ? "" : "s") + " · " + d.mounts.items.length + " mount item" + (d.mounts.items.length === 1 ? "" : "s")));
    const tabs = h("div", { class: "pt-tabs", role: "tablist" },
      ["pets", "sim", "mounts"].map(t => h("button", { class: "gw-tabx" + (S.tab === t ? " on" : ""), type: "button", role: "tab", "aria-selected": S.tab === t ? "true" : "false",
        onclick: async () => { if (S.tab === t) return; if (S.bar.size && !await GW.confirm({ title: "Unsaved changes", message: "Discard the unsaved pet changes?", ok: "Discard" })) return; resetPending(); S.tab = t; render(); } },
        t === "pets" ? "Pets" : t === "sim" ? "Merge & rebirth simulator" : "Mounts")));
    S.root.append(head, tabs);
    if (blocked()) S.root.append(h("div", { class: "gw-online " + (online() ? "on" : "warn") + " pt-block" }, S.tab === "mounts" && online() && d.writable
      ? (d.live.bag_add ? ch.name + " is online. Giving a mount or gear to the bag and deleting one from the bag go live (the game server does it and the player sees it at once). The riding slot, gear slots, quality and other places need the character logged out" + (d.live.kick ? ": use 'Kick to edit everything' on the banner." : ".")
        : ch.name + " is online. Live edits arrive with the round-2 server update; this character must be logged out to change mounts.")
      : blocked()));
    S.legalBox = null;
    if (S.tab === "pets") renderPets(); else if (S.tab === "sim") renderSim(); else renderMounts();
    S.root.append(S.bar.el);
  }

  // ================================================================== PETS
  function renderPets() {
    const d = S.data;
    const wrap = h("div", { class: "pt-cols" });
    wrap.append(shedWindow());
    const p = pet();
    wrap.append(p ? detailWindow(p) : h("section", { class: "gw-frame-normalwindow gw-win pt-detail" }, h("div", { class: "gw-wtitle" }, "Pet details"),
      h("p", { class: "gw-dim" }, d.pets.length ? "Pick a pet in the shed." : "This character has no pets. Click an empty shed cell or '+ Add pet'.")));
    S.root.append(wrap);
  }

  function shedWindow() {
    const d = S.data, ch = d.character, p = pet();
    const cap = Number(ch.pet_shed_capacity), max = Number(d.settings.maximum_owned_pet_count || 8);
    const owned = d.pets.filter(x => x.activity_state === "owned");
    const away = d.pets.filter(x => x.activity_state !== "owned");
    const cells = h("div", { class: "pt-shed", role: "list", "aria-label": "pet shed" });
    for (let i = 0; i < max; i++) {
      const x = owned[i];
      if (x) {
        const c = GW.cell({ sprite: x.sprite, name: x.species_name, corner: x.level, label: x.name + " level " + x.level,
          class: (x.id === S.sel ? "pt-on" : "") + (x.is_summoned ? " pt-summoned" : x.is_carried ? " pt-carried" : ""),
          help: x.name + " · " + x.species_name + " · Lv " + x.level + " · " + x.aptitude_name + (x.is_summoned ? " · summoned" : x.is_carried ? " · carried" : ""),
          onclick: () => pickPet(x.id) });
        cells.append(h("div", { class: "pt-shedcell", role: "listitem" }, c, h("span", { class: "pt-cap " + aptCls(x.aptitude) }, x.name)));
      } else if (i < cap) {
        cells.append(h("div", { class: "pt-shedcell", role: "listitem" }, GW.cell({ empty: true, label: "empty shed cell: add a pet", help: "Empty shed cell. Click to add a pet.", onclick: () => addPet() }),
          h("span", { class: "pt-cap gw-dim" }, "+ add")));
      } else {
        cells.append(h("div", { class: "pt-shedcell", role: "listitem" }, GW.cell({ locked: true, label: "locked shed cell", help: "Locked: the pet shed has " + cap + " cells. Click to open up to " + (i + 1) + " (Pet shed cells, 2-8).",
          onclick: () => setShed(i + 1) }), h("span", { class: "pt-cap gw-dim" }, "locked")));
      }
    }
    const shedField = GW.numberField({ field: "character_base.pet_shed_capacity", label: "Shed cells", value: cap, min: 2, max: 8, onChange: (v, ok) => { shedBtn.disabled = !ok || v === cap; } });
    const shedBtn = h("button", { class: "gw-btnx small", type: "button", disabled: true, onclick: () => setShed(shedField.value) }, "Save");
    const portrait = p ? GW.cell({ sprite: p.sprite, name: p.species_name, tipType: "pet", tipId: p.species_id }) : GW.cell({ empty: true });
    const pres = p ? (p.is_summoned ? "summoned (out in the world)" : p.is_carried ? "carried (in the pet bar)" : p.activity_state === "owned" ? "in the shed" : p.activity_state) : "";
    return h("section", { class: "gw-frame-normalwindow gw-win pt-shedwin" }, h("div", { class: "gw-wtitle" }, "Pet"),
      h("div", { class: "pt-top" }, portrait,
        h("div", { class: "pt-kv" }, lab(F("name"), "Name:"), val(p ? p.name : "-"), lab(F("level"), "Level:"), val(p ? String(p.level) : "-"),
          lab(null, "State:", "Where the pet is: in the shed, carried (its icon in the pet bar) or summoned (walking beside the player). One pet at most is carried."), val(pres)),
        h("div", { class: "pt-presbtns" },
          presButton(p, "Summon", { carried: true, summoned: true, contributes: false }, "Carried and summoned: the pet walks beside the player and its skills count."),
          presButton(p, "Carry", { carried: true, summoned: false, contributes: false }, "Carried: the pet's icon is in the pet bar, not summoned. Only one pet is carried; the editor clears the others."),
          presButton(p, "To shed", { carried: false, summoned: false, contributes: false }, "Kept in the shed: neither carried nor summoned."))),
      h("div", { class: "gw-frame-simplewindow pt-shedbox" }, cells),
      h("div", { class: "pt-row" }, shedField, shedBtn),
      h("div", { class: "pt-row" },
        h("button", { class: "gw-btnx", type: "button", "data-help": "Hatch a new pet for this character, like opening a pet egg: pick the species, aptitude, sex, level and name.", onclick: () => addPet() }, "+ Add pet"),
        p ? h("button", { class: "gw-btnx danger", type: "button", "data-help": "Delete this pet with its savvy and skills. Undo puts it back.", onclick: () => deletePet(p) }, "Delete pet") : null),
      away.length ? h("div", { class: "pt-away" }, h("div", { class: "gw-label" }, "Away (packed, dispatched or working):"),
        away.map(x => h("button", { class: "gw-chipbtn" + (x.id === S.sel ? " on" : ""), type: "button", onclick: () => pickPet(x.id),
          "data-help": "Read-only: a " + x.activity_state + " pet belongs to its item or job; the server's own commands keep its evidence." }, x.name + " (" + x.activity_state + ")"))) : null,
      findings());
  }
  function presButton(p, text, target, help) {
    if (!p) return h("button", { class: "gw-btnx small", type: "button", disabled: true }, text);
    const cur = curPresence(p);
    const on = cur.carried === target.carried && cur.summoned === target.summoned;
    return h("button", { class: "gw-btnx small" + (on ? " on" : ""), type: "button", "data-field": F(target.summoned ? "is_summoned" : "is_carried"), "data-help": help,
      disabled: p.sealed ? true : null, onclick: () => {
        const orig = { carried: !!p.is_carried, summoned: !!p.is_summoned, contributes: !!p.contributes_to_character };
        const t = Object.assign({}, target, { contributes: target.summoned && cur.contributes });
        setPend("p", "presence", orig, t, "pet state", F("is_carried"));
        render();
      } }, text);
  }
  function curPresence(p) {
    return S.pend.presence || { carried: !!p.is_carried, summoned: !!p.is_summoned, contributes: !!p.contributes_to_character };
  }
  function findings() {
    const f = S.data.findings || [];
    const box = h("div", { class: f.length ? "pt-findings" : "pt-row" });
    const draw = (list, full) => {
      GW.clear(box);
      box.className = list.length ? "pt-findings" : "pt-row";
      if (list.length) box.append(h("div", { class: "gw-label" }, "Checks (what the server would refuse or warn about):"),
        ...list.map(x => h("div", { class: x.severity === "error" ? "tt-bad" : "gw-dim" }, (x.severity === "error" ? "Error: " : "Note: ") + x.where + " - " + x.msg)));
      else box.append(h("span", { class: "gw-good" }, full ? "Start-up checks: all pets pass." : "No problems found."));
      if (!full) box.append(h("button", { class: "gw-btnx small", type: "button", "data-help": "Also run the reborn server's three pet start-up checks (a pet that fails one stops the server from starting). Every save runs them too.",
        onclick: async ev => { ev.target.disabled = true; ev.target.textContent = "checking ..."; try { const r = await S.ctx.api("checks/" + S.ctx.cid); draw(r.findings, true); } catch (e) { GW.status(e.message, "err"); } } }, "Run start-up checks"));
    };
    draw(f, false);
    return box;
  }
  async function pickPet(id) {
    if (id === S.sel) return;
    if (S.bar.size && !await GW.confirm({ title: "Unsaved changes", message: "Discard the unsaved changes of this pet?", ok: "Discard" })) return;
    resetPending(); S.sel = id; render();
  }
  async function setShed(n) {
    const cap = S.data.character.pet_shed_capacity;
    if (!await guard()) return;
    if (n === cap) return;
    if (!await GW.confirm({ title: "Pet shed cells", message: "Change the pet shed from " + cap + " to " + n + " cells?" +
      (n < S.data.character.owned_pets ? "\n\n|cFFFF0000The character owns " + S.data.character.owned_pets + " pets: the server's pet list cannot hold more pets than cells, so this is refused." : ""), ok: "Save" })) return;
    await write("shed/" + S.ctx.cid, { capacity: n, expected: cap }, "pet shed cells " + cap + " -> " + n);
  }

  // ------------------------------------------------------------------ the Pet details window
  function detailWindow(p) {
    const d = S.data;
    const ro = !!p.sealed || !!blocked();
    const sec = (title, ...kids) => h("div", { class: "pt-sec" }, h("div", { class: "pt-sectitle" }, title), ...kids);
    const win = h("section", { class: "gw-frame-normalwindow gw-win pt-detail" + (ro ? " pt-ro" : "") }, h("div", { class: "gw-wtitle" }, "Pet details"));
    if (p.sealed) win.append(h("div", { class: "gw-online warn" }, "Read-only: this pet is " + p.activity_state + " (packed in an item, dispatched or working). The server's seal, dispatch and work commands own it."));
    win.append(legalSection());

    // ---- facts
    const nameIn = h("input", { class: "pt-input", type: "text", maxlength: 31, value: p.name, "aria-label": "pet name", "data-field": F("name"),
      oninput: () => { const v = nameIn.value.trim(); const ok = /^[A-Za-z0-9_\-. ]{1,31}$/.test(v); nameIn.classList.toggle("bad", !ok); if (ok) setPend("f", "name", p.name, v, "name", F("name")); } });
    const sexNow = () => S.pend.fields.sex !== undefined ? Number(S.pend.fields.sex) : Number(p.sex);
    const sexBtns = h("span", { class: "pt-seg" }, [[0, "Female"], [1, "Male"]].map(([v, t]) => h("button", { class: "gw-chipbtn" + (sexNow() === v ? " on" : ""), type: "button", "data-field": F("sex"),
      onclick: () => { setPend("f", "sex", p.sex, v, "sex", F("sex")); render(); } }, t)));
    const aptNow = S.pend.fields.aptitude !== undefined ? Number(S.pend.fields.aptitude) : Number(p.aptitude);
    const apt = d.aptitudes.find(a => a.id === aptNow) || {};
    const aptBtn = h("button", { class: "pt-aptbtn " + aptCls(aptNow), type: "button", "data-field": F("aptitude"), onclick: () => pickAptitude(p) }, apt.name || String(aptNow),
      aptAlso(apt) ? h("span", { class: "gw-dim" }, " (" + aptAlso(apt) + ")") : null, " ▾");
    const soulNow = S.pend.fields.soul_contract_stage !== undefined ? Number(S.pend.fields.soul_contract_stage) : Number(p.soul_contract_stage);
    const soul = h("span", { class: "pt-pips", role: "radiogroup", "aria-label": "soul contract stage" }, [0, 1, 2, 3, 4, 5, 6].map(n => h("button", {
      class: "pt-pip" + (n <= soulNow && n > 0 ? " on" : "") + (n === soulNow ? " cur" : ""), type: "button", role: "radio", "aria-checked": n === soulNow ? "true" : "false",
      "data-field": F("soul_contract_stage"), "data-help": n === 0 ? "No soul contract." : "Stage " + n + " (signed with " + (n - 1) + " Contract Spirit" + (n === 2 ? "" : "s") + "): +" + d.soul_bonus[n] + " to every displayed savvy value. A contract is needed for rebirth.",
      onclick: () => { setPend("f", "soul_contract_stage", p.soul_contract_stage, n, "soul contract", F("soul_contract_stage")); render(); } }, n === 0 ? "none" : String(n))));
    const boundNow = S.pend.fields.bound !== undefined ? !!S.pend.fields.bound : !!p.bound;
    const bound = h("button", { class: "gw-sprite gw-check" + (boundNow ? " on" : ""), type: "button", role: "checkbox", "aria-checked": boundNow ? "true" : "false", "aria-label": "bound", "data-field": F("bound"),
      onclick: () => { setPend("f", "bound", !!p.bound, !boundNow, "bound", F("bound")); render(); } });
    win.append(h("div", { class: "pt-facts" },
      lab(F("name"), "Name:"), nameIn,
      lab(F("species_id"), "Type:", "The species. It decides the model, food and starter skill; to change it, delete the pet and add one of the new species."),
      h("span", { class: "pt-species", "data-tip-type": "pet", "data-tip-id": String(p.species_id) }, GW.icon(p.sprite, { name: p.species_name }), val(p.species_name + " #" + p.species_id)),
      lab(F("sex"), "Gender:"), sexBtns,
      lab(F("aptitude"), "Nature:", "The aptitude (Weak ... Transcendent). It decides the innate talents, the savvy bracket and the starting skill cells."), aptBtn,
      lab(null, "Food Type:", "What the pet eats (from the species): Herbivore, Carnivore or Omnivore food restores satiety and amity."), val(p.food || "?"),
      lab(F("soul_contract_stage"), "Contract:"), soul,
      lab(F("bound"), "Bound Status:"), h("span", { class: "pt-inline" }, bound, val(boundNow ? "bound to the owner" : "not bound"))));

    // ---- care bars
    const wire = d.wire;
    win.append(sec("Care", h("div", { class: "pt-bars" },
      bar(p, "satiety", "Satiety", 100, "food: 0 = the pet cannot be summoned", "sat"),
      bar(p, "amity", "Amity", 100, "friendship: owner merge needs 40", "ami"),
      bar(p, "remaining_lifetime", "Lifetime", Math.max(Number(p.max_lifetime) || 1500, Number(p.remaining_lifetime) || 0), "life left; the species' bar is full at " + GW.fmt(p.max_lifetime) + " (max " + GW.fmt(wire.remaining_lifetime[1]) + ")", "life", wire.remaining_lifetime[1]),
      bar(p, "current_energy", "Energy", null, "spent while merged into the owner; merge needs full energy", "en"))),
      h("div", { class: "pt-row" }, GW.numberField({ field: F("maximum_energy"), label: "Max energy", value: num(p.maximum_energy), min: 1, max: 100000,
        onChange: (v, ok) => { if (ok && v !== null) setPend("f", "maximum_energy", p.maximum_energy, v, "max energy", F("maximum_energy")); } }),
        h("span", { class: Number(p.maximum_energy) === 100 ? "gw-dim pt-note" : "pt-legalwarn pt-note", "data-help": "Every pet has max energy 100: both hatch inserts write 100 and nothing in the server changes it. Energy is a percentage; the game always shows it on a fixed 0-1800 scale, so a larger maximum looks the same but owner Merge drains 1 point per 3 s and recharges 5 per 6 s, i.e. it lasts longer." },
          Number(p.maximum_energy) === 100 ? "100 for every pet (normal)" : "normal is 100: more makes owner Merge last longer")));

    // ---- talents
    const aptRow = d.aptitudes.find(a => a.id === aptNow) || {};
    const mask = aptRow.talent_mask !== undefined ? aptRow.talent_mask : Number(p.talent_mask);
    const tal = h("div", { class: "gw-frame-simplewindow pt-cells6" });
    for (const t of p.talents) {
      const on = !!(mask & t.bit);
      tal.append(h("div", { class: "pt-talcell" }, GW.cell({ name: TALENT_ABBR[t.bit], locked: !on, class: on ? "pt-talon" : "",
        help: t.name + (on ? " (has it)" : " (not granted)") + ": " + t.help + "\n\nInnate talents follow the aptitude (database rule): Godly and above, or Grumpy (7), give all five; Smart to Almighty (10-13), or Brave (8), give Quest Dispatch, Healing and Merge; the rest give none. Change the Nature to change them." }),
      h("span", { class: "pt-cap" + (on ? "" : " gw-dim") }, t.name)));
    }
    tal.append(h("div", { class: "pt-talcell" }, GW.cell({ locked: true, help: "The sixth talent cell is unused (bit 5 is reserved by the client)." }), h("span", { class: "pt-cap gw-dim" }, "-")));
    win.append(sec(h("span", { "data-field": F("talent_mask") }, "Pet's Talents:"), tal,
      S.pend.fields.aptitude !== undefined ? h("div", { class: "gw-dim" }, "Shown for the new nature (" + aptRow.name + "); saved with it.") : null));

    // ---- skills
    win.append(sec(h("span", null, "Pet's Skills: ", h("span", { class: "gw-dim" }, "May only learn 1 of each type")), skillGrid(p)));

    // ---- numbers
    const req = num(p.exp_to_next);
    const lvNow = S.pend.fields.level !== undefined ? Number(S.pend.fields.level) : Number(p.level);
    const rebMax = Number(d.settings.maximum_rebirth_count || 100);
    const nf = (col, label, o) => GW.numberField(Object.assign({ field: F(col), label, value: num(p[col]),
      onChange: (v, ok) => { if (ok && v !== null) setPend("f", col, p[col], v, label.toLowerCase(), F(col)); } }, o || {}));
    const nextReq = (d.exp_steps || {})[String(lvNow)];
    win.append(sec("Level and rank", h("div", { class: "pt-nums pt-quiet" },
      nf("level", "Level", { min: Number(d.settings.minimum_level || 1), max: Number(d.settings.maximum_level || 120) }),
      h("span", { class: "gw-dim pt-note" }, "Added savvy is recomputed: (growth + acceleration) x level."),
      nf("experience", "Current Exp", { min: 0, max: U32 }),
      h("span", { class: "gw-dim pt-note", "data-help": "EXP the pet needs to reach the next level." },
        "Upgrade Exp: " + (nextReq ? GW.fmt(nextReq) : (req ? GW.fmt(req) : "max level"))),
      nf("rank", "Rank", { min: 0, max: Number(d.settings.maximum_rank || 655.35), decimals: 2, step: 0.01 }),
      h("span", { class: "gw-dim pt-note" }, "Rank picks each skill's effect step (its Restrict list) and is shown x100 in 16 bits (0 - 655.35)."),
      nf("completed_pet_merges", "Merge", { min: 0, max: 65535 }),
      nf("completed_rebirths", "Rebirth", { min: 0, max: Math.min(255, rebMax) }),
      nf("rebirths_remaining", "Left", { min: 0, max: 255 }),
      h("span", { class: "gw-dim pt-note" }, "Rebirths done + left must be 255 or less (one byte in the client). The published content has " + (d.rebirth_steps || []).length + " rebirth steps" +
        ((d.rebirth_steps || []).length ? " (first at level " + d.rebirth_steps[0].level + ")" : "") + "."),
      nf("available_stat_points", "Stat points", { min: 0, max: 2147483647 })), h("div", { class: "pt-row" }, GW.appliesChip("next_login"))));

    // ---- savvy table
    S.savvyBox = h("div", { class: "gw-frame-simplewindow pt-savvy" });
    win.append(sec(h("span", null, "Savvy Value ", h("span", { class: "gw-dim" }, "(Basic value + Added-value)")), S.savvyBox));
    drawSavvy();

    // ---- presence + merge
    const cur = curPresence(p);
    const chk = (key, label, help, dis) => h("label", { class: "pt-chk", "data-help": help },
      h("button", { class: "gw-sprite gw-check" + (cur[key] ? " on" : ""), type: "button", role: "checkbox", "aria-checked": cur[key] ? "true" : "false", "aria-label": label, disabled: dis ? true : null,
        onclick: () => {
          const t = Object.assign({}, cur); t[key] = !t[key];
          if (key === "summoned" && t.summoned) t.carried = true;
          if (key === "carried" && !t.carried) { t.summoned = false; t.contributes = false; }
          if (key === "summoned" && !t.summoned) t.contributes = false;
          if (key === "contributes" && t.contributes) { t.summoned = true; t.carried = true; }
          setPend("p", "presence", { carried: !!p.is_carried, summoned: !!p.is_summoned, contributes: !!p.contributes_to_character }, t, "pet state", F("is_carried"));
          render();
        } }), " ", label);
    win.append(sec("Carried, summoned, merged", h("div", { class: "pt-row" },
      chk("carried", "Carried", "The pet in the pet bar (one per character; carrying this one clears the others)."),
      chk("summoned", "Summoned", "Out in the world beside the player (must also be carried). Only a summoned pet's skills count."),
      chk("contributes", "Merged into owner", "Pet Unite: the pet is merged into its owner and lends its stats. It can only be started in game (pet summoned, Merge talent, full energy, 40+ amity): the game server ends every Merge when the character logs in, so the editor can only switch it off.",
        !p.contributes_to_character)),
      h("div", { class: "pt-row" }, GW.appliesChip("next_login"))));

    // ---- read-only facts
    const ro2 = [["growth_revealed", p.growth_revealed ? "yes" : "no"], ["birth_rank", p.birth_rank], ["hatch_rank_roll", p.hatch_rank_roll],
      ["activity_state", p.activity_state], ["initial_savvy_baseline_total", p.initial_savvy_baseline_total], ["revision", p.revision], ["created_at", p.created_at]];
    win.append(h("details", { class: "pt-sec pt-rofacts" }, h("summary", null, "Server-owned facts (read-only)"),
      h("div", { class: "pt-facts" }, ro2.map(([c, v]) => [lab(F(c), c.replace(/_/g, " ")), val(v === null || v === undefined ? "-" : String(v))]))));
    return win;
  }

  // a care bar you can click / drag, with its number box
  function bar(p, col, label, max, help, cls, hardMax) {
    const d = S.data;
    const now = () => S.pend.fields[col] !== undefined ? Number(S.pend.fields[col]) : Number(p[col]);
    const lim = hardMax !== undefined ? hardMax : (max === null ? Number(S.pend.fields.maximum_energy !== undefined ? S.pend.fields.maximum_energy : p.maximum_energy) : max);
    const vis = max === null ? lim : max;
    const fill = h("span", { class: "pt-bar-fill pt-" + cls });
    const txt = h("span", { class: "pt-bar-txt gw-outline" });
    const track = h("span", { class: "pt-bar", role: "slider", tabindex: 0, "aria-label": label, "aria-valuemin": 0, "aria-valuemax": lim, "data-field": F(col), "data-help": help }, fill, txt);
    const box = GW.numberField({ field: F(col), label: "", value: now(), min: 0, max: lim, slider: false, onChange: (v, ok) => { if (ok && v !== null) { set(v, true); } } });
    function draw(v) {
      fill.style.width = Math.max(0, Math.min(100, vis ? v / vis * 100 : 0)) + "%";
      txt.textContent = GW.fmt(v) + " / " + GW.fmt(vis);
      track.setAttribute("aria-valuenow", v);
    }
    function set(v, fromBox) {
      v = Math.max(0, Math.min(lim, Math.round(v)));
      setPend("f", col, p[col], v, label.toLowerCase(), F(col));
      draw(v);
      if (!fromBox) box.value = v;
    }
    const at = e => { const r = track.getBoundingClientRect(); return (e.clientX - r.left) / r.width * vis; };
    track.addEventListener("pointerdown", e => { if (p.sealed) return; track.setPointerCapture(e.pointerId); set(at(e)); });
    track.addEventListener("pointermove", e => { if (track.hasPointerCapture && track.hasPointerCapture(e.pointerId)) set(at(e)); });
    track.addEventListener("keydown", e => {
      const step = e.shiftKey ? Math.max(1, Math.round(vis / 10)) : 1;
      if (e.key === "ArrowRight" || e.key === "ArrowUp") { e.preventDefault(); set(now() + step); }
      if (e.key === "ArrowLeft" || e.key === "ArrowDown") { e.preventDefault(); set(now() - step); }
    });
    draw(now());
    return h("div", { class: "pt-barrow" }, lab(F(col), label + ":"), track, box);
  }

  // the savvy table (redrawn when level / savvy / growth change, without rebuilding the window)
  function drawSavvy() {
    const p = pet();
    if (!p || !S.savvyBox) return;
    const d = S.data;
    const lv = S.pend.fields.level !== undefined ? Number(S.pend.fields.level) : Number(p.level);
    const soul = d.soul_bonus[S.pend.fields.soul_contract_stage !== undefined ? Number(S.pend.fields.soul_contract_stage) : Number(p.soul_contract_stage)] || 0;
    const aptChanged = S.pend.fields.aptitude !== undefined;
    GW.clear(S.savvyBox);
    const t = h("table", { class: "pt-svt" }, h("tr", null, h("th", null, ""), h("th", { "data-field": SV("initial_savvy") }, "Basic"), h("th", { "data-help": "Soul contract bonus: added to every displayed value." }, "Soul"),
      h("th", { "data-field": SV("base_growth_rate") }, "Growth"), h("th", null, ""), h("th", { "data-field": SV("added_savvy") }, "Added"), h("th", { "data-help": "What the game shows: basic + soul + added." }, "Total")));
    let bsum = 0, gsum = 0;
    for (const s of p.stats) {
      const code = String(s.stat_code);
      const b = S.pend.savvy[code] !== undefined ? Number(S.pend.savvy[code]) : Number(s.initial_savvy);
      const g = S.pend.growth[code] !== undefined ? Number(S.pend.growth[code]) : Number(s.base_growth_rate);
      const added = (g + Number(s.growth_acceleration)) * lv;
      bsum += b; gsum += g;
      const bIn = h("input", { class: "pt-input pt-num" + (S.pend.savvy[code] !== undefined ? " changed" : ""), type: "text", inputmode: "decimal", value: fmt2(b), "aria-label": s.stat_name + " basic savvy",
        "data-field": SV("initial_savvy"), disabled: aptChanged || p.sealed ? true : null,
        onchange: () => { const v = Number(bIn.value); if (!isFinite(v) || v <= 0) { bIn.classList.add("bad"); return; } setPend("s", code, Number(s.initial_savvy), Number(v.toFixed(2)), s.stat_name + " basic savvy", SV("initial_savvy")); } });
      const gIn = h("input", { class: "pt-input pt-num" + (S.pend.growth[code] !== undefined ? " changed" : ""), type: "text", inputmode: "decimal", value: fmtg(g), "aria-label": s.stat_name + " growth",
        "data-field": SV("base_growth_rate"), disabled: aptChanged || p.sealed ? true : null,
        onchange: () => { const v = Number(gIn.value); if (!isFinite(v) || v <= 0) { gIn.classList.add("bad"); return; } setPend("g", code, Number(s.base_growth_rate), Number(v.toFixed(6)), s.stat_name + " growth", SV("base_growth_rate")); } });
      t.append(h("tr", null, h("td", { class: "gw-label" }, s.stat_name + ":"), h("td", null, bIn), h("td", { class: "pt-soul" }, soul ? "+" + soul : ""),
        h("td", null, gIn, Number(s.growth_acceleration) ? h("span", { class: "gw-good", "data-field": SV("growth_acceleration") }, " +" + fmtg(s.growth_acceleration)) : null),
        h("td", null, "+"), h("td", { class: "gw-value", "data-help": "(" + fmtg(g) + " + " + fmtg(s.growth_acceleration) + ") x level " + lv }, fmt2(added)),
        h("td", { class: "gw-value pt-tot" }, fmt2(b + soul + added))));
    }
    const base = Number(p.initial_savvy_baseline_total || 0);
    const gb = p.growth_bracket || ["0", "1000"];
    const bOk = bsum + 1e-9 >= base, gOk = gsum + 1e-9 >= Number(gb[0]) && gsum - 1e-9 <= Number(gb[1]);
    S.savvyBox.append(...[t,
      h("div", { class: "pt-svsum" },
        h("span", { class: bOk ? "gw-good" : "tt-bad", "data-help": "The server refuses to start if a pet's basic savvy total is below its birth baseline (start-up check)." },
          "Basic total " + fmt2(bsum) + (base ? " (at least " + base + ")" : "")),
        h("span", { class: gOk ? "gw-good" : "tt-bad", "data-help": "The six growth rates must add up to the " + (p.growth_revealed ? "pet's aptitude" : "Weak (growth not revealed yet)") + " bracket, or the server refuses to start." },
          "Growth total " + fmtg(gsum.toFixed(6)) + " (" + fmtg(gb[0]) + " - " + fmtg(gb[1]) + ", " + (p.growth_bracket_name || "") + ")")),
      aptChanged ? h("div", { class: "gw-dim" }, "The new nature resets the six basic values to the middle of its bracket (" + ((S.data.aptitudes.find(a => a.id === Number(S.pend.fields.aptitude)) || {}).savvy || []).join(" - ") + " in total); save it first, then fine-tune.") : null,
      h("div", { class: "pt-row" }, GW.appliesChip("next_login"))].filter(Boolean));
  }

  // ------------------------------------------------------------------ skill cells
  function skillGrid(p) {
    const opened = Number(S.pend.fields.opened_skill_slots !== undefined ? S.pend.fields.opened_skill_slots : p.opened_skill_slots);
    const avail = Number(S.pend.fields.available_skill_slots !== undefined ? S.pend.fields.available_skill_slots : p.available_skill_slots);
    const grid = h("div", { class: "gw-frame-simplewindow pt-cells12" });
    const bySlot = new Map(p.skills.map(s => [s.slot_index, s]));
    for (let i = 0; i < 12; i++) {
      const s = bySlot.get(i);
      let c;
      if (s) {
        c = GW.cell({ sprite: s.sprite, name: s.name, corner: ROMAN[s.tier] || "", tipType: "petskill", tipId: s.skill_id, label: "cell " + (i + 1) + ": " + s.name,
          class: (!s.species_ok ? "pt-warn" : "") + (!s.published ? " pt-warn" : ""), onclick: () => skillCell(p, s) });
      } else if (i < opened) {
        c = GW.cell({ empty: true, label: "cell " + (i + 1) + ": empty, opened", help: "Cell " + (i + 1) + ": opened and empty. Click to teach the pet a skill (the whole pet-skill library).", onclick: () => learn(p, null) });
      } else if (i < avail) {
        c = GW.cell({ locked: true, class: "pt-avail", label: "cell " + (i + 1) + ": available, not opened", help: "Cell " + (i + 1) + ": available but not opened (in game: Golden Apple Juice opens the next one). Click to open the cells up to here.",
          onclick: () => { setPend("f", "opened_skill_slots", p.opened_skill_slots, i + 1, "opened skill cells", F("opened_skill_slots")); render(); } });
      } else {
        c = GW.cell({ locked: true, label: "cell " + (i + 1) + ": locked", help: "Cell " + (i + 1) + ": locked (in game: Pet Enhance Spring makes the next one available). Click to make the cells up to here available and opened.",
          onclick: () => { setPend("f", "available_skill_slots", p.available_skill_slots, i + 1, "available skill cells", F("available_skill_slots"));
            setPend("f", "opened_skill_slots", p.opened_skill_slots, i + 1, "opened skill cells", F("opened_skill_slots")); render(); } });
      }
      grid.append(c);
    }
    const used = p.skills.length;
    const cellsRow = h("div", { class: "pt-row pt-quiet" },
      GW.numberField({ field: F("opened_skill_slots"), label: "Opened cells", value: opened, min: Math.max(1, used), max: 12, slider: false,
        onChange: (v, ok) => { if (ok && v !== null) { setPend("f", "opened_skill_slots", p.opened_skill_slots, v, "opened skill cells", F("opened_skill_slots")); } } }),
      GW.numberField({ field: F("available_skill_slots"), label: "Available cells", value: avail, min: 1, max: 12, slider: false,
        onChange: (v, ok) => { if (ok && v !== null) { setPend("f", "available_skill_slots", p.available_skill_slots, v, "available skill cells", F("available_skill_slots")); } } }), GW.appliesChip("next_login"));
    const probs = p.skill_problems || [];
    return h("div", null, grid,
      h("div", { class: "pt-row" },
        h("button", { class: "gw-btnx small", type: "button", disabled: p.sealed || used >= opened ? true : null, "data-help": used >= opened ? "All opened cells are used: open another cell first." : "Teach the pet a skill in the next free cell.", onclick: () => learn(p, null) }, "+ Learn a skill"),
        h("span", { class: "gw-dim" }, used + " of " + opened + " opened cells used · " + avail + " available · 12 max")),
      cellsRow,
      probs.length ? h("div", { class: "pt-probs" }, probs.map(x => h("div", { class: "tt-bad" }, x)),
        h("button", { class: "gw-btnx small", type: "button", "data-help": "Put the skills into cells 1..n without gaps, active, with each tier's canonical id and rank, one per family (what the game itself stores).", onclick: () => skillWrite(p, { action: "repair" }, "repair skill cells") }, "Repair skill cells")) : null,
      h("div", { class: "gw-dim pt-note" }, "Skill edits are saved at once (each with Undo). Cell counts go with 'Review & save'."));
  }

  async function skillWrite(p, body, label) {
    if (!await guard()) return null;
    if (S.bar.size && !await GW.confirm({ title: "Unsaved changes", message: "This pet has unsaved changes. A skill edit reloads the pet and discards them. Continue?", ok: "Continue" })) return null;
    return write("pet/" + S.ctx.cid + "/skill", Object.assign({ pet: p.id, revision: p.revision }, body), label);
  }

  async function skillCell(p, s) {
    const lib = await library();
    const e = lib.byId.get(s.skill_id) || {};
    const fam = lib.fam.get(e.type);
    const exp = GW.numberField({ field: "character_pet_skills.skill_experience", label: "Skill EXP", value: num(s.skill_experience), min: 0, max: 2147483647 });
    const tiers = fam ? Object.entries(fam.tiers).map(([tier, id]) => h("button", { class: "gw-chipbtn" + (Number(tier) === Number(s.tier) ? " on" : ""), type: "button",
      "data-tip-type": "petskill", "data-tip-id": String(id), onclick: async () => { m.close(); await skillWrite(p, { action: "set", skill: id, experience: 0 }, "tier " + ROMAN[tier]); } }, ROMAN[tier] || tier)) : [];
    const tip = await GW.render.tooltip("petskill", s.skill_id).catch(() => h("div"));
    const body = h("div", { class: "pt-skilldlg" }, tip,
      h("div", { class: "pt-facts" },
        lab(null, "Family:", "Pet_Skill.xml Type: a pet can have one skill of each family. Learning another tier of the same family replaces it."), val((e.family || "?") + " (type " + e.type + ")"),
        lab(null, "Tier:", "Priority: the skill book tier. The game stores the tier's first id with rank = tier."), h("span", { class: "pt-seg" }, tiers),
        lab(null, "Only for:", "Skills whose client text says 'Available to ... only' belong to one species family."), val(e.only ? e.only : "any pet", s.species_ok ? "" : "tt-bad"),
        lab("character_pet_skills.slot_index", "Cell:"), val(String(s.slot_index + 1)),
        lab("character_pet_skills.skill_id", "Id:"), val("#" + s.skill_id + (e.head && e.head !== s.skill_id ? " (canonical #" + e.head + ")" : ""))),
      h("div", { class: "pt-row" }, exp, h("button", { class: "gw-btnx small", type: "button", onclick: async () => { m.close(); await skillWrite(p, { action: "set", skill: s.skill_id, experience: exp.value || 0 }, "skill EXP"); } }, "Save EXP")),
      h("div", { class: "pt-row" },
        h("button", { class: "gw-btnx", type: "button", onclick: () => { m.close(); learn(p, s.slot_index); } }, "Change skill..."),
        h("button", { class: "gw-btnx danger", type: "button", "data-help": "Forget this skill. The skills after it move down one cell, like the game's unlearn.", onclick: async () => { m.close(); await skillWrite(p, { action: "remove", cell: s.slot_index }, "forget " + s.name); } }, "Forget")),
      h("div", { class: "pt-row" }, GW.appliesChip("next_login")));
    const m = modal(body, { title: "Skill cell " + (s.slot_index + 1) + ": " + s.name });
  }

  // the effect of one tier at a pet rank: the highest Restrict (rank) not above it picks Values (absolute, not added)
  function effectAt(e, rank) {
    const r = (e && e.restrict) || [], v = (e && e.values) || [];
    let out = null;
    for (let i = 0; i < r.length; i++) if (Number(rank) >= r[i]) out = { step: i + 1, steps: r.length, restrict: r[i], value: v[i] };
    return out;
  }

  // the skill picker: one row per family (the combined name, no I-VI), click it to pick the level (tier)
  async function learn(p, cell) {
    if (!await guard()) return;
    const lib = await library();
    const have = new Map(p.skills.map(s => [s.type, s]));
    const opt = { fits: true, q: "" };
    const starterType = ((lib.byId.get(Number((lib.species.find(x => x.id === Number(p.species_id)) || {}).starter)) || {}).type);
    const famOk = f => !f.species || f.species.includes(Number(p.species_id)) || f.type === starterType;
    const heads = f => Object.entries(f.tiers).map(([t, id]) => [Number(t), lib.byId.get(Number(id)) || { id: Number(id), name: "#" + id }]);
    const fams = [...lib.fam.values()].sort((a, b) => (have.has(b.type) - have.has(a.type)) || String(a.name).localeCompare(String(b.name)));
    const list = h("div", { class: "pt-famlist", role: "listbox", "aria-label": "pet skill families" });
    const detail = h("div", { class: "pt-libdetail" }, h("p", { class: "gw-dim" }, "Pick a skill on the left, then its level (tier I, II, ...)."));
    const expF = GW.numberField({ field: "character_pet_skills.skill_experience", label: "Skill EXP", value: 0, min: 0, max: 2147483647, slider: false });
    let sel = null;
    function drawList() {
      GW.clear(list);
      const q = opt.q.trim().toLowerCase();
      for (const f of fams) {
        if (opt.fits && !famOk(f)) continue;
        if (opt.fits && !heads(f).some(([, e]) => e.published !== false)) continue;
        if (q && !String(f.name).toLowerCase().includes(q) && !heads(f).some(([, e]) => String(e.info || "").toLowerCase().includes(q))) continue;
        const cur = have.get(f.type);
        const first = heads(f)[0][1];
        list.append(h("button", { class: "pt-famrow" + (sel === f.type ? " on" : "") + (cur ? " pt-has" : ""), type: "button", role: "option", "aria-selected": sel === f.type ? "true" : "false",
          onclick: () => { sel = f.type; drawList(); showFam(f, cur ? Number(cur.tier) : null); } },
          GW.icon(first.sprite, { name: f.name }), h("span", { class: "pt-famname" }, f.name),
          h("span", { class: "pt-famtiers" }, heads(f).map(([t]) => h("span", { class: "pt-tierpip" + (cur && Number(cur.tier) === t ? " cur" : "") + ((f.books || {})[t] && (f.books || {})[t].length ? "" : " nobook") }, ROMAN[t] || t))),
          f.only ? h("span", { class: "gw-dim pt-famonly" }, f.only) : null));
      }
      if (!list.children.length) list.append(h("p", { class: "gw-dim" }, "No skill matches."));
    }
    function showFam(f, tier) {
      const tiers = heads(f);
      const t = tier || tiers[0][0];
      const e = (tiers.find(([x]) => x === t) || tiers[0])[1];
      const cur = have.get(f.type);
      const eff = effectAt(e, p.rank);
      const trait = (e.trait || []).map((v, i) => [v, p.stats[i]]).filter(([v]) => v > 0);
      const books = (f.books || {})[t] || [];
      const chips = h("span", { class: "pt-seg" }, tiers.map(([x, ex]) => h("button", { class: "gw-chipbtn" + (x === t ? " on" : ""), type: "button", "data-tip-type": "petskill", "data-tip-id": String(ex.id),
        onclick: () => showFam(f, x) }, ROMAN[x] || x)));
      GW.clear(detail).append(...[
        h("div", { class: "pt-row" }, h("b", null, f.name), chips),
        h("div", { class: "pt-facts" },
          lab(null, "Level:", "Pet_Skill.xml Priority: the skill-book tier. The game stores the tier's first id with rank = tier; learning tier N needs tier N-1 first (a skill book each)."), val(e.name + " (tier " + (ROMAN[t] || t) + " of " + tiers.length + ")"),
          lab(null, "Effect now:", "At this pet's rank (" + p.rank + "): the highest Restrict step not above the rank gives the absolute value. Rank rises only by merging pets."),
          val(eff ? eff.value + " (step " + eff.step + " of " + eff.steps + ", from rank " + eff.restrict + ")" : "-"),
          lab(null, "Trait need:", "The savvy the game asks for when the book is used (Agility ... Luck). A learned skill keeps working if savvy drops later."),
          h("span", null, trait.length ? trait.map(([v, st]) => h("span", { class: (Number(st && st.total) >= v ? "gw-good" : "tt-bad") + " pt-trait" }, (st ? st.stat_name : "?") + " " + v)) : val("none")),
          lab(null, "Skill book:", "The item that teaches this tier in game (ItemBaseAttribute.xml PetSkill). No book = not reachable in game (except the species' starter family)."),
          val(books.length ? books.map(x => "#" + x).join(", ") : "none", books.length || f.type === starterType ? "" : "tt-bad"),
          lab(null, "Only for:"), val(f.only || "any pet", famOk(f) ? "" : "tt-bad"),
          lab(null, "Server:"), val(e.published === false ? "not published (no effect)" : "published", e.published === false ? "tt-bad" : "")),
        h("div", { class: "pt-libtip" }),
        cur ? h("div", { class: cur.tier === t ? "gw-dim" : "gw-online warn" }, cur.tier === t ? "The pet has this level now (cell " + (cur.slot_index + 1) + ")." : "The pet has " + cur.name + " in cell " + (cur.slot_index + 1) + ": this replaces it there (one of each type).") : null,
        h("div", { class: "pt-row" }, expF,
          h("button", { class: "gw-btnx", type: "button", disabled: cur && cur.tier === t && cur.skill_id === e.id ? true : null,
            onclick: async () => { m.close(); const body = { action: "set", skill: e.id, experience: expF.value || 0 }; if (cell !== null && !cur) body.cell = cell; await skillWrite(p, body, (cur ? "set " : "learn ") + e.name); } },
            cur ? "Set to " + (ROMAN[t] || t) : cell === null ? "Learn " + e.name : "Put " + e.name + " in cell " + (cell + 1)))].filter(Boolean));
      GW.render.tooltip("petskill", e.id).then(tip => { const box = detail.querySelector && detail.querySelector(".pt-libtip"); if (box) GW.clear(box).append(tip); }).catch(() => {});
    }
    const search = h("input", { class: "pt-input", type: "search", placeholder: "search skills", "aria-label": "search skills", oninput: () => { opt.q = search.value; drawList(); } });
    const fitsBtn = h("button", { class: "gw-chipbtn on", type: "button", "data-help": "Hides other species' families and unpublished skills.",
      onclick: () => { opt.fits = !opt.fits; fitsBtn.classList.toggle("on", opt.fits); drawList(); } }, "Only skills a " + p.species_name + " can learn");
    drawList();
    if (cell !== null) { const s = p.skills.find(x => x.slot_index === cell); if (s) { const f = lib.fam.get(s.type); if (f) { sel = f.type; drawList(); showFam(f, Number(s.tier)); } } }
    const body = h("div", { class: "pt-libwrap" },
      h("div", { class: "pt-row" }, search, fitsBtn,
        h("button", { class: "gw-chipbtn", type: "button", "data-help": "The old view: every one of the 1,655 pet-skill ids in a grid.", onclick: () => { m.close(); learnById(p, cell); } }, "Every id ..."),
        h("span", { class: "gw-dim" }, "Pips: the levels; a struck-out pip has no skill book in game.")),
      h("div", { class: "pt-libcols" }, list, detail),
      h("div", { class: "pt-row pt-libfoot" }, h("button", { class: "gw-btnx", type: "button", onclick: () => m.close() }, "Close")));
    const m = modal(body, { title: (cell === null ? "Teach " : "Change cell " + (cell + 1) + " of ") + p.name + ": pet skills", full: true });
    setTimeout(() => search.focus && search.focus(), 30);
  }

  // the pet-skill library by id: every pet skill, filtered for this pet, with family / tier / Trait facts
  async function learnById(p, cell) {
    if (!await guard()) return;
    const lib = await library();
    const have = new Map(p.skills.map(s => [s.type, s]));
    const opt = { fits: true, heads: true };
    const fits = r => {
      const e = lib.byId.get(Number(r.id));
      if (!e) return !opt.fits;
      if (opt.heads && !e.is_head) return false;
      if (!opt.fits) return true;
      return e.published && (!e.species || e.species.includes(Number(p.species_id)));
    };
    const detail = h("div", { class: "pt-libdetail" }, h("p", { class: "gw-dim" }, "Click a skill to see what it does; double-click or 'Learn' to teach it."));
    const expF = GW.numberField({ field: "character_pet_skills.skill_experience", label: "Skill EXP", value: 0, min: 0, max: 2147483647, slider: false });
    const okBtn = h("button", { class: "gw-btnx", type: "button", disabled: true }, cell === null ? "Learn" : "Put in cell " + (cell + 1));
    let selId = null;
    const grid = GW.grid({ type: "petskill", filter: fits, height: "calc(100vh - 330px)",
      onSelect: r => { selId = Number(r.id); okBtn.disabled = false; showDetail(Number(r.id)); },
      onPick: r => { selId = Number(r.id); go(); } });
    async function showDetail(id) {
      const e = lib.byId.get(id) || {};
      const tip = await GW.render.tooltip("petskill", id).catch(() => h("div"));
      const same = have.get(e.type);
      const trait = (e.trait || []).map((v, i) => [v, p.stats[i]]).filter(([v]) => v > 0);
      GW.clear(detail).append(...[tip, h("div", { class: "pt-facts" },
        lab(null, "Family:"), val((e.family || "?") + " · tier " + (ROMAN[e.tier] || e.tier)),
        lab(null, "Stored as:", "The game stores the tier's first id with rank = tier; picking any rank step of a tier stores that."), val("#" + e.head + ", rank " + e.tier),
        lab(null, "Only for:"), val(e.only || "any pet", e.species && !e.species.includes(Number(p.species_id)) ? "tt-bad" : ""),
        lab(null, "Trait need:", "Pet_Skill.xml Trait: the savvy the game asks for when the book is used. A learned skill keeps working if savvy drops later; the editor does not enforce it."),
        h("span", null, trait.length ? trait.map(([v, st]) => h("span", { class: (Number(st && st.total) >= v ? "gw-good" : "tt-bad") + " pt-trait" }, (st ? st.stat_name : "?") + " " + v)) : val("none")),
        lab(null, "Server:", "Published runtime pet skills (pet_skill_curve_steps) have an effect in game."), val(e.published ? "published" : "not published (no effect)", e.published ? "" : "tt-bad")),
        same ? h("div", { class: "gw-online warn" }, "The pet has " + same.name + " (same family) in cell " + (same.slot_index + 1) + ": learning this replaces it there (one of each type).") : null,
        cell !== null && !same && p.skills.some(s => s.slot_index === cell) ? h("div", { class: "gw-dim" }, "Replaces the skill in cell " + (cell + 1) + ".") : null].filter(Boolean));
    }
    async function go() {
      if (selId === null) return;
      m.close();
      const body = { action: "set", skill: selId, experience: expF.value || 0 };
      if (cell !== null) body.cell = cell;
      await skillWrite(p, body, "learn skill");
    }
    okBtn.addEventListener("click", go);
    const tog = (key, text, help) => {
      const b = h("button", { class: "gw-chipbtn" + (opt[key] ? " on" : ""), type: "button", "data-help": help, onclick: () => { opt[key] = !opt[key]; b.classList.toggle("on", opt[key]); grid.setType("petskill", true); } }, text);
      return b;
    };
    const body = h("div", { class: "pt-libwrap" },
      h("div", { class: "pt-row" }, tog("fits", "Only skills a " + p.species_name + " can learn", "Hides skills of other species' families and unpublished ids. Turn off to see all 1,655."),
        tog("heads", "One entry per tier", "Each tier has several ids (one per rank step); the game stores the first. Turn off to list every id."),
        h("span", { class: "gw-dim" }, "Pet skills from the client's Pet_Skill.xml, English names from Message_Pet.dat.")),
      h("div", { class: "pt-libcols" }, grid.el, detail),
      h("div", { class: "pt-row pt-libfoot" }, expF, okBtn, h("button", { class: "gw-btnx", type: "button", onclick: () => m.close() }, "Cancel")));
    const m = modal(body, { title: (cell === null ? "Teach " : "Change cell " + (cell + 1) + " of ") + p.name + ": pet skill library", full: true });
    setTimeout(() => grid.focus(), 30);
  }

  // ------------------------------------------------------------------ aptitude picker (all 16 natures, GUI)
  function pickAptitude(p) {
    if (p.sealed) return;
    const d = S.data;
    const talentNames = mask => [[1, "Random Event"], [2, "Quest Dispatch"], [4, "Work"], [8, "Healing"], [16, "Merge"]].filter(([b]) => mask & b).map(([, n]) => n).join(", ") || "none";
    const tiles = d.aptitudes.map(a => h("button", { class: "pt-apttile" + (a.id === Number(p.aptitude) ? " cur" : ""), type: "button",
      onclick: () => { m.close(); setPend("f", "aptitude", p.aptitude, a.id, "nature (aptitude)", F("aptitude")); render(); } },
      h("b", { class: aptCls(a.id) }, a.id + " " + a.name),
      aptAlso(a) ? h("span", { class: "gw-dim" }, aptAlso(a)) : null,
      h("span", null, "basic savvy " + a.savvy[0] + " - " + a.savvy[1] + " in total"),
      h("span", null, "growth " + fmtg(a.growth[0]) + " - " + fmtg(a.growth[1])),
      h("span", null, "talents: " + talentNames(a.talent_mask)),
      h("span", null, a.cells + " skill cell" + (a.cells === 1 ? "" : "s") + " at hatching")));
    const m = modal(h("div", null,
      h("p", { class: "gw-dim" }, "Changing the nature rewrites, in one transaction: the innate talents (database rule), the six basic savvy values (the middle of the new bracket, split evenly), the savvy baseline and the hatch evidence. Growth stays unless the pet's growth is revealed. The server's start-up checks run before the commit."),
      h("div", { class: "pt-aptgrid" }, tiles)), { title: "Nature (aptitude) of " + p.name, wide: true });
  }

  // ------------------------------------------------------------------ add / delete pet
  async function addPet() {
    if (!await guard()) return;
    const d = S.data, ch = d.character;
    if (ch.owned_pets >= ch.pet_shed_capacity) {
      if (await GW.confirm({ title: "The pet shed is full", message: "The shed has " + ch.pet_shed_capacity + " cells and " + ch.owned_pets + " pets. Open another cell first?", ok: "Open a cell" }))
        await setShed(Math.min(8, Number(ch.pet_shed_capacity) + 1));
      return;
    }
    const st = { species: null, aptitude: 1, sex: 1, level: 1 };
    const spInfo = new Map(d.species.map(s => [s.id, s]));
    const form = h("div", { class: "pt-addform" });
    const nameIn = h("input", { class: "pt-input", type: "text", maxlength: 31, "aria-label": "pet name", "data-field": F("name"), placeholder: "the species name" });
    const lvF = GW.numberField({ field: F("level"), label: "Level", value: 1, min: 1, max: Number(d.settings.maximum_level || 120), onChange: v => { st.level = v; } });
    const okBtn = h("button", { class: "gw-btnx", type: "button", disabled: true }, "Add pet");
    function drawForm() {
      const sp = spInfo.get(st.species);
      GW.clear(form).append(
        sp ? h("div", { class: "pt-top" }, GW.cell({ sprite: st.sex ? sp.sprite_male : sp.sprite_female, name: sp.name, tipType: "pet", tipId: sp.id }),
          h("div", { class: "pt-kv" }, lab(null, "Species:"), val(sp.name + " #" + sp.id), lab(null, "Food:"), val(sp.food), lab(null, "Starter skill:"), val(sp.starter_name + " #" + sp.starter),
            lab(null, "Lifetime:"), val(sp.lifetime_values.map(GW.fmt).join(" / ")))) : h("p", { class: "gw-dim" }, "Pick a species on the left (all " + d.species.length + " published species)."),
        h("div", { class: "pt-label2" }, lab(F("aptitude"), "Nature (aptitude):")),
        h("div", { class: "pt-aptchips" }, d.aptitudes.map(a => h("button", { class: "gw-chipbtn" + (st.aptitude === a.id ? " on" : ""), type: "button", "data-help": a.name + ": basic savvy " + a.savvy.join("-") + ", growth " + a.growth.map(fmtg).join("-") + ", " + a.cells + " skill cell(s)",
          onclick: () => { st.aptitude = a.id; drawForm(); } }, h("span", { class: st.aptitude === a.id ? "" : aptCls(a.id) }, a.name)))),
        h("div", { class: "pt-row" }, lab(F("sex"), "Gender:"), h("span", { class: "pt-seg" }, [[0, "Female"], [1, "Male"]].map(([v, t]) => h("button", { class: "gw-chipbtn" + (st.sex === v ? " on" : ""), type: "button", onclick: () => { st.sex = v; drawForm(); } }, t)))),
        h("div", { class: "pt-row" }, lvF),
        h("div", { class: "pt-row" }, lab(F("name"), "Name:"), nameIn),
        h("p", { class: "gw-dim" }, "Made like the server's own hatch: published species, hatch-rank evidence, basic savvy in the middle of the nature's bracket, Weak growth, the starter skill in cell 1, " +
          "1 skill cell (2 for nature 10 and above), full energy, amity and satiety 100."),
        h("div", { class: "pt-row" }, okBtn, h("button", { class: "gw-btnx", type: "button", onclick: () => m.close() }, "Cancel"), GW.appliesChip("next_login")));
      okBtn.disabled = !sp;
    }
    const grid = GW.grid({ type: "pet", height: "calc(100vh - 300px)", onSelect: r => { st.species = Number(r.id); drawForm(); }, onPick: r => { st.species = Number(r.id); drawForm(); } });
    okBtn.addEventListener("click", async () => {
      const nm = nameIn.value.trim();
      if (nm && !/^[A-Za-z0-9_\-. ]{1,31}$/.test(nm)) { GW.alert("The name must be 1-31 letters, digits, spaces or _ - .", "Name"); return; }
      m.close();
      await write("pet/" + S.ctx.cid + "/add", { species: st.species, aptitude: st.aptitude, sex: st.sex, level: st.level || 1, name: nm || null, shed: ch.pet_shed_capacity }, "add pet");
      const last = S.data.pets.reduce((a, x) => (x.id > (a ? a.id : 0) ? x : a), null);
      if (last) { S.sel = last.id; render(); }
    });
    drawForm();
    const m = modal(h("div", { class: "pt-libcols" }, grid.el, form), { title: "Add a pet to " + ch.name, full: true });
  }
  async function deletePet(p) {
    if (!await guard()) return;
    if (!await GW.confirm({ title: "Delete " + p.name + "?", message: "Delete the pet " + p.name + " (" + p.species_name + ", Lv " + p.level + ") with its savvy and " + p.skills.length + " skill(s)? Undo in the change log puts it back.", ok: "Delete", danger: true })) return;
    await write("pet/" + S.ctx.cid + "/delete", { pet: p.id, revision: p.revision }, "delete pet " + p.name);
  }

  // ================================================================== SIMULATOR (the Pet Manager's Merge and Rebirth)
  const STATN = ["Agility", "Strength", "Accuracy", "Technique", "Wisdom", "Luck"];
  const PRESET = { min: 0, avg: 500, max: 1000 };
  function simFromPet(p) {
    const st = (p ? p.stats : []).slice().sort((a, b) => a.stat_code - b.stat_code);
    const col = k => st.length === 6 ? st.map(s => fmtg(s[k])) : ["10", "10", "10", "10", "10", "10"];
    return { pet: p ? p.id : null, species: p ? Number(p.species_id) : 1, level: p ? Number(p.level) : 30, rank: p ? fmt2(p.rank) : "0.00",
      basic: col("initial_savvy"), growth: col("base_growth_rate"), accel: col("growth_acceleration"),
      completed: p ? Number(p.completed_rebirths) : 0, contract: p ? Number(p.soul_contract_stage) : 0, left: p ? Number(p.rebirths_remaining) : 0, edited: false };
  }
  function simState() {
    if (!S.sim || (S.sim.primary.pet !== (pet() || {}).id && !S.sim.primary.edited)) {
      const p = pet(), pri = simFromPet(p);
      const old = S.sim || {};
      S.sim = { primary: pri,
        partner: old.partner || Object.assign(simFromPet(p), { level: 120, accel: ["0", "0", "0", "0", "0", "0"] }),
        merge: old.merge || { n: 10, spirits: 0, pos: [500, 500, 500, 500, 500, 500], rpos: 500 },
        rebirth: old.rebirth || { n: 1, spirits: 0, pos: [500, 500, 500, 500, 500, 500], level: 120 } };
    }
    return S.sim;
  }
  async function simContent() {
    if (!S.simC) S.simC = await S.ctx.api("sim/content");
    return S.simC;
  }
  function scheduleSim() { clearTimeout(S.simT); S.simT = setTimeout(runSim, 250); }
  const numIn = (value, label, onSet, cls) => {
    const el = h("input", { class: "pt-input pt-num " + (cls || ""), type: "text", inputmode: "decimal", value: String(value), "aria-label": label,
      oninput: () => { const v = el.value.trim(); const ok = v !== "" && isFinite(Number(v)) && Number(v) >= 0; el.classList.toggle("bad", !ok); if (ok) { onSet(v); scheduleSim(); } } });
    return el;
  };
  function slider(value, label, onSet, disabled) {
    const out = h("span", { class: "pt-slval" }, Math.round(value / 10) + "%");
    const el = h("input", { class: "pt-slider", type: "range", min: 0, max: 1000, step: 10, value: String(value), "aria-label": label, disabled: disabled ? true : null,
      "data-help": "Where in this roll's range the result lands: 0% = the minimum, 50% = the average (the expected value of the server's uniform roll), 100% = the maximum.",
      oninput: () => { onSet(Number(el.value)); out.textContent = Math.round(Number(el.value) / 10) + "%"; scheduleSim(); } });
    return h("span", { class: "pt-slwrap" }, el, out);
  }
  function petCard(title, x, partner, c) {
    const rows = STATN.map((n, i) => h("tr", null, h("td", { class: "gw-label" }, n),
      h("td", null, numIn(x.basic[i], title + " " + n + " Basic", v => { x.basic[i] = v; x.edited = true; })),
      h("td", null, numIn(x.growth[i], title + " " + n + " growth", v => { x.growth[i] = v; x.edited = true; })),
      h("td", null, numIn(x.accel[i], title + " " + n + " acceleration", v => { x.accel[i] = v; x.edited = true; })),
      h("td", { class: "gw-value pt-simadded" })));
    const spSel = h("select", { class: "pt-input", "aria-label": title + " species", onchange: () => { x.species = Number(spSel.value); x.edited = true; scheduleSim(); } },
      c.species.map(s => h("option", { value: String(s.id), selected: s.id === x.species ? true : null }, s.name + " (x" + s.factor + ")")));
    const card = h("div", { class: "gw-frame-simplewindow pt-simcard" },
      h("div", { class: "pt-sectitle" }, title),
      h("div", { class: "pt-row" }, partner ? h("label", null, "Species ", spSel) : h("span", { class: "gw-dim" }, x.pet ? "from " + ((pet() || {}).name || "the pet") + (x.edited ? " (edited here)" : "") : "no pet: type the numbers"),
        h("label", null, "Level ", numIn(x.level, title + " level", v => { x.level = Math.min(c.max_level, Math.max(1, Math.round(Number(v)))); x.edited = true; })),
        h("label", null, "Rank ", numIn(x.rank, title + " rank", v => { x.rank = v; x.edited = true; }))),
      h("table", { class: "pt-svt" }, h("tr", null, h("th"), h("th", { "data-help": "Basic Savvy (hatch + merge gains). Merge compares the pet's Basic with the partner's Basic + Added / 5." }, "Basic"),
        h("th", { "data-help": "Base growth rate (the nature's roll)." }, "Growth"), h("th", { "data-help": "Growth acceleration: the sum of all rebirth rolls (Phoenix redraws it)." }, "Accel."),
        h("th", { "data-help": "Added = level x (growth + acceleration)." }, "Added")), rows));
    card._added = rows.map(r => r.children[4]);
    const drawAdded = () => card._added.forEach((td, i) => { td.textContent = (Number(x.level) * (Number(x.growth[i]) + Number(x.accel[i]))).toFixed(2); });
    drawAdded(); card._draw = drawAdded;
    return card;
  }

  async function renderSim() {
    const box = h("div", { class: "pt-sim" }, h("p", { class: "gw-dim" }, "Loading the Pet Manager tables ..."));
    S.root.append(box);
    let c;
    try { c = await simContent(); } catch (e) { GW.clear(box).append(h("div", { class: "tt-bad" }, e.message)); return; }
    const st = simState(), d = S.data, p = pet();
    const pick = h("select", { class: "pt-input", "aria-label": "pet to simulate", onchange: () => { S.sel = Number(pick.value); S.sim.primary = simFromPet(pet()); render(); } },
      d.pets.map(x => h("option", { value: String(x.id), selected: p && x.id === p.id ? true : null }, x.name + " · " + x.species_name + " Lv " + x.level)));
    const priCard = petCard("This pet", st.primary, false, c);
    const parCard = petCard("Fake partner (merged away)", st.partner, true, c);
    const mg = st.merge, rb = st.rebirth;
    const spChips = (o, key) => h("span", { class: "pt-seg" }, [0, 1, 2, 3, 4, 5].map(n => h("button", { class: "gw-chipbtn" + (o.spirits === n ? " on" : ""), type: "button",
      onclick: () => { o.spirits = n; render(); } }, String(n))));
    const presets = (o, withRank) => h("span", { class: "pt-seg" }, Object.entries(PRESET).map(([k, v]) => h("button", { class: "gw-chipbtn", type: "button",
      onclick: () => { o.pos = o.pos.map(() => v); if (withRank) o.rpos = v; render(); } }, k === "min" ? "Min" : k === "avg" ? "Avg" : "Max")));
    const mergeCtl = h("div", { class: "gw-frame-simplewindow pt-simcard" },
      h("div", { class: "pt-sectitle", "data-help": "Pet Manager > Merge (client: Inosculate). Both pets level " + c.merge_min_level + "+; the partner is consumed. Each merge rolls each stat separately; gains shrink as the pet's Basic catches up with the partner." }, "Merges"),
      h("div", { class: "pt-row" }, h("label", null, "How many ", numIn(mg.n, "number of merges", v => { mg.n = Math.min(1000, Math.max(0, Math.round(Number(v)))); })),
        h("span", { "data-help": "Merged Spirit (#" + c.items.merge_spirit + ") or, for a bound pet, Fused Harpyia (#" + c.items.merge_spirit_bound + "), 0-5 per merge. They raise each roll's minimum to 10-50% of the maximum." }, "Merged Spirits "), spChips(mg)),
      h("div", { class: "pt-row" }, h("span", { class: "gw-label" }, "Rolls land at:"), presets(mg, true)),
      h("div", { class: "pt-sliders" }, STATN.map((n, i) => h("label", { class: "pt-slrow" }, h("span", null, n), slider(mg.pos[i], n + " merge roll", v => { mg.pos[i] = v; }))),
        h("label", { class: "pt-slrow" }, h("span", null, "Rank"), slider(mg.rpos, "rank merge roll", v => { mg.rpos = v; }, mg.spirits === 0)),
        mg.spirits === 0 ? h("span", { class: "gw-dim pt-note" }, "With no spirit the rank gain is one fixed value (no range).") : null));
    const rebCtl = h("div", { class: "gw-frame-simplewindow pt-simcard" },
      h("div", { class: "pt-sectitle", "data-help": "Pet Manager > Rebirth (client: Resurge). Needs a Soul Contract and a rebirth attempt (water); the pet goes back to level 1 and every stat's growth acceleration rises by the roll. Rank and Basic stay." }, "Rebirths"),
      h("div", { class: "pt-row" }, h("label", null, "How many ", numIn(rb.n, "number of rebirths", v => { rb.n = Math.min(100, Math.max(0, Math.round(Number(v)))); })),
        h("span", { "data-help": "Rebirth Spirit (#" + c.items.rebirth_spirit + ") or, for a bound pet, Reborn Harpyia (#" + c.items.rebirth_spirit_bound + "), 0-5: the roll's minimum per stat is 0.01 / 0.02 / 0.04 / 0.06 / 0.08 / 0.10, the maximum always 0.20." }, "Rebirth Spirits "), spChips(rb)),
      h("div", { class: "pt-row" }, h("label", null, "Level afterwards ", numIn(rb.level, "level after the rebirths", v => { rb.level = Math.min(c.max_level, Math.max(1, Math.round(Number(v)))); }))),
      h("div", { class: "pt-row" }, h("span", { class: "gw-label" }, "Rolls land at:"), presets(rb, false)),
      h("div", { class: "pt-sliders" }, STATN.map((n, i) => h("label", { class: "pt-slrow" }, h("span", null, n), slider(rb.pos[i], n + " rebirth roll", v => { rb.pos[i] = v; })))));
    S.simOut = { merge: h("div", { class: "pt-simres" }), rebirth: h("div", { class: "pt-simres" }), cards: [priCard, parCard] };
    const canApply = p && !blocked() && !p.sealed;
    const apply = (what) => h("button", { class: "gw-btnx", type: "button", disabled: canApply ? null : true,
      "data-help": canApply ? "Write the result to " + p.name + ". The server re-runs the same simulation from the pet's SAVED values (not numbers edited here) and writes it in one transaction with the start-up checks; Undo in the change log puts it back." : (blocked() || "Pick a pet first."),
      onclick: () => applySim(what) }, what === "merge" ? "Apply the merges to " + (p ? p.name : "the pet") : what === "rebirth" ? "Apply the rebirths" : "Apply both");
    GW.clear(box).append(
      h("div", { class: "pt-row" }, h("label", null, "Pet ", pick), h("span", { class: "gw-dim" }, "Tables: " + c.source + ". The most one merge adds: " + c.ceiling.savvy + " per stat, " + c.ceiling.rank + " rank.")),
      h("div", { class: "pt-simcols" },
        h("div", { class: "pt-simin" }, priCard, parCard),
        h("div", { class: "pt-simin" }, mergeCtl, S.simOut.merge, rebCtl, S.simOut.rebirth,
          h("div", { class: "pt-row" }, apply("merge"), apply("rebirth"), apply("both"), st.primary.edited ? h("span", { class: "pt-legalwarn" }, "You edited this pet's numbers here: Apply uses its saved values.") : null))),
      planCard(c, st));
    runSim();
  }

  async function runSim() {
    if (!S || S.tab !== "sim" || !S.simOut) return;
    const st = S.sim, out = S.simOut, seq = (S.simSeq = (S.simSeq || 0) + 1);
    out.cards.forEach(cd => cd._draw && cd._draw());
    const pri = st.primary, par = st.partner;
    const mBody = { primary: { basic: pri.basic, rank: pri.rank, level: pri.level },
      partner: { species: par.species, basic: par.basic, rank: par.rank, level: par.level, growth: par.growth, accel: par.accel },
      merges: st.merge.n, spirits: st.merge.spirits, savvy_pos: st.merge.pos, rank_pos: st.merge.rpos };
    const rBody = { pet: { completed: pri.completed, growth: pri.growth, accel: pri.accel, soul_contract_stage: pri.contract, rebirths_remaining: pri.left },
      rebirths: st.rebirth.n, spirits: st.rebirth.spirits, pos: st.rebirth.pos, level: st.rebirth.level };
    const [m, r] = await Promise.all([S.ctx.api("sim/merge", mBody).catch(e => ({ error: e.message })), S.ctx.api("sim/rebirth", rBody).catch(e => ({ error: e.message }))]);
    if (!S || seq !== S.simSeq) return;
    drawMerge(out.merge, m); drawRebirth(out.rebirth, r, pri);
    if (out.plan) {
      const pBody = { primary: { basic: pri.basic, rank: pri.rank, level: pri.level, growth: pri.growth, accel: pri.accel, completed: pri.completed },
        partner: mBody.partner, merges: Math.min(300, st.merge.n), spirits: st.merge.spirits, rebirths: Math.min(st.rebirth.n, 100 - Number(pri.completed || 0)),
        rebirth_spirits: st.rebirth.spirits, level: st.rebirth.level, rebirth_pos: Math.round(st.rebirth.pos.reduce((a, b) => a + b, 0) / 6) };
      const pl = await S.ctx.api("sim/plan", pBody).catch(e => ({ error: e.message }));
      if (!S || seq !== S.simSeq) return;
      drawPlan(out.plan, pl);
    }
  }

  // ---- B21 item 23: merge-count planner (made-up partner, exact reachable range per merge count, rebirth gains)
  function planCard(c, st) {
    const apts = (S.data && S.data.aptitudes) || [];
    const fx = S.fx || (S.fx = { species: st.partner.species, aptitude: apts.length ? apts[apts.length - 1].id : 1, level: 120, quality: "mid", rebirths: 0 });
    const spSel = h("select", { class: "pt-input", "aria-label": "made-up partner species", onchange: () => { fx.species = Number(spSel.value); } },
      c.species.map(s => h("option", { value: String(s.id), selected: s.id === fx.species ? true : null }, s.name + " (x" + s.factor + ")")));
    const apSel = h("select", { class: "pt-input", "aria-label": "made-up partner nature", onchange: () => { fx.aptitude = Number(apSel.value); } },
      apts.map(a => h("option", { value: String(a.id), selected: a.id === fx.aptitude ? true : null }, a.name + " (Basic " + a.savvy.join("-") + ", growth " + a.growth.join("-") + ")")));
    const qSel = h("select", { class: "pt-input", "aria-label": "made-up partner hatch quality", onchange: () => { fx.quality = qSel.value; } },
      [["min", "Worst hatch"], ["mid", "Middle hatch"], ["max", "Best hatch"]].map(([v, t]) => h("option", { value: v, selected: v === fx.quality ? true : null }, t)));
    const lv = h("input", { class: "pt-input pt-num", type: "number", min: 1, max: c.max_level, value: String(fx.level), "aria-label": "made-up partner level", onchange: () => { fx.level = Number(lv.value); } });
    const rbIn = h("input", { class: "pt-input pt-num", type: "number", min: 0, max: c.max_rebirths, value: String(fx.rebirths), "aria-label": "made-up partner rebirths", onchange: () => { fx.rebirths = Number(rbIn.value); } });
    const make = h("button", { class: "gw-btnx", type: "button", "data-help": "Fill the partner card with a made-up pet hatched the way the server hatches one: Basic and growth in the middle (or at an edge) of the nature's ranges, split evenly over the six stats, and one of the nature's hatch ranks. Rebirths add the average roll (0.11 per stat).",
      onclick: async () => {
        try {
          const f = await S.ctx.api("sim/fictitious", fx);
          Object.assign(st.partner, { species: f.species, level: f.level, rank: f.rank, basic: f.basic, growth: f.growth, accel: f.accel, edited: true });
          render();
        } catch (e) { GW.alert(e.message, "Made-up partner"); }
      } }, "Use as partner");
    S.simOut.plan = h("div", { class: "pt-simres" }, h("p", { class: "gw-dim" }, "Planning ..."));
    return h("div", { class: "gw-frame-simplewindow pt-simcard pt-plan" },
      h("div", { class: "pt-sectitle", "data-help": "What the pet looks like after 0, 1, 2 ... merges with the partner above (same partner every time), using the merge and rebirth settings above.\n• Range: the lowest and highest result any luck can give (every roll combination).\n• Average: every roll at its middle.\n~ Gains shrink by themselves: each merge closes the gap between the pet and the partner that the next merge feeds on." }, "Merge-count planner"),
      h("div", { class: "pt-row" }, h("span", { class: "gw-label" }, "Made-up partner:"), h("label", null, "Species ", spSel), h("label", null, "Nature ", apSel), qSel,
        h("label", null, "Level ", lv), h("label", null, "Rebirths ", rbIn), make),
      S.simOut.plan);
  }
  function drawPlan(box, p) {
    GW.clear(box);
    if (p.error) { box.append(h("div", { class: "tt-bad" }, p.error)); return; }
    const rows = p.merge.rows, n = rows.length - 1;
    const pickRows = n <= 30 ? rows : rows.filter(r => r.n <= 10 || r.n % 10 === 0 || r.n === n);
    box.append(planChart(rows),
      h("table", { class: "pt-svt pt-plantab" },
        h("tr", null, h("th", null, "Merges"), h("th", { "data-help": "Lowest - highest Basic total any luck can give after this many merges." }, "Basic total (range)"),
          h("th", { "data-help": "Every roll at its middle." }, "Average"), h("th", { "data-help": "What the average merge at this count adds (all six stats) and that as a share of the first merge's gain." }, "This merge adds"),
          h("th", null, "Rank (range)"), h("th", null, "Rank avg."), h("th", { "data-help": "Basic + Added at level " + p.merge.level + " (Added does not change with merges)." }, "Shown savvy at Lv " + p.merge.level)),
        pickRows.map(r => h("tr", null, h("td", null, String(r.n)), h("td", null, r.range.basic_total.join(" - ")), h("td", { class: "gw-value" }, r.avg.basic_total),
          h("td", null, r.n ? "+" + r.gain + (r.gain_share !== null ? " (" + r.gain_share + "%)" : "") : "-"),
          h("td", null, r.range.rank.join(" - ")), h("td", null, r.avg.rank), h("td", null, r.visible_total)))));
    const rr = p.rebirth.rows;
    if (rr.length > 1) box.append(h("table", { class: "pt-svt pt-plantab" },
      h("tr", null, h("th", null, "Rebirths"), h("th", null, "Needs Lv"), h("th", null, "Attempt item"), h("th", { "data-help": "Sum of the six stats' Added at level " + p.rebirth.level + "." }, "Added total"),
        h("th", { "data-help": "Each rebirth adds the same points (one roll of " + p.rebirth.range.join(" - ") + " per stat, here " + p.rebirth.per_rebirth + "); the share of what the pet already had falls after the first." }, "Gain (share)")),
      rr.map(r => h("tr", null, h("td", null, String(r.n) + (r.model_change ? " (new model)" : "")), h("td", null, r.level_needed ? String(r.level_needed) : "-"),
        h("td", null, r.water ? r.water.name : "-"), h("td", { class: "gw-value" }, r.added_total), h("td", null, r.gain === null ? "-" : "+" + r.gain + " (" + r.gain_share + "%)")))));
    box.append(h("div", { class: "gw-dim" }, "End: Basic " + p.end.basic.join(" / ") + " + Added " + p.end.added.join(" / ") + " = shown " + p.end.visible_total + " at Lv " + p.end.level + "; rank " + p.end.rank + " (" + p.end.rank_range.join(" - ") + ")."),
      ...(p.merge.notes || []).map(x => h("div", { class: "pt-legalwarn" }, x)));
  }
  function planChart(rows) {
    const W = 560, H = 150, P = 28, ns = "http://www.w3.org/2000/svg";
    const lo = rows.map(r => Number(r.range.basic_total[0])), hi = rows.map(r => Number(r.range.basic_total[1])), av = rows.map(r => Number(r.avg.basic_total));
    const y0 = Math.min(...lo), y1 = Math.max(...hi, y0 + 1), n = Math.max(1, rows.length - 1);
    const X = i => P + (W - 2 * P) * i / n, Y = v => H - P + 8 - (H - P) * (v - y0) / (y1 - y0);
    const svg = document.createElementNS(ns, "svg");
    svg.setAttribute("viewBox", "0 0 " + W + " " + H); svg.setAttribute("class", "pt-planchart"); svg.setAttribute("role", "img");
    svg.setAttribute("aria-label", "Basic total by merge count: range band and average line");
    const el = (t, a) => { const e = document.createElementNS(ns, t); Object.entries(a).forEach(([k, v]) => e.setAttribute(k, v)); svg.append(e); return e; };
    el("polygon", { points: lo.map((v, i) => X(i) + "," + Y(v)).concat(hi.map((v, i) => X(i) + "," + Y(v)).reverse()).join(" "), class: "pt-band" });
    el("polyline", { points: av.map((v, i) => X(i) + "," + Y(v)).join(" "), class: "pt-line" });
    el("text", { x: P, y: 12, class: "pt-axis" }).textContent = "Basic total " + y0.toFixed(2) + " - " + y1.toFixed(2);
    el("text", { x: W - P, y: H - 4, class: "pt-axis", "text-anchor": "end" }).textContent = n + " merges";
    return svg;
  }
  function drawMerge(box, m) {
    GW.clear(box);
    if (m.error) { box.append(h("div", { class: "tt-bad" }, m.error)); return; }
    const first = m.steps[0];
    box.append(h("table", { class: "pt-svt" },
      h("tr", null, h("th"), h("th", null, "Basic now"), h("th", { "data-help": "The first merge's roll range for this stat (min - max), then what the slider picks." }, "1st merge"), h("th", null, "Gained"), h("th", null, "Basic after")),
      STATN.map((n, i) => h("tr", null, h("td", { class: "gw-label" }, n), h("td", null, m.start_basic[i]),
        h("td", { class: first && Number(first.bounds[i][1]) === 0 ? "tt-bad" : "" }, first ? (Number(first.bounds[i][1]) === 0 ? "nothing (need " + first.need[i] + " more)" : first.bounds[i].join(" - ")) : "-"),
        h("td", { class: "gw-good" }, "+" + m.gain[i]), h("td", { class: "gw-value pt-tot" }, m.basic[i]))),
      h("tr", null, h("td", { class: "gw-label" }, "Rank"), h("td", null, m.start_rank), h("td", null, first ? first.rank_bounds.join(" - ") : "-"), h("td", { class: "gw-good" }, "+" + m.rank_gain), h("td", { class: "gw-value pt-tot" }, m.rank))),
      ...m.notes.map(x => h("div", { class: "pt-legalwarn" }, x)),
      m.steps.length > 1 ? h("details", { class: "pt-simsteps" }, h("summary", null, "Every merge (" + m.steps.length + ")"),
        h("table", { class: "pt-svt" }, h("tr", null, h("th", null, "#"), STATN.map(n => h("th", null, n.slice(0, 3))), h("th", null, "Rank")),
          m.steps.slice(0, 200).map(s => h("tr", null, h("td", null, String(s.n)), s.gains.map((g, i) => h("td", { "data-help": "range " + s.bounds[i].join(" - ") }, "+" + g)), h("td", null, "+" + s.rank_gain))))) : null);
  }
  function drawRebirth(box, r, pri) {
    GW.clear(box);
    if (r.error) { box.append(h("div", { class: "tt-bad" }, r.error)); return; }
    box.append(h("table", { class: "pt-svt" },
      h("tr", null, h("th"), h("th", null, "Accel. now"), h("th", null, "Accel. after"), h("th", { "data-help": "growth + acceleration" }, "Effective growth"), h("th", null, "Added at Lv " + r.level)),
      STATN.map((n, i) => h("tr", null, h("td", { class: "gw-label" }, n), h("td", null, fmt2(pri.accel[i])), h("td", { class: "gw-good" }, r.accel[i]),
        h("td", null, (Number(pri.growth[i]) + Number(r.accel[i])).toFixed(2)), h("td", { class: "gw-value pt-tot" }, r.added_at_level[i])))),
      h("div", { class: "gw-dim" }, "Each rebirth: +" + r.range.join(" to +") + " per stat. After " + r.completed + " rebirth(s) a Phoenix reset would redraw the acceleration at " + r.phoenix.join(" - ") + "."),
      ...r.notes.map(x => h("div", { class: "pt-legalwarn" }, x)),
      r.steps.length ? h("details", { class: "pt-simsteps" }, h("summary", null, "Every rebirth (" + r.steps.length + "): level gate, water, model"),
        h("table", { class: "pt-svt" }, h("tr", null, h("th", null, "#"), h("th", null, "Needs Lv"), h("th", null, "Attempt item"), STATN.map(n => h("th", null, n.slice(0, 3)))),
          r.steps.map(s => h("tr", null, h("td", null, String(s.n) + (s.model_change ? " (new model)" : "")), h("td", null, String(s.level_needed)), h("td", null, s.water ? s.water.name : "-"),
            s.increase.map(x => h("td", null, "+" + x)))))) : null);
  }
  async function applySim(what) {
    const p = pet(), st = S.sim;
    if (!p || !await guard()) return;
    const par = st.partner;
    const body = { pet: p.id, revision: p.revision };
    if (what !== "rebirth" && st.merge.n > 0) body.merge = { partner: { species: par.species, basic: par.basic, rank: par.rank, level: par.level, growth: par.growth, accel: par.accel },
      merges: st.merge.n, spirits: st.merge.spirits, savvy_pos: st.merge.pos, rank_pos: st.merge.rpos };
    if (what !== "merge" && st.rebirth.n > 0) body.rebirth = { rebirths: st.rebirth.n, spirits: st.rebirth.spirits, pos: st.rebirth.pos, level: st.rebirth.level };
    if (!body.merge && !body.rebirth) { GW.alert("Set how many merges or rebirths first.", "Nothing to apply"); return; }
    const parts = [body.merge ? st.merge.n + " merge(s) with the fake partner (" + st.merge.spirits + " spirit(s))" : "", body.rebirth ? st.rebirth.n + " rebirth(s) (" + st.rebirth.spirits + " spirit(s)), ending at level " + st.rebirth.level : ""].filter(Boolean);
    if (!await GW.confirm({ title: "Apply to " + p.name + "?", message: "Write the simulated " + parts.join(" and ") + " to " + p.name + ": Basic savvy, rank, merge / rebirth counts, rebirth attempts, growth acceleration, level and Added. Undo in the change log puts it back.", ok: "Apply" })) return;
    S.sim.primary.edited = false;
    await write("pet/" + S.ctx.cid + "/apply-sim", body, "simulated " + parts.join(" + "));
    S.sim.primary = simFromPet(pet());
    render();
  }

  // ================================================================== MOUNTS
  function itemInst(m) { return { item_quality: m.quality, item_grade: m.grade, bound: m.bound, stack: m.stack, attrs: (m.attrs || []).map(a => ({ id: a.id, level: a.level })) }; }
  function mountCell(m, slot, opts) {
    const names = S.data.mounts.slot_names;
    if (m) return GW.cell({ sprite: m.sprite, name: m.name, quality: m.quality, tipType: "item", tipId: m.prop_id, tipInst: itemInst(m), label: names[slot] + ": " + m.name,
      class: m.row === S.msel ? "pt-on" : "", onclick: () => { S.msel = m.row; render(); } });
    return GW.cell({ empty: true, locked: opts && opts.locked, label: names[slot] + ": empty", help: (opts && opts.help) || ("Empty " + names[slot] + " slot. Click to put a new item there."), onclick: opts && opts.onclick });
  }
  function renderMounts() {
    const d = S.data, mo = d.mounts, ride = mo.ride, live = d.live;
    const gear = h("div", { class: "pt-gearrow" });
    for (const s of ["15", "16", "17", "18", "19"]) {
      const g = mo.gear[s];
      gear.append(h("div", { class: "pt-gearcell" }, mountCell(g, s, { locked: !ride, help: ride ? "Empty " + mo.slot_names[s] + " slot: click to put new mount gear there (level at most the mount's)." : "Mount gear needs a mount in the riding slot first.",
        onclick: ride ? () => addMount("gear", Number(s)) : null }), h("span", { class: "pt-cap" }, mo.slot_names[s])));
    }
    const left = h("section", { class: "gw-frame-normalwindow gw-win pt-mountwin" }, h("div", { class: "gw-wtitle" }, "Mount"),
      h("div", { class: "pt-ride" }, h("div", { class: "pt-ridecell" }, mountCell(ride, "20", { help: "The riding slot (equipment slot 20) holds the active mount. Click to put a new mount there.", onclick: () => addMount("ride") }),
        h("span", { class: "pt-cap" }, "Riding"))),
      gear,
      h("div", { class: "pt-row" },
        h("button", { class: "gw-btnx", type: "button", "data-help": live.bag_add && online() ? "The player is online and the game server gives the mount at once (live)." : "Adds the mount to the first free bag cell." + (online() ? " The player is online and this server has no live item edits: log them out first." : ""),
          onclick: () => addMount("bag") }, "+ Mount to bag"),
        GW.appliesChip(live.bag_add && online() ? "live" : "next_login", live.bag_add ? (online() ? "applied now (live)" : "applies at the next login (live when online)") : null),
        h("button", { class: "gw-btnx", type: "button", onclick: () => addMount("ride") }, "+ New mount to ride")),
      h("p", { class: "gw-dim pt-note" }, "Riding needs character level >= mount level >= gear level. The Ride skill (4904) uses the mount in the riding slot."));
    const list = h("div", { class: "pt-mlist", role: "list" });
    for (const m of mo.items) {
      list.append(h("button", { class: "pt-mrow" + (m.row === S.msel ? " on" : ""), type: "button", role: "listitem", onclick: () => { S.msel = m.row; render(); } },
        GW.cell({ sprite: m.sprite, name: m.name, quality: m.quality, tipType: "item", tipId: m.prop_id, tipInst: itemInst(m) }),
        h("span", { class: "pt-mname" }, h("b", { style: "color:" + GW.qColor(m.quality) }, m.name), h("span", { class: "gw-dim" }, " #" + m.prop_id)),
        h("span", { class: "pt-msub" }, m.where + " · " + m.quality_name + " Q" + m.quality + " · G" + m.grade + (m.bound ? " · bound" : "") + (m.speed !== null && m.speed !== undefined ? " · speed +" + Math.round(Number(m.speed) * 100) + "%" : "") + (m.minlv ? " · Lv " + m.minlv : ""))));
    }
    if (!mo.items.length) list.append(h("p", { class: "gw-dim" }, "No mounts or mount gear. Add one with the buttons on the left."));
    const sel = mo.items.find(m => m.row === S.msel);
    const right = h("section", { class: "gw-frame-normalwindow gw-win pt-mounts" }, h("div", { class: "gw-wtitle" }, "Owned mounts and gear"), list);
    const wrap = h("div", { class: "pt-cols" }, left, right);
    if (sel) wrap.append(mountEditor(sel));
    S.root.append(wrap);
  }

  function qualityChips(cur, onPick) {
    return h("div", { class: "pt-qchips" }, Array.from({ length: 21 }, (_, q) => h("button", { class: "gw-chipbtn pt-q" + (q === cur ? " on" : "") + (q > 20 ? " pt-over" : ""), type: "button",
      style: q === cur ? null : "color:" + GW.qColor(q), "data-help": "Q" + q + " " + GW.QUALITY_NAMES[q] + (q > 20 ? " - above this client's Q20: the client has no numbers for it and may crash drawing the item (the server extends mounts to Q20)." : "") + (q === 0 ? " - no quality (consumables)." : ""),
      onclick: () => onPick(q) }, "Q" + q)));
  }

  // The Q20 client has data up to Q20 / G25 (POINTS-AUDIT CL2, CL20; reborn docs/mount-quality-grade.md: the
  // client's item detail path can read past its data, upstream reports a missing stat or a crash on entering the scene)
  const capText = st => [st.quality > 20 ? "quality Q" + st.quality + " is above the client's Q20" : "", st.grade > 25 ? "grade G" + st.grade + " is above the client's G25" : ""].filter(Boolean).join(" and ");
  function capWarn(st) {
    const t = capText(st);
    return t ? h("p", { class: "tt-bad pt-capwarn" }, "Client limit: " + t + ". The server accepts it, but this client has no numbers for it and may show wrong stats or crash when it draws the item. Stay at Q10 / G12 unless the client was patched.") : null;
  }
  async function capOk(st) {
    const t = capText(st);
    return !t || GW.confirm({ title: "Above the client's limit", message: "The " + t + ". The game server accepts it, but this client has no data for it and may show wrong stats or crash when it draws the item. Save anyway?", ok: "Save anyway", danger: true });
  }

  function mountEditor(m) {
    const d = S.data;
    const st = { quality: m.quality, grade: m.grade, bound: m.bound };
    const prev = h("div", { class: "pt-mprev" });
    async function preview() {
      const t = await GW.render.tooltip("item", m.prop_id, Object.assign(itemInst(m), { item_quality: st.quality, item_grade: st.grade, bound: st.bound })).catch(e => h("div", { class: "tt-bad" }, e.message));
      GW.clear(prev).append(t);
    }
    const qBox = h("div"), warnBox = h("div");
    const drawW = () => { GW.clear(warnBox); const w = capWarn(st); if (w) warnBox.append(w); };
    const drawQ = () => { GW.clear(qBox).append(qualityChips(st.quality, q => { st.quality = q; drawQ(); preview(); dirty(); })); drawW(); };
    const gF = GW.numberField({ field: "character_items.item_grade", label: "Grade", value: m.grade, min: 0, max: 25, onChange: (v, ok) => { if (ok && v !== null) { st.grade = v; preview(); dirty(); drawW(); } } });
    const bBtn = h("button", { class: "gw-sprite gw-check" + (st.bound ? " on" : ""), type: "button", role: "checkbox", "aria-label": "bound", "data-field": "character_items.bound",
      onclick: () => { st.bound = st.bound ? 0 : 1; bBtn.classList.toggle("on", !!st.bound); preview(); dirty(); } });
    const save = h("button", { class: "gw-btnx", type: "button", disabled: true }, "Save quality / grade");
    const dirty = () => { save.disabled = st.quality === m.quality && st.grade === m.grade && st.bound === m.bound; };
    save.addEventListener("click", async () => {
      if (!await mountGuard(false)) return;
      if (!await capOk(st)) return;
      await write("mount/" + S.ctx.cid + "/set", { row: m.row, quality: st.quality, grade: st.grade, bound: st.bound, expected: m.expected }, "mount quality");
    });
    const isMount = m.kind === "mount";
    const equipped = m.location === 0;
    const actions = h("div", { class: "pt-row" },
      isMount && !(equipped && m.slot === 20) ? h("button", { class: "gw-btnx", type: "button", "data-help": "Make this the active mount: it moves into the riding slot and the current one takes its place.",
        onclick: async () => { if (await mountGuard(false)) await write("mount/" + S.ctx.cid + "/ride", { row: m.row, expected: m.expected }, "ride " + m.name); } }, "Ride this one") : null,
      equipped ? h("button", { class: "gw-btnx", type: "button", "data-help": "Move it to the first free bag cell (a mount only when no gear is equipped).",
        onclick: async () => { if (await mountGuard(false)) await write("mount/" + S.ctx.cid + "/unequip", { row: m.row, expected: m.expected }, "unequip " + m.name); } }, "Unequip") : null,
      h("button", { class: "gw-btnx danger", type: "button", "data-help": m.location === 1 ? "Delete it from the bag (at once when the player is online and the game server supports it)." : "Delete it (database; the character must be logged out).",
        onclick: async () => {
          if (!await mountGuard(m.location === 1)) return;
          if (!await GW.confirm({ title: "Delete " + m.name + "?", message: "Delete " + m.name + " (" + m.where + ")?" + (m.location === 1 && online() ? " The game server removes it from the player's bag now; a live change has no automatic Undo." : " Undo in the change log puts it back."), ok: "Delete", danger: true })) return;
          await write("mount/" + S.ctx.cid + "/delete", { row: m.row, expected: m.expected }, "delete " + m.name);
        } }, "Delete"));
    drawQ(); preview();
    return h("section", { class: "gw-frame-normalwindow gw-win pt-medit" }, h("div", { class: "gw-wtitle" }, m.name),
      h("div", { class: "pt-medcols" }, prev,
        h("div", null,
          h("div", { class: "pt-facts" }, lab(null, "Where:"), val(m.where), lab(null, "Kind:"), val(d.mounts.slot_names[String(d.mounts.kinds[m.kind])] || m.kind),
            lab(null, "Level:", "Item level: the character needs at least this level to ride (mounts); gear needs a mount of at least its level."), val(m.minlv ? String(m.minlv) : "-")),
          h("div", { class: "pt-label2" }, lab("character_items.item_quality", "Quality:", "Mount quality picks the speed / HP value (Q1-Q10 from the client, Q11-Q20 the server's extension).")),
          qBox, h("div", { class: "pt-row" }, gF), warnBox, h("div", { class: "pt-row" }, lab("character_items.bound", "Bound:"), bBtn),
          h("div", { class: "pt-row" }, save), actions)));
  }

  async function mountGuard(bagOnly) {
    if (!S.data.writable) { await GW.alert("This editor is read-only for database " + S.data.database + ".", "Cannot edit now"); return false; }
    if (online() && !(bagOnly && S.data.live.bag_remove)) {
      await GW.alert(S.data.character.name + " is online. " + (S.data.live.available ? "The game server can only add or remove kit-bag items live; this edit needs the character logged out." +
        (S.data.live.kick ? " Use 'Kick to edit everything' on the banner." : "") : "Live edits arrive with the round-2 server update; this character must be logged out."), "Cannot edit now");
      return false;
    }
    return true;
  }

  async function addMount(to, gearSlot) {
    const d = S.data, mo = d.mounts;
    if (to !== "bag" && !await mountGuard(false)) return;
    if (to === "bag" && online() && !d.live.bag_add) { await mountGuard(false); return; }
    let kindFilter = null, group = "Mount", title = to === "bag" ? "Pick a mount or mount gear (every one the client and server know)" : "Pick a mount (every one the client and server know)";
    if (to === "gear") {
      const kind = Object.keys(mo.kinds).find(k => mo.kinds[k] === gearSlot);
      kindFilter = r => r.key === kind;
      group = "Mount gear"; title = "Pick a " + mo.slot_names[String(gearSlot)] + " (mount gear)";
    }
    const id = await GW.pick("item", { title, group, filter: kindFilter || (to === "bag" ? (r => r.key in mo.kinds) : (r => r.key === "mount")) });
    if (id === null || id === "") return;
    const e = await GW.catalog.get("item", id).catch(() => ({}));
    const st = { quality: 1, grade: 1, bound: 0 };
    const prev = h("div", { class: "pt-mprev" });
    const preview = async () => GW.clear(prev).append(await GW.render.tooltip("item", id, { item_quality: st.quality, item_grade: st.grade, bound: st.bound, stack: 1 }).catch(() => h("div")));
    const qBox = h("div"), warnBox = h("div");
    const drawW = () => { GW.clear(warnBox); const w = capWarn(st); if (w) warnBox.append(w); };
    const drawQ = () => { GW.clear(qBox).append(qualityChips(st.quality, q => { st.quality = q; drawQ(); preview(); })); drawW(); };
    const gF = GW.numberField({ field: "character_items.item_grade", label: "Grade", value: 1, min: 0, max: 25, onChange: (v, ok) => { if (ok && v !== null) { st.grade = v; preview(); drawW(); } } });
    gF.classList.add("pt-quiet");
    const bBtn = h("button", { class: "gw-sprite gw-check", type: "button", role: "checkbox", "aria-label": "bound", onclick: () => { st.bound = st.bound ? 0 : 1; bBtn.classList.toggle("on", !!st.bound); preview(); } });
    const where = to === "bag" ? "the first free bag cell" : to === "ride" ? "the riding slot" + (mo.ride ? " (the current " + mo.ride.name + " moves to the bag)" : "") : "the " + mo.slot_names[String(gearSlot)] + " slot";
    const liveNow = to === "bag" && online() && d.live.bag_add;
    const ok = h("button", { class: "gw-btnx", type: "button", onclick: async () => {
      if (!await capOk(st)) return;
      m.close();
      await write("mount/" + S.ctx.cid + "/add", { item: Number(id), quality: st.quality, grade: st.grade, bound: st.bound, to }, "add " + (e.name || id));
    } }, "Add");
    drawQ(); preview();
    const m = modal(h("div", { class: "pt-medcols" }, prev, h("div", null,
      h("p", null, "Put a new ", h("b", null, e.name || ("#" + id)), " in " + where + "."),
      h("div", { class: "pt-label2" }, lab("character_items.item_quality", "Quality:")), qBox, h("div", { class: "pt-row" }, gF), warnBox,
      h("div", { class: "pt-row" }, lab("character_items.bound", "Bound:"), bBtn),
      h("div", { class: "pt-row" }, ok, h("button", { class: "gw-btnx", type: "button", onclick: () => m.close() }, "Cancel"),
        GW.appliesChip(liveNow ? "live" : "next_login")),
      liveNow ? h("p", { class: "gw-good" }, "The player is online: the game server puts it in their bag now.") : null)), { title: "New " + (to === "gear" ? "mount gear" : "mount"), wide: true });
  }

  // ================================================================== module
  GW.module("pets", {
    async mount(el, ctx) {
      S = { ctx, el, tab: (ctx.route[0] === "mounts" ? "mounts" : ctx.route[0] === "sim" ? "sim" : "pets"), sel: null, msel: null, lib: null, offs: [] };
      S.bar = GW.saveBar({ title: "Save these pet changes?", onSave: savePending, onDiscard: () => { resetPending(); render(); } });
      resetPending();
      let was = null;
      const banner = GW.onlineBanner(ctx.cid, { onState: st => { if (was !== null && was !== !!st.online && S.data) load(true); was = !!st.online; } });
      S.root = h("div", { class: "pt-root" }, h("p", { class: "gw-dim" }, "Loading the pets and mounts of this character ..."));
      el.append(banner, S.root);
      S.offs.push(GW.on("change", c => { if (S && c && (c.cid === ctx.cid || c.character === (S.data && S.data.character.name))) load(true); }));
      try { await load(false); }
      catch (e) { S.root.append(h("div", { class: "tt-bad" }, e.message)); }
    },
    unmount() { if (S) { clearTimeout(S.legalT); clearTimeout(S.simT); for (const f of S.offs) try { f(); } catch (e) { /* gone */ } } S = null; },
  });
})();
