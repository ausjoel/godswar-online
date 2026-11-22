/* shared/tooltip.js - one hover box for the whole page (UIHelper / ItemView look).
   Any element with
     data-field="table.column"            -> the glossary help for that field (what it is, range, where stored,
                                             when an edit applies, why it is read-only)
     data-help="plain text"               -> that text
     data-tip-type="item|skill|petskill|pet|status|talent|appendattr|holystone|quest|title|monster|npc|map|..."
     data-tip-id="<id>" [data-tip-inst='{"item_quality":5,...}']  -> the game-style tooltip of that entry
   gets it after ~250 ms of hover (or keyboard focus). Nothing else to call.

   Help text format (one house style for every hover): one short plain sentence that says what the thing does,
   then at most 2-3 short facts (limits, when a change applies, risk). GW.helpText({title, text, facts, list, note})
   builds such a text; GW.helpNode(text) draws it: long text wraps, lists are real lists. Line conventions, all
   optional, so old plain strings still work:
     "# Title"        heading (first line only)
     "• item" / "- item"   list item (consecutive lines make one list)
     "Label: value"   a fact; the label (a capital, at most 24 letters) is highlighted
     "~ note"         small dim note (where it is stored and the like)
     blank line       a gap. The game's |cAARRGGBB colour codes still work inside any line. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  let box = null, timer = null, current = null, seq = 0, lastX = 0, lastY = 0;
  const SEL = "[data-field],[data-help],[data-tip-type]";

  function ensure() {
    if (!box) { box = h("div", { id: "gw-tip", class: "gw-tip-box hidden", role: "tooltip" }); document.body.append(box); }
    return box;
  }
  function place(x, y) {
    const b = ensure();
    const w = b.offsetWidth, ht = b.offsetHeight;
    let left = x + 16, top = y + 16;
    if (left + w > window.innerWidth - 8) left = Math.max(8, x - w - 12);
    if (top + ht > window.innerHeight - 8) top = Math.max(8, window.innerHeight - ht - 8);
    b.style.left = left + "px"; b.style.top = top + "px";
  }
  function hide() { clearTimeout(timer); current = null; seq++; if (box) box.classList.add("hidden"); }
  GW.hideTip = hide;

  // ------------------------------------------------------------ the shared help renderer
  const FACT = /^([A-Z][A-Za-z' ]{1,23}):\s+(\S.*)$/;
  // GW.helpText({title, text, facts: [[label, value] | "sentence"], list: ["..."], note}) -> the string for data-help
  GW.helpText = function (o) {
    const out = [];
    if (o.title) out.push("# " + o.title);
    if (o.text) out.push(String(o.text).trim());
    const facts = (o.facts || []).filter(f => f && (Array.isArray(f) ? f[1] !== null && f[1] !== undefined && f[1] !== "" : true));
    if (facts.length) { out.push(""); for (const f of facts) out.push(Array.isArray(f) ? f[0] + ": " + f[1] : String(f)); }
    const list = (o.list || []).filter(Boolean);
    if (list.length) { if (o.listTitle) out.push("", o.listTitle + ":"); for (const x of list) out.push("• " + x); }
    if (o.note) out.push("~ " + o.note);
    return out.join("\n");
  };
  GW.helpNode = function (text) {
    const root = h("div", { class: "gw-tooltip gw-help" });
    const lines = String(text === null || text === undefined ? "" : text).replace(/\r/g, "").split("\n");
    let ul = null, gap = false, first = true;
    for (const raw of lines) {
      const line = raw.trim();
      if (!line) { ul = null; gap = !first; continue; }
      const plain = line.replace(/\|c[0-9A-Fa-f]{8}|\|r/g, "");
      const item = /^(?:•|-|\*)\s+(.*)$/.exec(plain);
      if (item) {
        if (!ul) { ul = h("ul", { class: "tt-list" + (gap ? " tt-gap" : "") }); root.append(ul); }
        ul.append(h("li", null, GW.text(line.replace(/^(?:\|c[0-9A-Fa-f]{8})?\s*(?:•|-|\*)\s+/, ""))));
        gap = false; first = false; continue;
      }
      ul = null;
      let node;
      if (first && plain.startsWith("# ")) node = h("div", { class: "tt-name tt-left" }, plain.slice(2));
      else if (plain.startsWith("~ ")) node = h("div", { class: "tt-line tt-small tt-note" }, plain.slice(2));
      else {
        const fact = plain === line ? FACT.exec(line) : null;
        node = fact ? h("div", { class: "tt-line tt-fact" }, h("span", { class: "tt-key" }, fact[1] + ": "), fact[2])
          : h("div", { class: "tt-line" }, GW.text(line));
      }
      if (gap) node.classList.add("tt-gap");
      root.append(node);
      gap = false; first = false;
    }
    return root;
  };

  async function content(el) {
    if (el.dataset.tipType && el.dataset.tipId !== undefined && el.dataset.tipId !== "") {
      let inst = null;
      if (el.dataset.tipInst) { try { inst = JSON.parse(el.dataset.tipInst); } catch (e) { inst = null; } }
      return GW.render.tooltip(el.dataset.tipType, el.dataset.tipId, inst);
    }
    if (el.dataset.field) {
      const f = await GW.glossary.field(el.dataset.field);
      const node = GW.render.fieldHelp(f);
      if (el.dataset.help) node.append(h("div", { class: "tt-rule" }), ...GW.helpNode(el.dataset.help).childNodes);
      return node;
    }
    return GW.helpNode(el.dataset.help);
  }
  async function show(el) {
    const my = ++seq;
    let node;
    try { node = await content(el); } catch (e) { node = h("div", { class: "gw-tooltip gw-help" }, h("span", { class: "tt-bad" }, e.message)); }
    if (my !== seq || current !== el) return;
    const b = ensure();
    GW.clear(b).append(node);
    b.classList.remove("hidden");
    place(lastX, lastY);
  }
  // GW.showTipFor(el) - show now (e.g. after a re-render under the pointer)
  GW.showTipFor = el => { current = el; show(el); };

  document.addEventListener("mouseover", e => {
    const el = e.target.closest && e.target.closest(SEL);
    if (el === current) return;
    hide();
    if (!el) return;
    current = el; lastX = e.clientX; lastY = e.clientY;
    timer = setTimeout(() => show(el), 250);
  });
  document.addEventListener("mousemove", e => { lastX = e.clientX; lastY = e.clientY; if (box && !box.classList.contains("hidden")) place(lastX, lastY); }, { passive: true });
  document.addEventListener("mouseout", e => { if (current && !(e.relatedTarget && current.contains(e.relatedTarget))) { if (!(e.relatedTarget && e.relatedTarget.closest && e.relatedTarget.closest(SEL) === current)) hide(); } });
  document.addEventListener("focusin", e => {
    const el = e.target.closest && e.target.closest(SEL);
    if (!el) return;
    const r = el.getBoundingClientRect(); lastX = r.left; lastY = r.bottom - 10;
    current = el; clearTimeout(timer); timer = setTimeout(() => show(el), 400);
  });
  document.addEventListener("focusout", hide);
  document.addEventListener("scroll", hide, true);
  document.addEventListener("keydown", e => { if (e.key === "Escape") hide(); });
})();
