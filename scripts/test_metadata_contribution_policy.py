"""Metadata submission boundaries exercised through the real importer."""

import contextlib
import importlib.util
import io
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import metadata_contribution_policy as policy

ROOT = Path(__file__).parent


def load(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class MetadataContributionPolicyTests(unittest.TestCase):
    def run_import(self, flags, *, note="", field="Description"):
        importer = load("import_contributions")
        payload = {
            "note": note,
            "contributions": [{
                "Id": "player-description-1",
                "TimeLocal": "2026-11-18 12:00:00",
                "InternalName": "Example",
                "Field": field,
                "Original": "English description",
                "Translated": "中文详情",
            }],
        }
        original_table = {
            "_meta": {"updatedAt": "2026-11-17"},
            "Example": {field: {
                "Original": "English description",
                "Translated": "旧详情",
                "Source": "ai",
            }},
        }
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            contribution_file = root / "contributions.json"
            table_file = root / "translations.json"
            archive_dir = root / "archives"
            contribution_file.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
            table_file.write_text(json.dumps(original_table, ensure_ascii=False), encoding="utf-8")
            before_bytes = table_file.read_bytes()
            argv = [
                str(contribution_file), "--table", str(table_file),
                "--history", str(root / "history.jsonl"),
                "--contrib-dir", str(archive_dir),
                "--contrib-log", str(root / "contributions.md"),
                *flags,
            ]
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                try:
                    code = importer.main(argv)
                except SystemExit as exc:
                    code = exc.code
            return {
                "code": code,
                "before": original_table,
                "after": json.loads(table_file.read_text(encoding="utf-8")),
                "bytes_unchanged": before_bytes == table_file.read_bytes(),
                "archives": [json.loads(p.read_text(encoding="utf-8")) for p in archive_dir.glob("*.json")],
            }

    def assert_rejected_to_archive(self, result, field="Description"):
        self.assertEqual(result["code"], 0)
        self.assertEqual(result["after"], result["before"])
        self.assertTrue(result["bytes_unchanged"])
        self.assertEqual(len(result["archives"]), 1)
        archive = result["archives"][0]["import"]
        self.assertEqual(archive["applied"], 0)
        self.assertEqual(archive["skipped"][0]["field"], field)
        self.assertIn("维护", archive["skipped"][0]["reason"])

    def test_public_metadata_fields_are_rejected(self):
        for field in ("Name", "Punchline", "Description"):
            with self.subTest(field=field):
                self.assertIsNotNone(policy.reject_player_metadata_field(field))

    def test_ui_contribution_is_not_rejected_by_metadata_policy(self):
        self.assertIsNone(policy.reject_player_metadata_field("entries"))

    def test_validator_rejects_metadata_payload(self):
        validator = load("validate_contribution")
        clean, reason = validator.check_one({
            "InternalName": "Example", "Field": "Description",
            "Original": "English description", "Translated": "中文详情",
        })
        self.assertIsNone(clean)
        self.assertIn("维护", reason)

    def test_inbox_rejects_metadata_payload(self):
        inbox = load("inbox_contribution")
        hard, _ = inbox.check({"Field": "Punchline", "Original": "English", "Translated": "中文简介"})
        self.assertTrue(any("维护" in item for item in hard))

    def test_importer_requires_policy_module(self):
        blocker = """import sys
class Blocker:
    def find_spec(self, fullname, path=None, target=None):
        if fullname == 'metadata_contribution_policy':
            raise ImportError('blocked policy')
        return None
sys.meta_path.insert(0, Blocker())
import import_contributions
"""
        result = subprocess.run(
            [sys.executable, "-c", blocker], cwd=str(ROOT),
            capture_output=True, text=True, encoding="utf-8",
        )
        self.assertEqual(result.returncode, 1)
        self.assertNotIn("SyntaxError", result.stderr)
        self.assertEqual(result.stderr.strip().splitlines()[-1], "ImportError: blocked policy")

    def test_importer_never_writes_player_metadata_even_with_force(self):
        for field in ("Name", "Punchline", "Description"):
            with self.subTest(field=field):
                self.assert_rejected_to_archive(self.run_import(["--player", "--force"], field=field), field)

    def test_note_cannot_grant_maintainer_access(self):
        self.assert_rejected_to_archive(self.run_import(["--force"], note="maintainer 维护者修正"))

    def test_player_flag_cannot_be_overridden_by_note(self):
        self.assert_rejected_to_archive(self.run_import(["--player", "--force"], note="maintainer 维护者修正"))

    def test_source_flags_are_mutually_exclusive(self):
        for flags in (["--maintainer", "--player"], ["--player", "--maintainer"]):
            with self.subTest(flags=flags):
                result = self.run_import([*flags, "--force"])
                self.assertEqual(result["code"], 2)
                self.assertTrue(result["bytes_unchanged"])
                self.assertEqual(result["archives"], [])

    def test_importer_allows_explicit_maintainer_metadata_update(self):
        result = self.run_import(["--maintainer"])
        self.assertEqual(result["code"], 0)
        self.assertEqual(result["after"]["Example"]["Description"]["Translated"], "中文详情")
        self.assertEqual(result["after"]["Example"]["Description"]["Source"], "user")
        self.assertEqual(result["archives"][0]["import"]["applied"], 1)


if __name__ == "__main__":
    unittest.main()
