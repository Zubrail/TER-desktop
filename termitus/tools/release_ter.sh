#!/bin/bash
# One-command TER release (2026-10-19; 0.6.3/0.6.4 were done by hand step by step and steps got
# forgotten). Run on the owner's Mac from the app worktree AFTER the owner's live test passed.
#
#   termitus/tools/release_ter.sh [--dry-run] [--from STEP] [--win] --notes dist/notes_ru_064.txt
#
# Steps (each prints what it did; --from resumes after a failure):
#   1 check   clean tree, VERSION, dist/TER-<v>-osx-arm64.dmg (+ win zip with --win), notes file
#   2 sign    dist/latest.json via sign_release.py (key ~/.ter-release), verified against the app's pubkey
#   3 upload  binaries to master /var/www/termitus-app/ (sha re-checked there), old latest.json kept
#             as latest.json.<old version>, latest.json uploaded LAST (= the moment auto-update starts)
#   4 site    static/ter-mac.html (+ ter-win.html with --win) links + sha in termitus-platform, committed
#             and pushed FROM MASTER (Mac keys are read-only), vpn-backend not touched (static files)
#   5 source  public GPL snapshot (make_public_snapshot.sh on top of the public main) -> pushed from
#             master + GitHub release v<v> with the dmg (and zip) attached
# Nothing here buys anything or touches users; every remote step is idempotent (re-run = same result).
set -euo pipefail
HERE="$(cd "$(dirname "$0")/../.." && pwd)"
TOOLS="$HERE/termitus/tools"
MASTER="${TER_MASTER:-vpn-server}"
WWW=/var/www/termitus-app
SITE_REPO=/opt/vpn-platform
PUBLIC_REPO="$(cat "$HERE/termitus/SOURCE_URL")"
URL_BASE="https://termitus.online/app/"
DRY=0; FROM=1; WIN=0; NOTES=""
while [ $# -gt 0 ]; do
  case "$1" in
    --dry-run) DRY=1 ;;
    --from) FROM="$2"; shift ;;
    --win) WIN=1 ;;
    --notes) NOTES="$2"; shift ;;
    *) echo "unknown arg $1"; exit 2 ;;
  esac
  shift
done
V="$(cat "$HERE/VERSION")"
DMG="$HERE/dist/TER-$V-osx-arm64.dmg"
ZIP="$HERE/dist/TER-$V-win-x64.zip"
say() { printf '\n== %s\n' "$*"; }
run() { if [ "$DRY" = 1 ]; then printf '   (dry) %s\n' "$*"; else "$@"; fi; }
sha() { shasum -a 256 "$1" | awk '{print $1}'; }

if [ "$FROM" -le 1 ]; then
  say "1/5 check TER $V"
  [ -z "$(git -C "$HERE" status --porcelain -- . ':!upstream' ':!dist')" ] || { echo "REFUSING: uncommitted changes"; exit 1; }
  [ -f "$DMG" ] || { echo "REFUSING: no $DMG (run package-ter-osx.sh)"; exit 1; }
  [ "$WIN" = 0 ] || [ -f "$ZIP" ] || { echo "REFUSING: --win but no $ZIP"; exit 1; }
  [ -n "$NOTES" ] && [ -s "$NOTES" ] || { echo "REFUSING: --notes <file> with the RU release notes is required"; exit 1; }
  ssh -o ConnectTimeout=10 "$MASTER" 'hostname' >/dev/null || { echo "REFUSING: master $MASTER unreachable"; exit 1; }
  echo "   dmg sha $(sha "$DMG")"; [ "$WIN" = 0 ] || echo "   zip sha $(sha "$ZIP")"
fi

if [ "$FROM" -le 2 ]; then
  say "2/5 sign latest.json"
  args=(sign --version "$V" --notes-file "$NOTES" --mac-arm64 "$DMG" --url-base "$URL_BASE" --out "$HERE/dist/latest.json")
  [ "$WIN" = 0 ] || args+=(--win-x64 "$ZIP")
  run python3 "$TOOLS/sign_release.py" "${args[@]}"
  PUB="$(grep -o 'PublicKeyBase64 = "[^"]*"' "$HERE/upstream/v2rayN/ServiceLib/Services/TermitusUpdate.cs" | cut -d'"' -f2)"
  run python3 "$TOOLS/sign_release.py" verify "$HERE/dist/latest.json" --pubkey "$PUB"
fi

if [ "$FROM" -le 3 ]; then
  say "3/5 upload to $MASTER:$WWW"
  files=("$DMG"); [ "$WIN" = 0 ] || files+=("$ZIP")
  for f in "${files[@]}"; do
    b="$(basename "$f")"
    run scp -q "$f" "$MASTER:$WWW/$b.part"
    run ssh "$MASTER" "set -e; cd $WWW; [ \"\$(sha256sum $b.part | cut -d' ' -f1)\" = $(sha "$f") ] || { echo 'sha mismatch on master'; exit 1; }; chmod 644 $b.part; mv -f $b.part $b"
  done
  old="$(ssh "$MASTER" "python3 -c 'import json;print(json.load(open(\"$WWW/latest.json\"))[\"version\"])'" 2>/dev/null || true)"
  if [ -n "$old" ] && [ "$old" != "$V" ]; then run ssh "$MASTER" "cp -n $WWW/latest.json $WWW/latest.json.$old"; fi
  run scp -q "$HERE/dist/latest.json" "$MASTER:$WWW/latest.json.part"
  run ssh "$MASTER" "chmod 644 $WWW/latest.json.part && mv -f $WWW/latest.json.part $WWW/latest.json"
  [ "$DRY" = 1 ] || curl -fsS "${URL_BASE}latest.json" | python3 -c "import json,sys; v=json.load(sys.stdin)['version']; assert v=='$V', v; print('   live latest.json =', v)"
fi

if [ "$FROM" -le 4 ]; then
  say "4/5 site pages"
  DSHA="$(sha "$DMG")"
  sub="s#TER-[0-9][0-9.]*-osx-arm64\\.dmg#TER-$V-osx-arm64.dmg#g; s#TER [0-9]\\.[0-9]\\.[0-9][0-9]*#TER $V#g; s#<code>[0-9a-f]\\{64\\}</code>#<code>$DSHA</code>#g"
  pages="static/ter-mac.html"
  if [ "$WIN" = 1 ]; then
    ZSHA="$(sha "$ZIP")"   # ter-win.html shows only the zip's hash
    run ssh "$MASTER" "cd $SITE_REPO && test -f static/ter-win.html && sed -i 's#TER-[0-9][0-9.]*-win-x64\\.zip#TER-$V-win-x64.zip#g; s#TER [0-9]\\.[0-9]\\.[0-9][0-9]*#TER $V#g; s#<code>[0-9a-f]\\{64\\}</code>#<code>$ZSHA</code>#g' static/ter-win.html"
    pages="$pages static/ter-win.html"
  fi
  run ssh "$MASTER" "set -e; cd $SITE_REPO; git status --porcelain | grep -v '^??' && { echo 'REFUSING: master repo dirty'; exit 1; } || true; \
    sed -i '$sub' static/ter-mac.html; git add $pages; \
    if git diff --cached --quiet; then echo '   site already on $V'; else \
      git -c user.name='TER release' -c user.email='noreply@termitus.online' commit -q -m 'Site: TER $V download links + sha'; \
      chown vpnscripts:vpnplatform $pages; chmod 664 $pages; \
      git -c credential.helper='!gh auth git-credential' push -q origin HEAD:main; git log --oneline -1; fi"
fi

if [ "$FROM" -le 5 ]; then
  say "5/5 public source + GitHub release v$V"
  W="${TMPDIR:-/tmp}/ter-pub-$V"; rm -rf "$W"; mkdir -p "$W"
  # worktree upstreams may lack the pinned v2rayN commit (17.10) -- use the main clone then
  UPC="$(cat "$HERE/UPSTREAM_COMMIT")"
  if [ -z "${TER_UPSTREAM_CLONE:-}" ] && ! git -C "$HERE/upstream" cat-file -e "$UPC^{commit}" 2>/dev/null; then
    TER_UPSTREAM_CLONE="$HOME/infra-mirror/app-ter/upstream"
  fi
  git -C "${TER_UPSTREAM_CLONE:-$HERE/upstream}" cat-file -e "$UPC^{commit}" \
    || { echo "REFUSING: no clone with v2rayN $UPC (set TER_UPSTREAM_CLONE)"; exit 1; }
  run git clone -q "$PUBLIC_REPO" "$W/parent"
  run git -C "$W/parent" bundle create -q "$W/parent.bundle" main
  run env PARENT_BUNDLE="$W/parent.bundle" bash "$TOOLS/make_public_snapshot.sh" "$W/out" "${TER_UPSTREAM_CLONE:-$HERE/upstream}"
  run scp -q "$W/out/TER-desktop-v$V.bundle" "$MASTER:/root/TER-desktop-v$V.bundle"
  run scp -q "$NOTES" "$MASTER:/root/ter-notes-$V.md"
  assets="$WWW/$(basename "$DMG")"; [ "$WIN" = 0 ] || assets="$assets $WWW/$(basename "$ZIP")"
  run ssh "$MASTER" "set -e; rm -rf /root/pub-$V; git clone -q -b main /root/TER-desktop-v$V.bundle /root/pub-$V; cd /root/pub-$V; \
    git fetch -q /root/TER-desktop-v$V.bundle 'refs/tags/*:refs/tags/*'; git remote set-url origin $PUBLIC_REPO; \
    git -c credential.helper='!gh auth git-credential' push -q origin main; \
    git -c credential.helper='!gh auth git-credential' push -q origin v$V; \
    gh release view v$V -R ${PUBLIC_REPO#https://github.com/} >/dev/null 2>&1 \
      || gh release create v$V -R ${PUBLIC_REPO#https://github.com/} --title 'TER $V' --notes-file /root/ter-notes-$V.md $assets"
fi
say "done: TER $V"
