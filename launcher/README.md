# GodsWar launcher (Go)

One small program per platform: downloads the client zip from the VPS, checks it, installs it, starts the game, and updates itself.

- Mac: `GodsWar Launcher.app` (universal, ad-hoc signed). Unpacks with `ditto` (keeps symlinks), starts the game with `open`.
- Windows: `gwlauncher-windows-VER.exe` (32-bit console exe). Tested in the Parallels Windows 11 VM (2026-11-22).
- Data: Mac `~/Library/Application Support/GodsWar Launcher`, Windows `%LOCALAPPDATA%\GodsWar` (`state.json`, `client/`, `launcher/`).
- Trust: `PublicKey` in `launcher.go` (same Ed25519 key as gwupdate; private key `~/GodsWar-Backups/patch-signing/private.pem`, back it up). `manifest.json` + `manifest.json.sig` at `/downloads/` on the VPS; refuses a bad signature or a lower `serial`.
- Self-update: a newer launcher is verified and saved in the data folder only; the next start hands over to it (`GW_LAUNCHER_DELEGATED=1` stops loops). The running file is never overwritten.
- Windows in-place patching (1.0.3+, `patch.go`, console d118): when the signed downloads manifest's Windows client is newer
  and carries `release` (gwpublish `-client-release`, `CLIENT_REL=N publish_downloads.sh`), the launcher applies the signed
  update-channel releases (`updates/stable/N/`) from the client's `.client-release` up to that release, with gwupdate's rules
  (signature, base_version chain, schema-2 `before` hashes, only listed zip names, sha256 per file, stage all before the first
  change). `UIScale/` and `.client-release` entries are skipped on Windows (the Windows zip never ships them; the launcher writes
  `.client-release` itself after every full install or patch). Any problem (no release number, broken chain, edited local file,
  game running) falls back to the full zip. Without `CLIENT_REL` in a publish, Windows players get the full zip.
  End-to-end check: `GW_E2E_OLD=<1.5.5 build/win> GW_E2E_NEW=<1.5.6 build/win> go test -run E2E .` patches a 1.5.5 copy with the
  live release 16 and requires it to equal 1.5.6. Mac gwupdate patches still apply inside the Mac app.
- Launcher window (1.0.5+, `gui*.go`, the admin 24 Nov): a normal start opens a window in the style of the original GodsWar launcher
  (its art is in `assets/igg/`, IGG's; see `assets/README.md`). Event Summary = the manifest's news (click a title to show it,
  "Read the full post" opens the site), Servers = login port 6999 up or down plus engine, character models and versions, a
  progress bar for downloads, patches and unpacking, Update (re-checks and installs the launcher and the game; a newer launcher
  restarts itself), Settings (character models Original/New; on a Mac also Wine or Native) and START (closes the window once the
  game starts). Models go where each game reads them: Windows config.ini [SYSTEM] Avatar, the Mac Wine app's settings.conf
  GW_AVATAR (or `open --env` when it has none), Native's settings.ini `avatar`. Everything is drawn in Go (`gui_draw.go`); the
  window shells are `gui_window_darwin.go/.m` (AppKit, cgo: the Mac build now needs cgo, see build.sh) and
  `gui_window_windows.go` (layered Win32 window, no cgo; the exe is built with -H windowsgui, log in
  %LOCALAPPDATA%\GodsWar\launcher.log). `--no-launch`, `--engine`, `--show-news`, `--no-gui`, `GW_NO_GUI=1` or no window keep the
  1.0.4 dialogs. Testing: `--ui-snapshot DIR` writes PNGs of the window's states; `GW_UI_DUMP=DIR` writes the frame on screen,
  `GW_UI_TRACE=1` logs clicks to launcher.log, `GW_UI_STACKS=1` writes goroutine stacks to stacks.txt every 5 s. Frames are drawn
  by one loop (`drawLoop`), so downloads never wait for drawing (drawing on the download thread made a 10 MB launcher download take a
  minute under the VM's x86 emulation). Settings only write the models after the player picks them (`state.models`); until then
  every game keeps its own config. A downloaded newer launcher restarts the window (Mac: same process, so the Dock keeps GodsWar).
  Without `assets/igg` (public export) the window draws a plain background.
- What's new box (1.0.4+, `news.go`, review #12): the signed manifest's optional `news` list (at most 3 items: id, date,
  title, url, summary) is shown once before the game opens. Mac: dialog with Play / Read more / Don't show again (gives up
  after 60 s = Play). Windows: message box, Yes = read the full post, No = don't show again, Cancel or close = play.
  "Don't show again" ids go to `news_hidden` in state.json; a first install marks the current items quietly. Links must be
  https://YOUR-DOMAIN.example. `publish_downloads.sh` fills the list from committed godswar-dev origin/main
  `signup/templates/_news.html` (`tools/news_from_site.py`; `NEWS=0` = none), so news reaches players only when the manifest is
  re-signed. `--show-news` shows the box with `--no-launch` (testing). Never shown after launch or in game.
- VC++ 2005 runtime (1.0.3+, `runtime_windows.go`, console d117): GodsWar.exe's FB.dll/KMMonitor.dll need the VC80 CRT 8.0.50727.762+.
  The exe embeds Microsoft's "VC++ 2005 SP1 Redistributable MFC Security Update" x86 (`vcredist/vcredist_x86.exe`, not in git;
  `build.sh` fetches it from download.microsoft.com and checks the pinned sha256 8648c5fc...adfd; Authenticode: Microsoft
  Corporation / Microsoft Code Signing PCA 2011, checked in the VM). If WinSxS has no x86 vc80.crt >= 762 with msvcp80.dll, it runs
  it elevated (one UAC prompt) with `/q:a /c:"msiexec /i vcredist.msi /qn /norestart"`; a declined prompt prints the manual
  install hint and the game still starts. `--force-runtime` runs it anyway (testing).
- Testing flags: `--manifest-url`, `--channel-url`, `--allow-file-urls` (file:// manifest/channel/zips, still checked against
  the compiled-in key), `--no-patch`, `--no-launch`.
- Test: `go test ./...` (9 tests, local HTTP server, no screen). Real run without launching: `HOME=/tmp/x ./gwlauncher-mac-1.0.0 --no-launch`.
- Publish: `publish_downloads.sh <client version> <mac zip> <windows zip>`.
- VPS: Caddy `/downloads/*` serves `/var/www/gwdownloads` (manifest, `files/`).
