#!/bin/bash
# build-dist.sh : player package of the native client (NOT published by this script).
#   dist/<ver>/GodsWar Native.app   Contents/MacOS/{GodsWar Native (launcher), gwplay, gwupdate}, Resources/Client (the published
#                             client of the Wine app's newest release: client-1.5.2 build), Resources/app-version
#   dist/<ver>/GodsWar-Native-mac-<ver>.zip (+ SHA256SUMS.txt)
# Launcher: first run (or new app version) clones Resources/Client to ~/Library/Application Support/GodsWar Native/Client,
# writes settings.ini there (LIVE server default), runs gwupdate on that copy (same signed channel as the Wine app,
# GW_AUTO_UPDATE=0 skips it), then execs gwplay. Ad-hoc signed (no hardened runtime: the emulator needs MAP_JIT).
set -euo pipefail
cd "$(dirname "$0")"
VER=0.6.1; STAMP=$(date +%Y%m%d%H%M)
SRCAPP=/path/to/GodsWar
[ -x build/gwplay ] || ./build-app.sh
OUT="dist/$VER"; rm -rf "$OUT"; mkdir -p "$OUT"; APP="$OUT/GodsWar Native.app"; M="$APP/Contents/MacOS"; R="$APP/Contents/Resources"
mkdir -p "$M" "$R"
cp build/gwplay "$M/gwplay"; cp "$SRCAPP/MacOS/gwupdate" "$M/gwupdate"
cp -Rc "$SRCAPP/Resources/Client" "$R/Client"; find "$R/Client" -name .DS_Store -delete
echo "$VER.$STAMP" > "$R/app-version"
cat > "$M/GodsWar Native" <<'SH'
#!/bin/bash
# GodsWar Native launcher (exe-port). Data: ~/Library/Application Support/GodsWar Native (Client copy, settings.ini, run/, updates/).
MACOS="$(cd "$(dirname "$0")" && pwd)"; RES="$MACOS/../Resources"; SUP="$HOME/Library/Application Support/GodsWar Native"
GAME="$SUP/Client"; SET="$SUP/settings.ini"; LOG="$SUP/launcher.log"; mkdir -p "$SUP"
exec >>"$LOG" 2>&1; echo "== $(date '+%F %T') launch $(cat "$RES/app-version")"
VER="$(cat "$RES/app-version")"
if [ ! -f "$GAME/GodsWar.exe" ] || [ "$(cat "$GAME/.app-version" 2>/dev/null)" != "$VER" ]; then
  echo "installing client copy"; rm -rf "$GAME.new"
  /bin/cp -Rc "$RES/Client" "$GAME.new" 2>/dev/null || /bin/cp -R "$RES/Client" "$GAME.new" || { echo "client copy failed"; exit 1; }
  echo "$VER" > "$GAME.new/.app-version"; rm -rf "$GAME"; mv "$GAME.new" "$GAME"
fi
if [ ! -f "$SET" ]; then
  printf '# GodsWar Native settings (edit with the game closed)\nclient=%s\nserver=YOUR_SERVER_IP\nport=6999\nsound=1\navatar=2\nwidth=1024\nheight=768\n' "$GAME" > "$SET"; chmod 600 "$SET"
fi
if [ "${GW_AUTO_UPDATE:-1}" = 1 ]; then
  "$MACOS/gwupdate" --client "$GAME" --work "$SUP/updates" --channel-url "https://YOUR-DOMAIN.example/updates/stable/" \
    --public-key "Ck1cz0ik0Gt7PAfXeSLsmOB8ZxLPup7aw6AgwEi+E4o=" & U=$!
  for i in $(seq 1 120); do kill -0 $U 2>/dev/null || break; sleep 0.5; done
  kill $U 2>/dev/null && echo "gwupdate timed out; starting with the files we have" || wait $U; echo "gwupdate exit $?"
fi
exec "$MACOS/gwplay" --settings "$SET" "$@"
SH
chmod +x "$M/GodsWar Native"
cat > "$APP/Contents/Info.plist" <<PL
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleName</key><string>GodsWar Native</string>
<key>CFBundleDisplayName</key><string>GodsWar Native</string>
<key>CFBundleIdentifier</key><string>local.godswar.native</string>
<key>CFBundleExecutable</key><string>GodsWar Native</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>$VER</string>
<key>CFBundleVersion</key><string>$VER.$STAMP</string>
<key>LSMinimumSystemVersion</key><string>12.0</string>
<key>NSHighResolutionCapable</key><true/>
</dict></plist>
PL
codesign --force --deep -s - "$APP" >/dev/null 2>&1; codesign --verify --deep --strict "$APP" && echo "signature ok"
(cd "$OUT" && ditto -c -k --sequesterRsrc --keepParent "GodsWar Native.app" "GodsWar-Native-mac-$VER.zip" && shasum -a 256 GodsWar-Native-mac-$VER.zip > SHA256SUMS.txt && cat SHA256SUMS.txt)
du -sh "$APP" "$OUT"/*.zip
