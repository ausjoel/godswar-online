"""modules/common.py - the shared backend of the module editors (contract: ../MODULES.md).

What a module gets from here:

  * module("<name>", title=..., icon=..., ...)  -> a Module; @M.get("route") / @M.post("route") register handlers.
    Routes are regular expressions matched against the path after /api/m/<name>/ (groups become arguments).
    A handler is fn(ctx, *groups) and returns a JSON-able object.
  * ctx (Ctx): ctx.app, ctx.rb (reborn.Reborn), ctx.q("key"), ctx.qint("key"), ctx.body, ctx.catalog,
    ctx.glossary, ctx.caps, and the write helpers ctx.char_write(...) / ctx.rows_write(...) / ctx.live_call(...).
  * services (also usable without a ctx): CATALOG (catalog-origin/*.json as English, icon-carrying ID lists),
    GLOSSARY (schema/glossary.json + schema/write_rules.json: what a field is, its range, whether and when an edit
    applies), capabilities() (the round2 /admin/capabilities probe of the game server's management port).
  * SQL helpers: lit / ident (from reborn.py), expected_sql, update_sql, insert_sql, delete_sql, scope().
  * exceptions: Bad (400), Refused (403: a read-only field / table, with the reason), OnlineRefused (423: the
    character is online; carries the signals and the live operations that would work), Conflict (409: the row
    changed since the page read it), NotFound (404).

Nothing here writes to the database except char_write / rows_write, which go through reborn.py's guarded
transaction (db guard, row locks, in-transaction online re-check, economy-baseline guard, pet guards and start-up
validators, backup, after-image, change record, Undo) - schema/WRITE-RULES.md section 7.
"""
import datetime
import json
import os
import re
import subprocess
import threading
import time
import urllib.error
import urllib.request
from decimal import Decimal

from . import _item_lifecycle as item_lifecycle

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))      # gwadmin-web/
CATALOG_DIR = os.path.join(HERE, "catalog-origin")
SCHEMA_DIR = os.path.join(HERE, "schema")

try:
    import reborn as R          # gwadmin-web/reborn.py (server.py puts gwadmin-web on sys.path)
    lit, ident, RebornError, RebornConflict = R.lit, R.ident, R.RebornError, R.RebornConflict
except Exception:               # pragma: no cover - lets the catalogue/glossary parts be imported alone
    R = None

    class RebornError(Exception):
        pass

    class RebornConflict(RebornError):
        conflicts = []

    def lit(v):
        if v is None:
            return "NULL"
        if isinstance(v, bool):
            return "true" if v else "false"
        if isinstance(v, (int, float, Decimal)):
            return str(v)
        return "'" + str(v).replace("'", "''") + "'"

    def ident(name):
        return '"%s"' % name.replace('"', '""')


# =========================================================================== errors
class Bad(Exception):
    """400: the request is wrong (a value out of range, a missing field)."""


class NotFound(Exception):
    """404."""


class Refused(Exception):
    """403: the field / table is read-only (sealed content, evidence, bookkeeping). The message says why."""


class Conflict(Exception):
    """409: the row changed since the page read it (expected values), or Undo would erase newer changes."""

    def __init__(self, msg, conflicts=None):
        Exception.__init__(self, msg)
        self.conflicts = conflicts or []


class OnlineRefused(Exception):
    """423: the character is online. .info = {name, reasons, why, live: [operations that work online], kick}."""

    def __init__(self, msg, info):
        Exception.__init__(self, msg)
        self.info = info


# =========================================================================== module registry
ACTOR = threading.local()       # .name = the signed-in editor account of the request being handled (set by server.py)


RESERVED = {"", "modules", "capabilities", "status", "catalog", "glossary", "characters", "online", "pending",
            "changes", "rules", "edit", "live"}
REGISTRY = {}


class Module:
    def __init__(self, name, title, icon=None, order=100, group="systems", status="coming", description="",
                 needs_character=False):
        self.name, self.title, self.icon, self.order, self.group = name, title, icon or {}, order, group
        self.status, self.description, self.needs_character = status, description, needs_character
        self.routes = []          # (method, compiled regex, fn)

    def _route(self, method, pattern):
        rx = re.compile("^" + pattern.strip("/") + "$")

        def deco(fn):
            self.routes.append((method, rx, fn))
            return fn
        return deco

    def get(self, pattern):
        """@M.get(r"character/(\\d+)") -> GET /api/m/<name>/character/7 calls fn(ctx, "7")."""
        return self._route("GET", pattern)

    def post(self, pattern):
        """@M.post("item") -> POST /api/m/<name>/item (JSON body in ctx.body, CSRF checked by server.py)."""
        return self._route("POST", pattern)

    def describe(self):
        static = os.path.join(HERE, "static", "modules")
        return {"name": self.name, "title": self.title, "icon": self.icon, "order": self.order, "group": self.group,
                "status": self.status, "description": self.description,
                "needs_character": self.needs_character,
                "js": "/static/modules/%s.js" % self.name if os.path.exists(os.path.join(static, self.name + ".js")) else None,
                "css": "/static/modules/%s.css" % self.name if os.path.exists(os.path.join(static, self.name + ".css")) else None,
                "routes": ["%s %s" % (m, rx.pattern.strip("^$")) for m, rx, _ in self.routes]}


def module(name, title, **kw):
    """Register a module (call once at import time of modules/<name>.py)."""
    if not re.match(r"^[a-z][a-z0-9_]*$", name) or name in RESERVED:
        raise ValueError("bad or reserved module name %r" % name)
    if name in REGISTRY:
        raise ValueError("module %r registered twice" % name)
    m = Module(name, title, **kw)
    REGISTRY[name] = m
    return m


# =========================================================================== small helpers
def now_iso():
    return datetime.datetime.now().isoformat(timespec="seconds")


def _load_json(path, default=None):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return default


def public_change(c):
    return {k: v for k, v in c.items() if k not in ("before", "after")}


def to_int(v, lo=None, hi=None, what="value"):
    try:
        if isinstance(v, bool):
            raise ValueError
        if isinstance(v, float) and not v.is_integer():
            raise ValueError
        n = int(str(v).strip()) if not isinstance(v, (int, float)) else int(v)
    except (TypeError, ValueError):
        raise Bad("%s must be a whole number, got %r" % (what, v))
    if lo is not None and n < lo:
        raise Bad("%s must be at least %s (got %s)" % (what, lo, n))
    if hi is not None and n > hi:
        raise Bad("%s must be at most %s (got %s)" % (what, hi, n))
    return n


# =========================================================================== catalogue service
CLASS_NAMES = {0: "Warrior", 1: "Champion", 2: "Priest", 3: "Mage"}
CAMP_NAMES = {0: "Sparta", 1: "Athens"}
# item kind -> picker tab (the game's own groups: Mall / bag Part captions)
ITEM_GROUPS = [("weapon", "Weapon"), ("shield", "Shield"), ("head", "Helmet"), ("armor", "Armor"), ("cloth", "Armor"),
               ("glove", "Gloves"), ("cuff", "Cuffs"), ("leggins", "Leggings"), ("shoes", "Shoes"), ("girdle", "Girdle"),
               ("amulet", "Amulet"), ("ring", "Ring"), ("stylish", "Fashion"), ("mount", "Mount"),
               ("mounthead", "Mount gear"), ("mountarmor", "Mount gear"), ("mountsoul", "Mount gear"),
               ("mountornament", "Mount gear"), ("mountamulet", "Mount gear"), ("consume item", "Consumable"),
               ("create", "Other"), ("pet", "Other")]
ITEM_GROUP = dict(ITEM_GROUPS)
INDEX_FIELDS = ["id", "name", "group", "cat", "sub", "sprite", "classes", "minlv", "maxlv", "color", "flags", "key"]


def _row(id_, name, group=None, cat=None, sub="", sprite=None, classes=None, minlv=None, maxlv=None, color=None,
         flags="", key=""):
    return [id_, name or "", group, cat, sub, sprite, classes, minlv, maxlv, color, flags, key]


def _lv(a, b):
    a = None if a in (None, "", 0) else a          # level 0 = no requirement
    b = None if b in (None, "", 0) else b
    if a is None and b is None:
        return ""
    if b in (None, "", 0, 200) or a == b:
        return "Lv %s" % a if a not in (None, "") else ""
    return "Lv %s-%s" % (a if a not in (None, "") else 1, b)


class CatalogService:
    """English ID lists from catalog-origin/*.json (the admin's Origin client), one per ID type, for the picker grid.
    index(type) -> {type, title, fields, rows, groups, count}; entry(type, id) -> the full catalogue entry."""

    TYPES = {
        "item": ("Items", "items"), "appendattr": ("Append attributes", "appendattrs"),
        "stattype": ("Attribute stat types", "attributes"), "skill": ("Skills", "skills"), "talent": ("Talents", "talents"),
        "petskill": ("Pet skills", "petskills"), "pet": ("Pet species", "pets"), "monster": ("Monsters", "monsters"),
        "npc": ("NPCs", "npcs"), "map": ("Maps", "maps"), "quest": ("Quests", "quests"), "title": ("Titles", "titles"),
        "status": ("Buffs and statuses", "statuses"), "guild_building": ("Guild buildings", "guild"),
        "altar": ("Guild altars", "guild"), "holystone": ("Holy stones", "holystones"), "suit": ("Holy suit levels", "suits"),
        "suit_tier": ("Holy suit tiers", "suits"), "zodiac_sign": ("Zodiac signs", "zodiac"),
        "zodiac_kind": ("Zodiac skills", "zodiac"), "zodiac_grid": ("Zodiac grids", "zodiac"),
        "ride": ("Ride statuses", "mounts"), "currency": ("Currencies", "currencies"),
        "class": ("Classes", "labels"), "camp": ("Camps", "labels"), "faith": ("Faiths", "labels"),
        "guild_duty": ("Guild duties", "labels"), "quality": ("Item qualities", "labels"), "grade": ("Item grades", "labels"),
        "aptitude": ("Pet aptitudes", "labels"), "equipment_slot": ("Equipment slots", "labels"), "sex": ("Sexes", "labels"),
    }

    def __init__(self):
        self.lock = threading.RLock()
        self._files = {}
        self._index = {}
        self.rb = None             # set by dispatch: lets the item index merge the server's own templates

    def file(self, name):
        with self.lock:
            if name not in self._files:
                self._files[name] = _load_json(os.path.join(CATALOG_DIR, name + ".json"), {}) or {}
            return self._files[name]

    def types(self):
        return [{"type": k, "title": v[0]} for k, v in self.TYPES.items()]

    def index(self, typ):
        if typ not in self.TYPES:
            raise NotFound("no catalogue type %r (known: %s)" % (typ, ", ".join(sorted(self.TYPES))))
        with self.lock:
            if typ not in self._index:
                rows = getattr(self, "_ix_" + typ)()
                groups = []
                for r in rows:
                    if r[2] and r[2] not in groups:
                        groups.append(r[2])
                if typ == "item":
                    order = []
                    for _, g in ITEM_GROUPS:
                        if g in groups and g not in order:
                            order.append(g)
                    groups = order + [g for g in groups if g not in order]
                extra_fields = ["dead_status", "dead_group", "generated_translation"] if typ == "item" else []
                for row in rows:
                    if typ == "item":
                        meta = item_lifecycle.metadata(row[0])
                        row.extend(meta[k] for k in extra_fields)
                    row.append(self.search_text(typ, row[0]))
                self._index[typ] = {"type": typ, "title": self.TYPES[typ][0], "fields": INDEX_FIELDS + extra_fields + ["search_text"], "rows": rows,
                                    "groups": groups, "count": len(rows), "built": now_iso()}
            if typ == "item":
                self._index[typ]["families"] = self.file("mount-families").get("families", [])
            return self._index[typ]

    def search_text(self, typ, id_):
        """Human descriptions/effects, without changing canonical IDs or fetching one HTTP entry per row."""
        data = self.file(self.TYPES[typ][1])
        sub = {"guild_building": "buildings", "altar": "altars", "suit": "codes", "suit_tier": "tiers",
               "zodiac_sign": "signs", "zodiac_kind": "kinds", "zodiac_grid": "grids", "ride": "rides",
               "class": "classes", "camp": "camps", "faith": "faiths", "equipment_slot": "equipment_slots",
               "quality": "qualities", "grade": "grades", "aptitude": "pet_aptitudes", "guild_duty": "guild_duties"}.get(typ)
        if sub:
            data = data.get(sub) or {}
        entry = data.get(str(id_), {}) if isinstance(data, dict) else {}
        words = []
        if not hasattr(self, "_search_labels"):
            self._search_labels = {}
            for a in self.file("attributes").values():
                if isinstance(a, dict):
                    for key in a.get("keys") or []:
                        label = a.get("label") or key
                        self._search_labels[key.lower()] = label
                        # Formula suffixes A/B/C/E/F/G/Z share the client base stat.
                        if key[-1:] in "ABCEFGZ":
                            self._search_labels[key[:-1].lower()] = label
        labels = self._search_labels
        human = {"name", "display_name", "label", "desc", "description", "info", "text", "tooltip", "effect_label", "status_name",
                 "title", "details", "objectives", "completion", "giver_name", "responder_name"}
        def walk(value, key=""):
            if isinstance(value, dict):
                for k, v in value.items():
                    if k.lower() in labels:
                        words.extend([k, labels[k.lower()]])
                    walk(v, k)
            elif isinstance(value, list):
                for v in value:
                    walk(v, key)
            elif isinstance(value, str) and key in human:
                words.append(value)
        if typ == "item":
            words.extend([entry.get("name", ""), entry.get("desc", "")])
            entry = item_lifecycle.display(entry, id_)
            words.extend([entry["dead_status"], entry["dead_group"]])
        walk(entry)
        text = " ".join(words)
        if re.search(r"(?i)\bmp\b|maxmp|mprecover", text):
            text += " mana"
        if re.search(r"(?i)\bhp\b|maxhp|hprecover", text):
            text += " health"
        return text

    def matches(self, typ, id_, query, *extra):
        """Shared picker/autocomplete matching, including native descriptions and effect labels.

        Keep legacy numeric ID-prefix lookup; other queries may combine words from
        a server name, a client description and a human-readable attribute label.
        """
        query = str(query or "").strip().casefold()
        if not query:
            return True
        if query.isdigit():
            return str(id_).startswith(query)
        # The source catalogues are process-pinned, just like the cached index.
        with self.lock:
            if not hasattr(self, "_search_text_cache"):
                self._search_text_cache = {}
            key = (typ, str(id_))
            if key not in self._search_text_cache:
                self._search_text_cache[key] = self.search_text(typ, id_).casefold()
            text = self._search_text_cache[key]
        text = " ".join([str(id_), *(str(v) for v in extra if v is not None), text]).casefold()
        return all(word in text for word in query.split())

    def entry(self, typ, id_):
        sid = str(id_)
        if typ == "item":
            e = dict(self.file("items").get(sid) or {})
            srv = None
            if self.rb is not None:
                try:
                    srv = self.rb.cat()["items"].get(sid)
                except Exception:          # noqa: BLE001 - catalogue still works without the DB
                    srv = None
            if not e and not srv:
                raise NotFound("no item %s" % sid)
            if srv:
                e.setdefault("name", srv.get("name"))
                e.setdefault("kind", srv.get("kind"))
                e["server"] = {k: srv.get(k) for k in ("kind", "slot", "classes", "minlv", "maxlv", "overlap", "bindtype", "name")}
            if self.rb is not None:
                egg = self.rb.item_meta(int(id_)).get("pet_egg")
                if egg:
                    labels=self.file("labels").get("pet_aptitudes") or {}
                    e["pet_egg"]=dict(egg, choices=[dict(id=a, **labels.get(str(a), {"name":"Aptitude %d"%a})) for a in egg["aptitudes"]])
            e["in_client"] = sid in self.file("items")
            e["in_server"] = srv is not None if self.rb is not None else None
            e.setdefault("id", int(sid) if sid.lstrip("-").isdigit() else sid)
            return item_lifecycle.display(e, id_)
        src = {"appendattr": ("appendattrs", None), "stattype": ("attributes", None), "skill": ("skills", None),
               "talent": ("talents", None), "petskill": ("petskills", None), "pet": ("pets", None),
               "monster": ("monsters", None), "npc": ("npcs", None), "map": ("maps", None), "quest": ("quests", None),
               "title": ("titles", None), "status": ("statuses", None), "guild_building": ("guild", "buildings"),
               "altar": ("guild", "altars"), "holystone": ("holystones", None), "suit": ("suits", "codes"),
               "suit_tier": ("suits", "tiers"), "zodiac_sign": ("zodiac", "signs"), "zodiac_kind": ("zodiac", "kinds"),
               "zodiac_grid": ("zodiac", "grids"), "ride": ("mounts", "rides"), "currency": ("currencies", None)}.get(typ)
        if src:
            d = self.file(src[0])
            if src[1]:
                d = d.get(src[1]) or {}
            e = d.get(sid)
            if e is None:
                raise NotFound("no %s %s" % (typ, sid))
            return e if isinstance(e, dict) else {"id": id_, "name": e}
        for r in self.index(typ)["rows"]:
            if str(r[0]) == sid:
                return dict(zip(INDEX_FIELDS, r))
        raise NotFound("no %s %s" % (typ, sid))

    def name(self, typ, id_):
        """English name of one id ('' when unknown) - for change-log labels."""
        try:
            e = self.entry(typ, id_)
        except NotFound:
            return ""
        return e.get("name") or e.get("title") or e.get("label") or ""

    # ---- index builders (one row per entry, INDEX_FIELDS order)
    def _ix_item(self):
        ix = self.file("items-index")
        f = {n: i for i, n in enumerate(ix.get("fields") or [])}
        cross = self.file("items-crosscheck")
        client_only = {int(x["id"]) for x in cross.get("client_only") or []}
        server = None
        if self.rb is not None:
            try:
                server = self.rb.cat()["items"]
            except Exception:          # noqa: BLE001
                server = None
        rows, seen = [], set()
        for r in ix.get("rows") or []:
            g = lambda k: r[f[k]] if k in f else None      # noqa: E731
            iid = g("id")
            seen.add(iid)
            kind = g("kind") or ""
            flags = []
            display = item_lifecycle.display(self.file("items").get(str(iid), {}), iid)
            if g("cn") and not display["generated_translation"]:
                flags.append("cn")
            if (server is not None and str(iid) not in server) or (server is None and iid in client_only):
                flags.append("noserver")
            cat = g("category") or kind
            sub = " · ".join(x for x in ("#%s" % iid, _lv(g("minlv"), g("maxlv")), cat,
                                           ("Dead items: " + item_lifecycle.STATUS_LABELS[display["dead_status"]]) if display["dead_status"] else "",
                                           "GENERATED translation" if display["generated_translation"] else "") if x)
            client_item = self.file("items").get(str(iid), {})
            sprite = client_item["sprite"] if "sprite" in client_item else g("sprite")
            rows.append(_row(iid, display.get("name") or g("name"), ITEM_GROUP.get(kind, "Other"), cat, sub, sprite, g("classes") or None,
                             g("minlv"), g("maxlv"), None, " ".join(flags), kind))
        extra = []
        if server is not None:
            for k, s in server.items():
                if int(k) not in seen:
                    extra.append({"id": int(k), "name": s.get("name"), "kind": s.get("kind"), "min_level": s.get("minlv")})
        else:
            extra = cross.get("db_only") or []
        for x in extra:
            kind = x.get("kind") or ""
            rows.append(_row(int(x["id"]), x.get("name") or "item %s" % x["id"], ITEM_GROUP.get(kind, "Other"), kind,
                             "#%s · %s · not in your client" % (x["id"], kind), None, None, x.get("min_level"), None, None,
                             "noclient", kind))
        rows.sort(key=lambda r: r[0])
        return rows

    def _ix_appendattr(self):
        rows = []
        d = self.file("appendattrs")
        for k, a in d.items():
            rows.append(_row(a["id"], a.get("label") or a.get("key"), None, a.get("label"),
                             "#%s · %s · grade table G1–G%s" % (a["id"], a.get("key"), a.get("max_level")), None, None, None, None,
                             "green", "", a.get("key") or ""))
        if self.rb is not None:
            try:
                for k, a in self.rb.cat()["attributes"].items():
                    if k not in d:
                        lab = (self.file("attributes").get(str(a.get("stat"))) or {}).get("label") or a.get("key")
                        rows.append(_row(int(k), lab, None, lab, "#%s · %s · not in your client" % (k, a.get("key")),
                                         None, None, None, None, "green", "noclient", a.get("key") or ""))
            except Exception:          # noqa: BLE001
                pass
        rows.sort(key=lambda r: r[0])
        return rows

    def _ix_stattype(self):
        return [_row(int(k), v.get("label"), None, None, "stat type %s" % k, key=" ".join(v.get("keys") or []))
                for k, v in sorted(self.file("attributes").items(), key=lambda kv: int(kv[0]))]

    def _ix_skill(self):
        rows = []
        for k, s in self.file("skills").items():
            cls = s.get("class")
            grp = CLASS_NAMES.get(cls, "Other (item / monster)") if cls is not None else "Other (item / monster)"
            sub = " · ".join(x for x in ("#%s" % s["id"], "Lv %s" % s["level"] if s.get("level") else "",
                                         "MP %s" % s["mp"] if s.get("mp") else "") if x)
            rows.append(_row(s["id"], s.get("name"), grp, s.get("family_name") or s.get("base"), sub, s.get("sprite"),
                             [cls] if cls is not None else None, None, None, None,
                             "cn" if re.search(r"[一-鿿]", s.get("name") or "") else "", str(s.get("family") or "")))
        rows.sort(key=lambda r: r[0])
        return rows

    def _ix_talent(self):
        rows = []
        for k, t in self.file("talents").items():
            cls = t.get("class")
            eff = ", ".join(e.get("label") or e.get("stat") or "" for e in t.get("effects") or [])
            rows.append(_row(t["id"], t.get("name"), CLASS_NAMES.get(cls, "Other"), None, "#%s · %s" % (t["id"], eff),
                             t.get("sprite"), [cls] if cls is not None else None))
        rows.sort(key=lambda r: r[0])
        return rows

    def _ix_petskill(self):
        rows = []
        for k, s in self.file("petskills").items():
            m = re.search(r"Available to (.+?) only", s.get("info") or "")
            fam = m.group(1) if m else None
            rows.append(_row(s["id"], s.get("name"), fam and "Species-only" or "All pets", fam,
                             "#%s · %s" % (s["id"], ("%s only" % fam) if fam else "any pet"), s.get("sprite"),
                             flags="restricted" if fam else ""))
        rows.sort(key=lambda r: r[0])
        return rows

    def _ix_pet(self):
        return [_row(p["species"], p.get("name"), None, None, "species %s" % p["species"], p.get("sprite"))
                for k, p in sorted(self.file("pets").items(), key=lambda kv: int(kv[0]))]

    def _ix_monster(self):
        maps = self.file("maps")
        rows = []
        for k, m in sorted(self.file("monsters").items()):
            where = ", ".join((maps.get(str(x)) or {}).get("name", str(x)) for x in (m.get("maps") or [])[:3])
            rows.append(_row(k, m.get("name"), (m.get("rank") or "normal").capitalize(), None,
                             "%s%s" % (k, " · " + where if where else ""), key=k))
        return rows

    def _ix_npc(self):
        return [_row(k, n.get("name"), n.get("scene"), None, "%s%s" % (k, " · " + n["desc"][:60] if n.get("desc") else ""), key=k)
                for k, n in sorted(self.file("npcs").items())]

    def _ix_map(self):
        return [_row(m["id"], m.get("name"), None, None, "#%s · %s" % (m["id"], m.get("scene") or ""), key=m.get("scene") or "")
                for k, m in sorted(self.file("maps").items(), key=lambda kv: int(kv[0]))]

    def _ix_quest(self):
        rows = []
        for k, q in self.file("quests").items():
            camp = q.get("camp")
            cls = q.get("class")
            grp = CAMP_NAMES.get(camp, "Both camps")
            sub = " · ".join(x for x in ("#%s" % q["id"], _lv(q.get("min_level"), q.get("max_level")),
                                         q.get("giver_name") or "") if x)
            rows.append(_row(q["id"], q.get("title") or "quest %s" % q["id"], grp, None, sub, None,
                             [cls] if isinstance(cls, int) and cls >= 0 else None, q.get("min_level"), q.get("max_level"),
                             None, "" if q.get("has_text", True) else "notext"))
        rows.sort(key=lambda r: r[0])
        return rows

    def _ix_title(self):
        return [_row(t["id"], t.get("name"), None, None, "#%s" % t["id"])
                for k, t in sorted(self.file("titles").items(), key=lambda kv: int(kv[0]))]

    def _ix_status(self):
        return [_row(s["id"], s.get("name"), None, s.get("effect_label"),
                     "#%s · %s" % (s["id"], s.get("effect_label") or ""), s.get("sprite"))
                for k, s in sorted(self.file("statuses").items(), key=lambda kv: int(kv[0]))]

    def _ix_guild_building(self):
        return [_row(b["id"], b.get("name"), None, None, "#%s" % b["id"], b.get("sprite"))
                for k, b in sorted((self.file("guild").get("buildings") or {}).items(), key=lambda kv: int(kv[0]))]

    def _ix_altar(self):
        return [_row(b["id"], b.get("name"), None, None, "#%s" % b["id"], b.get("sprite"))
                for k, b in sorted((self.file("guild").get("altars") or {}).items(), key=lambda kv: int(kv[0]))]

    def _ix_holystone(self):
        return [_row(s["id"], s.get("name"), None, None, "#%s · %s" % (s["id"], s.get("effect_text") or s.get("desc") or ""),
                     s.get("sprite") if s.get("sprite") is not None else s.get("socket_sprite"), key=s.get("key") or "")
                for k, s in sorted(self.file("holystones").items(), key=lambda kv: int(kv[0]))]

    SUIT_TIERS = {0: "Ordinary", 1: "Bronze", 2: "Silver", 3: "Gold", 4: "Platinum", 5: "Mithril", 6: "Orichalcum",
                  7: "Adamantium"}      # font.lua BRONZE_COLOR .. ADAMANTIUM_COLOR, EquipDescription.dat Suit<tier><level>

    def _ix_suit(self):
        tiers = self.file("suits").get("tiers") or {}
        rows = []
        for k, c in sorted((self.file("suits").get("codes") or {}).items(), key=lambda kv: int(kv[0])):
            t = int(c.get("tier") or 0)
            st = ((tiers.get(str(t)) or {}).get("stages") or [{}])[0]
            rows.append(_row(c["code"], c.get("name"), self.SUIT_TIERS.get(t, "tier %s" % t), None,
                             "code %s · +%s%% suit effect" % (c["code"], round(float(c.get("bonus") or 0) * 100, 1)),
                             st.get("sprite"), color="suit%d" % t if t else None))
        return rows

    def _ix_suit_tier(self):
        rows = []
        for k, t in sorted((self.file("suits").get("tiers") or {}).items(), key=lambda kv: int(kv[0])):
            st = ((t.get("stages") if isinstance(t, dict) else None) or [{}])
            rows.append(_row(int(k), self.SUIT_TIERS.get(int(k), "tier %s" % k), None, None,
                             " / ".join(x.get("name", "") for x in st), st[0].get("sprite"), color="suit%s" % k if int(k) else None))
        return rows

    def _ix_zodiac_sign(self):
        return [_row(s["id"], s.get("name"), None, None, "%s · %s" % (s.get("dates", ""), s.get("god", "")))
                for k, s in sorted((self.file("zodiac").get("signs") or {}).items(), key=lambda kv: int(kv[0]))]

    def _ix_zodiac_kind(self):
        return [_row(s["kind"], s.get("name"), CLASS_NAMES.get(s.get("class"), "Other"), None, "kind %s" % s["kind"],
                     s.get("sprite"), [s["class"]] if s.get("class") is not None else None)
                for k, s in sorted((self.file("zodiac").get("kinds") or {}).items(), key=lambda kv: int(kv[0]))]

    def _ix_zodiac_grid(self):
        return [_row(g["grid"], "Grid %d" % (g["grid"] + 1), None, None, "accepts %s" % g.get("accepts_kinds", ""))
                for k, g in sorted((self.file("zodiac").get("grids") or {}).items(), key=lambda kv: int(kv[0]))]

    def _ix_ride(self):
        return [_row(r["status"], r.get("name"), None, None, "status %s" % r["status"], r.get("sprite"))
                for k, r in sorted((self.file("mounts").get("rides") or {}).items(), key=lambda kv: int(kv[0]))]

    def _ix_currency(self):
        return [_row(k, c.get("label"), None, None, c.get("column", ""), key=k) for k, c in self.file("currencies").items()]

    def _labels(self, key, word, color=None):
        rows = []
        for k, v in (self.file("labels").get(key) or {}).items():
            if not str(k).lstrip("-").isdigit():
                continue
            name = v.get("name") if isinstance(v, dict) else v
            rows.append(_row(int(k), name, None, None, "%s %s" % (word, k), color=(color % int(k)) if color else None))
        rows.sort(key=lambda r: r[0])
        return rows

    def _ix_class(self):
        return self._labels("classes", "class")

    def _ix_camp(self):
        return self._labels("camps", "camp")

    def _ix_faith(self):
        return self._labels("faiths", "faith")

    def _ix_guild_duty(self):
        return self._labels("guild_duties", "duty")

    def _ix_quality(self):
        rows = [_row(0, "None (Q0)", None, None, "quality 0: consumables, materials", color="q0")]
        rows += self._labels("qualities", "quality", "q%d")
        for r in rows:
            if r[0] > 20:
                r[4] += " · above this client's Q20"
                r[10] = "overclient"
        return rows

    def _ix_grade(self):
        # the client lists grades 1-12 on gear (ItemColor.xml has colours to 13); the server allows 0-25
        rows = [_row(0, "Grade 0", None, None, "no grade")]
        for k in range(1, 26):
            rows.append(_row(k, "Grade %d" % k, None, None, "%d stars%s" % (k, " · above this client's G25" if k > 25 else ""),
                             color="g%d" % k, flags="overclient" if k > 25 else ""))
        return rows

    def _ix_aptitude(self):
        rows = self._labels("pet_aptitudes", "aptitude", "apt%d")
        for r in rows:
            if r[0] > 16:
                r[4] += " · the server allows 1-16"
                r[10] = "overserver"
        return rows

    def _ix_equipment_slot(self):
        return self._labels("equipment_slots", "slot")

    def _ix_sex(self):
        return self._labels("sexes", "sex")


CATALOG = CatalogService()


# =========================================================================== glossary + write rules
APPLIES_TEXT = {
    # glossary.json _meta.applies and write_rules.json legend.applies, in plain words for the chip
    "now": "applies at the next use", "per_use": "applies at the next use", "hot_reload": "applies within 5 seconds",
    "next_login": "applies at the character's next login", "login": "applies at the character's next login",
    "after_restart": "applies after gw251 restart", "startup": "applies after gw251 restart",
    "new_revision_and_restart": "not editable (published content)", "never": "the server does not read this",
    "not_editable": "not editable", "live": "applied now by the running server", "unknown": "not established",
}


class GlossaryService:
    """schema/glossary.json (what every table / column is) + schema/write_rules.json (whether the editor may write
    it, when it applies, why not). field(table, col) is what the hover help and the number fields show."""

    def __init__(self):
        self.lock = threading.RLock()
        self._g = None
        self._w = None

    def _load(self):
        with self.lock:
            if self._g is None:
                self._g = _load_json(os.path.join(SCHEMA_DIR, "glossary.json"), {}) or {}
                self._w = _load_json(os.path.join(SCHEMA_DIR, "write_rules.json"), {}) or {}
        return self._g, self._w

    def meta(self):
        g, w = self._load()
        return {"glossary": g.get("_meta", {}), "write_rules": {k: w.get(k) for k in ("legend", "online_check", "capabilities", "written")},
                "applies_text": APPLIES_TEXT}

    def rules(self, table):
        g, w = self._load()
        return (w.get("tables") or {}).get(table) or {}

    def table(self, table):
        g, w = self._load()
        t = g.get(table)
        if t is None:
            raise NotFound("no table %r in the glossary" % table)
        r = self.rules(table)
        cols = {c: self._merge(table, c, v, t, r) for c, v in (t.get("columns") or {}).items()}
        out = {k: v for k, v in t.items() if k != "columns"}
        out.update({"table": table, "columns": cols, "rules": {k: v for k, v in r.items() if k != "fields"},
                    "editor": r.get("editor"), "applies_rule": r.get("applies"),
                    "applies_text": APPLIES_TEXT.get(r.get("applies") or t.get("applies") or "unknown")})
        return out

    def field(self, table, col):
        g, w = self._load()
        t = g.get(table)
        if t is None or col not in (t.get("columns") or {}):
            raise NotFound("no field %s.%s in the glossary" % (table, col))
        return self._merge(table, col, t["columns"][col], t, self.rules(table))

    def fields(self, names):
        out = {}
        for n in names:
            t, _, c = n.partition(".")
            try:
                out[n] = self.field(t, c)
            except NotFound as e:
                out[n] = {"field": n, "missing": str(e)}
        return out

    def _merge(self, table, col, gc, gt, r):
        fr = (r.get("fields") or {}).get(col) or {}
        d = dict(gc)
        d["field"] = "%s.%s" % (table, col)
        d["table"], d["column"] = table, col
        if fr.get("label"):
            d["label"] = fr["label"]
        for k in ("min", "max"):
            if fr.get(k) is not None:
                d[k] = fr[k]
                d["range_source"] = "write_rules"
        if fr.get("enum"):
            d["values"] = fr["enum"]
        editor = fr.get("editor") or r.get("editor") or ("read_only" if gc.get("edit_class") in ("sealed", "internal") else None)
        applies = fr.get("applies") or r.get("applies") or gc.get("applies") or gt.get("applies") or "unknown"
        d["editor"] = editor
        d["applies_rule"] = applies
        d["applies_text"] = APPLIES_TEXT.get(applies, applies)
        why = fr.get("why") or fr.get("rule") or (r.get("why") if editor in ("read_only", "hidden") else None)
        if gc.get("edit_class") in ("sealed", "internal") and not why:
            why = gt.get("read_only_reason") or gt.get("note")
        if gc.get("edit_class") == "revision" and not why:
            why = "a version counter: the editor bumps it by one with the data, never types a value"
        if why:
            d["why"] = why
        if fr.get("revision"):
            d["revision"] = fr["revision"]
        if fr.get("live_capability"):
            d["live_capability"] = fr["live_capability"]
        d["online"] = r.get("online") or {}
        d["editable"] = (editor in ("edit", "edit_advanced", "insert_only")
                         and gc.get("edit_class") not in ("sealed", "internal", "revision", "derived")
                         and fr.get("editor") not in ("read_only", "hidden"))
        d["advanced"] = editor == "edit_advanced"
        return d


GLOSSARY = GlossaryService()


# =========================================================================== capabilities (round2) + server status
# One table maps round2's endpoint paths to the capability names the write rules use (WRITE-RULES.md s.7 step 7.2).
# If round2 ships other paths, change them HERE only.
CAP_PATHS = {
    "/admin/item/add": ["character.give_item"],
    "/admin/item/remove": ["character.item_delete"],
    "/admin/item/move": ["character.item_move"],
    "/admin/item/set": ["character.item_set"],
    "/admin/progression": ["character.level", "character.talent_points", "character.zodiac_energy", "character.progression"],
    "/admin/money": ["character.wallet"],
    "/admin/teleport": ["character.move"],
    "/admin/kick": ["character.kick"],
    "/admin/buff/apply": ["character.buff_apply"],
    "/admin/buff/inspect": ["character.buff_inspect"],
    "/admin/buff/remove": ["character.buff_remove"],
    "/admin/mute": ["character.mute"],
    "/admin/ban": ["account.ban"],
    "/admin/rename": ["character.rename"],
    "/admin/online": ["server.online_list"],
    "/admin/quest/chain": ["character.quest_chain"],
    "/rates/reload": ["rates.reload"],
}
_CAPS = {"t": 0.0, "v": None}
_CAPS_LOCK = threading.Lock()


def mgmt_port(app):
    if getattr(app, "rates", None) and app.rates.mgmt_port:
        return int(app.rates.mgmt_port)
    a = getattr(app, "a", None)
    if a is not None and getattr(a, "rates_mgmt", None):
        return int(a.rates_mgmt)
    rb = getattr(app, "reborn", None)
    return 9090 if rb is not None and rb.live else None


def _http(method, port, path, body=None, timeout=2.0):
    """(status, parsed JSON or text) from the loopback management port. Never sends anywhere but 127.0.0.1."""
    url = "http://127.0.0.1:%d%s" % (int(port), path)
    data = json.dumps(body).encode("utf-8") if body is not None else (b"" if method == "POST" else None)
    req = urllib.request.Request(url, data=data, method=method, headers={"Content-Type": "application/json"} if body is not None else {})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            raw = r.read(1_000_000).decode("utf-8", "replace")
            status = r.status
    except urllib.error.HTTPError as e:
        raw = e.read(200_000).decode("utf-8", "replace")
        status = e.code
    try:
        return status, json.loads(raw)
    except ValueError:
        return status, raw


def parse_capabilities(doc):
    """Both shapes: WRITE-RULES's {"operations": {name: {...}}} and round2's {"endpoints": [{method, path, body}]}."""
    ops = {}
    if isinstance(doc, dict) and isinstance(doc.get("operations"), dict):
        for name, o in doc["operations"].items():
            if isinstance(o, dict) and o.get("path"):
                ops[name] = {"method": o.get("method", "POST"), "path": o["path"], "online": bool(o.get("online", True)),
                             "offline": bool(o.get("offline", False)), "fields": o.get("fields") or [], "cas": bool(o.get("cas"))}
    if isinstance(doc, dict) and isinstance(doc.get("endpoints"), list):
        for e in doc["endpoints"]:
            if not isinstance(e, dict) or e.get("method") != "POST" or not e.get("path"):
                continue
            body = e.get("body") if isinstance(e.get("body"), dict) else {}
            for name in CAP_PATHS.get(e["path"], ["path:" + e["path"]]):
                # round2: an online character is edited by the server; an offline one answers 409 "offline"
                ops[name] = {"method": "POST", "path": e["path"], "online": True, "offline": False,
                             "fields": sorted(body), "body": body, "cas": False}
    return ops


def capabilities(app, force=False):
    """The game server's live-edit operations (round2), probed at most every 60 s; {available: False} when the
    management port has none (404, refused, malformed, or no port configured)."""
    port = mgmt_port(app)
    with _CAPS_LOCK:
        v = _CAPS["v"]
        if not force and v is not None and v.get("port") == port and time.time() - _CAPS["t"] < 60:
            return v
    out = {"available": False, "port": port, "checked": now_iso(), "operations": {}, "error": None}
    if not port:
        out["error"] = "no management port (start the editor with --rates-mgmt PORT for a copy; the live database uses 9090)"
    else:
        try:
            st, doc = _http("GET", port, "/admin/capabilities", timeout=2.0)
            if st == 200 and isinstance(doc, dict):
                ops = parse_capabilities(doc)
                out.update(available=bool(ops), operations=ops, version=doc.get("version"), server=doc.get("server"), gm_authority=doc.get("gmAuthority"), title_grants=doc.get("titleGrants"),
                           notes={k: doc[k] for k in ("offline", "busy") if k in doc})
                if not ops:
                    out["error"] = "the server answered /admin/capabilities but listed no operation this editor knows"
            else:
                out["error"] = "management port %s has no /admin/capabilities (HTTP %s): live edits of online characters " \
                               "are not available, log the character out to edit" % (port, st)
        except (OSError, urllib.error.URLError, ValueError) as e:
            out["error"] = "management port %s did not answer (%s)" % (port, getattr(e, "reason", e))
    with _CAPS_LOCK:
        _CAPS.update(t=time.time(), v=out)
    return out


def server_status(app):
    """Is the game server that uses this database up? (management /readyz; live: also systemd)."""
    port = mgmt_port(app)
    out = {"port": port, "state": "unknown", "detail": ""}
    if port:
        try:
            st, doc = _http("GET", port, "/readyz", timeout=2.0)
            out["state"] = "ready" if st == 200 else "not ready"
            out["detail"] = doc if isinstance(doc, (dict, str)) else ""
            if isinstance(out["detail"], str):
                out["detail"] = out["detail"][:300]
        except (OSError, urllib.error.URLError, ValueError) as e:
            out["state"], out["detail"] = "down", "management port %s did not answer (%s)" % (port, getattr(e, "reason", e))
    else:
        out["state"], out["detail"] = "no port", "no management port configured for this database (--rates-mgmt)"
    rb = getattr(app, "reborn", None)
    if rb is not None and rb.live:
        try:
            p = subprocess.run(["systemctl", "is-active", "godswar251"], capture_output=True, timeout=5)
            out["service"] = p.stdout.decode().strip()
        except (OSError, subprocess.SubprocessError):
            out["service"] = "unknown"
    return out


# =========================================================================== SQL helpers
def where_sql(where):
    """{col: value} -> 'col = lit AND ...' (None -> IS NULL)."""
    if isinstance(where, str):
        return where
    parts = []
    for c, v in where.items():
        parts.append("%s IS NULL" % ident(c) if v is None else "%s = %s" % (ident(c), lit(v)))
    return " AND ".join(parts) or "true"


def expected_sql(expected):
    """DO-block that aborts the transaction with 'gwadmin-conflict: ...' (HTTP 409) when a row no longer has the
    values the page showed. expected = [{"table", "where": {col: v} | sql, "values": {col: v}}]; a row that must
    not exist: values None."""
    if not expected:
        return None
    checks = []
    for e in expected:
        t = e["table"]
        if not re.match(r"^[a-z_][a-z0-9_]*$", t):
            raise Bad("bad table name %r" % t)
        w = where_sql(e.get("where") or {})
        vals = e.get("values")
        label = ("%s %s" % (t, w)).replace("'", "''").replace("%", "%%")[:160]
        if vals is None:
            checks.append("  IF EXISTS (SELECT 1 FROM %s WHERE %s) THEN RAISE EXCEPTION 'gwadmin-conflict: %s exists now'; END IF;"
                          % (t, w, label))
            continue
        cond = " AND ".join("%s IS NOT DISTINCT FROM %s" % (ident(c), lit(v)) for c, v in vals.items()) or "true"
        exp = (", ".join("%s=%s" % (c, v) for c, v in vals.items())).replace("'", "''").replace("%", "%%")[:200]
        checks.append("  IF NOT EXISTS (SELECT 1 FROM %s WHERE %s AND %s) THEN RAISE EXCEPTION "
                      "'gwadmin-conflict: %s changed since you opened it (expected %s)'; END IF;" % (t, w, cond, label, exp))
    return "DO $gwx$\nBEGIN\n%s\nEND\n$gwx$" % "\n".join(checks)


def _bumps_for(table, cols):
    """Revision counters that move with these columns: same-table counters for the SET list, and cross-table ones
    ('character_base.inventory_revision') the caller adds once per edit."""
    r = GLOSSARY.rules(table)
    same, cross = set(), set()
    for c in cols:
        rv = ((r.get("fields") or {}).get(c) or {}).get("revision")
        if rv:
            same.add(rv)
    for ctr in (r.get("revision") or {}).get("counters") or []:
        if table == "character_base":
            continue                        # character_base counters are per field (above)
        if "." in ctr:
            cross.add(ctr)
        else:
            same.add(ctr)
    return same, cross


def update_sql(table, where, values, bump=True):
    """UPDATE <table> SET col = value, ... [, <counter> = <counter> + 1] WHERE ... (counters from write_rules)."""
    if not values:
        raise Bad("nothing to update")
    sets = ["%s = %s" % (ident(c), lit(v)) for c, v in values.items()]
    if bump:
        same, _ = _bumps_for(table, values.keys())
        sets += ["%s = %s + 1" % (ident(c), ident(c)) for c in sorted(same) if c not in values]
    return "UPDATE %s SET %s WHERE %s" % (table, ", ".join(sets), where_sql(where))


def insert_sql(table, values):
    cols = list(values)
    return "INSERT INTO %s (%s) VALUES (%s)" % (table, ", ".join(ident(c) for c in cols), ", ".join(lit(values[c]) for c in cols))


def delete_sql(table, where):
    return "DELETE FROM %s WHERE %s" % (table, where_sql(where))


def bump_sql(cid, counter):
    """character_base.<counter> + 1 for one character (once per edit)."""
    c = counter.split(".")[-1]
    return "UPDATE character_base SET %s = %s + 1 WHERE id = %d" % (ident(c), ident(c), int(cid))


def scope(table, where, pk=None, counters=None, ignore=None):
    """One entry of a rows_write scope: {table, where, pk, counters, ignore}.
    pk       default: the glossary primary key;
    counters revision columns that move forward by one on a restore (default: write_rules counters of the table,
             plus columns the glossary classes 'revision');
    ignore   columns the database stamps itself (default: glossary edit_class 'derived', e.g. updated_at /
             updated_by): never restored and never counted as "changed since"."""
    g, _ = GLOSSARY._load()
    gt = g.get(table) or {}
    if pk is None:
        pk = gt.get("primary_key")
        if not pk:
            raise Bad("table %s has no primary key in the glossary; give pk=" % table)
    cols = gt.get("columns") or {}
    if counters is None:
        counters = [c for c in ((GLOSSARY.rules(table).get("revision") or {}).get("counters") or []) if "." not in c]
        counters += [c for c, v in cols.items() if v.get("edit_class") == "revision" and c not in counters and c not in pk]
    if ignore is None:
        ignore = [c for c, v in cols.items() if v.get("edit_class") == "derived" and c not in pk and c not in counters]
    return {"table": table, "where": where_sql(where), "pk": list(pk), "counters": list(counters), "ignore": list(ignore)}


# =========================================================================== field checks + writes
def check_fields(fields, advanced=False):
    """Refuse (403) a write to any read-only / hidden / bookkeeping field; edit_advanced needs advanced=True.
    fields: ["table.col", ...] or ["table.*"] for a whole-row insert/delete."""
    for f in fields:
        t, _, c = f.partition(".")
        r = GLOSSARY.rules(t)
        if not r:
            raise Refused("%s: no write rule for this table (schema/write_rules.json); not editable" % t)
        if r.get("editor") in ("read_only", "hidden"):
            raise Refused("%s is read-only: %s" % (r.get("label") or t, r.get("why") or "see schema/WRITE-RULES.md"))
        if r.get("editor") == "edit_advanced" and not advanced:
            raise Refused("%s is an advanced table: turn on 'Advanced' first (%s)" % (r.get("label") or t, r.get("why") or
                                                                                       "it has invariants the game does not show"))
        if c in ("", "*"):
            continue
        d = GLOSSARY.field(t, c)
        if not d["editable"]:
            if d.get("advanced") and advanced and d.get("edit_class") not in ("sealed", "internal", "revision", "derived"):
                continue
            raise Refused("%s (%s) is not editable: %s" % (d.get("label") or c, f, d.get("why") or d.get("edit_class") or "read-only"))


def online_info(app, cid):
    """The online state of one character with what still works: {online, name, reasons, live[], kick}."""
    rb = app.reborn
    on = rb.online_state(cid)
    caps = capabilities(app)
    ops = caps.get("operations") or {}
    on["live"] = sorted(k for k, v in ops.items() if v.get("online"))
    on["live_fields"] = sorted(c for c, (op, _) in LIVE_FIELDS.items() if (ops.get(op) or {}).get("online"))
    on["live_bag"] = sorted(op for op in ("character.give_item", "character.item_delete", "character.item_move") if op in ops)
    on["kick"] = "character.kick" in ops
    on["capabilities"] = caps.get("available", False)
    on["stale_hint"] = ("If the server crashed, these signals can stay set; the editor then refuses (safe). "
                        "Clear them only with the server stopped.") if on.get("online") else ""
    return on


# =========================================================================== live edits (round2) or database
# A character_base column the round2 management API can set on an ONLINE character: (capability, JSON body key).
# Columns sharing a capability path go out in one call (Money + Stone -> one /admin/money). Change names HERE only.
LIVE_FIELDS = {
    "Money": ("character.wallet", "silver"),
    "Stone": ("character.wallet", "gold"),
    "BindingGold": ("character.wallet", "bindGold"),
    "fighter_job_lv": ("character.level", "level"),
    "fighter_job_exp": ("character.level", "experience"),
    "SkillPoint": ("character.talent_points", "talentPoints"),
    "zodiac_energy": ("character.zodiac_energy", "zodiacEnergy"),
    "Map": ("character.move", "map"),
    "Pos_X": ("character.move", "x"),
    "Pos_Z": ("character.move", "z"),
}
MOVE_COLS = ("Map", "Pos_X", "Pos_Z")          # /admin/teleport needs all three
BAG_SLOTS = 96                                 # kit bag 0..95 (4 pages x 24); round2's item API covers only these
LIVE_RETRIES = 3                               # "busy" (changing maps / logging in): retry, never write the database


def live_plan(caps, cols):
    """Which round2 calls would set these character_base columns on an online character.
    Returns (calls {path: [cols]}, missing [cols without an advertised operation])."""
    ops = (caps or {}).get("operations") or {}
    calls, missing = {}, []
    for c in cols:
        lf = LIVE_FIELDS.get(c)
        o = ops.get(lf[0]) if lf else None
        if not o or not o.get("online"):
            missing.append(c)
            continue
        calls.setdefault(o["path"], []).append(c)
    return calls, missing


def live_text(cols):
    """'Silver, Gold' - the glossary labels of columns, for messages."""
    return ", ".join(GLOSSARY.field("character_base", c).get("label") or c for c in cols)


class Ctx:
    def __init__(self, app, method, path, query, body, mod=None):
        self.app, self.method, self.path, self.query, self.body, self.module = app, method, path, query or {}, body or {}, mod
        self.rb = app.reborn
        self.catalog, self.glossary = CATALOG, GLOSSARY

    # ---- request
    def q(self, key, default=""):
        return self.query.get(key, default)

    def qint(self, key, default=None, lo=None, hi=None):
        v = self.query.get(key)
        if v in (None, ""):
            return default
        return to_int(v, lo, hi, key)

    def need_rb(self):
        if not self.rb:
            raise Bad("the reborn database adapter is not available (see the editor's start-up log)")
        return self.rb

    @property
    def caps(self):
        return capabilities(self.app)

    # ---- writes
    def _record(self, extra):
        def rec(c):
            c.update(extra)
            self.app.record_reborn_change(c)
        return rec

    def char_write(self, cid, action, build, fields=(), expected=None, advanced=False, applies=None, cross_bumps=True,
                   online_ok=False):
        """Write one character's rows (offline only) through reborn.Reborn.write.
        build(d) -> [SQL]   (d = the character's rows right now: reborn.DUMP_TABLES, incl. the extra tables)
        fields   -> ["table.col"|"table.*"] being written: read-only ones are refused (403), revision counters
                    of character_base fields are bumped in your UPDATE if you use update_sql(), and cross-table
                    counters (character_items -> character_base.inventory_revision) are added once here.
        expected -> [{"table", "where", "values"}]: 409 if the rows changed since the page read them.
        Returns {"change": <public change record>, "applies", "applies_text"}."""
        rb = self.need_rb()
        cid = int(cid)
        check_fields(fields, advanced)
        info = online_info(self.app, cid)
        if info["online"] and not online_ok:
            whys = sorted({(GLOSSARY.rules(f.partition(".")[0]).get("online") or {}).get("why") or "" for f in fields} - {""})
            tail = ("This edit has no live version in the game server; log the character out first." if info.get("capabilities")
                    else "Live edits arrive with the round-2 server update; this character must be logged out.")
            raise OnlineRefused("%s is online - %s. %s %s" % (info["name"], "; ".join(info["reasons"]), " / ".join(whys), tail),
                                dict(info, why=whys))
        tables = sorted({f.partition(".")[0] for f in fields})
        if applies is None:
            ap = [GLOSSARY.rules(t).get("applies") for t in tables]
            applies = "next_login" if "next_login" in ap or not ap else (ap[0] or "next_login")
        cross = set()
        if cross_bumps:
            for t in tables:
                _, cr = _bumps_for(t, [])
                cross |= cr

        def b(d):
            stmts = list(build(d) or [])
            if not stmts:
                return stmts
            pre = expected_sql(expected)
            body = "\n".join(stmts)
            for ctr in sorted(cross):
                col = ctr.split(".")[-1]
                if not re.search(r"\b%s\s*=" % re.escape(col), body):
                    stmts.append(bump_sql(cid, col))
            return ([pre] if pre else []) + stmts

        try:
            c = rb.write(cid, action, b, self._record({"module": self.module.name if self.module else None, "applies": applies,
                                                       "applies_text": APPLIES_TEXT.get(applies, applies), "tables": tables}),
                         online_ok=online_ok)
        except RebornError as e:
            m = re.search(r"gwadmin-conflict: (.*)", str(e))
            if m:
                raise Conflict(m.group(1).strip())
            raise
        return {"change": public_change(c), "applies": applies, "applies_text": APPLIES_TEXT.get(applies, applies)}

    def rows_write(self, scope_list, action, build, fields=(), lock_cids=(), guard_sql=(), expected=None, advanced=False,
                   applies=None, target="", no_undo=None):
        """Write rows that are not one character's (guild, start-up content, settings) through reborn.write_rows.
        scope_list: [scope(table, where)] - every row the write may touch (snapshot, lock, Undo);
        lock_cids: characters whose rows also change (must be offline; locked FOR UPDATE OF c, a);
        guard_sql: DO-blocks that RAISE when an invariant breaks (run before COMMIT and again on Undo)."""
        rb = self.need_rb()
        check_fields(fields, advanced)
        tables = sorted({f.partition(".")[0] for f in fields} | {s["table"] for s in scope_list})
        if applies is None:
            ap = [GLOSSARY.rules(t).get("applies") for t in tables]
            applies = "after_restart" if "after_restart" in ap else (next((a for a in ap if a), "now"))
        for cid in lock_cids:
            info = online_info(self.app, cid)
            if info["online"]:
                raise OnlineRefused("%s is online - %s. Log the character out first." % (info["name"], "; ".join(info["reasons"])), info)

        def b(before):
            stmts = list(build(before) or [])
            pre = expected_sql(expected)
            return ([pre] if pre and stmts else []) + stmts

        try:
            c = rb.write_rows(scope_list, action, b, self._record({}), target=target, lock_cids=lock_cids, guard_sql=guard_sql,
                              meta={"module": self.module.name if self.module else None, "applies": applies,
                                    "applies_text": APPLIES_TEXT.get(applies, applies), "tables": tables, "no_undo": no_undo})
        except RebornError as e:
            m = re.search(r"gwadmin-conflict: (.*)", str(e))
            if m:
                raise Conflict(m.group(1).strip())
            raise
        return {"change": public_change(c), "applies": applies, "applies_text": APPLIES_TEXT.get(applies, applies)}

    # ---- live-or-database (the one helper a module needs for an edit that may hit an online character)
    def _live_post(self, path, character, body):
        """POST one round2 call (retries "busy"). Returns (status, doc)."""
        caps = capabilities(self.app)
        payload = dict(body)
        payload["actor"] = "web:%s" % (self.module.name if self.module else "editor")
        payload["character"] = character
        for i in range(LIVE_RETRIES):
            st, doc = _http("POST", caps["port"], path, payload, timeout=8.0)
            if not (st == 409 and isinstance(doc, dict) and doc.get("error") == "busy"):
                return st, doc
            time.sleep(1.0 + i)
        return st, doc

    def _live_record(self, cid, name, action, calls, before, after, inverse=None, applies="live"):
        rec = {"engine": "reborn-live", "id": "lv-" + datetime.datetime.now().strftime("%Y%m%d-%H%M%S-%f"), "time": now_iso(),
               "database": self.rb.db if self.rb else None, "character": name, "cid": cid, "action": action,
               "sql": [{"db": "game server 127.0.0.1:%s" % mgmt_port(self.app), "sql": "POST %s %s" % (p, json.dumps(b, default=str))}
                       for p, b, _ in calls],
               "output": "; ".join("%s -> %s" % (p, (d.get("result") if isinstance(d, dict) else d)) for p, _, d in calls)[:800],
               "before": before, "after": after, "undone": False, "module": self.module.name if self.module else None,
               "applies": applies, "applies_text": APPLIES_TEXT.get(applies, applies), "tables": ["character_base"]}
        try:
            rec["diff"] = R.row_diff(before, after) if R and before and after else {}
            rec["output"] += " | " + (R.diff_summary(rec["diff"]) if R else "")
        except Exception:     # noqa: BLE001 - the record is informative only
            pass
        if inverse:
            rec["inverse"] = inverse
        else:
            rec["no_undo"] = "applied by the running server; undo it with the opposite edit (or Undo after the player logs out)"
        self.app.record_reborn_change(rec)
        return rec

    def set_character_fields(self, cid, values, expected=None, advanced=False, label=None):
        """Set character_base columns of one character, ONLINE OR OFFLINE:
          * offline (no game session)             -> one guarded database write (char_write), applies at next login;
          * online and every column has a round2  -> the running server applies it (POST /admin/money, /progression,
            operation advertised (LIVE_FIELDS)       /teleport), the player sees it at once; recorded with Undo
                                                     (the inverse live call);
          * online otherwise                      -> 423 OnlineRefused naming the columns that need a logout.
        values   {column: new value} (character_base only); expected {column: value the page showed} (409 if changed).
        Returns {"mode": "live"|"database", "change", "applies", "applies_text", "result"}."""
        rb = self.need_rb()
        cid = int(cid)
        if not values:
            raise Bad("nothing to change")
        clean = {}
        for c, v in values.items():
            d = GLOSSARY.field("character_base", c)
            if not d.get("type"):
                raise Bad("character_base has no column %r" % c)
            if v is not None and (d.get("min") is not None or d.get("max") is not None) and not isinstance(v, str):
                lo, hi = d.get("min"), d.get("max")
                if (lo is not None and v < lo) or (hi is not None and v > hi):
                    raise Bad("%s must be %s..%s (got %s)" % (d.get("label") or c, lo, hi, v))
            clean[c] = v
        check_fields(["character_base.%s" % c for c in clean], advanced)
        info = online_info(self.app, cid)
        exp = [{"table": "character_base", "where": {"id": cid}, "values": dict(expected)}] if expected else None
        what = label or ", ".join("%s %s" % (GLOSSARY.field("character_base", c).get("label") or c, v) for c, v in clean.items())
        if info["online"]:
            caps = capabilities(self.app)
            calls, missing = live_plan(caps, clean)
            if missing or not calls:
                why = (GLOSSARY.rules("character_base").get("online") or {}).get("why") or ""
                if caps.get("available"):
                    can = live_text([c for cs in calls.values() for c in cs])
                    msg = ("%s is online. The game server cannot change %s live%s; log the character out to change %s." % (
                        info["name"], live_text(missing), (" (it can change %s; save those separately)" % can) if can else "",
                        "it" if len(missing) == 1 else "them"))
                else:
                    msg = ("%s is online. Live edits arrive with the round-2 server update; this character must be logged "
                           "out. (%s)" % (info["name"], why or "; ".join(info["reasons"])))
                raise OnlineRefused(msg, dict(info, why=[why] if why else [], missing=missing))
            return self._live_fields(cid, info, clean, calls, exp, what)
        r = self.char_write(cid, "set " + what, lambda d: [update_sql("character_base", {"id": cid}, clean)],
                            fields=["character_base.%s" % c for c in clean], expected=exp, advanced=advanced)
        r["mode"] = "database"
        return r

    def _live_fields(self, cid, info, clean, calls, exp, what):
        rb = self.rb
        before = rb.dump(cid)
        base = (before.get("character_base") or [{}])[0]
        if exp:
            # round2 has no compare-and-set: compare with the database (wallet / level are database-authoritative)
            bad = ["%s: %s -> %s" % (c, v, base.get(c)) for c, v in exp[0]["values"].items()
                   if str(base.get(c)) != str(v) and not (isinstance(v, (int, float)) and base.get(c) is not None
                                                          and float(base.get(c)) == float(v))]
            if bad:
                raise Conflict("character_base [%d] changed since you opened it: %s" % (cid, "; ".join(bad)),
                               [{"table": "character_base", "row": "[%d]" % cid, "what": b} for b in bad])
        done = []
        for path, cols in calls.items():
            body = {LIVE_FIELDS[c][1]: clean[c] for c in cols}
            if path.endswith("/teleport") or any(c in MOVE_COLS for c in cols):
                for c in MOVE_COLS:                                   # teleport needs map, x and z
                    body.setdefault(LIVE_FIELDS[c][1], clean.get(c, base.get(c)))
                body = {k: (float(v) if k in ("x", "z") else int(v)) for k, v in body.items()}
            if "level" in body and "experience" not in body:
                body["experience"] = 0        # like /level set; said in the result
            st, doc = self._live_post(path, info["name"], body)
            err = doc.get("error") if isinstance(doc, dict) else str(doc)[:200]
            if st == 409 and err == "offline" and not done:
                if online_info(self.app, cid)["online"]:
                    # no world session, but the database still says online: character select or entering the world
                    raise Conflict("%s is at the character-select screen or still entering the world: the game server cannot "
                                   "edit it live yet and the database must not be written. Try again in a few seconds. "
                                   "Nothing was changed." % info["name"])
                # logged out between the check and the call: the database path (it re-checks the online signals)
                r = self.char_write(cid, "set " + what, lambda d: [update_sql("character_base", {"id": cid}, clean)],
                                    fields=["character_base.%s" % c for c in clean], expected=exp)
                r["mode"] = "database"
                return r
            if st == 409 and err == "busy":
                raise Conflict("%s is changing maps or logging in; try again in a moment. %s" % (
                    info["name"], ("Already applied live: " + "; ".join(p for p, _, _ in done)) if done else "Nothing was changed."))
            if st != 200:
                if done:
                    self._live_record(cid, info["name"], "set (part) " + what, done, before, rb.dump(cid))
                raise Bad("the game server refused %s (HTTP %s): %s%s" % (path, st, err,
                          (" - already applied: " + ", ".join(p for p, _, _ in done)) if done else ""))
            done.append((path, body, doc))
        after = rb.dump(cid)
        a = (after.get("character_base") or [{}])[0]
        inverse = {"cid": cid, "values": {c: base.get(c) for c in clean}, "expected": {c: a.get(c) for c in clean}}
        rec = self._live_record(cid, info["name"], "set " + what + " (live)", done, before, after, inverse=inverse)
        return {"mode": "live", "change": public_change(rec), "applies": "live", "applies_text": APPLIES_TEXT["live"],
                "result": [d for _, _, d in done], "now": {c: a.get(c) for c in clean}}

    def bag_item(self, cid, op, p):
        """Kit-bag item edits ONLINE OR OFFLINE (round2's item API covers the kit bag only):
          op "add"    p = {item, quantity, quality, grade, bound}   (offline: first free bag slot(s), stacks split by Overlap)
          op "remove" p = {slot}                                    (bag slot 0..95)
          op "move"   p = {from, to}                                (bag slots; the target must be empty)
        Online + advertised -> the running server does it and refreshes the client's bag; offline -> reborn.py's guarded
        item write. Anything else (equipment, warehouse, swaps, attributes) is an offline edit: use char_write."""
        rb = self.need_rb()
        cid = int(cid)
        info = online_info(self.app, cid)
        if op == "add":
            pid = to_int(p.get("item"), 1, 2 ** 31 - 1, "item")
            qty = to_int(p.get("quantity", 1), 1, 9999, "quantity")
            meta = rb.item_meta(pid)
            if not meta["server"]:
                raise Bad("item %d is not an item template of this server" % pid)
            dflt = meta["defaults"]
            q = to_int(p.get("quality", dflt["item_quality"]), 0, 20, "quality")
            g = to_int(p.get("grade", dflt["item_grade"]), 0, 25, "grade")
            bound = to_int(p.get("bound", dflt["bound"]), 0, 1, "bound")
            live_op, live_body = "character.give_item", {"itemId": pid, "quantity": qty, "quality": q, "grade": g, "bound": bound}
            what = "add %s (#%d) x%d Q%d G%d%s to the bag" % (meta["name"], pid, qty, q, g, " bound" if bound else "")
        elif op == "remove":
            slot = to_int(p.get("slot"), 0, BAG_SLOTS - 1, "bag slot")
            live_op, live_body = "character.item_delete", {"slot": slot}
            what = "remove the item in bag slot %d" % slot
        elif op == "move":
            fs, ts = to_int(p.get("from"), 0, BAG_SLOTS - 1, "from slot"), to_int(p.get("to"), 0, BAG_SLOTS - 1, "to slot")
            live_op, live_body = "character.item_move", {"fromSlot": fs, "toSlot": ts}
            what = "move bag slot %d -> %d" % (fs, ts)
        else:
            raise Bad("bag op must be add, remove or move")
        check_fields(["character_items.*"])
        if info["online"]:
            caps = capabilities(self.app)
            o = (caps.get("operations") or {}).get(live_op)
            if not o:
                raise OnlineRefused("%s is online. %s" % (info["name"], "The game server does not offer this edit live; log the "
                                    "character out." if caps.get("available") else "Live edits arrive with the round-2 server "
                                    "update; this character must be logged out."), dict(info, missing=[live_op]))
            before = rb.dump(cid)
            st, doc = self._live_post(o["path"], info["name"], live_body)
            err = doc.get("error") if isinstance(doc, dict) else str(doc)[:200]
            if st == 200:
                rec = self._live_record(cid, info["name"], what + " (live)", [(o["path"], live_body, doc)], before, rb.dump(cid),
                                        inverse=None)
                return {"mode": "live", "change": public_change(rec), "applies": "live", "applies_text": APPLIES_TEXT["live"],
                        "result": doc}
            if st == 409 and err == "busy":
                raise Conflict("%s is changing maps or logging in; try again in a moment. Nothing was changed." % info["name"])
            if not (st == 409 and err == "offline"):
                raise Bad("the game server refused it (HTTP %s): %s" % (st, err))
            if online_info(self.app, cid)["online"]:
                raise Conflict("%s is at the character-select screen or still entering the world: the game server cannot "
                               "edit it live yet and the database must not be written. Try again in a few seconds. Nothing "
                               "was changed." % info["name"])
        # offline: reborn.py's own item operations (slot / kind / stack / audit / inventory_revision rules)
        if op == "add":
            def build(d):
                used = {r["slot_index"] for r in d["character_items"] if r["item_location"] == R.LOC_BAG}
                free = [s for s in range(BAG_SLOTS) if s not in used]
                ov = max(1, int(meta.get("overlap") or 1))
                stacks = [min(ov, qty - i) for i in range(0, qty, ov)]
                if len(stacks) > len(free):
                    raise RebornError("the bag has %d free slots; %d needed" % (len(free), len(stacks)))
                out = []
                for slot, n in zip(free, stacks):
                    b = R.op_item(rb, cid, {"location": R.LOC_BAG, "slot": slot, "item": {
                        "prop_id": pid, "stack": n, "item_quality": q, "item_grade": g, "bound": bound}})
                    out += [x for x in b(d) if "inventory_revision" not in x]
                    d["character_items"].append({"item_location": R.LOC_BAG, "slot_index": slot, "id": -1, "prop_id": pid})
                return out + ["UPDATE character_base SET inventory_revision = inventory_revision + 1 WHERE id = %d" % cid]
        elif op == "remove":
            build = R.op_item(rb, cid, {"location": R.LOC_BAG, "slot": live_body["slot"], "item": None})
        else:
            fs, ts = live_body["fromSlot"], live_body["toSlot"]
            mv = R.op_move(rb, cid, {"from": {"location": R.LOC_BAG, "slot": fs}, "to": {"location": R.LOC_BAG, "slot": ts}})

            def build(d):
                if R.find_item(d, R.LOC_BAG, ts):
                    raise RebornError("bag slot %d is not empty" % ts)
                return mv(d)
        r = self.char_write(cid, what, build, fields=["character_items.*"], cross_bumps=False)
        r["mode"] = "database"
        return r

    def live_call(self, op, character, body, *, cid=None, no_undo=None):
        """Run a round2 live operation on an ONLINE character. Returns {"ok": True, "result": ...} or
        {"ok": False, "offline": True} (write the database instead) or raises Bad / Conflict ("busy")."""
        caps = capabilities(self.app)
        o = (caps.get("operations") or {}).get(op)
        if not o:
            raise Bad("the game server does not offer %s (capabilities: %s)" % (op, ", ".join(sorted(caps.get("operations") or {})) or "none"))
        payload = dict(body or {})
        payload.setdefault("actor", "gwadmin-web")
        payload["character"] = character
        st, doc = _http(o.get("method", "POST"), caps["port"], o["path"], payload, timeout=8.0)
        err = doc.get("error") if isinstance(doc, dict) else None
        if st == 409 and err == "offline":
            return {"ok": False, "offline": True}
        if st == 409 and err == "busy":
            raise Conflict("the character is logging in or changing maps; try again in a moment (nothing written)")
        if st == 409 and err in ("buff_changed", "buff_not_removable"):
            raise Conflict("The buff changed or needs its own game-system control. Refresh the active effects before editing.")
        if st >= 400:
            raise Bad("the game server refused %s (HTTP %s): %s" % (op, st, err or doc))
        rec = {"engine": "reborn-live", "id": "lv-" + datetime.datetime.now().strftime("%Y%m%d-%H%M%S-%f"), "time": now_iso(),
               "database": self.rb.db if self.rb else None, "character": character, "cid": cid, "action": "%s %s" % (op, json.dumps(body)[:200]),
               "sql": [], "output": json.dumps(doc)[:500] if not isinstance(doc, str) else doc[:500], "undone": False,
               "no_undo": no_undo or "applied by the running server; undo it with the opposite edit", "module": self.module.name if self.module else None,
               "applies": "live", "applies_text": APPLIES_TEXT["live"]}
        self.app.record_reborn_change(rec)
        return {"ok": True, "result": doc, "change": rec}


def undo_live(app, c):
    """Undo of a live change (engine reborn-live): the inverse edit, live if the character is still online, else the
    database path. Called by server.py's /api/undo."""
    inv = c.get("inverse")
    if not inv:
        raise Bad("change %s cannot be undone here: %s" % (c["id"], c.get("no_undo") or "no inverse"))
    if c.get("undone"):
        raise Bad("change %s is already undone" % c["id"])
    ctx = Ctx(app, "POST", "undo", {}, {}, REGISTRY.get(c.get("module")))
    r = ctx.set_character_fields(inv["cid"], inv["values"], expected=inv.get("expected"), label="undo of %s" % c["id"])
    rid = (r.get("change") or {}).get("id")
    for x in app.changes:
        if x.get("id") == rid:
            x["undo_of"] = c["id"]
            x.pop("inverse", None)
            x["no_undo"] = "this is an undo; to put it back, edit the value again"
            app._save_change(x) if hasattr(app, "_save_change") else None
            r["change"] = public_change(x)
    c["undone"] = True
    if hasattr(app, "_save_change"):
        app._save_change(c)
    return r


# =========================================================================== core routes + dispatch
def _core(app, method, path, ctx, load_errors):
    parts = path.split("/") if path else [""]
    head = parts[0]
    if method == "GET" and head in ("", "modules"):
        mods = sorted(REGISTRY.values(), key=lambda m: (m.order, m.name))
        return {"modules": [m.describe() for m in mods], "load_errors": load_errors}
    if method == "GET" and head == "capabilities":
        return capabilities(app, force=ctx.q("refresh") == "1")
    if method == "GET" and head == "status":
        rb = app.reborn
        st = rb.status() if rb else {"available": False}
        caps = capabilities(app)
        return {"reborn": st, "server": server_status(app), "capabilities": {k: caps.get(k) for k in ("available", "port", "error", "version")},
                "live_operations": sorted((caps.get("operations") or {}).keys()), "editor": {"started": app.started, "port": app.a.port,
                "allow_force": bool(app.a.allow_force), "backup_dir": app.a.backup_dir, "mgmt_port": mgmt_port(app)},
                "dump_tables": [t for t, _, _ in R.DUMP_TABLES] if R else []}
    if method == "GET" and head == "catalog":
        if len(parts) == 1:
            return {"types": CATALOG.types(), "sprites": {"file": "/static/catalog-icons.png", "tile": 36, "columns": 64}}
        if len(parts) == 2 and parts[1] == "labels":
            return CATALOG.file("labels")          # stat / slot / currency captions for the tooltips
        if len(parts) == 2:
            return CATALOG.index(parts[1])
        if len(parts) == 3:
            return CATALOG.entry(parts[1], parts[2])
    if method == "GET" and head == "glossary":
        if len(parts) == 1:
            if ctx.q("fields"):
                return GLOSSARY.fields([f for f in ctx.q("fields").split(",") if f])
            return GLOSSARY.meta()
        if len(parts) == 2:
            return GLOSSARY.table(parts[1])
        if len(parts) == 3:
            return GLOSSARY.field(parts[1], parts[2])
    if method == "GET" and head == "rules" and len(parts) == 2:
        return GLOSSARY.rules(parts[1]) or {}
    if method == "GET" and head == "characters":
        return ctx.need_rb().characters(ctx.q("q"))
    if method == "GET" and head == "online" and len(parts) == 2 and parts[1].isdigit():
        ctx.need_rb()
        return online_info(app, int(parts[1]))
    if method == "POST" and head == "edit" and len(parts) == 3 and parts[1].isdigit():
        # the shared save path (GW.saveFields / GW.bagItem): live for an online character when round2 offers it
        cid = int(parts[1])
        if parts[2] == "fields":
            vals = ctx.body.get("values") or {}
            if not isinstance(vals, dict):
                raise Bad("values must be an object {column: value}")
            return ctx.set_character_fields(cid, vals, expected=ctx.body.get("expected") or None,
                                            advanced=bool(ctx.body.get("advanced")), label=ctx.body.get("label"))
        if parts[2] == "bag":
            return ctx.bag_item(cid, ctx.body.get("op"), ctx.body)
    if method == "POST" and head == "kick" and len(parts) == 2 and parts[1].isdigit():
        cid = int(parts[1])
        info = online_info(app, cid)
        caps = capabilities(app)
        o = (caps.get("operations") or {}).get("character.kick")
        if not o:
            raise Bad("the game server does not offer kick (round-2 update); use the in-game /kick or wait for the logout")
        if not info["online"]:
            return {"ok": True, "result": "%s is already offline" % info["name"]}
        st, doc = ctx._live_post(o["path"], info["name"], {"reason": str(ctx.body.get("reason") or "an admin edit")[:100]})
        if st != 200:
            raise Bad("kick refused (HTTP %s): %s" % (st, doc.get("error") if isinstance(doc, dict) else doc))
        for _ in range(30):                     # wait for the logout save (WRITE-RULES s.7 step 2.4)
            time.sleep(0.5)
            if not online_info(app, cid)["online"]:
                return {"ok": True, "result": "%s was disconnected and is offline now" % info["name"]}
        return {"ok": True, "result": "%s was disconnected; the logout save is still running" % info["name"], "pending": True}
    if method == "GET" and head == "pending":
        # changes that need a gw251 restart and are not undone (WRITE-RULES s.7 step 6)
        return {"pending": [public_change(c) for c in app.changes if c.get("applies") in ("after_restart", "startup")
                            and not c.get("undone") and not c.get("undo_of")]}
    if method == "GET" and head == "changes":
        mod = ctx.q("module")
        out = [public_change(c) for c in app.changes[-300:] if not mod or c.get("module") == mod]
        return out[::-1]
    return None


def guarded(fn):
    """(status, json) of fn() with the module error mapping: Bad 400, Refused 403, NotFound 404, Conflict 409,
    OnlineRefused 423, reborn errors 400/409."""
    try:
        return 200, fn()
    except Bad as e:
        return 400, {"error": str(e)}
    except NotFound as e:
        return 404, {"error": str(e)}
    except Refused as e:
        return 403, {"error": str(e), "refused": True}
    except OnlineRefused as e:
        return 423, {"error": str(e), "online": e.info}
    except Conflict as e:
        return 409, {"error": str(e), "conflict": e.conflicts or [{"table": "*", "row": "*", "what": str(e)}]}
    except RebornConflict as e:
        return 409, {"error": str(e), "conflict": getattr(e, "conflicts", [])}
    except RebornError as e:
        return 400, {"error": str(e)}


def dispatch(app, method, path, query, body, load_errors=None):
    path = path.strip("/")
    CATALOG.rb = app.reborn
    ctx = Ctx(app, method, path, query, body)

    def run():
        head, _, rest = path.partition("/")
        if head in REGISTRY:
            m = REGISTRY[head]
            ctx.module = m
            for meth, rx, fn in m.routes:
                if meth != method:
                    continue
                mm = rx.match(rest)
                if mm:
                    return fn(ctx, *mm.groups())
            raise NotFound("module %s has no %s route %r" % (head, method, rest))
        r = _core(app, method, path, ctx, load_errors or {})
        if r is None:
            raise NotFound("no route %s /api/m/%s" % (method, path))
        return r
    return guarded(run)
