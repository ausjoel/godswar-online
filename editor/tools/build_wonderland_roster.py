#!/usr/bin/env python3
"""Build editor/data/wonderland-roster.json (+ tests/fixtures/combat-v1-vectors.json) from a server dump.

The Wonderland roster lives only in server code (WonderlandMonsterPlan, WonderlandMonsterProfilePolicy,
WonderlandCapturedAttackPolicy, WonderlandBossAbilityPolicy); the editor cannot read it from the database.
Regenerate after any change to those files:
  1. copy server/ to a scratch folder, add tools/WonderlandRosterDump.cs.txt as
     tests/Godswar.Server.ProtocolChecks/ZzWonderlandRosterDump.cs and register it in Program.cs;
  2. GW_DUMP_OUT=/tmp/dump.json dotnet run --project tests/Godswar.Server.ProtocolChecks -c Release -- "ZZ dump"
  3. names: psql -AtF$'\\t' -c "select template_key, display_name from monster_templates where source_map_id=207"
  4. python3 -I editor/tools/build_wonderland_roster.py /tmp/dump.json names.tsv <server commit>
"""
import json, os, sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def main(dump_path, names_path, commit):
    d = json.load(open(dump_path))
    names = dict(line.rstrip("\n").split("\t", 1) for line in open(names_path, encoding="utf-8") if "\t" in line)
    roster = {}
    for camp, rows in d["roster"].items():
        out = []
        for r in rows:
            a = r["attack"]
            out.append({"id": r["object_id"], "island": r["island"], "key": r["key"], "name": names.get(r["key"], r["key"]),
                        "role": r["role"], "mechanic": r["mechanic"], "boss": r["boss"], "required": r["required"],
                        "allied": r["allied"], "stationary": r["stationary"], "level": r["level"], "hp": r["hp"],
                        "pd": r["pd"], "md": r["md"], "hit": r["hit"], "dodge": r["dodge"], "crit": r["crit"],
                        "interval": round(r["interval_s"], 3), "range": r["range"],
                        "magic": a["magic"], "pa": a["pa"], "ma": a["ma"], "area": r["area_radius"],
                        "authored_pa": r["authored"]["pa"], "authored_ma": r["authored"]["ma"], "authored_magic": r["authored"]["magic"]})
        roster[camp] = out
    doc = {"source": "godswar-dev server %s: WonderlandMonsterPlan + WonderlandMonsterProfilePolicy + "
                     "WonderlandCapturedAttackPolicy (normal hits, no silence) + WonderlandBossAbilityPolicy" % commit,
           "note": "Code roster: not stored in the database, so editor monster edits for map 207 have no effect. "
                   "HP is the same for every party size (party_size_matters=%s)." % d["party_size_matters"],
           "party_size_matters": d["party_size_matters"], "abilities": d["abilities"], "roster": roster}
    json.dump(doc, open(os.path.join(HERE, "data", "wonderland-roster.json"), "w"), indent=1)
    json.dump({"source": "AuthoredCombatPveCurrent (= V1) at server %s" % commit, "vectors": d["vectors"]},
              open(os.path.join(HERE, "tests", "fixtures", "combat-v1-vectors.json"), "w"))
    print(sum(len(v) for v in roster.values()), "roster rows,", len(d["vectors"]), "vectors")


if __name__ == "__main__":
    main(*sys.argv[1:4])
