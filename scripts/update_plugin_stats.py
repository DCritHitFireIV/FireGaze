#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""update_plugin_stats.py — 把中继的「插件发现」统计写进仓库。

- `discovery/stats.json`：最新一份（周赞 / 总赞 / 加库推荐；客户端离线时的兜底读数也会用它）
- `discovery/archive/<ISO周>.json`：每周一份，周内每次覆盖；跨周后不再动
  —— 周赞不清零，每周的历史都留在 git 里（用户 2026-10-06 定）。

中继没绑定 KV 或不可用时安静跳过（不把定时任务刷红）。
"""

from __future__ import annotations

import json
import os
import sys
import urllib.request

RELAY = os.environ.get("FIREGAZE_RELAY", "https://firegaze-relay.yuoonmail.workers.dev/plugin-stats")


def main() -> int:
    try:
        request = urllib.request.Request(
            RELAY,
            headers={
                "Accept": "application/json",
                "User-Agent": "FireGaze-CI/1.0 (+https://github.com/DCritHitFireIV/FireGaze)",
            },
        )
        with urllib.request.urlopen(request, timeout=30) as response:  # noqa: S310
            data = json.loads(response.read().decode("utf-8"))
    except Exception as error:  # noqa: BLE001
        print(f"中继不可用，跳过：{error}")
        return 0

    if not data.get("ok") or not data.get("week"):
        print(f"中继还没有插件统计端点（返回 {data.get('service')}/{data.get('version')}），跳过")
        return 0

    week = str(data.get("week") or "unknown")
    payload = {
        "updatedAt": data.get("updatedAt"),
        "week": week,
        "total": data.get("total") or {},
        "weekly": data.get("weekly") or {},
        "adds": data.get("adds") or {},
    }

    os.makedirs(os.path.join("discovery", "archive"), exist_ok=True)
    for path in (os.path.join("discovery", "stats.json"), os.path.join("discovery", "archive", f"{week}.json")):
        with open(path, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(payload, handle, ensure_ascii=False, indent=1, sort_keys=True)
            handle.write("\n")

    print(
        f"周={week}：总赞 {len(payload['total'])} 个插件 / 本周 {len(payload['weekly'])} 个 / "
        f"加库推荐 {len(payload['adds'])} 个"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
