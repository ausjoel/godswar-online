"""modules/accounts.py - the "Accounts" editor: list accounts, CREATE accounts, change passwords, e-mail, donator tier,
ban / unban. Contract: ../MODULES.md. Owned by the framework task (the admin asked for adding accounts in the new editor).

How the reborn server stores an account (src/Godswar.Server, commit b85a1244):
  * accounts.password is a versioned verifier, never the password:
      gws$pbkdf2-sha256$v1$<iterations>$<base64 salt, 16 bytes>$<base64 hash, 32 bytes>
    (Security/Authentication/PasswordVerifierRecord.cs, PasswordHasher.cs: Rfc2898DeriveBytes.Pbkdf2 SHA-256, 600,000
    iterations, AuthenticationOptions.cs). The KDF input is the 32 bytes the client sends in the login packet: the
    lower-case MD5 hex of the typed password (Game/LoginClientHandler.cs:190-200, CopyLegacyPasswordBytes; the same
    bytes gmclient.py / reborn_probe.py send). So: verifier = PBKDF2(md5hex(password)).
  * A new account is the server's own insert (Infrastructure/Accounts/PostgresAccountStore.cs:148-158,
    TryCreateAccountWithCredentialAsync): uuid '', email '', login_status 0, times now(), status 0. The account_realm
    row is created by the server at the first character create (State/PostgresGameStore.Characters.Persistence.cs:22).
  * Usernames: 1-32 printable ASCII, matched exactly (case-sensitive) at login (AccountUsername.cs).
  * accounts.status: the stock server never reads it; the round-2 build treats 1 as banned (login and game refuse).
Writes go through ctx.rows_write (backup, guarded transaction, change log, Undo). The plain password is never stored,
logged or sent back.
"""
import base64
import hashlib
import os
import re

from . import common
from .common import Bad, Refused, lit

M = common.module(
    "accounts", "Accounts",
    icon={"sprite": 542},          # catalog-icons.png tile (Pan's Invitation Letter)
    order=5,
    status="ready",
    description="Player accounts: create new ones, change a password, e-mail, donator tier, ban / unban, and see each account's characters.",
    needs_character=False,
)

ITERATIONS = 600_000          # AuthenticationOptions.DefaultIterations (stored range 100,000..2,000,000)
USER_RX = re.compile(r"^[A-Za-z0-9_]{3,32}$")
PASS_RX = re.compile(r"^[\x20-\x7e]{4,32}$")
EMAIL_RX = re.compile(r"^$|^[^@\s'\"]{1,100}@[^@\s'\"]{1,150}\.[A-Za-z]{2,24}$")
BAN_STATUS = 1                # round-2: accounts.status = 1 -> login and game ports refuse


def verifier(password):
    """The accounts.password value the reborn login accepts for this password (see the module docstring)."""
    md5hex = hashlib.md5(password.encode("ascii")).hexdigest().encode("ascii")
    salt = os.urandom(16)
    dk = hashlib.pbkdf2_hmac("sha256", md5hex, salt, ITERATIONS, 32)
    return "gws$pbkdf2-sha256$v1$%d$%s$%s" % (ITERATIONS, base64.b64encode(salt).decode(), base64.b64encode(dk).decode())


def _mask(ctx, r):
    """Hide the verifier in the change log's SQL text (Undo works from the row snapshots, not from this text)."""
    cid = (r.get("change") or {}).get("id")
    rx = re.compile(r"gws\$pbkdf2-sha256\$v1\$[^']*")
    for c in ctx.app.changes:
        if c.get("id") == cid:
            for x in c.get("sql") or []:
                x["sql"] = rx.sub("gws$pbkdf2-sha256$v1$(new password verifier, hidden)", x["sql"])
            if hasattr(ctx.app, "_save_change"):
                ctx.app._save_change(c)
            r["change"] = common.public_change(c)
    return r


def check_password(pw):
    if not isinstance(pw, str) or not PASS_RX.match(pw):
        raise Bad("the password must be 4-32 characters: letters, digits, spaces or ASCII punctuation "
                  "(the game client sends at most 32 characters)")
    return pw


def round2(ctx):
    """The round-2 build is the one that honours accounts.status = 1 (ban) and can kick."""
    caps = ctx.caps
    return bool(caps.get("available"))


def _accounts(ctx, where="true"):
    rb = ctx.need_rb()
    return rb.pg.json("""(SELECT coalesce(json_agg(r ORDER BY r.id), '[]') FROM (
        SELECT a.id, a.username, a.email, a.status, a.editor_admin, a.donator_tier, a.donator_expires_at, a.login_status,
               a.login_presence_token IS NOT NULL AS presence, a.last_login_time, a.last_logout_time, a.total_online_time,
               a.password LIKE 'gws$%%' AS hashed,
               (SELECT coalesce(json_agg(json_build_object('id', c.id, 'name', c.name, 'level', c.fighter_job_lv,
                        'profession', c.profession, 'camp', c.camp, 'online', c.checkpoint_owner_id IS NOT NULL) ORDER BY c.id), '[]')
                  FROM character_base c WHERE c.account_id = a.id) AS characters
        FROM accounts a WHERE %s) r)""" % where)


def _decorate(ctx, rows):
    r2 = round2(ctx)
    for a in rows:
        for c in a["characters"]:
            c["class"] = common.CLASS_NAMES.get(c["profession"], str(c["profession"]))
            c["camp_name"] = common.CAMP_NAMES.get(c["camp"], str(c["camp"]))
        a["online"] = bool(a["login_status"] or a["presence"] or any(c["online"] for c in a["characters"]))
        a["banned"] = a["status"] == BAN_STATUS
        a["ban_effective"] = r2
    return rows


def _one(ctx, aid):
    rows = _decorate(ctx, _accounts(ctx, "a.id = %d" % int(aid)))
    if not rows:
        raise common.NotFound("no account #%s" % aid)
    return rows[0]


@M.get("list")
def list_accounts(ctx):
    """GET /api/m/accounts/list?q= - every account with its characters (search: user name, e-mail, character name)."""
    q = (ctx.q("q") or "").strip()
    where = "true"
    if q:
        like = lit("%" + q + "%")
        where = "(a.username ILIKE %s OR a.email ILIKE %s OR CAST(a.id AS text) = %s OR EXISTS (SELECT 1 FROM character_base c " \
                "WHERE c.account_id = a.id AND c.name ILIKE %s))" % (like, like, lit(q), like)
    rows = _decorate(ctx, _accounts(ctx, where))
    return {"accounts": rows, "round2": round2(ctx), "writable": bool(ctx.rb and ctx.rb.writable),
            "rules": {"username": "3-32 letters, digits or _ (case-sensitive at login)",
                      "password": "4-32 characters (letters, digits, spaces, punctuation)"}}


@M.get(r"(\d+)")
def get_account(ctx, aid):
    return _one(ctx, aid)


def _scope(aid):
    return [common.scope("accounts", "id = %d" % int(aid), pk=["id"], counters=[],
                         ignore=["last_login_time", "last_logout_time", "last_login_ip", "last_login_mac", "total_online_time",
                                 "login_status", "login_presence_token"])]


def _refuse_if_online(ctx, a, what):
    if a["online"]:
        raise common.OnlineRefused("%s: the account %s is logged in (%s). Log it out first." % (
            what, a["username"], ", ".join([c["name"] for c in a["characters"] if c["online"]]) or "at the login / character screen"),
            {"name": a["username"], "online": True, "reasons": ["accounts.login_status=%s" % a["login_status"]] +
             (["accounts.login_presence_token is set"] if a["presence"] else []), "live": [], "kick": False})


@M.post("create")
def create(ctx):
    """POST /api/m/accounts/create {username, password, email?, donator_tier?} - a new account, exactly as the server's
    own registration inserts it. The player then logs in with it and creates characters as usual."""
    b = ctx.body
    user = str(b.get("username") or "").strip()
    if not USER_RX.match(user):
        raise Bad("the user name must be 3-32 letters, digits or _ (no spaces); the login is case-sensitive")
    pw = check_password(b.get("password"))
    email = str(b.get("email") or "").strip()
    if not EMAIL_RX.match(email):
        raise Bad("that e-mail address does not look right (leave it empty if you have none)")
    tier = common.to_int(b.get("donator_tier", 0), 0, 5, "donator tier")
    rb = ctx.need_rb()
    if rb.pg.json("(SELECT to_jsonb(count(*)) FROM accounts WHERE username = %s)" % lit(user)):
        raise Bad("the user name %s is taken" % user)
    ver = verifier(pw)
    scope = [common.scope("accounts", "username = %s" % lit(user), pk=["id"], counters=[],
                          ignore=["last_login_time", "last_logout_time"])]

    def build(before):
        if before.get("accounts"):
            raise Bad("the user name %s is taken" % user)
        # PostgresAccountStore.TryCreateAccountWithCredentialAsync, plus the optional e-mail / donator tier
        return ["INSERT INTO public.accounts (uuid, email, username, password, login_status, last_login_time, "
                "last_logout_time, last_login_ip, last_login_mac, total_online_time, status, donator_tier) VALUES "
                "('', %s, %s, %s, 0, now(), now(), '', '', 0, 0, %d)" % (lit(email), lit(user), lit(ver), tier)]

    r = ctx.rows_write(scope, "create account %s%s" % (user, " (donator tier %d)" % tier if tier else ""), build,
                       fields=["accounts.*"], applies="now", target="account " + user)
    _mask(ctx, r)
    acc = _decorate(ctx, _accounts(ctx, "a.username = %s" % lit(user)))
    r["account"] = acc[0] if acc else None
    r["note"] = ("Log in with %s in the game client; the first character is created at the character screen as usual." % user)
    return r


@M.post(r"(\d+)/password")
def set_password(ctx, aid):
    """POST /api/m/accounts/<id>/password {password} - a new password (works while the player is online: the login
    server only reads it at the next login)."""
    a = _one(ctx, aid)
    pw = check_password(ctx.body.get("password"))
    ver = verifier(pw)
    r = ctx.rows_write(_scope(aid), "new password for account %s" % a["username"],
                       lambda before: ["UPDATE accounts SET password = %s WHERE id = %d" % (lit(ver), int(aid))],
                       # accounts.password is "internal" in the write rules: never typed in as a raw value. This route is the
                       # one place that builds the verifier the rule asks for (and was proved with a headless login), so it
                       # passes the table-level check only.
                       fields=["accounts.*"], applies="next_login", target="account " + a["username"])
    _mask(ctx, r)
    r["account"] = _one(ctx, aid)
    return r


@M.post(r"(\d+)/edit")
def edit(ctx, aid):
    """POST /api/m/accounts/<id>/edit {email?, donator_tier?, donator_expires_at?, expected{}} - account fields."""
    a = _one(ctx, aid)
    b = ctx.body
    vals = {}
    if "email" in b:
        e = str(b.get("email") or "").strip()
        if not EMAIL_RX.match(e):
            raise Bad("that e-mail address does not look right")
        vals["email"] = e
    if "donator_tier" in b:
        vals["donator_tier"] = common.to_int(b["donator_tier"], 0, 5, "donator tier")
    if "donator_expires_at" in b:
        v = b.get("donator_expires_at")
        if v in (None, ""):
            vals["donator_expires_at"] = None
        elif isinstance(v, str) and re.match(r"^\d{4}-\d{2}-\d{2}([ T]\d{2}:\d{2}(:\d{2})?)?$", v):
            vals["donator_expires_at"] = v
        else:
            raise Bad("donator expiry must be a date (YYYY-MM-DD) or empty for never")
    if not vals:
        raise Bad("nothing to change")
    if set(vals) - {"email"}:
        _refuse_if_online(ctx, a, "donator tier")
    exp = b.get("expected") or None
    expected = [{"table": "accounts", "where": {"id": int(aid)}, "values": exp}] if exp else None
    what = ", ".join("%s %s" % (k.replace("_", " "), "(none)" if v is None else v) for k, v in vals.items())
    r = ctx.rows_write(_scope(aid), "account %s: %s" % (a["username"], what),
                       lambda before: [common.update_sql("accounts", {"id": int(aid)}, vals, bump=False)],
                       fields=["accounts.%s" % k for k in vals], expected=expected,
                       applies="next_login", target="account " + a["username"])
    r["account"] = _one(ctx, aid)
    return r


@M.post(r"(\d+)/ban")
def ban(ctx, aid):
    """POST /api/m/accounts/<id>/ban {banned: true|false} - accounts.status 1 / 0. With the round-2 server the login
    is refused and an online player is kicked; the stock server ignores the flag (the UI says so)."""
    a = _one(ctx, aid)
    on = bool(ctx.body.get("banned"))
    st = BAN_STATUS if on else 0
    if a["status"] == st:
        return {"account": a, "note": "already %s" % ("banned" if on else "not banned")}
    r = ctx.rows_write(_scope(aid), "%s account %s" % ("ban" if on else "unban", a["username"]),
                       lambda before: ["UPDATE accounts SET status = %d WHERE id = %d" % (st, int(aid))],
                       fields=["accounts.status"], expected=[{"table": "accounts", "where": {"id": int(aid)}, "values": {"status": a["status"]}}],
                       applies="next_login", target="account " + a["username"])
    kicked = []
    if on and a["online"]:
        o = (ctx.caps.get("operations") or {}).get("character.kick")
        for c in a["characters"]:
            if c["online"] and o:
                stt, doc = ctx._live_post(o["path"], c["name"], {"reason": "banned"})
                kicked.append("%s: %s" % (c["name"], doc.get("result") if isinstance(doc, dict) and stt == 200 else "kick failed (%s)" % stt))
    r["kicked"] = kicked
    r["account"] = _one(ctx, aid)
    r["note"] = ("" if round2(ctx) else "The stock server ignores this flag; the ban takes effect once the round-2 server "
                 "update is installed.")
    return r


@M.post(r"(\d+)/editor-admin")
def editor_admin(ctx, aid):
    a = _one(ctx, aid)
    on = ctx.body.get("enabled")
    expected = ctx.body.get("expected")
    if not isinstance(on, bool) or not isinstance(expected, bool):
        raise Bad("enabled and expected must be true or false")
    r = ctx.rows_write(_scope(aid), "account %s: editor administrator %s" % (a["username"], "enabled" if on else "disabled"),
                       lambda before: [common.update_sql("accounts", {"id": int(aid)}, {"editor_admin": on}, bump=False)],
                       fields=["accounts.editor_admin"],
                       expected=[{"table": "accounts", "where": {"id": int(aid)}, "values": {"editor_admin": expected}}],
                       applies="now", target="account " + a["username"])
    r["account"] = _one(ctx, aid)
    return r
