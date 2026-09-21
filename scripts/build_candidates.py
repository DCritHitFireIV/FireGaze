#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""build_candidates.py — 把玩家提交（GitHub issue 里的评论）汇总成候选清单，并按票数决定去留。

生命周期（与用户 2026-09-21 定下的规则一致）：

    提交 → issue（正文四条字段，无玩家信息）
        → 机器初筛（validate_contribution.py，纯规则）
        → 通过：每条译文一条评论，玩家给**评论**点 👍 / 👎
        → 每周一汇总（本脚本）：
             · 👍 ≥ min-votes（默认 1）        → 并入 translations.json（Source: user，机器译永不覆盖）
             · 0 赞且发布满 N 天（默认 30 天）  → 归档到 candidates-archive.json，不再出现在清单里
             · 其余                            → 留在 candidates.json（插件里的「评分」视图读它）

输入（二选一）：
    --issues issues.json     离线用：GitHub issues API 的 JSON 数组（含每个 issue 的评论与 reactions）
    --repo owner/name        联网用：自己调 GitHub API（需要 GITHUB_TOKEN 环境变量）

输出：
    candidates.json          还在评审中的候选（带 👍 数，不写 👎 数）
    candidates-archive.json  一个月没人点赞、已归档的
    translations.json        👍 达标的并入这里（Source=user）
    candidates-report.md     这次做了什么（贴到工作流日志/issue 里）

用法：
    python scripts/build_candidates.py --repo DCritHitFireIV/FireGaze
    python scripts/build_candidates.py --issues issues.json --dry-run
"""

from __future__ import annotations

import argparse
import datetime as dt
import io
import json
import os
import re
import sys
import urllib.request

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_TABLE = os.path.join(REPO_ROOT, "translations.json")
DEFAULT_CANDIDATES = os.path.join(REPO_ROOT, "candidates.json")
DEFAULT_ARCHIVE = os.path.join(REPO_ROOT, "candidates-archive.json")
MARKER = "FireGaze 翻译贡献"
ENTRY_MARK = re.compile(r"<!--\s*fg-entry\s*:\s*(\{.*?\})\s*-->", re.S)
FIELDS = ("Name", "Punchline", "Description")
THUMB_UP = "+1"
THUMB_DOWN = "-1"


def http_json(url: str, token: str):
    request = urllib.request.Request(
        url,
        headers={
            "Accept": "application/vnd.github+json",
            "User-Agent": "FireGaze-Candidates/1.0",
            **({"Authorization": f"Bearer {token}"} if token else {}),
        },
    )
    with urllib.request.urlopen(request, timeout=60) as response:
        return json.loads(response.read().decode("utf-8"))


def fetch_issues(repo: str, token: str, limit: int = 200) -> list[dict]:
    issues = http_json(f"https://api.github.com/repos/{repo}/issues?state=all&per_page={limit}", token)
    result = []
    for issue in issues:
        if "pull_request" in issue:
            continue
        if MARKER not in (issue.get("body") or ""):
            continue
        comments_url = issue.get("comments_url")
        comments = http_json(comments_url, token) if comments_url else []
        for comment in comments:
            reaction_url = comment.get("reactions", {}).get("url") if isinstance(comment.get("reactions"), dict) else None
            if reaction_url:
                comment["reactionList"] = http_json(reaction_url, token)
        issue["commentList"] = comments
        result.append(issue)
    return result


def reactions_of(item: dict) -> tuple[int, int]:
    """返回 (👍, 👎)。既支持 API 汇总字段，也支持 reactionList 明细。"""
    up = down = 0
    reactions = item.get("reactions") or {}
    if isinstance(reactions, dict):
        up += int(reactions.get("+1") or 0)
        down += int(reactions.get("-1") or 0)
    for row in item.get("reactionList") or []:
        if row.get("content") == THUMB_UP:
            up += 1
        elif row.get("content") == THUMB_DOWN:
            down += 1
    # 明细已含汇总时不重复计（reactionList 是独立数组，不会重复）
    return up, down


def parse_candidates(issues: list[dict]) -> list[dict]:
    """从 issue 的逐条评论里读出候选译文。"""
    found: dict[tuple[str, str], dict] = {}

    for issue in issues:
        number = issue.get("number")
        issue_created = (issue.get("created_at") or "")[:10]
        for comment in issue.get("commentList") or []:
            body = comment.get("body") or ""
            match = ENTRY_MARK.search(body)
            if not match:
                continue
            try:
                payload = json.loads(match.group(1))
            except Exception:  # noqa: BLE001
                continue

            internal = str(payload.get("InternalName") or "").strip()
            field = str(payload.get("Field") or "").strip()
            translated = str(payload.get("Translated") or "")
            original = str(payload.get("Original") or "")
            if not internal or field not in FIELDS or not translated.strip():
                continue

            up, down = reactions_of(comment)
            seen = (comment.get("created_at") or issue_created or "")[:10]

            # 同一个字段可以有多条候选（不同人交不同译法）：用 issue + 评论 id 做键，都留着
            key = (internal, field, str(number), str(comment.get("id")))
            found[key] = {
                "InternalName": internal,
                "Field": field,
                "Original": original,
                "Translated": translated,
                "Votes": up,
                "Down": down,
                "FirstSeen": seen,
                "Issue": number,
            }

    return list(found.values())


def main(argv=None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", default="", help="owner/name；联网抓 issue")
    parser.add_argument("--issues", default="", help="离线用：issues JSON 文件")
    parser.add_argument("--token-env", default="GITHUB_TOKEN")
    parser.add_argument("--table", default=DEFAULT_TABLE)
    parser.add_argument("--candidates", default=DEFAULT_CANDIDATES)
    parser.add_argument("--archive", default=DEFAULT_ARCHIVE)
    parser.add_argument("--report", default=os.path.join(REPO_ROOT, "candidates-report.md"))
    parser.add_argument("--min-votes", type=int, default=1, help="多少 👍 就能并入词表")
    parser.add_argument("--days", type=int, default=30, help="0 赞候选多久没动静就归档")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args(argv)

    if args.issues:
        issues = json.load(io.open(args.issues, encoding="utf-8"))
    elif args.repo:
        issues = fetch_issues(args.repo, os.environ.get(args.token_env, ""))
    else:
        print("要么给 --repo，要么给 --issues")
        return 2

    print(f"读到 {len(issues)} 个带标记的 issue")
    candidates = parse_candidates(issues)
    print(f"候选译文 {len(candidates)} 条")

    table = {}
    if os.path.exists(args.table):
        table = json.load(io.open(args.table, encoding="utf-8"))
    archive: list[dict] = []
    if os.path.exists(args.archive):
        archive = json.load(io.open(args.archive, encoding="utf-8")).get("archived", [])

    today = dt.date.today()
    merged: list[dict] = []
    kept: list[dict] = []
    archived: list[dict] = []

    for candidate in sorted(candidates, key=lambda c: (-c["Votes"], c["InternalName"])):
        internal, field = candidate["InternalName"], candidate["Field"]
        entry = table.setdefault(internal, {})
        pair = entry.get(field) or {}

        if candidate["Votes"] >= args.min_votes:
            # 并入词表：玩家译优先，机器译不再覆盖
            entry[field] = {
                "Original": candidate["Original"] or pair.get("Original", ""),
                "Translated": candidate["Translated"],
                "Source": "user",
            }
            merged.append(candidate)
            continue

        seen = candidate.get("FirstSeen") or today.isoformat()
        try:
            age = (today - dt.date.fromisoformat(seen)).days
        except Exception:  # noqa: BLE001
            age = 0

        if age >= args.days:
            archived.append(candidate)
        else:
            kept.append(candidate)

    lines = ["# 候选译文汇总", ""]
    lines.append(f"- 时间：{dt.datetime.now():%Y-%m-%d %H:%M}")
    lines.append(f"- 候选 {len(candidates)} 条：并入词表 {len(merged)}、留在清单 {len(kept)}、归档 {len(archived)}")
    lines.append("")
    if merged:
        lines.append("## 已并入词表（👍 ≥ %d）" % args.min_votes)
        for c in merged:
            lines.append(f"- {c['InternalName']} / {c['Field']}：{c['Translated'][:60]}（👍 {c['Votes']}）")
        lines.append("")
    if kept:
        lines.append("## 留在候选清单（等票）")
        for c in kept:
            lines.append(f"- {c['InternalName']} / {c['Field']}：{c['Translated'][:60]}（👍 {c['Votes']}，来于 {c['FirstSeen']}）")
        lines.append("")
    if archived:
        lines.append(f"## 已归档（{args.days} 天 0 赞）")
        for c in archived:
            lines.append(f"- {c['InternalName']} / {c['Field']}：{c['Translated'][:60]}")
        lines.append("")

    report = "\n".join(lines)
    io.open(args.report, "w", encoding="utf-8", newline="\n").write(report)

    payload = {
        "_meta": {
            "updatedAt": today.isoformat(),
            "note": "玩家提交、还没定下来的候选译文；插件「参与翻译 → 评分」视图读它。"
                    f"👍 ≥ {args.min_votes} 会在每周一并入词表；0 赞满 {args.days} 天归档。",
        },
        "candidates": [
            {
                "InternalName": c["InternalName"],
                "Field": c["Field"],
                "Original": c["Original"],
                "Translated": c["Translated"],
                "Votes": c["Votes"],
                "FirstSeen": c["FirstSeen"],
                "Issue": c["Issue"],
            }
            for c in kept
        ],
    }

    if args.dry_run:
        print("--dry-run：没有写任何文件。")
        print(report[:2000])
        return 0

    io.open(args.candidates, "w", encoding="utf-8", newline="\n").write(
        json.dumps(payload, ensure_ascii=False, indent=1) + "\n"
    )
    io.open(args.archive, "w", encoding="utf-8", newline="\n").write(
        json.dumps(
            {"_meta": {"updatedAt": today.isoformat()}, "archived": archive + archived},
            ensure_ascii=False,
            indent=1,
        ) + "\n"
    )
    io.open(args.table, "w", encoding="utf-8", newline="\n").write(
        json.dumps(table, ensure_ascii=False, indent=1) + "\n"
    )

    print(f"候选清单 {len(kept)} 条 → {args.candidates}")
    print(f"并入词表 {len(merged)} 条；归档 {len(archived)} 条")
    return 0


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(errors="replace")
    except Exception:
        pass
    sys.exit(main())
