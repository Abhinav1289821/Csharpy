# HQ configuration audit — charge heads, transaction types, tax heads, account mappings

Audited 11 Nov 2026 on fresh copies `MKHQ_AUDIT_MBM`, `MKHQ_AUDIT_MP`, `MKHQ_AUDIT_MAIN` (read-only; every repair
below was executed inside the rolled-back transaction of `tools\sql\q.ps1` and its detect query re-run).
Machine-readable checks: `config.checks.json` (21 checks, ids `CFG-01` … `CFG-21`; every `detectSql` is a plain
`SELECT` usable as `SELECT COUNT(*) FROM (<detectSql>) findings`).

Decompiled references are `ISP\<file>:<line>` under `C:\MK_WORKSPACE\Context\HQ_Decompiled\src\ISP`.

## Summary

| id | severity | fix | MBM | MP | finding |
|---|---|---|---:|---:|---|
| CFG-01 | high | auto | 1 | 0 | Round Off charge (73) is a *Less* type → every rounded bill posted on the wrong side |
| CFG-02 | high | auto | 54 | 0 | …bills dated 1 Oct 2026 or later with the wrong round-off side |
| CFG-03 | high | owner | 416 | 0 | …bills before 1 Oct 2026 with the wrong round-off side |
| CFG-04 | high | owner | 0 | 58 | Bill ledger rows posted to an account that does not exist (MP round-off → 424) |
| CFG-05 | high | owner | 4 | 6 | Charges in live charge sets point to missing accounts (MP Round Off, Cash Discount; Adjustment in both) |
| CFG-06 | low | report | 6 | 18 | Unused legacy charge heads point to missing/group accounts |
| CFG-07 | medium | auto | 3 | 1 | Charge sets/charges left by the old mk-core prototype |
| CFG-08 | low | report | 1 | 1 | IQCHRGSETS rows without a set master (set 0) |
| CFG-09 | low | report | 1 | 2 | Type asks for rounding but its charge set loads nothing (rounding silently off) |
| CFG-10 | medium | owner | 1 | 1 | Sales type does not round and its bills carry paise (MP GST Sales: 96 of 139) |
| CFG-11 | medium | owner | 2 | 2 | Bank Receipts/Payments default bank 387 does not exist |
| CFG-12 | low | report | 85 | 68 | VAT-era account columns of TTypes point to missing accounts (inert) |
| CFG-13 | low | auto | 3538 | 510 | Items missing Ttype_Acnt rows for Transfer In/Out (1769 / 255 items) |
| CFG-14 | high | auto | 0 | 0 | Ttype_Acnt account disagrees with the item's GST rate (guard) |
| CFG-15 | info | report | 17 | 2 | Ttype_Acnt rows of deleted items (238 / 30 rows; HQ leaves them) |
| CFG-16 | low | report | 0 | 0 | Ttype_Acnt rows for missing / non-item transaction types (guard) |
| CFG-17 | high | auto | 0 | 0 | Item GST rate fields / six tax-account ids inconsistent with VAT (guard) |
| CFG-18 | high | owner | 0 | 0 | GST rate account set incomplete / duplicated / unpostable (guard) |
| CFG-19 | low | owner | 64 | 64 | GST input/purchase heads typed *Income* (financial-statement grouping) |
| CFG-20 | low | report | 5 | 0 | Phone Pe sales received into *Cash on hand*; no default UPI bank |
| CFG-21 | info | report | 1 | 1 | IGST Sales is tax-exclusive while GST Sales is tax-inclusive |

Clean (guard checks returning 0 in both firms): all 36,460 / 5,520 Ttype_Acnt rows of live items match HQ's
`Set_GSTAccounts` rule exactly; all item VIP/VOP/CGST/SGST ids and CGST/SGST/IGST percentages match VAT; every GST
rate HQ offers (3, 5, 12, 18, 28, 0.1, 40, exempt) has exactly one active posting account per label; no Act_Name
duplicates among them; every label is in `SALE_TYPES`; Payment_Modes are identical and sane in both firms.

**Future-only vs re-posting.** Changing a charge type/account, a TType setting or an item's account mapping only
affects bills saved afterwards — plus any old bill an operator re-opens and re-saves in HQ, because the edit screen
reloads charges from `IQDoc_Chrgs` but takes Add/Less from the *current* charge type (proc `FillCharges_Trans`) and
re-posts the whole ledger. Already-posted bills change only through CFG-02/03/04, which edit `IQDayBook`.

---

## How HQ uses charge heads (basis for CFG-01…CFG-10)

* A bill's charge grid = `FillCharges_Trans(@Set_No = TTypes.vousetno)`: `IQCHRGSETMST ⋈ IQCHRGSETS ⋈ IQCHARGES ⋈
  IQCHRG_TYPES` ordered by `Grd_Indx`; `Add`/`Less` = `ChTp_Op` (1/0). Inner join on the master → a set without an
  `IQCHRGSETMST` row loads nothing (FrmInwards.cs:30699, proc text in DB).
* Rounding: when `TTypes.RoundedOffTo > 0` **and** the grid has rows, `CalcBillTotals` sums all charges except the last,
  rounds the total, and writes the signed difference into the **last** grid row (FrmInwards.cs:35143, 35228-35243). The
  last row's Add/Less does not change the bill amount — only its ledger side.
* Posting (`AccountPosting_GST`, FrmInwards.cs:39340-39456), value v, charge account A:
  inward (purchase) — Add: v>0 debit A, v<0 credit A; Less: v<0 debit, v>0 credit.
  outward (sale) — Add: v>0 credit A, v<0 debit; Less: v>0 debit, v<0 credit.
  The party/cash line always carries the rounded bill amount, so only *Add* balances a round-off. HQ's built-in
  charge type 9 "RoundOff" is also `ChTp_Op=0` and would be just as wrong.
* Posting only requires `Act_ID > 0` (39352/39397) — a deleted account still receives rows.

### CFG-01 / 02 / 03 — MBM Round Off is a *Less* charge (high)

MBM charge 73 "Round Off" has `Chrg_Type=6` (Less); it is the last charge of the purchase set 23 and sales set 24
(also job-card set 26). MP's charge 73 is type 5 (Add) and posts correctly.

Impact on posted bills (MBM, all FY 2026-27 so far):

| period | bills | Σ|round-off| | ledger gap (2×) | Round off a/c misstated (Dr) |
|---|---:|---:|---:|---:|
| before 1 Oct 2026 (GST purchases 197, IGST purchase 1, GST sales 218) | 416 | 106.38 | 212.76 | +0.16 |
| 1 Oct 2026 → (GST purchases 14, GST sales 40) | 54 | 14.24 | 28.48 | +5.08 |
| **total** | **470** | **120.62** | **241.24** | **+5.24** |

Every one of these bills is unbalanced in IQDayBook by exactly 2 × round-off (this is the bulk of the existing
"Unbalanced bill ledgers" check in `Support.cs`). The Round off account's own balance is only Rs 5.24 off because
purchase and sales errors largely cancel. GST returns are not affected (round-off is outside taxable value/tax).
The 37 purchase bills that carry the prototype's charge 136 (type Add) are posted correctly and are not counted.

* **CFG-01 (auto)** — set the charge type to Add (5): exactly the `IQCHARGES_insert` UPDATE that HQ's charge master
  issues (frmCharges.cs:1477; Ent_Date = today, Ent_Uid = 1, other columns unchanged). Future bills only.
  After this, mk-hq's parity rule for "Less" round-off (hq-ledger-quirks memory) no longer fires for MBM; the engine
  reads `ChTp_Op` live, so no code change is needed.
* **CFG-02 (auto, run after CFG-01)** — for the 54 bills from 1 Oct, flip the side of the round-off row
  (`D↔C`, `P↔R`) in IQDayBook. That is byte-for-byte the row HQ writes with an Add charge (same Act_ID, Contra_ID,
  amount, narration; nothing else on the bill changes because only round-off posts to that account). Verified: the
  54 October bills go from unbalanced to balanced, detect count 54 → 0.
* **CFG-03 (owner)** — same flip for the 416 bills before 1 Oct 2026. Question: *"HQ posted the round-off on 416
  bills (Apr–Sep 2026) on the wrong side — books off by Rs 212.76 in total, Round off account by Rs 0.16. GST is
  unaffected. Correct those closed months, or leave them and fix only from October?"*

### CFG-04 / 05 — charges posting to accounts that do not exist (high)

MP's Round Off (73) points to account **424**, which does not exist (the real `ROUND OFF` account is 10119, used only
by the prototype's charge 131). 58 MP purchase bills (16 Apr – 22 Oct 2026, net Rs −2.39) have their round-off line in
IQDayBook under 424, so those lines are invisible in every ledger and the trial balance.

Other live-set charges with phantom accounts (zero amounts so far, but one keystroke away from the same problem):
MP Cash Discount 108 → 613; Adjustment 111 → 10009 (both firms, purchase set 23); job-card set 26 charges
Engine Oil / Serviice Tax / Service Charge → 716/715/722 (both firms; the "Job Card Done" type is unused).
Root cause: the charge heads came from HQ's 2011-2016 template database; the accounts they referenced were never
created (or were deleted) in these firms, and neither the charge master nor bill entry validates the id.

* **CFG-05 (owner)** — owner picks the account per charge; then the charge-master UPDATE (template in fixSql).
  Proposed: MP 73 → 10119 ROUND OFF; MP 108 → 10116 DISCOUNT (or 10107 Discount A/c); 111 → an "Adjustment" account
  of the owner's choice, or remove 111 from set 23. Future bills only.
* **CFG-04 (owner)** — after CFG-05, repoint the 58 IQDayBook rows and the 199 IQDoc_Chrgs rows from 424 to 10119
  (what HQ would post if the charge had been right; IQDoc_Chrgs must change too because an HQ re-save reloads Act_ID
  from it). Most bills are before 1 Oct 2026 → owner. Question: *"Round-off on 58 MP purchase bills went to a deleted
  account no. 424. Move them to ROUND OFF (10119)?"*

### CFG-06 / 08 / 09 — inert charge-set leftovers (report)

* CFG-06: legacy heads not in any live set (PROJ.DISC, DLR.DISC, CASH DISC, PKGING, CST→group 406, add1-3, disc1-3,
  adj1-3, TRUNOVER DISC., and in MP also Freight 82 → 612 / Credit-Debit Note 110 → 611). Unreachable from bills.
* CFG-08: `IQCHRGSETS` has a set 0 (Freight) with no master row; FillCharges_Trans ignores it.
* CFG-09: MBM 247 / MP 245, 247 have `RoundedOffTo=1` but `vousetno=0`, so HQ never rounds them (needs grid rows).
  All three types are unused. The complementary guard (last charge of a rounding set is not a round-off head, which
  would dump the round-off into e.g. Freight) is part of the same query and returns nothing.

### CFG-07 — prototype debris (auto)

The TypeScript prototype created on 19 Oct 2026: MBM sets 27 "MK Core Verified sale…" and 28 "…purchase…", charges
135/136 "MK Core Round sale/purchase…"; MP set 27 and charge 131. No transaction type uses the sets. Charge 136 is on
37 MBM purchase bills (all saved 2–9 Nov 2026; 3 with BillEnteredSystem "MK-CORE", 34 "MK" — how those got set 28
was not traced) and charge 131 on 1 MP bill, so those charges stay. Repair deletes the unused sets (HQ's charge-set screen rule: refuse only if a
TType uses the set, then delete IQCHRGSETS + IQCHRGSETMST, frmChargeSets.cs:1712-1725) and charge 135 (proc
`Charges_Delete` rule: not in a set, not on a bill). Verified 3/1 → 0/0. No bill touched.

### CFG-10 — sales types that do not round (owner)

MP GST Sales (506) has `RoundedOffTo=0`: 96 of 139 bills this year end in paise. MBM IGST Sales (507) likewise
(1 bill). Question: *"Round MP sales bills to the rupee like MBM GST Sales?"* Change = `TTypes.RoundedOffTo=1.00`
(HQ's transaction-type screen). Future bills only; do CFG-05 first so MP's round-off has a real account.

## Transaction types (CFG-11, 12, 21)

* CFG-11 (owner): Bank Receipts 205 / Bank Payments 207 (`Trmode=1`) open with `Bank_naccno=387`, which does not
  exist (FrmFin_Vouchers.cs:3051, 3958 preselects it). Question: *"Which bank should bank vouchers default to?"*
  (MBM: KARUR VYSYABANK 890 – 10701 is the one used on bills; MP: 10210 THE KARUR YSYABANK or 10238 G PAY.)
* CFG-12 (report): VAT-era columns (Exempt/Gross*/Tax*/CashDisc/Freight/CrDrNote/Roundoff_ActID) and Bank/Sub
  defaults 387/587/404…675 copied from the template DB point to missing accounts. FrmInwards loads them
  (30232-30242) but nothing in GST posting reads them; Sub_naccno (sales-return adjustment) is likewise unused.
* CFG-21 (info): IGST Sales 507 `Inc_Tax=0` vs GST Sales 506 `Inc_Tax=1` in both firms — likely deliberate.
* Other oddities, no action: Transfer In 245 is `Firstor=0` (IGST) while Transfer Out is `Firstor=1`; MP types 221,
  234, 252 have a blank series `' '` with `SeriesType=0`; types 211, -1, -2, 266 are disabled (`Tt_Status=1`) but
  still get Ttype_Acnt rows because HQ's item grid query does not filter on status (expected).
  `Configs.SalesRndoffActID / SalesDiscActID / SalesCrNoteActID` are 0 in both firms (not used by FrmInwards).

## Item account mappings (CFG-13 … CFG-17)

Reference rule: docs/hq-spec-items.md §4 (`Set_GSTAccounts`, FrmItems.cs:12723-14417; grid rows from
`Fill_GridVAT_Accounts`, :10754). The checks rebuild the expected account for every item × TType from IQAccmst
(`Act_TT` label by rate, Firstor and purchase/sales side, highest Act_Id), so they keep working after rates or types
change.

* CFG-13 (auto): 1,769 MBM and 255 MP items lack rows for Transfer In (245) and Transfer Out (512 / 252) — items last
  saved before those types existed. HQ writes the rows on the next item save. Repair inserts exactly those rows
  (rate-derived account, Set_NO 0, Ent_Date today 00:00, Ent_Uid 1) and leaves existing rows alone. Verified
  3538/510 → 0/0; inserted values agree with recent HQ-written rows (e.g. MP 18% item, type 245 → 10124). Transfers
  are unused, so no bill depends on it.
* CFG-14 / CFG-17 (auto, guards): 0 findings. Sanity-tested by corrupting one Ttype_Acnt row and one item's VOP_ID /
  CGST inside the rolled-back transaction: both checks fired and their fixSql restored HQ's values.
* CFG-15 (report): 238 MBM / 30 MP rows for 17 / 2 deleted items — `Items_delete` never removes them; keep for parity.
* CFG-16: 0 rows for missing or non-item transaction types.

## GST rate accounts (CFG-18, 19)

* CFG-18 (guard): every label the rule needs exists once, active, posting-level, in both firms — including the newer
  0.1% (0.05% halves), 3% (1.5%), 40% (20%) sets HQ generated later. If it ever fires, regenerate through HQ.
* CFG-19 (owner, low): the 2017-era GST accounts are all `Act_Type='Income'` under *Un-Grouped Incomes* (MBM 10707)
  / *INCOMES* (MP 3) — input-tax credit and purchase heads sit on the income side, output tax is not a liability.
  Accounts HQ generated later use Expense (Purchase), Liability (Input/Output 20%/40%), Income (Sales). Bills and
  GST returns key on `Act_TT`, so only the P&L/balance sheet presentation is affected. Question for the accountant:
  *"Regroup GST input/output tax and purchase heads?"* Also: MBM FRIGHT CHARGES (10126) carries `Act_TT='Closing
  Stock'`, an odd report head for a freight expense.

## Payment modes and Configs (CFG-20)

Payment_Modes: same 8 modes in both firms, all active, sensible order. `Configs.DefBankActID_ForUPI=0`, so for
Phone Pe / G Pay sales the operator chooses the receiving account each time; 5 MBM Phone Pe sales (Apr–Sep 2026)
were received into *Cash on hand* (CFG-20, report → owner question whether they were really cash). Configs flags
that FirmProfile guards (separate audit DB, cost codes, rate write-back, global numbering) are all off; MP has NULL
in many newer Configs columns (never re-saved since those columns were added) — harmless, HQ treats NULL as 0.

## Company master (MKHQ_AUDIT_MAIN)

Two companies, FY rows through 2026-27 for both, product version 50.3.0.0. MP's *Mobile* field holds the GSTIN
string instead of a phone number, and MP has no bank/UPI details (MBM has bank details). Cosmetic (print header);
not included in the JSON because those checks run per firm database.

## Suggested order of automated repairs

1. CFG-07 (remove prototype sets/charge) — independent.
2. CFG-01 (Round Off → Add) then CFG-02 (flip October-onward round-off rows).
3. CFG-13 (missing Transfer rows); CFG-14/17 stay as guards.
4. Owner decisions: CFG-05 → CFG-04 (MP round-off account), CFG-03 (pre-October bills), CFG-10, CFG-11, CFG-19,
   CFG-20.
