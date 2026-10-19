#!/bin/bash
# Build TER-<version>-win-x64.zip (portable folder TER/) from the dotnet publish output
# (13.10.2026, first Windows build). Same rules as package-ter-osx.sh: cores from the
# OFFICIAL Windows releases (XTLS/Xray-core -> xray.exe + wintun.dll, SagerNet/sing-box ->
# sing-box.exe + libcronet.dll), downloaded + checksum-verified into ./cores/win
# (BUILD.md «Windows»), runetfreedom geo files from ./cores/rf, the same release gates
# (update key embedded, real SOURCE_URL). An installer (Inno Setup) needs Windows itself --
# this zip is what the VM live test and the first beta use.
#
#   run:  bash package-ter-win.sh   (it publishes itself -- 14.10: a stale publish dir once
#         shipped 0.6.2 code inside the 0.6.3 zip; ReadyToRun because JIT-at-start took 70-100 s
#         under x64 emulation on Windows ARM)
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
PUB="$HERE/upstream/v2rayN/bin/termitus/win-x64"
CORES="$HERE/cores"
WIN="$CORES/win"
DIST="$HERE/dist"
VERSION="$(tr -d ' \n' < "$HERE/VERSION")"
OUT="$DIST/win/TER"
ZIP="$DIST/TER-$VERSION-win-x64.zip"

# ---- release gates (same as package-ter-osx.sh) ----
KEYFILE="$HERE/upstream/v2rayN/ServiceLib/Services/TermitusUpdate.cs"
if ! grep -qE 'PublicKeyBase64 *= *"[A-Za-z0-9+/=]{40,}"' "$KEYFILE"; then
  [ "${ALLOW_NO_UPDATE_KEY:-}" = 1 ] || { echo "REFUSING: no update public key is embedded (see package-ter-osx.sh)"; exit 1; }
  echo "WARNING: building WITHOUT an update key -- this build must not be distributed"
fi
SOURCE_URL="$(tr -d ' \n' < "$HERE/termitus/SOURCE_URL" 2>/dev/null || true)"
case "$SOURCE_URL" in https://*) ;; *) echo "termitus/SOURCE_URL must hold the https URL of the public source repository"; exit 1;; esac
echo "$SOURCE_URL" | grep -q 'REPLACE-ME' && { [ "${ALLOW_PLACEHOLDER_SOURCE:-}" = 1 ] || { echo "REFUSING: SOURCE_URL is a placeholder"; exit 1; }; }

rm -rf "$PUB"
(cd "$HERE/upstream/v2rayN" && DOTNET_ROOT=$HOME/.dotnet "$HOME/.dotnet/dotnet" publish v2rayN.Desktop/v2rayN.Desktop.csproj \
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true \
  -p:Version="$VERSION" -o "$PUB" -nologo -v q) || { echo "dotnet publish failed"; exit 1; }
[ -f "$PUB/v2rayN.exe" ] || { echo "no publish output in $PUB (dotnet publish -r win-x64 first)"; exit 1; }
[ -f "$WIN/x/xray.exe" ] && [ -f "$WIN/x/wintun.dll" ] && [ -f "$WIN"/s/*/sing-box.exe ] || { echo "Windows cores missing in $WIN (BUILD.md «Windows»)"; exit 1; }
RF="$CORES/rf"
[ -f "$RF/geoip.dat" ] && [ -f "$RF/geosite.dat" ] && [ -f "$RF/RF_RELEASE" ] || { echo "runetfreedom geo files missing in $RF"; exit 1; }
(cd "$RF" && shasum -a 256 -c geoip.dat.sha256sum && shasum -a 256 -c geosite.dat.sha256sum) || { echo "geo checksum mismatch"; exit 1; }

rm -rf "$DIST/win" && mkdir -p "$OUT"
cp -R "$PUB/." "$OUT/"
rm -f "$OUT"/*.pdb "$OUT"/*.icns
# The process shows up as TER in Task Manager (the single-file bundle carries v2rayN.dll inside).
mv "$OUT/v2rayN.exe" "$OUT/TER.exe"
mkdir -p "$OUT/bin/xray" "$OUT/bin/sing_box"
cp "$WIN/x/xray.exe" "$WIN/x/wintun.dll" "$OUT/bin/xray/"
cp "$WIN"/s/*/sing-box.exe "$WIN"/s/*/libcronet.dll "$OUT/bin/sing_box/"
cp "$RF/geoip.dat" "$RF/geosite.dat" "$OUT/bin/"
cp "$RF/RF_RELEASE" "$OUT/bin/ter-geo.version"
printf '%s\n' "$SOURCE_URL" > "$OUT/ter-source-url.txt"
{
  echo "TER $VERSION for Windows — open-source notices"
  echo
  echo "TER is free software under the GNU General Public License v3.0, a fork of v2rayN (c) 2dust."
  echo "Corresponding source code of this exact release (tag v$VERSION): $SOURCE_URL"
  echo
  echo "Bundled components (unmodified binaries, source at the given release tags):"
  echo "  - Xray-core $(cat "$WIN/XRAY_VERSION") — MPL-2.0 — https://github.com/XTLS/Xray-core/releases/tag/$(cat "$WIN/XRAY_VERSION")"
  echo "  - Wintun (shipped with Xray-core) — see LICENSE-Wintun below"
  echo "  - sing-box $(cat "$WIN/SINGBOX_VERSION") — GPL-3.0 — https://github.com/SagerNet/sing-box/releases/tag/$(cat "$WIN/SINGBOX_VERSION")"
  echo "  - geoip.dat / geosite.dat, runetfreedom release $(cat "$RF/RF_RELEASE") — https://github.com/runetfreedom/russia-v2ray-rules-dat/releases/tag/$(cat "$RF/RF_RELEASE")"
  echo "  - Avalonia UI, ReactiveUI, SkiaSharp, .NET runtime and other NuGet packages — MIT / Apache-2.0 etc."
  echo
  echo "=============== GNU GENERAL PUBLIC LICENSE v3 (TER, v2rayN) ==============="
  cat "$HERE/upstream/LICENSE"
  echo
  echo "=============== Xray-core license ==============="
  cat "$WIN/x/LICENSE"
  echo
  echo "=============== Wintun license ==============="
  cat "$WIN/x/LICENSE-Wintun"
  echo
  echo "=============== sing-box license ==============="
  cat "$WIN"/s/*/LICENSE
} > "$OUT/THIRD-PARTY-NOTICES.txt"
# Same as macOS: user data outside the program folder, so an update can replace the folder.
echo "When this file exists, app will not store configs under this folder" > "$OUT/NotStoreConfigHere.txt"

rm -f "$ZIP" && (cd "$DIST/win" && zip -qr "$ZIP" TER)
shasum -a 256 "$ZIP" | tee "$ZIP.sha256"
echo "built: $ZIP"
