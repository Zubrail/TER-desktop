#!/usr/bin/env python3
"""make_update_fixture.py OUTDIR [--version 0.6.1] [--variant valid|tampered|same|badhash]

Builds a throw-away update set for LIVE checks of the TER updater against fake_backend.py (APP_DIR=OUTDIR/app):
  OUTDIR/key              throw-away Ed25519 key (NOT the release key; keep OUTDIR outside git, e.g. in $TMPDIR)
  OUTDIR/app/latest.json  signed manifest (variant decides what is wrong with it)
  OUTDIR/app/TER-<v>-osx-arm64.dmg   a tiny dmg holding TER.app (Info.plist: our bundle id + version, marker file "new")
  OUTDIR/installed/TER.app           the "installed" app the update swaps (marker file "old")
Prints the env for the test instance: TER_TEST_UPDATE_URL / _PUBKEY / _TARGET.  macOS only (hdiutil).
"""
import argparse, base64, json, os, shutil, subprocess, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import sign_release as sr

ap = argparse.ArgumentParser()
ap.add_argument("outdir")
ap.add_argument("--version", default="0.6.1")
ap.add_argument("--variant", default="valid", choices=["valid", "tampered", "same", "badhash"])
ap.add_argument("--port", default="18080")
a = ap.parse_args()
out = os.path.abspath(a.outdir)
if sr.in_git_checkout(os.path.join(out, "x")):
    sys.exit("OUTDIR is inside a git checkout; use a temp folder")
shutil.rmtree(out, ignore_errors=True)
os.makedirs(os.path.join(out, "app")); os.makedirs(os.path.join(out, "pkg", "TER.app", "Contents", "MacOS"))
os.makedirs(os.path.join(out, "installed", "TER.app", "Contents", "MacOS"))

seed = os.urandom(32)
open(os.path.join(out, "key"), "w").write(base64.b64encode(seed).decode() + "\n")
pub = base64.b64encode(sr.public_key(seed)).decode()

plist = """<?xml version="1.0" encoding="UTF-8"?>
<plist version="1.0"><dict>
  <key>CFBundleIdentifier</key><string>online.termitus.desktop</string>
  <key>CFBundleShortVersionString</key><string>{v}</string>
</dict></plist>
"""
for root, ver, marker in (("pkg", a.version, "new"), ("installed", "0.6.0", "old")):
    d = os.path.join(out, root, "TER.app", "Contents")
    open(os.path.join(d, "Info.plist"), "w").write(plist.format(v=ver))
    open(os.path.join(d, "MacOS", "marker"), "w").write(marker)

dmg = os.path.join(out, "app", f"TER-{a.version}-osx-arm64.dmg")
subprocess.run(["hdiutil", "create", "-quiet", "-volname", "TER", "-srcfolder", os.path.join(out, "pkg"), "-ov", "-format", "UDZO", dmg], check=True)

notes = "Что нового:\n— проверка подключения\n— сообщение о проблеме"
base = f"http://127.0.0.1:{a.port}/app/"
urls = {"mac_arm64": base + os.path.basename(dmg)}
hashes = {"mac_arm64": sr.sha256_file(dmg)}
version = "0.6.0" if a.variant == "same" else a.version
if a.variant == "badhash":
    hashes["mac_arm64"] = "0" * 64
sig = sr.sign(seed, sr.canonical(version, notes, urls, hashes))
shown_notes = notes + "\n— (подмена)" if a.variant == "tampered" else notes   # signed one text, published another
doc = {"version": version, "notes_ru": shown_notes, "url_mac_arm64": urls["mac_arm64"], "sha256": hashes, "signature": base64.b64encode(sig).decode()}
open(os.path.join(out, "app", "latest.json"), "w", encoding="utf-8").write(json.dumps(doc, ensure_ascii=False, indent=2) + "\n")
print(f"APP_DIR={out}/app")
print(f"TER_TEST_UPDATE_URL={base}latest.json")
print(f"TER_TEST_UPDATE_PUBKEY={pub}")
print(f"TER_TEST_UPDATE_TARGET={out}/installed/TER.app")
