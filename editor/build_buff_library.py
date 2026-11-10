#!/usr/bin/env python3
"""build_buff_library.py - the editor's buff library: EVERY status effect the game data defines, with plain labels.

Reads (nothing is hand-typed):
  * the Origin client's Settings/Sys/Status.ini (en_us): all status effects, including the multi-effect rows
    ("Effect=20,25" / "Values=-30,-20") that catalog-origin/statuses.json leaves unlabelled,
  * the 2008 server's Skill/Status.ini (integration/server-gameserver): which of them the old server also knew,
  * catalog-origin/skills.json (Magic.ini): the skills that cast each status,
  * catalog-origin/items.json: the items that cast those skills (potions, scrolls, event items),
  * catalog-origin/statuses.json: icon sprites.
Writes catalog-origin/buff-library.json. Whether the RUNNING server can apply an entry is decided by the editor at
request time from the server's own catalogue (GET /admin/buffs/catalog); this file only says what exists.

    python3 build_buff_library.py [--client DIR] [--server-ini FILE]
"""
import argparse, collections, json, os, re

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
EFFECTS = {   # Status.ini effect code -> plain English (same codes as build_origin_catalog.STATUS_EFFECTS)
    0: "Interrupts casting", 1: "Returns damage (special)", 2: "Cannot move", 3: "Cannot cast spells",
    4: "Cannot use combat skills", 5: "Cannot use normal attacks", 6: "Cannot use items", 7: "Returns damage",
    8: "Casting time changed", 9: "Healing received changed", 10: "Healing done changed",
    11: "Spell damage taken changed", 12: "Spell damage dealt changed", 13: "Physical damage taken changed",
    14: "Physical damage dealt changed", 15: "EXP gain changed", 16: "Max MP changed", 17: "Max HP changed",
    18: "Hit rate changed", 19: "Dodge rate changed", 20: "Defense changed", 21: "Attack changed",
    22: "Crit hit chance", 23: "Crit resistance", 24: "Spell attack changed", 25: "Spell defense changed",
    26: "Restores HP", 27: "Loses HP over time", 28: "Restores MP", 29: "Loses MP over time",
    30: "Status-spell success rate changed", 31: "Status-spell resistance changed", 32: "Talent EXP gain changed",
    33: "Movement speed changed", 34: "Pet EXP gain changed", 35: "Peace state (cannot attack or be attacked)",
    36: "Removes debuffs", 37: "Disables percentage healing"}
# effect codes that carry a fraction (0.1 = 10%) rather than a flat number; the unit is only shown, never assumed beyond this
FRACTION = {8, 9, 10, 11, 12, 13, 14, 15, 32, 34, 36, 37}
PER_SECOND = {26, 27, 28, 29}
CATEGORY_BY_EFFECT = [({32, 15, 34}, "EXP boost"), ({26, 28, 17, 16}, "Restore / potion"), ({27, 29}, "Damage over time"),
                      ({0, 1, 2, 3, 4, 5, 6, 35, 36, 37}, "Control & cleanse")]


def read_status_ini(path):
    raw = open(path, "rb").read()
    for enc in ("utf-16", "utf-8-sig", "gb18030"):
        try:
            text = raw.decode(enc)
            break
        except UnicodeError:
            continue
    out = {}
    for block in re.split(r"\r?\n(?=\[)", text):
        m = re.match(r"\[(\d+)\]", block)
        if m:
            out[int(m.group(1))] = dict(re.findall(r"^(\w+)\s*=\s*([^\r\n]*)", block, re.M))
    return out


def nums(v):
    out = []
    for x in str(v or "").split(","):
        x = x.strip()
        try:
            out.append(float(x) if "." in x else int(x))
        except ValueError:
            pass
    return out


def effect_label(code, value):
    name = EFFECTS.get(code, "Effect #%s" % code)
    if code in (11, 13) and isinstance(value, (int, float)):       # the server's own catalogue: +0.10 = 10% LESS damage taken
        what = "Spell" if code == 11 else "Physical"
        return "%s damage taken %s by %g%%" % (what, "reduced" if value > 0 else "increased", abs(round(value * 100, 4)))
    if code in FRACTION and isinstance(value, (int, float)):
        shown = "%+g%%" % round(value * 100, 4) if code != 36 else "%g%%" % round(abs(value) * 100, 4)
        return "%s (%s)" % (name, shown)
    if code in PER_SECOND:
        return "%s (%g per second)" % (name, value)
    if code in (2, 3, 4, 5, 6, 0, 35):
        return name
    return "%s (%+g)" % (name, value) if isinstance(value, (int, float)) else name


def category(effects, row, beneficial):
    codes = {e["code"] for e in effects}
    if nums(row.get("RideId")) and nums(row.get("RideId"))[0] >= 0:
        return "Mount / movement"
    for group, label in CATEGORY_BY_EFFECT:
        if codes & group:
            return label
    if 33 in codes:
        return "Mount / movement"
    return "Buff (stat boost)" if beneficial else "Debuff (stat penalty)"


def family_name(name):
    base = re.sub(r"\s+(Lv\.?|Level|Rank)\s*\d+$", "", name).strip()
    return re.sub(r"\s+[IVX]+$", "", base) if re.search(r"\s[IVX]{1,4}$", base) else base


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--client", default=os.path.join(ROOT, "GodsWar-2.51", "Origin-Client", "Client", "Localization", "en_us"))
    ap.add_argument("--server-ini", default=os.path.join(ROOT, "integration", "server-gameserver", "Skill", "Status.ini"))
    a = ap.parse_args()
    client = read_status_ini(os.path.join(a.client, "Settings", "Sys", "Status.ini"))
    server = read_status_ini(a.server_ini) if os.path.exists(a.server_ini) else {}
    cat = os.path.join(HERE, "catalog-origin")
    skills = json.load(open(os.path.join(cat, "skills.json")))
    items = json.load(open(os.path.join(cat, "items.json")))
    sprites = {int(k): v.get("sprite") for k, v in json.load(open(os.path.join(cat, "statuses.json"))).items()}
    by_status = collections.defaultdict(list)
    for sid, s in skills.items():
        if s.get("status") is not None:
            by_status[int(s["status"])].append(int(sid))
    by_skill_items = collections.defaultdict(list)
    for iid, it in items.items():
        if it.get("use_skill") not in (None, -1, 0):
            by_skill_items[int(it["use_skill"])].append(int(iid))
    entries = []
    for sid in sorted(client):
        r = client[sid]
        codes, values = [int(c) for c in nums(r.get("Effect"))], nums(r.get("Values"))
        effects = []
        for i, c in enumerate(codes):
            v = values[i] if i < len(values) else (values[-1] if values else 0)
            if v == 0 and c in FRACTION | PER_SECOND | {16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 30, 31, 33} and len(codes) > 1:
                continue                      # an unused second slot ("Values=0.1,0")
            effects.append({"code": c, "value": v, "label": effect_label(c, v)})
        beneficial = r.get("Style") == "1"
        ride = nums(r.get("RideId"))
        dur = nums(r.get("Time"))
        interval = nums(r.get("Interval"))
        src_skills = []
        for k in by_status.get(sid, []):
            s = skills[str(k)]
            src_skills.append({"id": k, "name": s.get("name") or "", "class": s.get("class"), "level": s.get("level"),
                               "items": by_skill_items.get(k, [])[:12], "item_count": len(by_skill_items.get(k, []))})
        name = r.get("Name", "").strip() or "Status #%d" % sid
        entry = {"id": sid, "name": name, "family_name": family_name(name), "note": r.get("Note", "").strip(),
                 "kind": int(nums(r.get("Kind"))[0]) if nums(r.get("Kind")) else None,
                 "priority": int(nums(r.get("Priority"))[0]) if nums(r.get("Priority")) else None,
                 "beneficial": beneficial, "effects": effects,
                 "duration_s": dur[0] if dur else None, "interval_s": interval[0] if interval else 0,
                 "ride": int(ride[0]) if ride and ride[0] >= 0 else None, "sprite": sprites.get(sid),
                 "category": category(effects, r, beneficial), "in_2008_server": sid in server, "skills": src_skills}
        if not effects:
            entry["effects"] = [{"code": None, "value": 0, "label": "Marker only (no numeric effect in Status.ini)"}]
        entries.append(entry)
    # Statuses that only the reborn server defines (State/ExperienceBoostState.cs): ids the client's Status.ini does not have.
    src = os.path.join(ROOT, "GodsWar-2.51", "Polish", "reborn-src-next", "src", "Godswar.Server", "State", "ExperienceBoostState.cs")
    if os.path.exists(src):
        for name, sid in re.findall(r"public const int (\w+) = (\d{3,4});", open(src, encoding="utf-8").read()):
            sid = int(sid)
            if sid >= 1500 and sid not in client:
                label = re.sub(r"(?<=[a-z])(?=[A-Z])", " ", name)
                entries.append({"id": sid, "name": label, "family_name": label, "note": "Defined only in the game server (ExperienceBoostState.cs); the client's Status.ini has no row for it.",
                                "kind": None, "priority": None, "beneficial": True, "duration_s": None, "interval_s": 0, "ride": None, "sprite": None,
                                "effects": [{"code": None, "value": 0, "label": "Experience / patron effect handled by the server"}],
                                "category": "Server-defined (not in client)", "in_2008_server": False, "skills": [], "server_only": True})
    out = {"version": 1, "source": "Origin client Settings/Sys/Status.ini (en_us), Magic.ini via catalog-origin/skills.json",
           "count": len(entries), "server_only": sum(1 for e in entries if e.get("server_only")), "categories": sorted({e["category"] for e in entries}),
           "with_skill": sum(1 for e in entries if e["skills"]), "in_2008_server": sum(1 for e in entries if e["in_2008_server"]),
           "entries": entries}
    path = os.path.join(cat, "buff-library.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, separators=(",", ":"))
    print("%d statuses (%d cast by a skill, %d known to the 2008 server) -> %s" % (out["count"], out["with_skill"], out["in_2008_server"], path))
    print(collections.Counter(e["category"] for e in entries))


if __name__ == "__main__":
    main()
