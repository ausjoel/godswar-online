#!/bin/bash
# Builds the Mac app shell: build/gwplay and the bundle "build/GodsWar Native.app" (no Xcode; clang + Cocoa).
# The bundle holds only the binary; the client folder and settings stay outside (see play.command).
cd "$(dirname "$0")"
mkdir -p build
[ -f third_party/lib/libunicorn-gw.a ] || { echo "run third_party/build-unicorn.sh first"; exit 1; }
nice -n 15 clang -O2 -g -Wall -Wno-unused-function -Wno-unused-variable -Wno-misleading-indentation -Wno-unused-but-set-variable -Wno-deprecated-declarations \
  -DGW_PATCHED_UC=1 -DGW_APP=1 -I/opt/homebrew/include -Ithird_party/unicorn-2.1.4/include -fobjc-arc \
  host/*.c host/mac.m third_party/lib/libunicorn-gw.a -lpthread -liconv -lz \
  -framework Cocoa -framework QuartzCore -framework AudioToolbox -framework CoreAudio -o build/gwplay || exit 1
APP="build/GodsWar Native.app"
mkdir -p "$APP/Contents/MacOS"
cp build/gwplay "$APP/Contents/MacOS/gwplay"
cat > "$APP/Contents/Info.plist" <<PL
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleName</key><string>GodsWar Native</string>
<key>CFBundleIdentifier</key><string>local.godswar.native</string>
<key>CFBundleExecutable</key><string>gwplay</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>0.1</string>
<key>LSMinimumSystemVersion</key><string>12.0</string>
<key>NSHighResolutionCapable</key><true/>
</dict></plist>
PL
codesign -s - --force "$APP" >/dev/null 2>&1   # ad-hoc, no hardened runtime (the emulator needs MAP_JIT)
echo "ok: build/gwplay and $APP"
