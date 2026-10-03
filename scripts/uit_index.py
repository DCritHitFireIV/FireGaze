#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""uit_index.py — 公共译文库索引的**唯一**构建口径（2026-10-04 多包模型）。

存储（2026-10-04 用户定）：
  · 基础包 `<插件>.json`：维护者翻译/导出、云端管线生成——**一个插件一份**；
  · 玩家包 `<插件>@<包ID>.json`：**每次收录的投稿新建一个**（包ID = `user-<内容指纹前6位>`，匿名且稳定）；
  · 索引 `index.json` 里每个插件长这样：
        {"file": "X.json", "entries": N, ...  ← 基础包的旧字段（旧客户端只看这些）
         "packs": [{"id":"library","file":"X.json","label":"基础包","source":"library",...},
                   {"id":"user-ab12cd","file":"X@user-ab12cd.json","label":"玩家包 · ab12cd","source":"user",...}]}

下载量（downloads）与 👍（likes）是**按包**统计的（中继 KV，键 `<插件>@<包ID>`），
由 update_library_counts.py 定时写回；本模块重建索引时必须原样保留这两列。
"""
from __future__ import annotations

import json
import os

PACK_SEPARATOR = "@"
BASE_PACK_ID = "library"
BASE_PACK_LABEL = "基础包"


def load_json(path: str):
    try:
        with open(path, encoding="utf-8") as handle:
            return json.load(handle)
    except Exception:  # noqa: BLE001
        return None


def pack_id_of(file_name: str) -> tuple[str, str]:
    """`X@user-ab12cd.json` → (插件名, 包ID)；`X.json` → (X, library)。"""
    stem = file_name[:-5]
    if PACK_SEPARATOR in stem:
        plugin, pack_id = stem.split(PACK_SEPARATOR, 1)
        return plugin, pack_id
    return stem, BASE_PACK_ID


def _counts(pack: dict) -> tuple[int, int, int]:
    return (
        len(pack.get("entries") or []),
        len(pack.get("resources") or []),
        len(pack.get("attributes") or []),
    )


def _pack_entry(pack: dict, file_name: str, pack_id: str, old_pack: dict | None) -> dict:
    meta = pack.get("_meta") or {}
    entries, resources, attributes = _counts(pack)
    label = str(meta.get("label") or "").strip()
    if not label:
        label = BASE_PACK_LABEL if pack_id == BASE_PACK_ID else f"玩家包 · {pack_id}"
    item = {
        "id": pack_id,
        "file": file_name,
        "label": label,
        "source": str(meta.get("source") or ("library" if pack_id == BASE_PACK_ID else "user")),
        "entries": entries,
        "resources": resources,
        "attributes": attributes,
        "updatedAt": meta.get("updatedAt"),
    }
    # 下载量与👍由中继统计（update_library_counts.py 写回）：重建时必须保留
    if isinstance(old_pack, dict):
        if "downloads" in old_pack:
            item["downloads"] = old_pack["downloads"]
        if "likes" in old_pack:
            item["likes"] = old_pack["likes"]
    return item


def collect(packs_dir: str, existing_plugins: dict | None = None) -> dict:
    """扫描 packs_dir，返回 index.json 的 `plugins` 表（基础包 + 各玩家包）。"""
    existing = existing_plugins or {}
    files = sorted(f for f in os.listdir(packs_dir) if f.endswith(".json") and f != "index.json")

    # 按插件分组（基础包与它的 @ 包归到一起；没有基础包只有玩家包的插件也保留）
    grouped: dict[str, list[str]] = {}
    for file_name in files:
        plugin, _ = pack_id_of(file_name)
        grouped.setdefault(plugin, []).append(file_name)

    plugins: dict[str, dict] = {}
    for plugin in sorted(grouped):
        base_file = plugin + ".json"
        old_entry = existing.get(plugin) if isinstance(existing.get(plugin), dict) else {}
        old_packs = {
            str(p.get("id") or ""): p
            for p in (old_entry.get("packs") or [])
            if isinstance(p, dict)
        }

        packs: list[dict] = []
        base_pack = None
        for file_name in grouped[plugin]:
            pack = load_json(os.path.join(packs_dir, file_name))
            if pack is None:
                continue
            _, pack_id = pack_id_of(file_name)
            if pack_id == BASE_PACK_ID:
                base_pack = pack
            packs.append(_pack_entry(pack, file_name, pack_id, old_packs.get(pack_id)))

        # 基础包永远排第一（旧客户端与「基础包优先合入」的语义都靠这个顺序）
        packs.sort(key=lambda item: (item["id"] != BASE_PACK_ID, item["id"]))

        entry: dict = {
            "file": base_file,
            "updatedAt": (base_pack or {}).get("_meta", {}).get("updatedAt") if base_pack else None,
            "entries": 0,
            "resources": 0,
            "attributes": 0,
        }
        if base_pack is not None:
            entry["entries"], entry["resources"], entry["attributes"] = _counts(base_pack)
        if base_pack is None:
            # 只有玩家包的插件：旧字段留 0；旧客户端拿不到东西（没有基础包），新客户端按 packs 走
            entry["updatedAt"] = packs[0]["updatedAt"] if packs else None

        # 旧索引的插件级 downloads/likes（基础包的）也保留一份，兼容只读旧字段的客户端
        if "downloads" in old_entry:
            entry["downloads"] = old_entry["downloads"]
        elif "downloads" in (packs[0] if packs else {}):
            entry["downloads"] = packs[0]["downloads"]
        if "likes" in old_entry:
            entry["likes"] = old_entry["likes"]

        entry["packs"] = packs
        plugins[plugin] = entry

    return plugins
