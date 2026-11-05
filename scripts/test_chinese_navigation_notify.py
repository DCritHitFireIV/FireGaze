import copy
import json
import unittest

from chinese_navigation_notify import GitHubOutbox, process_issue, server3_outcome


KEY = "a" * 64
PENDING = "chinese-navigation-notify-pending"
STATE_MARKER = "<!-- firegaze:chinese-navigation-notification:v1 -->"


def issue():
    return {"number": 42, "user": {"login": "navigation-app[bot]"},
            "body": f"<!-- firegaze:chinese-navigation:v1:{KEY} -->\nUntrusted user content",
            "labels": [{"name": "chinese-navigation"}, {"name": PENDING}],
            "html_url": "https://github.com/mock/repo/issues/42"}


def state_comment(state, login="github-actions[bot]", comment_id=10):
    return {"id": comment_id, "user": {"login": login},
            "body": STATE_MARKER + "\n```json\n" + json.dumps(state) + "\n```"}


def state(status="pending", attempts=1, retry_at=100):
    return {"schema": 1, "issueNumber": 42, "submissionId": KEY,
            "status": status, "attempts": attempts, "retryAt": retry_at,
            "lastError": ""}


class Boundary:
    """Only GitHub persistence and outbound HTTP are replaced."""
    repo = "mock/repo"
    state_bot = "github-actions[bot]"

    def __init__(self):
        self.comments = []
        self.operations = []
        self.sent = []
        self.outcome = ("sent", "")
        self.fail_save = False

    def list_comments(self, number):
        return copy.deepcopy(self.comments)

    def save_state(self, number, value, comment_id):
        if self.fail_save:
            raise RuntimeError("persistence failed")
        self.operations.append(("save", value["status"]))
        self.comments = [state_comment(copy.deepcopy(value))]
        return 10

    def remove_pending(self, number):
        self.operations.append(("remove-pending", number))

    def mark_attention(self, number):
        self.operations.append(("attention", number))

    def notify(self, url, title, description):
        self.operations.append(("notify", 42))
        self.sent.append((title, description))
        return self.outcome


class OutboxTests(unittest.TestCase):
    def run_issue(self, api, current=None, **overrides):
        arguments = {"trusted_bot": "navigation-app[bot]", "push_url": "https://secret.invalid/key", "now": 1000}
        arguments.update(overrides)
        return process_issue(api, current or issue(), **arguments)

    def test_success_persists_sent_before_removing_pending_and_never_forwards_user_text(self):
        api = Boundary()
        result = self.run_issue(api)
        self.assertEqual("sent", result["status"])
        self.assertEqual([("save", "pending"), ("notify", 42), ("save", "sent"), ("remove-pending", 42)], api.operations)
        self.assertEqual("汉化导航投稿 #42\nhttps://github.com/mock/repo/issues/42", api.sent[0][1])
        self.assertNotIn("Untrusted", json.dumps(api.sent))

    def test_failed_notification_remains_retryable_without_recreating_issue(self):
        api = Boundary()
        api.outcome = ("retry", "server3-business-error")
        result = self.run_issue(api)
        self.assertEqual("pending", result["status"])
        self.assertEqual(1, result["attempts"])
        self.assertEqual(1300, result["retryAt"])
        self.assertFalse(any(op[0] == "remove-pending" for op in api.operations))
        self.assertNotIn("secret.invalid", json.dumps(api.comments))

    def test_sent_receipt_clears_lingering_label_without_resending(self):
        api = Boundary()
        api.comments = [state_comment(state("sent", retry_at=0))]
        self.assertEqual("sent", self.run_issue(api)["status"])
        self.assertEqual([], api.sent)
        self.assertEqual([("remove-pending", 42)], api.operations)

    def test_untrusted_or_missing_author_cannot_trigger_notification(self):
        for login in ["", "other-app[bot]"]:
            api = Boundary()
            result = self.run_issue(api, trusted_bot=login)
            self.assertEqual("needs-attention" if not login else "ignored", result["status"])
            self.assertEqual([], api.sent)

    def test_missing_secret_marks_attention_but_preserves_pending(self):
        api = Boundary()
        result = self.run_issue(api, push_url="")
        self.assertEqual("needs-attention", result["status"])
        self.assertEqual([], api.sent)
        self.assertIn(("attention", 42), api.operations)
        self.assertFalse(any(op[0] == "remove-pending" for op in api.operations))

    def test_backoff_blocks_early_retries_and_ignores_forged_state_comments(self):
        api = Boundary()
        api.comments = [state_comment(state(retry_at=2000))]
        self.assertEqual("waiting", self.run_issue(api)["status"])
        self.assertEqual([], api.sent)
        api.comments = [state_comment(state("sent"), login="attacker")]
        self.assertEqual("sent", self.run_issue(api)["status"])
        self.assertEqual(1, len(api.sent))

    def test_persistence_failure_prevents_sending(self):
        api = Boundary()
        api.fail_save = True
        with self.assertRaises(RuntimeError):
            self.run_issue(api)
        self.assertEqual([], api.sent)

    def test_server3_requires_http_and_explicit_business_success(self):
        self.assertEqual(("sent", ""), server3_outcome(200, {"code": 0}))
        for status, payload in [(500, {"code": 0}), (200, {}), (200, {"code": 1}), (200, {"code": False})]:
            self.assertEqual("retry", server3_outcome(status, payload)[0])
        self.assertEqual("needs-attention", server3_outcome(403, {"code": 0})[0])

    def test_corrupt_owned_state_does_not_restart_notifications(self):
        api = Boundary()
        api.comments = [state_comment({"status": "sent"})]
        self.assertEqual("needs-attention", self.run_issue(api)["status"])
        self.assertEqual([], api.sent)

    def test_adapter_updates_only_state_comment_and_checks_notification_response(self):
        calls = []

        def transport(method, url, headers, payload):
            calls.append((method, url, headers, payload))
            return (200, {"id": 10}) if url.startswith("https://api.github.com/") else (200, {"code": 4})

        api = GitHubOutbox("mock/repo", "github-secret", transport=transport)
        self.assertEqual(10, api.save_state(42, state(), 10))
        self.assertEqual("PATCH", calls[0][0])
        self.assertEqual("https://api.github.com/repos/mock/repo/issues/comments/10", calls[0][1])
        self.assertIn(STATE_MARKER, calls[0][3]["body"])
        self.assertEqual("retry", api.notify("https://secret.invalid/key", "title", "message")[0])
        self.assertNotIn("Authorization", calls[1][2])


if __name__ == "__main__":
    unittest.main(verbosity=2)
