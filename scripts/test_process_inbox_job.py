"""Real Git/ingest regression tests; temporary repositories never contact GitHub."""
from __future__ import annotations

import hashlib
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys
import tempfile
import threading
import unittest

SCRIPTS = Path(__file__).resolve().parent
HELPER = SCRIPTS / "process_inbox_job.py"


class InboxTransactionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="firegaze inbox git ")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.remote = self.root / "remote.git"
        self.git(self.root, "init", "--bare", "--initial-branch=main", str(self.remote))
        self.seed = self.root / "seed"
        self.git(self.root, "clone", str(self.remote), str(self.seed))
        (self.seed / "scripts").mkdir()
        for script in SCRIPTS.glob("*.py"):
            if not script.name.startswith("test_"):
                shutil.copyfile(script, self.seed / "scripts" / script.name)
        (self.seed / "uit-packs").mkdir()
        (self.seed / "uit-packs" / "index.json").write_text('{"plugins":{}}\n', encoding="utf-8")
        self.commit(self.seed, "seed")
        self.git(self.seed, "push", "origin", "HEAD:main")
        self.a = self.root / "clone a"
        self.b = self.root / "clone b"
        self.git(self.root, "clone", str(self.remote), str(self.a))
        self.git(self.root, "clone", str(self.remote), str(self.b))

    def git(self, directory, *args, check=True):
        result = subprocess.run(["git", "-C", str(directory), *args], capture_output=True, text=True, encoding="utf-8")
        if check and result.returncode:
            self.fail(f"git {args} failed: {result.stderr}")
        return result.stdout.strip()

    def commit(self, directory, message):
        self.git(directory, "add", ".")
        self.git(directory, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.test", "commit", "-m", message)

    def issue(self, number=11, plugin="Alpha"):
        return {"number": number, "title": f"[译文贡献] {plugin} · 1 条", "user": {"login": "fixture"},
                "updated_at": "2026-10-18T12:00:00Z", "body": "```json\n" + json.dumps(self.payload(plugin), ensure_ascii=False) + "\n```"}

    def payload(self, plugin):
        return {"type": "uit-contribution", "plugin": plugin,
                "entries": [{"Original": "Enable " + plugin, "Translated": "启用" + plugin, "Source": "user"}],
                "resources": [], "attributes": []}

    def invoke(self, clone, issue=None, *, name="result", attempts=3):
        output = self.root / name
        command = [sys.executable, str(HELPER), "--repo", str(clone), "--output-dir", str(output),
                   "--max-attempts", str(attempts), "--backoff", "0"]
        if issue is None:
            command += ["--mode", "direct"]
        else:
            event = self.root / (name + "-event.json")
            event.write_text(json.dumps({"issue": issue}, ensure_ascii=False), encoding="utf-8")
            command += ["--mode", "issue", "--event", str(event)]
        result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8")
        return result, output

    def remote_json(self, path):
        return json.loads(self.git(self.remote, "show", "main:" + path))

    def remote_paths(self):
        return self.git(self.remote, "ls-tree", "-r", "--name-only", "main").splitlines()

    def pre_push(self, clone, command):
        # The competing writer runs only once, after A has generated its old-base commit.
        hook = clone / ".git" / "hooks" / "pre-push"
        marker = self.root / "competitor-ran"
        q = lambda path: shlex.quote(str(path).replace("\\", "/"))
        hook.write_text("#!/bin/sh\nset -eu\nunset GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE\n"
                        f"if [ ! -f {q(marker)} ]; then\n touch {q(marker)}\n {command}\nfi\n", encoding="utf-8", newline="\n")
        hook.chmod(0o755)

    def assert_success(self, result):
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_rejected_push_rebuilds_two_issues_and_the_shared_index(self):
        """Replaying/rebasing A's stale index would lose B or conflict instead of rerunning ingest."""
        event_b = self.root / "b-event.json"
        event_b.write_text(json.dumps({"issue": self.issue(22, "Beta")}), encoding="utf-8")
        args = [sys.executable, str(HELPER), "--repo", str(self.b), "--mode", "issue", "--event", str(event_b),
                "--output-dir", str(self.root / "b-result"), "--backoff", "0"]
        self.pre_push(self.a, " ".join(shlex.quote(arg.replace("\\", "/")) for arg in args))
        result, output = self.invoke(self.a, self.issue())
        self.assert_success(result)
        self.assertEqual(set(self.remote_json("uit-packs/index.json")["plugins"]), {"Alpha", "Beta"})
        paths = self.remote_paths()
        self.assertTrue(any(p.startswith("docs/contributions/inbox/uit-11-") for p in paths))
        self.assertTrue(any(p.startswith("docs/contributions/inbox/uit-22-") for p in paths))
        self.assertEqual(json.loads((output / "result.json").read_text())["attempts"], 2)
        self.assertEqual(len(list((output / "issues").glob("*.json"))), 1)

    def test_direct_retry_scans_new_arrivals_instead_of_replaying_old_output(self):
        """Keeping A's generated files would omit a direct payload that arrived before retry."""
        inbox = self.a / "docs" / "contributions" / "inbox"
        inbox.mkdir(parents=True)
        (inbox / "uit-direct-alpha.json").write_text(json.dumps(self.payload("Alpha")), encoding="utf-8")
        self.commit(self.a, "direct Alpha")
        self.git(self.a, "push", "origin", "HEAD:main")
        self.git(self.b, "pull", "--ff-only")
        beta = self.b / "docs" / "contributions" / "inbox" / "uit-direct-beta.json"
        beta.write_text(json.dumps(self.payload("Beta")), encoding="utf-8")
        self.commit(self.b, "concurrent direct Beta")
        q = lambda text: shlex.quote(str(text).replace("\\", "/"))
        self.pre_push(self.a, f"git -C {q(self.b)} push origin HEAD:main")
        result, output = self.invoke(self.a)
        self.assert_success(result)
        self.assertEqual(set(self.remote_json("uit-packs/index.json")["plugins"]), {"Alpha", "Beta"})
        self.assertTrue(self.remote_json("docs/contributions/inbox/uit-direct-alpha.json").get("importedAt"))
        self.assertTrue(self.remote_json("docs/contributions/inbox/uit-direct-beta.json").get("importedAt"))
        self.assertEqual(len(list((output / "reviews").glob("*.json"))), 2)

    def test_repeating_a_persisted_issue_is_idempotent(self):
        """Without a per-issue content ledger recovery could rearchive and renotify an unchanged issue."""
        first, _ = self.invoke(self.a, self.issue(), name="first")
        self.assert_success(first)
        before = self.git(self.remote, "rev-parse", "main")
        second, output = self.invoke(self.a, self.issue(), name="second")
        self.assert_success(second)
        self.assertEqual(self.git(self.remote, "rev-parse", "main"), before)
        self.assertFalse(list((output / "issues").glob("*.json")))

    def test_recovery_of_an_existing_pack_records_ledger_without_renotifying(self):
        first, _ = self.invoke(self.a, self.issue(), name="first")
        self.assert_success(first)
        # Another issue containing already persisted content is a duplicate even
        # though this issue itself has no ledger entry yet (old workflow history).
        duplicate, output = self.invoke(self.a, self.issue(22), name="duplicate")
        self.assert_success(duplicate)
        self.assertTrue(self.remote_json("docs/contributions/inbox/.processed-issues/22.json"))
        self.assertFalse(list((output / "issues").glob("*.json")))
        self.assertFalse(list((output / "reviews").glob("*.json")))

    def test_legacy_issue_replay_does_not_create_another_archive(self):
        issue = self.issue()
        issue["body"] = "```json\n" + json.dumps({"contributions": [{"InternalName": "Alpha",
            "Field": "Name", "Original": "Alpha", "Translated": "阿尔法"}]}) + "\n```"
        first, _ = self.invoke(self.a, issue, name="first")
        self.assert_success(first)
        before = self.git(self.remote, "rev-parse", "main")
        second, output = self.invoke(self.a, issue, name="second")
        self.assert_success(second)
        self.assertEqual(self.git(self.remote, "rev-parse", "main"), before)
        self.assertEqual(len([p for p in self.remote_paths() if p.startswith("docs/contributions/inbox/11-")]), 1)
        self.assertEqual([str(p.relative_to(output)).replace("\\", "/")
                          for p in output.rglob("*") if p.is_file()], ["resolved-issues/11.json"])

    def test_documented_legacy_title_and_body_marker_are_ingested(self):
        issue = self.issue()
        issue["title"] = "翻译贡献 2026-09-23 12:00（1 条）"
        issue["body"] = "### FireGaze 翻译贡献\n\n```json\n" + json.dumps({"contributions": [
            {"InternalName": "Alpha", "Field": "Name", "Original": "Alpha", "Translated": "阿尔法"}
        ]}) + "\n```"
        result, output = self.invoke(self.a, issue)
        self.assert_success(result)
        self.assertEqual(len([p for p in self.remote_paths() if p.startswith("docs/contributions/inbox/11-")]), 1)
        self.assertTrue(self.remote_json("docs/contributions/inbox/.processed-issues/11.json"))
        self.assertEqual(len(list((output / "issues").glob("*.json"))), 1)

    def test_legacy_title_without_the_documented_body_marker_is_ignored(self):
        issue = self.issue()
        issue["title"] = "翻译贡献 2026-09-23 12:00（1 条）"
        issue["body"] = "```json\n" + json.dumps({"contributions": [
            {"InternalName": "Alpha", "Field": "Name", "Original": "Alpha", "Translated": "阿尔法"}
        ]}) + "\n```"
        before = self.git(self.remote, "rev-parse", "main")
        result, output = self.invoke(self.a, issue)
        self.assert_success(result)
        self.assertEqual(self.git(self.remote, "rev-parse", "main"), before)
        self.assertFalse(output.exists())

    def test_identical_existing_base_pack_is_reconciled_without_new_pack_or_comment(self):
        pack = self.payload("Alpha")
        (self.a / "uit-packs/Alpha.json").write_text(json.dumps(pack), encoding="utf-8")
        self.commit(self.a, "pre-ledger imported base pack")
        self.git(self.a, "push", "origin", "HEAD:main")
        result, output = self.invoke(self.a, self.issue())
        self.assert_success(result)
        self.assertTrue(self.remote_json("docs/contributions/inbox/.processed-issues/11.json"))
        self.assertFalse(any(p.startswith("uit-packs/Alpha@") for p in self.remote_paths()))
        self.assertFalse(list((output / "issues").glob("*.json")))
        self.assertFalse(list((output / "reviews").glob("*.json")))

    def test_partial_existing_pack_does_not_hide_a_missing_translation(self):
        (self.a / "uit-packs/Alpha.json").write_text('{"entries":[],"resources":[],"attributes":[]}', encoding="utf-8")
        self.commit(self.a, "partial existing base")
        self.git(self.a, "push", "origin", "HEAD:main")
        result, output = self.invoke(self.a, self.issue())
        self.assert_success(result)
        self.assertTrue(any(p.startswith("uit-packs/Alpha@") for p in self.remote_paths()))
        self.assertEqual(len(list((output / "issues").glob("*.json"))), 1)

    def serve_issues(self, issues, *, status=200):
        class Handler(BaseHTTPRequestHandler):
            def do_GET(handler):
                encoded = json.dumps(issues).encode("utf-8")
                handler.send_response(status)
                handler.send_header("Content-Type", "application/json")
                handler.end_headers()
                handler.wfile.write(encoded)

            def log_message(self, *_args):
                pass

        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        return f"http://127.0.0.1:{server.server_port}"

    def recover(self, issues, *, max_pages=5, status=200):
        environment = dict(os.environ, GITHUB_REPOSITORY="fixture/repository",
                           GITHUB_API_URL=self.serve_issues(issues, status=status))
        # Never forward a real token to the local test server.
        environment.pop("GH_TOKEN", None)
        environment.pop("GITHUB_TOKEN", None)
        output = self.root / "recovered"
        result = subprocess.run([sys.executable, str(HELPER), "--repo", str(self.a), "--mode", "recover",
                                 "--output-dir", str(output), "--max-pages", str(max_pages), "--backoff", "0"],
                                capture_output=True, text=True, encoding="utf-8", env=environment)
        return result, output

    def test_recovery_continues_independent_issues_and_direct_files_but_reports_invalid_issue(self):
        inbox = self.a / "docs/contributions/inbox"
        inbox.mkdir(parents=True)
        (inbox / "uit-direct-beta.json").write_text(json.dumps(self.payload("Beta")), encoding="utf-8")
        self.commit(self.a, "unimported direct payload")
        self.git(self.a, "push", "origin", "HEAD:main")
        broken = self.issue(22, "Broken")
        broken["body"] = "```json\n{broken}\n```"
        ordinary = self.issue(33, "Ordinary")
        ordinary["title"] = "[收稿] Ordinary"
        result, output = self.recover([broken, ordinary, self.issue()])
        self.assert_success(result)
        self.assertEqual(set(self.remote_json("uit-packs/index.json")["plugins"]), {"Alpha", "Beta"})
        self.assertTrue(self.remote_json("docs/contributions/inbox/uit-direct-beta.json").get("importedAt"))
        self.assertFalse(any(p.endswith(".processed-issues/22.json") for p in self.remote_paths()))
        self.assertEqual(len(list((output / "issues").glob("*.json"))), 1)
        rejected = json.loads((output / "invalid-issues/22.json").read_text(encoding="utf-8"))
        self.assertEqual(rejected["status"], "needs-input")
        self.assertIn("Issue #22", result.stdout)

    def test_recovery_fails_visibly_when_pagination_limit_is_reached(self):
        ordinary = self.issue()
        ordinary["title"] = "[收稿] ignored"
        result, output = self.recover([ordinary] * 100, max_pages=1)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("page limit", result.stderr)
        self.assertFalse(output.exists())

    def test_exhausted_push_publishes_no_success_comment_or_review(self):
        """Publishing attempt outputs before push would falsely announce an unpersisted submission."""
        hook = self.remote / "hooks" / "pre-receive"
        hook.write_text("#!/bin/sh\nexit 1\n", encoding="utf-8", newline="\n")
        hook.chmod(0o755)
        before = self.git(self.remote, "rev-parse", "main")
        result, output = self.invoke(self.a, self.issue(), attempts=2)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.git(self.remote, "rev-parse", "main"), before)
        self.assertFalse(output.exists(), "Failed transaction must not publish any success outbox")

    def test_malformed_contribution_does_not_record_a_processed_ledger(self):
        """Input rejection must stay outside the persisted-contribution ledger and success outbox."""
        issue = self.issue()
        issue["body"] = "```json\n{broken}\n```"
        before = self.git(self.remote, "rev-parse", "main")
        result, output = self.invoke(self.a, issue)
        self.assert_success(result)
        self.assertEqual(self.git(self.remote, "rev-parse", "main"), before)
        rejected = json.loads((output / "invalid-issues/11.json").read_text(encoding="utf-8"))
        self.assertEqual(rejected["status"], "needs-input")
        self.assertEqual(rejected["bodySha256"], hashlib.sha256(issue["body"].encode("utf-8")).hexdigest())
        self.assertFalse(list((output / "issues").glob("*.json")))
        self.assertFalse(list((output / "reviews").glob("*.json")))
        self.assertFalse((output / "result.json").exists())

    def test_oversized_export_template_requests_json_without_publishing_the_local_path(self):
        """The #230 export-template shape is actionable input rejection, not an infrastructure failure."""
        issue = self.issue(230, "RotationSolver")
        issue["body"] = (
            "### FireGaze contributions · 插件界面文字译文贡献\n\n"
            "- 插件：`RotationSolver`\n- 条数：3157\n\n"
            "正文较长，放不进提交页链接；投稿内容已经导出成文件，请把**文件内容**（含 JSON 代码块）整段粘贴到这里再提交。\n\n"
            r"> 文件：`C:\Users\fixture\AppData\Roaming\XIVLauncherCN\pluginConfigs\FireGaze\contributions\uit-RotationSolver.json`"
        )
        before = self.git(self.remote, "rev-parse", "main")
        result, output = self.invoke(self.a, issue)
        self.assert_success(result)
        self.assertEqual(self.git(self.remote, "rev-parse", "main"), before)
        rejected_text = (output / "invalid-issues/230.json").read_text(encoding="utf-8")
        rejected = json.loads(rejected_text)
        self.assertEqual(rejected["issue_number"], 230)
        self.assertEqual(rejected["status"], "needs-input")
        self.assertIn("JSON", rejected["comment"])
        self.assertNotIn("fixture", rejected_text)
        self.assertNotIn("AppData", rejected_text)
        self.assertNotIn("RotationSolver", rejected_text)
        self.assertNotIn("body", set(rejected))
        self.assertFalse((output / "result.json").exists())
        self.assertFalse(any(p.endswith(".processed-issues/230.json") for p in self.remote_paths()))

    def test_invalid_issue_repeat_is_stable_and_edited_json_can_be_ingested(self):
        """A rejection must neither masquerade as success nor prevent a later corrected edit."""
        issue = self.issue()
        valid_body = issue["body"]
        issue["body"] = "No JSON has been supplied yet."
        before = self.git(self.remote, "rev-parse", "main")
        first, first_out = self.invoke(self.a, issue, name="invalid-first")
        second, second_out = self.invoke(self.a, issue, name="invalid-second")
        self.assert_success(first)
        self.assert_success(second)
        self.assertEqual(self.git(self.remote, "rev-parse", "main"), before)
        self.assertEqual((first_out / "invalid-issues/11.json").read_bytes(),
                         (second_out / "invalid-issues/11.json").read_bytes())
        for output in (first_out, second_out):
            self.assertFalse((output / "result.json").exists())
            self.assertFalse(list((output / "issues").glob("*.json")))
        issue["body"] = valid_body
        corrected, output = self.invoke(self.a, issue, name="corrected")
        self.assert_success(corrected)
        self.assertEqual(set(self.remote_json("uit-packs/index.json")["plugins"]), {"Alpha"})
        self.assertTrue(self.remote_json("docs/contributions/inbox/.processed-issues/11.json"))
        self.assertTrue((output / "issues/11.json").exists())
        self.assertFalse((output / "invalid-issues").exists())

    def test_invalid_entry_container_is_rejected_before_git_or_ingestion(self):
        """Malformed user containers should not become subprocess or repository errors."""
        issue = self.issue()
        payload = self.payload("Alpha")
        payload["entries"] = "not a list"
        issue["body"] = json.dumps(payload)
        before = self.git(self.remote, "rev-parse", "main")
        result, output = self.invoke(self.a, issue)
        self.assert_success(result)
        self.assertEqual(self.git(self.remote, "rev-parse", "main"), before)
        self.assertEqual(json.loads((output / "invalid-issues/11.json").read_text(encoding="utf-8"))["status"], "needs-input")

    def test_corrected_duplicate_pack_and_persisted_ledger_emit_only_resolution(self):
        """A corrected duplicate must clear old input guidance without inventing a new contribution."""
        (self.a / "uit-packs/Alpha.json").write_text(json.dumps(self.payload("Alpha")), encoding="utf-8")
        self.commit(self.a, "already persisted base pack")
        self.git(self.a, "push", "origin", "HEAD:main")
        issue = self.issue()
        valid_body = issue["body"]
        issue["body"] = "Please read my exported file."
        rejected, _ = self.invoke(self.a, issue, name="duplicate-invalid")
        self.assert_success(rejected)
        issue["body"] = valid_body
        corrected, first_out = self.invoke(self.a, issue, name="duplicate-corrected")
        self.assert_success(corrected)
        self.assertTrue(self.remote_json("docs/contributions/inbox/.processed-issues/11.json"))
        before = self.git(self.remote, "rev-parse", "main")
        replay, replay_out = self.invoke(self.a, issue, name="duplicate-replayed")
        self.assert_success(replay)
        self.assertEqual(self.git(self.remote, "rev-parse", "main"), before)
        for output in (first_out, replay_out):
            resolved_path = output / "resolved-issues/11.json"
            self.assertTrue(resolved_path.exists(), "A durable duplicate needs a resolution outbox")
            resolved = json.loads(resolved_path.read_text(encoding="utf-8"))
            self.assertEqual(resolved, {"issue_number": 11,
                             "bodySha256": hashlib.sha256(valid_body.encode("utf-8")).hexdigest(),
                             "status": "processed"})
            self.assertFalse(list((output / "issues").glob("*.json")))
            self.assertFalse(list((output / "reviews").glob("*.json")))
            self.assertFalse((output / "summary.txt").exists())
        self.assertFalse((replay_out / "result.json").exists())

    def test_duplicate_pack_push_failure_publishes_no_resolution(self):
        """An unpersisted duplicate ledger must not be exposed as a resolved issue."""
        (self.a / "uit-packs/Alpha.json").write_text(json.dumps(self.payload("Alpha")), encoding="utf-8")
        self.commit(self.a, "already persisted base pack")
        self.git(self.a, "push", "origin", "HEAD:main")
        hook = self.remote / "hooks" / "pre-receive"
        hook.write_text("#!/bin/sh\nexit 1\n", encoding="utf-8", newline="\n")
        hook.chmod(0o755)
        before = self.git(self.remote, "rev-parse", "main")
        result, output = self.invoke(self.a, self.issue(), attempts=1)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.git(self.remote, "rev-parse", "main"), before)
        self.assertFalse(output.exists())
        self.assertFalse(any(p.endswith(".processed-issues/11.json") for p in self.remote_paths()))

    def test_missing_or_invalid_uit_plugin_is_needs_input_without_processed_ledger(self):
        """Missing/unsafe plugin identity must not become a silently processed valid issue."""
        before = self.git(self.remote, "rev-parse", "main")
        for index, plugin in enumerate((None, "bad/name", "A" * 121), start=40):
            with self.subTest(plugin=plugin):
                issue = self.issue(index)
                payload = self.payload("Alpha")
                if plugin is None:
                    payload.pop("plugin")
                else:
                    payload["plugin"] = plugin
                issue["body"] = json.dumps(payload)
                result, output = self.invoke(self.a, issue, name=f"invalid-plugin-{index}")
                self.assert_success(result)
                self.assertEqual(self.git(self.remote, "rev-parse", "main"), before)
                rejection = json.loads((output / f"invalid-issues/{index}.json").read_text(encoding="utf-8"))
                self.assertEqual(rejection["status"], "needs-input")
                self.assertFalse((output / "result.json").exists())
                self.assertFalse((output / "resolved-issues").exists())
                self.assertFalse(any(p.endswith(f".processed-issues/{index}.json") for p in self.remote_paths()))

    def test_repository_json_corruption_is_not_misclassified_as_user_input(self):
        """Catching every ValueError would hide a broken persisted pack as an ordinary rejection."""
        (self.a / "uit-packs/Alpha.json").write_text("{broken}", encoding="utf-8")
        self.commit(self.a, "corrupted repository pack")
        self.git(self.a, "push", "origin", "HEAD:main")
        before = self.git(self.remote, "rev-parse", "main")
        result, output = self.invoke(self.a, self.issue())
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.git(self.remote, "rev-parse", "main"), before)
        self.assertFalse(output.exists())

    def test_corrupted_processed_ledger_publishes_no_resolution(self):
        """An unreadable persisted ledger cannot certify that an issue was processed."""
        ledger = self.a / "docs/contributions/inbox/.processed-issues/11.json"
        ledger.parent.mkdir(parents=True)
        ledger.write_text("{broken}", encoding="utf-8")
        self.commit(self.a, "corrupted processed ledger")
        self.git(self.a, "push", "origin", "HEAD:main")
        before = self.git(self.remote, "rev-parse", "main")
        result, output = self.invoke(self.a, self.issue())
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.git(self.remote, "rev-parse", "main"), before)
        self.assertFalse(output.exists())

    def test_git_fetch_failure_is_not_misclassified_as_user_input(self):
        """A valid contribution still fails visibly when its authoritative remote cannot be fetched."""
        self.git(self.a, "remote", "set-url", "origin", str(self.root / "missing.git"))
        result, output = self.invoke(self.a, self.issue())
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("git failed", result.stderr)
        self.assertFalse(output.exists())

    def test_recovery_api_http_failure_remains_a_workflow_failure(self):
        """An unavailable Issues API must not turn into a green run with no contributions."""
        before = self.git(self.remote, "rev-parse", "main")
        result, output = self.recover([], status=503)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("503", result.stderr)
        self.assertEqual(self.git(self.remote, "rev-parse", "main"), before)
        self.assertFalse(output.exists())

    def test_ordinary_issue_with_a_payload_is_not_ingested(self):
        """Accepting any JSON issue would process unrelated reports and review issues."""
        issue = self.issue()
        issue["title"] = "[反馈] an ordinary issue"
        before = self.git(self.remote, "rev-parse", "main")
        result, output = self.invoke(self.a, issue)
        self.assert_success(result)
        self.assertEqual(self.git(self.remote, "rev-parse", "main"), before)
        self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
