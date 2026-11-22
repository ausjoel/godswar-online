/* static/modules/awards.js - the daily Online Award: edit the item list, apply a preset, publish a new revision.
   Backend: modules/awards.py (validation mirrors the server's start-up checks; reaches players after a gw251 restart). */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const irow = id => (GW.catalog.loadedRow ? GW.catalog.loadedRow("item", id) : null) || {};

  GW.module("awards", {
    async mount(el, ctx) {
      let D = null, draft = [], checkTimer = null, X = { templates: [], hidden: [], online_minutes: {} }, showHidden = false, showRemoved = false;   // X: B21 item 29
      const top = h("div"), editor = h("div", { class: "gw-aw-editor" }), side = h("div", { class: "gw-aw-side" });
      el.append(top, h("div", { class: "gw-aw-body" }, editor, side));

      async function load() {
        [D] = await Promise.all([ctx.api("overview"), GW.catalog.load("item").catch(() => null)]);
        try { X = await ctx.api("extras" + (showRemoved ? "?archived=1" : "")); } catch (e) { X = { templates: [], hidden: [], online_minutes: { supported: false, reason: e.message } }; }
        const cur = (D.revisions || []).find(r => r.revision === (D.publication || {}).revision) || { entries: [] };
        draft = cur.entries.map(e => ({ item_id: e.item_id, quantity: e.quantity, item_quality: e.item_quality, bound: e.bound }));
        drawTop(); drawEditor(); drawSide();
      }
      function drawTop() {
        const p = D.publication || {};
        GW.clear(top).append(
          h("p", null, "The Online Award is what a character gets once per realm day from the ", h("b", null, "online reward"), " NPC (Athens_132 / Sparta_132). ",
            "Published revision ", h("b", null, String(p.revision)), " by ", String(p.updated_by || "?"), ". Claims in the last 24 hours: ", String(D.claims_today), ". ",
            GW.appliesChip("after_restart")),
          h("p", { class: "gw-dim" }, "Each save publishes a NEW revision (the old ones stay in the history and can be published again; that is the undo). " +
            "The running server keeps the award it read when it started, so players get the new list after the next server restart. " +
            "Everything the server checks at start-up is checked here first: 1-" + D.limits.rows + " rows, " + D.limits.total + " items in total, quality 1-16, stack size from the item, eggs only with a pet profile for that quality."));
      }
      function row(r, i) {
        const it = irow(r.item_id), egg = D.eggs[String(r.item_id)];
        const q = egg ? h("select", { "aria-label": "Quality (pet aptitude) of " + (it.name || r.item_id), onchange: e => { r.item_quality = Number(e.target.value); changed(); } },
          egg.map(a => h("option", { value: a, selected: a === r.item_quality || null }, "aptitude " + a)))
          : h("input", { type: "number", min: 1, max: 16, value: r.item_quality, class: "gw-aw-num", "aria-label": "Quality of " + (it.name || r.item_id), onchange: e => { r.item_quality = Number(e.target.value); changed(); } });
        const el = h("div", { class: "gw-aw-row", draggable: "true", "data-i": i },
          h("span", { class: "gw-npc-grip", "aria-hidden": "true" }, "⠿"),
          GW.cell({ sprite: it.sprite, name: it.name, count: r.quantity, tipType: "item", tipId: r.item_id }),
          h("span", { class: "gw-aw-name" }, it.name || "item #" + r.item_id, h("span", { class: "gw-dim" }, " #" + r.item_id + (egg ? " · pet egg" : ""))),
          h("label", null, "Count ", h("input", { type: "number", min: 1, max: 127, value: r.quantity, class: "gw-aw-num", "aria-label": "Count of " + (it.name || r.item_id), onchange: e => { r.quantity = Number(e.target.value); changed(); } })),
          h("label", null, egg ? "Aptitude " : "Quality ", q),
          h("label", null, h("input", { type: "checkbox", checked: r.bound ? true : null, "aria-label": "Bound", onchange: e => { r.bound = e.target.checked ? 1 : 0; changed(); } }), " bound"),
          h("button", { class: "gw-btnx small", type: "button", onclick: () => { draft.splice(i, 1); changed(true); } }, "Remove"));
        el.addEventListener("dragstart", e => { if (e.target.closest && e.target.closest("input,select,button")) { e.preventDefault(); return; } e.dataTransfer.setData("text/plain", String(i)); el.classList.add("gw-npc-dragging"); });
        el.addEventListener("dragend", () => el.classList.remove("gw-npc-dragging"));
        el.addEventListener("dragover", e => { e.preventDefault(); el.classList.add("gw-npc-drop-before"); });
        el.addEventListener("dragleave", () => el.classList.remove("gw-npc-drop-before"));
        el.addEventListener("drop", e => { e.preventDefault(); const from = Number(e.dataTransfer.getData("text/plain")); if (isNaN(from) || from === i) return; const [x] = draft.splice(from, 1); draft.splice(from < i ? i - 1 : i, 0, x); changed(true); });
        return el;
      }
      const status = h("div", { class: "gw-aw-status", role: "status" });
      function drawEditor() {
        GW.clear(editor).append(h("h3", null, "Award items (draft)"), h("p", { class: "gw-dim" }, "Players receive them in this order. Drag to reorder."),
          h("div", { class: "gw-aw-rows" }, draft.length ? draft.map(row) : h("p", { class: "tt-bad" }, "Empty: the server needs at least one row.")),
          h("div", { class: "gw-inv-btns" },
            h("button", { class: "gw-btnx", type: "button", onclick: add }, "+ Add items"),
            h("button", { class: "gw-btnx", type: "button", disabled: !D.writable || null, onclick: publish }, "Publish as revision " + ((D.publication || {}).revision + 1)),
            h("button", { class: "gw-btnx", type: "button", onclick: load }, "Discard draft"),
            h("button", { class: "gw-btnx", type: "button", "data-help": "Keep this item list as a named template (it is checked like a publish). Load it into the draft later and Publish.", onclick: saveTemplate }, "Save as template")),
          status);
        check();
      }
      function changed(redraw) { if (redraw) drawEditor(); else check(); }
      function check() {
        clearTimeout(checkTimer);
        checkTimer = setTimeout(async () => {
          GW.clear(status).append(h("span", { class: "gw-dim" }, "checking ..."));
          try {
            const r = await ctx.api("check", { rows: draft });
            const same = r.sha256 === (D.publication || {}).balance_sha256;
            GW.clear(status).append(h("span", { class: same ? "gw-dim" : "tt-green" }, same ? "This is the published award." :
              "OK for the server: " + r.sums.total + " items, " + r.sums.slots + " bag slots needed (an empty bag has 96)."));
          } catch (e) {
            GW.clear(status).append(h("pre", { class: "tt-bad gw-aw-err" }, e.message));
          }
        }, 250);
      }
      async function add() {
        const picked = await GW.pickMany("item", { title: "Add to the Online Award", allow: r => !/\b(noclient|noserver)\b/.test(r.flags || ""), max: 16 });
        if (!picked) return;
        for (const p of picked) {
          const egg = D.eggs[String(p.id)];
          let aptitudes = egg;
          if (!egg) { try { const t = await ctx.api("item", { item_id: p.id }); if (t.species !== null && t.species !== undefined) { D.eggs[String(p.id)] = t.profiles; aptitudes = t.profiles; } } catch (e) { GW.status(e.message, "err"); continue; } }
          draft.push({ item_id: p.id, quantity: 1, item_quality: aptitudes && aptitudes.length ? aptitudes[0] : 1, bound: 0 });
        }
        changed(true);
      }
      async function publish() {
        let r;
        try { r = await ctx.api("check", { rows: draft }); }
        catch (e) { await GW.alert(e.message, "Not published"); return; }
        const p = D.publication || {};
        const list = r.rows.map(x => "  " + x.name + " x" + x.quantity + (x.item_quality !== 1 ? " (quality " + x.item_quality + ")" : "") + (x.bound ? ", bound" : "")).join("\n");
        if (!await GW.confirm({ title: "Publish revision " + (p.revision + 1) + "?", message: "Every character's daily Online Award becomes:\n\n" + list +
          "\n\nIt reaches players after the next server restart. The old version stays in the history (Publish this again = undo).", ok: "Publish" })) return;
        try {
          const res = await ctx.api("publish", { expected_revision: p.revision, rows: draft });
          GW.writeDone(res, "Online Award revision " + res.revision + " published (after restart)");
          await load();
        } catch (e) { if (await GW.writeError(e) === "conflict") await load(); }
      }
      function entriesView(entries) {
        return h("div", { class: "gw-aw-mini" }, entries.map(e => { const it = irow(e.item_id); return GW.cell({ sprite: it.sprite, name: it.name || e.name, count: e.quantity, tipType: "item", tipId: e.item_id,
          help: (it.name || e.name || "#" + e.item_id) + " x" + e.quantity + (e.item_quality !== 1 ? " · quality " + e.item_quality : "") + (e.bound ? " · bound" : "") }); }));
      }
      // ---- B21 item 29: required online time, templates, hiding old revisions
      async function saveTemplate() {
        const name = h("input", { type: "text", maxlength: 60, "aria-label": "Template name", placeholder: "e.g. Weekend award" });
        if (!await GW.confirm({ title: "Save the draft as a template", ok: "Save", el: h("div", null, h("p", null, "Name:"), name) })) return;
        try { await ctx.api("templates/save", { name: name.value, rows: draft }); GW.status("template saved", "ok"); await load(); }
        catch (e) { await GW.alert(e.message, "Not saved"); }
      }
      function onlineBox() {
        const m = X.online_minutes || {};
        if (!m.supported) return h("div", { class: "gw-aw-preset" }, h("b", null, "Minutes online first"), h("p", { class: "gw-dim" }, m.reason || "Unavailable."));
        const inp = h("input", { type: "number", min: m.min, max: m.max, value: m.value, class: "gw-aw-num", "aria-label": "Minutes online before the award" });
        return h("div", { class: "gw-aw-preset" }, h("b", { "data-help": "How long a character must be in the world (this session) before the Online Award can be collected. 0 = at once, as in the original game. Applies to the next claim; no restart." }, "Minutes online first"),
          h("p", null, inp, " minutes ", h("button", { class: "gw-btnx small", type: "button", onclick: async () => {
            try { const r = await ctx.api("online-minutes", { minutes: Number(inp.value), expected: m.value }); GW.writeDone(r, "Online Award: " + r.value + " minutes online first"); await load(); }
            catch (e) { if (await GW.writeError(e) === "conflict") await load(); } } }, "Save")),
          h("p", { class: "gw-dim" }, m.value ? "Players must stay online " + m.value + " minute(s) after logging in; earlier they get one chat line saying how long is left." : "Now: at once (the original game)."));
      }
      function drawSide() {
        GW.clear(side).append(h("h3", null, "Online time"), onlineBox(), h("h3", null, "Templates"),
          h("label", { class: "gw-dim" }, h("input", { type: "checkbox", checked: showRemoved || null, onchange: async e => { showRemoved = e.target.checked; await load(); } }), " show removed"));
        for (const t of X.templates) {
          side.append(h("div", { class: "gw-aw-preset" + (t.archived ? " gen" : "") },
            h("b", null, t.name), h("span", { class: "gw-dim" }, " " + (t.created_by || "") + " · " + new Date(t.created * 1000).toLocaleDateString()),
            entriesView(t.rows),
            h("button", { class: "gw-btnx small", type: "button", onclick: () => { draft = t.rows.map(x => ({ item_id: x.item_id, quantity: x.quantity, item_quality: x.item_quality, bound: x.bound })); changed(true); GW.status("Template loaded into the draft. Nothing is saved until you press Publish.", "ok"); } }, "Load into draft"),
            " ",
            h("button", { class: "gw-btnx small" + (t.archived ? "" : " danger"), type: "button", onclick: async () => { await ctx.api("templates/" + t.id + "/remove", { restore: !!t.archived }); await load(); } }, t.archived ? "Restore" : "Remove")));
        }
        if (!X.templates.length) side.append(h("p", { class: "gw-dim" }, "No saved templates yet: build a draft and press Save as template."));
        side.append(h("h3", null, "Presets"));
        for (const p of D.presets) {
          side.append(h("div", { class: "gw-aw-preset" + (p.generated ? " gen" : "") },
            h("b", null, p.title), p.generated ? h("span", { class: "gw-chip never", "data-help": "Made up for this server; not from the original game." }, "Our own") : null,
            entriesView(p.rows), h("p", { class: "gw-dim" }, p.why),
            h("button", { class: "gw-btnx small", type: "button", onclick: () => { draft = p.rows.map(x => ({ item_id: x.item_id, quantity: x.quantity, item_quality: x.item_quality, bound: x.bound })); changed(true); GW.status("Preset loaded into the draft. Nothing is saved until you press Publish.", "ok"); } }, "Load into draft")));
        }
        side.append(h("h3", null, "History"), h("label", { class: "gw-dim" }, h("input", { type: "checkbox", checked: showHidden || null, onchange: e => { showHidden = e.target.checked; drawSide(); } }), " show hidden (" + X.hidden.length + ")"));
        for (const r of D.revisions) {
          const live = r.revision === (D.publication || {}).revision;
          const hidden = X.hidden.includes(r.revision);
          if (hidden && !showHidden) continue;
          side.append(h("div", { class: "gw-aw-preset" + (live ? " live" : "") },
            h("b", null, "Revision " + r.revision), live ? h("span", { class: "gw-chip now" }, "published") : null,
            h("span", { class: "gw-dim" }, " " + (r.created_by || "") + " · " + String(r.created_at || "").slice(0, 16).replace("T", " ")),
            entriesView(r.entries),
            live ? null : h("button", { class: "gw-btnx small", type: "button", onclick: () => { draft = r.entries.map(e => ({ item_id: e.item_id, quantity: e.quantity, item_quality: e.item_quality, bound: e.bound })); changed(true); GW.status("Revision " + r.revision + " loaded into the draft: press Publish to make it the award again (that is the undo).", "ok"); } }, "Publish this again"),
            live ? null : h("button", { class: "gw-btnx small", type: "button", "data-help": "Old revisions stay in the database (they cannot be deleted); this hides it from this list.", onclick: async () => { await ctx.api("revisions/" + r.revision + "/hide", { hide: !hidden }); await load(); } }, hidden ? "Unhide" : "Hide")));
        }
      }
      try { await load(); } catch (e) { GW.clear(editor).append(h("p", { class: "tt-bad" }, e.message)); }
    }
  });
})();
