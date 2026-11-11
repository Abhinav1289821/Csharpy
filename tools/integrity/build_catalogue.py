"""
Builds the engine's data-health catalogue (src/MK.Hq.Engine/Integrity/checks.json) from the audit findings in
docs/audit/*.checks.json. Each audit file lists checks with their detection SQL (run read-only on the live data) and,
for repairs, a set-based statement tested inside a rolled-back transaction until the check came back empty.

Run:  python tools/integrity/build_catalogue.py
"""
import json, pathlib, re, sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
AUDIT = ROOT / 'docs' / 'audit'
OUT = ROOT / 'src' / 'MK.Hq.Engine' / 'Integrity' / 'checks.json'

# Curation across the five audits: duplicates found by two audits are kept once (the variant that does not depend on a
# setting another repair changes), and checks that must not be reclassified by a repair are made independent of it.
DROP = {
    'LED-ROUNDOFF-CONFIG': 'same as CFG-01 (round-off charge type)',
    'LED-ROUNDOFF-2X': 'same as CFG-02/CFG-03, which compare the posted side with the correct one instead of reading the charge setting',
}
PATCH = {
    # Set round-off differences aside by the bill's own round-off charge, whatever its charge type, so switching the
    # Round Off charge to "Add" (CFG-01) does not move 466 known round-off bills into this list.
    'LED-UNBALANCED': [("AND ISNULL(t.ChTp_Op,0)=0 AND m.Chrg_Name LIKE '%round%'", "AND m.Chrg_Name LIKE '%round%'")],
}
OVERRIDE = {}

checks, seen = [], set()
for path in sorted(AUDIT.glob('*.checks.json')):
    for c in json.loads(path.read_text(encoding='utf-8')):
        cid = c['id'].strip()
        if cid in DROP:
            continue
        for old, new in PATCH.get(cid, []):
            if old not in c['detectSql']:
                sys.exit(f'{cid}: curation patch no longer matches the audit SQL: {old[:60]}')
            c['detectSql'] = c['detectSql'].replace(old, new)
        c.update(OVERRIDE.get(cid, {}))
        if cid in seen:
            sys.exit(f'{path.name}: duplicate check id {cid}')
        seen.add(cid)
        detect = c['detectSql'].strip().rstrip(';')
        if re.match(r'(?is)^\s*(;?\s*WITH)\b', detect):
            sys.exit(f'{path.name} {cid}: detection starts with WITH; the engine wraps it in SELECT COUNT(*) FROM (...)')
        fix = c.get('fix', 'report')
        fix_sql = (c.get('fixSql') or '').strip() or None
        if fix == 'auto' and not fix_sql and not c.get('engineRepair'):
            fix = 'owner'  # an "automatic" repair without a tested statement is a decision, not a button
        checks.append({
            'id': cid,
            'area': c.get('area', path.name.split('.')[0]),
            'title': c['title'].strip(),
            'severity': c.get('severity', 'warning'),
            'why': (c.get('why') or '').strip(),
            'detectSql': detect,
            'scope': 'cross' if c.get('database') == 'cross' else 'firm',
            'fix': fix,
            'fixSql': fix_sql,
            'engineRepair': c.get('engineRepair'),
            'advice': (c.get('fixNotes') or '').strip(),
        })

OUT.write_text(json.dumps(checks, ensure_ascii=False, indent=1), encoding='utf-8')
by = {}
for c in checks: by[c['fix']] = by.get(c['fix'], 0) + 1
print(f'{len(checks)} checks written to {OUT.relative_to(ROOT)}: {by}')
