#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""inbox_uit.py — 收 GitHub issue 里的「插件界面文字」译文投稿（由 .github/workflows/inbox.yml 调用）。

流程与简介词表同源：编辑器 →「提交人工译文到公共库…」→ 打开填好的新建 issue 页 → 玩家按 Submit
→ 本脚本把投稿并进 `uit-packs/<内部名>.json`（Source=user）+ 留档 + 回话。

payload（只有 type==uit-contribution 才处理）：
{
  "type": "uit-contribution", "plugin": "AutoHook",
  "entries":   [{"Original": "...", "Translated": "...", "Context": "...", "Source": "user"}],
  "resources": [{"Container": "...resources", "Key": "...", "Original": "...", "Translated": "..."}]
}

容器名支持三类（2026-10-03 与服务端对齐）：
  · `<程序集名>.resources`（DLL 内嵌资源）
  · `file:<相对路径>`（插件目录里的本地化文件，如 file:Localization/zh-CN.json）
  · `json:<资源名>.json`（DLL 内嵌 JSON 语言表，如 json:HaselTweaks.Translations.json）

口径：
  · 只改译文与 Source，不删条目；**人工译（Source=user）才允许覆盖**，机器译只补空槽——
    否则一份来自旧模型的机器译会把人写的译文永久顶掉（2026-10-03 评审 C-01）；
  · 硬问题（空译文 / 超长 / 控制字符 / 容器名不合法）直接跳过并在回话里点名；
  · 库里没有该插件的包时**新建一个空包**再收（2026-10-03 评审 C-04：客户端只会在中继返回成功时
    报「已上传」，若服务端直接退回就会变成谎报）；未收录插件的包会随下一次库更新进索引。
"""

from __future__ import annotations

import argparse
import glob
import hashlib
import json
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import uit_index  # noqa: E402
import validate_contribution as rules  # noqa: E402

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(SCRIPT_DIR)
PACKS_DIR = os.path.join(REPO_ROOT, "uit-packs")
INBOX_DIR = os.path.join(REPO_ROOT, "docs", "contributions", "inbox")
MAX_ENTRIES = 3000
MAX_TRANSLATED = 2000
MAX_ORIGINAL = 4000
CONTAINER_RESOURCES = re.compile(r"^[A-Za-z0-9_.\-]+\.resources$")


def valid_container(container: str) -> bool:
    """容器名合法性（与插件端三类容器对齐：程序集资源 / file: / json:）。"""
    if CONTAINER_RESOURCES.match(container):
        return True
    if container.startswith("file:"):
        rel = container[5:]
        if not rel or len(rel) > 200 or rel.startswith("/") or "\\" in rel or ".." in rel:
            return False
        return all(ch.isalnum() or ch in "._-/ " for ch in rel)
    if container.startswith("json:"):
        name = container[5:]
        return bool(name) and len(name) <= 200 and name.lower().endswith(".json") and all(
            ch.isalnum() or ch in "._-" for ch in name
        )
    return False


def is_user_source(item: dict) -> bool:
    """投稿条目是不是人工译（客户端会把每条自己的 Source 带上来）。"""
    return str(item.get("Source") or "").strip().lower().startswith("user")


def submission_fingerprint(plugin: str, payload: dict) -> str:
    """同一份投稿内容的指纹（插件 + 条目/资源/属性的原文→译文对，排序后哈希）。

    用途：中继超时但 issue 已建、玩家又交一次（或同一条 issue 被 edit 重新触发）
    → 第二次识别为重复，不再重复并入/重复通知（2026-10-03 评审 C-10）。"""
    parts = ["p\x01" + plugin]
    for item in payload.get("entries") or []:
        parts.append("e\x01" + str(item.get("Original") or "") + "\x02" + str(item.get("Translated") or ""))
    for item in payload.get("resources") or []:
        parts.append(
            "r\x01" + str(item.get("Container") or "") + "\x02" + str(item.get("Key") or "")
            + "\x02" + str(item.get("Translated") or "")
        )
    for item in payload.get("attributes") or []:
        parts.append("a\x01" + str(item.get("Original") or "") + "\x02" + str(item.get("Translated") or ""))
    return hashlib.sha256("\x00".join(sorted(parts)).encode("utf-8")).hexdigest()[:16]


def sanitize_pack_name(raw: str) -> str:
    """投稿包的显示名（2026-10-04 用户定：提交时可自取昵称/包名，留空 = 匿名）。

    清洗：去控制字符、去会干扰 ImGui 标签的 `###`、压空白、限 24 字；洗不出来就是空（匿名）。
    """
    text = re.sub(r"[\x00-\x1f\x7f]+", " ", raw or "")
    text = text.replace("###", "")
    text = re.sub(r"\s+", " ", text).strip()
    return text[:24]


KINDS = ("free", "llm", "human")
KIND_LABELS = {"free": "免费翻译", "llm": "大模型翻译", "human": "人工翻译"}


def sanitize_kinds(raw) -> list:
    """投稿声明的翻译类型（2026-10-04 用户定）：free / llm / human，别的丢掉、去重、固定顺序。"""
    if not isinstance(raw, list):
        return []
    values = {str(item).strip().lower() for item in raw}
    return [kind for kind in KINDS if kind in values]


def describe_kinds(kinds: list) -> str:
    return "、".join(KIND_LABELS.get(kind, kind) for kind in kinds)


def _one_line(text: str, limit: int = 120) -> str:
    flat = " ".join(str(text or "").split())
    return flat[:limit] + ("…" if len(flat) > limit else "")


def build_review(plugin: str, pack_name: str, pack_file: str, pack: dict, repo: str, when: str, kinds: list | None = None) -> dict:
    """机器人整理给维护者抽查的 issue 内容（标题 + 正文）。

    2026-10-04 用户要求：投稿除了入库，还要在 issue 里留一份可检查的样本——
    issue 正文有长度上限（65536），所以只列**一部分**原文→译文（均匀抽样，带包名）。
    """

    def spread(items: list, render) -> list[str]:
        if not items:
            return []
        count = min(len(items), 120)
        step = max(1, len(items) // count)
        picked = [items[i] for i in range(0, len(items), step)][:count]
        return [render(item) for item in picked]

    rows: list[str] = []
    rows += spread(pack.get("entries") or [], lambda e: f"{_one_line(e.get('Original'))} → {_one_line(e.get('Translated'))}")
    rows += spread(pack.get("resources") or [], lambda r: f"[资源] {_one_line(r.get('Key'))} → {_one_line(r.get('Translated'))}")
    rows += spread(pack.get("attributes") or [], lambda a: f"[属性] {_one_line(a.get('Original'))} → {_one_line(a.get('Translated'))}")

    # 长度预算：issue 正文上限 65536，给头部/说明留足余量
    kept: list[str] = []
    used = 0
    for row in rows:
        if used + len(row) + 1 > 40000:
            break
        kept.append(row)
        used += len(row) + 1

    entries = pack.get("entries") or []
    resources = pack.get("resources") or []
    attributes = pack.get("attributes") or []
    total = len(entries) + len(resources) + len(attributes)
    title = f"[收稿] {plugin} · {pack_name or '匿名'} · {total} 条"
    link = f"https://github.com/{repo}/blob/main/uit-packs/{pack_file}"
    body = "\n".join([
        "### FireGaze 收稿（机器人整理，供抽查）",
        "",
        f"- 插件：`{plugin}`",
        f"- 包名：{pack_name or '匿名'}",
        f"- 翻译类型：{describe_kinds(kinds or []) or '未标注'}",
        f"- 收录：{total} 条（条目 {len(entries)} · 资源 {len(resources)} · 属性 {len(attributes)}）",
        f"- 译文包：[{pack_file}]({link})",
        f"- 投稿时间：{when}",
        "",
        "**已经自动并入公共译文库**，玩家「一键汉化」时能直接用到；下面只是均匀抽出的样本，完整内容见上面的包文件。",
        "",
        f"样本 {len(kept)} 行 / 共 {total} 条：",
        "",
        "```",
        *(kept or ["（没有可展示的条目）"]),
        "```",
        "",
    ])
    return {"title": title, "body": body}


def find_previous_submission(out_dir: str, fingerprint: str) -> str | None:
    """这份内容是不是已经收过（扫最近的存档，最多 400 个；兼容没 fingerprint 字段的旧存档）。"""
    try:
        files = sorted(glob.glob(os.path.join(out_dir, "uit-*.json")), reverse=True)[:400]
    except Exception:  # noqa: BLE001
        return None
    for path in files:
        data = load_json(path)
        if not isinstance(data, dict):
            continue
        found = data.get("fingerprint")
        if not found:
            payload = data.get("payload")
            if isinstance(payload, dict):
                found = submission_fingerprint(str(payload.get("plugin") or ""), payload)
        if found == fingerprint:
            issue = data.get("issue")
            return f"issue #{issue}" if issue else os.path.basename(path)
    return None


def load_json(path: str):
    try:
        with open(path, encoding="utf-8") as handle:
            return json.load(handle)
    except Exception:  # noqa: BLE001
        return None


def save_json(path: str, data) -> None:
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(data, handle, ensure_ascii=False, indent=2)
        handle.write("\n")


def check_text(original: str, translated: str) -> str | None:
    """返回拒绝原因；None = 通过。"""
    if not translated.strip():
        return "译文为空"
    if len(translated) > MAX_TRANSLATED:
        return f"译文超长（{len(translated)} > {MAX_TRANSLATED}）"
    if len(original) > MAX_ORIGINAL:
        return f"原文超长（{len(original)} > {MAX_ORIGINAL}）"
    if rules.CONTROL.search(translated) or rules.CONTROL.search(original):
        return "有控制字符"
    if any(word.lower() in translated.lower() for word in rules.DENY_WORDS):
        return "含违规词"
    return None


def main(argv=None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--issue", required=True)
    parser.add_argument("--body", required=True, help="issue 正文文件")
    parser.add_argument("--author", default="")
    parser.add_argument("--packs-dir", default=PACKS_DIR)
    parser.add_argument("--out-dir", default=INBOX_DIR)
    parser.add_argument("--comment-out", default="", help="回在 issue 上的话写到这里")
    parser.add_argument("--summary-out", default="", help="一行摘要（手机通知用）")
    parser.add_argument("--review-out", default="", help="抽查 issue 内容（{title, body}）写到这里；直传通道用")
    args = parser.parse_args(argv)

    body = open(args.body, encoding="utf-8", errors="replace").read()
    payload = rules.extract_json(body)
    if not payload or payload.get("type") != "uit-contribution":
        print("这不是「插件界面文字」的投稿（没有 uit-contribution 标记），跳过。")
        return 0

    plugin = str(payload.get("plugin") or "").strip()
    # 内部名 = 插件目录名（字母数字、下划线、点、减号）：现在缺包会新建包、要拼路径写盘，
    # 必须挡住 ../ 与子目录（2026-10-03 复审 P2）
    if not re.fullmatch(r"[A-Za-z0-9_.\-]{1,120}", plugin):
        return fail(args, "插件内部名不合法。")

    # 幂等：同一份内容已经收过就不再重复并入 / 重复通知（中继超时重交、issue edit 重跑都会撞上）
    fingerprint = submission_fingerprint(plugin, payload)
    previous = find_previous_submission(args.out_dir, fingerprint)
    if previous:
        return fail(args, f"同一份投稿内容之前已经收到过了（{previous}），这次跳过（没有重复并入）。")

    # 2026-10-04 多包模型（用户定）：每次收录的投稿**新建一个独立包**
    # `<插件>@<包ID>.json`（包ID = user-<内容指纹前6位>，匿名且稳定）；
    # 基础包 `<插件>.json` 是维护者自己的译文，收稿不再碰它。
    pack_id = "user-" + fingerprint[:6]
    pack_file = f"{plugin}@{pack_id}.json"
    pack_path = os.path.join(args.packs_dir, pack_file)
    if os.path.exists(pack_path):
        return fail(args, f"同一份投稿已经收过（{pack_file} 已存在），这次跳过。")

    os.makedirs(args.packs_dir, exist_ok=True)
    pack_name = sanitize_pack_name(str(payload.get("packName") or ""))
    kinds = sanitize_kinds(payload.get("kinds"))
    pack = {
        "_meta": {
            "format": 2,
            "updatedAt": time.strftime("%Y-%m-%d"),
            "source": "user",
            "label": pack_name or f"玩家包 · {pack_id[5:]}",
            **({"kinds": kinds} if kinds else {}),
        },
        "entries": [],
        "resources": [],
        "attributes": [],
    }

    entries_by_original: dict = {}
    resources_by_key: dict = {}

    accepted = 0
    filled = 0
    overwritten = 0
    kept = 0
    rejected: list[str] = []
    for item in (payload.get("entries") or [])[:MAX_ENTRIES]:
        if not isinstance(item, dict):
            continue
        original = str(item.get("Original") or "")
        translated = str(item.get("Translated") or "")
        reason = check_text(original, translated)
        if reason:
            rejected.append(f"`{original[:40]}`：{reason}")
            continue
        user_source = is_user_source(item)
        target = entries_by_original.get(original)
        if target is None:
            target = {"Original": original, "Translated": translated, "Context": str(item.get("Context") or "")}
            target["Source"] = "user" if user_source else "library"
            pack["entries"].append(target)
            entries_by_original[original] = target
            accepted += 1
            filled += 1
            continue

        existing = str(target.get("Translated") or "").strip()
        if user_source:
            target["Translated"] = translated
            target["Source"] = "user"
            accepted += 1
            overwritten += 1
        elif not existing:
            target["Translated"] = translated
            target.setdefault("Source", "library")
            accepted += 1
            filled += 1
        else:
            # 机器译不覆盖已有内容（保护人工译不被旧模型永久顶掉）
            kept += 1

    for item in (payload.get("resources") or [])[:MAX_ENTRIES]:
        if not isinstance(item, dict):
            continue
        container = str(item.get("Container") or "")
        key = str(item.get("Key") or "")
        original = str(item.get("Original") or "")
        translated = str(item.get("Translated") or "")
        if not valid_container(container) or not key or len(key) > 200:
            rejected.append(f"`{container} / {key[:40]}`：容器名或 key 不合法")
            continue
        reason = check_text(original, translated)
        if reason:
            rejected.append(f"`{key[:40]}`：{reason}")
            continue
        user_source = is_user_source(item)
        target = resources_by_key.get((container, key))
        if target is None:
            target = {"Container": container, "Key": key, "Original": original, "Translated": translated}
            target["Source"] = "user" if user_source else "library"
            pack["resources"].append(target)
            resources_by_key[(container, key)] = target
            accepted += 1
            filled += 1
            continue

        existing = str(target.get("Translated") or "").strip()
        if user_source:
            target["Translated"] = translated
            target["Source"] = "user"
            accepted += 1
            overwritten += 1
        elif not existing:
            target["Translated"] = translated
            target.setdefault("Source", "library")
            accepted += 1
            filled += 1
        else:
            kept += 1

    attributes_by_original = {a.get("Original", ""): a for a in pack.get("attributes") or []}
    for item in (payload.get("attributes") or [])[:MAX_ENTRIES]:
        if not isinstance(item, dict):
            continue
        original = str(item.get("Original") or "")
        translated = str(item.get("Translated") or "")
        reason = check_text(original, translated)
        if reason:
            rejected.append(f"`{original[:40]}`：{reason}")
            continue
        user_source = is_user_source(item)
        target = attributes_by_original.get(original)
        if target is None:
            target = {"Original": original, "Translated": translated, "Context": "[投稿]"}
            target["Source"] = "user" if user_source else "library"
            pack["attributes"].append(target)
            attributes_by_original[original] = target
            accepted += 1
            filled += 1
            continue

        existing = str(target.get("Translated") or "").strip()
        if user_source:
            target["Translated"] = translated
            target["Source"] = "user"
            accepted += 1
            overwritten += 1
        elif not existing:
            target["Translated"] = translated
            target.setdefault("Source", "library")
            accepted += 1
            filled += 1
        else:
            kept += 1

    # 无论收录多少都留档：被退回的投稿也要能查（2026-10-03 评审 C-04）
    os.makedirs(args.out_dir, exist_ok=True)
    archive = os.path.join(args.out_dir, f"uit-{args.issue}-{time.strftime('%Y-%m-%dT%H%M%S')}.json")
    save_json(archive, {
        "issue": args.issue,
        "receivedAt": time.strftime("%Y-%m-%d %H:%M:%S"),
        "author": args.author,
        "plugin": plugin,
        "fingerprint": fingerprint,
        "packFile": pack_file,
        "accepted": accepted,
        "filled": filled,
        "overwritten": overwritten,
        "kept": kept,
        "rejected": rejected[:50],
        "payload": payload,
    })

    if accepted == 0:
        if kept:
            return fail(args, f"这条投稿里没有可收录的新译文：{kept} 条已有内容，而且机器译不会覆盖已有译文（人工改过的译文才会；译文都还在插件本地）。")
        return fail(args, "这条投稿里没有可收录的译文。")

    save_json(pack_path, pack)
    refresh_index(args.packs_dir)

    detail = (
        f"收录 {accepted} 条（{plugin}）→ 独立译文包 {pack_file}"
        + (f"「{pack_name}」" if pack_name else "")
        + f"（条目 {len(pack['entries'])} · 资源 {len(pack['resources'])} · 属性 {len(pack['attributes'])}）"
    )
    print(detail + f"，留档 {os.path.relpath(archive, REPO_ROOT)}")

    if args.review_out:
        # 抽查 issue（2026-10-04）：入库之外再给维护者一份可检查的样本（含包名）
        repo = os.environ.get("GITHUB_REPOSITORY") or "DCritHitFireIV/FireGaze"
        review = build_review(plugin, pack_name, pack_file, pack, repo, time.strftime("%Y-%m-%d %H:%M"), kinds)
        save_json(args.review_out, review)
        print(f"抽查 issue 内容已写出：{args.review_out}（{review['title']}）")
    if args.summary_out:
        with open(args.summary_out, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(
                f"issue #{args.issue}：{accepted} 条 {plugin} 界面文字译文"
                + (f"（玩家包「{pack_name}」）" if pack_name else "（已收为独立包）")
            )
    if args.comment_out:
        lines = [
            f"收到 {accepted} 条 `{plugin}` 的界面文字译文，已收为**独立译文包** `uit-packs/{pack_file}`"
            + (f"，包名「{pack_name}」。" if pack_name else "。"),
            "",
            "大家「一键汉化」时会自动合入（也可以在插件详情里单独下载、点赞）；下载数与 👍 按包分开统计。",
            "",
        ]
        if filled or overwritten:
            lines.append(f"其中配套资源/属性 {filled + overwritten} 条。")
            lines.append("")
        if kept:
            lines.append(f"另有 {kept} 条机器译因为库里已有内容而没有收录（人工改过的译文才会覆盖已有值）。")
            lines.append("")
        if rejected:
            lines.append(f"其中 {len(rejected)} 条没能收录：")
            lines.append("")
            for row in rejected[:10]:
                lines.append(f"- {row}")
            lines.append("")
        lines.append("维护者会抽查；这一条投稿与仓库里的存档都是长期凭证。")
        with open(args.comment_out, "w", encoding="utf-8", newline="\n") as handle:
            handle.write("\n".join(lines) + "\n")
    return 0


def fail(args, message: str) -> int:
    print(message)
    if args.comment_out:
        with open(args.comment_out, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(message + "\n")
    return 0


def refresh_index(packs_dir: str) -> None:
    """按 uit-packs/ 下所有包重算 index.json（基础包 + 各玩家包；保留下载量与👍）。"""
    index_path = os.path.join(packs_dir, "index.json")
    index = load_json(index_path) or {"plugins": {}}
    index["plugins"] = uit_index.collect(packs_dir, index.get("plugins") or {})
    index["updatedAt"] = time.strftime("%Y-%m-%d")
    save_json(index_path, index)


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(errors="replace")
    except Exception:  # noqa: BLE001
        pass
    sys.exit(main())
