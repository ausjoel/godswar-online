/* static/modules/skills.js - the "Skills & talents" page: the skill book (K), the talent tree, zodiac (sign, level,
   energy, the 16 training grids), titles and the quest log. Backend: modules/skills.py. Contract: MODULES.md.
   Database and client text is inserted as text (GW.h / GW.text), never as HTML. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const CLASSES = ["Warrior", "Champion", "Priest", "Mage"];
  const TABS = [["skills", "Skills"], ["talents", "Talents"], ["zodiac", "Zodiac"], ["titles", "Titles"], ["quests", "Quests"]];
  // SkillViewUI.xml Talent tab: Skillinfo1..18 cell positions (windows.json), and the arrows the layout draws
  const NODE_XY = [[101, 111], [28, 184], [78, 184], [127, 184], [175, 184], [28, 257], [78, 257], [127, 257], [28, 330], [78, 330],
    [127, 330], [28, 401], [78, 401], [127, 401], [175, 401], [28, 474], [78, 474], [127, 474]];
  const EDGES = [[1, 2], [1, 3], [1, 4], [1, 5], [2, 6], [3, 7], [4, 8], [6, 9], [7, 10], [8, 11], [5, 12], [5, 13], [5, 14], [5, 15], [12, 16], [13, 17], [14, 18]];
  // Constellation.xml Training tab: the four 4-cell panels = grid rows (SkillTrainConfig.lua Type 1-4)
  const GRID_ROWS = [
    { title: "Attack skills, group A", sub: "grids 1-4 · your class · Kinds 10000-19999", help: "Training a learned attack skill here raises its power (and its MP cost). Only your own class's skills, and only ones you have learned." },
    { title: "Attack skills, group B", sub: "grids 5-8 · your class · Kinds 20000-29999", help: "Same as group A, for the other half of the class skills." },
    { title: "Defence skills, group A", sub: "grids 9-12 · any class · Kinds 10000-19999", help: "Training an enemy skill here lowers the damage you take from it. Any class's skill." },
    { title: "Defence skills, group B", sub: "grids 13-16 · any class · Kinds 20000-29999", help: "Same as defence group A, for the other half of the skills." }];
  const STATE_NAMES = { 0: "In the quest log", 1: "Completed" };

  const fmt = GW.fmt;
  // Element.append() would write "null" for an empty slot: append only real nodes / text
  const add = (el, ...kids) => { for (const k of kids.flat(Infinity)) if (k !== null && k !== undefined && k !== false) el.append(k); return el; };
  const chip = (txt, cls, help) => h("span", { class: "gw-chip " + (cls || "never"), "data-help": help || null }, txt);
  function panel(title, opts, ...kids) {
    const o = opts || {};
    return h("section", { class: "gw-sk-panel" + (o.class ? " " + o.class : "") },
      h("div", { class: "gw-sk-ptitle" }, h("span", { class: "t", "data-field": o.field || null, "data-help": o.help || null }, title),
        o.applies ? GW.appliesChip(o.applies) : null, o.extra || null), ...kids);
  }
  const effVal = (r, per) => {
    const x = Math.min(Math.max(r, 0), 100);
    const e = x <= 40 ? x : x <= 60 ? 40 + (x - 40) * 2 : x <= 80 ? 80 + (x - 60) * 3 : x <= 90 ? 140 + (x - 80) * 5 : 190 + (x - 90) * 7;
    return e * per;
  };
  const showPct = (v, pct) => pct ? (Math.round(v * 10000) / 100) + "%" : fmt(Math.round(v * 100) / 100);

  const P = {
    async mount(el, ctx) {
      this.el = el; this.ctx = ctx; this.cid = ctx.cid; this.tab = TABS.some(t => t[0] === ctx.route[0]) ? ctx.route[0] : "skills";
      this.online = null; this.pend = { skills: {}, talents: {}, grids: {} }; this.sel = {}; this.advanced = false;
      if (this.cid === null || this.cid === undefined) { el.append(h("p", { class: "gw-dim" }, "Pick a character in the top bar.")); return; }
      this.banner = GW.onlineBanner(this.cid, { onState: st => { const was = this.online && this.online.online; this.online = st; if (this.data && was !== st.online) this.render(); } });
      this.head = h("div", { class: "gw-sk-head" });
      this.tabbar = h("div", { class: "gw-sk-tabs", role: "tablist" });
      this.body = h("div", { class: "gw-sk-body" });
      el.append(this.banner, this.head, this.tabbar, this.body);
      this.offChange = GW.on("change", c => { if (!c || Number(c.cid) === Number(this.cid)) this.reload(); });
      GW.catalog.load("item").catch(() => null);          // only for book / quest-item names: not worth waiting for (3,488 rows)
      await Promise.all(["skill", "talent", "zodiac_kind", "zodiac_sign", "title", "quest"].map(t => GW.catalog.load(t).catch(() => null)));
      this.rules = (await ctx.api("questrules").catch(() => ({ quests: {} }))).quests || {};
      await this.reload();
    },
    unmount() { if (this.offChange) this.offChange(); this.ctx = null; },

    async reload() {
      try { this.data = await this.ctx.api("sheet/" + this.cid); }
      catch (e) { GW.clear(this.body).append(h("p", { class: "tt-bad" }, e.message)); return; }
      this.pend = { skills: {}, talents: {}, grids: {} };
      this.render();
    },

    // ---------------------------------------------------------------- frame
    locked() { return !!(this.online && this.online.online); },
    live(col) { return !!(this.online && this.online.online && (this.online.live_fields || []).includes(col)); },
    lockNote(what) {
      if (!this.locked()) return null;
      const st = this.online;
      return h("div", { class: "gw-sk-lock", "data-help": (st.reasons || []).join("\n") },
        h("b", null, st.name + " is logged in. "), what + " ",
        st.capabilities ? "The game server has no live edit for this; log the character out (or use Kick on the banner)."
          : "Live edits arrive with the round-2 server update; this character must be logged out.");
    },
    fieldset(...kids) { return h("fieldset", { class: "gw-sk-fs", disabled: this.locked() || !this.data.writable || null }, ...kids); },
    render() {
      const d = this.data, b = d.character;
      add(GW.clear(this.head), 
        h("span", { class: "gw-sk-name" }, b.name), h("span", { class: "gw-dim" }, " #" + b.id + " · Lv " + b.level + " " + d.class_name + " · " + d.camp_name),
        !d.writable ? chip("read-only database", "restart", "This editor was started read-only for " + d.database + ".") : null,
        h("span", { class: "gw-sk-revision gw-dim", "data-help": "The server loads skills and talents from this pinned gameplay content revision (gameplay_content_publication)." }, "content " + String(d.revision).slice(0, 10)));
      GW.clear(this.tabbar);
      for (const [k, label] of TABS) {
        const n = k === "quests" ? " (" + d.quests.rows.filter(q => q.state === 0).length + "/" + d.quests.max_carried + ")" : "";
        this.tabbar.append(h("button", { class: "gw-tabx" + (k === this.tab ? " on" : ""), type: "button", role: "tab", "aria-selected": k === this.tab ? "true" : "false",
          onclick: () => { this.tab = k; history.replaceState(null, "", "#/skills/" + this.cid + "/" + k); this.render(); } }, label + n));
      }
      GW.clear(this.body);
      try { this["tab_" + this.tab](); } catch (e) { console.error(e); this.body.append(h("p", { class: "tt-bad" }, e.message)); }
    },
    async post(sub, body, label) {
      try {
        const r = await this.ctx.api(sub + "/" + this.cid, body);
        GW.writeDone(r, label);
        if (r.warning) GW.status(r.warning, "err");
        return r;
      } catch (e) {
        const k = await GW.writeError(e);
        if (k === "conflict") await this.reload();
        return null;
      }
    },
    saveBar(key, onSave) {
      const bar = GW.saveBar({ title: "Save these changes?", onSave, onDiscard: () => { this.pend[key] = {}; this.render(); } });
      return bar;
    },

    // ---------------------------------------------------------------- talent points (live-capable) strip
    pointsStrip() {
      const b = this.data.character;
      const live = this.live("SkillPoint");
      let tp = b.SkillPoint, te = b.SkillExp;
      const f1 = GW.numberField({ field: "character_base.SkillPoint", label: "Talent points", value: tp, min: 0, max: 2147483647, onChange: v => { tp = v; } });
      const f2 = GW.numberField({ field: "character_base.SkillExp", label: "Talent EXP", value: te, min: 0, max: 99, onChange: v => { te = v; } });
      const save = h("button", { class: "gw-btnx", type: "button", onclick: async () => {
        const vals = {}, exp = {};
        if (tp !== b.SkillPoint) { vals.SkillPoint = tp; exp.SkillPoint = b.SkillPoint; }
        if (te !== b.SkillExp) { vals.SkillExp = te; exp.SkillExp = b.SkillExp; }
        if (!Object.keys(vals).length) { GW.status("nothing changed"); return; }
        if (!f1.valid() || !f2.valid()) { GW.status("fix the red values first", "err"); return; }
        const r = await this.post("base", { values: vals, expected: exp, label: "talent points " + fmt(b.SkillPoint) + " -> " + fmt(tp) }, null);
        if (r) this.reload();
      } }, "Save");
      const disabled = !this.data.writable || (this.locked() && !live);
      if (this.locked() && live) f2.querySelector("input").disabled = true;
      const box = h("div", { class: "gw-sk-strip" }, f1, f2, save,
        live ? chip("live: the player sees it now", "now", "The round-2 game server changes talent points of a logged-in character at once (POST /admin/progression).") : null,
        h("span", { class: "gw-dim", "data-help": "100 talent EXP make one talent point (TalentExperienceCatalog). Changing points never changes the ranks below." }, "Points to spend on ranks; ranks are set directly below."));
      if (disabled) for (const x of box.querySelectorAll("input,button")) x.disabled = true;
      return box;
    },

    // ================================================================ SKILLS
    tab_skills() {
      const d = this.data, b = d.character, pend = this.pend.skills;
      const famBy = Object.fromEntries(d.skills.families.map(f => [f.base, f]));
      const bar = this.saveBar("skills", async items => {
        const set = {}, expected = {};
        for (const it of items) { set[it.key] = it.to_id; expected[it.key] = it.from_id; }
        const r = await this.post("skills", { set, expected }, "skill book: " + items.map(i => i.label + " " + i.to).join(", "));
        if (r) await this.reload();
        return null;
      });
      const stage = (f, id) => {
        const cur = f.current;
        if (id === cur || (id === null && cur === null)) delete pend[f.base]; else pend[f.base] = id;
        const lvl = x => x === null ? "not learned" : "Lv " + ((f.skills.find(s => s.id === x) || {}).level || 1) + " #" + x;
        bar.clear();
        for (const [k, v] of Object.entries(pend)) bar.set(k, { key: k, label: k, from: lvl(famBy[k].current), to: lvl(v), from_id: famBy[k].current, to_id: v, applies: "next_login", field: "character_skills.skill_id" });
        draw();
      };
      const list = h("div", { class: "gw-sk-book", role: "list" });
      const detail = h("div", { class: "gw-sk-detail" }, h("p", { class: "gw-dim" }, "Hover a skill for its game tooltip; click a level to learn it."));
      const showDetail = async (f, sid) => {
        GW.clear(detail);
        const s = f.skills.find(x => x.id === sid) || f.skills[0];
        try { detail.append(GW.render.skill(await GW.catalog.get("skill", s.id))); } catch (e) { detail.append(h("div", { class: "gw-dim" }, s.name)); }
        detail.append(h("table", { class: "gw-sk-kv" },
          h("tr", null, h("th", { "data-field": "character_skills.skill_id" }, "Skill id"), h("td", null, "#" + s.id)),
          h("tr", null, h("th", { "data-field": "character_skills.skill_level" }, "Level"), h("td", null, s.level || "(no levels)")),
          h("tr", null, h("th", { "data-help": "The level the skill book asks for (PlayLv). The editor lets you give it earlier; the server does not check it at login." }, "Book needs Lv"), h("td", { class: (s.minlv || 1) > b.level ? "tt-bad" : "" }, s.minlv || 1)),
          h("tr", null, h("th", { "data-help": "MP cost and cooldown from the server's pinned skill content." }, "MP / cooldown"), h("td", null, (s.mp || 0) + " / " + (s.cooldown || 0) + " s")),
          h("tr", null, h("th", { "data-help": "The skill book item(s) that teach this skill in game." }, "Book item"),
            h("td", null, (s.books || []).map(i => h("span", { class: "gw-sk-book-item", "data-tip-type": "item", "data-tip-id": String(i) }, GW.icon((GW.catalog.loadedRow("item", i) || {}).sprite), " ", (GW.catalog.loadedRow("item", i) || {}).name || "#" + i))))));
      };
      const draw = () => {
        GW.clear(list);
        let lastGeneral = null;
        for (const f of d.skills.families) {
          if (f.general !== lastGeneral) { list.append(h("div", { class: "gw-sk-sect" }, f.general ? "General skills (every class)" : d.class_name + " skills")); lastGeneral = f.general; }
          const want = Object.prototype.hasOwnProperty.call(pend, f.base) ? pend[f.base] : f.current;
          const cur = f.skills.find(s => s.id === want);
          const shown = cur || f.skills[0];
          const row = GW.catalog.loadedRow("skill", shown.id) || {};
          const pips = h("span", { class: "gw-sk-pips" },
            h("button", { class: "gw-sk-pip none" + (want === null ? " on" : ""), type: "button", "data-help": "Not learned: the skill is not in the skill book.", onclick: () => stage(f, null) }, "–"),
            f.skills.map(s => h("button", { class: "gw-sk-pip" + (s.id === want ? " on" : "") + ((s.minlv || 1) > b.level ? " over" : ""), type: "button",
              "data-tip-type": "skill", "data-tip-id": String(s.id), "aria-label": (GW.catalog.loadedRow("skill", s.id) || {}).name || s.name, onclick: () => { stage(f, s.id); showDetail(f, s.id); } },
              s.level ? String(s.level) : (f.skills.length > 1 ? "#" + s.id : "✓"))));
          list.append(h("div", { class: "gw-sk-fam" + (Object.prototype.hasOwnProperty.call(pend, f.base) ? " changed" : "") + (want === null ? " off" : ""), role: "listitem" },
            GW.cell({ sprite: row.sprite, name: f.base, tipType: "skill", tipId: shown.id, class: want === null ? "locked" : "", onclick: () => showDetail(f, shown.id) }),
            h("span", { class: "gw-sk-fname" }, h("b", null, f.base), h("span", { class: "gw-dim" }, cur ? (cur.level ? " Lv " + cur.level : " learned") : " not learned",
              " · book from Lv " + f.minlv)), pips));
        }
        if (d.skills.foreign.length) {
          list.append(h("div", { class: "gw-sk-sect tt-bad" }, "Rows the server ignores"));
          for (const s of d.skills.foreign) list.append(h("div", { class: "gw-sk-fam bad", "data-help": s.why }, "#" + s.skill_id + " (" + s.source + ") - " + s.why));
        }
      };
      const bulk = (fn, label) => h("button", { class: "gw-btnx small", type: "button", "data-help": label[1], onclick: () => { for (const f of d.skills.families) { const v = fn(f); if (v !== undefined) stage(f, v); } } }, label[0]);
      const tools = h("div", { class: "gw-sk-tools" },
        bulk(f => f.general ? undefined : f.skills[f.skills.length - 1].id, ["Every class skill at top level", "Stage every class skill family at its highest level (Lv 5)."]),
        bulk(f => { if (f.general) return undefined; const ok = f.skills.filter(s => (s.minlv || 1) <= b.level); return ok.length ? ok[ok.length - 1].id : null; },
          ["What Lv " + b.level + " allows", "Stage each class skill at the highest level whose book this character's level allows (what a player could have)."]),
        bulk(f => f.general ? undefined : null, ["Forget class skills", "Stage every class skill as not learned (general skills such as portals and riding stay)."]),
        h("button", { class: "gw-btnx small", type: "button", "data-help": "Browse every skill of the game (all classes, item and monster skills). Picking one of this class's skills stages it.",
          onclick: async () => {
            const id = await GW.pick("skill", { title: "Skill library: every skill (pick one of this class's skills to learn it)" });
            if (id === null || id === "") return;
            const f = d.skills.families.find(x => x.skills.some(s => s.id === Number(id)));
            if (!f) { GW.alert("Skill #" + id + " is not a " + d.class_name + " skill in the server's pinned skill content, so the server would not load it.", "Not for this class"); return; }
            stage(f, Number(id)); showDetail(f, Number(id));
          } }, "Skill library…"));
      draw();
      add(this.body, this.lockNote("Skills are sent to the client when it enters the world."),
        h("div", { class: "gw-sk-cols" },
          panel("Skill book", { applies: "next_login", field: "character_skills.skill_id", extra: h("span", { class: "gw-dim" }, " one row per skill family; a new level replaces the old one, like reading the next book") },
            this.fieldset(tools, list), bar.el),
          panel("Skill", { class: "gw-sk-side" }, detail)));
    },

    // ================================================================ TALENTS
    tab_talents() {
      const d = this.data, T = d.talents, pend = this.pend.talents;
      const tree = h("div", { class: "gw-sk-tree" });
      const side = h("div", { class: "gw-sk-detail" });
      const bar = this.saveBar("talents", async items => {
        const ranks = {}, expected = {};
        for (const it of items) { ranks[it.key] = it.to; expected[it.key] = it.from; }
        const r = await this.post("talents", { ranks, expected }, "talents: " + items.map(i => i.label + " " + i.to).join(", "));
        if (r) await this.reload();
        return null;
      });
      const rankOf = t => Object.prototype.hasOwnProperty.call(pend, t.id) ? pend[t.id] : t.rank;
      const stage = (t, v) => {
        v = Math.max(0, Math.min(T.rank_cap, Math.round(Number(v) || 0)));
        if (v === t.rank) { delete pend[t.id]; bar.unset(String(t.id)); }
        else { pend[t.id] = v; bar.set(String(t.id), { key: t.id, label: (GW.catalog.loadedRow("talent", t.id) || {}).name || t.name, from: t.rank, to: v, applies: "next_login", field: "character_talents.rank" }); }
        drawTree(); total();
      };
      const sumBox = h("div", { class: "gw-sk-sum" });
      const total = () => {
        const tot = T.list.reduce((a, t) => a + rankOf(t), 0);
        const cost = T.list.reduce((a, t) => a + T.table[rankOf(t)].total_cost, 0);
        GW.clear(sumBox).append(h("span", { "data-help": "Sum of all ranks in this tree." }, "Ranks: ", h("b", null, fmt(tot))), " · ",
          h("span", { "data-help": "What these ranks would cost a player in game (TalentProgression.CalculateUpgradeCost, summed). The editor does not charge points." }, "worth ", h("b", null, fmt(cost)), " talent points in game"));
      };
      const drawTree = () => {
        GW.clear(tree);
        const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
        svg.setAttribute("class", "gw-sk-arrows"); svg.setAttribute("width", "240"); svg.setAttribute("height", "420");
        const ln = (x1, y1, x2, y2) => { const l = document.createElementNS(svg.namespaceURI, "path"); l.setAttribute("d", `M${x1} ${y1} V${y2 - 12} H${x2} V${y2}`); svg.append(l); };
        for (const [a, c] of EDGES) {
          if (a > T.list.length || c > T.list.length) continue;
          const [ax, ay] = NODE_XY[a - 1], [cx, cy] = NODE_XY[c - 1];
          ln(ax - 20 + 21, ay - 100 + 42, cx - 20 + 21, cy - 100 - 2);
        }
        tree.append(svg);
        T.list.forEach((t, i) => {
          const [x, y] = NODE_XY[i] || [0, 0];
          const r = rankOf(t), row = GW.catalog.loadedRow("talent", t.id) || {};
          const cell = GW.cell({ sprite: row.sprite, name: t.name, tipType: "talent", tipId: t.id, label: (row.name || t.name) + " rank " + r,
            class: "gw-sk-node" + (r === 0 ? " shade" : "") + (this.sel.talent === t.id ? " sel" : "") + (Object.prototype.hasOwnProperty.call(pend, t.id) ? " changed" : "") + (r > T.client_cap ? " over" : ""),
            corner: null, count: null, onclick: () => { this.sel.talent = t.id; drawTree(); drawSide(t); } });
          cell.style.left = (x - 20) + "px"; cell.style.top = (y - 100) + "px";
          cell.append(h("span", { class: "gw-sk-rank gw-outline" }, String(r)));
          tree.append(cell);
        });
      };
      const drawSide = t => {
        GW.clear(side);
        if (!t) { side.append(h("p", { class: "gw-dim" }, "Click a talent in the tree.")); return; }
        const e = GW.catalog.loadedRow("talent", t.id) || {};
        const r = rankOf(t);
        GW.catalog.get("talent", t.id).then(full => {
          const effs = (full.effects || []).map(x => {
            const per = Number((x.values || [])[1] || 0);
            return h("div", { class: "gw-good" }, (x.label || x.stat) + ": +" + showPct(effVal(r, per), x.percent) + " at rank " + r + (r < T.rank_cap ? "  (next +" + showPct(effVal(r + 1, per), x.percent) + ")" : ""));
          });
          side.prepend(h("div", { class: "gw-sk-tname" }, GW.icon(e.sprite), h("b", null, " " + (e.name || t.name))), ...effs);
        }).catch(() => null);
        const nf = GW.numberField({ field: "character_talents.rank", label: "Rank", value: r, min: 0, max: T.rank_cap, onChange: (v, ok) => { if (ok && v !== null) stage(t, v); } });
        const q = (v, help) => h("button", { class: "gw-btnx small", type: "button", "data-help": help, onclick: () => { stage(t, v); drawSide(t); } }, String(v));
        const row = T.table[r];
        add(side, nf, h("div", { class: "gw-sk-quick" }, q(0, "Not learned."), q(10, "Rank 10."), q(40, "Rank 40."),
          q(T.client_cap, "Rank " + T.client_cap + ": the highest the admin's client shows."), q(T.rank_cap, "Rank " + T.rank_cap + ": the server's cap. The client shows 60 at most, but the server uses the real rank for stats.")),
          r > T.client_cap ? h("p", { class: "tt-bad" }, "Above " + T.client_cap + ": the server uses it, but the game client shows at most " + T.client_cap + ".") : null,
          h("table", { class: "gw-sk-kv" },
            h("tr", null, h("th", { "data-help": "Talent points a player spends to reach this rank from 0 (sum of every step)." }, "In game it costs"), h("td", null, fmt(row.total_cost) + " TP")),
            h("tr", null, h("th", { "data-help": "Player level the server wants before the last step to this rank (TalentProgression.CalculateRequiredPlayerLevel)." }, "Needs player Lv"), h("td", null, row.level || "-")),
            h("tr", null, h("th", { "data-help": "The client multiplies its per-rank value by this 'display rank' (docs/client-progressive-talent-tooltips.md)." }, "Display rank"), h("td", null, row.effective)),
            h("tr", null, h("th", { "data-field": "character_talents.outbox_revision" }, "Outbox revision"), h("td", null, t.outbox_revision === null || t.outbox_revision === undefined ? "(no row yet)" : t.outbox_revision))));
      };
      let allV = 60;
      const allField = GW.numberField({ label: "", value: allV, min: 0, max: T.rank_cap, slider: false, onChange: v => { allV = v; } });
      const tools = h("div", { class: "gw-sk-tools" },
        h("span", { "data-help": "Set every talent of the tree to one rank (staged; Review & save writes them in one transaction)." }, "Every talent to rank "), allField,
        h("button", { class: "gw-btnx small", type: "button", onclick: () => { if (allField.valid()) for (const t of T.list) stage(t, allV); } }, "Apply"),
        h("button", { class: "gw-btnx small", type: "button", "data-help": "Stage rank 0 for every talent (talent points are not refunded).", onclick: () => { for (const t of T.list) stage(t, 0); } }, "All to 0"));
      drawTree(); total();
      drawSide(T.list.find(t => t.id === this.sel.talent) || null);
      for (const [k, v] of Object.entries(pend)) { const t = T.list.find(x => String(x.id) === String(k)); if (t) bar.set(String(t.id), { key: t.id, label: t.name, from: t.rank, to: v, applies: "next_login" }); }
      add(this.body, panel("Talent points", { field: "character_base.SkillPoint" }, this.pointsStrip()),
        this.lockNote("Talent ranks are read when the character enters the world."),
        h("div", { class: "gw-sk-cols" },
          panel(d.class_name + " talent tree", { applies: "next_login", field: "character_talents.rank", class: "gw-sk-treepanel",
            extra: h("span", { class: "gw-dim" }, " rank 0-" + T.rank_cap + " (client shows " + T.client_cap + ")") },
            this.fieldset(tools, tree, sumBox), bar.el,
            T.stray.length ? h("p", { class: "tt-bad", "data-help": "Rows for talents of another class (or Champion's Might #68): the server never loads them." }, T.stray.length + " talent row(s) the server ignores: " + T.stray.map(x => "#" + x.talent_id).join(", ")) : null),
          panel("Talent", { class: "gw-sk-side" }, this.fieldset(side))));
    },

    // ================================================================ ZODIAC
    tab_zodiac() {
      const d = this.data, Z = d.zodiac, b = d.character, pend = this.pend.grids;
      let sign = Z.sign, level = Z.level, energy = Z.energy;
      const pic = h("img", { class: "gw-sk-zpic", alt: "zodiac sign picture", width: 176, height: 150 });
      const signRow = h("div", { class: "gw-sk-signs", role: "radiogroup", "data-field": "character_base.zodiac_type" });
      const stars = h("div", { class: "gw-sk-stars", "data-field": "character_base.zodiac_level" });
      const ebar = h("div", { class: "gw-sk-ebar" });
      const efield = GW.numberField({ field: "character_base.zodiac_energy", label: "Zodiac energy", value: energy, min: 0, max: Z.caps[level - 1], onChange: v => { energy = v; drawBar(); } });
      const liveE = this.live("zodiac_energy");
      const drawLeft = () => {
        pic.src = "/static/ui/zodiac/" + String(Math.min(11, Math.max(0, sign))).padStart(2, "0") + ".png";
        GW.clear(signRow);
        for (let s = 0; s < 12; s++) {
          const r = GW.catalog.loadedRow("zodiac_sign", s) || { name: "sign " + s };
          signRow.append(h("button", { class: "gw-chipbtn" + (s === sign ? " on" : ""), type: "button", role: "radio", "aria-checked": s === sign ? "true" : "false",
            "data-tip-type": "zodiac_sign", "data-tip-id": String(s), onclick: () => { sign = s; drawLeft(); } }, r.name));
        }
        GW.clear(stars);
        for (let l = 1; l <= 30; l++) stars.append(h("button", { class: "gw-sprite " + (l <= level ? "gw-zodiac-star-on" : "gw-zodiac-star-off"), type: "button",
          "aria-label": "zodiac level " + l, "data-help": "Zodiac level " + l + ": stores up to " + fmt(Z.caps[l - 1]) + " energy.", onclick: () => { level = l; efield.setRange(0, Z.caps[l - 1]); drawLeft(); drawBar(); } }));
        stars.append(h("span", { class: "gw-sk-zlv" }, "Level " + level + " / 30"));
      };
      const drawBar = () => {
        const cap = Z.caps[level - 1], pct = Math.min(100, Math.round((Number(energy) || 0) / cap * 100));
        GW.clear(ebar).append(h("span", { class: "fill", style: "width:" + pct + "%" }), h("span", { class: "txt gw-outline" }, fmt(energy) + " / " + fmt(cap)));
      };
      drawLeft(); drawBar();
      const saveZ = h("button", { class: "gw-btnx", type: "button", onclick: async () => {
        const vals = {}, exp = {};
        if (sign !== Z.sign) { vals.zodiac_type = sign; exp.zodiac_type = Z.sign; }
        if (level !== Z.level) { vals.zodiac_level = level; exp.zodiac_level = Z.level; }
        if (energy !== Z.energy) { vals.zodiac_energy = energy; exp.zodiac_energy = Z.energy; }
        if (!Object.keys(vals).length) { GW.status("nothing changed"); return; }
        if (!efield.valid()) { GW.status("energy is above the storage cap of that level", "err"); return; }
        const ok = await GW.confirm({ title: "Save zodiac?", message: Object.entries(vals).map(([k, v]) => k + ": " + fmt(exp[k]) + " -> " + fmt(v)).join("\n") }); if (!ok) return;
        const r = await this.post("base", { values: vals, expected: exp }, null);
        if (r) this.reload();
      } }, "Save sign, level and energy");
      const left = h("div", { class: "gw-sk-zleft" }, pic, signRow, stars, efield, ebar, saveZ,
        liveE ? chip("energy goes live", "now", "The round-2 server changes zodiac energy of a logged-in character at once. Sign and level need a logout.") : null);
      if (!d.writable || (this.locked() && !liveE)) for (const x of left.querySelectorAll("input,button")) x.disabled = true;
      else if (this.locked() && liveE) { for (const x of signRow.querySelectorAll("button")) x.disabled = true; for (const x of stars.querySelectorAll("button")) x.disabled = true; }

      // ---- training grids
      const bar = this.saveBar("grids", async items => {
        const grids = {}, expected = {};
        for (const it of items) { grids[it.key] = it.to_v; expected[it.key] = it.from_v; }
        const r = await this.post("grids", { grids, expected }, "zodiac grids: " + items.map(i => i.label + " " + i.to).join(", "));
        if (r) await this.reload();
        return null;
      });
      const kindName = k => k === -1 ? "no skill" : ((GW.catalog.loadedRow("zodiac_kind", k) || {}).name || "Kind " + k);
      const gv = g => pend[g] || Z.grids[g];
      const stage = (g, v) => {
        const o = Z.grids[g];
        if (v.level === 0) v.kind = -1;
        if (v.level === o.level && v.kind === o.kind) delete pend[g]; else pend[g] = v;
        bar.clear();
        for (const [k, x] of Object.entries(pend)) { const oo = Z.grids[k]; bar.set(k, { key: k, label: "Grid " + (Number(k) + 1), from: "Lv " + oo.level + " " + kindName(oo.kind), to: "Lv " + x.level + " " + kindName(x.kind), from_v: { level: oo.level, kind: oo.kind }, to_v: x, applies: "next_login", field: "character_zodiac_skill_grids.level" }); }
        drawGrids();
      };
      const gridBox = h("div", { class: "gw-sk-grids" });
      const drawGrids = () => {
        GW.clear(gridBox);
        GRID_ROWS.forEach((R, ri) => {
          const cells = h("div", { class: "gw-sk-gcells" });
          for (let g = ri * 4; g < ri * 4 + 4; g++) {
            const v = gv(g), row = v.kind !== -1 ? GW.catalog.loadedRow("zodiac_kind", v.kind) : null;
            const probs = (Z.problems[String(g)] || []);
            const c = GW.cell({ sprite: row ? row.sprite : null, name: row ? row.name : "", tipType: row ? "zodiac_kind" : null, tipId: row ? v.kind : null,
              label: "grid " + (g + 1) + ", level " + v.level + ", " + kindName(v.kind), class: (v.level === 0 ? "locked " : "") + (pend[g] ? "changed " : "") + (probs.length && !pend[g] ? "bad" : ""),
              onclick: () => this.gridDialog(g, gv(g), stage) });
            c.append(h("span", { class: "gw-sk-rank gw-outline" }, v.level ? String(v.level) : ""));
            cells.append(h("div", { class: "gw-sk-gcell", "data-help": probs.length ? "Problem: " + probs.join("; ") : null }, c, h("div", { class: "gw-sk-gname" }, "Grid " + (g + 1)),
              h("div", { class: "gw-dim gw-sk-gname" }, v.level ? kindName(v.kind) : (Z.grid_gold[g] ? "locked · " + fmt(Z.grid_gold[g]) + " Gold" : "locked · free"))));
          }
          gridBox.append(h("div", { class: "gw-sk-grow" }, h("div", { class: "gw-sk-gtitle", "data-help": R.help }, R.title), h("div", { class: "gw-dim gw-sk-gsub" }, R.sub), cells));
        });
      };
      let allL = 50;
      const allF = GW.numberField({ value: allL, min: 0, max: Z.grid_level_max, slider: false, onChange: v => { allL = v; } });
      const gtools = h("div", { class: "gw-sk-tools" },
        h("span", { "data-help": "Set the level of every grid (staged). Level 0 locks a grid again and clears its skill." }, "Every grid to level "), allF,
        h("button", { class: "gw-btnx small", type: "button", onclick: () => { if (!allF.valid()) return; for (let g = 0; g < 16; g++) { const v = gv(g); stage(g, { level: allL, kind: allL ? v.kind : -1 }); } } }, "Apply"));
      drawGrids();
      for (const [k, x] of Object.entries(pend)) stage(Number(k), x);
      add(this.body, this.lockNote("Sign, level and the training grids are read when the character enters the world."),
        h("div", { class: "gw-sk-cols" },
          panel("Zodiac", { applies: liveE ? "live" : "next_login", field: "character_base.zodiac_type", class: "gw-sk-zpanel" }, left),
          panel("Training (16 grids)", { applies: "next_login", field: "character_zodiac_skill_grids.level", class: "gw-sk-gpanel",
            extra: h("span", { class: "gw-dim" }, " level 0-" + Z.grid_level_max + "; click a grid") }, this.fieldset(gtools, gridBox), bar.el)));
    },
    async gridDialog(g, v, stage) {
      const Z = this.data.zodiac, prof = this.data.character.profession;
      const defence = g >= 8, group = (g % 8) < 4 ? 1 : 2;
      let level = v.level, kind = v.kind;
      let info = null;
      try { info = await GW.catalog.get("zodiac_grid", g); } catch (e) { info = null; }
      const lvTable = h("div", { class: "gw-sk-gtable" });
      const drawLv = () => {
        GW.clear(lvTable);
        const L = info && info.levels ? info.levels : [];
        const at = L.find(x => x.level === level);
        let e = 0, tp = 0;
        for (const x of L) if (x.level <= level) { e += x.energy || 0; tp += x.talent_points || 0; }
        lvTable.append(h("table", { class: "gw-sk-kv" },
          h("tr", null, h("th", { "data-help": "Skill effect of this grid level (SkillTrainConfig.lua SkillEff)." }, "Effect"), h("td", { class: "gw-good" }, at ? "+" + fmt(at.effect) : "-")),
          h("tr", null, h("th", { "data-help": "Extra MP the trained skill costs (SkillTrainConfig.lua MP)." }, "Extra MP"), h("td", null, at ? "+" + at.extra_mp : "-")),
          h("tr", null, h("th", { "data-help": "Zodiac level the game wants for this grid level (Starlv)." }, "Needs zodiac Lv"), h("td", { class: at && at.zodiac_level > Z.level ? "tt-bad" : "" }, at ? at.zodiac_level : "-")),
          h("tr", null, h("th", { "data-help": "What a player spends in game to train this grid from 1 to this level (UpdateE energy + UpdateS talent points). The editor does not charge anything." }, "In game it costs"), h("td", null, fmt(e) + " energy + " + fmt(tp) + " TP")),
          h("tr", null, h("th", { "data-help": "Gold to unlock the grid in game (UnlockG)." }, "Unlock"), h("td", null, Z.grid_gold[g] ? fmt(Z.grid_gold[g]) + " Gold" : "free"))));
      };
      const lf = GW.numberField({ field: "character_zodiac_skill_grids.level", label: "Grid level", value: level, min: 0, max: Z.grid_level_max, onChange: (x, ok) => { if (ok && x !== null) { level = x; drawLv(); drawKinds(); } } });
      const kindsBox = h("div", { class: "gw-sk-kinds", "data-field": "character_zodiac_skill_grids.selected_skill_id" });
      const rowKinds = new Set();
      for (let o = Math.floor(g / 4) * 4; o < Math.floor(g / 4) * 4 + 4; o++) if (o !== g) { const x = (this.pend.grids[o] || Z.grids[o]).kind; if (x !== -1) rowKinds.add(x); }
      const drawKinds = () => {
        GW.clear(kindsBox);
        kindsBox.append(h("button", { class: "gw-sk-kind" + (kind === -1 ? " on" : ""), type: "button", onclick: () => { kind = -1; drawKinds(); } }, GW.icon(null, { name: "–" }), h("span", null, "No skill")));
        for (const k of Z.kinds) {
          if (k.group !== group || (!defence && k.class !== prof)) continue;
          const row = GW.catalog.loadedRow("zodiac_kind", k.kind) || { name: "Kind " + k.kind };
          let why = "";
          if (level === 0) why = "give the grid a level first (an inactive grid holds no skill)";
          else if (!defence && !k.learned) why = "not learned: the server looks for skill ids " + k.family[0] + "-" + k.family[4] + " in the skill book";
          else if (rowKinds.has(k.kind)) why = "already in another grid of this row";
          kindsBox.append(h("button", { class: "gw-sk-kind" + (kind === k.kind ? " on" : "") + (why ? " no" : ""), type: "button", disabled: why ? true : null,
            "data-tip-type": "zodiac_kind", "data-tip-id": String(k.kind), "data-help": why || null, onclick: () => { kind = k.kind; drawKinds(); } },
            GW.icon(row.sprite, { name: row.name }), h("span", null, row.name, h("br"), h("small", { class: "gw-dim" }, CLASSES[k.class] + (why ? " · " + why : "")))));
        }
      };
      drawLv(); drawKinds();
      const body = h("div", { class: "gw-mod-skills gw-sk-gdlg" },
        h("p", { class: "gw-dim" }, GRID_ROWS[Math.floor(g / 4)].title + " · " + GRID_ROWS[Math.floor(g / 4)].help), lf, lvTable,
        h("h4", null, defence ? "Enemy skill to defend against" : "Your skill to train"), kindsBox);
      const ok = await GW.confirm({ title: "Zodiac grid " + (g + 1), el: body, ok: "Stage", wide: true });
      if (!ok || !lf.valid()) return;
      stage(g, { level, kind: level === 0 ? -1 : kind });
    },

    // ================================================================ TITLES
    tab_titles() {
      const d = this.data, T = d.titles, b = d.character;
      const medusa = Object.fromEntries((T.medusa_defs || []).map(x => [x.id, x]));
      const nameOf = id => (GW.catalog.loadedRow("title", id) || {}).name || (medusa[id] || {}).name || "title #" + id;
      const source = id => [T.medusa.some(x => x.title_id === id) ? "Medusa Island" : null,
        T.atlantis.some(x => x.title_id === id) ? "Atlantis" : null, T.wonderland.some(x => x.title_id === id) ? "Wonderland" : null,
        (T.admin || []).some(x => x.title_id === id) ? "Administrator grant" : null].filter(Boolean).join(" + ");
      const owned = h("div", { class: "gw-sk-titles", role: "list" });
      const pick = async id => {
        if (id === T.selected) return;
        const ctx = this.ctx, cid = this.cid;
        const ok = await GW.confirm({ title: "Shown title", message: "Show " + (id ? nameOf(id) : "no title") + " above " + b.name + "?" });
        if (!ok || this.ctx !== ctx || this.cid !== cid || this.data !== d) return;
        const r = await this.post("base", { values: { selected_title_id: id }, expected: { selected_title_id: T.selected } }, "shown title: " + (id ? nameOf(id) : "none"));
        if (r) this.reload();
      };
      owned.append(h("button", { class: "gw-sk-trow" + (T.selected === 0 ? " on" : ""), type: "button", onclick: () => pick(0) }, h("b", null, "No title"), h("span", { class: "gw-dim" }, " (0)")));
      for (const id of T.owned) {
        owned.append(h("button", { class: "gw-sk-trow" + (T.selected === id ? " on" : ""), type: "button", "data-tip-type": "title", "data-tip-id": String(id), onclick: () => pick(id) },
          h("b", null, nameOf(id)), h("span", { class: "gw-dim" }, " #" + id + " · " + source(id)), T.selected === id ? chip("shown", "now") : null));
      }
      if (!T.owned.length) owned.append(h("p", { class: "gw-dim" }, b.name + " has no available titles yet."));
      const wl = T.wonderland.length && !(T.grant_support || {}).available ? h("p", { class: "gw-dim" },
        "Wonderland titles: " + T.wonderland.map(x => nameOf(x.title_id)).join(", ") + " · selection requires the compatible server update.") : null;
      if (T.selected && !T.owned.includes(T.selected)) owned.append(h("p", { class: "tt-bad" }, "The shown title #" + T.selected + " is not owned: pick one above or 'No title'."));
      const lib = GW.grid({ type: "title", height: "420px" });
      add(this.body, this.lockNote("The shown title is read when the character enters the world."),
        h("div", { class: "gw-sk-cols" },
          panel("Titles owned", { applies: "next_login", field: "character_base.selected_title_id", extra: h("span", { class: "gw-dim", "data-help": T.why_read_only }, " click one to show it") },
            this.fieldset(owned), wl, h("a", {class:"gw-btnx",href:"#/all_titles/"+this.cid}, "Give all titles…"), this.titleGrantControls(d),
            h("p", { class: "gw-dim", "data-field": "character_title_ownership.title_id" }, T.why_read_only)),
          panel("Reference title library", { class: "gw-sk-side wide" }, lib.el)));
    },

    titleGrantControls(d) {
      const T = d.titles, ready = T.grant_support || {};
      if (!ready.available) return h("p", { class: "gw-dim" }, ready.reason || "Title grants require the compatible server update.");
      const initial = (T.admin || []).map(x => x.title_id).sort((a, b) => a - b), chosen = new Set(initial);
      const list = h("div", { style: "max-height:260px;overflow:auto" });
      const search = h("input", { type: "search", placeholder: "Search titles or descriptions", "aria-label": "Search grantable titles" });
      const draw = () => {
        GW.clear(list); const words = search.value.trim().toLocaleLowerCase().split(/\s+/).filter(Boolean);
        for (const row of T.grant_catalogue || []) {
          const text = (row.title_id + " " + row.display_name + " " + row.description).toLocaleLowerCase();
          if (!words.every(word => text.includes(word))) continue;
          const check = h("input", { type: "checkbox", checked: chosen.has(row.title_id), onchange: () => {
            if (check.checked) chosen.add(row.title_id); else chosen.delete(row.title_id);
          } });
          list.append(h("label", { style: "display:block", title: row.description }, check, " " + row.display_name + " #" + row.title_id));
        }
      };
      search.oninput = draw; draw();
      const note = h("input", { type: "text", maxlength: "512", placeholder: "Grant note (optional)", "aria-label": "Grant note" });
      const save = h("button", { class: "gw-btnx", type: "button", onclick: async () => {
        const ctx = this.ctx, cid = this.cid;
        const add = [...chosen].filter(id => !initial.includes(id)), remove = initial.filter(id => !chosen.has(id));
        if (!add.length && !remove.length) { GW.status("No administrator grants changed."); return; }
        const body = { add, remove, expected: initial.slice(), note: note.value };
        const ok = await GW.confirm({ title: "Change title grants?", message: "For " + d.character.name + ": add " + add.length + ", revoke " + remove.length + " administrator grants. Earned titles stay owned. Changes apply at next login." });
        if (!ok || this.ctx !== ctx || this.cid !== cid || this.data !== d) return;
        try {
          const r = await ctx.api("title-grants/" + cid, body); GW.writeDone(r, "Administrator title grants");
          if (this.ctx === ctx && this.cid === cid) await this.reload();
        } catch (e) { await GW.writeError(e); }
      } }, "Save administrator grants");
      return panel("Administrator grants", { applies: "next_login" },
        h("p", { class: "gw-dim" }, "Tick titles to grant; untick to revoke the administrator copy. Earned ownership remains intact."),
        this.fieldset(search, list, note, save));
    },

    // ================================================================ QUESTS
    tab_quests() {
      const d = this.data, Q = d.quests, b = d.character, rules = this.rules;
      const camp = d.camp_name;
      const byId = Object.fromEntries(Q.rows.map(q => [q.quest_id, q]));
      const sub = this.qsub || "log";
      const subs = h("div", { class: "gw-sk-subtabs" });
      const carried = Q.rows.filter(q => q.state === 0), done = Q.rows.filter(q => q.state === 1);
      for (const [k, label] of [["log", "Quest log (" + carried.length + "/" + Q.max_carried + ")"], ["done", "Completed (" + done.length + ")"], ["all", "All quests (" + camp + ")"]])
        subs.append(h("button", { class: "gw-tabx" + (k === sub ? " on" : ""), type: "button", onclick: () => { this.qsub = k; this.render(); } }, label));
      const detail = h("div", { class: "gw-sk-detail" }, h("p", { class: "gw-dim" }, "Pick a quest."));
      const qname = id => (GW.catalog.loadedRow("quest", id) || {}).name || "quest " + id;
      const listRow = q => {
        const r = rules[q.quest_id] || {};
        return h("button", { class: "gw-sk-qrow" + (this.sel.quest === q.quest_id ? " on" : ""), type: "button", "data-tip-type": "quest", "data-tip-id": String(q.quest_id),
          onclick: ev => { this.sel.quest = q.quest_id; this.questDetail(detail, q.quest_id); for (const x of list.children) x.classList.toggle("on", x === ev.currentTarget); } },
          h("span", { class: "nm" }, qname(q.quest_id)), h("span", { class: "gw-dim" }, " #" + q.quest_id + (r.minlv ? " · Lv " + r.minlv : "") + (q.state === 0 && q.counters.some(Boolean) ? " · progress " + q.counters.filter((x, i) => x || i === 0).join("/") : "")),
          !q.known ? chip("not in the server roster", "restart", "The server has no rules for this quest and ignores the row.") : null);
      };
      let list = h("div", { class: "gw-sk-qlist", role: "list" });
      let left;
      if (sub === "log" || sub === "done") {
        const rows = sub === "log" ? carried : done;
        for (const q of rows) list.append(listRow(q));
        if (!rows.length) list.append(h("p", { class: "gw-dim" }, sub === "log" ? "The quest log is empty. Pick a quest in 'All quests' to put it in the log." : "No completed quests yet."));
        left = list;
      } else {
        const filt = r => { const x = rules[r.id]; return !!x && (!x.camp || x.camp === camp); };
        const grid = GW.grid({ type: "quest", filter: filt, height: "460px", value: this.sel.quest,
          onSelect: r => { this.sel.quest = r.id; this.questDetail(detail, r.id); } });
        left = grid.el;
      }
      // ---- bulk tools
      let upto = b.level;
      const upF = GW.numberField({ value: upto, min: 1, max: 200, slider: false, onChange: v => { upto = v; } });
      // Issue #18: previewed main-chain completion and history reset, planned like the server (questchain).
      // Works for a logged-in character through the server's /admin/quest/chain when it offers one.
      const liveChain = this.locked() && (this.online.live || []).includes("character.quest_chain");
      const chain = async (body, label) => {
        let pv;
        try { pv = (await this.ctx.api("questchain/" + this.cid, body)).preview; }
        catch (e) { await GW.writeError(e); return; }
        if (!pv.changed.length && !pv.conflicts.length) { GW.status("nothing to change"); return; }
        const box = h("div", null,
          h("p", null, pv.what + " for " + pv.character + ": " + pv.changed.length + " quests."),
          pv.rewards ? h("p", { class: "gw-dim" }, "Rewards: " + pv.rewards + ".") : null,
          pv.conflicts.length ? h("p", { class: "tt-bad" }, "Refused while the quest log holds " + pv.conflicts.map(id => qname(id) + " #" + id).join(", ") +
            (pv.operation === "complete_main" ? " (it would also be completed)" : " (its prerequisite would be forgotten)") + ". Drop those quests first.") : null,
          h("div", { class: "gw-sk-qlist" }, ...pv.changed.map(id => h("div", { class: "gw-dim" }, "#" + id + " " + qname(id) + ((rules[id] || {}).minlv ? " · Lv " + rules[id].minlv : "")))));
        if (pv.conflicts.length) { await GW.confirm({ title: label, el: box, ok: "Close", wide: true }); return; }
        if (!await GW.confirm({ title: label + "?", el: box, ok: "Apply", wide: true, danger: pv.operation === "reset" })) return;
        const r = await this.post("questchain", Object.assign({}, body, { apply: true, expected: pv.changed }), label);
        if (r) this.reload();
      };
      const chainBox = (this.locked() && !liveChain) ? null : h("div", { class: "gw-sk-tools" },
        h("span", { "data-help": "Mark every " + camp + " main-line quest (not the daily or repeatable ones) with a starting level up to this one, and their prerequisites, as completed. No rewards are granted. Shows the quests first." }, "Finish main chains through Lv "), upF,
        h("button", { class: "gw-btnx small", type: "button", onclick: () => { if (upF.valid()) chain({ operation: "complete_main", level: upto }, "Finish main chains through Lv " + upto); } }, "Preview"),
        h("button", { class: "gw-btnx small", type: "button", "data-help": "Forget every completed " + camp + " main-line quest (including the daily main line), so it can be taken again.",
          onclick: () => chain({ operation: "reset", scope: "main" }, "Reset completed main-line quests") }, "Reset main line"),
        h("button", { class: "gw-btnx small", type: "button", "data-help": "Forget every completed quest of this character.",
          onclick: () => chain({ operation: "reset", scope: "all" }, "Reset all completed quests") }, "Reset all completed"),
        liveChain ? h("span", { class: "gw-dim" }, " (live: applied by the game server)") : null);
      const tools = h("div", { class: "gw-sk-tools" },
        h("button", { class: "gw-btnx small", type: "button", "data-help": "Take every quest out of the quest log (they are not completed; the player can take them again).", disabled: carried.length ? null : true, onclick: async () => {
          if (!await GW.confirm({ title: "Clear the quest log?", message: carried.length + " quests leave the log (not completed).", danger: true })) return;
          const r = await this.post("quests", { ops: carried.map(q => ({ quest: q.quest_id, action: "drop" })), expected: Object.fromEntries(carried.map(q => [q.quest_id, 0])) }, "cleared the quest log");
          if (r) this.reload();
        } }, "Clear quest log"));
      // ---- advanced: daily counters + appraisal
      const adv = h("label", { class: "gw-sk-adv", "data-help": "Daily quest counters and the quest EXP appraisal are 'advanced' tables: the game itself manages them." },
        h("input", { type: "checkbox", checked: this.advanced || null, onchange: e => { this.advanced = e.target.checked; this.render(); } }), " Advanced: daily counters and EXP appraisal");
      const advBox = this.advanced ? this.questAdvanced() : null;
      add(this.body, this.lockNote("The game server rewrites the whole quest list from memory when it saves, so a quest edit on a logged-in character would be lost."),
        h("div", { class: "gw-sk-cols" },
          panel("Quests", { applies: "next_login", field: "character_quests.state", class: "gw-sk-qpanel" }, subs, chainBox, this.fieldset(tools), left),
          panel("Quest", { class: "gw-sk-side" }, this.fieldset(detail))),
        h("div", null, adv, advBox));
      if (this.sel.quest !== undefined && this.sel.quest !== null) this.questDetail(detail, this.sel.quest);
    },
    async questDetail(box, id) {
      const d = this.data, Q = d.quests, rules = this.rules, r = rules[id] || null;
      const cur = Q.rows.find(q => q.quest_id === Number(id)) || null;
      GW.clear(box);
      let e = {};
      try { e = await GW.catalog.get("quest", id); } catch (err) { e = {}; }
      const kinds = r ? [r.main ? chip("main line", "login", "Story quest" + (r.segment ? " (segment " + r.segment + ")" : "") + (r.prereq ? "; needs quest " + r.prereq + " first" : "")) : null,
        r.daily ? chip("daily", "restart", "Can be done once per quest day (the day rolls at 12:00 server time).") : null,
        r.repeat ? chip("repeatable 3/day", "restart", "Can be done three times per quest day.") : null,
        r.scroll ? chip("quest scroll", "never", "Started from a quest scroll item.") : null] : [];
      const state = cur ? STATE_NAMES[cur.state] || "state " + cur.state : "Not taken";
      add(box, h("div", { class: "gw-sk-qtitle" }, GW.text(e.title || "quest " + id)),
        h("div", null, chip(state, cur ? (cur.state === 1 ? "now" : "login") : "never", "character_quests.state: " + (cur ? cur.state : "no row")), " ", kinds),
        h("table", { class: "gw-sk-kv" },
          h("tr", null, h("th", { "data-field": "character_quests.quest_id" }, "Quest id"), h("td", null, "#" + id)),
          h("tr", null, h("th", { "data-help": "Level band of the quest (client Quest.xml / server roster)." }, "Level"), h("td", null, r ? r.minlv + "-" + r.maxlv : (e.min_level || "?"))),
          h("tr", null, h("th", { "data-help": "Who gives and who takes the quest back." }, "From / to"), h("td", null, (e.giver_name || (r && r.giver) || "?") + " → " + (e.responder_name || (r && r.responder) || "?"))),
          r ? h("tr", null, h("th", { "data-help": "Rewards the server pays at hand-in (before the server rates)." }, "Rewards"), h("td", null, [r.exp ? fmt(r.exp) + " EXP" : "", r.tp ? r.tp + " TP" : "", r.silver ? fmt(r.silver) + " Silver" : "", r.gold ? fmt(r.gold) + " Gold" : ""].filter(Boolean).join(", ") || "-")) : null,
          !r ? h("tr", null, h("td", { colspan: 2, class: "tt-bad" }, "Not in the server's quest roster: the server ignores this quest.")) : null),
        e.objectives ? h("div", { class: "gw-sk-qtext" }, GW.text(e.objectives)) : null);
      // objectives with counters
      const counters = cur ? cur.counters.slice() : [0, 0, 0, 0];
      const objs = [];
      if (r) { r.objectives.forEach((o, i) => objs.push({ slot: i, name: o.name, required: o.required })); if (r.collect) objs.push({ slot: 3, name: ((GW.catalog.loadedRow("item", r.collect.item) || {}).name || "quest item #" + r.collect.item), required: r.collect.required }); }
      const shownSlots = new Set(objs.map(o => o.slot));
      counters.forEach((c, i) => { if (c && !shownSlots.has(i)) objs.push({ slot: i, name: "counter " + (i + 1), required: null }); });
      const fields = [];
      if (objs.length) {
        const ob = h("div", { class: "gw-sk-objs" }, h("div", { class: "gw-sk-otitle", "data-field": "character_quests.progress" }, "Objectives (progress)"));
        for (const o of objs.sort((a, c) => a.slot - c.slot)) {
          const nf = GW.numberField({ label: o.name, value: counters[o.slot], min: 0, max: Math.max(o.required || 0, 32767), slider: !!o.required, onChange: v => { counters[o.slot] = v; } });
          fields.push(nf);
          ob.append(h("div", { class: "gw-sk-obj" }, nf, o.required ? h("span", { class: "gw-dim" }, " of " + fmt(o.required)) : null,
            o.required ? h("button", { class: "gw-btnx small", type: "button", "data-help": "Set this objective to done.", onclick: () => { nf.value = o.required; counters[o.slot] = o.required; } }, "Max") : null));
        }
        box.append(ob);
      }
      const exp = {}; exp[id] = cur ? cur.state : null;
      const act = (action, label, extra, help, danger) => h("button", { class: "gw-btnx", type: "button", "data-help": help, onclick: async () => {
        if (fields.some(f => !f.valid())) { GW.status("fix the red values first", "err"); return; }
        if (!await GW.confirm({ title: label + "?", message: (e.title || "quest " + id) + "\n\n" + help, danger })) return;
        const r2 = await this.post("quests", { ops: [Object.assign({ quest: Number(id), action }, extra || {})], expected: exp }, label + " " + (e.title || id));
        if (r2) this.reload();
      } }, label);
      box.append(h("div", { class: "gw-sk-qbtns" },
        (!cur || cur.state === 1) && r ? act("accept", "Put in quest log", { counters }, "The quest becomes carried (in the quest log) with the progress above.") : null,
        cur && cur.state === 0 ? act("progress", "Save progress", { counters }, "Write the objective counters above.") : null,
        r && (!cur || cur.state === 0) ? act("complete", "Mark completed", null, "The quest becomes completed: the server never offers it again and the next main-line step opens.") : null,
        cur ? act("drop", cur.state === 1 ? "Mark not done" : "Remove from log", null, cur.state === 1 ? "The completed mark is removed: the server can offer the quest again." : "The quest leaves the quest log (not completed).", true) : null));
    },
    questAdvanced() {
      const d = this.data, Q = d.quests;
      const qname = id => (GW.catalog.loadedRow("quest", id) || {}).name || "quest " + id;
      const tbl = h("table", { class: "gw-sk-tbl" }, h("tr", null, h("th", { "data-field": "character_quest_daily.quest_id" }, "Quest"), h("th", { "data-field": "character_quest_daily.reset_day" }, "Quest day"),
        h("th", { "data-field": "character_quest_daily.completions" }, "Done that day"), h("th", null, "")));
      for (const x of Q.daily) {
        let n = x.completions;
        const nf = GW.numberField({ field: "character_quest_daily.completions", label: "", value: n, min: 0, max: 1000, slider: false, onChange: v => { n = v; } });
        tbl.append(h("tr", null, h("td", { "data-tip-type": "quest", "data-tip-id": String(x.quest_id) }, qname(x.quest_id) + " #" + x.quest_id),
          h("td", { class: String(x.reset_day) === Q.quest_day ? "" : "gw-dim", "data-help": String(x.reset_day) === Q.quest_day ? "today's quest day" : "an older day: it counts as 0 today" }, String(x.reset_day)),
          h("td", null, nf), h("td", null,
            h("button", { class: "gw-btnx small", type: "button", onclick: async () => { const r = await this.post("daily", { quest: x.quest_id, completions: n, advanced: true }, "daily counter"); if (r) this.reload(); } }, "Save for today"),
            h("button", { class: "gw-btnx small", type: "button", "data-help": "Delete the counter: the quest can be done again today.", onclick: async () => { const r = await this.post("daily", { quest: x.quest_id, completions: 0, advanced: true }, "daily counter reset"); if (r) this.reload(); } }, "Reset"))));
      }
      if (!Q.daily.length) tbl.append(h("tr", null, h("td", { colspan: 4, class: "gw-dim" }, "No daily counters.")));
      const addBtn = h("button", { class: "gw-btnx small", type: "button", onclick: async () => {
        const id = await GW.pick("quest", { title: "Daily / repeatable quest", filter: r => { const x = this.rules[r.id]; return !!x && (x.daily || x.repeat) && (!x.camp || x.camp === d.camp_name); } });
        if (id === null || id === "") return;
        const r = await this.post("daily", { quest: Number(id), completions: (this.rules[id] || {}).per_day || 1, advanced: true }, "daily counter");
        if (r) this.reload();
      } }, "+ counter (mark a daily as done today)");
      const ap = Q.appraisal;
      let bp = ap ? ap.bonus_basis_points : Q.appraisal_default;
      const apF = GW.numberField({ field: "character_quest_appraisal.bonus_basis_points", label: "Bonus (basis points)", value: bp, min: 1, max: 1000000, onChange: v => { bp = v; } });
      return h("div", { class: "gw-sk-cols" },
        panel("Daily quest counters (quest day " + Q.quest_day + ")", { applies: "next_login", field: "character_quest_daily.completions" }, this.fieldset(tbl, addBtn)),
        panel("Quest EXP appraisal", { applies: "next_login", field: "character_quest_appraisal.bonus_basis_points", class: "gw-sk-side" },
          this.fieldset(h("p", null, ap ? "Passed: +" + (ap.bonus_basis_points / 100) + "% EXP on every quest hand-in." : "Not passed (the 'Increase EXP gain' tab of the quest log)."),
            apF, h("span", { class: "gw-dim" }, " 1000 = +10% (the game's value)"),
            h("div", { class: "gw-sk-qbtns" },
              h("button", { class: "gw-btnx", type: "button", onclick: async () => { if (!apF.valid()) return; const r = await this.post("appraisal", { bonus_basis_points: bp, advanced: true }, "quest EXP appraisal"); if (r) this.reload(); } }, ap ? "Save bonus" : "Give the appraisal"),
              ap ? h("button", { class: "gw-btnx", type: "button", onclick: async () => { const r = await this.post("appraisal", { bonus_basis_points: null, advanced: true }, "appraisal removed"); if (r) this.reload(); } }, "Remove") : null))));
    },
  };
  GW.module("skills", P);
})();
