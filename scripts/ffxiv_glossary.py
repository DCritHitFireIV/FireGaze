#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ffxiv_glossary.py — 从公开 datamining 数据构建「英文 → 国服官方中文」术语表。

数据源（运行时拉取，不入库）：
    英文：https://raw.githubusercontent.com/xivapi/ffxiv-datamining/master/csv/en/<Sheet>.csv
    中文：https://raw.githubusercontent.com/thewakingsands/ffxiv-datamining-cn/master/<Sheet>.csv

两边都是 SaintCoinach 导出的同结构 CSV（第一列是行 key），按 key join 得到对照。
用途：翻译插件简介时，把命中的官方译名（地下城、地名、职业、技能、状态…）喂给模型，
保证「Palace of the Dead → 死者宫殿」这类专有名词一致。

缓存：scripts/.cache/（已 gitignore；删掉会重新下载）
"""

from __future__ import annotations

import csv
import io
import os
import re
import urllib.request

EN_BASE = "https://raw.githubusercontent.com/xivapi/ffxiv-datamining/master/csv/en"
CN_BASE = "https://raw.githubusercontent.com/thewakingsands/ffxiv-datamining-cn/master"

# 表名 → 名称列（找不到该列时回退到第 2 列）
SHEETS: dict[str, str] = {
    "PlaceName": "Name",
    "TerritoryType": "Name",
    "ContentFinderCondition": "Name",
    "ClassJob": "Name",
    "Action": "Name",
    "Status": "Name",
    "Mount": "Singular",
    "Emote": "Name",
    "BNpcName": "Singular",
    "Orchestrion": "Name",
}

CACHE_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), ".cache")
CJK = re.compile(r"[\u4e00-\u9fff]")
WORD_RE = re.compile(r"[A-Za-z][A-Za-z0-9'’\-]*")

# 常见泛词不参与匹配（避免把普通句子里的词当成专有名词）
STOP_SINGLE = {
    "attack", "damage", "target", "player", "party", "enemy", "action", "ready", "start",
    "window", "option", "setting", "module", "function", "system", "button", "display",
}


def _fetch(url: str, use_cache: bool = True) -> str:
    os.makedirs(CACHE_DIR, exist_ok=True)
    cache_file = os.path.join(CACHE_DIR, re.sub(r"[^A-Za-z0-9._-]", "_", url.split("//")[-1]))

    if use_cache and os.path.exists(cache_file) and os.path.getsize(cache_file) > 0:
        with open(cache_file, encoding="utf-8", errors="replace") as handle:
            return handle.read()

    request = urllib.request.Request(
        url,
        headers={
            "User-Agent": "FireGaze-Glossary/1.0 (+https://github.com/DCritHitFireIV/FireGaze)",
        },
    )
    with urllib.request.urlopen(request, timeout=120) as response:
        text = response.read().decode("utf-8", errors="replace")

    with open(cache_file, "w", encoding="utf-8", errors="replace") as handle:
        handle.write(text)

    return text


def _parse_sheet(text: str, name_column: str) -> dict[int, str]:
    """返回 {row_key: 名称}。自动找表头行（含 name_column 的那一行），跳过类型行。"""
    reader = csv.reader(io.StringIO(text))
    rows = list(reader)
    if not rows:
        return {}

    header_index = None
    name_col = 1
    for index, row in enumerate(rows[:4]):
        if name_column in row:
            header_index = index
            name_col = row.index(name_column)
            break

    if header_index is None:
        header_index = 0

    result: dict[int, str] = {}
    for row in rows[header_index + 1:]:
        if len(row) <= name_col:
            continue
        raw_key = (row[0] or "").strip()
        if not raw_key.isdigit():
            continue
        value = (row[name_col] or "").strip()
        if value:
            result[int(raw_key)] = value

    return result


def _clean_english(text: str) -> str:
    text = re.sub(r"\s+", " ", text).strip()
    return text


def build_glossary(refresh: bool = False, verbose: bool = True) -> dict[str, str]:
    """English(小写) → 简体中文。"""
    glossary: dict[str, str] = {}

    for sheet, name_column in SHEETS.items():
        try:
            en_text = _fetch(f"{EN_BASE}/{sheet}.csv", use_cache=not refresh)
            cn_text = _fetch(f"{CN_BASE}/{sheet}.csv", use_cache=not refresh)
        except Exception as error:  # noqa: BLE001
            if verbose:
                print(f"  [术语表] {sheet} 下载失败：{error}")
            continue

        try:
            en_map = _parse_sheet(en_text, name_column)
            cn_map = _parse_sheet(cn_text, name_column)
        except Exception as error:  # noqa: BLE001
            if verbose:
                print(f"  [术语表] {sheet} 解析失败：{error}")
            continue

        added = 0
        for key, english in en_map.items():
            chinese = cn_map.get(key)
            if not chinese:
                continue

            english = _clean_english(english)
            chinese = chinese.strip()
            if not english or not chinese or english.lower() == chinese.lower():
                continue
            if not CJK.search(chinese):
                continue
            if not (4 <= len(english) <= 48) or not (1 <= len(chinese) <= 24):
                continue

            words = english.split()
            if len(words) == 1 and (len(english) < 7 or english.lower() in STOP_SINGLE):
                continue
            # 多词术语里包含过短/泛词的（如 "on High"）直接丢掉
            if any(len(word) < 3 for word in words):
                continue

            glossary.setdefault(english.lower(), chinese)
            # 游戏数据里常带冠词（the Palace of the Dead），简化掉一个变体供匹配
            if english.lower().startswith("the "):
                glossary.setdefault(english[4:].lower(), chinese)
            added += 1

        if verbose:
            print(f"  [术语表] {sheet}: {added} 条")

    if verbose:
        print(f"术语表合计：{len(glossary)} 条")

    return glossary


def find_terms(texts: list[str], glossary: dict[str, str], limit: int = 40) -> list[tuple[str, str]]:
    """从文本里找出命中术语表的短语（优先长的、且首字母大写的专有名词）。"""
    if not glossary:
        return []

    hits: set[str] = set()
    for text in texts:
        words = WORD_RE.findall(text)
        for size in (4, 3, 2, 1):
            for start in range(len(words) - size + 1):
                chunk = words[start:start + size]
                # 专有名词启发：短语首词必须是大写开头（避开 on high / trap 这类普通词）
                if not chunk[0][:1].isupper():
                    continue
                phrase = " ".join(word.lower() for word in chunk)
                if phrase in glossary:
                    hits.add(phrase)

    ordered = sorted(hits, key=lambda phrase: (-len(phrase.split()), -len(phrase)))

    # 长短语命中的话，丢弃它包含的短语（eureka orthos ↔ eureka、the gold saucer ↔ gold saucer）
    kept: list[str] = []
    for phrase in ordered:
        if any(phrase in longer for longer in kept):
            continue
        kept.append(phrase)

    return [(phrase, glossary[phrase]) for phrase in kept[:limit]]


if __name__ == "__main__":
    table = build_glossary(refresh=True)
    for sample in ("Palace of the Dead", "Heaven on High", "Eureka Orthos", "The Gold Saucer",
                   "Ultimate Raid", "Savage", "Black Mage", "Limsa Lominsa"):
        print(f"  {sample} -> {table.get(sample.lower(), '（未收录）')}")
