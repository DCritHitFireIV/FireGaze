#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""inbox_uit.py — 收 GitHub issue 里的「插件界面文字」译文投稿（由 .github/workflows/inbox.yml 调用）。

流程与简介词表同源：编辑器 →「提交人工译文到公共库…」→ 打开填好的新建 issue 页 → 玩家按 Submit
→ 本脚本把投稿并进 `uit-packs/<内部名>.json`（Source=user）+ 留档 + 回话。

payload（只有 type==uit-contribution 才处理）：
{
  "type": "uit-contribution", "plugin": "AutoHook",
  "entries":   [{"Original": "...", "Translated": "...", "Context": "..."}],
  "resources": [{"Container": "...resources", "Key": "...", "Original": "...", "Translated": "..."}]
}

口径：
  · 只改译文与 Source，不删条目；玩家译在库包里标 user（插件侧合并时 user 永不被顶）；
  · 硬问题（空译文 / 超长 / 控制字符 / 容器名不合法）直接跳过并在回话里点名；
  · 容器名必须在库包或索引里能找到对应插件，防误投。
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

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(SCRIPT_DIR)
PACKS_DIR = os.path.join(REPO_ROOT, "uit-packs")
INBOX_DIR = os.path.join(REPO_ROOT, "docs", "contributions", "inbox")
MAX_ENTRIES = 3000
MAX_TRANSLATED = 2000
MAX_ORIGINAL = 4000
CONTAINER = re.compile(r"^[A-Za-z0-9_.\-]+\.resources$")


def load_json(path: str):
    try:
        with open(path, encoding="utf-8") as handle:
            return json.load(handle)
    except Exception:  # noqa: BLE001
        return None


def save_json(path: str, data) -> None:
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(data, handle, ensure_ascii=False, indent=2)
        handle.write("\n")


def check_text(original: str, translated: str) -> str | None:
    """返回拒绝原因；None = 通过。"""
    if not translated.strip():
        return "译文为空"
    if len(translated) > MAX_TRANSLATED:
        return f"译文超长（{len(translated)} > {MAX_TRANSLATED}）"
    if len(original) > MAX_ORIGINAL:
        return f"原文超长（{len(original)} > {MAX_ORIGINAL}）"
    if rules.CONTROL.search(translated) or rules.CONTROL.search(original):
        return "有控制字符"
    if any(word.lower() in translated.lower() for word in rules.DENY_WORDS):
        return "含违规词"
    return None


def main(argv=None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--issue", required=True)
    parser.add_argument("--body", required=True, help="issue 正文文件")
    parser.add_argument("--author", default="")
    parser.add_argument("--packs-dir", default=PACKS_DIR)
    parser.add_argument("--out-dir", default=INBOX_DIR)
    parser.add_argument("--comment-out", default="", help="回在 issue 上的话写到这里")
    parser.add_argument("--summary-out", default="", help="一行摘要（手机通知用）")
    args = parser.parse_args(argv)

    body = open(args.body, encoding="utf-8", errors="replace").read()
    payload = rules.extract_json(body)
    if not payload or payload.get("type") != "uit-contribution":
        print("这不是「插件界面文字」的投稿（没有 uit-contribution 标记），跳过。")
        return 0

    plugin = str(payload.get("plugin") or "").strip()
    if not plugin or any(ch.isspace() for ch in plugin) or len(plugin) > 120:
        return fail(args, "插件内部名不合法。")

    pack_path = os.path.join(args.packs_dir, plugin + ".json")
    pack = load_json(pack_path)
    if pack is None:
        return fail(args, f"库里还没有 `{plugin}` 的包（必须先有生成的包才能收译文）。")

    entries_by_original = {e.get("Original", ""): e for e in pack.get("entries") or []}
    resources_by_key = {
        (r.get("Container", ""), r.get("Key", "")): r for r in pack.get("resources") or []
    }

    accepted = 0
    rejected: list[str] = []
    for item in (payload.get("entries") or [])[:MAX_ENTRIES]:
        if not isinstance(item, dict):
            continue
        original = str(item.get("Original") or "")
        translated = str(item.get("Translated") or "")
        reason = check_text(original, translated)
        if reason:
            rejected.append(f"`{original[:40]}`：{reason}")
            continue
        target = entries_by_original.get(original)
        if target is None:
            target = {"Original": original, "Translated": "", "Context": str(item.get("Context") or "")}
            pack["entries"].append(target)
            entries_by_original[original] = target
        target["Translated"] = translated
        target["Source"] = "user"
        accepted += 1

    for item in (payload.get("resources") or [])[:MAX_ENTRIES]:
        if not isinstance(item, dict):
            continue
        container = str(item.get("Container") or "")
        key = str(item.get("Key") or "")
        original = str(item.get("Original") or "")
        translated = str(item.get("Translated") or "")
        if not CONTAINER.match(container) or not key or len(key) > 200:
            rejected.append(f"`{container} / {key[:40]}`：容器名或 key 不合法")
            continue
        reason = check_text(original, translated)
        if reason:
            rejected.append(f"`{key[:40]}`：{reason}")
            continue
        target = resources_by_key.get((container, key))
        if target is None:
            target = {"Container": container, "Key": key, "Original": original, "Translated": ""}
            pack["resources"].append(target)
            resources_by_key[(container, key)] = target
        target["Translated"] = translated
        target["Source"] = "user"
        accepted += 1

    if accepted == 0:
        return fail(args, "这条 issue 里没有可收录的译文。")

    meta = pack.setdefault("_meta", {})
    meta["updatedAt"] = time.strftime("%Y-%m-%d")
    meta.setdefault("source", "library")
    save_json(pack_path, pack)
    refresh_index(args.packs_dir)

    os.makedirs(args.out_dir, exist_ok=True)
    archive = os.path.join(args.out_dir, f"uit-{args.issue}-{time.strftime('%Y-%m-%dT%H%M%S')}.json")
    save_json(archive, {
        "issue": args.issue,
        "receivedAt": time.strftime("%Y-%m-%d %H:%M:%S"),
        "author": args.author,
        "plugin": plugin,
        "accepted": accepted,
        "rejected": rejected[:50],
        "payload": payload,
    })

    print(f"收录 {accepted} 条（{plugin}），留档 {os.path.relpath(archive, REPO_ROOT)}")
    if args.summary_out:
        with open(args.summary_out, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(f"issue #{args.issue}：{accepted} 条 {plugin} 界面文字译文（已并入 uit-packs）")
    if args.comment_out:
        lines = [f"收到 {accepted} 条 `{plugin}` 的界面文字译文，已并入 `uit-packs/{plugin}.json`（下次库更新时对玩家生效）。", ""]
        if rejected:
            lines.append(f"其中 {len(rejected)} 条没能收录：")
            lines.append("")
            for row in rejected[:10]:
                lines.append(f"- {row}")
            lines.append("")
        lines.append("维护者会抽查；这一条 issue 与仓库里的存档都是长期凭证。")
        with open(args.comment_out, "w", encoding="utf-8", newline="\n") as handle:
            handle.write("\n".join(lines) + "\n")
    return 0


def fail(args, message: str) -> int:
    print(message)
    if args.comment_out:
        with open(args.comment_out, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(message + "\n")
    return 0


def refresh_index(packs_dir: str) -> None:
    """按 uit-packs/ 下所有包重算 index.json（条数/日期）。"""
    index_path = os.path.join(packs_dir, "index.json")
    index = load_json(index_path) or {"plugins": {}}
    plugins = index.setdefault("plugins", {})
    for name in sorted(os.listdir(packs_dir)):
        if not name.endswith(".json") or name == "index.json":
            continue
        pack = load_json(os.path.join(packs_dir, name))
        if pack is None:
            continue
        meta = pack.get("_meta") or {}
        plugins[name[:-5]] = {
            "file": name,
            "updatedAt": meta.get("updatedAt"),
            "entries": len(pack.get("entries") or []),
            "resources": len(pack.get("resources") or []),
        }
    index["updatedAt"] = time.strftime("%Y-%m-%d")
    save_json(index_path, index)


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(errors="replace")
    except Exception:  # noqa: BLE001
        pass
    sys.exit(main())
