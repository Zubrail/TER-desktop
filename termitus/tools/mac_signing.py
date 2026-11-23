#!/usr/bin/env python3
"""Local macOS packaging signatures. No publication or credential creation.

Sign pinned cores exactly once BEFORE compiling their hashes. The bundle phase
must never re-sign those cores. Developer ID mode is explicit and requires a
valid private-key identity plus a named notarytool Keychain profile.
"""
import argparse
from dataclasses import dataclass
import hashlib
import json
import os
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
import sys

CODESIGN = "/usr/bin/codesign"
SECURITY = "/usr/bin/security"
XCRUN = "/usr/bin/xcrun"
CORE_PATHS = {"xray": "Contents/MacOS/bin/xray/xray",
              "sing-box": "Contents/MacOS/bin/sing_box/sing-box"}
MACHO_MAGICS = {bytes.fromhex(h) for h in
                ("feedface", "cefaedfe", "feedfacf", "cffaedfe", "cafebabe", "bebafeca", "cafebabf", "bfbafeca")}
# Existing runtime-relative resource paths. Keep aliases for the .NET code,
# but seal the actual data in Resources rather than a code-only directory.
RESOURCE_PATHS = {"v2rayN.icns", "v2rayN.png", "THIRD-PARTY-NOTICES.txt",
                  "NotStoreConfigHere.txt", "ter-source-url.txt", "bin/geoip.dat",
                  "bin/geosite.dat", "bin/ter-geo.version"}


class SigningError(Exception):
    pass


def command(args):
    try:
        return subprocess.run(args, check=True, capture_output=True, text=True)
    except (OSError, subprocess.CalledProcessError) as error:
        # Do not echo notary/account output or a credential-bearing environment.
        raise SigningError(f"{Path(args[0]).name} failed; packaging stopped") from error


def sha256(path):
    with Path(path).open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def regular_file(path):
    if path.is_symlink() or not path.is_file():
        raise SigningError("Expected a regular file, not a symlink")
    return path


@dataclass(frozen=True)
class Plan:
    mode: str
    identity: str
    profile: str = ""

    @classmethod
    def from_environment(cls):
        mode = os.environ.get("TER_MAC_SIGNING_MODE", "adhoc")
        identity = os.environ.get("TER_MAC_SIGN_IDENTITY", "")
        profile = os.environ.get("TER_MAC_NOTARY_PROFILE", "")
        if mode == "adhoc":
            if identity or profile:
                raise SigningError("Developer ID settings require explicit TER_MAC_SIGNING_MODE=developer-id")
            return cls(mode, "-")
        if mode != "developer-id":
            raise SigningError("TER_MAC_SIGNING_MODE must be adhoc or developer-id")
        if not re.fullmatch(r"[a-fA-F0-9]{40}", identity):
            raise SigningError("Developer ID mode requires an exact 40-hex identity fingerprint")
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_. -]{0,127}", profile):
            raise SigningError("Developer ID mode requires a named TER_MAC_NOTARY_PROFILE")
        found = command([SECURITY, "find-identity", "-v", "-p", "codesigning"]).stdout
        valid = re.findall(r'^\s*\d+\)\s+([A-Fa-f0-9]{40})\s+"Developer ID Application: [^"\n]+"\s*$',
                           found, re.MULTILINE)
        if identity.upper() not in {item.upper() for item in valid}:
            raise SigningError("Selected identity is not a valid Developer ID Application private-key identity")
        for tool in ("notarytool", "stapler"):
            command([XCRUN, "--find", tool])
        return cls(mode, identity, profile)

    def sign(self, path, *, identifier=None, entitlements=None, runtime=True):
        args = [CODESIGN, "--force", "--sign", self.identity]
        if identifier:
            args += ["--identifier", identifier]
        if self.mode == "developer-id":
            args += ["--timestamp"]
            if runtime:
                args += ["--options", "runtime"]
            if entitlements:
                args += ["--entitlements", str(entitlements)]
        command(args + [str(path)])
        verify = [CODESIGN, "--verify", "--strict"]
        if self.mode == "developer-id":
            # A trusted local/self-signed certificate with a lookalike name
            # must not pass merely because its display label matched.
            verify += ["-R", "anchor apple generic"]
        command(verify + [str(path)])


def prepare_cores(plan, xray, sing_box, output):
    if output.is_symlink() or not output.is_dir() or any(output.iterdir()):
        raise SigningError("Signed-core directory must be a fresh empty directory")
    pins = {}
    for name, source in (("xray", xray), ("sing-box", sing_box)):
        source = regular_file(source)
        target = output / name
        shutil.copy2(source, target)
        plan.sign(target, identifier="online.termitus.core." + name)
        pins[name] = sha256(target)
    (output / "pins.json").write_text(json.dumps(pins), encoding="utf-8")
    return pins


def verify_pins(app, signed_cores):
    pins = json.loads(regular_file(signed_cores / "pins.json").read_text(encoding="utf-8"))
    if (not isinstance(pins, dict) or set(pins) != set(CORE_PATHS)
            or any(not isinstance(h, str) or not re.fullmatch(r"[a-f0-9]{64}", h) for h in pins.values())):
        raise SigningError("Invalid signed-core pins")
    for name, rel in CORE_PATHS.items():
        if sha256(regular_file(app / rel)) != pins[name]:
            raise SigningError(f"Bundled {name} differs from the once-signed pinned bytes")
    return pins


def native_file(path):
    with path.open("rb") as stream:
        return stream.read(4) in MACHO_MAGICS


def resource_alias(app, path):
    try:
        relative = path.relative_to(app / "Contents/MacOS")
    except ValueError:
        return False
    if relative.as_posix() not in RESOURCE_PATHS:
        return False
    target = app / "Contents/Resources/RuntimeData" / relative
    if any(parent.is_symlink() for parent in target.parents if parent.is_relative_to(app)):
        return False
    return (os.readlink(path) == os.path.relpath(target, path.parent)
            and target.is_file() and not target.is_symlink()
            and not native_file(target))


def stage_resources(app):
    # Validate pre-existing links before any relocation. In particular a
    # symlinked Resources directory must never redirect writes outside app.
    for path in app.rglob("*"):
        if path.is_symlink() and not resource_alias(app, path):
            raise SigningError("Unapproved bundle symlink; layout review required")
    mac = app / "Contents/MacOS"
    for path in list(mac.rglob("*")):
        if path.is_symlink() or not path.is_file():
            continue
        relative = path.relative_to(mac)
        native = native_file(path)
        if relative.as_posix() not in RESOURCE_PATHS:
            if not native:
                raise SigningError("Unexpected resource in MacOS; layout review required")
            continue
        if native:
            raise SigningError("Executable at a resource path; layout review required")
        target = app / "Contents/Resources/RuntimeData" / relative
        if target.exists() or target.is_symlink():
            raise SigningError("Resource destination already exists")
        target.parent.mkdir(parents=True, exist_ok=True)
        path.rename(target)
        path.symlink_to(os.path.relpath(target, path.parent))


def sign_bundle(plan, app, signed_cores):
    if app.is_symlink() or not app.is_dir():
        raise SigningError("Expected a real app bundle")
    verify_pins(app, signed_cores)
    stage_resources(app)
    info = plistlib.loads(regular_file(app / "Contents/Info.plist").read_bytes())
    main_name = info.get("CFBundleExecutable", "")
    if not isinstance(main_name, str) or not main_name or Path(main_name).name != main_name:
        raise SigningError("Invalid bundle executable name")
    main = regular_file(app / "Contents/MacOS" / main_name)
    pinned = {app / rel for rel in CORE_PATHS.values()}
    # The present TER layout has flat Mach-O executables/dylibs, no nested
    # bundles/frameworks. Only the reviewed data aliases above are allowed.
    for path in sorted(app.rglob("*"), key=lambda p: (-len(p.parts), str(p))):
        if path.is_symlink():
            if resource_alias(app, path):
                continue
            raise SigningError("Symlinked bundle content needs explicit signing-layout review")
        if path.is_dir() and path.suffix in {".app", ".framework", ".xpc", ".bundle"}:
            raise SigningError("Nested bundle needs explicit signing-layout review")
        if path.is_file() and path not in pinned and path != main:
            with path.open("rb") as stream:
                native = stream.read(4) in MACHO_MAGICS
            if native:
                plan.sign(path)
    entitlements = Path(__file__).with_name("mac_app.entitlements")
    # The outer signature signs the apphost last. Never use --deep for signing:
    # that would replace the two core signatures whose hashes are compiled in.
    plan.sign(app, entitlements=entitlements)
    command([CODESIGN, "--verify", "--deep", "--strict", str(app)])
    verify_pins(app, signed_cores)


def finish_dmg(plan, dmg):
    regular_file(dmg)
    if plan.mode == "adhoc":
        return "adhoc: local integrity only; not a Developer ID/notarized release"
    plan.sign(dmg, runtime=False)
    submitted = command([XCRUN, "notarytool", "submit", str(dmg), "--keychain-profile", plan.profile,
                         "--wait", "--output-format", "json"])
    try:
        accepted = json.loads(submitted.stdout).get("status") == "Accepted"
    except (ValueError, AttributeError):
        accepted = False
    if not accepted:
        raise SigningError("Notarization was not accepted; do not distribute this image")
    command([XCRUN, "stapler", "staple", str(dmg)])
    command([XCRUN, "stapler", "validate", str(dmg)])
    command(["/usr/sbin/spctl", "--assess", "--type", "open", "--context",
             "context:primary-signature", "--verbose=2", str(dmg)])
    return "Developer ID notarization/stapled ticket/Gatekeeper image assessment passed"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="action", required=True)
    commands.add_parser("preflight")
    prepare = commands.add_parser("prepare-cores")
    prepare.add_argument("--xray", type=Path, required=True)
    prepare.add_argument("--sing-box", type=Path, required=True)
    prepare.add_argument("--output", type=Path, required=True)
    bundle = commands.add_parser("sign-bundle")
    bundle.add_argument("--app", type=Path, required=True)
    bundle.add_argument("--signed-cores", type=Path, required=True)
    image = commands.add_parser("finish-dmg")
    image.add_argument("--dmg", type=Path, required=True)
    args = parser.parse_args()
    plan = Plan.from_environment()  # must succeed before any signing/network operation
    if args.action == "prepare-cores":
        pins = prepare_cores(plan, args.xray, args.sing_box, args.output)
        print(";".join(f"{name}={pins[name]}" for name in CORE_PATHS))
    elif args.action == "sign-bundle":
        sign_bundle(plan, args.app, args.signed_cores)
    elif args.action == "finish-dmg":
        print(finish_dmg(plan, args.dmg))
    else:
        print(f"Signing preflight: {plan.mode}")


if __name__ == "__main__":
    try:
        main()
    except (SigningError, OSError, ValueError, plistlib.InvalidFileException) as error:
        print(f"REFUSING: {error}", file=sys.stderr)
        raise SystemExit(1)
