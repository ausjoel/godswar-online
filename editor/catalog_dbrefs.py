#!/usr/bin/env python3
"""catalog_dbrefs.py - read, in ONE read-only transaction, every ID the reborn database uses, so
build_origin_catalog.py can measure how many of them the Origin client catalogue names in English.

    # on the Mac, through ssh (the database is only reachable on the VPS):
    python3 -I catalog_dbrefs.py --psql "ssh vps sudo -n -u postgres psql" --db godswar
    # on the VPS:
    python3 -I catalog_dbrefs.py --db godswar

Writes catalog-origin/db-refs.json:
  {"database", "taken_at", "refs": {ref_name: {"type", "table", "column", "values": [[value, rows], ...]}},
   "item_templates": [{id, kind, name_key, display_name, equipment_slot, min_level, class_ids, texture, icon}]}

Safety: the SQL starts with BEGIN READ ONLY, so PostgreSQL itself refuses any write; nothing else is sent.
A table or column that does not exist in this database (an older or newer reborn schema) is skipped and
listed under "skipped" instead of failing the run.  Stdlib only.
"""
import argparse
import datetime
import json
import os
import re
import shlex
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))

# (ref name, id type, table, value expression, extra WHERE)
# The id type names the catalogue that must resolve the value (see build_origin_catalog.RESOLVERS).
REFS = [
    ("item_templates.id", "item", "item_templates", "id", ""),
    ("character_items.prop_id", "item", "character_items", "prop_id", ""),
    ("character_inventory_baseline_items.prop_id", "item", "character_inventory_baseline_items", "prop_id", ""),
    ("monster_loot_rules.item_id", "item", "monster_loot_rules", "item_id", ""),
    ("medusa_monster_loot_rules.item_id", "item", "medusa_monster_loot_rules", "item_id", ""),
    ("online_award_balance_entries.item_id", "item", "online_award_balance_entries", "item_id", ""),
    ("skill_book_templates.item_id", "item", "skill_book_templates", "item_id", ""),
    ("item_material_content_definitions.item_id", "item", "item_material_content_definitions", "item_id", ""),
    ("holy_suit_consumable_content_definitions.item_id", "item", "holy_suit_consumable_content_definitions", "item_id", ""),
    ("holy_suit_tiers.material_item_id", "item", "holy_suit_tiers", "material_item_id", ""),
    ("pet_content_species_definitions.egg_item_id", "item", "pet_content_species_definitions", "egg_item_id", ""),
    ("quest_reward_slots.item_id", "item", "quest_reward_slots", "item_id", ""),
    ("zeus_gift_limited_stock.item_id", "item", "zeus_gift_limited_stock", "item_id", ""),
    ("warehouse_expansion_policy_levels.key_item_id", "item", "warehouse_expansion_policy_levels", "key_item_id", ""),
    ("item_attribute_templates.id", "appendattr", "item_attribute_templates", "id", ""),
    ("character_items.attribute1..5", "appendattr", "character_items",
     "unnest(ARRAY[attribute1, attribute2, attribute3, attribute4, attribute5])", ""),
    ("monster_loot_rules.attribute1..5", "appendattr", "monster_loot_rules",
     "unnest(ARRAY[attribute1, attribute2, attribute3, attribute4, attribute5])", ""),
    ("item_attribute_templates.stat_type", "stattype", "item_attribute_templates", "stat_type", ""),
    ("skill_templates.skill_id", "skill", "skill_templates", "skill_id", ""),
    ("character_skills.skill_id", "skill", "character_skills", "skill_id", ""),
    ("skill_book_templates.skill_id", "skill", "skill_book_templates", "skill_id", ""),
    ("talent_templates.id", "talent", "talent_templates", "id", ""),
    ("character_talents.talent_id", "talent", "character_talents", "talent_id", ""),
    ("character_base.zodiac_type", "zodiac_sign", "character_base", "zodiac_type", ""),
    ("character_zodiac_skill_grids.grid_index", "zodiac_grid", "character_zodiac_skill_grids", "grid_index", ""),
    ("character_zodiac_skill_grids.selected_skill_id", "zodiac_kind", "character_zodiac_skill_grids",
     "selected_skill_id", "selected_skill_id >= 0"),
    ("monster_templates.template_key", "monster", "monster_templates", "template_key", ""),
    ("monster_spawn_definitions.template_key", "monster", "monster_spawn_definitions", "template_key", ""),
    ("monster_loot_rules.template_key", "monster", "monster_loot_rules", "template_key", ""),
    ("world_boss_areas.boss_template_key", "monster", "world_boss_areas", "boss_template_key", ""),
    ("npc_spawn_definitions.npc_key", "npc", "npc_spawn_definitions", "npc_key", ""),
    ("npc_text_templates.npc_key", "npc", "npc_text_templates", "npc_key", ""),
    ("npc_spawn_references.npc_key", "npc", "npc_spawn_references", "npc_key", ""),
    ("npc_appearance_templates.template_key", "npc_template", "npc_appearance_templates", "template_key", ""),
    ("map_templates.map_id", "map", "map_templates", "map_id", ""),
    ("character_base.Map", "map", "character_base", '"Map"', ""),
    ("monster_spawn_definitions.map_id", "map", "monster_spawn_definitions", "map_id", ""),
    ("npc_spawn_definitions.map_id", "map", "npc_spawn_definitions", "map_id", ""),
    ("map_address_points.map_id", "map", "map_address_points", "map_id", ""),
    ("map_address_points.(map_id,group,point)", "region", "map_address_points",
     "map_id::text || ':' || group_index::text || ':' || point_index::text", ""),
    ("character_quests.quest_id", "quest", "character_quests", "quest_id", ""),
    ("character_base.quest_completed_ids", "quest", "character_base", "unnest(quest_completed_ids)", ""),
    ("character_base.quest_current_id", "quest", "character_base", "quest_current_id", "quest_current_id > 0"),
    ("quest_monster_references.quest_id", "quest", "quest_monster_references", "quest_id", ""),
    ("quest_reward_slots.quest_id", "quest", "quest_reward_slots", "quest_id", ""),
    ("npc_spawn_references.quest_id", "quest", "npc_spawn_references", "quest_id", ""),
    ("character_base.selected_title_id", "title", "character_base", "selected_title_id", "selected_title_id > 0"),
    ("character_title_ownership.title_id", "title", "character_title_ownership", "title_id", ""),
    ("medusa_reward_title_definitions.client_title_id", "title", "medusa_reward_title_definitions", "client_title_id", ""),
    ("pet_templates.species_id", "species", "pet_templates", "species_id", ""),
    ("pet_content_species_definitions.species_id", "species", "pet_content_species_definitions", "species_id", ""),
    ("character_pets.species_id", "species", "character_pets", "species_id", ""),
    ("pet_skill_curve_steps.runtime_skill_id", "petskill", "pet_skill_curve_steps", "runtime_skill_id", ""),
    ("pet_content_species_definitions.starter_skill_id", "petskill", "pet_content_species_definitions", "starter_skill_id", ""),
    ("character_pet_skills.skill_id", "petskill", "character_pet_skills", "skill_id", ""),
    ("item_templates.id (kind mount)", "mount", "item_templates", "id", "kind = 'mount'"),
    ("character_experience_modifiers.status_id", "status", "character_experience_modifiers", "status_id", ""),
    ("guild_building_types.building_type", "guild_building", "guild_building_types", "building_type", ""),
    ("guild_buildings.building_type", "guild_building", "guild_buildings", "building_type", ""),
    ("character_items.holy_socket1..6_effect_id", "holystone", "character_items",
     "unnest(ARRAY[holy_socket1_effect_id, holy_socket2_effect_id, holy_socket3_effect_id, holy_socket4_effect_id,"
     " holy_socket5_effect_id, holy_socket6_effect_id])", ""),
    ("character_items.holy_suit_code", "suit", "character_items", "holy_suit_code", ""),
    ("holy_suit_tiers.type", "suit_tier", "holy_suit_tiers", "type", ""),
    ("class_templates.id", "class", "class_templates", "id", ""),
    ("character_base.profession", "class", "character_base", "profession", ""),
    ("character_base.belief", "faith", "character_base", "belief", ""),
    ("character_base.camp", "camp", "character_base", "camp", ""),
    ("character_currency_ledger.currency_code", "currency", "character_currency_ledger", "currency_code", ""),
    ("faction_crier_balance_options.currency_code", "currency", "faction_crier_balance_options", "currency_code", ""),
    ("item_quality_levels.level", "quality", "item_quality_levels", "level", ""),
    ("character_items.item_quality", "quality", "character_items", "item_quality", ""),
]

ITEM_SQL = ("(SELECT json_agg(json_build_object('id', id, 'kind', kind, 'name_key', name_key, 'display_name', display_name,"
            " 'equipment_slot', equipment_slot, 'min_level', min_level, 'class_ids', class_ids, 'texture', texture,"
            " 'icon', icon, 'stat_keys', (SELECT json_object_agg(k, array_length(string_to_array(v, ','), 1))"
            " FROM jsonb_each_text(stats::jsonb) AS e(k, v) WHERE v ~ '^-?[0-9.]+(,-?[0-9.]+)+$'))"
            " ORDER BY id) FROM item_templates)")


def run_psql(psql, db, sql):
    cmd = shlex.split(psql) + ["-X", "-q", "-At", "-v", "ON_ERROR_STOP=1", "-d", db]
    p = subprocess.run(cmd, input=sql, capture_output=True, text=True, timeout=300)
    if p.returncode != 0:
        raise RuntimeError("psql failed (%d): %s" % (p.returncode, p.stderr.strip()[-2000:]))
    return p.stdout


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--psql", default="sudo -n -u postgres psql", help="psql command (default: %(default)s)")
    ap.add_argument("--db", default="godswar", help="database name (plain name only)")
    ap.add_argument("--out", default=os.path.join(HERE, "catalog-origin", "db-refs.json"))
    a = ap.parse_args()
    if not re.match(r"^[A-Za-z_][A-Za-z0-9_]*$", a.db):
        sys.exit("--db must be a plain database name")

    # which tables/columns exist (so a different schema version skips instead of failing)
    cols = run_psql(a.psql, a.db, "BEGIN READ ONLY;\nSELECT table_name || '.' || column_name FROM information_schema.columns"
                    " WHERE table_schema = 'public';\nCOMMIT;\n").split()
    have = set(cols)
    parts, skipped, meta = [], [], {}
    for name, typ, table, expr, where in REFS:
        idents = re.findall(r'"?([A-Za-z_][A-Za-z0-9_]*)"?', re.sub(r"'[^']*'", "", expr + " " + where))
        missing = sorted(set("%s.%s" % (table, c) for c in idents
                             if c not in ("unnest", "ARRAY", "text") and "%s.%s" % (table, c) not in have))
        if missing:
            skipped.append({"ref": name, "missing": missing})
            continue
        meta[name] = {"type": typ, "table": table, "column": expr if len(expr) < 60 else name.split(".", 1)[1]}
        w = (" WHERE " + where) if where else ""
        parts.append("SELECT %s AS name, (SELECT COALESCE(json_agg(json_build_array(v, n) ORDER BY v), '[]'::json) FROM "
                     "(SELECT v, count(*) AS n FROM (SELECT %s AS v FROM public.%s%s) s WHERE v IS NOT NULL GROUP BY v) x) AS vals"
                     % ("'" + name.replace("'", "''") + "'", expr, table, w))
    sql = ("BEGIN READ ONLY;\nSELECT json_build_object('database', current_database(), 'taken_at', now(), 'refs', "
           "(SELECT json_object_agg(r.name, r.vals) FROM (%s) r), 'item_templates', %s);\nCOMMIT;\n"
           % ("\n UNION ALL ".join(parts), ITEM_SQL))
    out = run_psql(a.psql, a.db, sql).strip()
    doc = json.loads(out)
    refs = {}
    for name, vals in doc["refs"].items():
        refs[name] = dict(meta[name], values=vals)
    doc["refs"] = refs
    doc["skipped"] = skipped
    doc["read_by"] = "catalog_dbrefs.py (BEGIN READ ONLY)"
    os.makedirs(os.path.dirname(a.out), exist_ok=True)
    with open(a.out, "w", encoding="utf-8") as f:
        json.dump(doc, f, ensure_ascii=False, separators=(",", ":"))
    print("%s: %d refs, %d item templates, %d skipped -> %s" % (
        doc["database"], len(refs), len(doc.get("item_templates") or []), len(skipped), a.out))
    for s in skipped:
        print("  skipped %s (missing %s)" % (s["ref"], ", ".join(s["missing"])))


if __name__ == "__main__":
    main()
