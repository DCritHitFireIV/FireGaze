#!/usr/bin/env python3
"""把本机已经翻好的插件译文包导出成公共配套库（uit-packs/）。

- 输入：XIVLauncherCN/pluginConfigs/FireGaze/uitrans/*.json（本机译文包，UITextPack 格式）
- 输出：uit-packs/<内部名>.json + uit-packs/index.json（与 scripts/uit_library_build.py 的格式一致）

口径：
- 只导出**有译文**的条目；未译条目留给 CI 的增量翻译去补。
- PreserveID 一律写 false：库合并到别人包里是 `|=`，带 true 会把本地修正过的判定重新带坏
  （2026-10-02：拼接片段被加 ###原文、ImGui 控件全联动的教训）。
- Source 一律写 library：下载者看到的是「公共配套库」，不冒充别人手翻。
- Review / Skipped（本机复核与不翻标记）不导出——那是本地决定，不该外溢。

用法：
    python scripts/export_local_library.py --dry-run      # 只看统计
    python scripts/export_local_library.py               # 写入 uit-packs/
"""
from __future__ import annotations

import argparse
import io
import json
import os
import sys
import time

DEFAULT_PACKS_DIR = os.path.expandvars(
    r"%APPDATA%\XIVLauncherCN\pluginConfigs\FireGaze\uitrans"
)
DEFAULT_OUT_DIR = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "uit-packs"
)


def translated(item: dict) -> bool:
    return bool((item.get("Translated") or "").strip())


def export_pack(pack: dict) -> dict | None:
    """本机包 → 库包；没有任何译文时返回 None（不产出空包）。"""
    entries = []
    for item in pack.get("entries") or []:
        if not translated(item) or not (item.get("Original") or ""):
            continue
        entries.append(
            {
                "Original": item["Original"],
                "Translated": item["Translated"],
                "Context": item.get("Context") or "",
                "PreserveID": False,
                "Source": "library",
            }
        )

    resources = []
    for item in pack.get("resources") or []:
        if not translated(item):
            continue
        resources.append(
            {
                "Container": item.get("Container") or "",
                "Key": item.get("Key") or "",
                "Original": item.get("Original") or "",
                "Translated": item["Translated"],
                "Source": "library",
            }
        )

    attributes = []
    for item in pack.get("attributes") or []:
        if not translated(item) or not (item.get("Original") or ""):
            continue
        attributes.append(
            {
                "Original": item["Original"],
                "Context": item.get("Context") or "",
                "Translated": item["Translated"],
                "Source": "library",
            }
        )

    if not entries and not resources and not attributes:
        return None

    meta = pack.get("_meta") or {}
    return {
        "_meta": {
            "format": 2,
            "updatedAt": time.strftime("%Y-%m-%d"),
            "pluginVersion": meta.get("pluginVersion"),
            "source": "library",
        },
        "entries": entries,
        "resources": resources,
        "attributes": attributes,
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--packs-dir", default=DEFAULT_PACKS_DIR)
    parser.add_argument("--out", default=DEFAULT_OUT_DIR)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args(argv)

    if not os.path.isdir(args.packs_dir):
        print(f"找不到本机包目录：{args.packs_dir}")
        return 1

    exported = 0
    total_entries = 0
    total_resources = 0
    total_attributes = 0
    for file_name in sorted(os.listdir(args.packs_dir)):
        if not file_name.endswith(".json"):
            continue
        internal_name = file_name[:-5]
        path = os.path.join(args.packs_dir, file_name)
        try:
            pack = json.loads(io.open(path, encoding="utf-8").read())
        except Exception as error:  # noqa: BLE001
            print(f"  [{internal_name}] 读不了，跳过：{error}")
            continue
        if not isinstance(pack, dict) or "entries" not in pack:
            continue  # 不是译文包（如别的 json）

        library_pack = export_pack(pack)
        if library_pack is None:
            print(f"  [{internal_name}] 没有可导出的译文，跳过")
            continue

        entries = len(library_pack["entries"])
        resources = len(library_pack["resources"])
        attributes = len(library_pack["attributes"])
        total_entries += entries
        total_resources += resources
        total_attributes += attributes
        exported += 1
        print(f"  [{internal_name}] 条目 {entries} · 资源 {resources} · 属性 {attributes}")

        if not args.dry_run:
            os.makedirs(args.out, exist_ok=True)
            target = os.path.join(args.out, file_name)
            io.open(target, "w", encoding="utf-8", newline="\n").write(
                json.dumps(library_pack, ensure_ascii=False, indent=2) + "\n"
            )

    if not args.dry_run and exported > 0:
        write_index(args.out)

    print()
    print(f"共 {exported} 个插件：条目 {total_entries} · 资源 {total_resources} · 属性 {total_attributes}")
    if args.dry_run:
        print("（dry-run，没有写盘）")
    else:
        print(f"已写入 {args.out}")
    return 0


def write_index(out_dir: str) -> None:
    """汇总 out/ 下所有包成索引（保留以前构建过、这次没导出的）。"""
    plugins: dict[str, dict] = {}
    for file_name in sorted(os.listdir(out_dir)):
        if not file_name.endswith(".json") or file_name == "index.json":
            continue
        path = os.path.join(out_dir, file_name)
        try:
            pack = json.loads(io.open(path, encoding="utf-8").read())
        except Exception:  # noqa: BLE001
            continue
        meta = pack.get("_meta") or {}
        plugins[file_name[:-5]] = {
            "file": file_name,
            "updatedAt": meta.get("updatedAt"),
            "entries": len(pack.get("entries") or []),
            "resources": len(pack.get("resources") or []),
            "attributes": len(pack.get("attributes") or []),
        }

    index = {"updatedAt": time.strftime("%Y-%m-%d"), "plugins": plugins}
    io.open(os.path.join(out_dir, "index.json"), "w", encoding="utf-8", newline="\n").write(
        json.dumps(index, ensure_ascii=False, indent=2) + "\n"
    )
    print(f"索引：{len(plugins)} 个插件 -> {os.path.join(out_dir, 'index.json')}")


if __name__ == "__main__":
    sys.exit(main())
