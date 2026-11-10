#!/usr/bin/env python3
"""Build schema/glossary.json from the live schema snapshot plus the hand-written glossary.

Inputs (all in this folder):
  live-schema-meta.json   columns / constraints / indexes / triggers / comments / views / exact row
                          counts of the LIVE reborn database "godswar", produced by snapshot.sql
                          (one BEGIN READ ONLY transaction; see SCHEMA-MAP.md "Refreshing").
  glossary_tables.py      one entry per table (system, edit class, when the server reads it).
  glossary_columns.py     column descriptions.
Optional: --source DIR    reborn source tree; adds "used_by" (C# files that query each table).

Python 3 standard library only. Writes glossary.json and SCHEMA-MAP.md, prints a coverage report.
Usage:  python3 -I build_glossary.py [--source PATH] [--check]
"""
import json
import os
import re
import sys

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import glossary_tables as GT      # noqa: E402
import glossary_columns as GC     # noqa: E402
import schema_map_md              # noqa: E402

TYPE_RANGE = {
    "smallint": (-32768, 32767),
    "integer": (-2147483648, 2147483647),
    "bigint": (-9223372036854775808, 9223372036854775807),
}
ARRAY_NAMES = {"_int2": "smallint[]", "_int4": "integer[]", "_int8": "bigint[]", "_numeric": "numeric[]",
               "_uuid": "uuid[]", "_text": "text[]"}

FK_REF = {
    "accounts": "account", "character_base": "character", "item_templates": "item",
    "skill_templates": "skill", "talent_templates": "talent", "server": "realm", "character_pets": "pet",
    "guilds": "guild", "guild_duties": "guild_duty", "map_templates": "map", "character_items": "character_item",
    "command_inbox": "command", "command_audit": "command", "pet_templates": "pet_species",
    "pet_content_species_definitions": "pet_species",
}

REF_TYPES = {
    "account": "Account (accounts.id). Name: accounts.username.",
    "character": "Character (character_base.id). Name: character_base.name.",
    "realm": "Realm / server-list entry (server.id). Name: server.name.",
    "item": "Item template id. Names/icons: item_template_content_definitions (published) or the Origin client "
            "ItemBaseAttribute.xml + Text/EquipName.dat; catalog-origin/items.json.",
    "character_item": "One owned item row (character_items.id).",
    "item_attribute": "Append attribute id. item_attribute_content_definitions + client ItemAppendAttribute.xml / "
                      "Message.dat captions; catalog-origin/attributes.json.",
    "attribute_stat_type": "Attribute stat type 0-31 (0 attack, 1 defence, 2 magic attack, 3 magic defence, 4 hit, "
                           "5 dodge, 13 max HP, 14 max MP ...). Names: catalog-origin/attributes.json.",
    "item_quality": "Item quality 0-20; names in item_quality_levels (1 Common ... 20 Boundless).",
    "item_grade": "Item grade 0-25 (stars); item_grade_levels.",
    "equipment_slot": "Equipment slot 0-20 (see values on item_templates.equipment_slot).",
    "skill": "Player skill id (skill_templates / gameplay_skill_combat_definitions; client Magic.ini + SkillInfo.dat).",
    "pet_skill": "Pet skill id (pet_skill_curve_steps.runtime_skill_id; client Pet_Skill.xml).",
    "zodiac_skill": "Zodiac grid skill id (client zodiac files; no server table).",
    "talent": "Talent id (talent_templates / gameplay_talent_definitions).",
    "talent_effect": "Talent effect type (talent_effect_templates).",
    "class": "Class 0 Warrior, 1 Champion, 2 Priest, 3 Mage (class_templates).",
    "camp": "Camp 0 Sparta, 1 Athens.",
    "map": "Map id (gameplay_map_definitions / map_templates; English names from the client map list).",
    "map_scene": "Client scene folder name (map_templates.scene_key).",
    "monster_template": "Monster template key (gameplay_monster_templates.template_key).",
    "npc": "NPC key (npc_spawn_definitions.npc_key; names npc_dialogue_texts).",
    "npc_template": "NPC appearance template (npc_appearance_templates.template_key).",
    "quest": "Quest id (client Text/Quest/*.dat titles; no server table of quests).",
    "pet": "Pet (character_pets.id).",
    "pet_species": "Pet species 1-45 (pet_content_species_definitions; client Pet.xml + Message_Pet.dat).",
    "pet_aptitude": "Pet aptitude 1-16 (pet_content_aptitude_definitions).",
    "pet_merge_effect": "Pet Unite effect code (pet_owner_merge_effect_types).",
    "title": "Client title id (Medusa 5010+, Atlantis, Wonderland; client title files).",
    "medusa_title": "Medusa title 1-6 (medusa_reward_title_definitions).",
    "guild": "Guild (guilds.id).",
    "guild_duty": "Guild duty 0-6 (guild_duties).",
    "guild_building": "Guild building type (guild_building_types).",
    "holy_suit_tier": "Holy Suit tier (holy_suit_tier_content_definitions.suit_type).",
    "holy_spirit_effect": "Holy Stone (Holy Spirit) socket effect id (meaning per the Holy Stone docs; no table).",
    "status": "Character status/buff id (client status files; no server table).",
    "gm_waypoint": "GM waypoint (gm_waypoints.id).",
    "gm_dialogue": "GM NPC dialogue (gm_npc_dialogues.dialogue_key).",
    "command": "Server command record (internal).",
}

EDIT_CLASSES = {
    "free": "Safe to edit (for character rows: while that character is offline). Takes effect as 'applies' says.",
    "derived": "Written or recomputed by the server, or a copy the server does not read: editing is pointless or harmful.",
    "revision": "A version counter: never type a value; a write that changes the row bumps it by one.",
    "sealed": "Read-only: published content checked by hash at start-up, or evidence (ledgers, claims, audit) that "
              "triggers or exactly-once rules protect. The UI says why (read_only_reason).",
    "internal": "Server plumbing (leases, tokens, hashes, packets, archives). Never shown.",
}
APPLIES = {
    "startup": "Read when the server starts (or on first use) and kept until restart: edit, then gw251 restart.",
    "login": "Loaded when the character enters the world and kept in memory while online: edit only while offline.",
    "per_use": "Read from the database each time the feature runs: a change counts from the next use.",
    "hot_reload": "Polled by the patched server every 5 s or reloaded with POST /rates/reload: live.",
    "never": "The running server does not read it (staging copy, reference copy, evidence, archive or tooling).",
    "unknown": "Not established.",
}

NAME_REF = [
    (re.compile(r"(^|_)prop_id$|(^|_)item_id$|_item_id$"), "item"),
    (re.compile(r"character_id|^user_id$|owner_character_id"), "character"),
    (re.compile(r"account_id"), "account"),
    (re.compile(r"(^|_)map_id$"), "map"),
    (re.compile(r"quest_id"), "quest"),
    (re.compile(r"species_id"), "pet_species"),
    (re.compile(r"(^|_)title_id$"), "title"),
    (re.compile(r"^guild_id$"), "guild"),
    (re.compile(r"(^|_)pet_id$"), "pet"),
    (re.compile(r"^realm_id$|^server_id$"), "realm"),
]


def humanize(col):
    s = re.sub(r"([a-z])([A-Z])", r"\1 \2", col).replace("_", " ").strip()
    return s[:1].upper() + s[1:]


def type_name(c):
    t = c["type"]
    if t in ARRAY_NAMES:
        return ARRAY_NAMES[t]
    if t == "character varying":
        return "varchar(%s)" % c["len"] if c.get("len") else "varchar"
    if t == "numeric" and c.get("prec"):
        return "numeric(%s,%s)" % (c["prec"], c.get("scale") or 0)
    if t == "timestamp with time zone":
        return "timestamptz"
    return t


NUM = r"\(?'?(-?\d+(?:\.\d+)?)'?(?:::\w+)?\)?(?:::\w+)?"


def parse_bounds(col, defs, is_int):
    """min/max from simple single-column CHECKs, e.g. ((x >= 0) AND (x <= 20))."""
    lo = hi = None
    q = r'(?:"%s"|%s)' % (re.escape(col), re.escape(col))
    for d in defs:
        for m in re.finditer(q + r"\)?\s*(>=|>|<=|<)\s*" + NUM, d):
            op, v = m.group(1), float(m.group(2))
            if op == ">=":
                lo = v if lo is None else max(lo, v)
            elif op == ">":
                v2 = v + 1 if is_int else v
                lo = v2 if lo is None else max(lo, v2)
            elif op == "<=":
                hi = v if hi is None else min(hi, v)
            elif op == "<":
                v2 = v - 1 if is_int else v
                hi = v2 if hi is None else min(hi, v2)
    enum = None
    m = re.search(q + r"\)?(?:::text)?\s*=\s*ANY\s*\(\(?ARRAY\[(.*?)\]", " ".join(defs))
    if m:
        enum = [x.strip().split("::")[0].strip("'") for x in m.group(1).split(",")]
    conv = (lambda v: int(v)) if is_int else (lambda v: v)
    return (conv(lo) if lo is not None else None, conv(hi) if hi is not None else None, enum)


def scan_source(src):
    files = {}
    for root, dirs, fs in os.walk(os.path.join(src, "src")):
        dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
        for f in fs:
            if f.endswith(".cs"):
                p = os.path.join(root, f)
                with open(p, encoding="utf-8", errors="replace") as fh:
                    files[os.path.relpath(p, src)] = fh.read()
    return files


def main():
    args = sys.argv[1:]
    src = None
    if "--source" in args:
        src = args[args.index("--source") + 1]
    meta = json.load(open(os.path.join(HERE, "live-schema-meta.json"), encoding="utf-8"))
    rows = meta.get("rows", {})

    cols_by = {}
    for c in meta["columns"]:
        cols_by.setdefault((c["s"], c["t"]), []).append(c)
    cons_by = {}
    for c in meta["constraints"]:
        cons_by.setdefault((c["s"], c["t"]), []).append(c)
    trig_by = {}
    for t in meta["triggers"]:
        trig_by.setdefault((t["s"], t["t"]), []).append(t)
    comment = {}
    for c in meta["comments"]:
        comment[(c["t"], c["col"])] = c["d"]

    srcfiles = scan_source(src) if src and os.path.isdir(src) else None

    out_tables = {}
    report = {"tables": 0, "columns": 0, "desc_source": {}, "missing_table_entry": [], "unused_entries": []}
    seen = set()
    for (schema, table), cols in sorted(cols_by.items(), key=lambda x: (x[0][0] != "public", x[0][1])):
        key = table if schema == "public" else "%s.%s" % (schema, table)
        tmeta = GT.TABLES.get(table)
        if tmeta is None:
            report["missing_table_entry"].append(key)
            tmeta = {"system": "unknown", "edit_class": "internal", "applies": "unknown",
                     "description": "unknown"}
        seen.add(table)
        cons = cons_by.get((schema, table), [])
        tclass = tmeta["edit_class"]
        hand = GC.COLS.get(table, {})
        alias = GC.ALIAS.get(table)
        hand_alias = GC.COLS.get(alias, {}) if alias else {}
        pk = next((c["cols"] for c in cons if c["type"] == "p"), [])
        uniques = [c["cols"] for c in cons if c["type"] == "u"]
        fks = [c for c in cons if c["type"] == "f"]
        checks = [c for c in cons if c["type"] == "c"]
        col_out = {}
        for c in cols:
            name = c["c"]
            entry = None
            src_kind = None
            for cand, kind in ((hand.get(name), "hand"), (hand_alias.get(name), "hand"),
                               (comment.get((table, name)), "db_comment"), (GC.GENERIC.get(name), "generic")):
                if cand:
                    entry, src_kind = cand, kind
                    break
            if isinstance(entry, tuple):
                desc, ov = entry
            elif isinstance(entry, str):
                desc, ov = entry, {}
            else:
                desc, ov = None, {}
            # a generic/alias override never loosens a table class (see below)
            if desc is None and name.startswith("old_") and table.endswith("_archive"):
                desc, src_kind = "Value of %s before the migration replaced it." % humanize(name[4:]).lower(), "generic"
            if desc is None:
                desc, src_kind = humanize(name) + ".", "name"
            typ = type_name(c)
            is_int = c["type"] in TYPE_RANGE
            single = [k["def"] for k in checks if k["cols"] == [name]]
            multi = [k["def"] for k in checks if k["cols"] and name in k["cols"] and len(k["cols"]) > 1]
            lo, hi, enum = parse_bounds(name, single, is_int)
            rng_src = "check" if (lo is not None or hi is not None) else None
            if "min" in ov:
                lo, rng_src = ov["min"], "glossary"
            if "max" in ov:
                hi, rng_src = ov["max"], rng_src or "glossary"
            if lo is None and hi is None and is_int:
                lo, hi = TYPE_RANGE[c["type"]]
                rng_src = "type"
            fk = next((f for f in fks if f["cols"] == [name] or (f["cols"] and name in f["cols"])), None)
            ref = ov.get("ref")
            if not ref and fk:
                ref = FK_REF.get(fk["ref"].split(".")[-1])
            if not ref:
                for rx, r in NAME_REF:
                    if rx.search(name):
                        ref = r
                        break
            # edit class: a column may be stricter than its table, never looser
            cclass = ov.get("edit_class")
            if tclass in ("sealed", "derived", "internal"):
                cclass = "internal" if cclass == "internal" else tclass
            elif tclass == "revision":
                cclass = cclass or "derived"
            else:
                if cclass is None:
                    if name in pk and c.get("def") and "nextval" in (c.get("def") or ""):
                        cclass = "derived"
                    elif re.search(r"(^|_)revision$", name) and name not in ("balance_revision",):
                        cclass = "revision"
                    else:
                        cclass = "free"
            if c.get("gen") == "ALWAYS" or c.get("ident") == "YES":
                cclass = "derived" if cclass == "free" else cclass
            col = {
                "label": ov.get("label") or humanize(name),
                "description": desc,
                "desc_source": src_kind,
                "type": typ,
                "nullable": c["null"] == "YES",
                "min": lo, "max": hi,
                "range_source": rng_src,
                "ref": ref,
                "edit_class": cclass,
                "applies": ov.get("applies") or tmeta["applies"],
            }
            if c.get("def"):
                col["default"] = c["def"]
            if enum:
                col["allowed"] = enum
            if ov.get("values"):
                col["values"] = ov["values"]
            if ov.get("unit"):
                col["unit"] = ov["unit"]
            if ov.get("caution"):
                col["caution"] = ov["caution"]
            if single or multi:
                col["checks"] = single + multi
            if fk:
                col["fk"] = fk["def"]
            if (table, name) in comment and src_kind != "db_comment":
                col["db_comment"] = comment[(table, name)]
            if name in pk:
                col["primary_key"] = True
            col_out[name] = col
            report["columns"] += 1
            report["desc_source"][src_kind] = report["desc_source"].get(src_kind, 0) + 1
        tout = {
            "schema": schema,
            "description": tmeta["description"],
            "system": tmeta["system"],
            "edit_class": tclass,
            "applies": tmeta["applies"],
            "rows_live": rows.get("%s.%s" % (schema, table)),
            "primary_key": pk,
            "unique": uniques,
            "foreign_keys": [{"columns": f["cols"], "references": f["ref"], "def": f["def"]} for f in fks],
            "table_checks": [k["def"] for k in checks if k["cols"] and len(k["cols"]) > 1],
            "triggers": sorted({re.search(r"EXECUTE FUNCTION (\w+)", t["def"]).group(1)
                                for t in trig_by.get((schema, table), [])}),
            "columns": col_out,
        }
        for k in ("note", "read_only_reason"):
            if tmeta.get(k):
                tout[k] = tmeta[k]
        if (table, None) in comment:
            tout["db_comment"] = comment[(table, None)]
        if srcfiles is not None:
            rx = re.compile(r"(?<![A-Za-z0-9_])" + re.escape(table) + r"(?![A-Za-z0-9_])")
            hits = sorted(p for p, s in srcfiles.items() if table in s and rx.search(s))
            tout["used_by"] = [p for p in hits if "DatabaseMigrations" not in p][:12]
            tout["migrations"] = len([p for p in hits if "DatabaseMigrations" in p])
        out_tables[key] = tout
        report["tables"] += 1
    report["unused_entries"] = sorted(set(GT.TABLES) - seen)
    # who points at each table (an id must exist in a referenced table, even a staging copy)
    for k, tout in out_tables.items():
        for f in tout["foreign_keys"]:
            target = f["references"].split(".")[-1]
            if target in out_tables and target != k:
                out_tables[target].setdefault("referenced_by", []).append("%s(%s)" % (k, ", ".join(f["columns"])))
    for tout in out_tables.values():
        if "referenced_by" in tout:
            tout["referenced_by"] = sorted(set(tout["referenced_by"]))

    systems = {}
    for k, t in out_tables.items():
        systems.setdefault(t["system"], []).append(k)
    doc = {
        "_meta": {
            "database": meta.get("measured", {}).get("database", "godswar"),
            "measured": meta.get("measured", {}).get("date"),
            "table_count": len(out_tables),
            "column_count": report["columns"],
            "views": sorted(meta.get("views", [])),
            "edit_classes": EDIT_CLASSES,
            "applies": APPLIES,
            "ref_types": REF_TYPES,
            "systems": {k: sorted(v) for k, v in sorted(systems.items())},
            "desc_source": {
                "hand": "written for this glossary from the reborn source, migrations and live data",
                "db_comment": "the database's own COMMENT",
                "generic": "shared text for a column name used in many tables",
                "name": "only the column name is known; treat as unconfirmed",
            },
            "built_by": "schema/build_glossary.py",
            "shape": "Top-level keys are table names (schema-qualified only outside 'public', e.g. "
                     "'legacy.character_kitbag_archive'); '_meta' is the only other key.",
        },
    }
    doc.update(out_tables)   # shape: {"_meta": {...}, "<table>": {...}, ...}
    if "--check" not in args:
        with open(os.path.join(HERE, "glossary.json"), "w", encoding="utf-8") as fh:
            json.dump(doc, fh, ensure_ascii=False, indent=1, sort_keys=False)
            fh.write("\n")
        with open(os.path.join(HERE, "SCHEMA-MAP.md"), "w", encoding="utf-8") as fh:
            fh.write(schema_map_md.render(doc))
    print(json.dumps(report, indent=1))
    return 0 if not report["missing_table_entry"] and not report["unused_entries"] else 1


if __name__ == "__main__":
    sys.exit(main())
