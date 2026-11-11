# Ledger and accounting integrity audit

Audited 11 Nov 2026 on fresh copies of live data: `MKHQ_AUDIT_MBM` (building materials), `MKHQ_AUDIT_MP` (paints) and `MKHQ_AUDIT_MAIN` (company master; FY rows only, nothing ledger-related). All queries ran read-only. Every repair below was run inside a rolled-back transaction, with the check re-run in that same transaction.
The machine-readable checks are in `ledger.checks.json` (21 checks). Each `detectSql` returns one row per finding (`Finding`, plus `Doc_Id`/`Act_Id` where useful). None starts with `WITH` or ends with `ORDER BY`. HQ file references point to `Context/HQ_Decompiled/src/ISP`.

## Summary

| id | finding | MBM | MP | fix |
|---|---|---:|---:|---|
| LED-ROUNDOFF-CONFIG | Round Off charge 73 is type **Less** (sets 23/24/26) | 3 | 0 | owner |
| LED-ROUNDOFF-2X | Bills off by 2 x round-off because of that setting (about Rs 241 in total) | 466 | 0 | owner, then repair script ready |
| LED-UNBALANCED | Bills unbalanced for other reasons | 6 | 0 | owner (re-save in HQ) |
| LED-NO-LEDGER | Sales with no ledger rows (CS/76, CS/826) | 2 | 0 | owner (re-save in HQ) |
| LED-ORPHAN-ROWS | Ledger rows whose header is missing | 0 | 0 | guard |
| LED-HEADER-MISMATCH | Row date/series/no/party/amount/cash-mode differs from header | 0 | 0 | guard |
| LED-CASH-AS-PARTY | Phone Pe / credit sales posted to "Cash on hand" | 8 | 0 | owner |
| ADJ-DEAD | ADJUST links where both rows are gone (left from previous years) | 759 | 85 | **auto** |
| ADJ-HALF | Payments linked to opening rows that HQ later deleted and re-created | 19 | 0 | owner, release script ready |
| ADJ-BAD-LINK | Duplicate, self, zero, cross-account or same-side links | 0 | 0 | guard |
| BL-COUNTER | BL_adjusted differs from the sum of its links | 0 | 0 | **auto** (guard) |
| BL-RANGE | BL_adjusted < 0 or > AMOUNT | 0 | 0 | owner (guard) |
| ADJ-BACKUP-STALE | Previous-year rows left in HQ's `adjusted` backup table | 16 | 6 | **auto** |
| OPEN-MASTER-NOROW | Master opening balance with no Doc_ID=-1 row | 4 | 0 | owner |
| OPEN-MASTER-DIFF | Opening rows differ from master (account 8 P&L, HQ's design) | 1 | 1 | report |
| OPEN-DIFF-ACCOUNT | Account 5 out of step with the opening rows | 0 | 0 | **auto** (guard) |
| OPEN-TB-DIFFERENCE | Opening trial balance difference parked in account 5 | 1 | 1 | report |
| CASH-NEGATIVE | Days with negative cash in hand | 0 | 0 | guard |
| CASH-BANK-RECON | Bank overdrawn in books, no contra entries, cash keeps growing | 2 | 2 | owner |
| MK-CORE-CHARGE | Charge master made by the MK prototype is now used on HQ bills | 1 | 1 | report |
| NO-SUPPLIER-PAYMENTS | Credit purchases but no payment vouchers this year | 0 | 1 | owner |

Not defects:
- `IQDayBook.Adjusted` is recomputed by HQ's outstanding reports.
- Zero-amount opening rows: HQ writes an opening row even when the balance is zero.
- Cash bills to a named party (HeadAct_ID differs from Act_ID 6): MBM 230, MP 8. The ledger only touches Cash, which is HQ's design.
- The "12 bills with inconsistent ledgers" reported earlier are now 6 unbalanced bills plus 2 bills with no ledger. Nothing else is inconsistent.

## How balance is measured

A document balances when Σ D − Σ C + Σ R − Σ P − (cash leg) = 0. P and R rows carry Cash (account 6) as their implicit contra.
- The cash leg is Bill_Amt for cash-mode sales and cash receipts (Tt 204, 506, 507).
- It is −Bill_Amt for cash-mode purchases and cash payments (Tt 206, 500, 501).
- It is 0 for everything else.

On that measure, all 395 vouchers and every MP document balance. In MBM, 472 bills do not:
- 466 are off by exactly 2 × |round-off|.
- 6 are not explained by round-off.

## Findings needing a decision

**LED-ROUNDOFF-CONFIG / -2X.** HQ posts each charge with its Add/Less rule (`FrmInwards.AccountPosting_GST`, FrmInwards.cs:39340-39450). The Round Off charge is set to Less, so a sale's +0.12 round-off is debited instead of credited. Re-saving a bill in HQ reproduces this, so it is HQ's own posting, not damage.
- Question: *"May we switch Round Off (73) to type Add in HQ's charge master, as MK Paints has it? Should past bills be corrected too? Bills before 1 Oct 2026 belong to closed periods."*
- Prepared repair, tested in MBM (466 to 0, BL check still clean): flip the side of the single round-off row. This is exactly what the Add-rule posting (`InvoiceLedger.Plan`, IsAdd) produces. It is only valid after the configuration change; otherwise the next HQ re-save puts the old side back. Start with Doc_Date ≥ 2026-10-01 (40 sales and 14 purchases).
- GST is not affected: returns use the item lines, and the party row equals Bill_Amt.

**LED-UNBALANCED (MBM, Jun-Aug 2026).**

| Bill | Doc | Off by | Cause |
|---|---|---:|---|
| CS/465 | 9270 | −16.78 | CGST Output posted at the full tax (33.56), SGST at half (16.78). Edited 3 times. |
| CS/476 | 9322 | +10.08 | Same pattern (21.61 / 10.80) |
| CS/477 | 9324 | −46.53 | Same pattern (93.06 / 46.53). Edited 5 times. |
| CS/479 | 9327 | 0.57 | Round-off (0.58) plus 0.01 of tax rounding. Edited 17 times. |
| CS/323, CS/326 | 8936, 8941 | −0.52, 0.19 | Round-off plus 0.02-0.05 between the ledger tax and the line tax |

Root cause: HQ's edit deletes the ledger on an autocommit connection (FrmInwards.cs:38809), inserts rows one at a time, then re-nets them (`SaveDaybookActwise_Summary`, :39531). None of this runs in one transaction.
Repair: open each bill in HQ and save it unchanged; HQ re-posts from the item grid. The MK engine can do the same re-post under HQ's lock. Only the ledger changes; the GST lines and filed returns do not.

**LED-NO-LEDGER.** Cash sales CS/76 (Doc 8404, Rs 575) and CS/826 (Doc 10143, Rs 900) were edited (ModifyCnt 2), have no audit rows and no ledger: the edit deleted the ledger and stopped.
- Repair: re-save both in HQ.
- CS/826 also lost its round-off charge row (lines 900.20, bill 900.00). Ask: *"Is CS/826 Rs 900.00 or 900.20?"*

**LED-CASH-AS-PARTY.** Five Phone Pe sales (CS/78, 376, 572, 621, 856; Rs 60,719) and three credit sales (CS/403, 620, 634) name **Cash on hand** as the receiving or party account. Ask: *"Which account received this money?"* Then set that account and re-save each bill in HQ.

**ADJ-HALF.** On 8 Jun 2026 at 10:50, HQ's "update opening balances from previous year" ran:
- It deleted every Doc_ID=-1 row (FrmNewFy.cs:846-847) and re-inserted them (ClsNewFY.cs:1437 for bill-wise accounts). The same happened to individual accounts on 11 Oct and 2 Nov.
- ADJUST was never touched. 19 FY26 payments and receipts, totalling Rs 2,60,583, still count as allocated against rows that no longer exist.
- The re-created opening rows showed as open again, and several were settled a second time. For example, SURYA AGENIES' Rs 26,836 opening is settled by both BP/2 and BP/61, and SAAI KUMAR VALVES has 4 payments (Rs 1,33,338) pointing at nothing.

Options, chosen per party:
- (a) **Release.** Apply the `DeleteBilltoBillAdjustment` effect on the surviving row; the payment becomes on-account and is re-allocated in HQ. The script is in the JSON fixNotes; tested 19 to 0, BL check clean.
- (b) **Re-point** the link to the current opening row. This follows HQ's own precedent, `Update Adjust set Rel_SR_NO=new` at FrmInwards.cs:37823. It is only possible where that row has room: accounts 11059, 10209, 10218, 10695.

Ask: *"Which bills did these payments really pay?"*

**OPEN-MASTER-NOROW (MBM).** These accounts have a master opening balance but no opening row:
- MODERAN ELECTRIAL CENTER 49,143 Cr
- Shri srinivasa polymers 52,000 Cr
- HIRANYAMAYI PLASTICS-2018-2019-2020 49,200 Cr
- Hiranyamayi plastics 49,200 Dr (the two Hiranyamayi accounts cancel)

HQ reads openings only from IQDayBook, so it shows 0 for these accounts. MK's `ReportService.Opening` uses the master figure, so the two systems disagree.
- Ask: *"Are these balances real?"* If they are not, re-save the accounts in HQ, which writes 0. If they are, enter the openings in HQ.
- Code follow-up (not done here): `ReportService.Opening` should ignore Act_OPbal, as HQ does.

**CASH-BANK-RECON / NO-SUPPLIER-PAYMENTS.**
- MBM: cash in hand is Rs 1.72 crore, KVB 890 is −Rs 1.12 crore, and there are no cash/bank contra vouchers this year.
- MP: cash is Rs 92 lakh, KVB is −Rs 3.98 lakh, and there are 59 credit purchases (Rs 6.0 lakh) with no payment voucher at all.
- These are bookkeeping questions for the owner or accountant. There is no data repair.

## Automatic repairs

Each repair below was tested on both firms: run the check, run the repair, run the check again, then check that BL_adjusted equals the sum of links for every row. All four ran together in one transaction with the full suite re-run: only the expected counts changed.

| id | repair | tested |
|---|---|---|
| ADJ-DEAD | `DELETE` ADJUST rows whose two SR_NOs are both missing. This is `DeleteBilltoBillAdjustment` with nothing left to decrement; no balance or report changes. | MBM 759→0, MP 85→0 |
| ADJ-BACKUP-STALE | `DELETE` `adjusted` rows with Sr_No below this year's first ledger row. This is the state HQ's successful saves leave. | MBM 16→0, MP 6→0 |
| BL-COUNTER | `BL_adjusted = SUM(ADJUST links)`, the invariant HQ maintains. Currently clean. | seeded 1→0 on both |
| OPEN-DIFF-ACCOUNT | Account 5 master and row = net of the opening rows (`UpdateOpeningBalanceDifferenceAccount`, FrmAccounts.cs:9746), updated in place. HQ would re-insert with a new SR_NO. Currently clean. | seeded 1→0 on both |

## Risks

- **MP SR_NO collision.** 42 of MP's dead links carry SR_NOs above the current IQDayBook identity (26861; the next one is 49253). When numbering reaches them, old settlements will silently attach to new rows. This is the strongest reason to run ADJ-DEAD.
- **In-progress HQ edits.** HQ releases and re-applies settlements in separate autocommit steps. ADJ-HALF, BL-COUNTER and `adjusted` can show transient states while an HQ user is editing. Run repairs under the HQ document lock and app lock, never during an HQ edit. Back up deleted rows in the MK journal, not in `adjusted`, which HQ's bill-wise dialog reads.
- **Past periods.** Everything in LED-UNBALANCED and ADJ-HALF, and 412 of the 466 round-off bills, is dated before 1 Oct 2026. Ledger-only corrections do not change GST lines, but they do change books for periods that may already be closed. Get the owner's agreement first.
- **Account master saves.** HQ's account-master save (FrmAccounts) and the next new-year run will recompute account 5 and opening rows. Repairs to opening rows must follow HQ's rules or they will be overwritten.
