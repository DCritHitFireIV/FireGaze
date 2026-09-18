#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""import_contributions.py — 把玩家在「参与翻译」窗口导出的贡献写回词表。

用法：
    python scripts/import_contributions.py <导出文件.json> [--table translations.json] [--dry-run]

规则（与插件里的口径一致）：
  ① 玩家译文一旦写入，`Source` = `user`，机器翻译永不覆盖；
  ② 上游原文变了（与贡献里的 Original 对不上）→ 不写原处，记入 needs-review，
     由人决定是不是还要这条译文；
  ③ 每次写入都会把旧值追加到 scripts/translation-history.jsonl，可回滚；
  ④ 原文改过（记录里 Stale=true，或表里的原文与贡献对不上）→ 写入时加 `Review` 说明。
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_TABLE = os.path.join(REPO_ROOT, "translations.json")
HISTORY = os.path.join(REPO_ROOT, "scripts", "translation-history.jsonl")
FIELDS = ("Name", "Punchline", "Description")


def load_json(path: str) -> dict:
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("contributions", help="插件导出的 contributions-*.json")
    parser.add_argument("--table", default=DEFAULT_TABLE)
    parser.add_argument("--dry-run", action="store_true", help="只报告会改什么，不写文件")
    args = parser.parse_args(argv)

    payload = load_json(args.contributions)
    records = payload.get("contributions") or []
    if not records:
        print("这份文件里没有贡献条目。")
        return 1

    table: dict = {}
    if os.path.exists(args.table):
        table = load_json(args.table)

    print(f"词表现有 {len(table)} 条；本次贡献 {len(records)} 条")

    applied = 0
    skipped: list[str] = []
    history: list[dict] = []

    for record in records:
        key = (record.get("InternalName") or "").strip()
        field = (record.get("Field") or "").strip()
        original = record.get("Original") or ""
        translated = record.get("Translated") or ""
        if not key or field not in FIELDS:
            skipped.append(f"{key or '?'} / {field or '?'}：字段名不认识")
            continue

        entry = table.setdefault(key, {})
        pair = entry.get(field) or {}
        old_original = pair.get("Original") or ""
        old_translated = pair.get("Translated") or ""
        source_changed = bool(old_original) and bool(original) and old_original != original

        if source_changed:
            skipped.append(
                f"{key} / {field}：上游原文已经变了，先复核再决定"
                f"（旧：{old_original[:40]}… / 贡献：{original[:40]}…）"
            )
            continue

        if old_translated == translated and str(pair.get("Source") or "") == "user":
            continue   # 已经就是这一条

        new_pair = dict(pair)
        new_pair["Original"] = old_original or original
        new_pair["Translated"] = translated
        new_pair["Source"] = "user"
        if record.get("Stale") or source_changed:
            new_pair["Review"] = "上游原文改过，这条译文由玩家重新提交"
        else:
            new_pair.pop("Review", None)

        entry[field] = new_pair
        applied += 1
        history.append(
            {
                "time": time.strftime("%Y-%m-%d %H:%M:%S"),
                "plugin": key,
                "field": field,
                "from": old_translated,
                "to": translated,
                "source": record.get("Id") or "",
            }
        )

    print(f"应用 {applied} 条；跳过 {len(skipped)} 条")
    for line in skipped[:20]:
        print(f"  [跳过] {line}")

    if args.dry_run:
        print("--dry-run：没有写任何文件。")
        return 0

    if applied:
        with open(args.table, "w", encoding="utf-8") as handle:
            json.dump(table, handle, ensure_ascii=False, indent=1)

        with open(HISTORY, "a", encoding="utf-8") as handle:
            for row in history:
                handle.write(json.dumps(row, ensure_ascii=False) + "\n")

        print(f"已写回 {args.table}（历史追加到 {HISTORY}）")
    else:
        print("没有需要写回的条目。")

    return 0


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(errors="replace")
    except Exception:
        pass
    sys.exit(main())
