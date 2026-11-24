#!/usr/bin/env python3
"""build_quest_rewards.py - W23 (the admin 2026-11-24): what each quest's hand-in pays besides EXP / Talent / Silver / Gold,
for the character maxer's quest finisher (modules/maxer.py). Read-only on the server source; the editor treats the
output as data.

Run from the repo root (or pass the server folder):
    python3 -I editor/modules/skills_data/build_quest_rewards.py [SERVER_DIR]      (default: ../../../server)

Writes quest_rewards.json next to this file:
  items:  {quest id: [{slot, item}]} - the reward menu the hand-in answer offers, read the way
          Packets/QuestRewardItemCatalog.Read does it: the captured areas of objective kind 4 then 8
          (Packets/PacketBuilder.QuestAnswerFrames.Generated.cs CapturedQuestRewardAreas, either kind as fallback),
          else Domain/World/Content/StarterQuestRewardRecords.cs; 72-byte slots, item id at +8, 0 / FFFFFFFF = empty,
          an item already listed is skipped. The database table quest_reward_slots (the GM override) replaces a
          quest's whole menu at run time; the editor reads that itself.
  titles: {quest id: title id} - Application/Titles/TitleExpertPolicy.cs QuestTitles.
  mycenae: {quests: [...], item, quantity} - Application/North/MycenaeBossPolicy.cs (paid into the bag by the server).
EXP / Talent / Silver / Gold come from quest_rules.json (StarterQuestChain.cs) and quest_reward_values.
"""
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_SRC = os.path.normpath(os.path.join(HERE, "..", "..", "..", "server"))
APP = os.path.join("src", "Godswar.Server")
RECORD, ITEM_AT, SLOTS = 72, 8, 8


def _read(src, *parts):
    with open(os.path.join(src, APP, *parts), encoding="utf-8") as f:
        return f.read()


def _hex(body):
    return "".join(re.findall(r'"([0-9A-Fa-f]*)"', body))


def captured_areas(text):
    """{(quest, kind): bytes} from the CapturedQuestRewardAreas initialiser."""
    start = text.index("CapturedQuestRewardAreas = new()")
    block = text[start:text.index("};", start)]
    out = {}
    for m in re.finditer(r"\[\((\d+)u, (\d+)u\)\] =(.*?)(?=,\s*\[\(|\s*$)", block, re.S):
        out[(int(m.group(1)), int(m.group(2)))] = bytes.fromhex(_hex(m.group(3)))
    return out


def starter_areas(text):
    out = {}
    for m in re.finditer(r"\[(\d+)u\] = Convert\.FromHexString\((.*?)\)", text, re.S):
        out[int(m.group(1))] = bytes.fromhex(_hex(m.group(2)))
    return out


def menu(area):
    """[(slot, item)] of one area, empty slots skipped."""
    out = []
    for slot in range(min(SLOTS, len(area) // RECORD)):
        item = int.from_bytes(area[slot * RECORD + ITEM_AT: slot * RECORD + ITEM_AT + 4], "little")
        if item not in (0, 0xFFFFFFFF):
            out.append((slot, item))
    return out


def resolve(quest, captured, starter):
    """QuestRewardItemCatalog.Read(overridden: false): kinds 4 then 8, each falling back to the other kind and then to
    the starter records; the first slot naming an item wins."""
    items, seen = [], set()
    for kind in (4, 8):
        area = captured.get((quest, kind)) or captured.get((quest, 4 if kind == 8 else 8)) or starter.get(quest) or b""
        for slot, item in menu(area):
            if item not in seen:
                seen.add(item)
                items.append({"slot": slot, "item": item})
    return items


def quest_titles(text):
    start = text.index("QuestTitles = new Dictionary<uint, uint>")
    block = text[start:text.index("};", start)]
    return {int(q): int(t) for q, t in re.findall(r"\[(\d+)\] = (\d+)", block)}


def mycenae(text):
    ids = re.search(r"QuestIds \{ get; \} =\s*new uint\[\] \{([^}]*)\}", text).group(1)
    return {"quests": [int(x) for x in re.findall(r"\d+", ids)],
            "item": int(re.search(r"RewardItemId = (\d+);", text).group(1)),
            "quantity": int(re.search(r"RewardQuantity = (\d+);", text).group(1))}


def build(src):
    captured = captured_areas(_read(src, "Packets", "PacketBuilder.QuestAnswerFrames.Generated.cs"))
    starter = starter_areas(_read(src, "Domain", "World", "Content", "StarterQuestRewardRecords.cs"))
    quests = sorted({q for q, _ in captured} | set(starter))
    items = {str(q): resolve(q, captured, starter) for q in quests}
    return {"source": "server/src/Godswar.Server: PacketBuilder.QuestAnswerFrames.Generated.cs, StarterQuestRewardRecords.cs, "
                      "TitleExpertPolicy.cs, MycenaeBossPolicy.cs",
            "items": {q: v for q, v in items.items() if v},
            "titles": {str(k): v for k, v in sorted(quest_titles(_read(src, "Application", "Titles", "TitleExpertPolicy.cs")).items())},
            "mycenae": mycenae(_read(src, "Application", "North", "MycenaeBossPolicy.cs"))}


def main():
    src = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_SRC
    out = build(src)
    with open(os.path.join(HERE, "quest_rewards.json"), "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=1, sort_keys=True)
        f.write("\n")
    print("reward menus %d, quest titles %d, mycenae quests %d" % (len(out["items"]), len(out["titles"]), len(out["mycenae"]["quests"])))


if __name__ == "__main__":
    main()
