#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ingest_uit_direct.py — 把中继直传的投稿（`uit-direct-*.json`）并入公共译文库。

背景（2026-10-04 用户定）：几千条的投稿不再走 issue 粘贴；客户端「一键提交」直接把 JSON
POST 给中继，中继用 GitHub App 提交成 `docs/contributions/inbox/uit-direct-<时间>-<随机>.json`。
本脚本（由 inbox.yml 的 push 触发）把它包装成「issue 正文」，复用 `inbox_uit.py` 的同一套
校验 / 并入 / 存档逻辑——**合并口径只有一份实现**，两条来稿通道不会漂移。

幂等：
  · payload 文件里已有 `importedAt` → 跳过（工作流提交后会再触发一次，那次什么都不做就退出）；
  · 同一份内容重复投递（客户端超时重传）由 `inbox_uit.py` 的内容指纹去重拦住。

用法（工作流里跑）：
    python scripts/ingest_uit_direct.py [--inbox-dir docs/contributions/inbox]
                                        [--packs-dir uit-packs] [--summary-out /tmp/summary.txt]
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import sys
import tempfile
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import inbox_uit  # noqa: E402

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(SCRIPT_DIR)
PAYLOAD_GLOB = "uit-direct-*.json"
TAG_PREFIX = "uit-"


def wrap_payload(payload_text: str, plugin: str, total: int) -> str:
    """把直传 payload 包成 issue 正文格式（inbox_uit 从 ```json 块里取内容）。"""
    header = (
        "### FireGaze contributions · 插件界面文字译文贡献\n\n"
        f"- 插件：`{plugin}`\n"
        f"- 条数：{total}\n\n"
    )
    return header + "```json\n" + payload_text.rstrip("\n") + "\n```\n"


def is_archive(doc: dict) -> bool:
    """issue 通道写的存档（里面有 payload/fingerprint），不是直传 payload——静默跳过。"""
    return "fingerprint" in doc or "payload" in doc


def main(argv=None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--inbox-dir", default=os.path.join(REPO_ROOT, "docs", "contributions", "inbox"))
    parser.add_argument("--packs-dir", default=os.path.join(REPO_ROOT, "uit-packs"))
    parser.add_argument("--summary-out", default="")
    args = parser.parse_args(argv)

    if not os.path.isdir(args.inbox_dir):
        print(f"没有收件箱目录：{args.inbox_dir}（没有直传投稿）")
        return 0

    files = sorted(glob.glob(os.path.join(args.inbox_dir, PAYLOAD_GLOB)))
    lines: list[str] = []
    imported = 0
    for path in files:
        name = os.path.basename(path)
        tag = name[:-5]  # uit-direct-...
        try:
            raw = open(path, encoding="utf-8").read()
            doc = json.loads(raw)
        except Exception as error:  # noqa: BLE001
            print(f"[{name}] 读不出来，跳过：{error}")
            continue

        if not isinstance(doc, dict) or is_archive(doc) or doc.get("importedAt"):
            continue
        if doc.get("type") != "uit-contribution":
            print(f"[{name}] 不是界面文字投稿，跳过")
            continue

        plugin = str(doc.get("plugin") or "").strip()
        total = (
            len(doc.get("entries") or [])
            + len(doc.get("resources") or [])
            + len(doc.get("attributes") or [])
        )
        if not plugin or total == 0:
            print(f"[{name}] 没有插件名或没有条目，跳过")
            doc["importedAt"] = time.strftime("%Y-%m-%dT%H:%M:%S")
            doc["imported"] = {"error": "payload empty"}
            inbox_uit.save_json(path, doc)
            continue

        # 归档名 = uit-<issue_tag>-<ts>.json；issue_tag 用文件名去掉 "uit-" 前缀，保证能找回
        issue_tag = tag[len(TAG_PREFIX):] if tag.startswith(TAG_PREFIX) else tag
        with tempfile.TemporaryDirectory() as temp:
            body_path = os.path.join(temp, "body.md")
            summary_path = os.path.join(temp, "summary.txt")
            comment_path = os.path.join(temp, "comment.md")
            with open(body_path, "w", encoding="utf-8", newline="\n") as handle:
                handle.write(wrap_payload(raw, plugin, total))

            inbox_uit.main([
                "--issue", issue_tag,
                "--body", body_path,
                "--author", "匿名直传",
                "--packs-dir", args.packs_dir,
                "--out-dir", args.inbox_dir,
                "--comment-out", comment_path,
                "--summary-out", summary_path,
            ])

            summary = ""
            if os.path.exists(summary_path):
                summary = open(summary_path, encoding="utf-8").read().strip()
            comment = ""
            if os.path.exists(comment_path):
                comment = open(comment_path, encoding="utf-8").read().strip()

        archive_name = ""
        stats: dict = {}
        archives = sorted(glob.glob(os.path.join(args.inbox_dir, f"uit-{issue_tag}-*.json")))
        if archives:
            archive_name = os.path.basename(archives[-1])
            try:
                record = json.load(open(archives[-1], encoding="utf-8"))
                stats = {
                    "accepted": record.get("accepted"),
                    "filled": record.get("filled"),
                    "overwritten": record.get("overwritten"),
                    "kept": record.get("kept"),
                    "rejected": len(record.get("rejected") or []),
                }
            except Exception:  # noqa: BLE001
                pass

        doc["importedAt"] = time.strftime("%Y-%m-%dT%H:%M:%S")
        doc["imported"] = {"archive": archive_name, **stats}
        if comment:
            doc["importedNote"] = comment[:400]
        inbox_uit.save_json(path, doc)

        imported += 1
        line = summary or (comment.splitlines()[0] if comment else f"{name}：未并入（见存档）")
        print(f"[{name}] {line}")
        lines.append(line)

    if args.summary_out and lines:
        with open(args.summary_out, "w", encoding="utf-8", newline="\n") as handle:
            handle.write("\n".join(lines) + "\n")

    print(f"直传投稿：扫描 {len(files)} 个文件，本次并入 {imported} 份。")
    return 0


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(errors="replace")
    except Exception:  # noqa: BLE001
        pass
    sys.exit(main())
