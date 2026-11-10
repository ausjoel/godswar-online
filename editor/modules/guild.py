"""modules/guild.py - the "Guild" editor: guilds (create, rename, disband), members and duties, the leader, funds,
contribution, buildings and their levels, altar offering points, the guild Double-EXP boost, consistency checks and
repairs, the faction-area EXP control (world bosses), and what the server does not implement yet. Contract:
../MODULES.md. Owned by the guild module task (2026-10-28).

How the reborn server keeps guilds (src/Godswar.Server, commit b85a1244):
  * guilds (identity + funds), guild_members (one row per character, duty 1-6), guild_buildings (slot = building type,
    level 1..guild_building_types.maximum_level), guild_altar_worship (offering points per member per altar).
    State/DatabaseMigrations/PostgresSchemaMigrationCatalog.Guild*.cs.
  * The character keeps its own copy: character_base.consortia (guild id), consortia_job and guild_duty (= duty) and
    consortia_contribute (contribution points). PostgresGuildStore.Duties.cs writes them in the SAME transaction as
    guild_members, always; leaving clears all four (RemoveMemberAsync). Every write here does the same, and a guard
    (_guard_sql) refuses to COMMIT - and refuses an Undo - when they would disagree.
  * The guild window re-reads the guild on every open (TryReadGuildAsync), but each online member's panel status and
    altar bonus are cached in the session (GuildPanelStatusCache), so every member of a guild must be offline for an
    edit (write_rules.json "guilds": online -> refuse). The round-2 server has no guild endpoint; when it can kick, the
    page offers "Kick" for the online members.
  * Things the game does not do and the editor can: hand over the leadership (GameClientHandler.GuildMembers.cs:
    "Transferring guild leadership is not implemented"), disband, rename, raise the guild level (stuck at 1 in game),
    set funds, set building levels without paying (buildings are free in game anyway).
Writes go through ctx.rows_write: backup, one locked transaction (FOR UPDATE OF c, a for every member + in-transaction
online re-check), the guard, change log and Undo.

Adversarial check (task verify:guild, 2026-10-28, copy godswar_ed_vguild, round-2 instance 60649-60651) fixed here:
  * faction-area Undo: the glossary marks every faction_area_experience_control column "derived", so Undo of an update
    restored nothing and still said "identical", and a boss kill since was never seen; the scope now ignores nothing;
  * two "+ Add member" at once could both pass the member-limit check (3 of 2); it is re-checked under the write lock
    and inside the transaction;
  * Repair could never succeed while a guild had two problems (each repair's guard tripped on the other); Repair now
    guards only the character it repairs;
  * "false" as text was read as yes (bool("false")); bad expected / expected_level values gave HTTP 500;
  * altar Undo now puts back the drain clock (settled_at) with the points;
  * duty names are the English client's own (Text/Message.dat G1-G6: Member, Insider, Elite, Director, Vice-Leader,
    Leader), not translations of the Chinese table.
"""
import datetime
import math
import re
import uuid

from . import common
from .common import Bad, Refused, NotFound, Conflict, OnlineRefused, lit

M = common.module(
    "guild", "Guild",
    icon={"sprite": 1063},          # catalog-icons.png tile: Guild Footstone
    order=50,
    status="ready",
    description="Guilds and factions: create, rename or disband a guild, members and duties, leader, funds, buildings, "
                "altar offerings, the guild Double-EXP boost, world-boss faction areas (guild edits: every member offline).",
    needs_character=False,
)

R = common.R

# ----------------------------------------------------------------------------------------------- the client's rules
I32 = 2 ** 31 - 1
# The duty names the English game client itself prints: Text/Message.dat keys G1..G6 (the target window draws the duty as
# "G%d", Client-RE/msgcatalog-251.json). Consortia_Job.ini / Consortia.dat hold the Chinese originals (shown in the help).
DUTY_CLIENT = {1: "Member", 2: "Insider", 3: "Elite", 4: "Director", 5: "Vice-Leader", 6: "Leader"}
DUTY_HELP = {
    1: "Member (Chinese table: 见习会员, probationary member): a player who joins by application starts here.",
    2: "Insider (Chinese table: 会员, full member).",
    3: "Elite (精英).",
    4: "Director (理事): may set the 'refuse applications' switch, give duties below their own and remove members below them.",
    5: "Vice-Leader (副会长, the Deputy). The official game allowed 4 per guild; reborn has no cap.",
    6: "Leader (会长): exactly one, the guild's lord (guilds.lord_character_id). Change it with 'Make leader'.",
}
# altar = building type 10..19: the attribute the server pays (Application/Guilds/GuildAltarWorshipBonus.cs AttributeOf)
# and the raw WorshipImpactType it must carry in guild_building_levels (ExpectedType) or it pays nothing.
ALTAR_ATTR = {10: "Max MP", 11: "HP recovery", 12: "Max HP", 13: "Physical attack", 14: "Hit", 15: "Physical defence",
              16: "Magic defence", 17: "Damage absorb", 18: "Magic attack", 19: "Dodge"}
ALTAR_TYPE = {10: 1, 11: 2, 12: 0, 13: 4, 14: 8, 15: 5, 16: 7, 17: 14, 18: 6, 19: 9}
POINTS_PER_STEP, MAX_STEPS, PCT_PER_STEP = 50_000, 20, 7.5            # GuildAltarWorshipBonusPolicy
DRAIN = ((800_000, 8_000), (600_000, 5_000), (300_000, 1_500), (0, 600))  # GuildAltarDecayPolicy: (> floor, per hour)
ROSTER_SHOWN = 20                     # PacketBuilder.Guild.cs GuildMemberCapacity: the window lists 20 members
NAME_MAX, PROC_MAX, PROC_WIRE = 16, 150, 63   # client Consortia.xml InputEdt MaxText 16 / 150; base info carries 64 bytes
GUILD_BOOST_STATUS, GUILD_BOOST_KIND = 1007, 100   # ExperienceStatusIds.GuildDoubleExperience16Hours, Kinds.Guild

# Systems around guilds and social play: what the reborn server does (sources: the reborn source above and
# GodsWar-2.51/Official-Site/SCOPE.md + systems.json, the official-scope survey of 2026-10-28).
SYSTEMS = [
    {"name": "Guild founding, ranks, members", "state": "partial",
     "server": "Founding at the Guild Registrar costs a Guild Stone (item 4100); join by application (duty 1, Member), duties 1-6, "
               "remove, leave. No leadership hand-over, a flat 100-member cap, no Vice-Leader cap.",
     "official": "40 members at founding, houses raise it to 100; 4 Vice-Leaders; the Lord can hand over.",
     "editor": "Create without a Guild Stone, add/remove members, any duty, Make leader, member limit 1-10,000."},
    {"name": "Guild level and buildings", "state": "partial",
     "server": "Build / upgrade / remove at the altar NPC for free; the guild level never rises (stays 1).",
     "official": "Each level costs guild Silver and gems (Consortia.xml); the guild level follows the Footstone.",
     "editor": "Guild level 1-12 and every building 1-max, with the client's cost table shown for reference."},
    {"name": "Guild altars (offerings)", "state": "implemented",
     "server": "Offering spends contribution 1:1; each altar pays 7.5% of its base value per 50,000 points (max 150%), "
               "drained hourly 600-8,000 points (reborn's own numbers).",
     "official": "+1% per 1,000 points, cap 1.5x, drain 50-4,000 per hour by balance (Consortia.xml BuildingConsume).",
     "editor": "Offering points per member per altar, with the bonus they buy and the drain."},
    {"name": "Guild donations and funds", "state": "implemented",
     "server": "Donate Silver (1 contribution each) or Gold (100 contribution each) at the altar NPC.",
     "official": "The same.", "editor": "Guild Silver, Gold, gems (bijou); member contributions."},
    {"name": "Guild quests, weekly offerings, guild Double EXP", "state": "partial",
     "server": "Guild quests and the guild Double-EXP benefit are not implemented (status 1007 exists, nothing grants it).",
     "official": "Weekly guild quests; Double EXP bought with guild funds.",
     "editor": "Give every member the [Guild] Double EXP boost (the server's EXP code honours it)."},
    {"name": "Guild war, territory, city war", "state": "missing", "server": "Not implemented.",
     "official": "Guild wars and the Defensive War.", "editor": "Nothing to edit."},
    {"name": "Party", "state": "partial", "server": "Parties work (invite, leader, dissolve) but live only in memory: "
     "nothing is stored, so nothing can be edited. No EXP or quest sharing.",
     "official": "5 players, shared quests.", "editor": "Nothing to edit (not stored)."},
    {"name": "Friends and ignore list", "state": "missing", "server": "Not implemented (client messages 10098-10103 "
     "are not answered); no table.", "official": "Friends, groups, blacklist.", "editor": "Nothing to edit (no table)."},
    {"name": "Enemy list", "state": "client", "server": "Kept by the game client itself (30 entries).",
     "official": "The same.", "editor": "Nothing to edit on the server."},
    {"name": "Mail", "state": "missing", "server": "No mail system and no table.", "official": "-",
     "editor": "Nothing to edit."},
    {"name": "Marriage (Wedded Bliss)", "state": "missing", "server": "Not implemented (10314-10315); Cupid's dialogue only.",
     "official": "Engagement and marriage at Cupid.", "editor": "Nothing to edit."},
    {"name": "Master and apprentice", "state": "missing", "server": "Not implemented (10234-10235).",
     "official": "Mentor links with rewards.", "editor": "Nothing to edit."},
    {"name": "Factions (Athens / Sparta)", "state": "partial", "server": "Camp is fixed at creation; world-boss kills "
     "give the killer's camp an EXP bonus on that map for a while (faction-area control). No faction change.",
     "official": "Faction change, polis reputation.", "editor": "Faction areas (below). The camp itself is on the Character page (read-only)."},
]

# ----------------------------------------------------------------------------------------------- small helpers


def _cat():
    return common.CATALOG.file("guild")


def _labels():
    return common.CATALOG.file("labels")


def _duty_name(d):
    if int(d) in DUTY_CLIENT:
        return DUTY_CLIENT[int(d)]
    v = (_labels().get("guild_duties") or {}).get(str(d))
    return (v.get("name") if isinstance(v, dict) else v) or ("duty %s" % d)


def _ids(xs):
    xs = sorted({int(x) for x in xs})
    return ", ".join(str(x) for x in xs) if xs else "NULL"


def _int(v, lo, hi, what):
    return common.to_int(v, lo, hi, what)


def _flag(v, what):
    """A yes/no value: JSON true/false or 0/1 only. bool("false") is True, so text is refused rather than guessed."""
    if isinstance(v, bool):
        return v
    if isinstance(v, int) and v in (0, 1):
        return bool(v)
    raise Bad("%s must be true or false, got %r" % (what, v))


def _expected_dict(v):
    """The 'expected' object a page sends (the values it showed): absent/null, or {field: value}."""
    if v is None:
        return {}
    if not isinstance(v, dict):
        raise Bad("expected must be an object {field: value the page showed}, got %r" % (v,))
    return v


def _name_ok(name):
    if not isinstance(name, str):
        raise Bad("the guild name must be text")
    n = name.strip()
    if not n:
        raise Bad("the guild name cannot be empty")
    if len(n) > NAME_MAX:
        raise Bad("the guild name can have at most %d characters (the client's name box holds %d)" % (NAME_MAX, NAME_MAX))
    if not re.match(r"^[\x20-\x7e]+$", n) or "'" in n or "\\" in n:
        raise Bad("use letters, digits, spaces and plain punctuation in the guild name (no quote or backslash): the "
                  "server writes the name as ASCII into the guild packets")
    return n


def _proc_ok(p):
    if not isinstance(p, str):
        raise Bad("the proclamation must be text")
    if len(p) > PROC_MAX:
        raise Bad("the proclamation can have at most %d characters (the client's edit box)" % PROC_MAX)
    if not re.match(r"^[\x20-\x7e]*$", p):
        raise Bad("use plain ASCII text in the proclamation: the server sends it as ASCII")
    return p


def _altar_value(levels, btype, lvl):
    """(base value, attribute) an altar at this built level pays from, or (0, None)."""
    if btype not in ALTAR_ATTR or not lvl:
        return 0, None
    row = levels.get((btype, int(lvl)))
    if not row or row["worship_impact_type"] != ALTAR_TYPE[btype]:
        return 0, ALTAR_ATTR[btype]
    return int(row["worship_impact_value"] or 0), ALTAR_ATTR[btype]


def _bonus(points, base):
    if points <= 0 or base <= 0:
        return 0.0, 0
    pct = min(points // POINTS_PER_STEP, MAX_STEPS) * PCT_PER_STEP
    return pct, int(math.floor(base * pct / 100.0 + 0.5))


def _drain_rate(points):
    for floor, rate in DRAIN:
        if points > floor:
            return rate
    return DRAIN[-1][1]


def _drained(points, settled_at):
    """What the hourly drain leaves of a balance now (GuildAltarDecayPolicy.Settle), and the hours owed."""
    if not settled_at or points <= 0:
        return points, 0
    try:
        t = datetime.datetime.fromisoformat(str(settled_at).replace("Z", "+00:00"))
    except ValueError:
        return points, 0
    hours = int((datetime.datetime.now(datetime.timezone.utc) - t).total_seconds() // 3600)
    p = points
    for _ in range(max(0, min(hours, 100_000))):
        if p <= 0:
            break
        p = max(0, p - _drain_rate(p))
    return p, max(0, hours)


# ----------------------------------------------------------------------------------------------- Undo of deletes
# reborn.Reborn.restore_statements re-inserts a deleted row with "INSERT INTO t SELECT * FROM jsonb_populate_record(..)",
# which PostgreSQL refuses for GENERATED ALWAYS identity columns (guilds.id, guild_buildings.id): "cannot insert a
# non-DEFAULT value into column id ... Use OVERRIDING SYSTEM VALUE". Without this, Undo of a disband or a removed
# building fails. Narrow wrapper: only those two tables' re-insert statements change. FRAMEWORK CHANGE NEEDED: do this
# in reborn.py for every identity table (report, needs_admin).
_IDENTITY = ("guilds", "guild_buildings")


def _patch_restore():
    if R is None or getattr(R.Reborn.restore_statements, "_gw_guild", False):
        return
    orig = R.Reborn.restore_statements
    rx = re.compile(r"^INSERT INTO (%s) SELECT " % "|".join(_IDENTITY))

    def restore_statements(self, *a, **kw):
        out = orig(self, *a, **kw)
        return [rx.sub(r"INSERT INTO \1 OVERRIDING SYSTEM VALUE SELECT ", s) if isinstance(s, str) else s for s in out]

    restore_statements._gw_guild = True
    restore_statements.__doc__ = orig.__doc__
    R.Reborn.restore_statements = restore_statements


_patch_restore()


# ----------------------------------------------------------------------------------------------- reads
def _online(ctx, cids):
    """{cid: {name, online, reasons}} for these characters, in one query (+ the server log when configured)."""
    cids = sorted({int(c) for c in cids})
    if not cids:
        return {}
    rb = ctx.need_rb()
    rows = rb.pg.json("""(SELECT coalesce(json_agg(json_build_object('id', c.id, 'name', c.name,
        'owner', c.checkpoint_owner_id IS NOT NULL, 'login_status', a.login_status,
        'presence', a.login_presence_token IS NOT NULL)), '[]')
        FROM character_base c JOIN accounts a ON a.id = c.account_id WHERE c.id IN (%s))""" % _ids(cids))
    logst = rb.log_state() if getattr(rb, "log_path", None) else {}
    out = {}
    for r in rows:
        why = []
        if r["owner"]:
            why.append("a game session owns the character (checkpoint owner)")
        if r["presence"]:
            why.append("the account is in the world (presence token)")
        if r["login_status"]:
            why.append("the account is at the login / character screen (login_status=%s)" % r["login_status"])
        if logst.get(r["name"]) == "online":
            why.append("the server log says %s joined the world" % r["name"])
        out[r["id"]] = {"name": r["name"], "online": bool(why), "reasons": why}
    return out


def _batch(ctx, **exprs):
    """Several read-only SQL expressions in ONE psql call (each call spawns psql, which is slow on a busy VPS)."""
    return ctx.need_rb().pg.json("json_build_object(%s)" % ", ".join("%s, %s" % (lit(k), v) for k, v in exprs.items()), timeout=120)


LEVELS_SQL = """(SELECT coalesce(json_agg(json_build_object('t', building_type, 'l', level,
        'found', upgrade_found, 'bijou', upgrade_bijou, 'worship_impact_type', worship_impact_type,
        'worship_impact_value', worship_impact_value) ORDER BY building_type, level), '[]') FROM guild_building_levels)"""
TYPES_SQL = """(SELECT coalesce(json_agg(json_build_object('type', building_type, 'name', display_name,
        'max', maximum_level) ORDER BY building_type), '[]') FROM guild_building_types)"""


def _levelmap(rows):
    return {(r["t"], r["l"]): r for r in rows}


def _levels(ctx):
    return _levelmap(ctx.need_rb().pg.json(LEVELS_SQL))


def _types(ctx):
    return ctx.need_rb().pg.json(TYPES_SQL)


GUILD_SQL = """SELECT g.id, g.name, g.level, g.member_limit, g.lord_character_id, g.silver, g.gold, g.bijou, g.proclamation,
    g.refuse_applications, g.online_count, g.created_at, g.updated_at,
    (SELECT name FROM character_base WHERE id = g.lord_character_id) AS lord_name,
    (SELECT camp FROM character_base WHERE id = g.lord_character_id) AS camp,
    (SELECT count(*) FROM guild_members m WHERE m.guild_id = g.id) AS members,
    (SELECT count(*) FROM guild_buildings b WHERE b.guild_id = g.id) AS buildings,
    (SELECT coalesce(sum(points), 0) FROM guild_altar_worship w WHERE w.guild_id = g.id) AS offering_points
    FROM guilds g"""


def _guild_rows_sql(where="true"):
    return "(SELECT coalesce(json_agg(r ORDER BY r.id), '[]') FROM (%s WHERE %s) r)" % (GUILD_SQL, where)


def _guild_rows(ctx, where="true"):
    return ctx.need_rb().pg.json(_guild_rows_sql(where))


def _guild(ctx, gid):
    rows = _guild_rows(ctx, "g.id = %d" % int(gid))
    if not rows:
        raise NotFound("no guild #%s (it may have been disbanded)" % gid)
    return rows[0]


def _members_sql(gid):
    return """(SELECT coalesce(json_agg(r ORDER BY r.duty DESC, r.character_id), '[]') FROM (
        SELECT m.character_id, m.guild_id, m.duty, m.joined_at, m.contribution_silver, m.contribution_gold,
               c.name, c.fighter_job_lv AS level, c.profession, c.camp, c.account_id, a.username,
               c.consortia, c.consortia_job, c.guild_duty, c.consortia_contribute
        FROM guild_members m JOIN character_base c ON c.id = m.character_id JOIN accounts a ON a.id = c.account_id
        WHERE m.guild_id = %d) r)""" % int(gid)


def _members(ctx, gid):
    return ctx.need_rb().pg.json(_members_sql(gid))


def _member_ids(ctx, gid):
    if gid is None:
        return []
    return ctx.need_rb().pg.json("(SELECT coalesce(json_agg(character_id), '[]') FROM guild_members WHERE guild_id = %d)" % int(gid))


def _issues(ctx, gid=None):
    """Consistency problems (the rules the game keeps), all guilds or one."""
    return ctx.need_rb().pg.json(_issues_sql(gid))


def _issues_sql(gid=None):
    g1 = "" if gid is None else " AND m.guild_id = %d" % int(gid)
    g2 = "" if gid is None else " AND g.id = %d" % int(gid)
    g3 = "" if gid is None else " AND c.consortia = %d" % int(gid)
    return """(SELECT coalesce(json_agg(x), '[]') FROM (
      SELECT 'desync' AS kind, m.guild_id AS guild, c.id AS cid, c.name AS who,
             format('guild_members says guild %%s duty %%s; the character says guild %%s, duty %%s, job %%s', m.guild_id, m.duty, c.consortia, c.guild_duty, c.consortia_job) AS what
        FROM guild_members m JOIN character_base c ON c.id = m.character_id
        WHERE (c.consortia <> m.guild_id OR c.guild_duty <> m.duty OR c.consortia_job <> m.duty)%s
      UNION ALL
      SELECT 'orphan', c.consortia, c.id, c.name,
             format('not a member of any guild, but the character says guild %%s, duty %%s, job %%s', c.consortia, c.guild_duty, c.consortia_job)
        FROM character_base c WHERE NOT EXISTS (SELECT 1 FROM guild_members m WHERE m.character_id = c.id)
         AND (c.consortia <> 0 OR c.guild_duty <> 0 OR c.consortia_job <> 0)%s
      UNION ALL
      SELECT 'lord', g.id, g.lord_character_id, g.name,
             CASE WHEN NOT EXISTS (SELECT 1 FROM guild_members m WHERE m.character_id = g.lord_character_id AND m.guild_id = g.id)
                  THEN 'the leader (character #' || g.lord_character_id || ') is not a member any more: pick a new leader'
                  ELSE 'the leader does not hold duty 6, or another member does' END
        FROM guilds g WHERE (NOT EXISTS (SELECT 1 FROM guild_members m WHERE m.character_id = g.lord_character_id AND m.guild_id = g.id AND m.duty = 6)
           OR EXISTS (SELECT 1 FROM guild_members m WHERE m.guild_id = g.id AND m.duty = 6 AND m.character_id <> g.lord_character_id))%s
      UNION ALL
      SELECT 'building', b.guild_id, NULL, coalesce(t.display_name, 'type ' || b.building_type),
             format('slot %%s type %%s level %%s (the server keeps slot = type and level 1-%%s)', b.slot, b.building_type, b.level, coalesce(t.maximum_level, 0))
        FROM guild_buildings b LEFT JOIN guild_building_types t ON t.building_type = b.building_type
        WHERE (t.building_type IS NULL OR b.slot <> b.building_type OR b.level > t.maximum_level)%s
      UNION ALL
      SELECT 'full', g.id, NULL, g.name, format('%%s members but the limit is %%s: nobody can join until members leave', (SELECT count(*) FROM guild_members m WHERE m.guild_id = g.id), g.member_limit)
        FROM guilds g WHERE (SELECT count(*) FROM guild_members m WHERE m.guild_id = g.id) > g.member_limit%s
      UNION ALL
      SELECT 'roster', g.id, NULL, g.name, format('%%s members: the in-game guild window lists only the first %s (highest duty first)', (SELECT count(*) FROM guild_members m WHERE m.guild_id = g.id))
        FROM guilds g WHERE (SELECT count(*) FROM guild_members m WHERE m.guild_id = g.id) > %d%s
    ) x)""" % (g1, g3, g2, g1.replace("m.guild_id", "b.guild_id"), g2, ROSTER_SHOWN, ROSTER_SHOWN, g2)


def _guard_sql(gid, lock):
    """DO-block that RAISEs when guild state would break the server's own rules. Runs before COMMIT and on Undo."""
    g = int(gid)
    ids = _ids(lock)
    return """DO $gwg$ DECLARE bad text; BEGIN
  SELECT string_agg(c.name || ' (#' || c.id || ')', ', ') INTO bad FROM guild_members m JOIN character_base c ON c.id = m.character_id
   WHERE (m.guild_id = %(g)d OR m.character_id IN (%(ids)s)) AND (c.consortia <> m.guild_id OR c.guild_duty <> m.duty OR c.consortia_job <> m.duty);
  IF bad IS NOT NULL THEN RAISE EXCEPTION 'gwadmin-guild: the character columns consortia / consortia_job / guild_duty would disagree with guild_members for %%; nothing written', bad; END IF;
  SELECT string_agg(c.name || ' (#' || c.id || ')', ', ') INTO bad FROM character_base c
   WHERE (c.consortia = %(g)d OR c.id IN (%(ids)s)) AND NOT EXISTS (SELECT 1 FROM guild_members m WHERE m.character_id = c.id)
     AND (c.consortia <> 0 OR c.guild_duty <> 0 OR c.consortia_job <> 0);
  IF bad IS NOT NULL THEN RAISE EXCEPTION 'gwadmin-guild: %% would still be marked as in a guild without a membership; nothing written', bad; END IF;
  SELECT string_agg(g.name, ', ') INTO bad FROM guilds g WHERE g.id = %(g)d AND (
      (SELECT count(*) FROM guild_members m WHERE m.guild_id = g.id AND m.duty = 6) > 1
   OR EXISTS (SELECT 1 FROM guild_members m WHERE m.guild_id = g.id AND m.duty = 6 AND m.character_id <> g.lord_character_id)
   OR EXISTS (SELECT 1 FROM guild_members m WHERE m.character_id = g.lord_character_id AND (m.guild_id <> g.id OR m.duty <> 6)));
  IF bad IS NOT NULL THEN RAISE EXCEPTION 'gwadmin-guild: guild %% would not have exactly its leader at duty 6; nothing written', bad; END IF;
  SELECT string_agg(b.building_type::text, ', ') INTO bad FROM guild_buildings b LEFT JOIN guild_building_types t ON t.building_type = b.building_type
   WHERE b.guild_id = %(g)d AND (t.building_type IS NULL OR b.slot <> b.building_type OR b.level > t.maximum_level);
  IF bad IS NOT NULL THEN RAISE EXCEPTION 'gwadmin-guild: building type(s) %% would break the building rules (slot = type, level 1-maximum); nothing written', bad; END IF;
END $gwg$;""" % {"g": g, "ids": ids}


def _scope(gid, lock, boost=False):
    ids = _ids(lock)
    s = [common.scope("guilds", "id = %d" % gid, pk=["id"], counters=[], ignore=["updated_at", "online_count"]),
         common.scope("guild_members", "guild_id = %d OR character_id IN (%s)" % (gid, ids), pk=["character_id"], counters=[]),
         common.scope("guild_buildings", "guild_id = %d" % gid, pk=["id"], counters=[], ignore=["created_at", "updated_at"]),
         # settled_at is NOT ignored: Undo must put back the drain clock with the points (an edit restarts it)
         common.scope("guild_altar_worship", "guild_id = %d" % gid, pk=["guild_id", "character_id", "building_type"],
                      counters=[], ignore=["updated_at"]),
         common.scope("character_base", "id IN (%s)" % ids if lock else "false", pk=["id"])]
    if boost:
        s.append(common.scope("character_experience_modifiers", "character_id IN (%s) AND kind = %d" % (ids, GUILD_BOOST_KIND)
                              if lock else "false", pk=["character_id", "kind"], counters=[]))
    return s


def _refuse_online(ctx, cids, what):
    """423 when any of these characters is online, with every online member and whether the server can kick."""
    on = {c: v for c, v in _online(ctx, cids).items() if v["online"]}
    if not on:
        return
    caps = ctx.caps
    kick = "character.kick" in (caps.get("operations") or {})
    names = ", ".join(v["name"] for v in on.values())
    tail = ("The game server has no live guild edit; log them out first%s." % (" (Kick)" if kick else "")
            if caps.get("available") else
            "Live edits arrive with the round-2 server update; every member of the guild must be logged out.")
    raise OnlineRefused("%s: %s %s online. Each online member's guild window, rank tag and altar bonus are held by the "
                        "game session, so a database edit would not reach them. %s" % (
                            what, names, "is" if len(on) == 1 else "are", tail),
                        {"name": names, "online": True, "reasons": sorted({r for v in on.values() for r in v["reasons"]}),
                         "members": [{"cid": c, "name": v["name"], "reasons": v["reasons"]} for c, v in sorted(on.items())],
                         "kick": kick, "capabilities": bool(caps.get("available")), "live": [], "live_fields": [], "live_bag": []})


def _gwrite(ctx, gid, action, build, extra=(), fields=("guilds.*",), expected=None, target=None, boost=False, guard_only=None):
    """One guild write: every member (+ extra characters) offline and locked, the guild's rows in scope, the guard.
    guard_only: [cid, ...] = guard only these characters (Repair: other members' older problems must not block it)."""
    gid = int(gid)
    lock = sorted(set(_member_ids(ctx, gid)) | {int(x) for x in extra})
    _refuse_online(ctx, lock, action if isinstance(action, str) else "guild edit")
    guard = _guard_sql(-1, guard_only) if guard_only else _guard_sql(gid, lock)
    return ctx.rows_write(_scope(gid, lock, boost), action, build, fields=list(fields), lock_cids=lock,
                          guard_sql=[guard], expected=expected, advanced=True, applies="next_login",
                          target=target or "guild #%d" % gid)


def _char(ctx, cid):
    r = ctx.need_rb().pg.json("""(SELECT row_to_json(x) FROM (SELECT c.id, c.name, c.fighter_job_lv AS level, c.profession,
        c.camp, c.consortia, c.guild_duty, c.consortia_job, c.consortia_contribute, c.account_id,
        (SELECT row_to_json(m) FROM guild_members m WHERE m.character_id = c.id) AS membership
        FROM character_base c WHERE c.id = %d) x)""" % int(cid))
    if not r:
        raise NotFound("no character #%s" % cid)
    return r


def _char_cols(duty, gid, contribute=None):
    v = {"guild_duty": duty, "consortia_job": duty, "consortia": gid}
    if contribute is not None:
        v["consortia_contribute"] = contribute
    return v


# ----------------------------------------------------------------------------------------------- GET routes
@M.get("overview")
def overview(ctx):
    """GET /api/m/guild/overview - every guild with its summary, the reference tables and the checks."""
    rb = ctx.need_rb()
    caps = ctx.caps
    b = _batch(ctx, guilds=_guild_rows_sql(), issues=_issues_sql(), types=TYPES_SQL,
               mem="(SELECT coalesce(json_agg(json_build_object('g', guild_id, 'c', character_id)), '[]') FROM guild_members)")
    guilds, mem = b["guilds"], b["mem"]
    on = _online(ctx, [m["c"] for m in mem])
    for g in guilds:
        g["online_now"] = sum(1 for m in mem if m["g"] == g["id"] and (on.get(m["c"]) or {}).get("online"))
        g["camp_name"] = common.CAMP_NAMES.get(g["camp"], "?")
    cat = _cat()
    return {"guilds": guilds, "issues": b["issues"], "writable": bool(rb.writable), "database": rb.db,
            "round2": bool(caps.get("available")), "kick": "character.kick" in (caps.get("operations") or {}),
            "duties": [{"id": d, "name": _duty_name(d), "help": DUTY_HELP.get(d, "")} for d in range(1, 7)],
            "types": b["types"], "altars": [{"type": t, "attribute": a} for t, a in ALTAR_ATTR.items()],
            "defaults": cat.get("defaults") or {}, "levels": cat.get("levels") or {},
            "limits": {"name": NAME_MAX, "proclamation": PROC_MAX, "proclamation_wire": PROC_WIRE, "roster": ROSTER_SHOWN,
                       "money": I32, "member_limit": 10000, "level": 12},
            "systems": SYSTEMS}


@M.get(r"g/(\d+)")
def guild(ctx, gid):
    """GET /api/m/guild/g/<id> - one guild as the game window shows it, plus everything stored about it."""
    gid = int(gid)
    B = _batch(ctx, g=_guild_rows_sql("g.id = %d" % gid), members=_members_sql(gid), levels=LEVELS_SQL, types=TYPES_SQL,
               issues=_issues_sql(gid),
               built="(SELECT coalesce(json_agg(b ORDER BY b.slot), '[]') FROM guild_buildings b WHERE b.guild_id = %d)" % gid,
               worship="""(SELECT coalesce(json_agg(w ORDER BY w.character_id, w.building_type), '[]') FROM (
        SELECT w.*, c.name FROM guild_altar_worship w JOIN character_base c ON c.id = w.character_id WHERE w.guild_id = %d) w)""" % gid,
               boosts="""(SELECT coalesce(json_agg(e ORDER BY e.character_id), '[]') FROM (
        SELECT e.character_id, e.status_id, e.kind, e.bonus_basis_points, e.source, e.activated_at, e.expires_at,
               e.remaining_online_ticks, c.name
        FROM character_experience_modifiers e JOIN guild_members m ON m.character_id = e.character_id
        JOIN character_base c ON c.id = e.character_id WHERE m.guild_id = %d AND e.kind = %d) e)""" % (gid, GUILD_BOOST_KIND))
    if not B["g"]:
        raise NotFound("no guild #%s (it may have been disbanded)" % gid)
    g, members = B["g"][0], B["members"]
    on = _online(ctx, [m["character_id"] for m in members])
    for m in members:
        o = on.get(m["character_id"]) or {}
        m["online"], m["reasons"] = o.get("online", False), o.get("reasons", [])
        m["class"] = common.CLASS_NAMES.get(m["profession"], str(m["profession"]))
        m["duty_name"] = _duty_name(m["duty"])
        m["in_step"] = m["consortia"] == gid and m["guild_duty"] == m["duty"] and m["consortia_job"] == m["duty"]
        m["shown_in_window"] = True
    for i, m in enumerate(members):
        m["shown_in_window"] = i < ROSTER_SHOWN
    built = {b["building_type"]: b for b in B["built"]}
    levels = _levelmap(B["levels"])
    cat_b = (_cat().get("buildings") or {})
    buildings = []
    for t in B["types"]:
        b = built.get(t["type"])
        c = cat_b.get(str(t["type"])) or {}
        lv = b["level"] if b else 0
        nxt = levels.get((t["type"], lv + 1)) if lv < t["max"] else None
        base, attr = _altar_value(levels, t["type"], lv)
        buildings.append({"type": t["type"], "name": t["name"], "max": t["max"], "level": lv, "row": b,
                          "sprite": c.get("sprite"), "desc": c.get("desc", ""), "altar": t["type"] in ALTAR_ATTR,
                          "attribute": ALTAR_ATTR.get(t["type"]) or attr, "base_value": base,
                          "next_cost": {"silver": nxt["found"], "gems": nxt["bijou"]} if nxt else None,
                          "impact": {k: v for k, v in ((c.get("levels") or {}).get(str(lv)) or {}).items()
                                     if k not in ("found", "bijou")} if lv else {}})
    worship = B["worship"]
    lvl_of = {b["type"]: b["level"] for b in buildings}
    for w in worship:
        base, attr = _altar_value(levels, w["building_type"], lvl_of.get(w["building_type"], 0))
        left, hours = _drained(int(w["points"]), w.get("settled_at"))
        pct, val = _bonus(left, base)
        w.update({"attribute": attr or "-", "base_value": base, "points_now": left, "hours_owed": hours,
                  "bonus_percent": pct, "bonus": val, "drain_per_hour": _drain_rate(left) if left > 0 else 0})
    return {"guild": g, "members": members, "buildings": buildings, "worship": worship, "boosts": B["boosts"],
            "issues": B["issues"], "online": [m["name"] for m in members if m["online"]],
            "vice_leaders": sum(1 for m in members if m["duty"] == 5)}


@M.get("characters")
def characters(ctx):
    """GET /api/m/guild/characters - every character with its guild (the "add member" / "leader" picker lists ALL)."""
    rb = ctx.need_rb()
    rows = rb.pg.json("""(SELECT coalesce(json_agg(r ORDER BY r.name), '[]') FROM (
        SELECT c.id, c.name, c.fighter_job_lv AS level, c.profession, c.camp, a.username, m.guild_id, m.duty, g.name AS guild_name,
               c.checkpoint_owner_id IS NOT NULL OR a.login_presence_token IS NOT NULL OR a.login_status <> 0 AS online
        FROM character_base c JOIN accounts a ON a.id = c.account_id
        LEFT JOIN guild_members m ON m.character_id = c.id LEFT JOIN guilds g ON g.id = m.guild_id) r)""")
    for r in rows:
        r["class"] = common.CLASS_NAMES.get(r["profession"], str(r["profession"]))
        r["camp_name"] = common.CAMP_NAMES.get(r["camp"], "?")
    return {"characters": rows}


@M.get("factions")
def factions(ctx):
    """GET /api/m/guild/factions - realms, the 19 world-boss areas, who controls each now, camp populations."""
    rb = ctx.need_rb()
    realms_sql = ("(SELECT coalesce(json_agg(json_build_object('id', id, 'name', name) ORDER BY id), '[]') FROM server)")
    areas_sql = ("""(SELECT coalesce(json_agg(a ORDER BY a.map_id), '[]') FROM (
        SELECT w.map_id, w.boss_template_key, w.boss_display_name, w.bonus_basis_points, w.respawn_interval_seconds, w.enabled,
               EXISTS (SELECT 1 FROM gameplay_world_boss_definitions d WHERE d.map_id = w.map_id AND d.template_key = w.boss_template_key
                       AND d.revision = (SELECT revision FROM gameplay_content_publication WHERE family = 'gameplay')) AS published
        FROM world_boss_areas w) a)""")
    control_sql = ("""(SELECT coalesce(json_agg(c ORDER BY c.realm_id, c.map_id), '[]') FROM (
        SELECT c.*, c.expires_at > now() AND c.activated_at <= now() AS active FROM faction_area_experience_control c) c)""")
    camps_sql = ("""(SELECT coalesce(json_agg(x), '[]') FROM (SELECT camp, count(*) AS characters,
        count(*) FILTER (WHERE EXISTS (SELECT 1 FROM guild_members m WHERE m.character_id = c.id)) AS in_guilds
        FROM character_base c GROUP BY camp ORDER BY camp) x)""")
    B = _batch(ctx, realms=realms_sql, areas=areas_sql, control=control_sql, camps=camps_sql)
    realms, areas, control, camps = B["realms"], B["areas"], B["control"], B["camps"]
    for a in areas:
        a["map_name"] = common.CATALOG.name("map", a["map_id"]) or ("map %s" % a["map_id"])
    for c in camps:
        c["name"] = common.CAMP_NAMES.get(c["camp"], "?")
    return {"realms": realms, "areas": areas, "control": control, "camps": camps, "writable": bool(rb.writable)}


# ----------------------------------------------------------------------------------------------- POST: guilds
@M.post("create")
def create(ctx):
    """POST /api/m/guild/create {name, lord, level?, member_limit?, silver?, gold?, bijou?, proclamation?}
    The server's own create (PostgresGuildStore.TryCreateAsync) without the Guild Stone: guild row, the founder as
    duty 6, the founder's character columns, in one transaction."""
    b = ctx.body
    name = _name_ok(b.get("name"))
    lord = _int(b.get("lord"), 1, I32, "leader")
    level = _int(b.get("level", 1), 1, 12, "guild level")
    limit = _int(b.get("member_limit", 100), 1, 10000, "member limit")
    silver = _int(b.get("silver", 0), 0, I32, "guild Silver")
    gold = _int(b.get("gold", 0), 0, I32, "guild Gold")
    bijou = _int(b.get("bijou", 0), 0, I32, "guild gems")
    proc = _proc_ok(b.get("proclamation", ""))
    rb = ctx.need_rb()
    ch = _char(ctx, lord)
    if ch["membership"]:
        raise Bad("%s is already in a guild (#%s): remove them there first" % (ch["name"], ch["membership"]["guild_id"]))
    if rb.pg.json("(SELECT to_jsonb(count(*)) FROM guilds WHERE lower(name) = lower(%s))" % lit(name)):
        raise Bad("a guild called %s exists already (names are unique, ignoring case)" % name)
    gid = int(rb.pg.json("(SELECT to_jsonb(GREATEST((SELECT coalesce(max(id), 0) FROM guilds), "
                         "(SELECT last_value FROM public.guilds_id_seq)) + 1))"))

    def build(before):
        if before.get("guilds"):
            raise Bad("guild id %d was taken a moment ago; try again" % gid)
        return ["INSERT INTO guilds (id, name, level, member_limit, lord_character_id, silver, gold, bijou, proclamation, "
                "created_at, updated_at) OVERRIDING SYSTEM VALUE VALUES (%d, %s, %d, %d, %d, %d, %d, %d, %s, now(), now())"
                % (gid, lit(name), level, limit, lord, silver, gold, bijou, lit(proc)),
                # keep the identity ahead of the id used here, so the server's own creates never collide
                "SELECT setval('public.guilds_id_seq', GREATEST(%d, (SELECT last_value FROM public.guilds_id_seq)))" % gid,
                "INSERT INTO guild_members (character_id, guild_id, duty, joined_at) VALUES (%d, %d, 6, now())" % (lord, gid),
                common.update_sql("character_base", {"id": lord}, _char_cols(6, gid), bump=False)]

    r = _gwrite(ctx, gid, "create guild %s (leader %s)" % (name, ch["name"]), build, extra=[lord],
                fields=["guilds.*", "guild_members.*"], target="guild " + name)
    r["guild"] = _guild(ctx, gid)
    return r


@M.post(r"g/(\d+)/edit")
def edit(ctx, gid):
    """POST /api/m/guild/g/<id>/edit {values: {name, level, member_limit, silver, gold, bijou, proclamation,
    refuse_applications}, expected: {...shown}}"""
    gid = int(gid)
    g = _guild(ctx, gid)
    vals_in = ctx.body.get("values") or {}
    if not isinstance(vals_in, dict) or not vals_in:
        raise Bad("nothing to change")
    v = {}
    for k, x in vals_in.items():
        if k == "name":
            v[k] = _name_ok(x)
            if v[k].lower() != g["name"].lower() and ctx.need_rb().pg.json(
                    "(SELECT to_jsonb(count(*)) FROM guilds WHERE lower(name) = lower(%s) AND id <> %d)" % (lit(v[k]), gid)):
                raise Bad("a guild called %s exists already" % v[k])
        elif k == "level":
            v[k] = _int(x, 1, 12, "guild level")
        elif k == "member_limit":
            v[k] = _int(x, 1, 10000, "member limit")
        elif k in ("silver", "gold", "bijou"):
            v[k] = _int(x, 0, I32, k)
        elif k == "proclamation":
            v[k] = _proc_ok(x)
        elif k == "refuse_applications":
            v[k] = _flag(x, "refuse applications")
        elif k in ("id", "lord_character_id", "online_count", "created_at", "updated_at"):
            raise Refused("guilds.%s is not edited here: %s" % (k, {
                "id": "the guild number is the key every member and building points at",
                "lord_character_id": "use 'Make leader', which also moves the duties",
                "online_count": "the server recounts it every time the guild window opens"}.get(k, "the database stamps it")))
        else:
            raise Bad("unknown guild field %s" % k)
    v = {k: x for k, x in v.items() if g.get(k) != x}
    if not v:
        raise Bad("nothing changed")
    exp = _expected_dict(ctx.body.get("expected"))
    exp = {k: exp[k] for k in exp if k in v}
    expected = [{"table": "guilds", "where": {"id": gid}, "values": exp}] if exp else None
    what = ", ".join("%s %s" % (k.replace("_", " "), ("on" if x else "off") if isinstance(x, bool) else x) for k, x in v.items())
    r = _gwrite(ctx, gid, "guild %s: %s" % (g["name"], what),
                lambda before: ["UPDATE guilds SET %s, updated_at = now() WHERE id = %d" % (
                    ", ".join("%s = %s" % (common.ident(k), lit(x)) for k, x in v.items()), gid)],
                fields=["guilds.%s" % k for k in v], expected=expected, target="guild " + g["name"])
    r["guild"] = _guild(ctx, gid)
    return r


@M.post(r"g/(\d+)/disband")
def disband(ctx, gid):
    """POST /api/m/guild/g/<id>/disband - delete the guild, its memberships, buildings and offerings and the members'
    [Guild] Double EXP boost, and clear every member's guild columns (as leaving does: duty, guild, contribution to 0).
    Undo puts it all back."""
    gid = int(gid)
    g = _guild(ctx, gid)
    ids = _member_ids(ctx, gid)

    def build(before):
        st = ["DELETE FROM guild_altar_worship WHERE guild_id = %d" % gid,
              "DELETE FROM guild_buildings WHERE guild_id = %d" % gid,
              "DELETE FROM guild_members WHERE guild_id = %d" % gid,
              "DELETE FROM guilds WHERE id = %d" % gid]
        if ids:
            st.append("UPDATE character_base SET guild_duty = 0, consortia_job = 0, consortia = 0, consortia_contribute = 0 "
                      "WHERE id IN (%s)" % _ids(ids))
            # the [Guild] Double EXP benefit belongs to the guild: it ends with it (Undo gives it back)
            st.append("DELETE FROM character_experience_modifiers WHERE kind = %d AND character_id IN (%s)" % (GUILD_BOOST_KIND, _ids(ids)))
        # characters still pointing at this guild without a membership (left over from an older edit) are cleared too
        st.append("UPDATE character_base SET guild_duty = 0, consortia_job = 0, consortia = 0 WHERE consortia = %d" % gid)
        return st

    orphans = ctx.need_rb().pg.json("(SELECT coalesce(json_agg(id), '[]') FROM character_base WHERE consortia = %d)" % gid)
    return _gwrite(ctx, gid, "disband guild %s (%d members)" % (g["name"], len(ids)), build, extra=orphans,
                   fields=["guilds.*", "guild_members.*", "guild_buildings.*", "guild_altar_worship.*", "character_experience_modifiers.*"],
                   target="guild " + g["name"], boost=True)


# ----------------------------------------------------------------------------------------------- POST: members
@M.post(r"g/(\d+)/member/add")
def member_add(ctx, gid):
    """POST /api/m/guild/g/<id>/member/add {cid, duty (1-5)} - seat a character (PostgresGuildStore.SetMemberDutyAsync)."""
    gid = int(gid)
    g = _guild(ctx, gid)
    cid = _int(ctx.body.get("cid"), 1, I32, "character")
    duty = _int(ctx.body.get("duty", 1), 1, 5, "duty (6 = leader: use Make leader)")
    ch = _char(ctx, cid)
    if ch["membership"]:
        if ch["membership"]["guild_id"] == gid:
            raise Bad("%s is already a member" % ch["name"])
        raise Bad("%s is in another guild (#%s); remove them there first" % (ch["name"], ch["membership"]["guild_id"]))
    if g["members"] >= g["member_limit"]:
        raise Bad("the guild is full (%d of %d): raise the member limit first" % (g["members"], g["member_limit"]))

    def build(before):
        # re-checked on the snapshot taken under the write lock: two adds at once must not both pass the check above
        grow = (before.get("guilds") or [{}])[0]
        now_n = sum(1 for x in before.get("guild_members", []) if x["guild_id"] == gid)
        if grow and now_n >= int(grow["member_limit"]):
            raise Bad("the guild is full (%d of %d): raise the member limit first" % (now_n, grow["member_limit"]))
        return ["INSERT INTO guild_members (character_id, guild_id, duty, joined_at) VALUES (%d, %d, %d, now())" % (cid, gid, duty),
                common.update_sql("character_base", {"id": cid}, _char_cols(duty, gid), bump=False),
                # and in the transaction, against a join the game server commits at the same moment
                "DO $gwf$ BEGIN IF (SELECT count(*) FROM guild_members WHERE guild_id = %d) > (SELECT member_limit FROM guilds "
                "WHERE id = %d) THEN RAISE EXCEPTION 'gwadmin-guild: the guild is full; nothing written'; END IF; END $gwf$" % (gid, gid)]

    r = _gwrite(ctx, gid, "guild %s: add %s as %s" % (g["name"], ch["name"], _duty_name(duty)), build, extra=[cid],
                fields=["guild_members.*"], expected=[{"table": "guild_members", "where": {"character_id": cid}, "values": None}],
                target="guild " + g["name"])
    return r


@M.post(r"g/(\d+)/member/(\d+)/edit")
def member_edit(ctx, gid, cid):
    """POST .../member/<cid>/edit {duty?, contribution_silver?, contribution_gold?, consortia_contribute?, expected{}}"""
    gid, cid = int(gid), int(cid)
    g = _guild(ctx, gid)
    ch = _char(ctx, cid)
    m = ch["membership"]
    if not m or m["guild_id"] != gid:
        raise Bad("%s is not a member of %s" % (ch["name"], g["name"]))
    b = ctx.body
    mv, cv = {}, {}
    if "duty" in b:
        d = _int(b["duty"], 1, 6, "duty")
        if d != m["duty"]:
            if d == 6 or m["duty"] == 6:
                raise Bad("the leader's duty changes only with 'Make leader' (it also updates the guild's leader)")
            mv["duty"] = d
            cv.update(_char_cols(d, gid))
    for k in ("contribution_silver", "contribution_gold"):
        if k in b:
            x = _int(b[k], 0, I32, k.replace("_", " "))
            if x != m[k]:
                mv[k] = x
    if "consortia_contribute" in b:
        x = _int(b["consortia_contribute"], 0, I32, "guild contribution points")
        if x != ch["consortia_contribute"]:
            cv["consortia_contribute"] = x
    if not mv and not cv:
        raise Bad("nothing changed")
    exp = _expected_dict(b.get("expected"))
    expected = []
    me = {k: exp[k] for k in ("duty", "contribution_silver", "contribution_gold") if k in exp}
    if me:
        expected.append({"table": "guild_members", "where": {"character_id": cid}, "values": me})
    if "consortia_contribute" in exp:
        expected.append({"table": "character_base", "where": {"id": cid}, "values": {"consortia_contribute": exp["consortia_contribute"]}})

    def build(before):
        st = []
        if mv:
            st.append(common.update_sql("guild_members", {"character_id": cid}, mv, bump=False))
        if cv:
            st.append(common.update_sql("character_base", {"id": cid}, cv, bump=False))
        return st

    what = ", ".join("%s %s" % (k.replace("_", " "), _duty_name(x) if k == "duty" else x) for k, x in list(mv.items()) +
                     [(k, x) for k, x in cv.items() if k == "consortia_contribute"])
    fields = ["guild_members.%s" % k for k in mv] + (["character_base.consortia_contribute"] if "consortia_contribute" in cv else [])
    return _gwrite(ctx, gid, "guild %s, %s: %s" % (g["name"], ch["name"], what), build, extra=[cid], fields=fields,
                   expected=expected or None, target="guild " + g["name"])


@M.post(r"g/(\d+)/member/(\d+)/remove")
def member_remove(ctx, gid, cid):
    """POST .../member/<cid>/remove - PostgresGuildStore.RemoveMemberAsync: membership row + the character's guild
    columns, contribution points to 0 (what leaving does)."""
    gid, cid = int(gid), int(cid)
    g = _guild(ctx, gid)
    ch = _char(ctx, cid)
    m = ch["membership"]
    if not m or m["guild_id"] != gid:
        raise Bad("%s is not a member of %s" % (ch["name"], g["name"]))
    if cid == g["lord_character_id"]:
        raise Bad("%s leads the guild: make another member the leader first, or disband the guild" % ch["name"])

    def build(before):
        return ["DELETE FROM guild_members WHERE character_id = %d" % cid,
                common.update_sql("character_base", {"id": cid}, _char_cols(0, 0, 0), bump=False)]

    return _gwrite(ctx, gid, "guild %s: remove %s" % (g["name"], ch["name"]), build, extra=[cid], fields=["guild_members.*"],
                   expected=[{"table": "guild_members", "where": {"character_id": cid}, "values": {"guild_id": gid, "duty": m["duty"]}}],
                   target="guild " + g["name"])


@M.post(r"g/(\d+)/leader")
def leader(ctx, gid):
    """POST /api/m/guild/g/<id>/leader {cid, old_duty (1-5, default 5)} - hand the leadership over (the game cannot)."""
    gid = int(gid)
    g = _guild(ctx, gid)
    cid = _int(ctx.body.get("cid"), 1, I32, "new leader")
    old_duty = _int(ctx.body.get("old_duty", 5), 1, 5, "the old leader's new duty")
    ch = _char(ctx, cid)
    if not ch["membership"] or ch["membership"]["guild_id"] != gid:
        raise Bad("%s must be a member of %s first" % (ch["name"], g["name"]))
    if cid == g["lord_character_id"] and ch["membership"]["duty"] == 6:
        raise Bad("%s already leads the guild" % ch["name"])
    old = g["lord_character_id"]
    old_is_member = bool(ctx.need_rb().pg.json(
        "(SELECT to_jsonb(count(*)) FROM guild_members WHERE character_id = %d AND guild_id = %d)" % (old, gid)))

    def build(before):
        st = []
        # anyone else holding duty 6 (left over from an older state) steps down with the old leader
        others = [m["character_id"] for m in before.get("guild_members", []) if m["guild_id"] == gid and m["duty"] == 6 and m["character_id"] != cid]
        for o in others:
            st += [common.update_sql("guild_members", {"character_id": o}, {"duty": old_duty}, bump=False),
                   common.update_sql("character_base", {"id": o}, _char_cols(old_duty, gid), bump=False)]
        st += [common.update_sql("guild_members", {"character_id": cid}, {"duty": 6}, bump=False),
               common.update_sql("character_base", {"id": cid}, _char_cols(6, gid), bump=False),
               "UPDATE guilds SET lord_character_id = %d, updated_at = now() WHERE id = %d" % (cid, gid)]
        return st

    return _gwrite(ctx, gid, "guild %s: %s becomes the leader%s" % (g["name"], ch["name"], (
        " (%s -> %s)" % (g["lord_name"], _duty_name(old_duty))) if old_is_member and old != cid else ""), build,
        extra=[cid] + ([old] if old_is_member else []), fields=["guilds.lord_character_id", "guild_members.duty"],
        expected=[{"table": "guilds", "where": {"id": gid}, "values": {"lord_character_id": old}}], target="guild " + g["name"])


@M.post(r"repair/(\d+)")
def repair(ctx, cid):
    """POST /api/m/guild/repair/<cid> - put a character's guild columns back in step with its membership (or clear
    them when it has none). The fix for the 'desync' / 'orphan' checks."""
    ch = _char(ctx, cid)
    m = ch["membership"]
    cid = int(cid)
    if m:
        gid, vals = m["guild_id"], _char_cols(m["duty"], m["guild_id"])
    else:
        gid, vals = ch["consortia"] or 0, _char_cols(0, 0)
    vals = {k: x for k, x in vals.items() if ch.get(k) != x}
    if not vals:
        raise Bad("%s is already in step" % ch["name"])
    return _gwrite(ctx, gid, "repair %s's guild columns (%s)" % (ch["name"], ", ".join("%s %s" % kv for kv in vals.items())),
                   lambda before: [common.update_sql("character_base", {"id": cid}, vals, bump=False)], extra=[cid],
                   fields=["guild_members.*"], target=ch["name"], guard_only=[cid])


# ----------------------------------------------------------------------------------------------- POST: buildings, altars, boost
@M.post(r"g/(\d+)/building")
def building(ctx, gid):
    """POST /api/m/guild/g/<id>/building {type, level (0 = remove), expected_level} - what the altar NPC's Build /
    Upgrade / Delete buttons do (PostgresGuildStore.Buildings.cs), at any level and without cost."""
    gid = int(gid)
    g = _guild(ctx, gid)
    t = _int(ctx.body.get("type"), 0, I32, "building type")
    types = {x["type"]: x for x in _types(ctx)}
    if t not in types:
        raise Bad("building type %s does not exist (guild_building_types)" % t)
    lv = _int(ctx.body.get("level"), 0, types[t]["max"], "%s level" % types[t]["name"])
    cur = ctx.need_rb().pg.json("(SELECT row_to_json(b) FROM guild_buildings b WHERE b.guild_id = %d AND b.building_type = %d)" % (gid, t))
    have = cur["level"] if cur else 0
    if ctx.body.get("expected_level") is not None and _int(ctx.body["expected_level"], 0, 12, "expected_level") != have:
        raise Conflict("%s is level %s now, the page showed %s" % (types[t]["name"], have, ctx.body["expected_level"]))
    if lv == have:
        raise Bad("nothing changed")

    def build(before):
        if lv == 0:
            return ["DELETE FROM guild_buildings WHERE guild_id = %d AND building_type = %d" % (gid, t)]
        if cur:
            return ["UPDATE guild_buildings SET level = %d, updated_at = now() WHERE guild_id = %d AND building_type = %d" % (lv, gid, t)]
        return ["INSERT INTO guild_buildings (guild_id, slot, building_type, level, created_at, updated_at) "
                "VALUES (%d, %d, %d, %d, now(), now())" % (gid, t, t, lv)]

    what = "remove %s" % types[t]["name"] if lv == 0 else "%s level %d%s" % (types[t]["name"], lv, "" if cur else " (built)")
    return _gwrite(ctx, gid, "guild %s: %s" % (g["name"], what), build, fields=["guild_buildings.*"], target="guild " + g["name"])


@M.post(r"g/(\d+)/worship")
def worship(ctx, gid):
    """POST /api/m/guild/g/<id>/worship {cid, type (10-19), points (0 = clear), expected} - a member's offering
    balance on one altar. The drain starts counting from this save (settled_at = now)."""
    gid = int(gid)
    g = _guild(ctx, gid)
    cid = _int(ctx.body.get("cid"), 1, I32, "member")
    t = _int(ctx.body.get("type"), 10, 19, "altar")
    pts = _int(ctx.body.get("points"), 0, I32, "offering points")
    ch = _char(ctx, cid)
    if not ch["membership"] or ch["membership"]["guild_id"] != gid:
        raise Bad("%s is not a member of %s: only members can hold offering points" % (ch["name"], g["name"]))
    cur = ctx.need_rb().pg.json("(SELECT row_to_json(w) FROM guild_altar_worship w WHERE guild_id = %d AND character_id = %d "
                                "AND building_type = %d)" % (gid, cid, t))
    if ctx.body.get("expected") is not None and _int(ctx.body["expected"], 0, I32, "expected") != (cur["points"] if cur else 0):
        raise Conflict("the offering balance is %s now (the hourly drain or an offering moved it); the page showed %s"
                       % (cur["points"] if cur else 0, ctx.body["expected"]))
    if (cur["points"] if cur else 0) == pts and (cur or not pts):
        raise Bad("nothing changed")

    def build(before):
        if pts == 0:
            return ["DELETE FROM guild_altar_worship WHERE guild_id = %d AND character_id = %d AND building_type = %d" % (gid, cid, t)]
        return ["INSERT INTO guild_altar_worship (guild_id, character_id, building_type, points, updated_at, settled_at) "
                "VALUES (%d, %d, %d, %d, now(), now()) ON CONFLICT (guild_id, character_id, building_type) DO UPDATE SET "
                "points = EXCLUDED.points, updated_at = now(), settled_at = now()" % (gid, cid, t, pts)]

    return _gwrite(ctx, gid, "guild %s: %s offers %s points on the %s altar" % (g["name"], ch["name"], GWfmt(pts), _altar_name(t)),
                   build, extra=[cid], fields=["guild_altar_worship.*"], target="guild " + g["name"])


def GWfmt(n):
    return "{:,}".format(int(n))


def _altar_name(t):
    return ((_cat().get("buildings") or {}).get(str(t)) or {}).get("name") or "type %s" % t


@M.post(r"g/(\d+)/boost")
def boost(ctx, gid):
    """POST /api/m/guild/g/<id>/boost {hours, percent | remove: true} - the [Guild] Double EXP benefit (status 1007,
    kind 100) for every member: one character_experience_modifiers row each. The server's EXP code reads it at each
    reward; the trigger turns the hours into online time."""
    gid = int(gid)
    g = _guild(ctx, gid)
    ids = _member_ids(ctx, gid)
    if not ids:
        raise Bad("the guild has no members")
    if _flag(ctx.body.get("remove", False), "remove"):
        def build(before):
            if not before.get("character_experience_modifiers"):
                raise Bad("no member has the guild boost")
            return ["DELETE FROM character_experience_modifiers WHERE kind = %d AND character_id IN (%s)" % (GUILD_BOOST_KIND, _ids(ids))]
        what = "remove the guild EXP boost"
    else:
        hours = _int(ctx.body.get("hours", 16), 1, 24 * 365, "hours")
        pct = _int(ctx.body.get("percent", 100), 1, 1_000_000, "EXP bonus %")
        bp = pct * 100

        def build(before):
            return ["INSERT INTO character_experience_modifiers (character_id, status_id, kind, bonus_basis_points, priority, source, "
                    "activated_at, expires_at, remaining_online_ticks) SELECT id, %d, %d, %d, 1, %s, now(), now() + interval '%d hours', NULL "
                    "FROM character_base WHERE id IN (%s) ON CONFLICT (character_id, kind) DO UPDATE SET status_id = EXCLUDED.status_id, "
                    "bonus_basis_points = EXCLUDED.bonus_basis_points, priority = 1, source = EXCLUDED.source, activated_at = EXCLUDED.activated_at, "
                    "expires_at = EXCLUDED.expires_at, remaining_online_ticks = NULL"
                    % (GUILD_BOOST_STATUS, GUILD_BOOST_KIND, bp, lit("gwadmin:guild:%d" % gid), hours, _ids(ids))]
        what = "guild EXP boost +%s%% for %d online hours to %d members" % (GWfmt(pct), hours, len(ids))
    return _gwrite(ctx, gid, "guild %s: %s" % (g["name"], what), build, fields=["character_experience_modifiers.*"],
                   target="guild " + g["name"], boost=True)


# ----------------------------------------------------------------------------------------------- POST: faction areas
@M.post("faction/control")
def faction_control(ctx):
    """POST /api/m/guild/faction/control {realm, map, camp (0/1, null = nobody), hours, percent} - which camp holds a
    world-boss area's EXP bonus (what a boss kill writes, PostgresWorldBossAreaControlStore). Read at each reward: applies now."""
    b = ctx.body
    rb = ctx.need_rb()
    realm = _int(b.get("realm"), 1, I32, "realm")
    mp = _int(b.get("map"), 0, 32767, "map")
    area = rb.pg.json("(SELECT row_to_json(w) FROM world_boss_areas w WHERE map_id = %d)" % mp)
    if not area:
        raise Bad("map %s has no world boss (world_boss_areas)" % mp)
    if not rb.pg.json("(SELECT to_jsonb(count(*)) FROM server WHERE id = %d)" % realm):
        raise Bad("no realm #%s" % realm)
    camp = b.get("camp")
    # ignore=[]: the glossary classes every column of this table "derived" (a boss kill writes them), which would make
    # Undo restore NOTHING of an update (and report "identical") and never see a boss kill since. Here they are the data.
    sc = [common.scope("faction_area_experience_control", "realm_id = %d AND map_id = %d" % (realm, mp), pk=["realm_id", "map_id"],
                       counters=[], ignore=[])]
    mname = common.CATALOG.name("map", mp) or "map %d" % mp
    if camp is None or camp == "":
        def build(before):
            if not before.get("faction_area_experience_control"):
                raise Bad("nobody controls %s" % mname)
            return ["DELETE FROM faction_area_experience_control WHERE realm_id = %d AND map_id = %d" % (realm, mp)]
        what = "%s: no camp controls the area" % mname
    else:
        camp = _int(camp, 0, 1, "camp")
        hours = _int(b.get("hours", 12), 1, 24 * 365, "hours")
        pct = _int(b.get("percent", int(area["bonus_basis_points"]) // 100), 1, 1_000_000, "EXP bonus %")
        token = "gwadmin-" + uuid.uuid4().hex[:24]

        def build(before):
            return ["INSERT INTO faction_area_experience_control (realm_id, map_id, controlling_camp, boss_template_key, bonus_basis_points, "
                    "activated_at, expires_at, death_token) VALUES (%d, %d, %d, %s, %d, now(), now() + interval '%d hours', %s) "
                    "ON CONFLICT (realm_id, map_id) DO UPDATE SET controlling_camp = EXCLUDED.controlling_camp, boss_template_key = "
                    "EXCLUDED.boss_template_key, bonus_basis_points = EXCLUDED.bonus_basis_points, activated_at = EXCLUDED.activated_at, "
                    "expires_at = EXCLUDED.expires_at, death_token = EXCLUDED.death_token"
                    % (realm, mp, camp, lit(area["boss_template_key"]), pct * 100, hours, lit(token))]
        what = "%s: %s controls the area, +%s%% EXP for %d hours" % (mname, common.CAMP_NAMES.get(camp), GWfmt(pct), hours)
    return ctx.rows_write(sc, "faction area " + what, build, fields=["faction_area_experience_control.*"], advanced=True,
                          applies="now", target="faction area " + mname)
