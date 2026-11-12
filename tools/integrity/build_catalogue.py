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
    'CF05-charge-heads': 'the round-off difference is CFG-01 (MBM charge type); the other heads are CF07/CFG-05',
    'HL01-ledger-missing-account': 'same as CFG-04 (rows) and CFG-05 (charge heads), which ask the owner for the account',
    'LED-NO-LEDGER': 'same bills as TX01_HALF_SAVED_BILL, which asks re-post or delete',
    'GST12-bill-without-tax-summary': 'same bills as TX01_HALF_SAVED_BILL',
    'AU01-prototype-docs-12h-time': 'TX11_BILL_TIME_12H fixes Bill_Time; HQ itself writes 12-hour Ent_Time, so converting Ent_Time is not HQ-faithful',
    'TX13_NUMBER_GAPS': 'GST08 lists the sales gaps the GST filer needs; purchase/order gaps are internal',
    'TX19_DELETED_DOCUMENTS': 'same as AU03-deleted-documents',
    'GST09-nonstandard-series': 'the 1/1 bill is part of TX12_RENUMBERED_BILL',
    'GST03-gstin-state-mismatch': 'same as P-GST-STATE, which re-saves the account as HQ does',
    'GST04-gstin-but-unregistered': 'same as P-GST-TYPE',
    'GST05-duplicate-gstin': 'same as P-DUP-GSTIN',
    'P-GST-OWN': 'same as GST06-party-has-own-gstin, which reads the firms\' GSTINs from HQ\'s company database',
    'HL05-party-without-address': 'same as P-NO-ADDRESS',
    'OPEN-MASTER-NOROW': 'same accounts as P-OPEN-MASTER (which also covers master and rows disagreeing)',
    'P-OPEN-DIFF-ACCOUNT': 'same as OPEN-DIFF-ACCOUNT',
}
PATCH = {
    # Set round-off differences aside by the bill's own round-off charge, whatever its charge type, so switching the
    # Round Off charge to "Add" (CFG-01) does not move 466 known round-off bills into this list.
    'LED-UNBALANCED': [("AND ISNULL(t.ChTp_Op,0)=0 AND m.Chrg_Name LIKE '%round%'", "AND m.Chrg_Name LIKE '%round%'")],
    'CFG-10': [("' bills carry paise' Finding\n", "' bills carry paise' Finding, tt.TT_ID\n")],
}
# Decisions that need a value from the owner: "key" makes the repair one finding at a time (that column of the finding
# becomes @param), "choice" is what he picks; its options query returns Value/Label, the likeliest first.
# HQ's databases run at compatibility level 100 (no STRING_SPLIT), so the likely accounts come from a word list.
LEDGER_ACCOUNTS = ("SELECT CAST(a.Act_Id AS varchar(12)) Value, RTRIM(a.Act_Name)+' ('+CAST(a.Act_Id AS varchar(12))+')' Label "
                   "FROM IQAccmst a CROSS JOIN (SELECT Chrg_Name n FROM IQCHARGES WHERE Chrg_Id=@chrg) c "
                   "WHERE a.Act_Cat='A' AND ISNULL(a.Act_Usage,0)=0 AND a.Act_Type IN ('Income','Expense','Liability','Asset') "
                   "ORDER BY ISNULL((SELECT MIN(w.r) FROM (VALUES (1,'%round%','%round%'),(1,'%disc%','%disc%'),(1,'%adjust%','%adjust%'),"
                   "(2,'%adjust%','%disc%'),(1,'%oil%','%oil%'),(1,'%serv%','%serv%'),(1,'%freight%','%freight%'),(1,'%cartage%','%cartage%'),"
                   "(1,'%transport%','%transport%'),(1,'%pack%','%pack%'),(1,'%insur%','%insur%'),(1,'%labour%','%labour%'),(1,'%hamali%','%hamali%')) "
                   "w(r,cp,ap) WHERE c.n LIKE w.cp AND a.Act_Name LIKE w.ap),9), "
                   "CASE WHEN a.Act_Name LIKE '%GST%' OR a.Act_Name LIKE '%TDS%' OR a.Act_Name LIKE '%TCS%' OR a.Act_Name LIKE '%cess%' THEN 1 ELSE 0 END, "
                   "CASE a.Act_Type WHEN 'Expense' THEN 0 WHEN 'Income' THEN 1 ELSE 2 END, a.Act_Name")
BANK_TYPES = ("FROM TTypes tt LEFT JOIN IQAccmst a ON a.Act_Id=tt.Bank_naccno "
              "WHERE tt.Vouchtype IN ('Receipts','Payments') AND tt.Trmode=1 AND (a.Act_Id IS NULL OR a.Act_Type<>'Bank' OR a.Act_Cat<>'A' OR ISNULL(a.Act_Usage,0)<>0)")
OVERRIDE = {
    # Changing how HQ posts every future bill is the owner's call; correcting already-posted bills waits for it.
    'CFG-01': {'fix': 'owner'},
    'CFG-02': {'after': ['CFG-01']},
    'CFG-03': {'after': ['CFG-01']},
    # Re-saving 600+ dormant accounts only to add HQ's contact rows renews their dates for no visible gain: information.
    'P-NOT-FORM-SAVED': {'fix': 'report'},
    # Moving parties between balance-sheet groups changes how earlier printed balance sheets read: the owner's call.
    # Customer-Supplier accounts have no single group HQ's form would pick; they stay listed for the accountant.
    'P-GROUP-TYPE': {'fix': 'owner', 'each': True, 'repairsWhere': "CHARINDEX('[Customer-Supplier]', findings.Finding) = 0"},
    # One question for the whole list ("may we switch off these accounts?"); reversible in HQ.
    'P-DORMANT': {'each': True},
    # Only lines whose item now has a valid HSN can be filled in; the others wait for the item's HSN (Tidy item names).
    'GST01-taxed-lines-no-hsn': {'repairsWhere': "findings.Finding LIKE '%(item now has %'"},
    # A corrected GSTIN can match another account's: the duplicate was there all along, now it shows.
    # Earlier cash bills keep the GSTIN they were saved with, so they show as carrying an old GSTIN (information only).
    'P-GST-CHECKDIGIT': {'reveals': ['P-DUP-GSTIN', 'P-SPLIT-BALANCE', 'P-SAME-PAN', 'P-CASH-GSTIN-STALE',
                                     'CF02-party-gstin-differs', 'CF03-party-name-spelling', 'CF04-party-gst-type-state']},
    'P-GST-FORMAT': {'reveals': ['P-DUP-GSTIN', 'P-SPLIT-BALANCE', 'P-SAME-PAN', 'P-CASH-GSTIN-STALE']},
    'CFG-05': {
        'fixSql': "UPDATE IQCHARGES SET Act_ID=@act, IsPFChrg=ISNULL(IsPFChrg,0), Ent_Date=CAST(CAST(GETDATE() AS date) AS datetime), Ent_Uid=1 WHERE Chrg_Id=@chrg",
        'key': {'param': 'chrg', 'column': 'Chrg_Id'},
        'choice': {'param': 'act', 'label': 'Post this charge to', 'optionsSql': LEDGER_ACCOUNTS},
    },
    'CFG-10': {
        'fixSql': "UPDATE TTypes SET RoundedOffTo=1.00, RoundedOffType=0 WHERE TT_ID=@tt",
        'key': {'param': 'tt', 'column': 'TT_ID'},
    },
    'CFG-11': {
        'fixSql': "UPDATE tt SET Bank_naccno=@bank " + BANK_TYPES,
        'choice': {'param': 'bank', 'label': 'Bank vouchers open with',
                   'optionsSql': "SELECT CAST(a.Act_Id AS varchar(12)) Value, RTRIM(a.Act_Name)+' ('+CAST(a.Act_Id AS varchar(12))+')' Label FROM IQAccmst a "
                                 "WHERE a.Act_Type='Bank' AND a.Act_Cat='A' AND ISNULL(a.Act_Usage,0)=0 "
                                 "ORDER BY (SELECT COUNT(*) FROM IQDayBook d WHERE d.Act_ID=a.Act_Id) DESC, a.Act_Name"},
    },
}

PLAIN = {k: v for k, v in json.loads((AUDIT / 'plain.json').read_text(encoding='utf-8')).items() if not k.startswith('_')}

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
        if fix_sql and '<Act_Id from the finding>' in fix_sql:
            # The party audit's HQ account re-save, written for one account: run once per finding, the year from the firm.
            fix_sql = fix_sql.replace('<Act_Id from the finding>', '@act')
            fix_sql = re.sub(r"(DECLARE @fyStart datetime = )'\d{4}-\d{2}-\d{2}'", r'\1@periodStart', fix_sql)
            if '@periodStart' not in fix_sql:
                sys.exit(f'{cid}: account re-save without the financial-year start line')
            if "'<confirmed GSTIN>'" in fix_sql:
                # The owner confirms the suggested correction, or types the right GSTIN; HQ's own checks in the script
                # (length, type) and the check digit in the verification decide whether it is accepted.
                fix_sql = fix_sql.replace("'<confirmed GSTIN>'", 'UPPER(LTRIM(RTRIM(@gstin)))')
                body = re.sub(r'(?is)\s+ORDER\s+BY\s+[^()]*$', '', c['detectSql'].strip().rstrip(';'))
                c['choice'] = {'param': 'gstin', 'label': 'Correct GSTIN', 'free': True,
                               'optionsSql': f"SELECT f.Suggestion Value, f.Suggestion Label FROM (\n{body}\n) f WHERE f.Act_Id=@act AND ISNULL(f.Suggestion,'')<>''"}
            # Automatic repairs (and decisions taken for the whole list) run for every account; other decisions one at a time.
            c['key'] = {'param': 'act', 'column': 'Act_Id', 'each': c.get('each', fix == 'auto')}
        if fix == 'auto' and not fix_sql and not c.get('engineRepair'):
            fix = 'owner'  # an "automatic" repair without a tested statement is a decision, not a button
        if fix_sql and c.get('database') == 'cross':
            print(f'  {cid}: cross-firm repairs are not run by MK (another firm\'s write lock); kept as advice')
            fix_sql = None
        if fix_sql:
            # Every @name in a repair must be declared in it or supplied by its key/choice; otherwise it is advice only.
            body = re.sub(r'--[^\n]*', '', fix_sql)
            declared = {m.lower() for m in re.findall(r'(?i)\bDECLARE\s+@(\w+)', body)} | \
                       {m.lower() for m in re.findall(r'(?i),\s*@(\w+)\s+\w+', body)}
            supplied = {(c.get(k) or {}).get('param', '').lower() for k in ('key', 'choice')} | {'periodstart'}
            # @@FETCH_STATUS etc. are system functions; "@name=" outside a SET/WHERE is a procedure's named argument.
            used = {v.lower() for v in re.findall(r'(?<![@\w])@(\w+)\b(?!\s*=(?!=))', body)}
            missing = used - declared - supplied - {''}
            if missing:
                print(f'  {cid}: repair needs {sorted(missing)} with no way to supply it; kept as advice')
                fix_sql = None
        plain = PLAIN.get(cid, {})
        if not plain:
            print(f'  {cid}: no plain words in plain.json; the screen shows the technical text')
        checks.append({
            'id': cid,
            'area': 'security' if cid.startswith('SEC') else c.get('area', path.name.split('.')[0]),
            'title': (plain.get('title') or c['title']).strip(),
            'say': plain.get('say') or (c.get('why') or '').strip(),
            'ask': plain.get('ask') or (((c.get('fixNotes') or '').strip()) if fix == 'owner' else ''),
            'will': plain.get('will') or '',
            'severity': c.get('severity', 'warning'),
            'why': (c.get('why') or '').strip(),
            'detectSql': detect,
            'scope': 'cross' if c.get('database') == 'cross' else 'firm',
            'fix': fix,
            'fixSql': fix_sql,
            'engineRepair': c.get('engineRepair'),
            'advice': (c.get('fixNotes') or '').strip(),
            'key': c.get('key'),
            'choice': c.get('choice'),
            'after': c.get('after') or [],
            'repairsWhere': c.get('repairsWhere'),
            'reveals': c.get('reveals') or [],
        })

ids = {c['id'] for c in checks}
for c in checks:
    for a in c['after'] + c['reveals']:
        if a not in ids:
            sys.exit(f"{c['id']}: refers to {a}, which is not in the catalogue")

# Order: repairs that unblock others first (HQ refuses to re-save an account whose code another account also has, so the
# duplicate codes go before every other account re-save), then every check after the ones it waits for.
FIRST = ['P-DUP-CODE']
checks.sort(key=lambda c: 0 if c['id'] in FIRST and c['area'] == 'parties' else 1)  # stable: other order unchanged
placed, ordered = set(), []
def place(c, trail=()):
    if c['id'] in placed: return
    if c['id'] in trail: sys.exit(f"circular 'after' through {c['id']}")
    for a in c['after']:
        place(next(x for x in checks if x['id'] == a), trail + (c['id'],))
    placed.add(c['id']); ordered.append(c)
for c in list(checks): place(c)
checks = ordered

for k in PLAIN:
    if k not in seen and k not in DROP:
        print(f'  plain.json: {k} is not in any audit (dropped or renamed?)')

OUT.write_text(json.dumps(checks, ensure_ascii=False, indent=1), encoding='utf-8')
by = {}
for c in checks: by[c['fix']] = by.get(c['fix'], 0) + 1
print(f'{len(checks)} checks written to {OUT.relative_to(ROOT)}: {by}')
