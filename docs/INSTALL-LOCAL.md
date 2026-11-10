# Run GodsWar on your own computer

Good for testing and for playing with friends on your home network.

## Linux (Debian / Ubuntu)
```bash
sudo ./setup.sh --local
```
Same as the VPS install (see [INSTALL-VPS.md](INSTALL-VPS.md)) but the address defaults to `127.0.0.1`,
there is no Caddy/HTTPS, and the sign-up site (if you choose it) is at http://127.0.0.1:8090.
Give your LAN IP as the address to let other computers on your network join.

## macOS (Homebrew)
```bash
./setup.sh --local          # as yourself, not sudo
```
It installs PostgreSQL 17 with Homebrew (`brew services`), the .NET SDK into `local/dotnet` if you have
none, builds the server into `local/server`, and keeps passwords in `local/secrets` (mode 600, ignored by
git). Control it with `local/godswar start|stop|restart|status|logs`. The sign-up site and editor services
need Linux/systemd and are not installed on macOS.

## Docker (any OS)
```bash
./setup.sh --docker
```
Needs Docker with the compose plugin (Docker Desktop, Colima, or Docker Engine). Settings go in
`config/godswar.env` (made from the example, random passwords). Everyday commands:
```bash
docker compose --env-file config/godswar.env logs -f server   # watch the log
docker compose --env-file config/godswar.env stop             # stop
docker compose --env-file config/godswar.env up -d            # start again
```
Data lives in the Docker volume `godswar_godswar-postgres-data`; removing it deletes every character.
