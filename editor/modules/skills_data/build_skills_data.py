#!/usr/bin/env python3
"""build_skills_data.py - quest rules of the reborn server, for the Skills & talents editor (modules/skills.py).

Run on the Mac (it reads the reborn source tree, read-only):
    python3 -I gwadmin-web/modules/skills_data/build_skills_data.py [SOURCE_DIR]
SOURCE_DIR default: GodsWar-2.51/emulators/src/godswar-server (commit b85a1244, what godswar251 runs).

Writes quest_rules.json next to this file:
  quests: {id: {camp, giver, responder, exp, tp, silver, gold, minlv, maxlv, main, sort, color, segment, scroll,
                prereq, daily, repeat, per_day, giver_map, responder_map, utility, exact_level,
                objectives: [{name, required, monster, map}], collect: {item, required}}}
from src/Godswar.Server/Domain/World/Content/StarterQuestChain.cs (the quest roster the server knows),
StarterQuestObjectives.cs (kill objectives: up to 4 counters of 16 bits packed into character_quests.progress) and
StarterQuestCollectObjectives.cs (item objectives). The editor treats this file as data only.
"""
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_SRC = os.path.normpath(os.path.join(HERE, "..", "..", "..", "..", "..", "GodsWar-2.51", "emulators", "src",
                                            "godswar-server"))
CONTENT = os.path.join("src", "Godswar.Server", "Domain", "World", "Content")


def split_args(s):
    out, cur, q = [], "", False
    for ch in s:
        if ch == '"':
            q = not q
        if ch == "," and not q:
            out.append(cur.strip())
            cur = ""
        else:
            cur += ch
    if cur.strip():
        out.append(cur.strip())
    return out


def val(x):
    x = x.strip()
    if x.startswith('"'):
        return x.strip('"')
    if x in ("true", "false"):
        return x == "true"
    if x == "string.Empty":
        return ""
    if x in ("SpartaCamp", "AthensCamp"):
        return x[:-4]
    if re.match(r"^-?\d+u?$", x):
        return int(x.rstrip("u"))
    if re.match(r"^-?\d+(\.\d+)?f$", x):
        return float(x[:-1])
    return x


def main():
    src = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_SRC
    d = os.path.join(src, CONTENT)
    chain = open(os.path.join(d, "StarterQuestChain.cs"), encoding="utf-8").read()
    names = ["id", "camp", "giver", "responder", "exp", "tp", "silver", "gold", "minlv", "maxlv", "main", "sort", "color",
             "segment", "scroll", "prereq", "configured_daily", "giver_map", "responder_map", "utility", "exact_level"]
    quests = {}
    for m in re.finditer(r"^\s*new\((\d+), (.*)\),\s*$", chain, re.M):
        args = [val(a) for a in split_args(m.group(1) + ", " + m.group(2))]
        q = dict(zip(names, args))
        main_ = q["main"]
        daily = not q["utility"] and (q["configured_daily"] or (not main_ and (q["color"] == 2 or q["sort"] in (3, 4))))
        repeat = not main_ and not q["utility"] and not daily and q["sort"] == 1 and not q["scroll"]
        q["daily"], q["repeat"] = bool(daily), bool(repeat)
        q["per_day"] = 1 if daily else 3 if repeat else 0
        q["objectives"], q["collect"] = [], None
        quests[str(q["id"])] = q
    obj = open(os.path.join(d, "StarterQuestObjectives.cs"), encoding="utf-8").read()
    for m in re.finditer(r"\[(\d+)u\] = \[(.*?)\],\s*$", obj, re.M):
        qid = m.group(1)
        rows = []
        for o in re.finditer(r'new QuestObjective\("([^"]*)", "([^"]*)", (\d+), (\d+)u, (\d+)u', m.group(2)):
            rows.append({"name": o.group(1) or o.group(2), "required": int(o.group(3)), "monster": int(o.group(4)),
                         "map": int(o.group(5))})
        if qid in quests:
            quests[qid]["objectives"] = rows
    col = open(os.path.join(d, "StarterQuestCollectObjectives.cs"), encoding="utf-8").read()
    for m in re.finditer(r"\[(\d+)u\] = new\((\d+)u, (\d+)", col):
        if m.group(1) in quests:
            quests[m.group(1)]["collect"] = {"item": int(m.group(2)), "required": int(m.group(3))}
    out = {"source": "godswar-server " + CONTENT.replace(os.sep, "/") + " (StarterQuestChain.cs, "
                     "StarterQuestObjectives.cs, StarterQuestCollectObjectives.cs)",
           "counter_bits": 16, "counter_slots": 4, "counter_max": 32767, "max_carried": 20,
           "count": len(quests), "quests": quests}
    with open(os.path.join(HERE, "quest_rules.json"), "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, separators=(",", ":"), sort_keys=True)
    camps = {}
    for q in quests.values():
        camps[q["camp"]] = camps.get(q["camp"], 0) + 1
    print("quests %d %s; with kill objectives %d; collect %d" % (len(quests), camps, sum(1 for q in quests.values() if q["objectives"]),
                                                                sum(1 for q in quests.values() if q["collect"])))


if __name__ == "__main__":
    main()
