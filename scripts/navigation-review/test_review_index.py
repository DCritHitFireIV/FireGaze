import copy
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from review_index import ReviewError, build_index

SHA = 'a' * 64

def entry(name='Example', entry_id='nav-example'):
    return {'entryId': entry_id, 'target': {'internalName': name, 'sourceUrl': 'https://repo.example/index.json',
            'projectUrl': 'https://github.com/example/plugin', 'version': '1.2.3', 'apiLevel': 15, 'client': 'CN'},
            'kind': 'alternative', 'coverage': 'full', 'coverageNote': '所列版本菜单、设置和提示均有中文资源',
            'maintainer': 'example-maintainer', 'maintenance': 'active',
            'evidenceUrl': 'https://github.com/example/plugin-cn/blob/v1.2.3/README.md',
            'description': '独立中文版本', 'instructions': '查看项目说明并自行选择是否换装。',
            'entryUrl': 'https://github.com/example/plugin-cn', 'subscriptionUrl': None,
            'sourceIssueUrl': 'https://github.com/DCritHitFireIV/FireGaze/issues/123',
            'alternativeInternalName': 'ExampleCN'}

def review(value=None):
    return {'decision': 'verified', 'entry': value or entry(), 'reviewer': 'reviewer-login',
            'reviewedOn': '2026-11-05', 'identityConfirmed': True, 'languageConsumptionConfirmed': True,
            'coverageConfirmed': True, 'publicDataChecked': True, 'compatibilityConfirmed': True}

def batch(reviews=None):
    return {'schema': 1, 'expectedBaseSha256': SHA, 'expectedBaseRevision': 'r1',
            'revision': 'r2', 'reviews': reviews if reviews is not None else [review()]}

def base():
    old = entry('Unrelated', 'nav-keep')
    old.update(verification='verified', verifiedOn='2026-11-04')
    return {'schema': 1, 'revision': 'r1', 'entries': [old]}

class ReviewTests(unittest.TestCase):
    def test_verified_candidate_preserves_unrelated_entries_and_emits_exact_client_schema(self):
        original = base()
        result = build_index(original, batch(), SHA)
        self.assertEqual('r2', result['revision'])
        self.assertEqual(2, len(result['entries']))
        self.assertEqual(original['entries'][0], next(x for x in result['entries'] if x['entryId'] == 'nav-keep'))
        added = next(x for x in result['entries'] if x['entryId'] == 'nav-example')
        self.assertEqual('verified', added['verification'])
        self.assertEqual('2026-11-05', added['verifiedOn'])
        self.assertEqual('ExampleCN', added['alternativeInternalName'])
        self.assertNotIn('reviewer', added)
        self.assertEqual({'schema', 'revision', 'entries'}, set(result))
        self.assertEqual('r1', original['revision'])

    def test_no_automatic_verification_from_submission_or_missing_explicit_attestations(self):
        for field in ['identityConfirmed', 'languageConsumptionConfirmed', 'coverageConfirmed', 'publicDataChecked', 'compatibilityConfirmed']:
            item = review()
            item[field] = False
            with self.subTest(field=field), self.assertRaises(ReviewError):
                build_index(base(), batch([item]), SHA)
        item = review()
        item['decision'] = 'pending'
        with self.assertRaises(ReviewError):
            build_index(base(), batch([item]), SHA)

    def test_missing_version_source_evidence_or_cn_identity_is_not_publishable(self):
        for field, value in [('version', ''), ('sourceUrl', ''), ('client', 'INTL'), ('apiLevel', 0)]:
            item = review()
            item['entry']['target'][field] = value
            with self.subTest(field=field), self.assertRaises(ReviewError):
                build_index(base(), batch([item]), SHA)
        item = review(); item['entry']['evidenceUrl'] = ''
        with self.assertRaises(ReviewError):
            build_index(base(), batch([item]), SHA)

    def test_partial_is_preserved_not_promoted_and_unknown_cannot_be_verified(self):
        item = review(); item['entry']['coverage'] = 'partial'
        result = build_index(base(), batch([item]), SHA)
        added = next((x for x in result['entries'] if x['entryId'] == 'nav-example'), None)
        self.assertIsNotNone(added)
        self.assertEqual('partial', added['coverage'])
        item['entry']['coverage'] = 'unknown'
        with self.assertRaises(ReviewError):
            build_index(base(), batch([item]), SHA)

    def test_stale_base_or_duplicate_changes_fail_without_dropping_existing_entries(self):
        for changed in [dict(batch(), expectedBaseSha256='b'*64), dict(batch(), expectedBaseRevision='older'),
                        dict(batch(), revision='r1'), batch([review(), review()])]:
            with self.assertRaises(ReviewError):
                build_index(base(), changed, SHA)
        changed_base = base(); changed_base['entries'].append(copy.deepcopy(changed_base['entries'][0]))
        with self.assertRaises(ReviewError):
            build_index(changed_base, batch(), SHA)

    def test_known_private_data_and_unknown_fields_are_not_copied_into_public_index(self):
        for field, value in [('instructions', 'See C:\\Users\\Fire\\secret.txt'),
                             ('entryUrl', 'https://github.com/x?token=private'),
                             ('subscriptionUrl', 'http://192.168.0.1/repo'),
                             ('description', 'ghp_' + 'a'*36),
                             ('maintainer', 'person@example.org')]:
            item = review(); item['entry'][field] = value
            with self.subTest(field=field), self.assertRaises(ReviewError):
                build_index(base(), batch([item]), SHA)
        item = review(); item['entry']['rawSubmission'] = {'secret': 'x'}
        with self.assertRaises(ReviewError):
            build_index(base(), batch([item]), SHA)

    def test_withdrawal_keeps_record_and_requires_reviewed_reason_and_existing_id(self):
        withdrawal = {'decision': 'withdrawn', 'entryId': 'nav-keep', 'reviewer': 'reviewer-login',
                      'reviewedOn': '2026-11-05', 'publicDataChecked': True,
                      'reason': '该版本入口已失效', 'evidenceUrl': 'https://github.com/example/plugin/issues/4'}
        result = build_index(base(), batch([withdrawal]), SHA)
        self.assertEqual(1, len(result['entries']))
        self.assertEqual('withdrawn', result['entries'][0]['verification'])
        self.assertEqual('nav-keep', result['entries'][0]['entryId'])
        withdrawal['entryId'] = 'not-existing'
        with self.assertRaises(ReviewError):
            build_index(base(), batch([withdrawal]), SHA)

    def test_cli_outputs_new_candidate_only_and_refuses_overwriting_or_stale_inputs(self):
        with tempfile.TemporaryDirectory() as root:
            root = Path(root)
            baseline = root / 'index.json'; reviews = root / 'reviews.json'; output = root / 'candidate.json'
            baseline.write_text(json.dumps(base()), encoding='utf-8')
            raw = baseline.read_bytes(); incoming = batch()
            incoming['expectedBaseSha256'] = hashlib.sha256(raw).hexdigest()
            reviews.write_text(json.dumps(incoming), encoding='utf-8')
            command = [sys.executable, '-B', str(Path(__file__).with_name('review_index.py')),
                       '--base', str(baseline), '--reviews', str(reviews), '--output', str(output)]
            good = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(0, good.returncode, good.stderr)
            self.assertEqual(raw, baseline.read_bytes())
            self.assertEqual('r2', json.loads(output.read_text(encoding='utf-8'))['revision'])
            saved = output.read_bytes()
            repeated = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(2, repeated.returncode)
            self.assertEqual(saved, output.read_bytes())
            output.unlink(); baseline.write_text('{}', encoding='utf-8')
            stale = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(2, stale.returncode)
            self.assertFalse(output.exists())


    def test_official_source_preserves_identity_and_cannot_replace_third_party_entry(self):
        official = entry(entry_id='nav-official')
        official['target']['sourceUrl'] = 'OFFICIAL'
        third_party = entry(entry_id='nav-third-party')
        result = build_index(base(), batch([review(official), review(third_party)]), SHA)
        added = {item['entryId']: item for item in result['entries']}
        self.assertEqual('OFFICIAL', added['nav-official']['target']['sourceUrl'])
        self.assertEqual('https://repo.example/index.json', added['nav-third-party']['target']['sourceUrl'])
        self.assertEqual('Example', added['nav-official']['target']['internalName'])
        self.assertEqual('Example', added['nav-third-party']['target']['internalName'])
        existing = base()
        replacement = entry('Unrelated', 'nav-keep')
        replacement['target']['sourceUrl'] = 'OFFICIAL'
        with self.assertRaisesRegex(ReviewError, 'entry-id-reused-for-new-identity'):
            build_index(existing, batch([review(replacement)]), SHA)
        unconfirmed = review(official)
        unconfirmed['identityConfirmed'] = False
        with self.assertRaisesRegex(ReviewError, 'explicit-review-required'):
            build_index(base(), batch([unconfirmed]), SHA)

    def test_official_marker_is_source_only_exact_and_never_fills_unknown_identity(self):
        for source in ['official', 'Official', ' OFFICIAL', 'OFFICIAL ', 'OFFICIAL\n', 'OFFICIAL\t', 'LOCAL', '']:
            item = review()
            item['entry']['target']['sourceUrl'] = source
            with self.subTest(source=source), self.assertRaises(ReviewError):
                build_index(base(), batch([item]), SHA)
        item = review()
        item['entry']['target']['projectUrl'] = 'OFFICIAL'
        with self.assertRaises(ReviewError):
            build_index(base(), batch([item]), SHA)
        for field in ['entryUrl', 'evidenceUrl', 'subscriptionUrl', 'sourceIssueUrl']:
            item = review()
            item['entry'][field] = 'OFFICIAL'
            with self.subTest(field=field), self.assertRaises(ReviewError):
                build_index(base(), batch([item]), SHA)

if __name__ == '__main__':
    unittest.main(verbosity=2)
