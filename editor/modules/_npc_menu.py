"""B21 item 20: the whole NPC dialogue as the game draws it - greeting, function-page text and the yellow option lines.

The server only sends the page number (dialog index) and the option numbers (sub ids); the WORDS come from the
client's Lua (data/npc_menu_text.json, built on the Mac by build_npc_menu_text.py). So:
  * greeting name / text  -> server (npc_dialogue_texts), published by npc_dialogue.py, after a server restart;
  * option lines and page text -> client (UI/Base/LuaText.lua). Edits are kept as a DRAFT here and exported as a
    LuaText.lua patch for the next client update (tools/apply_npc_menu_text.py). Nothing reaches players before that.
The draft is a JSON file next to the editor's backups (like favourites), so an agent on the VPS can read it.
"""
import hashlib
import json
import os
import tempfile

from . import common
from .common import Bad, Conflict

DATA = os.path.join(common.HERE, "data", "npc_menu_text.json")
DRAFT_NAME = "npc-menu-text-draft.json"
LABEL_BYTES = 1024
MAX_LABELS = 2000
_cache = {}


def menu_data():
    try:
        st = os.stat(DATA)
    except OSError:
        return None
    if _cache.get("mtime") != st.st_mtime:
        with open(DATA, encoding="utf-8") as f:
            _cache.update(doc=json.load(f), mtime=st.st_mtime)
    return _cache["doc"]


def _uniq(items):
    seen, out = set(), []
    for x in items:
        k = (x.get("text"), tuple(x.get("keys") or ()))
        if k not in seen:
            seen.add(k)
            out.append(x)
    return out


def _el(e):
    keys = e.get("keys") or []
    return {"text": e.get("text", ""), "key": keys[0] if len(keys) == 1 and not e.get("dynamic") else None,
            "keys": keys, "dynamic": bool(e.get("dynamic")), "where": "%s:%s" % (e.get("file"), e.get("line"))}


# Buttons the game server appends after a teleporter's published menu (never part of it). Mirrors
# server/src/Godswar.Server/Domain/World/Content/TransporterProtocol.cs GetExtensionMenu (CapitalExtensionMenuValues,
# MycenaeExtensionMenuValues). The battlefield transporters' gated Parnassus button is not listed.
TRANSPORTER_EXTENSIONS = {
    "Athens_041": [11, 1089, 1088, 1091, 1090, 1092],
    "Sparta_042": [11, 1089, 1088, 1091, 1090, 1092],
    "Mycenae_All_013": [1091, 1092, 1093],
}
TRANSPORTER_BEHAVIOUR = 14


def extensions(npc_key, behavior):
    return list(TRANSPORTER_EXTENSIONS.get(npc_key, [])) if behavior == TRANSPORTER_BEHAVIOUR else []


def quests_by_npc(catalog):
    """npc_key -> quests it gives or takes back, from the client's quest list (catalog-origin/quests.json)."""
    try:
        rows = catalog.file("quests") or {}
    except Exception:
        return {}
    out = {}
    for q in (rows.values() if isinstance(rows, dict) else rows):
        for role, key in (("gives", q.get("giver")), ("completes", q.get("responder"))):
            if key and q.get("title"):
                out.setdefault(key, []).append({"id": q.get("id"), "title": q["title"], "role": role,
                                                "min_level": q.get("min_level"), "max_level": q.get("max_level")})
    for v in out.values():
        v.sort(key=lambda x: ((x["min_level"] or 0), x["id"] or 0))
    return out


def page(doc, dialog_index, subs, index=1, extra=()):
    """The first page of a function: its text and one option per sub id (menu order). index 1 = the page the
    server opens (the stock scripts put the opening menu under Index == 1)."""
    t = (doc.get("types") or {}).get(str(dialog_index))
    if not t:
        return {"resolved": False, "texts": [], "options": [{"sub": s, "label": None, "texts": []} for s in list(subs) + list(extra)],
                "note": "The client has no script for page %d, so the game shows no words for it." % dialog_index}
    pages = t["pages"].get(str(index)) or {}
    general = pages.get("*") or {}
    texts = [_el(e) for e in general.get("texts", [])]
    options = []
    for s in list(subs) + [x for x in extra if x not in subs]:
        branch = pages.get(str(s)) or pages.get("else") or {}
        btn = branch.get("buttons") or []
        texts += [_el(e) for e in branch.get("texts", [])]
        options.append({"sub": s, "label": _el(btn[0]) if btn else None, "texts": [_el(e) for e in branch.get("texts", [])],
                        "added": s in extra and s not in subs})
    return {"resolved": True, "script": t.get("file"), "function": t.get("fn"), "type": t.get("const"),
            "texts": _uniq(texts), "options": options}


def routes(ctx):
    """Published routes: npc_key -> [{profile, dialog_index, behaviour, subs}] (menu order)."""
    q = """(SELECT coalesce(json_agg(json_build_object('npc_key', b.npc_key, 'route_order', b.route_order,
      'script', b.client_script_key, 'profile', p.profile_key, 'dialog_index', p.dialog_index, 'behavior', p.behavior,
      'subs', (SELECT coalesce(json_agg(e.sub_id ORDER BY e.menu_order), '[]') FROM npc_dialogue_profile_entries e
               WHERE e.revision = p.revision AND e.profile_key = p.profile_key)) ORDER BY b.npc_key, b.route_order), '[]')
      FROM npc_dialogue_bindings b JOIN npc_dialogue_profiles p ON p.revision = b.revision AND p.profile_key = b.profile_key
      WHERE b.revision = (SELECT revision FROM npc_dialogue_publication WHERE family = 'npc-dialogues'))"""
    out = {}
    for r in ctx.need_rb().pg.json(q) or []:
        out.setdefault(r["npc_key"], []).append(r)
    return out


# ---------------------------------------------------------------- the option-text draft (client-side words)
def draft_path(ctx):
    return os.path.join(ctx.app.a.backup_dir, DRAFT_NAME)


def _version(doc):
    return hashlib.sha256(json.dumps(doc, sort_keys=True).encode()).hexdigest()[:16]


def read_draft(ctx):
    try:
        with open(draft_path(ctx), encoding="utf-8") as f:
            doc = json.load(f)
    except (OSError, ValueError):
        doc = {"labels": {}}
    doc.setdefault("labels", {})
    return doc, _version(doc)


def check_label(key, text, strings):
    if key not in strings:
        raise Bad("%s is not a text of the client's NPC scripts" % key)
    if not isinstance(text, str) or not text.strip():
        raise Bad("%s: the text cannot be empty" % key)
    if "\x00" in text or "\n" in text or "\r" in text:
        raise Bad("%s: one line only, no line breaks" % key)
    if len(text.encode("utf-8")) > LABEL_BYTES:
        raise Bad("%s: at most %d bytes" % (key, LABEL_BYTES))
    return text


def save_draft(ctx, labels, version):
    doc = menu_data()
    if not doc:
        raise Bad("data/npc_menu_text.json is missing; run build_npc_menu_text.py on the Mac")
    if not isinstance(labels, dict) or len(labels) > MAX_LABELS:
        raise Bad("labels must be an object of at most %d texts" % MAX_LABELS)
    old, now = read_draft(ctx)
    if version != now:
        raise Conflict("Someone else saved the option-text draft. Reload before saving.")
    strings = doc.get("strings") or {}
    clean = {}
    for k, v in labels.items():
        v = check_label(k, v, strings)
        if v != strings[k]["text"]:
            clean[k] = v              # same as the client = no change
    new = {"labels": dict(sorted(clean.items())), "client": doc.get("client"), "built": doc.get("generated"),
           "saved_at": common.now_iso()}
    path = draft_path(ctx)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    fd, tmp = tempfile.mkstemp(dir=os.path.dirname(path), prefix=".npc-menu-")
    with os.fdopen(fd, "w", encoding="utf-8") as f:
        json.dump(new, f, ensure_ascii=False, indent=1, sort_keys=True)
    os.replace(tmp, path)
    return new, _version(new)


def lua_quote(text):
    return '"' + text.replace("\\", "\\\\").replace('"', '\\"') + '"'


def export_lua(draft, strings):
    """The override block: appended to the end of UI/Base/LuaText.lua, later assignments win in Lua."""
    lines = ["", "-- gwadmin-web NPC option text (%d changed; built %s)" % (len(draft["labels"]), draft.get("saved_at", ""))]
    for k, v in sorted(draft["labels"].items()):
        was = strings.get(k, {})
        lines.append("%s = %s  -- was %s:%s" % (k, lua_quote(v), was.get("file", "?"), was.get("line", "?")))
    return "\r\n".join(lines) + "\r\n"
