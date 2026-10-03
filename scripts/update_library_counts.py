#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""update_library_counts.py — 把中继里的公共库下载量写回 `uit-packs/index.json`。

背景（2026-10-04 用户定）：译文包本体仍从 raw/镜像下载（CDN 不计下载量），下载量由插件在
下载成功后上报给中继（`/library-download`），中继存 KV；本脚本（`library-counts.yml` 每 6 小时
+ 手动触发）把计数同步进索引，插件界面就能显示每个包的「下载数 N」。

口径：
  · 只更新索引里**已有**的插件条目，不新增/删除条目（索引结构由库构建脚本负责）；
  · 中继没配 KV（counting disabled）时安静跳过——不算失败（绑定还没做时不该把定时任务刷红）。

用法：
    python scripts/update_library_counts.py [--index uit-packs/index.json] [--url <中继地址>]
"""
from __future__ import annotations

import argparse
import io
import json
import os
import sys
import urllib.request

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(SCRIPT_DIR)
RELAY_DEFAULT = "https://firegaze-relay.yuoonmail.workers.dev/library-counts"


def main(argv=None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--index", default=os.path.join(REPO_ROOT, "uit-packs", "index.json"))
    parser.add_argument("--url", default=RELAY_DEFAULT)
    args = parser.parse_args(argv)

    try:
        request = urllib.request.Request(args.url, headers={
            # 带一个正常 UA：Cloudflare 会对默认的 Python-urllib UA 回 403（实测）
            "User-Agent": "FireGaze-CI/1.0 (+https://github.com/DCritHitFireIV/FireGaze)",
            "Accept": "application/json",
        })
        with urllib.request.urlopen(request, timeout=30) as response:
            payload = json.load(response)
    except Exception as error:  # noqa: BLE001
        # 下载量不是关键数据：拉不到就跳过这一轮，不把 6 小时一次的定时任务刷红。
        print(f"拉取下载量失败（跳过这一轮）：{error}")
        return 0

    if not payload.get("ok"):
        if payload.get("error") == "counting disabled":
            print("中继还没有绑定 LIBRARY_COUNTS（KV）：跳过（绑定后这次失败就会消失）")
            return 0
        print(f"中继返回错误：{payload}")
        return 1

    # 中继还没升级到 v5 时，/library-counts 会落到通用 GET（返回 service/version）——
    # 没这个字段就当「还没开计数」，绝不能把 0 写进索引（2026-10-04 实测踩过）。
    if not isinstance(payload.get("counts"), dict):
        print(f"中继还没提供计数（回应：{payload}）：跳过")
        return 0

    counts = payload.get("counts") or {}
    likes = payload.get("likes") or {}
    index = json.load(io.open(args.index, encoding="utf-8"))
    plugins = index.setdefault("plugins", {})

    changed = 0
    for name, entry in plugins.items():
        if not isinstance(entry, dict):
            continue

        packs = entry.get("packs") or [{"id": "library"}]
        base = None
        for pack in packs:
            if not isinstance(pack, dict):
                continue
            pack_id = str(pack.get("id") or "library")
            key = f"{name}@{pack_id}"
            downloads = int(counts.get(key) or 0)
            like_count = int(likes.get(key) or 0)
            if pack.get("downloads") != downloads:
                pack["downloads"] = downloads
                changed += 1
            if pack.get("likes") != like_count:
                pack["likes"] = like_count
                changed += 1
            if pack_id == "library":
                base = pack

        # 插件级旧字段（旧客户端只看这些）= 基础包的计数
        if base is not None:
            if entry.get("downloads") != base.get("downloads"):
                entry["downloads"] = base.get("downloads")
                changed += 1
            if entry.get("likes") != base.get("likes"):
                entry["likes"] = base.get("likes")
                changed += 1

    if changed:
        io.open(args.index, "w", encoding="utf-8", newline="\n").write(
            json.dumps(index, ensure_ascii=False, indent=2) + "\n"
        )
        print(f"更新 {changed} 个包的下载量（中继共 {len(counts)} 个计数）")
    else:
        print("下载量没有变化")
    return 0


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(errors="replace")
    except Exception:  # noqa: BLE001
        pass
    sys.exit(main())
