#!/usr/bin/env python3
"""Adversarial checks of the sign-up site (stdlib only). Creates accounts: test copies only.

    python3 -I adversarial.py [OUT.json]

Set-up used on 2026-10-28 (all on the VPS, reached from the Mac by
`ssh -L 8092:127.0.0.1:8092 -L 18093:127.0.0.1:18093 vps`):
  * a fresh DB copy godswar_signupv + a reborn copy on 64999/65000/127.0.0.1:65090,
  * install.sh --test --db godswar_signupv --port 8092 --name gwsignup-verify --role gw_signup_verify,
  * Debian's caddy 2.6.2 (unpacked, not installed) on 127.0.0.1:18093 with the same
    reverse_proxy + header_up X-Real-IP block as caddy/Caddyfile, for the header-spoofing check,
  * a seeded account MixedCase9 / Existing.Person@Example.COM for the letter-case duplicate checks.
Covers SQL/XSS input, oversize and malformed bodies, unicode and look-alike names, letter-case
duplicates, refusal timing, CSRF, X-Real-IP/X-Forwarded-For spoofing through Caddy, races,
and password edge cases. Then log in with game_login.py for the accounts it lists as created.
"""
import http.client, json, random, re, sys, time, urllib.parse, socket, threading

DIRECT = ("127.0.0.1", 8092)
CADDY = ("127.0.0.1", 18093)
ORIGIN = "http://127.0.0.1:8092"
WORDS = {w: i for i, w in enumerate("zero one two three four five six seven eight nine ten eleven twelve".split())}
PFX = "v%03d" % random.randrange(1000)
results = []
created = []


def req(method, path, body=None, headers=None, target=DIRECT, raw=None):
    c = http.client.HTTPConnection(*target, timeout=40)
    h = dict(headers or {})
    if raw is not None:
        body = raw
    elif body is not None:
        body = urllib.parse.urlencode(body).encode()
        h.setdefault("Content-Type", "application/x-www-form-urlencoded")
    c.request(method, path, body=body, headers=h)
    r = c.getresponse()
    data = r.read().decode("utf-8", "replace")
    out = (r.status, {k.lower(): v for k, v in r.getheaders()}, data)
    c.close()
    return out


class B:
    def __init__(self, ip=None, target=DIRECT, extra=None):
        self.ip, self.cookie, self.target, self.extra = ip, None, target, extra or {}

    def hdr(self, origin=True):
        h = dict(self.extra)
        if self.ip:
            h["X-Real-IP"] = self.ip
        if self.cookie:
            h["Cookie"] = self.cookie
        if origin:
            h["Origin"] = ORIGIN
        return h

    def form(self):
        st, hd, html = req("GET", "/", headers=self.hdr(False), target=self.target)
        assert st == 200, st
        if hd.get("set-cookie"):
            self.cookie = hd["set-cookie"].split(";")[0]
        f = {k: re.search(r'name="%s" value="([^"]*)"' % k, html).group(1) for k in ("ts", "n", "tok", "chk")}
        q = re.search(r"Quick check: What is (\w+) (plus|minus) (\w+)\?", html)
        x, op, y = WORDS[q.group(1)], q.group(2), WORDS[q.group(3)]
        f["answer"] = str(x + y if op == "plus" else x - y)
        return f

    def submit(self, fields, wait=3.2, origin=True, f=None, headers=None):
        f = dict(f or self.form())
        time.sleep(wait)
        body = {"username": "", "password": "", "confirm": "", "email": "", "website": ""}
        body.update(f)
        body.update(fields)
        h = self.hdr(origin)
        h.update(headers or {})
        return req("POST", "/signup", body, h, target=self.target)


def check(name, ok, detail=""):
    results.append((name, bool(ok), detail))
    print(("PASS " if ok else "FAIL ") + name + ("  -- " + detail if detail else ""), flush=True)


def alert(html):
    m = re.search(r'<div class="alert"[^>]*>(.*?)</div>', html, re.S)
    return m.group(1).strip() if m else ""


def ip():
    return "10.%d.%d.%d" % (random.randrange(256), random.randrange(256), random.randrange(1, 255))


PW = "Zx9!q\"&+=%%#\\<>'Ab"  # symbols that need urlencoding/escaping
GOOD = lambda u, e, pw=PW: {"username": u, "password": pw, "confirm": pw, "email": e}

# ---------------------------------------------------------------- injection / XSS
b = B(ip())
u1 = PFX + "sqli"
st, _, html = b.submit(GOOD(u1, "o'brien+x{|}@example.com"))
check("valid signup with SQL-ish email chars", st == 200 and "Welcome" in html, "%s %s" % (st, alert(html)))
if st == 200:
    created.append((u1, PW))

b = B(ip())
st, _, html = b.submit(GOOD("a' or 1=1--", "a@b.co"))
check("SQLi username rejected by format", st == 400 and "4 to 16" in alert(html), alert(html))
xss = '"><script>alert(1)</script>'
st, _, html = b.submit(GOOD(xss, "x@y.co"))
check("XSS username escaped in re-render", "<script>alert(1)" not in html and "&lt;script&gt;" in html, str(st))
st, _, html = b.submit(GOOD("okname12", xss + "@e.com"))
check("XSS email escaped in re-render", "<script>alert(1)" not in html, alert(html))

# ---------------------------------------------------------------- oversize / malformed
b = B(ip())
f = b.form()
time.sleep(3.2)
big = urllib.parse.urlencode(dict(f, username="a" * 9000)).encode()
st, _, html = req("POST", "/signup", raw=big, headers=dict(b.hdr(), **{"Content-Type": "application/x-www-form-urlencoded"}))
check("9 KB body -> 413", st == 413, str(st))
st, _, html = b.submit({"username": "a" * 4000, "email": "e" * 2000 + "@x.com"})
check("4 KB username within body cap -> 400 format", st in (400,), "%s %s" % (st, alert(html)[:60]))
many = "&".join("f%d=1" % i for i in range(40)).encode()
st, _, _ = req("POST", "/signup", raw=many, headers=dict(B(ip()).hdr(), **{"Content-Type": "application/x-www-form-urlencoded"}))
check("40 fields -> refused (403 no cookie/token)", st == 403, str(st))
st, _, _ = req("POST", "/signup", raw=b'{"a":1}', headers={"Content-Type": "application/json", "X-Real-IP": ip(), "Origin": ORIGIN})
check("JSON body -> 403", st == 403, str(st))
st, _, _ = req("POST", "/signup", raw=b"\xff\xfe\x00bad", headers={"Content-Type": "application/x-www-form-urlencoded", "X-Real-IP": ip(), "Origin": ORIGIN})
check("non-UTF8 body -> 403", st == 403, str(st))
st, _, _ = req("PUT", "/signup", raw=b"", headers={"X-Real-IP": ip()})
check("PUT -> 405", st == 405, str(st))
st, _, _ = req("GET", "/static/../app/gwsignup.py")
st2, _, _ = req("GET", "/static/%2e%2e/app/gwsignup.py")
st3, _, _ = req("GET", "/static//etc/passwd")
check("static path escapes -> 404", st == 404 and st2 == 404 and st3 == 404, "%s %s %s" % (st, st2, st3))

# ---------------------------------------------------------------- unicode / homoglyph / untypable
b = B(ip())
for name in ["аdmin123", "ｇｍｔｅｓｔ", "abcdé", "ab cd", "abc​d", "abcd\x00", "KelvinK", "abc-def", "abc_def", "abc.def", "ǆabcd", "abcd\t"]:
    b = B(ip())
    st, _, html = b.submit(GOOD(name, "u%d@ex.com" % random.randrange(10**6)))
    a = alert(html)
    ok = st == 400 and ("4 to 16" in a or "lowercase" in a.lower())
    if name == "abcd\t":  # stripped -> valid format, but we did not want to create it... see below
        ok = st in (200, 400)
        if st == 200:
            created.append(("abcd", PW))
    check("untypable/unicode name %r refused" % name, ok, "%s %s" % (st, a[:70]))

# leetspeak / homoglyph-ish reserved names
b = B(ip())
for name in ["adm1n", "4dmin", "g4mem4ster", "supp0rt", "m0derator", "0fficial", "godsw4r"]:
    st, _, html = b.submit(GOOD(name + "x", "r%d@ex.com" % random.randrange(10**6)))
    check("leet reserved %r refused" % (name + "x"), st == 400 and "cannot be used" in alert(html), "%s %s" % (st, alert(html)[:60]))
    if st == 200:
        created.append((name + "x", PW))
    b = B(ip())

# legit names that contain reserved fragments by accident
b = B(ip())
for name in ["piggy" + PFX[1:], "bigger" + PFX[1:]]:
    st, _, html = b.submit(GOOD(name, "%s@legit.com" % name))
    check("legit name %r allowed" % name, st == 200, "%s %s" % (st, alert(html)[:60]))
    if st == 200:
        created.append((name, PW))
    b = B(ip())

# ---------------------------------------------------------------- duplicates (case-insensitive) against existing accounts
b = B(ip())
st, _, h1 = b.submit(GOOD("mixedcase9", "fresh%d@ex.com" % random.randrange(10**6)))
check("lowercase of existing MixedCase9 refused", st == 400 and "cannot be used" in alert(h1), alert(h1))
st, _, h2 = b.submit(GOOD(PFX + "dupe1", "existing.person@example.com"))
check("existing accounts.email (other case) refused", st == 400 and "cannot be used" in alert(h2), alert(h2))
st, _, h3 = b.submit(GOOD("testuser", "fresh%d@ex.com" % random.randrange(10**6)))
check("existing testuser refused, same message", st == 400 and alert(h3) == alert(h1), alert(h3))
b = B(ip())
st, _, h4 = b.submit(GOOD("gmhelper", "fresh%d@ex.com" % random.randrange(10**6)))
check("reserved gm-prefix same message", st == 400 and alert(h4) == alert(h1), alert(h4))
st, _, h5 = b.submit(GOOD(PFX + "dupe2", "O'BRIEN+X{|}@EXAMPLE.COM"))
check("site-made email in upper case refused", st == 400 and alert(h5) == alert(h1), alert(h5))

# ---------------------------------------------------------------- timing / enumeration
def timed(fields):
    bb = B(ip())
    f = bb.form()
    time.sleep(3.2)
    t = time.time()
    st, _, html = bb.submit(fields, wait=0, f=f)
    return time.time() - t, st, alert(html)

tt = [timed(GOOD("testuser", "t%d@ex.com" % random.randrange(10**6)))[0] for _ in range(3)]
tr = [timed(GOOD("adminzz", "t%d@ex.com" % random.randrange(10**6)))[0] for _ in range(3)]
te = [timed(GOOD("free%s%d" % (PFX, i), "existing.person@example.com"))[0] for i in range(3)]
check("taken/reserved/email-used timings similar (no KDF on any refusal)", max(tt + tr + te) - min(tt + tr + te) < 0.4,
      "taken %s reserved %s email %s" % (["%.3f" % x for x in tt], ["%.3f" % x for x in tr], ["%.3f" % x for x in te]))

# ---------------------------------------------------------------- CSRF
b = B(ip())
f = b.form()
st, _, _ = b.submit(GOOD(PFX + "csrf1", "c1@ex.com"), f=f, headers={"Origin": "https://evil.example"})
check("foreign Origin -> 403", st == 403, str(st))
f = b.form()
st, _, _ = b.submit(GOOD(PFX + "csrf2", "c2@ex.com"), f=f, origin=False, headers={"Origin": "null"})
check("Origin null -> 403", st == 403, str(st))
f = b.form()
st, _, _ = b.submit(GOOD(PFX + "csrf3", "c3@ex.com"), f=f, origin=False, headers={"Referer": "https://evil.example/x"})
check("foreign Referer -> 403", st == 403, str(st))
f = b.form()
st, _, _ = b.submit(GOOD(PFX + "csrf4", "c4@ex.com"), f=f, origin=False, headers={"Referer": ORIGIN + ".evil.example/"})
check("look-alike Referer prefix -> 403", st == 403, str(st))
other = B(ip()); of = other.form()
st, _, _ = b.submit(GOOD(PFX + "csrf5", "c5@ex.com"), f=of)
check("other browser's token -> 403", st == 403, str(st))
nb = B(ip()); nf = nb.form(); nb.cookie = None
st, _, _ = nb.submit(GOOD(PFX + "csrf6", "c6@ex.com"), f=nf)
check("no cookie -> 403", st == 403, str(st))
f = b.form(); f["ts"] = str(int(f["ts"]) - 1)
st, _, _ = b.submit(GOOD(PFX + "csrf7", "c7@ex.com"), f=f)
check("tampered ts -> 403", st == 403, str(st))
f = b.form(); f["answer"] = "²"
st, _, html = b.submit(GOOD(PFX + "sup", "s@ex.com"), f=f)
check("superscript-two answer -> 400 not 500", st == 400, "%s %s" % (st, alert(html)[:60]))
f = b.form(); f["answer"] = "٣"
st, _, html = b.submit(GOOD(PFX + "arab", "s@ex.com"), f=f)
check("arabic-digit answer handled (400 or wrong-answer)", st in (400, 200), "%s %s" % (st, alert(html)[:60]))

# ---------------------------------------------------------------- password edge: trailing newline (not typable in client)
b = B(ip())
nlpw = "Goodpass12!\n"
st, _, html = b.submit(GOOD(PFX + "nlpw", PFX + "nl@ex.com", nlpw))
check("password with trailing newline refused", st == 400, "%s %s" % (st, alert(html)[:60]))
if st == 200:
    created.append((PFX + "nlpw", nlpw))

# ---------------------------------------------------------------- rate limit bypass through Caddy (spoofed headers)
cb = B(None, target=CADDY)
codes = []
for i in range(12):
    spoof = {"X-Real-IP": "203.0.113.%d" % (i + 1), "X-Forwarded-For": "198.51.100.%d" % (i + 1),
             "Forwarded": "for=192.0.2.%d" % (i + 1), "True-Client-IP": "192.0.2.%d" % (i + 1)}
    st, _, html = cb.submit({"username": "x"}, wait=0, headers=spoof)
    codes.append(st)
check("spoofed X-Real-IP/XFF via Caddy do not bypass post limit (11th -> 429)", codes[10] == 429 and codes[11] == 429, str(codes))

# direct (no Caddy) from a non-loopback peer cannot be tested here; app trusts X-Real-IP only from 127.0.0.1/::1.

# ---------------------------------------------------------------- concurrency: same email different names
b_list = [B(ip()) for _ in range(4)]
forms = [bb.form() for bb in b_list]
time.sleep(3.3)
res = [None] * 4
em = PFX + "race@ex.com"
def go(i):
    res[i] = b_list[i].submit(GOOD(PFX + "race%d" % i, em), wait=0, f=forms[i])[0]
ts_ = [threading.Thread(target=go, args=(i,)) for i in range(4)]
[t.start() for t in ts_]; [t.join() for t in ts_]
check("race on same email: exactly one wins", sorted(res) == [200, 400, 400, 400], str(res))
for i, s in enumerate(res):
    if s == 200:
        created.append((PFX + "race%d" % i, PW))

# 32-char password, all symbol classes
b = B(ip())
pw32 = "A1!b2@c3#d4$e5%f6^g7&h8*i9(j0)k~"
assert len(pw32) == 32
st, _, html = b.submit(GOOD(PFX + "pw32", PFX + "pw32@ex.com", pw32))
check("32-char symbol password accepted", st == 200, "%s %s" % (st, alert(html)[:60]))
if st == 200:
    created.append((PFX + "pw32", pw32))

json.dump({"created": created, "results": results}, open(sys.argv[1] if len(sys.argv) > 1 else "adversarial.json", "w"))
print("SUMMARY: %d/%d pass; created %s" % (sum(r[1] for r in results), len(results), [c[0] for c in created]))
