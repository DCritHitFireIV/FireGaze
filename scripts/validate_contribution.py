#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""validate_contribution.py — 规则校验玩家提交的翻译（不调用任何模型，零 token）。

用途：
  · GitHub Actions：issue 一开就跑一遍，把结果作为评论贴回去；
  · 本地：合并前自己跑一遍，看有没有越界的内容。

用法：
    python scripts/validate_contribution.py --env ISSUE_BODY       # 从环境变量读 issue 正文
    python scripts/validate_contribution.py --file issue.txt       # 从文件读
    cat issue.txt | python scripts/validate_contribution.py        # 从 stdin 读

输出：
    report.md（评论正文）+ 终端摘要；退出码 0 = 至少有一条可用，1 = 一条都没有。

规则（全部是确定性的，不送模型）：
  ① 结构：只看 InternalName / Field / Original / Translated 四个字段，其余一律忽略**（不收任何玩家信息）；
  ② Field 必须是 Name / Punchline / Description；
  ③ 长度：插件名 ≤ 40，一行简介 < 100，插件详情 < 1000 个字符；
  ④ 控制字符（除换行/制表）直接判不合格；
  ⑤ URL / HTML 标签判不合格（只查译文；原文是作者写的，玩家改不了，不连坐）；
  ⑥ 违规词表：只用一份很短的中文名单（默认只挡最明显的那几个词），尽量不限制表达。
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys

FIELD_LIMITS = {"Name": 40, "Punchline": 99, "Description": 999}
FIELDS = set(FIELD_LIMITS)

# 极短的中文违规词表：只挡最容易引起麻烦的，其余交给玩家评分
DENY_WORDS = ["习近平", "法轮功", "六四", "台独", "港独", "反华", "支那", "nmsl", "共产党下台"]

CONTROL = re.compile(r"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]")
URLISH = re.compile(r"(https?://|www\.|[\w-]+\.(com|net|org|cn|io|gg)\b)", re.IGNORECASE)
HTMLLISH = re.compile(r"(<\s*/?\s*[a-zA-Z][a-zA-Z0-9]*(\s[^<>]*)?>|&#\d+;|&[a-zA-Z]{2,8};)")
# 允许的换行/制表
CJK = re.compile(r"[\u4e00-\u9fff]")


def extract_json(text: str) -> dict | None:
    """从 issue 正文里取出最后一个 ```json 代码块；没有就试着整体当 JSON 解析。"""
    blocks = re.findall(r"```json\s*(.*?)```", text, re.S | re.I)
    for candidate in reversed(blocks):
        try:
            return json.loads(candidate)
        except Exception:  # noqa: BLE001
            continue

    try:
        return json.loads(text)
    except Exception:  # noqa: BLE001
        return None


def check_one(item: dict) -> tuple[dict | None, str | None]:
    """返回 (干净的条目, 拒绝原因)。"""
    if not isinstance(item, dict):
        return None, "条目不是对象"

    internal = str(item.get("InternalName") or "").strip()
    field = str(item.get("Field") or "").strip()
    original = item.get("Original")
    translated = item.get("Translated")

    if not internal or len(internal) > 200 or any(ch.isspace() for ch in internal):
        return None, "插件内部名不合法"
    if field not in FIELDS:
        return None, f"字段名不认识：{field!r}"
    if not isinstance(original, str) or not isinstance(translated, str):
        return None, "原文或译文不是字符串"
    if not translated.strip():
        return None, "译文是空的（要删除译文请用空字符串以外的说明，或直接不提）"

    limit = FIELD_LIMITS[field]
    if len(translated) > limit:
        return None, f"译文太长（{len(translated)} > {limit}）"

    # 控制字符两边都查（会破坏 issue 正文）；网址 / HTML / 违规词只查**译文**。
    # 原文是上游作者写的，玩家改不了，连坐会把这类插件永远挡在门外。
    if CONTROL.search(original):
        return None, "原文里有控制字符"
    if CONTROL.search(translated):
        return None, "译文里有控制字符"

    if URLISH.search(translated):
        return None, "译文里有网址"
    if HTMLLISH.search(translated):
        return None, "译文里有 HTML 标记"
    for word in DENY_WORDS:
        if word.lower() in translated.lower():
            return None, "译文里含违规词"

    # 只留翻译本身：不含任何玩家信息（时间、昵称、ID 等一律丢弃）
    clean = {
        "InternalName": internal,
        "Field": field,
        "Original": original,
        "Translated": translated,
    }
    return clean, None


def main(argv=None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--env", default="", help="从哪个环境变量读正文（Actions 里用）")
    parser.add_argument("--file", default="", help="从哪个文件读正文")
    parser.add_argument("--report", default="report.md", help="评论正文写到哪个文件")
    parser.add_argument("--clean-out", default="", help="把过滤后的干净 JSON 写到哪个文件")
    parser.add_argument("--entries-dir", default="", help="每条译文写一个评论文件（贴到 issue 上供点赞投票用）")
    parser.add_argument("--max-entries", type=int, default=10, help="每个 issue 最多贴几条评论（防止刷屏）")
    args = parser.parse_args(argv)

    if args.env:
        text = os.environ.get(args.env, "")
    elif args.file:
        with open(args.file, encoding="utf-8") as handle:
            text = handle.read()
    else:
        text = sys.stdin.read()

    payload = extract_json(text)
    if payload is None:
        body = "### FireGaze 翻译贡献校验\n\n没找到可解析的 JSON（提交内容可能被改动过）。"
        with open(args.report, "w", encoding="utf-8") as handle:
            handle.write(body + "\n")
        print("找不到 JSON，一条都不收。")
        return 1

    if isinstance(payload, dict):
        items = payload.get("contributions") or []
    elif isinstance(payload, list):
        items = payload
    else:
        items = []

    accepted: list[dict] = []
    rejected: list[tuple[str, str]] = []
    for item in items:
        clean, reason = check_one(item if isinstance(item, dict) else {})
        if clean is not None:
            accepted.append(clean)
        else:
            name = ""
            if isinstance(item, dict):
                name = str(item.get("InternalName") or "")[:40]
            rejected.append((name, reason or "不合格"))

    # 去重：同一个插件 + 同一个字段只留最后一条
    dedup: dict[tuple[str, str], dict] = {}
    for item in accepted:
        dedup[(item["InternalName"], item["Field"])] = item
    accepted = list(dedup.values())

    lines = ["### FireGaze 翻译贡献校验", ""]
    lines.append(f"- 收到：{len(items)} 条")
    lines.append(f"- 可用：{len(accepted)} 条")
    lines.append(f"- 不收：{len(rejected)} 条")
    lines.append("")
    if accepted:
        lines.append("可用的条目会在维护时并入词表（以 user 来源，机器翻译不会覆盖）。")
        lines.append("")
        lines.append("| 插件 | 字段 | 译文 |")
        lines.append("|---|---|---|")
        for item in accepted[:20]:
            text_preview = item["Translated"].replace("|", "\\|").replace("\n", " ")[:60]
            lines.append(f"| {item['InternalName']} | {item['Field']} | {text_preview} |")
        if len(accepted) > 20:
            lines.append(f"| … | … | 还有 {len(accepted) - 20} 条 |")
        lines.append("")
    if rejected:
        lines.append("<details><summary>不收的条目与原因</summary>")
        lines.append("")
        for name, reason in rejected[:40]:
            lines.append(f"- {name or '（无插件名）'}：{reason}")
        if len(rejected) > 40:
            lines.append(f"- …另有 {len(rejected) - 40} 条")
        lines.append("")
        lines.append("</details>")

    body = "\n".join(lines)
    with open(args.report, "w", encoding="utf-8") as handle:
        handle.write(body + "\n")

    if args.clean_out:
        with open(args.clean_out, "w", encoding="utf-8") as handle:
            json.dump({"contributions": accepted}, handle, ensure_ascii=False, indent=1)

    if args.entries_dir:
        # 每条译文一条评论文件：玩家给**评论**点 👍 / 👎，汇总脚本按评论统计票数。
        # 用「一个文件一条评论」是为了让工作流能直接 gh issue comment --body-file（不怕换行/引号）。
        os.makedirs(args.entries_dir, exist_ok=True)
        written = 0
        for item in accepted[: args.max_entries]:
            marker = json.dumps(
                {
                    "InternalName": item["InternalName"],
                    "Field": item["Field"],
                    "Original": item["Original"],
                    "Translated": item["Translated"],
                },
                ensure_ascii=False,
                separators=(",", ":"),
            )
            field_name = {"Name": "插件名", "Punchline": "一行简介", "Description": "插件详情"}.get(item["Field"], item["Field"])
            body = (
                f"**{item['InternalName']} · {field_name}**\n\n"
                f"译文：{item['Translated']}\n\n"
                f"给这条点 👍 赞成、👎 反对（不用留言）。0 赞满 30 天会归档。\n\n"
                f"<!-- fg-entry: {marker} -->"
            )
            written += 1
            with open(os.path.join(args.entries_dir, f"comment-{written:02d}.md"), "w", encoding="utf-8", newline="\n") as handle:
                handle.write(body + "\n")

        if len(accepted) > args.max_entries:
            with open(os.path.join(args.entries_dir, "comment-99-note.md"), "w", encoding="utf-8", newline="\n") as handle:
                handle.write(
                    f"这个 issue 还有 {len(accepted) - args.max_entries} 条没逐条贴出来"
                    f"（一次最多贴 {args.max_entries} 条）。需要的话拆成几个 issue 再提交一次。\n"
                )

    print(f"收到 {len(items)}，可用 {len(accepted)}，不收 {len(rejected)}；报告：{args.report}")
    return 0 if accepted else 1


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(errors="replace")
    except Exception:
        pass
    sys.exit(main())
