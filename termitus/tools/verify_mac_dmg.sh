#!/bin/bash
# Validate the exact update payload without installing or launching TER.
set -euo pipefail
[ "$#" = 2 ] || { echo "usage: $0 DMG VERSION" >&2; exit 2; }
DMG="$1"; VERSION="$2"
[ -f "$DMG" ] || { echo "REFUSING: missing DMG: $DMG" >&2; exit 1; }
WORK="$(mktemp -d "${TMPDIR:-/tmp}/ter-dmg-verify.XXXXXX")"
chmod 700 "$WORK"
mkdir "$WORK/mount" "$WORK/stage"
ATTACH_ATTEMPTED=0
cleanup() {
  local result=$?
  trap - EXIT
  # A failed attach can still leave a mount. Never delete through a mounted path.
  if [ "$ATTACH_ATTEMPTED" = 1 ]; then
    if ! /usr/bin/hdiutil detach "$WORK/mount" >/dev/null; then
      echo "REFUSING: detach failed; verification files retained at $WORK" >&2
      exit 1
    fi
  fi
  /bin/rm -rf "$WORK"
  exit "$result"
}
trap cleanup EXIT
/usr/bin/hdiutil verify "$DMG" >/dev/null
ATTACH_ATTEMPTED=1
/usr/bin/hdiutil attach -nobrowse -readonly -noautoopen -mountpoint "$WORK/mount" "$DMG" >/dev/null
shopt -s nullglob
apps=("$WORK/mount/"*.app)
[ "${#apps[@]}" = 1 ] && [ ! -L "${apps[0]}" ] || { echo "REFUSING: expected one real app bundle" >&2; exit 1; }
APP="${apps[0]}"
PLIST="$APP/Contents/Info.plist"
ID="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$PLIST")"
V="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$PLIST")"
[ "$ID" = online.termitus.desktop ] && [ "$V" = "$VERSION" ] || { echo "REFUSING: bundle identity/version mismatch" >&2; exit 1; }
/usr/bin/codesign --verify --deep --strict "$APP"
/usr/bin/ditto "$APP" "$WORK/stage/TER.app"
/usr/bin/codesign --verify --deep --strict "$WORK/stage/TER.app"
echo "DMG gate passed: TER $VERSION (mounted and staged strict signatures)"
