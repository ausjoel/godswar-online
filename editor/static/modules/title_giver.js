/* static/modules/title_giver.js - Title giver (wave 4 D item 7). Backend: modules/title_giver.py.
   Categories on the left (Stats -> HP / ATK / MATK ..., Level titles -> class, Quest, Instance, Battlefield, Event ...),
   every title with what awards it, tick several, then Grant / Revoke the administrator copies in one write. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  let active = null;
  GW.module("title_giver", {
    async mount(el, ctx) {
      const session = {}; active = session;
      let V = null, cat = "all", sub = null, q = "", picked = new Set(), busy = false;
      const left = h("nav", { class: "gw-tg-cats", "aria-label": "Title categories" });
      const list = h("div", { class: "gw-tg-list", role: "list" });
      const bar = h("div", { class: "gw-tg-bar" });
      const info = h("p", { class: "gw-dim" });
      const search = h("input", { type: "search", placeholder: "Search titles, rules, NPCs, quests", "aria-label": "Search titles", oninput: e => { q = e.target.value.trim().toLowerCase(); draw(); } });
      el.append(GW.onlineBanner ? GW.onlineBanner(ctx.cid) : null, info, h("div", { class: "gw-tg-top" }, search, bar), h("div", { class: "gw-tg-cols" }, left, list));
      async function load() {
        try { const v = await ctx.api("view/" + ctx.cid); if (active !== session) return; V = v; picked = new Set(); draw(); }
        catch (e) { if (active === session) { GW.clear(list).append(h("p", { class: "tt-bad", role: "alert" }, e.message)); } }
      }
      const shown = () => V.titles.filter(t => (cat === "all" || t.category === cat) && (!sub || t.sub === sub) &&
        (!q || [t.id, t.name, t.description, t.how, t.npc, t.sub, (t.quests || []).join(" "), (t.rules || []).join(" ")].join(" ").toLowerCase().includes(q)));
      function draw() {
        if (!V) return;
        info.textContent = V.note + (V.support.available ? " Owned: " + V.titles.filter(t => t.owned).length + " · administrator copies: " + V.expected.length + (V.owned_limit ? " · limit " + V.owned_limit : "") : " " + V.support.reason);
        GW.clear(left);
        const catBtn = (id, label, n) => h("button", { type: "button", class: "gw-tg-cat" + (cat === id && !sub ? " on" : ""), onclick: () => { cat = id; sub = null; draw(); } }, label, h("span", { class: "gw-dim" }, " " + n));
        left.append(catBtn("all", "All titles", V.titles.length));
        for (const c of V.categories) {
          if (!c.count) continue;
          left.append(catBtn(c.id, c.title, c.count));
          if (cat === c.id) for (const s of c.subs) left.append(h("button", { type: "button", class: "gw-tg-sub" + (sub === s ? " on" : ""), onclick: () => { sub = s; draw(); } },
            "→ " + s, h("span", { class: "gw-dim" }, " " + V.titles.filter(t => t.category === c.id && t.sub === s).length)));
        }
        const rows = shown();
        GW.clear(list);
        if (!rows.length) list.append(h("p", { class: "gw-dim" }, "No titles match."));
        let lastSub = null;
        for (const t of rows) {
          const head = (cat === "all" ? (V.categories.find(c => c.id === t.category) || {}).title + " → " : "") + t.sub;
          if (head !== lastSub) { list.append(h("h4", { class: "gw-tg-head" }, head)); lastSub = head; }
          const can = V.writable && t.grantable;
          const box = h("input", { type: "checkbox", checked: picked.has(t.id) || null, disabled: !can || null, "aria-label": "Select " + t.name,
            onchange: e => { if (e.target.checked) picked.add(t.id); else picked.delete(t.id); drawBar(); } });
          const state = t.admin ? h("span", { class: "gw-chip" }, "administrator copy") : t.earned ? h("span", { class: "gw-chip on" }, "earned") : null;
          list.append(h("label", { class: "gw-tg-row" + (t.owned ? " owned" : "") + (t.live ? "" : " nolive"), role: "listitem", "data-tip-type": "title", "data-tip-id": String(t.id) },
            box, h("div", null,
              h("div", null, h("b", null, t.name), h("span", { class: "gw-dim" }, " #" + t.id + " "), state, t.live ? null : h("span", { class: "gw-chip never" }, "no server award")),
              h("div", null, h("span", { class: "gw-tg-how" }, t.how)),
              (t.rules && t.rules.length) ? h("div", { class: "gw-dim" }, "Live rule: " + t.rules.join("; ")) : null,
              t.bonus ? h("div", { class: "gw-tg-bonus" }, "Bonus: " + t.bonus) : null,
              h("div", { class: "gw-dim" }, [t.npc ? "Awarder: " + t.npc : null, t.quests ? "Quests: " + t.quests.join(", ") : null,
                t.items ? "Items: " + t.items.join(", ") : null, t.switch ? "Needs " + t.switch : null, t.note || null, t.grant_note || null,
                !t.grantable ? "Not in the administrator-grant catalogue: cannot be granted here." : null, t.source ? "Source: " + t.source : null].filter(Boolean).join(" · ")),
              t.description ? h("div", { class: "gw-dim gw-tg-desc" }, t.description) : null)));
        }
        drawBar();
      }
      function drawBar() {
        GW.clear(bar);
        const rows = shown().filter(t => V.writable && t.grantable);
        const add = [...picked].filter(id => !(V.titles.find(t => t.id === id) || {}).admin);
        const rem = [...picked].filter(id => (V.titles.find(t => t.id === id) || {}).admin);
        bar.append(
          h("button", { type: "button", class: "gw-btnx small", disabled: !rows.length || null, onclick: () => { rows.forEach(t => picked.add(t.id)); draw(); } }, "Tick all shown (" + rows.length + ")"),
          h("button", { type: "button", class: "gw-btnx small", disabled: !picked.size || null, onclick: () => { picked = new Set(); draw(); } }, "Clear"),
          h("button", { type: "button", class: "gw-btnx", disabled: busy || !add.length || null, onclick: () => write(add, []) }, "Grant " + add.length),
          h("button", { type: "button", class: "gw-btnx danger", disabled: busy || !rem.length || null, onclick: () => write([], rem) }, "Revoke " + rem.length + " administrator copies"));
      }
      async function write(add, remove) {
        if (busy || !V) return;
        const names = ids => ids.map(id => (V.titles.find(t => t.id === id) || {}).name || "#" + id);
        const ok = await GW.confirm({ title: add.length ? "Grant titles?" : "Revoke administrator copies?",
          message: (add.length ? "Grant " + add.length + ": " + names(add).join(", ") : "Revoke " + remove.length + ": " + names(remove).join(", ")) +
            "\n\nEarned titles stay owned. The character must be logged out; it applies at next login. A backup is kept (Undo in the change log).", ok: add.length ? "Grant" : "Revoke", danger: !add.length });
        if (!ok || active !== session) return;
        busy = true; drawBar();
        try { const r = await ctx.api("apply/" + ctx.cid, { add, remove, expected: V.expected }); GW.writeDone(r, (add.length ? "Granted " + add.length : "Revoked " + remove.length) + " titles"); await load(); }
        catch (e) { if (await GW.writeError(e) === "conflict") await load(); }
        finally { busy = false; if (active === session) drawBar(); }
      }
      session.stop = GW.on ? GW.on("change", () => { if (!busy) load(); }) : null;
      await load();
    },
    unmount() { if (active && active.stop) active.stop(); active = null; }
  });
})();
