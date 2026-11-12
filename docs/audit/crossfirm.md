# Cross-firm consistency and additional areas — audit

Status: 11 Nov 2026, read-only, run against fresh copies `MKHQ_AUDIT_MBM`, `MKHQ_AUDIT_MP`, `MKHQ_AUDIT_MAIN`
(company master). Every query and every repair was run through `tools\sql\q.ps1` (always rolled back).
Machine-readable checks: `crossfirm.checks.json` (32 checks, same shape as `ledger.checks.json`).

Conventions in the JSON:

* `database: "cross"` queries use `{MBM}`, `{MP}` and **`{MAIN}`** (the HQ company-master database; here
  `MKHQ_AUDIT_MAIN`). The engine must substitute `{MAIN}` as well — used by CF09, GST06, AU04, SEC01, SEC02.
* For `cross` checks `counts.mbm` = `counts.mp` = number of cross-firm rows/pairs.
* `both` queries are unqualified and run once per firm database.
* No detect query starts with `WITH`, none ends in `ORDER BY`; all were verified wrapped as
  `SELECT COUNT(*) FROM (<query>) findings`. The firm databases run at compatibility level 100
  (so no `STRING_AGG ... WITHIN GROUP`; plain `STRING_AGG`, `TRY_CAST`, `DATEFROMPARTS` work).
* Secrets: passwords, GSP credentials and SQL logins were only tested for existence/weakness; no value is
  printed by any query or in this file.

Overlaps with the sibling audits were **not** repeated as checks: config-account references to missing accounts
(→ CFG-06/CFG-12), dead ADJUST links (→ ADJ-DEAD), missing Ttype_Acnt rows (→ CFG-13), prototype charge heads
(→ CFG-07 / MK-CORE-CHARGE), round-off Less rule (→ LED-ROUNDOFF-*, CFG-01..03). Item name/HSN/GST checks within
one firm are left to `tools\item-cleanup\propose.py`.

---

## Summary

| id | area | sev | MBM | MP | fix |
|---|---|---|---|---|---|
| CF01 same product, different HSN/GST | crossfirm | medium | 16 pairs | | owner |
| CF02 same party, different GSTIN | crossfirm | high | 3 pairs | | owner |
| CF03 same GSTIN, different name spelling | crossfirm | low | 19 pairs | | owner |
| CF04 same GSTIN, different GST type/state/account type | crossfirm | medium | 3 pairs | | owner |
| CF05 charge heads differ (Round Off Less vs Add) | crossfirm | high | 3 heads | | owner (fixSql ready) |
| CF06 transaction types differ | crossfirm | medium | 7 | | owner |
| CF07 charge sets differ | crossfirm | low | 3 | | owner |
| CF08 Configs validation/print flags differ | crossfirm | medium | 9 | | owner |
| CF09 company master inconsistencies | crossfirm | medium | 5 | | owner |
| GST01 taxed lines without valid HSN | gst | high | 120 | 23 | owner + partial auto |
| GST02 GST bills with checksum-invalid party GSTIN | gst | high | 9 | 0 | owner |
| GST03 party state ≠ GSTIN state / blank | gst | medium | 11 | 0 | **auto** |
| GST04 GSTIN present but "Un-Registered" | gst | medium | 5 | 0 | owner |
| GST05 duplicate GSTIN in one firm | gst | medium | 12 | 0 | owner |
| GST06 party carries the firm's own GSTIN | gst | medium | 1 | 0 | owner |
| GST07 ITC on purchases without supplier GSTIN | gst | high | 84 | 1 | owner |
| GST08 missing sales invoice numbers | gst | medium | 10 | 4 | report |
| GST09 bill saved with a wrong series ("1/1") | gst | medium | 1 | 0 | owner |
| GST10 sales changed/deleted after GSTR-1 due | gst | high | 13 | 0 | report |
| GST11 duplicate supplier bill numbers | gst | medium | 2 | 1 | owner |
| GST12 GST bill without IQDOC_VAT row | gst | low | 2 | 0 | owner |
| GST13 invoice numbers out of date order | gst | low | 4 | 1 | report |
| AU01 prototype-created docs with 12h times | audit | medium | 62 | 1 | **auto** |
| AU03 deleted documents | audit | low | 34 | 9 | report |
| AU04 one shared admin login / test users | audit | medium | 13 rows | | owner |
| SEC01 plaintext, weak HQ passwords | other | high | 11 rows | | owner |
| SEC02 plaintext SQL sysadmin credentials | other | high | 3 rows | | owner |
| SEC03 GSP API credentials stored | other | medium | 1 | 1 | report |
| HL01 ledger rows to a missing account (MP round-off) | structure | high | 0 | 59 | **auto** |
| HL03 template-residue orphan rows (survey) | structure | low | 18 tables | 15 | report |
| HL05 parties without IQAccAdr row | structure | low | 133 | 0 | report |
| HL07 demo "Mobiles" HSN master rows used by items | structure | medium | 0 | 8 | owner, then auto |

Verified clean (no check needed): GST rate accounts (108 Act_TT pairs: same names/types in both firms),
SALE_TYPES identical, no IGST bill to a same-state party and no CGST/SGST bill to an other-state party (either
firm, sales and purchases), item master structure (one unit row per item, no duplicate codes or group names, pack
ids all 3, no inactive items, tax-account columns and CGST/SGST/IGST consistent with VAT%), no document lines or
ledger rows of missing headers, all documents inside FY 2026-27 (opening stock on 31 Mar), both firms' own GSTINs
valid and in state 37.

---

## Part A — cross-firm

### CF01 Same product, different HSN or GST rate
Matched by normalised item name (upper, no spaces/dots/dashes); item codes do **not** match across firms (6 equal
codes, all different products), so name is the only key. 16 pairs differ: 9 only by HSN length (`96034010` vs
`9603` brushes, `3922200` vs `3922`), 5 by heading — the water tanks are `39172390`/`39172110` (pipes) in MBM and
`7213` (iron bars) in MP, both wrong for plastic tanks (usually `3925 10 00`) — and 2 by rate (MBM paint enamels
878/879 at 0% with blank HSN, MP 18%).
Root cause: masters typed separately in each firm. **Owner:** pick the HSN/rate per pair; apply through the item
cleanup flow (HQ item save) in the wrong firm.

### CF02 Same party, different GSTIN — high
`VOLTA IMPEX` (MP `...F327` vs MBM `...F3Z7`), `DINESH TRADERS` (MP `37AEVVPJ2076H1Z`), `GODAVARI FINECHEM`
(MP 14 chars). In all three the MBM GSTIN passes the check digit and the MP one fails (the query prints
valid/INVALID for each side). **Owner:** confirm and correct in MP via the HQ account screen.

### CF03 Same GSTIN, different spelling (19)
E.g. `SIXTH ELEMENT ENINEERING`/`ENGINEERING`, `Mrgha thermo pack`/`MEGA THERMO PACK INDUSTRIS`,
`P SUDHANDRAVIJAYALAXMI`/`P SUDHACHANDRA VIJAYALAXMI`. MBM duplicates (KOYYALA ×3, UPRIGHT ×2, SVEYDI ×2) also
appear here. **Owner:** choose the legal name; rename through HQ (also renames IQNrMast).

### CF04 Same GSTIN, different GST type / state / account type (3)
`P SUDHA…` Unregistered (2) in MBM, Registered (0) in MP; `Kostal pharma` state blank in MBM (auto-fixed by
GST03); `VENKATA SAI GENERAL STORES` Supplier vs Customer-Supplier. **Owner.**

### CF05 Charge heads — high
| head | MBM | MP |
|---|---|---|
| 73 Round Off | type 6 **Less**, a/c 11134 Round off | type 5 **Add**, a/c **424 (missing)** |
| 82 Freight | Add, 10126 FRIGHT CHARGES | Add, 612 (missing) |
| 108 Cash Discount | Discount, 11132 | Discount, 613 (missing) |

Evidence that MP's type is the right one: 466 of 1,178 MBM GST bills are unbalanced by exactly 2× their
round-off (472 unbalanced in all); 0 of 199 MP bills are unbalanced. MP's account side is broken instead (HL01).
Repair (owner approval; HQ-equivalent to changing the head in HQ's Charges screen; tested in rollback):
`UPDATE {MBM}.dbo.IQCHARGES SET Chrg_Type=5 WHERE Chrg_Id=73 AND Chrg_Type=6;` — new bills only; old bills need
re-posting (ledger audit). **Question:** "Make Round Off an Add charge in both firms, as MK Paints has it?"

### CF06 Transaction types (7)
TT 211 GST Purchase only in MBM (legacy, status 1); TT 222 Opening Stock tax-inclusive 1/0; TT 245 Transfer In
charge set 24/0; TT 249 Sales Orders round 0/1; TT 506 GST Sales round **1/0** (MP sales bills are not rounded);
Transfer Out is TT 512 in MBM and 252 in MP. **Owner:** questions in the JSON; change via HQ's Transaction Types
screen; mk-hq must read these per firm.

### CF07 Charge sets (3)
Sales Set 24: MBM Freight + Cash Discount + Round Off, MP only Round Off. Sets 27/28 are the prototype's "MK Core
Verified" sets, numbered differently in each firm (see CFG-07). **Owner.**

### CF08 Configs flags (9 of 53 differing columns matter)
`DuplicateSuppNo` ("Check Duplicate Supplier Bill No.") 1/0, `ChkDuplicatItemBillEntry` 1/0,
`BillNo_Fixed_Length` 1/0, `ShowGSTIN_FINVOCHERS_PRINT` 1/0, `ShowOutStandingBills_Sales` 0/1, `ImportPurbill`,
`SalDCAutoPickup`, `TextPrintMode`, `ShowSPLRatesItemsCreation`. The other 43 are NULL in MP vs 0 in MBM
(options added after MP's row; HQ reads NULL as 0). MP already has a duplicate supplier bill (GST11).
**Owner:** recommended to switch both duplicate checks on in MP.

### CF09 Company master (MAIN)
* Both firms: the **PIN** field holds `PEDDAPURAM` — e-way bill/e-invoice need the 6-digit PIN.
* MP: the **Mobile** field holds MP's GSTIN.
* SYSPRM (HQ licence record): customer "MK Paints and Sanitary" registered with **MBM's GSTIN**.
* MBM: `Company Users` remembers FY 2021, which MBM never had (harmless; HQ's last-login memory).
* FY table: MBM 2023–2026, MP 2021–2026; only the 2026 databases exist on this server; firm data matches FY 2026.
* Firm databases have no own-GSTIN of their own: `VAT_Company` in both is HQ's "IQ Demo Company" residue.
**Owner:** real PIN and MP mobile, entered in HQ's Company screen; licence record → HQ support.

---

## Part B — additional areas

### GST readiness
* **GST01** 120 MBM / 23 MP taxed lines without a 4/6/8-digit HSN (Rs 40k tax in MBM). Only 4 MBM lines have an
  item that now carries a valid HSN; the partial auto step copies it (what HQ writes on re-save; tested 120→116).
  The rest waits for the item-cleanup HSN decisions. Filed months need an HSN-summary amendment.
* **GST02** 9 MBM GST purchases from suppliers whose GSTIN fails the check digit (VIMAL ELECTRICALS ×3, ALPHA
  POLYMERS, V M S MARKETING …): ITC will not match GSTR-2B.
* **GST03 (auto)** 11 MBM registered parties with blank state (6) or AP state for a Gujarat/Telangana/TN GSTIN
  (5); a 12th (VIMAL ELECTRICALS) is skipped because its GSTIN is malformed (GST02). Fix sets `Act_state` to the HQ caption (already used in the firm) for the GSTIN prefix — the value HQ's
  `Verify_GSTIN` selects; tested 11→0. No bill used the wrong tax type, so no posting changes.
* **GST04** 5 MBM parties with a GSTIN but type Un-Registered (HQ now refuses this combination).
* **GST05** 12 GSTINs shared by two or three MBM accounts (some genuine duplicates, some copy errors such as
  CROPX OVERSEAS / SENTINI COMTRADE).
* **GST06** MBM supplier `M G MARKETING` carries MBM's own GSTIN (no bills yet).
* **GST07 — high** 74 MBM GST purchases (input tax ≈ Rs 1.05 lakh) are booked to *Cash on hand* although the
  supplier bill numbers are GST invoices (e.g. `GST/2026-27/1305`), plus 10 from suppliers with no GSTIN; MP 1.
  This ITC cannot be matched with GSTR-2B. **Question:** "Who were these suppliers — re-enter against the supplier
  account (paid by cash)?"
* **GST08** Missing sales numbers: MBM CS/317–320, 401, 601, 602, 665, 748, 749 (only 3 have a delete record);
  MP GS/2–5. Report them as cancelled in GSTR-1 Table 13.
* **GST09** MBM sale 9800 saved as `1/1` (15 Sep, Rs 750) instead of a CS number.
* **GST10 — high** 12 MBM sales modified after the 11th of the following month (e.g. CS/315 Rs 1.62 lakh, CS/209
  Rs 72,845) and 1 deleted after it: check against filed GSTR-1, amend if needed. mk-hq should warn before
  saving into a closed month.
* **GST11** MP `VENUS CHEMICALS` bill 499 entered twice (GSTPUR/47 and /50, Rs 5,984 each) — MP has no duplicate
  check (CF08). MBM's two pairs are on Cash on hand.
* **GST12** MBM CS/76 and CS/826 have no `IQDOC_VAT` row (re-save in HQ). **GST13** 4 MBM / 1 MP invoice numbers
  dated later than the next number.

### Audit trail
* **AU01 (auto)** 63 documents were created by the MK prototype, not HQ: 7 untouched (BillEnteredSystem
  `MK-CORE`, `Bill_Time` like `7:16:48 PM`, ModifyCnt 2, no Modify_Date) and 55 MBM + 1 MP later re-saved in HQ
  whose `Ent_Time` is still 12-hour (their first User_IQDOC_Header copy shows the 12h Bill_Time; HQ-created
  documents after 24 Oct all have 24h Ent_Time). Fix converts Bill_Time/Ent_Time to HQ's `HH:mm:ss`; tested 62→0,
  1→0. Audit copies are left as evidence.
* **AU03** 34 MBM / 9 MP documents deleted (TaskType 1 copies). The User_IQ* "orphan" Doc_Ids in the survey are
  exactly these audit copies of deleted or re-numbered documents — expected, not corruption. Voucher copies
  (TT 204–209) carry `CID` NULL — HQ's own voucher screen behaviour.
* **AU04** Every document in both firms has `Ent_Uid = 1`: everyone works as admin, so nothing is attributable.
  MAIN has 10 users; 9 (iq, nag, nag1, Tst, test, T1, t2, T3, counter1) never saved a document; `User_Tasks` has
  permission rows for UIDs 13 and 40 that no longer exist. **Question:** per-person logins? which test users go?

### Security (existence only)
* **SEC01** HQ user passwords are stored in plain text in MAIN `[User Master]`; 7 of 10 (admin included) equal
  the user name; SYSPRM also holds a plaintext program login. HQ compares in plain text, so only a password
  change in HQ is possible.
* **SEC02** MAIN `SQLOPT` and both firms' `VAT_Company` store the SQL sysadmin login with a weak plaintext password.
* **SEC03** `GSTAPI_Config` (both firms) stores GSP/ASP passwords and a DSC password.

### Database health
* **Orphan survey** — every `Item_ID/Ic_Id/Act_ID/ActID/Contra_ID/Tax_ID/*_ActID/Doc_ID` column in every
  non-temp table (158 columns per firm) was checked against IQItems_Mast / IQAccmst / IQDoc_Header:
  * **HL01 (auto) — high:** MP charge 73 points at account 424, which does not exist; 58 IQDayBook round-off
    rows (16 Apr–22 Oct, net −2.51) and the charge rows post to nowhere. Repair re-points them to MP's single
    `ROUND OFF` account 10119 (HQ-created 2022, already used on 5 Nov) — exactly what HQ would have posted;
    guarded to do nothing unless exactly one such account exists; tested 59→0 (MBM unaffected). Same rows as
    CFG-04.
  * **HL03:** the rest is HQ demo/template residue with identical counts in both firms (IQAccbdg 900,
    IQAccOthers 470, job cards, vehicles, Tax_Rates, IQDoc_Delv_D, credit-card/denomination rows, kits, schemes).
    Report only: HQ never cleans these and nothing the shop uses reads them.
  * Ttype_Acnt rows of deleted items (238/30) — HQ behaviour, must stay (CFG-15). ADJUST dangling → ADJ-DEAD.
* **HL05** 133 MBM party accounts (Dec-2023 import of old-year parties, names ending `-2018` etc.) have no
  IQAccAdr row; one has a bill. Re-saving in HQ creates the row.
* **HL07** MP's HSN master contains 4 HQ demo rows "Mobiles"; 4 MP items picked them (3× `1233525`, e.g.
  "PACKING AND FORWORDING"; `986155` on "Asi acrylic wall putty 20 kgs", which the item-cleanup length check
  cannot catch). After the owner gives those items a real HSN, the guarded `DELETE` equals emptying HQ's HSN
  master grid and saving (`FrmHSNMaster.cs:932` deletes all rows, then re-inserts the grid).

---

## Auto repairs (all tested inside a rolled-back transaction; detect count after fix)

| check | MBM | MP | why it is HQ-identical |
|---|---|---|---|
| GST03 party state from GSTIN | 11→0 | 0 | value HQ's Verify_GSTIN sets; caption taken from the firm's own HQ rows |
| AU01 12h → 24h times | 62→0 | 1→0 | HQ's own `HH:mm:ss` format |
| HL01 MP round-off a/c 424 → 10119 | 0 | 59→0 | posting HQ makes when the head points at an existing account |
| GST01 partial (line HSN from item) | 120→116 | 23→23 | HQ copies item HSN into the line on save |
| HL07 delete demo HSN rows | – | guard holds (items still use them) | HQ's HSN master save |
| CF05 Round Off Less→Add (owner first) | 73 now type 5 | – | HQ Charges screen change |

## Owner questions (short list)
1. Round Off: make it an "Add" charge in MBM as in MP (CF05)? Should MP sales bills round to the rupee (CF06)?
2. Who were the 74 "Cash on hand" GST purchase suppliers — re-enter them for ITC (GST07)?
3. Confirm the MBM GSTINs for VOLTA IMPEX, DINESH TRADERS, GODAVARI FINECHEM so MP can be corrected (CF02).
4. Correct HSN/rate for the 16 cross-firm product pairs, esp. water tanks and the 0% MBM enamels (CF01).
5. Shop PIN codes and MP's mobile number for the company master (CF09).
6. Turn on duplicate supplier-bill and duplicate-item checks in MP (CF08); is VENUS CHEMICALS bill 499 a double entry (GST11)?
7. Separate HQ logins per person, remove test users, change the admin password (AU04, SEC01).
8. What was sale `1/1` of 15 Sep (GST09)? Are the 5 "Un-Registered" parties with GSTINs registered (GST04)?

## Note on concurrent activity
During the run two checks briefly returned inflated counts (GST12 246, CF07 34) that went back to 2 and 3 on
re-run; another session was evidently changing the AUDIT copies at the time. All counts above are from a clean
re-run.
