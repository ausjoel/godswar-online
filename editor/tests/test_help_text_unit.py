#!/usr/bin/env python3
"""tests/test_help_text_unit.py - the wording rules for text the editor shows (hover help, descriptions, limits).

    python3 -I tests/test_help_text_unit.py            (no database, no server)

1. No owner notes or internal ids in user-facing text: every string literal of the editor's Python modules (docstrings
   and comments are code, not text, and are skipped) and of the static JavaScript (comments skipped) is scanned for
   "the admin", decision / finding ids (F-123, q58), agent package names (W4A), "UNPROVEN", "GENERATED" and paths into the
   private Development/ and Polish/ trees. Provenance belongs in comments.
2. data/rate_text.json (the Server rates page's labels and descriptions) follows the house style and is the same
   text as the server's rate definitions (server/src), so the two cannot drift apart.
3. The rates module: the fallback cleaner, the multiplier display of the global EXP setting, clean limit notes.
"""
import ast
import json
import os
import re
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
ED = os.path.dirname(HERE)
sys.path.insert(0, ED)

LEFTOVER = re.compile(r"(?<!\w)the admin\b|\bF-\d{1,3}\b|\bW\d[A-Z]\b|\bUNPROVEN\b|\bGENERATED\b|Development/|Polish/|round-2 notes"
                      r"|\b[qc]\d{2}\b(?![\w.])|\bPR ?#\d+|\(package [A-Z]\)")
# Strings that match on purpose. Keep this list short and say why.
ALLOW = (
    "the admin's example",                       # a test fixture note (tests/, not shipped text)
    "admin: double exp",                     # an updated_by value in an example
    "(?:the admin|F-", "(?<!\\w)the admin",                   # modules/common.py plain_note: the code that REMOVES such notes from server text
)


def py_strings(path):
    """(line, text) of every string literal that is not a docstring."""
    with open(path, encoding="utf-8") as fh:
        src = fh.read()
    tree = ast.parse(src)
    doc = set()
    for n in ast.walk(tree):
        if isinstance(n, (ast.Module, ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)) and n.body \
                and isinstance(n.body[0], ast.Expr) and isinstance(getattr(n.body[0], "value", None), ast.Constant) \
                and isinstance(n.body[0].value.value, str):
            doc.add(id(n.body[0].value))
    for n in ast.walk(tree):
        if isinstance(n, ast.Constant) and isinstance(n.value, str) and id(n) not in doc:
            yield n.lineno, n.value


_JS = re.compile(r"""//[^\n]*|/\*.*?\*/|"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*'|`(?:[^`\\]|\\.)*`""", re.S)


def js_strings(path):
    with open(path, encoding="utf-8") as fh:
        src = fh.read()
    for m in _JS.finditer(src):
        t = m.group(0)
        if t[0] in "\"'`":
            # a regex literal can contain quotes; a "string" directly after a // inside a string is still a string
            yield src.count("\n", 0, m.start()) + 1, t[1:-1]


def shipped_files():
    for d, exts in (("modules", (".py",)), ("static/modules", (".js",)), ("static/shared", (".js",)), ("static", (".js",)), ("", (".py",))):
        full = os.path.join(ED, d)
        for f in sorted(os.listdir(full)):
            p = os.path.join(full, f)
            if os.path.isfile(p) and f.endswith(exts) and not f.startswith("build_"):
                yield p


class NoLeftovers(unittest.TestCase):
    def test_no_owner_notes_in_user_facing_strings(self):
        bad = []
        for p in shipped_files():
            for line, text in (py_strings(p) if p.endswith(".py") else js_strings(p)):
                m = LEFTOVER.search(text)
                if m and os.path.basename(p) == "common.py" and text in ("the admin", "the admin's"):
                    continue                                    # plain_note's own search words
                if m and not any(a in text for a in ALLOW):
                    bad.append("%s:%d  %r in %r" % (os.path.relpath(p, ED), line, m.group(0), text[:110]))
        self.assertEqual(bad, [], "\n" + "\n".join(bad))

    def test_schema_texts(self):
        bad = []
        for name in ("glossary.json", "write_rules.json"):
            def walk(o, path=""):
                if isinstance(o, dict):
                    for k, v in o.items():
                        if k not in ("evidence", "source", "desc_source", "built_by", "task", "db_comment", "fk"):
                            walk(v, path + "/" + k)
                elif isinstance(o, list):
                    for i, v in enumerate(o):
                        walk(v, path + "/%d" % i)
                elif isinstance(o, str) and LEFTOVER.search(o):
                    bad.append("%s %s: %r" % (name, path, o[:100]))
            with open(os.path.join(ED, "schema", name), encoding="utf-8") as fh:
                walk(json.load(fh))
        self.assertEqual(bad, [], "\n" + "\n".join(bad))


JARGON = re.compile(r"\.cs\b|\.sql\b|\.ini\b|\.lua\b|\.xml\b|\.[Dd]at\b|GODSWAR_|\b[a-z]+_[a-z_]+\b|\bas before\b|NF_L0|\(\d{4,5}\)"
                    r"|\bmaps? \d|finalise|round[- ]?2|\bstock\b|\bretail\b|\bwiki\b|[^\x00-\x7f]")


class RateText(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        with open(os.path.join(ED, "data", "rate_text.json"), encoding="utf-8") as fh:
            cls.d = json.load(fh)

    def test_house_style(self):
        bad = []
        for k, e in self.d["rates"].items():
            t = e["description"]
            if LEFTOVER.search(t) or JARGON.search(t):
                bad.append("%s: %r" % (k, (LEFTOVER.search(t) or JARGON.search(t)).group(0)))
            if not (t[0].isupper() or t[0].isdigit()) or not t.endswith("."):
                bad.append("%s: starts with a capital and ends with a full stop" % k)
            if len(t) > 420:
                bad.append("%s: %d characters (at most 420)" % (k, len(t)))
            if (" On: " in t) != (" Off: " in t):
                bad.append("%s: a switch says both On: and Off:" % k)
            if re.search(r"(^|\. )[01] = ", t) and " On: " in t:
                bad.append("%s: either On/Off or 0/1, not both" % k)
            if "label" in e and (LEFTOVER.search(e["label"]) or re.search(r"finalise|Athens_\d|\(item \d", e["label"])):
                bad.append("%s: label %r" % (k, e["label"]))
        self.assertEqual(bad, [], "\n" + "\n".join(bad))

    def test_same_text_as_the_server_source(self):
        src = os.path.join(os.path.dirname(ED), "server", "src")
        if not os.path.isdir(src):
            self.skipTest("server/src is not beside the editor (a deployed copy)")
        lit = re.compile(r'"((?:[^"\\\n]|\\.)*)"')
        group = re.compile(r'"(?:[^"\\\n]|\\.)*"(?:\s*\+\s*"(?:[^"\\\n]|\\.)*")*')
        have = set()
        for dp, _, files in os.walk(src):
            for f in files:
                if f.endswith(".cs"):
                    with open(os.path.join(dp, f), encoding="utf-8") as fh:
                        text = fh.read()
                    if "ServerRateDefinition" in text:
                        for m in group.finditer(text):
                            have.add("".join(re.sub(r"\\(.)", r"\1", x) for x in lit.findall(m.group(0))))
        missing = [k for k, e in self.d["rates"].items() if e["description"] not in have or e.get("label", e["description"]) not in have]
        self.assertEqual(missing, [], "these rates read differently in server/src (change both, or neither)")
        for old, new in self.d["categories"].items():
            self.assertIn(new, have)
            self.assertNotIn(old, have)


class RatesModule(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        from modules import rates as R
        import rates as top
        cls.R, cls.top = R, top

    def test_fallback_cleaner(self):
        P = self.R._plain
        self.assertEqual(P("the admin F-166 (b): the most drops one corpse lists."), "The most drops one corpse lists.")
        self.assertEqual(P("1 = quests open (the admin F-121). Needs client 1.4.2."), "1 = quests open. Needs client 1.4.2.")
        self.assertEqual(P("Keep 0 (UNPROVEN, see Development/build/x/README.md)."), "Keep 0.")
        self.assertEqual(P("Drops this often (GENERATED)."), "Drops this often.")
        self.assertEqual(P("Silver paid when a quest is handed in."), "Silver paid when a quest is handed in.")
        # gate notes (the Gates page)
        self.assertEqual(P("the admin F-19: enforce hours and the 20,000 Silver fee."), "Enforce hours and the 20,000 Silver fee.")
        self.assertEqual(P("the admin F-03/F-04: Saturday 22:45-23:30 AEST; F-68: 31-200; F-66: players go home."), "Saturday 22:45-23:30 AEST; 31-200; players go home.")
        self.assertEqual(P("the admin BAY2 50-90. The map split stays."), "50-90. The map split stays.")
        self.assertEqual(P("W2 F-27: the client's own hours."), "The client's own hours.")
        self.assertEqual(P("rate on (live). F-70: the 120 cap is now 200."), "Rate on (live). The 120 cap is now 200.")
        self.assertEqual(P("the admin chose 360."), "The owner chose 360.")

    def test_reword_uses_the_file_and_keeps_the_stored_text(self):
        rows = self.R._reword([
            {"rate_key": "drop.corpse_max_items", "label": "Maximum drops per corpse (all sources)", "category": "Monster drops",
             "description": "the admin F-166 (b): the most drops one corpse lists."},
            {"rate_key": "box.dial_contents", "label": "Gold-box dial contents (0 today, 1 retail, 2 both)",
             "category": "Item and NPC handlers (finalise2)", "description": "x (GENERATED)."},
            {"rate_key": "new.unknown_rate", "label": "New thing", "category": "Future", "description": "Does it (the admin F-999)."}])
        self.assertNotIn("the admin", rows[0]["description"])
        self.assertEqual(rows[0]["description_stored"], "the admin F-166 (b): the most drops one corpse lists.")
        self.assertEqual(rows[1]["category"], "Items and NPC features")
        self.assertEqual(rows[1]["label"], "Gold box contents (0 current, 1 original, 2 both)")
        self.assertEqual(rows[2]["description"], "Does it.")
        self.assertEqual(rows[2]["label"], "New thing")

    def test_global_exp_is_a_multiplier(self):
        s = self.top.STOCK_BY_ID["global_exp"]
        self.assertEqual((s["min"], s["max"], s["scale"]), (10000, 50000, 10000))       # stored units are unchanged
        self.assertEqual(self.R._shown(s, 10000), "x1")
        self.assertEqual(self.R._shown(s, 12500), "x1.25")
        self.assertEqual(self.R._shown(s, 50000), "x5")
        self.assertEqual(self.R._shown(s, None), "(empty)")
        self.assertEqual(self.R._shown(self.top.STOCK_BY_ID["level_gap"], 20), "20")
        self.assertIn("Monster kill EXP", s["help"])
        self.assertIn("restart", s["help"])
        self.assertNotIn("basis", s["help"] + s["label"])

    def test_limit_notes_are_plain(self):
        bad = []
        for k, info in self.R.KEY_INFO.items():
            for t in list(info.get("caps") or []) + [info.get("first_cap_why") or "", info.get("applies") or ""]:
                m = LEFTOVER.search(t) or re.search(r"32-bit|Round-2|\.xml|\bclient\b|monster_loot|drop\.[a-z_]+", t)
                if m:
                    bad.append("%s: %r in %r" % (k, m.group(0), t))
        self.assertEqual(bad, [], "\n" + "\n".join(bad))
        for s in self.top.STOCK_SETTINGS:
            self.assertFalse(re.search(r"Postgres|Snapshot|gw251|NULL|stock setting", s["help"] + s["label"]), s["id"])


class _Pg:
    def __init__(self, stock):
        self.stock, self.ran = stock, []

    def json(self, sql, timeout=0):
        return {"rows": [], "state": {"revision": 3}, "stock": dict(self.stock)}

    def run(self, sql, timeout=0):
        self.ran.append(sql)


class GlobalExpSave(unittest.TestCase):
    """The save still takes and stores basis points; texts (errors, the change record) speak multipliers."""

    def setUp(self):
        import threading
        import types
        from modules import rates as R
        import rates as top
        self.R = R
        pg = _Pg({"global_exp": 10000, "level_gap": 20})
        rb = types.SimpleNamespace(db="copy", writable=True, live=False, pg=pg, db_guard_sql=lambda: "-- guard")
        rt = top.Rates(rb)
        rt.reload_server = lambda: {"reachable": False}
        self.recorded = []
        app = types.SimpleNamespace(rates=rt, changes=[], record_reborn_change=self.recorded.append)
        self.ctx = types.SimpleNamespace(app=app, rb=rb, need_rb=lambda: rb, body={})
        self.pg = pg

    def test_a_multiplier_typed_as_stored_value_is_refused_in_plain_words(self):
        with self.assertRaises(self.R.Bad) as e:
            self.R._save(self.ctx, {}, {}, {"global_exp": 2}, {"global_exp": 10000}, "")
        self.assertIn("must be x1 to x5", str(e.exception))
        self.assertEqual(self.pg.ran, [])

    def test_saved_in_basis_points_and_recorded_as_multiplier(self):
        c = self.R._save(self.ctx, {}, {}, {"global_exp": 25000}, {"global_exp": 10000}, "")
        self.assertEqual(c["after"], {"stock:global_exp": 25000})
        self.assertIn("Global monster EXP multiplier x1 -> x2.5", c["action"])
        self.assertIn("global_experience_multiplier_basis_points = 25000", self.pg.ran[0])
        self.assertEqual(c["applies"], "after_restart")


if __name__ == "__main__":
    unittest.main(verbosity=1)
