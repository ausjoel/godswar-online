#!/usr/bin/env python3
"""Account WIPE test (Character module): runs against ANY editor instance whose database is a private copy.
  python3 -I test_account_wipe.py --editor http://127.0.0.1:58811 --account 44 --psql "psql -h 127.0.0.1 -p 55432 -U postgres" --db godswar
Checks: the plan, a refused plain delete, wipe refused for a wrong confirm, a wipe that removes the account and its kill / loot /
claim records, Undo puts every row back (row counts per table identical), a guild leader's account is refused.
Refuses unless --i-know-this-database-is-a-copy is given with --confirm-db NAME matching the editor's database."""
import argparse, json, re, subprocess, sys, urllib.request, urllib.error
ap = argparse.ArgumentParser()
ap.add_argument("--editor", required=True); ap.add_argument("--account", type=int, required=True)
ap.add_argument("--psql", required=True); ap.add_argument("--db", required=True)
ap.add_argument("--guild-leader-account", type=int, default=0)
ap.add_argument("--i-know-this-database-is-a-copy", action="store_true")
a = ap.parse_args()
if not a.i_know_this_database_is_a_copy:
    sys.exit("refusing: pass --i-know-this-database-is-a-copy (this test deletes an account, then undoes it)")
html = urllib.request.urlopen(a.editor + "/").read().decode()
CSRF = re.search(r'name="csrf" content="([^"]+)"', html).group(1)
N = [0]; FAIL = []


def api(path, body=None):
    req = urllib.request.Request(a.editor + path, data=json.dumps(body).encode() if body is not None else None,
                                 method="POST" if body is not None else "GET", headers={"Content-Type": "application/json", "X-CSRF-Token": CSRF})
    try:
        with urllib.request.urlopen(req, timeout=300) as r:
            return r.status, json.loads(r.read())
    except urllib.error.HTTPError as e:
        raw = e.read()
        try: return e.code, json.loads(raw or b"null")
        except ValueError: return e.code, {"error": raw[:300].decode(errors="replace")}


def step(msg, ok, detail=""):
    N[0] += 1
    print(("ok   " if ok else "FAIL ") + msg + ("" if ok else "  -> " + str(detail)[:300]), flush=True)
    if not ok: FAIL.append(msg)


def sql(q):
    return subprocess.check_output(a.psql.split() + ["-X", "-q", "-At", "-d", a.db, "-c", q], text=True).strip()


def counts():
    """Row count of every public table that can hold this account's data, by the account / character ids."""
    cids = sql("select coalesce(string_agg(id::text, ','), '0') from character_base where account_id=%d" % a.account)
    out = {}
    for t, col, ids in [("accounts", "id", str(a.account)), ("character_base", "account_id", str(a.account))]:
        out[t] = int(sql("select count(*) from %s where %s in (%s)" % (t, col, ids)))
    for t in ["monster_death_reward_settlements", "monster_death_pet_experience", "monster_loot_pickup_claims", "online_award_claim_settlements",
              "faction_crier_daily_claims", "sealed_pet_items", "quest_reward_item_claims", "character_items", "character_pets", "character_skills"]:
        cols = sql("select string_agg(column_name,',') from information_schema.columns where table_name='%s' and column_name in ('account_id','character_id','user_id')" % t).split(",")
        cond = " OR ".join("%s in (%s)" % (c, a.account if c == "account_id" else cids) for c in cols if c)
        out[t] = int(sql("select count(*) from %s where %s" % (t, cond))) if cond else -1
    return out


st, acct = api("/api/m/character/account/%d" % a.account)
step("account %d (%s) found" % (a.account, acct.get("username")), st == 200, acct)
user = acct["username"]
before = counts()
print("rows before:", before)
step("the account has evidence rows to wipe", before["monster_death_reward_settlements"] > 0, before)
st, plan = api("/api/m/character/account/%d/delete-plan" % a.account)
step("plain delete is blocked by evidence", st == 200 and not plan["can_delete"] and plan["blockers"], plan.get("blockers"))
w = plan["wipe"]
step("wipe plan: can wipe, lists the evidence tables", w["can_wipe"] and {t["table"] for t in w["tables"]} >= {"monster_death_reward_settlements"}, w)
step("wipe plan total covers account + evidence", w["total_rows"] > plan["total_rows"], (w["total_rows"], plan["total_rows"]))
st, r = api("/api/m/character/account/%d/delete" % a.account, {"confirm": user, "mode": "delete"})
step("plain delete refused (403)", st == 403, r)
st, r = api("/api/m/character/account/%d/delete" % a.account, {"confirm": "wrong", "mode": "wipe"})
step("wipe without the right user name refused (400)", st == 400, r)
st, r = api("/api/m/character/account/%d/delete" % a.account, {"confirm": user, "mode": "zap"})
step("unknown mode refused (400)", st == 400, r)
step("nothing changed by the refusals", counts() == before, counts())
st, r = api("/api/m/character/account/%d/delete" % a.account, {"confirm": user, "mode": "wipe"})
step("wipe %s" % user, st == 200, r)
if st != 200:
    sys.exit("stopped: wipe failed")
change = r["change"]["id"]
after = counts()
print("rows after:", after)
step("account, characters and every evidence table are empty", all(v in (0, -1) for v in after.values()), after)
st, r2 = api("/api/m/character/account/%d" % a.account)
step("the account is gone (404)", st == 404, r2)
st, u = api("/api/undo", {"change": change})
step("Undo accepted", st == 200, u)
restored = counts()
step("Undo: every row count is back", restored == before, {k: (before[k], restored[k]) for k in before if before[k] != restored[k]})
st, acct2 = api("/api/m/character/account/%d" % a.account)
step("the account and its characters are back", st == 200 and len(acct2["characters"]) == len(acct["characters"]), acct2.get("error"))
if a.guild_leader_account:
    st, p2 = api("/api/m/character/account/%d/delete-plan" % a.guild_leader_account)
    step("a guild leader's account cannot be wiped (guilds blocker)", st == 200 and not p2["wipe"]["can_wipe"] and
         any(x["table"] == "guilds" for x in p2["wipe"]["hard_blockers"]), p2.get("wipe"))
    st, r3 = api("/api/m/character/account/%d/delete" % a.guild_leader_account, {"confirm": (api("/api/m/character/account/%d" % a.guild_leader_account)[1] or {}).get("username", "?"), "mode": "wipe"})
    step("wiping it is refused (403)", st == 403, r3)
print("%d checks, %d failed" % (N[0], len(FAIL)))
sys.exit(1 if FAIL else 0)
