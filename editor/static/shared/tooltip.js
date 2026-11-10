/* shared/tooltip.js - one hover box for the whole page (UIHelper / ItemView look).
   Any element with
     data-field="table.column"            -> the glossary help for that field (what it is, range, where stored,
                                             when an edit applies, why it is read-only)
     data-help="plain text"               -> that text
     data-tip-type="item|skill|petskill|pet|status|talent|appendattr|holystone|quest|title|monster|npc|map|..."
     data-tip-id="<id>" [data-tip-inst='{"item_quality":5,...}']  -> the game-style tooltip of that entry
   gets it after ~250 ms of hover (or keyboard focus). Nothing else to call. */
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

  async function content(el) {
    if (el.dataset.tipType && el.dataset.tipId !== undefined && el.dataset.tipId !== "") {
      let inst = null;
      if (el.dataset.tipInst) { try { inst = JSON.parse(el.dataset.tipInst); } catch (e) { inst = null; } }
      return GW.render.tooltip(el.dataset.tipType, el.dataset.tipId, inst);
    }
    if (el.dataset.field) {
      const f = await GW.glossary.field(el.dataset.field);
      const node = GW.render.fieldHelp(f);
      if (el.dataset.help) node.append(h("div", { class: "tt-line tt-small" }, el.dataset.help));
      return node;
    }
    return h("div", { class: "gw-tooltip gw-help" }, GW.text(el.dataset.help));
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
