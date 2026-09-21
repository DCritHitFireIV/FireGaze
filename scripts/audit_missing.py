#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""audit_missing.py — 审计「被界面判为缺译」的条目，看规则有没有误判。

界面（TranslationIndex）判缺译的口径：上游 manifest 有这个字段、但词表里这个字段没有译文。
本脚本用同一份语料（Aetherfeed + 本机仓库文件）离线复现这个判断，把「缺译」逐条归类：

  A. 上游本来就没有这个字段              → 界面已排除（可补，不算缺译）
  B. 上游原文本身是中文                  → 界面已排除（国服/汉化分支）
  C. 词表里有译文，但**对不上当前原文**  → 界面会显示「待复核」而不是缺译（需人工）
  D. 词表里根本没有这个插件              → 界面会显示缺译（真缺）
  E. 词表里有插件、但这个字段没有译文     → 界面会显示缺译（真缺）
  F. 原文是纯符号/占位（N/A、v1.2 之类）  → 规则疏漏候选
  G. 插件名与原样相同（品牌名/专有名词）  → 名字不译是常态，规则疏漏候选
  H. 词表键与 manifest 的 InternalName 不同（国服 fork 改名）→ 规则疏漏候选

用法：
    python scripts/audit_missing.py --repos "%APPDATA%\XIVLauncherCN\dalamudConfig.json"
"""
from __future__ import annotations

import argparse
import io
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import update_translations as ut  # noqa: E402

CJK = re.compile(r"[\u4e00-\u9fff]")
SYMBOLIC = re.compile(r"^[\W\d_]+$", re.UNICODE)
VERSIONISH = re.compile(r"^[vV]?\d+(\.\d+)*$")


def is_symbolic(text: str) -> bool:
    t = (text or "").strip()
    if not t:
        return True
    if SYMBOLIC.match(t) or VERSIONISH.match(t):
        return True
    return len(t) <= 3 and not CJK.search(t)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--table", default=os.path.join(ut.REPO_ROOT, "translations.json"))
    parser.add_argument("--repos", default="", help="Dalamud 配置或仓库地址清单")
    parser.add_argument("--corpus", default="", help="直接读已缓存好的语料（省得每次重抓 1178 个仓库）")
    args = parser.parse_args(argv)

    if args.corpus:
        corpus = json.load(io.open(args.corpus, encoding="utf-8"))
        print(f"（用缓存语料：{args.corpus}）")
    else:
        corpus = ut.build_corpus(None, 0)
        if args.repos:
            urls = ut.load_repo_urls(args.repos)
            ut.add_repo_corpus(corpus, urls)

    table = json.load(io.open(args.table, encoding="utf-8"))
    table = {k: v for k, v in table.items() if not k.startswith("_")}

    buckets: dict[str, list[tuple[str, str, str]]] = {
        "A_上游没这个字段": [], "B_原文是中文": [], "C_原文已变(待复核)": [],
        "D_词表没有这个插件": [], "E_字段没译文": [], "F_原文是符号/占位": [], "H_内部名不同": [],
    }
    ok = 0

    for key, item in corpus.items():
        entry = table.get(key)
        if entry is None:
            # 看有没有「别的键但名字一样」的（国服 fork 改名）
            target_name = (item.get("name") or "").strip()
            alt = [k for k, v in table.items()
                   if (v.get("Name") or {}).get("Original", "").strip() == target_name and target_name]
            if alt:
                for field, text in (("Name", item["name"]), ("Punchline", item["punchline"]), ("Description", item["description"])):
                    if text and not ((table.get(alt[0]).get(field) or {}).get("Translated") or ""):
                        buckets["H_内部名不同"].append((key, field, f"同名键：{alt[0]}"))
            else:
                for field, text in (("Name", item["name"]), ("Punchline", item["punchline"]), ("Description", item["description"])):
                    if text:
                        buckets["D_词表没有这个插件"].append((key, field, text[:60]))
            continue

        for field, text in (("Name", item["name"]), ("Punchline", item["punchline"]), ("Description", item["description"])):
            if not text:
                buckets["A_上游没这个字段"].append((key, field, ""))
                continue

            pair = entry.get(field) or {}
            translated = (pair.get("Translated") or "").strip()
            saved_original = (pair.get("Original") or "").strip()

            if translated:
                if saved_original and saved_original != text:
                    buckets["C_原文已变(待复核)"].append((key, field, f"旧：{saved_original[:40]}"))
                else:
                    ok += 1
                continue

            if CJK.search(text):
                buckets["B_原文是中文"].append((key, field, text[:60]))
            elif is_symbolic(text):
                buckets["F_原文是符号/占位"].append((key, field, text[:60]))
            else:
                buckets["E_字段没译文"].append((key, field, text[:60]))

    print(f"语料 {len(corpus)} 个插件；词表 {len(table)} 条；有译文 {ok} 处\n")
    for name, rows in buckets.items():
        print(f"== {name}：{len(rows)} ==")
        for key, field, note in rows[:8]:
            print(f"   {key} / {field}  {note}")
        if len(rows) > 8:
            print(f"   …另有 {len(rows) - 8} 条")
        print()
    return 0


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(errors="replace")
    except Exception:
        pass
    sys.exit(main())
