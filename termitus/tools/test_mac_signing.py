"""Run: python3 -m unittest discover -s termitus/tools -p test_mac_signing.py.

TER_TEST_REAL_MAC_SIGNING=1 adds an actual ad-hoc bundle/pin test on macOS.
All signing is confined to fresh temporary fixtures; no app/core is launched.
"""
import importlib.util
import json
import os
from pathlib import Path
import plistlib
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

TOOLS = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("mac_signing", TOOLS / "mac_signing.py")
signing = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = signing
SPEC.loader.exec_module(signing)
IDENTITY = "A" * 40
ENV = {"TER_MAC_SIGNING_MODE": "developer-id", "TER_MAC_SIGN_IDENTITY": IDENTITY,
       "TER_MAC_NOTARY_PROFILE": "ter-notary"}


class SigningTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="ter-signing-test-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.calls = []

    def fake_command(self, args):
        self.calls.append(args)
        if args[:2] == [signing.SECURITY, "find-identity"]:
            return subprocess.CompletedProcess(args, 0, f'  1) {IDENTITY} "Developer ID Application: Fixture (FIXTURE)"\n', "")
        if args[0] == signing.CODESIGN and "--sign" in args:
            # Deliberately nondeterministic: a second signing changes the pin,
            # unlike deterministic ad-hoc signatures which hid the old defect.
            path = Path(args[-1])
            if path.is_file():
                with path.open("ab") as stream:
                    stream.write(f"signature-{len(self.calls)}".encode())
        return subprocess.CompletedProcess(args, 0, '{"status":"Accepted"}', "")

    def fixture(self):
        source = self.root / "source"
        source.mkdir()
        for name in signing.CORE_PATHS:
            (source / name).write_bytes(bytes.fromhex("cffaedfe") + name.encode())
        signed = self.root / "signed"
        signed.mkdir()
        pins = signing.prepare_cores(signing.Plan("developer-id", IDENTITY, "ter-notary"),
                                     source / "xray", source / "sing-box", signed)
        app = self.root / "TER.app"
        for name, rel in signing.CORE_PATHS.items():
            target = app / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(signed / name, target)
        (app / "Contents/Info.plist").write_bytes(plistlib.dumps({"CFBundleExecutable": "TER"}))
        (app / "Contents/MacOS/TER").write_bytes(bytes.fromhex("cffaedfe") + b"apphost")
        (app / "Contents/MacOS/helper.dylib").write_bytes(bytes.fromhex("cffaedfe") + b"library")
        return app, signed, pins

    def test_sign_once_then_outer_signature_preserves_pins(self):
        with patch.object(signing, "command", side_effect=self.fake_command):
            app, signed, pins = self.fixture()
            signing.sign_bundle(signing.Plan("developer-id", IDENTITY, "ter-notary"), app, signed)
        self.assertEqual(signing.verify_pins(app, signed), pins)
        sign_calls = [args for args in self.calls if "--sign" in args]
        core_calls = [args for args in sign_calls if Path(args[-1]).name in signing.CORE_PATHS]
        self.assertEqual(len(core_calls), 2)
        self.assertTrue(all("--deep" not in args for args in sign_calls))
        self.assertEqual(sign_calls[-1][-1], str(app))
        self.assertEqual(Path(sign_calls[-2][-1]).name, "helper.dylib")
        self.assertIn("--entitlements", sign_calls[-1])
        self.assertTrue(all("--entitlements" not in args for args in core_calls))
        self.assertTrue(all("--timestamp" in args and "runtime" in args for args in sign_calls))
        verification = [args for args in self.calls if "--verify" in args and "--deep" not in args]
        self.assertTrue(all("anchor apple generic" in args for args in verification))

    def test_accidental_outer_mutation_of_a_core_is_rejected(self):
        with patch.object(signing, "command", side_effect=self.fake_command):
            app, signed, _ = self.fixture()
        def wrong_outer(args):
            if args[0] == signing.CODESIGN and "--sign" in args and args[-1] == str(app):
                with (app / signing.CORE_PATHS["xray"]).open("ab") as stream:
                    stream.write(b"unexpected resign")
            return self.fake_command(args)
        with patch.object(signing, "command", side_effect=wrong_outer):
            with self.assertRaisesRegex(signing.SigningError, "once-signed pinned bytes"):
                signing.sign_bundle(signing.Plan("developer-id", IDENTITY, "ter-notary"), app, signed)

    def test_runtime_resources_move_to_sealed_resources_with_safe_aliases(self):
        with patch.object(signing, "command", side_effect=self.fake_command):
            app, signed, pins = self.fixture()
            for relative in ("ter-source-url.txt", "bin/ter-geo.version"):
                path = app / "Contents/MacOS" / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("fixture resource")
            signing.sign_bundle(signing.Plan("developer-id", IDENTITY, "ter-notary"), app, signed)
            for relative in ("ter-source-url.txt", "bin/ter-geo.version"):
                path = app / "Contents/MacOS" / relative
                self.assertTrue(path.is_symlink())
                self.assertEqual(path.read_text(), "fixture resource")
                self.assertEqual(path.resolve(), (app / "Contents/Resources/RuntimeData" / relative).resolve())
            self.assertEqual(signing.verify_pins(app, signed), pins)

    def test_external_resource_alias_is_rejected_without_modifying_target(self):
        with patch.object(signing, "command", side_effect=self.fake_command):
            app, signed, _ = self.fixture()
        external = self.root / "external"
        external.write_text("keep external bytes")
        (app / "Contents/MacOS/ter-source-url.txt").symlink_to(external)
        with self.assertRaises(signing.SigningError):
            signing.sign_bundle(signing.Plan("adhoc", "-"), app, signed)
        self.assertEqual(external.read_text(), "keep external bytes")

    def test_unknown_noncode_payload_requires_layout_review(self):
        with patch.object(signing, "command", side_effect=self.fake_command):
            app, signed, _ = self.fixture()
        (app / "Contents/MacOS/unexpected.txt").write_text("not a reviewed resource")
        with self.assertRaisesRegex(signing.SigningError, "Unexpected resource"):
            signing.sign_bundle(signing.Plan("adhoc", "-"), app, signed)

    def test_developer_id_configuration_refuses_missing_or_wrong_identity(self):
        for edits in [{"TER_MAC_SIGN_IDENTITY": ""}, {"TER_MAC_SIGN_IDENTITY": "-"},
                      {"TER_MAC_NOTARY_PROFILE": ""}, {"TER_MAC_SIGNING_MODE": "typo"}]:
            with self.subTest(edits=edits), patch.dict(os.environ, {**ENV, **edits}, clear=True):
                with patch.object(signing, "command") as command:
                    with self.assertRaises(signing.SigningError):
                        signing.Plan.from_environment()
                    command.assert_not_called()
        for label in ["Apple Development: Fixture (FIXTURE)", "Developer ID Application: Other (OTHER)"]:
            found = f'  1) {IDENTITY if label.startswith("Apple") else "B" * 40} "{label}"\n'
            with patch.dict(os.environ, ENV, clear=True), patch.object(signing, "command") as command:
                command.return_value = subprocess.CompletedProcess([], 0, found, "")
                with self.assertRaisesRegex(signing.SigningError, "not a valid Developer ID"):
                    signing.Plan.from_environment()
                self.assertEqual(command.call_count, 1)  # no sign/notary call

    def test_valid_identity_requires_both_notary_tools(self):
        with patch.dict(os.environ, ENV, clear=True), patch.object(signing, "command", side_effect=self.fake_command):
            plan = signing.Plan.from_environment()
        self.assertEqual(plan.mode, "developer-id")
        self.assertEqual(self.calls[1:], [[signing.XCRUN, "--find", "notarytool"],
                                         [signing.XCRUN, "--find", "stapler"]])

    def test_missing_notary_tool_refuses_without_any_signing(self):
        def missing(args):
            if args[0] == signing.XCRUN:
                raise signing.SigningError("notarytool unavailable")
            return self.fake_command(args)
        with patch.dict(os.environ, ENV, clear=True), patch.object(signing, "command", side_effect=missing):
            with self.assertRaisesRegex(signing.SigningError, "unavailable"):
                signing.Plan.from_environment()
        self.assertFalse(any("--sign" in args or "submit" in args for args in self.calls))

    def test_adhoc_does_not_silently_ignore_developer_settings(self):
        with patch.dict(os.environ, {}, clear=True), patch.object(signing, "command") as command:
            self.assertEqual(signing.Plan.from_environment().identity, "-")
            command.assert_not_called()
        with patch.dict(os.environ, {"TER_MAC_SIGN_IDENTITY": IDENTITY}, clear=True):
            with self.assertRaisesRegex(signing.SigningError, "explicit"):
                signing.Plan.from_environment()

    def test_notary_rejection_never_staples_or_assesses(self):
        image = self.root / "fixture.dmg"
        image.write_bytes(b"fixture")
        def rejected(args):
            self.calls.append(args)
            return subprocess.CompletedProcess(args, 0, '{"status":"Invalid"}', "")
        with patch.object(signing, "command", side_effect=rejected):
            with self.assertRaisesRegex(signing.SigningError, "not accepted"):
                signing.finish_dmg(signing.Plan("developer-id", IDENTITY, "ter-notary"), image)
        self.assertFalse(any("stapler" in args or "--assess" in args for args in self.calls))

    def test_notary_accepted_requires_stapled_ticket_and_assessment(self):
        image = self.root / "fixture.dmg"
        image.write_bytes(b"fixture")
        with patch.object(signing, "command", side_effect=self.fake_command):
            signing.finish_dmg(signing.Plan("developer-id", IDENTITY, "ter-notary"), image)
        self.assertTrue(any(args[1:3] == ["stapler", "staple"] for args in self.calls))
        self.assertTrue(any(args[1:3] == ["stapler", "validate"] for args in self.calls))
        self.assertEqual(self.calls[-1][1:3], ["--assess", "--type"])

    def test_adhoc_never_contacts_notary(self):
        image = self.root / "fixture.dmg"
        image.write_bytes(b"fixture")
        with patch.object(signing, "command") as command:
            result = signing.finish_dmg(signing.Plan("adhoc", "-"), image)
            self.assertIn("local integrity only", result)
            command.assert_not_called()

    def test_preflight_failure_preserves_existing_publish_output(self):
        repo = self.root / "repo"
        tools = repo / "termitus/tools"
        tools.mkdir(parents=True)
        shutil.copy2(TOOLS / "mac_signing.py", tools)
        packager = TOOLS.parents[1] / "package-ter-osx.sh"
        shutil.copy2(packager, repo)
        (repo / "VERSION").write_text("0.6.8\n")
        publish = repo / "upstream/v2rayN/bin/termitus/osx-arm64"
        publish.mkdir(parents=True)
        marker = publish / "prior-publish"
        marker.write_text("keep this")
        env = {**os.environ, **ENV, "TER_MAC_SIGN_IDENTITY": "invalid"}
        result = subprocess.run(["/bin/bash", str(repo / "package-ter-osx.sh")], env=env,
                                capture_output=True, text=True, timeout=10)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("exact 40-hex", result.stderr)
        self.assertEqual(marker.read_text(), "keep this")


@unittest.skipUnless(sys.platform == "darwin" and os.environ.get("TER_TEST_REAL_MAC_SIGNING") == "1",
                     "opt-in actual macOS ad-hoc fixture")
class ActualAdhocTests(unittest.TestCase):
    def test_real_codesign_keeps_both_pinned_cores_unchanged(self):
        with tempfile.TemporaryDirectory(prefix="ter-actual-adhoc-") as folder:
            root = Path(folder)
            signed = root / "signed"
            signed.mkdir()
            source = Path("/Applications/TER.app/Contents/MacOS/bin")
            plan = signing.Plan("adhoc", "-")
            pins = signing.prepare_cores(plan, source / "xray/xray", source / "sing_box/sing-box", signed)
            app = root / "TER.app"
            for name, rel in signing.CORE_PATHS.items():
                target = app / rel
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(signed / name, target)
            shutil.copyfile("/usr/bin/true", app / "Contents/MacOS/TER")
            (app / "Contents/MacOS/TER").chmod(0o755)
            (app / "Contents/Info.plist").write_bytes(plistlib.dumps({
                "CFBundleExecutable": "TER", "CFBundleIdentifier": "online.termitus.signing-fixture",
                "CFBundlePackageType": "APPL", "CFBundleVersion": "1"}))
            (app / "Contents/MacOS/bin/ter-geo.version").write_text("fixture-geo-version")
            signing.sign_bundle(plan, app, signed)
            self.assertEqual(signing.verify_pins(app, signed), pins)
            self.assertEqual((app / "Contents/MacOS/bin/ter-geo.version").read_text(), "fixture-geo-version")
            # The real outer signature's deep verification is in sign_bundle.


if __name__ == "__main__":
    unittest.main()
