# HQ voucher modify: specification (receipts, payments, credit and debit notes)

Researched 9 Nov 2026 from the decompiled HQ build (`Context/HQ_Decompiled/src/ISP`) and read-only queries on ISTMBM26 / ISTMP26.
Scope: TT_ID 204 to 209 (`FrmFin_Vouchers`, opened from `frmFinanceMaintance`). Journal vouchers share the form but are out of scope.
File references are to `Context/HQ_Decompiled/src/ISP/`.

## 1. Verdict

HQ modifies a voucher **in place at the header and by delete-and-reinsert below it**:

- `IQDoc_Header`: same `Doc_Id`, updated by `Update_Voudetails` (every column the insert sets is overwritten, including `Ent_Date` and `Ent_Uid`).
- `IQDayBook` and `IQbnktran`: all rows of the document deleted and re-inserted through the same `IQdaybook_insert` / `IQbnktran_insert` calls as a new voucher, so `SR_NO` and `BnkTranID` are **new identities**.
- Bill-wise settlements (`ADJUST` + `BL_adjusted`) are released before the save, outside the save transaction, then re-applied from HQ's bill-wise dialog against the new `SR_NO`s.
- Audit tables are **append-only**: each save adds a new `User_IQDOC_Header` row plus its `User_IQDayBook` / `User_IQbnktran` rows. `ModifyCnt` stays **0** for vouchers.
- Nothing is written to `IQDoc_Header.Modify_Date`, `Ent_Time` or `ModifyCnt`; they stay NULL/0 for all 388 vouchers in ISTMBM26.

## 2. Opening for edit and the .HQS lock

`frmFinanceMaintance.cmdEdit_Click` (frmFinanceMaintance.cs:2981-3027):

1. `mdlGeneral.LockRecord(docId)` (mdlGeneral.cs:5421-5470). It deletes any existing `{ServerPath}\UserLocks\{Database}{DocId}.HQS`, then opens it with `FileOpen(..., OpenMode.Output, OpenAccess.Write, OpenShare.LockWrite)` and keeps the handle. Error 55/75 (the file is held by another HQ) returns 0, and HQ shows "Invoice is modifying by another User".
2. It opens `FrmFin_Vouchers` with `DocID`, `Tid`, `EditMode = true`, `EdFileId = handle`.
3. On save, close or cancel (`cmdSave_Click` finally at :4720-4726, `cmdClose_Click` :3021-3027, `FormClosing` :4131-4147), the form calls:
   - `KillFile(docId)`: deletes `{ServerPath}\UserLocks\{Database}DOC{docId}` (no extension). This is a different name from the lock, so in practice it does nothing.
   - `UnlockRecord(docId, EdFileId)` (mdlGeneral.cs:5398-5410): `FileClose(handle)`, then deletes `{Database}{DocId}.HQS`.

`MK.Hq.Engine/Concurrency/Locks.cs` (`HqDocumentLock`) already follows this convention. Holding the `.HQS` file for the length of the write is enough. HQ never checks the `DOC` file.

What the form loads (`SetDefaults` → `FillVochersGrd` → `Editdata`/`FillDocHeader`, :3933-3975, :3110-3138, :4037-4073):

- Header: `Act_id`, `SM_Id`, `Narration`, `Doc_date`, `TSeries`, `TNo` (also kept in `txtNo.Tag`), `mode`, `Branch_ID`. `ChangeSeriesandNo` is skipped in edit mode (:3065), so the number is kept.
- Grid: rows of `IQDayBook` for the document `WHERE VType<>'D' AND lnNo=0 ORDER BY sr_no`, which excludes the header row (`lnno=-1`) and discount rows. Rows are LEFT JOINed to `IQbnktran`. The grid columns used later are:
  - col 21 = old `Sr_No`
  - cols 14/15/16 = `ChqType`/`ChqDate`/`ChqNo`
  - cols 17/18 = `Status`/`HrDate`
  - cols 23/24/25 = `DepositDate`/`bname`/`bplace`
  - col 26 = `ImportCollection_DtldID`
  - For a cash-transfer row (contra 6, `vType='1'`), the CASE expressions swap Act/Contra so the grid shows the line account.
- In edit mode the user can change: date (limited to FY start through login date), number (`txtNo_Validating` :3874-3913 runs the duplicate check excluding its own `Doc_Id`), header account, salesman, narration, lines, amounts and bank details.

## 3. Save in edit mode: exact ordered statements

`cmdSave_Click` (FrmFin_Vouchers.cs:4594-4731). Validation (`IsEmptyFound`, `IsSameHeader_Line_Accounts`) runs first. Then:

### Step A: release settlements (only if `mBillToBIll == 1`). Runs on `mDataCn`, autocommit, **outside** the save transaction (:4628-4657)

```sql
SELECT * FROM IQDaybook WHERE Doc_ID = @doc ORDER BY Sr_No      -- IQTCBook if TTypes.SubType>0 (never for 204-209)
-- for each old row X, in that order:
INSERT INTO adjusted(sr_no, rel_sr_no, amount) SELECT Sr_No, Rel_Sr_No, Amount FROM ADJUST WHERE sr_no = X   -- backup
EXEC DeleteBilltoBillAdjustment @sr_nodelete = X
```

`DeleteBilltoBillAdjustment` (live proc, verbatim logic): a cursor runs over `SELECT rel_sr_no, amount FROM Adjust WHERE Sr_No=X` with no ORDER BY. For each row it runs:

- `UPDATE iQDaybook SET Bl_Adjusted = Bl_Adjusted - @Amount WHERE Sr_No = @Rel_Sr_No`
- `UPDATE iQDaybook SET Bl_Adjusted = Bl_Adjusted - @Amount WHERE Sr_No = X`

After the cursor it runs `DELETE FROM Adjust WHERE Sr_No = X`. `@Amount` is `float`.

Only links where the voucher row is the **source** (`ADJUST.Sr_No`) are released. In ISTMBM26 no voucher row is ever a `Rel_Sr_No` target (ADJUST grouped by source/target type: targets are only Tt 500/501/506 or opening-balance rows). The `else` branch at :4650 (`Delete from adjusted ...`) cannot run because `mEditMode` is true. Its SQL is also missing a closing parenthesis.

### Step B: `SaveDaybookData` (:4755-5038). New connection, one `SqlTransaction`

1. `EXEC Update_Voudetails`. The parameters are those of `Insert_Voudetails`, built by `ClsDaybook.Insert_VouchersHeader` (ClsDaybook.cs:88-141), with `@Doc_id` = the existing id (InputOutput):

   | param | value |
   |---|---|
   | @Tt_id | mTtid |
   | @TSeries | txtSeries.Trim() |
   | @Tno | txtNo (may be edited) |
   | @TExtn | `" "` (VarChar(1)) |
   | @Mode | TTypes.Trmode, re-read from header `mode` |
   | @Doc_Date, @Doc_Duedate | both = dtpDate.Date |
   | @Branch_ID | branch combo, or 0 |
   | @Salesman_Id | cmbSalesmen (loaded from header) |
   | @Doc_Type | 0 |
   | @Bill_Amt | LblNet.Tag (sum of the line amounts) |
   | @Act_id | header account |
   | @Ent_Date | **`DateAndTime.Today`**, the modify date at midnight |
   | @Ent_Uid | mUserId |
   | @Db_Save, @TcBook | 0, 0 |
   | @Narration | txtNarration.Trim() |
   | @mDH_VouchType | TTypes.Vouchtype |

   The proc runs `UPDATE IQDoc_header SET Tt_id, Tseries, Tno, Textn, Mode, Doc_date, Doc_Duedate, Branch_ID, SM_ID, Doc_Type, bill_Amt, Act_id, Ent_Date, Ent_Uid, Db_Save, TcBook, Narration, DH_VouchType WHERE Doc_id=@Doc_id`, inside its own nested BEGIN TRAN/COMMIT. No other header column is touched.
2. `DELETE FROM IQbnktran WHERE Doc_Id = @doc` (:4799)
3. `DELETE FROM IQDayBook WHERE Doc_Id = @doc` (:4800)
4. Re-insert **exactly as for a new voucher** (:4801-4977). This is the same code path `VoucherLedger.Plan` and `VoucherService.Body` already reproduce:
   - For each valid grid row, in grid order: one `IQdaybook_insert`. If `Trmode==1`, an `IQbnktran_insert` follows immediately for that row's new `sr_no`.
   - Then the header row (`lnno=-1`), when header ≠ cash or the type is CN/DN.
   - Then the discount rows (VType `'D'`, only R/P with `sub_Naccno>0` and discount>0; never used in data).

   Two edit-only differences in the column sources:
   - `IQdaybook_insert @MImportCollection_DtldID` for line rows = grid col 26, so the old row's value is carried. The header row passes 0.
   - `IQbnktran_insert @mStatus`/`@mHRdate` = grid cols 17/18, so the old bank-reconciliation status and date are **carried over**; a new voucher passes 0. ChqType/chqno/chqdate/bname/BPlace/DepositDate come from the grid as edited. `bname` = header account name for Payments.

   All `IQDayBook.Ent_Date` values default to `getdate()` at reinsert time.
5. `COMMIT`. SMS and WhatsApp prompts may follow, with no voucher-table writes.

If `SaveDaybookData` returns 0 (error, rollback), cmdSave returns. **Step A is not undone.** The old rows survive with `BL_adjusted` reduced, their links deleted, and backups left in `adjusted`.

### Step C: `BillToBillAdjustment()` (:5246-5291). `mDataCn`, autocommit, only if `mBillToBIll == 1`

```sql
SELECT * FROM IQdaybook WHERE Doc_ID=@doc AND (ISNULL(Vtype,'')<>'D' OR Vtype IS NULL) ORDER BY sr_no
-- for each new row i (0-based) with new sr_no N:
EXEC GetBillToBillAdjustment @m_sr_no=N, @err OUTPUT, @szcond=0, @smode=...
--   @err=1 only if the account has Bill2Bill=1 AND ((Customer|Customer-Supplier) with C/R  OR  Supplier with D/P)
--   AND the account has open opposite-side items.
--   If @err=1: modal FrmBill2BillAdj(Sr_no=N, Old_srno=GrdVouchers.Rows[i].Cells[21])  (see §4)
DELETE FROM adjusted WHERE sr_no = <GrdVouchers.Rows[i].Cells[21]>    -- runs whether or not the dialog opened
```

Row `i` of the new ledger is paired with grid row `i` by position. This works because line rows are inserted in grid order before the header row. The header row (last) pairs with the grid's empty new-row (AllowUserToAddRows), whose col 21 is null, so `Old_srno = 0`. See §4.3.

### Step D: `SaveData_For_UserTracking()` (:5601-5646). `mDataCn`, autocommit

- `User_Insert_Voudetails` through `ClsDaybook.User_Insert_VouchersHeader` (ClsDaybook.cs:286-335). It takes the same values as step B1, with `Ent_Date = Today`. It passes **no `@mModifyCnt`**, so `ModifyCnt` gets the proc default 0. `TaskType` gets the column default 0.
- `User_SaveDaybookData()` (:5326-5444): the same `User_IQDayBook` rows as on create (`sr_no=0`, `lnno` = counter 0/1, `DiscRow 'D'` on counter 1, `VType` '1' for cash else '0'). A `User_IQbnktran` row (`BnkTranID 0`, `Sr_no 0`) is written for counter 0 when `chqno` is non-blank.
- Earlier audit rows are never deleted or updated.

### Step E: finally

`KillFile` + `UnlockRecord` (§2). The form closes (`EditMode` → `Close()` :4701-4705).

### Not used by the voucher form

The `Deleterows` proc and the `Deleterows()` method (:5302) are never called. `SaveDoc_MasterData` (:5564) and `SaveDaybookData_BankTran` (:5446) are dead in this flow.

## 4. Bill-wise settlements on modify

### 4.1 Release

Step A releases every `ADJUST` row whose `Sr_No` is one of the voucher's old rows. It restores the bills (`Rel_Sr_No`) and backs the links up into the scratch table `dbo.adjusted (Sr_No, Rel_Sr_No, Amount)`, a heap with no identity.

### 4.2 Re-apply: `FrmBill2BillAdj` (FrmBill2BillAdj.cs:790-1062)

On load:

- Header: the new row `N`. Its outstanding is `AMOUNT - BL_adjusted`, which equals the new amount.
- Open items:

  ```sql
  SELECT ... FROM IqDoc_Header RIGHT JOIN IQDayBook ... WHERE ROUND(AMOUNT-ISNULL(Bl_Adjusted,0),2)>0
    AND TYPE IN (<opposite side>) AND Act_id=<row's account>
  ORDER BY TRDATE, TSeries, TNo, TExtn
  ```

  The bills released in step A are open again, so they appear.
- If `Old_srno <= 0`, the first open item is pre-filled with `min(item outstanding, row outstanding)` (:837-840), the same as a new voucher.
- `SELECT * FROM Adjusted WHERE Sr_No = Old_srno`. Each backed-up `Amount` is put into the grid row whose `SR_NO = Rel_Sr_No` (:862-891). The old allocation is therefore **suggested, not applied**. The user can change it.
- Backed-up targets that are not in the list are silently dropped. This happens when the line account changed or the target is no longer open.

On Save (`SaveData`, DeleteData==0, :987-1014), on autocommit `mDataCn`:

```sql
EXEC DeleteBilltoBillAdjustment N                         -- no-op for a fresh row
-- for each grid row with Adjusted <> 0, in grid order (TRDATE, TSeries, TNo, TExtn):
INSERT INTO ADJUST(sr_no,Rel_Sr_No,Amount) VALUES (N, <target SR_NO>, <amount>)
UPDATE IQDaybook SET BL_Adjusted = ISNULL(BL_Adjusted,0) + <amount> WHERE Sr_No = <target>
-- once:
UPDATE IQDaybook SET BL_Adjusted = ISNULL(BL_Adjusted,0) + <sum of amounts> WHERE Sr_No = N
```

The only guard is "total adjusted ≤ row outstanding" (:957). If the dialog is closed without saving, nothing is re-applied, and step C still deletes the backup, so the allocation is lost.

The final state is identical to creating the voucher with those allocations: the `ADJUST` rows hang off the new `SR_NO`s. `VoucherService.Allocate` produces that end state. Its statement order is INSERT then UPDATE source+target together; ADJUST has no identity and BL_adjusted is a sum, so only intermediate states differ.

### 4.3 Credit/debit note header-row quirk (code-derived; no CN/DN in the data has been modified)

For CN/DN the bill-wise row is the **header** row (party, `lnno=-1`). It pairs with the grid's empty new-row, so `Old_srno = 0`. Consequences:

- The dialog ignores the backed-up allocation and pre-fills the oldest open bill.
- `DELETE FROM adjusted WHERE sr_no = 0` leaves the backup of the old header row **orphaned in `adjusted`**.

The same applies to any header row that qualifies (not possible for bank or cash headers, which are not Customer/Supplier). ISTMBM26.`adjusted` currently holds 16 orphan rows, all with pre-FY26 `Sr_No` (< 65989, the FY26 minimum), so none are voucher leftovers.

### 4.4 Delete path, for reference: `frmFinanceMaintance.DeleteData` (:3062-3117)

The engine already reproduces this:

1. If bill-wise, for each old `sr_no` (ordered): `DELETE FROM adjusted WHERE sr_No IN (SELECT sr_no FROM IQdaybook WHERE doc_id=@doc)`, then `EXEC DeleteBilltoBillAdjustment sr_no`.
2. `DELETE IQbnkTran`, `IQDaybook`, `IQTCbook`, `IQDoc_header` by doc_id. The engine omits `IQTCbook`; it is empty for these types.
3. `Update_UserDeleteStatus`: `UPDATE User_IQDOC_Header SET TaskType=1 WHERE doc_ID=@doc AND ModifyCnt=(SELECT COUNT(Doc_Id) ...)`. For vouchers `ModifyCnt` is always 0, so this **never matches**. The 6 deleted vouchers in ISTMBM26 (8525, 8549, 8550, 9305, 9827, 9926) all keep `TaskType=0`.

All steps are autocommit with no transaction.

## 5. Identity: kept vs changed

| Item | On modify |
|---|---|
| `IQDoc_Header.Doc_Id` | kept |
| `TSeries`, `TNo` | rewritten from the form (kept unless the user edits the number; duplicate-checked) |
| `Doc_Date`, `Doc_Duedate` | rewritten, both = new date |
| `Ent_Date` | **overwritten with the modify day** (date only). The creation day survives only in the first audit row |
| `Ent_Uid` | overwritten with the modifying user |
| `Bill_Time`, `Ent_Time`, `Modify_Date`, `ModifyCnt`, `IsAudit`, other header columns | untouched (NULL/0 for every voucher) |
| `IQDayBook.SR_NO` | **new** (identity). `Ent_Date` = getdate() at reinsert |
| `IQbnktran.BnkTranID` | **new**. `Status`/`HRdate` carried from the old row (all 0 in data) |
| `ADJUST` | old links deleted. New links point at the new `SR_NO` |
| `User_IQDOC_Header` / `User_IQDayBook` / `User_IQbnktran` | one more set appended per save. `ModifyCnt=0`, `TaskType=0` |
| `adjusted` | transient backup. Empty afterwards except for the §4.3 leak |

## 6. Evidence: the 8 modified vouchers in ISTMBM26

These are the documents with more than one `User_IQDOC_Header` row. ISTMP26 has none.

| Doc | Tt | What changed | Live rows now | Settlement after |
|---|---|---|---|---|
| 8914 | 205 | nothing (re-saved after 23 s) | SR 71034/71035, BnkTranID 30 | none (party Bill2Bill=0) |
| 9074 | 205 | nothing | 72787/72788 | 72787→69985 3450 |
| 9370 | 206 | amount 310→620 (audit Bill_Amt 310, then 620) | 74939 | none (Expense line) |
| 9536 | 207 | date 20 Aug→23 Jul (audit TRDATE 08-20, then 07-23; live TRDATE 07-23) | 76300/76301, Bnk 125 | 76300→69921 30000 |
| 10085 | 207 | date 06 Sep→07 Sep, chqdate 0913→0907 | 80431/80432, Bnk 185 | none (Bill2Bill=0) |
| 10086 | 207 | date 12 Sep→04 Sep | 80433/80434, Bnk 186 | 80433→72226 43050 |
| 10106 | 207 | amount 20000→21000 | 80435/80436, Bnk 187 | 80435→67524 20000 |
| 10183 | 205 | saved 3 times. Amount 61107→61107→58907 | 80871/80872, Bnk 191 | 80871→66490 3300 |

What the data shows:

- **New ids.** Docs 10085/10086/10106 were created on 13 Oct (audit row 1, `Ent_Date` 2026-10-13). They now hold SR_NOs 80431-80436 and BnkTranIDs 185-187, allocated consecutively on 15 Oct at 20:00-20:07. That is delete and reinsert.
- **Header changes.** `IQDoc_Header.Ent_Date` = modify day (10085: 2026-10-15, created 10-13), and `Doc_Id` is unchanged.
- **Step order D after C.** Where the party is bill-wise, the gap between live `IQDayBook.Ent_Date` and the audit `User_IQDayBook.Ent_Date` is 1.3-4.6 s: the dialog was on screen between them (10106: 20:06:57.793 → 20:06:59.067). Where it is not bill-wise, the gap is 4-10 ms (8914, 10085).
- **Counters.** For every settled row involved, `BL_adjusted` equals the sum of its ADJUST links.
- **Audit numbering.** `ModifyCnt` = 0 on all 405 voucher audit headers (403 ISTMBM26 + 2 ISTMP26). The live `IQDoc_Header.ModifyCnt` is also always 0.

## 7. Credit note and debit note specifics

### How the form presents them

`Fill_Accounts_Combo` (:4007):

- The **header** combo ("party") lists only `Act_Type IN ('Customer','Supplier','Customer-Supplier')`. For receipts and payments it lists Cash or Bank accounts.
- Lines are any account. `GetActsList_Cond` keeps `IntCond=0`, so the account search is unrestricted. The line account is the "against" account (discount, sales/purchase, income).
- There is no reason field. The reason is carried by narration: header `Narration` (`txtNarration`, may be blank, stored as " ") and line `Narration` (grid col 12). The ledger header row reuses line 0's narration.
- There are **no GST or item lines**. CN/DN are plain two-sided ledger vouchers: no `IQDoc_Recd`/`Delv`, no tax rows, no `IQbnktran`, no `IQTCBook`.
- The grid query for CN/DN is the same as for receipts and payments (:3116). `TTypes.Disc_COl=0` for 208/209, so discount rows are not offered.

### Posting (`SaveDaybookData` :4846-4857, :4935-4943)

- **CN** (208): each line is posted `D` on the line account (contra = party). The header row is `C` on the party, `lnno -1`, contra = line 0. So a CN credits the party.
- **DN** (209): each line is posted `C` on the line account. The header row is `D` on the party.
- `VType 'DPAY'` on both rows.
- Audit: 2 `User_IQDayBook` rows per line. Counter 0 is the party row (CN: party C; DN: party D) with `lnno 0`. Counter 1 is the line-account row with `lnno 1`, `DiscRow 'D'`. `GetSignType` :5541-5550.

### Bill-wise

The party header row qualifies for `GetBillToBillAdjustment`:

- CN header `C` on a Customer settles that customer's debit items.
- DN header `D` on a Supplier settles that supplier's credit items.

Line rows qualify only if the line account is itself a bill-wise Customer/Supplier on the qualifying side (CN line `D` → Supplier; DN line `C` → Customer).

### The one CN in ISTMBM26: Doc 9941 (Tt 208, CRE/1, 30 Sep 2026)

- Header: `Act_ID 10502` ("=", Customer, Bill2Bill 1), `Bill_Amt 1948.00`, `Narration ' '`, `Mode 0`, `Ent_Date 2026-09-30`, `DH_VouchType 'Credit Note'`, `ModifyCnt 0`.
- IQDayBook:
  - 79202: `10970` (K B K BIOTECH, Customer) D 1948 contra 10502, lnno 0, BL 0, **Adjusted 1948**, narration "SPECIAL DISC ISSUED"
  - 79203: `10502` C 1948 contra 10970, lnno -1, **BL_adjusted 1948**, Adjusted 0, same narration
- ADJUST: `79203 → 70004` 1948.00. Target 70004 is the opening-balance row of account 10502: `Doc_ID -1`, `VTYPE 'opbal'`, D 69827, BL 1948.
- Audit: 1 header row (ModifyCnt 0, TaskType 0). User_IQDayBook rows: (10502 C lnno 0), (10970 D lnno 1 'D'). No bank rows.
- The user picked a placeholder customer "=" as the party and a real customer as the "against" line. Data-entry oddity; HQ allows it.

### The one DN in ISTMBM26: Doc 9245 (Tt 209, DEB/1, dated 4 Jul, entered 23 Jul 2026)

- Header: `Act_ID 10757` (USHASRI STEEL, Supplier, Bill2Bill 1), `Bill_Amt 2779.66`, `Narration '20 PAISA CREDITNOT FOR PRICE REDUCTION'`.
- IQDayBook:
  - 73894: `10356` (GST Purchase 18%, Income) C 2779.66 lnno 0, Adjusted 2779.66
  - 73895: `10757` D 2779.66 lnno -1, BL 2779.66, Adjusted 2779.66
- ADJUST: `73895 → 70009` 2779.66. Target 70009 is the supplier's opening balance: Doc_ID -1, C 19030, BL 19030.
- Audit: (10757 D lnno 0), (10356 C lnno 1 'D').

`IQDayBook.Adjusted` (as distinct from `BL_adjusted`) is **not** written by the voucher form, which passes 0. It is recomputed by HQ's outstanding reports (`UpdateReceiveables`/`UpdatePayables`, called from frmReceivablesOutstanding.cs:3482 and mdlGeneral.cs:6524). That is why most vouchers carry a non-zero `Adjusted`. The parity test is right to ignore it.

## 8. Quick checks (question 4)

| Check | ISTMBM26 | ISTMP26 |
|---|---|---|
| Sales bills by Mode | 0: 465, 1: 395, 2: 1, 4: 16 | 0: 108, 1: 13 |
| **Mode 3 "Others" split (or any mode outside 0,1,2,4-7)** | **0** | **0** |
| Purchases from a `Customer-Supplier` account | 3 (all Mode 1 credit) | 0 |
| ...of which the account is bill-wise | 2 (Sheetal traders: docs 8753, 10251) | 0 |
| ADJUST links touching Customer-Supplier rows | 0 | 0 |
| Customer-Supplier accounts (bill-wise) | 12 (11) | 1 (0) |

`Payment_Modes` row 3 is "Others" and row 7 is "Others1"; the latter has been used 0 times as well. The current refusal of Mode 3 costs nothing. Bill-wise purchases from Customer-Supplier occur twice, with no settlements.

## 9. Implications for the engine (modify plan)

To be byte-identical in end state with an HQ modify that is saved with the same allocations:

1. **Lock**: `HqDocumentLock` on the `Doc_Id` (as for delete). Hash the current state for REVIEW_CHANGED.
2. **Release**: for each old row ordered by `SR_NO`, apply the `DeleteBilltoBillAdjustment` effect: target `BL_adjusted -= amount`, then `DELETE ADJUST WHERE Sr_No IN (old rows)`. Do not touch the source counters: those rows are about to be deleted.
   - Recommendation: **do not write `adjusted`**. On a successful HQ modify of a receipt or payment it ends empty, and only HQ's own crash paths and the CN/DN quirk leave rows behind.
   - Put the release in the same SQL transaction as the rest. HQ does not, but atomicity cannot change the final bytes.
3. **Header**: `EXEC Update_Voudetails` with `@Doc_id` = existing and the same Args the create path builds:
   - `Ent_Date` = today (date only), `Ent_Uid` = HQ user
   - `TExtn " "`, `Doc_Duedate = Doc_Date`, `Narration` trimmed or `" "`, `mDH_VouchType`
   - `Tno` = existing unless renumbering is requested. If it changes, apply HQ's duplicate rule excluding the doc's own id.
4. `DELETE FROM IQbnktran WHERE Doc_Id=@id`, then `DELETE FROM IQDayBook WHERE Doc_Id=@id`.
5. **Body**: exactly `VoucherService.Body` (same `VoucherLedger.Plan` rows, same `IQdaybook_insert`/`IQbnktran_insert` args), with these edit-only differences:
   - `IQbnktran_insert @mStatus/@mHRdate` = the old bank row's `Status`/`HRdate` for the line being kept. They are 0 everywhere today, but carry them.
   - `IQdaybook_insert @MImportCollection_DtldID` = the old line row's value for kept lines (0/NULL today).
6. **Allocate**: `VoucherService.Allocate(new entry, a)` per requested allocation. When the account is unchanged, offer the released links as the default, mirroring HQ's prefill.
7. **Audit**: append `User_Insert_Voudetails` + `User_IQdaybook_insert`/`User_IQbnktran_insert` exactly as on create. Never update earlier audit rows.
8. **Verify**: `Bill_Amt`, the ledger row count, and that `ADJUST` counters reconcile (`BL_adjusted` = sum of links for each touched row).

### Divergence found in the current engine

- `VoucherService.Audit` passes `mModifyCnt = auditRevision` (1). **HQ passes nothing, so 0**. All 405 voucher audit headers in both firms have `ModifyCnt=0`. `VoucherParityTests` hides this because it ignores `ModifyCnt`/`TaskType`.
- Knock-on effect: the engine's delete step "Mark audit deleted" (`TaskType=1 WHERE ModifyCnt = COUNT(*)`) **matches** an engine-created voucher (1 = 1). The same statement is a no-op on HQ's own vouchers.
- Fix: pass `mModifyCnt = 0` (or omit it) for vouchers, and keep a modify at 0 too.

## 10. Open questions

1. **CN/DN modify (§4.3)**: is HQ's real behaviour the oldest-bill prefill plus the orphan backup in `adjusted`? It has not been observed in data (no CN/DN was ever modified). Confirm by modifying a scratch CN in HQ against `MKHQ_PARITY_MBM` and diffing `adjusted`/`ADJUST`. Until then, the engine should offer the old header allocation explicitly and not write `adjusted`.
2. **Stale `ADJUST` targets on account change**: HQ drops them (they are released and not offered). The engine should warn: "N settled against <old party> will be detached."
3. **Cursor order** in `DeleteBilltoBillAdjustment`: unordered on a heap. Only intermediate counter values depend on it; the end state does not.
4. **Partial-failure states** (release done, save failed): HQ can leave bills released with backups in `adjusted`. We reproduce only the success path. Should a reader flag existing vouchers whose `BL_adjusted` ≠ sum of links? None today in the 14 rows checked.
5. **`IQDayBook.Salesman_ID` / `TRDueDate`** on reinsert: these follow the create path. Since the 4 Nov 2026 build, Salesman_ID is 0 rather than NULL. A modify by today's HQ therefore rewrites old NULLs as 0. The engine should do the same, so it should not copy the old value.
6. The SMS prompt on receipt edit (`Push_SMS_InterNet`) and WhatsApp send were not traced for DB writes (e.g. SMS log tables). They are irrelevant to voucher tables but unverified.
