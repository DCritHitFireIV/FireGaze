#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""inbox_contribution.py — 把 GitHub issue 里的译文投稿收进仓库（由 .github/workflows/inbox.yml 调用）。

为什么有这一步（用户 2026-09-23 定 + 独立审查 P0-2）：
此前插件把译文直接 POST 到维护者手机的推送服务，而那串推送密钥内嵌在发给所有玩家的插件里
—— 等于把维护者的手机收件地址公开了。现在改成：
    玩家「一键提交」→ 打开填好的 GitHub 新建 issue 页 → 玩家按 Submit
    → 本脚本（工作流里跑）把投稿落进仓库 + 体检 → 工作流用 secret 里的地址通知维护者
密钥只存在于仓库 secret，客户端不再持有任何推送凭据。

产出：
    docs/contributions/inbox/<issue 号>-<时间>.json   原始投稿 + 体检结论（长期凭证）
    <runner 临时目录>/summary.txt                      给手机通知用的一行摘要
    <runner 临时目录>/comment.md                       回在 issue 上的话（玩家能看见「收到了」）

用法：
    python scripts/inbox_contribution.py --issue 12 --body <正文文件> [--author <登录名>] [--out-dir …]
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import validate_contribution as rules  # noqa: E402

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_OUT = os.path.join(REPO_ROOT, "docs", "contributions", "inbox")
FIELD_LABELS = {"Name": "插件名", "Punchline": "一行简介", "Description": "插件详情"}

_PLACEHOLDER = re.compile(r"^(?:测试|test|todo|tbd|xxx|asdf|qwe|abc|aaa|123|111)\d*$", re.IGNORECASE)


def check(record: dict) -> tuple[list[str], list[str]]:
    """与本地收稿同一套口径：硬问题（要人确认）/ 软提醒。"""
    hard: list[str] = []
    soft: list[str] = []
    field = str(record.get("Field") or "")
    original = str(record.get("Original") or "")
    translated = str(record.get("Translated") or "")

    if not translated.strip():
        soft.append("译文为空（等于清除这条）")
    else:
        if len(translated) > rules.FIELD_LIMITS.get(field, 999):
            hard.append(f"译文超长（{len(translated)} > {rules.FIELD_LIMITS.get(field, 999)}）")
        if rules.CONTROL.search(translated):
            hard.append("译文里有控制字符")
        if rules.URLISH.search(translated):
            soft.append("译文里有网址")
        if rules.HTMLLISH.search(translated):
            soft.append("译文里有 HTML")
        if any(w.lower() in translated.lower() for w in rules.DENY_WORDS):
            soft.append("译文里含违规词")
        if _PLACEHOLDER.match(translated.strip()):
            hard.append("看着像占位值")
        if translated.strip() == original.strip():
            hard.append("改后与改前一样")
        if field in ("Punchline", "Description") and len(translated.strip()) <= 2:
            soft.append("译文只有一两个字")
    return hard, soft


NOTIFY_MAX_ROWS = 20
NOTIFY_MAX_CHARS = 3500


def build_notify(records: list[dict], hard_count: int) -> str:
    """给手机通知用的译文清单：直接看到每条翻成了什么（过长只放前若干条）。"""
    lines = [f"{len(records)} 条译文：", ""]
    for record in records[:NOTIFY_MAX_ROWS]:
        field = FIELD_LABELS.get(str(record.get("Field") or ""), str(record.get("Field") or ""))
        text = (record.get("Translated") or "").replace("\n", " ").strip() or "（清除译文）"
        if len(text) > 60:
            text = text[:60] + "…"
        lines.append(f"- `{record.get('InternalName')}` / {field}：{text}")
    if len(records) > NOTIFY_MAX_ROWS:
        lines.append(f"…（还有 {len(records) - NOTIFY_MAX_ROWS} 条，见 issue）")
    if hard_count:
        lines.append("")
        lines.append(f"⚠ 有 {hard_count} 条硬问题，收录前要确认")
    notify = "\n".join(lines)
    if len(notify) > NOTIFY_MAX_CHARS:
        notify = notify[:NOTIFY_MAX_CHARS] + "\n…（过长已截断，见 issue）"
    return notify


def main(argv=None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--issue", required=True, help="issue 号")
    parser.add_argument("--body", required=True, help="issue 正文文件（工作流传 github.event.issue.body 落成的文件）")
    parser.add_argument("--author", default="", help="提交者登录名（只写进通知里给维护者看，不进公开留档）")
    parser.add_argument("--out-dir", default=DEFAULT_OUT)
    parser.add_argument("--summary-out", default="", help="摘要写到这里（给手机通知用）")
    parser.add_argument("--comment-out", default="", help="回在 issue 上的话写到这里")
    parser.add_argument("--notify-out", default="", help="手机通知正文写到这里（含译文清单）")
    args = parser.parse_args(argv)

    body = open(args.body, encoding="utf-8", errors="replace").read()
    payload = rules.extract_json(body)
    if not payload or not payload.get("contributions"):
        print("这份 issue 里没有可用的译文（没找到 ```json``` 块）。")
        if args.comment_out:
            with open(args.comment_out, "w", encoding="utf-8", newline="\n") as handle:
                handle.write("没找到本插件生成的 JSON 投稿块。请用游戏里「参与翻译 → 一键提交」重新提交一次。\n")
        return 2

    records = [r for r in payload["contributions"] if isinstance(r, dict)]
    problems: list[dict] = []
    for record in records:
        hard, soft = check(record)
        if hard or soft:
            problems.append(
                {
                    "plugin": record.get("InternalName") or "",
                    "field": record.get("Field") or "",
                    "to": (record.get("Translated") or "")[:60],
                    "hard": hard,
                    "soft": soft,
                }
            )

    hard_count = sum(1 for p in problems if p["hard"])
    os.makedirs(args.out_dir, exist_ok=True)
    stamp = time.strftime("%Y-%m-%dT%H%M%S")
    path = os.path.join(args.out_dir, f"{args.issue}-{stamp}.json")
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(
            {
                "issue": args.issue,
                "receivedAt": time.strftime("%Y-%m-%d %H:%M:%S"),
                "author": args.author,
                "check": {"records": len(records), "hard": hard_count, "problems": problems},
                **payload,
            },
            handle,
            ensure_ascii=False,
            indent=1,
        )
        handle.write("\n")

    names = ", ".join(sorted({(r.get("InternalName") or "") for r in records})[:6])
    summary = f"issue #{args.issue}：{len(records)} 条译文（{names}{'…' if len(records) > 6 else ''}）"
    if hard_count:
        summary += f" ⚠ 有 {hard_count} 条硬问题，先看一眼再收"

    comment = [f"收到 {len(records)} 条译文，已存进仓库（`{os.path.relpath(path, REPO_ROOT)}`）。", ""]
    if hard_count:
        comment.append(f"⚠ 其中 {hard_count} 条有硬问题（测试值 / 超长 / 控制字符 / 空操作），要维护者确认后才会收录：")
        comment.append("")
        for p in problems:
            if p["hard"]:
                comment.append(f"- `{p['plugin']}` / {FIELD_LABELS.get(p['field'], p['field'])}：{'；'.join(p['hard'])}")
        comment.append("")
    soft_rows = [p for p in problems if p["soft"]]
    if soft_rows:
        comment.append("另外几条只是提醒（不影响收录）：")
        comment.append("")
        for p in soft_rows[:10]:
            comment.append(f"- `{p['plugin']}` / {FIELD_LABELS.get(p['field'], p['field'])}：{'；'.join(p['soft'])}")
        comment.append("")
    comment.append("维护者审核后会并入词表，随词表更新发到游戏里。这一条 issue 与仓库里的存档都是长期凭证。")
    comment_text = "\n".join(comment)

    notify_text = build_notify(records, hard_count)

    for target, text in ((args.summary_out, summary), (args.comment_out, comment_text), (args.notify_out, notify_text)):
        if target:
            with open(target, "w", encoding="utf-8", newline="\n") as handle:
                handle.write(text)

    print(summary)
    print(f"存档：{path}")
    return 0


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(errors="replace")
    except Exception:
        pass
    sys.exit(main())
