#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""translate_descriptions.py — 批量翻译卫月插件简介（Punchline + Description）为简体中文。

产出与 FastDalamudCN 同结构的 translations.json：
    { "<InternalName>": { "Punchline": {"Original":..., "Translated":...},
                          "Description": {"Original":..., "Translated":...} }, ... }

用法:
    python translate_descriptions.py --sample 10          # 试翻 10 条（先看质量）
    python translate_descriptions.py                      # 全量（自动断点续传）
    python translate_descriptions.py --stats              # 只看统计，不翻译

数据来源（按顺序取，能拿到哪个用哪个）:
    1. --index 指定的 tsv + 对应文件（默认用上次全量扫描抓下来的语料）
    2. --config 里的仓库列表（会现场抓取，慢）

翻译后端:
    --backend deepseek（默认，用环境变量/注册表里的 DEEPSEEK_API_KEY）
    --backend ollama  （本地，需要已 ollama pull 一个生成模型）
"""

from __future__ import annotations

import argparse
import concurrent.futures as cf
import json
import os
import re
import subprocess
import sys
import threading
import time

try:
    import requests
except ImportError:
    requests = None

TMP = os.environ.get("TEMP", r"C:/Users/Fire/AppData/Local/Temp")
DEFAULT_INDEX = os.path.join(TMP, "repo_index.tsv")
DEFAULT_CONFIG = os.path.join(os.environ.get("APPDATA", ""), "XIVLauncherCN", "dalamudConfig.json")
HERE = os.path.dirname(os.path.abspath(__file__))
CJK = re.compile(r"[\u4e00-\u9fff]")


# ------------------------------------------------------------------ 语料抽取 --

def load_corpus(index_path: str, config_path: str):
    """返回 OrderedDict[InternalName] = (punchline, description, source_url)"""
    uniq: dict[str, tuple] = {}

    def add(key: str, pl: str, de: str, url: str):
        if not key:
            return
        prev = uniq.get(key)
        if prev is None or len(prev[0]) + len(prev[1]) < len(pl) + len(de):
            uniq[key] = (pl, de, url)

    if os.path.exists(index_path):
        rows = []
        for line in open(index_path, encoding="utf-8"):
            p = line.rstrip("\n").split("\t")
            if len(p) >= 2 and p[1] and os.path.exists(p[1]):
                rows.append((p[0].lstrip("\ufeff"), p[1]))
        if rows:
            for url, path in rows:
                try:
                    doc = json.loads(open(path, encoding="utf-8", errors="replace").read().lstrip("\ufeff"))
                except Exception:
                    continue
                if not isinstance(doc, list):
                    continue
                for it in doc:
                    if not isinstance(it, dict):
                        continue
                    key = it.get("InternalName")
                    if isinstance(key, str) and key:
                        add(key, (it.get("Punchline") or "").strip(), (it.get("Description") or "").strip(), url)
            return uniq

    # 退路：现场抓（慢）
    cfg = json.load(open(config_path, encoding="utf-8"))
    urls = [x["Url"] for x in cfg["ThirdRepoList"]["$values"] if x.get("IsEnabled")]
    for u in urls:
        try:
            r = requests.get(u, timeout=25, headers={"User-Agent": "Dalamud/15.0.3.4"})
            doc = r.json()
        except Exception:
            continue
        if not isinstance(doc, list):
            continue
        for it in doc:
            if isinstance(it, dict):
                key = it.get("InternalName")
                if isinstance(key, str) and key:
                    add(key, (it.get("Punchline") or "").strip(), (it.get("Description") or "").strip(), u)
    return uniq


def needs_translation(text: str) -> bool:
    if not text:
        return False
    return len(CJK.findall(text)) / max(1, len(text)) <= 0.3


# ---------------------------------------------------------------------- 后端 --

def get_api_key() -> str | None:
    k = os.environ.get("DEEPSEEK_API_KEY")
    if k:
        return k.strip()
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment") as h:
            v, _ = winreg.QueryValueEx(h, "DEEPSEEK_API_KEY")
            return str(v).strip()
    except Exception:
        return None


SYS_PROMPT = (
    "你是专业的 FFXIV 插件文档译者。请把用户给出的 JSON 中每个插件的简介翻译成简体中文。"
    "规则：保持专有名词/插件名/命令（如 Penumbra、Glamourer、/pdr）的英文原样；"
    "不要添加解释、注释或礼貌用语；保留原有换行风格；只输出 JSON，不要代码块。"
    "输出格式：{\"items\":[{\"id\":<原 id>,\"p\":\"<Punchline 译文>\",\"d\":\"<Description 译文>\"}]}"
)


def _chat(url: str, api_key: str, model: str, messages: list, proxies, timeout=120):
    payload = {"model": model, "messages": messages, "temperature": 0.2, "max_tokens": 8000}
    r = requests.post(url, json=payload, headers={"Authorization": f"Bearer {api_key}"},
                      timeout=timeout, proxies=proxies)
    r.raise_for_status()
    data = r.json()
    return data["choices"][0]["message"]["content"], data.get("usage", {})


def translate_batch_deepseek(batch: list[tuple[str, str, str]], model: str, api_key: str):
    items = [{"id": i, "p": pl, "d": de} for i, (k, pl, de) in enumerate(batch)]
    content, usage = None, {}
    last_err = None
    for proxies in (None, {"http": "http://127.0.0.1:10808", "https": "http://127.0.0.1:10808"}):
        try:
            content, usage = _chat("https://api.deepseek.com/chat/completions", api_key, model,
                                   [{"role": "system", "content": SYS_PROMPT},
                                    {"role": "user", "content": json.dumps({"items": items}, ensure_ascii=False)}],
                                   proxies)
            break
        except Exception as e:
            last_err = e
    if content is None:
        raise RuntimeError(f"API 失败: {last_err}")

    s = content.strip()
    if s.startswith("```"):
        s = re.sub(r"^```[a-zA-Z]*\n?", "", s)
        s = re.sub(r"\n?```$", "", s)
    obj = json.loads(s)
    out = {}
    for it in obj.get("items", []):
        i = int(it["id"])
        out[i] = (it.get("p", ""), it.get("d", ""))
    return out, usage


def translate_batch_ollama(batch, model):
    items = [{"id": i, "p": pl, "d": de} for i, (k, pl, de) in enumerate(batch)]
    r = requests.post("http://127.0.0.1:11434/api/chat", timeout=600, json={
        "model": model, "stream": False, "options": {"temperature": 0.2},
        "messages": [{"role": "system", "content": SYS_PROMPT},
                     {"role": "user", "content": json.dumps({"items": items}, ensure_ascii=False)}],
    })
    r.raise_for_status()
    content = r.json()["message"]["content"]
    s = content.strip()
    if s.startswith("```"):
        s = re.sub(r"^```[a-zA-Z]*\n?", "", s)
        s = re.sub(r"\n?```$", "", s)
    obj = json.loads(s)
    out = {}
    for it in obj.get("items", []):
        out[int(it["id"])] = (it.get("p", ""), it.get("d", ""))
    return out, {}


# ------------------------------------------------------------------------ 主 --

_lock = threading.Lock()


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--index", default=DEFAULT_INDEX)
    ap.add_argument("--config", default=DEFAULT_CONFIG)
    ap.add_argument("--out", default=os.path.join(HERE, "translations.json"))
    ap.add_argument("--cache", default=os.path.join(HERE, "translations_cache.jsonl"))
    ap.add_argument("--backend", default="deepseek", choices=["deepseek", "ollama"])
    ap.add_argument("--model", default=None)
    ap.add_argument("--batch", type=int, default=8)
    ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--sample", type=int, default=0, help="只翻前 N 条（试质量）")
    ap.add_argument("--stats", action="store_true")
    args = ap.parse_args(argv)

    if requests is None:
        print("需要 requests：pip install requests")
        return 2

    model = args.model or ("deepseek-flash" if args.backend == "deepseek" else "qwen3:8b")
    api_key = get_api_key() if args.backend == "deepseek" else None
    if args.backend == "deepseek" and not api_key:
        print("找不到 DEEPSEEK_API_KEY（环境变量或 HKCU\\Environment）")
        return 2

    corpus = load_corpus(args.index, args.config)
    todo_all = {k: v for k, v in corpus.items()
                if needs_translation(v[0]) or needs_translation(v[1])}
    print(f"语料: 唯一插件 {len(corpus)} 条 | 需要翻译 {len(todo_all)} 条 "
          f"| 字符 {sum(len(v[0])+len(v[1]) for v in todo_all.values()):,}")

    if args.stats:
        return 0

    # 读缓存
    done = {}
    if os.path.exists(args.cache):
        for line in open(args.cache, encoding="utf-8"):
            try:
                o = json.loads(line)
                done[o["k"]] = o
            except Exception:
                pass
        print(f"缓存命中 {len(done)} 条")

    todo = [(k, v[0], v[1]) for k, v in todo_all.items() if k not in done]
    if args.sample:
        todo = todo[: args.sample]
    print(f"本次待翻 {len(todo)} 条（批次 {args.batch}，并发 {args.workers}，模型 {model}）\n")

    batches = [todo[i:i + args.batch] for i in range(0, len(todo), args.batch)]
    stats = {"in": 0, "out": 0, "ok": 0, "fail": 0}
    t0 = time.time()

    def work(b):
        try:
            if args.backend == "deepseek":
                res, usage = translate_batch_deepseek(b, model, api_key)
            else:
                res, usage = translate_batch_ollama(b, model)
            with _lock:
                stats["in"] += usage.get("prompt_tokens", 0) or 0
                stats["out"] += usage.get("completion_tokens", 0) or 0
            lines = []
            for i, (k, pl, de) in enumerate(b):
                tp, td = res.get(i, ("", ""))
                if not tp and pl:
                    tp = pl          # 模型漏翻时保底
                if not td and de:
                    td = de
                obj = {"k": k,
                       "p": {"Original": pl, "Translated": tp},
                       "d": {"Original": de, "Translated": td}}
                lines.append(json.dumps(obj, ensure_ascii=False))
                with _lock:
                    stats["ok"] += 1
            with _lock:
                with open(args.cache, "a", encoding="utf-8") as f:
                    f.write("\n".join(lines) + "\n")
            return len(b)
        except Exception as e:
            with _lock:
                stats["fail"] += len(b)
            print(f"  [批次失败] {e}")
            return 0

    with cf.ThreadPoolExecutor(max_workers=args.workers) as ex:
        list(ex.map(work, batches))

    print(f"\n完成: 成功 {stats['ok']} / 失败 {stats['fail']}，用时 {round(time.time()-t0,1)}s"
          f"，token 用量 in={stats['in']:,} out={stats['out']:,}")

    # 汇总输出（缓存 + 旧文件里已有的，保持全量）
    final = {}
    for o in list(done.values()):
        final[o["k"]] = {"Punchline": o["p"], "Description": o["d"]}
    for line in (open(args.cache, encoding="utf-8") if os.path.exists(args.cache) else []):
        try:
            o = json.loads(line)
            final[o["k"]] = {"Punchline": o["p"], "Description": o["d"]}
        except Exception:
            pass
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(final, f, ensure_ascii=False, indent=1)
    print(f"词表已写出: {args.out}（{len(final)} 条）")
    return 0


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(errors="replace")
    except Exception:
        pass
    sys.exit(main())
