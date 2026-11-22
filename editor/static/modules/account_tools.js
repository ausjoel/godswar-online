/* static/modules/account_tools.js - B21 item 24: character search, item search (illegal items) and account renumbering.
   Backend: modules/account_tools.py (search, items, renumber/preview, renumber/apply). */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const CLS = ["Warrior", "Champion", "Priest", "Mage"];
  const STATN = { max_hp: "Max HP", max_mp: "Max MP", physical_attack: "Physical attack", physical_defense: "Physical defense", magic_attack: "Magic attack",
    magic_defense: "Magic defense", hit: "Hit", dodge: "Dodge", critical: "Critical", critical_resistance: "Critical resistance", damage_absorb: "Damage absorb",
    physical_damage_bonus: "Physical damage bonus", magic_damage_bonus: "Magic damage bonus", cure_bonus: "Healing bonus", hp_recovery: "HP recovery",
    mp_recovery: "MP recovery", ignore_physical_defense: "Ignore physical defense", ignore_magic_defense: "Ignore magic defense", weapon_score: "Weapon score", armor_score: "Armor score" };
  const qs = o => Object.entries(o).filter(([, v]) => v !== "" && v !== null && v !== undefined && v !== false).map(([k, v]) => encodeURIComponent(k) + "=" + encodeURIComponent(v)).join("&");
  const num = (label, help, w) => h("input", { type: "number", "aria-label": label, "data-help": help || null, style: "width:" + (w || 6) + "em" });

  GW.module("account_tools", {
    async mount(el, ctx) {
      let alive = true, tab = "search";
      this.cleanup = () => { alive = false; };
      const body = h("div");
      const tabs = h("div", { class: "gw-at-tabs", role: "tablist" });
      function drawTabs() {
        GW.clear(tabs).append(...[["search", "Find characters"], ["items", "Find items"], ["renumber", "Renumber accounts"]].map(([k, t]) =>
          h("button", { class: "gw-tabx" + (tab === k ? " on" : ""), type: "button", role: "tab", "aria-selected": tab === k ? "true" : "false",
            onclick: () => { tab = k; drawTabs(); draw(); } }, t)));
      }
      el.append(tabs, body);
      function draw() { GW.clear(body); if (tab === "search") search(); else if (tab === "items") items(); else renumber(); }

      // ------------------------------------------------------------------ characters
      function search() {
        const q = h("input", { type: "search", placeholder: "name, user name, e-mail or number", "aria-label": "Search text", style: "width:16em" });
        const cls = CLS.map((n, i) => h("label", null, h("input", { type: "checkbox", value: String(i), "aria-label": n }), " " + n));
        const fac = h("select", { "aria-label": "Faction" }, h("option", { value: "" }, "Both factions"), h("option", { value: "0" }, "Sparta"), h("option", { value: "1" }, "Athens"));
        const lmin = num("Level from"), lmax = num("Level to");
        const stat = h("select", { "aria-label": "Stat" }, h("option", { value: "" }, "(no stat)"), Object.entries(STATN).map(([k, n]) => h("option", { value: k }, n)));
        const smin = num("Stat at least", null, 8), smax = num("Stat at most", null, 8), silver = num("Silver at least", null, 9), gold = num("Gold at least", null, 8);
        const flag = (k, t) => h("select", { "aria-label": t }, h("option", { value: "" }, t + ": any"), h("option", { value: "1" }, t + ": yes"), h("option", { value: "0" }, t + ": no"));
        const online = flag("online", "Online"), banned = flag("banned", "Banned"), gm = flag("gm", "GM"), donor = flag("donator", "Donator");
        const deleted = h("input", { type: "checkbox", "aria-label": "Include deleted characters" });
        const out = h("div"), status = h("p", { role: "status" });
        async function run() {
          status.textContent = "Searching ...";
          const p = { q: q.value.trim(), class: cls.map(l => l.firstChild).filter(c => c.checked).map(c => c.value).join(","), faction: fac.value,
            level_min: lmin.value, level_max: lmax.value, stat: stat.value, stat_min: smin.value, stat_max: smax.value, silver_min: silver.value,
            gold_min: gold.value, online: online.value, banned: banned.value, gm: gm.value, donator: donor.value, deleted: deleted.checked ? "1" : "" };
          try {
            const r = await ctx.api("search?" + qs(p));
            if (!alive) return;
            status.textContent = r.characters.length + " character(s) on " + r.accounts + " account(s)" + (r.characters.length >= r.limit ? " (first " + r.limit + ")" : "") + ".";
            const st = stat.value;
            GW.clear(out).append(h("table", { class: "gw-at-table" },
              h("tr", null, ["Character", "Account", "Class", "Faction", "Level", "Silver", "Gold", st ? STATN[st] : "Max HP", "Online", ""].map(t => h("th", null, t))),
              r.characters.map(c => h("tr", { class: c.lifecycle_state && c.lifecycle_state !== "active" ? "gw-dim" : "" },
                h("td", null, c.name + " #" + c.id), h("td", null, c.username + " #" + c.account_id + (c.account_status === 1 ? " (banned)" : "")),
                h("td", null, c.class), h("td", null, c.faction), h("td", null, String(c.level)), h("td", null, GW.fmt(c.silver)), h("td", null, GW.fmt(c.gold)),
                h("td", null, c.stats ? GW.fmt(c.stats[st || "max_hp"]) : "-"), h("td", null, c.online ? "yes" : ""),
                h("td", null, h("a", { href: "#/character/" + c.id, "data-help": "Open this character's sheet." }, "Open"))))));
          } catch (e) { if (alive) status.textContent = e.message; }
        }
        q.addEventListener("keydown", e => { if (e.key === "Enter") run(); });
        body.append(h("div", { class: "gw-at-controls" }, q, ...cls, fac, h("label", null, "Level ", lmin, " - ", lmax)),
          h("div", { class: "gw-at-controls" }, h("label", { "data-help": "A fighting stat from the server's character sheet (worn gear, level and buffs as the server computes them). The list is sorted by it." }, "Stat ", stat), smin, " - ", smax,
            h("label", null, "Silver ≥ ", silver), h("label", null, "Gold ≥ ", gold)),
          h("div", { class: "gw-at-controls" }, online, banned, gm, donor, h("label", null, deleted, " Include deleted"),
            h("button", { class: "gw-btnx", type: "button", onclick: run }, "Search")), status, out);
        run();
      }

      // ------------------------------------------------------------------ items
      function items() {
        const q = h("input", { type: "search", placeholder: "item name or number(s) 1001,1002", "aria-label": "Item", style: "width:18em" });
        const locs = [["bag", "Bags"], ["warehouse", "Warehouses"], ["equip", "Worn"]].map(([v, t]) => h("label", null, h("input", { type: "checkbox", value: v, checked: true, "aria-label": t }), " " + t));
        const illegal = h("input", { type: "checkbox", checked: true, "aria-label": "Only items with a problem" });
        const deep = h("input", { type: "checkbox", "aria-label": "Full check" });
        const minq = num("Quality at least", null, 4), ming = num("Grade at least", null, 4);
        const out = h("div"), status = h("p", { role: "status" });
        async function run() {
          status.textContent = "Searching every bag and warehouse ...";
          const p = { q: q.value.trim(), loc: locs.map(l => l.firstChild).filter(c => c.checked).map(c => c.value).join(","), illegal: illegal.checked ? "1" : "",
            deep: deep.checked ? "1" : "", min_quality: minq.value, min_grade: ming.value };
          try {
            const [r] = await Promise.all([ctx.api("items?" + qs(p)), GW.catalog.load("item").catch(() => null)]);
            if (!alive) return;
            status.textContent = r.count + " item(s)" + (r.flagged ? ", " + r.flagged + " with a problem" : "") + "." + (r.note ? " " + r.note : "") +
              (r.truncated ? " More items match: only the first " + r.count + " are listed (searched " + r.scanned + " rows); narrow the search." : "");
            GW.clear(out).append(h("table", { class: "gw-at-table" },
              h("tr", null, ["", "Item", "Count", "Q / G", "Character", "Account", "Where", "Problem"].map(t => h("th", null, t))),
              r.items.map(x => h("tr", null,
                h("td", null, GW.cell({ sprite: (GW.catalog.loadedRow("item", x.prop_id) || {}).sprite, name: x.name, quality: x.item_quality, tipType: "item", tipId: x.prop_id,
                  tipInst: { item_quality: x.item_quality, item_grade: x.item_grade, bound: x.bound, stack: x.stack, holy_suit_code: x.holy_suit_code,
                    attrs: [1, 2, 3, 4, 5].map(i => x["attribute" + i]).filter(a => a !== null && a !== undefined).map(id => ({ id })) } })),
                h("td", null, x.name + " #" + x.prop_id), h("td", null, String(x.stack)), h("td", null, "Q" + x.item_quality + " / G" + x.item_grade),
                h("td", null, h("a", { href: "#/inventory/" + x.user_id }, x.character)), h("td", null, x.username + " #" + x.account_id),
                h("td", null, x.where + " " + (x.slot_index + 1)), h("td", { class: x.flags.length ? "tt-bad" : "" }, x.flags.join("; ") || "-")))));
          } catch (e) { if (alive) status.textContent = e.message; }
        }
        q.addEventListener("keydown", e => { if (e.key === "Enter") run(); });
        body.append(h("p", { class: "gw-dim" }, "Searches every character's kit bag, warehouse and worn gear. Problems found: not in the server or the client, GM and dead items, stacks above 99 or above the item's stack size, quality or grade above the client, more than 4 sockets, worn below its level. Full check also runs the Inventory page's item check on each row (slower)."),
          h("div", { class: "gw-at-controls" }, q, ...locs, h("label", null, illegal, " Only items with a problem"), h("label", { "data-help": "Runs the Inventory page's full check (stats, sockets, class stats) on every item. Slower on a big database." }, deep, " Full check"),
            h("label", null, "Quality ≥ ", minq), h("label", null, "Grade ≥ ", ming), h("button", { class: "gw-btnx", type: "button", onclick: run }, "Search")), status, out);
        run();
      }

      // ------------------------------------------------------------------ renumber
      function renumber() {
        const mode = h("select", { "aria-label": "Renumber how" }, h("option", { value: "compact" }, "Close the gaps: 1, 2, 3 ... in today's order"), h("option", { value: "set" }, "Give one account a new number"));
        const from = num("Account number"), to = num("New number"), start = num("First number");
        start.value = "1";
        const cfg = h("input", { type: "checkbox", "aria-label": "I will update the server configuration" });
        const status = h("p", { role: "status" }), out = h("div");
        let view = null, last = null;
        const applyBtn = h("button", { class: "gw-btnx", type: "button", disabled: true, onclick: () => apply() }, "Renumber");
        const opts = () => mode.value === "set" ? { mode: "set", from: Number(from.value), to: Number(to.value) } : { mode: "compact", start: Number(start.value || 1) };
        function sync() { from.parentNode && (from.parentNode.style.display = mode.value === "set" ? "" : "none"); start.parentNode && (start.parentNode.style.display = mode.value === "set" ? "none" : ""); }
        async function preview(body) {
          view = null; applyBtn.disabled = true; status.textContent = "Dry run ..."; GW.clear(out);
          try {
            const v = await ctx.api("renumber/preview", body || opts());
            if (!alive) return;
            view = Object.assign(v, { body: body || opts() });
            const n = v.moves.length;
            status.textContent = n ? n + " account(s) move, " + v.rows_total + " row(s) in " + v.tables.length + " table(s). Nothing is written until you press Renumber." : "Nothing to do: the accounts already have these numbers.";
            out.append(...[
              ...v.warnings.map(w => h("p", { class: "pt-legalwarn tt-bad" }, w)),
              h("p", { class: "gw-dim" }, v.advice),
              n ? h("table", { class: "gw-at-table" }, h("tr", null, ["Now", "Becomes", "User name", "Characters", "Online"].map(t => h("th", null, t))),
                v.moves.map(m => h("tr", null, h("td", null, "#" + m.from), h("td", { class: "gw-good" }, "#" + m.to), h("td", null, m.username), h("td", null, m.characters.join(", ") || "-"), h("td", { class: m.online ? "tt-bad" : "" }, m.online ? "yes" : "")))) : null,
              n ? h("details", null, h("summary", null, "Rows that change (" + v.rows_total + ")"),
                h("table", { class: "gw-at-table" }, h("tr", null, h("th", null, "Table"), h("th", null, "Column"), h("th", null, "Rows")),
                  v.tables.map(t => h("tr", null, h("td", null, t.table), h("td", null, t.column + (t.array ? " (list)" : "")), h("td", null, String(t.rows)))))) : null,
              v.config_named.length ? h("label", { class: "gw-at-controls" }, cfg, " I will change the server configuration for account " + v.config_named.map(x => "#" + x).join(", ") + " too") : null].filter(Boolean));
            applyBtn.disabled = !n || !v.writable || v.online.length > 0 || (v.online_all || []).length > 0 || v.unreviewed.length > 0 || !v.can_replica;   // B22 d56: nobody online server-wide
          } catch (e) { if (alive) status.textContent = e.message; }
        }
        async function apply() {
          if (!view) return;
          const v = view;
          if (!await GW.confirm({ title: "Renumber " + v.moves.length + " account(s)?", danger: true,
            message: v.moves.map(m => "#" + m.from + " → #" + m.to + " " + m.username).join("\n") + "\n\n" + v.rows_total + " rows change in one transaction. There is no Undo button: the result offers Renumber back. Stop the game server first.", ok: "Renumber" })) return;
          try {
            const r = await ctx.api("renumber/apply", Object.assign({}, v.body, { token: v.token, confirm_config: cfg.checked }));
            GW.writeDone(r, "Accounts renumbered");
            last = r;
            await preview();
            out.prepend(h("div", { class: "gw-frame-simplewindow gw-at-done" }, h("p", null, "Done: " + Object.entries(r.map).map(([a, b]) => "#" + a + " → #" + b).join(", ") + "."),
              h("button", { class: "gw-btnx", type: "button", "data-help": "Puts every account back to the number it had before this renumber (a second renumber with the inverse map).",
                onclick: () => preview({ mode: "map", map: last.inverse }) }, "Renumber back (dry run)")));
          } catch (e) { await GW.writeError(e); }
        }
        [mode, from, to, start].forEach(c => c.addEventListener("change", () => { sync(); preview(); }));
        body.append(h("p", { class: "gw-dim" }, "Changes account numbers and every row that points at them (characters, realm, ledgers, rewards, sign-up e-mail) in one transaction, after a dry run. It runs only while nobody at all is online on the server; the dry run names anyone who is."),
          h("div", { class: "gw-at-controls" }, mode, h("label", null, "Start at ", start), h("label", null, "Account #", from, " → #", to),
            h("button", { class: "gw-btnx", type: "button", onclick: () => preview() }, "Dry run"), applyBtn), status, out);
        sync(); preview();
      }
      drawTabs(); draw();
    },
    unmount() { if (this.cleanup) this.cleanup(); },
  });
})();
