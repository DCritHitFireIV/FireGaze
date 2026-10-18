#!/usr/bin/env python3
"""Persist contribution ingestion against current main before publishing side effects.

A rejected push discards its generated files. The next attempt fetches main and
runs the existing ingestion scripts again, so shared indexes are never rebased
from a stale snapshot. No force pushes or queue-cancelling concurrency are used.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timedelta, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.parse
import urllib.request

from validate_contribution import extract_json
from inbox_uit import submission_fingerprint

INBOX = Path("docs/contributions/inbox")
LEDGER = INBOX / ".processed-issues"


def write_json(path: Path, data) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def run(directory: Path, *command: str, check: bool = True) -> subprocess.CompletedProcess:
    result = subprocess.run(command, cwd=directory, capture_output=True, text=True, encoding="utf-8")
    if result.stdout:
        print(result.stdout.rstrip(), flush=True)
    if result.stderr:
        print(result.stderr.rstrip(), file=sys.stderr, flush=True)
    if check and result.returncode:
        raise RuntimeError(f"{command[0]} failed (exit {result.returncode})")
    return result


def prepare_issue(issue: dict) -> tuple[int, str, dict] | None:
    title = str(issue.get("title") or "")
    body = str(issue.get("body") or "")
    legacy_title = title.startswith("翻译贡献 ") and "### FireGaze 翻译贡献" in body
    if (not title.startswith("[译文贡献]") and not legacy_title) or "pull_request" in issue:
        return None
    number = int(issue["number"])
    if number < 1:
        raise ValueError("Issue number must be positive")
    payload = extract_json(body)
    if not isinstance(payload, dict):
        raise ValueError(f"Issue #{number} has no valid JSON contribution")
    if legacy_title and (not isinstance(payload.get("contributions"), list) or not payload["contributions"]):
        return None
    if payload.get("type") == "uit-contribution":
        for key in ("entries", "resources", "attributes"):
            items = payload.get(key, [])
            if not isinstance(items, list) or any(not isinstance(item, dict) for item in items):
                raise ValueError(f"Issue #{number} has invalid {key}")
    elif not isinstance(payload.get("contributions"), list) or not payload["contributions"]:
        raise ValueError(f"Issue #{number} is not a contribution payload")
    return number, body, payload


def ingest_issue(worktree: Path, outbox: Path, issue: dict, prepared: tuple) -> None:
    number, body, payload = prepared
    fingerprint = hashlib.sha256(body.encode("utf-8")).hexdigest()
    ledger_path = worktree / LEDGER / f"{number}.json"
    if ledger_path.exists():
        previous = json.loads(ledger_path.read_text(encoding="utf-8"))
        if previous.get("bodySha256") == fingerprint:
            print(f"Issue #{number}: unchanged persisted contribution; skipping")
            return

    is_uit = payload.get("type") == "uit-contribution"
    if is_uit:
        plugin = str(payload.get("plugin") or "").strip()
        if re.fullmatch(r"[A-Za-z0-9_.\-]{1,120}", plugin):
            content_fingerprint = submission_fingerprint(plugin, payload)
            packs = [worktree / "uit-packs" / f"{plugin}.json"]
            packs.extend((worktree / "uit-packs").glob(f"{plugin}@*.json"))
            for pack in packs:
                if not pack.exists():
                    continue
                saved = json.loads(pack.read_text(encoding="utf-8"))
                # Reconcile only a complete content match. Partial coverage must
                # still ingest the submission, including its missing entries.
                if submission_fingerprint(plugin, saved) == content_fingerprint:
                    write_json(ledger_path, {"issue": number, "bodySha256": fingerprint})
                    return
    else:
        # Old legacy runs predate the ledger. Reconcile their existing archives
        # without rearchiving or sending old submissions as new notifications.
        for archive in (worktree / INBOX).glob(f"{number}-*.json"):
            saved = json.loads(archive.read_text(encoding="utf-8"))
            if all(saved.get(key) == value for key, value in payload.items()):
                write_json(ledger_path, {"issue": number, "bodySha256": fingerprint})
                return

    body_path = outbox / "body.md"
    body_path.write_text(body, encoding="utf-8")
    comment = outbox / "comment.md"
    summary = outbox / "summary.txt"
    notify = outbox / "notify.md"
    command = [sys.executable, str(worktree / "scripts" / ("inbox_uit.py" if is_uit else "inbox_contribution.py")),
               "--issue", str(number), "--body", str(body_path),
               "--author", str((issue.get("user") or {}).get("login") or ""),
               "--out-dir", str(worktree / INBOX), "--comment-out", str(comment),
               "--summary-out", str(summary)]
    if is_uit:
        reviews = outbox / "reviews"
        reviews.mkdir()
        command += ["--packs-dir", str(worktree / "uit-packs"),
                    "--review-out", str(reviews / f"issue-{number}.json")]
    else:
        command += ["--notify-out", str(notify)]
    run(worktree, *command)
    if not comment.exists():
        raise RuntimeError(f"Issue #{number}: ingestion produced no terminal result")
    changes = run(worktree, "git", "status", "--porcelain", "--", str(INBOX), "uit-packs").stdout.strip()
    if changes:
        write_json(outbox / "issues" / f"{number}.json", {
            "issue_number": number,
            "comment": comment.read_text(encoding="utf-8"),
            "summary": summary.read_text(encoding="utf-8") if summary.exists() else "",
            "notify": notify.read_text(encoding="utf-8") if notify.exists() else "",
        })
    # A parsed, terminal result (including duplicates/rejections) is replay-safe.
    # Parse/ingest errors above never reach this point and remain recoverable.
    write_json(ledger_path, {"issue": number, "bodySha256": fingerprint})
    body_path.unlink()
    comment.unlink()
    summary.unlink(missing_ok=True)
    notify.unlink(missing_ok=True)


def transact(args, *, issue: dict | None = None) -> None:
    prepared = prepare_issue(issue) if issue is not None else None
    if issue is not None and prepared is None:
        return
    repo = args.repo.resolve()
    for attempt in range(1, args.max_attempts + 1):
        with tempfile.TemporaryDirectory(prefix="firegaze-inbox-") as temporary:
            temp = Path(temporary)
            worktree, outbox = temp / "checkout", temp / "outbox"
            outbox.mkdir()
            run(repo, "git", "fetch", "--no-tags", "origin", "+refs/heads/main:refs/remotes/origin/main")
            run(repo, "git", "worktree", "add", "--detach", str(worktree), "refs/remotes/origin/main")
            try:
                if issue is None:
                    run(worktree, sys.executable, str(worktree / "scripts/ingest_uit_direct.py"),
                        "--summary-out", str(outbox / "summary.txt"), "--reviews-dir", str(outbox / "reviews"))
                    message = "chore: ingest direct translation contributions"
                else:
                    ingest_issue(worktree, outbox, issue, prepared)
                    message = f"chore: ingest translation contribution #{prepared[0]}"
                # Stage only the ingestion's public data, never scripts or unrelated checkout changes.
                paths = [str(path) for path in (INBOX, Path("uit-packs")) if (worktree / path).exists()]
                if paths:
                    run(worktree, "git", "add", "--", *paths)
                if run(worktree, "git", "diff", "--cached", "--quiet", check=False).returncode == 0:
                    return
                run(worktree, "git", "-c", "user.name=github-actions[bot]", "-c",
                    "user.email=41898282+github-actions[bot]@users.noreply.github.com",
                    "commit", "-m", message)
                pushed = run(worktree, "git", "push", "origin", "HEAD:refs/heads/main", check=False)
                if pushed.returncode == 0:
                    # Only a durable transaction may make its comments/reviews visible to workflow steps.
                    write_json(outbox / "result.json", {"attempts": attempt})
                    args.output_dir.mkdir(parents=True, exist_ok=True)
                    shutil.copytree(outbox, args.output_dir, dirs_exist_ok=True)
                    return
            finally:
                run(repo, "git", "worktree", "remove", "--force", str(worktree))
        if attempt < args.max_attempts:
            delay = min(args.backoff * 2 ** (attempt - 1), 30)
            print(f"Push failed; refetching and rerunning ingestion (attempt {attempt + 1})", flush=True)
            time.sleep(delay)
    raise RuntimeError(f"Could not persist ingestion after {args.max_attempts} attempts; no success outbox published")


def recent_issues(args):
    """Bounded recovery scan; only contribution titles are eligible for ingestion."""
    repository = os.environ.get("GITHUB_REPOSITORY", "")
    if not repository or len(repository.split("/")) != 2:
        raise ValueError("GITHUB_REPOSITORY must identify the recovery repository")
    since = (datetime.now(timezone.utc) - timedelta(days=args.lookback_days)).isoformat().replace("+00:00", "Z")
    headers = {"Accept": "application/vnd.github+json", "User-Agent": "FireGaze-inbox-recovery"}
    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    if token:
        headers["Authorization"] = "Bearer " + token
    api = os.environ.get("GITHUB_API_URL", "https://api.github.com").rstrip("/")
    for page in range(1, args.max_pages + 1):
        query = urllib.parse.urlencode({"state": "all", "sort": "updated", "direction": "desc",
                                        "since": since, "per_page": 100, "page": page})
        request = urllib.request.Request(f"{api}/repos/{repository}/issues?{query}", headers=headers)
        with urllib.request.urlopen(request, timeout=30) as response:
            batch = json.load(response)
        if not isinstance(batch, list):
            raise ValueError("GitHub Issues API returned an invalid response")
        yield from batch
        if len(batch) < 100:
            return
    raise RuntimeError(f"Recovery reached its {args.max_pages}-page limit; rerun with a narrower lookback or larger limit")


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path.cwd())
    parser.add_argument("--mode", choices=("issue", "direct", "recover"), required=True)
    parser.add_argument("--event", type=Path)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--max-attempts", type=int, default=5)
    parser.add_argument("--backoff", type=float, default=1)
    parser.add_argument("--lookback-days", type=int, default=7)
    parser.add_argument("--max-pages", type=int, default=5)
    args = parser.parse_args(argv)
    if args.max_attempts < 1 or args.backoff < 0 or args.lookback_days < 1 or args.max_pages < 1:
        parser.error("attempts, lookback and page limits must be positive; backoff must be nonnegative")
    args.output_dir = args.output_dir.resolve()
    if args.mode == "issue":
        if args.event is None:
            parser.error("issue mode requires --event")
        transact(args, issue=json.loads(args.event.read_text(encoding="utf-8"))["issue"])
    elif args.mode == "direct":
        transact(args)
    else:
        failures = []
        try:
            for issue in recent_issues(args):
                try:
                    transact(args, issue=issue)
                except (OSError, ValueError, RuntimeError) as error:
                    failures.append(f"Issue #{issue.get('number')}: {error}")
                    print(failures[-1], file=sys.stderr)
        except (OSError, ValueError, RuntimeError) as error:
            failures.append(str(error))
        try:
            transact(args)
        except (OSError, ValueError, RuntimeError) as error:
            failures.append(f"Direct recovery: {error}")
        if failures:
            raise RuntimeError("Recovery incomplete: " + "; ".join(failures))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, RuntimeError, KeyError) as error:
        print(error, file=sys.stderr)
        sys.exit(1)
