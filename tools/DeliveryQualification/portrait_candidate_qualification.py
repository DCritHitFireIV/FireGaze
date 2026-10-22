"""Offline artifact qualification shared by future fixed-slot deployments. No game or network API."""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
from uuid import UUID

PAYLOAD = ('FireGaze.json', 'FireGaze.deps.json', 'FireGaze.LocalPreview.Core.dll', 'dnlib.dll',
           'translations.json', 'ffxiv-glossary.tsv', 'uit-rules.json', 'images/icon.png', 'FireGaze.dll')
ROOT_ASSETS = ('translations.json', 'ffxiv-glossary.tsv', 'uit-rules.json')


def require(value, message):
    if not value: raise RuntimeError(message)


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest().upper()


def current_inputs(repo):
    files = [repo/name for name in ('build-local-preview.ps1', *ROOT_ASSETS)]
    for folder in ('src', 'tools', 'Resources'):
        files.extend(path for path in (repo/folder).rglob('*') if path.is_file()
                     and not {'bin', 'obj', '__pycache__'} & set(path.relative_to(repo).parts))
    return {str(path.relative_to(repo)): digest(path) for path in sorted(files)}


def read_pe_metadata(path):
    result = subprocess.run(['pwsh', '-NoProfile', '-File',
        str(Path(__file__).with_name('read_portrait_pe_identity.ps1')), '-Path', str(path)],
        check=True, capture_output=True, text=True, encoding='utf-8', timeout=20)
    return json.loads(result.stdout)


def qualify(repo, full_report, version, read_pe=read_pe_metadata):
    repo, full_report = Path(repo).resolve(), Path(full_report).resolve()
    source = repo/'src/FireGaze/bin/Release'
    report = json.loads(full_report.read_text(encoding='utf-8'))
    log_path = full_report.with_suffix('.log')
    log = log_path.read_text(encoding='utf-8-sig')
    current = current_inputs(repo)
    require(report.get('exitCode') == 0 and report.get('inputFilesUnchanged') is True
            and report.get('deployedUnchanged') is True and bool(current)
            and report.get('inputBefore') == report.get('inputAfter') == current,
            'Fresh successful complete gate and unchanged real inputs required')
    require(report.get('externalFilesUnchanged') is True
            and isinstance(report.get('externalFilesBefore'), dict)
            and bool(report['externalFilesBefore'])
            and report['externalFilesBefore'] == report.get('externalFilesAfter'),
            'Unchanged real CN client and SDK evidence required')
    projects = sorted(path.stem for path in (repo/'tools').glob('*Tests/*Tests.csproj'))
    executed = re.findall(r'^Running offline suite: (\S+)\s*$', log, re.MULTILINE)
    summaries = re.findall(r'^Offline validation finished: (\d+) suites; skipped: (.*?)\.\s*$', log, re.MULTILINE)
    require(projects and len(projects) == len(set(projects)) == len(executed) == len(set(executed))
            and set(projects) == set(executed) and len(summaries) == 1
            and int(summaries[0][0]) == len(projects) and summaries[0][1].strip() == '',
            'Every current test project must execute exactly once with zero skips')
    payload = {name: digest(source/name) for name in PAYLOAD}
    root_assets = {name: digest(repo/name) for name in ROOT_ASSETS}
    require(all(payload[name] == value for name, value in root_assets.items()),
            'Built data copies differ from qualified root inputs')
    pe = read_pe(source/'FireGaze.dll')
    require(isinstance(pe, dict) and pe.get('version') == version
            and pe.get('sha256') == payload['FireGaze.dll'], 'Actual PE version or hash differs')
    try: valid_mvid = isinstance(pe.get('mvid'), str) and UUID(pe['mvid']).int != 0
    except (ValueError, TypeError, AttributeError): valid_mvid = False
    require(valid_mvid, 'A real nonempty PE MVID is required')
    return dict(version=version, suites=len(projects), skips=0, deployed=False, inGameAccepted=False,
        fullReport=str(full_report), fullReportSHA256=digest(full_report), fullLogSHA256=digest(log_path),
        peMetadata=pe, payload=payload, rootSourceAssets=root_assets, suiteNames=executed)


def load(path, pin, repo, version, read_pe=read_pe_metadata):
    require(len(pin) == 64 and digest(path) == pin, 'Pinned candidate qualification changed')
    value = json.loads(Path(path).read_text(encoding='utf-8'))
    current = qualify(repo, value.get('fullReport', ''), version, read_pe)
    require(value == current, 'Pinned report, source, payload, or actual PE changed')
    return current


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', required=True, type=Path)
    parser.add_argument('--full-report', required=True, type=Path)
    parser.add_argument('--version', required=True)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args(argv)
    require(not args.output.exists(), 'Preserve previous qualification evidence')
    value = qualify(args.repo, args.full_report, args.version)
    with args.output.open('x', encoding='utf-8') as output:
        output.write(json.dumps(value, ensure_ascii=False, indent=2))
    print(json.dumps(dict(version=value['version'], suites=value['suites'], skips=0,
        peMetadata=value['peMetadata'], qualificationSHA256=digest(args.output))))
    return 0


if __name__ == '__main__': raise SystemExit(main())
