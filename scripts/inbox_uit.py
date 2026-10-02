#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""inbox_uit.py — 收 GitHub issue 里的「插件界面文字」译文投稿（由 .github/workflows/inbox.yml 调用）。

流程与简介词表同源：编辑器 →「提交人工译文到公共库…」→ 打开填好的新建 issue 页 → 玩家按 Submit
→ 本脚本把投稿并进 `uit-packs/<内部名>.json`（Source=user）+ 留档 + 回话。

payload（只有 type==uit-contribution 才处理）：
{
  "type": "uit-contribution", "plugin": "AutoHook",
  "entries":   [{"Original": "...", "Translated": "...", "Context": "...", "Source": "user"}],
  "resources": [{"Container": "...resources", "Key": "...", "Original": "...", "Translated": "..."}]
}

容器名支持三类（2026-10-03 与服务端对齐）：
  · `<程序集名>.resources`（DLL 内嵌资源）
  · `file:<相对路径>`（插件目录里的本地化文件，如 file:Localization/zh-CN.json）
  · `json:<资源名>.json`（DLL 内嵌 JSON 语言表，如 json:HaselTweaks.Translations.json）

口径：
  · 只改译文与 Source，不删条目；**人工译（Source=user）才允许覆盖**，机器译只补空槽——
    否则一份来自旧模型的机器译会把人写的译文永久顶掉（2026-10-03 评审 C-01）；
  · 硬问题（空译文 / 超长 / 控制字符 / 容器名不合法）直接跳过并在回话里点名；
  · 库里没有该插件的包时**新建一个空包**再收（2026-10-03 评审 C-04：客户端只会在中继返回成功时
    报「已上传」，若服务端直接退回就会变成谎报）；未收录插件的包会随下一次库更新进索引。
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
CONTAINER_RESOURCES = re.compile(r"^[A-Za-z0-9_.\-]+\.resources$")


def valid_container(container: str) -> bool:
    """容器名合法性（与插件端三类容器对齐：程序集资源 / file: / json:）。"""
    if CONTAINER_RESOURCES.match(container):
        return True
    if container.startswith("file:"):
        rel = container[5:]
        if not rel or len(rel) > 200 or rel.startswith("/") or "\\" in rel or ".." in rel:
            return False
        return all(ch.isalnum() or ch in "._-/ " for ch in rel)
    if container.startswith("json:"):
        name = container[5:]
        return bool(name) and len(name) <= 200 and name.endswith(".json") and all(
            ch.isalnum() or ch in "._-" for ch in name
        )
    return False


def is_user_source(item: dict) -> bool:
    """投稿条目是不是人工译（客户端会把每条自己的 Source 带上来）。"""
    return str(item.get("Source") or "").strip().lower().startswith("user")


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
        # 未收录插件的投稿：新建空包再收（2026-10-03 C-04：客户端只会在中继成功时报成功，
        # 服务端直接退回就等于谎报；新包会随下一次库更新进索引，可直接被其他玩家下载）
        os.makedirs(args.packs_dir, exist_ok=True)
        pack = {
            "_meta": {"source": "contribution", "updatedAt": time.strftime("%Y-%m-%d")},
            "entries": [],
            "resources": [],
            "attributes": [],
        }
        print(f"库里还没有 {plugin} 的包：新建一个空包再收。")

    pack.setdefault("entries", [])
    pack.setdefault("resources", [])
    pack.setdefault("attributes", [])

    entries_by_original = {e.get("Original", ""): e for e in pack.get("entries") or []}
    resources_by_key = {
        (r.get("Container", ""), r.get("Key", "")): r for r in pack.get("resources") or []
    }

    accepted = 0
    filled = 0
    overwritten = 0
    kept = 0
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
        user_source = is_user_source(item)
        target = entries_by_original.get(original)
        if target is None:
            target = {"Original": original, "Translated": translated, "Context": str(item.get("Context") or "")}
            target["Source"] = "user" if user_source else "library"
            pack["entries"].append(target)
            entries_by_original[original] = target
            accepted += 1
            filled += 1
            continue

        existing = str(target.get("Translated") or "").strip()
        if user_source:
            target["Translated"] = translated
            target["Source"] = "user"
            accepted += 1
            overwritten += 1
        elif not existing:
            target["Translated"] = translated
            target.setdefault("Source", "library")
            accepted += 1
            filled += 1
        else:
            # 机器译不覆盖已有内容（保护人工译不被旧模型永久顶掉）
            kept += 1

    for item in (payload.get("resources") or [])[:MAX_ENTRIES]:
        if not isinstance(item, dict):
            continue
        container = str(item.get("Container") or "")
        key = str(item.get("Key") or "")
        original = str(item.get("Original") or "")
        translated = str(item.get("Translated") or "")
        if not valid_container(container) or not key or len(key) > 200:
            rejected.append(f"`{container} / {key[:40]}`：容器名或 key 不合法")
            continue
        reason = check_text(original, translated)
        if reason:
            rejected.append(f"`{key[:40]}`：{reason}")
            continue
        user_source = is_user_source(item)
        target = resources_by_key.get((container, key))
        if target is None:
            target = {"Container": container, "Key": key, "Original": original, "Translated": translated}
            target["Source"] = "user" if user_source else "library"
            pack["resources"].append(target)
            resources_by_key[(container, key)] = target
            accepted += 1
            filled += 1
            continue

        existing = str(target.get("Translated") or "").strip()
        if user_source:
            target["Translated"] = translated
            target["Source"] = "user"
            accepted += 1
            overwritten += 1
        elif not existing:
            target["Translated"] = translated
            target.setdefault("Source", "library")
            accepted += 1
            filled += 1
        else:
            kept += 1

    attributes_by_original = {a.get("Original", ""): a for a in pack.get("attributes") or []}
    for item in (payload.get("attributes") or [])[:MAX_ENTRIES]:
        if not isinstance(item, dict):
            continue
        original = str(item.get("Original") or "")
        translated = str(item.get("Translated") or "")
        reason = check_text(original, translated)
        if reason:
            rejected.append(f"`{original[:40]}`：{reason}")
            continue
        user_source = is_user_source(item)
        target = attributes_by_original.get(original)
        if target is None:
            target = {"Original": original, "Translated": translated, "Context": "[投稿]"}
            target["Source"] = "user" if user_source else "library"
            pack["attributes"].append(target)
            attributes_by_original[original] = target
            accepted += 1
            filled += 1
            continue

        existing = str(target.get("Translated") or "").strip()
        if user_source:
            target["Translated"] = translated
            target["Source"] = "user"
            accepted += 1
            overwritten += 1
        elif not existing:
            target["Translated"] = translated
            target.setdefault("Source", "library")
            accepted += 1
            filled += 1
        else:
            kept += 1

    # 无论收录多少都留档：被退回的投稿也要能查（2026-10-03 评审 C-04）
    os.makedirs(args.out_dir, exist_ok=True)
    archive = os.path.join(args.out_dir, f"uit-{args.issue}-{time.strftime('%Y-%m-%dT%H%M%S')}.json")
    save_json(archive, {
        "issue": args.issue,
        "receivedAt": time.strftime("%Y-%m-%d %H:%M:%S"),
        "author": args.author,
        "plugin": plugin,
        "accepted": accepted,
        "filled": filled,
        "overwritten": overwritten,
        "kept": kept,
        "rejected": rejected[:50],
        "payload": payload,
    })

    if accepted == 0:
        if kept:
            return fail(args, f"这条 issue 里没有可收录的新译文：{kept} 条已有内容，而机器译不会覆盖已有译文（人工改过的译文才会；译文都还在插件本地）。")
        return fail(args, "这条 issue 里没有可收录的译文。")

    meta = pack.setdefault("_meta", {})
    meta["updatedAt"] = time.strftime("%Y-%m-%d")
    meta.setdefault("source", "library")
    save_json(pack_path, pack)
    refresh_index(args.packs_dir)

    detail = f"收录 {accepted} 条（{plugin}）"
    if filled:
        detail += f"，其中补缺 {filled} 条"
    if overwritten:
        detail += f"，覆盖（人工译）{overwritten} 条"
    if kept:
        detail += f"，跳过 {kept} 条机器译（已有内容不覆盖）"
    print(detail + f"，留档 {os.path.relpath(archive, REPO_ROOT)}")
    if args.summary_out:
        with open(args.summary_out, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(f"issue #{args.issue}：{accepted} 条 {plugin} 界面文字译文（已并入 uit-packs）")
    if args.comment_out:
        lines = [f"收到 {accepted} 条 `{plugin}` 的界面文字译文，已并入 `uit-packs/{plugin}.json`（下次库更新时对玩家生效）。", ""]
        if filled or overwritten:
            lines.append(f"其中补缺 {filled} 条、覆盖人工译 {overwritten} 条。")
            lines.append("")
        if kept:
            lines.append(f"另有 {kept} 条机器译因为库里已有内容而没有覆盖（人工改过的译文才会覆盖已有值）。")
            lines.append("")
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
            "attributes": len(pack.get("attributes") or []),
        }
    index["updatedAt"] = time.strftime("%Y-%m-%d")
    save_json(index_path, index)


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(errors="replace")
    except Exception:  # noqa: BLE001
        pass
    sys.exit(main())
