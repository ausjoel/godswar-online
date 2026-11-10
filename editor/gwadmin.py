#!/usr/bin/env python3
"""gwadmin.py - GodsWar 1.0 account / character editor (python3 stdlib only).

Talks to MariaDB exactly like `gw` does: the mariadb/mysql command line client,
root@127.0.0.1 over TCP, no password, SSL advisory lines filtered by sentinel.
Every character column and every packed string (bags, storage, equipment,
skills, quests) is decoded the way GameServer/GameCore/Player.cpp decodes it;
see README-gwadmin.md for the formats and the evidence.

Databases: `godswar` + `accounts` are the LIVE names and need --live.
`--db-suffix _test` selects godswar_test/accounts_test (the VPS test stack).

    gwadmin.py --db-suffix _test characters
    gwadmin.py --db-suffix _test show the admin
    gwadmin.py --db-suffix _test give the admin "Small Healing Potion" --count 20
    gwadmin.py --live set-level Hero 40          # live DB: --live is mandatory

The GameServer loads a character into memory at world entry and writes ALL of
its tables back at logout and on periodic saves, so edits made while the
account is online are overwritten (or half-applied). Every write command
refuses when the account looks online; see README "Online detection".
"""
import argparse
import datetime
import json
import os
import re
import shutil
import subprocess
import sys

VERSION = "1.0"
HERE = os.path.dirname(os.path.abspath(__file__))

# ------------------------------------------------------------ constants
# GameServer/GameCore/GameData.h (measured, see README)
MAX_BAGS, MAX_BAG_GRID, MAX_APPEND, EQUIP_COUNT, QUEST_COUNT, PLAYER_MAXLV = 4, 24, 5, 12, 1000, 200
STORAGE_WRITE_SLOTS = 32          # MAX_STORAGE_GRID >> 1 written by PackStorageInfo
STORAGE_READ_SLOTS = 54           # CreatePropFromDB accepts up to 54
# enum EquipType, Player.h
EQUIP_SLOTS = ["Head", "Amulet", "Glove", "Armor", "Cuff", "Girdle", "Shoes", "Leggins",
               "Ring1", "Ring2", "MainHand", "OffHand"]
# ItemBaseAttribute.xml Type -> equipment slot(s)
TYPE_TO_SLOTS = {"head": ["Head"], "amulet": ["Amulet"], "glove": ["Glove"], "armor": ["Armor"],
                 "cloth": ["Armor"], "cuff": ["Cuff"], "girdle": ["Girdle"], "shoes": ["Shoes"],
                 "leggins": ["Leggins"], "ring": ["Ring1", "Ring2"], "weapon": ["MainHand"],
                 "shield": ["OffHand"]}
CLASSES = {0: "Warrior", 1: "Champion", 2: "Priest", 3: "Mage"}       # Client/Text/Profession.dat
CAMPS = {0: "Sparta", 1: "Athens"}                                      # SP_INSERT_ROLE / GM_SP_UPDATE_PLAYER_POSIT
CONSORTIA_JOBS = {0: "none", 1: "trainee", 2: "member", 3: "elite", 4: "director", 5: "vice-chairman", 6: "chairman"}
# enum QuestStatus, GameCore/QuestDef.h:38-49; PackQuest writes (int)m_Quests[a].Status verbatim
QUEST_STATUS = {0: "none", 1: "available", 2: "unavailable", 3: "complete", 4: "incomplete", 5: "failed", 6: "over"}
# new-character spawn points (SP_INSERT_ROLE) and GM "move to town" (GM_SP_UPDATE_PLAYER_POSIT)
TOWNS = {
    "athens": (1, 98.0, -178.0, "Athens (Newbie), new-character spawn"),
    "sparta": (51, -108.0, -122.0, "Sparta (Newbie), new-character spawn"),
    "athens-main": (0, 150.0, -146.0, "Athens main city revival point"),
    "sparta-main": (50, 6.0, -2.0, "Sparta main city revival point"),
}
# GameServer log markers (GBK bytes): "<name>进入游戏:[IP:" at world entry, "<name>退出游戏!" at exit
LOG_ENTER = b":[IP:"
LOG_EXIT = "退出游戏".encode("gbk")
ADVISORY = re.compile(r"^(WARNING|Warning):|ssl-verify-server-cert")


class Fail(Exception):
    pass


# ------------------------------------------------------------------ DB
class DB:
    def __init__(self, a):
        self.suffix = a.db_suffix
        self.game = "godswar" + self.suffix
        self.acc = "accounts" + self.suffix
        self.live = (self.suffix == "")
        if self.live and not a.live:
            raise Fail("refusing to touch the LIVE databases (godswar/accounts) without --live; "
                       "use --db-suffix _test for godswar_test/accounts_test")
        self.client = a.mysql or shutil.which("mariadb") or shutil.which("mysql")
        if not self.client:
            raise Fail("no mariadb/mysql client found (install mariadb-client, or pass --mysql PATH)")
        self.base = [self.client, "--host=" + a.host, "--port=%d" % a.port, "--protocol=TCP",
                     "-u", a.user, "-N", "-B"]
        if a.password:
            self.base.append("--password=" + a.password)
        self.dry = a.dry_run
        self.verbose = a.verbose
        self.audit = a.audit_log

    def _exec(self, sql, db):
        cmd = self.base + ["-D", db, "-e", sql]
        if self.verbose:
            sys.stderr.write("SQL[%s]> %s\n" % (db, sql if len(sql) < 600 else sql[:600] + "..."))
        p = subprocess.run(cmd, capture_output=True)
        out = p.stdout.decode("utf-8", "replace")
        err = p.stderr.decode("utf-8", "replace")
        lines = [l for l in (out + err).splitlines() if not ADVISORY.search(l)]
        if p.returncode != 0:
            raise Fail("mysql client failed (%d): %s" % (p.returncode, " | ".join(l for l in lines if l.strip())))
        rows = []
        for l in out.splitlines():
            if ADVISORY.search(l):
                continue
            rows.append([unescape(c) for c in l.split("\t")])
        return rows

    def query(self, sql, db=None):
        return self._exec(sql, db or self.game)

    def one(self, sql, db=None):
        r = self.query(sql, db)
        return r[0] if r else None

    def write(self, sql, db=None, note=""):
        db = db or self.game
        if self.dry:
            print("DRY-RUN SQL[%s]: %s" % (db, sql))
            return
        self._exec(sql, db)
        if self.audit:
            try:
                with open(self.audit, "a", encoding="utf-8") as f:
                    f.write("%s %s %s | %s\n" % (datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"), db, note, sql))
            except OSError:
                pass

    def columns(self, table, db=None):
        return [r[0] for r in self.query("SHOW COLUMNS FROM `%s`" % table, db)]

    def row(self, table, where, db=None):
        cols = self.columns(table, db)
        r = self.one("SELECT %s FROM `%s` WHERE %s" % (",".join("`%s`" % c for c in cols), table, where), db)
        return dict(zip(cols, r)) if r else None


def unescape(s):
    """mysql -B escapes \\t \\n \\\\ \\0 in values; NULL is the word NULL."""
    if s == "NULL":
        return None
    out, i = [], 0
    while i < len(s):
        c = s[i]
        if c == "\\" and i + 1 < len(s):
            n = s[i + 1]
            out.append({"t": "\t", "n": "\n", "0": "\0", "\\": "\\", "r": "\r"}.get(n, n))
            i += 2
        else:
            out.append(c)
            i += 1
    return "".join(out)


def q(s):
    """SQL string literal."""
    if s is None:
        return "NULL"
    s = str(s)
    return "'" + s.replace("\\", "\\\\").replace("'", "\\'").replace("\0", "\\0") + "'"


def num(v, lo=None, hi=None, what="value"):
    try:
        n = int(v)
    except (TypeError, ValueError):
        raise Fail("%s must be an integer, got %r" % (what, v))
    if lo is not None and n < lo or hi is not None and n > hi:
        raise Fail("%s must be between %s and %s, got %d" % (what, lo, hi, n))
    return n


# ------------------------------------------------------------- catalog
class Catalog:
    def __init__(self, path):
        self.path = path
        self.items = self._load("items.json")
        self.skills = self._load("skills.json")
        self.passive = self._load("passive_skills.json").get("skills", {})
        self.maps = self._load("maps.json")
        self.quests = self._load("quests.json")
        self.appends = self._load("appends.json")

    def _load(self, name):
        p = os.path.join(self.path, name)
        if not os.path.exists(p):
            return {}
        with open(p, encoding="utf-8") as f:
            return json.load(f)

    def item(self, iid):
        return self.items.get(str(iid))

    def item_name(self, iid):
        it = self.item(iid)
        return it["name"] if it and it["name"] else "item %s" % iid

    def skill_name(self, sid, passive=False):
        d = (self.passive if passive else self.skills).get(str(sid))
        return d["name"] if d and d.get("name") else "?"

    def map_name(self, mid):
        m = self.maps.get(str(mid))
        return (m.get("display") or m.get("name")) if m else "map %s" % mid

    def find_items(self, text):
        text = text.lower()
        return [v for v in self.items.values() if text in v["name"].lower() or text in v["tag"].lower()]

    def resolve_item(self, spec):
        if str(spec).isdigit():
            it = self.item(int(spec))
            if not it:
                raise Fail("item id %s is not in ItemBaseAttribute.xml (catalog)" % spec)
            return it
        hits = self.find_items(spec)
        exact = [h for h in hits if h["name"].lower() == str(spec).lower()]
        if len(exact) == 1:
            return exact[0]
        if len(hits) == 1:
            return hits[0]
        if not hits:
            raise Fail("no item matches %r (try: item-search)" % spec)
        raise Fail("ambiguous item %r: %s" % (spec, ", ".join("%d=%s" % (h["id"], h["name"]) for h in hits[:12])))


# ------------------------------------------------ packed string codecs
def split_entries(s):
    """'[a,b]#[]#...' -> list of inner strings ('' for an empty entry),
    exactly like CPlayer::CreatePropFromDB walks it."""
    out = []
    s = s or ""
    while s and s != "NULL":
        a = s.find("#")
        chunk = s if a < 0 else s[:a]
        b = chunk.find("]")
        inner = chunk[1:b] if b > 0 else chunk[1:]
        out.append(inner)
        s = "" if a < 0 else s[a + 1:]
    return out


def decode_item(inner):
    """Item entry fields in CreatePropFromDB order (d = 1..11)."""
    if inner == "":
        return None
    f = inner.split(",")
    g = lambda i, d=0: (int(f[i]) if i < len(f) and f[i] != "" else d)
    return {
        "base": g(0, -1),
        "append": [(int(f[i]) if i < len(f) and f[i] != "" else -1) for i in range(1, 6)],
        "binding": g(6), "overlap": g(7), "baselv": g(8), "appendlv": g(9), "cdkey": g(10),
        "nfields": len(f),
    }


def encode_item(it):
    if it is None:
        return "[]#"
    ap = ",".join("" if a == -1 else str(a) for a in it["append"])
    return "[%d,%s,%d,%d,%d,%d,%d]#" % (it["base"], ap, it["binding"], it["overlap"], it["baselv"], it["appendlv"], it["cdkey"])


def new_item(base, count=1, binding=0, baselv=1, appendlv=1, append=None):
    ap = list(append or [])[:MAX_APPEND]
    ap += [-1] * (MAX_APPEND - len(ap))
    return {"base": base, "append": ap, "binding": binding, "overlap": count, "baselv": baselv,
            "appendlv": appendlv, "cdkey": 0}


def decode_bag(s):
    """24 slots (None = empty)."""
    slots = [decode_item(e) for e in split_entries(s)]
    slots = slots[:MAX_BAG_GRID]
    return slots + [None] * (MAX_BAG_GRID - len(slots))


def encode_bag(slots):
    return "".join(encode_item(x) for x in slots[:MAX_BAG_GRID])


def decode_storage(s):
    """(slots, money, nslots): the last entry with a single field is the money."""
    ents = split_entries(s)
    money = 0
    if ents and "," not in ents[-1]:
        money = int(ents[-1] or 0)
        ents = ents[:-1]
    slots = [decode_item(e) for e in ents]
    return slots, money


def encode_storage(slots, money):
    return "".join(encode_item(x) for x in slots) + "[%d]#" % money


def decode_equip(s):
    slots = [decode_item(e) for e in split_entries(s)]
    slots = slots[:EQUIP_COUNT]
    return slots + [None] * (EQUIP_COUNT - len(slots))


def encode_equip(slots):
    out = []
    for it in slots[:EQUIP_COUNT]:
        if it is None:
            out.append("[]#")
        else:
            it = dict(it, binding=1, overlap=1)          # PackEquipInfo writes "1,1," here
            out.append(encode_item(it))
    return "".join(out)


def decode_pairs(s):
    """'[id,val]#...' -> list of (id, val) (active skills: cooldown left; passive: grade)."""
    out = []
    for e in split_entries(s):
        if e == "":
            continue
        f = e.split(",")
        out.append((int(f[0]), int(f[1]) if len(f) > 1 and f[1] != "" else 0))
    return out


def encode_pairs(pairs):
    return "".join("[%d,%d]#" % (a, b) for a, b in pairs)


def decode_allquest(s, camp):
    """allqueststatus: comma-separated unsigned 64-bit words; bit (index) set =
    quest completed. QuestID = index (+1000 for Athens, CPlayer::GetQuest)."""
    done = []
    words = [w for w in (s or "").split(",") if w.strip() != ""]
    for d, w in enumerate(words):
        try:
            v = int(w)
        except ValueError:
            continue
        for c in range(64):
            if v & (1 << c):
                idx = d * 64 + c
                done.append(idx + (QUEST_COUNT if camp == 1 else 0))
    return done, words


def encode_allquest(done_ids, camp, nwords):
    words = [0] * max(nwords, 1)
    for qid in done_ids:
        idx = qid - (QUEST_COUNT if camp == 1 else 0)
        if idx < 0 or idx >= QUEST_COUNT:
            continue
        d, c = divmod(idx, 64)
        while d >= len(words):
            words.append(0)
        words[d] |= (1 << c)
    return ",".join(str(w) for w in words) + ","


def decode_quest_entries(s):
    out = []
    for e in split_entries(s):
        if e == "":
            continue
        f = [x for x in e.split(",")]
        ints = []
        for x in f:
            if x == "":
                continue
            try:
                ints.append(int(x))
            except ValueError:
                ints.append(0)
        rec = {"quest": ints[0] if ints else 0, "completed": ints[1] if len(ints) > 1 else 0,
               "status": ints[2] if len(ints) > 2 else 0, "time": ints[3] if len(ints) > 3 else 0,
               "items": ints[4:8], "creatures": ints[8:12], "explorer": ints[12:]}
        out.append(rec)
    return out


# --------------------------------------------------------- online check
def gamelog_state(path, name):
    """Last world-entry/exit event for `name` in a GameServer log (GBK bytes).
    Returns ('online'|'offline'|'unknown', detail)."""
    if not path:
        return "unknown", "no GameServer log given (--gamelog)"
    try:
        data = open(path, "rb").read()
    except OSError as e:
        return "unknown", "cannot read %s (%s)" % (path, e)
    key = name.encode("gbk", "replace")
    last = None
    pos = 0
    while True:
        i = data.find(key, pos)
        if i < 0:
            break
        tail = data[i + len(key): i + len(key) + 64]
        if tail[:1].isalnum() or tail[:1] == b"_":
            # a longer name that starts with ours (AdminCopy...), not this character
            pos = i + len(key)
            continue
        if tail.startswith(LOG_EXIT):
            last = ("offline", i)
        elif LOG_ENTER in tail and data.rfind(b"\n", 0, i) < i:
            # "<name><gbk 进入游戏>:[IP:x.x.x.x]" (CPlayer::RequestGameDataFromDB)
            last = ("online", i)
        pos = i + len(key)
    if last is None:
        return "offline", "%s: no entry/exit line for %s" % (path, name)
    lineno = data.count(b"\n", 0, last[1]) + 1
    return last[0], "%s line %d: last event is %s" % (path, lineno, "world entry" if last[0] == "online" else "exit")


def ensure_offline(ctx, char, action):
    """Refuse to write a character/account that looks online. See README."""
    reasons = []
    acc = ctx.db.row("accounts_info", "accounts=%s" % q(char["accounts"]), ctx.db.acc)
    if acc and str(acc.get("login_status")) == "1":
        reasons.append("accounts_info.login_status=1 for account %r (no server code ever clears this column; "
                       "if the account is really offline run: account set-online %s 0)" % (char["accounts"], char["accounts"]))
    st, detail = gamelog_state(ctx.gamelog, char["name"])
    if st == "online":
        reasons.append("GameServer log says the character is in the world (%s)" % detail)
    if reasons and not ctx.force:
        raise Fail("%s refused: %s looks ONLINE:\n  - %s\nWrites made while the GameServer holds the character are "
                   "overwritten at its next save/logout. Make the player log out first, or pass --force-offline "
                   "if you are certain." % (action, char["name"], "\n  - ".join(reasons)))
    if reasons:
        print("WARNING: --force-offline: proceeding although: %s" % "; ".join(reasons))
    elif st == "unknown":
        print("note: online check used login_status only (%s)" % detail)


# ------------------------------------------------------------ helpers
class Ctx:
    pass


def find_char(ctx, spec):
    if str(spec).startswith("#") and spec[1:].isdigit():
        where = "id=%d" % int(spec[1:])
    elif str(spec).isdigit():
        where = "id=%d" % int(spec)
    else:
        where = "name=%s" % q(spec)
    row = ctx.db.row("character_base", where)
    if not row:
        raise Fail("no character %r in %s.character_base" % (spec, ctx.db.game))
    return row


def kitbag_row(ctx, cid):
    row = ctx.db.row("character_kitbag", "user_id=%d" % cid)
    if not row:
        raise Fail("character %d has no character_kitbag row (corrupt?); the server expects exactly one" % cid)
    return row


def update_base(ctx, char, assignments, note):
    sets = ", ".join("`%s`=%s" % (k, v) for k, v in assignments.items())
    ctx.db.write("UPDATE character_base SET %s WHERE id=%d" % (sets, int(char["id"])), note=note)


def update_kitbag(ctx, cid, col, value, note):
    assert col in ("kitbag_1", "kitbag_2", "kitbag_3", "kitbag_4", "storage", "equip")
    ctx.db.write("UPDATE character_kitbag SET `%s`=%s WHERE user_id=%d" % (col, q(value), cid), note=note)


def fmt_item(ctx, it, idx=None):
    if it is None:
        return None
    cat = ctx.cat
    meta = cat.item(it["base"]) or {}
    s = "%d x %s (id %d" % (it["overlap"], cat.item_name(it["base"]), it["base"])
    if meta.get("type"):
        s += ", %s" % meta["type"]
    if it["binding"]:
        s += ", bound"
    if it["baselv"] not in (0, 1) or it["appendlv"] not in (0, 1):
        s += ", baseLv %d appLv %d" % (it["baselv"], it["appendlv"])
    aps = [a for a in it["append"] if a != -1]
    if aps:
        s += ", appends " + ",".join("%d:%s" % (a, (cat.appends.get(str(a)) or {}).get("tag", "?")) for a in aps)
    if it.get("cdkey"):
        s += ", cdkey %d" % it["cdkey"]
    return s + ")"


def table(rows, headers):
    if not rows:
        print("(none)")
        return
    rows = [[("" if c is None else str(c)) for c in r] for r in rows]
    w = [max(len(h), *(len(r[i]) for r in rows)) for i, h in enumerate(headers)]
    print("  ".join(h.ljust(w[i]) for i, h in enumerate(headers)))
    for r in rows:
        print("  ".join(r[i].ljust(w[i]) for i in range(len(headers))))


def backup_dir(ctx):
    d = ctx.backup_dir
    os.makedirs(d, exist_ok=True)
    return d


# ------------------------------------------------------------ commands
def cmd_accounts(ctx, a):
    where = ""
    if a.search:
        where = "WHERE a.accounts LIKE %s" % q("%" + a.search + "%")
    rows = ctx.db.query(
        "SELECT a.id,a.accounts,a.login_status,a.accounts_status,a.last_login_time,a.last_login_IP,"
        "(SELECT COUNT(*) FROM `%s`.character_base b WHERE b.accounts=a.accounts),"
        "IFNULL(g.IsDel,'no gamearea row') FROM accounts_info a LEFT JOIN gamearea_info g ON g.PassName=a.accounts %s "
        "ORDER BY a.id" % (ctx.db.game, where), ctx.db.acc)
    if a.json:
        print(json.dumps(rows, ensure_ascii=False, indent=1))
        return
    table(rows, ["id", "account", "login_status", "status", "last_login", "last_ip", "chars", "IsDel"])
    print("\nlogin_status is never written by the servers (stale); IsDel=1 blocks login (gamearea_info).")


def cmd_characters(ctx, a):
    where = []
    if a.account:
        where.append("accounts=%s" % q(a.account))
    if a.search:
        where.append("name LIKE %s" % q("%" + a.search + "%"))
    sql = ("SELECT id,accounts,name,gender,GM,camp,profession,fighter_job_lv,fighter_job_exp,Map,Pos_X,Pos_Z,Money,Stone,"
           "SkillPoint,LastLogin_time FROM character_base %s ORDER BY id" % (("WHERE " + " AND ".join(where)) if where else ""))
    rows = ctx.db.query(sql)
    acc_status = {r[0]: r[1] for r in ctx.db.query("SELECT accounts,login_status FROM accounts_info", ctx.db.acc)}
    out = []
    for r in rows:
        cid, acc, name, gender, gm, camp, prof, lv, exp, mp, x, z, money, stone, sp, last = r
        st, _ = gamelog_state(ctx.gamelog, name)
        online = "log:online" if st == "online" else ("login_status=1" if acc_status.get(acc) == "1" else "offline")
        out.append([cid, acc, name, lv, CLASSES.get(int(prof), prof), CAMPS.get(int(camp), camp),
                    "%s (%s)" % (ctx.cat.map_name(mp), mp), "%.0f,%.0f" % (float(x), float(z)), money, stone, sp,
                    "GM" if gm == "1" else "", online, last])
    if a.json:
        print(json.dumps(out, ensure_ascii=False, indent=1))
        return
    table(out, ["id", "account", "name", "lv", "class", "camp", "map", "pos", "money", "stone", "skillpts", "gm", "online", "last_login"])


def decode_character(ctx, char):
    cid = int(char["id"])
    camp = int(char["camp"])
    kb = kitbag_row(ctx, cid)
    magic = ctx.db.row("user_magic", "user_id=%d" % cid) or {}
    skill = ctx.db.row("user_skill", "user_id=%d" % cid) or {}
    quest = ctx.db.row("user_quest", "id=%d" % cid) or {}
    rel = ctx.db.row("user_relation", "user_id=%d" % cid) or {}
    altar = ctx.db.row("user_altar", "user_id=%d" % cid) or {}
    acc = ctx.db.row("accounts_info", "accounts=%s" % q(char["accounts"]), ctx.db.acc) or {}
    bags = {}
    for i in range(1, MAX_BAGS + 1):
        s = kb.get("kitbag_%d" % i)
        bags[i] = None if s is None else decode_bag(s)
    storage, smoney = decode_storage(kb.get("storage") or "")
    equip = decode_equip(kb.get("equip") or "")
    done, words = decode_allquest(quest.get("allqueststatus") or "", camp)
    d = {
        "base": char, "account": acc,
        "class": CLASSES.get(int(char["profession"]), char["profession"]),
        "camp_name": CAMPS.get(camp, camp), "map_name": ctx.cat.map_name(char["Map"]),
        "bags": bags, "storage": storage, "storage_money": smoney, "equip": equip,
        "active_skills": decode_pairs(magic.get("magic_info") or ""),
        "passive_skills": decode_pairs(skill.get("skill_info") or ""),
        "quests_done": done, "quest_words": words,
        "quests_active": decode_quest_entries(quest.get("activestatus") or ""),
        "quests_timed": decode_quest_entries(quest.get("timequest") or ""),
        "friends": [e for e in split_entries(rel.get("friend") or "") if e],
        "blacklist": [e for e in split_entries(rel.get("blacklist") or "") if e],
        "altar": altar, "raw": {"character_kitbag": kb, "user_magic": magic, "user_skill": skill,
                                "user_quest": quest, "user_relation": rel, "user_altar": altar},
    }
    return d


def cmd_show(ctx, a):
    char = find_char(ctx, a.name)
    d = decode_character(ctx, char)
    if a.json:
        print(json.dumps(d, ensure_ascii=False, indent=1, default=str))
        return
    b = d["base"]
    cat = ctx.cat
    st, detail = gamelog_state(ctx.gamelog, b["name"])
    print("== %s  (id %s, account %s, login_status=%s, gamelog: %s)" % (
        b["name"], b["id"], b["accounts"], d["account"].get("login_status", "?"), st))
    print("   %s %s, %s, GM=%s, level %s (exp %s), skill points %s (skill exp %s)" % (
        d["camp_name"], d["class"], b["gender"], b["GM"], b["fighter_job_lv"], b["fighter_job_exp"], b["SkillPoint"], b["SkillExp"]))
    print("   map %s (%s) at (%.1f, %.1f); money %s, stone %s; HP %s/%s MP %s/%s (Max recomputed at login)" % (
        d["map_name"], b["Map"], float(b["Pos_X"]), float(b["Pos_Z"]), b["Money"], b["Stone"], b["curHP"], b["MaxHP"], b["curMP"], b["MaxMP"]))
    print("   prestige %s, earl_rank %s, belief %s, consortia %s job %s (%s) contribute %s, store_num %s, bag_num %s" % (
        b["prestige"], b["earl_rank"], b["belief"], b["consortia"], b["consortia_job"],
        CONSORTIA_JOBS.get(int(b["consortia_job"]), "?"), b["consortia_contribute"], b["store_num"], b["bag_num"]))
    print("   hair %s face %s status %s registered %s last login %s online-minutes(mutetime) %s" % (
        b["hair_style"], b["face_shap"], b["status"], b["Register_time"], b["LastLogin_time"], b["mutetime"]))
    print("-- equipment")
    for i, it in enumerate(d["equip"]):
        if it:
            print("   %-9s %s" % (EQUIP_SLOTS[i], fmt_item(ctx, it)))
    for i in range(1, MAX_BAGS + 1):
        bag = d["bags"][i]
        if bag is None:
            print("-- bag %d: not activated (NULL)" % i)
            continue
        n = sum(1 for x in bag if x)
        print("-- bag %d: %d/%d slots used" % (i, n, MAX_BAG_GRID))
        for s, it in enumerate(bag):
            if it:
                print("   slot %2d  %s" % (s + 1, fmt_item(ctx, it)))
    print("-- storage: %d slots, %d used, money %d" % (len(d["storage"]), sum(1 for x in d["storage"] if x), d["storage_money"]))
    for s, it in enumerate(d["storage"]):
        if it:
            print("   slot %2d  %s" % (s + 1, fmt_item(ctx, it)))
    print("-- active skills (user_magic: id, cooldown seconds left)")
    print("   " + (", ".join("%d %s%s" % (i, cat.skill_name(i), " cd%d" % v if v else "") for i, v in d["active_skills"]) or "(none)"))
    print("-- passive skills (user_skill: id, grade)")
    print("   " + (", ".join("%d %s%s" % (i, cat.skill_name(i, True), " g%d" % v if v else "") for i, v in d["passive_skills"]) or "(none)"))
    print("-- quests: %d completed (bitmask words %s)" % (len(d["quests_done"]), d["quest_words"]))
    if d["quests_done"]:
        print("   " + ", ".join("%d %s" % (qid, (cat.quests.get(str(qid)) or {}).get("title", "")) for qid in d["quests_done"][:60])
              + (" ..." if len(d["quests_done"]) > 60 else ""))
    for label, key in (("accepted (activestatus)", "quests_active"), ("timed/repeat (timequest)", "quests_timed")):
        qs = d[key]
        print("-- quests %s: %d" % (label, len(qs)))
        for r in qs[:40]:
            print("   %d %s status=%s(%s) completed=%s time=%s items=%s creatures=%s" % (
                r["quest"], (cat.quests.get(str(r["quest"])) or {}).get("title", ""), r["status"],
                QUEST_STATUS.get(r["status"], "?"), r["completed"], r["time"], r["items"], r["creatures"]))
        if len(qs) > 40:
            print("   ... %d more" % (len(qs) - 40))
    print("-- friends: %s" % (", ".join(d["friends"]) or "(none)"))
    print("-- blacklist: %s" % (", ".join(d["blacklist"]) or "(none)"))
    al = d["altar"]
    if al:
        print("-- altars: %s" % " ".join("%s=%s" % (k, v) for k, v in al.items() if k != "user_id"))
    print("-- pets/mounts/life skills: the 2008 server has no such tables or columns (CLAUDE.md key fact)")


def cmd_set_level(ctx, a):
    char = find_char(ctx, a.name)
    lv = num(a.level, 1, PLAYER_MAXLV, "level")
    ensure_offline(ctx, char, "set-level")
    sets = {"fighter_job_lv": lv, "scholar_job_lv": lv}
    exp = num(a.exp, 0, 2 ** 32 - 1, "exp") if a.exp is not None else 0
    sets["fighter_job_exp"] = sets["scholar_job_exp"] = exp
    update_base(ctx, char, sets, "set-level %s -> %d" % (char["name"], lv))
    print("%s: level %s -> %d, exp -> %d (MaxHP/MaxMP are recomputed by the server at login: CPlayer::SetAtttibute)" % (
        char["name"], char["fighter_job_lv"], lv, exp))


def cmd_set_exp(ctx, a):
    char = find_char(ctx, a.name)
    exp = num(a.exp, 0, 2 ** 32 - 1, "exp")
    ensure_offline(ctx, char, "set-exp")
    update_base(ctx, char, {"fighter_job_exp": exp, "scholar_job_exp": exp}, "set-exp %s -> %d" % (char["name"], exp))
    print("%s: exp %s -> %d" % (char["name"], char["fighter_job_exp"], exp))


def cmd_set_money(ctx, a):
    char = find_char(ctx, a.name)
    sets = {}
    if a.money is not None:
        sets["Money"] = num(a.money, 0, 2 ** 32 - 1, "money")
    if a.stone is not None:
        sets["Stone"] = num(a.stone, 0, 2 ** 32 - 1, "stone")
    if a.storage_money is not None:
        sm = num(a.storage_money, 0, 2 ** 31 - 1, "storage money")
    if not sets and a.storage_money is None:
        raise Fail("nothing to set (give MONEY and/or --stone/--storage-money)")
    ensure_offline(ctx, char, "set-money")
    if sets:
        update_base(ctx, char, sets, "set-money %s %s" % (char["name"], sets))
    if a.storage_money is not None:
        kb = kitbag_row(ctx, int(char["id"]))
        slots, _ = decode_storage(kb.get("storage") or "")
        update_kitbag(ctx, int(char["id"]), "storage", encode_storage(slots, sm), "storage money %s -> %d" % (char["name"], sm))
        sets["storage_money"] = sm
    print("%s: %s" % (char["name"], ", ".join("%s=%s" % kv for kv in sets.items())))


def cmd_set_skillpoints(ctx, a):
    char = find_char(ctx, a.name)
    sets = {"SkillPoint": num(a.points, 0, 2 ** 32 - 1, "skill points")}
    if a.skillexp is not None:
        sets["SkillExp"] = num(a.skillexp, 0, 2 ** 32 - 1, "skill exp")
    ensure_offline(ctx, char, "set-skillpoints")
    update_base(ctx, char, sets, "set-skillpoints %s %s" % (char["name"], sets))
    print("%s: %s" % (char["name"], sets))


def cmd_set_pos(ctx, a):
    char = find_char(ctx, a.name)
    mid = num(a.map, 0, 255, "map id")
    x, z = float(a.x), float(a.z)
    if not ctx.cat.maps.get(str(mid)):
        print("warning: map %d is not in Scene/SceneDefine.ini (catalog); the server will not know it" % mid)
    if abs(x) >= 240 or abs(z) >= 240:
        raise Fail("coordinates must be inside (-240, 240): the server resets anything else to the camp revival point")
    ensure_offline(ctx, char, "set-pos")
    update_base(ctx, char, {"Map": mid, "Pos_X": "%.5f" % x, "Pos_Z": "%.5f" % z}, "set-pos %s" % char["name"])
    print("%s: now on map %d (%s) at (%.1f, %.1f)" % (char["name"], mid, ctx.cat.map_name(mid), x, z))


def cmd_unstick(ctx, a):
    char = find_char(ctx, a.name)
    camp = int(char["camp"])
    town = a.town
    if town == "auto":
        town = "athens" if camp == 1 else "sparta"
    if a.main:
        town += "-main"
    mid, x, z, desc = TOWNS[town]
    ensure_offline(ctx, char, "unstick")
    update_base(ctx, char, {"Map": mid, "Pos_X": "%.5f" % x, "Pos_Z": "%.5f" % z}, "unstick %s -> %s" % (char["name"], town))
    print("%s: moved to %s: map %d (%s) at (%.0f, %.0f)" % (char["name"], desc, mid, ctx.cat.map_name(mid), x, z))


def load_target(ctx, kb, a, bag_num):
    """Return (column, slots, extra) for --bag N | --storage | --equip."""
    if a.storage:
        slots, money = decode_storage(kb.get("storage") or "")
        if not slots:
            slots = [None] * 18          # the server writes 18 empty entries for a fresh character
        return "storage", slots, money
    if getattr(a, "equip", None):
        return "equip", decode_equip(kb.get("equip") or ""), None
    bag = num(a.bag, 1, MAX_BAGS, "bag")
    col = "kitbag_%d" % bag
    s = kb.get(col)
    if s is None:
        if bag > bag_num:
            raise Fail("bag %d is not activated for this character (bag_num=%d, column NULL); "
                       "use --bag 1, or `set-bags NAME %d` first" % (bag, bag_num, bag))
        s = ""
    return col, decode_bag(s), None


def save_target(ctx, cid, col, slots, extra, note):
    if col == "storage":
        update_kitbag(ctx, cid, col, encode_storage(slots, extra), note)
    elif col == "equip":
        update_kitbag(ctx, cid, col, encode_equip(slots), note)
    else:
        update_kitbag(ctx, cid, col, encode_bag(slots), note)


def cmd_give(ctx, a):
    char = find_char(ctx, a.name)
    cid = int(char["id"])
    item = ctx.cat.resolve_item(a.item)
    count = num(a.count, 1, 10 ** 6, "count")
    maxstack = max(1, int(item.get("overlap") or 1))
    binding = 1 if a.bind else 0
    appends = [num(x, -1, 10 ** 6, "append") for x in a.append.split(",")] if a.append else None
    kb = kitbag_row(ctx, cid)
    ensure_offline(ctx, char, "give")
    if a.equip:
        slot = a.equip
        if slot not in EQUIP_SLOTS:
            raise Fail("equip slot must be one of %s" % ", ".join(EQUIP_SLOTS))
        allowed = TYPE_TO_SLOTS.get(item.get("type", ""), [])
        if slot not in allowed and not a.force:
            raise Fail("%s is type %r which goes in %s, not %s (use --force to override)" % (
                item["name"], item.get("type"), allowed or "no equipment slot", slot))
        slots = decode_equip(kb.get("equip") or "")
        idx = EQUIP_SLOTS.index(slot)
        old = slots[idx]
        slots[idx] = new_item(item["id"], 1, 1, a.level, a.append_level, appends)
        save_target(ctx, cid, "equip", slots, None, "equip %s %s=%s" % (char["name"], slot, item["id"]))
        print("%s: %s <- %s%s" % (char["name"], slot, fmt_item(ctx, slots[idx]),
                                  (" (replaced %s)" % fmt_item(ctx, old)) if old else ""))
        return
    col, slots, extra = load_target(ctx, kb, a, int(char["bag_num"]))
    placed = []
    remaining = count
    if maxstack > 1 and not a.no_stack:
        for i, it in enumerate(slots):
            if remaining <= 0:
                break
            if it and it["base"] == item["id"] and it["binding"] == binding and it["overlap"] < maxstack and \
                    it["baselv"] == a.level and it["appendlv"] == a.append_level:
                add = min(maxstack - it["overlap"], remaining)
                it["overlap"] += add
                remaining -= add
                placed.append((i, add, "stacked"))
    for i, it in enumerate(slots):
        if remaining <= 0:
            break
        if it is None:
            n = min(maxstack, remaining)
            slots[i] = new_item(item["id"], n, binding, a.level, a.append_level, appends)
            remaining -= n
            placed.append((i, n, "new"))
    if remaining > 0:
        raise Fail("not enough free slots in %s for %d more %s (nothing written)" % (col, remaining, item["name"]))
    save_target(ctx, cid, col, slots, extra, "give %s %dx%d" % (char["name"], count, item["id"]))
    for i, n, how in placed:
        print("%s: %s slot %d: +%d %s (%s)" % (char["name"], col, i + 1, n, item["name"], how))


def cmd_remove(ctx, a):
    char = find_char(ctx, a.name)
    cid = int(char["id"])
    kb = kitbag_row(ctx, cid)
    ensure_offline(ctx, char, "remove")
    if a.equip:
        if a.equip not in EQUIP_SLOTS:
            raise Fail("equip slot must be one of %s" % ", ".join(EQUIP_SLOTS))
        slots = decode_equip(kb.get("equip") or "")
        idx = EQUIP_SLOTS.index(a.equip)
        if slots[idx] is None:
            raise Fail("%s slot is already empty" % a.equip)
        print("%s: removed %s from %s" % (char["name"], fmt_item(ctx, slots[idx]), a.equip))
        slots[idx] = None
        save_target(ctx, cid, "equip", slots, None, "remove equip %s %s" % (char["name"], a.equip))
        return
    col, slots, extra = load_target(ctx, kb, a, int(char["bag_num"]))
    if a.item:
        item = ctx.cat.resolve_item(a.item)
        hit = [i for i, it in enumerate(slots) if it and it["base"] == item["id"]]
        if not hit:
            raise Fail("no %s in %s" % (item["name"], col))
        for i in hit:
            print("%s: %s slot %d: removed %s" % (char["name"], col, i + 1, fmt_item(ctx, slots[i])))
            slots[i] = None
    else:
        if a.slot is None:
            raise Fail("give --slot N or --item ID/NAME")
        i = num(a.slot, 1, len(slots), "slot") - 1
        it = slots[i]
        if it is None:
            raise Fail("%s slot %d is empty" % (col, i + 1))
        if a.count and a.count < it["overlap"]:
            it["overlap"] -= a.count
            print("%s: %s slot %d: -%d, now %s" % (char["name"], col, i + 1, a.count, fmt_item(ctx, it)))
        else:
            print("%s: %s slot %d: removed %s" % (char["name"], col, i + 1, fmt_item(ctx, it)))
            slots[i] = None
    save_target(ctx, cid, col, slots, extra, "remove %s %s" % (char["name"], col))


def cmd_set_bags(ctx, a):
    char = find_char(ctx, a.name)
    n = num(a.count, 1, MAX_BAGS, "bag count")
    cid = int(char["id"])
    kb = kitbag_row(ctx, cid)
    ensure_offline(ctx, char, "set-bags")
    for i in range(2, n + 1):
        if kb.get("kitbag_%d" % i) is None:
            update_kitbag(ctx, cid, "kitbag_%d" % i, encode_bag([None] * MAX_BAG_GRID), "init bag %d %s" % (i, char["name"]))
    update_base(ctx, char, {"bag_num": n}, "set-bags %s %d" % (char["name"], n))
    print("%s: bag_num %s -> %d; empty bag strings created where the column was NULL" % (char["name"], char["bag_num"], n))


def cmd_skill(ctx, a):
    char = find_char(ctx, a.name)
    cid = int(char["id"])
    passive = a.passive
    tbl, col, key = ("user_skill", "skill_info", "user_id") if passive else ("user_magic", "magic_info", "user_id")
    row = ctx.db.row(tbl, "%s=%d" % (key, cid)) or {}
    pairs = decode_pairs(row.get(col) or "")
    sid = num(a.id, 0, 10 ** 6, "skill id")
    name = ctx.cat.skill_name(sid, passive)
    if name == "?" and not a.force:
        raise Fail("skill %d is not in %s (catalog); --force to add anyway" % (sid, "Skill.ini" if passive else "Magic.ini"))
    ensure_offline(ctx, char, "skill")
    ids = [p[0] for p in pairs]
    if a.action == "add":
        val = num(a.value, 0, 10 ** 6, "grade/cooldown") if a.value is not None else 0
        if sid in ids:
            pairs = [(i, (val if i == sid else v)) for i, v in pairs]
        else:
            pairs.append((sid, val))
    else:
        if sid not in ids:
            raise Fail("%s does not have skill %d" % (char["name"], sid))
        pairs = [(i, v) for i, v in pairs if i != sid]
    s = encode_pairs(pairs)
    if row:
        ctx.db.write("UPDATE `%s` SET `%s`=%s WHERE %s=%d" % (tbl, col, q(s), key, cid), note="skill %s" % char["name"])
    else:
        ctx.db.write("INSERT INTO `%s` (`%s`,`%s`) VALUES (%d,%s)" % (tbl, key, col, cid, q(s)), note="skill %s" % char["name"])
    print("%s: %s %s skill %d %s -> %s" % (char["name"], a.action, "passive" if passive else "active", sid, name, s))


def cmd_quest(ctx, a):
    char = find_char(ctx, a.name)
    cid = int(char["id"])
    camp = int(char["camp"])
    qid = num(a.quest, 0, 2 * QUEST_COUNT - 1, "quest id")
    idx = qid - (QUEST_COUNT if camp == 1 else 0)
    if idx < 0 or idx >= QUEST_COUNT:
        raise Fail("quest %d is not in this character's camp range (%s: %d..%d)" % (
            qid, CAMPS.get(camp), QUEST_COUNT if camp == 1 else 0, (QUEST_COUNT if camp == 1 else 0) + QUEST_COUNT - 1))
    row = ctx.db.row("user_quest", "id=%d" % cid)
    if not row:
        raise Fail("no user_quest row for %s" % char["name"])
    ensure_offline(ctx, char, "quest")
    done, words = decode_allquest(row.get("allqueststatus") or "", camp)
    if a.state == "done":
        if qid not in done:
            done.append(qid)
    else:
        done = [d for d in done if d != qid]
    sets = ["allqueststatus=%s" % q(encode_allquest(done, camp, len(words)))]
    if a.state == "none" or a.drop_active:
        for col in ("activestatus", "timequest"):
            ents = [e for e in split_entries(row.get(col) or "") if e and e.split(",")[0] != str(qid)]
            sets.append("%s=%s" % (col, q("".join("[%s]#" % e for e in ents))))
    ctx.db.write("UPDATE user_quest SET %s WHERE id=%d" % (", ".join(sets), cid), note="quest %s %d %s" % (char["name"], qid, a.state))
    print("%s: quest %d %s -> %s (%d completed quests now)" % (
        char["name"], qid, (ctx.cat.quests.get(str(qid)) or {}).get("title", ""), a.state, len(done)))


def cmd_set_gm(ctx, a):
    char = find_char(ctx, a.name)
    flag = num(a.flag, 0, 1, "GM flag")
    ensure_offline(ctx, char, "set-gm")
    update_base(ctx, char, {"GM": flag}, "set-gm %s %d" % (char["name"], flag))
    print("%s: GM %s -> %d (character_base.GM; read by DealWith_Select_RoleAttribute -> SetGM; chat commands "
          "/addmoney /move /fly /levelup /kick /ping /placard /online need it)" % (char["name"], char["GM"], flag))


def cmd_rename(ctx, a):
    char = find_char(ctx, a.name)
    new = a.new_name.strip()
    if not new or len(new.encode("gbk", "replace")) > 15:
        raise Fail("new name must be 1..15 bytes (MAX_ROLENAME_SIZE 16 incl. NUL)")
    if any(c in new for c in "[]#,'\"\\\t\n"):
        raise Fail("name must not contain [ ] # , quotes or backslashes (they would break the packed relation strings)")
    if ctx.db.one("SELECT id FROM character_base WHERE name=%s" % q(new)):
        raise Fail("a character named %r already exists" % new)
    ensure_offline(ctx, char, "rename")
    old = char["name"]
    update_base(ctx, char, {"name": q(new)}, "rename %s -> %s" % (old, new))
    for col in ("friend", "blacklist"):
        ctx.db.write("UPDATE user_relation SET `%s`=REPLACE(`%s`,%s,%s) WHERE `%s` LIKE %s" % (
            col, col, q("[%s]#" % old), q("[%s]#" % new), col, q("%%[%s]#%%" % old)), note="rename relation refs")
    ctx.db.write("UPDATE gm_speakforbid SET rolename=%s WHERE rolename=%s" % (q(new), q(old)), note="rename speakforbid")
    print("renamed %s -> %s (character_base.name, friend/blacklist references, gm_speakforbid)" % (old, new))


def dump_character(ctx, char):
    cid = int(char["id"])
    tables = {"character_base": "id", "character_kitbag": "user_id", "user_magic": "user_id", "user_skill": "user_id",
              "user_quest": "id", "user_relation": "user_id", "user_altar": "user_id"}
    raw = {}
    for t, k in tables.items():
        raw[t] = ctx.db.row(t, "%s=%d" % (k, cid))
    d = decode_character(ctx, char)
    d.pop("raw", None)
    return {"gwadmin": VERSION, "dumped": datetime.datetime.now().isoformat(timespec="seconds"),
            "database": ctx.db.game, "character": char["name"], "id": cid, "tables": raw, "decoded": d}


def cmd_dump(ctx, a):
    char = find_char(ctx, a.name)
    d = dump_character(ctx, char)
    text = json.dumps(d, ensure_ascii=False, indent=1, default=str)
    if a.output == "-":
        print(text)
        return
    out = a.output or os.path.join(backup_dir(ctx), "%s-%s.json" % (char["name"], datetime.datetime.now().strftime("%Y%m%d-%H%M%S")))
    with open(out, "w", encoding="utf-8") as f:
        f.write(text)
    print("dumped %s (id %s) to %s" % (char["name"], char["id"], out))


def cmd_delete(ctx, a):
    char = find_char(ctx, a.name)
    ensure_offline(ctx, char, "delete")
    out = os.path.join(backup_dir(ctx), "%s-%s-deleted.json" % (char["name"], datetime.datetime.now().strftime("%Y%m%d-%H%M%S")))
    with open(out, "w", encoding="utf-8") as f:
        f.write(json.dumps(dump_character(ctx, char), ensure_ascii=False, indent=1, default=str))
    print("backup written to %s" % out)
    if not a.yes:
        raise Fail("add --yes to really delete %s (id %s) from %s (7 tables, via SP_DELETE_ROLE_INFO like the server)" % (
            char["name"], char["id"], ctx.db.game))
    ctx.db.write("CALL SP_DELETE_ROLE_INFO(%d)" % int(char["id"]), note="delete %s" % char["name"])
    print("deleted %s (id %s); restore with: restore %s" % (char["name"], char["id"], out))


def cmd_restore(ctx, a):
    try:
        with open(a.file, encoding="utf-8") as f:
            d = json.load(f)
    except (OSError, ValueError) as e:
        raise Fail("cannot read dump %s: %s" % (a.file, e))
    tables = d.get("tables") or {}
    base = tables.get("character_base")
    if not base:
        raise Fail("%s has no character_base row" % a.file)
    name = a.as_name or base["name"]
    account = a.account or base["accounts"]
    if not ctx.db.one("SELECT 1 FROM accounts_info WHERE accounts=%s" % q(account), ctx.db.acc):
        raise Fail("account %r does not exist in %s.accounts_info" % (account, ctx.db.acc))
    existing = ctx.db.one("SELECT id FROM character_base WHERE name=%s" % q(name))
    old_id = int(base["id"])
    if existing and not a.overwrite:
        raise Fail("character %r exists (id %s); add --overwrite to replace all 7 rows, or --as-name NEW" % (name, existing[0]))
    if existing:
        ensure_offline(ctx, {"name": name, "accounts": account, "id": existing[0]}, "restore")
    if not a.yes:
        raise Fail("add --yes to write %r (account %s) into %s" % (name, account, ctx.db.game))
    new_id = old_id
    if a.new_id or (not existing and ctx.db.one("SELECT 1 FROM character_base WHERE id=%d" % old_id)):
        new_id = None            # let AUTO_INCREMENT pick
    if existing:
        new_id = int(existing[0])
        ctx.db.write("CALL SP_DELETE_ROLE_INFO(%d)" % new_id, note="restore: clear %s" % name)
    row = dict(base)
    row["name"], row["accounts"] = name, account
    cols = [c for c in row if not (c == "id" and new_id is None)]
    if new_id is not None:
        row["id"] = new_id
    ctx.db.write("INSERT INTO character_base (%s) VALUES (%s)" % (
        ",".join("`%s`" % c for c in cols), ",".join(q(row[c]) for c in cols)), note="restore base %s" % name)
    if new_id is None:
        r = ctx.db.one("SELECT id FROM character_base WHERE name=%s" % q(name))
        if not r and ctx.db.dry:
            r = ["<new id>"]
        new_id = r[0]
    keys = {"character_kitbag": "user_id", "user_magic": "user_id", "user_skill": "user_id",
            "user_quest": "id", "user_relation": "user_id", "user_altar": "user_id"}
    for t, k in keys.items():
        r = tables.get(t)
        if not r:
            continue
        r = dict(r)
        r[k] = new_id
        cols = list(r)
        ctx.db.write("REPLACE INTO `%s` (%s) VALUES (%s)" % (t, ",".join("`%s`" % c for c in cols),
                                                             ",".join(q(r[c]) for c in cols)), note="restore %s %s" % (t, name))
    print("restored %s as %r (id %s, account %s) into %s" % (a.file, name, new_id, account, ctx.db.game))


# ---- accounts
def account_row(ctx, name):
    r = ctx.db.row("accounts_info", "accounts=%s" % q(name), ctx.db.acc)
    if not r:
        raise Fail("no account %r in %s.accounts_info" % (name, ctx.db.acc))
    return r


def cmd_account(ctx, a):
    sub = a.subcmd
    if sub == "show":
        r = account_row(ctx, a.account)
        g = ctx.db.row("gamearea_info", "PassName=%s" % q(a.account), ctx.db.acc)
        reg = ctx.db.row("accounts_register", "PassName=%s" % q(a.account), ctx.db.acc)
        bans = ctx.db.query("SELECT bandate,unbandate,bannedby,banreason FROM accounts_ban WHERE id=%s" % q(g["GID"] if g else 0), ctx.db.acc)
        print("accounts_info:     %s" % json.dumps(r, ensure_ascii=False, default=str))
        print("gamearea_info:     %s" % json.dumps(g, ensure_ascii=False, default=str))
        print("accounts_register: %s" % json.dumps(reg, ensure_ascii=False, default=str))
        print("accounts_ban:      %s" % (bans or "(none)"))
        chars = ctx.db.query("SELECT id,name,fighter_job_lv,profession,camp,Map FROM character_base WHERE accounts=%s" % q(a.account))
        print("characters:        %s" % ", ".join("%s(#%s lv%s %s %s)" % (c[1], c[0], c[2], CLASSES.get(int(c[3])), CAMPS.get(int(c[4]))) for c in chars) or "(none)")
        return
    if sub == "create":
        name, pw = a.account, a.password
        if not re.match(r"^[A-Za-z0-9_]{1,32}$", name):
            raise Fail("account: letters, numbers, underscore, 1..32 chars")
        if not re.match(r"^[A-Za-z0-9_]{1,32}$", pw):
            raise Fail("password: letters, numbers, underscore, 1..32 chars (the client sends it in a 32-byte field)")
        if ctx.db.one("SELECT 1 FROM gamearea_info WHERE PassName=%s" % q(name), ctx.db.acc) or \
                ctx.db.one("SELECT 1 FROM accounts_info WHERE accounts=%s" % q(name), ctx.db.acc):
            raise Fail("account %r already exists" % name)
        ctx.db.write("INSERT INTO accounts_info (accounts,password,login_status,last_login_time,last_logout_time,last_login_IP,"
                     "last_login_mac,total_online_time,accounts_status) VALUES (%s,%s,0,NOW(),NOW(),'127.0.0.1','',0,1);"
                     "INSERT INTO gamearea_info (GameArea,AccID,PassName,PassPwd,Balance,IsDel,IsDraw) VALUES (1,LAST_INSERT_ID(),%s,%s,NULL,0,0)"
                     % (q(name), q(pw), q(name), q(pw)), ctx.db.acc, note="account create %s" % name)
        print("account %r created (accounts_info + gamearea_info; SP_SELECT_ACCOUNTS_INFO checks gamearea_info)" % name)
        return
    r = account_row(ctx, a.account)
    if sub == "set-password":
        pw = a.password
        if not re.match(r"^[A-Za-z0-9_]{1,32}$", pw):
            raise Fail("password: letters, numbers, underscore, 1..32 chars")
        if str(r.get("login_status")) == "1" and not ctx.force:
            raise Fail("account %s has login_status=1; the LoginServer re-reads gamearea_info at every login so a change "
                       "is harmless, but the rule is the same: --force-offline or `account set-online %s 0` first"
                       % (a.account, a.account))
        ctx.db.write("UPDATE accounts_info SET password=%s WHERE accounts=%s" % (q(pw), q(a.account)), ctx.db.acc, note="set-password")
        ctx.db.write("UPDATE gamearea_info SET PassPwd=%s WHERE PassName=%s" % (q(pw), q(a.account)), ctx.db.acc, note="set-password")
        ctx.db.write("UPDATE accounts_register SET PassPwd=%s WHERE PassName=%s" % (q(pw), q(a.account)), ctx.db.acc, note="set-password")
        print("password of %s changed in accounts_info, gamearea_info (what the login checks) and accounts_register (plain text; "
              "the 1.0 client sends it in clear, the 2.51 client would send md5)" % a.account)
    elif sub == "set-online":
        v = num(a.value, 0, 1, "login_status")
        ctx.db.write("UPDATE accounts_info SET login_status=%d WHERE accounts=%s" % (v, q(a.account)), ctx.db.acc, note="set-online")
        print("accounts_info.login_status of %s: %s -> %d (informational column; no server code writes it)" % (a.account, r["login_status"], v))
    elif sub == "set-status":
        v = num(a.value, 0, 255, "accounts_status")
        ctx.db.write("UPDATE accounts_info SET accounts_status=%d WHERE accounts=%s" % (v, q(a.account)), ctx.db.acc, note="set-status")
        print("accounts_status of %s: %s -> %d (not read by any server code; website use)" % (a.account, r["accounts_status"], v))
    elif sub == "disable":
        v = 1 if a.value in (None, "1") else num(a.value, 0, 1, "IsDel")
        ctx.db.write("UPDATE gamearea_info SET IsDel=%d WHERE PassName=%s" % (v, q(a.account)), ctx.db.acc, note="disable")
        print("gamearea_info.IsDel of %s -> %d (note: SP_SELECT_ACCOUNTS_INFO does NOT check IsDel; use `account ban` to block logins)" % (a.account, v))
    elif sub == "ban":
        g = ctx.db.row("gamearea_info", "PassName=%s" % q(a.account), ctx.db.acc)
        if not g:
            raise Fail("no gamearea_info row for %s" % a.account)
        days = num(a.value or 36500, 1, 100000, "days")
        ctx.db.write("REPLACE INTO accounts_ban (id,bandate,unbandate,bannedby,banreason) VALUES (%s,NOW(),DATE_ADD(NOW(),INTERVAL %d DAY),%s,%s)"
                     % (q(g["GID"]), days, q(a.by or "gwadmin"), q(a.reason or "")), ctx.db.acc, note="ban %s" % a.account)
        print("%s banned for %d days (accounts_ban.id = gamearea_info.GID %s; SP_SELECT_ACCOUNTS_INFO refuses while NOW()<unbandate)" % (a.account, days, g["GID"]))
    elif sub == "unban":
        g = ctx.db.row("gamearea_info", "PassName=%s" % q(a.account), ctx.db.acc)
        ctx.db.write("DELETE FROM accounts_ban WHERE id=%s" % q(g["GID"] if g else -1), ctx.db.acc, note="unban %s" % a.account)
        print("%s unbanned" % a.account)


# ---- lookups
def cmd_item_search(ctx, a):
    hits = ctx.cat.find_items(a.text) if not a.text.isdigit() else [ctx.cat.item(int(a.text))]
    hits = [h for h in hits if h]
    rows = [[h["id"], h["name"], h["type"], h["playlv"], h["class"], h["overlap"], h["group"], (h["desc"] or "")[:60]] for h in hits[:a.limit]]
    table(rows, ["id", "name", "type", "lv", "class", "stack", "group", "description"])
    if len(hits) > a.limit:
        print("... %d more (use --limit)" % (len(hits) - a.limit))


def cmd_skill_search(ctx, a):
    t = a.text.lower()
    rows = []
    for k, v in ctx.cat.skills.items():
        if t in v["name"].lower() or t == k:
            rows.append([k, "active", v["name"], v.get("mp"), v.get("cooling"), v.get("kind")])
    for k, v in ctx.cat.passive.items():
        if t in v["name"].lower() or t == k:
            rows.append([k, "passive", v["name"], "", "", json.dumps(v.get("effects"), ensure_ascii=False)])
    rows.sort(key=lambda r: (r[1], int(r[0])))
    table(rows[:a.limit], ["id", "kind", "name", "mp", "cooling", "kind/effects"])


def cmd_maps(ctx, a):
    rows = [[v["id"], v["name"], v["display"], v["kind"], "%d (%.0f,%.0f)" % (v["athens_revival"]["map"], v["athens_revival"]["x"], v["athens_revival"]["z"]),
             "%d (%.0f,%.0f)" % (v["sparta_revival"]["map"], v["sparta_revival"]["x"], v["sparta_revival"]["z"])]
            for v in sorted(ctx.cat.maps.values(), key=lambda m: m["id"])]
    table(rows, ["id", "scene", "display", "kind", "athens revival", "sparta revival"])


def cmd_schema(ctx, a):
    for db in (ctx.db.game, ctx.db.acc):
        for (t,) in [r[:1] for r in ctx.db.query("SHOW FULL TABLES", db)]:
            n = ctx.db.one("SELECT COUNT(*) FROM `%s`" % t, db)
            print("== %s.%s (%s rows)" % (db, t, n[0] if n else "?"))
            for r in ctx.db.query("DESCRIBE `%s`" % t, db):
                print("   %-22s %-28s %s %s %s" % (r[0], r[1], "NULL" if r[2] == "YES" else "", r[3] or "", ("default " + str(r[4])) if r[4] not in (None, "") else ""))


def cmd_sql(ctx, a):
    if not a.write and re.match(r"\s*(INSERT|UPDATE|DELETE|REPLACE|CALL|DROP|ALTER|TRUNCATE|CREATE)", a.sql, re.I):
        raise Fail("that looks like a write; add --write")
    db = ctx.db.acc if a.accounts else ctx.db.game
    if a.write:
        ctx.db.write(a.sql, db, note="sql")
        print("ok")
    else:
        for r in ctx.db.query(a.sql, db):
            print("\t".join("NULL" if c is None else c for c in r))


# ---------------------------------------------------------------- main
def build_parser():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--db-suffix", default="", help="'' = live godswar/accounts (needs --live); '_test' = godswar_test/accounts_test")
    p.add_argument("--live", action="store_true", help="allow the live databases")
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--port", type=int, default=3306)
    p.add_argument("--user", default="root")
    p.add_argument("--password", default="")
    p.add_argument("--mysql", default=None, help="path of the mariadb/mysql client (auto)")
    p.add_argument("--catalog", default=os.path.join(HERE, "catalog"), help="catalog dir from build_catalog.py")
    p.add_argument("--gamelog", default=None, help="GameServer stdout log for the online check "
                   "(default: /opt/godswar/logs/gameserver.log for live, else none)")
    p.add_argument("--force-offline", action="store_true", help="edit even if the account/character looks online")
    p.add_argument("--dry-run", action="store_true", help="print the SQL instead of writing")
    p.add_argument("--backup-dir", default=os.path.join(os.getcwd(), "gwadmin-backups"))
    p.add_argument("--audit-log", default=os.path.join(os.path.expanduser("~"), ".gwadmin-audit.log"))
    p.add_argument("--verbose", "-v", action="store_true")
    s = p.add_subparsers(dest="cmd", required=True)

    x = s.add_parser("accounts", help="list accounts"); x.add_argument("--search"); x.add_argument("--json", action="store_true"); x.set_defaults(fn=cmd_accounts)
    x = s.add_parser("characters", help="list characters"); x.add_argument("--account"); x.add_argument("--search"); x.add_argument("--json", action="store_true"); x.set_defaults(fn=cmd_characters)
    x = s.add_parser("show", help="show one character, everything decoded"); x.add_argument("name", help="name or #id"); x.add_argument("--json", action="store_true"); x.set_defaults(fn=cmd_show)
    x = s.add_parser("set-level", help="set level (1..200); exp reset to 0 unless --exp"); x.add_argument("name"); x.add_argument("level"); x.add_argument("--exp"); x.set_defaults(fn=cmd_set_level)
    x = s.add_parser("set-exp"); x.add_argument("name"); x.add_argument("exp"); x.set_defaults(fn=cmd_set_exp)
    x = s.add_parser("set-money", help="money and/or --stone / --storage-money"); x.add_argument("name"); x.add_argument("money", nargs="?"); x.add_argument("--stone"); x.add_argument("--storage-money"); x.set_defaults(fn=cmd_set_money)
    x = s.add_parser("set-skillpoints"); x.add_argument("name"); x.add_argument("points"); x.add_argument("--skillexp"); x.set_defaults(fn=cmd_set_skillpoints)
    x = s.add_parser("set-pos", help="map id, X, Z"); x.add_argument("name"); x.add_argument("map"); x.add_argument("x"); x.add_argument("z"); x.set_defaults(fn=cmd_set_pos)
    x = s.add_parser("unstick", help="move to the camp's town spawn"); x.add_argument("name"); x.add_argument("--town", choices=["auto", "athens", "sparta"], default="auto"); x.add_argument("--main", action="store_true", help="main city revival point instead of the newbie town"); x.set_defaults(fn=cmd_unstick)
    x = s.add_parser("give", help="give an item (id or name) to a bag / storage / equip slot")
    x.add_argument("name"); x.add_argument("item"); x.add_argument("--count", default=1); x.add_argument("--bind", action="store_true")
    x.add_argument("--bag", default=1); x.add_argument("--storage", action="store_true"); x.add_argument("--equip", default=None, help="slot: " + ",".join(EQUIP_SLOTS))
    x.add_argument("--level", type=int, default=1, help="BaseLV (forge level of the base attribute), default 1 like the server")
    x.add_argument("--append-level", type=int, default=1, help="AppendLV, default 1")
    x.add_argument("--append", default=None, help="append attribute ids, comma separated (see appends.json)")
    x.add_argument("--no-stack", action="store_true"); x.add_argument("--force", action="store_true"); x.set_defaults(fn=cmd_give)
    x = s.add_parser("remove", help="remove an item by slot or by item"); x.add_argument("name"); x.add_argument("--bag", default=1); x.add_argument("--storage", action="store_true"); x.add_argument("--equip", default=None)
    x.add_argument("--slot", default=None); x.add_argument("--item", default=None); x.add_argument("--count", type=int, default=0); x.set_defaults(fn=cmd_remove)
    x = s.add_parser("set-bags", help="activate N bags (bag_num + empty strings)"); x.add_argument("name"); x.add_argument("count"); x.set_defaults(fn=cmd_set_bags)
    x = s.add_parser("skill", help="add/remove an active (magic) or --passive skill"); x.add_argument("action", choices=["add", "remove"]); x.add_argument("name"); x.add_argument("id"); x.add_argument("--passive", action="store_true"); x.add_argument("--value", help="grade (passive) / cooldown (active)"); x.add_argument("--force", action="store_true"); x.set_defaults(fn=cmd_skill)
    x = s.add_parser("quest", help="mark a quest done / not done"); x.add_argument("name"); x.add_argument("quest"); x.add_argument("state", choices=["done", "none"]); x.add_argument("--drop-active", action="store_true", help="also remove it from activestatus/timequest"); x.set_defaults(fn=cmd_quest)
    x = s.add_parser("set-gm", help="character_base.GM 0/1"); x.add_argument("name"); x.add_argument("flag"); x.set_defaults(fn=cmd_set_gm)
    x = s.add_parser("rename"); x.add_argument("name"); x.add_argument("new_name"); x.set_defaults(fn=cmd_rename)
    x = s.add_parser("delete", help="backup JSON, then SP_DELETE_ROLE_INFO"); x.add_argument("name"); x.add_argument("--yes", action="store_true"); x.set_defaults(fn=cmd_delete)
    x = s.add_parser("dump", help="dump one character as JSON"); x.add_argument("name"); x.add_argument("-o", "--output", default=None, help="file, or - for stdout"); x.set_defaults(fn=cmd_dump)
    x = s.add_parser("restore", help="restore a dump"); x.add_argument("file"); x.add_argument("--as-name"); x.add_argument("--account"); x.add_argument("--overwrite", action="store_true"); x.add_argument("--new-id", action="store_true"); x.add_argument("--yes", action="store_true"); x.set_defaults(fn=cmd_restore)
    x = s.add_parser("account", help="account show|create|set-password|set-online|set-status|disable|ban|unban")
    x.add_argument("subcmd", choices=["show", "create", "set-password", "set-online", "set-status", "disable", "ban", "unban"])
    x.add_argument("account"); x.add_argument("value", nargs="?", help="password / 0|1 / days"); x.add_argument("--reason"); x.add_argument("--by"); x.set_defaults(fn=cmd_account)
    x = s.add_parser("item-search"); x.add_argument("text"); x.add_argument("--limit", type=int, default=40); x.set_defaults(fn=cmd_item_search)
    x = s.add_parser("skill-search"); x.add_argument("text"); x.add_argument("--limit", type=int, default=40); x.set_defaults(fn=cmd_skill_search)
    x = s.add_parser("maps", help="map ids and revival points"); x.set_defaults(fn=cmd_maps)
    x = s.add_parser("schema", help="DESCRIBE every table of both databases"); x.set_defaults(fn=cmd_schema)
    x = s.add_parser("sql", help="run a query (reads; --write for writes; --accounts for the accounts db)"); x.add_argument("sql"); x.add_argument("--write", action="store_true"); x.add_argument("--accounts", action="store_true"); x.set_defaults(fn=cmd_sql)
    return p


def main(argv=None):
    try:
        sys.stdout.reconfigure(line_buffering=True)
    except (AttributeError, ValueError):
        pass
    p = build_parser()
    a = p.parse_args(argv)
    if a.cmd == "account" and a.subcmd in ("create", "set-password"):
        a.password = a.value
        if not a.password:
            p.error("account %s needs a password" % a.subcmd)
    ctx = Ctx()
    try:
        ctx.db = DB(a)
        ctx.cat = Catalog(a.catalog)
        ctx.force = a.force_offline
        ctx.backup_dir = a.backup_dir
        ctx.gamelog = a.gamelog
        if ctx.gamelog is None and ctx.db.live and os.path.exists("/opt/godswar/logs/gameserver.log"):
            ctx.gamelog = "/opt/godswar/logs/gameserver.log"
        if not ctx.cat.items:
            print("note: no catalog at %s (run build_catalog.py); names will show as ids" % a.catalog, file=sys.stderr)
        if ctx.db.live:
            print("** LIVE databases %s / %s **" % (ctx.db.game, ctx.db.acc), file=sys.stderr)
        return a.fn(ctx, a) or 0
    except Fail as e:
        sys.stdout.flush()
        print("gwadmin: %s" % e, file=sys.stderr)
        return 2
    except KeyboardInterrupt:
        return 130
    except BrokenPipeError:
        try:
            sys.stdout = open(os.devnull, "w")
        except OSError:
            pass
        return 0


if __name__ == "__main__":
    sys.exit(main())
