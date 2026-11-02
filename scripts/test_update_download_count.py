"""Statistics publication against real local Git repositories and a local Releases API."""
from __future__ import annotations

from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import importlib.util
import io
import json
import os
from pathlib import Path
import shlex
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest import mock
from contextlib import redirect_stdout, redirect_stderr
from urllib.parse import parse_qs, urlparse


HELPER = Path(__file__).resolve().with_name("update_download_count.py")


class DownloadCountTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="firegaze stats git ")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.remote = self.root / "remote.git"
        self.git(self.root, "init", "--bare", "--initial-branch=main", str(self.remote))
        self.a = self.root / "clone a"
        self.b = self.root / "clone b"
        self.git(self.root, "clone", str(self.remote), str(self.a))
        (self.a / "Resources").mkdir()
        self.write_master(self.a, {"InternalName": "FireGaze", "Author": "original", "DownloadCount": 0,
                                   "LastUpdate": 1, "AssemblyVersion": "1.5.0.100"})
        (self.a / "Resources/download-count.json").write_text("{}\n", encoding="utf-8")
        (self.a / "unrelated.txt").write_text("original\n", encoding="utf-8")
        self.commit(self.a, "seed")
        self.git(self.a, "push", "origin", "HEAD:main")
        self.git(self.root, "clone", str(self.remote), str(self.b))
        self.api_data = self.root / "api.json"
        self.write_api([{"published_at": "2020-01-02T00:00:00Z", "assets": [{"download_count": 10}]}])
        self.api_status = 200
        self.api_calls = []
        fixture = self

        class Handler(BaseHTTPRequestHandler):
            def do_GET(handler):
                fixture.api_calls.append(handler.path)
                if handler.path.split("?")[0] != "/repos/fixture/repository/releases":
                    handler.send_error(404)
                    return
                page = int(parse_qs(urlparse(handler.path).query).get("page", ["1"])[0])
                pages = json.loads(fixture.api_data.read_text(encoding="utf-8"))
                body = json.dumps(pages.get(str(page), [])).encode("utf-8")
                handler.send_response(fixture.api_status)
                handler.send_header("Content-Type", "application/json")
                handler.end_headers()
                handler.wfile.write(body)

            def log_message(self, *_args):
                pass

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(self.server.server_close)
        self.addCleanup(self.server.shutdown)

    def git(self, directory, *arguments):
        result = subprocess.run(["git", "-C", str(directory), *arguments], capture_output=True,
                                text=True, encoding="utf-8", timeout=30)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        return result.stdout.strip()

    def commit(self, directory, message):
        self.git(directory, "add", ".")
        self.git(directory, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.test",
                 "commit", "-m", message)

    def write_master(self, directory, item):
        (directory / "pluginmaster.json").write_text(json.dumps([item], indent=2) + "\n", encoding="utf-8")

    def write_api(self, releases):
        self.api_data.write_text(json.dumps({"1": releases}), encoding="utf-8")

    def invoke(self, *, branch="main", attempts=3):
        environment = dict(os.environ, GITHUB_REPOSITORY="fixture/repository",
                           GITHUB_API_URL=f"http://127.0.0.1:{self.server.server_port}", PYTHONIOENCODING="utf-8")
        environment.pop("GH_TOKEN", None)
        environment.pop("GITHUB_TOKEN", None)
        return subprocess.run([sys.executable, str(HELPER), "--repo", str(self.a), "--branch", branch,
                               "--max-attempts", str(attempts), "--backoff", "0"],
                              capture_output=True, text=True, encoding="utf-8", env=environment, timeout=60)

    def remote_json(self, path, *, branch="main"):
        return json.loads(self.git(self.remote, "show", branch + ":" + path))

    @staticmethod
    def quote(value):
        return shlex.quote(str(value).replace("\\", "/"))

    def pre_push(self, command, *, once=True):
        marker = self.root / "competitor-ran"
        hook = self.a / ".git/hooks/pre-push"
        body = "#!/bin/sh\nset -eu\nunset GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE\n"
        if once:
            body += f"if [ ! -f {self.quote(marker)} ]; then\n touch {self.quote(marker)}\n {command}\nfi\n"
        else:
            body += command + "\n"
        hook.write_text(body, encoding="utf-8", newline="\n")
        hook.chmod(0o755)

    def assert_success(self, result):
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_concurrent_metadata_is_preserved_and_statistics_are_recomputed(self):
        self.write_master(self.b, {"InternalName": "FireGaze", "Author": "concurrent-author", "DownloadCount": 0,
                                   "LastUpdate": 1, "AssemblyVersion": "1.5.0.200"})
        self.commit(self.b, "new version metadata")
        next_api = self.root / "next-api.json"
        next_api.write_text(json.dumps({"1": [{"published_at": "2020-01-02T00:00:00Z",
                                               "assets": [{"download_count": 42}]}]}), encoding="utf-8")
        self.pre_push(f"cp {self.quote(next_api)} {self.quote(self.api_data)}\n"
                      f"git -C {self.quote(self.b)} push origin HEAD:main")
        result = self.invoke()
        self.assert_success(result)
        self.assertEqual(self.remote_json("pluginmaster.json"), [{"InternalName": "FireGaze",
            "Author": "concurrent-author", "DownloadCount": 42, "LastUpdate": 1577923200,
            "AssemblyVersion": "1.5.0.200"}])
        self.assertEqual(self.remote_json("Resources/download-count.json")["message"], "42")
        self.assertIn("attempt=2", result.stdout)
        self.assertEqual(self.git(self.remote, "diff-tree", "--no-commit-id", "--name-only", "-r", "main").splitlines(),
                         ["Resources/download-count.json", "pluginmaster.json"])

    def test_retry_can_become_noop_after_another_writer_persists_the_same_statistics(self):
        self.write_master(self.b, {"InternalName": "FireGaze", "Author": "concurrent-author", "DownloadCount": 10,
                                   "LastUpdate": 1577923200, "AssemblyVersion": "1.5.0.200"})
        (self.b / "Resources/download-count.json").write_text(json.dumps({"schemaVersion": 1, "label": "下载",
            "message": "10", "color": "blue"}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        self.commit(self.b, "already current statistics")
        wanted = self.git(self.b, "rev-parse", "HEAD")
        self.pre_push(f"git -C {self.quote(self.b)} push origin HEAD:main")
        result = self.invoke()
        self.assert_success(result)
        self.assertEqual(self.git(self.remote, "rev-parse", "main"), wanted)
        self.assertIn("status=unchanged attempt=2", result.stdout)

    def test_remote_policy_rejection_fails_without_retrying_unchanged_main(self):
        marker = self.root / "rejections"
        hook = self.remote / "hooks/pre-receive"
        hook.write_text(f"#!/bin/sh\necho rejected >> {self.quote(marker)}\nexit 1\n", encoding="utf-8", newline="\n")
        hook.chmod(0o755)
        before = self.git(self.remote, "rev-parse", "main")
        result = self.invoke()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.git(self.remote, "rev-parse", "main"), before)
        self.assertEqual(marker.read_text().splitlines(), ["rejected"])
        self.assertNotIn("status=updated", result.stdout)

    def test_lost_push_acknowledgement_is_confirmed_from_the_real_remote_commit(self):
        spec = importlib.util.spec_from_file_location("download_statistics_lost_ack", HELPER)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        real_run = module.run

        def lost_ack(directory, *command, **kwargs):
            result = real_run(directory, *command, **kwargs)
            if command[:2] == ("git", "push") and result.returncode == 0:
                # The real Git operation persists its commit; only its transport
                # acknowledgement is lost. Recovery must inspect the real remote.
                return subprocess.CompletedProcess(result.args, 1, result.stdout, "connection closed after push")
            return result

        environment = dict(os.environ, GITHUB_REPOSITORY="fixture/repository",
                           GITHUB_API_URL=f"http://127.0.0.1:{self.server.server_port}")
        environment.pop("GH_TOKEN", None)
        environment.pop("GITHUB_TOKEN", None)
        output = io.StringIO()
        with mock.patch.dict(os.environ, environment, clear=True), mock.patch.object(module, "run", side_effect=lost_ack):
            with redirect_stdout(output), redirect_stderr(io.StringIO()):
                self.assertEqual(module.main(["--repo", str(self.a), "--max-attempts", "2", "--backoff", "0"]), 0)
        self.assertEqual(self.remote_json("pluginmaster.json")[0]["DownloadCount"], 10)
        self.assertIn("status=updated attempt=1 verified-after-push-error", output.getvalue())
        self.assertEqual(self.git(self.remote, "rev-list", "--count", "main"), "2")

    def test_competing_writers_are_bounded_and_never_force_pushed(self):
        counter = self.root / "races"
        compete = self.root / "compete.py"
        compete.write_text(
            "import json,pathlib,subprocess,sys\n"
            "repo=pathlib.Path(sys.argv[1]); counter=pathlib.Path(sys.argv[2])\n"
            "def git(*args): subprocess.run(['git','-C',str(repo),*args],check=True)\n"
            "git('fetch','origin','main'); git('reset','--hard','origin/main')\n"
            "n=int(counter.read_text())+1 if counter.exists() else 1; counter.write_text(str(n))\n"
            "p=repo/'pluginmaster.json'; data=json.loads(p.read_text()); data[0]['Author']='writer-'+str(n)\n"
            "p.write_text(json.dumps(data,indent=2)+'\\n'); git('add','pluginmaster.json')\n"
            "git('-c','user.name=Fixture','-c','user.email=fixture@example.test','commit','-m','competing metadata')\n"
            "git('push','origin','HEAD:main')\n", encoding="utf-8")
        self.pre_push(" ".join(self.quote(x) for x in [sys.executable, compete, self.b, counter]), once=False)
        result = self.invoke(attempts=2)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(counter.read_text(), "2")
        self.assertEqual(self.remote_json("pluginmaster.json")[0]["DownloadCount"], 0)
        self.assertEqual(self.remote_json("pluginmaster.json")[0]["Author"], "writer-2")

    def test_release_pagination_totals_all_assets_and_uses_latest_publication(self):
        self.api_data.write_text(json.dumps({"1": [
            {"published_at": "2020-01-01T00:00:00Z", "assets": [{"download_count": 1001}, {"download_count": 2}]},
            {"published_at": None, "assets": [{"download_count": None}]}],
            "2": [{"published_at": "2020-01-02T00:00:00Z", "assets": [{"download_count": 1000}]}]}), encoding="utf-8")
        result = self.invoke()
        self.assert_success(result)
        self.assertEqual(self.remote_json("pluginmaster.json")[0]["DownloadCount"], 2003)
        self.assertEqual(self.remote_json("pluginmaster.json")[0]["LastUpdate"], 1577923200)
        self.assertEqual(self.remote_json("Resources/download-count.json"),
                         {"schemaVersion": 1, "label": "下载", "message": "2,003", "color": "blue"})

    def test_api_failure_does_not_commit_or_report_success(self):
        self.api_status = 503
        before = self.git(self.remote, "rev-parse", "main")
        result = self.invoke()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.git(self.remote, "rev-parse", "main"), before)
        self.assertEqual(self.remote_json("pluginmaster.json")[0]["DownloadCount"], 0)

    def test_repeating_current_statistics_makes_no_commit(self):
        self.assert_success(self.invoke())
        before = self.git(self.remote, "rev-parse", "main")
        repeated = self.invoke()
        self.assert_success(repeated)
        self.assertEqual(self.git(self.remote, "rev-parse", "main"), before)
        self.assertIn("status=unchanged", repeated.stdout)

    def test_selected_default_branch_is_updated_without_touching_main(self):
        before = self.git(self.remote, "rev-parse", "main")
        self.git(self.a, "push", "origin", "HEAD:stable")
        result = self.invoke(branch="stable")
        self.assert_success(result)
        self.assertEqual(self.git(self.remote, "rev-parse", "main"), before)
        self.assertEqual(self.remote_json("pluginmaster.json", branch="stable")[0]["DownloadCount"], 10)

    def test_caller_changes_are_preserved_and_not_published(self):
        (self.a / "unrelated.txt").write_text("caller local edit\n", encoding="utf-8")
        (self.a / "untracked.txt").write_text("caller untracked\n", encoding="utf-8")
        result = self.invoke()
        self.assert_success(result)
        self.assertEqual((self.a / "unrelated.txt").read_text(), "caller local edit\n")
        self.assertEqual((self.a / "untracked.txt").read_text(), "caller untracked\n")
        self.assertEqual(self.git(self.remote, "show", "main:unrelated.txt"), "original")
        self.assertNotIn("untracked.txt", self.git(self.remote, "ls-tree", "-r", "--name-only", "main"))


if __name__ == "__main__":
    unittest.main()
