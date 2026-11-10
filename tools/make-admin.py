#!/usr/bin/env python3
"""Print the SQL that creates (or resets) the administrator account.

    python3 -I tools/make-admin.py USERNAME PASSWORD
    printf '%s' "$PW" | python3 -I tools/make-admin.py USERNAME -     (password from stdin: not visible in ps)

The game client sends md5(password); the server stores PBKDF2-HMAC-SHA256 over that
(server/src/.../PasswordVerifierRecord.cs). This builds the same verifier so the account
works for in-game login. Setup pipes the output into psql; the password is never stored
anywhere but the verifier.
"""
import base64, hashlib, re, secrets, sys

def verifier(password, iterations=600000):
    md5hex = hashlib.md5(password.encode("ascii")).hexdigest().encode("ascii")
    salt = secrets.token_bytes(16)
    dk = hashlib.pbkdf2_hmac("sha256", md5hex, salt, iterations, 32)
    return "gws$pbkdf2-sha256$v1$%d$%s$%s" % (iterations, base64.b64encode(salt).decode(), base64.b64encode(dk).decode())

if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[2] == "-":
        sys.argv[2] = sys.stdin.read().strip()
    if len(sys.argv) != 3 or not re.fullmatch(r"[A-Za-z0-9_]{3,16}", sys.argv[1]) or not re.fullmatch(r"[\x21-\x7e]{6,32}", sys.argv[2]):
        sys.exit("usage: make-admin.py USERNAME PASSWORD  (username 3-16 letters/digits/_, password 6-32 printable ASCII)")
    u, v = sys.argv[1], verifier(sys.argv[2])
    print(f"INSERT INTO accounts (username, password, status) VALUES ('{u}', '{v}', 0) "
          f"ON CONFLICT (username) DO UPDATE SET password = EXCLUDED.password;")
