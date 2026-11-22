#!/usr/bin/env python3
"""Build editor/data/medusa-stock-hp.json: the stock Medusa Island HP the server's migration writes into
medusa_monster_rules (State/DatabaseMigrations/PostgresSchemaMigrationCatalog.MedusaExternalHealth.cs: one Normal HP per
monster alias, x1 / x2 / x5 for difficulty 1 / 2 / 3).

The Monster stat curve page multiplies these, so writing a curve twice never compounds and "No change" restores stock.
Regenerate after the migration changes:
  python3 -I editor/tools/build_medusa_stock_hp.py server/src/Godswar.Server/State/DatabaseMigrations/PostgresSchemaMigrationCatalog.MedusaExternalHealth.cs
"""
import json, os, re, sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def parse(text):
    hp = {a: int(v) for a, v in re.findall(r"\('([a-z0-9-]+)',\s*(\d+)\)", text.split("difficulties(")[0])}
    mult = {d: int(m) for d, m in re.findall(r"\((\d),\s*(\d+)\)", text.split("difficulties(")[1].split(")\n")[0] + ")")}
    if not hp or sorted(mult) != ["1", "2", "3"]:
        raise SystemExit("could not read the captured HP / difficulty multipliers")
    return hp, mult


def main(src):
    hp, mult = parse(open(src, encoding="utf-8").read())
    doc = {"source": "server " + os.path.basename(src) + " (migration 20260828_120_medusa_external_health)",
           "difficulty_multiplier": mult, "normal_hp": hp}
    with open(os.path.join(HERE, "data", "medusa-stock-hp.json"), "w") as f:
        json.dump(doc, f, indent=1, sort_keys=True)
    print(len(hp), "monsters, multipliers", mult)


if __name__ == "__main__":
    main(sys.argv[1])
