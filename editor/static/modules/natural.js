/* static/modules/natural.js - read-only: what on a character could not come from normal play. Backend: modules/natural.py. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const LABEL = { impossible: "impossible", unnatural: "above normal play" };

  GW.module("natural", {
    async mount(el, ctx) {
      const D = await ctx.api("report");
      el.append(h("p", { class: "gw-dim" }, "Read-only. Nothing here changes a character or blocks an edit. GM characters are expected to show up."));
      for (const c of D.characters) {
        const bad = c.findings.filter(f => f.severity === "impossible").length;
        el.append(h("details", { class: "gw-nat-char", open: bad > 0 },
          h("summary", null, h("b", null, c.name), " level " + c.level + " " + c["class"] + (c.gm ? " (GM)" : ""), " ",
            h("span", { class: "gw-chip" }, c.findings.length ? c.findings.length + " found" : "clean"), bad ? h("span", { class: "gw-chip" }, bad + " impossible") : null),
          c.findings.length ? h("table", null, h("tbody", null, c.findings.map(f => h("tr", null, h("td", null, LABEL[f.severity] || f.severity), h("td", null, f.where), h("td", null, f.message)))))
            : h("p", { class: "gw-dim" }, "Nothing found by these rules.")));
      }
      el.append(h("h3", null, "Not checked"), h("ul", null, D.not_checked.map(t => h("li", null, t))));
    }
  });
})();
