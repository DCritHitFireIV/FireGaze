#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""uit_library_build.py — CI 用：为指定插件生成「插件界面文字」的公共译文包（uit-packs/）。

口径（2026-10-02 用户定）：
  · 包是**纯译文数据**（原文→译文 + 资源容器/key），补丁仍在玩家本地由 FireGaze 打；
  · 字面量按「原文」、资源按「容器 + key」增量：没变的沿用旧译文，新文本才送模型；
  · 译文只标记 Source=library（玩家自己改过的译文永不被顶，合并逻辑在插件侧）；
  · 只收 UI 角色的字面量（灰名单不翻，与插件口径一致）；资源值已由抽取器过滤。

流程（每个目标插件）：
  1. Aetherfeed 找到它所在仓库 → 拉仓库文件 → 取 DownloadLinkInstall；
  2. 下载 zip、取出主 DLL（优先 `内部名.dll`，否则取最大的 DLL），按版本缓存；
  3. 跑 UITextProbe：`--json` 拿字面量判定、`--resources` 拿资源文本；
  4. 读已有包做增量，新条目用 DeepSeek 翻译（可带 FF14 术语表）；
  5. 写 `--out/<内部名>.json`，最后汇总 `--out/index.json`。

用法（GitHub Actions 里跑；本机只允许 --dry-run）：
    python scripts/uit_library_build.py --probe tools/UITextProbe/bin/Release/net10.0/UITextProbe.dll --out uit-packs
    python scripts/uit_library_build.py --dry-run            # 只报告缺多少条，不调模型不写盘
    python scripts/uit_library_build.py --targets A B C      # 临时指定几个插件
"""

from __future__ import annotations

import argparse
import glob
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import zipfile
import urllib.request

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(SCRIPT_DIR)
if SCRIPT_DIR not in sys.path:
    sys.path.insert(0, SCRIPT_DIR)

import update_translations as ut  # noqa: E402 复用 http_get / parse_repo_json / deepseek

AETHERFEED = ut.AETHERFEED
DEFAULT_TARGETS = os.path.join(SCRIPT_DIR, "uit_targets.txt")
DEFAULT_GLOSSARY = os.path.join(REPO_ROOT, "ffxiv-glossary.tsv")

# 「不汉化」名单（与插件端 UITextRules.IsDoNotLocalize 同一份口径，2026-10-02 用户定）：
# 中文插件、由朋友维护，汉化只会增加维护负担——云端库也不许给它们出包。
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
}

TRANSLATE_PROMPT = (
    "你是 FFXIV 插件界面的简体中文译者。输入 JSON 的每条 text 是插件界面上的英文文本"
    "（可能带 {0}、%s、\\n 之类的占位符或代码片段）。规则："
    "① 翻成自然、简短、口语化的简体中文；占位符、首尾空格、换行一律原样保留；"
    "② 专有名词（技能 / 副本 / 地名 / 状态 / 道具）用 FF14 国服官方译名；下面给了术语表就必须照用；"
    "③ 命令名（/xxx）、快捷键、URL、代码标识符、全大写缩写保持原样；"
    "④ 已经很短且是通用词（OK、HP、DPS…）可以不动；已经是中文的原样返回；"
    "⑤ 只输出 JSON，不要代码块、不要解释。"
    '输出格式：{"items":[{"id":<原 id>,"t":"<译文>"}]}'
)


# ------------------------------------------------------------------ 文本工具 --

def load_targets(path: str) -> list[str]:
    names: list[str] = []
    if not path or not os.path.exists(path):
        return names
    for line in io.open(path, encoding="utf-8"):
        line = line.strip()
        if line and not line.startswith("#") and line.lower() not in DO_NOT_LOCALIZE:
            names.append(line)
    return names


def load_glossary(path: str) -> list[tuple[str, str]]:
    pairs: list[tuple[str, str]] = []
    if not path or not os.path.exists(path):
        return pairs
    with io.open(path, encoding="utf-8", errors="replace") as handle:
        for line in handle:
            parts = line.rstrip("\n").split("\t")
            if len(parts) >= 2 and parts[0].strip() and parts[1].strip():
                pairs.append((parts[0].strip(), parts[1].strip()))
    return pairs


def build_glossary_index(pairs: list[tuple[str, str]]) -> dict[str, list[tuple[str, str]]]:
    """按术语的第一个单词建索引，避免每条文本都扫 3 万个术语。"""
    index: dict[str, list[tuple[str, str]]] = {}
    for english, chinese in pairs:
        head = re.split(r"\s+", english.lower())[0]
        index.setdefault(head, []).append((english, chinese))
    return index


def glossary_hits(text: str, index: dict[str, list[tuple[str, str]]]) -> dict[str, str]:
    lowered = text.lower()
    found: dict[str, str] = {}
    for word in set(re.findall(r"[a-z0-9'’\-.]+", lowered)):
        for english, chinese in index.get(word, ()):
            if english.lower() in lowered and english not in found:
                found[english] = chinese
    return found


# ------------------------------------------------------------------ 网络/下载 --

def http_get_bytes(url: str, timeout: int = 120, attempts: int = 3) -> bytes:
    last: Exception | None = None
    for attempt in range(attempts):
        try:
            request = urllib.request.Request(
                url.replace(" ", "%20"),
                headers={"User-Agent": "firegaze-uit-packs", "Accept": "*/*"},
            )
            with urllib.request.urlopen(request, timeout=timeout) as response:
                return response.read()
        except Exception as error:  # noqa: BLE001
            last = error
            time.sleep(1.5 * (attempt + 1))
    raise RuntimeError(f"下载失败：{url}：{last}")


def resolve_downloads(targets: list[str], cache_dir: str, refresh: bool) -> dict[str, tuple[str, str]]:
    """InternalName -> (下载链接, 版本号)。找不到的目标直接跳过。"""
    aetherfeed_cache = os.path.join(cache_dir, "aetherfeed.json")
    fresh = os.path.exists(aetherfeed_cache) and (time.time() - os.path.getmtime(aetherfeed_cache)) < 12 * 3600
    if refresh or not fresh:
        try:
            text = ut.http_get_mirrors(AETHERFEED)
            os.makedirs(cache_dir, exist_ok=True)
            io.open(aetherfeed_cache, "w", encoding="utf-8").write(text)
        except Exception as error:  # noqa: BLE001
            if not os.path.exists(aetherfeed_cache):
                raise
            print(f"  Aetherfeed 更新失败，用缓存：{error}")

    doc = json.loads(io.open(aetherfeed_cache, encoding="utf-8").read())
    wanted = set(targets)
    repo_of: dict[str, str] = {}
    for repo in doc:
        repo_url = repo.get("repo_url") or repo.get("repo_source_url") or ""
        if not repo_url:
            continue
        for plugin in repo.get("plugins") or []:
            name = (plugin.get("InternalName") or "").strip()
            if name in wanted and name not in repo_of:
                repo_of[name] = repo_url

    result: dict[str, tuple[str, str]] = {}
    for name in targets:
        repo_url = repo_of.get(name)
        if not repo_url:
            print(f"  [{name}] Aetherfeed 里找不到（不在公开库？）")
            continue
        try:
            manifest = ut.parse_repo_json(ut.http_get_mirrors(repo_url))
        except Exception as error:  # noqa: BLE001
            print(f"  [{name}] 仓库文件读不出来：{error}")
            continue
        for plugin in manifest if isinstance(manifest, list) else ():
            if (plugin.get("InternalName") or "").strip() != name:
                continue
            link = plugin.get("DownloadLinkInstall") or plugin.get("DownloadLink")
            if not link:
                print(f"  [{name}] 仓库里没有下载链接")
                continue
            version = str(plugin.get("AssemblyVersion") or "")
            result[name] = (str(link), version)
    return result


def load_rules() -> dict[str, list[str]]:
    """读仓库根目录的 uit-rules.json（与插件端同一份）：主程序集内部名 → 伴生 DLL 文件名。"""
    path = os.path.join(REPO_ROOT, "uit-rules.json")
    try:
        doc = json.loads(io.open(path, encoding="utf-8").read())
        return {k: list(v or []) for k, v in (doc.get("companions") or {}).items()}
    except Exception as error:  # noqa: BLE001
        print(f"  读 uit-rules.json 失败（只按同名前缀规则）：{error}")
        return {}


def ensure_dlls(name: str, url: str, version: str, cache_dir: str,
                companions_map: dict[str, list[str]]) -> list[str]:
    """下载插件包，取出主 DLL + 伴生 DLL（按名字前缀规则 or 名单），返回「主在前」的路径列表。"""
    out_dir = os.path.join(cache_dir, "dll", name)
    stamp = os.path.join(out_dir, ".version")
    main_path = os.path.join(out_dir, name + ".dll")
    if os.path.isdir(out_dir) and os.path.exists(stamp):
        if io.open(stamp, encoding="utf-8").read().strip() == version:
            files = sorted(glob.glob(os.path.join(out_dir, "*.dll")))
            if files:
                return ([main_path] if os.path.exists(main_path) else []) + [f for f in files if f != main_path]

    payload = http_get_bytes(url)
    os.makedirs(out_dir, exist_ok=True)
    for old in glob.glob(os.path.join(out_dir, "*.dll")):
        os.remove(old)

    listed = [c.lower() for c in companions_map.get(name, [])]
    if payload[:2] == b"PK":
        with zipfile.ZipFile(io.BytesIO(payload)) as archive:
            names = [n for n in archive.namelist() if n.lower().endswith(".dll")]
            if not names:
                raise RuntimeError("zip 里没有 DLL")
            wanted = (name + ".dll").lower()
            main = next((n for n in names if os.path.basename(n).lower() == wanted), None)
            if main is None:
                main = max(names, key=lambda n: archive.getinfo(n).file_size)
            main_stem = os.path.splitext(os.path.basename(main))[0]

            picked = [main]
            for n in names:
                base = os.path.basename(n)
                if base.lower() == os.path.basename(main).lower():
                    continue
                is_companion = base.lower().startswith(main_stem.lower() + ".")
                if is_companion or base.lower() in listed:
                    picked.append(n)

            for n in picked:
                target = os.path.join(out_dir, os.path.basename(n))
                with archive.open(n) as source, io.open(target, "wb") as destination:
                    shutil.copyfileobj(source, destination)
    else:
        io.open(main_path, "wb").write(payload)

    io.open(stamp, "w", encoding="utf-8").write(version or "0")
    files = sorted(glob.glob(os.path.join(out_dir, "*.dll")))
    return ([main_path] if os.path.exists(main_path) else []) + [f for f in files if f != main_path]


ROLE_RANK = {"UI": 0, "Ambiguous": 1, "Excluded": 2}


def merge_probe_entries(lists: list[list[dict]], file_names: list[str]) -> list[dict]:
    """与插件端 ExtractMany 同口径：同原文取更强判定，PreserveID 取或，多文件给 Context 加文件名前缀。"""
    merged: dict[str, dict] = {}
    multi = len(lists) > 1
    for index, items in enumerate(lists):
        tag = f"[{os.path.splitext(file_names[index])[0]}] "
        for raw in items:
            original = raw.get("Original") or ""
            if not original:
                continue
            item = dict(raw)
            if multi and not (item.get("Context") or "").startswith(tag):
                item["Context"] = tag + (item.get("Context") or "")
            old = merged.get(original)
            if old is None:
                merged[original] = item
                continue
            if item.get("PreserveID"):
                old["PreserveID"] = True
            if ROLE_RANK.get(item.get("Role"), 9) < ROLE_RANK.get(old.get("Role"), 9):
                item["PreserveID"] = bool(item.get("PreserveID") or old.get("PreserveID"))
                merged[original] = item
    return list(merged.values())


def merge_probe_resources(lists: list[list[dict]]) -> list[dict]:
    """资源按「容器 + key」去重。"""
    merged: dict[tuple[str, str], dict] = {}
    for items in lists:
        for item in items:
            key = (item.get("Container") or "", item.get("Key") or "")
            if key[0] and key[1] and key not in merged:
                merged[key] = item
    return list(merged.values())


def merge_probe_attributes(lists: list[list[dict]], file_names: list[str]) -> list[dict]:
    """属性字符串按「值」去重（与插件端一致，Context 记第一个宿主）。"""
    merged: dict[str, dict] = {}
    multi = len(lists) > 1
    for index, items in enumerate(lists):
        tag = f"[{os.path.splitext(file_names[index])[0]}] " if multi else ""
        for item in items:
            value = item.get("Value") or ""
            if not value or value in merged:
                continue
            merged[value] = {
                "Owner": tag + (item.get("Owner") or ""),
                "Attribute": item.get("Attribute") or "",
                "Value": value,
            }
    return list(merged.values())


def run_probe(probe: str, dll: str) -> tuple[list[dict], list[dict], list[dict]]:
    def call(flag: str) -> list[dict]:
        proc = subprocess.run(
            ["dotnet", probe, dll, flag],
            capture_output=True, text=True, encoding="utf-8", errors="replace",
        )
        if proc.returncode != 0:
            raise RuntimeError(f"{flag} 失败：{(proc.stderr or proc.stdout).strip()[:200]}")
        return json.loads(proc.stdout)

    return call("--json"), call("--resources"), call("--attributes")


# ------------------------------------------------------------------ 翻译 --

def translate_texts(api_key: str, texts: list[str], glossary: dict[str, list[tuple[str, str]]],
                    batch_size: int, sleep_seconds: float, limit: int) -> dict[str, str]:
    done: dict[str, str] = {}
    batches = [texts[i:i + batch_size] for i in range(0, len(texts), batch_size)]
    for batch_index, chunk in enumerate(batches):
        if limit and len(done) >= limit:
            print(f"    达到 --max-new {limit}，本插件其余条目留到下次")
            break
        terms: dict[str, str] = {}
        for text in chunk:
            terms.update(glossary_hits(text, glossary))
        system = TRANSLATE_PROMPT
        if terms:
            system += "\n\n术语表（必须照用）：\n" + "\n".join(f"- {en} → {zh}" for en, zh in sorted(terms.items()))

        payload = {"items": [{"id": i, "text": t} for i, t in enumerate(chunk)]}
        last_error: Exception | None = None
        for attempt in range(3):
            try:
                content = ut.deepseek(api_key, system, payload, max_tokens=8000)
                data = ut.parse_json_block(content)
                mapped = {}
                for item in data.get("items", []):
                    idx = item.get("id")
                    value = (item.get("t") or "").strip()
                    if isinstance(idx, int) and 0 <= idx < len(chunk) and value:
                        mapped[chunk[idx]] = value
                if mapped:
                    done.update(mapped)
                    last_error = None
                    break
                last_error = RuntimeError("模型没有返回任何译文")
            except Exception as error:  # noqa: BLE001
                last_error = error
                time.sleep(2.0 * (attempt + 1))
        if last_error is not None:
            raise RuntimeError(f"第 {batch_index + 1} 批翻译失败（已翻 {len(done)} 条）：{last_error}")

        if sleep_seconds:
            time.sleep(sleep_seconds)
    return done


# ------------------------------------------------------------------ 包构建 --

def build_pack(name: str, version: str, entries: list[dict], resources: list[dict],
               attributes: list[dict], existing: dict | None) -> tuple[dict, list[str]]:
    old_entries = {e.get("Original", ""): e for e in (existing or {}).get("entries", [])}
    old_resources = {
        r.get("Container", "") + "\x01" + r.get("Key", ""): r
        for r in (existing or {}).get("resources", [])
    }
    old_attributes = {a.get("Original", ""): a for a in (existing or {}).get("attributes", [])}

    pack = {
        "_meta": {
            "format": 2,
            "updatedAt": time.strftime("%Y-%m-%d"),
            "pluginVersion": version,
            "source": "library",
        },
        "entries": [],
        "resources": [],
        "attributes": [],
    }
    pending: dict[str, None] = {}

    seen: set[str] = set()
    for entry in entries:
        if entry.get("Role") != "UI":
            continue
        original = entry.get("Original") or ""
        if not original or original in seen:
            continue
        seen.add(original)
        old = old_entries.get(original) or {}
        translated = (old.get("Translated") or "").strip()
        item = {
            "Original": original,
            "Translated": translated,
            "Context": entry.get("Context") or "",
            "PreserveID": bool(entry.get("PreserveID")),
        }
        if translated:
            # 保留既有来源：投稿进来的人工译文（user）不能被下次重建洗成 machine 译
            item["Source"] = old.get("Source") or "library"
        pack["entries"].append(item)
        if not translated:
            pending[original] = None

    seen_res: set[tuple[str, str]] = set()
    for entry in resources:
        container = entry.get("Container") or ""
        key = entry.get("Key") or ""
        value = entry.get("Value") or ""
        if not container or not key or (container, key) in seen_res:
            continue
        seen_res.add((container, key))
        old = old_resources.get(container + "\x01" + key) or {}
        translated = (old.get("Translated") or "").strip()
        item = {
            "Container": container,
            "Key": key,
            "Original": value,
            "Translated": translated,
        }
        if translated:
            item["Source"] = old.get("Source") or "library"
            if (old.get("Original") or "") != value:
                item["Review"] = "原文改过了，译文待复核"
        pack["resources"].append(item)
        if not translated:
            pending[value] = None

    seen_attr: set[str] = set()
    for entry in attributes:
        value = entry.get("Value") or ""
        if not value or value in seen_attr:
            continue
        seen_attr.add(value)
        old = old_attributes.get(value) or {}
        translated = (old.get("Translated") or "").strip()
        item = {
            "Original": value,
            "Translated": translated,
            "Context": f"[属性] {entry.get('Attribute') or ''} {entry.get('Owner') or ''}".strip(),
        }
        if translated:
            item["Source"] = old.get("Source") or "library"
        pack["attributes"].append(item)
        if not translated:
            pending[value] = None

    return pack, list(pending.keys())


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="生成公共译文包（uit-packs/）")
    parser.add_argument("--targets", nargs="*", default=None, help="内部名列表（默认读 scripts/uit_targets.txt）")
    parser.add_argument("--targets-file", default=DEFAULT_TARGETS)
    parser.add_argument("--probe", default=os.path.join(REPO_ROOT, "tools/UITextProbe/bin/Release/net10.0/UITextProbe.dll"))
    parser.add_argument("--out", default=os.path.join(REPO_ROOT, "uit-packs"))
    parser.add_argument("--cache", default=os.path.join(tempfile.gettempdir(), "firegaze-uit-cache"))
    parser.add_argument("--glossary", default=DEFAULT_GLOSSARY)
    parser.add_argument("--batch", type=int, default=24)
    parser.add_argument("--sleep", type=float, default=0.3)
    parser.add_argument("--max-new", type=int, default=0, help="每个插件本次最多翻多少条（0 = 不限）")
    parser.add_argument("--refresh", action="store_true", help="忽略缓存，重拉 Aetherfeed")
    parser.add_argument("--dry-run", action="store_true", help="只报告，不调模型、不写盘")
    parser.add_argument("--only", nargs="*", default=None, help="只处理这些内部名")
    args = parser.parse_args(argv)

    targets = args.targets if args.targets else load_targets(args.targets_file)
    if args.only:
        targets = [t for t in targets if t in set(args.only)]
    targets = [t for t in targets if t.lower() not in DO_NOT_LOCALIZE]
    if not targets:
        print("没有目标插件（--targets 或 scripts/uit_targets.txt）")
        return 1

    api_key = os.environ.get("DEEPSEEK_API_KEY", "").strip()
    glossary = build_glossary_index(load_glossary(args.glossary)) if not args.dry_run else {}
    os.makedirs(args.cache, exist_ok=True)
    if not args.dry_run:
        os.makedirs(args.out, exist_ok=True)

    print(f"目标 {len(targets)} 个插件；模式：{'dry-run' if args.dry_run else ('翻译' if api_key else '无 key（只盘点）')}")
    links = resolve_downloads(targets, args.cache, args.refresh)
    companions_map = load_rules()

    total_new = 0
    processed = 0
    for name in targets:
        if name not in links:
            continue
        url, version = links[name]
        try:
            files = ensure_dlls(name, url, version, args.cache, companions_map)
            probe_entries: list[list[dict]] = []
            probe_resources: list[list[dict]] = []
            probe_attributes: list[list[dict]] = []
            file_names = [os.path.basename(f) for f in files]
            for path in files:
                file_entries, file_resources, file_attributes = run_probe(args.probe, path)
                probe_entries.append(file_entries)
                probe_resources.append(file_resources)
                probe_attributes.append(file_attributes)
            entries = merge_probe_entries(probe_entries, file_names)
            resources = merge_probe_resources(probe_resources)
            attributes = merge_probe_attributes(probe_attributes, file_names)
        except Exception as error:  # noqa: BLE001
            print(f"  [{name}] 失败：{error}")
            continue

        existing_path = os.path.join(args.out, name + ".json")
        existing = None
        if os.path.exists(existing_path):
            try:
                existing = json.loads(io.open(existing_path, encoding="utf-8").read())
            except Exception:  # noqa: BLE001
                existing = None

        pack, pending = build_pack(name, version, entries, resources, attributes, existing)
        ui_count = len(pack["entries"])
        res_count = len(pack["resources"])
        attr_count = len(pack["attributes"])
        if ui_count + res_count + attr_count == 0:
            print(f"  [{name}] v{version}：没有可翻的界面文本（界面已经是中文 / 没有字面量），跳过")
            continue
        print(f"  [{name}] v{version}：字面量 {ui_count} · 资源 {res_count} · 属性 {attr_count} · 待翻 {len(pending)}")

        if args.dry_run:
            total_new += len(pending)
            processed += 1
            continue

        if pending:
            if not api_key:
                print(f"    没有 DEEPSEEK_API_KEY，跳过这个插件（保留旧包）")
                continue
            try:
                translated = translate_texts(api_key, pending, glossary, args.batch, args.sleep, args.max_new)
            except Exception as error:  # noqa: BLE001
                print(f"    翻译失败：{error}")
                continue

            for item in pack["entries"]:
                if not item["Translated"] and item["Original"] in translated:
                    item["Translated"] = translated[item["Original"]]
                    item["Source"] = "library"
            for item in pack["resources"]:
                if not item["Translated"] and item["Original"] in translated:
                    item["Translated"] = translated[item["Original"]]
                    item["Source"] = "library"
            for item in pack["attributes"]:
                if not item["Translated"] and item["Original"] in translated:
                    item["Translated"] = translated[item["Original"]]
                    item["Source"] = "library"
            total_new += len(translated)
        else:
            # 即使没有要翻的，也重写一次：插件版本/上下文变了要跟上
            pass

        io.open(os.path.join(args.out, name + ".json"), "w", encoding="utf-8", newline="\n").write(
            json.dumps(pack, ensure_ascii=False, indent=2) + "\n"
        )
        processed += 1

    if not args.dry_run:
        write_index(args.out)
    print(f"完成：处理 {processed} 个插件，本次新翻 {total_new} 条；输出目录 {args.out}")
    return 0


def write_index(out_dir: str) -> None:
    """把 out/ 下所有包汇总成索引（含以前构建过、这次没处理的）。"""
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
    try:
        sys.stdout.reconfigure(errors="replace")
    except Exception:  # noqa: BLE001
        pass
    sys.exit(main())
