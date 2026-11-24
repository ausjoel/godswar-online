#!/bin/bash
# Builds the launcher, signs the manifest and publishes launcher + client downloads to the VPS.
# Usage: publish_downloads.sh <client version> <mac client zip> <windows client zip>
#   (pass "-" for a client zip to keep what is already published for that platform: not supported yet; pass both)
# Native engine (launcher 1.0.2+): NATIVE_ZIP=path NATIVE_VER=0.5 publishes a new GodsWar Native app zip; without them the
# native entry of the live manifest is carried over (signature checked), so a client release never drops it.
# CLIENT_REL=N (the clients' Client/.client-release, e.g. 16 for 1.5.6) goes into the manifest so launcher 1.0.3+ can patch
# Windows in place through the update channel; without it Windows players get the full zip (a warning is printed).
# WIN_LAUNCHER_EXE=path: publish that tested Windows exe instead of the fresh build.
# News (launcher 1.0.4+ "What's new" box): the 3 newest posts of the player site, read from COMMITTED godswar-dev
# origin/main signup/templates/_news.html (GODSWAR_DEV=path overrides the repo, NEWS_SRC=file a template file, NEWS=0 = none).
# News reaches launchers only when the manifest is re-signed, i.e. by this script.
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
# WIN_LAUNCHER_EXE=path publishes that exact Windows exe (the one tested in the VM) instead of this fresh build;
# it must be the same version (a rebuild differs only in the embedded git stamp).
if [ -n "${WIN_LAUNCHER_EXE:-}" ]; then
  go version -m "$WIN_LAUNCHER_EXE" | grep -q 'path[[:space:]]*gwlauncher' || { echo "$WIN_LAUNCHER_EXE is not a gwlauncher build" >&2; exit 1; }
  cp "$WIN_LAUNCHER_EXE" "$STAGE/gwlauncher-windows-$LV.exe"
fi
cp -c "$MAC" "$STAGE/GodsWar-mac-$CV.zip"; cp -c "$WIN" "$STAGE/GodsWar-windows-$CV.zip"
ditto -c -k --sequesterRsrc --keepParent "$STAGE/GodsWar Launcher.app" "$STAGE/GodsWar-Launcher-mac.zip"
cp "$STAGE/gwlauncher-windows-$LV.exe" "$STAGE/GodsWar-Launcher-windows.exe"
NATIVE_ARGS=(); NATIVE_FILE=()
if [ -n "${NATIVE_ZIP:-}" ]; then
  NZ="GodsWar-Native-mac-${NATIVE_VER:?NATIVE_VER with NATIVE_ZIP}.zip"; cp -c "$NATIVE_ZIP" "$STAGE/$NZ"
  NATIVE_ARGS=(-mac-native "$NZ" -native-version "$NATIVE_VER"); NATIVE_FILE=("$NZ")
fi
[ -n "${CLIENT_REL:-}" ] || echo "warning: CLIENT_REL not set; Windows players will download the full client zip" >&2
NEWS_ARGS=()
if [ "${NEWS:-1}" != 0 ]; then
  SRC="$STAGE/_news.html"
  if [ -n "${NEWS_SRC:-}" ]; then cp "$NEWS_SRC" "$SRC"
  else git -C "${GODSWAR_DEV:-/path/to/GodsWar" show origin/main:signup/templates/_news.html > "$SRC" || : > "$SRC"; fi
  python3 -I tools/news_from_site.py "$SRC" > "$STAGE/news.json" && NEWS_ARGS=(-news "$STAGE/news.json")
fi
go run ./tools/gwpublish -key "$KEY" -dir "$STAGE" -client-version "$CV" -client-release "${CLIENT_REL:-0}" ${NEWS_ARGS[@]+"${NEWS_ARGS[@]}"} ${NATIVE_ARGS[@]+"${NATIVE_ARGS[@]}"} \
  -mac-client "GodsWar-mac-$CV.zip" -windows-client "GodsWar-windows-$CV.zip" \
  -launcher-version "$LV" -mac-launcher "gwlauncher-mac-$LV" -windows-launcher "gwlauncher-windows-$LV.exe"
cd "$STAGE"
rsync --partial --inplace --timeout=60 -e ssh GodsWar-mac-$CV.zip GodsWar-windows-$CV.zip gwlauncher-mac-$LV gwlauncher-windows-$LV.exe \
  GodsWar-Launcher-mac.zip GodsWar-Launcher-windows.exe ${NATIVE_FILE[@]+"${NATIVE_FILE[@]}"} "$GW_PUBLISH_HOST:/var/www/gwdownloads/files/"
scp -q manifest.json manifest.json.sig "$GW_PUBLISH_HOST:"
ssh "$GW_PUBLISH_HOST" 'sudo -n chmod 644 /var/www/gwdownloads/files/* && sudo -n install -m644 ~/manifest.json ~/manifest.json.sig /var/www/gwdownloads/ && rm ~/manifest.json ~/manifest.json.sig'   # manifest last
echo "published client $CV, launcher $LV"
