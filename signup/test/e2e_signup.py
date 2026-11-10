#!/usr/bin/env python3
"""End-to-end checks of the sign-up site through its real HTML form (stdlib only).

    python3 -I e2e_signup.py BASE_URL [--prefix NAME] [--json OUT]

BASE_URL is the TEST app, e.g. http://127.0.0.1:8091 (on the VPS, or through an ssh tunnel).
Each group of checks uses its own fake client IP (X-Real-IP, which the app trusts only from
127.0.0.1) so the rate limits of one group do not affect another.

It prints one line per check and writes the accounts it created (and the ones it expects NOT
to exist) to --json, so the database can be checked afterwards with check_db.sh.
Never point this at the live site: it creates accounts.
"""
import argparse
import http.client
import json
import random
import re
import sys
import time
import urllib.parse

ap = argparse.ArgumentParser()
ap.add_argument("base")
ap.add_argument("--prefix", default="e2e%04d" % random.randrange(10000))
ap.add_argument("--json", default=None)
ap.add_argument("--password", default="Olymp0s!Rise")
a = ap.parse_args()
u = urllib.parse.urlsplit(a.base)
assert u.hostname in ("127.0.0.1", "localhost"), "only for the local test app"
ORIGIN = "%s://%s" % (u.scheme, u.netloc)

WORDS = {w: i for i, w in enumerate(["zero", "one", "two", "three", "four", "five", "six", "seven", "eight",
                                     "nine", "ten", "eleven", "twelve"])}
results, created, absent = [], [], []


def request(method, path, body=None, headers=None):
    c = http.client.HTTPConnection(u.hostname, u.port, timeout=30)
    h = dict(headers or {})
    if body is not None:
        body = urllib.parse.urlencode(body).encode()
        h.setdefault("Content-Type", "application/x-www-form-urlencoded")
    c.request(method, path, body=body, headers=h)
    r = c.getresponse()
    data = r.read().decode("utf-8", "replace")
    out = (r.status, dict((k.lower(), v) for k, v in r.getheaders()), data)
    c.close()
    return out


class Browser:
    def __init__(self, ip):
        self.ip, self.cookie = ip, None

    def get_form(self):
        st, hd, html = request("GET", "/", headers=self.hdr())
        assert st == 200, st
        sc = hd.get("set-cookie")
        if sc:
            self.cookie = sc.split(";")[0]
        f = {k: re.search(r'name="%s" value="([^"]*)"' % k, html).group(1) for k in ("ts", "n", "tok", "chk")}
        q = re.search(r"Quick check: What is (\w+) (plus|minus) (\w+)\?", html)
        x, op, y = WORDS[q.group(1)], q.group(2), WORDS[q.group(3)]
        f["answer"] = str(x + y if op == "plus" else x - y)
        return f, html

    def hdr(self, origin=True):
        h = {"X-Real-IP": self.ip}
        if self.cookie:
            h["Cookie"] = self.cookie
        if origin:
            h["Origin"] = ORIGIN
        return h

    def submit(self, fields, form=None, wait=3.3, origin=True, cookie=True):
        f = form or self.get_form()[0]
        time.sleep(wait)
        body = dict(f)
        body.update({"username": "", "password": "", "confirm": "", "email": "", "website": ""})
        body.update(fields)
        h = self.hdr(origin)
        if not cookie:
            h.pop("Cookie", None)
        return request("POST", "/signup", body, h)


def check(name, cond, detail=""):
    results.append({"check": name, "ok": bool(cond), "detail": detail})
    print("%s  %s%s" % ("PASS" if cond else "FAIL", name, ("  (" + detail + ")") if detail else ""))


def text_of(html):
    m = re.search(r'<div class="alert"[^>]*>(.*?)</div>', html, re.S)
    if m:
        return m.group(1).strip()
    m = re.search(r"<h1>(.*?)</h1>", html, re.S)
    return m.group(1).strip() if m else html[:80]


P = a.password
pre = a.prefix
main_name = pre + "main"
main_email = "%s.main@example.test" % pre

# --- 1. a normal sign-up
b = Browser("10.0.0.1")
f, page = b.get_form()
check("page has the logo, the form and a CSRF cookie",
      'src="/static/logo.png"' in page and 'name="website"' in page and b.cookie is not None, b.cookie and b.cookie.split("=")[0])
st, hd, html = b.submit({"username": main_name, "password": P, "confirm": P, "email": main_email}, form=f)
check("valid sign-up creates the account", st == 200 and "Welcome, %s" % main_name in html, "%d %s" % (st, text_of(html)))
check("success page says: download the client and log in", "Next: download the client and log in" in html)
created.append({"username": main_name, "email": main_email})

# --- 2. duplicates and invalid names (same fake IP group, well under the limits)
b2 = Browser("10.0.0.2")
GENERIC = "That username or email cannot be used"
cases = [
    ("duplicate username", {"username": main_name, "email": pre + ".dup@example.test"}, GENERIC),
    ("duplicate email (other case)", {"username": pre + "other", "email": main_email.upper()}, GENERIC),
    ("reserved name 'admin'", {"username": "admin", "email": pre + ".r1@example.test"}, GENERIC),
    ("reserved name 'gm" + pre[-4:] + "'", {"username": "gm" + pre[-4:], "email": pre + ".r2@example.test"}, GENERIC),
    ("existing game account (curlhero1)", {"username": "curlhero1", "email": pre + ".r3@example.test"}, GENERIC),
]
for label, fields, expect in cases:
    fields = dict(fields, password=P, confirm=P)
    st, _, html = b2.submit(fields, wait=3.1)
    check("refused: " + label, st == 400 and expect in html, "%d %s" % (st, text_of(html)))
    absent.append(fields["username"]) if fields["username"] != main_name and fields["username"] != "curlhero1" else None

b3 = Browser("10.0.0.3")
bad = [
    ("uppercase name", {"username": pre.capitalize() + "x", "password": P, "confirm": P}, "lowercase"),
    ("too short name", {"username": "abc", "password": P, "confirm": P}, "4 to 16"),
    ("symbol in name", {"username": pre + "_x", "password": P, "confirm": P}, "4 to 16"),
    ("too long name", {"username": "a" * 17, "password": P, "confirm": P}, "4 to 16"),
    ("passwords differ", {"username": pre + "pw1", "password": P, "confirm": P + "x"}, "not the same"),
    ("password too short", {"username": pre + "pw2", "password": "abc12", "confirm": "abc12"}, "8 to 32"),
    ("password with space", {"username": pre + "pw3", "password": "has space 123", "confirm": "has space 123"}, "8 to 32"),
    ("bad email", {"username": pre + "em1", "password": P, "confirm": P, "email": "not-an-email"}, "email address"),
]
for label, fields, expect in bad:
    fields.setdefault("email", "%s.%s@example.test" % (pre, fields["username"][-3:]))
    st, _, html = b3.submit(fields, wait=3.1)
    check("refused: " + label, st == 400 and expect in html, "%d %s" % (st, text_of(html)))
    absent.append(fields["username"])

# --- 3. the challenge
b4 = Browser("10.0.0.4")
f, _ = b4.get_form()
f["answer"] = str(int(f["answer"]) + 1)
st, _, html = b4.submit({"username": pre + "ch1", "password": P, "confirm": P, "email": pre + ".ch1@example.test"}, form=f)
check("refused: wrong answer to the question", st == 400 and "answer to the question was not right" in html, "%d" % st)
st, _, html = b4.submit({"username": pre + "ch2", "password": P, "confirm": P, "email": pre + ".ch2@example.test"}, wait=0)
check("refused: form sent faster than a person could", st == 400 and "That was quick" in html, "%d" % st)
absent += [pre + "ch1", pre + "ch2"]

# --- 4. honeypot: looks like success, creates nothing
b5 = Browser("10.0.0.5")
st, _, html = b5.submit({"username": pre + "bot", "password": P, "confirm": P, "email": pre + ".bot@example.test",
                         "website": "http://spam.example"})
check("honeypot: bot sees a success page", st == 200 and "Welcome" in html, "%d" % st)
absent.append(pre + "bot")

# --- 5. CSRF
b6 = Browser("10.0.0.6")
st, _, html = b6.submit({"username": pre + "cs1", "password": P, "confirm": P, "email": pre + ".cs1@example.test"}, cookie=False)
check("CSRF: no cookie -> 403", st == 403, "%d %s" % (st, text_of(html)))
f, _ = b6.get_form()
f2 = dict(f, tok="0" * 64)
st, _, _ = b6.submit({"username": pre + "cs2", "password": P, "confirm": P, "email": pre + ".cs2@example.test"}, form=f2)
check("CSRF: forged token -> 403", st == 403, "%d" % st)
other = Browser("10.0.0.6")
other.get_form()
b6c = b6.cookie
b6.cookie = other.cookie
st, _, _ = b6.submit({"username": pre + "cs3", "password": P, "confirm": P, "email": pre + ".cs3@example.test"}, form=f)
check("CSRF: token from another browser's cookie -> 403", st == 403, "%d" % st)
b6.cookie = b6c
f, _ = b6.get_form()
st, _, html = b6.submit({"username": pre + "cs4", "password": P, "confirm": P, "email": pre + ".cs4@example.test"}, form=f)
check("CSRF: first use of a token is accepted", st == 200 and "Welcome" in html, "%d %s" % (st, text_of(html)))
created.append({"username": pre + "cs4", "email": pre + ".cs4@example.test"})
st, _, html = b6.submit({"username": pre + "cs5", "password": P, "confirm": P, "email": pre + ".cs5@example.test"}, form=f, wait=0)
check("CSRF: the same token again -> 403", st == 403, "%d" % st)
f, _ = b6.get_form()
h = b6.hdr()
h["Origin"] = "https://evil.example"
time.sleep(3.2)
st, _, _ = request("POST", "/signup", dict(f, username=pre + "cs6", password=P, confirm=P, email=pre + ".cs6@example.test", website=""), h)
check("CSRF: foreign Origin header -> 403", st == 403, "%d" % st)
absent += [pre + "cs1", pre + "cs2", pre + "cs3", pre + "cs5", pre + "cs6"]

# --- 6. rate limits
b7 = Browser("10.0.0.7")
codes = []
for i in range(11):
    st, _, _ = b7.submit({"username": "x", "password": P, "confirm": P, "email": "x@example.test"}, wait=0)
    codes.append(st)
check("rate limit: 11th form post within 10 minutes -> 429", codes[:10].count(429) == 0 and codes[10] == 429, str(codes))

b8 = Browser("10.0.0.8")
codes = []
for i in range(4):
    n = "%srl%d" % (pre, i)
    st, _, html = b8.submit({"username": n, "password": P, "confirm": P, "email": "%s.rl%d@example.test" % (pre, i)}, wait=3.1)
    codes.append(st)
    if st == 200 and "Welcome" in html:
        created.append({"username": n, "email": "%s.rl%d@example.test" % (pre, i)})
    else:
        absent.append(n)
check("rate limit: 4th new account from one IP in an hour -> 429", codes == [200, 200, 200, 429], str(codes))

# --- 7. misc
st, _, _ = request("GET", "/healthz")
check("health check", st == 200)
st, _, _ = request("GET", "/static/../app/gwsignup.py")
check("static files cannot escape the static folder", st == 404, "%d" % st)
st, hd, _ = request("GET", "/static/style.css")
check("stylesheet is served", st == 200 and hd.get("content-type", "").startswith("text/css"))

ok = sum(r["ok"] for r in results)
print("\n%d/%d checks passed. prefix=%s" % (ok, len(results), pre))
if a.json:
    json.dump({"prefix": pre, "password": "(given on the command line)", "created": created,
               "absent": sorted(set(absent)), "results": results}, open(a.json, "w"), indent=1)
sys.exit(0 if ok == len(results) else 1)
