"""Compile an explicitly reviewed batch into a candidate navigation index; no network."""
import argparse
import copy
from datetime import date
import hashlib
import ipaddress
import json
from pathlib import Path
import re
import sys
from urllib.parse import parse_qsl, urlsplit


class ReviewError(ValueError):
    pass


ID = re.compile(r'[A-Za-z0-9_.-]{1,128}')
ENTRY_FIELDS = {'entryId', 'target', 'kind', 'coverage', 'coverageNote', 'verification', 'maintainer',
                'maintenance', 'verifiedOn', 'evidenceUrl', 'description', 'instructions', 'entryUrl',
                'subscriptionUrl', 'sourceIssueUrl', 'alternativeInternalName'}
IDENTITY_FIELDS = {'internalName', 'sourceUrl', 'projectUrl', 'version', 'apiLevel', 'client'}
PRIVATE_PATTERNS = re.compile(
    r'(?i)((?<![A-Za-z0-9])[A-Z]:[\\/]|\\\\[^\\\s]+\\|/(?:Users|home|tmp|private)/|'
    r'gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|'
    r'(?:api[_-]?key|token|secret|password)\s*[:=]|Bearer\s+\S+|'
    r'[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,})')


def require(condition, code):
    if not condition:
        raise ReviewError(code)


def public_text(value, maximum, required=True):
    require(isinstance(value, str) and len(value) <= maximum
            and (not required or bool(value.strip())), 'invalid-text')
    require(not any(ord(c) < 32 and c not in '\n\t' or ord(c) == 127 for c in value), 'control-character')
    require(not PRIVATE_PATTERNS.search(value), 'possible-private-data')
    return value


def public_url(value, optional=False):
    if optional and value in (None, ''):
        return value
    public_text(value, 2048)
    try:
        parsed = urlsplit(value)
        host = (parsed.hostname or '').rstrip('.').lower()
        require(parsed.scheme in ('http', 'https') and host and not parsed.username and not parsed.password,
                'invalid-public-url')
        require(not any(c.isspace() for c in value) and value == value.strip(), 'invalid-public-url')
        require(not host.endswith(('.localhost', '.local', '.internal', '.lan', '.home')) and host != 'localhost',
                'private-url')
        try:
            address = ipaddress.ip_address(host)
        except ValueError:
            require('.' in host and all(re.fullmatch(r'[A-Za-z0-9-]+', label) for label in host.split('.')),
                    'invalid-public-host')
        else:
            require(address.is_global, 'private-url')
        private_keys = {'token', 'key', 'api_key', 'apikey', 'access_token', 'password', 'secret', 'signature', 'sig'}
        require(not any(key.lower() in private_keys for key, _ in parse_qsl(parsed.query)), 'private-query')
        _ = parsed.port
    except (TypeError, ValueError):
        raise ReviewError('invalid-public-url') from None
    return value


def reviewed_date(value):
    try:
        require(isinstance(value, str) and date.fromisoformat(value).isoformat() == value, 'invalid-review-date')
    except (TypeError, ValueError):
        raise ReviewError('invalid-review-date') from None
    return value


def validate_entry(value):
    require(isinstance(value, dict) and not set(value) - ENTRY_FIELDS, 'unknown-entry-field')
    require(bool(ID.fullmatch(value.get('entryId', ''))), 'invalid-entry-id')
    identity = value.get('target')
    require(isinstance(identity, dict) and set(identity) == IDENTITY_FIELDS, 'invalid-target')
    require(bool(ID.fullmatch(identity.get('internalName', ''))), 'invalid-plugin-name')
    if identity['sourceUrl'] != 'OFFICIAL':
        public_url(identity['sourceUrl'])
    public_url(identity['projectUrl'], optional=True)
    public_text(identity['version'], 64)
    require(type(identity['apiLevel']) is int and 1 <= identity['apiLevel'] <= 1000 and identity['client'] == 'CN',
            'unsupported-client-identity')
    require(value.get('kind') in ('native', 'language-option', 'alternative'), 'invalid-kind')
    require(value.get('coverage') in ('full', 'partial'), 'unknown-coverage')
    require(value.get('verification') in ('verified', 'withdrawn'), 'unreviewed-entry')
    public_text(value.get('coverageNote'), 2000)
    public_text(value.get('maintainer'), 128)
    require(value.get('maintenance') in ('active', 'inactive', 'unknown'), 'invalid-maintenance')
    reviewed_date(value.get('verifiedOn'))
    public_url(value.get('evidenceUrl'))
    public_text(value.get('description'), 2000)
    public_text(value.get('instructions'), 4000)
    public_url(value.get('entryUrl'))
    public_url(value.get('subscriptionUrl'), optional=True)
    issue = value.get('sourceIssueUrl')
    if issue:
        public_url(issue)
        require(bool(re.fullmatch(r'https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/issues/[1-9][0-9]*', issue)),
                'invalid-source-issue')
    if value['kind'] == 'alternative':
        require(bool(ID.fullmatch(value.get('alternativeInternalName') or '')), 'missing-alternative-identity')
    return value


def build_index(base, batch, base_sha256):
    require(isinstance(base, dict) and set(base) == {'schema', 'revision', 'entries'} and base['schema'] == 1,
            'invalid-base')
    require(isinstance(batch, dict) and set(batch) == {'schema', 'expectedBaseSha256', 'expectedBaseRevision', 'revision', 'reviews'}
            and batch['schema'] == 1, 'invalid-review-batch')
    public_text(base['revision'], 128)
    public_text(batch['revision'], 128)
    require(batch['expectedBaseSha256'] == base_sha256 and bool(re.fullmatch('[0-9a-f]{64}', base_sha256))
            and batch['expectedBaseRevision'] == base['revision'], 'stale-base')
    require(batch['revision'] != base['revision'], 'revision-not-advanced')
    require(isinstance(base['entries'], list) and len(base['entries']) <= 5000, 'invalid-entry-count')
    entries = {}
    for value in base['entries']:
        validate_entry(value)
        require(value['entryId'] not in entries, 'duplicate-base-entry')
        entries[value['entryId']] = copy.deepcopy(value)
    require(isinstance(batch['reviews'], list) and 1 <= len(batch['reviews']) <= 100, 'invalid-review-count')
    changed = set()
    for review in batch['reviews']:
        require(isinstance(review, dict), 'invalid-review')
        require(bool(ID.fullmatch(review.get('reviewer', ''))), 'missing-reviewer')
        checked_on = reviewed_date(review.get('reviewedOn'))
        require(review.get('publicDataChecked') is True, 'public-data-review-required')
        if review.get('decision') == 'verified':
            expected = {'decision', 'entry', 'reviewer', 'reviewedOn', 'identityConfirmed', 'languageConsumptionConfirmed',
                        'coverageConfirmed', 'publicDataChecked', 'compatibilityConfirmed'}
            require(set(review) == expected, 'invalid-verification-fields')
            require(all(review[key] is True for key in ('identityConfirmed', 'languageConsumptionConfirmed',
                                                        'coverageConfirmed', 'compatibilityConfirmed')), 'explicit-review-required')
            value = copy.deepcopy(review['entry'])
            require(isinstance(value, dict) and 'verification' not in value and 'verifiedOn' not in value,
                    'review-owned-verification')
            value.update(verification='verified', verifiedOn=checked_on)
            validate_entry(value)
            entry_id = value['entryId']
            if entry_id in entries:
                require(entries[entry_id]['target'] == value['target'] and entries[entry_id]['kind'] == value['kind'],
                        'entry-id-reused-for-new-identity')
        elif review.get('decision') == 'withdrawn':
            require(set(review) == {'decision', 'entryId', 'reviewer', 'reviewedOn', 'publicDataChecked', 'reason', 'evidenceUrl'},
                    'invalid-withdrawal-fields')
            entry_id = review['entryId']
            require(entry_id in entries, 'withdrawal-target-missing')
            public_text(review['reason'], 2000)
            public_url(review['evidenceUrl'])
            value = copy.deepcopy(entries[entry_id])
            value.update(verification='withdrawn', verifiedOn=checked_on, evidenceUrl=review['evidenceUrl'],
                         coverageNote=review['reason'])
        else:
            raise ReviewError('explicit-decision-required')
        require(entry_id not in changed, 'duplicate-review-entry')
        changed.add(entry_id)
        entries[entry_id] = value
    require(len(entries) <= 5000, 'too-many-entries')
    result = {'schema': 1, 'revision': batch['revision'], 'entries': [entries[key] for key in sorted(entries)]}
    require(len(json.dumps(result, ensure_ascii=False).encode('utf-8')) <= 2_000_000, 'index-too-large')
    return result

def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base', type=Path, required=True)
    parser.add_argument('--reviews', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        require(args.output.resolve() not in (args.base.resolve(), args.reviews.resolve()), 'output-is-input')
        require(not args.output.exists(), 'output-already-exists')
        require(args.base.stat().st_size <= 2_000_000 and args.reviews.stat().st_size <= 2_000_000, 'input-too-large')
        raw = args.base.read_bytes()
        base_hash = hashlib.sha256(raw).hexdigest()
        result = build_index(json.loads(raw), json.loads(args.reviews.read_bytes()), base_hash)
        require(hashlib.sha256(args.base.read_bytes()).hexdigest() == base_hash, 'base-changed-during-review')
        with args.output.open('x', encoding='utf-8', newline='\n') as stream:
            stream.write(json.dumps(result, ensure_ascii=False, indent=2) + '\n')
        print(json.dumps({'status': 'candidate-written', 'revision': result['revision'], 'entries': len(result['entries']),
                          'baseSha256': base_hash, 'published': False}))
        return 0
    except ReviewError as error:
        print(json.dumps({'status': 'rejected', 'reason': str(error), 'published': False}), file=sys.stderr)
    except (OSError, ValueError, TypeError):
        print(json.dumps({'status': 'rejected', 'reason': 'input-or-output-unavailable', 'published': False}), file=sys.stderr)
    return 2

if __name__ == '__main__':
    raise SystemExit(main())
