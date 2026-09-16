#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""translate_names.py — 给 translations.json 补上插件名的中文译名（Name 字段）。

与 translate_descriptions.py 配套使用：
    python translate_descriptions.py            # 先产出 Punchline / Description
    python translate_names.py --sample 20       # 试翻名字，看质量
    python translate_names.py                   # 全量（断点续传）

规则（保守）：
    · 专有名词 / 品牌 / 代号 / 缩写（Penumbra、Glamourer、PalacePal、BOCCHI）保持原样；
    · 描述性英文短语（Daily Routines、Skip Cutscene）译成简短中文（≤10 字）；
    · 名字里已有的数字 / 符号保留；译名里出现中文才算有效，否则回退原文（等于不改名）。

用法要点：
    --index  语料索引 tsv（URL<TAB>文件路径），默认用上次全量扫描的 %TEMP%/repo_index.tsv
    --out    目标词表，默认仓库根目录的 translations.json（原地合并 Name 字段）
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
from collections import Counter

try:
    import requests
except ImportError:
    requests = None

TMP = os.environ.get("TEMP", r"C:/Users/Fire/AppData/Local/Temp")
DEFAULT_INDEX = os.path.join(TMP, "repo_index.tsv")
HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(HERE)
DEFAULT_OUT = os.path.join(REPO_ROOT, "translations.json")
DEFAULT_CACHE = os.path.join(HERE, "names_cache.jsonl")
CJK = re.compile(r"[\u4e00-\u9fff]")


# ------------------------------------------------------------------ 语料抽取 --

def load_names(index_path: str) -> dict[str, str]:
    """从抓下来的仓库文件里抽出 InternalName -> Name（同名取多数，平票取较长的）。"""
    votes: dict[str, Counter] = {}
    for line in open(index_path, encoding="utf-8"):
        parts = line.rstrip("\n").split("\t")
        if len(parts) < 2 or not parts[1] or not os.path.exists(parts[1]):
            continue
        try:
            doc = json.loads(open(parts[1], encoding="utf-8", errors="replace").read().lstrip("\ufeff"))
        except Exception:
            continue
        if not isinstance(doc, list):
            continue
        for item in doc:
            if not isinstance(item, dict):
                continue
            key = item.get("InternalName")
            name = (item.get("Name") or "").strip()
            if isinstance(key, str) and key and name:
                votes.setdefault(key, Counter())[name] += 1

    result = {}
    for key, counter in votes.items():
        if not counter:
            continue
        top = max(counter.values())
        candidates = [n for n, c in counter.items() if c == top]
        result[key] = max(candidates, key=len)
    return result


def needs_translation(name: str) -> bool:
    return bool(name) and not CJK.search(name)


def get_api_key() -> str | None:
    key = os.environ.get("DEEPSEEK_API_KEY")
    if key:
        return key.strip()
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment") as handle:
            value, _ = winreg.QueryValueEx(handle, "DEEPSEEK_API_KEY")
            return str(value).strip()
    except Exception:
        return None


SYS_PROMPT = (
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


def _chat(api_key: str, model: str, messages: list, proxies=None, timeout=120):
    payload = {
        "model": model,
        "messages": messages,
        "temperature": 0.2,
        "max_tokens": 4000,
        "thinking": {"type": "disabled"},
    }
    response = requests.post(
        "https://api.deepseek.com/chat/completions",
        json=payload,
        headers={"Authorization": f"Bearer {api_key}"},
        timeout=timeout,
        proxies=proxies,
    )
    response.raise_for_status()
    data = response.json()
    return data["choices"][0]["message"]["content"], data.get("usage", {})


def translate_batch(batch: list[tuple[str, str]], model: str, api_key: str):
    items = [{"id": i, "name": name} for i, (_, name) in enumerate(batch)]
    content, usage, last_error = None, {}, None
    for proxies in (None, {"http": "http://127.0.0.1:10808", "https": "http://127.0.0.1:10808"}):
        try:
            content, usage = _chat(
                api_key,
                model,
                [
                    {"role": "system", "content": SYS_PROMPT},
                    {"role": "user", "content": json.dumps({"items": items}, ensure_ascii=False)},
                ],
                proxies,
            )
            break
        except Exception as error:  # noqa: BLE001
            last_error = error
    if content is None:
        raise RuntimeError(f"API 失败: {last_error}")

    text = content.strip()
    if text.startswith("```"):
        text = re.sub(r"^```[a-zA-Z]*\n?", "", text)
        text = re.sub(r"\n?```$", "", text)
    parsed = json.loads(text)
    out = {}
    for item in parsed.get("items", []):
        out[int(item["id"])] = (item.get("n") or "").strip()
    return out, usage


_lock = threading.Lock()


def main(argv=None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--index", default=DEFAULT_INDEX)
    parser.add_argument("--out", default=DEFAULT_OUT)
    parser.add_argument("--cache", default=DEFAULT_CACHE)
    parser.add_argument("--model", default="deepseek-flash")
    parser.add_argument("--batch", type=int, default=25)
    parser.add_argument("--workers", type=int, default=4)
    parser.add_argument("--sample", type=int, default=0, help="只翻前 N 条看质量")
    parser.add_argument("--stats", action="store_true")
    args = parser.parse_args(argv)

    if requests is None:
        print("需要 requests：pip install requests")
        return 2

    if not os.path.exists(args.index):
        print(f"找不到语料索引：{args.index}（先跑一次 translate_descriptions.py 的语料抓取）")
        return 2

    names = load_names(args.index)
    todo_all = {k: v for k, v in names.items() if needs_translation(v)}
    print(f"语料: 唯一插件 {len(names)} 条 | 名称需要翻译 {len(todo_all)} 条")

    if args.stats:
        return 0

    done: dict[str, str] = {}
    if os.path.exists(args.cache):
        for line in open(args.cache, encoding="utf-8"):
            try:
                obj = json.loads(line)
                done[obj["k"]] = obj["n"]
            except Exception:
                pass
        print(f"缓存命中 {len(done)} 条")

    api_key = get_api_key()
    if not api_key:
        print("找不到 DEEPSEEK_API_KEY（环境变量或 HKCU\\Environment）")
        return 2

    todo = [(k, v) for k, v in todo_all.items() if k not in done]
    if args.sample:
        todo = todo[: args.sample]
    print(f"本次待翻 {len(todo)} 条（批次 {args.batch}，并发 {args.workers}，模型 {args.model}）\n")

    batches = [todo[i:i + args.batch] for i in range(0, len(todo), args.batch)]
    stats = {"in": 0, "out": 0, "ok": 0, "fail": 0}
    started = time.time()

    def work(batch):
        try:
            result, usage = translate_batch(batch, args.model, api_key)
        except Exception as error:  # noqa: BLE001
            # 批次失败按一半拆分重试（最细到单条），避免一整批因个别条目挂掉
            if len(batch) > 1:
                half = len(batch) // 2
                return work(batch[:half]) + work(batch[half:])
            with _lock:
                stats["fail"] += 1
            print(f"  [条目失败] {batch[0][0]}: {error}")
            return 0

        with _lock:
            stats["in"] += usage.get("prompt_tokens", 0) or 0
            stats["out"] += usage.get("completion_tokens", 0) or 0
        lines = []
        for i, (key, name) in enumerate(batch):
            translated = result.get(i, "")
            if not translated or not CJK.search(translated):
                translated = name  # 专有名词或模型没翻：保持原样（等于不改名）
            lines.append(json.dumps({"k": key, "orig": name, "n": translated}, ensure_ascii=False))
            with _lock:
                stats["ok"] += 1
        with _lock:
            with open(args.cache, "a", encoding="utf-8") as handle:
                handle.write("\n".join(lines) + "\n")
        return len(batch)

    with cf.ThreadPoolExecutor(max_workers=args.workers) as executor:
        list(executor.map(work, batches))

    print(f"\n完成: 成功 {stats['ok']} / 失败 {stats['fail']}，用时 {round(time.time() - started, 1)}s"
          f"，token 用量 in={stats['in']:,} out={stats['out']:,}")

    # 合并进目标词表（保留已有的 Punchline / Description）
    table = {}
    if os.path.exists(args.out):
        table = json.load(open(args.out, encoding="utf-8"))

    cache: dict[str, tuple[str, str]] = {}
    if os.path.exists(args.cache):
        for line in open(args.cache, encoding="utf-8"):
            try:
                obj = json.loads(line)
                cache[obj["k"]] = (obj["orig"], obj["n"])
            except Exception:
                pass

    merged = 0
    for key, (original, translated) in cache.items():
        entry = table.setdefault(key, {})
        entry["Name"] = {"Original": original, "Translated": translated}
        merged += 1

    with open(args.out, "w", encoding="utf-8") as handle:
        json.dump(table, handle, ensure_ascii=False, indent=1)
    print(f"词表已写出: {args.out}（{len(table)} 条，其中 Name 字段 {merged} 条）")
    return 0


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(errors="replace")
    except Exception:
        pass
    sys.exit(main())
