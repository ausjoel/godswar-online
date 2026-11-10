import copy
import http.client
import json
from pathlib import Path
import sys
import threading
import types
import unittest
from http.server import ThreadingHTTPServer
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from editor_auth import Auth, COOKIE, TTL, verify_password
from modules.accounts import verifier
from server import Handler

class AuthTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls): cls.record = verifier('Valid!123')
    def setUp(self):
        self.now = 100.0
        self.account = dict(id=7, username='Admin', password=self.record, status=0, editor_admin=True)
        self.auth = Auth(self.lookup, lambda: self.now)
    def lookup(self, username=None, aid=None):
        return copy.copy(self.account) if (username == self.account['username'] or aid == self.account['id']) else None
    def test_password_format_and_exact_game_password(self):
        self.assertTrue(verify_password('Valid!123', self.record))
        for pw in ['valid!123', 'bad', None, '🤔'*4]: self.assertFalse(verify_password(pw, self.record))
        for record in ['Valid!123', self.record.replace('600000', '2000001'), 'gws$pbkdf2-sha256$v1$600000$??$??']:
            self.assertFalse(verify_password('Valid!123', record))
    def test_only_active_admin_accounts_authenticate(self):
        self.assertEqual(self.auth.login('Admin', 'Valid!123', 'ip')[1], 200)
        for user, pw in [('admin', 'Valid!123'), ('Admin', 'Wrong!123'), ('Unknown', 'Valid!123')]:
            self.assertEqual(self.auth.login(user, pw, 'ip')[1], 401)
        self.account['editor_admin'] = False
        self.assertEqual(self.auth.login('Admin', 'Valid!123', 'ip')[1], 401)
        self.account.update(editor_admin=True, status=1)
        self.assertEqual(self.auth.login('Admin', 'Valid!123', 'ip')[1], 401)
    def test_role_ban_password_revocation_and_expiry(self):
        for field, value in [('editor_admin', False), ('status', 1), ('password', 'changed')]:
            old = self.account[field]; token = self.auth.issue(self.account); cookie = COOKIE+'='+token
            self.assertIsNotNone(self.auth.session(cookie))
            self.account[field] = value
            self.assertIsNone(self.auth.session(cookie)); self.account[field] = old
            self.assertIsNone(self.auth.session(cookie))
        token = self.auth.issue(self.account)
        self.now += TTL
        self.assertIsNone(self.auth.session(COOKIE+'='+token))
    def test_cookie_tampering_logout_and_server_memory(self):
        token = self.auth.issue(self.account)
        self.assertIsNone(self.auth.session(COOKIE+'='+token+'x'))
        self.auth.logout(COOKIE+'='+token)
        self.assertIsNone(self.auth.session(COOKIE+'='+token))
        token = self.auth.issue(bootstrap=True)
        self.assertTrue(self.auth.session(COOKIE+'='+token)['bootstrap'])
        self.assertNotIn(token, self.auth.sessions)
    def test_username_throttle_and_window_expiry(self):
        for _ in range(10): self.assertTrue(self.auth._allowed_attempt('ip', 'Admin'))
        self.assertFalse(self.auth._allowed_attempt('different-ip', 'Admin'))
        self.now += 901
        self.assertTrue(self.auth._allowed_attempt('ip', 'Admin'))

class HttpAuthTests(AuthTests):
    def setUp(self):
        super().setUp()
        app = types.SimpleNamespace(auth=self.auth, bootstrap_token='x'*48, csrf='csrf', a=types.SimpleNamespace(verbose=False, public_origin='https://editor.example'))
        class TestHandler(Handler): pass
        TestHandler.app = app
        self.httpd = ThreadingHTTPServer(('127.0.0.1', 0), TestHandler)
        self.thread = threading.Thread(target=self.httpd.serve_forever, daemon=True); self.thread.start()
    def tearDown(self): self.httpd.shutdown(); self.httpd.server_close(); self.thread.join()
    def request(self, method, path, body=None, headers=None):
        conn = http.client.HTTPConnection('127.0.0.1', self.httpd.server_port)
        headers = {'Host':'127.0.0.1', **(headers or {})}
        conn.request(method, path, body, headers)
        r = conn.getresponse(); data = r.read(); result = (r.status, dict(r.getheaders()), data); conn.close(); return result
    def test_anonymous_all_private_surfaces_blocked(self):
        for path in ['/api/meta', '/api/m/accounts/list', '/api/changes', '/api/m/catalog/index/item']:
            self.assertEqual(self.request('GET',path)[0],401,path)
        for path in ['/', '/classic', '/static/progress.json', '/static/shell.js']:
            self.assertEqual(self.request('GET',path)[0],303,path)
        self.assertEqual(self.request('GET','/login')[0],200)
        self.assertEqual(self.request('POST','/api/m/accounts/7/editor-admin','{}')[0],401)
    def test_login_cookie_attributes_origin_and_session(self):
        body = json.dumps({'username':'Admin','password':'Valid!123'})
        self.assertEqual(self.request('POST','/auth/login',body,{'Content-Type':'application/json','Origin':'https://evil.example'})[0],403)
        status, headers, data = self.request('POST','/auth/login',body,{'Content-Type':'application/json','Origin':'https://editor.example'})
        self.assertEqual(status,303)
        cookie = headers['Set-Cookie']; self.assertTrue(all(x in cookie for x in ['Secure','HttpOnly','SameSite=Strict','Path=/']))
        cookie = cookie.split(';')[0]
        r = self.request('GET','/auth/session',headers={'Cookie':cookie}); self.assertEqual(r[0],200); self.assertEqual(json.loads(r[2])['username'],'Admin')
        self.account['editor_admin'] = False
        self.assertEqual(self.request('GET','/auth/session',headers={'Cookie':cookie})[0],303)
    def test_bootstrap_cannot_be_forged_and_logout_needs_csrf(self):
        self.assertEqual(self.request('GET','/auth/bootstrap')[0],403)
        self.assertEqual(self.request('GET','/auth/bootstrap',headers={'X-Editor-Bootstrap':'wrong'})[0],403)
        r = self.request('GET','/auth/bootstrap',headers={'X-Editor-Bootstrap':'x'*48}); self.assertEqual(r[0],303)
        cookie=r[1]['Set-Cookie'].split(';')[0]
        headers={'Cookie':cookie,'Origin':'https://editor.example'}
        self.assertEqual(self.request('POST','/auth/logout',headers=headers)[0],403)
        headers['X-CSRF-Token']='csrf'
        r=self.request('POST','/auth/logout',headers=headers); self.assertEqual(r[0],303); self.assertIn('Max-Age=0',r[1]['Set-Cookie'])
        self.assertEqual(self.request('GET','/auth/session',headers={'Cookie':cookie})[0],303)


class OwnerRecoveryTests(unittest.TestCase):
    def test_long_owner_password_is_separate_from_game_credentials(self):
        password = 'OwnerRecovery' * 5
        record = verifier(password)
        auth = Auth(lambda **kw: None, recovery_record=record)
        self.assertFalse(verify_password(password, record))
        token, status = auth.recovery_login('admin', password, 'test')
        self.assertEqual(status, 200)
        self.assertTrue(auth.session(COOKIE+'='+token)['bootstrap'])
        self.assertEqual(auth.recovery_login('other', password, 'test')[1],401)
        self.assertEqual(auth.recovery_login('admin', 'Wrong!123', 'test')[1],401)
    def test_recovery_disabled_without_a_private_verifier(self):
        self.assertEqual(Auth(lambda **kw: None).recovery_login('admin','Wrong!123','test')[1],503)


class PersistentSessionTests(unittest.TestCase):
    def test_restart_logout_and_owner_credential_rotation(self):
        import tempfile
        with tempfile.TemporaryDirectory() as root:
            file=Path(root)/'sessions.sqlite3';record=verifier('Owner!123')
            first=Auth(lambda **kw:None,clock=lambda:100.0,recovery_record=record,session_file=file)
            token=first.issue(bootstrap=True);cookie=COOKIE+'='+token
            second=Auth(lambda **kw:None,clock=lambda:101.0,recovery_record=record,session_file=file)
            self.assertTrue(second.session(cookie)['bootstrap'])
            self.assertEqual(file.stat().st_mode & 0o777,0o600)
            self.assertNotIn(token,file.read_bytes().decode('latin1'))
            rotated=Auth(lambda **kw:None,clock=lambda:102.0,recovery_record=verifier('Changed!123'),session_file=file)
            self.assertIsNone(rotated.session(cookie))
            token=second.issue(bootstrap=True);cookie=COOKIE+'='+token;second.logout(cookie)
            third=Auth(lambda **kw:None,clock=lambda:103.0,recovery_record=record,session_file=file)
            self.assertIsNone(third.session(cookie))

if __name__ == '__main__': unittest.main(verbosity=2)
