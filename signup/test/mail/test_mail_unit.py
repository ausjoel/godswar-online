#!/usr/bin/env python3
"""Unit and app-level tests for the mail features. No database and no internet: the database is a
small in-memory stand-in (FakeStore, same rules as sql/mail-grants.sql, which test_mail_sql.sh
tests for real) and Resend is either a fake send function or a tiny HTTP server on 127.0.0.1.

    python3 test/mail/test_mail_unit.py -v

Needs jinja2 importable (PYTHONPATH). psycopg2 is replaced by a stub if it is not installed.
"""
import base64
import hashlib
import http.server
import json
import os
import re
import sys
import tempfile
import threading
import time
import types
import unittest
from html.parser import HTMLParser
from io import BytesIO
from urllib.parse import urlencode, urlsplit, parse_qs

HERE = os.path.dirname(os.path.abspath(__file__))
SIGNUP = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, os.path.join(SIGNUP, "app"))

try:
    import psycopg2  # noqa: F401
except ImportError:   # stub: the tests never open a connection
    stub = types.ModuleType("psycopg2")

    class _Err(Exception):
        pass
    stub.OperationalError = stub.InterfaceError = _Err
    stub.Binary = bytes
    stub.pool = types.ModuleType("psycopg2.pool")
    stub.pool.ThreadedConnectionPool = object
    sys.modules["psycopg2"] = stub
    sys.modules["psycopg2.pool"] = stub.pool

import gwsignup  # noqa: E402

with open(os.path.join(SIGNUP, "sql", "mail-grants.sql")) as f:
    SQL_VERIFIER_RE = re.search(r"p_verifier !~ '([^']+)'", f.read()).group(1).replace("\\$", r"\$")

KEY = "re_TestKey_1234567890abcdef"


class TempEnv:
    """Set GWSIGNUP_* (and secret files) for one Config() and restore afterwards."""

    def __init__(self, key=KEY, **kw):
        self.key, self.kw = key, kw

    def __enter__(self):
        self.dir = tempfile.TemporaryDirectory()
        for n, v in (("db_password", "x" * 40), ("secret_key", "s" * 64), ("resend_api_key", self.key)):
            if v is not None:
                with open(os.path.join(self.dir.name, n), "w") as f:
                    f.write(v + "\n")
        self.saved = {k: v for k, v in os.environ.items() if k.startswith("GWSIGNUP_") or k == "CREDENTIALS_DIRECTORY"}
        for k in self.saved:
            del os.environ[k]
        os.environ["CREDENTIALS_DIRECTORY"] = self.dir.name
        os.environ["GWSIGNUP_FORM_MIN_SECONDS"] = "0"
        os.environ["GWSIGNUP_ORIGIN"] = "https://YOUR-DOMAIN.example,https://YOUR-DOMAIN.example"
        os.environ["GWSIGNUP_MAIL_FROM"] = "GodsWar <noreply@YOUR-DOMAIN.example>"
        os.environ["GWSIGNUP_MAIL_REPLY_TO"] = "admin@YOUR-DOMAIN.example"
        for k, v in self.kw.items():
            os.environ["GWSIGNUP_" + k] = v
        return self

    def __exit__(self, *a):
        for k in [k for k in os.environ if k.startswith("GWSIGNUP_") or k == "CREDENTIALS_DIRECTORY"]:
            del os.environ[k]
        os.environ.update(self.saved)
        self.dir.cleanup()


# --------------------------------------------------------------------------- fake database

class FakeStore:
    """In-memory stand-in for Store, following sql/mail-grants.sql's rules."""

    def __init__(self):
        self.lock = threading.Lock()
        self.accounts = {}     # id -> dict(username, email, password)
        self.emails = {}       # id -> dict(email, verified)
        self.tokens = {}       # sha256 -> dict(account, purpose, expires, used)
        self.next_id = 1

    def ping(self):
        return True

    def precheck(self, username, email_lower):
        return any(a["username"] == username or a["email"].lower() == email_lower for a in self.accounts.values())

    def create(self, username, email, verifier, ip_hash):
        with self.lock:
            if self.precheck(username, email.lower()):
                return None
            i = self.next_id
            self.next_id += 1
            self.accounts[i] = dict(username=username, email=email, password=verifier)
            self.emails[i] = dict(email=email, verified=False)
            return i

    def accounts_for_email(self, email_lower):
        return [(i, self.accounts[i]["username"], e["email"], e["verified"])
                for i, e in sorted(self.emails.items()) if e["email"].lower() == email_lower and i in self.accounts]

    def issue_token(self, account_id, purpose, digest):
        e = self.emails.get(account_id)
        if e is None or len(digest) != 32:
            return False
        if purpose == "verify_email" and e["verified"]:
            return False
        if purpose == "reset_password" and not e["verified"]:
            return False
        life = {"verify_email": 86400, "reset_password": 3600}.get(purpose)
        if life is None:
            return False
        for t in self.tokens.values():
            if t["account"] == account_id and t["purpose"] == purpose and not t["used"]:
                t["expires"] = min(t["expires"], time.time())
        self.tokens[digest] = dict(account=account_id, purpose=purpose, expires=time.time() + life, used=False)
        return True

    def _live(self, digest, purpose):
        t = self.tokens.get(digest)
        return t if t and t["purpose"] == purpose and not t["used"] and t["expires"] > time.time() else None

    def peek_reset(self, digest):
        t = self._live(digest, "reset_password")
        return self.accounts[t["account"]]["username"] if t else None

    def verify_email(self, digest):
        t = self._live(digest, "verify_email")
        if not t:
            return None
        t["used"] = True
        self.emails[t["account"]]["verified"] = True
        return t["account"]

    def reset_password(self, digest, verifier):
        if not re.fullmatch(SQL_VERIFIER_RE, verifier):
            raise ValueError("bad verifier format")
        t = self._live(digest, "reset_password")
        if not t:
            return None
        t["used"] = True
        self.accounts[t["account"]]["password"] = verifier
        return t["account"]


# --------------------------------------------------------------------------- tiny WSGI browser

class Hidden(HTMLParser):
    def __init__(self):
        super().__init__()
        self.fields = {}

    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        if tag == "input" and a.get("type") == "hidden" and a.get("name"):
            self.fields[a["name"]] = a.get("value", "")


class Browser:
    def __init__(self, app, ip="203.0.113.7"):
        self.app, self.ip, self.cookies = app, ip, {}
        self.policy = ""      # referrer policy of the last page, as a browser would apply it to its form

    def request(self, method, path, form=None):
        path, _, query = path.partition("?")
        body = urlencode(form).encode() if form is not None else b""
        environ = {"REQUEST_METHOD": method, "PATH_INFO": path, "QUERY_STRING": query,
                   "REMOTE_ADDR": "127.0.0.1", "HTTP_X_REAL_IP": self.ip,
                   # Like a real browser: a form POST from a page with referrer policy no-referrer
                   # carries "Origin: null" (Fetch spec), which origin_ok() refuses.
                   "HTTP_ORIGIN": "null" if method == "POST" and self.policy == "no-referrer" else self.app.cfg.origins[0],
                   "HTTP_COOKIE": "; ".join("%s=%s" % kv for kv in self.cookies.items()),
                   "CONTENT_TYPE": "application/x-www-form-urlencoded", "CONTENT_LENGTH": str(len(body)),
                   "wsgi.input": BytesIO(body)}
        got = {}

        def start(status, headers):
            got["status"], got["headers"] = int(status.split()[0]), headers
        out = b"".join(self.app(environ, start))
        if method == "GET":
            m = re.search(rb'<meta name="referrer" content="([^"]+)"', out)
            self.policy = m.group(1).decode() if m else ""
        for k, v in got["headers"]:
            if k == "Set-Cookie":
                name, _, rest = v.partition("=")
                self.cookies[name] = rest.split(";")[0]
        return got["status"], out.decode("utf-8")

    def form(self, path):
        status, html = self.request("GET", path)
        p = Hidden()
        p.feed(html)
        return status, html, p.fields

    def submit(self, get_path, post_path, **fields):
        status, html, hidden = self.form(get_path)
        assert status == 200, (status, html[:300])
        data = dict(hidden)
        q = re.search(r"Quick check: What is (\w+) (plus|minus) (\w+)\?", html)
        if q:
            a, b = gwsignup.NUMBER_WORDS.index(q.group(1)), gwsignup.NUMBER_WORDS.index(q.group(3))
            data["answer"] = str(a + b if q.group(2) == "plus" else a - b)
        data.update(fields)
        return self.request("POST", post_path, data)


class Outbox:
    def __init__(self):
        self.mails, self.fail = [], None

    def __call__(self, cfg, to, subject, text, html=None):
        if self.fail:
            raise self.fail
        self.mails.append(dict(to=to, subject=subject, text=text, html=html))
        return "fake-%d" % len(self.mails)


def make_app(**env):
    with TempEnv(**env):
        cfg = gwsignup.Config()
    cfg.iterations = 1000       # fast PBKDF2 for tests (the format is the same)
    app = gwsignup.App(cfg)
    app.store = FakeStore()
    app.mailer.store = app.store
    box = Outbox()
    app.mailer.send_fn = box
    return app, box


def drain(app):
    app.mailer.q.join()


def link_in(mail, path):
    m = re.search(r"https://YOUR-DOMAIN\.example" + re.escape(path) + r"\?t=([A-Za-z0-9_-]{43})", mail["text"])
    assert m, mail["text"]
    return path + "?t=" + m.group(1)


PW = "Olymp0s!Rise"


def signup(app, name="hero1", email="Hero1@Example.com", ip="203.0.113.7"):
    return Browser(app, ip).submit("/", "/signup", username=name, password=PW, confirm=PW, email=email)


# --------------------------------------------------------------------------- tests

class ConfigTests(unittest.TestCase):
    def test_defaults(self):
        with TempEnv():
            cfg = gwsignup.Config()
        self.assertTrue(cfg.mail_enabled)
        self.assertEqual(cfg.origins, ["https://YOUR-DOMAIN.example", "https://YOUR-DOMAIN.example"])
        self.assertEqual(cfg.public_url, "https://YOUR-DOMAIN.example")
        self.assertEqual(cfg.mail_from, "GodsWar <noreply@YOUR-DOMAIN.example>")
        self.assertEqual(cfg.mail_reply_to, "admin@YOUR-DOMAIN.example")
        self.assertEqual(cfg.resend_url, "https://api.resend.com/emails")
        self.assertEqual(cfg.lim_mail_all_day, (90, 86400))
        self.assertEqual(cfg.lim_mail_addr_hour, (3, 3600))
        self.assertEqual(cfg.lim_mail_per_ip, (10, 3600))

    def test_mail_off_cases(self):
        for key in (None, "", "   "):
            with TempEnv(key=key):
                self.assertFalse(gwsignup.Config().mail_enabled, repr(key))
        with TempEnv(MAIL="0"):
            self.assertFalse(gwsignup.Config().mail_enabled)
        with TempEnv(key="re_has space inside"):
            self.assertFalse(gwsignup.Config().mail_enabled)

    def test_bad_settings_refuse_to_start(self):
        with TempEnv(RESEND_URL="http://api.resend.com/emails"), self.assertRaises(SystemExit):
            gwsignup.Config()
        with TempEnv(MAIL_FROM="nobody"), self.assertRaises(SystemExit):
            gwsignup.Config()
        with TempEnv(MAIL_REPLY_TO="a@b.c\r\nBcc: x@y.z"), self.assertRaises(SystemExit):
            gwsignup.Config()
        with TempEnv(RESEND_URL="http://127.0.0.1:9/emails"):
            self.assertTrue(gwsignup.Config().mail_enabled)


class Tokens(unittest.TestCase):
    def test_shape_hash_unique(self):
        t, h = gwsignup.new_token()
        self.assertRegex(t, r"^[A-Za-z0-9_-]{43}$")
        self.assertEqual(h, hashlib.sha256(t.encode()).digest())
        self.assertEqual(gwsignup.token_hash(t), h)
        self.assertEqual(len({gwsignup.new_token()[0] for _ in range(2000)}), 2000)
        for bad in ("", "a" * 42, "a" * 44, "a" * 42 + "!", None, 123, "ä" * 43):
            self.assertIsNone(gwsignup.token_hash(bad), repr(bad))


class Verifier(unittest.TestCase):
    def test_app_verifier_matches_the_sql_check(self):
        for pw in (PW, "a" * 32, "Zz!12345"):
            v = gwsignup.game_verifier(pw, 600000)
            self.assertRegex(v, SQL_VERIFIER_RE)
            self.assertTrue(gwsignup.check_game_verifier(v, hashlib.md5(pw.encode()).hexdigest()))
        parts = gwsignup.game_verifier(PW, 600000).split("$")
        self.assertEqual(parts[:4], ["gws", "pbkdf2-sha256", "v1", "600000"])
        self.assertEqual((len(base64.b64decode(parts[4])), len(base64.b64decode(parts[5]))), (16, 32))

    def test_sql_check_refuses_other_values(self):
        good = gwsignup.game_verifier(PW, 1000)
        for bad in ("plaintext", good + "x", good.replace("v1", "v2"), good.replace("$1000$", "$0$"), good.replace("==", "=")):
            self.assertNotRegex(bad, SQL_VERIFIER_RE)


class FakeResend(http.server.BaseHTTPRequestHandler):
    answer = (200, {"id": "abc-123"})
    seen = []

    def do_POST(self):
        body = self.rfile.read(int(self.headers["Content-Length"]))
        FakeResend.seen.append((dict(self.headers), json.loads(body)))
        code, obj = FakeResend.answer
        data = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *a):
        pass


class ResendTransport(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.srv = http.server.HTTPServer(("127.0.0.1", 0), FakeResend)
        threading.Thread(target=cls.srv.serve_forever, daemon=True).start()
        cls.url = "http://127.0.0.1:%d/emails" % cls.srv.server_port

    @classmethod
    def tearDownClass(cls):
        cls.srv.shutdown()

    def cfg(self):
        with TempEnv(RESEND_URL=self.url):
            return gwsignup.Config()

    def test_request_shape(self):
        FakeResend.answer, FakeResend.seen = (200, {"id": "abc-123"}), []
        self.assertEqual(gwsignup.resend_send(self.cfg(), "p@example.com", "Subj", "plain", "<p>html</p>"), "abc-123")
        headers, body = FakeResend.seen[0]
        self.assertEqual(headers["Authorization"], "Bearer " + KEY)
        self.assertEqual(headers["Content-Type"], "application/json")
        self.assertTrue(headers["User-Agent"].startswith("gwsignup/"))
        self.assertEqual(body, {"from": "GodsWar <noreply@YOUR-DOMAIN.example>", "to": ["p@example.com"],
                                "subject": "Subj", "text": "plain", "html": "<p>html</p>",
                                "reply_to": "admin@YOUR-DOMAIN.example"})

    def test_error_is_readable_and_has_no_key(self):
        FakeResend.answer = (403, {"name": "validation_error", "message": "The YOUR-DOMAIN.example domain is not verified. " + KEY})
        with self.assertRaises(gwsignup.MailError) as ctx:
            gwsignup.resend_send(self.cfg(), "p@example.com", "s", "t")
        msg = str(ctx.exception)
        self.assertIn("HTTP 403 validation_error", msg)
        self.assertIn("not verified", msg)
        self.assertNotIn(KEY, msg)

    def test_unreachable(self):
        with TempEnv(RESEND_URL="http://127.0.0.1:9/emails", RESEND_TIMEOUT="2"):
            cfg = gwsignup.Config()
        with self.assertRaises(gwsignup.MailError):
            gwsignup.resend_send(cfg, "p@example.com", "s", "t")


class Flows(unittest.TestCase):
    def test_signup_sends_verify_and_verify_works(self):
        app, box = make_app()
        status, html = signup(app)
        self.assertEqual(status, 200, html[:400])
        self.assertIn("We sent a link to your email address", html)
        drain(app)
        self.assertEqual(len(box.mails), 1)
        m = box.mails[0]
        self.assertEqual(m["to"], "Hero1@Example.com")
        self.assertIn("Confirm your", m["subject"])
        self.assertIn("hero1", m["html"])
        path = link_in(m, "/verify")
        self.assertIn(path.split("=")[1], m["html"])
        b = Browser(app)
        # GET only shows a button (mail scanners open links); nothing is used up yet.
        status, html, hidden = b.form(path)
        self.assertEqual(status, 200)
        self.assertFalse(app.store.emails[1]["verified"])
        status, html = b.request("POST", "/verify", hidden)
        self.assertEqual(status, 200, html[:400])
        self.assertIn("Email confirmed", html)
        self.assertTrue(app.store.emails[1]["verified"])
        # single use
        status, html, hidden = b.form(path)
        status, html = b.request("POST", "/verify", hidden)
        self.assertEqual(status, 400)
        self.assertIn("does not work any more", html)

    def test_reset_flow_writes_signup_format(self):
        app, box = make_app()
        signup(app)
        drain(app)
        app.store.emails[1]["verified"] = True
        old = app.store.accounts[1]["password"]
        status, html = Browser(app).submit("/forgot", "/forgot", email="hero1@example.COM")
        self.assertEqual(status, 200)
        drain(app)
        self.assertEqual(len(box.mails), 2)
        path = link_in(box.mails[1], "/reset")
        b = Browser(app)
        new = "N3w!Passw0rd"
        status, html = b.submit(path, "/reset", password=new, confirm=new)
        self.assertEqual(status, 200, html[:500])
        self.assertIn("Password changed", html)
        v = app.store.accounts[1]["password"]
        self.assertNotEqual(v, old)
        # Same format the sign-up INSERT writes (game_verifier), checkable as the game client sends it.
        self.assertRegex(v, SQL_VERIFIER_RE)
        self.assertEqual(v.split("$")[:4], old.split("$")[:4])
        self.assertTrue(gwsignup.check_game_verifier(v, hashlib.md5(new.encode()).hexdigest()))
        self.assertFalse(gwsignup.check_game_verifier(v, hashlib.md5(PW.encode()).hexdigest()))
        # single use
        status, html = b.request("GET", path)
        self.assertEqual(status, 400)

    def test_reset_password_rules_apply(self):
        app, box = make_app()
        signup(app)
        app.store.emails[1]["verified"] = True
        Browser(app).submit("/forgot", "/forgot", email="hero1@example.com")
        drain(app)
        path = link_in(box.mails[-1], "/reset")
        status, html = Browser(app).submit(path, "/reset", password="short", confirm="short")
        self.assertEqual(status, 400)
        self.assertIn("8 to 32", html)
        self.assertIsNotNone(app.store.peek_reset(gwsignup.token_hash(path.split("=")[1])))   # still usable

    def test_unverified_address_gets_a_confirmation_link_not_a_reset(self):
        app, box = make_app()
        signup(app)
        drain(app)
        Browser(app).submit("/forgot", "/forgot", email="hero1@example.com")
        drain(app)
        self.assertEqual(len(box.mails), 2)
        self.assertIn("/verify?t=", box.mails[1]["text"])
        self.assertNotIn("/reset?t=", box.mails[1]["text"])
        Browser(app).submit("/forgot-username", "/forgot-username", email="hero1@example.com")
        drain(app)
        self.assertNotIn("Username: hero1", box.mails[-1]["text"])

    def test_forgot_username(self):
        app, box = make_app()
        signup(app)
        drain(app)
        app.store.emails[1]["verified"] = True
        status, html = Browser(app).submit("/forgot-username", "/forgot-username", email="HERO1@example.com")
        self.assertEqual(status, 200)
        drain(app)
        self.assertIn("Username: hero1", box.mails[-1]["text"])
        self.assertEqual(box.mails[-1]["to"], "Hero1@Example.com")

    def test_no_enumeration(self):
        app, box = make_app()
        signup(app)
        drain(app)
        app.store.emails[1]["verified"] = True
        n = len(box.mails)
        for page in ("/forgot", "/forgot-username"):
            known = Browser(app, "198.51.100.1").submit(page, page, email="hero1@example.com")
            unknown = Browser(app, "198.51.100.2").submit(page, page, email="nobody@example.com")
            self.assertEqual(known, unknown)
        drain(app)
        self.assertEqual(len(box.mails), n + 2)
        self.assertTrue(all(m["to"] == "Hero1@Example.com" for m in box.mails))

    def test_per_address_limit(self):
        app, box = make_app(LIMIT_MAILFORM_POSTS="100")
        signup(app)
        drain(app)    # mail 1 for this address (the confirmation)
        app.store.emails[1]["verified"] = True
        pages = [Browser(app, "198.51.100.%d" % i).submit("/forgot", "/forgot", email="hero1@example.com") for i in range(5)]
        drain(app)
        self.assertEqual(len({p for p in pages}), 1)      # same answer every time
        self.assertEqual(len(box.mails), 3)               # 3 per address per hour, confirmation included

    def test_per_ip_limit(self):
        app, box = make_app(LIMIT_MAILFORM_POSTS="100")
        for i in range(12):
            app.store.create("user%02d" % i, "u%02d@example.com" % i, "v", "h")
            app.store.emails[i + 1]["verified"] = True
        b = Browser(app, "192.0.2.50")
        for i in range(12):
            b.submit("/forgot", "/forgot", email="u%02d@example.com" % i)
        drain(app)
        self.assertEqual(len(box.mails), 10)

    def test_global_daily_cap(self):
        app, box = make_app(LIMIT_MAILS_DAY="2")
        for i in range(3):
            signup(app, "hero%d" % (i + 1), "h%d@example.com" % i, "192.0.2.%d" % i)
        drain(app)
        self.assertEqual(len(box.mails), 2)

    def test_fail_soft_resend_error(self):
        app, box = make_app()
        box.fail = gwsignup.MailError("HTTP 500 internal_server_error: boom")
        with self.assertLogs("gwsignup", "INFO") as logs:
            status, html = signup(app)
            drain(app)
        self.assertEqual(status, 200)
        self.assertIn("Welcome, hero1", html)
        self.assertIn(1, app.store.accounts)
        text = "\n".join(logs.output)
        self.assertIn("mail not sent kind=verify error=HTTP 500", text)
        self.assertNotIn(KEY, text)
        self.assertNotIn("Example.com", text)    # no addresses in the log

    def test_fail_soft_unexpected_exception(self):
        app, box = make_app()
        box.fail = RuntimeError("anything")
        status, html = signup(app)
        drain(app)
        self.assertEqual(status, 200)
        self.assertIn(1, app.store.accounts)

    def test_mail_off(self):
        app, box = make_app(key="")
        status, html = signup(app)
        self.assertEqual(status, 200)
        self.assertIn("we have not checked it yet", html)
        self.assertNotIn('href="/forgot"', html)
        for path in ("/forgot", "/forgot-username", "/verify?t=" + "a" * 43, "/reset?t=" + "a" * 43):
            self.assertEqual(Browser(app).request("GET", path)[0], 404, path)
        self.assertEqual(box.mails, [])

    def test_no_referrer_page_would_break_the_form(self):
        # Guard against the Origin: null trap: no page with a form may use referrer policy no-referrer.
        b = Browser(make_app()[0])
        b.policy = "no-referrer"
        self.assertEqual(b.request("POST", "/verify", {"t": "a" * 43})[0], 403)

    def test_footer_links_when_on(self):
        app, box = make_app()
        status, html = Browser(app).request("GET", "/")
        self.assertIn('href="/forgot"', html)
        self.assertIn('href="/forgot-username"', html)

    def test_bad_and_expired_links(self):
        app, box = make_app()
        signup(app)
        app.store.emails[1]["verified"] = True
        Browser(app).submit("/forgot", "/forgot", email="hero1@example.com")
        drain(app)
        path = link_in(box.mails[-1], "/reset")
        for t in app.store.tokens.values():
            t["expires"] = time.time() - 1
        self.assertEqual(Browser(app).request("GET", path)[0], 400)
        self.assertEqual(Browser(app).request("GET", "/reset?t=bogus")[0], 400)
        self.assertEqual(Browser(app).request("GET", "/verify")[0], 400)

    def test_link_uses_first_origin_not_host_header(self):
        app, box = make_app(ORIGIN="https://YOUR-DOMAIN.example,https://YOUR-DOMAIN.example")
        signup(app)
        drain(app)
        self.assertIn("https://YOUR-DOMAIN.example/verify?t=", box.mails[0]["text"])

    def test_mail_html_escapes(self):
        app, box = make_app()
        subject, text, html = app.mailer.build("username", names="  Username: <b>x</b>\n", usernames=["<b>x</b>"], forgot="https://x/forgot")
        self.assertIn("&lt;b&gt;x&lt;/b&gt;", html)
        self.assertNotIn("<b>x</b>", html)


class Scripts(unittest.TestCase):
    def test_shell_syntax(self):
        import subprocess
        for f in ("install.sh", "ops/set-resend-key.sh", "ops/test-mail.sh", "test/mail/pg-cluster.sh", "test/mail/test_mail_sql.sh"):
            p = os.path.join(SIGNUP, f)
            if os.path.exists(p):
                r = subprocess.run(["sh", "-n", p], capture_output=True, text=True)
                self.assertEqual(r.returncode, 0, f + r.stderr)


if __name__ == "__main__":
    unittest.main()
