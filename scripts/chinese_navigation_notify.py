"""Persistent notification outbox; never creates issues or approves submissions.

Run only in the globally serialized navigation Actions workflow. Issue labels are
the durable queue; an Actions-bot-owned comment is the notification receipt.
"""
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
from urllib.parse import quote, urlsplit


NAVIGATION = "chinese-navigation"
PENDING = "chinese-navigation-notify-pending"
ATTENTION = "chinese-navigation-needs-attention"
STATE_MARKER = "<!-- firegaze:chinese-navigation-notification:v1 -->"
SUBMISSION_MARKER = re.compile(r"^<!-- firegaze:chinese-navigation:v1:([0-9a-f]{64}) -->$", re.MULTILINE)
REPO_PATTERN = re.compile(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+")


class OutboxError(Exception):
    """A controlled code only: no upstream response, URL, token or exception text."""


def read_state(comments, *, number, submission_id, state_bot):
    owned = [comment for comment in comments
             if comment.get("user", {}).get("login") == state_bot
             and str(comment.get("body", "")).startswith(STATE_MARKER)]
    if not owned:
        return None, None
    latest = max(owned, key=lambda comment: comment.get("id", 0))
    body = latest["body"]
    prefix = STATE_MARKER + "\n```json\n"
    if not body.startswith(prefix) or not body.endswith("\n```"):
        raise OutboxError("invalid-state-receipt")
    try:
        value = json.loads(body[len(prefix):-4])
        if (value.get("schema") != 1 or value.get("issueNumber") != number
                or value.get("submissionId") != submission_id
                or value.get("status") not in ("pending", "sent", "needs-attention")
                or type(value.get("attempts")) is not int or not 0 <= value["attempts"] <= 1000000
                or type(value.get("retryAt")) is not int or value["retryAt"] < 0):
            raise ValueError()
    except (ValueError, TypeError, AttributeError):
        raise OutboxError("invalid-state-receipt") from None
    return value, latest["id"]


def process_issue(api, issue, *, trusted_bot, push_url, now):
    number = issue.get("number")
    if not trusted_bot:
        return {"status": "needs-attention", "reason": "trusted-author-not-configured"}
    labels = {label.get("name") for label in issue.get("labels", [])}
    marker = SUBMISSION_MARKER.search(str(issue.get("body", "")))
    if (type(number) is not int or number <= 0 or issue.get("pull_request")
            or issue.get("user", {}).get("login") != trusted_bot
            or not {NAVIGATION, PENDING}.issubset(labels) or not marker):
        return {"status": "ignored"}
    submission_id = marker.group(1)
    try:
        current, comment_id = read_state(api.list_comments(number), number=number,
                                        submission_id=submission_id, state_bot=api.state_bot)
    except OutboxError:
        api.mark_attention(number)
        return {"status": "needs-attention", "reason": "invalid-state-receipt"}
    if current and current["status"] == "sent":
        api.remove_pending(number)
        return current
    if current and current["retryAt"] > now:
        return {"status": "waiting", "retryAt": current["retryAt"]}
    receipt = {"schema": 1, "issueNumber": number, "submissionId": submission_id,
               "status": "pending", "attempts": current["attempts"] if current else 0,
               "retryAt": 0, "lastError": ""}
    if not push_url:
        receipt.update(status="needs-attention", retryAt=now + 3600,
                       lastError="server3-not-configured")
        api.save_state(number, receipt, comment_id)
        api.mark_attention(number)
        return receipt
    receipt["attempts"] += 1
    receipt["retryAt"] = now + min(86400, 300 * 2 ** min(receipt["attempts"] - 1, 9))
    # Persist the attempt before external I/O. If this fails, do not send.
    comment_id = api.save_state(number, receipt, comment_id)
    try:
        outcome, error = api.notify(push_url, f"FireGaze 汉化导航 #{number}",
                                    f"汉化导航投稿 #{number}\nhttps://github.com/{api.repo}/issues/{number}")
    except Exception:
        outcome, error = "retry", "notification-transport-error"
    if outcome == "sent":
        receipt.update(status="sent", retryAt=0, lastError="")
        # Sent receipt first: a crash before removing the label must not resend.
        api.save_state(number, receipt, comment_id)
        api.remove_pending(number)
    else:
        receipt["status"] = "needs-attention" if outcome == "needs-attention" else "pending"
        receipt["lastError"] = error if re.fullmatch(r"[a-z0-9-]{1,80}", error or "") else "notification-error"
        if outcome == "needs-attention":
            receipt["retryAt"] = max(receipt["retryAt"], now + 3600)
        api.save_state(number, receipt, comment_id)
        if receipt["status"] == "needs-attention":
            api.mark_attention(number)
    return receipt


def server3_outcome(status, payload):
    if status in (401, 403):
        return "needs-attention", "server3-auth-failed"
    if not 200 <= status < 300:
        return "retry", "server3-http-failed"
    if not isinstance(payload, dict) or type(payload.get("code")) is not int:
        return "retry", "server3-invalid-receipt"
    if payload["code"] != 0:
        return "retry", "server3-business-error"
    return "sent", ""


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, new_url):
        return None


def http_json(method, url, headers, payload):
    request = urllib.request.Request(url, method=method, headers=headers,
                                     data=None if payload is None else json.dumps(payload).encode("utf-8"))
    try:
        with urllib.request.build_opener(NoRedirect()).open(request, timeout=15) as response:
            raw = response.read(16 * 1024 * 1024 + 1)
            if len(raw) > 16 * 1024 * 1024:
                raise OutboxError("response-too-large")
            return response.status, json.loads(raw) if raw else None
    except urllib.error.HTTPError as error:
        # Never read/log arbitrary upstream error bodies or redirect credentials.
        error.close()
        return error.code, None
    except (urllib.error.URLError, TimeoutError, ValueError, OSError):
        raise OutboxError("http-transport-failed") from None


class GitHubOutbox:
    state_bot = "github-actions[bot]"

    def __init__(self, repo, token, transport=None):
        if not REPO_PATTERN.fullmatch(repo or "") or not token:
            raise OutboxError("github-not-configured")
        self.repo = repo
        self.transport = transport or http_json
        self.headers = {"Authorization": f"Bearer {token}", "Accept": "application/vnd.github+json",
                        "Content-Type": "application/json", "User-Agent": "firegaze-navigation-outbox",
                        "X-GitHub-Api-Version": "2022-11-28"}

    def request(self, method, path, payload=None, *, allow_missing=False):
        status, response = self.transport(method, "https://api.github.com/repos/" + self.repo + path,
                                          self.headers, payload)
        if allow_missing and status == 404:
            return None
        if not 200 <= status < 300:
            raise OutboxError(f"github-http-{status}")
        return response

    def pages(self, path):
        values = []
        separator = "&" if "?" in path else "?"
        for page in range(1, 101):
            batch = self.request("GET", f"{path}{separator}per_page=100&page={page}")
            if not isinstance(batch, list):
                raise OutboxError("github-invalid-page")
            values.extend(batch)
            if len(batch) < 100:
                return values
        raise OutboxError("github-pagination-limit")

    def pending_issues(self):
        return self.pages("/issues?state=all&sort=created&direction=asc&labels=" + quote(PENDING))

    def get_issue(self, number):
        return self.request("GET", f"/issues/{int(number)}")

    def list_comments(self, number):
        return self.pages(f"/issues/{number}/comments")

    def save_state(self, number, value, comment_id):
        body = STATE_MARKER + "\n```json\n" + json.dumps(value, ensure_ascii=True, sort_keys=True) + "\n```"
        path = f"/issues/comments/{int(comment_id)}" if comment_id else f"/issues/{int(number)}/comments"
        response = self.request("PATCH" if comment_id else "POST", path, {"body": body})
        if not isinstance(response, dict) or type(response.get("id")) is not int:
            raise OutboxError("github-invalid-comment-receipt")
        return response["id"]

    def remove_pending(self, number):
        self.request("DELETE", f"/issues/{number}/labels/{PENDING}", allow_missing=True)

    def mark_attention(self, number):
        self.request("POST", f"/issues/{number}/labels", {"labels": [ATTENTION]})

    def notify(self, url, title, description):
        parsed = urlsplit(url)
        if parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password:
            return "needs-attention", "server3-invalid-configuration"
        status, response = self.transport("POST", url, {"Content-Type": "application/json"},
                                          {"title": title, "desp": description})
        return server3_outcome(status, response)


def main():
    trusted_bot = os.environ.get("NAVIGATION_BOT_LOGIN", "").strip()
    if not trusted_bot:
        print(json.dumps({"status": "needs-attention", "reason": "trusted-author-not-configured"}))
        return 2
    try:
        api = GitHubOutbox(os.environ.get("GITHUB_REPOSITORY", ""), os.environ.get("GH_TOKEN", ""))
        requested = os.environ.get("ISSUE_NUMBER", "").strip()
        if requested and not re.fullmatch(r"[1-9][0-9]{0,12}", requested):
            raise OutboxError("invalid-issue-number")
        issues = [api.get_issue(int(requested))] if requested else api.pending_issues()
    except OutboxError as error:
        print(json.dumps({"status": "needs-attention", "reason": str(error)}))
        return 2
    failed = False
    for issue in issues:
        try:
            result = process_issue(api, issue, trusted_bot=trusted_bot,
                                   push_url=os.environ.get("SERVER3_PUSH_URL", "").strip(), now=int(time.time()))
            print(json.dumps({"issue": issue.get("number"), "status": result["status"],
                              "reason": result.get("lastError", result.get("reason", ""))}))
            failed |= result["status"] == "needs-attention"
        except OutboxError as error:
            failed = True
            print(json.dumps({"issue": issue.get("number"), "status": "needs-attention", "reason": str(error)}))
        except Exception:
            failed = True
            print(json.dumps({"issue": issue.get("number"), "status": "needs-attention", "reason": "unexpected-outbox-error"}))
    return 2 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
