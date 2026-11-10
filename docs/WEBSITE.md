# Sign-up website and editor (optional)

`./setup.sh --vps` installs the sign-up website for you (answer yes, or pass `--signup`). The notes
below are for doing it by hand or changing it later.

## Sign-up website (`signup/`)
Players create accounts (and, with mail, confirm their address and reset passwords).
`sudo signup/install.sh --live --origin https://your.domain --no-caddy` installs it on 127.0.0.1:8090 with
its own least-privilege database role (read `signup/README.md`). For HTTPS, use `signup/caddy/Caddyfile`
with `GW_SIGNUP_DOMAIN` set in Caddy's environment (setup.sh writes it with your domain filled in).
Mail: Resend (`GWSIGNUP_MAIL_FROM` in `/etc/gwsignup/gwsignup.env`, key via `signup/ops/set-resend-key.sh`).

## Editor (`editor/`)
A web tool for characters, items, rates and accounts. It is for **administrators only**. `setup.sh
--editor` runs it on 127.0.0.1:8787 (service `godswar-editor`); reach it through an SSH tunnel:
`ssh -L 8787:127.0.0.1:8787 you@your.server`, then open http://127.0.0.1:8787. Never expose it publicly
without HTTPS and authentication. Read `editor/README.md`.
