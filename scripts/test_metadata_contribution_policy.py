import metadata_contribution_policy as policy
import importlib.util
import contextlib
import io
import json
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).parent


def load(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_public_metadata_fields_are_rejected():
    for field in ("Name", "Punchline", "Description"):
        assert policy.reject_player_metadata_field(field) is not None


def test_ui_contribution_is_not_rejected_by_metadata_policy():
    assert policy.reject_player_metadata_field("entries") is None


def test_validator_rejects_metadata_payload():
    validator = load("validate_contribution")
    clean, reason = validator.check_one({
        "InternalName": "Example",
        "Field": "Description",
        "Original": "English description",
        "Translated": "中文详情",
    })
    assert clean is None
    assert "维护" in reason


def test_inbox_rejects_metadata_payload():
    inbox = load("inbox_contribution")
    hard, soft = inbox.check({
        "Field": "Punchline",
        "Original": "English",
        "Translated": "中文简介",
    })
    assert any("维护" in item for item in hard)


def test_importer_requires_policy_module():
    """The write-back importer must fail closed if its policy is unavailable."""
    blocker = (
        "import sys; "
        "class Blocker: "
        "  def find_spec(self, fullname, path=None, target=None): "
        "    raise ImportError('blocked policy') if fullname == 'metadata_contribution_policy' else None; "
        "sys.meta_path.insert(0, Blocker()); import import_contributions"
    )
    result = subprocess.run(
        [sys.executable, "-c", blocker],
        cwd=str(ROOT),
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    assert result.returncode != 0
    assert "blocked policy" in (result.stderr or "")


def test_importer_never_writes_player_metadata_even_with_force():
    """The importer keeps a rejected metadata record in the archive only."""
    importer = load("import_contributions")
    payload = {
        "contributions": [
            {
                "Id": "player-description-1",
                "TimeLocal": "2026-11-18 12:00:00",
                "InternalName": "Example",
                "Field": "Description",
                "Original": "English description",
                "Translated": "中文详情",
            }
        ]
    }
    with tempfile.TemporaryDirectory() as temp:
        root = Path(temp)
        contribution_file = root / "contributions.json"
        table_file = root / "translations.json"
        history_file = root / "history.jsonl"
        archive_dir = root / "contributions"
        log_file = root / "contributions.md"
        original_table = {
            "_meta": {"updatedAt": "2026-11-17"},
            "Example": {
                "Description": {
                    "Original": "English description",
                    "Translated": "旧详情",
                    "Source": "ai",
                }
            },
        }
        contribution_file.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
        table_file.write_text(json.dumps(original_table, ensure_ascii=False), encoding="utf-8")

        with contextlib.redirect_stdout(io.StringIO()):
            result = importer.main(
                [
                    str(contribution_file),
                    "--table",
                    str(table_file),
                    "--history",
                    str(history_file),
                    "--contrib-dir",
                    str(archive_dir),
                    "--contrib-log",
                    str(log_file),
                    "--player",
                    "--force",
                ]
            )

        assert result == 0
        assert json.loads(table_file.read_text(encoding="utf-8")) == original_table
        archives = list(archive_dir.glob("*.json"))
        assert len(archives) == 1
        archived = json.loads(archives[0].read_text(encoding="utf-8"))
        assert archived["import"]["applied"] == 0
        assert archived["import"]["skipped"][0]["field"] == "Description"
        assert "维护" in archived["import"]["skipped"][0]["reason"]


def test_importer_allows_explicit_maintainer_metadata_update():
    """The protection is for player submissions; maintainers keep the edit path."""
    importer = load("import_contributions")
    payload = {
        "note": "维护者修正官方插件详情",
        "contributions": [
            {
                "InternalName": "Example",
                "Field": "Description",
                "Original": "English description",
                "Translated": "维护者详情",
            }
        ],
    }
    with tempfile.TemporaryDirectory() as temp:
        root = Path(temp)
        contribution_file = root / "contributions.json"
        table_file = root / "translations.json"
        history_file = root / "history.jsonl"
        archive_dir = root / "contributions"
        log_file = root / "contributions.md"
        original_table = {
            "_meta": {"updatedAt": "2026-11-17"},
            "Example": {
                "Description": {
                    "Original": "English description",
                    "Translated": "旧详情",
                    "Source": "ai",
                }
            },
        }
        contribution_file.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
        table_file.write_text(json.dumps(original_table, ensure_ascii=False), encoding="utf-8")

        with contextlib.redirect_stdout(io.StringIO()):
            result = importer.main(
                [
                    str(contribution_file),
                    "--table",
                    str(table_file),
                    "--history",
                    str(history_file),
                    "--contrib-dir",
                    str(archive_dir),
                    "--contrib-log",
                    str(log_file),
                    "--maintainer",
                ]
            )

        assert result == 0
        updated = json.loads(table_file.read_text(encoding="utf-8"))
        assert updated["Example"]["Description"]["Translated"] == "维护者详情"
        assert updated["Example"]["Description"]["Source"] == "user"
