#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""译文包体检器 v3：结构 / 内容 / 未翻 / 版本漂移（stale），输出 report.md + audit.json。

规则口径 = FireGaze 既定约定 + 实机复盘教训：
  error ：译文空但有来源；译文含 ### 而原文没有；占位符缺失；JSON 坏
  warn  ：日文假名残留；译文==原文；控制字符；异常膨胀；真未翻（排除「规范化保留」）
  stale ：包里的 Original 在当前 DLL 里找不到（插件更新过/包是旧版本）——需探针对比
  名单  ：DoNotLocalize（从 UITextRules.cs 提取）里的包只标注、不参与结论
"""
import json, os, re, glob, subprocess
from collections import Counter
from concurrent.futures import ThreadPoolExecutor

DIR = r'C:\Users\Fire\AppData\Roaming\XIVLauncherCN\pluginConfigs\FireGaze\uitrans'
INVENTORY = r'C:\Users\Fire\fg-adaptation\inventory.json'
PROBE = r'C:\everyone\FireGaze-Refactor\tools\UITextProbe\bin\Release\net10.0\UITextProbe.exe'
RULES = r'C:\everyone\FireGaze-Refactor\src\FireGaze\UIText\UITextRules.cs'
OUT = r'C:\Users\Fire\fg-audit'

PLACEHOLDER = re.compile(r'\{[^{}]*\}|%[sdif]|\\n|\\t|\\r')
KANA = re.compile(r'[\u3040-\u30ff\u31f0-\u31ff\uff66-\uff9d]')
CJK = re.compile(r'[\u4e00-\u9fff]')
CTRL = re.compile(r'[\x00-\x08\x0b\x0c\x0e-\x1f]')
LONG_EN = re.compile(r'[A-Za-z][A-Za-z \'\-\.,]{14,}')


def load_donot():
    s = open(RULES, encoding='utf-8').read()
    m = re.search(r'DoNotLocalize\s*=\s*new\([^)]*\)\s*\{(.*?)\}', s, re.S)
    return set(re.findall(r'"([^"]+)"', m.group(1))) if m else set()


def norm(x):
    return re.sub(r"[\s:：,，.。!！?？\"'·\u3000\\/|_\-]", '', x).lower()


def audit_entry(e):
    issues = []
    orig = e.get('Original') or ''
    trans = e.get('Translated') or ''
    src = (e.get('Source') or '').strip()
    if not trans.strip():
        if src:
            issues.append(('error', 'empty-translation', '译文为空但有来源'))
        return issues
    if '###' in trans and '###' not in orig:
        issues.append(('error', 'hash-in-translation', '译文含 ### 而原文没有'))
    src_ph, dst_ph = Counter(PLACEHOLDER.findall(orig)), Counter(PLACEHOLDER.findall(trans))
    for k, n in src_ph.items():
        if dst_ph.get(k, 0) < n:
            issues.append(('error', 'placeholder-missing', f'缺占位符 {k!r}'))
            break
    if CTRL.search(trans):
        issues.append(('warn', 'control-char', '含控制字符'))
    if KANA.search(trans) and not KANA.search(orig):
        issues.append(('warn', 'japanese-left', '译文含日文假名'))
    if trans == orig:
        issues.append(('warn', 'equals-original', '译文与原文相同'))
    if not CJK.search(trans) and len(re.findall(r'[A-Za-z]', orig)) >= 3 and norm(orig) != norm(trans):
        issues.append(('warn', 'untranslated', '译文无中文且非规范化保留'))
    if len(trans) > len(orig) * 3 + 12:
        issues.append(('warn', 'too-long', f'异常膨胀（译 {len(trans)} / 原 {len(orig)}）'))
    m = LONG_EN.search(trans)
    if m and not LONG_EN.search(orig):
        issues.append(('info', 'english-left', f'英文残留：{m.group(0)[:26]!r}'))
    return issues


ORIGINALS = r'C:\Users\Fire\AppData\Roaming\XIVLauncherCN\FireGazeData\uit-originals'


def probe_target(name, dll):
    """优先用「原始备份」做对比：打过补丁的 DLL 里原文已被替换，直接比会把正常包误判成 stale。"""
    d = os.path.join(ORIGINALS, name)
    if os.path.isdir(d):
        origs = glob.glob(os.path.join(d, '*.orig'))
        if origs:
            return max(origs, key=os.path.getsize)
    return dll


def probe_literals(dll):
    try:
        r = subprocess.run([PROBE, dll, '--all', '--json'], capture_output=True, timeout=180)
        data = json.loads(r.stdout.decode('utf-8', 'replace'))
        lits = {e.get('Original') or '' for e in data}
        tails = {l.split('###', 1)[1] for l in lits if '###' in l}
        return lits | tails
    except Exception:
        return None


def audit_pack(item, donot):
    path, dll = item
    name = os.path.basename(path)[:-5]
    try:
        d = json.load(open(path, encoding='utf-8'))
    except Exception as ex:
        return {'name': name, 'status': 'parse-error', 'detail': str(ex)[:120], 'total': 0,
                'errors': 1, 'warns': 0, 'counts': {}, 'samples': [], 'verdict': 'JSON 坏'}
    entries = (d.get('entries') or []) + (d.get('resources') or []) + (d.get('attributes') or [])
    counts, samples = Counter(), []
    for e in entries:
        for level, kind, detail in audit_entry(e):
            counts[f'{level}:{kind}'] += 1
            if len(samples) < 8 and level == 'error':
                samples.append(f"[error] {kind}: {(e.get('Original') or '')[:44]!r} → {(e.get('Translated') or '')[:44]!r}")
    if not (d.get('_meta') or {}).get('updatedAt'):
        counts['info:meta'] += 1

    errors = sum(v for k, v in counts.items() if k.startswith('error'))
    warns = sum(v for k, v in counts.items() if k.startswith('warn'))
    total = len(entries)

    stale = None
    target = probe_target(name, dll) if dll else None
    if target and os.path.exists(target) and total > 0:
        lits = probe_literals(target)
        if lits is not None:
            missing = sum(1 for e in entries if (e.get('Original') or '') not in lits)
            stale = missing / total

    jp, eq, un = counts.get('warn:japanese-left', 0), counts.get('warn:equals-original', 0), counts.get('warn:untranslated', 0)
    if name in donot:
        verdict = '名单内（跳过）'
    elif total == 0:
        verdict = '空包（未翻译）'
    elif stale is not None and stale > 0.3:
        verdict = f'旧版本包（{stale:.0%} 找不到），建议重抽'
    elif un > total * 0.3:
        verdict = '未翻完（建议重翻）'
    elif errors >= max(5, total * 0.05) or jp > max(5, total * 0.05) or eq > total * 0.3:
        verdict = '建议重翻'
    elif errors or warns:
        verdict = '可修（少量）'
    else:
        verdict = '健康'
    return {'name': name, 'status': 'ok', 'total': total, 'counts': dict(counts),
            'errors': errors, 'warns': warns, 'stale': stale, 'samples': samples, 'verdict': verdict}


def main():
    os.makedirs(OUT, exist_ok=True)
    donot = load_donot()
    inv = {r['name']: r.get('dll') for r in json.load(open(INVENTORY, encoding='utf-8'))}
    packs = sorted(glob.glob(os.path.join(DIR, '*.json')))
    items = [(p, inv.get(os.path.basename(p)[:-5])) for p in packs]
    with ThreadPoolExecutor(max_workers=4) as pool:
        results = list(pool.map(lambda it: audit_pack(it, donot), items))
    results.sort(key=lambda r: (r.get('verdict', ''), -r.get('errors', 0)))

    lines = ['# 译文包体检报告 v3（含版本漂移检查）', '', f'扫描 {len(packs)} 个包；名单内 {len(donot)} 个已标注', '']
    lines.append('| 包 | 条目 | error | warn | stale | 结论 |')
    lines.append('|---|---|---|---|---|---|')
    for r in results:
        st = f"{r['stale']:.0%}" if r.get('stale') is not None else '—'
        lines.append(f"| {r['name']} | {r['total']} | {r['errors']} | {r['warns']} | {st} | {r['verdict']} |")
    lines.append('')
    for r in results:
        if r.get('samples'):
            lines.append(f"### {r['name']}（{r['verdict']}）")
            lines += [f"- {s}" for s in r['samples']]
            lines.append('')
    open(os.path.join(OUT, 'report.md'), 'w', encoding='utf-8').write('\n'.join(lines))
    json.dump(results, open(os.path.join(OUT, 'audit.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=1)

    from collections import Counter as C
    v = C(r.get('verdict', '?') for r in results)
    print('结论分布：')
    for k, n in v.most_common():
        print(f'  {k}: {n}')
    print('\n逐包：')
    for r in results:
        st = f"{r['stale']:.0%}" if r.get('stale') is not None else '-'
        print(f"  {r['name']:32s} {r['total']:5d} err={r['errors']} warn={r['warns']} stale={st:>5s}  {r['verdict']}")
    print('\n报告：', os.path.join(OUT, 'report.md'))


if __name__ == '__main__':
    main()
