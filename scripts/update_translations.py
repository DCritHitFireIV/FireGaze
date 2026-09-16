#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""update_translations.py — CI 用：从 DalamudRepoBrowser 的数据源（Aetherfeed）拉全量插件，
只翻译「新增 / 原文有变化」的条目，合并回 translations.json。

数据源：
    https://raw.githubusercontent.com/Aetherfeed/aetherfeed.github.io/refs/heads/main/public/data/plugins.json
    （1283 个仓库 / 2000+ 插件，含 Name 与 Description；缺 Punchline）

对新增条目会再抓一次它所在的仓库文件补 Punchline（带国内镜像兜底）。

翻译后端：DeepSeek（环境变量 DEEPSEEK_API_KEY），未配置时只做「语料比对」不翻新条目。

用法：
    python scripts/update_translations.py                 # 正常增量更新
    python scripts/update_translations.py --stats-only    # 只报告差异，不翻译
    python scripts/update_translations.py --limit 20      # 只翻前 20 条（调试）
"""

from __future__ import annotations

import argparse
import concurrent.futures as cf
import json
import os
import re
import sys
import threading
import time
import urllib.request

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_TABLE = os.path.join(REPO_ROOT, "translations.json")
AETHERFEED = "https://raw.githubusercontent.com/Aetherfeed/aetherfeed.github.io/refs/heads/main/public/data/plugins.json"
CJK = re.compile(r"[\u4e00-\u9fff]")

NAME_PROMPT = (
    "你是 FFXIV 插件生态的译者。输入一组卫月插件的英文名，请判断是否需要译成简体中文。"
    "规则："
    "① 专有名词、品牌名、代号、缩写、自造词（如 Penumbra、Glamourer、PalacePal、BOCCHI、Aetherphone、WrathCombo）保持原样输出；"
    "② 由多个普通英文单词组成、含义明确的描述性短语译成简短中文（不超过 12 字），"
    "如 Hunt Train Assistant→猎车助手、Skip Cutscene→跳过过场动画、Auto Party Finder→自动招募、Chest Helper→宝箱助手；"
    "单个普通词若明显是功能词（Inviter、Prioritizer）也可以意译；"
    "③ 名字里的数字、括号版本号（API12/API13）、符号、大小写风格保留；"
    "④ 只输出 JSON，不要代码块。"
    '输出格式：{"items":[{"id":<原 id>,"n":"<结果>"}]}'
)

DESC_PROMPT = (
    "你是专业的 FFXIV 插件文档译者。请把用户给出的 JSON 中每个插件的简介翻译成简体中文。"
    "规则：保持专有名词/插件名/命令（如 Penumbra、Glamourer、/pdr）的英文原样；"
    "如果输入里带 glossary（英文→国服官方中文的术语表），**必须使用其中的译名**；"
    "不要添加解释、注释或礼貌用语；保留原有换行风格；只输出 JSON，不要代码块。"
    '输出格式：{"items":[{"id":<原 id>,"p":"<Punchline 译文>","d":"<Description 译文>"}]}'
)

_lock = threading.Lock()


# ------------------------------------------------------------------ 网络 --

def http_get(url: str, timeout: int = 30) -> str:
    request = urllib.request.Request(
        url,
        headers={
            "User-Agent": "FireGaze-Translator/1.0 (+https://github.com/DCritHitFireIV/FireGaze)",
            "Accept": "application/json",
        },
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return response.read().decode("utf-8", errors="replace")


def mirror_urls(url: str) -> list[str]:
    urls = [url]
    if url.startswith("https://raw.githubusercontent.com/"):
        urls.append("https://gh.atmoomen.top/" + url[len("https://"):])
        urls.append("https://gh-proxy.org/" + url)
    elif url.startswith("https://github.com/"):
        urls.append("https://gh-proxy.org/" + url)
    return urls


def fetch_repo_plugins(repo_url: str) -> list[dict]:
    for candidate in mirror_urls(repo_url):
        try:
            doc = json.loads(http_get(candidate).lstrip("\ufeff"))
            if isinstance(doc, list):
                return [x for x in doc if isinstance(x, dict)]
        except Exception:
            continue
    return []


def deepseek(api_key: str, system: str, payload: dict, max_tokens: int) -> dict:
    request = urllib.request.Request(
        "https://api.deepseek.com/chat/completions",
        data=json.dumps(
            {
                "model": "deepseek-flash",
                "messages": [
                    {"role": "system", "content": system},
                    {"role": "user", "content": json.dumps(payload, ensure_ascii=False)},
                ],
                "temperature": 0.2,
                "max_tokens": max_tokens,
                "thinking": {"type": "disabled"},
            },
            ensure_ascii=False,
        ).encode("utf-8"),
        headers={
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json",
        },
    )
    with urllib.request.urlopen(request, timeout=180) as response:
        data = json.loads(response.read().decode("utf-8"))
    return data["choices"][0]["message"]["content"]


def parse_json_block(content: str) -> dict:
    text = content.strip()
    if text.startswith("```"):
        text = re.sub(r"^```[a-zA-Z]*\n?", "", text)
        text = re.sub(r"\n?```$", "", text)
    return json.loads(text)


# ------------------------------------------------------------------ 语料 --

def build_corpus(api_key: str | None, limit: int) -> dict[str, dict]:
    """InternalName -> {"name":..., "punchline":..., "description":..., "repo_url":...}

    同一个 InternalName 可能在多个仓库里出现（国际原版 + 国服汉化分支）。
    优先保留「原文不是中文」的那一条：国服分支本身就是中文，不需要我们替换；
    而国际版的英文原文才能在游戏里匹配上。
    """
    doc = json.loads(http_get(AETHERFEED))
    if not isinstance(doc, list):
        raise RuntimeError("Aetherfeed plugins.json 格式异常")

    def text_of(item: dict) -> str:
        return " ".join((item.get(k) or "") for k in ("Name", "Punchline", "Description"))

    def score(item: dict) -> tuple[int, int]:
        text = text_of(item)
        return (0 if CJK.search(text) else 1, len(text))

    corpus: dict[str, dict] = {}
    for repo in doc:
        if not isinstance(repo, dict):
            continue
        repo_url = repo.get("repo_url") or ""
        for plugin in repo.get("plugins") or []:
            key = (plugin.get("InternalName") or "").strip()
            if not key:
                continue

            previous = corpus.get(key)
            if previous is not None:
                # 旧条目的得分（用储存的文本重算）
                prev_text = " ".join((previous.get(k) or "") for k in ("name", "punchline", "description"))
                prev_score = (0 if CJK.search(prev_text) else 1, len(prev_text))
                if score(plugin) <= prev_score:
                    continue

            corpus[key] = {
                "name": (plugin.get("Name") or "").strip(),
                "punchline": "",
                "description": (plugin.get("Description") or "").strip(),
                "repo_url": repo_url,
            }

    print(f"Aetherfeed：{len(doc)} 个仓库 / {len(corpus)} 个插件")
    return corpus


def enrich_punchlines(corpus: dict[str, dict], needed: set[str]) -> int:
    """给需要的条目抓源仓库补 Punchline。"""
    repos: dict[str, list[str]] = {}
    for key in needed:
        entry = corpus.get(key)
        if entry and entry["repo_url"]:
            repos.setdefault(entry["repo_url"], []).append(key)

    print(f"需要补 Punchline：{len(needed)} 条，分布在 {len(repos)} 个仓库")
    filled = 0

    def work(item):
        repo_url, keys = item
        plugins = fetch_repo_plugins(repo_url)
        by_key = {p.get("InternalName"): p for p in plugins}
        hit = 0
        for key in keys:
            plugin = by_key.get(key)
            if plugin:
                entry = corpus[key]
                punchline = (plugin.get("Punchline") or "").strip()
                # 源仓库里的简介也可能是中文（国服汉化版）：不拿它覆盖英文原版
                if punchline and not (CJK.search(punchline) and not CJK.search(entry["punchline"])):
                    entry["punchline"] = punchline
                if not entry["name"]:
                    entry["name"] = (plugin.get("Name") or "").strip()
                if not entry["description"]:
                    entry["description"] = (plugin.get("Description") or "").strip()
                hit += 1
        return hit

    with cf.ThreadPoolExecutor(max_workers=16) as executor:
        for hit in executor.map(work, repos.items()):
            filled += hit

    print(f"补到 Punchline：{filled} 条")
    return filled


# ------------------------------------------------------------------ 主流程 --

def main(argv=None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--table", default=DEFAULT_TABLE)
    parser.add_argument("--stats-only", action="store_true", help="只报告差异，不翻译")
    parser.add_argument("--limit", type=int, default=0, help="最多翻译多少条（调试）")
    args = parser.parse_args(argv)

    api_key = os.environ.get("DEEPSEEK_API_KEY", "").strip() or None

    corpus = build_corpus(api_key, args.limit)

    table: dict[str, dict] = {}
    if os.path.exists(args.table):
        table = json.load(open(args.table, encoding="utf-8"))
    print(f"现有词表：{len(table)} 条")

    # 找出需要处理的部分：缺条目，或三个字段里任意一个的原文变了 / 还没有译文
    name_todo: list[tuple[str, str]] = []
    desc_todo: list[tuple[str, str, str]] = []
    stats = {"new": 0, "name": 0, "desc": 0}

    def prefer(existing: str, new_text: str) -> bool:
        """新文本是否值得替换旧文本：内容真的变了，且不用「国服分支的中文原文」覆盖英文原文。"""
        if not new_text or existing == new_text:
            return False
        if CJK.search(new_text) and not CJK.search(existing):
            return False
        return True

    for key, entry in corpus.items():
        saved = table.get(key) or {}
        is_new = key not in table
        if is_new:
            stats["new"] += 1

        name = entry["name"]
        punchline = entry["punchline"]
        description = entry["description"]

        saved_name = (saved.get("Name") or {}).get("Original") or ""
        saved_desc = (saved.get("Description") or {}).get("Original") or ""
        saved_punch = (saved.get("Punchline") or {}).get("Original") or ""

        if name and (is_new or prefer(saved_name, name)):
            name_todo.append((key, name))

        need_desc = bool(description) and (is_new or prefer(saved_desc, description))
        need_punch = bool(punchline) and (is_new or prefer(saved_punch, punchline))
        if need_desc or need_punch:
            desc_todo.append((key, punchline, description))

    # 新条目的仓库可能还没取到 Punchline
    if not args.stats_only:
        missing_punch = {k for k, p, d in desc_todo if not p and d}
        if missing_punch:
            enrich_punchlines(corpus, missing_punch)
            desc_todo = [(k, corpus[k]["punchline"], corpus[k]["description"]) for k, _, _ in desc_todo]

    stats["name"] = len(name_todo)
    stats["desc"] = len(desc_todo)
    print(f"待翻译：插件名 {len(name_todo)} 条 / 简介+详情 {len(desc_todo)} 条（新增 {stats['new']} 条）")

    if args.stats_only or (not name_todo and not desc_todo):
        print("无需翻译（或 --stats-only）。")
        return 0

    if not api_key:
        print("未设置 DEEPSEEK_API_KEY，跳过翻译。")
        return 0

    if args.limit:
        name_todo = name_todo[: args.limit]
        desc_todo = desc_todo[: args.limit]

    usage = {"in": 0, "out": 0, "ok": 0, "fail": 0}
    started = time.time()

    # 术语表（英文→国服官方译名）：只在真要翻译时才拉，失败不影响主流程
    glossary: dict[str, str] = {}
    try:
        import ffxiv_glossary
        print("正在构建 FFXIV 官方术语表…")
        glossary = ffxiv_glossary.build_glossary(refresh=False, verbose=False)
        print(f"术语表就绪：{len(glossary)} 条")
    except Exception as error:  # noqa: BLE001
        print(f"术语表不可用（继续翻译）：{error}")

    def translate_names(batch: list[tuple[str, str]]):
        payload = {"items": [{"id": i, "name": name} for i, (_, name) in enumerate(batch)]}
        result: dict[int, str] = {}
        try:
            content = deepseek(api_key, NAME_PROMPT, payload, 4000)
            parsed = parse_json_block(content)
            for item in parsed.get("items", []):
                result[int(item["id"])] = (item.get("n") or "").strip()
        except Exception as error:
            if len(batch) > 1:
                half = len(batch) // 2
                translate_names(batch[:half])
                translate_names(batch[half:])
                return
            with _lock:
                usage["fail"] += 1
            print(f"  [名字失败] {batch[0][0]}: {error}")
            return

        for i, (key, name) in enumerate(batch):
            translated = result.get(i, "")
            if not translated or not CJK.search(translated):
                translated = name
            with _lock:
                entry = table.setdefault(key, {})
                entry["Name"] = {"Original": name, "Translated": translated}
                usage["ok"] += 1

    def translate_descs(batch: list[tuple[str, str, str]]):
        payload: dict = {
            "items": [{"id": i, "p": punchline, "d": description}
                      for i, (_, punchline, description) in enumerate(batch)]
        }

        if glossary:
            try:
                import ffxiv_glossary
                terms = ffxiv_glossary.find_terms(
                    [f"{p}\n{d}" for _, p, d in batch], glossary)
                if terms:
                    payload["glossary"] = [{"en": english, "zh": chinese} for english, chinese in terms]
            except Exception:  # noqa: BLE001
                pass

        result: dict[int, tuple[str, str]] = {}
        try:
            content = deepseek(api_key, DESC_PROMPT, payload, 8000)
            parsed = parse_json_block(content)
            for item in parsed.get("items", []):
                result[int(item["id"])] = ((item.get("p") or "").strip(), (item.get("d") or "").strip())
        except Exception as error:
            if len(batch) > 1:
                half = len(batch) // 2
                translate_descs(batch[:half])
                translate_descs(batch[half:])
                return
            with _lock:
                usage["fail"] += 1
            print(f"  [简介失败] {batch[0][0]}: {error}")
            return

        for i, (key, punchline, description) in enumerate(batch):
            translated_p, translated_d = result.get(i, ("", ""))
            with _lock:
                entry = table.setdefault(key, {})

                # 只在「原文变了」或「还没有译文」时写入：
                # 只改详情的条目不应该把一行简介也重翻一遍（否则会造成无意义的译文漂泊）
                saved_p = entry.get("Punchline") or {}
                if punchline and (saved_p.get("Original") != punchline or not saved_p.get("Translated")):
                    entry["Punchline"] = {
                        "Original": punchline,
                        "Translated": translated_p or punchline,
                    }

                saved_d = entry.get("Description") or {}
                if description and (saved_d.get("Original") != description or not saved_d.get("Translated")):
                    entry["Description"] = {
                        "Original": description,
                        "Translated": translated_d or description,
                    }

                usage["ok"] += 1

    name_batches = [name_todo[i:i + 25] for i in range(0, len(name_todo), 25)]
    desc_batches = [desc_todo[i:i + 8] for i in range(0, len(desc_todo), 8)]

    with cf.ThreadPoolExecutor(max_workers=4) as executor:
        list(executor.map(translate_names, name_batches))
        list(executor.map(translate_descs, desc_batches))

    # 清掉语料里已经消失的条目？不删：保持历史译文，避免上游临时抽风导致词表缩水。

    with open(args.table, "w", encoding="utf-8") as handle:
        json.dump(table, handle, ensure_ascii=False, indent=1)

    print(
        f"完成：成功 {usage['ok']} / 失败 {usage['fail']}，用时 {round(time.time() - started, 1)}s，"
        f"词表 {len(table)} 条 -> {args.table}"
    )
    return 0


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(errors="replace")
    except Exception:
        pass
    sys.exit(main())
