# GodsWar sign-up site

Players create an account (username, password, e-mail), and the site writes it to the server's
database. Small Python app (standard library + jinja2 + psycopg2) behind Caddy for HTTPS.

## Install (on the server)
```bash
sudo ./install.sh --live --origin https://signup.example.com
```
The top-level `setup.sh --vps` runs this for you. Needs a domain (or an `<ip>.sslip.io` name) pointing at
the server and ports 80/443 open. `install.sh --help` explains the options. It creates a least-privilege
database role and a systemd unit; `caddy/Caddyfile` is the HTTPS front (`GW_SIGNUP_DOMAIN` from the
environment, or filled in by setup.sh).

Close sign-ups at any time with `touch /etc/gwsignup/closed`.
Mail (address confirmation, password reset) uses Resend: set `GWSIGNUP_MAIL_FROM` in
`/etc/gwsignup/gwsignup.env`, then `ops/set-resend-key.sh` and `ops/test-mail.sh you@example.com`.
Without a key and sender, mail is off and addresses are stored unverified.

Tests: `test/` (run against a throwaway database, never a real one).
