/* static/modules/teleport-pages.js - B22 B26-T (the admin d121-d123): menu pages for the teleporter menu editor
   (static/modules/teleport-menu.js loads this file). Backend: modules/_teleport_pages.py.
   A page-1 button can open a second page (the capitals' "Go to the Northern lands", button 1104). Each row gets a
   "Page" choice: page 1, or the page of one of the page buttons. Page buttons go nowhere themselves. 12 per page. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;

  /** Mark the rows the server stored as page buttons (another row's page names them). */
  function fromServer(rows) {
    const parents = new Set(rows.filter(r => r.page).map(r => r.page));
    rows.forEach(r => { r.page_button = parents.has(r.sub); });
    return rows;
  }

  const pageButtons = rows => rows.filter(r => r.page_button && !r.page);

  /** "Page 1: 8 of 12 shown. Page 1104: 7 of 12 shown." */
  function summary(rows, max) {
    const parts = [["Page 1", rows.filter(r => !r.page && !r.hidden).length]];
    pageButtons(rows).forEach(b => parts.push(["Page " + b.sub, rows.filter(r => r.page === b.sub && !r.hidden).length]));
    return parts.map(([n, c]) => n + ": " + c + " of " + max + " shown").join(". ") + ".";
  }

  /** The Page select of one row (null when the server has no page support and no page is in use). */
  function control(r, rows, M, redraw) {
    const buttons = pageButtons(rows);
    if (!M.page_support && !buttons.length) return null;
    if (r.page_button) return h("span", { class: "gw-chip", "data-help": "This button opens a page of its own; it goes nowhere itself." }, "opens page " + r.sub);
    const sel = h("select", { "aria-label": "Page of this button", onchange: e => { r.page = e.target.value ? Number(e.target.value) : null; redraw(); } },
      h("option", { value: "" }, "Page 1"),
      buttons.filter(b => b.sub !== r.sub).map(b => h("option", { value: b.sub }, "Page " + b.sub)));
    sel.value = r.page ? String(r.page) : "";
    return h("label", null, "Page ", sel);
  }

  /** Free slots that can become a page button (the "+ Add" list). */
  function addOptions(free, info, strip) {
    return free.map(s => { const c = info(s); return h("option", { value: "page:" + s }, "New page button (button " + s + (c.label ? ": " + strip(c.label) : ", words come with a client update") + ")"); });
  }

  function newPageButton(sub) {
    return { sub, hidden: false, price: null, currency: "silver", map: null, x: null, z: null, level: null, name: null, page: null, page_button: true };
  }

  /** Rows of a page sit under it in the list (stable order otherwise), so positions follow the pages. */
  function ordered(rows) {
    const out = [];
    rows.filter(r => !r.page).forEach(r => {
      out.push(r);
      if (r.page_button) rows.filter(c => c.page === r.sub).forEach(c => out.push(c));
    });
    rows.filter(r => r.page && !out.includes(r)).forEach(r => out.push(r)); // a page whose button was removed
    return out;
  }

  window.GWTelePages = { fromServer, summary, control, addOptions, newPageButton, ordered };
})();
