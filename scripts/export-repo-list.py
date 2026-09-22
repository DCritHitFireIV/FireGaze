#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""export-repo-list.py — 从 Dalamud 配置里导出「这台机器订阅了哪些插件仓库」。

给周更工作流用：`update_translations.py --repos scripts/repos.txt`。
为什么需要它：Aetherfeed 那份语料**完全没有 Punchline（一行简介）**，
而仓库文件（pluginmaster.json）里才有 —— 拿真实仓库列表去抓，一行简介才不会漏。

用法：
    python scripts/export-repo-list.py                       # 默认读 %APPDATA%\XIVLauncherCN\dalamudConfig.json
    python scripts/export-repo-list.py <config.json> <out.txt>
"""
from __future__ import annotations

import json
import os
import sys

DEFAULT_CONFIG = os.path.join(
    os.environ.get("APPDATA", ""), "XIVLauncherCN", "dalamudConfig.json"
)
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_OUT = os.path.join(REPO_ROOT, "scripts", "repos.txt")


def main(argv=None) -> int:
    args = (argv if argv is not None else sys.argv[1:])
    config = args[0] if args else DEFAULT_CONFIG
    out = args[1] if len(args) > 1 else DEFAULT_OUT

    with open(config, encoding="utf-8-sig") as handle:
        doc = json.load(handle)

    node = doc.get("ThirdRepoList")
    if isinstance(node, dict):
        node = node.get("$values")

    urls: list[str] = []
    for item in node or []:
        if isinstance(item, dict) and item.get("Url"):
            urls.append(item["Url"])
        elif isinstance(item, str):
            urls.append(item)

    urls = sorted(set(urls))
    with open(out, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("# FireGaze 周更用的仓库清单（由 scripts/export-repo-list.py 导出）\n")
        handle.write("# 用法：python scripts/update_translations.py --repos scripts/repos.txt\n")
        for url in urls:
            handle.write(url + "\n")

    print(f"已导出 {len(urls)} 个仓库地址 -> {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
