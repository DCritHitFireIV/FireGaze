#!/usr/bin/env python3
"""Recompute download statistics on current main when another writer wins the push.

Only the two statistics files are committed. A failed push is retried only after
a fresh fetch proves that the target branch changed; policy/API/Git failures
remain failures. The caller's checkout is never reset or rebased.
"""
from __future__ import annotations

import argparse
from datetime import datetime
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time
import urllib.parse
import urllib.request


OUTPUTS = ("pluginmaster.json", "Resources/download-count.json")


def run(directory: Path, *command: str, check: bool = True) -> subprocess.CompletedProcess:
    result = subprocess.run(command, cwd=directory, capture_output=True, text=True,
                            encoding="utf-8", timeout=120)
    if result.stdout:
        print(result.stdout.rstrip(), flush=True)
    if result.stderr:
        print(result.stderr.rstrip(), file=sys.stderr, flush=True)
    if check and result.returncode:
        raise RuntimeError(f"{command[0]} failed (exit {result.returncode})")
    return result


def release_statistics(repository: str) -> tuple[int, int]:
    api = os.environ.get("GITHUB_API_URL", "https://api.github.com").rstrip("/")
    headers = {"Accept": "application/vnd.github+json", "User-Agent": "FireGaze-download-statistics"}
    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    if token:
        headers["Authorization"] = "Bearer " + token
    total, latest_update = 0, 0
    page = 1
    while True:
        query = urllib.parse.urlencode({"per_page": 100, "page": page})
        request = urllib.request.Request(f"{api}/repos/{repository}/releases?{query}", headers=headers)
        with urllib.request.urlopen(request, timeout=30) as response:
            releases = json.load(response)
        if not isinstance(releases, list):
            raise ValueError("GitHub Releases API returned an invalid response")
        if not releases:
            return total, latest_update
        for release in releases:
            for asset in release.get("assets", []):
                total += int(asset.get("download_count") or 0)
            published = release.get("published_at")
            if published:
                latest_update = max(latest_update, int(datetime.fromisoformat(published.replace("Z", "+00:00")).timestamp()))
        page += 1


def update_statistics(directory: Path, total: int, latest_update: int) -> None:
    master_path = directory / OUTPUTS[0]
    master = json.loads(master_path.read_text(encoding="utf-8"))
    if not isinstance(master, list) or any(not isinstance(item, dict) for item in master):
        raise ValueError("pluginmaster.json must contain a list of plugin manifests")
    changed = False
    for item in master:
        if item.get("DownloadCount") != total:
            item["DownloadCount"] = total
            changed = True
        if latest_update and item.get("LastUpdate") != latest_update:
            item["LastUpdate"] = latest_update
            changed = True
    if changed:
        master_path.write_text(json.dumps(master, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    badge_path = directory / OUTPUTS[1]
    badge = {"schemaVersion": 1, "label": "下载", "message": f"{total:,}", "color": "blue"}
    badge_text = json.dumps(badge, ensure_ascii=False, indent=2) + "\n"
    if not badge_path.exists() or badge_path.read_text(encoding="utf-8") != badge_text:
        badge_path.parent.mkdir(parents=True, exist_ok=True)
        badge_path.write_text(badge_text, encoding="utf-8")
    print(f"total downloads = {total}, latest update = {latest_update}", flush=True)


def publish(args) -> None:
    repo = args.repo.resolve()
    target = "refs/heads/" + args.branch
    tracking = "refs/remotes/origin/" + args.branch
    fetch = ("git", "fetch", "--no-tags", "origin", f"+{target}:{tracking}")
    run(repo, "git", "check-ref-format", target)
    for attempt in range(1, args.max_attempts + 1):
        run(repo, *fetch)
        base = run(repo, "git", "rev-parse", tracking).stdout.strip()
        with tempfile.TemporaryDirectory(prefix="firegaze-download-count-") as temporary:
            worktree = Path(temporary) / "checkout"
            run(repo, "git", "worktree", "add", "--detach", str(worktree), base)
            try:
                total, latest_update = release_statistics(args.repository)
                update_statistics(worktree, total, latest_update)
                run(worktree, "git", "add", "--", *OUTPUTS)
                changed = run(worktree, "git", "diff", "--cached", "--quiet", check=False)
                if changed.returncode == 0:
                    print(f"status=unchanged attempt={attempt}", flush=True)
                    return
                if changed.returncode != 1:
                    raise RuntimeError("Could not inspect staged statistics")
                run(worktree, "git", "-c", "user.name=github-actions[bot]", "-c",
                    "user.email=41898282+github-actions[bot]@users.noreply.github.com",
                    "commit", "-m", "chore: update download count")
                commit = run(worktree, "git", "rev-parse", "HEAD").stdout.strip()
                pushed = run(worktree, "git", "push", "--porcelain", "origin", f"HEAD:{target}", check=False)
                if pushed.returncode == 0:
                    print(f"status=updated attempt={attempt}", flush=True)
                    return
                # A pre-push race can appear as either local rejection or remote
                # compare-and-swap failure. Inspect the branch, not error wording.
                run(repo, *fetch)
                current = run(repo, "git", "rev-parse", tracking).stdout.strip()
                persisted = run(repo, "git", "merge-base", "--is-ancestor", commit, current, check=False)
                if persisted.returncode == 0:
                    print(f"status=updated attempt={attempt} verified-after-push-error", flush=True)
                    return
                if persisted.returncode != 1:
                    raise RuntimeError("Could not verify statistics publication after the failed push")
                if current == base:
                    raise RuntimeError("Statistics push failed while the remote branch was unchanged")
            finally:
                run(repo, "git", "worktree", "remove", "--force", str(worktree))
        if attempt < args.max_attempts:
            print(f"Remote branch changed; refetching and recomputing statistics (attempt {attempt + 1})", flush=True)
            time.sleep(min(args.backoff * 2 ** (attempt - 1), 30))
    raise RuntimeError(f"Could not persist statistics after {args.max_attempts} attempts")


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path.cwd())
    parser.add_argument("--repository", default=os.environ.get("GITHUB_REPOSITORY", ""))
    parser.add_argument("--branch", default="main")
    parser.add_argument("--max-attempts", type=int, default=5)
    parser.add_argument("--backoff", type=float, default=1)
    args = parser.parse_args(argv)
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", args.repository):
        parser.error("GITHUB_REPOSITORY must identify the statistics repository")
    if args.max_attempts < 1 or args.backoff < 0:
        parser.error("attempts must be positive and backoff must be nonnegative")
    publish(args)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, RuntimeError, KeyError, TypeError, subprocess.TimeoutExpired) as error:
        print(error, file=sys.stderr)
        sys.exit(1)
