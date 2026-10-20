#!/bin/bash
# Build TER.app + TER-<version>-osx-arm64.dmg from the dotnet publish output.
# Cores come from OFFICIAL releases (XTLS/Xray-core, SagerNet/sing-box), already
# downloaded + checksum-verified into ./cores (see BUILD.md), not from the
# third-party 2dust/v2rayN-core-bin bundle the stock package-osx.sh uses.
# Geo files: official runetfreedom russia-v2ray-rules-dat release (cores/rf, sha256
# verified against the release's .sha256sum), because TER's default RU routing uses
# geosite:ru-available-only-inside / geoip:ru-blocked which the Xray zip's files lack.
# DMG: dmgbuild (local venv .tools/venv) for the usual "drag to Applications"
# window; falls back to a plain hdiutil image if it is missing.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
FILE_VERSION="$(tr -d ' \n' < "$HERE/VERSION")"
VERSION="${1:-$FILE_VERSION}"
[ "$VERSION" = "$FILE_VERSION" ] || { echo "version $VERSION != VERSION file ($FILE_VERSION): bump VERSION, re-run apply-changes.sh and publish"; exit 1; }
PUB="$HERE/upstream/v2rayN/bin/termitus/osx-arm64"
# 14.10: always publish fresh (a stale publish dir once shipped old code under a new version, LESSONS 29)
rm -rf "$PUB"
# LESSONS 45: no build-machine path (Mac user name) inside the binaries / client logs (CallerFilePath).
UP_REAL="$(cd "$HERE/upstream/v2rayN" && pwd -P)"
PATHMAP="$UP_REAL=/src"
[ "$UP_REAL" = "$HERE/upstream/v2rayN" ] || PATHMAP="$PATHMAP%2C$HERE/upstream/v2rayN=/src"
# 0.6.5 D-1: pin the bundled cores INSIDE the binary. The privileged macOS helper accepts a core only if its sha256 equals the one
# compiled in here (a hash computed at run time from the user-writable data folder would prove nothing).
XRAY_BIN="$HERE/cores/x/xray"; SB_BIN="$(ls "$HERE"/cores/s/*/sing-box 2>/dev/null | head -1)"
[ -f "$XRAY_BIN" ] && [ -f "$SB_BIN" ] || { echo "cores missing in $HERE/cores (cannot pin their checksums)"; exit 1; }
# The bundle is ad-hoc signed below, which rewrites the cores' Mach-O signature: pin the hash of the SIGNED core (same file name = same
# identifier, ad-hoc signing is deterministic). A gate after signing checks that the real .app matches.
PIN_TMP="$(mktemp -d)"; cp "$XRAY_BIN" "$PIN_TMP/xray"; cp "$SB_BIN" "$PIN_TMP/sing-box"
codesign --force -s - "$PIN_TMP/xray" "$PIN_TMP/sing-box" 2>/dev/null || { echo "codesign of the pinned cores failed"; exit 1; }
CORE_HASHES="xray=$(shasum -a 256 "$PIN_TMP/xray" | cut -d' ' -f1);sing-box=$(shasum -a 256 "$PIN_TMP/sing-box" | cut -d' ' -f1)"
rm -rf "$PIN_TMP"
HASH_SRC="$HERE/upstream/v2rayN/ServiceLib/Services/TermitusCoreHashes.cs"
[ -f "$HASH_SRC" ] || { echo "TermitusCoreHashes.cs missing: run apply-changes.sh first"; exit 1; }
sed -i '' -E "s|(private const string Raw = \")[^\"]*(\";)|\1$CORE_HASHES\2|" "$HASH_SRC"
grep -q "xray=[0-9a-f]\{64\};sing-box=" "$HASH_SRC" || { echo "failed to embed core hashes"; exit 1; }
(cd "$HERE/upstream/v2rayN" && DOTNET_ROOT=$HOME/.dotnet "$HOME/.dotnet/dotnet" publish v2rayN.Desktop/v2rayN.Desktop.csproj \
  -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true \
  -p:Version="$VERSION" "-p:PathMap=$PATHMAP" -p:Deterministic=true -o "$PUB" -nologo -v q) || { echo "dotnet publish failed"; exit 1; }
CORES="$HERE/cores"
DIST="$HERE/dist"
APP="$DIST/TER.app"

# ---- 0.6 release gates (BUILD.md / GPL-COMPLIANCE.md / RUNBOOK «Выпуск обновления TER») ----
# 1) The binary must carry the update public key, otherwise the built-in updater is OFF and users of this
#    build can never be updated in place. The check reads what was actually compiled in (the patched upstream copy).
UPD_SRC="$HERE/upstream/v2rayN/ServiceLib/Services/TermitusUpdate.cs"
if grep -q 'PublicKeyBase64 = ""' "$UPD_SRC" 2>/dev/null; then
  [ "${ALLOW_NO_UPDATE_KEY:-}" = 1 ] || { echo "REFUSING: no update public key is embedded (updater would be dead). Run: python3 termitus/tools/sign_release.py keygen --patch-app termitus/overlay/ServiceLib/Services/TermitusUpdate.cs ; bash apply-changes.sh ./upstream ; rebuild. (test builds: ALLOW_NO_UPDATE_KEY=1)"; exit 1; }
  echo "WARNING: building WITHOUT an update key (ALLOW_NO_UPDATE_KEY=1) -- this build must not be distributed"
fi
# 2) GPL-3.0: every binary must point to the public source. termitus/SOURCE_URL is shown in the app (About).
SOURCE_URL="$(tr -d ' \n' < "$HERE/termitus/SOURCE_URL" 2>/dev/null || true)"
case "$SOURCE_URL" in https://*) ;; *) echo "termitus/SOURCE_URL must hold the https URL of the public source repository"; exit 1;; esac
if echo "$SOURCE_URL" | grep -q 'REPLACE-ME'; then
  [ "${ALLOW_PLACEHOLDER_SOURCE:-}" = 1 ] || { echo "REFUSING: termitus/SOURCE_URL is still the placeholder. Publish the source (GPL-COMPLIANCE.md), put its URL there. (test builds: ALLOW_PLACEHOLDER_SOURCE=1)"; exit 1; }
  echo "WARNING: building with the PLACEHOLDER source URL -- this build must not be distributed"
fi

[ -x "$PUB/v2rayN" ] || { echo "no publish output in $PUB (run dotnet publish first)"; exit 1; }
[ -x "$CORES/x/xray" ] && [ -x "$CORES"/s/*/sing-box ] || { echo "cores missing in $CORES"; exit 1; }
RF="$CORES/rf"
[ -f "$RF/geoip.dat" ] && [ -f "$RF/geosite.dat" ] && [ -f "$RF/RF_RELEASE" ] || { echo "runetfreedom geo files missing in $RF (see BUILD.md)"; exit 1; }
(cd "$RF" && shasum -a 256 -c geoip.dat.sha256sum && shasum -a 256 -c geosite.dat.sha256sum) || { echo "geo checksum mismatch"; exit 1; }

rm -rf "$APP" && mkdir -p "$APP/Contents/Resources"
cp -R "$PUB" "$APP/Contents/MacOS"
# 0.5: the process is called TER (Activity Monitor, Force Quit), not v2rayN. The .NET apphost
# finds v2rayN.dll by its embedded name, so renaming the launcher file is safe.
mv "$APP/Contents/MacOS/v2rayN" "$APP/Contents/MacOS/TER"
mkdir -p "$APP/Contents/MacOS/bin/xray" "$APP/Contents/MacOS/bin/sing_box"
cp "$CORES/x/xray" "$APP/Contents/MacOS/bin/xray/xray"
cp "$CORES"/s/*/sing-box "$APP/Contents/MacOS/bin/sing_box/sing-box"
cp "$RF/geoip.dat" "$RF/geosite.dat" "$APP/Contents/MacOS/bin/"
# Marker read by TermitusRussiaPreset.SyncBundledGeoFiles (overwrites old copies on upgrade).
cp "$RF/RF_RELEASE" "$APP/Contents/MacOS/bin/ter-geo.version"
cp "$APP/Contents/MacOS/v2rayN.icns" "$APP/Contents/Resources/AppIcon.icns"
# 0.6 GPL: where the source is (About -> «Исходный код») and the license texts (About -> «Лицензии открытого ПО»).
printf '%s\n' "$SOURCE_URL" > "$APP/Contents/MacOS/ter-source-url.txt"
{
  echo "TER $VERSION — open-source notices"
  echo
  echo "TER is free software under the GNU General Public License v3.0, a fork of v2rayN (c) 2dust."
  echo "Corresponding source code of this exact release (tag v$VERSION): $SOURCE_URL"
  echo
  echo "Bundled components (unmodified binaries, source available at the given release tags):"
  echo "  - Xray-core $(cat "$CORES/XRAY_VERSION") — MPL-2.0 — https://github.com/XTLS/Xray-core/releases/tag/$(cat "$CORES/XRAY_VERSION")"
  echo "  - sing-box $(cat "$CORES/SINGBOX_VERSION") — GPL-3.0 — https://github.com/SagerNet/sing-box/releases/tag/$(cat "$CORES/SINGBOX_VERSION")"
  echo "  - geoip.dat / geosite.dat, runetfreedom release $(cat "$RF/RF_RELEASE") — https://github.com/runetfreedom/russia-v2ray-rules-dat/releases/tag/$(cat "$RF/RF_RELEASE")"
  echo "  - Avalonia UI, ReactiveUI, SkiaSharp, .NET runtime and other NuGet packages — MIT / Apache-2.0 etc., see the package pages on nuget.org (versions: upstream/v2rayN/Directory.Packages.props in the source)"
  echo
  echo "=============== GNU GENERAL PUBLIC LICENSE v3 (TER, v2rayN) ==============="
  cat "$HERE/upstream/LICENSE"
  echo
  echo "=============== Xray-core license ==============="
  cat "$CORES/x/LICENSE"
  echo
  echo "=============== sing-box license ==============="
  cat "$CORES"/s/*/LICENSE
} > "$APP/Contents/MacOS/THIRD-PARTY-NOTICES.txt"
# User data lives in ~/Library/Application Support, not inside the bundle
# (an update replaces the .app without losing subscriptions).
echo "When this file exists, app will not store configs under this folder" > "$APP/Contents/MacOS/NotStoreConfigHere.txt"
chmod +x "$APP/Contents/MacOS/TER" "$APP/Contents/MacOS/bin/xray/xray" "$APP/Contents/MacOS/bin/sing_box/sing-box"

cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key><string>ru</string>
  <key>CFBundleLocalizations</key><array><string>ru</string><string>en</string></array>
  <key>CFBundleDisplayName</key><string>TER</string>
  <key>CFBundleName</key><string>TER</string>
  <key>CFBundleExecutable</key><string>TER</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundleIconName</key><string>AppIcon</string>
  <key>CFBundleIdentifier</key><string>online.termitus.desktop</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>${VERSION}</string>
  <key>CFBundleVersion</key><string>${VERSION}</string>
  <key>CSResourcesFileMapped</key><true/>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSMinimumSystemVersion</key><string>13.6</string>
</dict>
</plist>
EOF

# Ad-hoc signature: Apple Silicon refuses to run unsigned arm64 code. Real
# Developer ID signing + notarization comes later (BUILD.md section 6).
codesign --force --deep -s - "$APP"
codesign --verify --deep "$APP" && echo "codesign (ad-hoc): ok"
# D-1 gate: the cores inside the finished bundle must be exactly the ones whose hashes are compiled into the app
for pair in "xray:$APP/Contents/MacOS/bin/xray/xray" "sing-box:$APP/Contents/MacOS/bin/sing_box/sing-box"; do
  n="${pair%%:*}"; f="${pair#*:}"
  want="$(grep -o "$n=[0-9a-f]\{64\}" "$HASH_SRC" | head -1 | cut -d= -f2)"; got="$(shasum -a 256 "$f" | cut -d' ' -f1)"
  [ -n "$want" ] && [ "$want" = "$got" ] || { echo "REFUSING: bundled $n ($got) does not match the pinned hash ($want): TUN would be refused at run time"; exit 1; }
done
echo "core pins: ok"

DMG="$DIST/TER-${VERSION}-osx-arm64.dmg"
rm -f "$DMG"
STAGE="$(mktemp -d)"
cp -R "$APP" "$STAGE/"
BG="$HERE/termitus/dmg/dmg-background.tiff"
DMGBUILD="$HERE/.tools/venv/bin/dmgbuild"   # python3 -m venv .tools/venv && .tools/venv/bin/pip install dmgbuild
if [ -x "$DMGBUILD" ] && [ -f "$BG" ]; then
  # Installer window: TER icon left, arrow (in the background), Applications right.
  # dmgbuild writes the Finder layout (.DS_Store) itself -- no AppleScript/Finder
  # automation, which timed out (-1712) with create-dmg.
  "$DMGBUILD" -s "$HERE/termitus/dmg/dmgbuild_settings.py" \
    -D app="$STAGE/TER.app" -D bg="$BG" -D icon="$APP/Contents/Resources/AppIcon.icns" \
    "TER" "$DMG"
else
  echo "WARNING: dmgbuild venv or background missing -> plain dmg without window layout"
  ln -s /Applications "$STAGE/Applications"
  hdiutil create -quiet -volname "TER" -srcfolder "$STAGE" -ov -format UDZO "$DMG"
fi
rm -rf "$STAGE"
shasum -a 256 "$DMG" | tee "$DMG.sha256"
echo "built: $DMG"
