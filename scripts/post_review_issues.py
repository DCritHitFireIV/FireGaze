#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""post_review_issues.py — 把收稿产出的「抽查 issue」内容用机器人发成 GitHub issue。

由 inbox.yml 的 direct 作业在「建完提交」之后调用：读 `--reviews-dir` 下的 *.json（{title, body}），
逐个 POST 成 issue。正文里带**包名**与**原文→译文样本**（只列一部分，受 issue 长度上限约束）——
投稿已经自动并入公共译文库，这条 issue 只给维护者抽查用。

环境变量：
  GH_TOKEN            带 issues: write 的 token（工作流里用 github.token）
  GITHUB_REPOSITORY   owner/repo（Actions 自带；本机调试可显式给）

口径：发 issue 失败只打印，**不算整体失败**——译文已经并进仓库，绝不能因为发 issue 把工作流刷红。
本机调试用 `--dry-run`（只打印标题与正文长度）。
"""
from __future__ import annotations

import argparse
import glob
import io
import json
import os
import sys
import urllib.request


def post_issue(repo: str, token: str, title: str, body: str) -> tuple[bool, str]:
    request = urllib.request.Request(
        f"https://api.github.com/repos/{repo}/issues",
        data=json.dumps({"title": title, "body": body}).encode("utf-8"),
        method="POST",
        headers={
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.github+json",
            "User-Agent": "firegaze-review-bot",
            "Content-Type": "application/json",
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            document = json.load(response)
        return True, str(document.get("html_url") or "")
    except Exception as error:  # noqa: BLE001
        return False, str(error)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--reviews-dir", required=True)
    parser.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", ""))
    parser.add_argument("--token", default=os.environ.get("GH_TOKEN", ""))
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args(argv)

    files = sorted(glob.glob(os.path.join(args.reviews_dir, "*.json")))
    if not files:
        print("没有要发 issue 的抽查内容")
        return 0

    if args.dry_run or not args.repo or not args.token:
        print(f"[dry-run] 共 {len(files)} 条抽查内容（只打印，不发）：")
        for path in files:
            document = json.load(io.open(path, encoding="utf-8"))
            print(f"  · {document.get('title')}（正文 {len(document.get('body') or '')} 字符）")
        return 0

    failed = 0
    for path in files:
        document = json.load(io.open(path, encoding="utf-8"))
        title = str(document.get("title") or "[收稿]").strip()
        body = str(document.get("body") or "")
        ok, detail = post_issue(args.repo, args.token, title, body)
        print(("✓ " if ok else "✗ ") + f"{title} → {detail}")
        failed += 0 if ok else 1

    print(f"共 {len(files)} 条，失败 {failed}")
    return 0


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(errors="replace")
    except Exception:  # noqa: BLE001
        pass
    sys.exit(main())
