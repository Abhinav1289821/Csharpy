# Audit: party (customer / supplier) masters

Status: audit, 11 Nov 2026. Data: `MKHQ_AUDIT_MBM`, `MKHQ_AUDIT_MP` (fresh copies of ISTMBM26 / ISTMP26), `MKHQ_AUDIT_MAIN`
(company master). Every query and every repair below was run read-only through `tools\sql\q.ps1` (one transaction,
always rolled back); nothing was written to any database. Machine-readable checks: `parties.checks.json` (same shape as
`ledger.checks.json` / `config.checks.json`; each `detectSql` returns one row per finding with a text column `Finding`
and, where it applies, `Act_Id`; none starts with `WITH`).

Background: `docs/hq-spec-accounts.md` (how FrmAccounts writes an account), `AccountMaster.cs` (the engine's HQ-identical
account save), decompiled `ISP/FrmAccounts.cs` (`Fill_EditData` 10545, `IsDuplicateFound` 10134, `IS_Valid_GSTIN` 9683,
`Verify_GSTIN` 12161).

Scope: accounts with `Act_Cat='A'` and `Act_Type` Customer / Supplier / Customer-Supplier — MBM 952 (630 C, 310 S,
12 C-S), MP 74 (52 C, 21 S, 1 C-S). The walk-in placeholder `=` (MBM #10502; MP has none) is excluded from the
duplicate checks and audited on its own.

## 1. Summary

| Id | Title | Severity | MBM | MP | Fix |
|---|---|---|---|---|---|
| P-DUP-NAME | Probable duplicate parties (same name after normalisation) | warning | 95 | 0 | owner |
| P-DUP-GSTIN | Same GSTIN on more than one party account | error | 25 | 0 | owner |
| P-SAME-PAN | Different GSTINs with the same PAN (related registrations or typo) | info | 6 | 0 | report |
| P-DUP-MOBILE | Same mobile number on different parties | info | 6 | 0 | report |
| P-SPLIT-BALANCE | One real party's balance spread over several accounts | warning | 10 | 0 | owner |
| P-NAME-PLACEHOLDER | Accounts named by a phone number or 1-2 characters (other than the '=' walk-in placeholder) | info | 2 | 0 | report |
| P-GST-FORMAT | GSTIN stored with blanks/lower case that hide a valid GSTIN | warning | 1 | 0 | auto |
| P-GST-LENGTH | GSTIN not 15 characters | error | 13 | 2 | owner |
| P-GST-CHECKDIGIT | GSTIN fails the official check digit | error | 36 | 7 | owner |
| P-GST-TYPE | GST registration type contradicts the GSTIN | warning | 5 | 0 | auto |
| P-GST-STATE | State does not match the GSTIN's state code | warning | 12 | 0 | auto |
| P-STATE-CAPTION | State caption not in HQ's state list (blank or old spelling) | info | 72 | 3 | auto |
| P-GST-OWN | Party carries the firm's own GSTIN (or the sister firm's) | error | 1 | 0 | owner |
| P-GST-PAN | PAN field disagrees with the PAN inside the GSTIN | info | 0 | 0 | report |
| P-CASH-KNOWN-PARTY | Anonymous cash/walk-in bills typed with the name of an existing account | warning | 13 | 1 | owner |
| P-CASH-GSTIN-UNREG | GST bills carrying a GSTIN but marked Un-Registered | error | 13 | 0 | owner |
| P-CASH-GSTIN-NO-ACCOUNT | B2B bills to a GSTIN that has no party account | info | 3 | 0 | owner |
| P-CASH-GSTIN-STALE | Bill copy of the party GSTIN differs from the account's current GSTIN | info | 36 | 0 | report |
| P-CASH-MODE-PARTY | Parties whose default bill mode is Cash (bills to them bypass their ledger) | info | 43 | 1 | owner |
| P-PLACEHOLDER | Walk-in placeholder '=' carries a balance or postings | warning | 2 | 0 | owner |
| P-NO-ADDRESS | Party without an address row (IQAccAdr) | warning | 133 | 0 | auto |
| P-NOT-FORM-SAVED | Party never saved through HQ's account screen (no 10 contact rows / no opening row / blank Act_TT) | info | 617 | 34 | auto |
| P-OPEN-MASTER | Opening balance in the master differs from the opening ledger rows | error | 4 | 0 | owner |
| P-OPEN-DIFF-ACCOUNT | Opening Balance Difference account (5) out of step with the opening rows | warning | 0 | 0 | auto |
| P-DUP-CODE | Duplicate account codes (HQ refuses to save either account) | warning | 38 | 0 | auto |
| P-GROUP-TYPE | Customer/supplier filed under the wrong group | warning | 135 | 1 | auto |
| P-TREE | Broken chart-of-accounts tree fields on a party | warning | 0 | 0 | auto |
| P-INACTIVE-BALANCE | Inactive party with a balance or open items | warning | 0 | 0 | report |
| P-DORMANT | Dormant parties (no balance, no ledger rows, no bills, never referenced) | info | 695 | 31 | owner |
| P-ORPHAN-REFS | Rows pointing at an account that no longer exists | warning | 48 | 49 | report |
| P-BALANCE-SIDE | Customer in credit / supplier in debit | info | 37 | 1 | report |

Severity: `error` = wrong money/GST data or HQ refuses to edit the account; `warning` = misleading reports or blocked
repairs; `info` = hygiene. Fix: `auto` = deterministic and written exactly as HQ's account screen would write it;
`owner` = needs a decision (exact question in each section); `report` = inform only.

## 2. Root causes (what made the data this way)

1. **Year carry-forward imports, not form saves.** MBM: 751 of 952 parties still have `Ent_Date = 20231206` (MP: 32 of
   74 have `20220325`) — created by HQ's year-end/import path, not by FrmAccounts. Those rows lack what every form save
   writes: 133 MBM parties have **no IQAccAdr row at all**, 748 have **0 instead of 10 IQAccOthers rows**, 753 have **no
   opening row** (`Doc_Id=-1`), `Act_TT` is blank, and **`Act_BillMode` is 0 (Cash)** on 798 MBM parties (759 of them
   created before 2024) and 37 MP parties, whereas the form defaults new accounts to 1 (Credit).
   Only accounts someone later opened and saved in HQ (216 MBM / 49 MP with 10 contact rows) look like form output.
2. **Year-copy accounts.** Old balances were parked by renaming accounts with year suffixes (`K CHANTI REDDY
   -2018-2019-2020`, then a second copy `…-2021-2022`). The 10-character `Act_Code` truncated both to the same code
   (`A20274-201`), giving 19 duplicate-code pairs — and FrmAccounts refuses to save an account whose code exists on
   another account, so neither copy can be edited or switched off in HQ. Many copies sit under FIXED ASSETS or a group
   called `Computers`.
3. **HQ's checks are thin.** Uniqueness is exact name and code only (case-insensitive) — no GSTIN, mobile or fuzzy name
   check, so `Y K CONSTRUCTIONS` / `YK CONSTRUCTIONS` and two accounts with one GSTIN coexist. The GSTIN check digit only
   sets a red label; invalid GSTINs save. The state follows the GSTIN only when the GSTIN or GST type is *typed*, so
   carried-forward accounts kept blank or wrong states.
4. **Cash-mode billing to known parties.** Because legacy accounts default to bill mode Cash, bills to them post to
   `Cash on hand` with the party only in the bill's `IQCash_TRAN` copy; operators also type a known party's name on an
   anonymous cash bill instead of picking the account. Either way the party's ledger misses the bill.

## 3. The HQ-identical repair: an account re-save

Every `auto` fix is "open the account in HQ, change one thing, press Save" — reproduced statement-for-statement. The
script (`fixSql` of each auto check; the overrides block differs) does what `FrmAccounts.SaveData` does for an existing
account (spec §1.2), with the values reloaded the way `Fill_EditData`/`EditAccount` reload them:

1. `EXEC IQaccmst_update` with the stored values (name untrimmed, `Act_TT` = type, `Ent_Date` = today, `Ent_UId` = 1,
   opening = sum of the account's `Doc_Id=-1` rows, sign D when ≥ 0). A changed `Rec_higher` makes the procedure
   recompute `Act_MG` and run `IQUpdateRecOrder` (≈220 other rows' `Rec_Order` shift +1, as in HQ).
2. `DELETE IQAccadr` + `EXEC IQAccAdr_insert` — every box trimmed; empty boxes `' '`, PAN/TAN/GSTIN/Aadhaar/notes/images
   `''`; missing row ⇒ blanks, state `37-Andhra Pradesh`, GST type 2 (exactly what the form shows for such an account);
   `Act_Mode` = bill mode ≠ 0; `Act_FoodLICExpDate` = today; `Cash_Discount` → NULL.
3. `DELETE IQAccOthers` + 10 × `EXEC IQAccOthers_insert` (LnNo 0–9, existing contacts kept).
4. Opening row: delete + `EXEC IQdaybook_insert` (0 included) — **except** when `ADJUST` rows settle against it, where
   it is updated in place (the engine's deliberate deviation, spec §6; HQ would orphan the settlements).
5. Account 5 "Opening Balance Difference": master figure + one `opDif` row rewritten (steps 9–12).

Guards (the script raises and changes nothing): bill-wise opening; **`Act_OPbal` ≠ opening rows** (a re-save would
silently overwrite the master figure — those accounts are `P-OPEN-MASTER`, owner first); and FrmAccounts' own refusals:
GSTIN length ≠ 15 after trim, GST type inconsistent with GSTIN, duplicate name, duplicate code.

Side effects that are HQ-normal and must be accepted with any repair: `Ent_Date` = today, `Act_TT` filled, zero-opening
sign forced to D, blank address boxes become `' '`, ship state blank → AP, `Act_FoodLICExpDate` = today, opening row
gets a new SR_NO (unless settled).

**Tested (rolled back) on MBM:**

| Account | Repair | Result |
|---|---|---|
| #10408 SS POLYMERS | state from GSTIN | `Act_state` 37-Andhra Pradesh → 36-Telangana; 10 contact rows + 0 opening row created; account 5 unchanged (2,183,415.60 C) |
| #10105 SANTHI PLASTICS | GSTIN blanks | `37AAB HP9506P1ZJ` → `37AABHP9506P1ZJ` (valid) |
| #11033 Sri ramadasu motar transport | GST type | type 2 → 0 with its valid GSTIN; state already AP |
| #10722 SIGMA SEA FOODS | group | FIXED ASSETS (411) → SUNDRY DEBTORS (414): `Act_MG` A, `Rec_Level` 3, `Rec_Index` 714, `Rec_Order` 936 (249 rows affected in all, mostly the Rec_Order shift); balance 141,709 Dr unchanged |
| #10779 R S V AGENCISES | group | Profit and Loss Accounts (390) → SUNDRY CREDITORS (407) |
| #10242 SRI PAPERS PVT Ltd-2013-2014 | missing IQAccAdr | address row inserted with HQ defaults |
| #11088 B chandrarao-…-2022 | duplicate code | `A20474-201` → `A12088` |
| #11043 Recomax engineering | caption | `36-Telengana` → `36-Telangana` |
| #10428 MODERAN ELECTRIAL CENTER | guard | refused: master 49,143 C vs no opening row |

Production path: `AccountMasterService.SaveAsync` already implements steps 1–5 for Customer/Supplier. To run these
repairs through it, it needs (a) a missing-address account treated as the `Fill_EditData` defaults instead of
`HQ_ACCOUNT_NO_ADDRESS`, (b) a group override (today it keeps the stored group when the type is unchanged), (c) code
override, (d) Customer-Supplier support for the few such repairs. Run each repair as its own transaction; the
order matters per account: GSTIN length/check digit → GST type → state → group/code → anything else.

## 4. Findings

### P-DUP-NAME — Probable duplicate parties (same name after normalisation)
*warning; MBM 95, MP 0; fix: **owner***

**Why it matters.** The same real party under two or more accounts splits its ledger, outstanding and GST history; HQ's own duplicate check is exact-name only (case-insensitive), so 'Y K CONSTRUCTIONS' and 'YK CONSTRUCTIONS', or a renamed '-2018-2019-2020' copy, pass it.

**Root cause.** HQ checks only exact names; legacy year-copies ('-2018-2019-2020'), spelling variants and 'M/S'/'SRI' prefixes were entered as new accounts.

**Examples.**
* MBM: cluster BCNDR: #10872 B chandrarao-2018-2019-2020 [Customer] bal 0.00, ledger rows 0, last -, bills 0, open items 0 (0.00)
* MBM: cluster BCNDR: #11088 B chandrarao-2018-2019-2020-2021-2022 [Customer] bal 0.00, ledger rows 0, last -, bills 0, open items 0 (0.00)
* MBM: cluster BLRJ: #10864 Balaraju-2018-2019-2020 [Customer] bal 0.00, ledger rows 0, last -, bills 0, open items 0 (0.00)
* MBM: cluster BLRJ: #11082 Balaraju-2018-2019-2020-2021-2022 [Customer] bal 0.00, ledger rows 0, last -, bills 0, open items 0 (0.00)
* MBM: cluster BSTB: #10873 B sathibabu-2018-2019-2020 [Customer] bal 0.00, ledger rows 0, last -, bills 0, open items 0 (0.00)

**Repair (owner).** Name key = upper case, 'M/S' and year suffixes ('-2018-2019-2020') removed, punctuation/spaces removed, words PVT/LTD/SRI/SREE/TRADERS/ENTERPRISES/AGENCIES/AGENCISES/GARU/AND/CO removed, then vowels and H/Y removed and doubled consonants collapsed (catches STELLS/STEELS, CONSTRUCTONS/CONSTRUCTIONS, RAMAKRISHANA/RAMAKRISHNA). Clusters whose members carry different GSTINs are usually different firms (e.g. MAHAVEER MARKETING vs SRI MAHAVEER MARKETING) and should be dismissed. HQ has no merge feature. A merge that stays HQ-consistent is: (1) pick the survivor; (2) for every posted row move Act_ID loser->survivor in IQDayBook (and Contra_ID in the other rows of those vouchers), IQDoc_Header.Act_ID (and HeadAct_ID/Act_ControlID), IQCash_TRAN.Act_ID (keep the bill's typed name/GSTIN copy), IQDOC_Recd/IQDOC_Delv.Act_Id if set, IQNrMast.Act_Id, Vehicles.Act_Id; ADJUST links are by SR_NO so they survive; (3) fold the loser's opening row into the survivor's (sum Doc_Id=-1 rows, one row, then the account-5 rewrite); (4) untick Active on the loser (Act_Usage=1 via a re-save) rather than delete, because HQ's delete is refused while any row references it. Moving rows of bills dated before 1 Oct 2026 changes filed GST returns' party (B2B GSTIN) and printed ledgers. Question to ask: 'Are <A> and <B> the same party? If yes, which name/GSTIN/address is right, and may we move all of <B>'s bills, receipts and opening balance (including bills before 1 Oct 2026) onto <A> and switch <B> off?'

Detect: `parties.checks.json` → `P-DUP-NAME`.

### P-DUP-GSTIN — Same GSTIN on more than one party account
*error; MBM 25, MP 0; fix: **owner***

**Why it matters.** A GSTIN identifies one registered business per state; two accounts with one GSTIN split that business's ledger and outstanding (or one of them carries someone else's GSTIN, which puts B2B invoices on the wrong recipient in GSTR-1). HQ does not check GSTIN uniqueness.

**Root cause.** No GSTIN uniqueness check in HQ; second accounts opened for a unit, for the supplier side of a customer, or with a pasted GSTIN.

**Examples.**
* MBM: GSTIN 37AABCD0248B1Z9 on 2 accounts: #10247 DEVI SEA FOODS Ltd UNIT - 3 [Customer] bal 12139.00 Dr, ledger rows 83, last 2026-11-09, bills 71, open items 1 (540.00)
* MBM: GSTIN 37AABCD0248B1Z9 on 2 accounts: #11155 DEVI SEA FOODS Ltd unit [Customer] bal 45108.00 Dr, ledger rows 46, last 2026-11-06, bills 35, open items 3 (37695.00)
* MBM: GSTIN 37AADCB5352J1ZL on 2 accounts: #10627 B S R INFRATECH INDIA Ltd [Customer] bal 0.00, ledger rows 0, last -, bills 0, open items 0 (0.00)
* MBM: GSTIN 37AADCB5352J1ZL on 2 accounts: #10638 BSR INFRATECH INDIAL LTD [Customer] bal 0.00, ledger rows 0, last -, bills 0, open items 0 (0.00)
* MBM: GSTIN 37AADCS1196B1ZJ on 2 accounts: #10655 S R M T Ltd [Customer] bal 530.00 Dr, ledger rows 0, last -, bills 0, open items 0 (0.00)

**Repair (owner).** Per cluster decide: same party (merge as in P-DUP-NAME), a Customer and a Supplier account for the same firm (legal in HQ; owner may prefer one 'Customer-Supplier' account, which is a merge), two units/branches of one company (legal only if they are separate GST registrations, which would have different GSTINs - so one GSTIN is wrong), or a typing error (correct the GSTIN by a re-save). Question: 'Accounts <A> and <B> both carry GSTIN <g>. Same business? If not, which one has the wrong GSTIN?'

Detect: `parties.checks.json` → `P-DUP-GSTIN`.

### P-SAME-PAN — Different GSTINs with the same PAN (related registrations or typo)
*info; MBM 6, MP 0; fix: **report***

**Why it matters.** Characters 3-12 of a GSTIN are the holder's PAN. Two GSTINs with one PAN are either the same business in two states / a second registration (legal), or one of them is a mistyped copy of the other (e.g. 'CIZ3' vs '1Z3').

**Root cause.** Second registrations of one business, or a mistyped copy of a GSTIN.

**Examples.**
* MBM: PAN AAACR5055K: #10628 REALIENCE INDUSTRIES LTD GSTIN 37AAACR5055K1Z6
* MBM: PAN AAACR5055K: #11277 RELIANCE INDUSRIES LIMTED GSTIN 37AAACR5055K3Z4
* MBM: PAN ACEPH1039C: #10401 CHOUDHRAY SANITARY GSTIN 29ACEPH1039CIZ3
* MBM: PAN ACEPH1039C: #10951 CS WATER TAP GSTIN 29ACEPH1039C1Z3
* MBM: PAN AMYPM2139A: #10741 M/s sathyanarayana .mummini GSTIN 37AMYPM2139A1ZX

**Repair (report).** Report. Where one GSTIN fails the check digit (P-GST-CHECKDIGIT) and the other is valid, the invalid one is usually a typo of the valid one and the pair is then a P-DUP-GSTIN case.

Detect: `parties.checks.json` → `P-SAME-PAN`.

### P-DUP-MOBILE — Same mobile number on different parties
*info; MBM 6, MP 0; fix: **report***

**Why it matters.** One phone number on two accounts is either a family/firm relation (legal) or the same customer entered twice; one account here is even named after the number.

**Root cause.** No mobile uniqueness check; one account even named after the number.

**Examples.**
* MBM: mobile 8332044688: #10606 Sri gayathri enterprises [Customer] bal 0.00, ledger rows 0, last -, bills 0, open items 0 (0.00)
* MBM: mobile 8332044688: #10607 SRI SAI GAYTHRI AGENCISES [Customer] bal 0.00, ledger rows 0, last -, bills 0, open items 0 (0.00)
* MBM: mobile 9030688374: #10488 G srinivasarao [Customer] bal 0.00, ledger rows 0, last -, bills 0, open items 0 (0.00)
* MBM: mobile 9030688374: #11170 9030688374 [Customer] bal 30000.00 Cr, ledger rows 0, last -, bills 0, open items 0 (0.00)
* MBM: mobile 9866012909: #10647 Matte srinivasarao [Customer] bal 0.00, ledger rows 0, last -, bills 0, open items 0 (0.00)

**Repair (report).** Ask the shop; merge only if the same person (P-DUP-NAME procedure).

Detect: `parties.checks.json` → `P-DUP-MOBILE`.

### P-SPLIT-BALANCE — One real party's balance spread over several accounts
*warning; MBM 10, MP 0; fix: **owner***

**Why it matters.** When a duplicate cluster (same name key or same GSTIN) has non-zero balances on two or more accounts, statements, outstanding lists and dunning show each part separately and often with opposite signs (one owes, one is owed), so the true dues are invisible.

**Root cause.** Duplicates (above) that both received postings or an opening balance.

**Examples.**
* MBM: group GSTIN 37AABCD0248B1Z9: #10247 DEVI SEA FOODS Ltd UNIT - 3 [Customer] bal 12139.00 Dr, ledger rows 83, last 2026-11-09, bills 71, open items 1 (540.00) | group net 57247.00 Dr
* MBM: group GSTIN 37AABCD0248B1Z9: #11155 DEVI SEA FOODS Ltd unit [Customer] bal 45108.00 Dr, ledger rows 46, last 2026-11-06, bills 35, open items 3 (37695.00) | group net 57247.00 Dr
* MBM: group GSTIN 37AAHFU5775F1ZQ: #11178 UPRIGHT projects LLP [Customer] bal 115707.00 Dr, ledger rows 43, last 2026-11-07, bills 38, open items 1 (6710.00) | group net 17924.00 Dr
* MBM: group GSTIN 37AAHFU5775F1ZQ: #11287 UPRIGHT projects LLP ( A D B ) [Customer] bal 97783.00 Cr, ledger rows 25, last 2026-09-08, bills 21, open items 0 (0.00) | group net 17924.00 Dr
* MBM: group GSTIN 37AQTPG7032K1ZD: #11241 SAI DURGA WATER TUBES [Supplier] bal 9005.00 Cr, ledger rows 0, last -, bills 0, open items 0 (0.00) | group net 61075.00 Cr

**Repair (owner).** Same decision and merge procedure as P-DUP-NAME. If the owner says 'same party' but does not want posted bills moved, the HQ-native alternative is a journal voucher (VType JV) on today's date transferring the loser's balance to the survivor, which leaves history untouched and zeroes the loser. Question: '<A> shows <x> Dr and <B> shows <y> Cr. Are they the same party, and should the balances be combined (by a transfer entry today, or by moving all their bills)?'

Detect: `parties.checks.json` → `P-SPLIT-BALANCE`.

### P-NAME-PLACEHOLDER — Accounts named by a phone number or 1-2 characters (other than the '=' walk-in placeholder)
*info; MBM 2, MP 0; fix: **report***

**Why it matters.** An account called '9030688374' or 'Vd' cannot be found by name, prints badly on bills and is usually a second account for a known customer.

**Root cause.** Account created in a hurry from the phone number or initials.

**Examples.**
* MBM: #10482 'Vd' [Customer] bal 0.00, ledger rows 0, last -, bills 0, open items 0 (0.00)
* MBM: #11170 '9030688374' [Customer] bal 30000.00 Cr, ledger rows 0, last -, bills 0, open items 0 (0.00)

**Repair (report).** Ask who it is; then rename (re-save with the new name; IQaccmst_update also renames IQNrMast) or merge into the existing account (see P-DUP-MOBILE).

Detect: `parties.checks.json` → `P-NAME-PLACEHOLDER`.

### P-GST-FORMAT — GSTIN stored with blanks/lower case that hide a valid GSTIN
*warning; MBM 1, MP 0; fix: **auto***

**Why it matters.** HQ stores GSTIN upper-cased and trimmed; an embedded blank ('37AAB HP9506P1ZJ') makes GST portal uploads and GSTIN searches fail although the number itself is right.

**Root cause.** GSTIN typed with an embedded blank before the form upper-cased/trimmed the ends only.

**Examples.**
* MBM: #10105 SANTHI PLASTICS: '37AAB HP9506P1ZJ' -> 37AABHP9506P1ZJ

**Repair (auto).** Auto: re-save the account with GSTIN = upper-case, blanks removed (only when that value passes the check digit; otherwise the row belongs to P-GST-CHECKDIGIT). HQ-identical write path: IQaccmst_update + IQAccAdr delete/insert + 10 IQAccOthers + opening row + account 5, exactly FrmAccounts' Save. Fields that change besides GSTIN are what any HQ save changes: Ent_Date=today, Act_TT=type, address boxes trimmed and empty ones stored as ' ', Act_FoodLICExpDate=today, Cash_Discount NULL, zero opening sign forced to D, opening row re-inserted (kept in place if settled). Production path: AccountMasterService.SaveAsync with LoadAsync() values and the corrected GSTIN.

`fixSql`: the HQ re-save script of §3 with the override below.
```sql
UPDATE @d SET GSTIN = REPLACE(REPLACE(UPPER(GSTIN),' ',''),CHAR(9),'');  -- caller has verified the result passes the check digit
```

Detect: `parties.checks.json` → `P-GST-FORMAT`.

### P-GST-LENGTH — GSTIN not 15 characters
*error; MBM 13, MP 2; fix: **owner***

**Why it matters.** Not a GSTIN at all (a digit or letter missing/extra). GST returns reject it, and HQ's account screen refuses to save the account ('GSTIN Must be Length of 15') until it is corrected - which also blocks every other repair on that account.

**Root cause.** Typing errors in carried-forward accounts (HQ's 15-character rule only fires when the account is saved in the form).

**Examples.**
* MBM: #10285 SRI KANAKA DURGA HARDWARE STORES: '37AJ4PD9630 NZF' has 14 characters
* MBM: #10507 GUNA MARKETING: '07ATHP5774D1ZU' has 14 characters
* MBM: #10572 S G ENVIROTECH: '37BMVPR47231F1Z1' has 16 characters
* MBM: #10595 K P R POLY PIPES: '37AATFK14981Z0' has 14 characters
* MBM: #10666 Guru krupa hardware house: '37BEWP1163A1ZQ' has 14 characters
* MP: #10219 SRI DURGA VENKATA STORES: '37AAQF2759E1ZR' has 14 characters
* MP: #10224 Godavari fine chem: '37AARFG48371ZB' has 14 characters

**Repair (owner).** Needs the real GSTIN (from the party's invoice/letterhead or the GST portal search by PAN). Question: 'What is the correct GSTIN of <party>? We have <x>.' If the party is dormant, clearing the GSTIN and setting the type to Un-Registered is the HQ-legal alternative.

Detect: `parties.checks.json` → `P-GST-LENGTH`.

### P-GST-CHECKDIGIT — GSTIN fails the official check digit
*error; MBM 36, MP 7; fix: **owner***

**Why it matters.** The 15th character is a checksum; a failing GSTIN is mistyped (mostly O/0 and I/1 confusions). GSTR-1 B2B rows and e-invoices for this party are rejected by the portal; HQ itself only shows 'Invalid GSTIN!' and saves anyway.

**Root cause.** O/0 and I/1 confusions; HQ only shows 'Invalid GSTIN!' and saves anyway.

**Examples.**
* MBM: #10097 GANESH PAINTS&GENERALS: 37AYSPB4938Q1Z1 (check character should be I)
* MBM: #10107 SUDHA SPINNING DIVISION: 37AABCS1177N1ZX (check character should be Z)
* MBM: #10142 SUGUNA  MOTARS AND PUMPS: 371KCPA5870A1ZN (check character should be W)
* MBM: #10225 VIMAL ELECTRICALS: 37ASLPSO191F1Z1 (check character should be 5), used on 3 bills
* MBM: #10263 VENKATA SREE RAMA  & SONS: 37AESPM1589E1ZO (check character should be 0) - likely 37AESPM1589E1Z0
* MP: #10212 VOLTA IMPEX PVT LTD: 37AAACV8160F327 (check character should be 2) - likely 37AAACV8160F3Z7
* MP: #10213 SUPRAJA PAINTS& HARDWARE: 37BLEPS5785G1Z1 (check character should be I)
* MP: #10218 DINESH TRADERS: 37AEVVPJ2076H1Z (check character should be Z)

**Repair (owner).** Suggestion = the only single-character O/0, I/1, S/5, B/8, Z/2, G/6 swap that makes the GSTIN valid (found for 20 of the 36 15-character failures in MBM and 3 of 7 in MP). It is a strong hint, not proof, so it is an owner confirmation (one click per party), then the same HQ re-save as P-GST-FORMAT (state follows the corrected prefix). Correcting the master also corrects past credit bills in HQ's GST reports (they read IQAccAdr.GSTIN at report time) but not cash bills (they keep their own IQCash_TRAN copy). Question: 'Is <suggestion> the GSTIN of <party>?'

`fixSql`: the HQ re-save script of §3 with the override below.
```sql
UPDATE @d SET GSTIN = '<confirmed GSTIN>';  -- the Suggestion column, once the owner confirms it
```

Detect: `parties.checks.json` → `P-GST-CHECKDIGIT`.

### P-GST-TYPE — GST registration type contradicts the GSTIN
*warning; MBM 5, MP 0; fix: **auto***

**Why it matters.** A party with a GSTIN but GSTTaxType Un-Registered is reported as B2C/unregistered in HQ's GST reports (no ITC for the buyer, wrong GSTR-1 table); HQ's form refuses to save this combination, so the account cannot be edited until fixed. The reverse (Registered without GSTIN) is equally refused.

**Root cause.** Carried-forward accounts never re-saved in the form (which refuses this combination).

**Examples.**
* MBM: #11032 VIJAYA MARKETING: GSTIN '37DKVPS9808E1ZP' with type Un-Registered (GSTIN itself invalid - fix that first)
* MBM: #11033 Sri ramadasu motar transport Ltd: GSTIN '37AADCS1196B1ZJ' with type Un-Registered
* MBM: #11034 Sri suryanarayana enterprises: GSTIN '37AEAPV5603C1ZQ' with type Un-Registered
* MBM: #11040 R T AGENCISES: GSTIN '37ABCFR31891ZY' with type Un-Registered (GSTIN itself invalid - fix that first)
* MBM: #11062 P SUDHANDRAVIJAYALAXMI: GSTIN '37EFLPP3785T1Z9' with type Un-Registered (GSTIN itself invalid - fix that first)

**Repair (auto).** Auto only where the GSTIN is valid: re-save with GSTTaxType 0 (Regular/Registered - the only registered type this shop uses; no Composition party exists) and Act_state from the prefix, exactly what FrmAccounts does when the operator picks 'Registered' (Verify_GSTIN fires on the type change). Rows whose GSTIN is invalid wait for P-GST-CHECKDIGIT/P-GST-LENGTH. 'Registered without GSTIN' (0 rows today) would be owner: get the GSTIN or set Un-Registered.

`fixSql`: the HQ re-save script of §3 with the override below.
```sql
UPDATE @d SET GSTTaxType = 0 WHERE LEN(GSTIN)=15 AND GSTTaxType>1;
UPDATE @d SET Act_state = st.cap FROM @d CROSS JOIN (VALUES ('01-Jammu & Kashmir'),('02-Himachal Pradesh'),('03-Punjab'),('04-Chandigarh'),('05-Uttarakhand'),('06-Haryana'),('07-Delhi'),('08-Rajasthan'),('09-Uttar Pradesh'),('10-Bihar'),('11-Sikkim'),('12-Arunachal Pradesh'),('13-Nagaland'),('14-Manipur'),('15-Mizoram'),('16-Tripura'),('17-Meghalaya'),('18-Assam'),('19-West Bengal'),('20-Jharkhand'),('21-Odisha'),('22-Chhattisgarh'),('23-Madhya Pradesh'),('24-Gujarat'),('25-Daman & Diu'),('26-Dadra & Nagar Haveli'),('27-Maharashtra'),('29-Karnataka'),('30-Goa'),('31-Lakshdweep'),('32-Kerala'),('33-Tamil Nadu'),('34-Pondicherry'),('35-Andaman & Nicobar Islands'),('36-Telangana'),('37-Andhra Pradesh'),('97-Other Territory')) st(cap) WHERE LEFT(st.cap,2)=LEFT(GSTIN,2) AND LEN(GSTIN)=15 AND GSTTaxType<=1;
```

Detect: `parties.checks.json` → `P-GST-TYPE`.

### P-GST-STATE — State does not match the GSTIN's state code
*warning; MBM 12, MP 0; fix: **auto***

**Why it matters.** For credit bills HQ's GSTR-1 takes the place of supply from IQAccAdr.Act_state, so a Telangana GSTIN with state '37-Andhra Pradesh' (or blank, which reports as AP) is filed as an intra-state supply; e-way bills and e-invoices are rejected. HQ's form sets the state from the GSTIN only when the GSTIN or GST type is typed, so old accounts kept the wrong state.

**Root cause.** The state is set from the GSTIN only when the GSTIN or GST type is typed; carried-forward and early accounts kept AP or blank.

**Examples.**
* MBM: #10091 STAR PLASTICS: GSTIN 37AAKFS1090C1ZB but state '' -> 37-Andhra Pradesh
* MBM: #10157 Kostal pharma labs pvt ltd: GSTIN 37AAECA9377P1ZS but state '' -> 37-Andhra Pradesh
* MBM: #10225 VIMAL ELECTRICALS: GSTIN 37ASLPSO191F1Z1 but state '' -> 37-Andhra Pradesh
* MBM: #10296 HALSTON distributors: GSTIN 37AAEFH9506Q1ZQ but state '' -> 37-Andhra Pradesh
* MBM: #10310 PADMAVATHI ELECTRICALS: GSTIN 37AIAPN5380L1ZW but state '' -> 37-Andhra Pradesh

**Repair (auto).** Auto: re-save with Act_state = FrmAccounts' caption whose first two characters equal the GSTIN's (Verify_GSTIN's rule; it does not require the check digit to pass). ship_Act_state is left as it is (HQ does not drive it from the GSTIN). Tested in a rolled-back transaction on #10408 (36-Telangana) - only Act_state plus the standard re-save fields changed.

`fixSql`: the HQ re-save script of §3 with the override below.
```sql
UPDATE @d SET Act_state = st.cap FROM @d CROSS JOIN (VALUES ('01-Jammu & Kashmir'),('02-Himachal Pradesh'),('03-Punjab'),('04-Chandigarh'),('05-Uttarakhand'),('06-Haryana'),('07-Delhi'),('08-Rajasthan'),('09-Uttar Pradesh'),('10-Bihar'),('11-Sikkim'),('12-Arunachal Pradesh'),('13-Nagaland'),('14-Manipur'),('15-Mizoram'),('16-Tripura'),('17-Meghalaya'),('18-Assam'),('19-West Bengal'),('20-Jharkhand'),('21-Odisha'),('22-Chhattisgarh'),('23-Madhya Pradesh'),('24-Gujarat'),('25-Daman & Diu'),('26-Dadra & Nagar Haveli'),('27-Maharashtra'),('29-Karnataka'),('30-Goa'),('31-Lakshdweep'),('32-Kerala'),('33-Tamil Nadu'),('34-Pondicherry'),('35-Andaman & Nicobar Islands'),('36-Telangana'),('37-Andhra Pradesh'),('97-Other Territory')) st(cap) WHERE LEFT(st.cap,2)=LEFT(GSTIN,2) AND LEN(GSTIN)=15 AND GSTTaxType<=1;
```

Detect: `parties.checks.json` → `P-GST-STATE`.

### P-STATE-CAPTION — State caption not in HQ's state list (blank or old spelling)
*info; MBM 72, MP 3; fix: **auto***

**Why it matters.** Act_state must be one of FrmAccounts' captions. '36-Telengana' (old spelling) and blank states are not in the list; GST reports treat blank as '37-Andhra Pradesh' and HQ shows AP when the account is opened, so the stored value and what HQ shows disagree.

**Root cause.** Older HQ caption list ('36-Telengana') and imports with an empty state.

**Examples.**
* MBM: #10087 PSG  INDUSTREAL INSTITUTE: state '' -> 37-Andhra Pradesh (what HQ loads)
* MBM: #10113 SARVANI MINARALS: state '' -> 37-Andhra Pradesh (what HQ loads)
* MBM: #10114 APPEX VISWA ENGINEERING SERVISES PV LTD: state '' -> 37-Andhra Pradesh (what HQ loads)
* MBM: #10118 MY HOME INDUSTRIES: state '' -> 37-Andhra Pradesh (what HQ loads)
* MBM: #10119 BALAJI WIRE NETTING STORE: state '' -> 37-Andhra Pradesh (what HQ loads)
* MP: #10121 SUPPLIER-1: state '' -> 37-Andhra Pradesh (what HQ loads)
* MP: #10122 CUSTOMER-1: state '' -> 37-Andhra Pradesh (what HQ loads)
* MP: #10123 SAI SIRI: state '' -> 37-Andhra Pradesh (what HQ loads)

**Repair (auto).** Auto: re-save; Fill_EditData already loads a blank state as '37-Andhra Pradesh', and a known code with a non-list spelling is mapped to the list caption of the same code ('36-Telengana' -> '36-Telangana'). Registered parties whose state disagrees with the GSTIN are left to P-GST-STATE (excluded here). An unregistered party really located in another state needs the owner to pick the state; blank-state unregistered parties are all local (AP towns) in the sample.

`fixSql`: the HQ re-save script of §3 with the override below.
```sql
UPDATE @d SET Act_state = st.cap FROM @d CROSS JOIN (VALUES ('01-Jammu & Kashmir'),('02-Himachal Pradesh'),('03-Punjab'),('04-Chandigarh'),('05-Uttarakhand'),('06-Haryana'),('07-Delhi'),('08-Rajasthan'),('09-Uttar Pradesh'),('10-Bihar'),('11-Sikkim'),('12-Arunachal Pradesh'),('13-Nagaland'),('14-Manipur'),('15-Mizoram'),('16-Tripura'),('17-Meghalaya'),('18-Assam'),('19-West Bengal'),('20-Jharkhand'),('21-Odisha'),('22-Chhattisgarh'),('23-Madhya Pradesh'),('24-Gujarat'),('25-Daman & Diu'),('26-Dadra & Nagar Haveli'),('27-Maharashtra'),('29-Karnataka'),('30-Goa'),('31-Lakshdweep'),('32-Kerala'),('33-Tamil Nadu'),('34-Pondicherry'),('35-Andaman & Nicobar Islands'),('36-Telangana'),('37-Andhra Pradesh'),('97-Other Territory')) st(cap) WHERE LEFT(st.cap,3)=LEFT(Act_state,3) AND Act_state<>st.cap;
```

Detect: `parties.checks.json` → `P-STATE-CAPTION`.

### P-GST-OWN — Party carries the firm's own GSTIN (or the sister firm's)
*error; MBM 1, MP 0; fix: **owner***

**Why it matters.** A supplier with our own GSTIN means purchases from it are booked as from ourselves - its real GSTIN is unknown and ITC on its invoices cannot be matched. The sister firm's GSTIN on a party is legal only if that party IS the sister firm.

**Root cause.** Firm's own GSTIN typed into a supplier account (opening balance 59,971 Cr).

**Examples.**
* MBM: #10439 M G MARKETING [Supplier] has GSTIN 37AHXPB8768N1Z6 = MK BUILDING MATERIALS (MBM)

**Repair (owner).** The two GSTINs are [Company Master].GSTIN in the company database (MKHQ_AUDIT_MAIN / live company master); the engine should take them from there instead of the literal list. Question: 'M G MARKETING (#10439) is saved with our own GSTIN 37AHXPB8768N1Z6 and an opening balance of 59,971 Cr. What is its real GSTIN?'

Detect: `parties.checks.json` → `P-GST-OWN`.

### P-GST-PAN — PAN field disagrees with the PAN inside the GSTIN
*info; MBM 0, MP 0; fix: **report***

**Why it matters.** Characters 3-12 of a GSTIN are the PAN; a different Act_PAN means one of the two is wrong (TDS/TCS and e-invoice use the PAN).

**Root cause.** -

**Repair (report).** No account has Act_PAN filled today, so this guards future data. Owner decides which is right.

Detect: `parties.checks.json` → `P-GST-PAN`.

### P-CASH-KNOWN-PARTY — Anonymous cash/walk-in bills typed with the name of an existing account
*warning; MBM 13, MP 1; fix: **owner***

**Why it matters.** The bill was made to Cash (or the '=' placeholder) with the party's name typed in, so the party's ledger, sales history and GST B2B reporting do not show it although HQ has an account for the party (fragmented identity).

**Root cause.** Bill made in cash mode with the name typed instead of picking the existing account.

**Examples.**
* MBM: Sales - Orders SO/205 2026-08-05 Rs 29084.16 typed 'THAMMANA NAGEASH GARU' = account #11302 Thammana nageash garu [Customer] (before 1 Oct 2026)
* MBM: GST Purchases GSTPUR/272 2026-08-19 Rs 8021.00 typed 'VENKATA SAI GENERAL STORES' = account #10096 VENKATA SAI GENERAL STORES [Supplier] (before 1 Oct 2026)
* MBM: Sales - Orders SO/340 2026-10-06 Rs 16323.04 typed 'T SATHIBABU' = account #10441 T sathibabu [Customer]
* MBM: GST Purchases GSTPUR/266 2026-10-06 Rs 1610.00 typed 'CALCUTTA TUBE CO' = account #10120 CALCUTTA TUBE CO., [Supplier]
* MBM: GST Purchases GSTPUR/267 2026-10-08 Rs 18047.00 typed 'VENKATA SAI GENERAL STORES' = account #10096 VENKATA SAI GENERAL STORES [Supplier]
* MP: GST Sales GS/10 2026-04-10 Rs 17546.64 typed 'pai maheash' = account #10268 PAI MAHEASH [Customer] (before 1 Oct 2026)

**Repair (owner).** Do not relink automatically: setting IQCash_TRAN.Act_ID only changes the bill's party lookup, while making it a credit bill to the party would re-post its ledger (cash -> party), which is a bill modification (owner; before 1 Oct 2026 also a filed-return change). For the future, the bill screen should offer the matching account when a typed name equals an account name. Question: 'Cash purchase GSTPUR/267 typed VENKATA SAI GENERAL STORES - should such bills be booked on the supplier account (credit) or stay cash?'

Detect: `parties.checks.json` → `P-CASH-KNOWN-PARTY`.

### P-CASH-GSTIN-UNREG — GST bills carrying a GSTIN but marked Un-Registered
*error; MBM 13, MP 0; fix: **owner***

**Why it matters.** The bill copy (IQCash_TRAN) has a 15-character GSTIN but GSTTaxType 2, so HQ's GSTR reports put the bill in the unregistered (B2C / B2BUR) tables: the seller's GSTR-1 never shows it as B2B and the input credit cannot be matched.

**Root cause.** Cash purchase screen: GSTIN typed but the GST type left at the default Un-Registered.

**Examples.**
* MBM: GST Purchases GSTPUR/272 2026-08-19 Rs 8021.00 party 'VENKATA SAI GENERAL STORES' GSTIN 37AUXPK5620R1ZL typed as Un-Registered (before 1 Oct 2026)
* MBM: GST Purchases GSTPUR/266 2026-10-06 Rs 1610.00 party 'CALCUTTA TUBE CO' GSTIN 37ALEPS1787R1Z2 typed as Un-Registered
* MBM: GST Purchases GSTPUR/267 2026-10-08 Rs 18047.00 party 'VENKATA SAI GENERAL STORES' GSTIN 37AUXPK5620R1ZL typed as Un-Registered
* MBM: GST Purchases GSTPUR/269 2026-10-08 Rs 6088.00 party 'SRI GODHA ENTERPRISES' GSTIN 37DQGPD1968D1ZW typed as Un-Registered
* MBM: GST Purchases GSTPUR/270 2026-10-08 Rs 51525.00 party 'ADITYA ENTERPRISES' GSTIN 37AAKFA9259R1ZF typed as Un-Registered

**Repair (owner).** Posted bills: owner. HQ-native fix is to open each bill in HQ, set the party's GST type to Registered and save (HQ re-posts the bill). A direct UPDATE IQCash_TRAN SET GSTTaxType=0 would give the same GST-report result but is not what HQ writes on a bill save (no re-post, no User_* audit copy), so it is not offered as auto. Question: 'These 13 MBM purchase bills from registered suppliers were saved as unregistered; may we re-save them as registered so the input credit appears?'

Detect: `parties.checks.json` → `P-CASH-GSTIN-UNREG`.

### P-CASH-GSTIN-NO-ACCOUNT — B2B bills to a GSTIN that has no party account
*info; MBM 3, MP 0; fix: **owner***

**Why it matters.** A registered party billed in cash mode with a GSTIN that no account carries: GST reports are right (they read the bill copy), but the party has no account, so nothing ties its bills together and its GSTIN is never validated.

**Root cause.** Occasional registered suppliers paid in cash without an account.

**Examples.**
* MBM: GST Purchases GSTPUR/266 2026-10-06 Rs 1610.00 party 'CALCUTTA TUBE CO' GSTIN 37ALEPS1787R1Z2
* MBM: GST Purchases GSTPUR/287 2026-10-26 Rs 4055.00 party 'CALCUTTA TUBE CO.,' GSTIN 37ALEPS1787R1Z2
* MBM: GST Purchases GSTPUR/289 2026-10-30 Rs 17226.00 party '(NEW) JC ENTERPRISES' GSTIN 37BYRPG4570P1ZJ

**Repair (owner).** Owner: create the account (normal new-account save) if the party recurs. Today only cash purchases appear (no cash sale to a GSTIN without an account).

Detect: `parties.checks.json` → `P-CASH-GSTIN-NO-ACCOUNT`.

### P-CASH-GSTIN-STALE — Bill copy of the party GSTIN differs from the account's current GSTIN
*info; MBM 36, MP 0; fix: **report***

**Why it matters.** The bill keeps the GSTIN that was on the account when it was saved; the account was corrected later. Credit bills report the account's (current) GSTIN, but cash-mode bills and sales orders keep the old (often invalid) one.

**Root cause.** The bill keeps a copy of the party's GSTIN; the account was corrected afterwards.

**Examples.**
* MBM: Sales - Orders SO/7 2026-04-07 Rs 200.00 #10636 SPARKLE STEELS PROPFILES Pvt Ltd: bill GSTIN 37AARCS2879R1ZO vs account 37AARCS2879R1Z0 (cash mode: GST reports use the bill copy)
* MBM: Sales - Orders SO/8 2026-04-07 Rs 630.00 #10636 SPARKLE STEELS PROPFILES Pvt Ltd: bill GSTIN 37AARCS2879R1ZO vs account 37AARCS2879R1Z0 (cash mode: GST reports use the bill copy)
* MBM: Sales - Orders SO/25 2026-04-17 Rs 2000.00 #10636 SPARKLE STEELS PROPFILES Pvt Ltd: bill GSTIN 37AARCS2879R1ZO vs account 37AARCS2879R1Z0 (cash mode: GST reports use the bill copy)
* MBM: GST Purchases GSTPUR/65 2026-04-29 Rs 25800.00 #11044 J C ENTERPRISES: bill GSTIN 37AGZPP1329EZ0 vs account 37AGZPP1329E1Z0
* MBM: Sales - Orders SO/59 2026-05-06 Rs 240.00 #10636 SPARKLE STEELS PROPFILES Pvt Ltd: bill GSTIN 37AARCS2879R1ZO vs account 37AARCS2879R1Z0 (cash mode: GST reports use the bill copy)

**Repair (report).** Report. Only cash-mode GST bills matter for returns (mostly sales orders here, which are not reported).

Detect: `parties.checks.json` → `P-CASH-GSTIN-STALE`.

### P-CASH-MODE-PARTY — Parties whose default bill mode is Cash (bills to them bypass their ledger)
*info; MBM 43, MP 1; fix: **owner***

**Why it matters.** Act_BillMode 0 (Cash) makes HQ's bill screen default to cash for the party: the bill goes to 'Cash on hand' and only the IQCash_TRAN copy names the party, so the party ledger and outstanding never show those sales. 798 of 952 MBM parties (759 of them created before 2024) and 37 of 74 MP parties have bill mode Cash; the form's default for new accounts is Credit.

**Root cause.** Carried-forward accounts have Act_BillMode 0 (Cash).

**Examples.**
* MBM: #10096 VENKATA SAI GENERAL STORES [Supplier] bill mode Cash; 24 cash-mode GST bills linked to it (Rs 172179.00)
* MBM: #10261 Sri mounika bharathi agencises [Supplier] bill mode Cash; 7 cash-mode GST bills linked to it (Rs 49991.00)
* MBM: #10722 SIGMA SEA FOODS [Customer] bill mode Cash; 6 cash-mode GST bills linked to it (Rs 43102.00)
* MBM: #10748 M J P A P B C W R SCHOOL [Customer] bill mode Cash; 5 cash-mode GST bills linked to it (Rs 40284.00)
* MBM: #10144 LAKSHMI MACHINERY STORES [Supplier] bill mode Cash; 3 cash-mode GST bills linked to it (Rs 22116.00)
* MP: #10215 VENUS CHEMICALS [Supplier] bill mode Cash; 1 cash-mode GST bills linked to it (Rs 5960.00)

**Repair (owner).** Only parties that actually received cash-mode GST bills are listed (the other ~755 Cash-mode parties received no cash-mode GST bill this year). Switching the default to Credit is an HQ-identical re-save (cmbBillmode=Credit -> Act_BillMode 1 and IQAccAdr.Act_Mode 1) but changes how future bills post, so it is the owner's call; past bills are not touched. Question: 'Should <party> be billed on credit by default (its bills then appear in its ledger), or stay cash?'

`fixSql`: the HQ re-save script of §3 with the override below.
```sql
UPDATE @a SET Act_BillMode = 1;  -- Credit (owner-approved)
```

Detect: `parties.checks.json` → `P-CASH-MODE-PARTY`.

### P-PLACEHOLDER — Walk-in placeholder '=' carries a balance or postings
*warning; MBM 2, MP 0; fix: **owner***

**Why it matters.** '=' is only a name holder for walk-in estimates; any ledger balance on it is money owed by unknown people and any voucher posted to it belongs to a real party.

**Root cause.** Unpaid walk-in estimates carried forward as an opening balance on '=', and a credit note keyed to it.

**Examples.**
* MBM: #10502 '=' [Customer] bal 67879.00 Dr, ledger rows 1, last 2026-09-30, bills 1, open items 0 (0.00)
* MBM: voucher CRE/1 2026-09-30 Rs 1948.00 posted to '=' (SPECIAL DISC ISSUED, contra #10970)

**Repair (owner).** MBM: '=' has an opening balance of 69,827 Dr carried from last year and credit note CRE/1 (30 Sep 2026, 1,948 'SPECIAL DISC ISSUED', contra #10970 K B K BIOTECH) posted to it. Question: 'Who owes the 69,827 on the walk-in placeholder - write it off, or move it to named customers? Was CRE/1 meant for K B K BIOTECH?'

Detect: `parties.checks.json` → `P-PLACEHOLDER`.

### P-NO-ADDRESS — Party without an address row (IQAccAdr)
*warning; MBM 133, MP 0; fix: **auto***

**Why it matters.** Accounts imported in the Dec-2023 carry-forward without IQAccAdr have no GST type, GSTIN, state, town or bill-mode bit; reports fall back to blanks/AP, and the MK engine refuses to edit them (HQ_ACCOUNT_NO_ADDRESS).

**Root cause.** Dec-2023 carry-forward created the master row only.

**Examples.**
* MBM: #10242 SRI PAPERS PVT Ltd-2013-2014 [Customer] bal 0.00, ledger rows 0, last -, bills 0, open items 0 (0.00)
* MBM: #10319 GUNA MARKETING-2016 [Supplier] bal 0.00, ledger rows 0, last -, bills 0, open items 0 (0.00)
* MBM: #10472 INVENTA CHEMICALS-2016 [Customer] bal 0.00, ledger rows 0, last -, bills 0, open items 0 (0.00)
* MBM: #10659 Mattey srinu-2018 [Customer] bal 0.00, ledger rows 0, last -, bills 0, open items 0 (0.00)
* MBM: #10775 RAMESH KUMAR JAIN-2019 [Customer] bal 0.00, ledger rows 0, last -, bills 0, open items 0 (0.00)

**Repair (auto).** Auto: plain HQ re-save. EditAccount LEFT JOINs the address, so FrmAccounts opens these accounts with blank boxes, state '37-Andhra Pradesh' and GST type Un-Registered, and Save inserts exactly that row (blanks ' ', PAN/TAN/GSTIN '', Act_Rate 1, Act_Mode = bill mode<>0, ship state AP, FoodLICExpDate today), plus 10 contact rows and the zero opening row. Tested on #10242. The engine needs one change to do this: AccountMasterService must treat a missing address as those defaults instead of refusing. Nearly all are dormant (1 of 133 has ledger rows), so deactivating them (P-DORMANT) is the alternative.

`fixSql`: the HQ re-save script of §3 with the override below.
```sql
-- plain re-save, no field changes
```

Detect: `parties.checks.json` → `P-NO-ADDRESS`.

### P-NOT-FORM-SAVED — Party never saved through HQ's account screen (no 10 contact rows / no opening row / blank Act_TT)
*info; MBM 617, MP 34; fix: **auto***

**Why it matters.** HQ's Save always writes 10 IQAccOthers rows, one opening row (even 0) and Act_TT=type; accounts created by the year carry-forward lack them. Harmless to HQ's reports, but it makes these rows look foreign to any parity check and the MK engine's account verification (10 contact rows) fails on them.

**Root cause.** Carry-forward/import path; never opened and saved in FrmAccounts.

**Examples.**
* MBM: #10087 PSG  INDUSTREAL INSTITUTE: 0 contact rows; no opening row; Act_TT blank;
* MBM: #10091 STAR PLASTICS: 0 contact rows; no opening row; Act_TT blank;
* MBM: #10097 GANESH PAINTS&GENERALS: 0 contact rows; no opening row; Act_TT blank;
* MBM: #10099 STHYA AGENCIES: 0 contact rows; no opening row; Act_TT blank;
* MBM: #10100 SREE DEVI ENTERPISES: 0 contact rows; no opening row; Act_TT blank;
* MP: #10121 SUPPLIER-1: 0 contact rows; no opening row; Act_TT blank;
* MP: #10122 CUSTOMER-1: 0 contact rows; no opening row; Act_TT blank;
* MP: #10123 SAI SIRI: 0 contact rows; no opening row; Act_TT blank;

**Repair (auto).** Auto, lowest priority: a plain re-save writes them; better done only when the account is touched for another reason (every re-save also rewrites account 5 and renews Ent_Date). Accounts with a non-zero Act_OPbal but no opening row are in P-OPEN-MASTER instead.

`fixSql`: the HQ re-save script of §3 with the override below.
```sql
-- plain re-save, no field changes
```

Detect: `parties.checks.json` → `P-NOT-FORM-SAVED`.

### P-OPEN-MASTER — Opening balance in the master differs from the opening ledger rows
*error; MBM 4, MP 0; fix: **owner***

**Why it matters.** HQ's balances, ledgers and the account screen read the opening from the IQDayBook Doc_Id=-1 rows; IQAccmst.Act_OPbal/Act_OpSign is only a copy. Where they differ, the account screen shows the ledger figure and the next save silently overwrites the master figure, so one of the two numbers is wrong and nobody sees it.

**Root cause.** Opening figure typed into the master by the carry-forward without the IQDayBook opening row.

**Examples.**
* MBM: #10428 MODERAN ELECTRIAL CENTER [Supplier]: master 49143.00 C vs opening rows 0.00 (0 rows)
* MBM: #10788 Shri srinivasa polymers [Supplier]: master 52000.00 C vs opening rows 0.00 (0 rows)
* MBM: #10828 HIRANYAMAYI PLASTICS-2018-2019-2020 [Supplier]: master 49200.00 C vs opening rows 0.00 (0 rows)
* MBM: #11280 Hiranyamayi plastics [Supplier]: master 49200.00 D vs opening rows 0.00 (0 rows)

**Repair (owner).** Owner decides which figure is true. If the ledger rows (0) are right: plain re-save (HQ writes Act_OPbal=0 and a 0 row). If the master figure is right: re-save with that opening, which inserts the opening row and moves the opening-difference account 5 by the same amount. Note HIRANYAMAYI PLASTICS-2018-2019-2020 (#10828, 49,200 C) and Hiranyamayi plastics (#11280, 49,200 D) look like a transfer that was keyed into the masters only. Question: 'Do MODERAN ELECTRIAL CENTER (49,143 Cr), Shri srinivasa polymers (52,000 Cr) and the two Hiranyamayi accounts really have these opening balances for 2026-27?'

`fixSql`: the HQ re-save script of §3 with the override below.
```sql
-- plain re-save, no field changes
```

Detect: `parties.checks.json` → `P-OPEN-MASTER`.

### P-OPEN-DIFF-ACCOUNT — Opening Balance Difference account (5) out of step with the opening rows
*warning; MBM 0, MP 0; fix: **auto***

**Why it matters.** HQ rewrites account 5 (master + one opDif row) on every account save so that all opening rows balance; a stale account 5 makes the opening trial balance not tally.

**Root cause.** -

**Repair (auto).** 0 findings today in both firms (the 8 Nov HQ save left it exact). Kept as a guard because every MK repair that touches an opening row must end with this rewrite.

`fixSql`: see JSON.
```sql
-- steps 9-12 of the re-save template only (UpdateOpeningBalanceDifferenceAccount):
/* ---------------- 9-12. account 5 "Opening Balance Difference": rewritten on every account save ---------------- */
DECLARE @sr bigint;
DECLARE @net numeric(18,2) = (SELECT ISNULL(SUM(CASE WHEN d.TYPE IN ('D','d') THEN d.AMOUNT ELSE -d.AMOUNT END),0) FROM dbo.IQDayBook d JOIN dbo.IQAccMst m ON d.Act_ID=m.Act_ID
  WHERE d.TYPE IN ('D','d','C','c') AND m.Act_ID<>5 AND d.Doc_ID=-1);
DECLARE @dAmt numeric(18,2) = ABS(@net), @dSide varchar(1) = CASE WHEN @net>0 THEN 'C' ELSE 'D' END;
UPDATE dbo.IQAccMst SET Act_Opbal=@dAmt, Act_opSign=@dSide WHERE Act_Id=5;
DELETE FROM dbo.IQDayBook WHERE Div_no=0 AND Act_Id=5;
EXEC dbo.IQdaybook_insert @sr_no=@sr OUTPUT, @trdate=DATEADD(day,-1,'2026-04-01'), @Act_id=5, @Amount=@dAmt, @type=@dSide, @Contra_id=0, @BL_Adjusted=0, @Adjusted=0, @Doc_ID=-1, @VType='opDif',
  @ARS=0, @ARS_Ord=0, @Div_no=0, @days_from=0, @days_to=0, @int_credit=0, @Tseries='', @Tno=0, @Textn='', @discount=0, @doc_type=0, @Narration='', @Bill_Qty=0,
  @ent_Uid=1, @adj_id=0, @lnno=0, @mDiscRow='', @MImportCollection_DtldID=0;
```

Detect: `parties.checks.json` → `P-OPEN-DIFF-ACCOUNT`.

### P-DUP-CODE — Duplicate account codes (HQ refuses to save either account)
*warning; MBM 38, MP 0; fix: **auto***

**Why it matters.** FrmAccounts' duplicate check counts other accounts with the same Act_Code, so both accounts of each pair can no longer be edited or deactivated in HQ ('Account code already exists'). The codes were truncated to 10 characters when the '-2018-2019-2020' copies were made.

**Root cause.** Year-copy accounts got codes truncated to 10 characters ('A20474-201').

**Examples.**
* MBM: code A20227-202: #10961 SOVEREIGN PIPES&ALLIED INDUSTRIES-2020 (keeps the code)
* MBM: code A20227-202: #11092 SOVEREIGN PIPES&ALLIED INDUSTRIES-2020-2021-2022 -> A12092
* MBM: code A20248-201: #10920 AADITHYA FOAMS TRADERS-2017-2018-2019-2020 (keeps the code)
* MBM: code A20248-201: #11091 AADITHYA FOAMS TRADERS-2017-2018-2019-2020-2021-2022 -> A12091
* MBM: code A20274-201: #10847 K CHANTI REDDY -2018-2019-2020 (keeps the code)

**Repair (auto).** Auto: the oldest account keeps the code; every other one is re-saved with HQ's own code formula for its id, 'A' + (1000 + Act_Id) (what the form proposes for new accounts, e.g. A12088 for #11088; verified unused). Same re-save path, so the name change propagation in IQaccmst_update is a no-op. All 19 pairs are year-copies with no activity this year (only #10565 ITAMAX INDUSTRIES -2017 carries a balance, 3,981 Cr), so P-DORMANT deactivation can be done in the same save.

`fixSql`: the HQ re-save script of §3 with the override below.
```sql
UPDATE @a SET Act_Code = 'A' + CAST(1000 + @id AS varchar(12)); IF EXISTS (SELECT 1 FROM dbo.IQAccMst WHERE Act_Code='A' + CAST(1000 + @id AS varchar(12)) AND Act_Id<>@id) RAISERROR('replacement code taken',16,1);
```

Detect: `parties.checks.json` → `P-DUP-CODE`.

### P-GROUP-TYPE — Customer/supplier filed under the wrong group
*warning; MBM 135, MP 1; fix: **auto***

**Why it matters.** HQ's balance sheet and trial balance group accounts by Rec_Higher/Act_MG, while outstanding reports select by Act_Type. Customers under FIXED ASSETS/Computers inflate fixed assets (SIGMA SEA FOODS alone 1.42 lakh Dr) and are missing from Sundry Debtors; suppliers under 'Profit and Loss Accounts' or 'CAPITAL AND RESERVES' distort P&L/capital.

**Root cause.** Wrong group picked (or kept from a renamed year-copy); outstanding reports still list them by type, so nobody noticed.

**Examples.**
* MBM: #10698 VIJAYAKRISHANA ENTERPRISES [Supplier] under CAPITAL AND RESERVES -> SUNDRY CREDITORS [Supplier] bal 0.00, ledger rows 0, last -, bills 0, open items 0 (0.00)
* MBM: #11000 LAKSHMI ENTERPRISES [Supplier] under CAPITAL AND RESERVES -> SUNDRY CREDITORS [Supplier] bal 17700.00 Cr, ledger rows 1, last 2026-05-22, bills 1, open items 0 (0.00)
* MBM: #11041 RAJENDRA ELECTRICALS [Supplier] under CAPITAL AND RESERVES -> SUNDRY CREDITORS [Supplier] bal 1399.00 Dr, ledger rows 0, last -, bills 0, open items 0 (0.00)
* MBM: #10842 E M C Ltd-2018-2019-2020 [Customer] under Computers -> SUNDRY DEBTORS [Customer] bal 0.00, ledger rows 0, last -, bills 0, open items 0 (0.00)
* MBM: #10843 Aruna constructions-2018-2019-2020 [Customer] under Computers -> SUNDRY DEBTORS [Customer] bal 0.00, ledger rows 0, last -, bills 0, open items 0 (0.00)
* MP: #10259 VENKATA SAI GENERAL STORES [Customer-Supplier] under LIABILITIES -> owner to choose [Customer-Supplier] bal 69560.00 Dr, ledger rows 0, last -, bills 0, open items 0 (0.00)

**Repair (auto).** Auto for Customer and Supplier: re-save with Rec_higher = SUNDRY DEBTORS (414) / SUNDRY CREDITORS (407), the group FrmAccounts itself selects for the type. IQaccmst_update recomputes Act_MG from the new parent and runs IQUpdateRecOrder (new Rec_Level/Rec_Index, Rec_Order after the group's last child, ~220 other rows shifted +1) - tested on #10722 and #10779. Posted bills are untouched; only the balance-sheet heading of the balance moves (prior-period printed balance sheets will differ, so tell the owner/accountant once). Customer-Supplier accounts (8 MBM, 1 MP) are owner: debtors or creditors.

`fixSql`: the HQ re-save script of §3 with the override below.
```sql
UPDATE x SET Rec_Higher = g.Act_Id FROM @a x JOIN dbo.IQAccmst g ON g.Act_Cat='G' AND g.Act_Name = CASE WHEN x.Act_Type='Customer' THEN 'SUNDRY DEBTORS' ELSE 'SUNDRY CREDITORS' END WHERE x.Act_Type IN ('Customer','Supplier');
```

Detect: `parties.checks.json` → `P-GROUP-TYPE`.

### P-TREE — Broken chart-of-accounts tree fields on a party
*warning; MBM 0, MP 0; fix: **auto***

**Why it matters.** NULL Rec_Level/Rec_Index/Rec_Order, an Act_MG different from the parent group's, a level not parent+1, or a parent that is not a group make the account vanish from or misplace it in the chart of accounts and group totals.

**Root cause.** -

**Repair (auto).** 0 parties today in both firms (the 28 NULL-tree rows per firm are HQ-generated GST tax ledgers and groups, not parties; HQ tolerates them). Repair = re-save into the right group, which re-runs IQUpdateRecOrder.

`fixSql`: the HQ re-save script of §3 with the override below.
```sql
UPDATE x SET Rec_Higher = g.Act_Id FROM @a x JOIN dbo.IQAccmst g ON g.Act_Cat='G' AND g.Act_Name = CASE WHEN x.Act_Type='Customer' THEN 'SUNDRY DEBTORS' ELSE 'SUNDRY CREDITORS' END WHERE x.Act_Type IN ('Customer','Supplier');
```

Detect: `parties.checks.json` → `P-TREE`.

### P-INACTIVE-BALANCE — Inactive party with a balance or open items
*warning; MBM 0, MP 0; fix: **report***

**Why it matters.** An account switched off (Act_Usage=1) disappears from pick lists while money is still owed.

**Root cause.** -

**Repair (report).** 0 today: no party is inactive in either firm. Guard for P-DORMANT deactivations: never deactivate an account with a balance.

Detect: `parties.checks.json` → `P-INACTIVE-BALANCE`.

### P-DORMANT — Dormant parties (no balance, no ledger rows, no bills, never referenced)
*info; MBM 695, MP 31; fix: **owner***

**Why it matters.** Hundreds of carried-forward accounts (incl. the '-2018-2019-2020' year copies) clutter every party pick list and make duplicates more likely; HQ cannot delete most of them cheaply but can switch them off.

**Root cause.** Every party since 2013 carried forward year after year.

**Examples.**
* MBM: #10087 PSG  INDUSTREAL INSTITUTE [Supplier] created 20231206
* MBM: #10097 GANESH PAINTS&GENERALS [Supplier] created 20231206
* MBM: #10099 STHYA AGENCIES [Supplier] created 20231206
* MBM: #10100 SREE DEVI ENTERPISES [Supplier] created 20231206
* MBM: #10105 SANTHI PLASTICS [Supplier] created 20231206
* MP: #10121 SUPPLIER-1 [Supplier] created 20220325
* MP: #10122 CUSTOMER-1 [Customer] created 20220325
* MP: #10123 SAI SIRI [Customer] created 20220325

**Repair (owner).** Owner: switch off (Active unticked = Act_Usage 1 via re-save; reversible) rather than delete. HQ's delete would also be legal for these (no referencing rows), but it is irreversible. Accounts with a duplicate code must get a new code in the same save (P-DUP-CODE). Question: 'May we switch off the 695 MBM and 31 MP parties that have had no transaction and no balance since the 2023 carry-forward? They stay in the database and can be switched on again.'

`fixSql`: the HQ re-save script of §3 with the override below.
```sql
UPDATE @a SET Act_Usage = 1;  -- Active unticked (owner-approved)
```

Detect: `parties.checks.json` → `P-DORMANT`.

### P-ORPHAN-REFS — Rows pointing at an account that no longer exists
*warning; MBM 48, MP 49; fix: **report***

**Why it matters.** Ledger rows, charges or contact rows referencing a deleted Act_Id: the ledger rows vanish from every report that joins IQAccmst (trial balance does not tally), and HQ would post new round-offs to the missing account again.

**Root cause.** Accounts deleted outside HQ's delete path (or template leftovers).

**Examples.**
* MBM: IQAccOthers: 10 contact rows of deleted account #387
* MBM: IQAccOthers: 10 contact rows of deleted account #398
* MBM: IQAccOthers: 10 contact rows of deleted account #423
* MBM: IQAccOthers: 10 contact rows of deleted account #633
* MBM: IQAccOthers: 10 contact rows of deleted account #673
* MP: IQDayBook: 58 rows on missing account #424 net -2.39 (used by charge 73 Round Off)
* MP: IQAccOthers: 10 contact rows of deleted account #387
* MP: IQAccOthers: 10 contact rows of deleted account #398

**Repair (report).** MP: 58 posted rows (Apr-Oct 2026, net -2.51) on account #424, which is the ledger of charge 73 'Round Off' but no longer exists - hand to the ledger audit (owner: recreate a 'Round Off' account and repoint charge 73 + the rows, or post a correction). The 470 IQAccOthers rows of 47 deleted low-numbered accounts and narration 92 are identical in both firms (template leftovers) and harmless; deleting them is safe but optional.

`fixSql`: see JSON.
```sql
-- only the contact-row part is mechanical (it is what HQ's own delete would have removed):
DELETE o FROM dbo.IQAccOthers o WHERE NOT EXISTS (SELECT 1 FROM dbo.IQAccmst a WHERE a.Act_Id=o.actID);
```

Detect: `parties.checks.json` → `P-ORPHAN-REFS`.

### P-BALANCE-SIDE — Customer in credit / supplier in debit
*info; MBM 37, MP 1; fix: **report***

**Why it matters.** A customer owing a negative amount (advance or overpayment) or a supplier we have overpaid is legal but often a receipt/payment posted to the wrong one of two duplicate accounts.

**Root cause.** Advances, overpayments, or receipts posted to the wrong one of two duplicates.

**Examples.**
* MBM: #10090 SURYA AGENIES [Supplier] bal 9335.00 Dr, ledger rows 7, last 2026-09-18, bills 2, open items 0 (0.00)
* MBM: #10093 Adhithya entrprises [Supplier] bal 6493.00 Dr, ledger rows 49, last 2026-11-07, bills 36, open items 14 (606285.00)
* MBM: #10101 SATHYA AGENCIESS [Supplier] bal 29638.00 Dr, ledger rows 2, last 2026-07-28, bills 1, open items 1 (38955.00)
* MBM: #10106 R J WIRE PRODUCTS [Supplier] bal 42397.00 Dr, ledger rows 0, last -, bills 0, open items 0 (0.00)
* MBM: #10139 S R V B S J B  M R COLLEGE [Customer] bal 21039.00 Cr, ledger rows 2, last 2026-07-18, bills 1, open items 0 (0.00)
* MP: #10217 SHRI SRINIVASA POLYMERS [Supplier] bal 37400.00 Dr, ledger rows 0, last -, bills 0, open items 0 (0.00)

**Repair (report).** Report; cross-check against P-SPLIT-BALANCE (e.g. B v lakshmi garu , 14,996 Cr next to B V LAKSHMI GARU 91,145 Dr).

Detect: `parties.checks.json` → `P-BALANCE-SIDE`.

## 5. Merging two parties (no HQ feature exists)

HQ has no account-merge, re-link or "transfer ledger" function (searched the decompiled client and every procedure; the
only `UPDATE IQDayBook SET Act_ID` paths are voucher edits and balance refreshers). HQ's delete refuses any account
with child accounts, non-zero ledger rows, documents, narrations, charges or vehicles (spec §4). So a merge can only
be one of:

* **Transfer entry (HQ-native, recommended for posted history).** A journal voucher today: Dr/Cr the loser's balance
  to the survivor, then switch the loser off (Active unticked = `Act_Usage 1`, via a re-save). History, bills and filed
  GST returns stay exactly as they are; the party statement shows one transfer line. Works for any date range.
* **Re-pointing (true merge).** In one transaction, for loser L → survivor S: `IQDayBook.Act_ID` and `Contra_ID`,
  `IQDoc_Header.Act_ID`/`HeadAct_ID`/`Act_ControlID`, `IQCash_TRAN.Act_ID` (keep the bill's typed name/GSTIN),
  `IQDOC_Recd`/`IQDOC_Delv.Act_Id`, `IQNrMast.Act_Id`, `Vehicles.Act_Id`, `IQCHARGES.Act_Id`; combine the two opening rows
  into S's one row (then the account-5 rewrite); `ADJUST` links are by SR_NO and survive; finally switch L off (not
  delete: HQ's own delete would still be refused only if something was missed, which is a useful check). Bills of
  credit parties are reported in GST returns under the account's GSTIN, so re-pointing a bill dated before
  1 Oct 2026 changes an already-filed GSTR-1 line → owner decision per bill range, and the User_* audit tables keep
  the old Act_Id.

The question for each cluster is in `P-DUP-NAME` / `P-SPLIT-BALANCE` below.

## 6. Questions for the owner (one list)

* **P-DUP-NAME** — Are <A> and <B> the same party? If yes, which name/GSTIN/address is right, and may we move all of <B>'s bills, receipts and opening balance (including bills before 1 Oct 2026) onto <A> and switch <B> off?
* **P-DUP-GSTIN** — Accounts <A> and <B> both carry GSTIN <g>. Same business? If not, which one has the wrong GSTIN?
* **P-SPLIT-BALANCE** — <A> shows <x> Dr and <B> shows <y> Cr. Are they the same party, and should the balances be combined (by a transfer entry today, or by moving all their bills)?
* **P-GST-LENGTH** — What is the correct GSTIN of <party>? We have <x>.
* **P-GST-CHECKDIGIT** — Is <suggestion> the GSTIN of <party>?
* **P-GST-OWN** — M G MARKETING (#10439) is saved with our own GSTIN 37AHXPB8768N1Z6 and an opening balance of 59,971 Cr. What is its real GSTIN?
* **P-CASH-KNOWN-PARTY** — Cash purchase GSTPUR/267 typed VENKATA SAI GENERAL STORES - should such bills be booked on the supplier account (credit) or stay cash?
* **P-CASH-GSTIN-UNREG** — These 13 MBM purchase bills from registered suppliers were saved as unregistered; may we re-save them as registered so the input credit appears?
* **P-CASH-MODE-PARTY** — Should <party> be billed on credit by default (its bills then appear in its ledger), or stay cash?
* **P-PLACEHOLDER** — Who owes the 69,827 on the walk-in placeholder - write it off, or move it to named customers? Was CRE/1 meant for K B K BIOTECH?
* **P-OPEN-MASTER** — Do MODERAN ELECTRIAL CENTER (49,143 Cr), Shri srinivasa polymers (52,000 Cr) and the two Hiranyamayi accounts really have these opening balances for 2026-27?
* **P-DORMANT** — May we switch off the 695 MBM and 31 MP parties that have had no transaction and no balance since the 2023 carry-forward? They stay in the database and can be switched on again.

## 7. Outside this area (passed on)

* MP: charge 73 `Round Off` points to account #424, which does not exist; 58 posted IQDayBook rows (Apr–Oct 2026) sit
  on it and fall out of every report that joins IQAccmst (`P-ORPHAN-REFS`). Belongs to the ledger/config audit.
* `Catalogue.PartiesAsync` counts opening balances twice (spec §7) — any party balance shown by the current engine is
  off by the opening amount; the figures in this report are computed from IQDayBook only, like HQ.
* Company master MP: `Mobile` holds the GSTIN (`37AKSPB4550E1Z7`) — cosmetic, company master, not a party.
