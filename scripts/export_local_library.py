#!/usr/bin/env python3
"""把本机已经翻好的插件译文包导出/合并成公共配套库（uit-packs/）。

- 输入：XIVLauncherCN/pluginConfigs/FireGaze/uitrans/*.json（本机译文包，UITextPack 格式）
- 输出：uit-packs/<内部名>.json + uit-packs/index.json（与 scripts/uit_library_build.py 的格式一致）

合并口径（2026-10-02 用户定）：
- **默认合并**（--replace 才整包覆盖）：以已上传的库包为底，本机有译文的条目**以本机为准**
  （同键译文不同 → 覆盖；相同 → 保留），本机没有的库条目**原样保留**，本机独有的**追加**。
- 只导出**有译文**的条目；未译条目等云端管线手动触发或玩家投稿去补。
- PreserveID 一律写 false：库合并到别人包里是 `|=`，带 true 会把本地修正过的判定重新带坏
  （2026-10-02：拼接片段被加 ###原文、ImGui 控件全联动的教训）。
- Source 一律写 library：下载者看到的是「公共配套库」，不冒充别人手翻。
- Review / Skipped（本机复核与不翻标记）不导出——那是本地决定，不该外溢。
  例外（2026-10-03）：系统「自动排除」的带译文条目要导出——它标的是「已不在新一轮抽取的候选里」，
  最常见成因是「这条已经翻好并打进了 DLL」，不收就会出现「本机翻得越全、库里反而越少」。
- 「中文插件」名单一律跳过、也不碰库里已有的包。

用法：
    python scripts/export_local_library.py --dry-run      # 只看统计
    python scripts/export_local_library.py               # 合并写入 uit-packs/
    python scripts/export_local_library.py --replace     # 整包覆盖（慎用）
"""
from __future__ import annotations

import argparse
import io
import json
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import uit_index  # noqa: E402 索引口径（基础包 + 玩家包）

DEFAULT_PACKS_DIR = os.path.expandvars(
    r"%APPDATA%\XIVLauncherCN\pluginConfigs\FireGaze\uitrans"
)
DEFAULT_OUT_DIR = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "uit-packs"
)

# 与插件端 UITextRules.IsDoNotLocalize、CI 的 uit_library_build.py 同一份口径。
DO_NOT_LOCALIZE = {
    "aeassistv3", "aeassist", "dailyroutines", "omnitoolbox", "xsztoolbox",
    "kodakkuassist", "promerotation", "nyadraw", "i-ching-gl", "missfisher",
    "lightlesssync", "lightlesscn", "sillytoolbox", "pvpauto", "bocchi",
    "aetherphonecn", "avantgardecn", "bossmodreborncn", "brio-cn", "dalamudcnadapter",
    "damagemeter-cn", "extrachat-cn", "fuckdalamudcn", "hyperboreacn", "icecn",
    "inventorytoolscn", "itemvendorlocationcn", "mahjong.plugin.cn", "malmstonecn", "marketroutecn",
    "ocnfarmer", "pvplogscn", "priceinsight-cn", "pvpstatscn", "retainerrepricercn",
    "tidychatcn", "vfxeditorcn", "xivcomboexpandedcn", "visland_cn", "keitatoolbox",
    "pfclassifier", "pfradar", "pvpselector", "untarnishedheart", "autohook",
    "dctravelerx", "proxyplugin",
    "coyote-ffxiv", "ffxivnetworkpacketanalysistool", "gposeresizer",
    "cleanwindow", "autotriadc", "ttc_siren", "coordimporter",
    # 2026-10-03：XTeleport / 琉璃的宝石 / Anmi 的插件 / VanillaPlus / Inverse Kinematics
    "teleport", "ruri", "arenapilot", "beastmaster", "chronicler", "phantom",
    "dalamudact", "vanillaplus", "footik",
}


def translated(item: dict) -> bool:
    return bool((item.get("Translated") or "").strip())


# 与插件侧 UITextPack.AutoSkipNotePrefix 同一份口径（包含 v1.2.0.37 用过的旧文案）。
AUTO_SKIP_MARKERS = ("\u81ea\u52a8\u6392\u9664\uff1a", "\u65b0\u4e00\u8f6e\u62bd\u53d6\u5df2\u6392\u9664")


def is_auto_skipped(item: dict) -> bool:
    """这条「不翻」是不是系统自动排的——不是人的判定。

    自动排除的含义是「已不在新一轮抽取的候选里」，最常见的成因就是**它已经被翻好、
    打进了 DLL**（字面量变成 `译文###原文`，抽取器判「已是中文」不再当候选）。
    这类条目恰恰是库最该收的译文：不收就会出现「本机翻得越全、库里反而越少」
    （2026-10-03 实测：漏掉 1,188 条，BossMod 一家 883 条）。
    人工标的「不翻」没有这个备注，仍然排除。
    """
    review = item.get("Review") or ""
    return any(review.startswith(marker) for marker in AUTO_SKIP_MARKERS)


KANA_RE = re.compile(r"[\u3040-\u30ff\u31f0-\u31ff\uff66-\uff9d]")
HAN_RE = re.compile(r"[\u4e00-\u9fff]")


def is_already_chinese(text: str) -> bool:
    """有汉字、没有假名 —— 与插件端 UITextRules.IsAlreadyChinese 同一口径。"""
    if not text:
        return False
    return bool(HAN_RE.search(text)) and not KANA_RE.search(text)


def display_part(literal: str) -> str:
    """ImGui 标签的显示段：第一个 ### 之前（没有就整条）。"""
    return literal.split("###", 1)[0]


def export_pack(pack: dict) -> tuple[dict | None, int, int, int, int]:
    """本机包 → 库包；返回 (库包或 None, 已是中文跳过的条数, 本机不翻跳过的条数, 自动排除收回数, 标识符跳过的条数)。

    已经是中文的（含原生「中文###ID」标签）不导出：它们不需要翻译，翻出来的也只是噪声
    （2026-10-02 扫出 AetherBlackbox / ARSR / ArmoireButler 一批）。
    本机标了「不翻」的也不导出（2026-10-03）：那是对「这个字符串不该翻」的本地判定，
    不该随库外溢——否则会把图标资源名 / 缓存 id 这类不该翻的条目教给别人。
    **例外（同日追加）**：系统「自动排除」的条目照常导出——它标的是「已不在新一轮抽取的候选里」，
    最常见成因是「这条已经翻好并打进了 DLL」，正是库最该收的译文。
    **标识符不导出（2026-10-10）**：Role=Excluded 是插件 1.4.0.17 起对插件 / IPC 标识符的
    判定——它们可能同时画在界面上，但翻出去会破坏插件识别，一条都不进库。
    """
    skipped_chinese = 0
    skipped_local = 0
    recovered_auto = 0
    skipped_identities = 0
    skipped_set = set(pack.get("skipped") or [])
    skipped_res_set = set(pack.get("skippedResources") or [])
    skipped_attr_set = set(pack.get("skippedAttributes") or [])
    entries = []
    for item in pack.get("entries") or []:
        if not translated(item) or not (item.get("Original") or ""):
            continue
        if (item.get("Role") or "") == "Excluded":
            skipped_identities += 1
            continue
        if item["Original"] in skipped_set:
            if is_auto_skipped(item):
                recovered_auto += 1
            else:
                skipped_local += 1
                continue
        if is_already_chinese(display_part(item["Original"])):
            skipped_chinese += 1
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
        if (item.get("Container") or "") + "\u0001" + (item.get("Key") or "") in skipped_res_set:
            if is_auto_skipped(item):
                recovered_auto += 1
            else:
                skipped_local += 1
                continue
        if is_already_chinese(item.get("Original") or item.get("Translated") or ""):
            skipped_chinese += 1
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
        if item["Original"] in skipped_attr_set:
            if is_auto_skipped(item):
                recovered_auto += 1
            else:
                skipped_local += 1
                continue
        if is_already_chinese(display_part(item["Original"])):
            skipped_chinese += 1
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
        return None, skipped_chinese, skipped_local, recovered_auto, skipped_identities

    meta = pack.get("_meta") or {}
    library_pack = {
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
    return library_pack, skipped_chinese, skipped_local, recovered_auto, skipped_identities


def _overlay(base: list[dict], local: list[dict], key) -> tuple[list[dict], int, int, int, int]:
    """底稿里已有的保留（本机同键则替换），本机独有的追加。

    返回 (结果, 覆盖, 新增, 相同, 保留)。顺序：底稿顺序优先，新增追加在末尾。
    """
    result: dict = {}
    for item in base or []:
        result[key(item)] = item

    overridden = 0
    added = 0
    same = 0
    for item in local or []:
        k = key(item)
        if k in result:
            if (result[k].get("Translated") or "") != (item.get("Translated") or ""):
                overridden += 1
            else:
                same += 1
        else:
            added += 1
        result[k] = item

    kept = len(result) - len(local or [])
    return list(result.values()), overridden, added, same, max(0, kept)


def merge_library(existing: dict, local: dict) -> tuple[dict, tuple[int, int, int, int]]:
    """把本机库包合并到已上传的库包上（本机为准、缺的补齐）。"""
    entries, o1, a1, s1, k1 = _overlay(existing.get("entries") or [], local.get("entries") or [], lambda x: x.get("Original"))
    resources, o2, a2, s2, k2 = _overlay(
        existing.get("resources") or [],
        local.get("resources") or [],
        lambda x: (x.get("Container"), x.get("Key")),
    )
    attributes, o3, a3, s3, k3 = _overlay(
        existing.get("attributes") or [],
        local.get("attributes") or [],
        lambda x: x.get("Original"),
    )

    merged = {
        "_meta": local.get("_meta") or existing.get("_meta") or {},
        "entries": entries,
        "resources": resources,
        "attributes": attributes,
    }
    return merged, (o1 + o2 + o3, a1 + a2 + a3, s1 + s2 + s3, k1 + k2 + k3)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--packs-dir", default=DEFAULT_PACKS_DIR)
    parser.add_argument("--out", default=DEFAULT_OUT_DIR)
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--replace", action="store_true", help="整包覆盖而不是合并（慎用）")
    args = parser.parse_args(argv)

    if not os.path.isdir(args.packs_dir):
        print(f"找不到本机包目录：{args.packs_dir}")
        return 1

    exported = 0
    total_entries = 0
    total_resources = 0
    total_attributes = 0
    total_overridden = 0
    total_added = 0
    total_skipped_local = 0
    total_skipped_identities = 0
    for file_name in sorted(os.listdir(args.packs_dir)):
        if not file_name.endswith(".json"):
            continue
        internal_name = file_name[:-5]
        if internal_name.lower() in DO_NOT_LOCALIZE:
            print(f"  [{internal_name}] 中文插件，跳过")
            continue
        path = os.path.join(args.packs_dir, file_name)
        try:
            pack = json.loads(io.open(path, encoding="utf-8").read())
        except Exception as error:  # noqa: BLE001
            print(f"  [{internal_name}] 读不了，跳过：{error}")
            continue
        if not isinstance(pack, dict) or "entries" not in pack:
            continue  # 不是译文包（如别的 json）

        library_pack, skipped_chinese, skipped_local, recovered_auto, skipped_identities = export_pack(pack)
        if library_pack is None:
            print(f"  [{internal_name}] 没有可导出的译文，跳过")
            continue
        total_skipped_local += skipped_local
        total_skipped_identities += skipped_identities

        target = os.path.join(args.out, file_name)
        override_stats = ""
        if not args.replace and os.path.exists(target):
            try:
                existing = json.loads(io.open(target, encoding="utf-8").read())
            except Exception as error:  # noqa: BLE001
                print(f"  [{internal_name}] 库包读不了，按整包写入：{error}")
                existing = None
            if isinstance(existing, dict):
                library_pack, (over, add, same, kept) = merge_library(existing, library_pack)
                total_overridden += over
                total_added += add
                override_stats = f" ｜ 覆盖 {over} · 新增 {add} · 保留 {kept} · 相同 {same}"

        entries = len(library_pack["entries"])
        resources = len(library_pack["resources"])
        attributes = len(library_pack["attributes"])
        total_entries += entries
        total_resources += resources
        total_attributes += attributes
        exported += 1
        cn_note = f" ｜ 已是中文跳过 {skipped_chinese}" if skipped_chinese else ""
        cn_note += f" ｜ 不翻跳过 {skipped_local}" if skipped_local else ""
        cn_note += f" ｜ 自动排除收回 {recovered_auto}" if recovered_auto else ""
        cn_note += f" ｜ 标识符跳过 {skipped_identities}" if skipped_identities else ""
        print(f"  [{internal_name}] 条目 {entries} · 资源 {resources} · 属性 {attributes}{override_stats}{cn_note}")

        if not args.dry_run:
            os.makedirs(args.out, exist_ok=True)
            io.open(target, "w", encoding="utf-8", newline="\n").write(
                json.dumps(library_pack, ensure_ascii=False, indent=2) + "\n"
            )

    if not args.dry_run and exported > 0:
        write_index(args.out)

    print()
    print(
        f"共 {exported} 个插件：条目 {total_entries} · 资源 {total_resources} · 属性 {total_attributes}"
        f"（相对库里已有：覆盖 {total_overridden} · 新增 {total_added}；本机不翻跳过 {total_skipped_local} · 标识符跳过 {total_skipped_identities}）"
    )
    if args.dry_run:
        print("（dry-run，没有写盘）")
    else:
        print(f"已写入 {args.out}")
    return 0


def write_index(out_dir: str) -> None:
    """汇总 out/ 下所有包成索引（保留以前构建过、这次没导出的；基础包 + 玩家包）。"""
    index_path = os.path.join(out_dir, "index.json")
    existing: dict = {}
    if os.path.exists(index_path):
        try:
            existing = json.loads(io.open(index_path, encoding="utf-8").read()).get("plugins") or {}
        except Exception:  # noqa: BLE001
            existing = {}
    plugins = uit_index.collect(out_dir, existing)
    index = {"updatedAt": time.strftime("%Y-%m-%d"), "plugins": plugins}
    io.open(index_path, "w", encoding="utf-8", newline="\n").write(
        json.dumps(index, ensure_ascii=False, indent=2) + "\n"
    )
    print(f"索引：{len(plugins)} 个插件 -> {index_path}")


if __name__ == "__main__":
    sys.exit(main())
