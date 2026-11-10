/* shared/changes.js - the Change log drawer and Undo, shared by every page.
   GW.changes.open() / close() / refresh(); GW.changes.undo(change, full) - the same /api/undo the classic page
   uses (newest first, 409 "changed since" -> ask to overwrite, Restore = put back the whole pre-write dump). */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const ENGINE = { reborn: "character", rows: "rows", rates: "server rates", "reborn-live": "live (game server)" };
  let drawer = null, listEl = null, filterSel = null, timer = null;

  function ensure() {
    if (drawer) return drawer;
    filterSel = h("select", { class: "gw-cl-filter", "aria-label": "Show changes of" }, h("option", { value: "" }, "all pages"));
    listEl = h("div", { class: "gw-cl-list" });
    drawer = h("aside", { id: "gw-changes", class: "gw-changes gw-frame-normalwindow gw-text hidden", "aria-label": "Change log" },
      h("div", { class: "gw-wtitle" }, "Change log"),
      h("button", { class: "gw-sprite gw-close gw-modal-x", "aria-label": "Close the change log", onclick: () => GW.changes.close() }),
      h("div", { class: "gw-cl-bar" }, filterSel, h("button", { class: "gw-btnx small", type: "button", onclick: () => GW.changes.refresh() }, "Reload")),
      listEl);
    filterSel.addEventListener("change", () => GW.changes.refresh());
    document.body.append(drawer);
    return drawer;
  }

  function entry(c) {
    const undoable = !c.undone && !c.no_undo && !c.undo_of;
    const full = c.engine === "reborn" || c.engine === "rows";
    return h("div", { class: "gw-cl-item" + (c.undone ? " undone" : "") + (c.undo_of ? " undo" : "") },
      h("div", { class: "gw-cl-act" }, h("b", null, c.character || ""), " — ", c.action || ""),
      h("div", { class: "gw-cl-meta" }, c.time || "", " · ", ENGINE[c.engine] || c.engine || "", c.module ? " · " + c.module : "", " · ", c.database || "",
        c.undone ? h("b", { class: "tt-bad" }, " · UNDONE") : null),
      c.applies ? h("div", null, GW.appliesChip(c.applies, c.applies_text)) : null,
      c.output ? h("div", { class: "gw-cl-out" }, c.output) : null,
      c.verify && c.verify.errors && c.verify.errors.length ? h("div", { class: "tt-bad" }, "CHECK: " + c.verify.errors.map(e => (e.where || "") + ": " + (e.msg || "")).join("; ")) : null,
      c.result && c.result.identical ? h("div", { class: c.result.all_identical ? "gw-good" : "tt-bad" }, c.result.all_identical ? "undo check: rows identical" : "undo check: " + JSON.stringify(c.result.identical)) : null,
      (c.sql || []).length ? h("details", null, h("summary", null, "SQL (" + c.sql.length + ")"), h("pre", null, c.sql.map(x => x.sql).join("\n\n"))) : null,
      c.no_undo ? h("div", { class: "gw-dim" }, c.no_undo) : null,
      undoable ? h("div", { class: "gw-cl-buttons" },
        h("button", { class: "gw-btnx small", type: "button", "data-help": "Put back what this change changed (newest changes first).", onclick: () => GW.changes.undo(c, false) }, "Undo"),
        full ? h("button", { class: "gw-btnx small", type: "button", "data-help": "Put back EVERY row this change's backup holds, as it was before the write (also reverts what the game saved since).", onclick: () => GW.changes.undo(c, true) }, "Restore") : null) : null);
  }

  GW.changes = {
    open() { ensure().classList.remove("hidden"); GW.changes.refresh(); clearInterval(timer); timer = setInterval(() => { if (!drawer.classList.contains("hidden")) GW.changes.refresh(); }, 15000); },
    close() { if (drawer) drawer.classList.add("hidden"); clearInterval(timer); },
    toggle() { if (!drawer || drawer.classList.contains("hidden")) GW.changes.open(); else GW.changes.close(); },
    async refresh() {
      ensure();
      let all;
      try { all = await GW.api("/api/m/changes"); } catch (e) { GW.clear(listEl).append(h("div", { class: "tt-bad" }, e.message)); return; }
      const mods = Array.from(new Set(all.map(c => c.module).filter(Boolean))).sort();
      const cur = filterSel.value;
      GW.clear(filterSel).append(h("option", { value: "" }, "all pages (" + all.length + ")"), mods.map(m => h("option", { value: m }, m)));
      filterSel.value = mods.includes(cur) ? cur : "";
      const shown = filterSel.value ? all.filter(c => c.module === filterSel.value) : all;
      GW.clear(listEl);
      if (!shown.length) listEl.append(h("div", { class: "gw-dim" }, "No changes yet."));
      for (const c of shown.slice(0, 150)) listEl.append(entry(c));
      const n = all.filter(c => !c.undone && !c.undo_of && !c.no_undo).length;
      const badge = document.getElementById("gw-chg-count");
      if (badge) badge.textContent = n;
    },
    async undo(c, full) {
      const ok = await GW.confirm({ title: full ? "Restore the backup?" : "Undo this change?",
        message: (full ? "Every row in this change's backup goes back to how it was before the write. What the game saved since is lost too." :
          "The values this change wrote go back to what they were.") + "\n\n" + (c.character || "") + ": " + (c.action || "") +
          "\n\nRevision counters move forward, not back. Undo the newest changes first.", ok: full ? "Restore" : "Undo", danger: full });
      if (!ok) return;
      GW.status("undoing ...");
      let r;
      try {
        try { r = await GW.api("/api/undo", { change: c.id, full: !!full }); }
        catch (e) {
          if (!(e.status === 409 && e.data && e.data.conflict)) throw e;
          const lines = e.data.conflict.slice(0, 25).map(x => x.table + " " + x.row + ": " + x.what).join("\n");
          const go = await GW.confirm({ title: "Changed since that write", message: "These rows changed after that write (the game server or another edit):\n\n" + lines +
            "\n\nOverwrite them anyway?", ok: "Overwrite", danger: true });
          if (!go) { GW.status("undo cancelled", "err"); return; }
          r = await GW.api("/api/undo", { change: c.id, full: !!full, overwrite: true });
        }
        const res = r.result || {};
        GW.status(res.all_identical === false ? "undo done, but NOT identical: " + JSON.stringify(res.identical) : "undo done", res.all_identical === false ? "err" : "ok");
        GW.emit("change", r.change);
        GW.changes.refresh();
      } catch (e) { GW.status(e.message, "err"); GW.alert(e.message, "Undo refused"); }
    },
  };
})();
