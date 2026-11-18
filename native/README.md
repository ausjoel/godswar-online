# GodsWar Native (macOS, Apple Silicon)

Runs the unmodified Windows game client (`GodsWar.exe`, 32-bit x86) on a Mac without Wine: the x86 code runs in a
patched [unicorn](https://www.unicorn-engine.org/) 2.1.4 CPU emulator, and the Windows APIs the client calls
(kernel32, user32, gdi32, Direct3D 9, DirectSound, Winsock and others) are reimplemented by the host in `host/`.
Direct3D 9 is drawn by a software rasteriser; the window is a Cocoa view.

This folder is the source of the published "GodsWar Native" Mac app. The game client files are not included; the app
copies them from the regular client download.

## Build

1. `third_party/`: download `unicorn-2.1.4.tar.gz` from the unicorn releases page, unpack it as
   `third_party/unicorn-2.1.4`, apply `unicorn-2.1.4-gwhost.patch` (`patch -p0 < unicorn-2.1.4-gwhost.patch` from
   `third_party/`), then run `build-unicorn.sh` (needs cmake and ninja). It writes `third_party/lib/libunicorn-gw.a`.
2. `./build.sh` builds the headless host `build/gwhost`; `./build-app.sh` builds the windowed app binary `gwplay`.
3. `./build-dist.sh` packages `GodsWar Native.app` around a client folder (edit `SRCAPP`, the server address and the
   update channel URL at the top for your own server).

Settings file: `~/Library/Application Support/GodsWar Native/settings.ini` (`server`, `port`, `width`, `height`,
`sound`); `play-settings.example.ini` shows the keys.

## Client patches applied in memory

`tools/uiscale_skip_patch.py` (skips a whole-UI rescale at scale 1.0 during loading) and `tools/cull_sse_patch.py`
(SSE frustum test) are applied by the host at load. `tools/rank_display_patch.py` is applied to the shipped exe: it lets
the server's `/rank` command lower the armour/weapon rank effect a player shows.

## Licence

The host code in this folder is distributed under the GNU General Public License version 2, because it links unicorn
(GPL-2). Our changes to unicorn are in `third_party/unicorn-2.1.4-gwhost.patch`.
