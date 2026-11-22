"""modules/_login_check.py - "Login check": would this character get into the world? Wave 6 package A.

Two sources, merged:

  the game server   POST /admin/character/entry-check (W6A build): a dry run of the real login. It reads the
                    character the way a login does and builds the world-entry packets in memory. It writes nothing.
                    severity "blocks" = the login fails; "hidden" = the login works and the server leaves that item or
                    pet out of what the player sees until it is fixed.
  the database      known bad rows the editor can find by itself, so the check also works before that server build
                    is live: today a packed Seal Jade (item 10109) with no pet inside (no sealed_pet_items row).
                    Without the W6A server such a row stops the login ("blocks"); with it, it is "hidden".

Used by modules/player_health.py (the page and the Fix button) and by modules/common.py, which runs it after every
character write so a change that would stop a login is reported at once.
"""
from . import _session_health as SH

PACKED_SEAL_JADE = 10109
ENTRY_CHECK_PATH = "/admin/character/entry-check"
BATCH = 200                                    # the server accepts 500 account ids per call
CONTAINER_LOCATION = {"equipment": 0, "bag": 1}


def where_text(location, slot):
    location, slot = int(location), int(slot)
    if location == 1:
        return "Bag page %d, slot %d" % (slot // 24 + 1, slot % 24 + 1)
    return "%s slot %d" % ({0: "Equipment", 2: "Temporary bag", 3: "Warehouse"}.get(location, "Location %d" % location), slot + 1)


def server_offers_check(caps):
    return ("path:" + ENTRY_CHECK_PATH) in ((caps or {}).get("operations") or {})


ORPHAN_JADE_SQL = """(SELECT coalesce(json_agg(json_build_object('row', ci.id, 'cid', ci.user_id, 'location', ci.item_location,
      'slot', ci.slot_index, 'item', ci.prop_id) ORDER BY ci.user_id, ci.item_location, ci.slot_index), '[]')
    FROM character_items ci
    WHERE ci.prop_id = %d %%s
      AND NOT EXISTS (SELECT 1 FROM sealed_pet_items s WHERE s.item_instance_id = ci.id))""" % PACKED_SEAL_JADE


def database_findings(query, cids=None, self_healing=False):
    """{cid: [finding]} for the bad rows the editor recognises without the game server. query(sql) -> json."""
    scope = "" if cids is None else "AND ci.user_id = ANY(ARRAY[%s]::int[])" % ",".join(str(int(c)) for c in cids)
    if cids is not None and not cids:
        return {}
    out = {}
    for r in query(ORPHAN_JADE_SQL % scope) or []:
        out.setdefault(int(r["cid"]), []).append(_jade_finding(r, self_healing))
    return out


def _jade_finding(r, self_healing):
    return {
        "severity": "hidden" if self_healing else "blocks",
        "code": "seal_jade_no_pet",
        "title": "A packed Seal Jade has no pet inside it",
        "detail": ("It was added by hand, so it is an empty shell. The server hides it from the player so the login works."
                   if self_healing else
                   "It was added by hand, so it is an empty shell. The game cannot send it to the client, and the login "
                   "fails every time until it is removed."),
        "where": where_text(r["location"], r["slot"]),
        "source": "database",
        "fix": {"kind": "remove_item", "row": int(r["row"]), "location": int(r["location"]), "slot": int(r["slot"]),
                "item": int(r["item"]), "label": "Remove this Seal Jade (it has no pet inside)"},
    }


def server_results(http, port, account_ids, actor="editor"):
    """{account_id: result} from the game server's dry run, or None when it cannot be asked. http = common._http."""
    if not port or not account_ids:
        return None
    out = {}
    ids = sorted({int(a) for a in account_ids})
    for i in range(0, len(ids), BATCH):
        try:
            st, doc = http("POST", port, ENTRY_CHECK_PATH, {"actor": actor, "accountIds": ids[i:i + BATCH]}, timeout=20.0)
        except Exception:   # noqa: BLE001 - the game server is down or slow: the database half still answers
            return None
        if st != 200 or not isinstance(doc, dict) or not isinstance(doc.get("results"), list):
            return None
        for r in doc["results"]:
            if isinstance(r, dict) and "accountId" in r:
                out[int(r["accountId"])] = r
    return out


STAGE_WORDS = {"load": "reading the saved character", "enter_main": "the character sheet", "bag": "the bag",
               "bag_index": "the bag", "equipment": "the equipment", "player_detail": "the character details",
               "titles": "the titles", "pets": "the pets", "pet": "a pet", "item": "an item"}


def merge(characters, server, db, item_rows, item_names):
    """One answer per character.
    characters: [{"id", "account_id", "name"}]; server: {account_id: result} | None; db: {cid: [finding]};
    item_rows: {(cid, location, slot): row id}; item_names: {item id: name}."""
    out = []
    for c in characters:
        cid, findings, seen = int(c["id"]), [], set()
        for f in db.get(cid, []):
            findings.append(f)
            seen.add((f["fix"]["location"], f["fix"]["slot"]))
        result = (server or {}).get(int(c["account_id"]))
        for f in (result or {}).get("findings") or []:
            location = CONTAINER_LOCATION.get(f.get("container"))
            if location is not None and f.get("slot") is not None:
                key = (location, int(f["slot"]))
                if key in seen:
                    continue                      # the database half already names this row, with its Fix button
                seen.add(key)
            findings.append(_server_finding(cid, f, location, item_rows, item_names))
        blocks = any(f["severity"] == "blocks" for f in findings)
        out.append({"cid": cid, "account_id": int(c["account_id"]), "character": c["name"], "username": c.get("username"),
                    "can_enter": not blocks, "checked_by_server": result is not None, "findings": findings,
                    "light": "red" if blocks else "amber" if findings else "green"})
    out.sort(key=lambda r: ({"red": 0, "amber": 1, "green": 2}[r["light"]], (r["character"] or "").lower()))
    return out


def _server_finding(cid, f, location, item_rows, item_names):
    message = str(f.get("message") or "")
    words = SH.plain_error(message)
    stage = STAGE_WORDS.get(f.get("stage"), str(f.get("stage") or "the login"))
    finding = {"severity": "blocks" if f.get("severity") == "blocks" else "hidden", "code": str(f.get("code") or "server"),
               "title": words if words != message else "The server could not build %s" % stage,
               "detail": message if words != message else message,
               "where": "", "source": "server", "fix": None}
    if location is not None and f.get("slot") is not None:
        slot, item = int(f["slot"]), int(f.get("itemId") or 0)
        finding["where"] = where_text(location, slot)
        name = item_names.get(item) or "item %d" % item
        finding["title"] = "%s: %s" % (name, finding["title"]) if words == message else finding["title"]
        row = item_rows.get((cid, location, slot))
        if row is not None:
            finding["fix"] = {"kind": "remove_item", "row": int(row), "location": location, "slot": slot, "item": item,
                              "label": "Remove this %s" % name}
    return finding


def sentence(result):
    """What a write result says when the character now has blockers: one plain sentence per finding."""
    lines = []
    for f in result.get("findings") or []:
        lead = "This would stop %s logging in" if f["severity"] == "blocks" else "The server will hide this from %s"
        lines.append("%s: %s%s." % (lead % (result.get("character") or "the character"), f["title"],
                                    " (%s)" % f["where"] if f.get("where") else ""))
    return lines
