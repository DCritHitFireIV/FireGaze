#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""inbox_repo.py — 收录玩家投稿的第三方插件仓库地址（「插件发现 → 投稿插件库」）。

链路：插件本机先做卫月同款契约检测 → 中继把 {url, submittedAt} 写进
`docs/contributions/inbox/repo-*.json` → 本脚本再抓一遍并复核 → 通过就追加到
`scripts/community-repos.txt`（归一化去重；与 repos.txt 分开维护）→ 移进
`docs/contributions/repo-accepted/`（被拒的移进 `repo-rejected/` 并写原因）。
之后工作流触发一次增量翻译（repository_dispatch translate-now），新插件几分钟后进词表。

用法：
    python scripts/inbox_repo.py [--inbox docs/contributions/inbox] [--out scripts/community-repos.txt]
                                [--notify-out <文件>]
"""

from __future__ import annotations

import argparse
import glob
import json
import os
import re
import shutil
import sys
import urllib.parse

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import update_translations as ut  # noqa: E402

REPO_ROOT = ut.REPO_ROOT
DEFAULT_INBOX = os.path.join(REPO_ROOT, "docs", "contributions", "inbox")
ACCEPTED_DIR = os.path.join(REPO_ROOT, "docs", "contributions", "repo-accepted")
REJECTED_DIR = os.path.join(REPO_ROOT, "docs", "contributions", "repo-rejected")

_VERSION = re.compile(r"^\d+(\.\d+){1,3}$")


def normalize_repo_url(url: str) -> str:
    """归一化仓库地址：去镜像前缀、去尾斜杠、scheme/host 小写（路径保持原样）。"""
    text = (url or "").strip()
    for prefix in ("https://gh.atmoomen.top/", "https://gh-proxy.org/"):
        if text.lower().startswith(prefix):
            rest = text[len(prefix):]
            return normalize_repo_url(rest if rest.lower().startswith("http") else "https://" + rest)

    while text.endswith("/"):
        text = text[:-1]
    try:
        parts = urllib.parse.urlsplit(text)
        if parts.scheme and parts.netloc:
            text = urllib.parse.urlunsplit(
                (parts.scheme.lower(), parts.netloc.lower(), parts.path, parts.query, parts.fragment)
            )
    except ValueError:
        pass
    return text


def check_repo(doc) -> tuple[int, int, str | None]:
    """卫月口径的仓库契约检查（和插件侧 ManifestCheck 同义）：数组 + 每条内部名/名字/版本。

    返回 (有效条数, 总条数, 失败原因)；版本格式明显不对的条目按「会被卫月丢弃」计。
    """
    if not isinstance(doc, list):
        return 0, 0, "不是数组"
    if not doc:
        return 0, 0, "空数组，一条插件都没有"

    valid = 0
    for item in doc:
        if not isinstance(item, dict):
            continue
        internal = str(item.get("InternalName") or "").strip()
        name = str(item.get("Name") or "").strip()
        version = item.get("AssemblyVersion")
        if not internal or not name or version is None:
            continue
        if isinstance(version, str) and not _VERSION.match(version.strip()):
            continue
        if isinstance(version, (int, float)):
            continue
        if not isinstance(version, str):
            continue
        valid += 1

    if valid == 0:
        return 0, len(doc), f"文件里有 {len(doc)} 条，但没有一条能被卫月识别（缺内部名 / 名字 / 版本号）"
    return valid, len(doc), None


def read_existing(out_path: str) -> dict[str, str]:
    """读一份地址清单（跳过 # 注释）→ {归一化地址: 原始行}。"""
    known: dict[str, str] = {}
    if not os.path.exists(out_path):
        return known
    with open(out_path, encoding="utf-8-sig") as handle:
        for line in handle:
            text = line.strip()
            if not text or text.startswith("#"):
                continue
            known[normalize_repo_url(text)] = text
    return known


def main(argv=None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--inbox", default=DEFAULT_INBOX)
    parser.add_argument("--out", default=os.path.join(REPO_ROOT, "scripts", "community-repos.txt"))
    parser.add_argument("--notify-out", default="")
    args = parser.parse_args(argv)

    files = sorted(glob.glob(os.path.join(args.inbox, "repo-*.json")))
    if not files:
        print("收件箱里没有 repo-*.json（没有新投稿）")
        return 0

    # 去重底数：维护者清单 + 社区清单（同一地址的镜像/尾斜杠形态算同一条）
    known: dict[str, str] = {}
    for source in (
        os.path.join(REPO_ROOT, "scripts", "repos.txt"),
        os.path.join(REPO_ROOT, "scripts", "community-repos.txt"),
    ):
        known.update(read_existing(source))

    accepted: list[tuple[str, str, int]] = []
    rejected: list[tuple[str, str, str]] = []
    for path in files:
        try:
            with open(path, encoding="utf-8-sig") as handle:
                payload = json.load(handle)
        except Exception as error:  # noqa: BLE001
            rejected.append((os.path.basename(path), "", f"投稿文件读不了：{error}"))
            continue

        url = str(payload.get("url") or "").strip()
        normalized = normalize_repo_url(url)
        if not url or not normalized:
            rejected.append((os.path.basename(path), url, "没有地址"))
            continue

        if normalized in known:
            # 已收录：归档为 accepted（幂等），不计新增
            print(f"  [{url}] 已在清单里，跳过")
            _archive(path, ACCEPTED_DIR, {"result": "already-known", "url": url})
            continue

        try:
            text = ut.http_get_mirrors(url, timeout=45)
            doc = ut.parse_repo_json(text)
        except Exception as error:  # noqa: BLE001
            rejected.append((os.path.basename(path), url, f"抓不到：{error}"))
            _archive(path, REJECTED_DIR, {"result": "fetch-failed", "url": url, "reason": str(error)})
            continue

        valid, total, reason = check_repo(doc)
        if reason is not None:
            rejected.append((os.path.basename(path), url, reason))
            _archive(path, REJECTED_DIR, {"result": "invalid", "url": url, "reason": reason, "entries": total})
            continue

        known[normalized] = url
        accepted.append((url, normalized, valid))
        _archive(path, ACCEPTED_DIR, {"result": "accepted", "url": url, "valid": valid, "entries": total})

    if accepted:
        write_list(args.out, known)
        print(f"收录 {len(accepted)} 条 / 打回 {len(rejected)} 条 -> {args.out}")
    else:
        print(f"没有新收录（打回 {len(rejected)} 条）")

    for url, reason in [(u, r) for _, u, r in rejected]:
        print(f"  [打回] {url}：{reason}")

    if args.notify_out:
        lines = []
        for url, _, valid in accepted:
            lines.append(f"+ {url}（{valid} 条插件）")
        for _, url, reason in rejected:
            lines.append(f"- {url}：{reason}")
        if lines:
            with open(args.notify_out, "w", encoding="utf-8") as handle:
                handle.write("\n".join(lines))

    # 没有新收录也没有打回（全是已收录）时不触发翻译；有打回要通知维护者看一眼
    print(f"RESULT accepted={len(accepted)} rejected={len(rejected)}")
    return 0


def write_list(path: str, known: dict[str, str]) -> None:
    urls = sorted({url for url in known.values() if url})
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("# FireGaze 社区投稿的仓库清单（由 .github/workflows/repo-inbox.yml 调用 scripts/inbox_repo.py 追加）\n")
        handle.write("#\n")
        handle.write("# 与 scripts/repos.txt 分开维护：repos.txt 是维护者用 export-repo-list.py 从本机 Dalamud 配置\n")
        handle.write("# 整份重导出的，不能混写；这份只装玩家在「插件发现 → 投稿插件库」里投来的、过了检测的地址。\n")
        handle.write("#\n")
        handle.write("# 用法：python scripts/update_translations.py --repos scripts/repos.txt,scripts/community-repos.txt\n")
        for url in urls:
            handle.write(url + "\n")


def _archive(path: str, target_dir: str, result: dict) -> None:
    """把原始投稿 JSON 加结果字段后移进归档目录（保留证据、也避免下次重复处理）。"""
    try:
        os.makedirs(target_dir, exist_ok=True)
        with open(path, encoding="utf-8-sig") as handle:
            payload = json.load(handle)
        payload["result"] = result
        target = os.path.join(target_dir, os.path.basename(path))
        with open(target, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(payload, handle, ensure_ascii=False, indent=1)
            handle.write("\n")
        os.remove(path)
    except Exception as error:  # noqa: BLE001
        print(f"  （归档 {path} 失败：{error}）")


if __name__ == "__main__":
    sys.exit(main())
