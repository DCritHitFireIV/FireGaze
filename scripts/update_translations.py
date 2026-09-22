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

# 仓库文件的容错解析：卫月（Json.NET）允许注释与尾随逗号，我们也得允许，
# 否则整座仓库的插件（含 Punchline）会静默漏掉（2026-09-22 实例）。
_TRAILING_COMMA = re.compile(r",(\s*[\]}]+)")
_LINE_COMMENT = re.compile(r"^\s*//[^\n]*$", re.M)
_BLOCK_COMMENT = re.compile(r"/\*.*?\*/", re.S)

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


def http_get_mirrors(url: str, timeout: int = 60) -> str:
    """直连失败就依次走国内镜像（raw.githubusercontent 直连经常不通/超时）。"""
    last: Exception | None = None
    for candidate in mirror_urls(url):
        try:
            return http_get(candidate, timeout=timeout)
        except Exception as error:  # noqa: BLE001
            last = error
    raise last if last else RuntimeError(f"取不到：{url}")


def mirror_urls(url: str) -> list[str]:
    urls = [url]
    if url.startswith("https://raw.githubusercontent.com/"):
        urls.append("https://gh.atmoomen.top/" + url[len("https://"):])
        urls.append("https://gh-proxy.org/" + url)
    elif url.startswith("https://github.com/"):
        urls.append("https://gh-proxy.org/" + url)
    return urls


def parse_repo_json(text: str):
    """按卫月（Json.NET）的口径解析仓库文件：**允许注释与尾随逗号**。

    真实案例（2026-09-22）：Ashylila/AshPluggyRepo 的 repo.json 在数组末尾多一个逗号，
    严格 json 直接抛异常 → 整座仓库的插件（含 Punchline）进不了语料 → 工作流永远看不到
    这些一行简介，词表里就一直是「没有这一字段」，游戏里看着就是永远缺译。
    卫月自己能读，所以游戏里有原文 —— 我们也得能读。
    """
    cleaned = text.lstrip("\ufeff")
    try:
        return json.loads(cleaned)
    except Exception:  # noqa: BLE001
        pass

    relaxed = _TRAILING_COMMA.sub(r"\1", _BLOCK_COMMENT.sub("", _LINE_COMMENT.sub("", cleaned)))
    try:
        return json.loads(relaxed)
    except Exception:  # noqa: BLE001
        return None


def fetch_repo_plugins(repo_url: str) -> list[dict]:
    for candidate in mirror_urls(repo_url):
        try:
            doc = parse_repo_json(http_get(candidate))
            if isinstance(doc, list):
                return [x for x in doc if isinstance(x, dict)]
        except Exception:  # noqa: BLE001
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

CORPUS_FIELDS = ("name", "punchline", "description")


def _text_of(item: dict) -> str:
    return " ".join((item.get(k) or "") for k in CORPUS_FIELDS)


def _score(item: dict) -> tuple[int, int]:
    """越靠后越优先：先把「含中文的」排后面，再比文本长度。"""
    text = _text_of(item)
    return (0 if CJK.search(text) else 1, len(text))


def merge_plugin(corpus: dict[str, dict], plugin: dict, repo_url: str) -> bool:
    """把一条插件并进语料；被采纳返回 True。

    同一个 InternalName 可能在多个仓库里出现（国际原版 + 国服汉化分支），
    优先保留「原文不是中文」的那一条：国服分支本身就是中文、不需要我们替换，
    而国际版的英文原文才能在游戏里匹配上。
    """
    key = (plugin.get("InternalName") or "").strip()
    if not key:
        return False

    candidate = {
        "name": (plugin.get("Name") or "").strip(),
        "punchline": (plugin.get("Punchline") or "").strip(),
        "description": (plugin.get("Description") or "").strip(),
        "repo_url": repo_url,
    }

    previous = corpus.get(key)
    if previous is not None and _score(candidate) <= _score(previous):
        return False

    corpus[key] = candidate
    return True


def fetch_repo_plugins_safe(url: str) -> tuple[str, list[dict]]:
    try:
        return url, fetch_repo_plugins(url)
    except Exception:  # noqa: BLE001
        return url, []


def add_repo_corpus(corpus: dict[str, dict], urls: list[str]) -> int:
    """用一整套仓库文件补语料（带 Punchline）。

    Aetherfeed 只给 Name/Description，**完全没有 Punchline**；
    而玩家手里那 1000+ 个仓库文件才是「一行简介」的真正来源，
    同时也覆盖 Aetherfeed 没收的仓库（国服/小众库）。
    """
    print(f"从 {len(urls)} 个仓库文件补语料（含一行简介）…")
    taken = 0
    done = 0

    with cf.ThreadPoolExecutor(max_workers=16) as executor:
        for url, plugins in executor.map(fetch_repo_plugins_safe, urls):
            done += 1
            for plugin in plugins:
                if merge_plugin(corpus, plugin, url):
                    taken += 1
            if done % 200 == 0:
                print(f"  已抓 {done}/{len(urls)} 个仓库…")

    print(f"仓库文件贡献/更新了 {taken} 条插件")
    return taken


def load_repo_urls(path: str) -> list[str]:
    """从 Dalamud 配置（dalamudConfig.json）或纯文本清单里读出第三方仓库地址。"""
    with open(path, encoding="utf-8-sig") as handle:
        raw = handle.read()

    if path.lower().endswith(".json"):
        doc = json.loads(raw)
        node = doc.get("ThirdRepoList") if isinstance(doc, dict) else None
        if isinstance(node, dict):
            node = node.get("$values")
        urls: list[str] = []
        for item in node or []:
            if isinstance(item, dict) and item.get("Url"):
                urls.append(item["Url"])
            elif isinstance(item, str):
                urls.append(item)
        return urls

    return [line.strip() for line in raw.splitlines()
            if line.strip() and not line.strip().startswith("#")]


def build_corpus(api_key: str | None, limit: int) -> dict[str, dict]:
    """InternalName -> {"name":..., "punchline":..., "description":..., "repo_url":...}"""
    doc = json.loads(http_get_mirrors(AETHERFEED))
    if not isinstance(doc, list):
        raise RuntimeError("Aetherfeed plugins.json 格式异常")

    corpus: dict[str, dict] = {}
    for repo in doc:
        if not isinstance(repo, dict):
            continue
        repo_url = repo.get("repo_url") or ""
        for plugin in repo.get("plugins") or []:
            merge_plugin(corpus, plugin, repo_url)

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
    parser.add_argument("--repos", default="",
                        help="额外语料：Dalamud 配置（dalamudConfig.json）或一行一个仓库地址的文本文件；"
                             "能补全 Aetherfeed 没有的仓库，并把「一行简介」一次抓齐")
    parser.add_argument("--limit", type=int, default=0, help="最多翻译多少条（调试）")
    args = parser.parse_args(argv)

    api_key = os.environ.get("DEEPSEEK_API_KEY", "").strip() or None

    corpus = build_corpus(api_key, args.limit)

    if args.repos:
        try:
            repo_urls = load_repo_urls(args.repos)
            print(f"{args.repos}：{len(repo_urls)} 个仓库地址")
            add_repo_corpus(corpus, repo_urls)
            print(f"合并后语料：{len(corpus)} 个插件")
        except Exception as error:  # noqa: BLE001
            print(f"补充语料失败（继续用 Aetherfeed）：{error}")

    table: dict[str, dict] = {}
    if os.path.exists(args.table):
        table = json.load(open(args.table, encoding="utf-8"))
        # 以下划线开口的键是元数据（_meta.updatedAt = 词表维护日期），不当插件条目
        table = {key: value for key, value in table.items() if not key.startswith("_")}
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

    def upstream_is_localized(text: str) -> bool:
        """上游给的原文已经是中文（国服/汉化分支的简介）：直接当译文用，不再送机器翻译。

        原因：机器翻译会把它当外文重译一遍，既浪费又可能改得很奇怪；
        而对这类条目，非中文玩家看到的原文本来就已经是中文。
        """
        return bool(text) and bool(CJK.search(text))

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
        saved_name_t = (saved.get("Name") or {}).get("Translated") or ""
        saved_desc_t = (saved.get("Description") or {}).get("Translated") or ""
        saved_punch_t = (saved.get("Punchline") or {}).get("Translated") or ""

        if name and (is_new or not saved_name_t or prefer(saved_name, name)):
            name_todo.append((key, name))

        # 「原文在、但表里没译文 / 连这个字段都还没收录」也要排进待翻译：
        # 旧逻辑只看「新增或原文变了」，导致 Aetherfeed 从来不给 Punchline 的那批条目
        # 永远停在「没翻译」。（一行简介的原文要等下面的 enrich 去源仓库回抓）
        need_desc = bool(description) and (is_new or not saved_desc_t or prefer(saved_desc, description))
        need_punch = bool(punchline) and (is_new or not saved_punch_t or prefer(saved_punch, punchline))
        # 表里连一行简介都没有、但 Aetherfeed 不含 Punchline → 排进来，稍后回抓源仓库补。
        # 用了 --repos 时语料已经把各仓库的 Punchline 抓全了，不必再回抓（否则会反复重试）。
        want_punch_from_repo = (not saved_punch_t) and bool(description) and not args.repos
        if need_desc or need_punch or want_punch_from_repo:
            desc_todo.append((key, punchline, description))

    # 新条目的仓库可能还没取到 Punchline；Aetherfeed 永远不给 Punchline，
    # 所以「表里没有一行简介」的条目也要回抓源仓库。
    if not args.stats_only:
        missing_punch = {k for k, p, d in desc_todo if not p and d}
        if missing_punch:
            enrich_punchlines(corpus, missing_punch)
            desc_todo = [(k, corpus[k]["punchline"], corpus[k]["description"]) for k, _, _ in desc_todo]

    stats["name"] = len(name_todo)
    stats["desc"] = len(desc_todo)
    print(f"待翻译：插件名 {len(name_todo)} 条 / 简介+详情 {len(desc_todo)} 条（新增 {stats['new']} 条）")

    def write_table(path: str) -> None:
        """写回词表：头部记上维护日期（插件界面显示「词表更新：YYYY-MM-DD（周X）」，离线可读）。"""
        ordered: dict = {"_meta": {"updatedAt": time.strftime("%Y-%m-%d")}}
        for key, value in table.items():
            if key.startswith("_"):
                continue
            ordered[key] = value

        with open(path, "w", encoding="utf-8") as handle:
            json.dump(ordered, handle, ensure_ascii=False, indent=1)

    if args.stats_only:
        print("无需翻译（--stats-only 不写文件）。")
        return 0

    if not name_todo and not desc_todo:
        # 没有要翻的，也要把维护日期带上（当天跑过一次就是一次维护）
        write_table(args.table)
        print(f"无需翻译：只更新词表维护日期 -> {args.table}")
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

    # 需要复核 / 需要新机器译的条目（收尾时写报告）
    needs_review: list[tuple[str, str, str, str]] = []   # key, field, 旧原文, 旧译文
    failed: list[tuple[str, str, str]] = []              # key, field, 原因

    def set_name(key: str, original: str, translated: str | None) -> None:
        """写插件名。translated=None = 这次没拿到译文（只对齐原文，绝不覆盖已有的）。"""
        entry = table.setdefault(key, {})
        saved = entry.get("Name") or {}
        old_original = saved.get("Original") or ""
        old_translated = saved.get("Translated") or ""
        is_user = str(saved.get("Source") or "").lower() == "user"
        source_changed = bool(old_original) and old_original != original

        if old_translated and not source_changed:
            # 上游原文没变 → 现有译文永远优先（用户译或机器译都是）
            if old_original != original:
                saved["Original"] = original          # 首次记录原文
            saved.setdefault("Translated", old_translated)
            if not saved.get("Source"):
                saved["Source"] = "user" if is_user else "ai"
            entry["Name"] = saved
            return

        if not translated:
            # 拿不到新译文：保留现有译文，只对齐原文；如果原文确实变了，记一笔待复核
            if source_changed:
                needs_review.append((key, "Name", old_original, old_translated))
            if old_original != original or not old_translated:
                saved["Original"] = original
            if old_translated and not saved.get("Translated"):
                saved["Translated"] = old_translated
            saved.setdefault("Source", "user" if is_user else "ai")
            entry["Name"] = saved
            failed.append((key, "Name", "这次没拿到译文"))
            return

        # 上游原文本身就是中文：直接当译文用（这类条目不送机器翻译）
        if upstream_is_localized(original):
            translated = original

        # 有译文：只有「原来没译文」或「原文真的变了」才写
        # 用户译在原文变了时会被新机器译覆盖 —— 按用户要求先把旧译文记进待复核
        if is_user and old_translated:
            needs_review.append((key, "Name", old_original, old_translated))

        new_pair = {"Original": original, "Translated": translated, "Source": "ai"}
        if is_user and old_translated and old_translated == translated:
            new_pair["Source"] = "user"      # 与新译文完全重合：保留用户译
            new_pair.pop("Review", None)
        entry["Name"] = new_pair

    def set_desc_pair(key: str, field: str, original: str, translated: str | None) -> None:
        """写 Punchline / Description，规则与 set_name 一致。"""
        entry = table.setdefault(key, {})
        saved = entry.get(field) or {}
        old_original = saved.get("Original") or ""
        old_translated = saved.get("Translated") or ""
        is_user = str(saved.get("Source") or "").lower() == "user"
        source_changed = bool(old_original) and old_original != original

        if old_translated and not source_changed:
            if old_original != original:
                saved["Original"] = original
            saved.setdefault("Translated", old_translated)
            if not saved.get("Source"):
                saved["Source"] = "user" if is_user else "ai"
            entry[field] = saved
            return

        if not translated:
            if source_changed:
                needs_review.append((key, field, old_original, old_translated))
            saved["Original"] = original
            if old_translated:
                saved["Translated"] = old_translated
                saved.setdefault("Source", "user" if is_user else "ai")
                saved["Review"] = "原文已更新，译文还停在旧版"
            entry[field] = saved
            failed.append((key, field, "这次没拿到译文"))
            return

        # 上游原文本身就是中文：直接当译文用（这类条目不送机器翻译）
        if upstream_is_localized(original):
            translated = original

        if is_user and old_translated:
            needs_review.append((key, field, old_original, old_translated))

        new_pair = {"Original": original, "Translated": translated, "Source": "ai"}
        if is_user and old_translated and old_translated == translated:
            new_pair["Source"] = "user"
        entry[field] = new_pair

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
                set_name(key, name, translated)
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
                if punchline:
                    set_desc_pair(key, "Punchline", punchline, translated_p or None)
                if description:
                    set_desc_pair(key, "Description", description, translated_d or None)
                usage["ok"] += 1

    name_batches = [name_todo[i:i + 25] for i in range(0, len(name_todo), 25)]
    desc_batches = [desc_todo[i:i + 8] for i in range(0, len(desc_todo), 8)]

    with cf.ThreadPoolExecutor(max_workers=4) as executor:
        list(executor.map(translate_names, name_batches))
        list(executor.map(translate_descs, desc_batches))

    # 清掉语料里已经消失的条目？不删：保持历史译文，避免上游临时抽风导致词表缩水。

    # 上游原文改过、旧译文可能对不上的条目：写一份报告，供人工/插件侧（参与翻译）复核
    if needs_review:
        review_path = os.path.join(os.path.dirname(os.path.abspath(args.table)), "scripts", "translation-review.md")
        try:
            with open(review_path, "w", encoding="utf-8") as handle:
                handle.write("# 需要复核的译文\n\n")
                handle.write(f"生成时间：{time.strftime('%Y-%m-%d %H:%M')}\n\n")
                handle.write("上游原文改过了，以下条目的译文停在旧版；玩家提交过的译文会优先保留。\n\n")
                handle.write("| 插件 | 字段 | 旧原文 | 旧译文 |\n|---|---|---|---|\n")
                for key, field, old_original, old_translated in needs_review:
                    def cell(text: str) -> str:
                        return (text or "").replace("|", "\\|").replace("\n", " ")[:120]
                    handle.write(f"| {cell(key)} | {field} | {cell(old_original)} | {cell(old_translated)} |\n")
            print(f"需要复核：{len(needs_review)} 处，报告写到 {review_path}")
        except Exception as error:  # noqa: BLE001
            print(f"写复核报告失败：{error}")

    if failed:
        print(f"【需要注意】{len(failed)} 个字段这次没拿到译文，已保留原译文：")
        for key, field, reason in failed[:10]:
            print(f"  {key} / {field}：{reason}")

    # 词表头部记上维护日期：插件界面显示「词表更新：YYYY-MM-DD（周X）」，离线可读
    write_table(args.table)

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
