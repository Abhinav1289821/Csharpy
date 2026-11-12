# Transactions and inventory audit

Audited 11 Nov 2026 on fresh copies of live data: `MKHQ_AUDIT_MBM` (building materials), `MKHQ_AUDIT_MP` (paints) and `MKHQ_AUDIT_MAIN`. MAIN holds only the company master, users and the FY table, so it has no transactions to check. All queries ran read-only through `tools/sql/q.ps1`. Every repair below was run inside a rolled-back transaction, and the check was re-run in that same transaction.

The machine-readable checks are in `transactions.checks.json` (24 checks, `area: "transactions"`). Each `detectSql` returns one row per finding, with a `Finding` text and `Doc_Id`/`Item_ID` where they apply. None starts with `WITH` or contains a trailing `ORDER BY`, so each one can be wrapped as `SELECT COUNT(*) FROM (<detectSql>) findings`. HQ file references point to `Context/HQ_Decompiled/src/ISP`.

## Summary

| id | finding | MBM | MP | fix |
|---|---|---:|---:|---|
| TX01_HALF_SAVED_BILL | Sales with lines but no VAT, charges, ledger or audit rows (CS/76, CS/826) | 2 | 0 | owner |
| TX02_HALF_SAVED_ORDER | Estimates/orders with no charges or VAT row (20 SO + PO/1) | 21 | 0 | **auto** |
| TX03_LINES_CHANGED_AFTER_SAVE | Lines no longer add up to the saved total and ledger (5 purchases, CS/753, SO/273) | 7 | 0 | owner |
| TX04_DUPLICATE_LINE_NO_OPEN | Repeated line number on orders and on bills dated 1 Oct 2026 or later | 22 | 0 | **auto** |
| TX05_DUPLICATE_LINE_NO_POSTED | Repeated line number on bills dated before 1 Oct 2026 | 9 | 0 | owner, script ready |
| TX06_ORDER_SUPPLIED_TWICE | Order lines billed twice, or billed to another customer | 18 | 0 | owner |
| TX07_STOCK_DRIFT | `IQItems_Mast.Stk` differs from HQ's own stock formula | 76 | 4 | **auto** (HQ's menu command) |
| TX08_NEGATIVE_STOCK | Items with negative stock | 723 | 48 | owner |
| TX09_NEGATIVE_OPENING_STOCK | Opening-stock lines with negative quantity carried from last year | 749 | 47 | owner, script ready |
| TX10_OPENING_STOCK_IRREGULAR | Opening stock with an odd date, a duplicate item or a total mismatch | 0 | 0 | guard |
| TX11_BILL_TIME_12H | `Bill_Time` stored as "7:16:48 PM" (written by the MK-CORE prototype) | 7 | 0 | **auto** |
| TX12_RENUMBERED_BILL | Bill renumbered after saving, outside its series, or sharing a number | 4 | 1 | owner |
| TX13_NUMBER_GAPS | Missing numbers in a series, with the reason | 34 | 8 | report |
| TX14_SUPPLIER_DATE_DEFAULTED | Supplier invoice date left at the default (the day the bill was typed) | 246 | 58 | owner |
| TX15_DUE_BEFORE_BILL_DATE | Due date earlier than the bill date | 0 | 6 | report |
| TX16_ZERO_RATE_SALE_LINE | Sale lines at rate 0 | 4 | 1 | report |
| TX17_ORPHAN_PAYMENT_SPLIT | All-zero `IQCreditCard` row left by an abandoned bill | 1 | 0 | **auto** |
| TX18_PRIOR_YEAR_LEFTOVERS | `IQCreditCard` rows for last year's bills | 38 | 55 | report |
| TX19_DELETED_DOCUMENTS | Bills deleted in HQ (`TaskType=1`) | 34 | 9 | report |
| TX20_ORPHAN_DOCUMENT_ROWS | Detail or ledger rows whose header is gone | 0 | 0 | guard, **auto** |
| TX21_LINE_GST_ARITHMETIC | Line tax not equal to taxable x rate, or CGST+SGST not equal to the tax | 0 | 0 | guard |
| TX22_LINE_ITEM_REFERENCES | Line for a missing or inactive item, or an item without exactly one unit row | 0 | 0 | guard |
| TX23_DATE_OUT_OF_YEAR | Document outside 1 Apr 2026 to 31 Mar 2027, or in the future | 0 | 0 | guard |
| TX24_FINISHED_BILL_WITHOUT_AUDIT | Finished bill with no `User_IQDOC_Header` copy | 0 | 0 | guard |

Rolled-back repair tests, with the check count before and after the repair in the same transaction:

| check | MBM | MP |
|---|---|---|
| TX02 | 21 -> 0 | 0 -> 0 |
| TX04 | 22 -> 0 | 0 -> 0 |
| TX05 | 9 -> 0 | 0 -> 0 |
| TX07 | 76 -> 0 | 4 -> 0 |
| TX09 | 749 -> 0 | 47 -> 0 |
| TX11 | 7 -> 0 | 0 -> 0 |
| TX17 | 1 -> 0 | 0 -> 0 |
| TX20 | 0 -> 0 | 0 -> 0 (runs cleanly) |

After the TX02 repair, TX03 and TX24 stay clean. After the TX09 repair, TX10 stays clean.

Overlap with the ledger audit: TX01 covers the same two bills as `LED-NO-LEDGER`, seen from the document side. Last year's `ADJUST` links are `ADJ-DEAD` in `ledger.checks.json`, so they are not repeated here.

## How HQ saves a bill, and why it leaves half-saved documents

`FrmInwards` does not save a bill in one step:

1. When the cursor leaves the first valid grid row, `GrdItems_RowValidating` calls `Save_DocHeader()`. That writes the `IQDoc_Header` row and `IQCash_TRAN`, then the line (`Save_DocDelv`, `Save_DocRecd` or `Save_Orders`). Every later row is written the same way as the operator moves off it. This also happens when an already saved bill is reopened for editing.
2. Only F10/Save (`CmdSave_Click`) writes the rest:
   - the change-log copies (`SaveData_For_UserChanges_or_Tracking`, `User_IQ*`);
   - `IQDOC_VAT` and `IQDoc_Chrgs`;
   - `Update_BillAmt`;
   - for bills, `AccountPosting_GST` (`IQDayBook`) and the stock refresh.
3. Closing the window with valid rows forces a save (`Close_Button` calls `CmdSave_Click`). Closing with no valid rows deletes only `IQCash_Tran` and `IQDoc_Header`.

So a half-saved document can only come from HQ being killed, a power cut, or an exception after step 1. Every step runs on its own connection call, with no transaction around them. This one mechanism produces TX01, TX02 and TX03, and some of TX04 (rows inserted in the middle of the grid are autosaved with their neighbour's number).

## How HQ derives stock

`[UpdateClosing_Stock_B+NG] @mItem_ID` (identical in both firms) sets:

```
Stk = Σ (IQDoc_Recd qty+free, × IssueConv when Stockconv=1)
    − Σ (IQDoc_Delv qty+free, × IssueConv when Stockconv=1)
```

- It counts only lines that have a header and have `DcRecd_ID`/`DcDelv_ID` = 0. It ignores dates, `Doc_Cancel` and the ledger. Opening stock counts because it is `IQDoc_Recd` lines on Tt 222.
- It updates only items that still have at least one movement.
- It joins `IQItems_Units` by item only. Today every item has exactly one unit row, which TX22 guards.

HQ calls the procedure only for the items on the bill being saved, from `mdlGeneral.UpdateStock`. It does not run it in these cases, which is why Stk drifts (TX07):

- **Deletes.** `Outward_Delete` and `Inward_Delete` only set `Cost_Calculated=0`. Six MBM drifts match deleted bills exactly: item 548 is +12 from a deleted GSTPUR, item 2068 is +12, and items 125, 749 and 754 are off by the deleted sale quantity. MP items 53 and 322 are the same.
- **A line removed while editing.** The removed item is no longer on the bill, so it is not refreshed. Example: item 246 was taken off CS/759.
- **Opening stock.** HQ's new-year tool regenerated the OPSTK documents around early June: Doc_Id 8771–8801 sit among the June bills, although `Ent_Date` says 01/04/2026. OPSTK/23 was edited on 07/07/2026. Neither refreshed Stk. This explains the remaining ~55 MBM items, for example item 883 HINGES PCCOATED (213 stored, −27 by the formula) and item 1501 NO 3 JALLY (115 against −5).
- **Items whose every movement was deleted** (10 MBM, 1 MP) keep their last figure, because the procedure skips items with no movements.

The repair is HQ's own menu command, Inventory > "Update ClosingStock to Items List" (`Mainfrm.UpdateClosingStockToItemsListToolStripMenuItem_Click` → `mdlGeneral.UpdateStock(0, cn, AllItemsUpdate: true)`):

```sql
UPDATE dbo.IQItems_Mast SET Stk=0;
EXEC dbo.[UpdateClosing_Stock_B+NG] @mItem_ID=0;
```

It changes only the derived Stk column. Run it after any HQ delete and after any opening-stock change. The engine already refreshes removed items itself (`DocumentPlanBuilder.StockSteps(clearEmpty: true)` in `DocumentMutations`).

### Negative stock (TX08 and TX09)

After the recalculation, 723 MBM items and 48 MP items are negative. The causes in MBM:

| cause | items | total quantity |
|---|---:|---:|
| Negative opening stock and no purchase this year | 580 | −16,475 |
| Negative opening stock, with purchases | 62 | −3,514 |
| Sold but never received (no opening stock, no purchase) | 45 | −1,166 |
| Opening stock only, oversold | 30 | −360 |
| Sold more than opening stock + purchases | 12 | −278 |

MP has the same pattern: 32, 2, 4, 8 and 3 items.

The root cause is HQ's new-year carry-forward (`ClsNewFY.UpdateClosingStock`). It copies negative closing balances into the opening stock at rate 0: 749 MBM lines and 47 MP lines. This cannot be fixed automatically, because nobody knows the real shelf quantity. The owner decides between a physical count and zeroing the negative opening lines. Zeroing is the TX09 `fixSql`. It does not change the opening stock value, because those lines are at rate 0.

## Findings in detail

### TX01: half-saved sale bills (owner)

| bill | date | amount | party | rows present |
|---|---|---:|---|---|
| CS/76 | 25/04/2026 | 575 | sowkya recidency | header, `IQCash_TRAN`, 1 line |
| CS/826 | 18/10/2026 | 900 | y sathibabu | header, `IQCash_TRAN`, 1 line |

Neither bill has VAT, charges, ledger or audit rows. CS/826's line is Rs 900.20, so it was meant to round off by −0.20.

- The goods count as sold (stock went down), but the cash never reached the cash book.
- The surrounding bills show no re-entry of either sale.

Question for the owner: did these sales happen?
- **Yes:** re-post each bill through the engine's amend path with the stored line. That is HQ's edit path. It writes charges with the round-off, VAT, `IQDayBook`, the stock refresh and the audit copy.
- **No:** delete the bill with `Outward_Delete`, then refresh stock.

### TX02: half-saved orders (auto)

20 MBM estimates and PO/1 have a header, `IQCash_TRAN` and lines, but no `IQDoc_Chrgs` and no `IQDOC_VAT` row. Their lines still have `RecdOrDelv_ID = 0`, which HQ fills only on the final save: 19 of the 20 estimates, and PO/1.

The `fixSql` writes exactly what HQ's final save writes for an order, through HQ's procedures:
- `Bill_Amt` and `Product_Value` set to the sum of the lines;
- `RecdOrDelv_ID` set to the line's own id;
- the firm's charge rows with zero amounts, copied from the latest finished order of the same type: 82/108/73 in MBM, 73 in MP;
- the zero VAT summary row.

Orders have no ledger or stock effect. The repair deliberately writes no `User_IQ*` copy, because the change log rightly shows the order never finished. For a full HQ-identical re-save, use `DocumentMutations.AmendAsync` with the stored lines.

### TX03: lines edited after the last full save (owner)

| bill | date | lines now | saved total | what changed |
|---|---|---:|---:|---|
| GSTPUR/36 | 08/05/2026 | 8150.03 | 8149.79 | line rates gained 3–4 decimals (e.g. 30.08 → 30.084) after the audited save |
| GSTPUR/48 | 15/05/2026 | 8359.64 | 8359.60 | same |
| GSTPUR/129 | 07/07/2026 | 26705.50 | 26706.12 | same |
| GSTPUR/139 | 17/07/2026 | 11000.32 | 10999.37 | same |
| GSTPUR/152 | 31/07/2026 | 7199.89 | 7199.77 | same |
| CS/753 | 05/10/2026 | 6261.43 | 6250.05 | line 6 rate 38.00 → 38.14 (net 33.63 → 45.01); bill and ledger still Rs 6,250 |
| SO/273 (estimate) | 09/09/2026 | 56988.30 | 40412.99 | revision 2 has 57 lines; the header still holds revision 1's 32-line total |

For each bill, the last completed save is in `User_IQDOC_Delv`/`User_IQDOC_Recd`: its line total equals the header.

The owner chooses for each bill:
- **(a)** restore the lines from that save. This is the default suggestion for the purchases (the supplier invoice total is fixed) and for CS/753 (the bill was printed at Rs 6,250).
- **(b)** re-save with the new lines through the edit path. This changes the total, the round-off and the ledger.

Six of the seven are before 1 Oct 2026, or are an estimate whose amount the customer saw.

### TX04 and TX05: repeated line numbers

31 cases are spread across orders, sales and purchases. The items differ and the totals are correct. Each is a row inserted mid-grid that kept its neighbour's number.

This also matters to the engine. `DocumentPlanBuilder.Audit` copies a line with `WHERE Doc_Id=@id AND Ln_No=@ln`, so amending one of these bills would copy two rows into the change log.

- **Repair:** renumber 1..n in (Ln_No, line id) order, which is what an HQ re-save of the grid produces.
- **Auto (TX04):** orders, and bills dated 1 Oct 2026 or later.
- **Owner (TX05):** 8 posted bills from Jun–Sep 2026 (9 repeated numbers).

### TX06: order lines billed twice or to another customer (owner)

A sale line records its source in `IQDoc_Delv.OrdDelv_Id`.

| order | party | billed as |
|---|---|---|
| SO/135 | Kondabbu events, Rs 50,386 | all 68 lines in **both** CS/314 (Krishana garu (hotel), Rs 1,82,499) and CS/315 (Kondabbu events, Rs 1,61,660), both on 22/06/2026 |
| SO/220, 229, 245, 251 | Sparkle Steels | both CS/598 (K Ramarao garu, cash) and CS/629 (Sparkle Steels) |

In 8 more sale bills (CS/285, 424, 479, 496, 583, 746, 764, 774), a non-cash customer was billed from another non-cash customer's estimate. Some of these may be harmless (an estimate under a contractor's name, the bill under the house owner's).

Ask the owner whether each pair is two genuine sales. If one link is simply wrong, the repair is to clear `OrdDelv_Id` on that bill's lines, which needs the owner's approval because these are posted bills.

Two things are not flagged:
- Supplying more than the order quantity in a single sale (6 orders). The estimate gets edited at billing; that is normal shop practice.
- Orders whose sale was later deleted (135 line links in the audit copies). They correctly become pending again.

### TX11: 12-hour Bill_Time (auto)

Seven MBM documents written by the old TypeScript prototype (`BillEnteredSystem = 'MK-CORE'`: SO/392, 428, 433, 439 and GSTPUR/272, 273, 304) store `Bill_Time` as "7:16:48 PM". HQ always writes 24-hour "HH:mm:ss" here.

The repair converts `Bill_Time` in `IQDoc_Header` and `User_IQDOC_Header` to the same instant in 24-hour form. GSTPUR/272 is dated 19/08/2026, but the field is a formatting artefact of our own write, not bill content.

`Ent_Time` is left alone: HQ's own current build writes `Ent_Time` in 12-hour form on orders since 24/10/2026 and on purchases since 02/11/2026 (34 MBM and 1 MP HQ-written documents). That is HQ's own behaviour, not a defect.

These seven are the only prototype-written documents. Before trusting them further, diff them column by column against an HQ re-save, using the ReplayParityTests method.

### TX12: renumbered or repeated bill numbers (owner)

1. **Bill "1/1"** of 15/09/2026, Rs 750. It was saved as **CS/665** and renumbered on 09/10/2026 into series "1", which is not the type's series (CS). CS/665 is now a hole in the GSTR-1 series. Suggested repair: put it back to CS/665 through the edit path, which rewrites the header and `IQDayBook.Tseries/Tno` together.
2. **CS/326 with suffix 1** of 23/06/2026, Rs 620, Srilalitha Enterprises. Its history is A/1 → CS/469 → CS/470 → CS/326, and CS/326 of 22/06/2026 (Rs 90,998) already has that number. Ask the owner which number the customer received and which was filed.
3. **Low risk:** CS/750 (first CS/747, renumbered 6 minutes later; the next bill took 747, and 748–749 were skipped) and MP GS/140 (first GS/144, renumbered within seconds). Only confirm the customer copies.

### TX13: gaps in number series (report)

| series | numbers | reason |
|---|---|---|
| MBM sales | CS/317, 319, 320 | deleted |
| MBM sales | CS/318, 401, 601, 602, 748, 749 | no trace: the number was handed out to a bill closed with no valid line, or skipped while renumbering |
| MBM sales | CS/665 | renumbered (TX12) |
| MP sales | GS/2–5 | no trace |
| MBM purchases | 13 numbers | deleted |
| MBM SO/BRC/BP | 11 numbers | deleted |

The deleted orders and vouchers keep an audit copy but no deletion marker. `Orders_Delete` and voucher deletes do not set `TaskType`.

Nothing is repaired; HQ never reissues numbers. Give this list to the GST filer for the GSTR-1 document summary, together with TX19.

### TX14: supplier invoice date never entered (owner)

| firm | flagged | of | evidence |
|---|---:|---:|---|
| MBM | 246 | 293 purchases | supplier date = date the bill was typed, after the purchase date (all 246) |
| MP | 58 | 60 purchases | supplier date after the purchase date |

HQ's date picker defaults to the day the bill is typed. GSTR-2B and ITC matching use the supplier's invoice date.

Ask the owner whether to take the purchase date as the supplier date or supply the real dates. Either way, this rewrites posted purchases, so it needs the owner's approval. Going forward, the engine takes the date from the bill photo.

### TX15, TX16, TX17, TX18, TX19: smaller items

- **TX15 (report).** MP GS/126–131 were typed on 10/11/2026 for October with due date 01/10/2026. They are cash bills and their ledger rows have no due date. HQ calculates the due date from the party's credit days, so there is no single HQ-identical value to restore.
- **TX16 (report).** Sale lines at rate 0: MBM CS/323 (4 lines: reducer, solvent gum, nails, CPVC gum) and MP GS/22 (15 × ASI EN BUS GREEN 500ML). These may be free items or missed charges.
- **TX17 (auto).** `IQCreditCard` row 107 has Doc_Id 9119, dated 14/07/2026, all amounts zero, and no bill. It was written by the "Others" payment dialog for a bill that was then abandoned; `Close_Button` deletes only the header and `IQCash_Tran`. Doc_Id is an identity column and is never reissued, so the delete is safe. Only all-zero rows in this year's Doc_Id range are deleted.
- **TX18 (report).** `IQCreditCard` rows for last year's bills, copied by the new-year tool. Leave them.
- **TX19 (report).** Bills deleted with HQ's delete (`TaskType=1`): MBM 16 purchases and 18 sales, MP 6 purchases and 3 sales. Several were re-entered under a new number the same day, which is how this shop corrects bills. Each deleted sales number belongs in the GSTR-1 document summary. Each deletion left Stk stale (TX07).

### Checked and clean (guards)

- **Orphan rows:** none in `IQDoc_Delv`, `IQDoc_Recd`, `IQOrd_Detail`, `IQCash_TRAN`, `IQDOC_VAT`, `IQDoc_Chrgs` or `IQDayBook` (TX20).
- **Wrong line table:** no lines sit in the wrong table for their document type.
- **Duplicate rows:** no duplicate `IQCash_TRAN` or `IQDOC_VAT` rows.
- **Party snapshot:** every cash-mode bill has its `IQCash_TRAN` row.
- **Line GST arithmetic** (TX21): clean across about 12,400 MBM lines and 540 MP lines (opening stock excluded). The only exception is an HQ quirk: SO/184 line 14 has a negative value, and HQ stores the absolute value of its CGST/SGST split.
- **Items:** no line points to a missing or inactive item. Every item has exactly one `IQItems_Units` row: 2,485 MBM and 400 MP (TX22).
- **Dates:**
  - Nothing outside the financial year or in the future (TX23).
  - Opening stock is dated 31/03/2026, which is HQ's convention (TX10).
  - No time is stored inside `Doc_Date`.
  - Vouchers and opening stock have an empty `Bill_Time`; that is native HQ.
- **Links:** `ZDoc_Id`, `ProcRefID`, `JobCardRefID`, delivery-challan, indent and temp links are all 0. Purchase-order links (`OrdRecd_Id`) are unused. No sale points to a missing order line.
- **Numbers and audit copies:**
  - No two documents share the same series, number and suffix.
  - Every finished bill has its change-log copy (TX24).
  - `IQDayBook` series, number and date match their header for every document.

## Notes for the engine

1. **Interstate purchases.** HQ's two IGST purchases (MBM 8814, 9412) store `CGST_Tax = SGST_Tax = 0` and zero half-amounts. `DocumentPlanBuilder.LineStep` sets `mCGST_Tax`/`mSGST_Tax` to IGST/2 for every interstate kind. HQ's IGST sale 507 does store halves, so the half-rate rule matches HQ only for sales. Check this against ReplayParityTests before the next interstate purchase.
2. **Audit copies and repeated line numbers.** `DocumentPlanBuilder.Audit` selects a line by (Doc_Id, Ln_No). On TX04/TX05 bills it would copy two rows. Run the TX04 repair before amending such a bill, or key the copy on the line id.
3. **Product_Value on orders.** HQ leaves `Product_Value` at the value of the first saved row when more rows are added; `Bill_Amt` is correct. TX03 therefore compares orders on `Bill_Amt` only.
4. **Stock after HQ deletes.** HQ's deletes never refresh stock. Any tool that lists stock should either run the TX07 repair first, or compute stock with the TX07 formula instead of reading `Stk`.
