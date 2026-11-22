"""What each title is awarded for (wave 4 D item 7). Reviewed by hand from the server source; every entry cites it.

Categories follow the admin's wording ("stats -> matk, titles, instance titles..."): a top category and a sub-category.
'how' is the award rule, 'npc' the awarder NPC keys, 'quests' the quest ids, 'items' the item ids that grant it, 'bonus'
the stat effect (only the Medusa titles have one: State/MedusaTitleAttributePolicy.cs, the strongest owned wins; every other
title is cosmetic, TitleExpertPolicy.cs remarks), 'live' False when no server code awards it (administrator grant only),
'switch' the server switch it needs. Paths are relative to server/src/Godswar.Server/.
"""

EXPERT = "Title Expert (Athens_071 / Sparta_071)"
AWARDER = "Battlefield Awarder (Athens_073 / Sparta_073)"
TE_SRC = "Application/Titles/TitleExpertPolicy.cs"
TE_SWITCH = "npc.title_expert = 1 (server rate) or GODSWAR_TITLE_EXPERT=1; off by default"

CATEGORIES = [
    ("stats", "Stats titles", "Title Expert attribute titles: the character's own stat (without bonuses) reaches a threshold"),
    ("level", "Level titles", "Title Expert class titles: level and attack (physical for Warrior/Champion, magic for Priest/Mage)"),
    ("quest", "Quest titles", "Finishing a story quest"),
    ("instance", "Instance titles", "Clearing an instance (Medusa's Island, Atlantis, Wonderland)"),
    ("battlefield", "Battlefield titles", "Pindus Mountain and Ni Mini Valley rankings, claimed at the Battlefield Awarder"),
    ("event", "Event titles", "Lelantine Farm, Troy, races and other events"),
    ("marriage", "Marriage titles", "Engagement and wedding"),
    ("vip", "VIP titles", "VIP (donator) star levels"),
    ("special", "Staff and special titles", "Given by a GM or an item"),
]

S = {}


def add(ids, cat, sub, how, src, **kw):
    for i in ids:
        S[i] = dict(category=cat, sub=sub, how=how, source=src, **kw)


# ---- Stats (Title Expert, attribute page; thresholds from TitleExpertPolicy.AttributeTitles)
for stat, a, b, t1, t2 in [("HP", 20000, 32000, 3000, 3001), ("ATK", 3600, 4500, 3002, 3003), ("MATK", 2800, 3500, 3004, 3005),
                           ("DEF", 2000, 2600, 3006, 3007), ("MDEF", 1300, 1800, 3008, 3009), ("Hit", 500, 650, 3010, 3011),
                           ("Dodge", 500, 650, 3012, 3013), ("Absorb", 900, 1300, 3014, 3015)]:
    add([t1], "stats", stat, "Own %s at least %s (not counting altar or title bonuses), claimed at the Title Expert" % (stat, format(a, ",")),
        TE_SRC + " AttributeTitles", npc=EXPERT, switch=TE_SWITCH)
    add([t2], "stats", stat, "Own %s at least %s (not counting bonuses), claimed at the Title Expert after the first %s title" % (stat, format(b, ","), stat),
        TE_SRC + " AttributeTitles", npc=EXPERT, switch=TE_SWITCH)

# ---- B22 item 12: stats titles tiers 3-8, per class (TitleExpertPolicy.LadderThresholds; ids 3016-3063, generated names)
_B22_LADDER = {  # stat: {class: tier 3-8 needs}; same numbers as tools/b22-title-ladder/title-ladder.json
    "HP": {"Warrior": [86700, 108400, 130100, 151800, 173500, 195200], "Champion": [87700, 109600, 131500, 153400, 175300, 197300],
           "Priest": [67300, 84200, 101000, 117800, 134600, 151500], "Mage": [59800, 74800, 89700, 104700, 119600, 134600]},
    "ATK": {"Warrior": [6960, 8700, 10440, 12180, 13920, 15660], "Champion": [8000, 10000, 12000, 14000, 16000, 18000]},
    "MATK": {"Priest": [5650, 7060, 8480, 9890, 11300, 12710], "Mage": [6030, 7530, 9040, 10550, 12050, 13560]},
    "DEF": {"Warrior": [4430, 5540, 6650, 7750, 8860, 9970], "Champion": [2900, 3620, 4340, 5070, 5790, 6520],
            "Priest": [3200, 4010, 4810, 5610, 6410, 7210], "Mage": [2940, 3320, 3750, 4240, 4790, 5420]},
    "MDEF": {"Warrior": [3250, 4060, 4880, 5690, 6500, 7320], "Champion": [2530, 3170, 3800, 4440, 5070, 5700],
             "Priest": [3960, 4950, 5940, 6930, 7920, 8910], "Mage": [3340, 4170, 5010, 5840, 6680, 7510]},
    "Hit": {"Warrior": [930, 1170, 1400, 1630, 1870, 2100], "Champion": [1360, 1700, 2040, 2380, 2720, 3060],
            "Priest": [1040, 1300, 1560, 1820, 2080, 2340], "Mage": [1250, 1570, 1880, 2190, 2510, 2820]},
    "Dodge": {"Warrior": [920, 1150, 1380, 1610, 1840, 2060], "Champion": [1130, 1420, 1700, 1990, 2270, 2550],
              "Priest": [1240, 1550, 1860, 2170, 2480, 2790], "Mage": [920, 1150, 1380, 1610, 1840, 2070]},
    "Absorb": {"Warrior": [17800, 22200, 26600, 31100, 35500, 39900], "Champion": [15000, 18700, 22500, 26200, 29900, 33700],
               "Priest": [17500, 21800, 26200, 30500, 34900, 39300], "Mage": [15300, 19100, 22900, 26700, 30600, 34400]},
}
for _b, _stat in enumerate(["HP", "ATK", "MATK", "DEF", "MDEF", "Hit", "Dodge", "Absorb"]):
    for _tier in range(3, 9):
        _need = ", ".join("%s %s" % (c, format(v[_tier - 3], ",")) for c, v in _B22_LADDER[_stat].items())
        add([3016 + _b * 6 + _tier - 3], "stats", _stat,
            "Tier %d: own %s at least %s (not counting bonuses), claimed in order at the Title Expert" % (_tier, _stat, _need),
            TE_SRC + " LadderThresholds (B22)", npc=EXPERT, switch=TE_SWITCH)

# ---- Level titles (Title Expert, class page; TitleExpertPolicy.LevelTiers)
for cls, rows, att in [("Warrior", [(40, 250, 1000), (60, 600, 1001), (80, 1400, 1002), (100, 2400, 1003)], "physical"),
                       ("Champion", [(40, 300, 1100), (60, 800, 1101), (80, 1800, 1102), (100, 2800, 1103)], "physical"),
                       ("Priest", [(40, 180, 1200), (60, 400, 1201), (80, 900, 1202), (100, 1800, 1203)], "magic"),
                       ("Mage", [(40, 220, 1300), (60, 500, 1301), (80, 1100, 1302), (100, 2100, 1303)], "magic")]:
    for lv, at, tid in rows:
        add([tid], "level", cls, "%s, level %d and %s attack at least %s; claimed in order at the Title Expert" % (cls, lv, att, format(at, ",")),
            TE_SRC + " LevelTiers", npc=EXPERT, switch=TE_SWITCH)

# ---- Quest titles (TitleExpertPolicy.QuestTitles); 2000/2500 also from the Private Badges (State/GiftPackPolicy.Finalise2.cs)
Q = {2000: ([1012], "Athens"), 2001: ([1116], "Athens"), 2002: ([1118], "Athens"), 2003: ([1217], "Athens"), 2004: ([1221], "Athens"),
     2005: ([1122], "Athens"), 2500: ([12], "Sparta"), 2501: ([116], "Sparta"), 2502: ([118], "Sparta"), 2503: ([217], "Sparta"),
     2504: ([221], "Sparta"), 2505: ([122], "Sparta"), 2506: ([1376, 376], "both")}
for tid, (qs, camp) in Q.items():
    extra = {}
    if tid in (2000, 2500):
        extra = dict(items=[14029 if tid == 2000 else 14030],
                     note="The quest is not in the server's quest chain; the Athenian/Spartan Private Badge gives the title.")
    elif tid in (2001, 2501):
        extra = dict(live=False, note="The quest is not in the server's quest chain, so it cannot be earned yet.")
    add([tid], "quest", camp, "Finish quest %s; claimed at the Title Expert" % " / ".join(map(str, qs)), TE_SRC + " QuestTitles",
        npc=EXPERT, quests=qs, switch=TE_SWITCH, **extra)

# ---- Instances
MEDUSA_SRC = "State/DatabaseMigrations/PostgresSchemaMigrationCatalog.MedusaRewardPolicy.cs (medusa_reward_title_definitions)"
for tid, name, pct, how in [(5011, "Medusa Challengers", 3, "Medusa's Island: the team reaches 3,000 points within 20 minutes"),
                            (5010, "Medusa Slayers", 2, "Medusa's Island: 3,000 points within 15 minutes"),
                            (5009, "Medusa Executioners", 1, "Medusa's Island: 3,000 points within 10 minutes"),
                            (5154, "Gorgon Breaker", 4, "Medusa's Island completion reward"),
                            (5153, "Bane of the Three Sisters", 5, "Medusa's Island completion reward"),
                            (5152, "Heir of Perseus", 6, "Medusa's Island completion reward")]:
    add([tid], "instance", "Medusa's Island", how + " (medusa_completion_reward_rules decide which)", MEDUSA_SRC, name=name,
        bonus="+%d%% physical and magic attack and defence (only the strongest owned Medusa title counts)" % pct,
        bonus_pct=pct, grant_note="Earned ownership; 5152-5154 are not in editor_title_catalog, so they cannot be granted here.")
add([5013], "instance", "Atlantis", "Clear Atlantis with a team", "Application/WorldInstances/AtlantisCompletionRewardContracts.cs")
add([5014], "instance", "Atlantis", "Clear Atlantis alone", "Application/WorldInstances/AtlantisCompletionRewardContracts.cs")
for isl, tid, name in [(1, 5155, "Gatebreaker"), (2, 5114, "Demonbreaker"), (3, 5156, "Flamebreaker"), (4, 5115, "Stonebreaker"),
                       (5, 5157, "Marshal's Bane"), (6, 5116, "Dragonbane"), (7, 5117, "Hydra's Bane"), (8, 5118, "Wonderland Sovereign")]:
    add([tid], "instance", "Wonderland", "Clear Wonderland island %d (everyone still in the run)" % isl,
        "Application/WorldInstances/WonderlandTitleContracts.cs", name=name)

# ---- Battlefields (Application/WorldInstances/BattlefieldRewardPolicy.cs, Game/GameClientHandler.BattlefieldAwarder.cs)
BF = "Application/WorldInstances/BattlefieldRewardPolicy.cs"
add([5005], "battlefield", "Pindus Mountain", "1st by kill points in a Pindus round", BF, npc=AWARDER)
add([5006], "battlefield", "Pindus Mountain", "2nd-5th by kill points in a Pindus round", BF, npc=AWARDER)
add([5007], "battlefield", "Pindus Mountain", "1st by score (non-kill points) in a Pindus round", BF, npc=AWARDER)
add([5008], "battlefield", "Pindus Mountain", "2nd-5th by score in a Pindus round", BF, npc=AWARDER)
add([5131, 5132], "battlefield", "Pindus Mountain", "Most healing on the Pindus battlefield (client text)", "client DesigInfo.dat",
    live=False, note="No healing ranking in the server: administrator grant only.")
add([5015], "battlefield", "Ni Mini Valley", "First place in Ni Mini Valley, Athens", BF, npc=AWARDER)
add([5016], "battlefield", "Ni Mini Valley", "First place in Ni Mini Valley, Sparta", BF, npc=AWARDER)
add([5000], "battlefield", "Guild war", "Guild Defensive War (client text)", "client DesigInfo.dat", live=False,
    note="No Guild Defensive War in the server: administrator grant only.")

# ---- Events
add([5111], "event", "Lelantine Farm", "Kill Cerberus on Lelantine Farm", "State/PostgresGameStore.LelantineFarmRounds.cs")
add([5112], "event", "Lelantine Farm", "Highest score of the faction in a Lelantine Farm round", "State/PostgresGameStore.LelantineFarmRounds.cs")
add([5123], "event", "Troy", "Best scoring guild of the Trojan Expedition; lasts a week", "Application/Troy/TroyEvent.cs")
add([5001, 5002, 5003, 5004], "event", "Round-the-city race", "Weekly race prizes (client text)", "client DesigInfo.dat", live=False,
    note="No race ranking in the server: administrator grant only.")
add([5113, 5124, 5125, 5126, 5127, 5128], "event", "Retail events", "Retail IGG event titles (client text)", "client DesigInfo.dat",
    live=False, note="No award path in the server: administrator grant only.")
add([6000], "event", "Retail events", "GodsWar Goddess contest (client text)", "client DesigInfo.dat", live=False,
    note="Administrator grant only.")

# ---- Marriage (Infrastructure/Characters/PostgresMarriageStore.cs)
add([1401], "marriage", "Marriage", "Getting engaged (removed again on divorce)", "Infrastructure/Characters/PostgresMarriageStore.cs")
add([1402], "marriage", "Marriage", "Getting married (removed again on divorce)", "Infrastructure/Characters/PostgresMarriageStore.cs")

# ---- VIP and special
add([5017, 5119, 5120, 5121, 5122], "vip", "VIP", "VIP star level 1-5 (client text)", "client DesigInfo.dat", live=False,
    note="The server has no VIP star titles: administrator grant only.")
add([5129], "special", "Item", "Use a Guardian's Badge (permanent)", "State/ProgressItemPolicy.cs", items=[4115])   # permanent: owner decision 2026-10-31
add([5012], "special", "Staff", "Newbie tutor title given by a GM", "client DesigInfo.dat", live=False)
add([5130], "special", "Staff", "Significant contribution to the game", "client DesigInfo.dat", live=False)
add([5150], "special", "Staff", "Testers who helped shape the Origin server", "editor_title_catalog", live=False)
add([5151], "special", "Staff", "Bug hunters", "editor_title_catalog", live=False)

for v in S.values():
    v.setdefault("live", True)
    v.setdefault("bonus", None)


def describe(title_id):
    """The source entry of one title, or an 'unknown' entry."""
    return S.get(int(title_id)) or dict(category="special", sub="Unknown", how="No award rule found", source="", live=False, bonus=None)
