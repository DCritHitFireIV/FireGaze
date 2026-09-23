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

# 日语假名（含半角片假名与片假名扩展）：用来区分「中文原文」与「日文原文」。
# 日文夹着汉字，光看 CJK 会把它当成中文 —— 但玩家要的是中文译文（2026-09-22 用户定）。
KANA = re.compile(r"[\u3040-\u30ff\u31f0-\u31ff\uff66-\uff9d]")


def has_english_run(text: str, need: int = 3) -> bool:
    """文本里有没有「连续 need 个英文单词」的片段。

    用来把「中英混排」的原文抖出来（例如作者把中英两版并排写进简介：
    「将爆发药特效替换为 RGB 效果 / Replace tincture VFX with ...」）——
    这种不算「已经是中文」，要把里面的英文洗掉（实测 AoAoEnergy 就是这样）。
    品牌名单词不算（AE3、hackbox 这类不会命中 need≥3）。
    """
    run = 0
    for token in re.split(r"\s+", text or ""):
        if re.fullmatch(r"[A-Za-z][A-Za-z'\-]*", token) and len(token) >= 2:
            run += 1
            if run >= need:
                return True
        else:
            run = 0
    return False


def _echoed(source: str, output: str) -> bool:
    """模型把原文原样回显（没翻）。

    2026-09-23 实例：bozjalone 等 7 个日文条目「译文」= 日文原文，安装器里看着像没翻译。
    """
    src = " ".join((source or "").split())
    out = " ".join((output or "").split())
    return bool(src) and src == out


def upstream_is_localized(text: str) -> bool:
    """上游给的原文是不是**已经是中文、不需要再翻**。

    三个条件同时满足：有汉字、没有假名、也没有成串的英文。
    · 日语不算（它只是「不是英文」，仍要翻成中文，2026-09-22 用户定）；
    · 中英混排也不算（得把英文那半洗掉，2026-09-22 AoAoEnergy 实例）。
    """
    return (
        bool(text)
        and bool(CJK.search(text))
        and not KANA.search(text)
        and not has_english_run(text)
    )

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

def http_get(url: str, timeout: int = 30, attempts: int = 2) -> str:
    """抓一份文件。

    · **先把 URL 里的空格转成 %20**：仓库地址里带空格时（例如
      `.../master/Animation Wardrobe/pluginmaster.json`）urllib 会直接报
      InvalidURL "URL can't contain control characters" —— 整座仓库就此消失（2026-09-22 实例）。
    · 再**重试一次**：puni.sh 那批聚合端点在并发下经常超时，重试就能拉回来。
    """
    target = url.replace(" ", "%20")
    request = urllib.request.Request(
        target,
        headers={
            "User-Agent": "FireGaze-Translator/1.0 (+https://github.com/DCritHitFireIV/FireGaze)",
            "Accept": "application/json",
        },
    )

    last: Exception | None = None
    for attempt in range(max(1, attempts)):
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                return response.read().decode("utf-8", errors="replace")
        except Exception as error:  # noqa: BLE001
            last = error
            if attempt + 1 < max(1, attempts):
                time.sleep(1.5)

    raise last if last else RuntimeError(f"取不到：{url}")


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


def _lang_score(text: str) -> tuple[int, int]:
    """文本优先级：先要「不是中文原文」，再要更长。"""
    return (0 if CJK.search(text) else 1, len(text))


def _score(item: dict) -> tuple[int, int]:
    """整条插件的优先级（越靠后越优先）：先要「不是中文原文」，再比总文本长度。"""
    text = _text_of(item)
    return (0 if CJK.search(text) else 1, len(text))


def merge_plugin(corpus: dict[str, dict], plugin: dict, repo_url: str) -> bool:
    """把一条插件并进语料；被采纳返回 True。

    同一个 InternalName 可能在多个仓库里出现（国际原版 + 国服汉化分支 + 各种聚合库）：
    · 主版本还是「整条二选一」：先要原文不是中文，再要文本更长（原来是这个口径，别动，
      改成逐字段合并会把各种 CN 分支的名字/详情都卷进来，平得一堆「原文变了」重翻）；
    · **只补一处**：主版本没有一行简介、而另一份提供（且不是中文）时，把简介拿过来。
      不补就会像 LMeter / SmartStrafe / PyonCam / RacingwayRewrite 那样，
      游戏里永远缺那一条简介（2026-09-22 实测）。
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
    if previous is None:
        corpus[key] = candidate
        return bool(_text_of(candidate))

    if _score(candidate) > _score(previous):
        corpus[key] = candidate
        return True

    # 主版本不看：只把缺的一行简介补上
    if not previous["punchline"] and candidate["punchline"] and not upstream_is_localized(candidate["punchline"]):
        previous["punchline"] = candidate["punchline"]
        return True

    return False


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
                # 源仓库里的简介也可能是中文（国服汉化版）：不拿它覆盖英文原版；日语简介是真原文，可以用
                if punchline and not (upstream_is_localized(punchline) and not upstream_is_localized(entry["punchline"])):
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
    parser.add_argument(
        "--recheck",
        action="store_true",
        help="全量重做：把所有非玩家译的简介/详情再送一遍模型（默认只处理新增/缺译/译文=原文的）",
    )
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
    stats = {"new": 0, "name": 0, "desc": 0, "upstream_absent": 0}

    def prefer(existing: str, new_text: str) -> bool:
        """新文本是否值得替换旧文本：内容真的变了，且不用「国服分支的中文原文」覆盖英文原文。"""
        if not new_text or existing == new_text:
            return False
        if CJK.search(new_text) and not CJK.search(existing):
            return False
        return True

    def copy_of_source(saved_text: str, upstream_text: str) -> bool:
        """旧译文是不是「把外文原文一字不差当译文写进去了」。

        以前只把 CJK 当「已经本地化」，于是日文简介、中英混排的简介都被原样当译文存了下来；
        这类条目要重新送机器翻译（用户 2026-09-22：日语也要翻，中英混排要把英文洗掉）。
        插件名不走这里（品牌名本来就常常跟原文一样）。
        """
        text = (saved_text or "").strip()
        upstream = (upstream_text or "").strip()
        return bool(text) and text == upstream and not upstream_is_localized(upstream)

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

        # 上游压根没给这个字段：**不算缺译**，也不排进待翻译（用户 2026-09-22 定）。
        # 只统计一下，报告里好对账。
        for upstream_text in (name, punchline, description):
            if not upstream_text:
                stats["upstream_absent"] += 1

        if name and (is_new or not saved_name_t or prefer(saved_name, name)):
            name_todo.append((key, name))

        # 「原文在、但表里没译文 / 连这个字段都还没收录」也要排进待翻译：
        # 旧逻辑只看「新增或原文变了」，导致 Aetherfeed 从来不给 Punchline 的那批条目
        # 永远停在「没翻译」。（一行简介的原文要等下面的 enrich 去源仓库回抓）
        need_desc = bool(description) and (is_new or not saved_desc_t or copy_of_source(saved_desc_t, description) or prefer(saved_desc, description))
        need_punch = bool(punchline) and (is_new or not saved_punch_t or copy_of_source(saved_punch_t, punchline) or prefer(saved_punch, punchline))
        # 上游原文已经是中文（且没有成串英文、没假名）→ 不翻、不写译文，也不排进待翻译。
        # 以前会把原文原样写成译文，表里就多出一份「译文 = 原文」的副本（实测 320 处，用户要求去掉）。
        if upstream_is_localized(description):
            need_desc = False
        if upstream_is_localized(punchline):
            need_punch = False
        # 表里连一行简介都没有、但 Aetherfeed 不含 Punchline → 排进来，稍后回抓源仓库补。
        # 用了 --repos 时语料已经把各仓库的 Punchline 抓全了，不必再回抓（否则会反复重试）。
        want_punch_from_repo = (not saved_punch_t) and bool(description) and not args.repos
        if need_desc or need_punch or want_punch_from_repo:
            desc_todo.append((key, punchline, description))

    if args.recheck:
        # 全量重做（用户 2026-09-22 要求：规则改完后让大模型把这类全部再过一遍）：
        # 把所有「非玩家译、上游给了外文原文」的简介/详情重新翻一遍。
        queued = {k for k, _, _ in desc_todo}
        recheck_count = 0
        for key, entry in corpus.items():
            if key in queued:
                continue
            saved = table.get(key) or {}
            if not any(
                (entry.get(ck) or "").strip()
                and not upstream_is_localized(entry.get(ck) or "")
                and str(((saved.get(field) or {}).get("Source") or "")).lower() != "user"
                for field, ck in (("Punchline", "punchline"), ("Description", "description"))
            ):
                continue
            desc_todo.append((key, entry["punchline"], entry["description"]))
            recheck_count += 1

        if recheck_count:
            print(f"全量重做（--recheck）：{recheck_count} 个插件再送一遍模型")

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
    print(f"上游没给（不计入待翻译、也不算缺译）：{stats['upstream_absent']} 处字段")

    def write_table(path: str) -> None:
        """写回词表：头部记上维护日期（插件界面显示「词表更新：YYYY-MM-DD（周X）」，离线可读）。

        写之前做两道清理：
        · 丢掉「原文和译文都空」的字段（以前会留下 `"Punchline": {"Original": "", "Translated": ""}` 空壳）；
        · **上游原文本来就是中文的字段，不保留 Translated 副本** —— 以前会把中文原文原样写成译文，
          表里就多出一份「译文 = 原文」，看着像没翻译（实测 320 处；用户 2026-09-22 要求自动跳过）。
        """
        ordered: dict = {"_meta": {"updatedAt": time.strftime("%Y-%m-%d")}}
        for key, value in table.items():
            if key.startswith("_"):
                continue

            cleaned: dict = {}
            for field, pair in (value or {}).items():
                pair = pair or {}
                original = str(pair.get("Original") or "").strip()
                translated = str(pair.get("Translated") or "").strip()
                if not original and not translated:
                    continue

                is_user = str(pair.get("Source") or "").lower() == "user"
                if translated and is_user is False and upstream_is_localized(original):
                    keep = {k: v for k, v in pair.items() if k != "Translated"}
                    cleaned[field] = keep
                    continue

                # 译文与原文一字不差（模型回显的假译文，2026-09-23）→ 不留在表里当成已翻译；
                # 下次跑翻译时会因为「没有译文」重新排队。
                if (
                    field in ("Punchline", "Description")
                    and translated
                    and is_user is False
                    and not upstream_is_localized(original)
                    and _echoed(original, translated)
                ):
                    keep = {k: v for k, v in pair.items() if k != "Translated"}
                    cleaned[field] = keep
                    continue

                cleaned[field] = pair

            if cleaned:
                ordered[key] = cleaned

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

        # 玩家译是**唯一不能被机器翻译顶掉**的：原文改了就只更新原文 + 打 Review，译文与来源原样留着
        # （审查 P0-1：以前这里会直接走下面的“写机器译”分支，把玩家的译文换成 ai 且仓库里不留痕）
        if is_user and old_translated and source_changed:
            saved["Original"] = original
            saved["Translated"] = old_translated
            saved["Source"] = "user"
            saved["Review"] = "上游原文改过：这条是你的译文，等复核"
            entry["Name"] = saved
            needs_review.append((key, "Name", old_original, old_translated))
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

        # 上游原文本身就是中文（且没有成串英文/假名）：不写译文副本，只把原文记下来。
        # 玩家译在这里也不能静默消失：旧译文进复核报告（docs/translation-review.md，会被提交进库）。
        if upstream_is_localized(original):
            if is_user and old_translated:
                needs_review.append((key, "Name", old_original, old_translated))
            entry["Name"] = {"Original": original}
            return

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

        # 旧译文是「外文原文原样当译文」（例如日文简介）→ 不当已有译文，重翻；
        # 但**玩家译例外**：那可能是玩家故意的（保留原样），不能当成没译。
        if old_translated and not source_changed and (is_user or not copy_of_source(old_translated, original)):
            if old_original != original:
                saved["Original"] = original
            saved.setdefault("Translated", old_translated)
            if not saved.get("Source"):
                saved["Source"] = "user" if is_user else "ai"
            entry[field] = saved
            return

        # 玩家译同样不能被顶：原文改了就只更新原文 + 打 Review
        if is_user and old_translated and source_changed:
            saved["Original"] = original
            saved["Translated"] = old_translated
            saved["Source"] = "user"
            saved["Review"] = "上游原文改过：这条是你的译文，等复核"
            entry[field] = saved
            needs_review.append((key, field, old_original, old_translated))
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

        # 上游原文本身就是中文（且没有成串英文/假名）：不写译文副本，只把原文记下来。
        # 玩家在游戏里看到的本来就是中文，插件也不会去替换（2026-09-22 用户要求：自动跳过）。
        # 但玩家译不能静默消失 —— 旧译文进复核报告（会被提交进库）。
        if upstream_is_localized(original):
            if is_user and old_translated:
                needs_review.append((key, field, old_original, old_translated))
            entry[field] = {"Original": original}
            return

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

    def translate_descs(batch: list[tuple[str, str, str]], allow_retry: bool = True):
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

        retry_items: list[tuple[str, str, str]] = []
        for i, (key, punchline, description) in enumerate(batch):
            translated_p, translated_d = result.get(i, ("", ""))
            # 模型偶尔把原文原样回显（2026-09-23 bozjalone 实例）：不写假译文，稍后单独重试
            if punchline and not upstream_is_localized(punchline) and _echoed(punchline, translated_p):
                translated_p = ""
            if description and not upstream_is_localized(description) and _echoed(description, translated_d):
                translated_d = ""
            if (punchline and not translated_p) or (description and not translated_d):
                retry_items.append((key, punchline, description))
            with _lock:
                if punchline:
                    set_desc_pair(key, "Punchline", punchline, translated_p or None)
                if description:
                    set_desc_pair(key, "Description", description, translated_d or None)
                usage["ok"] += 1

        if retry_items and allow_retry:
            print(f"  原样回显 {len(retry_items)} 条，单独重试…")
            for item in retry_items:
                translate_descs([item], allow_retry=False)

    name_batches = [name_todo[i:i + 25] for i in range(0, len(name_todo), 25)]
    desc_batches = [desc_todo[i:i + 8] for i in range(0, len(desc_todo), 8)]

    with cf.ThreadPoolExecutor(max_workers=4) as executor:
        list(executor.map(translate_names, name_batches))
        list(executor.map(translate_descs, desc_batches))

    # 清掉语料里已经消失的条目？不删：保持历史译文，避免上游临时抽风导致词表缩水。

    # 上游原文改过、旧译文可能对不上的条目：写一份报告，供人工/插件侧（参与翻译）复核
    if needs_review:
        review_path = os.path.join(os.path.dirname(os.path.abspath(args.table)), "docs", "translation-review.md")
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
