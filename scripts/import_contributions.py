#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""import_contributions.py — 把玩家在「参与翻译」页的贡献写回词表，并可一条命令推上仓库。

来源：推送（server3 酱）正文里的 ```json``` 代码块，或插件导出的 contributions-*.json。
把它存成一个文件，路径当第一个参数传进来即可。

用法：
    python scripts/import_contributions.py <导出文件.json> [--dry-run]
    python scripts/import_contributions.py <导出文件.json> --push      # 收录 + 提交 + 推上 GitHub

`--push` 是维护者桌面端的「审完直接进仓库」：
  收录词表 → 落 docs/contributions/ 原始存档 + 重写 docs/contributions.md
  → git add/commit → fetch+rebase → push origin main
GitHub 直连不通时走本机代理（HTTPS_PROXY 或 127.0.0.1:10808），失败会把要手敲的命令打出来。

规则（与插件里的口径一致）：
  ① 玩家译文一旦写入，`Source` = `user`，机器翻译永不覆盖；
  ② 上游原文变了（与贡献里的 Original 对不上）→ 不写原处，记入报告，
     由人决定是不是还要这条译文；
  ③ 每次写入都会把旧值追加到 scripts/translation-history.jsonl，可回滚；
  ④ 原文改过（记录里 Stale=true，或表里的原文与贡献对不上）→ 写入时加 `Review` 说明；
  ⑤ 官方主库插件（记录里 Official=true）同样接受：上游本来就没有中文，
     玩家提交的译文会成为唯一来源；注意 update_translations.py 的语料不含官方库，
     所以这类条目只能靠人/玩家维护。
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
try:
    import validate_contribution as rules   # 「同一套口径」：长度 / 控制字符 / 网址 / HTML / 违规词
except Exception:  # noqa: BLE001
    rules = None

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_TABLE = os.path.join(REPO_ROOT, "translations.json")
HISTORY = os.path.join(REPO_ROOT, "scripts", "translation-history.jsonl")
# 公开留档：原始投稿与一份人可读的汇总（推送服务里的消息会过期，仓库里这份才是长期凭证）
CONTRIB_DIR = os.path.join(REPO_ROOT, "docs", "contributions")
CONTRIB_LOG = os.path.join(REPO_ROOT, "docs", "contributions.md")
FIELDS = ("Name", "Punchline", "Description")


def git(*argv: str, proxy: str | None = None) -> subprocess.CompletedProcess:
    """在仓库根目录跑一条 git，默认静默、不弹凭据窗口。"""
    env = os.environ.copy()
    env.setdefault("GIT_TERMINAL_PROMPT", "0")
    env.setdefault("GCM_INTERACTIVE", "never")
    if proxy:
        env.setdefault("HTTPS_PROXY", proxy)
        env.setdefault("HTTP_PROXY", proxy)
    return subprocess.run(
        ["git", *argv],
        cwd=REPO_ROOT,
        env=env,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )


def github_token() -> str | None:
    """从凭据管理器取一个 github token（本机 GCM 里已登录，比让 git 自己弹窗可靠）。"""
    try:
        proc = subprocess.run(
            ["git", "credential", "fill"],
            cwd=REPO_ROOT,
            input=f"protocol=https\nhost=github.com\nusername=DCritHitFireIV\n\n",
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=30,
        )
    except Exception:  # noqa: BLE001
        return None

    for line in (proc.stdout or "").splitlines():
        if line.startswith("password="):
            token = line[len("password="):].strip()
            return token or None
    return None


def push_to_github(paths: list[str], message: str, proxy: str = "http://127.0.0.1:10808") -> bool:
    """收录后一条命令进仓库：add → commit → fetch+rebase → push。

    失败不会把已写好的文件弄丢（它们已经在工作区/提交里），只会把要手敲的命令打出来。
    推送优先用凭据管理器里的 token（本机实测比让 git 自己弹窗稳），拿不到才回退成普通 push。
    """
    print("\n=== 推上仓库 ===")
    add = git("add", *paths)
    if add.returncode != 0:
        detail = (add.stdout + add.stderr).strip().splitlines()
        print("  git add 失败：" + (detail[0] if detail else "未知原因"))
        for line in detail[1:4]:
            print("    " + line)
        return False

    commit = git("commit", "-m", message)
    if commit.returncode != 0:
        detail = (commit.stdout + commit.stderr).strip().splitlines()
        print("  git commit：没有提交成功 →", detail[-1] if detail else "未知原因")
        print("  已停在这里（文件都在工作区，没有丢）：")
        print(f"    cd {REPO_ROOT}")
        print("    git add -A && git commit -m \"收录译文\" && git push origin main")
        return False

    print("  git commit：已提交")

    if git("fetch", "origin", "main", proxy=proxy).returncode == 0:
        rebase = git("rebase", "origin/main")
        print("  fetch+rebase：" + ("已同步" if rebase.returncode == 0 else "需要手动处理"))
        if rebase.returncode != 0:
            git("rebase", "--abort")
            print("（已回退 rebase，避免卡住；请手动 fetch/rebase 后 push）")
    else:
        print("  git fetch：跳过（网络不通？）")

    token = github_token()
    if token:
        push = git("push", f"https://DCritHitFireIV:{token}@github.com/DCritHitFireIV/FireGaze.git", "main", proxy=proxy)
    else:
        print("  （没取到 token，尝试普通 push）")
        push = git("push", "origin", "main", proxy=proxy)

    if push.returncode == 0:
        print("  已推送到 GitHub ✓")
        print("  留档：https://github.com/DCritHitFireIV/FireGaze/blob/main/docs/contributions.md")
        return True

    detail = (push.stderr or push.stdout).strip().splitlines()
    redacted = (detail[-1] if detail else "未知原因").replace(token, "***") if token else (detail[-1] if detail else "未知原因")
    print("  推送失败：" + redacted)
    print("  手动执行：")
    print(f"    cd {REPO_ROOT}")
    print("    git fetch origin main && git rebase origin/main && git push origin main")
    return False


def check_record(record: dict) -> tuple[list[str], list[str]]:
    """按「硬规则 / 软提醒」两层体检一条投稿（与 validate_contribution.py 同一套口径）。

    硬：内部名不合法、字段名不认识、译文超长（40/99/999）、控制字符、占位值（测试1/test/xxx/123…）、
        改后与改前一样（空操作）。——要 --force 才肯收。
    软：译文为空（=清除这条）、网址、HTML、违规词、译文只有一两个字 —— 只打印提醒。
    审查 2026-09-22 指出的就是这里以前“零校验”。
    """
    hard: list[str] = []
    soft: list[str] = []
    internal = str(record.get("InternalName") or "").strip()
    field = str(record.get("Field") or "").strip()
    original = str(record.get("Original") or "")
    translated = str(record.get("Translated") or "")

    if not internal or len(internal) > 200 or any(ch.isspace() for ch in internal):
        hard.append("插件内部名不合法")
    if rules is not None and field not in rules.FIELDS:
        hard.append(f"字段名不认识：{field}")

    if not translated.strip():
        soft.append("译文为空（等于清除这条译文）")
    else:
        limit = rules.FIELD_LIMITS.get(field, 999) if rules is not None else 999
        if len(translated) > limit:
            hard.append(f"译文太长（{len(translated)} > {limit}）")
        if rules is not None and rules.CONTROL.search(translated):
            hard.append("译文里有控制字符")
        if rules is not None and rules.URLISH.search(translated):
            soft.append("译文里有网址")
        if rules is not None and rules.HTMLLISH.search(translated):
            soft.append("译文里有 HTML 标记")
        if rules is not None and any(w.lower() in translated.lower() for w in rules.DENY_WORDS):
            soft.append("译文里含违规词")
        if translated.strip() == original.strip():
            hard.append("改后与改前一样（空操作）")
        if looks_like_placeholder(record):
            hard.append(looks_like_placeholder(record) or "看着像占位值")
        if field in ("Punchline", "Description") and len(translated.strip()) <= 2:
            soft.append("译文只有一两个字")

    return hard, soft


def guess_source(payload: dict, records: list[dict]) -> str:
    """猜这批是谁改的：`player` / `maintainer` / `unknown`。

    不是插件导出的（缺 Id / TimeLocal）或 note 里写了维护者动作 → maintainer；
    拿不准就回 unknown，让调用方停下来要个明确开关（审查 P1-1：不能只靠人记得加参数）。
    """
    note = str(payload.get("note") or "")
    if re.search(r"维护者|修正|撤回|回滚|测试|maintainer", note, re.IGNORECASE):
        return "maintainer"
    if all(str(r.get("Id") or "").strip() for r in records):
        return "player"
    return "unknown"


FIELD_LABELS = {"Name": "插件名", "Punchline": "一行简介", "Description": "插件详情"}

# 看着就像测试/占位数据的值：不阻断，但要求 --force 才肯收录
# （2026-09-22 事故：用真实插件测提交流程，结果「测试1」被收录并公示出去了）
_PLACEHOLDER = re.compile(r"^(?:测试|test|todo|tbd|xxx|asdf|qwe|abc|aaa|123|111)\d*$", re.IGNORECASE)


def looks_like_placeholder(record: dict) -> str | None:
    """返回「为什么觉得它可疑」，可疑就返回原因，否则 None。只做轻量启发，不调模型。"""
    text = (record.get("Translated") or "").strip()
    field = record.get("Field") or ""
    if not text:
        return None
    if _PLACEHOLDER.match(text):
        return "看着像占位值"
    if "测试" in text or re.search(r"\btest\b", text, re.IGNORECASE):
        return "包含「测试 / test」"
    if field in ("Punchline", "Description") and len(text) <= 2:
        return "译文只有一两个字"
    return None


def shorten(text: str, limit: int = 60) -> str:
    """表格里塞不下的长文本改成一行 + 省略号（全文在 jsonl 与原始投稿里）。"""
    one_line = " ".join((text or "").split())
    if not one_line:
        return "（空）"
    return one_line if len(one_line) <= limit else one_line[:limit] + "…"


def write_json_atomic(path: str, doc: dict) -> None:
    """先写临时文件再 os.replace（审查 P2-4：直写中途崩会留下半份）。"""
    tmp = f"{path}.tmp"
    with open(tmp, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(doc, handle, ensure_ascii=False, indent=1)
        handle.write("\n")
    os.replace(tmp, path)


def archive_payload(payload: dict, contrib_dir: str = CONTRIB_DIR, maintainer: bool = False) -> str:
    """把收到的原始投稿 JSON 原样存一份进仓库。

    为什么要存：投稿只存在于推送服务（手机/后台）的消息里，消息一过期或被清就什么都没了；
    仓库里这份才是永久凭证。内容本来就只有译文（不含任何玩家信息），可以公开。
    维护者自己的修正（不是玩家投稿）文件名带 maintainer- 前缀，从目目录上就分得清。
    """
    os.makedirs(contrib_dir, exist_ok=True)
    stamp = time.strftime("%Y-%m-%dT%H%M%S")
    prefix = "maintainer-" if maintainer else ""
    path = os.path.join(contrib_dir, f"{prefix}{stamp}.json")
    # 同一秒里收两批时不能静默盖掉前一份（审查 P2-2）
    seq = 2
    while os.path.exists(path):
        path = os.path.join(contrib_dir, f"{prefix}{stamp}-{seq}.json")
        seq += 1
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=1)
        handle.write("\n")
    return path


def write_public_log(history_path: str = HISTORY, log_path: str = CONTRIB_LOG) -> tuple[int, int, int, int]:
    """按 translation-history.jsonl 重新生成人可读的公开留档（幂等：重复跑不会重复累加）。

    **玩家投稿与维护者修正分开记**（用户 2026-09-22 指出：我拿同一个脚本把测试值改回去时，
    那条「撤回」被当成玩家投稿写进了公示里，看着像是玩家改的）。
    历史里 `by` 为空视作玩家投稿（早期行没有这个字段）。
    """
    if not os.path.exists(history_path):
        return (0, 0, 0, 0)

    rows: list[dict] = []
    with open(history_path, encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                rows.append(json.loads(line))
            except json.JSONDecodeError:
                continue

    players = [r for r in rows if (r.get("by") or "player") == "player"]
    maintainers = [r for r in rows if (r.get("by") or "player") == "maintainer"]
    tests = [r for r in rows if (r.get("by") or "player") in ("test", "void")]
    done_keys = {(r.get("plugin"), r.get("field"), (r.get("to") or "").strip()) for r in rows}

    # 「已收到 · 未并入」：从每次收录的存档侧记里读（审查 P1-3：跳过的东西以前只存在于终端输出里）
    pending: list[dict] = []
    contrib_dir = os.path.dirname(log_path)
    for name in sorted(os.listdir(contrib_dir)) if os.path.isdir(contrib_dir) else []:
        if not name.endswith(".json"):
            continue
        try:
            with open(os.path.join(contrib_dir, name), encoding="utf-8") as handle:
                doc = json.load(handle)
        except Exception:  # noqa: BLE001
            continue
        side = doc.get("import") or {}
        for row in side.get("skipped") or []:
            key = (row.get("plugin"), row.get("field"), (row.get("to") or "").strip())
            if key in done_keys:
                continue
            row = dict(row)
            row["at"] = side.get("at") or ""
            pending.append(row)

    lines = [
        "# 译文留档",
        "",
        "本文件由 `scripts/import_contributions.py` 自动生成，分三部分：",
        "",
        "- **玩家投稿**：玩家在游戏内「参与翻译」里提交、经维护者收录的译文；",
        "- **维护者修正**：维护者自己改的（例如撤回测试值、修正错字）——它们不是玩家提交的，别归错人；",
        "- **测试数据**：为验证流程写进去、随后已回滚的；",
        "",
        "另外单列一节「已收到 · 未并入」：收到了但因为不可信（原文对不上、校验不过）没进的。",
        "",
        "三部分都只含「时间 / 插件 / 字段 / 改前 → 改后」，没有账号或机器信息；",
        "原始投稿 JSON 存在 `docs/contributions/` 下（推送服务里的消息会过期，仓库里这两份才是长期凭证，",
        "维护者自己的那份文件名带 `maintainer-` 前缀）。查自己那条：在 `scripts/translation-history.jsonl` 里搜",
        "插件内部名或译文即可（每条都带「投稿时间 + 收录时间 + 来源 Id」）。",
        "",
        f"玩家投稿 {len(players)} 条 · 维护者修正 {len(maintainers)} 条 · 测试数据 {len(tests)} 条；"
        f"待复核 {len(pending)} 条；最近更新 {time.strftime('%Y-%m-%d %H:%M')}",
        "",
    ]

    def append_section(title: str, items: list[dict]) -> None:
        lines.append(f"## {title}")
        lines.append("")
        if not items:
            lines.append("（还没有）")
            lines.append("")
            return

        by_day: dict[str, list[dict]] = {}
        for row in items:
            stamp = row.get("submitted") or row.get("time") or ""
            by_day.setdefault(stamp[:10], []).append(row)

        for day in sorted(by_day, reverse=True):
            day_items = by_day[day]
            lines.append(f"### {day}（{len(day_items)} 条）")
            lines.append("")
            lines.append("| 时间 | 插件 | 字段 | 改前 | 改后 |")
            lines.append("|---|---|---|---|---|")
            for row in day_items:
                stamp = row.get("submitted") or row.get("time") or ""
                lines.append(
                    "| {t} | {p} | {f} | {a} | {b} |".format(
                        t=stamp[11:16] or "—",
                        p=row.get("plugin") or "",
                        f=FIELD_LABELS.get(row.get("field") or "", row.get("field") or ""),
                        a=shorten(row.get("from") or ""),
                        b=shorten(row.get("to") or ""),
                    )
                )
            lines.append("")

    append_section("玩家投稿", players)
    append_section("维护者修正", maintainers)
    append_section("测试数据（已回滚）", tests)

    lines.append("## 已收到 · 未并入（待复核）")
    lines.append("")
    if pending:
        lines.append("| 收到 | 插件 | 字段 | 想改成 | 原因 |")
        lines.append("|---|---|---|---|---|")
        for row in pending:
            lines.append(
                "| {t} | {p} | {f} | {b} | {w} |".format(
                    t=(row.get("at") or "")[5:16] or "—",
                    p=row.get("plugin") or "",
                    f=FIELD_LABELS.get(row.get("field") or "", row.get("field") or ""),
                    b=shorten(row.get("to") or "", 40),
                    w=row.get("reason") or "",
                )
            )
        lines.append("")
    else:
        lines.append("（没有）")
        lines.append("")

    os.makedirs(os.path.dirname(log_path) or ".", exist_ok=True)
    with open(log_path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(lines))
    return (len(players), len(maintainers), len(tests), len(pending))


def load_json(path: str) -> dict:
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("contributions", help="插件导出的 contributions-*.json")
    parser.add_argument("--table", default=DEFAULT_TABLE)
    parser.add_argument("--history", default=HISTORY, help="历史留档 jsonl（测试时可换成临时路径）")
    parser.add_argument("--contrib-dir", default=CONTRIB_DIR, help="原始投稿存档目录")
    parser.add_argument("--contrib-log", default=CONTRIB_LOG, help="公开留档 markdown")
    parser.add_argument("--dry-run", action="store_true", help="只报告会改什么，不写文件")
    parser.add_argument("--push", action="store_true", help="收录后直接提交并推上 GitHub（维护者桌面端用）")
    parser.add_argument("--maintainer", action="store_true", help="这批是维护者自己改的（留档里归到「维护者修正」，不算玩家投稿）")
    parser.add_argument("--player", action="store_true", help="这批确实是玩家投稿（当你本机留下的 JSON 缺 Id/TimeLocal 时用）")
    parser.add_argument("--force", action="store_true", help="即使有硬问题（测试值 / 超长 / 控制字符 / 空操作）也照样收录")
    args = parser.parse_args(argv)

    payload = load_json(args.contributions)
    records = payload.get("contributions") or []
    if not records:
        print("这份文件里没有贡献条目。")
        return 1

    # 这批算谁的？（审查 P1-1：以前默认 player，靠人记得加 --maintainer，就会把维护者的修正
    # 误记成玩家投稿——今天的事故就是这么来的）
    if args.maintainer:
        source_kind = "maintainer"
    elif args.player:
        source_kind = "player"
    else:
        source_kind = guess_source(payload, records)
        if source_kind == "unknown":
            print("看不出这批是玩家投稿还是维护者修正（缺 Id/TimeLocal、note 里也没写）：")
            print("  是玩家投稿就加 --player；是你自己改的就加 --maintainer。（这批还什么都没写）")
            return 1
        print(f"这批没有指定来源，按内容判定为「{'维护者修正' if source_kind == 'maintainer' else '玩家投稿'}」；"
              "不对就加 --player / --maintainer 重跑")

    # 两层体检（审查 P0-2 / P1-2）：硬问题要 --force 才过；软提醒只打印。
    problem_rows: list[tuple[dict, list[str], list[str]]] = []
    for record in records:
        hard, soft = check_record(record)
        if hard or soft:
            problem_rows.append((record, hard, soft))

    if problem_rows:
        print(f"⚠ {len(problem_rows)} 条需要看一眼：")
        for record, hard, soft in problem_rows:
            label = f"{record.get('InternalName')} / {record.get('Field')} = 「{record.get('Translated')}」"
            if hard:
                print(f"   · [硬] {label}：{'；'.join(hard)}")
            if soft:
                print(f"   · [提醒] {label}：{'；'.join(soft)}")

    hard_count = sum(1 for _, hard, _ in problem_rows if hard)
    if hard_count and not args.force and not args.dry_run:
        print(f"（有 {hard_count} 条硬问题，确认无误就加 --force 重跑；这批还什么都没写）")
        return 1

    table: dict = {}
    if os.path.exists(args.table):
        table = load_json(args.table)

    entries = {key: value for key, value in table.items() if not key.startswith("_")}
    print(f"词表现有 {len(entries)} 条；本次贡献 {len(records)} 条")

    applied = 0
    skipped: list[str] = []
    skipped_rows: list[dict] = []
    history: list[dict] = []

    # 去重（审查 P2-2）：同一份投稿（按记录 Id）已经收过就不再写一遍，
    # 否则词表值后来被别的东西改过时，重复 import 会让留档多一条、计数翻倍。
    seen_ids: set[str] = set()
    if os.path.exists(args.history):
        with open(args.history, encoding="utf-8") as handle:
            for line in handle:
                line = line.strip()
                if not line:
                    continue
                try:
                    row = json.loads(line)
                except json.JSONDecodeError:
                    continue
                src = str(row.get("source") or "").strip()
                if src:
                    seen_ids.add(f"{src}|{row.get('plugin')}|{row.get('field')}")

    for record in records:
        key = (record.get("InternalName") or "").strip()
        field = (record.get("Field") or "").strip()
        original = record.get("Original") or ""
        translated = record.get("Translated") or ""
        record_id = str(record.get("Id") or "").strip()

        if record_id and f"{record_id}|{key}|{field}" in seen_ids:
            skipped.append(f"{key} / {field}：这条以前已经收过（Id={record_id}），不重复写")
            skipped_rows.append({"plugin": key, "field": field, "to": translated, "reason": "重复投稿（已收过）"})
            continue

        if not key or field not in FIELDS:
            skipped.append(f"{key or '?'} / {field or '?'}：字段名不认识")
            skipped_rows.append({"plugin": key, "field": field, "to": translated, "reason": "字段名不认识"})
            continue

        entry = table.setdefault(key, {})
        pair = entry.get(field) or {}
        old_original = pair.get("Original") or ""
        old_translated = pair.get("Translated") or ""
        source_changed = bool(old_original) and bool(original) and old_original != original

        if source_changed:
            skipped.append(
                f"{key} / {field}：上游原文已经变了，先复核再决定"
                f"（旧：{old_original[:40]}… / 贡献：{original[:40]}…）"
            )
            skipped_rows.append({"plugin": key, "field": field, "to": translated,
                                 "reason": "上游原文已变，待复核"})
            continue

        if old_translated == translated and str(pair.get("Source") or "") == "user":
            continue   # 已经就是这一条

        new_pair = dict(pair)
        new_pair["Original"] = old_original or original
        new_pair["Translated"] = translated
        new_pair["Source"] = "user"
        if record.get("Stale") or source_changed:
            new_pair["Review"] = "上游原文改过，这条译文由玩家重新提交"
        else:
            new_pair.pop("Review", None)

        entry[field] = new_pair
        applied += 1
        history.append(
            {
                "time": time.strftime("%Y-%m-%d %H:%M:%S"),
                "submitted": (record.get("TimeLocal") or "")[:19],
                "by": source_kind,
                "plugin": key,
                "field": field,
                "from": old_translated,
                "to": translated,
                "source": record.get("Id") or "",
            }
        )

    print(f"应用 {applied} 条；跳过 {len(skipped)} 条")
    for line in skipped[:20]:
        print(f"  [跳过] {line}")

    official = sum(1 for record in records if record.get("Official"))
    if official:
        print(f"其中官方库条目 {official} 条（这类译文只能靠玩家/人维护）")

    if args.dry_run:
        label = "维护者修正" if source_kind == "maintainer" else "玩家投稿"
        print("--dry-run：没有写任何文件。")
        print(f"（这批会被记为「{label}」 {len(records)} 条；实际会应用 {applied} 条、跳过 {len(skipped)} 条）")
        return 0

    if applied:
        # 词表头部记上维护日期（收到贡献也是一次维护）
        ordered: dict = {"_meta": {"updatedAt": time.strftime("%Y-%m-%d")}}
        for key, value in table.items():
            if key.startswith("_"):
                continue
            ordered[key] = value
        write_json_atomic(args.table, ordered)

        with open(args.history, "a", encoding="utf-8", newline="\n") as handle:
            for row in history:
                handle.write(json.dumps(row, ensure_ascii=False) + "\n")

    # 存档与公示**无条件**落盘（审查 P1-3）：以前只有「真有收录」时才落，
    # 于是「收到了但没并入」这种最需要凭证的情况恰好什么都没留下。
    #
    # 若投稿来自 issue 收件箱（docs/contributions/inbox/），就直接往那份文件里补 `import` 侧记，
    # 不再另存一份 —— 一次投稿一份文件，不会出现「inbox 有、archive 也有一份」的重复。
    source_file = os.path.abspath(args.contributions)
    in_inbox = os.path.basename(os.path.dirname(source_file)) == "inbox"
    raw_path = source_file if in_inbox else archive_payload(payload, args.contrib_dir, maintainer=(source_kind == "maintainer"))
    write_json_atomic(
        raw_path,
        {
            "import": {
                "at": time.strftime("%Y-%m-%d %H:%M:%S"),
                "by": source_kind,
                "applied": applied,
                "force": bool(args.force),
                "skipped": skipped_rows,
            },
            **payload,
        },
    )
    players, maintainers, tests, pending = write_public_log(args.history, args.contrib_log)
    label = "维护者修正" if source_kind == "maintainer" else "玩家投稿"

    print(f"词表：{'已写回 ' + args.table if applied else '没有变化（跳过 ' + str(len(skipped)) + ' 条）'}")
    print(f"原始投稿存档：{raw_path}")
    print(f"公开留档：{args.contrib_log}（{label}；玩家 {players} · 维护者 {maintainers} · 测试 {tests} · 待复核 {pending}）")

    if args.push:
        push_to_github(
            [args.table, args.history, args.contrib_dir, args.contrib_log],
            f"收录译文（{applied} 条，{label}）：{time.strftime('%Y-%m-%d %H:%M')}",
        )

    return 0


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(errors="replace")
    except Exception:
        pass
    sys.exit(main())
