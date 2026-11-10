"""Opt-in game-account authentication for the editor; no game login/presence changes."""
import base64
import hashlib
import hmac
from http.cookies import SimpleCookie, CookieError
import secrets
import os
from pathlib import Path
import sqlite3
import threading
import time

COOKIE = '__Host-gweditor'
TTL = 4 * 60 * 60
MAX_SESSIONS = 1024


def verify_password(password, record, max_length=32):
    try:
        if not isinstance(password, str) or not 4 <= len(password) <= max_length:
            return False
        prefix, algorithm, version, count, salt, expected = record.split('$')
        iterations = int(count)
        if (prefix, algorithm, version) != ('gws', 'pbkdf2-sha256', 'v1') or not 100_000 <= iterations <= 2_000_000:
            return False
        salt = base64.b64decode(salt, validate=True)
        expected = base64.b64decode(expected, validate=True)
        if len(salt) != 16 or len(expected) != 32:
            return False
        legacy = hashlib.md5(password.encode('ascii')).hexdigest().encode('ascii')
        actual = hashlib.pbkdf2_hmac('sha256', legacy, salt, iterations, 32)
        return hmac.compare_digest(actual, expected)
    except (ValueError, TypeError, AttributeError, UnicodeError):
        return False


class Auth:
    def __init__(self, lookup, clock=time.time, recovery_record=None, session_file=None):
        self.recovery_record = recovery_record
        self.lookup = lookup
        self.clock = clock
        self.lock = threading.RLock()
        self.sessions = {}
        self.attempts = {}
        self.session_file = Path(session_file) if session_file else None
        if self.session_file:
            if self.session_file.is_symlink(): raise ValueError("session database must not be a symlink")
            fd=os.open(self.session_file,os.O_CREAT|os.O_RDWR|getattr(os,"O_NOFOLLOW",0),0o600);os.close(fd)
            self.session_file.chmod(0o600)
            with sqlite3.connect(self.session_file) as db:
                db.execute("CREATE TABLE IF NOT EXISTS sessions (token_hash TEXT PRIMARY KEY, payload TEXT NOT NULL)")
                for key,payload in db.execute("SELECT token_hash,payload FROM sessions"):
                    try:
                        value=__import__('json').loads(payload)
                        if len(key)==64 and all(c in '0123456789abcdef' for c in key) and self.clock()<value['expires']<=self.clock()+TTL+60:
                            self.sessions[key]=value
                    except (ValueError,KeyError,TypeError): pass

        # Same KDF work on nonexistent, banned and non-admin accounts.
        self.dummy = 'gws$pbkdf2-sha256$v1$600000$' + base64.b64encode(secrets.token_bytes(16)).decode() + '$' + base64.b64encode(secrets.token_bytes(32)).decode()

    def _persist(self):
        if self.session_file:
            import json
            with sqlite3.connect(self.session_file) as db:
                db.execute("DELETE FROM sessions")
                db.executemany("INSERT INTO sessions(token_hash,payload) VALUES (?,?)",[(key,json.dumps(value)) for key,value in self.sessions.items()])

    def _recovery_fingerprint(self):
        return hashlib.sha256(self.recovery_record.encode()).hexdigest() if self.recovery_record else None

    def _allowed_attempt(self, address, user):
        now = self.clock()
        with self.lock:
            self.attempts = {k: v for k, v in self.attempts.items() if v[0] > now - 900}
            # Global, per address and per username limits; no unbounded attacker keys.
            keys = [('global', ''), ('ip', address), ('user', user)]
            limits = [120, 20, 10]
            if len(self.attempts) > 4096:
                return False
            if any(self.attempts.get(k, (now, 0))[1] >= limit for k, limit in zip(keys, limits)):
                return False
            for k in keys:
                since, count = self.attempts.get(k, (now, 0))
                self.attempts[k] = (since, count + 1)
            return True

    @staticmethod
    def fingerprint(account):
        return hashlib.sha256(str(account['password']).encode()).hexdigest()

    def issue(self, account=None, bootstrap=False):
        now = self.clock()
        token = secrets.token_urlsafe(32)
        with self.lock:
            self.sessions = {k: v for k, v in self.sessions.items() if v['expires'] > now}
            if len(self.sessions) >= MAX_SESSIONS:
                oldest = min(self.sessions, key=lambda k: self.sessions[k]['expires'])
                del self.sessions[oldest]
            self.sessions[hashlib.sha256(token.encode()).hexdigest()] = {
                'expires': now + TTL, 'bootstrap': bootstrap,
                'username': account['username'] if account else 'owner recovery',
                'id': account['id'] if account else None,
                'fingerprint': self.fingerprint(account) if account else self._recovery_fingerprint(),
            }
            self._persist()
        return token

    def login(self, username, password, address):
        if not isinstance(username, str) or not 1 <= len(username) <= 32 or any(ord(c) < 33 or ord(c) > 126 for c in username):
            username = ''
        if not self._allowed_attempt(address, username):
            return None, 429
        account = self.lookup(username=username) if username else None
        eligible = account and account.get('editor_admin') is True and account.get('status') == 0
        valid = verify_password(password, account['password'] if eligible else self.dummy)
        if not eligible or not valid:
            return None, 401
        return self.issue(account), 200

    def recovery_login(self, username, password, address):
        if not self._allowed_attempt(address, 'owner-recovery'):
            return None, 429
        if not self.recovery_record:
            return None, 503
        eligible = username == 'admin'
        valid = verify_password(password, self.recovery_record if eligible else self.dummy, max_length=256)
        return (self.issue(bootstrap=True), 200) if eligible and valid else (None, 401)

    def session(self, cookie):
        try:
            parsed = SimpleCookie(); parsed.load(cookie or '')
            token = parsed[COOKIE].value
            if len(token) > 128:
                return None
        except (CookieError, KeyError, TypeError):
            return None
        key = hashlib.sha256(token.encode()).hexdigest()
        with self.lock:
            session = self.sessions.get(key)
        if not session:
            return None
        valid = session['expires'] > self.clock()
        if valid and session['bootstrap']:
            valid = session['fingerprint'] == self._recovery_fingerprint()
        if valid and not session['bootstrap']:
            account = self.lookup(aid=session['id'])
            valid = bool(account and account.get('editor_admin') is True and account.get('status') == 0 and
                         hmac.compare_digest(session['fingerprint'], self.fingerprint(account)))
        if not valid:
            with self.lock:
                self.sessions.pop(key, None)
                self._persist()
            return None
        return dict(session)

    def logout(self, cookie):
        try:
            parsed = SimpleCookie(); parsed.load(cookie or '')
            key = hashlib.sha256(parsed[COOKIE].value.encode()).hexdigest()
            with self.lock:
                self.sessions.pop(key, None)
                self._persist()
        except (CookieError, KeyError, TypeError):
            pass


def account_lookup(rb, *, username=None, aid=None):
    from modules.common import lit
    where = 'id = %d' % int(aid) if aid is not None else 'username = %s' % lit(username)
    return rb.pg.json('(SELECT row_to_json(a) FROM (SELECT id, username, password, status, editor_admin FROM accounts WHERE %s) a)' % where)
