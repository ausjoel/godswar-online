# GodsWar launcher (Go)

One small program per platform: downloads the client zip from the VPS, checks it, installs it, starts the game, and updates itself.

- Mac: `GodsWar Launcher.app` (universal, ad-hoc signed). Unpacks with `ditto` (keeps symlinks), starts the game with `open`.
- Windows: `gwlauncher-windows-VER.exe` (32-bit console exe). Never run on Windows yet.
- Data: Mac `~/Library/Application Support/GodsWar Launcher`, Windows `%LOCALAPPDATA%\GodsWar` (`state.json`, `client/`, `launcher/`).
- Trust: `PublicKey` in `launcher.go` (same Ed25519 key as gwupdate; private key `~/GodsWar-Backups/patch-signing/private.pem`, back it up). `manifest.json` + `manifest.json.sig` at `/downloads/` on the VPS; refuses a bad signature or a lower `serial`.
- Self-update: a newer launcher is verified and saved in the data folder only; the next start hands over to it (`GW_LAUNCHER_DELEGATED=1` stops loops). The running file is never overwritten.
- Windows has no patcher yet: a client update replaces the whole zip (about 550 MB). Mac gwupdate patches still apply inside the Mac app.
- Test: `go test ./...` (9 tests, local HTTP server, no screen). Real run without launching: `HOME=/tmp/x ./gwlauncher-mac-1.0.0 --no-launch`.
- Publish: `publish_downloads.sh <client version> <mac zip> <windows zip>`.
- VPS: Caddy `/downloads/*` serves `/var/www/gwdownloads` (manifest, `files/`).
