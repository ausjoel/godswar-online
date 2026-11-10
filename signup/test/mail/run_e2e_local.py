#!/usr/bin/env python3
"""Run the existing HTTP test test/e2e_signup.py against the app on THIS machine, with the
in-memory FakeStore from test_mail_unit.py instead of PostgreSQL and a fake Resend sender.
A regression check for the sign-up page itself, with mail on and with mail off.

    PYTHONPATH=<dir with jinja2> python3 test/mail/run_e2e_local.py on|off
"""
import os
import subprocess
import sys
import threading
from wsgiref.simple_server import make_server

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import test_mail_unit as t  # noqa: E402  (also stubs psycopg2 if needed)
import gwsignup  # noqa: E402

mode = sys.argv[1] if len(sys.argv) > 1 else "on"
PORT = 18091
origin = "http://127.0.0.1:%d" % PORT
app, box = t.make_app(key=t.KEY if mode == "on" else "", ORIGIN=origin + ",http://localhost:%d" % PORT)
app.store.create("curlhero1", "curl@example.test", "v", "h")   # e2e_signup.py expects this account to exist
app.cfg.form_min_age = 3          # e2e_signup.py checks the too-fast refusal
srv = make_server("127.0.0.1", PORT, app, server_class=gwsignup.ThreadingWSGIServer, handler_class=gwsignup.QuietHandler)
threading.Thread(target=srv.serve_forever, daemon=True).start()
r = subprocess.run([sys.executable, "-B", os.path.join(HERE, "..", "e2e_signup.py"), origin])
app.mailer.q.join()
srv.shutdown()
made = [a["username"] for a in app.store.accounts.values()]   # includes the pre-made curlhero1
print("mail=%s accounts made: %s; mails sent: %d" % (mode, made, len(box.mails)))
if mode == "on" and len(box.mails) != len(made) - 1:
    print("FAIL: expected one confirmation mail per account made")
    sys.exit(1)
if mode == "off" and box.mails:
    print("FAIL: mail is off but mails were sent")
    sys.exit(1)
sys.exit(r.returncode)
