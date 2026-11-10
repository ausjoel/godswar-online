"""modules/skills.py - the "Skills & talents" editor: the skill book (K), the talent tree, zodiac (sign, level, energy and
the 16 training grids), titles and the quest log. Contract: ../MODULES.md. Owned by the skills module task.

What the reborn server does with each part (source: GodsWar-2.51/emulators/src/godswar-server, commit b85a1244,
abbreviated S/ = src/Godswar.Server):

  * Skills (character_skills): read at world entry from the PINNED gameplay content
    (S/Infrastructure/Characters/PostgresCharacterSnapshotReader.Progression.cs SkillsQuery: joins
    gameplay_skill_combat_definitions at the published revision and keeps only skills whose class_ids contain the
    character's class). A skill "family" (base_name, e.g. Light Chop 1..5) has ONE row: reading the next book deletes
    the family's row and inserts the new skill id with the book's skill_level (S/Infrastructure/Pets/
    PostgresPetDurableCommandExecutor.PlayerSkillBooks.cs:281-332). The editor writes exactly that.
  * Talents (character_talents): rank 0..100 (S/Application/Talents/TalentProgression.cs RankCap); the client shows at
    most 60 (Polish/POINTS-AUDIT.md CL1). Only the class's talents are loaded; Champions get 18 of the 19 class-1
    talents in a fixed order (PostgresCharacterSnapshotReader.Progression.cs TalentsQuery). Every changed row bumps
    outbox_revision (PostgresTalentUpgradeCommandExecutor.Mutation.cs:126-142). Changing a rank does not touch the
    talent points (character_base.SkillPoint), like a GM edit.
  * Zodiac: sign character_base.zodiac_type 0..11 (S/Packets/PacketBuilder.ForgeZodiac.cs:47), level 1..30, energy
    0..storage cap of the level (S/State/ZodiacEnergyCatalog.cs). Grids (character_zodiac_skill_grids): 16 grids, level
    0..50, selected skill = a MagicMini.xml Kind or -1, with the server's selection rules
    (S/State/ZodiacSkillGridSelection.cs): only an active grid (level > 0) holds a skill; grids 0-3 / 8-11 take Kinds
    10000-19999, grids 4-7 / 12-15 take 20000-29999; attack grids (0-7) only the character's own class and a learned
    skill of that family (runtime ids (Kind % 10000) * 10 .. +4); defence grids (8-15) any class; a Kind once per row.
  * Titles: earned Medusa/Atlantis/Wonderland ownership remains evidence. Compatible title-grant v1
    runtime + migration234 expose independent administrative grants. The offline transaction path uses
    exact expected grant IDs, pre-edit backups and Undo. Legacy readers retain their existing selectable set.
  * Quests (character_quests): state 0 = carried (in the quest log, at most 20), state 1 = completed (progress 0);
    progress = up to four 16-bit kill / item counters (S/Domain/World/Content/StarterQuestObjectives.cs:648-658).
    The server rewrites the whole table from its memory on save, so an online edit would be lost -> offline only.
    Only quests in the server's roster (StarterQuestChain.cs, 944 quests, extracted to skills_data/quest_rules.json)
    are used; the camp must match the character. Daily counters (character_quest_daily) and the quest EXP appraisal
    (character_quest_appraisal) are "advanced" tables.

Live edits (round-2 server, a logged-in character): talent points (SkillPoint) and zodiac energy go through the shared
live-or-database path (ctx.set_character_fields -> POST /admin/progression); everything else needs the character
logged out (423 with the reason) because the server keeps these in memory for the session.
"""
import datetime
import json
import os
import threading

from . import common, title_grants
from .common import Bad, NotFound, Refused, lit

M = common.module(
    "skills", "Skills & talents",
    icon={"sprite": 35},          # catalog-icons.png tile
    order=40,
    status="ready",
    description="The skill book (K): class skills, the talent tree, zodiac sign / level / energy and the 16 training "
                "grids, titles and the quest log.",
    needs_character=True,
)

HERE = os.path.dirname(os.path.abspath(__file__))
CLASS_NAMES = common.CLASS_NAMES
CAMP_KEYS = {0: "Sparta", 1: "Athens"}

# ---------------------------------------------------------------- server constants (sources in the docstring)
TALENT_RANK_CAP = 100
CLIENT_TALENT_CAP = 60
CHAMPION_TALENTS = [64, 59, 61, 65, 51, 55, 62, 67, 50, 53, 56, 63, 66, 52, 54, 58, 60, 57]
ZODIAC_SIGNS = 12
ZODIAC_LEVEL_MAX = 30
ZODIAC_STORAGE = [1_000, 3_000, 8_000, 20_000, 28_000, 42_000, 60_000, 80_000, 100_000, 125_000, 150_000, 180_000,
                  210_000, 245_000, 280_000, 315_000, 355_000, 400_000, 445_000, 500_000, 555_000, 610_000, 665_000,
                  725_000, 785_000, 845_000, 905_000, 965_000, 1_025_000, 1_090_000]
GRID_COUNT, GRID_LEVEL_MAX, GRIDS_PER_ROW = 16, 50, 4
GRID_GOLD = [0, 2_300, 7_200, 14_400, 0, 2_300, 7_200, 14_400, 0, 0, 920, 920, 0, 0, 920, 920]
ZODIAC_KIND_CLASS = {
    20010: 0, 20001: 0, 20002: 0, 10003: 0, 10004: 0, 10005: 0, 10006: 0,
    10025: 1, 10026: 1, 10027: 1, 20028: 1, 20029: 1, 10030: 1, 10031: 1, 20032: 1, 20033: 1,
    10080: 2, 10081: 2, 20082: 2, 20083: 2, 20084: 2, 10085: 2, 10086: 2, 10075: 2, 10076: 2,
    10050: 3, 10051: 3, 10052: 3, 20053: 3, 20054: 3, 20055: 3, 20056: 3, 10057: 3, 20058: 3}
MAX_CARRIED_QUESTS = 20
COUNTER_SLOTS, COUNTER_BITS, COUNTER_MAX = 4, 16, 32767
APPRAISAL_DEFAULT_BP = 1000          # +10 % quest EXP (docs/quest-experience-appraisal-20260924.md, SM_L0_06)
QUEST_DAY_ROLLOVER_HOUR = 12         # S/State/QuestDailyState.cs:19 (server-local day, the VPS runs UTC)


def talent_step_cost(rank):
    """Talent points to go from `rank` to rank + 1 (TalentProgression.CalculateUpgradeCost)."""
    n = max(0, rank) + 1
    tri = lambda v: v * (v + 1) // 2         # noqa: E731
    if n > TALENT_RANK_CAP:
        return 0
    if n <= 10:
        return n
    if n <= 40:
        o = n - 11
        return 10 + o * 6 + tri(o)
    if n <= 60:
        o = n - 41
        return 380 + o * 18 + tri(o)
    if n <= 80:
        o = n - 61
        return 900 + o * 32 + tri(o) * 2
    if n <= 90:
        o = n - 81
        return 1900 + o * 65 + tri(o) * 5
    o = n - 91
    return 2900 + o * 90 + tri(o) * 7


def talent_required_level(rank):
    """Player level the server wants before rank -> rank + 1 (TalentProgression.CalculateRequiredPlayerLevel)."""
    n = max(0, rank) + 1

    def scale(v, smin, smax, tmin, tmax):
        if v <= smin:
            return tmin
        if v >= smax:
            return tmax
        return tmin + ((v - smin) * (tmax - tmin)) // (smax - smin)
    if n <= 40:
        return scale(n, 1, 40, 1, 120)
    if n <= 60:
        return 120 + (n - 40)
    return scale(n, 61, TALENT_RANK_CAP, 141, 160)


def talent_effective_rank(rank):
    r = min(max(rank, 0), TALENT_RANK_CAP)
    if r <= 40:
        return r
    if r <= 60:
        return 40 + (r - 40) * 2
    if r <= 80:
        return 80 + (r - 60) * 3
    if r <= 90:
        return 140 + (r - 80) * 5
    return 190 + (r - 90) * 7


TALENT_TABLE = [{"rank": r, "step_cost": talent_step_cost(r - 1) if r else 0,
                 "total_cost": sum(talent_step_cost(k) for k in range(r)),
                 "level": talent_required_level(r - 1) if r else 0, "effective": talent_effective_rank(r)}
                for r in range(TALENT_RANK_CAP + 1)]


def zodiac_cap(level):
    return ZODIAC_STORAGE[min(max(int(level), 1), ZODIAC_LEVEL_MAX) - 1]


def grid_group(g):
    """1 = Kinds 10000-19999, 2 = 20000-29999 (ZodiacSkillGridSelectionCatalog.IsAllowedForGrid)."""
    return 1 if g % 8 < GRIDS_PER_ROW else 2


def grid_is_defence(g):
    return g >= GRID_COUNT // 2


def kind_family_ids(kind):
    first = (kind % 10000) * 10
    return list(range(first, first + 5))


def quest_day(now=None):
    """The server's quest quota day (QuestDailyState.DayOf): before 12:00 local it is still the previous day."""
    now = now or datetime.datetime.now()
    d = now.date()
    return d - datetime.timedelta(days=1) if now.hour < QUEST_DAY_ROLLOVER_HOUR else d


# ---------------------------------------------------------------- reference data (cached per process)
_LOCK = threading.Lock()
_CACHE = {}


def quest_rules():
    with _LOCK:
        if "rules" not in _CACHE:
            try:
                with open(os.path.join(HERE, "skills_data", "quest_rules.json"), encoding="utf-8") as f:
                    _CACHE["rules"] = json.load(f)
            except (OSError, ValueError) as e:
                _CACHE["rules"] = {"quests": {}, "error": "skills_data/quest_rules.json: %s" % e}
        return _CACHE["rules"]


def content(ctx):
    """The pinned gameplay content the server uses (skills, books, talents) - read once per database."""
    rb = ctx.need_rb()
    key = "content:" + rb.db
    with _LOCK:
        if key in _CACHE:
            return _CACHE[key]
    pub = "(SELECT revision FROM gameplay_content_publication WHERE family = 'gameplay')"
    d = rb.pg.json("""json_build_object(
      'revision', %(pub)s,
      'skills', (SELECT coalesce(json_agg(json_build_object('id', skill_id, 'name', display_name, 'base', base_name,
                  'level', skill_level, 'classes', class_ids, 'prev', previous_skill_id, 'minlv', min_level,
                  'maxlv', max_level, 'mp', mp, 'cooldown', cooldown_seconds, 'cast', cast_time_seconds) ORDER BY skill_id), '[]')
                 FROM gameplay_skill_combat_definitions WHERE revision = %(pub)s),
      'books', (SELECT coalesce(json_agg(json_build_object('item', item_id, 'skill', skill_id, 'name', display_name)
                 ORDER BY skill_id, item_id), '[]') FROM gameplay_skill_book_definitions WHERE revision = %(pub)s),
      'talents', (SELECT coalesce(json_agg(json_build_object('id', id, 'class', class_id, 'order', tree_order, 'name', name,
                  'prefix', prefix_id, 'prefix_rank', required_prefix_rank, 'total_rank', required_total_rank,
                  'effect', effect_type, 'value', effect_value, 'percent', is_percent) ORDER BY class_id, tree_order, id), '[]')
                 FROM gameplay_talent_definitions WHERE revision = %(pub)s),
      'medusa_titles', (SELECT coalesce(json_agg(json_build_object('title', title, 'id', client_title_id, 'name', display_name,
                  'patk', physical_attack_basis_points, 'matk', magic_attack_basis_points, 'pdef', physical_defense_basis_points,
                  'mdef', magic_defense_basis_points) ORDER BY title), '[]') FROM medusa_reward_title_definitions)
    )""" % {"pub": pub}, timeout=120)
    for s in d["skills"]:
        s["classes"] = [int(x) for x in (s.get("classes") or [])]
    with _LOCK:
        _CACHE[key] = d
    return d


def skill_index(ctx):
    c = content(ctx)
    return {s["id"]: s for s in c["skills"]}


def families_for(ctx, profession):
    """{base_name: [skills of that family the class may have, by level]} (class_ids contains the class)."""
    fam = {}
    for s in content(ctx)["skills"]:
        if profession in s["classes"]:
            fam.setdefault(s["base"] or ("skill %d" % s["id"]), []).append(s)
    for v in fam.values():
        v.sort(key=lambda s: (s["level"] or 0, s["id"]))
    return fam


def talents_for(ctx, profession):
    """The class's talents in the order the server sends them (and the client lays them out)."""
    rows = [t for t in content(ctx)["talents"] if t["class"] == profession]
    if profession == 1:
        by = {t["id"]: t for t in rows}
        return [by[i] for i in CHAMPION_TALENTS if i in by]
    return sorted(rows, key=lambda t: (t["order"], t["id"]))


# ---------------------------------------------------------------- reading one character
def char_state(ctx, cid):
    rb = ctx.need_rb()
    cid = int(cid)
    d = rb.pg.json("""(SELECT json_build_object(
      'base', json_build_object('id', c.id, 'name', c.name, 'account_id', c.account_id, 'profession', c.profession,
              'camp', c.camp, 'level', c.fighter_job_lv, 'SkillPoint', c."SkillPoint", 'SkillExp', c."SkillExp",
              'zodiac_type', c.zodiac_type, 'zodiac_level', c.zodiac_level, 'zodiac_energy', c.zodiac_energy,
              'selected_title_id', c.selected_title_id, 'medusa_reward_revision', c.medusa_reward_revision,
              'Stone', c."Stone", 'lifecycle_state', c.lifecycle_state),
      'skills', (SELECT coalesce(json_agg(json_build_object('skill_id', s.skill_id, 'skill_level', s.skill_level,
              'source', s.source, 'acquired_at', s.acquired_at) ORDER BY s.skill_id), '[]') FROM character_skills s WHERE s.user_id = c.id),
      'talents', (SELECT coalesce(json_agg(json_build_object('talent_id', t.talent_id, 'rank', t.rank,
              'outbox_revision', t.outbox_revision, 'updated_at', t.updated_at) ORDER BY t.talent_id), '[]')
              FROM character_talents t WHERE t.user_id = c.id),
      'grids', (SELECT coalesce(json_agg(json_build_object('grid', g.grid_index, 'level', g.level,
              'kind', g.selected_skill_id, 'updated_at', g.updated_at) ORDER BY g.grid_index), '[]')
              FROM character_zodiac_skill_grids g WHERE g.user_id = c.id),
      'quests', (SELECT coalesce(json_agg(json_build_object('quest_id', q.quest_id, 'state', q.state, 'progress', q.progress,
              'accepted_at', q.accepted_at) ORDER BY q.state, q.quest_id), '[]') FROM character_quests q WHERE q.character_id = c.id),
      'daily', (SELECT coalesce(json_agg(json_build_object('quest_id', q.quest_id, 'reset_day', q.reset_day,
              'completions', q.completions) ORDER BY q.quest_id), '[]') FROM character_quest_daily q WHERE q.character_id = c.id),
      'appraisal', (SELECT json_build_object('bonus_basis_points', a.bonus_basis_points, 'appraised_at', a.appraised_at)
              FROM character_quest_appraisal a WHERE a.character_id = c.id),
      'titles_medusa', (SELECT coalesce(json_agg(json_build_object('title', o.title, 'title_id', o.title_id,
              'acquired_at', o.acquired_at) ORDER BY o.title_id), '[]') FROM character_title_ownership o WHERE o.character_id = c.id),
      'titles_atlantis', (SELECT coalesce(json_agg(json_build_object('title_id', o.title_id, 'acquired_at', o.acquired_at)
              ORDER BY o.title_id), '[]') FROM atlantis_character_title_ownership o WHERE o.character_id = c.id),
      'titles_wonderland', (SELECT coalesce(json_agg(json_build_object('title_id', o.title_id, 'island', o.source_island_number,
              'acquired_at', o.acquired_at) ORDER BY o.title_id), '[]') FROM wonderland_character_title_ownership o WHERE o.character_id = c.id)
    ) FROM character_base c WHERE c.id = %d)""" % cid)
    if not d:
        raise NotFound("no reborn character #%d" % cid)
    d['title_grants_support'] = title_grants.support(ctx)
    d['titles_admin'] = title_grants.rows(ctx, cid) if d['title_grants_support']['available'] else []
    return d


def owned_titles(st):
    """Match the running reader; legacy binaries omit Wonderland/admin grants."""
    sources = [st['titles_medusa'], st['titles_atlantis']]
    if st.get('title_grants_support', {}).get('available'):
        sources += [st['titles_wonderland'], st['titles_admin']]
    return sorted({t['title_id'] for rows in sources for t in rows})


def learned_family_ids(st):
    return {int(s["skill_id"]) for s in st["skills"]}


def grid_rows(st):
    g = {i: {"grid": i, "level": 0, "kind": -1, "stored": False} for i in range(GRID_COUNT)}
    for r in st["grids"]:
        g[int(r["grid"])] = {"grid": int(r["grid"]), "level": int(r["level"]), "kind": int(r["kind"]), "stored": True}
    return g


def grid_problems(profession, grids, learned):
    """The server's selection rules over a whole grid state {g: {level, kind}} -> [(grid, message)]."""
    out = []
    for g in range(GRID_COUNT):
        lv, kind = int(grids[g]["level"]), int(grids[g]["kind"])
        if not 0 <= lv <= GRID_LEVEL_MAX:
            out.append((g, "level must be 0-%d" % GRID_LEVEL_MAX))
        if kind == -1:
            continue
        if lv == 0:
            out.append((g, "an inactive grid (level 0) cannot hold a skill: give it level 1 or more first"))
        if kind not in ZODIAC_KIND_CLASS:
            out.append((g, "Kind %d is not a zodiac skill (MagicMini.xml)" % kind))
            continue
        if kind // 10000 != grid_group(g):
            out.append((g, "grid %d takes Kinds %s; Kind %d belongs to the other group" % (
                g + 1, "10000-19999" if grid_group(g) == 1 else "20000-29999", kind)))
        if not grid_is_defence(g):
            if ZODIAC_KIND_CLASS[kind] != profession:
                out.append((g, "attack grids take only the character's own class skills (Kind %d is a %s skill)" % (
                    kind, CLASS_NAMES.get(ZODIAC_KIND_CLASS[kind], "?"))))
            elif not learned & set(kind_family_ids(kind)):
                out.append((g, "attack grids need the skill learned: the server looks for skill ids %d-%d in the skill "
                               "book" % (kind_family_ids(kind)[0], kind_family_ids(kind)[-1])))
        row = g // GRIDS_PER_ROW * GRIDS_PER_ROW
        for o in range(row, row + GRIDS_PER_ROW):
            if o < g and int(grids[o]["kind"]) == kind:
                out.append((g, "Kind %d is already in grid %d of the same row (a skill only once per row)" % (kind, o + 1)))
    return out


@M.get(r"sheet/(\d+)")
def sheet(ctx, cid):
    """GET /api/m/skills/sheet/<cid> - everything the page shows for one character."""
    st = char_state(ctx, cid)
    b = st["base"]
    prof = int(b["profession"])
    fams = families_for(ctx, prof)
    have = {int(s["skill_id"]): s for s in st["skills"]}
    books = {}
    for x in content(ctx)["books"]:
        books.setdefault(x["skill"], []).append(x["item"])
    families = []
    for base, members in fams.items():
        cur = [have[s["id"]] for s in members if s["id"] in have]
        families.append({"base": base, "general": len(members[0]["classes"]) > 1,
                         "skills": [dict(s, books=books.get(s["id"], [])) for s in members],
                         "current": cur[0]["skill_id"] if cur else None, "current_rows": [c["skill_id"] for c in cur],
                         "minlv": min((s["minlv"] or 1) for s in members)})
    families.sort(key=lambda f: (f["general"], f["minlv"], f["base"]))
    fam_ids = {s["id"] for f in fams.values() for s in f}
    foreign = [dict(s, why="not a skill of this class in the pinned gameplay content: the server does not load it")
               for i, s in have.items() if i not in fam_ids]
    tal = talents_for(ctx, prof)
    ranks = {int(t["talent_id"]): t for t in st["talents"]}
    talents = [dict(t, node=n, rank=int((ranks.get(t["id"]) or {}).get("rank") or 0),
                    stored=t["id"] in ranks, outbox_revision=(ranks.get(t["id"]) or {}).get("outbox_revision"))
               for n, t in enumerate(tal)]
    tal_ids = {t["id"] for t in tal}
    stray_talents = [t for i, t in ranks.items() if i not in tal_ids]
    learned = learned_family_ids(st)
    grids = grid_rows(st)
    problems = {}
    for g, msg in grid_problems(prof, grids, learned):
        problems.setdefault(str(g), []).append(msg)
    kinds = []
    for k, c in sorted(ZODIAC_KIND_CLASS.items()):
        kinds.append({"kind": k, "class": c, "group": k // 10000, "family": kind_family_ids(k),
                      "learned": bool(learned & set(kind_family_ids(k))) and c == prof})
    rules = quest_rules()["quests"]
    qrows = []
    for q in st["quests"]:
        r = rules.get(str(q["quest_id"])) or {}
        qrows.append(dict(q, counters=[(int(q["progress"]) >> (COUNTER_BITS * s)) & 0xFFFF for s in range(COUNTER_SLOTS)],
                          known=bool(r), camp=r.get("camp")))
    return {
        "character": b, "class_name": CLASS_NAMES.get(prof, str(prof)), "camp_name": CAMP_KEYS.get(int(b["camp"]), "?"),
        "writable": bool(ctx.rb.writable), "database": ctx.rb.db, "revision": content(ctx)["revision"],
        "skills": {"families": families, "rows": st["skills"], "foreign": foreign},
        "talents": {"list": talents, "stray": stray_talents, "rank_cap": TALENT_RANK_CAP, "client_cap": CLIENT_TALENT_CAP,
                    "table": TALENT_TABLE},
        "zodiac": {"sign": b["zodiac_type"], "level": b["zodiac_level"], "energy": b["zodiac_energy"],
                   "caps": ZODIAC_STORAGE, "grids": [grids[i] for i in range(GRID_COUNT)], "grid_gold": GRID_GOLD,
                   "problems": problems, "kinds": kinds, "grid_level_max": GRID_LEVEL_MAX},
        "titles": {"selected": b["selected_title_id"], "owned": owned_titles(st), "medusa": st["titles_medusa"],
                   "atlantis": st["titles_atlantis"], "wonderland": st["titles_wonderland"],
                   "medusa_defs": content(ctx)["medusa_titles"],
                   "admin": st['titles_admin'], "grant_support": st['title_grants_support'],
                   "grant_catalogue": title_grants.catalogue(ctx) if st['title_grants_support']['available'] else [],
                   "why_read_only": "Owned titles are evidence rows: each one is tied by a foreign key to the instance "
                                    "run that earned it, so those rows stay intact. Administrator grants are separate and can be revoked. "
                                    "Pick which owned title is shown with 'Shown title'."},
        "quests": {"rows": qrows, "daily": st["daily"], "appraisal": st["appraisal"], "max_carried": MAX_CARRIED_QUESTS,
                   "quest_day": quest_day().isoformat(), "appraisal_default": APPRAISAL_DEFAULT_BP},
    }


@M.get("questrules")
def questrules(ctx):
    """GET /api/m/skills/questrules - the server's quest roster (camp, main line, prerequisites, objectives, daily rules)."""
    return quest_rules()


@M.get("talenttable")
def talenttable(ctx):
    return {"table": TALENT_TABLE, "rank_cap": TALENT_RANK_CAP, "client_cap": CLIENT_TALENT_CAP}


# ---------------------------------------------------------------- write helpers
def _int(v, lo, hi, what):
    return common.to_int(v, lo, hi, what)


def _expected_map(v, what):
    """The 'expected' object of a request: a {key: value} map (or nothing). Anything else is a 400, not a crash."""
    if v in (None, ""):
        return {}
    if not isinstance(v, dict):
        raise Bad("expected must be {%s: value shown}" % what)
    return v


def _conflict_block(checks):
    """DO-block: each check = (sql condition that must be TRUE, message). A false one aborts with gwadmin-conflict (409)."""
    if not checks:
        return None
    body = "\n".join("  IF NOT (%s) THEN RAISE EXCEPTION 'gwadmin-conflict: %s'; END IF;" % (
        cond, msg.replace("'", "''").replace("%", "%%")[:200]) for cond, msg in checks)
    return "DO $gwsk$\nBEGIN\n%s\nEND\n$gwsk$" % body


def _cid_name(ctx, cid):
    st = char_state(ctx, cid)
    return st, st["base"]["name"]


# ---------------------------------------------------------------- skills
@M.post(r"skills/(\d+)")
def set_skills(ctx, cid):
    """POST /api/m/skills/skills/<cid>  {set: {base_name: skill_id | null}, expected: {base_name: skill_id | null}}
    Learn / change level / forget whole skill families in one transaction (the server's book rule: one row per family)."""
    cid = int(cid)
    want = ctx.body.get("set") or {}
    exp = _expected_map(ctx.body.get("expected"), "skill family")
    if not isinstance(want, dict) or not want:
        raise Bad("set must be {skill family: skill id or null}")
    for base, e in exp.items():
        if e not in (None, ""):
            exp[base] = _int(e, 0, 2 ** 31 - 1, "expected skill id of %s" % base)
    st = char_state(ctx, cid)
    prof = int(st["base"]["profession"])
    fams = families_for(ctx, prof)
    plan, labels = [], []
    for base, sid in want.items():
        if base not in fams:
            raise Refused("%s is not a skill family of a %s in the server's pinned skill content, so the server would not "
                          "load it" % (base, CLASS_NAMES.get(prof, "?")))
        members = {s["id"]: s for s in fams[base]}
        if sid not in (None, ""):
            sid = _int(sid, 0, 2 ** 31 - 1, "skill id")
            if sid not in members:
                raise Bad("skill %d is not in the %s family (%s)" % (sid, base, ", ".join(str(i) for i in members)))
        else:
            sid = None
        plan.append((base, sorted(members), sid, members.get(sid)))
        labels.append("%s %s" % (base, ("Lv %s (#%d)" % (members[sid]["level"] or 1, sid)) if sid is not None else "forgotten"))
    # An attack grid (0-7) may only hold a skill the character has learned (ZodiacSkillGridSelection SkillNotLearned).
    # Forgetting a family that an attack grid still uses would leave a grid state the game never allows: refuse it.
    learned_after = set(learned_family_ids(st))
    for base, ids, sid, _ in plan:
        learned_after -= set(ids)
        if sid is not None:
            learned_after.add(sid)
    grids_now = grid_rows(st)
    stuck = [(g, grids_now[g]["kind"]) for g in range(GRID_COUNT // 2)
             if grids_now[g]["kind"] != -1 and ZODIAC_KIND_CLASS.get(grids_now[g]["kind"]) == prof
             and learned_family_ids(st) & set(kind_family_ids(grids_now[g]["kind"]))
             and not learned_after & set(kind_family_ids(grids_now[g]["kind"]))]
    if stuck:
        raise Bad("zodiac attack grid%s %s still use%s %s: an attack grid may only hold a learned skill, so clear %s on "
                  "the Zodiac tab before forgetting the skill" % (
                      "s" if len(stuck) > 1 else "", ", ".join(str(g + 1) for g, _ in stuck), "" if len(stuck) > 1 else "s",
                      ", ".join(sorted({"Kind %d" % k for _, k in stuck})), "them" if len(stuck) > 1 else "it"))

    def build(d):
        out, checks = [], []
        have = {int(r["skill_id"]) for r in d["character_skills"]}
        for base, ids, sid, s in plan:
            cur = sorted(have & set(ids))
            if base in exp:
                e = exp[base]
                cond = ("NOT EXISTS (SELECT 1 FROM character_skills WHERE user_id = %d AND skill_id IN (%s))" % (cid, ",".join(map(str, ids)))
                        if e in (None, "") else
                        "EXISTS (SELECT 1 FROM character_skills WHERE user_id = %d AND skill_id = %d)" % (cid, int(e)))
                checks.append((cond, "character_skills %s changed since you opened it (expected %s)" % (base, e)))
            if cur == ([sid] if sid is not None else []):
                continue
            if cur:
                out.append("DELETE FROM character_skills WHERE user_id = %d AND skill_id IN (%s)" % (cid, ",".join(map(str, ids))))
            if sid is not None:
                out.append("INSERT INTO character_skills (user_id, skill_id, skill_level, source) VALUES (%d, %d, %d, 'gwadmin-web')"
                           % (cid, sid, max(1, int(s["level"] or 1))))
        if not out:
            raise Bad("nothing to change: the skill book already looks like that")
        blk = _conflict_block(checks)
        return ([blk] if blk else []) + out
    r = ctx.char_write(cid, "skills: " + "; ".join(labels)[:300], build, fields=["character_skills.*"])
    r["mode"] = "database"
    return r


# ---------------------------------------------------------------- talents
@M.post(r"talents/(\d+)")
def set_talents(ctx, cid):
    """POST /api/m/skills/talents/<cid>  {ranks: {talent_id: rank 0-100}, expected: {talent_id: rank shown}}"""
    cid = int(cid)
    ranks = ctx.body.get("ranks") or {}
    exp = _expected_map(ctx.body.get("expected"), "talent id")
    if not isinstance(ranks, dict) or not ranks:
        raise Bad("ranks must be {talent id: rank}")
    st = char_state(ctx, cid)
    prof = int(st["base"]["profession"])
    allowed = {t["id"]: t for t in talents_for(ctx, prof)}
    want = {}
    for k, v in ranks.items():
        tid = _int(k, 0, 2 ** 31 - 1, "talent id")
        if tid not in allowed:
            raise Refused("talent %d is not a %s talent the server loads (it sends only the class's own talents%s)" % (
                tid, CLASS_NAMES.get(prof, "?"), "; Champion's Might #68 is never sent" if prof == 1 else ""))
        want[tid] = _int(v, 0, TALENT_RANK_CAP, "rank of %s" % allowed[tid]["name"])

    def build(d):
        cur = {int(r["talent_id"]): int(r["rank"]) for r in d["character_talents"]}
        out, checks = [], []
        for tid, rk in sorted(want.items()):
            if str(tid) in exp or tid in exp:
                e = _int(exp.get(str(tid), exp.get(tid)), 0, 32767, "expected rank")
                checks.append(("COALESCE((SELECT rank FROM character_talents WHERE user_id = %d AND talent_id = %d), 0) = %d" % (cid, tid, e),
                               "character_talents %s changed since you opened it (expected rank %d)" % (allowed[tid]["name"], e)))
            if cur.get(tid, 0) == rk and (tid in cur or rk == 0):
                continue
            out.append("INSERT INTO character_talents (user_id, talent_id, rank, updated_at) VALUES (%d, %d, %d, now()) "
                       "ON CONFLICT (user_id, talent_id) DO UPDATE SET rank = EXCLUDED.rank, updated_at = now(), "
                       "outbox_revision = character_talents.outbox_revision + 1" % (cid, tid, rk))
        if not out:
            raise Bad("nothing to change: the talents already have those ranks")
        blk = _conflict_block(checks)
        return ([blk] if blk else []) + out
    label = ", ".join("%s %d" % (allowed[t]["name"], r) for t, r in sorted(want.items()))
    r = ctx.char_write(cid, "talents: " + label[:300], build, fields=["character_talents.*"])
    r["mode"] = "database"
    over = [allowed[t]["name"] for t, r_ in want.items() if r_ > CLIENT_TALENT_CAP]
    if over:
        r["warning"] = "Above rank %d: the server uses it, but the admin's client shows at most %d (%s)." % (
            CLIENT_TALENT_CAP, CLIENT_TALENT_CAP, ", ".join(over))
    return r


# ---------------------------------------------------------------- zodiac grids
@M.post(r"grids/(\d+)")
def set_grids(ctx, cid):
    """POST /api/m/skills/grids/<cid>  {grids: {grid 0-15: {level 0-50, kind Kind | -1}}, expected: {grid: {level, kind}}}"""
    cid = int(cid)
    req = ctx.body.get("grids") or {}
    exp = _expected_map(ctx.body.get("expected"), "grid")
    if any(not isinstance(v, dict) for v in exp.values()):
        raise Bad("expected must be {grid: {level, kind}}")
    if not isinstance(req, dict) or not req:
        raise Bad("grids must be {grid: {level, kind}}")
    st = char_state(ctx, cid)
    prof = int(st["base"]["profession"])
    state = grid_rows(st)
    new = {}
    for k, v in req.items():
        g = _int(k, 0, GRID_COUNT - 1, "grid")
        if not isinstance(v, dict):
            raise Bad("grid %d: give {level, kind}" % g)
        lv = _int(v.get("level", state[g]["level"]), 0, GRID_LEVEL_MAX, "grid %d level" % (g + 1))
        kind = _int(v.get("kind", state[g]["kind"]), -1, 29999, "grid %d skill Kind" % (g + 1))
        if lv == 0:
            kind = -1                   # an inactive grid holds no skill
        new[g] = {"level": lv, "kind": kind}
    final = {g: dict(state[g], **new.get(g, {})) for g in range(GRID_COUNT)}
    # Refuse every problem the change creates: one on an edited grid, and one it causes on another grid (a Kind put
    # into grid 1 that grid 2 of the same row already holds is reported on grid 2). Problems already stored stay shown.
    learned = learned_family_ids(st)
    before = set(grid_problems(prof, state, learned))
    bad = [(g, m) for g, m in grid_problems(prof, final, learned) if g in new or (g, m) not in before]
    if bad:
        raise Bad("; ".join("grid %d: %s" % (g + 1, m) for g, m in bad))

    def build(d):
        out, checks = [], []
        cur = {int(r["grid_index"]): r for r in d["character_zodiac_skill_grids"]}
        for g, v in sorted(new.items()):
            if str(g) in exp or g in exp:
                e = exp.get(str(g), exp.get(g)) or {}
                el, ek = _int(e.get("level", 0), 0, 32767, "expected level"), _int(e.get("kind", -1), -1, 2 ** 31 - 1, "expected kind")
                checks.append(("COALESCE((SELECT level FROM character_zodiac_skill_grids WHERE user_id = %d AND grid_index = %d), 0) = %d AND "
                               "COALESCE((SELECT selected_skill_id FROM character_zodiac_skill_grids WHERE user_id = %d AND grid_index = %d), -1) = %d"
                               % (cid, g, el, cid, g, ek), "grid %d changed since you opened it (expected level %d)" % (g + 1, el)))
            c = cur.get(g)
            if (c and int(c["level"]) == v["level"] and int(c["selected_skill_id"]) == v["kind"]) or \
               (not c and v["level"] == 0 and v["kind"] == -1):
                continue
            out.append("INSERT INTO character_zodiac_skill_grids (user_id, grid_index, level, selected_skill_id, updated_at) "
                       "VALUES (%d, %d, %d, %d, now()) ON CONFLICT (user_id, grid_index) DO UPDATE SET level = EXCLUDED.level, "
                       "selected_skill_id = EXCLUDED.selected_skill_id, updated_at = now()" % (cid, g, v["level"], v["kind"]))
        if not out:
            raise Bad("nothing to change: the grids already look like that")
        blk = _conflict_block(checks)
        return ([blk] if blk else []) + out
    label = ", ".join("grid %d Lv %d%s" % (g + 1, v["level"], (" Kind %d" % v["kind"]) if v["kind"] != -1 else "")
                      for g, v in sorted(new.items()))
    r = ctx.char_write(cid, "zodiac grids: " + label[:300], build, fields=["character_zodiac_skill_grids.*"])
    r["mode"] = "database"
    return r


# ---------------------------------------------------------------- character_base fields of this page (live or database)
BASE_FIELDS = ("zodiac_type", "zodiac_level", "zodiac_energy", "SkillPoint", "SkillExp", "selected_title_id")


@M.post(r"base/(\d+)")
def set_base(ctx, cid):
    """POST /api/m/skills/base/<cid>  {values: {zodiac_type, zodiac_level, zodiac_energy, SkillPoint, SkillExp,
    selected_title_id}, expected: {column: shown value}} - checked against the server's rules, then the shared
    live-or-database path (talent points and zodiac energy go live on a round-2 server)."""
    cid = int(cid)
    vals = ctx.body.get("values") or {}
    if not isinstance(vals, dict) or not vals:
        raise Bad("values must be {column: value}")
    unknown = [k for k in vals if k not in BASE_FIELDS]
    if unknown:
        raise Bad("this page edits only %s (not %s)" % (", ".join(BASE_FIELDS), ", ".join(unknown)))
    st = char_state(ctx, cid)
    b = st["base"]
    clean = {}
    if "zodiac_type" in vals:
        clean["zodiac_type"] = _int(vals["zodiac_type"], 0, ZODIAC_SIGNS - 1, "zodiac sign")
    if "zodiac_level" in vals:
        clean["zodiac_level"] = _int(vals["zodiac_level"], 1, ZODIAC_LEVEL_MAX, "zodiac level")
    if "zodiac_energy" in vals:
        lvl = clean.get("zodiac_level", b["zodiac_level"])
        cap = zodiac_cap(lvl)
        clean["zodiac_energy"] = _int(vals["zodiac_energy"], 0, cap, "zodiac energy (storage cap of zodiac level %s)" % lvl)
    elif "zodiac_level" in vals and int(b["zodiac_energy"]) > zodiac_cap(clean["zodiac_level"]):
        raise Bad("zodiac energy {:,} is above the storage cap of level {} ({:,}): lower the energy too".format(
            int(b["zodiac_energy"]), clean["zodiac_level"], zodiac_cap(clean["zodiac_level"])))
    if "SkillPoint" in vals:
        clean["SkillPoint"] = _int(vals["SkillPoint"], 0, 2 ** 31 - 1, "talent points")
    if "SkillExp" in vals:
        clean["SkillExp"] = _int(vals["SkillExp"], 0, 99, "talent EXP (100 = one talent point)")
    if "selected_title_id" in vals:
        t = _int(vals["selected_title_id"], 0, 2 ** 31 - 1, "shown title")
        if t and t not in owned_titles(st):
            raise Refused("title %d is not owned by %s: select an earned title or add an administrator grant first" % (t, b["name"]))
        clean["selected_title_id"] = t
    exp = _expected_map(ctx.body.get("expected"), "column") or None
    if exp:
        exp = {k: v for k, v in exp.items() if k in clean}
    label = ctx.body.get("label") or None
    r = ctx.set_character_fields(cid, clean, expected=exp or None, label=label)
    return r


# ---------------------------------------------------------------- quests
def _pack(counters):
    v = 0
    for s, c in enumerate(counters[:COUNTER_SLOTS]):
        v |= (_int(c, 0, COUNTER_MAX, "objective counter %d" % (s + 1)) & 0xFFFF) << (COUNTER_BITS * s)
    return v


@M.post(r"quests/(\d+)")
def set_quests(ctx, cid):
    """POST /api/m/skills/quests/<cid>  {ops: [{quest, action: accept|progress|complete|drop, counters: [n,...]}],
    expected: {quest: state | null}}"""
    cid = int(cid)
    ops = ctx.body.get("ops") or []
    exp = _expected_map(ctx.body.get("expected"), "quest id")
    for k, e in list(exp.items()):
        if e not in (None, ""):
            exp[k] = _int(e, 0, 1, "expected state of quest %s (0 = in the quest log, 1 = completed)" % k)
    if not isinstance(ops, list) or not ops:
        raise Bad("ops must be a list of {quest, action}")
    if len(ops) > 2000:
        raise Bad("at most 2000 quest changes at once")
    st = char_state(ctx, cid)
    camp = CAMP_KEYS.get(int(st["base"]["camp"]))
    rules = quest_rules()["quests"]
    plan, labels = [], []
    for o in ops:
        if not isinstance(o, dict):
            raise Bad("each op is {quest, action}")
        qid = _int(o.get("quest"), 0, 2 ** 31 - 1, "quest id")
        act = o.get("action")
        if act not in ("accept", "progress", "complete", "drop"):
            raise Bad("quest action must be accept, progress, complete or drop")
        r = rules.get(str(qid))
        if act != "drop":
            if not r:
                raise Refused("quest %d is not in the server's quest roster (StarterQuestChain): the server would ignore it" % qid)
            if r.get("camp") and r["camp"] != camp:
                raise Refused("quest %d is an %s quest and %s is %s: the server never offers or counts it for this camp" % (
                    qid, r["camp"], st["base"]["name"], camp))
        prog = 0
        if act in ("accept", "progress"):
            cnt = o.get("counters") or []
            if not isinstance(cnt, list):
                raise Bad("counters must be a list of up to %d numbers" % COUNTER_SLOTS)
            prog = _pack(cnt)
        plan.append((qid, act, prog))
        labels.append("%s %d" % (act, qid))

    def build(d):
        cur = {int(r["quest_id"]): r for r in d["character_quests"]}
        out, checks = [], []
        for k, e in exp.items():
            qid = _int(k, 0, 2 ** 31 - 1, "quest id")
            cond = ("NOT EXISTS (SELECT 1 FROM character_quests WHERE character_id = %d AND quest_id = %d)" % (cid, qid)
                    if e in (None, "") else
                    "EXISTS (SELECT 1 FROM character_quests WHERE character_id = %d AND quest_id = %d AND state = %d)" % (cid, qid, int(e)))
            checks.append((cond, "character_quests quest %d changed since you opened it" % qid))
        state = {q: int(r["state"]) for q, r in cur.items()}
        for qid, act, prog in plan:
            if act == "drop":
                if qid in state:
                    out.append("DELETE FROM character_quests WHERE character_id = %d AND quest_id = %d" % (cid, qid))
                    state.pop(qid)
                continue
            if act == "progress" and state.get(qid) != 0:
                raise Bad("quest %d is not in the quest log (accept it first)" % qid)
            st_new = 1 if act == "complete" else 0
            pr = 0 if act == "complete" else prog
            r = cur.get(qid)
            if r is not None and int(r["state"]) == st_new and int(r["progress"]) == pr and state.get(qid) == st_new:
                continue
            out.append("INSERT INTO character_quests (character_id, quest_id, state, progress) VALUES (%d, %d, %d, %d) "
                       "ON CONFLICT (character_id, quest_id) DO UPDATE SET state = EXCLUDED.state, progress = EXCLUDED.progress"
                       % (cid, qid, st_new, pr))
            state[qid] = st_new
        carried = sum(1 for v in state.values() if v == 0)
        if carried > MAX_CARRIED_QUESTS:
            raise Bad("that would put %d quests in the quest log; the server carries at most %d" % (carried, MAX_CARRIED_QUESTS))
        if not out:
            raise Bad("nothing to change: the quest log already looks like that")
        blk = _conflict_block(checks)
        return ([blk] if blk else []) + out
    lab = "; ".join(labels[:12]) + (" ... (%d quests)" % len(labels) if len(labels) > 12 else "")
    r = ctx.char_write(cid, "quests: " + lab, build, fields=["character_quests.*"])
    r["mode"] = "database"
    return r


# ---------------------------------------------------------------- main chains and quest history (issue #18)
# Same plan as the server's QuestChainPlanner (S/Domain/World/Content/QuestChainPlanner.cs): the server re-plans and
# refuses unless its ids equal the previewed ones, so a drift between this copy and the server can never apply.
QUEST_CHAIN_OPS = ("complete_main", "reset")
QUEST_CHAIN_SCOPES = ("main", "all")


def counted_main_line(r, camp):
    return bool(r.get("main")) and not r.get("daily") and not r.get("repeat") and r.get("camp") == camp


def main_line_through(rules, camp, level):
    """The camp's main-line quest ids with a starting level up to `level`, plus every prerequisite they name."""
    chosen = {int(r["id"]) for r in rules.values() if counted_main_line(r, camp) and (r.get("minlv") or 1) <= level}
    pending = list(chosen)
    while pending:
        r = rules.get(str(pending.pop()))
        pre = int((r or {}).get("prereq") or 0)
        p = rules.get(str(pre)) if pre else None
        if p and counted_main_line(p, camp) and pre not in chosen:
            chosen.add(pre)
            pending.append(pre)
    return sorted(chosen)


def quest_chain_plan(rules, camp, operation, level, scope, rows):
    """{changed: [ids], conflicts: [ids]} for one character's character_quests rows (offline or online)."""
    completed = {int(r["quest_id"]) for r in rows if int(r["state"]) == 1}
    carried = {int(r["quest_id"]) for r in rows if int(r["state"]) == 0}
    if operation == "complete_main":
        chain = main_line_through(rules, camp, level)
        return {"changed": [q for q in chain if q not in completed and q not in carried],
                "conflicts": [q for q in chain if q in carried]}
    removed = sorted(q for q in completed if scope == "all" or (
        (rules.get(str(q)) or {}).get("main") and (rules.get(str(q)) or {}).get("camp") == camp))
    gone = set(removed)
    conflicts = sorted(q for q in carried if int((rules.get(str(q)) or {}).get("prereq") or 0) in gone)
    return {"changed": removed, "conflicts": conflicts}


@M.post(r"questchain/(\d+)")
def quest_chain(ctx, cid):
    """POST /api/m/skills/questchain/<cid>  {operation: complete_main|reset, level (complete_main), scope: main|all (reset),
    apply: false = preview, true = write; expected: [ids] from the preview (required to apply)}.
    Completing grants no rewards. Online characters go through the server (/admin/quest/chain), offline ones to the database."""
    cid = int(cid)
    op = ctx.body.get("operation")
    if op not in QUEST_CHAIN_OPS:
        raise Bad("operation must be complete_main or reset")
    scope = ctx.body.get("scope") or "main"
    if scope not in QUEST_CHAIN_SCOPES:
        raise Bad("scope must be main or all")
    level = _int(ctx.body.get("level", 1), 1, 200, "level") if op == "complete_main" else 0
    st = char_state(ctx, cid)
    name = st["base"]["name"]
    camp = CAMP_KEYS.get(int(st["base"]["camp"]))
    rules = quest_rules()["quests"]
    plan = quest_chain_plan(rules, camp, op, level, scope, st["quests"])
    what = ("complete %s main line through Lv %d" % (camp, level) if op == "complete_main"
            else "forget completed quests (%s)" % ("main line" if scope == "main" else "all"))
    preview = {"operation": op, "scope": scope, "level": level, "camp": camp, "character": name, "what": what,
               "changed": plan["changed"], "conflicts": plan["conflicts"],
               "rewards": "none: completing this way grants no EXP, TP, Silver, Gold or items" if op == "complete_main" else None}
    if not ctx.body.get("apply"):
        return {"preview": preview}
    exp = ctx.body.get("expected")
    if not isinstance(exp, list) or sorted(_int(x, 1, 2 ** 31 - 1, "expected quest id") for x in exp) != plan["changed"]:
        raise common.Conflict("the quest history changed since the preview; preview again (nothing written)")
    if plan["conflicts"]:
        raise Refused("the quest log holds %s, which this would %s; drop those quests first" % (
            ", ".join(map(str, plan["conflicts"])), "complete" if op == "complete_main" else "leave without their prerequisite"))
    if not plan["changed"]:
        raise Bad("nothing to change")
    if common.online_info(ctx.app, cid).get("online"):
        body = {"operation": op, "scope": scope, "level": level, "expected": plan["changed"]}
        r = ctx.live_call("character.quest_chain", name, body, cid=cid,
                          no_undo="applied by the running server; undo it with the opposite quest edit")
        if r.get("ok"):
            return {"mode": "live", "preview": preview, "result": r["result"], "change": common.public_change(r["change"])}
        # the character logged out between the check and the call: fall through to the database

    def build(d):
        again = quest_chain_plan(rules, camp, op, level, scope, d.get("character_quests") or [])
        if again["changed"] != plan["changed"] or again["conflicts"]:
            raise common.Conflict("the quest history changed since the preview; preview again (nothing written)")
        ids = ", ".join(map(str, plan["changed"]))
        if op == "complete_main":
            return ["INSERT INTO character_quests (character_id, quest_id, state, progress) SELECT %d, q, 1, 0 FROM unnest(ARRAY[%s]) q "
                    "ON CONFLICT (character_id, quest_id) DO NOTHING" % (cid, ids)]
        return ["DELETE FROM character_quests WHERE character_id = %d AND state = 1 AND quest_id IN (%s)" % (cid, ids)]
    r = ctx.char_write(cid, "quests: %s (%d quests)" % (what, len(plan["changed"])), build, fields=["character_quests.*"])
    r["mode"] = "database"
    r["preview"] = preview
    return r


@M.post(r"daily/(\d+)")
def set_daily(ctx, cid):
    """POST /api/m/skills/daily/<cid>  {quest, completions (0 = reset), advanced: true} - today's quota-day counter."""
    cid = int(cid)
    qid = _int(ctx.body.get("quest"), 0, 2 ** 31 - 1, "quest id")
    n = _int(ctx.body.get("completions", 0), 0, 1000, "completions")
    day = quest_day()
    exp = ctx.body.get("expected")
    if n:
        # only a quest the server counts per day, for this character's camp (a reset of an existing row is always fine)
        r = quest_rules()["quests"].get(str(qid))
        if not r:
            raise Refused("quest %d is not in the server's quest roster (StarterQuestChain): the server would ignore its "
                          "daily counter" % qid)
        if not int(r.get("per_day") or 0):
            raise Bad("quest %d has no per-day limit: the server counts completions per day only for daily (1 a day) and "
                      "repeatable (3 a day) quests (StarterQuestChain MaxCompletionsPerDay)" % qid)
        st = char_state(ctx, cid)
        camp = CAMP_KEYS.get(int(st["base"]["camp"]))
        if r.get("camp") and r["camp"] != camp:
            raise Refused("quest %d is an %s quest and %s is %s: the server never counts it for this camp" % (
                qid, r["camp"], st["base"]["name"], camp))

    def build(d):
        cur = {int(r["quest_id"]): r for r in d.get("character_quest_daily", [])}
        checks = []
        if exp is not None:
            checks.append(("COALESCE((SELECT completions FROM character_quest_daily WHERE character_id = %d AND quest_id = %d AND reset_day = %s), 0) = %d"
                           % (cid, qid, lit(day.isoformat()), _int(exp, 0, 2 ** 31 - 1, "expected")),
                           "character_quest_daily quest %d changed since you opened it" % qid))
        if n == 0:
            if qid not in cur:
                raise Bad("quest %d has no daily counter to reset" % qid)
            out = ["DELETE FROM character_quest_daily WHERE character_id = %d AND quest_id = %d" % (cid, qid)]
        else:
            out = ["INSERT INTO character_quest_daily (character_id, quest_id, reset_day, completions) VALUES (%d, %d, %s, %d) "
                   "ON CONFLICT (character_id, quest_id) DO UPDATE SET reset_day = EXCLUDED.reset_day, completions = EXCLUDED.completions"
                   % (cid, qid, lit(day.isoformat()), n)]
        blk = _conflict_block(checks)
        return ([blk] if blk else []) + out
    r = ctx.char_write(cid, "daily quest %d: %s" % (qid, "reset" if n == 0 else "%d done on %s" % (n, day)), build,
                       fields=["character_quest_daily.*"], advanced=bool(ctx.body.get("advanced")))
    r["mode"] = "database"
    return r


@M.post(r"appraisal/(\d+)")
def set_appraisal(ctx, cid):
    """POST /api/m/skills/appraisal/<cid>  {bonus_basis_points | null (remove), advanced: true}"""
    cid = int(cid)
    v = ctx.body.get("bonus_basis_points")
    bp = None if v in (None, "") else _int(v, 1, 1_000_000, "bonus (basis points, 1000 = +10 %)")

    def build(d):
        cur = (d.get("character_quest_appraisal") or [None])[0] if d.get("character_quest_appraisal") else None
        if bp is None:
            if not cur:
                raise Bad("the character has no quest EXP appraisal to remove")
            return ["DELETE FROM character_quest_appraisal WHERE character_id = %d" % cid]
        return ["INSERT INTO character_quest_appraisal (character_id, bonus_basis_points) VALUES (%d, %d) "
                "ON CONFLICT (character_id) DO UPDATE SET bonus_basis_points = EXCLUDED.bonus_basis_points" % (cid, bp)]
    r = ctx.char_write(cid, "quest EXP appraisal: %s" % ("removed" if bp is None else "+%s %%" % (bp / 100)), build,
                       fields=["character_quest_appraisal.*"], advanced=bool(ctx.body.get("advanced")))
    r["mode"] = "database"
    return r


@M.post(r"title-grants/(\d+)")
def set_title_grants(ctx, cid):
    return title_grants.write(ctx, cid)
