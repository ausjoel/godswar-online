#!/bin/bash
# Builds the GodsWar launcher: Mac app (universal) and Windows exe (32-bit).
# Usage: build.sh OUTDIR     -> OUTDIR/GodsWar Launcher.app, gwlauncher-mac-VER, gwlauncher-windows-VER.exe
set -euo pipefail
cd "$(dirname "$0")"
OUT="$(cd "${1:?usage: build.sh OUTDIR}" 2>/dev/null && pwd || (mkdir -p "$1" && cd "$1" && pwd))"
VER="$(sed -n 's/^const Version = "\(.*\)"/\1/p' launcher.go)"
: "${GW_MANIFEST_URL:?set GW_MANIFEST_URL, e.g. https://your.host/downloads/manifest.json}"
: "${GW_UPDATE_PUBKEY:?set GW_UPDATE_PUBKEY, the base64 public key printed by tools/make-update-key.sh}"
LDF="-s -w -X main.PublicKey=$GW_UPDATE_PUBKEY -X main.DefaultManifestURL=$GW_MANIFEST_URL"
go test ./... >/dev/null
GOOS=darwin GOARCH=arm64 CGO_ENABLED=0 go build -trimpath -ldflags "$LDF" -o "$OUT/.arm64" .
GOOS=darwin GOARCH=amd64 CGO_ENABLED=0 go build -trimpath -ldflags "$LDF" -o "$OUT/.amd64" .
lipo -create "$OUT/.arm64" "$OUT/.amd64" -output "$OUT/gwlauncher-mac-$VER"
rm "$OUT/.arm64" "$OUT/.amd64"; chmod 755 "$OUT/gwlauncher-mac-$VER"
GOOS=windows GOARCH=386 CGO_ENABLED=0 go build -trimpath -ldflags "$LDF" -o "$OUT/gwlauncher-windows-$VER.exe" .
APP="$OUT/GodsWar Launcher.app"
rm -rf "$APP"; mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$OUT/gwlauncher-mac-$VER" "$APP/Contents/MacOS/GodsWarLauncher"
ICON="$(cd ../.. && pwd)/GodsWar-App-Build/GodsWar.icns"   # was inside the trashed "GodsWar (players' build).app"
[ -f "$ICON" ] && cp "$ICON" "$APP/Contents/Resources/GodsWar.icns"
cat > "$APP/Contents/Info.plist" <<PL
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleName</key><string>GodsWar</string>
<key>CFBundleDisplayName</key><string>GodsWar</string>
<key>CFBundleExecutable</key><string>GodsWarLauncher</string>
<key>CFBundleIdentifier</key><string>org.preservation.godswar.launcher</string>
<key>CFBundleIconFile</key><string>GodsWar</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>$VER</string>
<key>CFBundleVersion</key><string>$VER</string>
<key>LSMinimumSystemVersion</key><string>11.0</string>
<key>LSUIElement</key><true/>
</dict></plist>
PL
codesign --force --sign - "$APP"; codesign --verify --strict "$APP"
echo "built $VER in $OUT"
