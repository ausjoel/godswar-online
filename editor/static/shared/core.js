/* shared/core.js - the GW namespace every module page uses (contract: MODULES.md).
   DOM helper, API calls with the CSRF token, events, modal / confirm boxes in the game's look,
   |cAARRGGBB colour codes as text spans, icons and cells, module registration.
   Client and database text is always inserted as text nodes, never as HTML. */
(function () {
  "use strict";
  const GW = window.GW = window.GW || {};
  const meta = n => { const m = document.querySelector(`meta[name="${n}"]`); return m ? m.content : ""; };
  GW.CSRF = meta("csrf");

  // ------------------------------------------------------------ DOM
  // h("div", {class, style, onclick, dataset..., "data-field": ...}, kids...) - strings become text nodes
  GW.h = function h(tag, attrs, ...kids) {
    const el = document.createElement(tag);
    if (attrs) for (const k in attrs) {
      const v = attrs[k];
      if (v === null || v === undefined || v === false) continue;
      if (k === "class") el.className = v;
      else if (k === "style") el.style.cssText = v;
      else if (k.startsWith("on") && typeof v === "function") el.addEventListener(k.slice(2), v);
      else if (k === "text") el.textContent = v;
      else el.setAttribute(k, v === true ? "" : v);
    }
    for (const kid of kids.flat(Infinity)) {
      if (kid === null || kid === undefined || kid === false) continue;
      el.append(kid.nodeType ? kid : document.createTextNode(String(kid)));
    }
    return el;
  };
  const h = GW.h;
  GW.$ = (s, el) => (el || document).querySelector(s);
  GW.$$ = (s, el) => Array.from((el || document).querySelectorAll(s));
  GW.clear = el => { while (el.firstChild) el.removeChild(el.firstChild); return el; };
  GW.fmt = n => (n === null || n === undefined || n === "") ? "" : (typeof n === "number" || /^-?\d+$/.test(String(n))) ? Number(n).toLocaleString("en-US") : String(n);

  // Game text: "|cFFDCC883Effect|cFFFFFFFF rest" -> spans with colours; everything is a text node.
  GW.text = function (s, base) {
    const frag = document.createDocumentFragment();
    if (s === null || s === undefined) return frag;
    const parts = String(s).split(/\|c([0-9A-Fa-f]{8})/);
    let color = base || null;
    for (let i = 0; i < parts.length; i++) {
      if (i % 2 === 1) { color = "#" + parts[i].slice(2); continue; }
      if (!parts[i]) continue;
      const span = document.createElement("span");
      if (color) span.style.color = color;
      span.textContent = parts[i].replace(/\|r/g, "");
      frag.append(span);
    }
    return frag;
  };

  // ------------------------------------------------------------ events
  const handlers = {};
  GW.on = (evt, fn) => { (handlers[evt] = handlers[evt] || []).push(fn); return () => GW.off(evt, fn); };
  GW.off = (evt, fn) => { handlers[evt] = (handlers[evt] || []).filter(f => f !== fn); };
  GW.emit = (evt, data) => { for (const fn of (handlers[evt] || []).slice()) { try { fn(data); } catch (e) { console.error(e); } } };

  // ------------------------------------------------------------ API
  // GW.api(path) = GET, GW.api(path, body) = POST JSON. Errors throw Error with .status and .data
  // (.data.online for 423 "character online", .data.conflict for 409, .data.refused for 403).
  GW.api = async function (path, body, opts) {
    const o = opts || {};
    const init = body !== undefined && body !== null
      ? { method: "POST", headers: { "Content-Type": "application/json", "X-CSRF-Token": GW.CSRF }, body: JSON.stringify(body) }
      : {};
    let r;
    try { r = await fetch(path, init); } catch (e) { const err = new Error("the editor did not answer (is the SSH tunnel up?)"); err.status = 0; throw err; }
    let j = null;
    try { j = await r.json(); } catch (e) { if (!o.raw) { const err = new Error("bad answer from the editor (HTTP " + r.status + ")"); err.status = r.status; throw err; } }
    if (!r.ok || (j && j.error && !o.allowError)) {
      const err = new Error((j && j.error) || ("HTTP " + r.status)); err.status = r.status; err.data = j || {}; throw err;
    }
    return j;
  };
  const cache = {};
  GW.cached = function (path) { if (!cache[path]) cache[path] = GW.api(path).catch(e => { delete cache[path]; throw e; }); return cache[path]; };

  // ------------------------------------------------------------ status line (top bar)
  GW.status = function (msg, kind) {
    const el = document.getElementById("gw-status");
    if (!el) return;
    el.textContent = msg || "";
    el.className = "gw-status " + (kind || "");
    el.title = msg || "";
  };

  // ------------------------------------------------------------ modal / confirm (MessageBoxUI, T_SimpleWindow)
  GW.modal = function (content, opts) {
    const o = opts || {};
    const back = h("div", { class: "gw-modal-back" });
    const box = h("section", { class: "gw-modal gw-frame-normalwindow gw-text" + (o.wide ? " wide" : "") + (o.full ? " full" : ""), role: "dialog", "aria-modal": "true", "aria-label": o.title || "dialog" },
      h("div", { class: "gw-modal-title" }, o.title || ""),
      h("button", { class: "gw-sprite gw-close gw-modal-x", "aria-label": "Close", title: "Close", onclick: () => close() }),
      h("div", { class: "gw-modal-body" }, content));
    back.append(box);
    const prevFocus = document.activeElement;
    function close(v) {
      back.remove(); document.removeEventListener("keydown", onKey, true);
      if (prevFocus && prevFocus.focus) try { prevFocus.focus(); } catch (e) { /* gone */ }
      if (o.onclose) o.onclose(v);
    }
    function onKey(e) { if (e.key === "Escape") { e.stopPropagation(); close(); } }
    document.addEventListener("keydown", onKey, true);
    back.addEventListener("mousedown", e => { if (e.target === back && !o.sticky) close(); });
    document.body.append(back);
    setTimeout(() => { const f = box.querySelector("[autofocus], input, select, textarea, button.gw-btn4, button"); if (f) f.focus(); }, 0);
    return { el: box, body: box.querySelector(".gw-modal-body"), close };
  };

  // GW.confirm({title, message | el, ok: "Save", cancel: "Cancel", danger}) -> Promise<boolean>
  GW.confirm = function (o) {
    return new Promise(res => {
      let done = false;
      const fin = v => { if (!done) { done = true; res(v); } };
      const body = h("div", { class: "gw-confirm" },
        o.el || null,
        o.message ? h("div", { class: "gw-confirm-msg" }, GW.text(o.message)) : null,
        h("div", { class: "gw-confirm-buttons" },
          h("button", { class: "gw-btnx" + (o.danger ? " danger" : ""), autofocus: true, onclick: () => { fin(true); m.close(true); } }, o.ok || "OK"),
          o.cancel === false ? null : h("button", { class: "gw-btnx", onclick: () => { fin(false); m.close(false); } }, o.cancel || "Cancel")));
      const m = GW.modal(body, { title: o.title || "Confirm", wide: !!o.wide, onclose: () => fin(false) });
    });
  };
  GW.alert = (message, title) => GW.confirm({ title: title || "GodsWar editor", message, ok: "OK", cancel: false });

  // ------------------------------------------------------------ icons and cells
  // catalog-icons.png: 36 x 36 tiles, 64 per row (catalog-origin/catalog.json "sprites")
  GW.SPRITE_FILE = "/static/catalog-icons.png";
  GW.icon = function (sprite, opts) {
    const o = opts || {};
    const el = h("span", { class: "gw-icon" + (o.class ? " " + o.class : ""), "aria-hidden": "true" });
    if (sprite === null || sprite === undefined || sprite === "") {
      el.classList.add("none");
      el.textContent = (o.name || "?").slice(0, 2);
      return el;
    }
    const n = Number(sprite);
    el.style.backgroundImage = `url(${GW.SPRITE_FILE})`;
    el.style.backgroundPosition = `-${(n % 64) * 36}px -${Math.floor(n / 64) * 36}px`;
    return el;
  };
  // 42 px stone cell with the icon at 3,3; opts: {sprite, name, count, quality, locked, label, tipType, tipId, tipInst, onclick}
  GW.cell = function (opts) {
    const o = opts || {};
    const el = h(o.onclick ? "button" : "span", {
      class: "gw-sprite gw-cell gw-cellx" + (o.quality !== undefined && o.quality !== null ? " gq gq" + o.quality : "") + (o.locked ? " locked" : "") + (o.empty ? " empty" : "") + (o.class ? " " + o.class : ""),
      type: o.onclick ? "button" : null, "aria-label": o.label || o.name || "empty cell",
      "data-tip-type": o.tipType || null, "data-tip-id": o.tipId !== undefined && o.tipId !== null ? String(o.tipId) : null,
      "data-tip-inst": o.tipInst ? JSON.stringify(o.tipInst) : null, "data-help": o.help || null, onclick: o.onclick || null });
    if (o.sprite !== undefined && o.sprite !== null) el.append(GW.icon(o.sprite));
    else if (o.name) el.append(GW.icon(null, { name: o.name }));
    if (o.count !== undefined && o.count !== null && o.count !== "" && Number(o.count) !== 1) el.append(h("span", { class: "gw-cell-count gw-outline" }, o.count));
    if (o.corner) el.append(h("span", { class: "gw-cell-corner gw-outline" }, o.corner));
    return el;
  };

  // ------------------------------------------------------------ module registration
  // GW.module("inventory", { mount(el, ctx) {...}, unmount() {...}, route(ctx) {...} })
  GW.modules = GW.modules || {};
  GW.module = function (name, def) {
    GW.modules[name] = Object.assign({ name }, def);
    GW.emit("module-registered", name);
    return GW.modules[name];
  };

  // "coming" placeholder for stub pages
  GW.coming = function (title, text, classic) {
    return h("section", { class: "gw-frame-normalwindow gw-text gw-coming" },
      h("div", { class: "gw-wtitle" }, title),
      h("div", { class: "gw-coming-body" },
        h("div", { class: "gw-coming-big" }, "Coming"),
        h("p", null, text || "This editor is being built."),
        classic ? h("p", null, "Until then use ", h("a", { href: classic.href }, classic.label), ".") : null));
  };

  // the editor's own state (filled by the shell)
  GW.state = GW.state || { cid: null, character: null, status: null, caps: null, modules: [] };
})();
