#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""反馈 issue 的二次复核（GitHub Actions 用）。

Worker 已在建 issue 前做过一轮规则过滤；这里在 issue 创建后再核一遍：
- 垃圾内容 → 关闭 + 打 `spam` 标 + 说明原因；
- 正常反馈 → 打 `feedback` 标 + 回一句 + （可选）Server酱通知维护者。
只用仓库自带的 GITHUB_TOKEN（Actions 机密），不使用任何个人 token。
"""
import json
import os
import re
import subprocess
import sys
import urllib.request

BANNED = [
    r"viagra", r"casino", r"porn", r"escort", r"博彩", r"赌场", r"代刷", r"加微信",
    r"贷款", r"开票", r"usdt", r"telegram", r"whatsapp", r"兼职刷单", r"色情", r"成人服务",
]
MARKER = "### FireGaze 反馈"


def run(cmd: list[str], *, check: bool = True) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", check=check)


def spam_reason(text: str) -> str | None:
    if len(text) < 10:
        return "内容过短"
    if len(text) > 30000:
        return "内容过长"
    links = len(re.findall(r"https?://", text))
    if links > 8:
        return "链接过多"
    lowered = text.lower()
    for pattern in BANNED:
        if re.search(pattern, lowered):
            return f"命中垃圾词（{pattern}）"
    letters = len(re.findall(r"[^\W\d_]", text, re.UNICODE))
    if letters < 10 and links >= 1:
        return "几乎只有链接"
    lines = [line.strip() for line in text.splitlines() if line.strip()]
    if len(lines) > 30 and len(set(lines)) / len(lines) < 0.3:
        return "重复刷屏"
    return None


def comment(number: str, body: str) -> None:
    run(["gh", "issue", "comment", number, "--body", body])


def label(number: str, name: str, color: str, description: str) -> None:
    run(["gh", "label", "create", name, "--color", color, "--description", description, "--force"], check=False)
    run(["gh", "issue", "edit", number, "--add-label", name], check=False)


def notify(message: str) -> None:
    url = os.environ.get("SERVER3_PUSH_URL", "").strip()
    if not url:
        return
    try:
        request = urllib.request.Request(
            url,
            data=json.dumps({"title": "FireGaze 反馈", "desp": message}).encode("utf-8"),
            headers={"Content-Type": "application/json"},
        )
        urllib.request.urlopen(request, timeout=10)
    except Exception as exc:  # noqa: BLE001 - 通知失败不影响主流程
        print(f"notify skipped: {exc}")


def main() -> int:
    body = os.environ.get("ISSUE_BODY", "")
    number = os.environ.get("ISSUE_NUMBER", "")
    title = os.environ.get("ISSUE_TITLE", "")
    if not number or MARKER not in body:
        print("not a feedback issue, skip")
        return 0

    content = body.split(MARKER, 1)[1]
    reason = spam_reason(content)
    if reason:
        print(f"spam detected: {reason}")
        label(number, "spam", "b60205", "自动判定为垃圾内容")
        run(["gh", "issue", "close", number, "--reason", "not planned"], check=False)
        comment(number, f"这条反馈被自动复核拦下（原因：{reason}）。如果这是误判，请直接回复本条 issue。")
        return 0

    label(number, "feedback", "0e8a16", "用户反馈")
    comment(number, "收到，感谢反馈！诊断日志已附在正文里，维护者会尽快查看。")
    notify(f"{title}\n{content[:500]}")
    print("feedback accepted")
    return 0


if __name__ == "__main__":
    sys.exit(main())
