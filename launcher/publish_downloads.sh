#!/bin/bash
# Builds the launcher, signs the manifest and publishes launcher + client downloads to the VPS.
# Usage: publish_downloads.sh <client version> <mac client zip> <windows client zip>
#   (pass "-" for a client zip to keep what is already published for that platform: not supported yet; pass both)
# Native engine (launcher 1.0.2+): NATIVE_ZIP=path NATIVE_VER=0.5 publishes a new GodsWar Native app zip; without them the
# native entry of the live manifest is carried over (signature checked), so a client release never drops it.
# Launcher version comes from `const Version` in launcher.go: bump it before publishing a new launcher.
# The signing key: GW_PATCH_KEY, default local/update-private.pem (tools/make-update-key.sh). Never commit it.
set -euo pipefail
cd "$(dirname "$0")"
CV="${1:?client version}" MAC="${2:?mac client zip}" WIN="${3:?windows client zip}"
KEY="${GW_PATCH_KEY:-../local/update-private.pem}"   # made by tools/make-update-key.sh
: "${GW_PUBLISH_HOST:?set GW_PUBLISH_HOST to the ssh host that serves /var/www/gwdownloads}"
STAGE="$(mktemp -d)"
./build.sh "$STAGE" >/dev/null
LV="$(sed -n 's/^const Version = "\(.*\)"/\1/p' launcher.go)"
cp -c "$MAC" "$STAGE/GodsWar-mac-$CV.zip"; cp -c "$WIN" "$STAGE/GodsWar-windows-$CV.zip"
ditto -c -k --sequesterRsrc --keepParent "$STAGE/GodsWar Launcher.app" "$STAGE/GodsWar-Launcher-mac.zip"
cp "$STAGE/gwlauncher-windows-$LV.exe" "$STAGE/GodsWar-Launcher-windows.exe"
NATIVE_ARGS=(); NATIVE_FILE=()
if [ -n "${NATIVE_ZIP:-}" ]; then
  NZ="GodsWar-Native-mac-${NATIVE_VER:?NATIVE_VER with NATIVE_ZIP}.zip"; cp -c "$NATIVE_ZIP" "$STAGE/$NZ"
  NATIVE_ARGS=(-mac-native "$NZ" -native-version "$NATIVE_VER"); NATIVE_FILE=("$NZ")
fi
go run ./tools/gwpublish -key "$KEY" -dir "$STAGE" -client-version "$CV" ${NATIVE_ARGS[@]+"${NATIVE_ARGS[@]}"} \
  -mac-client "GodsWar-mac-$CV.zip" -windows-client "GodsWar-windows-$CV.zip" \
  -launcher-version "$LV" -mac-launcher "gwlauncher-mac-$LV" -windows-launcher "gwlauncher-windows-$LV.exe"
cd "$STAGE"
rsync --partial --inplace --timeout=60 -e ssh GodsWar-mac-$CV.zip GodsWar-windows-$CV.zip gwlauncher-mac-$LV gwlauncher-windows-$LV.exe \
  GodsWar-Launcher-mac.zip GodsWar-Launcher-windows.exe ${NATIVE_FILE[@]+"${NATIVE_FILE[@]}"} "$GW_PUBLISH_HOST:/var/www/gwdownloads/files/"
scp -q manifest.json manifest.json.sig "$GW_PUBLISH_HOST:"
ssh "$GW_PUBLISH_HOST" 'sudo -n chmod 644 /var/www/gwdownloads/files/* && sudo -n install -m644 ~/manifest.json ~/manifest.json.sig /var/www/gwdownloads/ && rm ~/manifest.json ~/manifest.json.sig'   # manifest last
echo "published client $CV, launcher $LV"
